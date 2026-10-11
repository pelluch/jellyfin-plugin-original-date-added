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

public class FindOriginalDatesTask : IScheduledTask
{
    private const int ChunkSize = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly AddedDateResolver _resolver;
    private readonly AddedDateApplier _applier;
    private readonly ILogger<FindOriginalDatesTask> _logger;

    public FindOriginalDatesTask(ILibraryManager libraryManager, AddedDateResolver resolver, AddedDateApplier applier, ILogger<FindOriginalDatesTask> logger)
    {
        _libraryManager = libraryManager;
        _resolver = resolver;
        _applier = applier;
        _logger = logger;
    }

    public string Name => "Find original dates added";

    public string Key => "OriginalDateAddedFindOriginalDates";

    public string Description => "Looks up the most recently added movies and episodes in every source and gives them an earlier date added where one is found.";

    public string Category => "Original Date Added";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                IsVirtualItem = false,
                OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
                Limit = Plugin.Instance!.Configuration.ItemsToCheck,
            })
            .Where(item => ItemIdentity.IsSupported(item) && item.DateLastRefreshed != DateTime.MinValue)
            .ToList();
        _logger.LogInformation("Finding original dates added for {Count} items", items.Count);

        var updated = 0;
        for (var start = 0; start < items.Count; start += ChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = items.GetRange(start, Math.Min(ChunkSize, items.Count - start));
            var decisions = await _resolver.ResolveAsync(chunk, cancellationToken).ConfigureAwait(false);
            foreach (var item in chunk)
            {
                if (decisions.TryGetValue(item.Id, out var decision)
                    && await _applier.ApplyAsync(item, decision, "by the Find original dates added task", cancellationToken).ConfigureAwait(false))
                {
                    updated++;
                }
            }

            progress.Report(100.0 * (start + chunk.Count) / items.Count);
        }

        if (PluginSettings.DryRun)
        {
            _logger.LogInformation("Dry run: {Updated} of {Count} items would get an earlier date added", updated, items.Count);
        }
        else
        {
            _logger.LogInformation("{Updated} of {Count} items got an earlier date added", updated, items.Count);
        }
    }
}
