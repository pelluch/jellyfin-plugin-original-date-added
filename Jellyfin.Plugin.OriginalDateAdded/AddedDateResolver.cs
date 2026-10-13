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
        var others = new List<(string Source, Guid ItemId, AddedDateCandidate Candidate)>();
        foreach (var source in _sources)
        {
            var pending = sourceItems.Where(sourceItem => !decisions.ContainsKey(sourceItem.Item.Id)).ToList();
            var candidates = await GetCandidatesAsync(source, pending, cancellationToken).ConfigureAwait(false);
            foreach (var (item, scannedAt) in pending)
            {
                if (!candidates.TryGetValue(item.Id, out var candidate))
                {
                    continue;
                }

                if (candidate.IsAuthoritative)
                {
                    var (addedAt, detail) = ClampToServerSetUp(candidate, serverSetUpAt);
                    decisions[item.Id] = new AddedDateDecision(new ItemEntry(addedAt, source.Name, scannedAt, Authoritative: true), detail);
                }
                else
                {
                    others.Add((source.Name, item.Id, candidate));
                }
            }
        }

        var undecided = sourceItems
            .Where(sourceItem => !decisions.ContainsKey(sourceItem.Item.Id)
                && _store.GetItem(sourceItem.Item.Id) is not { Authoritative: true }) // An authoritative source, if any, didn't answer
            .ToDictionary(sourceItem => sourceItem.Item.Id, sourceItem => sourceItem.ScannedAt);
        foreach (var (sourceName, itemId, candidate) in others)
        {
            if (undecided.TryGetValue(itemId, out var scannedAt)
                && DecideOrdinary(itemId, candidate, sourceName, scannedAt, serverSetUpAt) is { } decision
                && (!decisions.TryGetValue(itemId, out var current) || decision.Entry.AddedAt < current.Entry.AddedAt))
            {
                decisions[itemId] = decision;
            }
        }

        return decisions;
    }

    private static (DateTime AddedAt, string Detail) ClampToServerSetUp(AddedDateCandidate candidate, DateTime? serverSetUpAt)
        => serverSetUpAt is { } setUpAt && candidate.AddedAt < setUpAt
            ? (setUpAt, string.Create(CultureInfo.InvariantCulture, $"{candidate.Detail}, {candidate.AddedAt:u} is before this server was set up"))
            : (candidate.AddedAt, candidate.Detail);

    private AddedDateDecision? DecideOrdinary(Guid itemId, AddedDateCandidate candidate, string sourceName, DateTime scannedAt, DateTime? serverSetUpAt)
    {
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
        else if (!IsAcceptable(itemId, candidate, sourceName, scannedAt))
        {
            return null;
        }

        return new AddedDateDecision(new ItemEntry(addedAt, sourceName, scannedAt), detail);
    }

    private bool IsAcceptable(Guid itemId, AddedDateCandidate candidate, string sourceName, DateTime scannedAt)
    {
        if (candidate.AddedAt > scannedAt - PluginSettings.MinimumDifference)
        {
            _logger.Log(
                PluginSettings.DetailLevel,
                "Item {ItemId}: rejected {AddedAt} from {Source} ({Detail}), not at least {Minutes} minutes earlier than {ScannedAt}",
                itemId,
                candidate.AddedAt,
                sourceName,
                candidate.Detail,
                PluginSettings.MinimumDifference.TotalMinutes,
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
