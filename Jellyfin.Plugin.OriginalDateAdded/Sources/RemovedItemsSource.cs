using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public class RemovedItemsSource : IAddedDateSource
{
    private readonly AddedDateStore _store;

    public RemovedItemsSource(AddedDateStore store)
    {
        _store = store;
    }

    public string Name => "removed-items";

    public Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        foreach (var item in items.Select(sourceItem => sourceItem.Item).Where(ItemIdentity.IsSupported))
        {
            var entry = _store.FindRemoved(ItemIdentity.ContentKeys(item), item.GetBaseItemKind());
            if (entry is not null)
            {
                candidates[item.Id] = new AddedDateCandidate(entry.AddedAt, entry.RemovedAt, "removed item with the same content keys");
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, AddedDateCandidate>>(candidates);
    }
}
