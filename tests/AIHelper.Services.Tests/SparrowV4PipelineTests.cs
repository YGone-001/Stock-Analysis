using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Locks the v4 pipeline: volume-pattern gate semantics, unit insensitivity, replay parity and UI parameters.</summary>
public sealed class SparrowV4PipelineTests
{
    [Fact]
    public void V4RuleEvaluator_ImplementsV4()
    {
        Assert.Equal(SparrowStrategyVersions.V4, SparrowV4RuleEvaluator.RuleVersion);
    }

    [Fact]
    public void Evaluate_WithGatesDisabled_MatchesV3Verbatim()
    {
        SparrowKlineSnapshot snapshot = RallySnapshot(Volumes(passPattern: true));

        SparrowTechnicalEvaluation v3 = SparrowV3RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff);
        SparrowTechnicalEvaluation v4NullConfig = SparrowV4RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff, volumeGates: null);
        SparrowTechnicalEvaluation v4BothOff = SparrowV4RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff,
            new SparrowVolumeGateConfig(VolBreakoutMultiple: null, VolContractionMaxRatio: null));

        Assert.True(v3.Rule.Passed);
        Assert.Equal(v3.Rule.Passed, v4NullConfig.Rule.Passed);
        Assert.Equal(v3.Rule.ReasonCode, v4NullConfig.Rule.ReasonCode);
        Assert.Equal(v3.Rule.Passed, v4BothOff.Rule.Passed);
        Assert.Equal(v3.Rule.ReasonCode, v4BothOff.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_AcceptsBreakoutWithPriorContraction()
    {
        // Today 2000 >= 1.5 x mean(500 x5); contraction mean 500 <= 0.8 x baseline 1000.
        SparrowTechnicalEvaluation result = SparrowV4RuleEvaluator.Evaluate(RallySnapshot(Volumes(true)), 0, LooseParameters(), MomentumGatesOff, new SparrowVolumeGateConfig());

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_RejectsInsufficientBreakoutVolume()
    {
        SparrowTechnicalEvaluation result = SparrowV4RuleEvaluator.Evaluate(RallySnapshot(Volumes(false)), 0, LooseParameters(), MomentumGatesOff, new SparrowVolumeGateConfig());

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3VolBreakout, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_RejectsMissingContractionBeforeBreakout()
    {
        // Breakout day 2000 >= 1.5 x 900, but the pre-breakout window mean 900 exceeds 0.8 x baseline 1000.
        double[] volumes = Volumes(true);
        for (int index = 1; index <= 5; index++) volumes[index] = 900;

        SparrowTechnicalEvaluation result = SparrowV4RuleEvaluator.Evaluate(RallySnapshot(volumes), 0, LooseParameters(), MomentumGatesOff, new SparrowVolumeGateConfig());

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3VolContraction, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithoutVolumeData_IsExplicitlyUnavailable()
    {
        var noVolume = new SparrowKlineSnapshot(Closes(), Closes()[0], 0);
        var nanVolume = RallySnapshot(new double[120].Select(_ => double.NaN).ToArray());

        SparrowTechnicalEvaluation missing = SparrowV4RuleEvaluator.Evaluate(noVolume, 0, LooseParameters(), MomentumGatesOff, new SparrowVolumeGateConfig());
        SparrowTechnicalEvaluation nan = SparrowV4RuleEvaluator.Evaluate(nanVolume, 0, LooseParameters(), MomentumGatesOff, new SparrowVolumeGateConfig());

        Assert.Equal(SparrowComparisonReasonCodes.P3VolUnavailable, missing.Rule.ReasonCode);
        Assert.Equal(SparrowRuleOutcome.DataUnavailable, missing.Rule.Outcome);
        Assert.Equal(SparrowComparisonReasonCodes.P3VolUnavailable, nan.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_IsInsensitiveToTheVolumeUnit()
    {
        // Scaling every volume by 100 (hands -> shares) must not change the ratio-based outcome.
        double[] scaled = Volumes(true).Select(value => value * 100).ToArray();

        SparrowTechnicalEvaluation result = SparrowV4RuleEvaluator.Evaluate(RallySnapshot(scaled), 0, LooseParameters(), MomentumGatesOff, new SparrowVolumeGateConfig());

        Assert.True(result.Rule.Passed);
    }

    [Fact]
    public void ParameterFingerprint_CoversTheV4SnapshotAndItsGates()
    {
        SparrowBacktestRequest baseline = Backtest(V4Parameters(VolBreakoutMultiple: 1.5));
        SparrowBacktestRequest sameGates = Backtest(V4Parameters(VolBreakoutMultiple: 1.5));
        SparrowBacktestRequest tighterGates = Backtest(V4Parameters(VolBreakoutMultiple: 2.0));
        SparrowBacktestRequest v3 = new(SparrowStrategyMode.V3, SparrowStrategyVersions.V3, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), 2, new[] { 1 },
            ClassicParameters: null, V2Parameters: null,
            V3Parameters: new SparrowV3ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false, 25, 75, true, 14, 12, 26, 9));

        Assert.Equal(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(sameGates));
        Assert.NotEqual(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(tighterGates));
        Assert.NotEqual(SparrowHistoricalFingerprint.Parameters(baseline), SparrowHistoricalFingerprint.Parameters(v3));
    }

    [Fact]
    public void Replay_WithoutV4Snapshot_IsExplicitlyUnsupported_AndGatesOffMatchesV3()
    {
        HistoricalMarketDataset dataset = Dataset();
        DateOnly date = dataset.TradingDates[65];
        var engine = new SparrowHistoricalReplayEngine();

        SparrowReplayResult missing = engine.Replay(dataset, V4Request(date) with { V4Parameters = null });
        Assert.Equal(HistoricalReplaySupport.Unsupported, missing.Support);
        Assert.Contains(missing.Warnings, warning => warning.Contains("缺少 V4 策略的参数快照", StringComparison.Ordinal));

        SparrowReplayResult v3 = engine.Replay(dataset, new(SparrowStrategyMode.V3, SparrowStrategyVersions.V3, date, 2,
            V3Parameters: new SparrowV3ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false, null, null, false, 14, 12, 26, 9)));
        SparrowReplayResult v4 = engine.Replay(dataset, V4Request(date, VolBreakoutMultiple: null, VolContractionMaxRatio: null));
        Assert.Equal(v3.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)),
            v4.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)));
    }

    [Fact]
    public void V4UiParameters_InheritV3DefaultsAndCarryVolumeGateDefaults()
    {
        SparrowV4UiParameters parameters = SparrowParameterDefaults.CreateV4();
        SparrowSystemSettings system = SparrowParameterDefaults.CreateSystemSettings();

        Assert.Equal(SparrowParameterDefaults.RsiMin, parameters.RsiMin);
        Assert.True(parameters.RequireMacdBullish);
        Assert.True(parameters.VolumeGatesEnabled);
        Assert.Equal(SparrowParameterDefaults.VolBreakoutMultiple, parameters.VolBreakoutMultiple);
        Assert.Equal(SparrowParameterDefaults.VolContractionMaxRatio, parameters.VolContractionMaxRatio);
        Assert.True(parameters.IsRecommended);

        SparrowVolumeGateConfig? gates = SparrowParameterMapper.ToVolumeGates(parameters);
        Assert.NotNull(gates);
        gates!.Validate();
        Assert.Equal(1.5d, gates.VolBreakoutMultiple);
        Assert.Equal(.8d, gates.VolContractionMaxRatio);

        Assert.True(SparrowParameterValidator.Validate(SparrowStrategyMode.V4, parameters, parameters, system, parameters, parameters).IsValid);

        parameters.VolumeGatesEnabled = false;
        SparrowVolumeGateConfig? disabled = SparrowParameterMapper.ToVolumeGates(parameters);
        Assert.NotNull(disabled);
        Assert.Null(disabled!.VolBreakoutMultiple);
        Assert.Null(disabled.VolContractionMaxRatio);

        parameters.VolumeGatesEnabled = true;
        parameters.VolBreakoutMultiple = 0;
        Assert.False(SparrowParameterValidator.Validate(SparrowStrategyMode.V4, parameters, parameters, system, parameters, parameters).IsValid);
    }

    private static SparrowMomentumGateConfig MomentumGatesOff => new(RsiMin: null, RsiMax: null, RequireMacdBullish: false);

    private static double[] Closes() => Enumerable.Range(0, 120).Select(index => 8.0 + index * 0.02).Reverse().ToArray();

    /// <summary>Newest-first volume series: breakout day, contraction window, baseline window, then filler.</summary>
    private static double[] Volumes(bool passPattern)
    {
        double[] volumes = Enumerable.Repeat(1000d, 120).ToArray();
        double breakout = passPattern ? 2000 : 600;
        volumes[0] = breakout;
        for (int index = 1; index <= 5; index++) volumes[index] = 500;
        return volumes;
    }

    private static SparrowKlineSnapshot RallySnapshot(double[] volumes) => new(Closes(), Closes()[0], 0, volumes);

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

    private static SparrowReplayRequest V4Request(DateOnly date, double? VolBreakoutMultiple = 1.5, double? VolContractionMaxRatio = 0.8) => new(
        SparrowStrategyMode.V4, SparrowStrategyVersions.V4, date, 2, V4Parameters: V4Parameters(VolBreakoutMultiple, VolContractionMaxRatio));

    private static SparrowV4ParameterSnapshot V4Parameters(double? VolBreakoutMultiple = 1.5, double? VolContractionMaxRatio = 0.8) => new(
        false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
        null, null, false, 14, 12, 26, 9,
        VolBreakoutMultiple, VolContractionMaxRatio, 5, 5, 10);

    private static SparrowBacktestRequest Backtest(SparrowV4ParameterSnapshot snapshot) => new(
        SparrowStrategyMode.V4, SparrowStrategyVersions.V4, new DateOnly(2026, 1, 1), new DateOnly(2026, 4, 1), 2, new[] { 1 },
        ClassicParameters: null, V2Parameters: null, V3Parameters: null, V4Parameters: snapshot);

    private static HistoricalMarketDataset Dataset()
    {
        DateOnly start = new(2026, 1, 1); DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        KlineSeries[] klines = new[] { "600000", "600001" }.Select((code, stock) => new KlineSeries(code, dates.Select((date, i) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + i * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => new[] { "600000", "600001" }.Select((code, index) => new HistoricalQuoteObservation(date, new QuoteSnapshot(code, code, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        HistoricalMarketContext[] context = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, 0.5)).ToArray();
        return new HistoricalMarketDataset("v4-pipeline-fixture", dates, quotes, klines, context, source: "InMemory");
    }
}
