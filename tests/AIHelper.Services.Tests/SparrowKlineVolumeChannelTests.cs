using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>
/// Locks the volume data channel: bars keep carrying volume end-to-end into the evaluator snapshot,
/// unavailable volume stays NaN instead of being fabricated, and the frozen V2/V3 evaluators are unaffected.
/// </summary>
public sealed class SparrowKlineVolumeChannelTests
{
    [Fact]
    public void FromSeries_AlignsVolumesWithCloses_AndMarksUnavailableVolumeAsNaN()
    {
        KlineSeries series = new("600000", new[]
        {
            Bar(day: 0, close: 10, volume: 100),
            Bar(day: 1, close: 11, volume: 0),          // zero volume is a real value (suspension)
            Bar(day: 2, close: 12, volume: null),         // missing volume becomes NaN
            Bar(day: 3, close: 13, volume: -5),           // negative volume is invalid
            Bar(day: 4, close: 0, volume: 999),           // non-positive close drops the bar entirely
            Bar(day: 5, close: 14, volume: 200)
        });

        SparrowKlineSnapshot snapshot = SparrowKlineSnapshotFactory.FromSeries(series)!;

        Assert.Equal(new[] { 14d, 13d, 12d, 11d, 10d }, snapshot.ClosesNewestFirst);
        Assert.Equal(5, snapshot.Volumes.Count);
        Assert.Equal(200d, snapshot.Volumes[0], 10);
        Assert.True(double.IsNaN(snapshot.Volumes[1]));
        Assert.True(double.IsNaN(snapshot.Volumes[2]));
        Assert.Equal(0d, snapshot.Volumes[3], 10);
        Assert.Equal(100d, snapshot.Volumes[4], 10);
        Assert.True(snapshot.HasVolumeData);
    }

    [Fact]
    public void FromSeries_WithoutAnyVolume_ReportsNoUsableVolumeData()
    {
        KlineSeries series = new("600000", new[] { Bar(0, 10, null), Bar(1, 11, null), Bar(2, 12, null) });

        SparrowKlineSnapshot snapshot = SparrowKlineSnapshotFactory.FromSeries(series)!;

        // The aligned list stays length-matched to closes; every entry is NaN and HasVolumeData stays false.
        Assert.Equal(3, snapshot.ClosesNewestFirst.Count);
        Assert.Equal(3, snapshot.Volumes.Count);
        Assert.All(snapshot.Volumes, value => Assert.True(double.IsNaN(value)));
        Assert.False(snapshot.HasVolumeData);

        // A snapshot built without any volume argument also reports no volume data.
        Assert.False(new SparrowKlineSnapshot(new[] { 10d, 11d }, 11, 0).HasVolumeData);
        Assert.Empty(new SparrowKlineSnapshot(new[] { 10d, 11d }, 11, 0).Volumes);
    }

    [Fact]
    public void FromCachedJson_ParsesVolumeAndTreatsLegacyEntriesWithoutVolumeAsNaN()
    {
        string withVolume = """
            {"data":[{"Date":"2026-01-02","Close":10,"Volume":100},{"Date":"2026-01-05","Close":11,"Volume":150}]}
            """;
        string legacy = """
            {"data":[{"Date":"2026-01-02","Close":10},{"Date":"2026-01-05","Close":11}]}
            """;

        SparrowKlineSnapshot parsed = SparrowKlineSnapshotFactory.FromCachedJson(withVolume)!;
        Assert.Equal(new[] { 150d, 100d }, parsed.Volumes);

        SparrowKlineSnapshot legacyParsed = SparrowKlineSnapshotFactory.FromCachedJson(legacy)!;
        Assert.Equal(2, legacyParsed.ClosesNewestFirst.Count);
        Assert.All(legacyParsed.Volumes, value => Assert.True(double.IsNaN(value)));
        Assert.False(legacyParsed.HasVolumeData);
    }

    [Fact]
    public void VolumeBearingSnapshots_DoNotChangeTheFrozenV2AndV3Outcomes()
    {
        double[] closes = Enumerable.Range(0, 120).Select(index => 8.0 + index * 0.02).Reverse().ToArray();
        double[] volumes = closes.Select((_, index) => 1000d + index).ToArray();
        var withoutVolume = new SparrowKlineSnapshot(closes, closes[0], 0.25);
        var withVolume = new SparrowKlineSnapshot(closes, closes[0], 0.25, volumes);
        SparrowScanParameters parameters = LooseParameters();

        SparrowTechnicalEvaluation v2Plain = SparrowV2RuleEvaluator.Evaluate(withoutVolume, 0, parameters);
        SparrowTechnicalEvaluation v2Volume = SparrowV2RuleEvaluator.Evaluate(withVolume, 0, parameters);
        SparrowTechnicalEvaluation v3Plain = SparrowV3RuleEvaluator.Evaluate(withoutVolume, 0, parameters, new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: false));
        SparrowTechnicalEvaluation v3Volume = SparrowV3RuleEvaluator.Evaluate(withVolume, 0, parameters, new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: false));

        Assert.Equal(v2Plain.Rule.Passed, v2Volume.Rule.Passed);
        Assert.Equal(v2Plain.Rule.ReasonCode, v2Volume.Rule.ReasonCode);
        Assert.Equal(v3Plain.Rule.Passed, v3Volume.Rule.Passed);
        Assert.Equal(v3Plain.Rule.ReasonCode, v3Volume.Rule.ReasonCode);
        Assert.True(v2Volume.Rule.Passed);
    }

    [Fact]
    public void VolumeFieldCapability_IsDeclaredInHandsFromQuoteCoverage()
    {
        DateOnly[] dates = { new(2026, 1, 2), new(2026, 1, 5) };
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => new[]
        {
            new HistoricalQuoteObservation(date, Quote("600000", volume: 100)),
            new HistoricalQuoteObservation(date, Quote("600001", volume: null))
        }).ToArray();
        HistoricalMarketDataset dataset = new("volume-capability-fixture", dates, quotes,
            new[] { new KlineSeries("600000", Array.Empty<KlineBar>()) }, source: "InMemory");

        HistoricalFieldCapability? capability = dataset.GetFieldCapability(HistoricalField.Volume);

        Assert.NotNull(capability);
        Assert.Equal(HistoricalValueUnit.Hands, capability!.Unit);
        Assert.Equal(HistoricalFieldCoverage.Partial, capability.Coverage);
        Assert.NotEqual(HistoricalFieldOrigin.Unavailable, capability.Origin);
    }

    private static QuoteSnapshot Quote(string symbol, double? volume) => new(symbol, symbol, 10, 9.9, 1, volume, 1_000, 5, null, null, 10, 10.1, 9.8);

    private static KlineBar Bar(int day, double close, double? volume) => new(
        new DateTime(2026, 1, 2).AddDays(day), null, null, null, close, volume, null, null, null, null);

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
}
