using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class OriginalDateAddedProvider : ICustomMetadataProvider<Movie>, ICustomMetadataProvider<Episode>, IHasItemChangeMonitor
{
    private readonly AddedDateResolver _resolver;
    private readonly AddedDateStore _store;
    private readonly ILogger<OriginalDateAddedProvider> _logger;

    public OriginalDateAddedProvider(AddedDateResolver resolver, AddedDateStore store, ILogger<OriginalDateAddedProvider> logger)
    {
        _resolver = resolver;
        _store = store;
        _logger = logger;
    }

    public string Name => "Original Date Added";

    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => FetchAsync((BaseItem)item, cancellationToken);

    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => FetchAsync((BaseItem)item, cancellationToken);

    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
        => !PluginSettings.DryRun && _store.GetItem(item.Id) is { } entry && item.DateCreated != entry.Date;

    private async Task<ItemUpdateType> FetchAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (!ItemIdentity.IsSupported(item))
            {
                return ItemUpdateType.None;
            }

            if (item.DateLastRefreshed == DateTime.MinValue)
            {
                _logger.Log(PluginSettings.DetailLevel, "First refresh of {Name} ({ItemId})", item.Name, item.Id);
                var decisions = await _resolver.ResolveAsync([item], cancellationToken).ConfigureAwait(false);
                if (decisions.TryGetValue(item.Id, out var decision))
                {
                    if (PluginSettings.DryRun)
                    {
                        _logger.LogInformation(
                            "Dry run: would set date added of {Name} ({ItemId}) to {Date} from {Source} ({Detail}), scanned as {Original}",
                            item.Name,
                            item.Id,
                            decision.Entry.Date,
                            decision.Entry.Source,
                            decision.Detail,
                            decision.Entry.Original);
                        return ItemUpdateType.None;
                    }

                    _store.SetItem(item.Id, decision.Entry);
                    _logger.LogInformation(
                        "Date added of {Name} ({ItemId}) is {Date} from {Source} ({Detail}), scanned as {Original}",
                        item.Name,
                        item.Id,
                        decision.Entry.Date,
                        decision.Entry.Source,
                        decision.Detail,
                        decision.Entry.Original);
                }
                else
                {
                    _logger.Log(PluginSettings.DetailLevel, "No earlier date added found for {Name} ({ItemId})", item.Name, item.Id);
                }
            }

            var entry = _store.GetItem(item.Id);
            if (entry is null || item.DateCreated == entry.Date)
            {
                return ItemUpdateType.None;
            }

            if (PluginSettings.DryRun)
            {
                _logger.LogInformation("Dry run: would set date added of {Name} ({ItemId}) from {Current} back to {Date}", item.Name, item.Id, item.DateCreated, entry.Date);
                return ItemUpdateType.None;
            }

            _logger.Log(PluginSettings.DetailLevel, "Setting date added of {Name} ({ItemId}) from {Current} to {Date}", item.Name, item.Id, item.DateCreated, entry.Date);
            item.DateCreated = entry.Date;
            return ItemUpdateType.MetadataEdit;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not set the date added of {Name} ({ItemId})", item.Name, item.Id);
            return ItemUpdateType.None;
        }
    }
}
