using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public class SourceException : Exception
{
    public SourceException()
    {
    }

    public SourceException(string message)
        : base(message)
    {
    }

    public SourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    // Worth retrying: timeouts, connection errors, 5xx, rate limits
    public bool IsTransient { get; init; }
}
