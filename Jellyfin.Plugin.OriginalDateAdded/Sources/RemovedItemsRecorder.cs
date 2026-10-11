using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public sealed class RemovedItemsRecorder : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateStore _store;
    private readonly AddedDateResolver _resolver;
    private readonly ILogger<RemovedItemsRecorder> _logger;

    public RemovedItemsRecorder(ILibraryManager libraryManager, AddedDateStore store, AddedDateResolver resolver, ILogger<RemovedItemsRecorder> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _resolver = resolver;
        _logger = logger;
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

    // All versions of an item that were added after it
    private static InternalItemsQuery? SuccessorQuery(BaseItem item)
    {
        switch (item)
        {
            case Movie:
                var ids = new Dictionary<string, string>();
                foreach (var provider in new[] { MetadataProvider.Tmdb, MetadataProvider.Imdb })
                {
                    if (item.TryGetProviderId(provider, out var id))
                    {
                        ids[provider.ToString()] = id;
                    }
                }

                return new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie], IsVirtualItem = false, HasAnyProviderId = ids };
            case Episode { ParentIndexNumber: { } season, IndexNumber: { } number } episode:
                return new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    IsVirtualItem = false,
                    AncestorIds = [episode.SeriesId],
                    ParentIndexNumber = season,
                    IndexNumber = number,
                };
            default:
                return null;
        }
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
        if (keys.Count == 0)
        {
            return;
        }

        _store.AddRemoved(keys, new RemovedEntry(item.DateCreated, DateTime.UtcNow, item.GetBaseItemKind()));
        if (SuccessorQuery(item) is { } query)
        {
            _ = Task.Run(() => UpdateRefreshedSuccessorsAsync(item, keys, query));
        }
    }

    private async Task UpdateRefreshedSuccessorsAsync(BaseItem removed, IReadOnlyList<string> keys, InternalItemsQuery query)
    {
        try
        {
            var successors = _libraryManager.GetItemList(query)
                .Where(item => item.DateLastRefreshed != DateTime.MinValue // items that already have had their first refresh
                    && ItemIdentity.ContentKeys(item).Intersect(keys).Any())
                .ToList();
            if (successors.Count == 0)
            {
                return;
            }

            var decisions = await _resolver.ResolveAsync(successors, CancellationToken.None).ConfigureAwait(false);
            foreach (var successor in successors)
            {
                if (!decisions.TryGetValue(successor.Id, out var decision)
                    || (_store.GetItem(successor.Id) is { } stored && stored.Date <= decision.Entry.Date))
                {
                    continue;
                }

                if (PluginSettings.DryRun)
                {
                    _logger.LogInformation(
                        "Dry run: would set date added of {Name} ({ItemId}) to {Date} from {Source} ({Detail}), scanned as {Original}, after {RemovedName} ({RemovedId}) was removed",
                        successor.Name,
                        successor.Id,
                        decision.Entry.Date,
                        decision.Entry.Source,
                        decision.Detail,
                        decision.Entry.Original,
                        removed.Name,
                        removed.Id);
                    continue;
                }

                _store.SetItem(successor.Id, decision.Entry);
                _logger.LogInformation(
                    "Date added of {Name} ({ItemId}) is {Date} from {Source} ({Detail}), scanned as {Original}, applied after {RemovedName} ({RemovedId}) was removed",
                    successor.Name,
                    successor.Id,
                    decision.Entry.Date,
                    decision.Entry.Source,
                    decision.Detail,
                    decision.Entry.Original,
                    removed.Name,
                    removed.Id);
                successor.DateCreated = decision.Entry.Date;
                await successor.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not update the items replacing {Name} ({ItemId})", removed.Name, removed.Id);
        }
    }
}
