using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class SeriesAndSeasonDates
{
    private const string Source = "episodes";
    private const string Detail = "its earliest episode";

    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateStore _store;
    private readonly ILogger<SeriesAndSeasonDates> _logger;

    public SeriesAndSeasonDates(ILibraryManager libraryManager, AddedDateStore store, ILogger<SeriesAndSeasonDates> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    public async Task UpdateAsync(string reason, CancellationToken cancellationToken)
    {
        var earliest = new Dictionary<Guid, DateTime>();
        void Consider(Guid folderId, DateTime date)
        {
            if (!folderId.Equals(Guid.Empty) && (!earliest.TryGetValue(folderId, out var current) || date < current))
            {
                earliest[folderId] = date;
            }
        }

        foreach (var episode in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Episode], IsVirtualItem = false }).OfType<Episode>())
        {
            Consider(episode.SeriesId, episode.DateCreated);
            Consider(episode.SeasonId, episode.DateCreated); // Jellyfin gives episodes directly in a series folder a season without a folder
        }

        var folders = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Season] })
            .Where(item => item is Series or Season)
            .ToList();
        var dryRun = PluginSettings.DryRun;
        var series = 0;
        var seasons = 0;
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (earliest.TryGetValue(folder.Id, out var date) && await ApplyAsync(folder, date, reason, dryRun, cancellationToken).ConfigureAwait(false))
            {
                if (folder is Series)
                {
                    series++;
                }
                else
                {
                    seasons++;
                }
            }
        }

        var seriesCount = folders.Count(folder => folder is Series);
        if (dryRun)
        {
            _logger.LogInformation("Dry run: {Series} of {SeriesCount} shows and {Seasons} of {SeasonCount} seasons would have their date added changed to their earliest episode's", series, seriesCount, seasons, folders.Count - seriesCount);
        }
        else
        {
            _logger.LogInformation("{Series} of {SeriesCount} shows and {Seasons} of {SeasonCount} seasons had their date added changed to their earliest episode's", series, seriesCount, seasons, folders.Count - seriesCount);
        }
    }

    private async Task<bool> ApplyAsync(BaseItem folder, DateTime earliest, string reason, bool dryRun, CancellationToken cancellationToken)
    {
        var name = ItemIdentity.Describe(folder);
        var minimum = PluginSettings.MinimumDifference;
        if ((earliest - folder.DateCreated).Duration() < minimum)
        {
            _logger.Log(
                PluginSettings.DetailLevel,
                "Leaving date added of {Name} ({ItemId}) at {Current} as it is: its earliest episode has {Earliest}, which is within {Minutes} minutes of it",
                name,
                folder.Id,
                folder.DateCreated,
                earliest,
                minimum.TotalMinutes);
            return false;
        }

        if (folder.IsLocked)
        {
            _logger.Log(PluginSettings.DetailLevel, "Leaving date added of {Name} ({ItemId}) as it is, the item is locked", name, folder.Id);
            return false;
        }

        var entry = new ItemEntry(earliest, Source, _store.GetItem(folder.Id)?.ScannedAt ?? folder.DateCreated);
        if (dryRun)
        {
            _logger.LogInformation(
                "Dry run: would set date added of {Name} ({ItemId}) to {AddedAt} from {Source} ({Detail}), scanned as {ScannedAt}, {Reason}",
                name,
                folder.Id,
                entry.AddedAt,
                entry.Source,
                Detail,
                entry.ScannedAt,
                reason);
            return true;
        }

        _store.SetItem(folder.Id, entry);
        folder.DateCreated = earliest;
        await folder.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Date added of {Name} ({ItemId}) is {AddedAt} from {Source} ({Detail}), scanned as {ScannedAt}, {Reason}",
            name,
            folder.Id,
            entry.AddedAt,
            entry.Source,
            Detail,
            entry.ScannedAt,
            reason);
        return true;
    }
}
