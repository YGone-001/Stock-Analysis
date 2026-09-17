using System.Text.Json;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowBenchmarkAnalysisTests
{
    [Fact]
    public void BenchmarkSeries_ValidatesAndCanonicalizesObservations()
    {
        DateOnly d0 = new(2026, 1, 2); DateOnly d1 = d0.AddDays(3);
        HistoricalBenchmarkSeries series = Series(new[] { new HistoricalBenchmarkObservation(d1, 102), new HistoricalBenchmarkObservation(d0, 100) });
        Assert.Equal(new[] { d0, d1 }, series.Observations.Select(item => item.TradingDate));
        Assert.Throws<ArgumentException>(() => Series(new[] { new HistoricalBenchmarkObservation(d0, 100), new HistoricalBenchmarkObservation(d0, 101) }));
        Assert.Throws<ArgumentException>(() => Series(new[] { new HistoricalBenchmarkObservation(d0, 0) }));
    }

    [Fact]
    public async Task BenchmarkJson_RoundTripsAndOldEmptyDatasetFingerprintIsStable()
    {
        HistoricalMarketDataset baseline = Dataset();
        HistoricalMarketDataset explicitEmpty = Dataset(benchmarks: Array.Empty<HistoricalBenchmarkSeries>());
        HistoricalMarketDataset withBenchmark = Dataset(benchmarks: new[] { Series(Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index))) });
        Assert.Equal(baseline.Fingerprint, explicitEmpty.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, withBenchmark.Fingerprint);

        string path = Path.Combine(Path.GetTempPath(), $"benchmark-{Guid.NewGuid():N}.json");
        try
        {
            await new HistoricalDatasetJsonWriter().WriteAsync(withBenchmark, path);
            HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(path);
            Assert.True(loaded.Success, string.Join("; ", loaded.Errors));
            Assert.Equal(withBenchmark.Fingerprint, loaded.Dataset!.Fingerprint);
            Assert.Equal(100, loaded.Dataset.Benchmarks["000300.SH"].Observations[0].Close);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BenchmarkFingerprint_TracksSemanticSeriesButNotAcquisitionTimestamp()
    {
        HistoricalBenchmarkObservation[] observations = Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index)).ToArray();
        HistoricalMarketDataset ordered = Dataset(benchmarks: new[] { Series(observations) });
        HistoricalMarketDataset reordered = Dataset(benchmarks: new[] { Series(observations.Reverse()) });
        HistoricalMarketDataset changedClose = Dataset(benchmarks: new[] { Series(observations.Select((item, index) => index == 10 ? item with { Close = 999 } : item)) });
        HistoricalMarketDataset changedSource = Dataset(benchmarks: new[] { Series(observations, source: "other-source") });
        HistoricalMarketDataset changedTimestamp = Dataset(benchmarks: new[] { Series(observations, acquiredAt: DateTimeOffset.UtcNow) });

        Assert.Equal(ordered.Fingerprint, reordered.Fingerprint);
        Assert.Equal(ordered.Fingerprint, changedTimestamp.Fingerprint);
        Assert.NotEqual(ordered.Fingerprint, changedClose.Fingerprint);
        Assert.NotEqual(ordered.Fingerprint, changedSource.Fingerprint);
    }

    [Fact]
    public void BenchmarkOutcome_UsesExactDatasetMarketDateAndNeverSkipsForward()
    {
        DateOnly friday = new(2026, 1, 2); DateOnly tuesday = new(2026, 1, 6); DateOnly wednesday = new(2026, 1, 7);
        HistoricalMarketDataset full = Dataset(new[] { friday, tuesday, wednesday }, new[] { Series(new[] { new HistoricalBenchmarkObservation(friday, 100), new HistoricalBenchmarkObservation(tuesday, 102), new HistoricalBenchmarkObservation(wednesday, 103) }) });
        BenchmarkForwardReturn return1 = new HistoricalBenchmarkOutcomeEvaluator().Evaluate(full, "000300.SH", friday, new[] { 1 }).Single();
        Assert.True(return1.Available); Assert.Equal(tuesday, return1.ExitDate); Assert.Equal(2, return1.ReturnPercent!.Value, 10);

        HistoricalMarketDataset gap = Dataset(new[] { friday, tuesday, wednesday }, new[] { Series(new[] { new HistoricalBenchmarkObservation(friday, 100), new HistoricalBenchmarkObservation(wednesday, 103) }, HistoricalFieldCoverage.Partial) });
        BenchmarkForwardReturn missing = new HistoricalBenchmarkOutcomeEvaluator().Evaluate(gap, "000300.SH", friday, new[] { 1 }).Single();
        Assert.False(missing.Available); Assert.Equal(SparrowBenchmarkReasonCodes.BenchmarkExitMissing, missing.ReasonCode); Assert.Null(missing.ReturnPercent);
    }

    [Fact]
    public void BenchmarkAnalysis_RejectsCostOrSlippageAndMissingBenchmarkIsUnsupported()
    {
        HistoricalMarketDataset dataset = Dataset();
        SparrowBacktestRequest cost = Request(dataset) with { RoundTripCostRate = .001 };
        Assert.Throws<ArgumentException>(() => new SparrowBenchmarkAnalysisRequest(cost, "000300.SH"));
        SparrowBenchmarkAnalysisResult absent = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset, new SparrowBenchmarkAnalysisRequest(Request(dataset), "000300.SH"));
        Assert.Equal(HistoricalReplaySupport.Unsupported, absent.Support);
        Assert.Equal(new[] { SparrowBenchmarkReasonCodes.BenchmarkSeriesNotFound }, absent.SupportReasonCodes);
        Assert.Empty(absent.UnavailableReasonCounts);
    }

    [Fact]
    public void PartialBenchmarkCoverage_IsNeverReportedAsComplete()
    {
        HistoricalMarketDataset dataset = Dataset(benchmarks: new[]
        {
            Series(Dates().Skip(1).Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index)), HistoricalFieldCoverage.Partial)
        });

        SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset, new SparrowBenchmarkAnalysisRequest(Request(dataset), "000300.SH"));

        Assert.Equal(HistoricalReplaySupport.Partial, result.Support);
        Assert.Contains(SparrowBenchmarkReasonCodes.BenchmarkCoveragePartial, result.SupportReasonCodes);
        Assert.Empty(result.UnavailableReasonCounts);
    }

    [Fact]
    public void UnsupportedStrategy_PreservesIndividualSupportReasonsWithoutOutcomeAttrition()
    {
        HistoricalMarketDataset dataset = Dataset(
            benchmarks: new[] { Series(Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index))) },
            strategyCapabilities: new[] { new HistoricalStrategyCapabilityExplanation(SparrowStrategyMode.V2, HistoricalReplaySupport.Unsupported, ["SOME_REQUIRED_FIELD_UNAVAILABLE", "UNIVERSE_PARTIAL"]) });

        SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset, new SparrowBenchmarkAnalysisRequest(Request(dataset), "000300.SH"));

        Assert.Equal(HistoricalReplaySupport.Unsupported, result.Support);
        Assert.Equal(new[] { "SOME_REQUIRED_FIELD_UNAVAILABLE", "UNIVERSE_PARTIAL" }, result.SupportReasonCodes);
        Assert.Empty(result.RelativeSelections);
        Assert.Empty(result.HorizonMetrics);
        Assert.Empty(result.UnavailableReasonCounts);
    }

    [Fact]
    public void BenchmarkCoverageNone_IsUnsupportedWithoutSyntheticAttrition()
    {
        HistoricalMarketDataset dataset = Dataset(benchmarks: new[]
        {
            Series(Array.Empty<HistoricalBenchmarkObservation>(), HistoricalFieldCoverage.None)
        });

        SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset, new SparrowBenchmarkAnalysisRequest(Request(dataset), "000300.SH"));

        Assert.Equal(HistoricalReplaySupport.Unsupported, result.Support);
        Assert.Equal(new[] { SparrowBenchmarkReasonCodes.BenchmarkCoverageNone }, result.SupportReasonCodes);
        Assert.Empty(result.RelativeSelections);
        Assert.Empty(result.HorizonMetrics);
        Assert.Empty(result.UnavailableReasonCounts);
    }

    [Fact]
    public void ActualBenchmarkOutcomeGaps_AreCountedAsAttritionOnly()
    {
        DateOnly[] dates = Dates();
        HistoricalMarketDataset dataset = Dataset(benchmarks: new[]
        {
            Series(dates.Where((_, index) => index != 66).Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index)), HistoricalFieldCoverage.Partial)
        });
        SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset,
            new SparrowBenchmarkAnalysisRequest(Request(dataset) with { Horizons = new[] { 1 } }, "000300.SH"));

        Assert.Equal(HistoricalReplaySupport.Partial, result.Support);
        Assert.Equal(2, result.UnavailableReasonCounts[SparrowBenchmarkReasonCodes.BenchmarkExitMissing]);
        Assert.DoesNotContain(SparrowBenchmarkReasonCodes.BenchmarkCoveragePartial, result.UnavailableReasonCounts.Keys);
        SparrowBenchmarkHorizonMetrics metric = Assert.Single(result.HorizonMetrics);
        Assert.Equal(2, metric.SelectionCount);
        Assert.Equal(2, metric.StockAvailableCount);
        Assert.Equal(0, metric.BenchmarkAvailableCount);
        Assert.Equal(0, metric.ExcessAvailableCount);
    }

    [Fact]
    public void SelectionWeightedMetrics_UseArithmeticExcessAndZeroIsNotOutperformance()
    {
        HistoricalMarketDataset dataset = Dataset(benchmarks: new[]
        {
            Series(Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, 8 + index * .02)))
        });
        SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset,
            new SparrowBenchmarkAnalysisRequest(Request(dataset) with { Horizons = new[] { 1 } }, "000300.SH"));

        SparrowBenchmarkHorizonMetrics metric = Assert.Single(result.HorizonMetrics);
        Assert.Equal(2, metric.SelectionCount);
        Assert.Equal(2, metric.StockAvailableCount);
        Assert.Equal(2, metric.BenchmarkAvailableCount);
        Assert.Equal(2, metric.ExcessAvailableCount);
        Assert.Equal(0, metric.AverageExcessReturnPercent!.Value, 10);
        Assert.Equal(0, metric.MedianExcessReturnPercent!.Value, 10);
        Assert.Equal(0, metric.OutperformanceRate!.Value, 10);
    }

    [Fact]
    public void FutureBenchmarkClose_CannotChangeSelectionRankingOrBaseStockOutcome()
    {
        HistoricalMarketDataset baseline = Dataset(benchmarks: new[] { Series(Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index))) });
        HistoricalMarketDataset changed = Dataset(benchmarks: new[] { Series(Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, index > 65 ? 1_000 + index : 100 + index))) });
        SparrowBenchmarkAnalysisRequest request = new(Request(baseline), "000300.SH");
        SparrowBenchmarkAnalysisResult first = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(baseline, request);
        SparrowBenchmarkAnalysisResult second = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(changed, request);
        Assert.Equal(first.BaseBacktestResult!.Selections.Select(item => (item.ReplayDate, item.Selection.Code, item.Selection.RankedCandidate.Rank, item.Selection.RankedCandidate.TotalScore)),
            second.BaseBacktestResult!.Selections.Select(item => (item.ReplayDate, item.Selection.Code, item.Selection.RankedCandidate.Rank, item.Selection.RankedCandidate.TotalScore)));
        Assert.Equal(first.BaseBacktestResult.Selections.SelectMany(item => item.Outcomes).Select(item => item.ReturnPercent), second.BaseBacktestResult.Selections.SelectMany(item => item.Outcomes).Select(item => item.ReturnPercent));
    }

    [Fact]
    public async Task BenchmarkAnalysis_ExportsDeterministicMachineReadableResult()
    {
        HistoricalMarketDataset dataset = Dataset(benchmarks: new[] { Series(Dates().Select((date, index) => new HistoricalBenchmarkObservation(date, 100 + index))) });
        SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(dataset, new SparrowBenchmarkAnalysisRequest(Request(dataset), "000300.SH"));
        string path = Path.Combine(Path.GetTempPath(), $"benchmark-result-{Guid.NewGuid():N}.json");
        try
        {
            await SparrowHistoricalResultExporter.ExportBenchmarkAnalysisJsonAsync(result, path);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("V2", json.RootElement.GetProperty("Strategy").GetString());
            Assert.Equal("000300.SH", json.RootElement.GetProperty("BenchmarkId").GetString());
            Assert.True(json.RootElement.TryGetProperty("SupportReasonCodes", out JsonElement supportReasons));
            Assert.Equal(JsonValueKind.Array, supportReasons.ValueKind);
            Assert.Equal("SelectionWeighted", json.RootElement.GetProperty("WeightingMethod").GetString());
            Assert.Equal("GrossCloseToClose", json.RootElement.GetProperty("ReturnBasis").GetString());
            Assert.Equal("Raw", json.RootElement.GetProperty("StockPriceAdjustmentMode").GetString());
            Assert.Equal("StockCloseToClose", json.RootElement.GetProperty("StockReturnBasis").GetString());
            Assert.Equal("IndexCloseToClose", json.RootElement.GetProperty("BenchmarkReturnBasis").GetString());
            Assert.Equal("StockReturnPercent - BenchmarkReturnPercent", json.RootElement.GetProperty("ExcessReturnFormula").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static HistoricalBenchmarkSeries Series(IEnumerable<HistoricalBenchmarkObservation> observations, HistoricalFieldCoverage coverage = HistoricalFieldCoverage.Full,
        string source = "fixture", DateTimeOffset? acquiredAt = null) => new("000300.SH", HistoricalBenchmarkPriceBasis.IndexClose, source, observations, coverage,
        new HistoricalBenchmarkProvenance(source, Dates()[0], Dates()[^1], "fixture date-set equality", acquiredAt));

    private static DateOnly[] Dates() => Enumerable.Range(0, 90).Select(index => new DateOnly(2026, 1, 1).AddDays(index)).ToArray();
    private static SparrowBacktestRequest Request(HistoricalMarketDataset dataset) => new(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, dataset.TradingDates[65], dataset.TradingDates[65], 2, new[] { 1, 3, 5, 10, 20 }, V2Parameters: new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false));

    private static HistoricalMarketDataset Dataset(IEnumerable<HistoricalBenchmarkSeries>? benchmarks = null, IEnumerable<HistoricalStrategyCapabilityExplanation>? strategyCapabilities = null) => Dataset(Dates(), benchmarks, strategyCapabilities);
    private static HistoricalMarketDataset Dataset(IReadOnlyList<DateOnly> dates, IEnumerable<HistoricalBenchmarkSeries>? benchmarks = null, IEnumerable<HistoricalStrategyCapabilityExplanation>? strategyCapabilities = null)
    {
        string[] symbols = ["600000", "600001"];
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => symbols.Select((symbol, index) => new HistoricalQuoteObservation(date,
            new QuoteSnapshot(symbol, symbol, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        KlineSeries[] klines = symbols.Select(symbol => new KlineSeries(symbol, dates.Select((date, index) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + index * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalSecurity[] securities = symbols.Select(symbol => new HistoricalSecurity(symbol, symbol, HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai, dates[0], null, HistoricalLifecycleQuality.Partial, dates[0])).ToArray();
        HistoricalUniverseSnapshot[] universes = dates.Select(date => new HistoricalUniverseSnapshot(date, symbols, HistoricalUniverseQuality.Partial, "fixture")).ToArray();
        HistoricalFieldCapability[] fields = Enum.GetValues<HistoricalField>().Select(field => new HistoricalFieldCapability(field, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full)).ToArray();
        HistoricalPriceSeriesProvenance[] provenance = symbols.Select(symbol => new HistoricalPriceSeriesProvenance(symbol, "fixture", HistoricalPriceAdjustmentMode.Raw, dates[0], dates[^1])).ToArray();
        HistoricalMarketContext[] contexts = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, .5)).ToArray();
        HistoricalMarketContextProvenance[] contextProvenance = dates.Select(date => new HistoricalMarketContextProvenance(date, HistoricalFieldOrigin.Observed, "fixture", HistoricalFieldOrigin.Observed, "fixture")).ToArray();
        return new HistoricalMarketDataset("benchmark-fixture", dates, quotes, klines, contexts, priceAdjustmentMode: "Raw", source: "fixture", schemaVersion: 2,
            metadata: new HistoricalDatasetMetadata("benchmark-fixture", "fixture", null, HistoricalUniverseQuality.Partial), securities: securities, universes: universes,
            fieldCapabilities: fields, priceSeriesProvenance: provenance, marketContextProvenance: contextProvenance,
            qualitySummary: new HistoricalDatasetQualitySummary(HistoricalUniverseQuality.Partial, HistoricalLifecycleQuality.Partial, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.None, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.None),
            benchmarks: benchmarks, strategyCapabilities: strategyCapabilities);
    }
}
