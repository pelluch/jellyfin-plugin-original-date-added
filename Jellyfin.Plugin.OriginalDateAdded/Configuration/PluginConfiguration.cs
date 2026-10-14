using System.Collections.ObjectModel;
using Jellyfin.Plugin.OriginalDateAdded.Arr;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.OriginalDateAdded.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public int ReplacementWindowDays { get; set; } = 7;

    public int MinimumDifferenceMinutes { get; set; } = 20;

    public int ItemsToCheck { get; set; } = 500;

    public bool DryRun { get; set; }

    public bool VerboseLogging { get; set; }

    public bool ArrEnabled { get; set; }

    public Collection<ArrConnection> ArrInstances { get; init; } = [];
}
