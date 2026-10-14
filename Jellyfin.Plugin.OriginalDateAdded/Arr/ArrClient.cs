using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public class ArrClient
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Numbers from https://github.com/Sonarr/Sonarr/blob/develop/src/NzbDrone.Core/History/EpisodeHistory.cs
    private static readonly (int EventType, ArrEventKind Kind)[] _sonarrHistory = [(3, ArrEventKind.Imported), (5, ArrEventKind.Deleted), (6, ArrEventKind.Renamed)];

    // Numbers from https://github.com/Radarr/Radarr/blob/develop/src/NzbDrone.Core/History/History.cs
    private static readonly (int EventType, ArrEventKind Kind)[] _radarrHistory = [(3, ArrEventKind.Imported), (6, ArrEventKind.Deleted), (8, ArrEventKind.Renamed)];

    private readonly IHttpClientFactory _httpClientFactory;

    public ArrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ArrStatus> GetStatusAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        var status = await GetAsync<StatusDto>(connection, "system/status", cancellationToken).ConfigureAwait(false);
        return new ArrStatus(status.AppName, status.Version);
    }

    // Paths of folders and files come back as Jellyfin sees them, while history paths stay as the arr wrote them.
    public async Task<IReadOnlyList<ArrRootFolder>> GetRootFoldersAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        var folders = await GetAsync<List<RootFolderDto>>(connection, "rootfolder", cancellationToken).ConfigureAwait(false);
        return folders.Select(folder => new ArrRootFolder(folder.Path, ArrPaths.Map(folder.Path, connection.PathMappings))).ToList();
    }

    public async Task<IReadOnlyList<ArrSeries>> GetSeriesAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        var series = await GetAsync<List<ArrSeries>>(connection, "series", cancellationToken).ConfigureAwait(false);
        return series.Select(show => show with { Path = ArrPaths.Map(show.Path, connection.PathMappings) }).ToList();
    }

    public async Task<IReadOnlyList<ArrFile>> GetEpisodeFilesAsync(ArrConnection connection, int seriesId, CancellationToken cancellationToken)
    {
        var files = await GetAsync<List<ArrFile>>(connection, string.Create(CultureInfo.InvariantCulture, $"episodefile?seriesId={seriesId}"), cancellationToken).ConfigureAwait(false);
        return files.Select(file => file with { Path = ArrPaths.Map(file.Path, connection.PathMappings) }).ToList();
    }

    public async Task<IReadOnlyList<ArrEpisode>> GetEpisodesAsync(ArrConnection connection, int seriesId, CancellationToken cancellationToken)
        => await GetAsync<List<ArrEpisode>>(connection, string.Create(CultureInfo.InvariantCulture, $"episode?seriesId={seriesId}"), cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ArrMovie>> GetMoviesAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        var movies = await GetAsync<List<ArrMovie>>(connection, "movie", cancellationToken).ConfigureAwait(false);
        return movies.Select(movie => movie.MovieFile is { } file ? movie with { MovieFile = file with { Path = ArrPaths.Map(file.Path, connection.PathMappings) } } : movie).ToList();
    }

    public Task<IReadOnlyList<ArrEvent>> GetSeriesHistoryAsync(ArrConnection connection, int seriesId, CancellationToken cancellationToken)
        => GetHistoryAsync(connection, string.Create(CultureInfo.InvariantCulture, $"history/series?seriesId={seriesId}"), _sonarrHistory, record => record.EpisodeId, cancellationToken);

    public Task<IReadOnlyList<ArrEvent>> GetMovieHistoryAsync(ArrConnection connection, int movieId, CancellationToken cancellationToken)
        => GetHistoryAsync(connection, string.Create(CultureInfo.InvariantCulture, $"history/movie?movieId={movieId}"), _radarrHistory, record => record.MovieId, cancellationToken);

    private static ArrEvent ToEvent(HistoryDto record, ArrEventKind kind, int itemId)
    {
        var data = record.Data ?? [];
        return kind switch
        {
            ArrEventKind.Imported => new ArrEvent(record.Date, kind, itemId, data.GetValueOrDefault("importedPath") ?? string.Empty, null, null),
            ArrEventKind.Deleted => new ArrEvent(record.Date, kind, itemId, record.SourceTitle, null, data.GetValueOrDefault("reason")),
            _ => new ArrEvent(record.Date, kind, itemId, data.GetValueOrDefault("path") ?? string.Empty, data.GetValueOrDefault("sourcePath"), null),
        };
    }

    private async Task<IReadOnlyList<ArrEvent>> GetHistoryAsync(
        ArrConnection connection,
        string path,
        (int EventType, ArrEventKind Kind)[] eventTypes,
        Func<HistoryDto, int> itemId,
        CancellationToken cancellationToken)
    {
        var events = new List<ArrEvent>();
        foreach (var (eventType, kind) in eventTypes)
        {
            var records = await GetAsync<List<HistoryDto>>(connection, string.Create(CultureInfo.InvariantCulture, $"{path}&eventType={eventType}"), cancellationToken).ConfigureAwait(false);
            events.AddRange(records.Select(record => ToEvent(record, kind, itemId(record))));
        }

        return events;
    }

    private async Task<T> GetAsync<T>(ArrConnection connection, string path, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(connection.Url.TrimEnd('/') + "/api/v3/" + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArrException("The URL is not a valid http(s) URL");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("X-Api-Key", connection.ApiKey);

        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ArrException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The API key was rejected",
                    HttpStatusCode.NotFound => "There is no Sonarr or Radarr API at this URL",
                    _ => string.Create(CultureInfo.InvariantCulture, $"The server returned HTTP {(int)response.StatusCode}"),
                })
                {
                    IsTransient = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests,
                };
            }

            return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new ArrException("The response was empty");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArrException(string.Create(CultureInfo.InvariantCulture, $"No response within {_timeout.TotalSeconds} seconds")) { IsTransient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new ArrException("Could not connect: " + ex.Message, ex) { IsTransient = true };
        }
        catch (JsonException ex)
        {
            throw new ArrException("The response isn't from Sonarr's or Radarr's API", ex);
        }
    }

    private sealed record StatusDto(string AppName, string Version);

    private sealed record RootFolderDto(string Path);

    private sealed record HistoryDto(DateTime Date, int EpisodeId, int MovieId, string SourceTitle, Dictionary<string, string?>? Data);
}
