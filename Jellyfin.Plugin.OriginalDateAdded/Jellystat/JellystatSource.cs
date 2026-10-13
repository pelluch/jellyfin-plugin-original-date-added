using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using MediaBrowser.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Jellystat;

public class JellystatSource : IAddedDateSource
{
    private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);
    private static readonly string[] _movieCollectionTypes = ["movies", "mixed"];
    private static readonly string[] _showCollectionTypes = ["tvshows", "mixed"];

    private readonly JellystatClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly IApplicationHost _applicationHost;
    private readonly ILogger<JellystatSource> _logger;
    private readonly Lock _cacheLock = new();
    private readonly Dictionary<string, (DateTime FetchedAt, Task Response)> _cache = [];

    public JellystatSource(JellystatClient client, ILibraryManager libraryManager, IApplicationHost applicationHost, ILogger<JellystatSource> logger)
    {
        _client = client;
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    public string Name => "jellystat";

    public bool CanAnswerAuthoritatively => false;

    public async Task<SourceAnswers> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var config = PluginSettings.Configuration;
        if (!config.JellystatEnabled || string.IsNullOrEmpty(config.JellystatUrl) || string.IsNullOrEmpty(config.JellystatApiKey))
        {
            return SourceAnswers.None;
        }

        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        var failures = new Dictionary<Guid, string>();

        var connection = new JellystatConnection(config.JellystatUrl, config.JellystatApiKey);
        var done = 0;
        try
        {
            var libraries = await GetLibrariesAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!libraries.Any(library => library.BelongsTo(_applicationHost.SystemId)))
            {
                _logger.LogWarning("Jellystat tracks a different Jellyfin server, skipping it for {Count} item(s)", items.Count);
                return SourceAnswers.None;
            }

            foreach (var (item, scannedAt) in items)
            {
                if (ItemIdentity.IsSupported(item)
                    && await FindCandidateAsync(item, scannedAt, libraries, connection, cancellationToken).ConfigureAwait(false) is { } candidate)
                {
                    candidates[item.Id] = candidate;
                }

                done++;
            }
        }
        catch (JellystatException ex)
        {
            _logger.LogWarning("Jellystat unavailable, skipping it for {Count} item(s): {Message}", items.Count - done, ex.Message);
            foreach (var unanswered in items.Skip(done).Select(sourceItem => sourceItem.Item).Where(ItemIdentity.IsSupported))
            {
                failures[unanswered.Id] = "Jellystat is unavailable";
            }
        }

        return new SourceAnswers(candidates, failures);
    }

    private async Task<AddedDateCandidate?> FindCandidateAsync(
        BaseItem item,
        DateTime scannedAt,
        IReadOnlyList<JellystatLibrary> libraries,
        JellystatConnection connection,
        CancellationToken cancellationToken)
    {
        string reference;
        IReadOnlyList<(string ItemId, DateTime? AddedAt)> copies;
        if (item is Episode episode)
        {
            if (episode.ParentIndexNumber is not { } season || episode.IndexNumber is not { } number)
            {
                return null;
            }

            reference = string.Create(CultureInfo.InvariantCulture, $"\"{episode.SeriesName}\" S{season:00}E{number:00}");
            var seriesIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (episode.SeriesId != Guid.Empty)
            {
                seriesIds.Add(episode.SeriesId.ToString("N"));
            }

            if (episode.Series is { } series)
            {
                var sameName = await FindByNameAsync(series, "Series", _showCollectionTypes, libraries, connection, cancellationToken).ConfigureAwait(false);
                seriesIds.UnionWith(sameName.Select(row => row.Id));
            }

            copies = await FindEpisodeCopiesAsync(seriesIds, season, number, connection, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            reference = string.Create(CultureInfo.InvariantCulture, $"movie \"{item.Name}\" ({item.ProductionYear})");
            var sameName = await FindByNameAsync(item, "Movie", _movieCollectionTypes, libraries, connection, cancellationToken).ConfigureAwait(false);
            copies = sameName.Select(row => (row.Id, row.DateCreated)).ToList();
        }

        var earlier = new List<DateTime>();
        foreach (var (copyId, addedAt) in copies)
        {
            if (!Guid.TryParse(copyId, out var id) || id == item.Id)
            {
                continue;
            }

            var live = _libraryManager.GetItemById(id);
            if (live is null)
            {
                if (addedAt is { } date && date <= scannedAt - PluginSettings.MinimumDifference)
                {
                    earlier.Add(date);
                }
            }
            else if (ItemIdentity.IsDifferentContent(item, live))
            {
                _logger.LogInformation(
                    "Jellystat {Reference} matches {Item} ({ItemId}) with a different item, {Copy} ({CopyId}), so none of its copies are used",
                    reference,
                    ItemIdentity.Describe(item),
                    item.Id,
                    ItemIdentity.Describe(live),
                    id);
                return null;
            }
            else
            {
                _logger.Log(
                    PluginSettings.DetailLevel,
                    "Jellystat {Reference}: {Copy} ({CopyId}) is still in Jellyfin, skipped for {Item} ({ItemId})",
                    reference,
                    ItemIdentity.Describe(live),
                    id,
                    ItemIdentity.Describe(item),
                    item.Id);
            }
        }

        if (earlier.Count == 0)
        {
            _logger.Log(PluginSettings.DetailLevel, "Jellystat has no earlier removed copies of {Name} ({ItemId})", item.Name, item.Id);
            return null;
        }

        return new AddedDateCandidate(earlier.Min(), null, string.Create(CultureInfo.InvariantCulture, $"Jellystat {reference}, {earlier.Count} earlier copies"));
    }

    private async Task<IReadOnlyList<JellystatLibraryItem>> FindByNameAsync(
        BaseItem item,
        string type,
        string[] collectionTypes,
        IReadOnlyList<JellystatLibrary> libraries,
        JellystatConnection connection,
        CancellationToken cancellationToken)
    {
        var matches = new List<JellystatLibraryItem>();
        if (string.IsNullOrEmpty(item.Name))
        {
            return matches;
        }

        foreach (var library in libraries.Where(library => collectionTypes.Contains(library.CollectionType, StringComparer.OrdinalIgnoreCase)))
        {
            var rows = await GetCachedAsync(connection, "items:" + library.Id, token => _client.GetLibraryItemsAsync(connection, library.Id, token), cancellationToken)
                .ConfigureAwait(false);
            matches.AddRange(rows.Where(row => row.Type == type && row.Name == item.Name && row.ProductionYear == item.ProductionYear));
        }

        return matches;
    }

    private async Task<IReadOnlyList<(string ItemId, DateTime? AddedAt)>> FindEpisodeCopiesAsync(
        IEnumerable<string> seriesIds,
        int season,
        int number,
        JellystatConnection connection,
        CancellationToken cancellationToken)
    {
        var copies = new List<(string ItemId, DateTime? AddedAt)>();
        foreach (var seriesId in seriesIds)
        {
            var seasons = await GetCachedAsync(connection, "seasons:" + seriesId, token => _client.GetSeasonsAsync(connection, seriesId, token), cancellationToken)
                .ConfigureAwait(false);
            foreach (var seasonRow in seasons.Where(row => row.IndexNumber == season))
            {
                var episodes = await GetCachedAsync(connection, "episodes:" + seasonRow.Id, token => _client.GetEpisodesAsync(connection, seasonRow.Id, token), cancellationToken)
                    .ConfigureAwait(false);
                copies.AddRange(episodes.Where(row => row.IndexNumber == number).Select(row => (row.EpisodeId, row.DateCreated)));
            }
        }

        return copies;
    }

    private Task<IReadOnlyList<JellystatLibrary>> GetLibrariesAsync(JellystatConnection connection, CancellationToken cancellationToken)
        => GetCachedAsync(connection, "libraries", token => _client.GetLibrariesAsync(connection, token), cancellationToken);

    private async Task<IReadOnlyList<T>> GetCachedAsync<T>(JellystatConnection connection, string key, Func<CancellationToken, Task<IReadOnlyList<T>>> fetch, CancellationToken cancellationToken)
    {
        key = connection.Url + "|" + key;
        Task<IReadOnlyList<T>> response;
        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            if (_cache.TryGetValue(key, out var cached) && now - cached.FetchedAt < _cacheDuration && !cached.Response.IsFaulted && !cached.Response.IsCanceled)
            {
                response = (Task<IReadOnlyList<T>>)cached.Response;
            }
            else
            {
                foreach (var expired in _cache.Where(pair => now - pair.Value.FetchedAt >= _cacheDuration).Select(pair => pair.Key).ToList())
                {
                    _cache.Remove(expired);
                }

                response = fetch(CancellationToken.None); // shared with concurrent callers, so one caller's cancellation mustn't fail it
                _cache[key] = (now, response);
            }
        }

        return await response.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
