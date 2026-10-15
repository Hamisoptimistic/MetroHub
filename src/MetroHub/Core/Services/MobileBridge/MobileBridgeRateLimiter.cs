using System;
using System.Collections.Generic;

namespace MetroHub.Core.Services.MobileBridge;

/// <summary>
/// Fixed-capacity sliding-window limiter keyed by a caller identity (remote address).
/// Bounds how fast a phone can create tiles or push bytes, without any background timer:
/// buckets only ever shrink when a new request arrives.
/// </summary>
public sealed class MobileBridgeRateLimiter
{
    private const int MaxTrackedKeys = 32;

    private readonly int _maxEvents;
    private readonly long _windowTicks;
    private readonly Dictionary<string, Queue<long>> _buckets = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public MobileBridgeRateLimiter(int maxEvents, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);

        _maxEvents = maxEvents;
        _windowTicks = Math.Max(1, window.Ticks);
    }

    /// <summary>Records an attempt and returns true when it is inside the allowance.</summary>
    public bool TryAcquire(string key)
    {
        string bucketKey = string.IsNullOrEmpty(key) ? "unknown" : key;
        long now = DateTime.UtcNow.Ticks;

        lock (_lock)
        {
            if (!_buckets.TryGetValue(bucketKey, out Queue<long>? window))
            {
                if (_buckets.Count >= MaxTrackedKeys)
                {
                    // Bounded memory: drop the least-recently-used bucket rather than growing forever.
                    PruneOldestBucket();
                }

                window = new Queue<long>(_maxEvents);
                _buckets[bucketKey] = window;
            }

            long cutoff = now - _windowTicks;
            while (window.Count > 0 && window.Peek() < cutoff)
            {
                window.Dequeue();
            }

            if (window.Count >= _maxEvents)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _buckets.Clear();
        }
    }

    private void PruneOldestBucket()
    {
        string? oldestKey = null;
        long oldestStamp = long.MaxValue;

        foreach (var pair in _buckets)
        {
            long newest = pair.Value.Count > 0 ? pair.Value.Peek() : long.MinValue;
            if (newest < oldestStamp)
            {
                oldestStamp = newest;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey != null)
        {
            _buckets.Remove(oldestKey);
        }
    }
}
