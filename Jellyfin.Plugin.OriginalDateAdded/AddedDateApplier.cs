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
    private readonly ServerSetup _serverSetup;
    private readonly ILogger<AddedDateApplier> _logger;

    public AddedDateApplier(AddedDateStore store, ServerSetup serverSetup, ILogger<AddedDateApplier> logger)
    {
        _store = store;
        _serverSetup = serverSetup;
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
        if (!IsChange(item, decision))
        {
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

    private bool IsChange(BaseItem item, AddedDateDecision decision)
    {
        var addedAt = decision.Entry.AddedAt;
        var stored = _store.GetItem(item.Id);
        if (decision.IsAuthoritative)
        {
            return (addedAt - item.DateCreated).Duration() >= AddedDateResolver.MinimumDifference && stored?.AddedAt != addedAt;
        }

        var isEarlier = addedAt <= item.DateCreated - AddedDateResolver.MinimumDifference;
        var isBeforeServerSetUp = item.DateCreated < _serverSetup.Date;
        return (isEarlier || isBeforeServerSetUp) && !(stored is not null && stored.AddedAt <= addedAt);
    }
}
