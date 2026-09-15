using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

public sealed class HistoricalSnapshotBuilder
{
    public HistoricalMarketSnapshot Build(HistoricalMarketDataset dataset, DateOnly tradingDate)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        if (!dataset.TradingDates.Contains(tradingDate)) throw new ArgumentException("Replay date is not a dataset trading date.", nameof(tradingDate));
        var securities = new List<HistoricalSecuritySnapshot>();
        foreach ((DateOnly date, string symbol) key in dataset.Quotes.Keys.Where(key => key.Date == tradingDate).OrderBy(key => key.Symbol, StringComparer.Ordinal))
        {
            if (!dataset.Klines.TryGetValue(key.symbol, out KlineSeries? series)) continue;
            KlineBar[] visibleBars = series.Bars.Where(bar => DateOnly.FromDateTime(bar.Date) <= tradingDate).ToArray();
            securities.Add(new HistoricalSecuritySnapshot(dataset.Quotes[key], new KlineSeries(key.symbol, visibleBars)));
        }
        dataset.MarketContexts.TryGetValue(tradingDate, out HistoricalMarketContext? context);
        return new HistoricalMarketSnapshot(tradingDate, securities, context, HistoricalReplaySupport.Supported, Array.Empty<string>());
    }
}

public sealed class SparrowHistoricalReplayEngine
{
    private readonly HistoricalSnapshotBuilder _snapshotBuilder;
    private readonly SparrowRankingEngine _ranking;
    public SparrowHistoricalReplayEngine(HistoricalSnapshotBuilder? snapshotBuilder = null, SparrowRankingEngine? ranking = null)
    { _snapshotBuilder = snapshotBuilder ?? new HistoricalSnapshotBuilder(); _ranking = ranking ?? new SparrowRankingEngine(); }

    public SparrowReplayResult Replay(HistoricalMarketDataset dataset, SparrowReplayRequest request, CancellationToken cancellationToken = default)
    {
        ValidateVersion(request);
        HistoricalMarketSnapshot snapshot = _snapshotBuilder.Build(dataset, request.TradingDate);
        cancellationToken.ThrowIfCancellationRequested();
        var warnings = new List<string>();
        if (request.StrategyMode == SparrowStrategyMode.Compare) return Unsupported(dataset, request, "Comparison is orchestration, not a replay strategy.");
        if (request.StrategyMode == SparrowStrategyMode.Classic && request.ClassicParameters == null) return Unsupported(dataset, request, "Classic parameter snapshot is required.");
        if (request.StrategyMode == SparrowStrategyMode.V2 && request.V2Parameters == null) return Unsupported(dataset, request, "V2 parameter snapshot is required.");
        if (!HasRequiredCapabilities(dataset.Capabilities, request, snapshot.MarketContext, out string? gap)) return Unsupported(dataset, request, gap!);
        return request.StrategyMode == SparrowStrategyMode.Classic
            ? ReplayClassic(dataset, request, snapshot, warnings, cancellationToken)
            : ReplayV2(dataset, request, snapshot, warnings, cancellationToken);
    }

    private SparrowReplayResult ReplayClassic(HistoricalMarketDataset dataset, SparrowReplayRequest request, HistoricalMarketSnapshot snapshot, List<string> warnings, CancellationToken ct)
    {
        SparrowClassicScanParameters parameters = request.ClassicParameters!.ToParameters();
        if (parameters.MacroDef && snapshot.MarketContext!.ClassicMarketRegime!.Defensive)
        {
            warnings.Add("Classic market regime is defensive; no candidates were evaluated.");
            return Result(dataset, request, HistoricalReplaySupport.Supported, Array.Empty<SparrowReplaySelection>(), warnings);
        }
        var candidates = new List<(SparrowRankingFeatures Features, string ReasonCode, string Reason)>();
        foreach (HistoricalSecuritySnapshot security in snapshot.Securities)
        {
            ct.ThrowIfCancellationRequested();
            SparrowQuoteData quote = SparrowQuoteDataContract.FromSnapshot(security.Quote);
            SparrowRuleComparison p2 = SparrowComparisonRuleEvaluators.EvaluateClassicQuote(quote, parameters);
            if (!p2.Passed) continue;
            SparrowKlineSnapshot? kline = SparrowKlineSnapshotFactory.FromSeries(security.Klines);
            SparrowTechnicalEvaluation p3 = SparrowClassicComparisonEvaluator.Evaluate(kline, parameters);
            if (!p3.Rule.Passed) continue;
            candidates.Add((Features(security.Quote, quote, p3), p3.Rule.ReasonCode, p3.Rule.Reason));
        }
        IReadOnlyList<SparrowRankedCandidate> ranked = _ranking.RankClassic(candidates.Select(item => item.Features), parameters.MinRise, parameters.MaxRise);
        return Result(dataset, request, HistoricalReplaySupport.Supported, ToSelections(ranked, candidates, request.TopN), warnings);
    }

    private SparrowReplayResult ReplayV2(HistoricalMarketDataset dataset, SparrowReplayRequest request, HistoricalMarketSnapshot snapshot, List<string> warnings, CancellationToken ct)
    {
        SparrowScanParameters parameters = request.V2Parameters!.ToParameters();
        double shPercent = snapshot.MarketContext?.V2ShanghaiDailyPercent ?? 0;
        if (parameters.MacroDef && shPercent <= -2.5)
        {
            warnings.Add("V2 Shanghai daily change is defensive; no candidates were evaluated.");
            return Result(dataset, request, HistoricalReplaySupport.Supported, Array.Empty<SparrowReplaySelection>(), warnings);
        }
        var candidates = new List<(SparrowRankingFeatures Features, string ReasonCode, string Reason)>();
        foreach (HistoricalSecuritySnapshot security in snapshot.Securities)
        {
            ct.ThrowIfCancellationRequested();
            SparrowQuoteData quote = SparrowQuoteDataContract.FromSnapshot(security.Quote);
            SparrowKlineSnapshot? kline = SparrowKlineSnapshotFactory.FromSeries(security.Klines);
            SparrowV2CandidateEvaluation evaluation = SparrowV2CandidateEvaluator.Evaluate(security.Quote.Symbol, quote, kline, shPercent, parameters);
            if (!evaluation.Passed) continue;
            SparrowTechnicalEvaluation technical = SparrowV2RuleEvaluator.Evaluate(kline, shPercent, parameters);
            candidates.Add((Features(security.Quote, quote, technical, parameters.CheckAlpha ? technical.LatestPercent - shPercent : null), evaluation.P3.ReasonCode, evaluation.P3.Reason));
        }
        IReadOnlyList<SparrowRankedCandidate> ranked = _ranking.RankV2(candidates.Select(item => item.Features), parameters.MinRise, parameters.MaxRise, parameters.CheckAlpha);
        return Result(dataset, request, HistoricalReplaySupport.Supported, ToSelections(ranked, candidates, request.TopN), warnings);
    }

    private static SparrowRankingFeatures Features(QuoteSnapshot source, SparrowQuoteData quote, SparrowTechnicalEvaluation technical, double? alpha = null) => new()
    { Code = source.Symbol, Name = source.Name, RisePercent = quote.Percent ?? quote.PriceDerivedPercent, Amount = quote.Amount, OuterVolume = quote.OuterVolume, InnerVolume = quote.InnerVolume, BuyPressureRatio = SparrowRankingFeatures.CalculateBuyPressureRatio(quote.OuterVolume, quote.InnerVolume), Adhesion = technical.Adhesion, Turnover = quote.Turnover, Momentum = technical.Momentum, AlphaMargin = alpha };
    private static IReadOnlyList<SparrowReplaySelection> ToSelections(IReadOnlyList<SparrowRankedCandidate> ranked, IEnumerable<(SparrowRankingFeatures Features, string ReasonCode, string Reason)> evidence, int topN)
    { var map = evidence.ToDictionary(item => item.Features.Code, StringComparer.Ordinal); return ranked.Take(Math.Clamp(topN, 1, 20)).Select(candidate => new SparrowReplaySelection(candidate.Code, candidate.Name, candidate, map[candidate.Code].ReasonCode, map[candidate.Code].Reason)).ToArray(); }
    private static SparrowReplayResult Result(HistoricalMarketDataset d, SparrowReplayRequest r, HistoricalReplaySupport s, IReadOnlyList<SparrowReplaySelection> selections, IReadOnlyList<string> warnings) => new(r, SparrowHistoricalFingerprint.Parameters(r), d.DatasetId, d.Fingerprint, s, selections, warnings);
    private static SparrowReplayResult Unsupported(HistoricalMarketDataset d, SparrowReplayRequest r, string warning) => Result(d, r, HistoricalReplaySupport.Unsupported, Array.Empty<SparrowReplaySelection>(), new[] { warning });
    private static bool HasRequiredCapabilities(HistoricalDataCapabilities c, SparrowReplayRequest r, HistoricalMarketContext? context, out string? gap)
    {
        bool common = c.HasAmount && c.HasOuterVolume && c.HasInnerVolume;
        bool market = r.StrategyMode == SparrowStrategyMode.Classic ? (!r.ClassicParameters!.MacroDef || c.HasClassicMarketRegime && context?.ClassicMarketRegime != null) : (!r.V2Parameters!.MacroDef || c.HasV2ShanghaiDailyPercent && context?.V2ShanghaiDailyPercent != null);
        gap = common && market ? null : "HistoricalFieldUnavailable: required quote or market-regime fields are not present for this replay date."; return gap == null;
    }
    private static void ValidateVersion(SparrowReplayRequest request)
    { string expected = request.StrategyMode == SparrowStrategyMode.Classic ? SparrowStrategyVersions.Classic : request.StrategyMode == SparrowStrategyMode.V2 ? SparrowStrategyVersions.V2 : ""; if (!string.Equals(request.StrategyVersion, expected, StringComparison.Ordinal)) throw new ArgumentException($"Requested strategy version '{request.StrategyVersion}' does not match '{expected}'.", nameof(request)); }
}

public sealed class HistoricalOutcomeEvaluator
{
    public IReadOnlyList<ForwardReturn> Evaluate(HistoricalMarketDataset dataset, DateOnly replayDate, string symbol, IReadOnlyList<int> horizons, double cost = 0, double slippage = 0)
    {
        int start = -1; for (int index = 0; index < dataset.TradingDates.Count; index++) if (dataset.TradingDates[index] == replayDate) { start = index; break; }
        if (start < 0 || !dataset.Klines.TryGetValue(symbol, out KlineSeries? series)) return horizons.Select(h => new ForwardReturn(h, null, null, null, false)).ToArray();
        double? entry = Close(series, replayDate);
        return horizons.Distinct().Order().Select(h => { DateOnly? exitDate = start + h < dataset.TradingDates.Count ? dataset.TradingDates[start + h] : null; double? exit = exitDate.HasValue ? Close(series, exitDate.Value) : null; double? gross = entry is > 0 && exit.HasValue ? (exit.Value / entry.Value - 1) * 100 : null; double? net = gross.HasValue ? gross.Value - (cost + 2 * slippage) * 100 : null; return new ForwardReturn(h, entry, exit, net, net.HasValue); }).ToArray();
    }
    private static double? Close(KlineSeries series, DateOnly date) => series.Bars.SingleOrDefault(bar => DateOnly.FromDateTime(bar.Date) == date)?.Close;
}

public sealed class SparrowHistoricalBacktestEngine
{
    private readonly SparrowHistoricalReplayEngine _replay;
    private readonly HistoricalOutcomeEvaluator _outcomes;
    public SparrowHistoricalBacktestEngine(SparrowHistoricalReplayEngine? replay = null, HistoricalOutcomeEvaluator? outcomes = null) { _replay = replay ?? new(); _outcomes = outcomes ?? new(); }
    public SparrowBacktestResult Run(HistoricalMarketDataset dataset, SparrowBacktestRequest request, CancellationToken cancellationToken = default)
    {
        var selections = new List<SparrowBacktestSelection>(); var warnings = new List<string>();
        foreach (DateOnly date in dataset.TradingDates.Where(date => date >= request.StartDate && date <= request.EndDate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replayRequest = new SparrowReplayRequest(request.StrategyMode, request.StrategyVersion, date, request.TopN, request.ClassicParameters, request.V2Parameters);
            SparrowReplayResult replay = _replay.Replay(dataset, replayRequest, cancellationToken);
            warnings.AddRange(replay.Warnings.Select(warning => $"{date:O}: {warning}"));
            foreach (SparrowReplaySelection selection in replay.Selections)
                selections.Add(new SparrowBacktestSelection(date, selection, _outcomes.Evaluate(dataset, date, selection.Code, request.Horizons, request.RoundTripCostRate, request.SlippageRate)));
        }
        SparrowHorizonMetrics[] metrics = request.Horizons.Distinct().Order().Select(horizon => Metrics(horizon, selections)).ToArray();
        return new SparrowBacktestResult(request, SparrowHistoricalFingerprint.Parameters(request), dataset.DatasetId, dataset.Fingerprint, selections, metrics, warnings.Distinct().ToArray());
    }
    private static SparrowHorizonMetrics Metrics(int horizon, IReadOnlyList<SparrowBacktestSelection> selections)
    {
        double[] values = selections.SelectMany(selection => selection.Outcomes).Where(outcome => outcome.HorizonTradingDays == horizon && outcome.Available && outcome.ReturnPercent.HasValue).Select(outcome => outcome.ReturnPercent!.Value).Order().ToArray();
        double? median = values.Length == 0 ? null : values.Length % 2 == 1 ? values[values.Length / 2] : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
        return new SparrowHorizonMetrics(horizon, selections.Count, values.Length, values.Length == 0 ? null : values.Average(), median, values.Length == 0 ? null : values.Count(value => value > 0) / (double)values.Length);
    }
}
