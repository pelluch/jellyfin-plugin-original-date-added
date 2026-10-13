using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
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
    private readonly ILogger<RestoreOriginalDatesTask> _logger;

    public RestoreOriginalDatesTask(ILibraryManager libraryManager, AddedDateResolver resolver, AddedDateApplier applier, ILogger<RestoreOriginalDatesTask> logger)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
        _applier = applier;
        _logger = logger;
    }

    public string Name => "Restore original dates added";

    public string Key => "OriginalDateAddedBackdateReplacements"; // the task's earlier name: a new key would lose its run history and triggers

    public string Description => "Gives movies and episodes the date they were first added: from Sonarr's and Radarr's import history, or from an earlier copy they replaced. Checks the newest items, as many as Items to check in the plugin settings (0 = all). Locked items are skipped.";

    public string Category => "Original Date Added";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var itemsToCheck = PluginSettings.Configuration.ItemsToCheck;
        var itemIds = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                IsLocked = false,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = itemsToCheck > 0 ? itemsToCheck : null,
            })
            .Where(item => ItemIdentity.IsSupported(item) && ItemIdentity.HasBeenRefreshed(item))
            .OrderBy(item => item.Path, StringComparer.Ordinal) // a series' episodes together, so its Sonarr data is fetched once per run
            .Select(item => item.Id)
            .ToList();
        _logger.LogInformation("Restoring original dates added: checking {Count} items", itemIds.Count);

        var updated = 0;
        var skipped = 0;
        for (var start = 0; start < itemIds.Count; start += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkSize = Math.Min(ChunkSize, itemIds.Count - start);
            var chunk = itemIds.GetRange(start, chunkSize)
                .Select(id => _libraryManager.GetItemById(id)) // items change or disappear during a long run
                .OfType<BaseItem>()
                .ToList();
            var resolution = await _resolver.ResolveAsync(chunk, cancellationToken).ConfigureAwait(false);
            skipped += resolution.Failures.Count;
            foreach (var item in chunk)
            {
                if (resolution.Decisions.TryGetValue(item.Id, out var decision)
                    && await _applier.ApplyAsync(item, decision, "by the Restore original dates added task", cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }

            progress.Report(100.0 * (start + chunkSize) / itemIds.Count);
        }

        if (PluginSettings.DryRun)
        {
            _logger.LogInformation("Dry run: {Updated} of {Count} items would have their date added changed, {Skipped} skipped because a source was unavailable", updated, itemIds.Count, skipped);
        }
        else
        {
            _logger.LogInformation("{Updated} of {Count} items had their date added changed, {Skipped} skipped because a source was unavailable", updated, itemIds.Count, skipped);
        }
    }
}
