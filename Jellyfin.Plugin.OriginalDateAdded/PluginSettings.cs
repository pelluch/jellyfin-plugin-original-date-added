using System;
using Jellyfin.Plugin.OriginalDateAdded.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

internal static class PluginSettings
{
    public static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public static bool DryRun => Configuration.DryRun;

    public static LogLevel DetailLevel => Configuration.VerboseLogging ? LogLevel.Information : LogLevel.Debug;

    public static TimeSpan ReplacementWindow => TimeSpan.FromDays(Configuration.ReplacementWindowDays);
}
