using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.OriginalDateAdded.Arr;

public static class ArrLineage
{
    // History paths are compared by file name
    public static ArrLineageStart FindStart(IEnumerable<ArrEvent> events, ArrFile currentFile, TimeSpan window)
    {
        ArrLineageStart? start = null;
        string? current = null;
        DateTime? deletedAt = null;
        string? deletionReason = null;

        void Appear(DateTime date, bool isImport)
        {
            if (start is null)
            {
                start = new ArrLineageStart(date, isImport, null);
            }
            else if (deletedAt is { } gapStart
                && !(deletionReason == "MissingFromDisk" && !isImport) // Replaced outside the arr and picked up by a rescan
                && date - gapStart > window)
            {
                start = new ArrLineageStart(date, isImport, gapStart);
            }

            deletedAt = null;
        }

        foreach (var e in events.OrderBy(e => e.Date).ThenBy(e => e.Kind))
        {
            switch (e.Kind)
            {
                case ArrEventKind.Deleted:
                    if (current is null)
                    {
                        Appear(e.Date, isImport: false);
                    }
                    else if (ArrPaths.FileName(e.Path) != current)
                    {
                        continue; // Some older file
                    }

                    current = null;
                    if (e.Reason != "Upgrade")
                    {
                        deletedAt = e.Date;
                        deletionReason = e.Reason;
                    }

                    break;
                case ArrEventKind.Imported:
                    if (current is null)
                    {
                        Appear(e.Date, isImport: true);
                    }

                    current = ArrPaths.FileName(e.Path);
                    break;
                case ArrEventKind.Renamed:
                    if (current is null)
                    {
                        Appear(e.Date, isImport: false);
                    }

                    current = ArrPaths.FileName(e.Path);
                    break;
            }
        }

        if (current != ArrPaths.FileName(currentFile.Path))
        {
            Appear(currentFile.DateAdded, isImport: false); // picked up without an import or a rename
        }

        return start ?? new ArrLineageStart(currentFile.DateAdded, false, null);
    }
}
