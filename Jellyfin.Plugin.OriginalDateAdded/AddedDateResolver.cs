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
        var window = PluginSettings.ReplacementWindow;
        var serverSetUpAt = _serverSetup.Date;
        var sourceItems = items
            .DistinctBy(item => item.Id)
            .Select(item => new SourceItem(item, _store.GetItem(item.Id)?.ScannedAt ?? item.DateCreated))
            .ToList();
        var scannedDates = sourceItems.ToDictionary(sourceItem => sourceItem.Item.Id, sourceItem => sourceItem.ScannedAt);
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
                if (!scannedDates.TryGetValue(itemId, out var scannedAt))
                {
                    continue;
                }

                if (candidate.AddedAt > scannedAt - MinimumDifference)
                {
                    _logger.Log(
                        PluginSettings.DetailLevel,
                        "Item {ItemId}: rejected {AddedAt} from {Source} ({Detail}), not at least a minute earlier than {ScannedAt}",
                        itemId,
                        candidate.AddedAt,
                        source.Name,
                        candidate.Detail,
                        scannedAt);
                    continue;
                }

                if (candidate.PredecessorRemovedAt is { } removedAt && scannedAt - removedAt > window)
                {
                    _logger.Log(
                        PluginSettings.DetailLevel,
                        "Item {ItemId}: rejected {AddedAt} from {Source} ({Detail}), predecessor removed {Days:F1} days before, window is {WindowDays} days",
                        itemId,
                        candidate.AddedAt,
                        source.Name,
                        candidate.Detail,
                        (scannedAt - removedAt).TotalDays,
                        window.TotalDays);
                    continue;
                }

                var addedAt = candidate.AddedAt;
                var detail = candidate.Detail;
                if (serverSetUpAt is { } setUpAt && addedAt < setUpAt)
                {
                    detail = string.Create(CultureInfo.InvariantCulture, $"{detail}, {addedAt:u} is before this server was set up");
                    addedAt = setUpAt;
                }

                if (!best.TryGetValue(itemId, out var current) || addedAt < current.Entry.AddedAt)
                {
                    best[itemId] = new AddedDateDecision(new ItemEntry(addedAt, source.Name, scannedAt), detail);
                }
            }
        }

        return best;
    }
}
