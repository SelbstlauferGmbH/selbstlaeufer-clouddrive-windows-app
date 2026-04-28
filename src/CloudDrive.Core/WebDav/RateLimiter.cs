using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CloudDrive.Core.WebDav;

/// <summary>
/// Sliding-window rate limiter that enforces a maximum number of requests
/// within a time window. Thread-safe for concurrent callers.
/// </summary>
public class RateLimiter
{
    private readonly int _maxRequests;
    private readonly TimeSpan _window;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<DateTime> _timestamps = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RateLimiter(int maxRequests, TimeSpan window, ILogger logger)
    {
        _maxRequests = maxRequests;
        _window = window;
        _logger = logger;
    }

    /// <summary>
    /// Waits until a request slot is available within the rate limit window.
    /// </summary>
    public async Task WaitAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await _gate.WaitAsync(ct);
            var releaseGate = true;
            try
            {
                PurgeExpired();

                if (_timestamps.Count < _maxRequests)
                {
                    _timestamps.Enqueue(DateTime.UtcNow);
                    releaseGate = false;
                    _gate.Release();
                    return;
                }

                // Calculate how long to wait until the oldest request expires
                if (_timestamps.TryPeek(out var oldest))
                {
                    var waitTime = oldest + _window - DateTime.UtcNow;
                    if (waitTime > TimeSpan.Zero)
                    {
                        _logger.LogDebug("RateLimiter: throttling for {WaitMs}ms ({Count}/{Max} requests in window)",
                            (int)waitTime.TotalMilliseconds, _timestamps.Count, _maxRequests);
                        releaseGate = false;
                        _gate.Release();
                        await Task.Delay(waitTime, ct);
                        continue; // Re-acquire and retry
                    }
                }

                // Oldest has expired, purge and allow
                PurgeExpired();
                _timestamps.Enqueue(DateTime.UtcNow);
                releaseGate = false;
                _gate.Release();
                return;
            }
            finally
            {
                if (releaseGate)
                    _gate.Release();
            }
        }
    }

    private void PurgeExpired()
    {
        var cutoff = DateTime.UtcNow - _window;
        while (_timestamps.TryPeek(out var ts) && ts < cutoff)
        {
            _timestamps.TryDequeue(out _);
        }
    }
}
