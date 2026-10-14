using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.OriginalDateAdded;

public static class ItemIdentity
{
    public static bool IsSupported(BaseItem item) => item is Movie or Episode && !item.IsVirtualItem;

    public static bool HasBeenRefreshed(BaseItem item) => item.DateLastRefreshed != DateTime.MinValue;

    // GetUserDataKeys() always ends with the item's own ID, which changes when the file is replaced.
    public static IReadOnlyList<string> ContentKeys(BaseItem item)
    {
        var ownKey = item.Id.ToString();
        return item.GetUserDataKeys().Where(key => key != ownKey).ToList();
    }

    public static string Describe(BaseItem item) => item switch
    {
        Episode { ParentIndexNumber: { } season, IndexNumber: { } number } episode
            => string.Create(CultureInfo.InvariantCulture, $"{episode.SeriesName} S{season:00}E{number:00}"),
        { ProductionYear: { } year } => string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({year})"),
        _ => item.Name,
    };

    public static InternalItemsQuery? SameContentQuery(BaseItem item)
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
}
