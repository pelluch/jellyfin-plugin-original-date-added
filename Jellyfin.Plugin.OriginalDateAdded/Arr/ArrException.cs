using System;
using Jellyfin.Plugin.OriginalDateAdded.Sources;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrException : SourceException
{
    public ArrException()
    {
    }

    public ArrException(string message)
        : base(message)
    {
    }

    public ArrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
