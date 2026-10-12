using System;
using System.Collections.Generic;
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
    private readonly ILogger<AddedDateResolver> _logger;

    public AddedDateResolver(IEnumerable<IAddedDateSource> sources, AddedDateStore store, ILogger<AddedDateResolver> logger)
    {
        _sources = sources;
        _store = store;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, AddedDateDecision>> ResolveAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        var window = PluginSettings.ReplacementWindow;
        var sourceItems = items
            .DistinctBy(item => item.Id)
            .Select(item => new SourceItem(item, _store.GetItem(item.Id)?.Original ?? item.DateCreated))
            .ToList();
        var originals = sourceItems.ToDictionary(sourceItem => sourceItem.Item.Id, sourceItem => sourceItem.Original);
        var best = new Dictionary<Guid, AddedDateDecision>();

        foreach (var source in _sources)
        {
            IReadOnlyDictionary<Guid, AddedDateCandidate> candidates;
            try
            {
                candidates = await source.GetCandidatesAsync(sourceItems, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Source {Source} failed", source.Name);
                continue;
            }

            foreach (var (itemId, candidate) in candidates)
            {
                if (!originals.TryGetValue(itemId, out var original))
                {
                    continue;
                }

                if (candidate.AddedAt > original - MinimumDifference)
                {
                    _logger.Log(
                        PluginSettings.DetailLevel,
                        "Item {ItemId}: rejected {Date} from {Source} ({Detail}), not at least a minute earlier than {Original}",
                        itemId,
                        candidate.AddedAt,
                        source.Name,
                        candidate.Detail,
                        original);
                    continue;
                }

                if (candidate.PredecessorRemovedAt is { } removedAt && original - removedAt > window)
                {
                    _logger.Log(
                        PluginSettings.DetailLevel,
                        "Item {ItemId}: rejected {Date} from {Source} ({Detail}), predecessor removed {Days:F1} days before, window is {WindowDays} days",
                        itemId,
                        candidate.AddedAt,
                        source.Name,
                        candidate.Detail,
                        (original - removedAt).TotalDays,
                        window.TotalDays);
                    continue;
                }

                if (!best.TryGetValue(itemId, out var current) || candidate.AddedAt < current.Entry.Date)
                {
                    best[itemId] = new AddedDateDecision(new ItemEntry(candidate.AddedAt, source.Name, original), candidate.Detail);
                }
            }
        }

        return best;
    }
}
