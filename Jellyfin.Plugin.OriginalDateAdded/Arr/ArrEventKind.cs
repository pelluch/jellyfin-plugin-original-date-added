namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

// The order replays events with the same time: an upgrade's deletion, then its import.
public enum ArrEventKind
{
    Deleted,
    Imported,
    Renamed,
}
