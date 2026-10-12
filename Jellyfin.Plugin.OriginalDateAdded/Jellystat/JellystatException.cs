using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public class JellystatException : Exception
{
    public JellystatException()
    {
    }

    public JellystatException(string message)
        : base(message)
    {
    }

    public JellystatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
