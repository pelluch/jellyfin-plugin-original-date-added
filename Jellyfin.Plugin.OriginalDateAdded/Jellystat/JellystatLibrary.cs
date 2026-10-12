using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public sealed record JellystatLibrary(string Id, string? ServerId, string? CollectionType, bool Archived)
{
    public bool BelongsTo(string serverId) => !Archived && string.Equals(ServerId, serverId, StringComparison.OrdinalIgnoreCase);
}
