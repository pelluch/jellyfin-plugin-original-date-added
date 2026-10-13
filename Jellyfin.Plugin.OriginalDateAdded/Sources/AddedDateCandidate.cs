using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public sealed record AddedDateCandidate(
    DateTime AddedAt,
    DateTime? PredecessorRemovedAt,
    string Detail,
    bool IsAuthoritative = false);
