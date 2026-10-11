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
        var window = TimeSpan.FromDays(Plugin.Instance!.Configuration.ReplacementWindowDays);
        var originals = items
            .DistinctBy(item => item.Id)
            .ToDictionary(item => item.Id, item => _store.GetItem(item.Id)?.Original ?? item.DateCreated);
        var best = new Dictionary<Guid, AddedDateDecision>();

        foreach (var source in _sources)
        {
            IReadOnlyDictionary<Guid, AddedDateCandidate> candidates;
            try
            {
                candidates = await source.GetCandidatesAsync(items, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Source {Source} failed", source.Name);
                continue;
            }

            foreach (var (itemId, candidate) in candidates)
            {
                if (!originals.TryGetValue(itemId, out var original) || !IsAcceptable(candidate, original, window))
                {
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

    private static bool IsAcceptable(AddedDateCandidate candidate, DateTime original, TimeSpan window)
        => candidate.AddedAt < original
            && (candidate.PredecessorRemovedAt is not { } removedAt || original - removedAt <= window);
}
