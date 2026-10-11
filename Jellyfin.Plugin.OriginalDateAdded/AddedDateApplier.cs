using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class AddedDateApplier
{
    private readonly AddedDateStore _store;
    private readonly ILogger<AddedDateApplier> _logger;

    public AddedDateApplier(AddedDateStore store, ILogger<AddedDateApplier> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<bool> ApplyAsync(BaseItem item, AddedDateDecision decision, string reason, CancellationToken cancellationToken)
    {
        var entry = decision.Entry;
        if (entry.Date > item.DateCreated - AddedDateResolver.MinimumDifference || (_store.GetItem(item.Id) is { } stored && stored.Date <= entry.Date))
        {
            return false;
        }

        if (PluginSettings.DryRun)
        {
            _logger.LogInformation(
                "Dry run: would set date added of {Name} ({ItemId}) to {Date} from {Source} ({Detail}), scanned as {Original}, {Reason}",
                item.Name,
                item.Id,
                entry.Date,
                entry.Source,
                decision.Detail,
                entry.Original,
                reason);
            return true;
        }

        _store.SetItem(item.Id, entry);
        _logger.LogInformation(
            "Date added of {Name} ({ItemId}) is {Date} from {Source} ({Detail}), scanned as {Original}, {Reason}",
            item.Name,
            item.Id,
            entry.Date,
            entry.Source,
            decision.Detail,
            entry.Original,
            reason);
        item.DateCreated = entry.Date;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
