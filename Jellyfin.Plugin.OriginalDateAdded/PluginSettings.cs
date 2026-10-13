using System;
using Jellyfin.Plugin.OriginalDateAdded.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

internal static class PluginSettings
{
    public static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public static bool DryRun => Configuration.DryRun;

    public static LogLevel DetailLevel => Configuration.VerboseLogging ? LogLevel.Information : LogLevel.Debug;

    // Dates closer than this count as the same
    public static TimeSpan MinimumDifference => TimeSpan.FromMinutes(Configuration.MinimumDifferenceMinutes);

    // A copy appearing more than this after its predecessor's removal counts as new content
    public static TimeSpan ReplacementWindow => Configuration.ReplacementWindowDays > 0 ? TimeSpan.FromDays(Configuration.ReplacementWindowDays) : TimeSpan.MaxValue;
}
