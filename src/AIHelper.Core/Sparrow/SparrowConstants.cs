using System;

namespace AIHelper.Core.Sparrow;

/// <summary>
/// Centralized tuning constants for the Sparrow scanners.
/// Kept separate from <see cref="SparrowDataCachePolicy"/> (cache TTLs) and
/// <see cref="SparrowParameterDefaults"/> (user-facing defaults) so each
/// concern has a single source of truth.
/// </summary>
public static class SparrowConstants
{
    /// <summary>V2 P1 defensive threshold: Shanghai daily percent at or below this value blocks the scan.</summary>
    public const double V2MarketDefensivePercent = -2.5;

    /// <summary>V2 momentum lookback offset: MA5 is compared against MA5 this many bars earlier.</summary>
    public const int MomentumLookbackOffset = 3;

    /// <summary>Number of codes per quote batch request.</summary>
    public const int QuoteBatchSize = 50;

    /// <summary>Universe size at or above which a full-market quote snapshot is preferred over batches.</summary>
    public const int FullMarketQuoteThreshold = 500;

    /// <summary>Daily K-line lookback used by V2 and Compare.</summary>
    public const int V2KlineLimit = 120;

    /// <summary>Daily K-line lookback used by Classic.</summary>
    public const int ClassicKlineLimit = 65;

    /// <summary>Upper bound for P2 quote concurrency.</summary>
    public const int MaxP2Concurrency = 8;

    /// <summary>Upper bound for P3 K-line concurrency.</summary>
    public const int MaxP3Concurrency = 32;

    /// <summary>P3 concurrency multiplier applied to the user-facing MaxConcurrency.</summary>
    public const int P3ConcurrencyMultiplier = 4;

    /// <summary>Shanghai index code used by V2 P1 (EastMoney daily index endpoint).</summary>
    public const string ShanghaiIndexCode = "sh000001";

    /// <summary>Shanghai index code used by Classic market-regime intraday trend.</summary>
    public const string ShanghaiIndexTrendCode = "1.000001";

    /// <summary>CSI 1000 index code used by Classic market-regime intraday trend.</summary>
    public const string Csi1000IndexTrendCode = "1.000852";
}
