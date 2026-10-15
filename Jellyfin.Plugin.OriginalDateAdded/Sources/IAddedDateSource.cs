using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public interface IAddedDateSource
{
    string Name { get; }

    Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken);
}
