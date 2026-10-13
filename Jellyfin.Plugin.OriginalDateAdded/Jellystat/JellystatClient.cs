using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public class JellystatClient
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;

    public JellystatClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<IReadOnlyList<JellystatLibrary>> GetLibrariesAsync(JellystatConnection connection, CancellationToken cancellationToken)
        => SendAsync<JellystatLibrary>(connection, "getLibraries", null, cancellationToken);

    public Task<IReadOnlyList<JellystatLibraryItem>> GetLibraryItemsAsync(JellystatConnection connection, string libraryId, CancellationToken cancellationToken)
        => SendAsync<JellystatLibraryItem>(connection, "getLibraryItems", new { libraryid = libraryId }, cancellationToken);

    public Task<IReadOnlyList<JellystatSeason>> GetSeasonsAsync(JellystatConnection connection, string seriesId, CancellationToken cancellationToken)
        => SendAsync<JellystatSeason>(connection, "getSeasons", new { Id = seriesId }, cancellationToken);

    public Task<IReadOnlyList<JellystatEpisode>> GetEpisodesAsync(JellystatConnection connection, string seasonId, CancellationToken cancellationToken)
        => SendAsync<JellystatEpisode>(connection, "getEpisodes", new { Id = seasonId }, cancellationToken);

    private async Task<IReadOnlyList<T>> SendAsync<T>(JellystatConnection connection, string endpoint, object? body, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(connection.Url.TrimEnd('/') + "/api/" + endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new JellystatException("The Jellystat URL is not a valid http(s) URL");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, uri);
        request.Headers.Add("x-api-token", connection.ApiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: _jsonOptions);
        }

        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new JellystatException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Jellystat rejected the API key",
                    HttpStatusCode.NotFound => "Jellystat's API was not found at this URL",
                    _ => string.Create(CultureInfo.InvariantCulture, $"Jellystat returned HTTP {(int)response.StatusCode}"),
                })
                {
                    IsTransient = (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests,
                };
            }

            return await response.Content.ReadFromJsonAsync<List<T>>(_jsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new JellystatException("Jellystat returned an empty response");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JellystatException(string.Create(CultureInfo.InvariantCulture, $"Jellystat did not respond within {_timeout.TotalSeconds} seconds")) { IsTransient = true };
        }
        catch (HttpRequestException ex)
        {
            throw new JellystatException("Could not reach Jellystat: " + ex.Message, ex) { IsTransient = true };
        }
        catch (JsonException ex)
        {
            throw new JellystatException("Jellystat returned an unexpected response", ex);
        }
    }
}
