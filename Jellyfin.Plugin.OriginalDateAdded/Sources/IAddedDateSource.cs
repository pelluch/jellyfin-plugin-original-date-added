using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public interface IAddedDateSource
{
    string Name { get; }

    // Can only be overridden by other authoritative sources
    bool IsAuthoritative => false;

    Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken);
}
