using System;
using System.Collections.Concurrent;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Thread-safe in-memory cache for raw market data with explicit TTL and TimeProvider support.
/// Never stores strategy decisions.
/// </summary>
public sealed class SparrowMarketDataCache
{
    private sealed record CacheEntry(string Json, DateTimeOffset ExpiresAt, DateTimeOffset StoredAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _items = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public SparrowCacheStatistics Statistics { get; } = new();

    public SparrowMarketDataCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Attempts to retrieve a cached raw data string.
    /// If useCache is false, skips reading from cache and records a bypass.
    /// If an entry is expired, it is purged and recorded as expired.
    /// </summary>
    public bool TryGet(string key, bool useCache, out string? value)
    {
        value = null;

        if (!useCache)
        {
            Statistics.RecordBypass();
            return false;
        }

        if (!_items.TryGetValue(key, out CacheEntry? entry))
        {
            Statistics.RecordMiss();
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            _items.TryRemove(key, out _);
            Statistics.RecordExpired();
            return false;
        }

        Statistics.RecordHit();
        value = entry.Json;
        return true;
    }

    /// <summary>
    /// Stores raw JSON data in the cache with the given TTL.
    /// Does not cache null, empty, or whitespace content.
    /// </summary>
    public void Set(string key, string json, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _items[key] = new CacheEntry(json, now.Add(ttl), now);
    }

    /// <summary>
    /// Removes a specific cache entry.
    /// </summary>
    public bool Remove(string key)
    {
        return _items.TryRemove(key, out _);
    }

    /// <summary>
    /// Purges all entries from the cache.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
    }

    /// <summary>
    /// Gets current count of items in cache (including unpurged expired items).
    /// </summary>
    public int Count => _items.Count;
}
