using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OriginalDateAdded.Store;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Tasks;

public class RevertToScannedDatesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateStore _store;
    private readonly ILogger<RevertToScannedDatesTask> _logger;

    public RevertToScannedDatesTask(ILibraryManager libraryManager, AddedDateStore store, ILogger<RevertToScannedDatesTask> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _logger = logger;
    }

    public string Name => "Revert to scanned dates";

    public string Key => "OriginalDateAddedRevertToScannedDates";

    public string Description => "Sets every date added this plugin changed back to the date Jellyfin scanned the file in, and forgets those changes.";

    public string Category => "Original Date Added";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var entries = _store.GetItems();
        _logger.LogInformation("Reverting {Count} items to their scanned dates", entries.Count);

        var reverted = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (itemId, entry) = entries[i];
            var item = _libraryManager.GetItemById(itemId);
            if (item is not null && item.DateCreated != entry.Original)
            {
                if (PluginSettings.DryRun)
                {
                    _logger.LogInformation("Dry run: would revert date added of {Name} ({ItemId}) from {Date} to {Original}", item.Name, itemId, item.DateCreated, entry.Original);
                }
                else
                {
                    _logger.LogInformation("Reverting date added of {Name} ({ItemId}) from {Date} to {Original}", item.Name, itemId, item.DateCreated, entry.Original);
                    item.DateCreated = entry.Original;
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                reverted++;
            }

            if (!PluginSettings.DryRun)
            {
                _store.RemoveItem(itemId);
            }

            progress.Report(100.0 * (i + 1) / entries.Count);
        }

        if (PluginSettings.DryRun)
        {
            _logger.LogInformation("Dry run: {Reverted} of {Count} items would be reverted to their scanned dates", reverted, entries.Count);
        }
        else
        {
            _logger.LogInformation("{Reverted} of {Count} items reverted to their scanned dates", reverted, entries.Count);
        }
    }
}
