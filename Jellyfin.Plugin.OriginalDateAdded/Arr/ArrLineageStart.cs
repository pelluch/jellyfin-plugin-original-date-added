using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public sealed record ArrLineageStart(DateTime Date, bool IsImport, DateTime? PreviousFileDeletedAt);
