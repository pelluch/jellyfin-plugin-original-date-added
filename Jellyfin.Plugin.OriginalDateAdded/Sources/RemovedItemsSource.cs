using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public class RemovedItemsSource : IAddedDateSource
{
    private readonly AddedDateStore _store;

    public RemovedItemsSource(AddedDateStore store)
    {
        _store = store;
    }

    public string Name => "removed-items";

    public Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        foreach (var item in items.Where(ItemIdentity.IsSupported))
        {
            var entry = _store.FindRemoved(ItemIdentity.ContentKeys(item), item.GetBaseItemKind());
            if (entry is not null)
            {
                candidates[item.Id] = new AddedDateCandidate(entry.Date, entry.RemovedAt, "removed item with the same content keys");
            }
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, AddedDateCandidate>>(candidates);
    }
}
