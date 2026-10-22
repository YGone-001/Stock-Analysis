using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.Sparrow;
using Serilog;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Thread-safe in-memory cache for raw market data with explicit TTL and TimeProvider support.
/// Optionally persists daily K-line entries to disk so they survive an application restart.
/// Never stores strategy decisions.
/// </summary>
public sealed class SparrowMarketDataCache
{
    private const string KlineKeyPrefix = "kline:";
    private const int PersistVersion = 1;
    private static readonly TimeSpan PersistDebounceDelay = TimeSpan.FromSeconds(1);

    private sealed record CacheEntry(string Json, DateTimeOffset ExpiresAt, DateTimeOffset StoredAt);
    private sealed record PersistedCache(int Version, List<PersistedEntry> Entries);
    private sealed record PersistedEntry(string Key, string Json, DateTimeOffset ExpiresAtUtc);

    private readonly ConcurrentDictionary<string, CacheEntry> _items = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly string? _persistFile;
    private readonly object _persistLock = new();
    private int _persistScheduled;

    public SparrowCacheStatistics Statistics { get; } = new();

    public SparrowMarketDataCache(TimeProvider? timeProvider = null, string? persistFile = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persistFile = persistFile;
        if (_persistFile != null)
        {
            LoadFromDisk();
        }
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
    /// Daily K-line entries are also persisted to disk (debounced).
    /// </summary>
    public void Set(string key, string json, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        _items[key] = new CacheEntry(json, now.Add(ttl), now);

        if (_persistFile != null && key.StartsWith(KlineKeyPrefix, StringComparison.Ordinal))
        {
            SchedulePersist();
        }
    }

    /// <summary>Removes a specific cache entry.</summary>
    public bool Remove(string key)
    {
        return _items.TryRemove(key, out _);
    }

    /// <summary>Purges all entries from the cache.</summary>
    public void Clear()
    {
        _items.Clear();
    }

    /// <summary>Gets current count of items in cache (including unpurged expired items).</summary>
    public int Count => _items.Count;

    /// <summary>Writes the current daily K-line entries to disk immediately.</summary>
    public void Flush()
    {
        PersistKlinesToDisk();
    }

    private void SchedulePersist()
    {
        if (Interlocked.Exchange(ref _persistScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PersistDebounceDelay).ConfigureAwait(false);
                PersistKlinesToDisk();
            }
            finally
            {
                Interlocked.Exchange(ref _persistScheduled, 0);
            }
        });
    }

    private void PersistKlinesToDisk()
    {
        if (_persistFile == null)
        {
            return;
        }

        lock (_persistLock)
        {
            try
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                List<PersistedEntry> entries = _items
                    .Where(kv => kv.Key.StartsWith(KlineKeyPrefix, StringComparison.Ordinal) && kv.Value.ExpiresAt > now)
                    .Select(kv => new PersistedEntry(kv.Key, kv.Value.Json, kv.Value.ExpiresAt))
                    .ToList();

                string json = JsonSerializer.Serialize(new PersistedCache(PersistVersion, entries));
                string? directory = Path.GetDirectoryName(_persistFile);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = _persistFile + ".tmp";
                File.WriteAllText(tempPath, json, Encoding.UTF8);
                File.Move(tempPath, _persistFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist Sparrow Kline cache to {Path}", _persistFile);
            }
        }
    }

    private void LoadFromDisk()
    {
        if (_persistFile == null || !File.Exists(_persistFile))
        {
            return;
        }

        try
        {
            PersistedCache? cache = JsonSerializer.Deserialize<PersistedCache>(
                File.ReadAllText(_persistFile, Encoding.UTF8));
            if (cache == null || cache.Version != PersistVersion)
            {
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (PersistedEntry entry in cache.Entries)
            {
                if (entry.ExpiresAtUtc > now && !string.IsNullOrWhiteSpace(entry.Json))
                {
                    _items[entry.Key] = new CacheEntry(entry.Json, entry.ExpiresAtUtc, now);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load Sparrow Kline cache from {Path}", _persistFile);
        }
    }
}
