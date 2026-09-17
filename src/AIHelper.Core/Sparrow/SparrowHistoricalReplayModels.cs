using System.Collections.ObjectModel;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

public enum HistoricalReplaySupport { Supported, Partial, Unsupported }

public sealed record HistoricalDataCapabilities(
    bool HasAmount,
    bool HasTurnover,
    bool HasOuterVolume,
    bool HasInnerVolume,
    bool HasClassicMarketRegime,
    bool HasV2ShanghaiDailyPercent)
{
    public static HistoricalDataCapabilities Complete { get; } = new(true, true, true, true, true, true);
}

public sealed record HistoricalQuoteObservation(DateOnly TradingDate, QuoteSnapshot Quote);
public sealed record HistoricalMarketContext(
    DateOnly TradingDate,
    SparrowMarketRegime? ClassicMarketRegime = null,
    double? V2ShanghaiDailyPercent = null);

/// <summary>Immutable, preloaded research data. It never performs live market-data I/O.</summary>
public sealed class HistoricalMarketDataset
{
    public HistoricalMarketDataset(
        string datasetId,
        IEnumerable<DateOnly> tradingDates,
        IEnumerable<HistoricalQuoteObservation> quotes,
        IEnumerable<KlineSeries> klineSeries,
        IEnumerable<HistoricalMarketContext>? marketContexts = null,
        HistoricalDataCapabilities? capabilities = null,
        string priceAdjustmentMode = "Unknown",
        string source = "InMemory",
        int schemaVersion = 1,
        HistoricalDatasetMetadata? metadata = null,
        IEnumerable<HistoricalSecurity>? securities = null,
        IEnumerable<HistoricalUniverseSnapshot>? universes = null,
        IEnumerable<HistoricalFieldCapability>? fieldCapabilities = null,
        IEnumerable<HistoricalPriceSeriesProvenance>? priceSeriesProvenance = null,
        IEnumerable<HistoricalMarketContextProvenance>? marketContextProvenance = null,
        IEnumerable<HistoricalObservationDeclaration>? observationDeclarations = null,
        IEnumerable<HistoricalRiskStatusObservation>? riskStatusObservations = null,
        IEnumerable<HistoricalAdjustmentFactor>? adjustmentFactors = null,
        HistoricalDatasetQualitySummary? qualitySummary = null,
        HistoricalDatasetScope? datasetScope = null,
        IEnumerable<HistoricalCoverageEvidence>? coverageEvidence = null,
        IEnumerable<HistoricalStrategyCapabilityExplanation>? strategyCapabilities = null,
        IEnumerable<HistoricalBenchmarkSeries>? benchmarks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        DatasetId = datasetId;
        TradingDates = tradingDates.Distinct().OrderBy(date => date).ToArray();
        if (TradingDates.Count == 0) throw new ArgumentException("Dataset requires at least one trading date.", nameof(tradingDates));
        if (TradingDates.Zip(TradingDates.Skip(1)).Any(pair => pair.First >= pair.Second)) throw new ArgumentException("Trading dates must be ascending and unique.", nameof(tradingDates));
        Quotes = quotes.GroupBy(item => (item.TradingDate, item.Quote.Symbol), StringComparerTuple.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single().Quote, StringComparerTuple.Ordinal);
        Klines = klineSeries.ToDictionary(series => series.Symbol, StringComparer.Ordinal);
        MarketContexts = (marketContexts ?? Array.Empty<HistoricalMarketContext>())
            .ToDictionary(context => context.TradingDate);
        SchemaVersion = schemaVersion;
        PriceAdjustmentMode = ParseAdjustmentMode(priceAdjustmentMode);
        LegacyPriceAdjustmentMode = priceAdjustmentMode;
        Source = source;
        Metadata = metadata ?? new HistoricalDatasetMetadata(datasetId, source, null,
            schemaVersion <= 1 ? HistoricalUniverseQuality.DerivedFromObservations : HistoricalUniverseQuality.Unknown);
        Securities = BuildSecurities(securities, quotes, Klines);
        Universes = BuildUniverses(universes, TradingDates, Quotes);
        Dictionary<HistoricalField, HistoricalFieldCapability> suppliedFieldCapabilities = (fieldCapabilities ?? Array.Empty<HistoricalFieldCapability>())
            .GroupBy(capability => capability.Field)
            .ToDictionary(group => group.Key, group => group.Single());
        Capabilities = capabilities ?? (suppliedFieldCapabilities.Count > 0 ? CompatibilityCapabilities(suppliedFieldCapabilities) : HistoricalDataCapabilities.Complete);
        FieldCapabilities = suppliedFieldCapabilities.Count > 0
            ? suppliedFieldCapabilities
            : schemaVersion <= 1 ? LegacyFieldCapabilities(Quotes, Capabilities) : suppliedFieldCapabilities;
        PriceSeriesProvenance = (priceSeriesProvenance ?? (schemaVersion <= 1
                ? Klines.Keys.Select(symbol => new HistoricalPriceSeriesProvenance(symbol, source, PriceAdjustmentMode))
                : Array.Empty<HistoricalPriceSeriesProvenance>()))
            .ToDictionary(provenance => provenance.Symbol, StringComparer.Ordinal);
        MarketContextProvenance = (marketContextProvenance ?? (schemaVersion <= 1
                ? MarketContexts.Keys.Select(date => new HistoricalMarketContextProvenance(date))
                : Array.Empty<HistoricalMarketContextProvenance>()))
            .ToDictionary(provenance => provenance.TradingDate);
        ObservationDeclarations = (observationDeclarations ?? Array.Empty<HistoricalObservationDeclaration>())
            .ToDictionary(declaration => (declaration.TradingDate, declaration.Symbol), StringComparerTuple.Ordinal);
        RiskStatusObservations = (riskStatusObservations ?? Array.Empty<HistoricalRiskStatusObservation>())
            .ToDictionary(observation => (observation.TradingDate, observation.Symbol), StringComparerTuple.Ordinal);
        AdjustmentFactors = (adjustmentFactors ?? Array.Empty<HistoricalAdjustmentFactor>())
            .ToDictionary(factor => (factor.TradingDate, factor.Symbol), StringComparerTuple.Ordinal);
        QualitySummary = qualitySummary ?? new HistoricalDatasetQualitySummary(Metadata.UniverseQuality, HistoricalLifecycleQuality.Unknown,
            HistoricalFieldCoverage.Unknown, HistoricalFieldCoverage.Unknown, HistoricalFieldCoverage.Unknown,
            HistoricalFieldCoverage.Unknown, HistoricalFieldCoverage.Unknown, HistoricalFieldCoverage.Unknown, Array.Empty<string>());
        HasExplicitDatasetScope = datasetScope is not null;
        DatasetScope = datasetScope ?? new HistoricalDatasetScope();
        CoverageEvidence = (coverageEvidence ?? Array.Empty<HistoricalCoverageEvidence>())
            .OrderBy(item => item.Endpoint, StringComparer.Ordinal).ThenBy(item => item.Scope.Symbol, StringComparer.Ordinal)
            .ThenBy(item => item.Scope.ListStatus, StringComparer.Ordinal).ThenBy(item => item.Scope.StartDate).ThenBy(item => item.Scope.EndDate).ToArray();
        ValidateCoverageEvidence(CoverageEvidence);
        StrategyCapabilities = (strategyCapabilities ?? Array.Empty<HistoricalStrategyCapabilityExplanation>())
            .GroupBy(item => item.Strategy).ToDictionary(group => group.Key, group => group.Single());
        HistoricalBenchmarkSeries[] canonicalBenchmarks = (benchmarks ?? Array.Empty<HistoricalBenchmarkSeries>())
            .OrderBy(item => item.BenchmarkId, StringComparer.Ordinal).ToArray();
        if (canonicalBenchmarks.GroupBy(item => item.BenchmarkId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ArgumentException("Benchmark identifiers must be unique.", nameof(benchmarks));
        if (canonicalBenchmarks.SelectMany(item => item.Observations).Any(item => !TradingDates.Contains(item.TradingDate)))
            throw new ArgumentException("Benchmark observations must use dataset trading dates.", nameof(benchmarks));
        if (canonicalBenchmarks.Any(item => item.Coverage == HistoricalFieldCoverage.Full && !item.Observations.Select(observation => observation.TradingDate).ToHashSet().SetEquals(TradingDates)))
            throw new ArgumentException("A full benchmark series must cover exactly the dataset trading-date set.", nameof(benchmarks));
        Benchmarks = new ReadOnlyDictionary<string, HistoricalBenchmarkSeries>(canonicalBenchmarks.ToDictionary(item => item.BenchmarkId, StringComparer.Ordinal));
        Fingerprint = SparrowHistoricalFingerprint.Dataset(this);
    }

    public int SchemaVersion { get; }
    public string DatasetId { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<DateOnly> TradingDates { get; }
    public IReadOnlyDictionary<(DateOnly Date, string Symbol), QuoteSnapshot> Quotes { get; }
    public IReadOnlyDictionary<string, KlineSeries> Klines { get; }
    public IReadOnlyDictionary<DateOnly, HistoricalMarketContext> MarketContexts { get; }
    public HistoricalDataCapabilities Capabilities { get; }
    public HistoricalPriceAdjustmentMode PriceAdjustmentMode { get; }
    /// <summary>Original V1 declaration retained only for legacy fingerprint compatibility and migration diagnostics.</summary>
    public string LegacyPriceAdjustmentMode { get; }
    public string Source { get; }
    public HistoricalDatasetMetadata Metadata { get; }
    public IReadOnlyDictionary<string, HistoricalSecurity> Securities { get; }
    public IReadOnlyDictionary<DateOnly, HistoricalUniverseSnapshot> Universes { get; }
    public IReadOnlyDictionary<HistoricalField, HistoricalFieldCapability> FieldCapabilities { get; }
    public IReadOnlyDictionary<string, HistoricalPriceSeriesProvenance> PriceSeriesProvenance { get; }
    public IReadOnlyDictionary<DateOnly, HistoricalMarketContextProvenance> MarketContextProvenance { get; }
    public IReadOnlyDictionary<(DateOnly Date, string Symbol), HistoricalObservationDeclaration> ObservationDeclarations { get; }
    public IReadOnlyDictionary<(DateOnly Date, string Symbol), HistoricalRiskStatusObservation> RiskStatusObservations { get; }
    public IReadOnlyDictionary<(DateOnly Date, string Symbol), HistoricalAdjustmentFactor> AdjustmentFactors { get; }
    public HistoricalDatasetQualitySummary QualitySummary { get; }
    public HistoricalDatasetScope DatasetScope { get; }
    /// <summary>False preserves the semantic fingerprint of pre-3.1D Schema-V2 artifacts.</summary>
    public bool HasExplicitDatasetScope { get; }
    public IReadOnlyList<HistoricalCoverageEvidence> CoverageEvidence { get; }
    public IReadOnlyDictionary<SparrowStrategyMode, HistoricalStrategyCapabilityExplanation> StrategyCapabilities { get; }
    /// <summary>Optional immutable benchmark close series. Empty preserves all pre-3.3 dataset semantics.</summary>
    public IReadOnlyDictionary<string, HistoricalBenchmarkSeries> Benchmarks { get; }
    public bool TryGetQuote(DateOnly date, string symbol, out QuoteSnapshot quote) => Quotes.TryGetValue((date, symbol), out quote!);

    public HistoricalFieldCapability? GetFieldCapability(HistoricalField field) =>
        FieldCapabilities.TryGetValue(field, out HistoricalFieldCapability? capability) ? capability : null;

    private static void ValidateCoverageEvidence(IEnumerable<HistoricalCoverageEvidence> evidence)
    {
        foreach (HistoricalCoverageEvidence item in evidence)
        {
            if (string.IsNullOrWhiteSpace(item.Endpoint) || string.IsNullOrWhiteSpace(item.Source)) throw new ArgumentException("Coverage evidence endpoint/source is required.");
            if (item.Scope.StartDate > item.Scope.EndDate || item.ReturnedRows < 0 || item.ObservedCount < 0 || item.DuplicateRows < 0 || item.InvalidRows < 0 || item.QueryCount < 0)
                throw new ArgumentException("Coverage evidence contains invalid range or count.");
            if (item.MaxRowsPerRequest is <= 0) throw new ArgumentException("Coverage evidence maximum rows must be positive when supplied.");
            if (item.CoverageStatus == HistoricalCoverageAcquisitionStatus.Full && (item.ResponseCapHit || item.FailureReason is not null || item.PermissionDeniedEvents > 0 || item.RateLimitEvents > 0 || item.MissingCount is > 0))
                throw new ArgumentException("Full coverage evidence cannot contain caps, failures, permission/rate-limit events, or defined missing observations.");
        }
    }

    private static IReadOnlyDictionary<string, HistoricalSecurity> BuildSecurities(
        IEnumerable<HistoricalSecurity>? supplied,
        IEnumerable<HistoricalQuoteObservation> quotes,
        IReadOnlyDictionary<string, KlineSeries> klines)
    {
        if (supplied is not null)
            return supplied.ToDictionary(security => security.Symbol, StringComparer.Ordinal);

        return quotes.GroupBy(observation => observation.Quote.Symbol, StringComparer.Ordinal)
            .Select(group => new HistoricalSecurity(
                group.Key,
                group.Select(observation => observation.Quote.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? group.Key,
                LifecycleQuality: HistoricalLifecycleQuality.CurrentUniverseFallback,
                ObservedHistoryStart: klines.TryGetValue(group.Key, out KlineSeries? series) && series.Bars.Count > 0
                    ? DateOnly.FromDateTime(series.Bars.Min(bar => bar.Date)) : null))
            .ToDictionary(security => security.Symbol, StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<DateOnly, HistoricalUniverseSnapshot> BuildUniverses(
        IEnumerable<HistoricalUniverseSnapshot>? supplied,
        IReadOnlyList<DateOnly> dates,
        IReadOnlyDictionary<(DateOnly Date, string Symbol), QuoteSnapshot> quotes)
    {
        if (supplied is not null)
            return supplied.ToDictionary(universe => universe.TradingDate);

        return dates.ToDictionary(
            date => date,
            date => new HistoricalUniverseSnapshot(date,
                quotes.Keys.Where(key => key.Date == date).Select(key => key.Symbol).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray(),
                HistoricalUniverseQuality.DerivedFromObservations,
                "V1Compatibility:QuotesAtTradingDate",
                new[] { "Universe was derived from V1 quote observations and is not a complete historical universe." }));
    }

    private static HistoricalDataCapabilities CompatibilityCapabilities(IReadOnlyDictionary<HistoricalField, HistoricalFieldCapability> capabilities) => new(
        Has(capabilities, HistoricalField.Amount),
        Has(capabilities, HistoricalField.Turnover),
        Has(capabilities, HistoricalField.OuterVolume),
        Has(capabilities, HistoricalField.InnerVolume),
        true,
        true);

    private static bool Has(IReadOnlyDictionary<HistoricalField, HistoricalFieldCapability> capabilities, HistoricalField field) =>
        capabilities.TryGetValue(field, out HistoricalFieldCapability? capability)
        && capability.Origin != HistoricalFieldOrigin.Unavailable
        && capability.Coverage is not HistoricalFieldCoverage.None and not HistoricalFieldCoverage.Unknown;

    private static IReadOnlyDictionary<HistoricalField, HistoricalFieldCapability> LegacyFieldCapabilities(
        IReadOnlyDictionary<(DateOnly Date, string Symbol), QuoteSnapshot> quotes,
        HistoricalDataCapabilities legacy)
    {
        return Enum.GetValues<HistoricalField>().Select(field =>
        {
            bool declaredAvailable = field switch
            {
                HistoricalField.Amount => legacy.HasAmount,
                HistoricalField.Turnover => legacy.HasTurnover,
                HistoricalField.OuterVolume => legacy.HasOuterVolume,
                HistoricalField.InnerVolume => legacy.HasInnerVolume,
                _ => true
            };
            bool[] values = quotes.Values.Select(quote => HasValue(quote, field)).ToArray();
            HistoricalFieldCoverage coverage = !declaredAvailable || values.Length == 0 || values.All(value => !value)
                ? HistoricalFieldCoverage.None
                : values.All(value => value) ? HistoricalFieldCoverage.Full : HistoricalFieldCoverage.Partial;
            HistoricalFieldOrigin origin = coverage == HistoricalFieldCoverage.None
                ? HistoricalFieldOrigin.Unavailable : HistoricalFieldOrigin.LegacyDeclared;
            HistoricalValueUnit unit = field switch
            {
                HistoricalField.Amount => HistoricalValueUnit.CurrencyBaseUnit,
                HistoricalField.Turnover or HistoricalField.ChangePercent => HistoricalValueUnit.Percentage,
                HistoricalField.OuterVolume or HistoricalField.InnerVolume => HistoricalValueUnit.Hands,
                _ => HistoricalValueUnit.Unknown
            };
            return new HistoricalFieldCapability(field, origin, coverage, unit);
        }).ToDictionary(capability => capability.Field);
    }

    private static bool HasValue(QuoteSnapshot quote, HistoricalField field) => field switch
    {
        HistoricalField.Price => quote.Price.HasValue,
        HistoricalField.PreviousClose => quote.PreviousClose.HasValue,
        HistoricalField.ChangePercent => quote.ChangePercent.HasValue,
        HistoricalField.Amount => quote.Amount.HasValue,
        HistoricalField.Turnover => quote.Turnover.HasValue,
        HistoricalField.OuterVolume => quote.OuterVolume.HasValue,
        HistoricalField.InnerVolume => quote.InnerVolume.HasValue,
        _ => false
    };

    public static HistoricalPriceAdjustmentMode ParseAdjustmentMode(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "RAW" => HistoricalPriceAdjustmentMode.Raw,
        "FORWARDADJUSTED" or "FORWARD_ADJUSTED" => HistoricalPriceAdjustmentMode.ForwardAdjusted,
        "BACKWARDADJUSTED" or "BACKWARD_ADJUSTED" => HistoricalPriceAdjustmentMode.BackwardAdjusted,
        _ => HistoricalPriceAdjustmentMode.Unknown
    };

    private sealed class StringComparerTuple : IEqualityComparer<(DateOnly Date, string Symbol)>
    {
        public static StringComparerTuple Ordinal { get; } = new();
        public bool Equals((DateOnly Date, string Symbol) x, (DateOnly Date, string Symbol) y) => x.Date == y.Date && StringComparer.Ordinal.Equals(x.Symbol, y.Symbol);
        public int GetHashCode((DateOnly Date, string Symbol) value) => HashCode.Combine(value.Date, StringComparer.Ordinal.GetHashCode(value.Symbol));
    }
}

public sealed record HistoricalSecuritySnapshot(HistoricalSecurity Security, QuoteSnapshot Quote, KlineSeries Klines);
public sealed record HistoricalMarketSnapshot(
    DateOnly TradingDate,
    IReadOnlyList<HistoricalSecuritySnapshot> Securities,
    HistoricalMarketContext? MarketContext,
    HistoricalReplaySupport Support,
    IReadOnlyList<string> Warnings,
    HistoricalUniverseSnapshot? Universe = null,
    IReadOnlyList<HistoricalSecurityObservation>? Observations = null)
{
    public IReadOnlyList<HistoricalSecurityObservation> Observations { get; init; } = Observations ?? Array.Empty<HistoricalSecurityObservation>();
}

public sealed record SparrowClassicParameterSnapshot(bool MacroDef, double MinRise, double MaxRise, double VolRatio, double MinAmount, bool CheckMA60, double MinAdhesion, double MaxAdhesion)
{
    public SparrowClassicScanParameters ToParameters() => new() { MacroDef = MacroDef, MinRise = MinRise, MaxRise = MaxRise, VolRatio = VolRatio, MinAmount = MinAmount, CheckMA60 = CheckMA60, MinAdhesion = MinAdhesion, MaxAdhesion = MaxAdhesion };
    public static SparrowClassicParameterSnapshot From(SparrowClassicScanParameters p) => new(p.MacroDef, p.MinRise, p.MaxRise, p.VolRatio, p.MinAmount, p.CheckMA60, p.MinAdhesion, p.MaxAdhesion);
}
public sealed record SparrowV2ParameterSnapshot(bool MacroDef, double MinRise, double MaxRise, double VolRatio, double MinAmount, bool CheckMA60, double MinAdhesion, double MaxAdhesion, double MinTurnover, double MaxTurnover, double MomentumThreshold, bool CheckAlpha)
{
    public SparrowScanParameters ToParameters() => new() { MacroDef = MacroDef, MinRise = MinRise, MaxRise = MaxRise, VolRatio = VolRatio, MinAmount = MinAmount, CheckMA60 = CheckMA60, MinAdhesion = MinAdhesion, MaxAdhesion = MaxAdhesion, MinTurnover = MinTurnover, MaxTurnover = MaxTurnover, MomentumThreshold = MomentumThreshold, CheckAlpha = CheckAlpha };
    public static SparrowV2ParameterSnapshot From(SparrowScanParameters p) => new(p.MacroDef, p.MinRise, p.MaxRise, p.VolRatio, p.MinAmount, p.CheckMA60, p.MinAdhesion, p.MaxAdhesion, p.MinTurnover, p.MaxTurnover, p.MomentumThreshold, p.CheckAlpha);
}
public sealed record SparrowReplayRequest(SparrowStrategyMode StrategyMode, string StrategyVersion, DateOnly TradingDate, int TopN, SparrowClassicParameterSnapshot? ClassicParameters = null, SparrowV2ParameterSnapshot? V2Parameters = null);
public sealed record SparrowReplaySelection(string Code, string Name, SparrowRankedCandidate RankedCandidate, string ReasonCode, string Reason);
public sealed record SparrowReplayResult(SparrowReplayRequest Request, string ParameterFingerprint, string DatasetId, string DatasetFingerprint, HistoricalReplaySupport Support, IReadOnlyList<SparrowReplaySelection> Selections, IReadOnlyList<string> Warnings);
public sealed record ForwardReturn(int HorizonTradingDays, double? EntryPrice, double? ExitPrice, double? ReturnPercent, bool Available);
public sealed record SparrowBacktestRequest(SparrowStrategyMode StrategyMode, string StrategyVersion, DateOnly StartDate, DateOnly EndDate, int TopN, IReadOnlyList<int> Horizons, double RoundTripCostRate = 0, double SlippageRate = 0, SparrowClassicParameterSnapshot? ClassicParameters = null, SparrowV2ParameterSnapshot? V2Parameters = null);
public sealed record SparrowBacktestSelection(DateOnly ReplayDate, SparrowReplaySelection Selection, IReadOnlyList<ForwardReturn> Outcomes);
public sealed record SparrowHorizonMetrics(int HorizonTradingDays, int SelectionCount, int AvailableCount, double? AverageReturnPercent, double? MedianReturnPercent, double? WinRate);
public sealed record SparrowBacktestResult(SparrowBacktestRequest Request, string ParameterFingerprint, string DatasetId, string DatasetFingerprint, IReadOnlyList<SparrowBacktestSelection> Selections, IReadOnlyList<SparrowHorizonMetrics> Metrics, IReadOnlyList<string> Warnings);
