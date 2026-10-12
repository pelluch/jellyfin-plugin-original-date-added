using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Store;

public sealed class AddedDateStore : IDisposable
{
    private const int BackupsToKeep = 5;

    private static readonly TimeSpan _writeDelay = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly ILogger<AddedDateStore> _logger;
    private readonly string _path;
    private readonly Lock _dataLock = new();
    private readonly Lock _writeLock = new();
    private readonly Timer _writeTimer;
    private readonly StoreData _data;
    private bool _writePending;

    public AddedDateStore(IApplicationPaths applicationPaths, ILogger<AddedDateStore> logger)
    {
        _logger = logger;
        var directory = Path.Combine(applicationPaths.DataPath, "original-date-added");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "dates.json");
        _data = Load();
        _writeTimer = new Timer(_ => Write(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public ItemEntry? GetItem(Guid itemId)
    {
        lock (_dataLock)
        {
            return _data.Items.GetValueOrDefault(itemId);
        }
    }

    public IReadOnlyList<KeyValuePair<Guid, ItemEntry>> GetItems()
    {
        lock (_dataLock)
        {
            return _data.Items.ToList();
        }
    }

    public void SetItem(Guid itemId, ItemEntry entry)
    {
        entry = entry with { AddedAt = AsUtc(entry.AddedAt), ScannedAt = AsUtc(entry.ScannedAt) };
        lock (_dataLock)
        {
            if (_data.Items.GetValueOrDefault(itemId) == entry)
            {
                return;
            }

            _data.Items[itemId] = entry;
            ScheduleWrite();
        }
    }

    public void RemoveItem(Guid itemId)
    {
        lock (_dataLock)
        {
            if (_data.Items.Remove(itemId))
            {
                ScheduleWrite();
            }
        }
    }

    public void AddRemoved(IEnumerable<string> keys, RemovedEntry entry, DateTime expiredBefore)
    {
        entry = entry with { AddedAt = AsUtc(entry.AddedAt), RemovedAt = AsUtc(entry.RemovedAt) };
        expiredBefore = AsUtc(expiredBefore);
        lock (_dataLock)
        {
            foreach (var key in keys)
            {
                // The expiry check is needed for combined entries, since a combined entry
                // takes this removal's time, letting expired dates pass the window.
                if (_data.Removed.TryGetValue(key, out var existing) && existing.ItemType == entry.ItemType && existing.RemovedAt >= expiredBefore)
                {
                    _data.Removed[key] = entry with
                    {
                        AddedAt = existing.AddedAt < entry.AddedAt ? existing.AddedAt : entry.AddedAt,
                        RemovedAt = existing.RemovedAt > entry.RemovedAt ? existing.RemovedAt : entry.RemovedAt,
                    };
                }
                else
                {
                    _data.Removed[key] = entry;
                }

                ScheduleWrite();
            }
        }
    }

    public RemovedEntry? FindRemoved(IEnumerable<string> keys, BaseItemKind itemType)
    {
        lock (_dataLock)
        {
            return keys
                .Select(key => _data.Removed.GetValueOrDefault(key))
                .Where(entry => entry is not null && entry.ItemType == itemType)
                .MinBy(entry => entry!.AddedAt);
        }
    }

    public void PruneRemoved(DateTime removedBefore)
    {
        removedBefore = AsUtc(removedBefore);
        lock (_dataLock)
        {
            var expired = _data.Removed.Where(pair => pair.Value.RemovedAt < removedBefore).Select(pair => pair.Key).ToList();
            foreach (var key in expired)
            {
                _data.Removed.Remove(key);
            }

            if (expired.Count > 0)
            {
                ScheduleWrite();
            }
        }
    }

    public (int Items, int Removed, string? BackupPath) ClearWithBackup()
    {
        lock (_writeLock)
        {
            lock (_dataLock)
            {
                if (_data.Items.Count == 0 && _data.Removed.Count == 0)
                {
                    return (0, 0, null);
                }

                var backupPath = string.Create(CultureInfo.InvariantCulture, $"{_path}.cleared-{DateTime.UtcNow:yyyyMMddHHmmss}");
                WriteAtomically(backupPath, JsonSerializer.SerializeToUtf8Bytes(_data, _jsonOptions));
                var backups = Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".cleared-*")
                    .Order(StringComparer.Ordinal)
                    .SkipLast(BackupsToKeep);
                foreach (var oldBackup in backups)
                {
                    File.Delete(oldBackup);
                }

                var result = (_data.Items.Count, _data.Removed.Count, backupPath);
                _data.Items.Clear();
                _data.Removed.Clear();
                ScheduleWrite();
                return result;
            }
        }
    }

    public void Dispose()
    {
        _writeTimer.Dispose();
        Write();
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        var tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private static DateTime AsUtc(DateTime date)
        => date.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime();

    private StoreData Load()
    {
        if (!File.Exists(_path))
        {
            return new StoreData();
        }

        try
        {
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<StoreData>(stream, _jsonOptions) ?? new StoreData();
        }
        catch (JsonException ex)
        {
            var corruptPath = string.Create(CultureInfo.InvariantCulture, $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}");
            File.Move(_path, corruptPath);
            _logger.LogError(ex, "Could not read {Path}, moved it to {CorruptPath} and starting with an empty store", _path, corruptPath);
            return new StoreData();
        }
    }

    private void ScheduleWrite()
    {
        if (!_writePending)
        {
            _writePending = true;
            _writeTimer.Change(_writeDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Write()
    {
        lock (_writeLock)
        {
            byte[] json;
            lock (_dataLock)
            {
                if (!_writePending)
                {
                    return;
                }

                _writePending = false;
                json = JsonSerializer.SerializeToUtf8Bytes(_data, _jsonOptions);
            }

            try
            {
                WriteAtomically(_path, json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not write {Path}", _path);
                lock (_dataLock)
                {
                    ScheduleWrite();
                }
            }
        }
    }
}
