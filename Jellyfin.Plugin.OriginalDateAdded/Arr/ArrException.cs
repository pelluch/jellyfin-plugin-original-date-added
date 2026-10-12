using System;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrException : Exception
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
