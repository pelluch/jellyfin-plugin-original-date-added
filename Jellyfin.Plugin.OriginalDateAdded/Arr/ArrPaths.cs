using System;
using System.IO;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public static class ArrPaths
{
    public static bool IsWithin(string path, string folder)
    {
        var relative = Path.GetRelativePath(folder, path);
        return relative == "." // the folder itself
            || (relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !Path.IsPathRooted(relative)); // on another drive
    }
}
