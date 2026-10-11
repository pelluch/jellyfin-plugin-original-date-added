using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

internal static class PluginSettings
{
    public static bool DryRun => Plugin.Instance!.Configuration.DryRun;

    public static LogLevel DetailLevel => Plugin.Instance!.Configuration.VerboseLogging ? LogLevel.Information : LogLevel.Debug;
}
