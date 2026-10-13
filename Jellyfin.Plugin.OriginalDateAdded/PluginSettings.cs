using System;
using Jellyfin.Plugin.OriginalDateAdded.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

internal static class PluginSettings
{
    public static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public static bool DryRun => Configuration.DryRun;

    public static LogLevel DetailLevel => Configuration.VerboseLogging ? LogLevel.Information : LogLevel.Debug;

    // Below this we don't consider updating dates
    public static TimeSpan MinimumDifference => TimeSpan.FromMinutes(Configuration.MinimumDifferenceMinutes);

    public static TimeSpan ReplacementWindow => Configuration.ReplacementWindowDays > 0 ? TimeSpan.FromDays(Configuration.ReplacementWindowDays) : TimeSpan.MaxValue; // 0 days: no limit
}
