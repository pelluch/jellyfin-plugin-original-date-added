using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public class TracearrException : Exception
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
