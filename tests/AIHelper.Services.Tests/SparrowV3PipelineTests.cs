using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Locks the v3 pipeline wiring: snapshot identity, replay parity with the frozen v2 rules, and UI parameters.</summary>
public sealed class SparrowV3PipelineTests
{
    [Fact]
    public void V3RuleEvaluator_ImplementsV3()
    {
        Assert.Equal(SparrowStrategyVersions.V3, SparrowV3RuleEvaluator.RuleVersion);
    }

    [Fact]
    public void Replay_WithGatesDisabled_MatchesTheV2ReplayExactly()
    {
        HistoricalMarketDataset dataset = Dataset();
        DateOnly date = dataset.TradingDates[65];
        var engine = new SparrowHistoricalReplayEngine();

        SparrowReplayResult v2 = engine.Replay(dataset, V2Request(date));
        SparrowReplayResult v3 = engine.Replay(dataset, V3Request(date, RsiMin: null, RsiMax: null, RequireMacdBullish: false));

        Assert.Equal(HistoricalReplaySupport.Supported, v3.Support);
        Assert.NotEmpty(v2.Selections);
        Assert.Equal(v2.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)),
            v3.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)));
    }

    [Fact]
    public void Replay_WithDefaultGates_FiltersTheMonotonicRally()
    {
        // The fixture is an uninterrupted linear rally, so Wilder RSI(14) is 100 - outside the default [25, 75] band.
        HistoricalMarketDataset dataset = Dataset();
        SparrowReplayResult result = new SparrowHistoricalReplayEngine().Replay(dataset, V3Request(dataset.TradingDates[65]));

        Assert.Equal(HistoricalReplaySupport.Supported, result.Support);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public void Replay_WithoutV3Snapshot_IsExplicitlyUnsupported()
    {
        HistoricalMarketDataset dataset = Dataset();
        SparrowReplayRequest missing = V3Request(dataset.TradingDates[65]) with { V3Parameters = null };

        SparrowReplayResult result = new SparrowHistoricalReplayEngine().Replay(dataset, missing);

        Assert.Equal(HistoricalReplaySupport.Unsupported, result.Support);
        Assert.Contains(result.Warnings, warning => warning.Contains("缺少 V3 策略的参数快照", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => new SparrowHistoricalReplayEngine().Replay(dataset, V3Request(dataset.TradingDates[65]) with { StrategyVersion = SparrowStrategyVersions.V2 }));
    }

    [Fact]
    public void ParameterFingerprint_CoversTheV3SnapshotAndItsGates()
    {
        SparrowBacktestRequest baseline = Backtest(V3Parameters(RsiMin: 25, RsiMax: 75, RequireMacdBullish: true));
        SparrowBacktestRequest sameGates = Backtest(V3Parameters(RsiMin: 25, RsiMax: 75, RequireMacdBullish: true));
        SparrowBacktestRequest tighterGates = Backtest(V3Parameters(RsiMin: 30, RsiMax: 70, RequireMacdBullish: true));
        SparrowBacktestRequest v2 = new(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), 2, new[] { 1 },
            ClassicParameters: null, V2Parameters: new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false));

        Assert.Equal(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(sameGates));
        Assert.NotEqual(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(tighterGates));
        Assert.NotEqual(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(v2));
    }

    private static SparrowBacktestRequest Backtest(SparrowV3ParameterSnapshot snapshot) => new(
        SparrowStrategyMode.V3, SparrowStrategyVersions.V3, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), 2, new[] { 1 },
        ClassicParameters: null, V2Parameters: null, V3Parameters: snapshot);

    [Fact]
    public void V3UiParameters_InheritV2DefaultsAndCarryGateDefaults()
    {
        SparrowV3UiParameters parameters = SparrowParameterDefaults.CreateV3();

        Assert.Equal(SparrowParameterDefaults.MinTurnover, parameters.MinTurnover);
        Assert.Equal(SparrowParameterDefaults.MaxTurnover, parameters.MaxTurnover);
        Assert.Equal(SparrowParameterDefaults.MomentumThreshold, parameters.MomentumThreshold);
        Assert.True(parameters.CheckAlpha);
        Assert.Equal(SparrowParameterDefaults.RsiMin, parameters.RsiMin);
        Assert.Equal(SparrowParameterDefaults.RsiMax, parameters.RsiMax);
        Assert.True(parameters.RequireMacdBullish);
        Assert.True(parameters.IsRecommended);

        SparrowMomentumGateConfig gates = SparrowParameterMapper.ToMomentumGates(parameters);
        gates.Validate();
        Assert.Equal(25d, gates.RsiMin);
        Assert.Equal(75d, gates.RsiMax);
        Assert.True(gates.RequireMacdBullish);
    }

    [Fact]
    public void ParameterUiState_RoutesActiveAndEnhancementParametersByMode()
    {
        var state = new SparrowParameterUiState();

        state.StrategyMode = SparrowStrategyMode.V3;
        Assert.Same(state.V3Parameters, state.ActiveParameters);
        Assert.Same(state.V3Parameters, state.EnhancementParameters);
        Assert.True(state.ShowV2Parameters);
        Assert.True(state.ShowV3Parameters);
        Assert.Equal("V2 增强条件（作用于 V3）", state.V2ParametersHeader);

        state.StrategyMode = SparrowStrategyMode.V2;
        Assert.Same(state.V2Parameters, state.ActiveParameters);
        Assert.Same(state.V2Parameters, state.EnhancementParameters);
        Assert.False(state.ShowV3Parameters);

        state.StrategyMode = SparrowStrategyMode.Classic;
        Assert.Same(state.ClassicParameters, state.ActiveParameters);
        Assert.Same(state.V2Parameters, state.EnhancementParameters);
    }

    [Fact]
    public void Validator_RejectsCrossedOrOutOfRangeRsiGates()
    {
        var state = new SparrowParameterUiState();
        state.StrategyMode = SparrowStrategyMode.V3;

        Assert.True(SparrowParameterValidator.Validate(SparrowStrategyMode.V3, state.ClassicParameters, state.V2Parameters, state.SystemSettings, state.V3Parameters).IsValid);

        state.V3Parameters.RsiMin = 80;
        state.V3Parameters.RsiMax = 20;
        Assert.False(SparrowParameterValidator.Validate(SparrowStrategyMode.V3, state.ClassicParameters, state.V2Parameters, state.SystemSettings, state.V3Parameters).IsValid);

        state.V3Parameters.RsiMin = null;
        state.V3Parameters.RsiMax = 20;
        Assert.True(SparrowParameterValidator.Validate(SparrowStrategyMode.V3, state.ClassicParameters, state.V2Parameters, state.SystemSettings, state.V3Parameters).IsValid);
    }

    private static SparrowReplayRequest V2Request(DateOnly date) => new(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, date, 2,
        V2Parameters: new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false));

    private static SparrowReplayRequest V3Request(DateOnly date, double? RsiMin = 25, double? RsiMax = 75, bool RequireMacdBullish = true) => new(
        SparrowStrategyMode.V3, SparrowStrategyVersions.V3, date, 2,
        V3Parameters: V3Parameters(RsiMin, RsiMax, RequireMacdBullish));

    private static SparrowV3ParameterSnapshot V3Parameters(double? RsiMin, double? RsiMax, bool RequireMacdBullish) => new(
        false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
        RsiMin, RsiMax, RequireMacdBullish, 14, 12, 26, 9);

    private static HistoricalMarketDataset Dataset()
    {
        DateOnly start = new(2026, 1, 1); DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        KlineSeries[] klines = new[] { "600000", "600001" }.Select((code, stock) => new KlineSeries(code, dates.Select((date, i) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + i * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => new[] { "600000", "600001" }.Select((code, index) => new HistoricalQuoteObservation(date, new QuoteSnapshot(code, code, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        HistoricalMarketContext[] context = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, 0.5)).ToArray();
        return new HistoricalMarketDataset("v3-pipeline-fixture", dates, quotes, klines, context, source: "InMemory");
    }
}
