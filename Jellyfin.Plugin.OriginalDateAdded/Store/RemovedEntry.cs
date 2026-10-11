using System;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

public sealed record RemovedEntry(DateTime Date, DateTime RemovedAt, BaseItemKind ItemType);
