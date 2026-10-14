using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public static class ArrPaths
{
    public static string FileName(string path) => Path.GetFileName(Normalize(path));

    public static bool IsWithin(string path, string folder)
    {
        var normalized = Normalize(path);
        var prefix = Normalize(folder);
        return normalized == prefix || normalized.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    public static ArrPathMapping? FindMapping(string arrPath, IEnumerable<ArrPathMapping> mappings) =>
        mappings
            .Where(mapping => mapping.ArrPath.Length > 0 && mapping.JellyfinPath.Length > 0 && IsWithin(arrPath, mapping.ArrPath))
            .MaxBy(mapping => Normalize(mapping.ArrPath).Length);

    public static string Map(string arrPath, IEnumerable<ArrPathMapping> mappings)
    {
        if (FindMapping(arrPath, mappings) is not { } mapping)
        {
            return arrPath;
        }

        var rest = Normalize(arrPath)[Normalize(mapping.ArrPath).Length..];
        return (Normalize(mapping.JellyfinPath) + rest).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
