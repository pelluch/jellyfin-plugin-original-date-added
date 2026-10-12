using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
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

public sealed class RemovedItemsRecorder : BackgroundService
{
    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateStore _store;
    private readonly AddedDateResolver _resolver;
    private readonly AddedDateApplier _applier;
    private readonly ILogger<RemovedItemsRecorder> _logger;
    private readonly Channel<Removal> _removals = Channel.CreateUnbounded<Removal>(new UnboundedChannelOptions { SingleReader = true });

    public RemovedItemsRecorder(
        ILibraryManager libraryManager,
        AddedDateStore store,
        AddedDateResolver resolver,
        AddedDateApplier applier,
        ILogger<RemovedItemsRecorder> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _resolver = resolver;
        _applier = applier;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _store.PruneRemoved(DateTime.UtcNow - TimeSpan.FromDays(Plugin.Instance!.Configuration.ReplacementWindowDays));
        _libraryManager.ItemRemoved += OnItemRemoved;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var removal in _removals.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await UpdateRefreshedSuccessorsAsync(removal, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) // an exception escaping ExecuteAsync stops Jellyfin
            {
                _logger.LogError(ex, "Could not update the items replacing {Name} ({ItemId})", removal.Item.Name, removal.Item.Id);
            }
        }
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

        var removedAt = DateTime.UtcNow;
        var window = TimeSpan.FromDays(Plugin.Instance!.Configuration.ReplacementWindowDays);
        _store.AddRemoved(keys, new RemovedEntry(item.DateCreated, removedAt, item.GetBaseItemKind()), expiredBefore: removedAt - window);
        if (SuccessorQuery(item) is { } query)
        {
            _removals.Writer.TryWrite(new Removal(item, keys, query));
        }
    }

    private async Task UpdateRefreshedSuccessorsAsync(Removal removal, CancellationToken cancellationToken)
    {
        var successors = _libraryManager.GetItemList(removal.Query)
            .Where(item => item.DateLastRefreshed != DateTime.MinValue // items that already have had their first refresh
                && ItemIdentity.ContentKeys(item).Intersect(removal.Keys).Any())
            .ToList();
        if (successors.Count == 0)
        {
            return;
        }

        var decisions = await _resolver.ResolveAsync(successors, cancellationToken).ConfigureAwait(false);
        var reason = string.Create(CultureInfo.InvariantCulture, $"after {removal.Item.Name} ({removal.Item.Id}) was removed");
        foreach (var successor in successors)
        {
            if (decisions.TryGetValue(successor.Id, out var decision))
            {
                await _applier.ApplyAsync(successor, decision, reason, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed record Removal(BaseItem Item, IReadOnlyList<string> Keys, InternalItemsQuery Query);
}
