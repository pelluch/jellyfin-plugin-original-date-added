using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public sealed record ArrFile(int Id, string Path, DateTime DateAdded);
