using System.Collections.ObjectModel;

namespace AIHelper.Core.Sparrow;

/// <summary>
/// Research-only capability contract for the retired Legacy strategy. This is deliberately
/// separate from <see cref="AIHelper.Models.SparrowStrategyMode"/>: Legacy is not a runnable
/// historical replay strategy until its live semantics have been independently characterized.
/// </summary>
public static class SparrowLegacyHistoricalReplayCapability
{
    public const string StrategyIdentity = "Legacy";

    public const string StrategyIdentityNotActive = "LEGACY_STRATEGY_IDENTITY_NOT_ACTIVE";
    public const string IntradayQuoteSemanticsUnavailable = "LEGACY_INTRADAY_QUOTE_SEMANTICS_UNAVAILABLE";
    public const string OuterVolumeUnavailable = "LEGACY_OUTER_VOLUME_UNAVAILABLE";
    public const string InnerVolumeUnavailable = "LEGACY_INNER_VOLUME_UNAVAILABLE";
    public const string IntradayMarketRegimeUnavailable = "LEGACY_INTRADAY_MARKET_REGIME_UNAVAILABLE";
    public const string KlineAsOfSemanticsUnverified = "LEGACY_KLINE_ASOF_SEMANTICS_UNVERIFIED";
    public const string CurrentUniverseDependency = "LEGACY_CURRENT_UNIVERSE_DEPENDENCY";

    private static readonly IReadOnlyList<string> OrderedBlockers = Array.AsReadOnly(new[]
    {
        StrategyIdentityNotActive,
        IntradayQuoteSemanticsUnavailable,
        OuterVolumeUnavailable,
        InnerVolumeUnavailable,
        IntradayMarketRegimeUnavailable,
        KlineAsOfSemanticsUnverified,
        CurrentUniverseDependency
    });

    /// <summary>Stable, exhaustive blocker codes in their canonical reporting order.</summary>
    public static IReadOnlyList<string> BlockerReasonCodes => OrderedBlockers;

    public static SparrowLegacyHistoricalReplayResult CreateUnsupported(
        SparrowLegacyHistoricalReplayRequest request,
        HistoricalMarketDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataset);

        return new SparrowLegacyHistoricalReplayResult(
            request.TradingDate,
            dataset.DatasetId,
            dataset.Fingerprint,
            Copy(OrderedBlockers),
            Array.Empty<string>(),
            Array.AsReadOnly(new[]
            {
                "Legacy historical replay is intentionally unsupported until its historical data and as-of semantics are characterized."
            }));
    }

    internal static IReadOnlyDictionary<string, string> FreezeEvidence(IReadOnlyDictionary<string, string>? evidence)
    {
        Dictionary<string, string> copy = (evidence ?? new Dictionary<string, string>())
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, string>(copy);
    }

    private static IReadOnlyList<string> Copy(IEnumerable<string> values) => Array.AsReadOnly(values.ToArray());
}

/// <summary>
/// Immutable request evidence for reporting Legacy historical replay capability. It cannot select,
/// evaluate, rank, or otherwise execute the Legacy strategy.
/// </summary>
public sealed class SparrowLegacyHistoricalReplayRequest
{
    public SparrowLegacyHistoricalReplayRequest(DateOnly tradingDate, IReadOnlyDictionary<string, string>? parameterEvidence = null)
    {
        if (tradingDate == default) throw new ArgumentOutOfRangeException(nameof(tradingDate));
        TradingDate = tradingDate;
        ParameterEvidence = SparrowLegacyHistoricalReplayCapability.FreezeEvidence(parameterEvidence);
    }

    public string StrategyIdentity => SparrowLegacyHistoricalReplayCapability.StrategyIdentity;
    public string StrategyVersion => SparrowStrategyVersions.Legacy;
    public DateOnly TradingDate { get; }
    /// <summary>Optional descriptive evidence only; it is not a strategy parameter snapshot.</summary>
    public IReadOnlyDictionary<string, string> ParameterEvidence { get; }
}

/// <summary>
/// Immutable, machine-readable proof that Legacy replay was not run. The only construction path
/// fixes support to Unsupported, selections to zero, and blockers to the complete canonical set.
/// </summary>
public sealed class SparrowLegacyHistoricalReplayResult
{
    internal SparrowLegacyHistoricalReplayResult(
        DateOnly tradingDate,
        string datasetId,
        string datasetFingerprint,
        IReadOnlyList<string> blockerReasonCodes,
        IReadOnlyList<string> selections,
        IReadOnlyList<string> warnings)
    {
        TradingDate = tradingDate;
        DatasetId = datasetId;
        DatasetFingerprint = datasetFingerprint;
        BlockerReasonCodes = blockerReasonCodes;
        Selections = selections;
        Warnings = warnings;
    }

    public string StrategyIdentity => SparrowLegacyHistoricalReplayCapability.StrategyIdentity;
    public string StrategyVersion => SparrowStrategyVersions.Legacy;
    public DateOnly TradingDate { get; }
    public string DatasetId { get; }
    public string DatasetFingerprint { get; }
    public HistoricalReplaySupport Support => HistoricalReplaySupport.Unsupported;
    public IReadOnlyList<string> BlockerReasonCodes { get; }
    public IReadOnlyList<string> Selections { get; }
    public IReadOnlyList<string> Warnings { get; }
    public string Explanation => "Legacy replay is unsupported; no strategy evaluator, ranking engine, live provider, or fallback strategy was invoked.";
}
