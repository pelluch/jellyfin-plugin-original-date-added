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

    public async Task<IReadOnlyList<string>> GetRootFoldersAsync(ArrConnection connection, CancellationToken cancellationToken)
    {
        var folders = await GetAsync<List<RootFolderDto>>(connection, "rootfolder", cancellationToken).ConfigureAwait(false);
        return folders.Select(folder => folder.Path).ToList();
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
                });
            }

            return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new ArrException("The response was empty");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArrException(string.Create(CultureInfo.InvariantCulture, $"No response within {_timeout.TotalSeconds} seconds"));
        }
        catch (HttpRequestException ex)
        {
            throw new ArrException("Could not connect: " + ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new ArrException("The response isn't from Sonarr's or Radarr's API", ex);
        }
    }

    private sealed record StatusDto(string AppName, string Version);

    private sealed record RootFolderDto(string Path);
}
