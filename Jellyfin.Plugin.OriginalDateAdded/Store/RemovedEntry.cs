using System;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

public sealed record RemovedEntry(DateTime AddedAt, DateTime RemovedAt, BaseItemKind ItemType);
