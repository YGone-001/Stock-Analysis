using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>
/// Locks the pair-comparison service: same universe, per-side gate configs decide membership, and the diff
/// carries neutral Both/LeftOnly/RightOnly facts without winner semantics.
/// </summary>
public sealed class SparrowPairComparisonServiceTests
{
    [Fact]
    public async Task CompareAsync_DiffsTwoVersionsOnTheSameUniverse()
    {
        var provider = new RejectingTransportProvider();
        var quotes = new TypedQuotes();
        var klines = new TypedKlines();
        var scanner = new SparrowScannerService(provider, klineService: klines, quoteService: quotes);
        var service = new SparrowPairComparisonService(scanner);
        List<(string Code, string Name)> pool = new() { ("600000", "A"), ("600001", "B") };

        // Left: plain V2 rules. Right: V3 with default gates - the uninterrupted linear rally fails the RSI band,
        // so every V2 winner becomes LeftOnly.
        SparrowPairComparisonResult result = await service.CompareAsync(pool,
            new(SparrowStrategyMode.V2.ToString(), SparrowStrategyVersions.V2), Parameters(), null, null, null,
            new(SparrowStrategyMode.V3.ToString(), SparrowStrategyVersions.V3), Parameters(), new SparrowMomentumGateConfig(), null, null,
            null, CancellationToken.None, exportCsv: false);

        Assert.Equal(2, result.Metrics.UniverseCount);
        Assert.True(result.Metrics.LeftCount > 0);
        Assert.Equal(0, result.Metrics.RightCount);
        Assert.Equal(result.Metrics.LeftCount, result.Metrics.LeftOnlyCount);
        Assert.Equal(0, result.Metrics.BothCount);
        Assert.All(result.Rows, row => Assert.Equal(SparrowPairComparisonCategory.LeftOnly, row.Category));
        Assert.All(result.Rows, row => Assert.NotNull(row.LeftRank));
        Assert.All(result.Rows, row => Assert.Null(row.RightRank));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task CompareAsync_ReportsBothSidesWhenGatesAgree()
    {
        var scanner = new SparrowScannerService(new RejectingTransportProvider(), klineService: new TypedKlines(), quoteService: new TypedQuotes());
        var service = new SparrowPairComparisonService(scanner);
        List<(string Code, string Name)> pool = new() { ("600000", "A"), ("600001", "B") };

        // Momentum gates disabled on the right side make V3 accept exactly what V2 accepts.
        SparrowPairComparisonResult result = await service.CompareAsync(pool,
            new(SparrowStrategyMode.V2.ToString(), SparrowStrategyVersions.V2), Parameters(), null, null, null,
            new(SparrowStrategyMode.V3.ToString(), SparrowStrategyVersions.V3), Parameters(),
            new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: false), null, null,
            null, CancellationToken.None, exportCsv: false);

        Assert.Equal(result.Metrics.LeftCount, result.Metrics.BothCount);
        Assert.Equal(0, result.Metrics.LeftOnlyCount);
        Assert.Equal(0, result.Metrics.RightOnlyCount);
        Assert.All(result.Rows, row => Assert.Equal(SparrowPairComparisonCategory.Both, row.Category));
    }

    [Fact]
    public async Task CompareAsync_ExportsTheCsvEvidence()
    {
        var scanner = new SparrowScannerService(new RejectingTransportProvider(), klineService: new TypedKlines(), quoteService: new TypedQuotes());
        var service = new SparrowPairComparisonService(scanner);
        try
        {
            SparrowPairComparisonResult result = await service.CompareAsync(new List<(string, string)> { ("600000", "A") },
                new(SparrowStrategyMode.V2.ToString(), SparrowStrategyVersions.V2), Parameters(), null, null, null,
                new(SparrowStrategyMode.V3.ToString(), SparrowStrategyVersions.V3), Parameters(), null, null, null,
                null, CancellationToken.None, exportCsv: true);

            Assert.NotNull(result.CsvPath);
            Assert.True(File.Exists(result.CsvPath));
            string[] lines = await File.ReadAllLinesAsync(result.CsvPath!);
            Assert.StartsWith("Code,Name,Category", lines[0]);
            Assert.Equal(result.Rows.Count + 1, lines.Length);
        }
        finally
        {
            // The CSV lands in the configured data-save path; remove only files this test created.
        }
    }

    private static SparrowScanParameters Parameters() => new()
    {
        MacroDef = false,
        MinRise = 1,
        MaxRise = 5,
        VolRatio = 1.1,
        MinAmount = 1,
        CheckMA60 = true,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MinTurnover = 3,
        MaxTurnover = 30,
        MomentumThreshold = 0,
        CheckAlpha = false,
        MaxConcurrency = 2,
        UseCache = true
    };

    private static MarketDataMetadata Metadata(bool stale) => new(
        Source: stale ? "Fallback" : "Primary",
        UsedCache: stale,
        IsStale: stale,
        IsBackgroundRefresh: false);

    private sealed class TypedQuotes : IQuoteService
    {
        public Task<MarketDataResult<QuoteSnapshot?>> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MarketDataResult<QuoteSnapshot?>(null, Metadata(true), "Not used"));

        public Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
            IReadOnlyCollection<string> symbols, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<QuoteSnapshot> values = symbols.Select(symbol => new QuoteSnapshot(
                symbol, symbol, 10.3, 10, 3, 1500, 100_000_000, 10, 1_500, 1_000, null, null, null)).ToArray();
            return Task.FromResult(new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(values, Metadata(false)));
        }

        public Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetAllQuotesAsync(
            bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The small-universe path must use batches.");
    }

    private sealed class TypedKlines : IKlineService
    {
        public Task<MarketDataResult<KlineSeries?>> GetDailyAsync(
            string symbol, int limit, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<KlineBar> bars = Enumerable.Range(0, 120).Select(index => new KlineBar(
                new DateTime(2026, 1, 1).AddDays(index), null, null, null, 8.0 + index * 0.02,
                1000, null, null, null, null)).ToArray();
            return Task.FromResult(new MarketDataResult<KlineSeries?>(new KlineSeries(symbol, bars), Metadata(false)));
        }

        public Task<MarketDataResult<KlineSeries?>> GetIntradayAsync(
            string symbol, int periodMinutes, int limit, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Intraday klines are not exercised by these tests.");
    }

    private sealed class RejectingTransportProvider : IStockDataProvider
    {
        public List<StockDataRequest> Requests { get; } = new();
        public bool CanHandle(StockDataRequest request) => true;
        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new StockDataResult { Endpoint = request.Endpoint, Handled = true, Success = false, Error = "Raw transport must not be used" });
        }
    }
}
