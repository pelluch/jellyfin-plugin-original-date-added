using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public sealed record JellystatLibraryItem(string Id, string? Name, string? Type, int? ProductionYear, DateTime? DateCreated);
