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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public class TracearrClient
{
    private const int MaxRateLimitRetries = 5;

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _defaultRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TracearrClient> _logger;

    public TracearrClient(IHttpClientFactory httpClientFactory, ILogger<TracearrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TracearrServer>> GetJellyfinServersAsync(TracearrConnection connection, CancellationToken cancellationToken)
    {
        var response = await GetAsync<ServersResponse>(connection, "/servers", cancellationToken).ConfigureAwait(false)
            ?? throw new TracearrException("Tracearr's public API was not found at this URL");
        return (response.Data ?? [])
            .Where(server => server.ServerType == "jellyfin")
            .Select(server => new TracearrServer(server.ServerId, server.ServerName))
            .ToList();
    }

    public async Task<IReadOnlyList<TracearrCopy>?> GetCopiesAsync(TracearrConnection connection, string reference, CancellationToken cancellationToken)
    {
        var media = await GetAsync<MediaDto>(connection, "/media/" + Uri.EscapeDataString(reference), cancellationToken).ConfigureAwait(false);
        if (media is null)
        {
            return null;
        }

        var copies = new List<TracearrCopy>();
        foreach (var availability in media.Availability ?? [])
        {
            copies.Add(new TracearrCopy(availability.ServerId, availability.RatingKey, availability.AddedAt, availability.RemovedAt));
            if (availability.Replaces is { } replaced)
            {
                copies.Add(new TracearrCopy(availability.ServerId, null, replaced.AddedAt, replaced.RemovedAt));
            }
        }

        return copies;
    }

    public async Task<IReadOnlyList<TracearrChild>> GetChildrenAsync(TracearrConnection connection, string reference, CancellationToken cancellationToken)
    {
        var response = await GetAsync<ChildrenResponse>(connection, "/media/" + Uri.EscapeDataString(reference) + "/children", cancellationToken).ConfigureAwait(false);
        return (response?.Data ?? [])
            .Select(child => new TracearrChild(child.Id, child.SeasonNumber, child.EpisodeNumber))
            .ToList();
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : _defaultRetryDelay);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > _maxRetryDelay ? _maxRetryDelay : delay;
    }

    private async Task<T?> GetAsync<T>(TracearrConnection connection, string path, CancellationToken cancellationToken)
        where T : class
    {
        if (!Uri.TryCreate(connection.Url.TrimEnd('/') + "/api/v2/public" + path, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new TracearrException("The Tracearr URL is not a valid http(s) URL");
        }

        for (var attempt = 0; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);

            try
            {
                using var response = await _httpClientFactory.CreateClient(NamedClient.Default)
                    .SendAsync(request, timeout.Token)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
                {
                    var delay = RetryDelay(response);
                    _logger.LogInformation("Tracearr rate limit reached, retrying in {Seconds:F0} seconds", delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TracearrException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Tracearr rejected the API key",
                        HttpStatusCode.TooManyRequests =>"Tracearr's rate limit was still exceeded after retrying",
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
    }

    private sealed record ServersResponse(IReadOnlyList<ServerDto>? Data);

    private sealed record ServerDto(string ServerId, string ServerName, string ServerType);

    private sealed record MediaDto(IReadOnlyList<AvailabilityDto>? Availability);

    private sealed record AvailabilityDto(string ServerId, string? RatingKey, DateTime AddedAt, DateTime? RemovedAt, ReplacedDto? Replaces);

    private sealed record ReplacedDto(DateTime AddedAt, DateTime RemovedAt);

    private sealed record ChildrenResponse(IReadOnlyList<ChildDto>? Data);

    private sealed record ChildDto(string Id, int? SeasonNumber, int? EpisodeNumber);
}
