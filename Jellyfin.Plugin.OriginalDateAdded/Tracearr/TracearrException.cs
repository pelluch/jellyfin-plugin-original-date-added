using System;
using Jellyfin.Plugin.OriginalDateAdded.Sources;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public class TracearrException : SourceException
{
    public TracearrException()
    {
    }

    public TracearrException(string message)
        : base(message)
    {
    }

    public TracearrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
