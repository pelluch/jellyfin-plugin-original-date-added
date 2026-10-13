using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrTester
{
    private readonly ArrClient _client;
    private readonly ILibraryManager _libraryManager;

    public ArrTester(ArrClient client, ILibraryManager libraryManager)
    {
        _client = client;
        _libraryManager = libraryManager;
    }

    public async Task<ArrTestResult> TestAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var status = await _client.GetStatusAsync(connection, cancellationToken).ConfigureAwait(false);
            if (status.AppName is not ("Sonarr" or "Radarr"))
            {
                return new ArrTestResult(false, string.Create(CultureInfo.InvariantCulture, $"Connected, but this is {status.AppName}, not Sonarr or Radarr"));
            }

            var connected = string.Create(CultureInfo.InvariantCulture, $"Connected to {status.AppName} {status.Version}.");
            var rootFolders = await _client.GetRootFoldersAsync(connection, cancellationToken).ConfigureAwait(false);
            if (rootFolders.Count == 0)
            {
                return new ArrTestResult(false, connected + " It has no root folders, so it manages no files.");
            }

            var libraryFolders = _libraryManager.GetVirtualFolders().SelectMany(library => library.Locations).ToList();
            var unmatched = rootFolders
                .Where(root => !libraryFolders.Any(folder => ArrPaths.IsWithin(root, folder) || ArrPaths.IsWithin(folder, root)))
                .ToList();
            if (unmatched.Count == rootFolders.Count)
            {
                return new ArrTestResult(
                    false,
                    string.Create(CultureInfo.InvariantCulture, $"{connected} None of its root folders ({string.Join(", ", unmatched)}) are in a Jellyfin library. Currently, the plugin requires Jellyfin seeing them at the same path."));
            }

            if (unmatched.Count > 0)
            {
                return new ArrTestResult(
                    true,
                    string.Create(CultureInfo.InvariantCulture, $"{connected} These root folders aren't in a Jellyfin library, so their files won't be matched: {string.Join(", ", unmatched)}."));
            }

            return new ArrTestResult(true, connected);
        }
        catch (ArrException ex)
        {
            return new ArrTestResult(false, ex.Message);
        }
    }
}
