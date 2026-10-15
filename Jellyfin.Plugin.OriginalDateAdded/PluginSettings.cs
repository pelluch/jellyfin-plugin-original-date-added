using System;
using Jellyfin.Plugin.OriginalDateAdded.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

internal static class PluginSettings
{
    // Dates closer than this count as the same
    public static readonly TimeSpan MinimumDifference = TimeSpan.FromMinutes(10);

    public static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public static bool DryRun => Configuration.DryRun;

    public static LogLevel DetailLevel => Configuration.VerboseLogging ? LogLevel.Information : LogLevel.Debug;

    // A copy scanned more than this before or after its predecessor's removal keeps its own date
    public static TimeSpan ReplacementWindow => Configuration.ReplacementWindowDays > 0 ? TimeSpan.FromDays(Configuration.ReplacementWindowDays) : TimeSpan.MaxValue;
}
