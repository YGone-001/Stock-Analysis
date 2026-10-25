using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Locks the v5 pipeline: weekly-resample indicator, weekly-gate semantics, replay parity and UI parameters.</summary>
public sealed class SparrowV5PipelineTests
{
    [Fact]
    public void V5RuleEvaluator_ImplementsV5()
    {
        Assert.Equal(SparrowStrategyVersions.V5, SparrowV5RuleEvaluator.RuleVersion);
    }

    [Fact]
    public void TryWeeklyMaRising_ResamplesByCalendarWeekAndDetectsDirection()
    {
        // 26 trading weeks from a Monday; weekly closes rise 100, 101, ... so every weekly MA window shifts up.
        DateOnly start = new(2026, 1, 5);
        List<DateOnly> datesOldestFirst = new();
        List<double> closesOldestFirst = new();
        for (int day = 0; datesOldestFirst.Count < 26 * 5; day++)
        {
            DateOnly date = start.AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            datesOldestFirst.Add(date);
            closesOldestFirst.Add(100 + day / 7);
        }
        datesOldestFirst.Reverse();
        closesOldestFirst.Reverse();

        Assert.True(SparrowTechnicalIndicators.TryWeeklyMaRising(datesOldestFirst, closesOldestFirst, 20, out bool rising));
        Assert.True(rising);

        // A flat weekly series keeps the MA flat: not strictly rising.
        List<double> flat = closesOldestFirst.Select(_ => 100d).ToList();
        Assert.True(SparrowTechnicalIndicators.TryWeeklyMaRising(datesOldestFirst, flat, 20, out bool flatRising));
        Assert.False(flatRising);

        // Fewer than maPeriod + 1 weeks is explicitly unavailable.
        Assert.False(SparrowTechnicalIndicators.TryWeeklyMaRising(datesOldestFirst.Take(15 * 5).ToList(), closesOldestFirst.Take(15 * 5).ToList(), 20, out _));
    }

    [Fact]
    public void Evaluate_WithWeeklyGateDisabled_MatchesV4Verbatim()
    {
        SparrowKlineSnapshot snapshot = RallySnapshot();

        SparrowTechnicalEvaluation v4 = SparrowV4RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff);
        SparrowTechnicalEvaluation v5 = SparrowV5RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff,
            new SparrowWeeklyGateConfig(RequireWeeklyUptrend: false));

        Assert.True(v4.Rule.Passed);
        Assert.Equal(v4.Rule.Passed, v5.Rule.Passed);
        Assert.Equal(v4.Rule.ReasonCode, v5.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_WeeklyGateAcceptsRisingTrendAndRejectsSideways()
    {
        SparrowKlineSnapshot rising = RallySnapshot();
        SparrowTechnicalEvaluation accepted = SparrowV5RuleEvaluator.Evaluate(rising, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff,
            new SparrowWeeklyGateConfig());
        Assert.True(accepted.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, accepted.Rule.ReasonCode);

        // Flat weekly closes keep the weekly MA flat, which the strictly-rising gate rejects.
        SparrowTechnicalEvaluation rejected = SparrowV5RuleEvaluator.Evaluate(FlatWeeklySnapshot(), 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff,
            new SparrowWeeklyGateConfig());
        Assert.False(rejected.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3WeeklyTrend, rejected.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithoutDatesOrWeeklyDepth_IsExplicitlyUnavailable()
    {
        var noDates = new SparrowKlineSnapshot(RallyCloses(), RallyCloses()[0], 0, Volumes());
        SparrowTechnicalEvaluation missingDates = SparrowV5RuleEvaluator.Evaluate(noDates, 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff,
            new SparrowWeeklyGateConfig());
        Assert.Equal(SparrowComparisonReasonCodes.P3WeeklyUnavailable, missingDates.Rule.ReasonCode);

        SparrowTechnicalEvaluation tooShort = SparrowV5RuleEvaluator.Evaluate(ShortHistorySnapshot(), 0, LooseParameters(), MomentumGatesOff, VolumeGatesOff,
            new SparrowWeeklyGateConfig());
        Assert.Equal(SparrowComparisonReasonCodes.P3WeeklyUnavailable, tooShort.Rule.ReasonCode);
    }

    [Fact]
    public void Replay_WithoutV5Snapshot_IsExplicitlyUnsupported_AndGateOffMatchesV4()
    {
        HistoricalMarketDataset dataset = Dataset();
        DateOnly date = dataset.TradingDates[65];
        var engine = new SparrowHistoricalReplayEngine();

        SparrowReplayResult missing = engine.Replay(dataset, V5Request(date) with { V5Parameters = null });
        Assert.Equal(HistoricalReplaySupport.Unsupported, missing.Support);
        Assert.Contains(missing.Warnings, warning => warning.Contains("缺少 V5 策略的参数快照", StringComparison.Ordinal));

        SparrowReplayResult v4 = engine.Replay(dataset, new(SparrowStrategyMode.V4, SparrowStrategyVersions.V4, date, 2,
            V4Parameters: V4Snapshot()));
        SparrowReplayResult v5 = engine.Replay(dataset, V5Request(date, RequireWeeklyUptrend: false));
        Assert.Equal(v4.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)),
            v5.Selections.Select(item => (item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore)));
    }

    [Fact]
    public void V5UiParameters_InheritV4DefaultsAndCarryTheWeeklyGateDefault()
    {
        SparrowV5UiParameters parameters = SparrowParameterDefaults.CreateV5();

        Assert.True(parameters.VolumeGatesEnabled);
        Assert.True(parameters.RequireWeeklyUptrend);
        Assert.True(parameters.IsRecommended);

        SparrowWeeklyGateConfig gates = SparrowParameterMapper.ToWeeklyGates(parameters);
        gates.Validate();
        Assert.Equal(20, gates.WeeklyMaPeriod);
        Assert.True(gates.RequireWeeklyUptrend);
    }

    private static SparrowMomentumGateConfig MomentumGatesOff => new(RsiMin: null, RsiMax: null, RequireMacdBullish: false);
    private static SparrowVolumeGateConfig? VolumeGatesOff => new(VolBreakoutMultiple: null, VolContractionMaxRatio: null);

    private static double[] RallyCloses() => Enumerable.Range(0, 130).Select(index => 8.0 + index * 0.02).Reverse().ToArray();

    private static double[] Volumes() => Enumerable.Repeat(1000d, 130).ToArray();

    private static List<DateOnly> RallyDates()
    {
        DateOnly start = new(2026, 6, 1);
        List<DateOnly> dates = new();
        for (int day = 0; dates.Count < 130; day++)
        {
            DateOnly date = start.AddDays(day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            dates.Add(date);
        }
        return dates;
    }

    /// <summary>Linear daily uptrend over 26 calendar weeks: weekly closes strictly increase, so the weekly MA20 rises.</summary>
    private static SparrowKlineSnapshot RallySnapshot()
    {
        List<DateOnly> dates = RallyDates();
        return new SparrowKlineSnapshot(RallyCloses(), RallyCloses()[0], 0, Volumes(), dates.AsReadOnly());
    }

    /// <summary>
    /// Alternating weekly patterns share the same Friday close (12) so every weekly close is identical and the
    /// weekly MA20 stays flat, while the daily MA5 momentum stays strictly positive at the newest (B-pattern Friday) bar.
    /// A perfectly periodic pattern would zero the momentum, hence the alternation.
    /// </summary>
    private static SparrowKlineSnapshot FlatWeeklySnapshot()
    {
        List<DateOnly> dates = RallyDates();
        double[] weekA = { 10, 10.5, 11, 11.5, 12 };
        double[] weekB = { 9.5, 11, 11.5, 11.8, 12 };
        double[] closes = new double[130];
        for (int week = 0; week < 26; week++)
        {
            double[] pattern = week % 2 == 0 ? weekA : weekB;
            for (int day = 0; day < 5; day++) closes[week * 5 + day] = pattern[day];
        }
        Array.Reverse(closes);
        return new SparrowKlineSnapshot(closes, closes[0], 0, Volumes(), dates.AsReadOnly());
    }

    /// <summary>Enough closes for the V2 base rules but only ~15 calendar weeks, below the 21-week weekly-MA depth.</summary>
    private static SparrowKlineSnapshot ShortHistorySnapshot()
    {
        List<DateOnly> dates = RallyDates();
        double[] closes = Enumerable.Range(0, 75).Select(index => 8.0 + index * 0.02).Reverse().ToArray();
        List<DateOnly> shortDates = dates.Take(75).ToList();
        return new SparrowKlineSnapshot(closes, closes[0], 0, Enumerable.Repeat(1000d, 75).ToArray(), shortDates.AsReadOnly());
    }

    private static SparrowV4ParameterSnapshot V4Snapshot() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
        null, null, false, 14, 12, 26, 9, null, null, 5, 5, 10);

    private static SparrowReplayRequest V5Request(DateOnly date, bool RequireWeeklyUptrend = true) => new(
        SparrowStrategyMode.V5, SparrowStrategyVersions.V5, date, 2,
        V5Parameters: new SparrowV5ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
            null, null, false, 14, 12, 26, 9, null, null, 5, 5, 10, RequireWeeklyUptrend, 20));

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
        return new HistoricalMarketDataset("v5-pipeline-fixture", dates, quotes, klines, context, source: "InMemory");
    }
}
