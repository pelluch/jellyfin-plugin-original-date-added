using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public sealed record JellystatEpisode(string EpisodeId, int? IndexNumber, DateTime? DateCreated);
