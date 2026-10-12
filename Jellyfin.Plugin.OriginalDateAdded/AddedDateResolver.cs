using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class AddedDateResolver
{
    public static readonly TimeSpan MinimumDifference = TimeSpan.FromMinutes(1); // absorbs rounding between sources, e.g. Tracearr's milliseconds

    private readonly IEnumerable<IAddedDateSource> _sources;
    private readonly AddedDateStore _store;
    private readonly ServerSetup _serverSetup;
    private readonly ILogger<AddedDateResolver> _logger;

    public AddedDateResolver(IEnumerable<IAddedDateSource> sources, AddedDateStore store, ServerSetup serverSetup, ILogger<AddedDateResolver> logger)
    {
        _sources = sources;
        _store = store;
        _serverSetup = serverSetup;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, AddedDateDecision>> ResolveAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        var serverSetUpAt = _serverSetup.Date;
        var sourceItems = items
            .DistinctBy(item => item.Id)
            .Select(item => new SourceItem(item, _store.GetItem(item.Id)?.ScannedAt ?? item.DateCreated))
            .ToList();
        var decisions = new Dictionary<Guid, AddedDateDecision>();
        await AddAuthoritativeDecisionsAsync(sourceItems, serverSetUpAt, decisions, cancellationToken).ConfigureAwait(false);

        var authoritativeSources = _sources.Where(source => source.IsAuthoritative).Select(source => source.Name).ToHashSet(StringComparer.Ordinal);
        var undecided = sourceItems
            .Where(sourceItem => !decisions.ContainsKey(sourceItem.Item.Id)
                && !(_store.GetItem(sourceItem.Item.Id) is { } stored && authoritativeSources.Contains(stored.Source))) // its authoritative source didn't answer this time, e.g. it's down
            .ToList();
        await AddEarliestDecisionsAsync(undecided, serverSetUpAt, decisions, cancellationToken).ConfigureAwait(false);
        return decisions;
    }

    private static (DateTime AddedAt, string Detail) ClampToServerSetUp(AddedDateCandidate candidate, DateTime? serverSetUpAt)
        => serverSetUpAt is { } setUpAt && candidate.AddedAt < setUpAt
            ? (setUpAt, string.Create(CultureInfo.InvariantCulture, $"{candidate.Detail}, {candidate.AddedAt:u} is before this server was set up"))
            : (candidate.AddedAt, candidate.Detail);

    private async Task AddAuthoritativeDecisionsAsync(
        List<SourceItem> sourceItems,
        DateTime? serverSetUpAt,
        Dictionary<Guid, AddedDateDecision> decisions,
        CancellationToken cancellationToken)
    {
        foreach (var source in _sources.Where(source => source.IsAuthoritative))
        {
            var pending = sourceItems.Where(sourceItem => !decisions.ContainsKey(sourceItem.Item.Id)).ToList();
            var candidates = await GetCandidatesAsync(source, pending, cancellationToken).ConfigureAwait(false);
            foreach (var (item, scannedAt) in pending)
            {
                if (candidates.TryGetValue(item.Id, out var candidate))
                {
                    var (addedAt, detail) = ClampToServerSetUp(candidate, serverSetUpAt);
                    decisions[item.Id] = new AddedDateDecision(new ItemEntry(addedAt, source.Name, scannedAt), detail, IsAuthoritative: true);
                }
            }
        }
    }

    private async Task AddEarliestDecisionsAsync(
        List<SourceItem> sourceItems,
        DateTime? serverSetUpAt,
        Dictionary<Guid, AddedDateDecision> decisions,
        CancellationToken cancellationToken)
    {
        var scannedDates = sourceItems.ToDictionary(sourceItem => sourceItem.Item.Id, sourceItem => sourceItem.ScannedAt);
        foreach (var source in _sources.Where(source => !source.IsAuthoritative))
        {
            var candidates = await GetCandidatesAsync(source, sourceItems, cancellationToken).ConfigureAwait(false);
            foreach (var (itemId, candidate) in candidates)
            {
                if (!scannedDates.TryGetValue(itemId, out var scannedAt))
                {
                    continue;
                }

                var (addedAt, detail) = ClampToServerSetUp(candidate, serverSetUpAt);
                if (serverSetUpAt is { } setUpAt && scannedAt < setUpAt)
                {
                    // The item's own date can't be right, so any record of its content gives it the setup date.
                    if (addedAt > setUpAt)
                    {
                        detail = string.Create(CultureInfo.InvariantCulture, $"{detail}, this item's date {scannedAt:u} is before this server was set up");
                        addedAt = setUpAt;
                    }
                }
                else if (!IsAcceptable(itemId, candidate, source.Name, scannedAt))
                {
                    continue;
                }

                if (!decisions.TryGetValue(itemId, out var current) || addedAt < current.Entry.AddedAt)
                {
                    decisions[itemId] = new AddedDateDecision(new ItemEntry(addedAt, source.Name, scannedAt), detail);
                }
            }
        }
    }

    private bool IsAcceptable(Guid itemId, AddedDateCandidate candidate, string sourceName, DateTime scannedAt)
    {
        if (candidate.AddedAt > scannedAt - MinimumDifference)
        {
            _logger.Log(
                PluginSettings.DetailLevel,
                "Item {ItemId}: rejected {AddedAt} from {Source} ({Detail}), not at least a minute earlier than {ScannedAt}",
                itemId,
                candidate.AddedAt,
                sourceName,
                candidate.Detail,
                scannedAt);
            return false;
        }

        var window = PluginSettings.ReplacementWindow;
        if (candidate.PredecessorRemovedAt is { } removedAt && scannedAt - removedAt > window)
        {
            _logger.Log(
                PluginSettings.DetailLevel,
                "Item {ItemId}: rejected {AddedAt} from {Source} ({Detail}), predecessor removed {Days:F1} days before, window is {WindowDays} days",
                itemId,
                candidate.AddedAt,
                sourceName,
                candidate.Detail,
                (scannedAt - removedAt).TotalDays,
                window.TotalDays);
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IAddedDateSource source, List<SourceItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<Guid, AddedDateCandidate>();
        }

        try
        {
            return await source.GetCandidatesAsync(items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Source {Source} failed", source.Name);
            return new Dictionary<Guid, AddedDateCandidate>();
        }
    }
}
