using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Tracearr;

public class TracearrSource : IAddedDateSource
{
    private static readonly MetadataProvider[] _movieProviders = [MetadataProvider.Tmdb, MetadataProvider.Imdb, MetadataProvider.Tvdb];
    private static readonly MetadataProvider[] _episodeProviders = [MetadataProvider.Tvdb, MetadataProvider.Imdb, MetadataProvider.Tmdb];
    private static readonly MetadataProvider[] _showProviders = [MetadataProvider.Tvdb, MetadataProvider.Tmdb, MetadataProvider.Imdb];

    private readonly TracearrClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TracearrSource> _logger;

    public TracearrSource(TracearrClient client, ILibraryManager libraryManager, ILogger<TracearrSource> logger)
    {
        _client = client;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "tracearr";

    public async Task<IReadOnlyDictionary<Guid, AddedDateCandidate>> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var config = PluginSettings.Configuration;
        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        if (!config.TracearrEnabled
            || string.IsNullOrEmpty(config.TracearrUrl)
            || string.IsNullOrEmpty(config.TracearrApiKey)
            || string.IsNullOrEmpty(config.TracearrServerId))
        {
            return candidates;
        }

        var connection = new TracearrConnection(config.TracearrUrl, config.TracearrApiKey);
        var window = PluginSettings.ReplacementWindow;
        for (var i = 0; i < items.Count; i++)
        {
            var (item, original) = items[i];
            if (!ItemIdentity.IsSupported(item))
            {
                continue;
            }

            string reference;
            IReadOnlyList<TracearrCopy>? copies;
            try
            {
                (reference, copies) = await FindCopiesAsync(item, connection, cancellationToken).ConfigureAwait(false);
            }
            catch (TracearrException ex)
            {
                _logger.LogWarning("Tracearr unavailable, skipping it for {Count} item(s): {Message}", items.Count - i, ex.Message);
                break;
            }

            if (copies is null)
            {
                _logger.Log(PluginSettings.DetailLevel, "Tracearr has no record of {Name} ({ItemId})", item.Name, item.Id);
                continue;
            }

            var removedCopies = copies
                .Where(copy => copy.ServerId == config.TracearrServerId && IsGoneFromJellyfin(copy, item, reference))
                .ToList();
            if (FollowChain(removedCopies, original, window, reference) is { } candidate)
            {
                candidates[item.Id] = candidate;
            }
        }

        return candidates;
    }

    private static string ReferenceName(MetadataProvider provider) => provider switch
    {
        MetadataProvider.Tmdb => "tmdb",
        MetadataProvider.Imdb => "imdb",
        MetadataProvider.Tvdb => "tvdb",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private AddedDateCandidate? FollowChain(List<TracearrCopy> copies, DateTime original, TimeSpan window, string reference)
    {
        TracearrCopy? first = null;
        TracearrCopy? earliest = null;
        var links = 0;
        var appearedAt = original;
        while (copies
            .Where(copy => copy.AddedAt <= appearedAt - AddedDateResolver.MinimumDifference && (copy.RemovedAt is not { } removedAt || appearedAt - removedAt <= window))
            .MinBy(copy => copy.AddedAt) is { } link)
        {
            first ??= link;
            earliest = link;
            appearedAt = link.AddedAt;
            links++;
        }

        foreach (var copy in copies)
        {
            if (copy.AddedAt <= appearedAt - AddedDateResolver.MinimumDifference && copy.RemovedAt is { } removedAt)
            {
                _logger.Log(
                    PluginSettings.DetailLevel,
                    "Tracearr {Reference}: copy added {AddedAt} was removed {Days:F1} days before the next copy appeared, window is {WindowDays} days, skipped",
                    reference,
                    copy.AddedAt,
                    (appearedAt - removedAt).TotalDays,
                    window.TotalDays);
            }
        }

        return earliest is null
            ? null
            : new AddedDateCandidate(earliest.AddedAt, first!.RemovedAt, string.Create(CultureInfo.InvariantCulture, $"Tracearr {reference}, {links} earlier copies"));
    }

    private bool IsGoneFromJellyfin(TracearrCopy copy, BaseItem item, string reference)
    {
        if (copy.RatingKey is null)
        {
            return true; // replaced copies carry no item ID, and Tracearr only lists them once removed
        }

        if (!Guid.TryParse(copy.RatingKey, out var itemId) || itemId == item.Id)
        {
            return false;
        }

        if (_libraryManager.GetItemById(itemId) is null)
        {
            return true;
        }

        _logger.Log(PluginSettings.DetailLevel, "Tracearr {Reference}: copy {CopyId} is still in Jellyfin, skipped", reference, itemId);
        return false;
    }

    private async Task<(string Reference, IReadOnlyList<TracearrCopy>? Copies)> FindCopiesAsync(BaseItem item, TracearrConnection connection, CancellationToken cancellationToken)
    {
        var (type, providers) = item is Episode ? ("episode", _episodeProviders) : ("movie", _movieProviders);
        foreach (var provider in providers)
        {
            if (item.TryGetProviderId(provider, out var id))
            {
                var reference = $"{type}:{ReferenceName(provider)}:{id}";
                var copies = await _client.GetCopiesAsync(connection, reference, cancellationToken).ConfigureAwait(false);
                if (copies is not null)
                {
                    return (reference, copies);
                }
            }
        }

        if (item is Episode { Series: { } series, ParentIndexNumber: { } season, IndexNumber: { } number })
        {
            return await FindEpisodeThroughShowAsync(series, season, number, connection, cancellationToken).ConfigureAwait(false);
        }

        return (string.Empty, null);
    }

    private async Task<(string Reference, IReadOnlyList<TracearrCopy>? Copies)> FindEpisodeThroughShowAsync(
        Series series,
        int season,
        int number,
        TracearrConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var provider in _showProviders)
        {
            if (!series.TryGetProviderId(provider, out var id))
            {
                continue;
            }

            var show = $"show:{ReferenceName(provider)}:{id}";
            var seasons = await _client.GetChildrenAsync(connection, show, cancellationToken).ConfigureAwait(false);
            if (seasons.FirstOrDefault(child => child.SeasonNumber == season) is not { } seasonChild)
            {
                continue;
            }

            var episodes = await _client.GetChildrenAsync(connection, seasonChild.Id, cancellationToken).ConfigureAwait(false);
            if (episodes.FirstOrDefault(child => child.EpisodeNumber == number) is not { } episodeChild)
            {
                continue;
            }

            var copies = await _client.GetCopiesAsync(connection, episodeChild.Id, cancellationToken).ConfigureAwait(false);
            return (string.Create(CultureInfo.InvariantCulture, $"{show} S{season:00}E{number:00}"), copies);
        }

        return (string.Empty, null);
    }
}
