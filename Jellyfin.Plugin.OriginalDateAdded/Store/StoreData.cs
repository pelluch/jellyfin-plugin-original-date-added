using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

internal sealed class StoreData
{
    public Dictionary<Guid, ItemEntry> Items { get; init; } = [];

    public Dictionary<string, RemovedEntry> Removed { get; init; } = [];
}
