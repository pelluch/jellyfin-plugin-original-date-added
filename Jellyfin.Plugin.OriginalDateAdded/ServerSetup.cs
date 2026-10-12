using System;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded;

public class ServerSetup
{
    // Jellyfin dates these by the folders it creates for them; media items are left out because their dates can be file times.
    private static readonly BaseItemKind[] _ownItemKinds =
    [
        BaseItemKind.AggregateFolder,
        BaseItemKind.UserRootFolder,
        BaseItemKind.CollectionFolder,
        BaseItemKind.UserView,
        BaseItemKind.Person,
        BaseItemKind.Studio,
        BaseItemKind.Genre,
        BaseItemKind.MusicGenre,
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ServerSetup> _logger;
    private readonly Lock _lock = new();
    private DateTime? _date;
    private bool _searched;

    public ServerSetup(ILibraryManager libraryManager, ILogger<ServerSetup> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public DateTime? Date
    {
        get
        {
            lock (_lock)
            {
                if (!_searched)
                {
                    _date = FindDate();
                    _searched = true;
                }

                return _date;
            }
        }
    }

    private DateTime? FindDate()
    {
        var earliest = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = _ownItemKinds,
            MinDateCreated = DateTime.MinValue, // leaves out items without a date, which the database stores as NULL
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Ascending)],
            Limit = 1,
        });
        DateTime? date = earliest.Count > 0 ? earliest[0].DateCreated : null;
        if (date is null)
        {
            _logger.LogInformation("Could not tell when this server was set up, so dates added from before that aren't adjusted");
        }
        else
        {
            _logger.LogInformation("This server was set up on {Date}; earlier dates added from sources count as that date", date);
        }

        return date;
    }
}
