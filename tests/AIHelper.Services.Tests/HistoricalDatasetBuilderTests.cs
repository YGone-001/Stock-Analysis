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

    private sealed class FixtureSource : IHistoricalMarketDataSource
    {
        public Task<IReadOnlyList<HistoricalSourceCapability>> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalSourceCapability>>([
            new("stock_basic", HistoricalSourceCapabilityStatus.Available), new("daily", HistoricalSourceCapabilityStatus.Available), new("trade_cal", HistoricalSourceCapabilityStatus.Available),
            new("daily_basic", HistoricalSourceCapabilityStatus.Available), new("index_daily", HistoricalSourceCapabilityStatus.Available)]);
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
        public Task<IReadOnlyList<HistoricalIndexDaily>> GetIndexDailyAsync(string indexCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalIndexDaily>>([
            new(indexCode, new DateOnly(2024, 1, 2), 3000, 2963, 1.25, "tushare")]);
        public Task<IReadOnlyList<HistoricalSuspension>> GetSuspensionsAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalSuspension>>(Array.Empty<HistoricalSuspension>());
    }
}
