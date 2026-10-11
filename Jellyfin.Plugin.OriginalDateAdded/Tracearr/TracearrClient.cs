using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public class TracearrClient
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IHttpClientFactory _httpClientFactory;

    public TracearrClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IReadOnlyList<TracearrServer>> GetJellyfinServersAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        var response = await GetAsync<ServersResponse>(url, apiKey, "/servers", cancellationToken).ConfigureAwait(false);
        return (response.Data ?? [])
            .Where(server => server.ServerType == "jellyfin")
            .Select(server => new TracearrServer(server.ServerId, server.ServerName))
            .ToList();
    }

    private async Task<T> GetAsync<T>(string url, string apiKey, string path, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.TrimEnd('/') + "/api/v2/public" + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new TracearrException("The Tracearr URL is not a valid http(s) URL");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TracearrException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Tracearr rejected the API key",
                    HttpStatusCode.NotFound => "Tracearr's public API was not found at this URL",
                    _ => string.Create(CultureInfo.InvariantCulture, $"Tracearr returned HTTP {(int)response.StatusCode}"),
                });
            }

            return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new TracearrException("Tracearr returned an empty response");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TracearrException(string.Create(CultureInfo.InvariantCulture, $"Tracearr did not respond within {_timeout.TotalSeconds} seconds"));
        }
        catch (HttpRequestException ex)
        {
            throw new TracearrException("Could not reach Tracearr: " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new TracearrException("Tracearr returned an unexpected response", ex);
        }
    }

    private sealed record ServersResponse(IReadOnlyList<ServerDto>? Data);

    private sealed record ServerDto(string ServerId, string ServerName, string ServerType);
}
