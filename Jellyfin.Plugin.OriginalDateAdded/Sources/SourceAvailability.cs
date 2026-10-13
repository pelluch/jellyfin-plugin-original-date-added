using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OriginalDateAdded.Sources;

public class SourceAvailability
{
    private static readonly TimeSpan[] _defaultRetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];
    private static readonly TimeSpan _pause = TimeSpan.FromMinutes(5);

    private readonly ILogger<SourceAvailability> _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _pausedUntil = [];

    public SourceAvailability(ILogger<SourceAvailability> logger)
        : this(logger, _defaultRetryDelays, TimeProvider.System)
    {
    }

    public SourceAvailability(ILogger<SourceAvailability> logger, IReadOnlyList<TimeSpan> retryDelays, TimeProvider timeProvider)
    {
        _logger = logger;
        _retryDelays = retryDelays;
        _timeProvider = timeProvider;
    }

    // While a service is paused its calls fail at once, so an outage doesn't cost every batch the retries and timeouts
    public async Task<T> CallAsync<T>(string key, string service, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_pausedUntil.TryGetValue(key, out var until) && _timeProvider.GetUtcNow() < until)
            {
                throw new SourceException(string.Create(CultureInfo.InvariantCulture, $"{service} is unavailable, next try after {until.UtcDateTime:u}"));
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await call(cancellationToken).ConfigureAwait(false);
                Resume(key, service);
                return result;
            }
            catch (SourceException ex) when (ex.IsTransient && attempt <= _retryDelays.Count)
            {
                var delay = _retryDelays[attempt - 1];
                _logger.Log(PluginSettings.DetailLevel, "{Service}: {Message}, retrying in {Seconds} seconds", service, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (SourceException ex)
            {
                Pause(key, service, ex.Message, attempt);
                throw;
            }
        }
    }

    private void Pause(string key, string service, string message, int attempts)
    {
        bool wasAvailable;
        lock (_lock)
        {
            wasAvailable = !_pausedUntil.ContainsKey(key);
            _pausedUntil[key] = _timeProvider.GetUtcNow() + _pause;
        }

        if (wasAvailable)
        {
            _logger.LogWarning(
                "{Service} is unavailable after {Attempts} attempt(s) ({Message}); items it would be asked about get no date added decision, trying again in {Minutes} minutes",
                service,
                attempts,
                message,
                _pause.TotalMinutes);
        }
        else
        {
            _logger.Log(PluginSettings.DetailLevel, "{Service} is still unavailable ({Message}), trying again in {Minutes} minutes", service, message, _pause.TotalMinutes);
        }
    }

    private void Resume(string key, string service)
    {
        bool wasPaused;
        lock (_lock)
        {
            wasPaused = _pausedUntil.Remove(key);
        }

        if (wasPaused)
        {
            _logger.LogInformation("{Service} answers again", service);
        }
    }
}
