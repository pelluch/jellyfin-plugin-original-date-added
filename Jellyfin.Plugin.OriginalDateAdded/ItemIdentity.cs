using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.OriginalDateAdded;

public static class ItemIdentity
{
    public static bool IsSupported(BaseItem item) => item is Movie or Episode && !item.IsVirtualItem;

    // GetUserDataKeys() always ends with the item's own ID, which changes when the file is replaced.
    public static IReadOnlyList<string> ContentKeys(BaseItem item)
    {
        var ownKey = item.Id.ToString();
        return item.GetUserDataKeys().Where(key => key != ownKey).ToList();
    }
}
