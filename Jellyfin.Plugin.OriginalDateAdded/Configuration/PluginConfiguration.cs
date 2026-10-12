using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OriginalDateAdded.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public int ReplacementWindowDays { get; set; } = 7;

    public int ItemsToCheck { get; set; } = 500;

    public bool DryRun { get; set; }

    public bool VerboseLogging { get; set; }

    public bool TracearrEnabled { get; set; }

    public string TracearrUrl { get; set; } = string.Empty;

    public string TracearrApiKey { get; set; } = string.Empty;

    public string TracearrServerId { get; set; } = string.Empty;

    public string TracearrServerName { get; set; } = string.Empty;

    public bool JellystatEnabled { get; set; }

    public string JellystatUrl { get; set; } = string.Empty;

    public string JellystatApiKey { get; set; } = string.Empty;
}
