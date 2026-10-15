using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OriginalDateAdded.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Tasks;

public class RestoreOriginalDatesTask : IScheduledTask
{
    private const int ChunkSize = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateResolver _resolver;
    private readonly AddedDateApplier _applier;
    private readonly SourceAvailability _availability;
    private readonly ILogger<RestoreOriginalDatesTask> _logger;

    public RestoreOriginalDatesTask(ILibraryManager libraryManager, AddedDateResolver resolver, AddedDateApplier applier, SourceAvailability availability, ILogger<RestoreOriginalDatesTask> logger)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
        _applier = applier;
        _availability = availability;
        _logger = logger;
    }

    public string Name => "Restore original dates added";

    public string Key => "OriginalDateAddedRestoreOriginalDates";

    public string Description => "Gives movies and episodes the date they were first added: from Sonarr's and Radarr's import history, or from an earlier copy they replaced. Checks the newest items, as many as Items to check in the plugin settings (0 = all). Locked items are skipped.";

    public string Category => "Original Date Added";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
    ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var failuresBefore = _availability.FailureCounts();
        var itemsToCheck = PluginSettings.Configuration.ItemsToCheck;
        var supported = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                IsLocked = false,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = itemsToCheck > 0 ? itemsToCheck : null,
            })
            .Where(ItemIdentity.IsSupported)
            .ToList();
        var itemIds = supported
            .Where(ItemIdentity.HasBeenRefreshed)
            .OrderBy(item => item.Path, StringComparer.Ordinal) // a series' episodes together, so its Sonarr data is fetched once per run
            .Select(item => item.Id)
            .ToList();
        _logger.LogInformation("Restoring original dates added: checking {Count} items ({NotRefreshed} skipped, not refreshed yet)", itemIds.Count, supported.Count - itemIds.Count);

        var updated = 0;
        var skipped = 0;
        var removed = 0;
        for (var start = 0; start < itemIds.Count; start += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkSize = Math.Min(ChunkSize, itemIds.Count - start);
            var chunk = itemIds.GetRange(start, chunkSize)
                .Select(id => _libraryManager.GetItemById(id)) // items change or disappear during a long run
                .OfType<BaseItem>()
                .ToList();
            removed += chunkSize - chunk.Count;
            var resolution = await _resolver.ResolveAsync(chunk, cancellationToken).ConfigureAwait(false);
            skipped += resolution.Failures.Count;
            foreach (var item in chunk)
            {
                if (!resolution.Decisions.TryGetValue(item.Id, out var decision))
                {
                    if (!resolution.Failures.ContainsKey(item.Id))
                    {
                        _logger.Log(PluginSettings.DetailLevel, "No date added found for {Name} ({ItemId})", item.Name, item.Id);
                    }
                }
                else if (await _applier.ApplyAsync(item, decision, "by the Restore original dates added task", cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }

            progress.Report(100.0 * (start + chunkSize) / itemIds.Count);
        }

        if (PluginSettings.DryRun)
        {
            _logger.LogInformation("Dry run: {Updated} of {Count} items would have their date added changed, {Skipped} skipped because a source failed, {Removed} no longer in the library", updated, itemIds.Count, skipped, removed);
        }
        else
        {
            _logger.LogInformation("{Updated} of {Count} items had their date added changed, {Skipped} skipped because a source failed, {Removed} no longer in the library", updated, itemIds.Count, skipped, removed);
        }

        var unavailable = _availability.FailureCounts()
            .Where(failure => failure.Value.Count > failuresBefore.GetValueOrDefault(failure.Key).Count)
            .Select(failure => failure.Value.Service)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (unavailable.Count > 0)
        {
            _logger.LogWarning("Unavailable during this run: {Services}. Run it again to check the items they manage", string.Join(", ", unavailable));
        }
    }
}
