using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public interface IAddedDateSource
{
    string Name { get; }

    bool CanAnswerAuthoritatively { get; }

    // A failure stops querying other sources
    // for this item
    Task<SourceAnswers> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken);
}
