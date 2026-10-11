using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public sealed record TracearrCopy(string ServerId, string? RatingKey, DateTime AddedAt, DateTime? RemovedAt);
