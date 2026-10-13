using System.Collections.Generic;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public sealed record TracearrTestResult(bool Success, string Message, IReadOnlyList<TracearrServer>? Servers);
