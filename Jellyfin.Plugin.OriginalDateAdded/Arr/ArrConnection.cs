namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

// A class with setters rather than a record: the plugin's XML configuration stores a list of these.
public class ArrConnection
{
    public string Url { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
}
