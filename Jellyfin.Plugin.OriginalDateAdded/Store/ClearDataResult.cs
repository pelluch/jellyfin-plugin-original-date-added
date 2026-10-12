namespace Jellyfin.Plugin.OriginalDateAdded.Store;

public sealed record ClearDataResult(int Items, int Removed, string? BackupFile);
