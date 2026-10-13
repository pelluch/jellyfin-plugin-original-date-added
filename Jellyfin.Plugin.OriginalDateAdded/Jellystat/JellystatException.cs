using System;
using Jellyfin.Plugin.OriginalDateAdded.Sources;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public class JellystatException : SourceException
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
