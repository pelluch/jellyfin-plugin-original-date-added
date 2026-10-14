using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrSource : IAddedDateSource
{
    private readonly ArrClient _client;
    private readonly SourceAvailability _availability;
    private static readonly TimeSpan _maxAgeAfterMiss = TimeSpan.FromMinutes(1);

    private readonly ResponseCache _cache;
    private readonly ConcurrentDictionary<string, KnownInstance> _lastKnown = new(StringComparer.Ordinal);

    public ArrSource(ArrClient client, SourceAvailability availability)
        : this(client, availability, TimeProvider.System)
    {
    }

    public ArrSource(ArrClient client, SourceAvailability availability, TimeProvider timeProvider)
    {
        _client = client;
        _availability = availability;
        _cache = new ResponseCache(TimeSpan.FromMinutes(5), timeProvider);
    }

    public string Name => "arr";

    public bool CanAnswerAuthoritatively => true;

    public async Task<SourceAnswers> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var config = PluginSettings.Configuration;
        if (!config.ArrEnabled)
        {
            return SourceAnswers.None;
        }

        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        var failures = new Dictionary<Guid, string>();
        var remaining = items.Select(sourceItem => sourceItem.Item).Where(item => ItemIdentity.IsSupported(item) && !string.IsNullOrEmpty(item.Path)).ToList();
        foreach (var instance in config.ArrInstances.Where(instance => instance.Url.Length > 0 && instance.ApiKey.Length > 0))
        {
            if (remaining.Count == 0)
            {
                break;
            }

            string? app = null;
            try
            {
                app = (await FetchAsync(instance, "status " + instance.Url, token => _client.GetStatusAsync(instance, token), cancellationToken).ConfigureAwait(false)).AppName;
                var rootFolders = await FetchAsync(instance, "root folders " + CacheKey(instance), token => _client.GetRootFoldersAsync(instance, token), cancellationToken).ConfigureAwait(false);
                var rootFolderPaths = rootFolders.Select(folder => folder.Path).ToList();
                _lastKnown[instance.Url] = new KnownInstance(app, rootFolderPaths);
                switch (app)
                {
                    case "Sonarr":
                        await FindEpisodesAsync(instance, app, rootFolderPaths, remaining.OfType<Episode>().ToList(), candidates, cancellationToken).ConfigureAwait(false);
                        break;
                    case "Radarr":
                        await FindMoviesAsync(instance, app, rootFolderPaths, remaining.OfType<Movie>().ToList(), candidates, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (SourceException)
            {
                var known = _lastKnown.GetValueOrDefault(instance.Url);
                app ??= known?.App;
                var unanswered = remaining.Where(item => !candidates.ContainsKey(item.Id) && MayManage(app, known?.RootFolders, item)).ToList();
                var reason = string.Create(CultureInfo.InvariantCulture, $"{app ?? "Sonarr/Radarr"} at {instance.Url} is unavailable");
                foreach (var item in unanswered)
                {
                    failures.TryAdd(item.Id, reason);
                }
            }

            remaining.RemoveAll(item => candidates.ContainsKey(item.Id));
        }

        foreach (var itemId in candidates.Keys)
        {
            failures.Remove(itemId); // answered by another instance
        }

        return new SourceAnswers(candidates, failures);
    }

    private static bool MayManage(string? app, IReadOnlyList<string>? rootFolders, BaseItem item)
    {
        var isItsKind = app switch
        {
            "Sonarr" => item is Episode,
            "Radarr" => item is Movie,
            _ => true,
        };
        return isItsKind && (rootFolders is null || rootFolders.Any(folder => ArrPaths.IsWithin(item.Path, folder)));
    }

    private static string CacheKey(ArrConnection instance) =>
        instance.Url + string.Concat(instance.PathMappings.Select(mapping => " " + mapping.ArrPath + "=>" + mapping.JellyfinPath));

    private static AddedDateCandidate ToCandidate(string app, BaseItem item, ArrLineageStart start)
    {
        var detail = start.IsImport
            ? string.Create(CultureInfo.InvariantCulture, $"{app} {ItemIdentity.Describe(item)}, imported {start.Date:u}")
            : string.Create(CultureInfo.InvariantCulture, $"{app} {ItemIdentity.Describe(item)}, had a file by {start.Date:u} without an import record");
        if (start.PreviousFileDeletedAt is { } deletedAt)
        {
            detail += string.Create(CultureInfo.InvariantCulture, $", {(start.Date - deletedAt).TotalDays:F0} days after the previous file was deleted");
        }

        return new AddedDateCandidate(start.Date, null, detail, IsAuthoritative: start.IsImport);
    }

    private async Task FindMoviesAsync(ArrConnection instance, string app, List<string> rootFolders, List<Movie> movies, Dictionary<Guid, AddedDateCandidate> found, CancellationToken cancellationToken)
    {
        if (movies.Count == 0)
        {
            return;
        }

        var byPath = await FetchMoviesAsync(instance, null, cancellationToken).ConfigureAwait(false);
        foreach (var movie in movies)
        {
            TimeSpan? maxAge = null;
            if (!byPath.ContainsKey(movie.Path) && rootFolders.Any(folder => ArrPaths.IsWithin(movie.Path, folder)))
            {
                maxAge = _maxAgeAfterMiss; // imported since the cached list was fetched
                byPath = await FetchMoviesAsync(instance, maxAge, cancellationToken).ConfigureAwait(false);
            }

            if (byPath.TryGetValue(movie.Path, out var arrMovie))
            {
                var history = await FetchAsync(
                        instance,
                        string.Create(CultureInfo.InvariantCulture, $"movie history {instance.Url} {arrMovie.MovieId}"),
                        token => _client.GetMovieHistoryAsync(instance, arrMovie.MovieId, token),
                        cancellationToken,
                        maxAge)
                    .ConfigureAwait(false);
                found[movie.Id] = ToCandidate(app, movie, ArrLineage.FindStart(history, arrMovie.File, PluginSettings.ReplacementWindow));
            }
        }
    }

    private Task<Dictionary<string, (int MovieId, ArrFile File)>> FetchMoviesAsync(ArrConnection instance, TimeSpan? maxAge, CancellationToken cancellationToken) =>
        FetchAsync(
            instance,
            "movies " + CacheKey(instance),
            async token => IndexByFilePath(await _client.GetMoviesAsync(instance, token).ConfigureAwait(false)),
            cancellationToken,
            maxAge);

    private static Dictionary<string, (int MovieId, ArrFile File)> IndexByFilePath(IReadOnlyList<ArrMovie> movies)
    {
        var byPath = new Dictionary<string, (int MovieId, ArrFile File)>(StringComparer.Ordinal);
        foreach (var movie in movies)
        {
            if (movie.MovieFile is { } file)
            {
                byPath.TryAdd(file.Path, (movie.Id, file));
            }
        }

        return byPath;
    }

    private async Task FindEpisodesAsync(ArrConnection instance, string app, List<string> rootFolders, List<Episode> episodes, Dictionary<Guid, AddedDateCandidate> found, CancellationToken cancellationToken)
    {
        if (episodes.Count == 0)
        {
            return;
        }

        var allSeries = await FetchSeriesAsync(instance, null, cancellationToken).ConfigureAwait(false);
        foreach (var episode in episodes)
        {
            TimeSpan? maxAge = null;
            var show = FindShow(allSeries, episode);
            if (show is null && rootFolders.Any(folder => ArrPaths.IsWithin(episode.Path, folder)))
            {
                maxAge = _maxAgeAfterMiss; // a series added since the cached list was fetched
                allSeries = await FetchSeriesAsync(instance, maxAge, cancellationToken).ConfigureAwait(false);
                show = FindShow(allSeries, episode);
            }

            if (show is null)
            {
                continue;
            }

            var key = string.Create(CultureInfo.InvariantCulture, $"{CacheKey(instance)} {show.Id}");
            var files = await FetchAsync(instance, "episode files " + key, token => _client.GetEpisodeFilesAsync(instance, show.Id, token), cancellationToken, maxAge).ConfigureAwait(false);
            var file = files.FirstOrDefault(file => file.Path == episode.Path);
            if (file is null && maxAge is null)
            {
                maxAge = _maxAgeAfterMiss; // imported since the cached files were fetched
                files = await FetchAsync(instance, "episode files " + key, token => _client.GetEpisodeFilesAsync(instance, show.Id, token), cancellationToken, maxAge).ConfigureAwait(false);
                file = files.FirstOrDefault(file => file.Path == episode.Path);
            }

            if (file is null)
            {
                continue;
            }

            var arrEpisodes = await FetchAsync(instance, "episodes " + key, token => _client.GetEpisodesAsync(instance, show.Id, token), cancellationToken, maxAge).ConfigureAwait(false);
            var history = await FetchAsync(instance, "series history " + key, token => _client.GetSeriesHistoryAsync(instance, show.Id, token), cancellationToken, maxAge).ConfigureAwait(false);
            var start = arrEpisodes
                .Where(arrEpisode => arrEpisode.EpisodeFileId == file.Id)
                .Select(arrEpisode => ArrLineage.FindStart(history.Where(e => e.ItemId == arrEpisode.Id), file, PluginSettings.ReplacementWindow))
                .OrderBy(lineage => lineage.Date)
                .ThenBy(lineage => !lineage.IsImport)
                .FirstOrDefault();
            if (start is not null)
            {
                found[episode.Id] = ToCandidate(app, episode, start); // a file with several episodes takes the earliest
            }
        }
    }

    private static ArrSeries? FindShow(IReadOnlyList<ArrSeries> allSeries, Episode episode) =>
        allSeries.Where(series => ArrPaths.IsWithin(episode.Path, series.Path)).MaxBy(series => series.Path.Length);

    private Task<IReadOnlyList<ArrSeries>> FetchSeriesAsync(ArrConnection instance, TimeSpan? maxAge, CancellationToken cancellationToken) =>
        FetchAsync(instance, "series " + CacheKey(instance), token => _client.GetSeriesAsync(instance, token), cancellationToken, maxAge);

    private Task<T> FetchAsync<T>(ArrConnection instance, string cacheKey, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken, TimeSpan? maxAge = null)
    {
        var service = _lastKnown.TryGetValue(instance.Url, out var known) ? known.App + " at " + instance.Url : instance.Url;
        return _cache.GetAsync(cacheKey, token => _availability.CallAsync("arr " + instance.Url, service, fetch, token), cancellationToken, maxAge);
    }

    private sealed record KnownInstance(string App, IReadOnlyList<string> RootFolders);
}
