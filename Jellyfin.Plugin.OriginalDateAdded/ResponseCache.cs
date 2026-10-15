using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OriginalDateAdded;

public sealed class ResponseCache
{
    private readonly TimeSpan _duration;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTime FetchedAt, Task Response)> _entries = [];

    public ResponseCache(TimeSpan duration)
    {
        _duration = duration;
    }

    public async Task<T> GetAsync<T>(string key, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken)
    {
        Task<T> response;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_entries.TryGetValue(key, out var cached) && now - cached.FetchedAt < _duration && !cached.Response.IsFaulted && !cached.Response.IsCanceled)
            {
                response = (Task<T>)cached.Response;
            }
            else
            {
                foreach (var expired in _entries.Where(pair => now - pair.Value.FetchedAt >= _duration).Select(pair => pair.Key).ToList())
                {
                    _entries.Remove(expired);
                }

                response = fetch(CancellationToken.None); // shared with concurrent callers
                _entries[key] = (now, response);
            }
        }

        return await response.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
