using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public interface IAddedDateSource
{
    string Name { get; }

    bool UseDuringRefresh { get; }

    Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken);
}
