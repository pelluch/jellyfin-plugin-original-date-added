using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrSource : IAddedDateSource
{
    private readonly ArrClient _client;
    private readonly ILogger<ArrSource> _logger;
    private readonly ResponseCache _cache = new(TimeSpan.FromMinutes(5));

    public ArrSource(ArrClient client, ILogger<ArrSource> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "arr";

    public async Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var config = PluginSettings.Configuration;
        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        if (!config.ArrEnabled)
        {
            return candidates;
        }

        var remaining = items.Select(sourceItem => sourceItem.Item).Where(item => ItemIdentity.IsSupported(item) && !string.IsNullOrEmpty(item.Path)).ToList();
        foreach (var instance in config.ArrInstances.Where(instance => instance.Url.Length > 0 && instance.ApiKey.Length > 0))
        {
            if (remaining.Count == 0)
            {
                break;
            }

            try
            {
                var status = await _cache.GetAsync("status " + instance.Url, token => _client.GetStatusAsync(instance, token), cancellationToken).ConfigureAwait(false);
                var found = status.AppName switch
                {
                    "Sonarr" => await FindEpisodesAsync(instance, status.AppName, remaining.OfType<Episode>().ToList(), cancellationToken).ConfigureAwait(false),
                    "Radarr" => await FindMoviesAsync(instance, status.AppName, remaining.OfType<Movie>().ToList(), cancellationToken).ConfigureAwait(false),
                    _ => [],
                };
                foreach (var (itemId, candidate) in found)
                {
                    candidates[itemId] = candidate;
                }

                remaining.RemoveAll(item => found.ContainsKey(item.Id));
            }
            catch (ArrException ex)
            {
                _logger.LogWarning("{Url} unavailable, skipping it for {Count} item(s): {Message}", instance.Url, remaining.Count, ex.Message);
            }
        }

        return candidates;
    }

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

    private async Task<Dictionary<Guid, AddedDateCandidate>> FindMoviesAsync(ArrConnection instance, string app, List<Movie> movies, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, AddedDateCandidate>();
        if (movies.Count == 0)
        {
            return found;
        }

        var byPath = await _cache.GetAsync(
                "movies " + instance.Url,
                async token => IndexByFilePath(await _client.GetMoviesAsync(instance, token).ConfigureAwait(false)),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var movie in movies)
        {
            if (byPath.TryGetValue(movie.Path, out var arrMovie))
            {
                var history = await _cache.GetAsync(
                        string.Create(CultureInfo.InvariantCulture, $"movie history {instance.Url} {arrMovie.MovieId}"),
                        token => _client.GetMovieHistoryAsync(instance, arrMovie.MovieId, token),
                        cancellationToken)
                    .ConfigureAwait(false);
                found[movie.Id] = ToCandidate(app, movie, ArrLineage.FindStart(history, arrMovie.File, PluginSettings.ReplacementWindow));
            }
        }

        return found;
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

    private async Task<Dictionary<Guid, AddedDateCandidate>> FindEpisodesAsync(ArrConnection instance, string app, List<Episode> episodes, CancellationToken cancellationToken)
    {
        var found = new Dictionary<Guid, AddedDateCandidate>();
        if (episodes.Count == 0)
        {
            return found;
        }

        var allSeries = await _cache.GetAsync("series " + instance.Url, token => _client.GetSeriesAsync(instance, token), cancellationToken).ConfigureAwait(false);
        foreach (var episode in episodes)
        {
            var show = allSeries.Where(series => ArrPaths.IsWithin(episode.Path, series.Path)).MaxBy(series => series.Path.Length);
            if (show is null)
            {
                continue;
            }

            var key = string.Create(CultureInfo.InvariantCulture, $"{instance.Url} {show.Id}");
            var files = await _cache.GetAsync("episode files " + key, token => _client.GetEpisodeFilesAsync(instance, show.Id, token), cancellationToken).ConfigureAwait(false);
            if (files.FirstOrDefault(file => file.Path == episode.Path) is not { } file)
            {
                continue;
            }

            var arrEpisodes = await _cache.GetAsync("episodes " + key, token => _client.GetEpisodesAsync(instance, show.Id, token), cancellationToken).ConfigureAwait(false);
            var history = await _cache.GetAsync("series history " + key, token => _client.GetSeriesHistoryAsync(instance, show.Id, token), cancellationToken).ConfigureAwait(false);
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

        return found;
    }
}
