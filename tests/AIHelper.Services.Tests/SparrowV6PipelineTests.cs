using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Locks the v6 pipeline: intraday gate semantics, gate-off parity with v5, and the historical replay boundary.</summary>
public sealed class SparrowV6PipelineTests
{
    [Fact]
    public void V6RuleEvaluator_ImplementsV6()
    {
        Assert.Equal(SparrowStrategyVersions.V6, SparrowV6RuleEvaluator.RuleVersion);
    }

    [Fact]
    public void IntradayGateOnly_AcceptsCloseAboveMa_RejectsBelowAndReportsUnavailable()
    {
        // Newest close 11 above the 10-bar MA of 10: pass.
        double[] above = Enumerable.Range(0, 20).Select(index => 9.0 + index * 0.1).Reverse().ToArray();
        SparrowRuleComparison accepted = SparrowV6RuleEvaluator.EvaluateIntradayGateOnly(
            new SparrowKlineSnapshot(above, above[0], 0), new SparrowIntradayGateConfig());
        Assert.True(accepted.Passed);

        // Newest close 9 at the MA of 9: reject (at-or-below).
        double[] flat = Enumerable.Repeat(9.0, 20).ToArray();
        SparrowRuleComparison rejected = SparrowV6RuleEvaluator.EvaluateIntradayGateOnly(
            new SparrowKlineSnapshot(flat, 9, 0), new SparrowIntradayGateConfig());
        Assert.False(rejected.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3IntradayPullback, rejected.ReasonCode);

        SparrowRuleComparison missing = SparrowV6RuleEvaluator.EvaluateIntradayGateOnly(null, new SparrowIntradayGateConfig());
        Assert.False(missing.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3IntradayUnavailable, missing.ReasonCode);

        SparrowRuleComparison shallow = SparrowV6RuleEvaluator.EvaluateIntradayGateOnly(
            new SparrowKlineSnapshot(new[] { 10d, 9d }, 10, 0), new SparrowIntradayGateConfig());
        Assert.Equal(SparrowComparisonReasonCodes.P3IntradayUnavailable, shallow.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithGateDisabled_MatchesV5Verbatim()
    {
        SparrowKlineSnapshot snapshot = RallySnapshot();

        SparrowTechnicalEvaluation v5 = SparrowV5RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff, WeeklyGatesOff);
        SparrowTechnicalEvaluation v6Off = SparrowV6RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff, WeeklyGatesOff,
            new SparrowIntradayGateConfig(Enabled: false), null);
        SparrowTechnicalEvaluation v6NullConfig = SparrowV6RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff, WeeklyGatesOff,
            intradayGates: null, intradaySnapshot: null);

        Assert.True(v5.Rule.Passed);
        Assert.Equal(v5.Rule.Passed, v6Off.Rule.Passed);
        Assert.Equal(v5.Rule.ReasonCode, v6Off.Rule.ReasonCode);
        Assert.Equal(v5.Rule.Passed, v6NullConfig.Rule.Passed);
        Assert.Equal(v5.Rule.ReasonCode, v6NullConfig.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithGateEnabledAndNoIntradayData_IsExplicitlyUnavailable()
    {
        SparrowTechnicalEvaluation result = SparrowV6RuleEvaluator.Evaluate(RallySnapshot(), 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff, WeeklyGatesOff,
            new SparrowIntradayGateConfig(Enabled: true), intradaySnapshot: null);

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3IntradayUnavailable, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithAcceptingIntradayData_PassesWithTheV5Evidence()
    {
        // Intraday closes 9.1..11 newest-first: newest 11 above the MA of the next 10 bars.
        double[] intraday = Enumerable.Range(0, 20).Select(index => 9.1 + index * 0.1).Reverse().ToArray();

        SparrowTechnicalEvaluation result = SparrowV6RuleEvaluator.Evaluate(RallySnapshot(), 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff, WeeklyGatesOff,
            new SparrowIntradayGateConfig(Enabled: true), new SparrowKlineSnapshot(intraday, intraday[0], 0));

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);
    }

    [Fact]
    public void Replay_WithIntradayGateEnabled_IsExplicitlyUnsupported_AndGateOffMatchesV5()
    {
        HistoricalMarketDataset dataset = Dataset();
        DateOnly date = dataset.TradingDates[65];
        var engine = new SparrowHistoricalReplayEngine();

        SparrowReplayResult enabled = engine.Replay(dataset, V6Request(date, IntradayGateEnabled: true));
        Assert.Equal(HistoricalReplaySupport.Unsupported, enabled.Support);
        Assert.Contains(enabled.Warnings, warning => warning.Contains("分钟级回踩门限", StringComparison.Ordinal));

        SparrowReplayResult missing = engine.Replay(dataset, V6Request(date) with { V6Parameters = null });
        Assert.Equal(HistoricalReplaySupport.Unsupported, missing.Support);
        Assert.Contains(missing.Warnings, warning => warning.Contains("缺少 V6 策略的参数快照", StringComparison.Ordinal));

        SparrowReplayResult v5 = engine.Replay(dataset, new(SparrowStrategyMode.V5, SparrowStrategyVersions.V5, date, 2, V5Parameters: V5Snapshot()));
        SparrowReplayResult v6 = engine.Replay(dataset, V6Request(date, IntradayGateEnabled: false));
        Assert.Equal(v5.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)),
            v6.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)));
    }

    [Fact]
    public void ParameterFingerprint_CoversTheV6SnapshotAndItsGates()
    {
        SparrowBacktestRequest baseline = Backtest(V6Snapshot(IntradayMaPeriod: 10));
        SparrowBacktestRequest sameGates = Backtest(V6Snapshot(IntradayMaPeriod: 10));
        SparrowBacktestRequest differentGates = Backtest(V6Snapshot(IntradayMaPeriod: 20));
        SparrowBacktestRequest enabled = Backtest(V6Snapshot(IntradayGateEnabled: true));

        Assert.Equal(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(sameGates));
        Assert.NotEqual(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(differentGates));
        Assert.NotEqual(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(enabled));
    }

    [Fact]
    public void V6UiParameters_InheritV5DefaultsAndCarryIntradayDefaults()
    {
        SparrowV6UiParameters parameters = SparrowParameterDefaults.CreateV6();

        Assert.True(parameters.VolumeGatesEnabled);
        Assert.True(parameters.RequireWeeklyUptrend);
        Assert.True(parameters.IntradayGateEnabled);
        Assert.Equal(SparrowParameterDefaults.IntradayPeriodMinutes, parameters.IntradayPeriodMinutes);
        Assert.Equal(SparrowParameterDefaults.IntradayMaPeriod, parameters.IntradayMaPeriod);
        Assert.True(parameters.IsRecommended);

        SparrowIntradayGateConfig gates = SparrowParameterMapper.ToIntradayGates(parameters);
        gates.Validate();
        Assert.Equal(60, gates.PeriodMinutes);
        Assert.Equal(10, gates.MaPeriod);
        Assert.Equal(SparrowConstants.V6IntradayBarsLimit, gates.BarsLimit);
    }

    private static SparrowMomentumGateConfig MomentumGatesOff => new(RsiMin: null, RsiMax: null, RequireMacdBullish: false);
    private static SparrowVolumeGateConfig? VolumeGatesOff => new(VolBreakoutMultiple: null, VolContractionMaxRatio: null);
    private static SparrowWeeklyGateConfig WeeklyGatesOff => new(RequireWeeklyUptrend: false);

    private static double[] RallyCloses() => Enumerable.Range(0, 130).Select(index => 8.0 + index * 0.02).Reverse().ToArray();
    private static double[] Volumes() => Enumerable.Repeat(1000d, 130).ToArray();

    private static SparrowKlineSnapshot RallySnapshot()
    {
        List<DateOnly> dates = new();
        DateOnly start = new(2026, 6, 1);
        for (int day = 0; dates.Count < 130; day++)
        {
            DateOnly date = start.AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(date);
        }
        return new SparrowKlineSnapshot(RallyCloses(), RallyCloses()[0], 0, Volumes(), dates.AsReadOnly());
    }

    private static SparrowV5ParameterSnapshot V5Snapshot() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
        null, null, false, 14, 12, 26, 9, null, null, 5, 5, 10, false, 20);

    private static SparrowV6ParameterSnapshot V6Snapshot(bool IntradayGateEnabled = false, int IntradayMaPeriod = 10) => new(
        false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
        null, null, false, 14, 12, 26, 9, null, null, 5, 5, 10, false, 20,
        IntradayGateEnabled, 60, IntradayMaPeriod);

    private static SparrowReplayRequest V6Request(DateOnly date, bool IntradayGateEnabled = false) => new(
        SparrowStrategyMode.V6, SparrowStrategyVersions.V6, date, 2, V6Parameters: V6Snapshot(IntradayGateEnabled));

    private static SparrowBacktestRequest Backtest(SparrowV6ParameterSnapshot snapshot) => new(
        SparrowStrategyMode.V6, SparrowStrategyVersions.V6, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), 2, new[] { 1 },
        ClassicParameters: null, V2Parameters: null, V3Parameters: null, V4Parameters: null, V5Parameters: null, V6Parameters: snapshot);

    private static SparrowScanParameters LooseParameters() => new()
    {
        MacroDef = false,
        MinRise = 1,
        MaxRise = 5,
        VolRatio = 1.1,
        MinAmount = 1,
        CheckMA60 = false,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MinTurnover = 3,
        MaxTurnover = 30,
        MomentumThreshold = 0,
        CheckAlpha = false,
        MaxConcurrency = 2,
        UseCache = true
    };

    private static HistoricalMarketDataset Dataset()
    {
        DateOnly start = new(2026, 1, 1); DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        KlineSeries[] klines = new[] { "600000", "600001" }.Select((code, stock) => new KlineSeries(code, dates.Select((date, i) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + i * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => new[] { "600000", "600001" }.Select((code, index) => new HistoricalQuoteObservation(date, new QuoteSnapshot(code, code, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        HistoricalMarketContext[] context = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, 0.5)).ToArray();
        return new HistoricalMarketDataset("v6-pipeline-fixture", dates, quotes, klines, context, source: "InMemory");
    }
}
