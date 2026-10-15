using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly IAddedDateSource _removedItems;
    private readonly IAddedDateSource _arr;
    private readonly AddedDateStore _store;
    private readonly ILogger<AddedDateResolver> _logger;

    public AddedDateResolver(IAddedDateSource removedItems, IAddedDateSource arr, AddedDateStore store, ILogger<AddedDateResolver> logger)
    {
        _removedItems = removedItems;
        _arr = arr;
        _store = store;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<Guid, AddedDateDecision>> ResolveAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
        => ResolveAsync(items, [_removedItems], cancellationToken);

    public Task<IReadOnlyDictionary<Guid, AddedDateDecision>> ResolveForBackfillAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
        => ResolveAsync(items, [_arr, _removedItems], cancellationToken);

    private async Task<IReadOnlyDictionary<Guid, AddedDateDecision>> ResolveAsync(IReadOnlyList<BaseItem> items, IReadOnlyList<IAddedDateSource> sources, CancellationToken cancellationToken)
    {
        var sourceItems = items
            .DistinctBy(item => item.Id)
            .Select(item => new SourceItem(item, _store.GetItem(item.Id)?.ScannedAt ?? item.DateCreated))
            .ToList();
        var decisions = new Dictionary<Guid, AddedDateDecision>();
        foreach (var source in sources)
        {
            var candidates = await GetCandidatesAsync(source, sourceItems, cancellationToken).ConfigureAwait(false);
            foreach (var (item, scannedAt) in sourceItems)
            {
                if (candidates.TryGetValue(item.Id, out var candidate)
                    && IsAcceptable(item.Id, candidate, source.Name, scannedAt)
                    && (!decisions.TryGetValue(item.Id, out var current) || candidate.AddedAt < current.Entry.AddedAt))
                {
                    decisions[item.Id] = new AddedDateDecision(new ItemEntry(candidate.AddedAt, source.Name, scannedAt), candidate.Detail, candidate.IsImport);
                }
            }
        }

        return decisions;
    }

    private bool IsAcceptable(Guid itemId, AddedDateCandidate candidate, string sourceName, DateTime scannedAt)
    {
        if (!candidate.IsImport && candidate.AddedAt > scannedAt - PluginSettings.MinimumDifference) // AddedDateApplier decides whether an import may move a date later
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
            return ReadOnlyDictionary<Guid, AddedDateCandidate>.Empty;
        }

        try
        {
            return await source.GetCandidatesAsync(items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Source {Source} failed", source.Name);
            return ReadOnlyDictionary<Guid, AddedDateCandidate>.Empty;
        }
    }
}
