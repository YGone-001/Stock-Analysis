using System.Collections.ObjectModel;
using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Pure, close-level benchmark outcome evaluator. It never queries a provider or alters strategy inputs.</summary>
public sealed class HistoricalBenchmarkOutcomeEvaluator
{
    public IReadOnlyList<BenchmarkForwardReturn> Evaluate(HistoricalMarketDataset dataset, string benchmarkId, DateOnly replayDate, IReadOnlyList<int> horizons)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkId);
        int start = IndexOf(dataset.TradingDates, replayDate);
        if (!dataset.Benchmarks.TryGetValue(benchmarkId, out HistoricalBenchmarkSeries? series))
            return horizons.Distinct().Order().Select(horizon => Missing(horizon, null, null, SparrowBenchmarkReasonCodes.BenchmarkSeriesNotFound)).ToArray();
        if (start < 0)
            return horizons.Distinct().Order().Select(horizon => Missing(horizon, null, null, SparrowBenchmarkReasonCodes.TargetMarketDateUnavailable)).ToArray();

        Dictionary<DateOnly, double> closes = series.Observations.ToDictionary(item => item.TradingDate, item => item.Close);
        closes.TryGetValue(replayDate, out double entry);
        bool entryAvailable = closes.ContainsKey(replayDate);
        return horizons.Distinct().Order().Select(horizon =>
        {
            DateOnly? exitDate = start + horizon < dataset.TradingDates.Count ? dataset.TradingDates[start + horizon] : null;
            if (!exitDate.HasValue) return Missing(horizon, replayDate, null, SparrowBenchmarkReasonCodes.TargetMarketDateUnavailable, entryAvailable ? entry : null);
            if (!entryAvailable) return Missing(horizon, replayDate, exitDate, SparrowBenchmarkReasonCodes.BenchmarkEntryMissing);
            if (!closes.TryGetValue(exitDate.Value, out double exit)) return Missing(horizon, replayDate, exitDate, SparrowBenchmarkReasonCodes.BenchmarkExitMissing, entry);
            return new BenchmarkForwardReturn(horizon, replayDate, exitDate, entry, exit, (exit / entry - 1) * 100, true, null);
        }).ToArray();
    }

    private static BenchmarkForwardReturn Missing(int horizon, DateOnly? entryDate, DateOnly? exitDate, string reason, double? entry = null) =>
        new(horizon, entryDate, exitDate, entry, null, null, false, reason);
    private static int IndexOf(IReadOnlyList<DateOnly> dates, DateOnly date)
    {
        for (int index = 0; index < dates.Count; index++) if (dates[index] == date) return index;
        return -1;
    }
}

/// <summary>Selection-level benchmark overlay over the existing deterministic historical backtest.</summary>
public sealed class SparrowHistoricalBenchmarkAnalysisEngine
{
    private readonly SparrowHistoricalBacktestEngine _backtest;
    private readonly HistoricalBenchmarkOutcomeEvaluator _benchmarkOutcomes;

    public SparrowHistoricalBenchmarkAnalysisEngine(SparrowHistoricalBacktestEngine? backtest = null, HistoricalBenchmarkOutcomeEvaluator? benchmarkOutcomes = null)
    {
        _backtest = backtest ?? new SparrowHistoricalBacktestEngine();
        _benchmarkOutcomes = benchmarkOutcomes ?? new HistoricalBenchmarkOutcomeEvaluator();
    }

    public SparrowBenchmarkAnalysisResult Analyze(HistoricalMarketDataset dataset, SparrowBenchmarkAnalysisRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(request);
        string parameterFingerprint = SparrowHistoricalFingerprint.Parameters(request.BacktestRequest);
        string analysisFingerprint = SparrowHistoricalFingerprint.BenchmarkAnalysis(request, dataset.Fingerprint, parameterFingerprint);
        if (!dataset.Benchmarks.TryGetValue(request.BenchmarkId, out HistoricalBenchmarkSeries? benchmark))
            return Unsupported(dataset, request, parameterFingerprint, analysisFingerprint, SparrowBenchmarkReasonCodes.BenchmarkSeriesNotFound, "Requested benchmark series is not present in the immutable dataset.");
        if (benchmark.Coverage == HistoricalFieldCoverage.None)
            return Unsupported(dataset, request, parameterFingerprint, analysisFingerprint, SparrowBenchmarkReasonCodes.BenchmarkCoverageNone, "Requested benchmark series has no usable close-level coverage.", benchmark.Provenance);
        if (dataset.StrategyCapabilities.TryGetValue(request.BacktestRequest.StrategyMode, out HistoricalStrategyCapabilityExplanation? capability)
            && capability.Status == HistoricalReplaySupport.Unsupported)
            return Unsupported(dataset, request, parameterFingerprint, analysisFingerprint, string.Join(',', capability.ReasonCodes), "Requested strategy is unsupported by this dataset.", benchmark.Provenance);

        SparrowBacktestResult baseResult = _backtest.Run(dataset, request.BacktestRequest, cancellationToken);
        List<SparrowBenchmarkRelativeSelection> selections = [];
        foreach (SparrowBacktestSelection selection in baseResult.Selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<BenchmarkForwardReturn> benchmarkReturns = _benchmarkOutcomes.Evaluate(dataset, request.BenchmarkId, selection.ReplayDate, request.BacktestRequest.Horizons);
            Dictionary<int, ForwardReturn> stocks = selection.Outcomes.ToDictionary(item => item.HorizonTradingDays);
            RelativeForwardReturn[] relative = benchmarkReturns.Select(benchmarkReturn => Relative(stocks.TryGetValue(benchmarkReturn.HorizonTradingDays, out ForwardReturn? stock) ? stock : null, benchmarkReturn)).ToArray();
            selections.Add(new SparrowBenchmarkRelativeSelection(selection, benchmarkReturns, relative));
        }

        SparrowBenchmarkHorizonMetrics[] metrics = request.BacktestRequest.Horizons.Distinct().Order().Select(horizon => Metrics(horizon, selections)).ToArray();
        Dictionary<string, int> reasons = selections.SelectMany(item => item.RelativeOutcomes).Where(item => !item.ExcessAvailable && !string.IsNullOrWhiteSpace(item.UnavailableReasonCode))
            .GroupBy(item => item.UnavailableReasonCode!, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
        if (benchmark.Coverage != HistoricalFieldCoverage.Full)
            reasons.TryAdd(SparrowBenchmarkReasonCodes.BenchmarkCoveragePartial, 1);
        List<string> warnings = baseResult.Warnings.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
        if (benchmark.Coverage != HistoricalFieldCoverage.Full) warnings.Add("Benchmark coverage is partial.");
        warnings.Add(dataset.PriceAdjustmentMode == HistoricalPriceAdjustmentMode.Raw
            ? "Benchmark analysis compares raw stock close-to-close return with index close-to-close return."
            : $"Benchmark analysis preserves stock price adjustment declaration '{dataset.PriceAdjustmentMode}' and does not transform prices.");
        HistoricalReplaySupport support = reasons.Count == 0 ? HistoricalReplaySupport.Supported : HistoricalReplaySupport.Partial;
        return new SparrowBenchmarkAnalysisResult(request, baseResult, dataset.DatasetId, dataset.Fingerprint, parameterFingerprint, analysisFingerprint,
            dataset.PriceAdjustmentMode, support, benchmark.Provenance, selections, metrics, new ReadOnlyDictionary<string, int>(reasons), warnings.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray());
    }

    private static RelativeForwardReturn Relative(ForwardReturn? stock, BenchmarkForwardReturn benchmark)
    {
        bool stockAvailable = stock is { Available: true, ReturnPercent: not null };
        bool benchmarkAvailable = benchmark.Available && benchmark.ReturnPercent.HasValue;
        string? reason = !stockAvailable ? SparrowBenchmarkReasonCodes.StockOutcomeUnavailable : !benchmarkAvailable ? benchmark.ReasonCode : null;
        double? excess = stockAvailable && benchmarkAvailable ? stock!.ReturnPercent!.Value - benchmark.ReturnPercent!.Value : null;
        return new RelativeForwardReturn(benchmark.HorizonTradingDays, stock?.ReturnPercent, stockAvailable, benchmark.ReturnPercent, benchmarkAvailable, excess, excess.HasValue, reason);
    }

    private static SparrowBenchmarkHorizonMetrics Metrics(int horizon, IReadOnlyList<SparrowBenchmarkRelativeSelection> selections)
    {
        RelativeForwardReturn[] outcomes = selections.SelectMany(item => item.RelativeOutcomes).Where(item => item.HorizonTradingDays == horizon).ToArray();
        double[] excess = outcomes.Where(item => item.ExcessAvailable && item.ExcessReturnPercent.HasValue).Select(item => item.ExcessReturnPercent!.Value).Order().ToArray();
        double? median = excess.Length == 0 ? null : excess.Length % 2 == 1 ? excess[excess.Length / 2] : (excess[excess.Length / 2 - 1] + excess[excess.Length / 2]) / 2;
        return new SparrowBenchmarkHorizonMetrics(horizon, outcomes.Length, outcomes.Count(item => item.StockAvailable), outcomes.Count(item => item.BenchmarkAvailable), excess.Length,
            excess.Length == 0 ? null : excess.Average(), median, excess.Length == 0 ? null : excess.Count(item => item > 0) / (double)excess.Length);
    }

    private static SparrowBenchmarkAnalysisResult Unsupported(HistoricalMarketDataset dataset, SparrowBenchmarkAnalysisRequest request, string parameterFingerprint,
        string analysisFingerprint, string reason, string warning, HistoricalBenchmarkProvenance? provenance = null) => new(
        request, null, dataset.DatasetId, dataset.Fingerprint, parameterFingerprint, analysisFingerprint, dataset.PriceAdjustmentMode, HistoricalReplaySupport.Unsupported, provenance,
        Array.Empty<SparrowBenchmarkRelativeSelection>(), Array.Empty<SparrowBenchmarkHorizonMetrics>(),
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(StringComparer.Ordinal) { [reason] = 1 }), new[] { warning });
}
