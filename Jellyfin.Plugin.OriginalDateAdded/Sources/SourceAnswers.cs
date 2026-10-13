using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public sealed record SourceAnswers(IReadOnlyDictionary<Guid, AddedDateCandidate> Candidates, IReadOnlyDictionary<Guid, string> Failures)
{
    public static SourceAnswers None { get; } = new(new Dictionary<Guid, AddedDateCandidate>(), new Dictionary<Guid, string>());
}
