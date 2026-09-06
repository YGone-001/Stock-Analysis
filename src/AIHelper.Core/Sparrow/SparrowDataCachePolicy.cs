using System;
using System.Collections.Generic;
using System.Linq;

namespace AIHelper.Core.Sparrow;

/// <summary>
/// Centralized cache policy and key definitions for Sparrow real-time and historical market data.
/// </summary>
public static class SparrowDataCachePolicy
{
    /// <summary>
    /// Default TTL for daily K-lines (5 minutes = 300s). Allows intraday refreshes without overloading the server.
    /// </summary>
    public static readonly TimeSpan DefaultKlineTtl = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Default TTL for intraday minute trends (10s).
    /// </summary>
    public static readonly TimeSpan DefaultTrendTtl = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Scanner-level quote TTL is zero: quotes are real-time and must never be frozen across scans.
    /// </summary>
    public static readonly TimeSpan DefaultQuoteTtl = TimeSpan.Zero;

    /// <summary>
    /// Generates a normalized cache key for K-lines.
    /// Format: kline:{normalizedCode}:{period}:{limit}
    /// </summary>
    public static string GetKlineKey(string code, int limit, string period = "day")
    {
        string norm = NormalizeCode(code);
        return $"kline:{norm}:{period.ToLowerInvariant()}:{limit}";
    }

    /// <summary>
    /// Generates a canonical cache key for quotes (sorted and deduplicated).
    /// Format: quote:v2:{canonicalCodes}
    /// </summary>
    public static string GetQuoteKey(IEnumerable<string> codes)
    {
        var list = codes
            .Select(NormalizeCode)
            .Where(c => c.Length == 6)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal);

        return "quote:v2:" + string.Join(",", list);
    }

    /// <summary>
    /// Generates a normalized cache key for intraday trends.
    /// Format: trend:{normalizedCode}:{ndays}
    /// </summary>
    public static string GetTrendKey(string code, int ndays = 1)
    {
        string norm = NormalizeCode(code);
        return $"trend:{norm}:{ndays}";
    }

    /// <summary>
    /// Normalizes a stock code by stripping market prefixes (sh, sz, bj, 1., 0.).
    /// </summary>
    public static string NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return string.Empty;
        }

        string val = code.Trim().ToLowerInvariant()
            .Replace("sh", "")
            .Replace("sz", "")
            .Replace("bj", "");

        if (val.StartsWith("1.") || val.StartsWith("0."))
        {
            val = val[2..];
        }

        return val;
    }
}

/// <summary>
/// Cache diagnostic counters for a scan session.
/// </summary>
public sealed class SparrowCacheStatistics
{
    public long HitCount;
    public long MissCount;
    public long ExpiredCount;
    public long BypassCount;

    public void RecordHit() => System.Threading.Interlocked.Increment(ref HitCount);
    public void RecordMiss() => System.Threading.Interlocked.Increment(ref MissCount);
    public void RecordExpired() => System.Threading.Interlocked.Increment(ref ExpiredCount);
    public void RecordBypass() => System.Threading.Interlocked.Increment(ref BypassCount);

    public override string ToString() =>
        $"Hit: {HitCount}, Miss: {MissCount}, Expired: {ExpiredCount}, Bypass: {BypassCount}";
}
