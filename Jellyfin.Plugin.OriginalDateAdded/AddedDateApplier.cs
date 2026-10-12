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
        var dryRun = PluginSettings.DryRun;
        if (!Record(item, decision, reason, dryRun))
        {
            return false;
        }

        if (!dryRun)
        {
            item.DateCreated = decision.Entry.AddedAt;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public bool Record(BaseItem item, AddedDateDecision decision, string reason, bool dryRun)
    {
        var entry = decision.Entry;
        if (entry.AddedAt > item.DateCreated - AddedDateResolver.MinimumDifference || (_store.GetItem(item.Id) is { } stored && stored.AddedAt <= entry.AddedAt))
        {
            return false;
        }

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
}
