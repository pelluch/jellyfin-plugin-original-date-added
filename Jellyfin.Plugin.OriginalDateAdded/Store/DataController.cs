using System;
using System.IO;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

[ApiController]
[Route("OriginalDateAdded/Data")]
[Authorize(Policy = Policies.RequiresElevation)]
public class DataController : ControllerBase
{
    private readonly AddedDateStore _store;
    private readonly ILogger<DataController> _logger;

    public DataController(AddedDateStore store, ILogger<DataController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpPost("Clear")]
    public ActionResult<ClearDataResult> Clear()
    {
        try
        {
            var (items, removed, backupPath) = _store.ClearWithBackup();
            if (backupPath is null)
            {
                return new ClearDataResult(0, 0, null);
            }

            _logger.LogInformation(
                "Cleared {Items} item entries and {Removed} removed-item entries, backup saved to {BackupPath}; dates added in Jellyfin were not changed",
                items,
                removed,
                backupPath);
            return new ClearDataResult(items, removed, Path.GetFileName(backupPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not back up the plugin data, so nothing was cleared");
            return Problem("Could not back up the plugin data, so nothing was cleared: " + ex.Message);
        }
    }
}
