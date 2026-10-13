using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public sealed record ArrEvent(DateTime Date, ArrEventKind Kind, int ItemId, string Path, string? SourcePath, string? Reason);
