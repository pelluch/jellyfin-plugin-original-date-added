using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.OriginalDateAdded;

public sealed class ResponseCache
{
    private readonly TimeSpan _duration;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (DateTimeOffset FetchedAt, Task Response)> _entries = [];

    public ResponseCache(TimeSpan duration, TimeProvider timeProvider)
    {
        _duration = duration;
        _timeProvider = timeProvider;
    }

    public async Task<T> GetAsync<T>(string key, Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken, TimeSpan? maxAge = null)
    {
        Task<T> response;
        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            var acceptedAge = maxAge is { } age && age < _duration ? age : _duration;
            if (_entries.TryGetValue(key, out var cached) && now - cached.FetchedAt < acceptedAge && !cached.Response.IsFaulted && !cached.Response.IsCanceled)
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
