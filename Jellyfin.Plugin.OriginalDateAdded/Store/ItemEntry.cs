using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

public sealed record ItemEntry(DateTime Date, string Source, DateTime Original);
