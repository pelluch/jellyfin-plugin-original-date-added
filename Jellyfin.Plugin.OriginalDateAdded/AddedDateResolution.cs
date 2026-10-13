using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalDateAdded;

public sealed record AddedDateResolution(IReadOnlyDictionary<Guid, AddedDateDecision> Decisions, IReadOnlyDictionary<Guid, string> Failures);
