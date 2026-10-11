using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OriginalDateAdded.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public int ReplacementWindowDays { get; set; } = 7;

    public bool DryRun { get; set; }

    public bool VerboseLogging { get; set; }
}
