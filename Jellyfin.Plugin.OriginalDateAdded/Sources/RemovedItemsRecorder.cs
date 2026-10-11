using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public sealed class RemovedItemsRecorder : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateStore _store;

    public RemovedItemsRecorder(ILibraryManager libraryManager, AddedDateStore store)
    {
        _libraryManager = libraryManager;
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.PruneRemoved(DateTime.UtcNow - TimeSpan.FromDays(Plugin.Instance!.Configuration.ReplacementWindowDays));
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        if (!ItemIdentity.IsSupported(item))
        {
            return;
        }

        _store.RemoveItem(item.Id);
        var keys = ItemIdentity.ContentKeys(item);
        if (keys.Count > 0)
        {
            _store.AddRemoved(keys, new RemovedEntry(item.DateCreated, DateTime.UtcNow, item.GetBaseItemKind()));
        }
    }
}
