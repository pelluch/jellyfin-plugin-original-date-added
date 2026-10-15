using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class AddedDateApplier
{
    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateStore _store;
    private readonly ILogger<AddedDateApplier> _logger;

    public AddedDateApplier(ILibraryManager libraryManager, AddedDateStore store, ILogger<AddedDateApplier> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    public async Task<bool> ApplyAsync(BaseItem item, AddedDateDecision decision, string reason, CancellationToken cancellationToken)
    {
        // Saving a removed item re-inserts it, and saving a stale copy undoes changes made since it was read
        if (_libraryManager.GetItemById(item.Id) is not { } current)
        {
            _logger.Log(PluginSettings.DetailLevel, "Leaving date added of {Name} ({ItemId}) as it is, the item is no longer in the library", item.Name, item.Id);
            return false;
        }

        var dryRun = PluginSettings.DryRun;
        if (!Record(current, decision, reason, dryRun))
        {
            return false;
        }

        if (!dryRun)
        {
            current.DateCreated = decision.Entry.AddedAt;
            await current.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public bool Record(BaseItem item, AddedDateDecision decision, string reason, bool dryRun)
    {
        if (WhyUnchanged(item, decision) is { } why)
        {
            _logger.Log(
                PluginSettings.DetailLevel,
                "Leaving date added of {Name} ({ItemId}) at {Current} as it is: {Source} has {AddedAt} ({Detail}), which {Why}",
                item.Name,
                item.Id,
                item.DateCreated,
                decision.Entry.Source,
                decision.Entry.AddedAt,
                decision.Detail,
                why);
            return false;
        }

        if (item.IsLocked)
        {
            _logger.Log(PluginSettings.DetailLevel, "Leaving date added of {Name} ({ItemId}) as it is, the item is locked", item.Name, item.Id);
            return false;
        }

        var entry = decision.Entry;
        if (dryRun)
        {
            _logger.LogInformation(
                "Dry run: would set date added of {Name} ({ItemId}) to {AddedAt} from {Source} ({Detail}), scanned as {ScannedAt}, {Reason}",
                item.Name,
                item.Id,
                entry.AddedAt,
                entry.Source,
                decision.Detail,
                entry.ScannedAt,
                reason);
            return true;
        }

        _store.SetItem(item.Id, entry);
        _logger.LogInformation(
            "Date added of {Name} ({ItemId}) is {AddedAt} from {Source} ({Detail}), scanned as {ScannedAt}, {Reason}",
            item.Name,
            item.Id,
            entry.AddedAt,
            entry.Source,
            decision.Detail,
            entry.ScannedAt,
            reason);
        return true;
    }

    // Null when the decision changes the item's date
    private string? WhyUnchanged(BaseItem item, AddedDateDecision decision)
    {
        var addedAt = decision.Entry.AddedAt;
        var stored = _store.GetItem(item.Id);
        var minimum = PluginSettings.MinimumDifference;
        if (decision.IsAuthoritative)
        {
            if (stored?.AddedAt == addedAt)
            {
                return "is already recorded";
            }

            return (addedAt - item.DateCreated).Duration() < minimum
                ? string.Create(CultureInfo.InvariantCulture, $"is within {minimum.TotalMinutes:0.##} minutes of it")
                : null;
        }

        if (stored is not null && stored.AddedAt <= addedAt)
        {
            return string.Create(CultureInfo.InvariantCulture, $"is no earlier than the recorded {stored.AddedAt} from {stored.Source}");
        }

        var isEarlier = addedAt <= item.DateCreated - minimum;
        return isEarlier
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"isn't at least {minimum.TotalMinutes:0.##} minutes earlier");
    }
}
