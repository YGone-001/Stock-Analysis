namespace AIHelper.Core.Sparrow;

/// <summary>Index-series price semantics, intentionally separate from stock qfq/hfq adjustment modes.</summary>
public enum HistoricalBenchmarkPriceBasis { IndexClose }

public sealed record HistoricalBenchmarkObservation(DateOnly TradingDate, double Close);

/// <summary>Semantic acquisition facts for a benchmark series. AcquiredAt is diagnostic only.</summary>
public sealed record HistoricalBenchmarkProvenance(
    string Source,
    DateOnly RequestedStartDate,
    DateOnly RequestedEndDate,
    string CoverageProof,
    DateTimeOffset? AcquiredAt = null)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(CoverageProof);
        if (RequestedStartDate > RequestedEndDate) throw new ArgumentException("Benchmark provenance date range is invalid.");
    }
}

/// <summary>Immutable close-level index evidence used only for benchmark-relative research.</summary>
public sealed class HistoricalBenchmarkSeries
{
    public HistoricalBenchmarkSeries(
        string benchmarkId,
        HistoricalBenchmarkPriceBasis priceBasis,
        string source,
        IEnumerable<HistoricalBenchmarkObservation> observations,
        HistoricalFieldCoverage coverage,
        HistoricalBenchmarkProvenance provenance,
        string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(provenance);
        provenance.Validate();

        HistoricalBenchmarkObservation[] canonical = observations.OrderBy(item => item.TradingDate).ToArray();
        if (canonical.GroupBy(item => item.TradingDate).Any(group => group.Count() != 1))
            throw new ArgumentException("Benchmark series contains duplicate trading dates.", nameof(observations));
        if (canonical.Any(item => !double.IsFinite(item.Close) || item.Close <= 0))
            throw new ArgumentException("Benchmark close must be finite and positive.", nameof(observations));

        BenchmarkId = benchmarkId;
        PriceBasis = priceBasis;
        Source = source;
        DisplayName = displayName;
        Observations = Array.AsReadOnly(canonical);
        Coverage = coverage;
        Provenance = provenance;
    }

    public string BenchmarkId { get; }
    public string? DisplayName { get; }
    public HistoricalBenchmarkPriceBasis PriceBasis { get; }
    public string Source { get; }
    public IReadOnlyList<HistoricalBenchmarkObservation> Observations { get; }
    public HistoricalFieldCoverage Coverage { get; }
    public HistoricalBenchmarkProvenance Provenance { get; }
}

/// <summary>Stable reason identifiers for pure benchmark outcome evaluation and overlay attrition.</summary>
public static class SparrowBenchmarkReasonCodes
{
    public const string BenchmarkSeriesNotFound = "BENCHMARK_SERIES_NOT_FOUND";
    public const string BenchmarkCoverageNone = "BENCHMARK_COVERAGE_NONE";
    public const string BenchmarkCoveragePartial = "BENCHMARK_COVERAGE_PARTIAL";
    public const string BenchmarkEntryMissing = "BENCHMARK_ENTRY_MISSING";
    public const string BenchmarkExitMissing = "BENCHMARK_EXIT_MISSING";
    public const string TargetMarketDateUnavailable = "TARGET_MARKET_DATE_UNAVAILABLE";
    public const string StockOutcomeUnavailable = "STOCK_OUTCOME_UNAVAILABLE";
}

public sealed record BenchmarkForwardReturn(int HorizonTradingDays, DateOnly? EntryDate, DateOnly? ExitDate, double? EntryClose, double? ExitClose, double? ReturnPercent, bool Available, string? ReasonCode);
public sealed record RelativeForwardReturn(int HorizonTradingDays, double? StockReturnPercent, bool StockAvailable, double? BenchmarkReturnPercent, bool BenchmarkAvailable, double? ExcessReturnPercent, bool ExcessAvailable, string? UnavailableReasonCode);

public sealed class SparrowBenchmarkAnalysisRequest
{
    public SparrowBenchmarkAnalysisRequest(SparrowBacktestRequest backtestRequest, string benchmarkId)
    {
        ArgumentNullException.ThrowIfNull(backtestRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkId);
        if (backtestRequest.RoundTripCostRate != 0 || backtestRequest.SlippageRate != 0)
            throw new ArgumentException("Phase 3.3 benchmark analysis requires zero round-trip cost and slippage.", nameof(backtestRequest));
        if (backtestRequest.StrategyMode is not AIHelper.Models.SparrowStrategyMode.Classic and not AIHelper.Models.SparrowStrategyMode.V2)
            throw new ArgumentException("Benchmark analysis supports Classic and V2 only.", nameof(backtestRequest));
        BacktestRequest = backtestRequest;
        BenchmarkId = benchmarkId;
    }

    public SparrowBacktestRequest BacktestRequest { get; }
    public string BenchmarkId { get; }
}

public sealed record SparrowBenchmarkRelativeSelection(SparrowBacktestSelection Selection, IReadOnlyList<BenchmarkForwardReturn> BenchmarkOutcomes, IReadOnlyList<RelativeForwardReturn> RelativeOutcomes);
public sealed record SparrowBenchmarkHorizonMetrics(int HorizonTradingDays, int SelectionCount, int StockAvailableCount, int BenchmarkAvailableCount, int ExcessAvailableCount, double? AverageExcessReturnPercent, double? MedianExcessReturnPercent, double? OutperformanceRate);

/// <summary>Selection-weighted, gross close-to-close arithmetic benchmark-relative research result.</summary>
public sealed record SparrowBenchmarkAnalysisResult(
    SparrowBenchmarkAnalysisRequest Request,
    SparrowBacktestResult? BaseBacktestResult,
    string DatasetId,
    string DatasetFingerprint,
    string StrategyParameterFingerprint,
    string AnalysisFingerprint,
    HistoricalPriceAdjustmentMode StockPriceAdjustmentMode,
    HistoricalReplaySupport Support,
    IReadOnlyList<string> SupportReasonCodes,
    HistoricalBenchmarkProvenance? BenchmarkProvenance,
    IReadOnlyList<SparrowBenchmarkRelativeSelection> RelativeSelections,
    IReadOnlyList<SparrowBenchmarkHorizonMetrics> HorizonMetrics,
    IReadOnlyDictionary<string, int> UnavailableReasonCounts,
    IReadOnlyList<string> Warnings)
{
    public const string WeightingMethod = "SelectionWeighted";
    public const string ReturnBasis = "GrossCloseToClose";
    public const string StockReturnBasis = "StockCloseToClose";
    public const string BenchmarkReturnBasis = "IndexCloseToClose";
    public const string ExcessReturnFormula = "StockReturnPercent - BenchmarkReturnPercent";
}
