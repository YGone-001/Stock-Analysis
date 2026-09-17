using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class HistoricalDatasetBuilderTests
{
    [Fact]
    public async Task Builder_UsesLifecycleCalendarAndObservedFields_WithoutFabricatingDepth()
    {
        string path = Path.Combine(Path.GetTempPath(), $"historical-builder-{Guid.NewGuid():N}.json");
        try
        {
            HistoricalDatasetBuildRequest request = new("fixture", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), path, IncludeTurnover: true);
            HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(new FixtureSource()).BuildAsync(request);

            Assert.True(File.Exists(path));
            Assert.Equal(HistoricalPriceAdjustmentMode.Raw, result.Dataset.PriceAdjustmentMode);
            Assert.Equal(HistoricalValueUnit.CurrencyThousands, result.Dataset.GetFieldCapability(HistoricalField.Amount)!.Unit);
            Assert.Equal(HistoricalFieldOrigin.Unavailable, result.Dataset.GetFieldCapability(HistoricalField.OuterVolume)!.Origin);
            Assert.Equal(HistoricalFieldCoverage.None, result.Dataset.GetFieldCapability(HistoricalField.InnerVolume)!.Coverage);
            Assert.Contains("600001", result.Dataset.Universes[new DateOnly(2024, 1, 2)].SecuritySymbols); // later-delisted remains present at T
            Assert.DoesNotContain("600002", result.Dataset.Universes[new DateOnly(2024, 1, 2)].SecuritySymbols); // future listing excluded
            Assert.Equal(HistoricalObservationStatus.UnknownDataGap, result.Dataset.ObservationDeclarations[(new DateOnly(2024, 1, 4), "600001")].ObservationStatus);
            Assert.Equal(1.25, result.Dataset.MarketContexts[new DateOnly(2024, 1, 2)].V2ShanghaiDailyPercent);
            HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(path);
            Assert.True(loaded.Success);
            Assert.Equal(result.Dataset.Fingerprint, loaded.Dataset!.Fingerprint);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Builder_RejectsNonRawAndProducesStableSemanticFingerprint()
    {
        string first = Path.Combine(Path.GetTempPath(), $"historical-builder-{Guid.NewGuid():N}.json");
        string second = Path.Combine(Path.GetTempPath(), $"historical-builder-{Guid.NewGuid():N}.json");
        try
        {
            HistoricalDatasetBuilder builder = new(new FixtureSource());
            HistoricalDatasetBuildResult one = await builder.BuildAsync(new("fixture", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), first));
            HistoricalDatasetBuildResult two = await builder.BuildAsync(new("fixture", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), second));
            Assert.Equal(one.Dataset.Fingerprint, two.Dataset.Fingerprint);
            await Assert.ThrowsAsync<NotSupportedException>(() => builder.BuildAsync(new("fixture", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), first, HistoricalPriceAdjustmentMode.ForwardAdjusted)));
        }
        finally { if (File.Exists(first)) File.Delete(first); if (File.Exists(second)) File.Delete(second); }
    }

    [Fact]
    public async Task Builder_CapturesDatedStSuspensionAndFactorsWithoutChangingRawPrices()
    {
        string path = Path.Combine(Path.GetTempPath(), $"historical-evidence-{Guid.NewGuid():N}.json");
        try
        {
            HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(new FixtureSource()).BuildAsync(
                new("evidence", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), path, IncludeHistoricalSt: true, IncludeSuspension: true, IncludeAdjustmentFactors: true));
            Assert.Equal(HistoricalFieldCoverage.Partial, result.Dataset.QualitySummary.HistoricalStCoverage);
            Assert.Equal(HistoricalObservationStatus.Suspended, result.Dataset.ObservationDeclarations[(new DateOnly(2024, 1, 4), "600000")].ObservationStatus);
            Assert.True(result.Dataset.RiskStatusObservations[(new DateOnly(2024, 1, 2), "600000")].IsSt);
            Assert.Equal(100d, result.Dataset.AdjustmentFactors[(new DateOnly(2024, 1, 2), "600000")].Factor);
            Assert.Equal(HistoricalPriceAdjustmentMode.Raw, result.Dataset.PriceAdjustmentMode);
            Assert.False(SparrowClassicUniverseEligibility.Evaluate("600000", "Normal Corp", true).Eligible);
            Assert.False(SparrowClassicUniverseEligibility.Evaluate("600000", "Normal Corp", null).Eligible);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Builder_FingerprintsHistoricalEvidenceButNotAcquisitionTimestamp()
    {
        string one = Path.Combine(Path.GetTempPath(), $"historical-factor-{Guid.NewGuid():N}.json");
        string two = Path.Combine(Path.GetTempPath(), $"historical-factor-{Guid.NewGuid():N}.json");
        try
        {
            HistoricalDatasetBuildRequest request = new("factor", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), one, IncludeHistoricalSt: true, IncludeSuspension: true, IncludeAdjustmentFactors: true);
            HistoricalDatasetBuildResult first = await new HistoricalDatasetBuilder(new FixtureSource(100)).BuildAsync(request);
            HistoricalDatasetBuildResult changed = await new HistoricalDatasetBuilder(new FixtureSource(101)).BuildAsync(request with { OutputPath = two });
            Assert.NotEqual(first.Dataset.Fingerprint, changed.Dataset.Fingerprint);
        }
        finally { if (File.Exists(one)) File.Delete(one); if (File.Exists(two)) File.Delete(two); }
    }

    [Fact]
    public async Task Builder_AcquiresOnlyExplicitBenchmarkIdsAndStoresCloseLevelSeries()
    {
        string path = Path.Combine(Path.GetTempPath(), $"historical-benchmark-{Guid.NewGuid():N}.json");
        try
        {
            FixtureSource source = new();
            HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(source).BuildAsync(
                new("benchmark", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), path,
                    IncludeV2IndexContext: false, BenchmarkIds: new[] { "000300.SH" }));

            HistoricalBenchmarkSeries series = Assert.Single(result.Dataset.Benchmarks).Value;
            Assert.Equal("000300.SH", series.BenchmarkId);
            Assert.Equal(HistoricalBenchmarkPriceBasis.IndexClose, series.PriceBasis);
            Assert.Equal(new[] { "000300.SH" }, source.IndexRequests);
            Assert.Equal(HistoricalFieldCoverage.Partial, series.Coverage); // fixture has one of three market dates
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Builder_WithNoBenchmarkIds_DoesNotAcquireBenchmarkSeries()
    {
        string path = Path.Combine(Path.GetTempPath(), $"historical-no-benchmark-{Guid.NewGuid():N}.json");
        try
        {
            FixtureSource source = new();
            HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(source).BuildAsync(
                new("no-benchmark", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), path, IncludeV2IndexContext: false));

            Assert.Empty(result.Dataset.Benchmarks);
            Assert.Empty(source.IndexRequests);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Builder_PersistsEveryExplicitBenchmarkId()
    {
        string path = Path.Combine(Path.GetTempPath(), $"historical-multiple-benchmarks-{Guid.NewGuid():N}.json");
        try
        {
            FixtureSource source = new();
            HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(source).BuildAsync(
                new("multiple-benchmarks", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), path,
                    IncludeV2IndexContext: false, BenchmarkIds: new[] { "000852.SH", "000300.SH" }));

            Assert.Equal(new[] { "000300.SH", "000852.SH" }, result.Dataset.Benchmarks.Keys);
            Assert.Equal(new[] { "000300.SH", "000852.SH" }, source.IndexRequests);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BuildRequest_RejectsDuplicateOrBlankBenchmarkIdentifiers()
    {
        string output = Path.Combine(Path.GetTempPath(), "historical-benchmark-validation.json");
        Assert.Throws<ArgumentException>(() => new HistoricalDatasetBuildRequest("fixture", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), output,
            BenchmarkIds: new[] { "000300.SH", "000300.SH" }).Validate());
        Assert.Throws<ArgumentException>(() => new HistoricalDatasetBuildRequest("fixture", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4), output,
            BenchmarkIds: new[] { " " }).Validate());
    }

    private sealed class FixtureSource(double factor = 100) : IHistoricalMarketDataSource
    {
        public List<string> IndexRequests { get; } = [];
        public Task<IReadOnlyList<HistoricalSourceCapability>> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalSourceCapability>>([
            new("stock_basic", HistoricalSourceCapabilityStatus.Available), new("daily", HistoricalSourceCapabilityStatus.Available), new("trade_cal", HistoricalSourceCapabilityStatus.Available),
            new("daily_basic", HistoricalSourceCapabilityStatus.Available), new("index_daily", HistoricalSourceCapabilityStatus.Available),
            new("stock_st", HistoricalSourceCapabilityStatus.Available), new("suspend_d", HistoricalSourceCapabilityStatus.Available), new("adj_factor", HistoricalSourceCapabilityStatus.Available)]);
        public Task<IReadOnlyList<HistoricalCalendarDay>> GetCalendarAsync(string exchange, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalCalendarDay>>([
            new(new DateOnly(2024, 1, 2), true, null, "tushare"), new(new DateOnly(2024, 1, 3), true, new DateOnly(2024, 1, 2), "tushare"), new(new DateOnly(2024, 1, 4), true, new DateOnly(2024, 1, 3), "tushare")]);
        public Task<IReadOnlyList<HistoricalSourceSecurity>> GetSecuritiesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalSourceSecurity>>([
            new("600000", "600000.SH", "A", "主板", "SSE", "L", new DateOnly(2020, 1, 1), null, "tushare"),
            new("600001", "600001.SH", "B", "主板", "SSE", "D", new DateOnly(2020, 1, 1), new DateOnly(2024, 1, 5), "tushare"),
            new("600002", "600002.SH", "C", "主板", "SSE", "L", new DateOnly(2024, 1, 5), null, "tushare")]);
        public Task<IReadOnlyList<HistoricalDailyPrice>> GetDailyPricesAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            string symbol = tsCode[..6];
            return Task.FromResult<IReadOnlyList<HistoricalDailyPrice>>([
                new(symbol, tsCode, new DateOnly(2024, 1, 2), 10, 11, 9, 10, 9.8, .2, 2.04, 100, 250, "tushare", HistoricalPriceAdjustmentMode.Raw),
                new(symbol, tsCode, new DateOnly(2024, 1, 3), 10, 11, 9, 10.5, 10, .5, 5, 110, 260, "tushare", HistoricalPriceAdjustmentMode.Raw)]);
        }
        public Task<IReadOnlyList<HistoricalTurnover>> GetTurnoverAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalTurnover>>([
            new(tsCode[..6], tsCode, new DateOnly(2024, 1, 2), 1.2, "tushare"), new(tsCode[..6], tsCode, new DateOnly(2024, 1, 3), 1.3, "tushare")]);
        public Task<IReadOnlyList<HistoricalIndexDaily>> GetIndexDailyAsync(string indexCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
        {
            IndexRequests.Add(indexCode);
            return Task.FromResult<IReadOnlyList<HistoricalIndexDaily>>([new(indexCode, new DateOnly(2024, 1, 2), 3000, 2963, 1.25, "tushare")]);
        }
        public Task<IReadOnlyList<HistoricalSuspension>> GetSuspensionsAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalSuspension>>([new("600000", "600000.SH", new DateOnly(2024, 1, 4), "S", null, "tushare")]);
        public Task<IReadOnlyList<HistoricalStStatus>> GetStStatusesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalStStatus>>([new("600000", "600000.SH", new DateOnly(2024, 1, 2), "ST", "tushare")]);
        public Task<IReadOnlyList<HistoricalSourceAdjustmentFactor>> GetAdjustmentFactorsAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalSourceAdjustmentFactor>>([new(tsCode[..6], tsCode, new DateOnly(2024, 1, 2), factor, "tushare")]);
    }
}
