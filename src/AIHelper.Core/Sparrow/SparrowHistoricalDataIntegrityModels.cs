using AIHelper.Core.StockData;

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
