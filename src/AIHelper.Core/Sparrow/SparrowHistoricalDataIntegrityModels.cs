using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

/// <summary>How complete the security lifecycle evidence is for a historical security.</summary>
public enum HistoricalLifecycleQuality
{
    Unknown,
    CurrentUniverseFallback,
    Partial,
    ListingKnown,
    Complete
}

public enum HistoricalSecurityType { Unknown, Stock, Etf, Index }
public enum HistoricalSecurityMarket { Unknown, Shanghai, Shenzhen, Beijing }

/// <summary>
/// DelistingEffectiveDate, when present, is the first date on which the security is not listed.
/// It is deliberately exclusive: T &lt; DelistingEffectiveDate is active; T &gt;= it is delisted.
/// </summary>
public sealed record HistoricalSecurity(
    string Symbol,
    string Name,
    HistoricalSecurityType SecurityType = HistoricalSecurityType.Unknown,
    HistoricalSecurityMarket Market = HistoricalSecurityMarket.Unknown,
    DateOnly? ListingDate = null,
    DateOnly? DelistingEffectiveDate = null,
    HistoricalLifecycleQuality LifecycleQuality = HistoricalLifecycleQuality.Unknown,
    DateOnly? ObservedHistoryStart = null);

public enum HistoricalSecurityStatus { Active, Suspended, Delisted, NotYetListed, Unknown }

/// <summary>Data availability is distinct from a strategy rule pass/fail result.</summary>
public enum HistoricalObservationStatus
{
    Available,
    NotYetListed,
    Delisted,
    Suspended,
    MissingQuote,
    MissingKline,
    UnknownDataGap,
    Unsupported
}

public enum HistoricalUniverseQuality
{
    Unknown,
    CurrentUniverseFallback,
    DerivedFromObservations,
    Partial,
    Complete
}

public sealed record HistoricalUniverseSnapshot(
    DateOnly TradingDate,
    IReadOnlyList<string> SecuritySymbols,
    HistoricalUniverseQuality Quality,
    string Source,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? Array.Empty<string>();
}

public sealed record HistoricalUniverseResolution(
    HistoricalUniverseSnapshot Universe,
    IReadOnlyList<HistoricalSecurity> ActiveSecurities,
    IReadOnlyDictionary<string, HistoricalObservationStatus> ExcludedStatuses);

public enum HistoricalField
{
    Price,
    PreviousClose,
    ChangePercent,
    Amount,
    Turnover,
    OuterVolume,
    InnerVolume
}

public enum HistoricalFieldOrigin { Unknown, Observed, Derived, Declared, LegacyDeclared, Unavailable }
public enum HistoricalFieldCoverage { Unknown, None, Partial, Full }
public enum HistoricalValueUnit { Unknown, CurrencyBaseUnit, CurrencyThousands, Percentage, Hands }

/// <summary>Acquisition truth is deliberately separate from materialized field-value coverage.</summary>
public enum HistoricalCoverageAcquisitionStatus { Full, Partial, Unavailable, PermissionDenied, RateLimited, Failed, NotRequested, Unknown }
public enum HistoricalDatasetScopeKind { MarketUniverse, ValidationSubset, ExplicitSymbolSet }

public sealed record HistoricalDatasetScope(
    HistoricalDatasetScopeKind Kind = HistoricalDatasetScopeKind.MarketUniverse,
    string MarketUniverse = "A-share",
    IReadOnlyList<string>? Symbols = null)
{
    public IReadOnlyList<string> Symbols { get; init; } = (Symbols ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}

public sealed record HistoricalCoverageScope(
    DateOnly StartDate,
    DateOnly EndDate,
    string? Exchange = null,
    string? Symbol = null,
    string? SymbolPartition = null,
    string? IndexCode = null,
    string? ListStatus = null);

public sealed record HistoricalCoverageChunk(DateOnly StartDate, DateOnly EndDate, int ReturnedRows, bool ResponseCapHit = false);

/// <summary>Stable, source-derived evidence explaining exactly why coverage is Full or Partial.</summary>
public sealed record HistoricalCoverageEvidence(
    string Endpoint,
    string Source,
    HistoricalCoverageScope Scope,
    int? MaxRowsPerRequest,
    bool PaginationSupported,
    string? PermissionRequirement,
    string QueryShape,
    int QueryCount,
    IReadOnlyList<HistoricalCoverageChunk>? RequestedChunks,
    int ReturnedRows,
    bool ResponseCapHit,
    int DuplicateRows,
    int InvalidRows,
    int RetryCount,
    int RateLimitEvents,
    int PermissionDeniedEvents,
    int? ExpectedCount,
    int ObservedCount,
    int? MissingCount,
    HistoricalCoverageAcquisitionStatus CoverageStatus,
    string ProofMethod,
    string? FailureReason = null,
    DateTimeOffset? RetrievedAtUtc = null)
{
    public IReadOnlyList<HistoricalCoverageChunk> RequestedChunks { get; init; } = RequestedChunks ?? Array.Empty<HistoricalCoverageChunk>();
}

public sealed record HistoricalAcquisitionResult<T>(IReadOnlyList<T> Data, IReadOnlyList<HistoricalCoverageEvidence>? CoverageEvidence = null, IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<HistoricalCoverageEvidence> CoverageEvidence { get; init; } = CoverageEvidence ?? Array.Empty<HistoricalCoverageEvidence>();
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? Array.Empty<string>();
}

public sealed record HistoricalStrategyCapabilityExplanation(
    SparrowStrategyMode Strategy,
    HistoricalReplaySupport Status,
    IReadOnlyList<string>? ReasonCodes = null)
{
    public IReadOnlyList<string> ReasonCodes { get; init; } = (ReasonCodes ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal).ToArray();
}

/// <summary>Dataset-level declaration. Per-observation values still decide whether a symbol can be evaluated at T.</summary>
public sealed record HistoricalFieldCapability(
    HistoricalField Field,
    HistoricalFieldOrigin Origin,
    HistoricalFieldCoverage Coverage,
    HistoricalValueUnit Unit = HistoricalValueUnit.Unknown);

public enum HistoricalPriceAdjustmentMode { Unknown, Raw, ForwardAdjusted, BackwardAdjusted }

/// <summary>One coherent adjustment contract per symbol; segmented mixed-mode series are not supported.</summary>
public sealed record HistoricalPriceSeriesProvenance(
    string Symbol,
    string Source,
    HistoricalPriceAdjustmentMode AdjustmentMode,
    DateOnly? ObservationStart = null,
    DateOnly? ObservationEnd = null,
    DateTimeOffset? AcquiredAt = null);

public sealed record HistoricalMarketContextProvenance(
    DateOnly TradingDate,
    HistoricalFieldOrigin ClassicMarketRegimeOrigin = HistoricalFieldOrigin.Unknown,
    string ClassicMarketRegimeSource = "",
    HistoricalFieldOrigin V2ShanghaiDailyPercentOrigin = HistoricalFieldOrigin.Unknown,
    string V2ShanghaiDailyPercentSource = "");

public sealed record HistoricalDatasetMetadata(
    string DatasetId,
    string Source,
    DateTimeOffset? CreatedAt,
    HistoricalUniverseQuality UniverseQuality,
    IReadOnlyList<string>? Warnings = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = Warnings ?? Array.Empty<string>();
}

public sealed record HistoricalSecurityObservation(
    HistoricalSecurity Security,
    HistoricalSecurityStatus SecurityStatus,
    HistoricalObservationStatus ObservationStatus,
    QuoteSnapshot? Quote,
    KlineSeries? Klines,
    string Reason = "");

/// <summary>Optional source-declared state. Missing data is never promoted to Suspended by inference.</summary>
public sealed record HistoricalObservationDeclaration(
    DateOnly TradingDate,
    string Symbol,
    HistoricalSecurityStatus SecurityStatus,
    HistoricalObservationStatus ObservationStatus,
    string Reason = "");

/// <summary>Positive ST evidence from a dated historical source. Absence only means non-ST when coverage is Full.</summary>
public sealed record HistoricalRiskStatusObservation(DateOnly TradingDate, string Symbol, bool IsSt, string Source);
public sealed record HistoricalAdjustmentFactor(string Symbol, DateOnly TradingDate, double Factor, string Source, DateTimeOffset? RetrievedAtUtc = null);

/// <summary>Structured quality facts; intentionally not a numeric score.</summary>
public sealed record HistoricalDatasetQualitySummary(
    HistoricalUniverseQuality UniverseQuality,
    HistoricalLifecycleQuality LifecycleQuality,
    HistoricalFieldCoverage HistoricalStCoverage,
    HistoricalFieldCoverage SuspensionCoverage,
    HistoricalFieldCoverage TurnoverCoverage,
    HistoricalFieldCoverage AdjustmentFactorCoverage,
    HistoricalFieldCoverage IndexCoverage,
    HistoricalFieldCoverage OuterInnerCoverage,
    IReadOnlyList<string>? Reasons = null)
{
    public IReadOnlyList<string> Reasons { get; init; } = Reasons ?? Array.Empty<string>();
}

/// <summary>Pure resolver for explicit historical universe snapshots and lifecycle boundaries.</summary>
public sealed class HistoricalUniverseResolver
{
    public HistoricalUniverseResolution GetUniverseAsOf(HistoricalMarketDataset dataset, DateOnly tradingDate)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!dataset.Universes.TryGetValue(tradingDate, out HistoricalUniverseSnapshot? universe))
            throw new ArgumentException("Historical universe is unavailable for the requested trading date.", nameof(tradingDate));

        var active = new List<HistoricalSecurity>();
        var excluded = new Dictionary<string, HistoricalObservationStatus>(StringComparer.Ordinal);
        foreach (string symbol in universe.SecuritySymbols.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!dataset.Securities.TryGetValue(symbol, out HistoricalSecurity? security))
            {
                excluded[symbol] = HistoricalObservationStatus.UnknownDataGap;
                continue;
            }
            if (security.ListingDate.HasValue && security.ListingDate.Value > tradingDate)
            {
                excluded[symbol] = HistoricalObservationStatus.NotYetListed;
                continue;
            }
            if (security.DelistingEffectiveDate.HasValue && security.DelistingEffectiveDate.Value <= tradingDate)
            {
                excluded[symbol] = HistoricalObservationStatus.Delisted;
                continue;
            }
            active.Add(security);
        }
        return new HistoricalUniverseResolution(universe, active, excluded);
    }
}
