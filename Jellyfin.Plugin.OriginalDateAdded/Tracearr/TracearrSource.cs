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
    private readonly SourceAvailability _availability;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TracearrSource> _logger;

    public TracearrSource(TracearrClient client, SourceAvailability availability, ILibraryManager libraryManager, ILogger<TracearrSource> logger)
    {
        _client = client;
        _availability = availability;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "tracearr";

    public bool CanAnswerAuthoritatively => false;

    public async Task<SourceAnswers> GetCandidatesAsync(IReadOnlyList<SourceItem> items, CancellationToken cancellationToken)
    {
        var config = PluginSettings.Configuration;
        if (!config.TracearrEnabled
            || string.IsNullOrEmpty(config.TracearrUrl)
            || string.IsNullOrEmpty(config.TracearrApiKey)
            || string.IsNullOrEmpty(config.TracearrServerId))
        {
            return SourceAnswers.None;
        }

        var candidates = new Dictionary<Guid, AddedDateCandidate>();
        var failures = new Dictionary<Guid, string>();

        var connection = new TracearrConnection(config.TracearrUrl, config.TracearrApiKey);
        var window = PluginSettings.ReplacementWindow;
        for (var i = 0; i < items.Count; i++)
        {
            var (item, scannedAt) = items[i];
            if (!ItemIdentity.IsSupported(item))
            {
                continue;
            }

            if (!HasLookupIds(item))
            {
                _logger.Log(PluginSettings.DetailLevel, "Can't look up {Name} ({ItemId}) in Tracearr: it has no TMDB, IMDb or TVDB ID", item.Name, item.Id);
                continue;
            }

            string reference;
            IReadOnlyList<TracearrCopy>? copies;
            try
            {
                (reference, copies) = await FindCopiesAsync(item, connection, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceException)
            {
                foreach (var unanswered in items.Skip(i).Select(sourceItem => sourceItem.Item).Where(ItemIdentity.IsSupported))
                {
                    failures[unanswered.Id] = "Tracearr is unavailable";
                }

                break;
            }

            if (copies is null)
            {
                _logger.Log(PluginSettings.DetailLevel, "Tracearr has no record of {Name} ({ItemId})", item.Name, item.Id);
                continue;
            }

            if (FindRemovedCopies(copies, item, config.TracearrServerId, reference) is { } removedCopies
                && FollowChain(removedCopies, scannedAt, window, reference) is { } candidate)
            {
                candidates[item.Id] = candidate;
            }
        }

        return new SourceAnswers(candidates, failures);
    }

    private static bool HasLookupIds(BaseItem item) =>
        (item is Episode ? _episodeProviders : _movieProviders).Any(provider => item.TryGetProviderId(provider, out _))
        || (item is Episode { Series: { } series } && _showProviders.Any(provider => series.TryGetProviderId(provider, out _)));

    private static string ReferenceName(MetadataProvider provider) => provider switch
    {
        MetadataProvider.Tmdb => "tmdb",
        MetadataProvider.Imdb => "imdb",
        MetadataProvider.Tvdb => "tvdb",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private Task<T> CallAsync<T>(TracearrConnection connection, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
        => _availability.CallAsync("tracearr " + connection.Url, "Tracearr at " + connection.Url, call, cancellationToken);

    private AddedDateCandidate? FollowChain(List<TracearrCopy> copies, DateTime scannedAt, TimeSpan window, string reference)
    {
        TracearrCopy? first = null;
        TracearrCopy? earliest = null;
        var links = 0;
        var appearedAt = scannedAt;
        while (copies
            .Where(copy => copy.AddedAt <= appearedAt - PluginSettings.MinimumDifference && (copy.RemovedAt is not { } removedAt || appearedAt - removedAt <= window))
            .MinBy(copy => copy.AddedAt) is { } link)
        {
            first ??= link;
            earliest = link;
            appearedAt = link.AddedAt;
            links++;
        }

        foreach (var copy in copies)
        {
            if (copy.AddedAt <= appearedAt - PluginSettings.MinimumDifference && copy.RemovedAt is { } removedAt)
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

    // Null when Tracearr groups the item with different content, which makes all of its copies suspect.
    private List<TracearrCopy>? FindRemovedCopies(IReadOnlyList<TracearrCopy> copies, BaseItem item, string serverId, string reference)
    {
        var removedCopies = new List<TracearrCopy>();
        foreach (var copy in copies.Where(copy => copy.ServerId == serverId))
        {
            if (copy.RatingKey is null)
            {
                removedCopies.Add(copy); // replaced copies carry no item ID, and Tracearr only lists them once removed
                continue;
            }

            if (!Guid.TryParse(copy.RatingKey, out var copyId) || copyId == item.Id)
            {
                continue;
            }

            var live = _libraryManager.GetItemById(copyId);
            if (live is null)
            {
                removedCopies.Add(copy);
            }
            else if (ItemIdentity.IsDifferentContent(item, live))
            {
                _logger.LogInformation(
                    "Tracearr {Reference} groups {Item} ({ItemId}) with a different item, {Copy} ({CopyId}), so none of its copies are used",
                    reference,
                    ItemIdentity.Describe(item),
                    item.Id,
                    ItemIdentity.Describe(live),
                    copyId);
                return null;
            }
            else
            {
                _logger.Log(
                    PluginSettings.DetailLevel,
                    "Tracearr {Reference}: {Copy} ({CopyId}) is still in Jellyfin, skipped for {Item} ({ItemId})",
                    reference,
                    ItemIdentity.Describe(live),
                    copyId,
                    ItemIdentity.Describe(item),
                    item.Id);
            }
        }

        return removedCopies;
    }

    private async Task<(string Reference, IReadOnlyList<TracearrCopy>? Copies)> FindCopiesAsync(BaseItem item, TracearrConnection connection, CancellationToken cancellationToken)
    {
        var (type, providers) = item is Episode ? ("episode", _episodeProviders) : ("movie", _movieProviders);
        foreach (var provider in providers)
        {
            if (item.TryGetProviderId(provider, out var id))
            {
                var reference = $"{type}:{ReferenceName(provider)}:{id}";
                var copies = await CallAsync(connection, token => _client.GetCopiesAsync(connection, reference, token), cancellationToken).ConfigureAwait(false);
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
            var seasons = await CallAsync(connection, token => _client.GetChildrenAsync(connection, show, token), cancellationToken).ConfigureAwait(false);
            if (seasons.FirstOrDefault(child => child.SeasonNumber == season) is not { } seasonChild)
            {
                continue;
            }

            var episodes = await CallAsync(connection, token => _client.GetChildrenAsync(connection, seasonChild.Id, token), cancellationToken).ConfigureAwait(false);
            if (episodes.FirstOrDefault(child => child.EpisodeNumber == number) is not { } episodeChild)
            {
                continue;
            }

            var copies = await CallAsync(connection, token => _client.GetCopiesAsync(connection, episodeChild.Id, token), cancellationToken).ConfigureAwait(false);
            return (string.Create(CultureInfo.InvariantCulture, $"{show} S{season:00}E{number:00}"), copies);
        }

        return (string.Empty, null);
    }
}
