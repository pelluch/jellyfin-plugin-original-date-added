using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private const int ParallelFetches = 5;

    private readonly ArrClient _client;
    private readonly SourceAvailability _availability;
    private readonly ResponseCache _cache = new(TimeSpan.FromMinutes(5));
    private readonly ConcurrentDictionary<string, string> _appNames = new(StringComparer.Ordinal);

    public ArrSource(ArrClient client, SourceAvailability availability)
    {
        _client = client;
        _availability = availability;
    }

    public string Name => "arr";

    public async Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var config = PluginSettings.Configuration;
        if (!config.ArrEnabled)
        {
            return ReadOnlyDictionary<Guid, AddedDateCandidate>.Empty;
        }

        var candidates = new ConcurrentDictionary<Guid, AddedDateCandidate>();
        var remaining = items.Select(sourceItem => sourceItem.Item).Where(item => ItemIdentity.IsSupported(item) && !string.IsNullOrEmpty(item.Path)).ToList();
        foreach (var instance in config.ArrInstances.Where(instance => instance.Url.Length > 0 && instance.ApiKey.Length > 0))
        {
            if (remaining.Count == 0)
            {
                break;
            }

            try
            {
                var app = (await FetchAsync(instance, "status " + instance.Url, token => _client.GetStatusAsync(instance, token), cancellationToken).ConfigureAwait(false)).AppName;
                _appNames[instance.Url] = app;
                switch (app)
                {
                    case "Sonarr":
                        await FindEpisodesAsync(instance, app, remaining.OfType<Episode>().ToList(), candidates, cancellationToken).ConfigureAwait(false);
                        break;
                    case "Radarr":
                        await FindMoviesAsync(instance, app, remaining.OfType<Movie>().ToList(), candidates, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
            catch (SourceException)
            {
                // Its items get no answer from it this run
            }

            remaining.RemoveAll(item => candidates.ContainsKey(item.Id));
        }

        return candidates;
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

        return new AddedDateCandidate(start.Date, null, detail, IsImport: start.IsImport);
    }

    private static Task ForEachAsync<T>(IEnumerable<T> source, Func<T, CancellationToken, Task> body, CancellationToken cancellationToken)
        => Parallel.ForEachAsync(
            source,
            new ParallelOptions { MaxDegreeOfParallelism = ParallelFetches, CancellationToken = cancellationToken },
            async (element, token) =>
            {
                try
                {
                    await body(element, token).ConfigureAwait(false);
                }
                catch (SourceException)
                {
                }
            });

    private async Task FindMoviesAsync(ArrConnection instance, string app, List<Movie> movies, ConcurrentDictionary<Guid, AddedDateCandidate> found, CancellationToken cancellationToken)
    {
        if (movies.Count == 0)
        {
            return;
        }

        var byPath = await FetchAsync(
                instance,
                "movies " + CacheKey(instance),
                async token => IndexByFilePath(await _client.GetMoviesAsync(instance, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);
        await ForEachAsync(
                movies.Where(movie => byPath.ContainsKey(movie.Path)),
                async (movie, token) =>
                {
                    var (movieId, file) = byPath[movie.Path];
                    var history = await FetchAsync(
                            instance,
                            string.Create(CultureInfo.InvariantCulture, $"movie history {instance.Url} {movieId}"),
                            historyToken => _client.GetMovieHistoryAsync(instance, movieId, historyToken),
                            token)
                        .ConfigureAwait(false);
                    found[movie.Id] = ToCandidate(app, movie, ArrLineage.FindStart(history, file, PluginSettings.ReplacementWindow));
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

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

    private async Task FindEpisodesAsync(ArrConnection instance, string app, List<Episode> episodes, ConcurrentDictionary<Guid, AddedDateCandidate> found, CancellationToken cancellationToken)
    {
        if (episodes.Count == 0)
        {
            return;
        }

        var allSeries = await FetchAsync(instance, "series " + CacheKey(instance), token => _client.GetSeriesAsync(instance, token), cancellationToken).ConfigureAwait(false);
        var byShow = episodes
            .Select(episode => (Episode: episode, Show: allSeries.Where(series => ArrPaths.IsWithin(episode.Path, series.Path)).MaxBy(series => series.Path.Length)))
            .Where(pair => pair.Show is not null)
            .GroupBy(pair => pair.Show!, pair => pair.Episode);
        await ForEachAsync(byShow, (group, token) => FindShowEpisodesAsync(instance, app, group.Key, group.ToList(), found, token), cancellationToken).ConfigureAwait(false);
    }

    private async Task FindShowEpisodesAsync(ArrConnection instance, string app, ArrSeries show, List<Episode> episodes, ConcurrentDictionary<Guid, AddedDateCandidate> found, CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{CacheKey(instance)} {show.Id}");
        var files = await FetchAsync(instance, "episode files " + key, token => _client.GetEpisodeFilesAsync(instance, show.Id, token), cancellationToken).ConfigureAwait(false);
        var matched = new List<(Episode Episode, ArrFile File)>();
        foreach (var episode in episodes)
        {
            if (files.FirstOrDefault(file => file.Path == episode.Path) is { } file)
            {
                matched.Add((episode, file));
            }
        }

        if (matched.Count == 0)
        {
            return;
        }

        var arrEpisodes = await FetchAsync(instance, "episodes " + key, token => _client.GetEpisodesAsync(instance, show.Id, token), cancellationToken).ConfigureAwait(false);
        var history = await FetchAsync(instance, "series history " + key, token => _client.GetSeriesHistoryAsync(instance, show.Id, token), cancellationToken).ConfigureAwait(false);
        foreach (var (episode, file) in matched)
        {
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

    private Task<T> FetchAsync<T>(ArrConnection instance, string cacheKey, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken)
    {
        var service = _appNames.TryGetValue(instance.Url, out var app) ? app + " at " + instance.Url : instance.Url;
        return _cache.GetAsync(cacheKey, token => _availability.CallAsync("arr " + instance.Url, service, fetch, token), cancellationToken);
    }
}
