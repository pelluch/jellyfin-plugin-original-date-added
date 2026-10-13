using Jellyfin.Plugin.OriginalDateAdded.Store;

namespace Jellyfin.Plugin.OriginalDateAdded;

public sealed record AddedDateDecision(ItemEntry Entry, string Detail);
