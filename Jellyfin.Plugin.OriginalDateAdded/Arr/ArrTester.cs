using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrTester
{
    private const int SamplesPerRootFolder = 3;

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

            var mappings = connection.PathMappings.Where(mapping => mapping.ArrPath.Length > 0 && mapping.JellyfinPath.Length > 0).ToList();
            var unusedMappings = mappings
                .Where(mapping => !rootFolders.Any(root => ArrPaths.IsWithin(root.ArrPath, mapping.ArrPath) || ArrPaths.IsWithin(mapping.ArrPath, root.ArrPath)))
                .Select(mapping => string.Create(CultureInfo.InvariantCulture, $"The path mapping for {mapping.ArrPath} matches none of its root folders."))
                .ToList();
            var samples = await GetSamplesAsync(connection, status.AppName, rootFolders, cancellationToken).ConfigureAwait(false);
            var libraryFolders = _libraryManager.GetVirtualFolders().SelectMany(library => library.Locations).ToList();

            var verified = 0;
            var mappedFailed = false;
            var unmappedFailed = false;
            var notes = new List<string>(unusedMappings);
            foreach (var root in rootFolders)
            {
                var isMapped = ArrPaths.FindMapping(root.ArrPath, mappings) is not null;
                var where = isMapped ? string.Create(CultureInfo.InvariantCulture, $"{root.ArrPath} (mapped to {root.Path})") : root.ArrPath;
                var hint = isMapped ? "Check its path mapping." : "If Jellyfin sees these files at another path, add a path mapping.";
                var files = samples[root];
                if (files.Any(file => _libraryManager.FindByPath(file, false) is not null))
                {
                    verified++;
                    continue;
                }

                if (!libraryFolders.Any(folder => ArrPaths.IsWithin(root.Path, folder) || ArrPaths.IsWithin(folder, root.Path)))
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"{where} isn't in a Jellyfin library, so its files won't be matched. {hint}"));
                }
                else if (files.Count == 0)
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"{where} has no files yet, so it couldn't be checked."));
                    continue;
                }
                else
                {
                    notes.Add(string.Create(CultureInfo.InvariantCulture, $"Jellyfin has none of the {files.Count} files checked in {where}, e.g. {files[0]}. {hint}"));
                }

                mappedFailed |= isMapped;
                unmappedFailed |= !isMapped;
            }

            if (verified > 0)
            {
                notes.Insert(0, string.Create(CultureInfo.InvariantCulture, $"Jellyfin has its files in {verified} of {rootFolders.Count} root folder(s)."));
            }

            var success = !mappedFailed && unusedMappings.Count == 0 && (verified > 0 || !unmappedFailed);
            return new ArrTestResult(success, string.Join(" ", notes.Prepend(connected)));
        }
        catch (ArrException ex)
        {
            return new ArrTestResult(false, ex.Message);
        }
    }

    private async Task<Dictionary<ArrRootFolder, List<string>>> GetSamplesAsync(ArrConnection connection, string app, IReadOnlyList<ArrRootFolder> rootFolders, CancellationToken cancellationToken)
    {
        var samples = rootFolders.ToDictionary(root => root, _ => new List<string>());
        if (app == "Radarr")
        {
            var files = (await _client.GetMoviesAsync(connection, cancellationToken).ConfigureAwait(false))
                .Select(movie => movie.MovieFile?.Path)
                .OfType<string>()
                .ToList();
            foreach (var root in rootFolders)
            {
                samples[root].AddRange(files.Where(file => ArrPaths.IsWithin(file, root.Path)).Take(SamplesPerRootFolder));
            }

            return samples;
        }

        var allSeries = await _client.GetSeriesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var root in rootFolders)
        {
            foreach (var show in allSeries.Where(series => ArrPaths.IsWithin(series.Path, root.Path)).Take(SamplesPerRootFolder))
            {
                var files = await _client.GetEpisodeFilesAsync(connection, show.Id, cancellationToken).ConfigureAwait(false);
                if (files.Count > 0)
                {
                    samples[root].Add(files[0].Path);
                }
            }
        }

        return samples;
    }
}
