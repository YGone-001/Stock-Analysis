using AIHelper.Core.StockData;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using System.Text.Json;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowV2TypedEngineTests
{
    [Fact]
    public async Task V2_ProductionPath_UsesTypedQuoteAndKlineServices_WithoutRawQuoteOrKlineRequests()
    {
        var provider = new RejectingTransportProvider();
        var quotes = new TypedQuotes(stale: false);
        var klines = new TypedKlines(stale: false);
        var scanner = new SparrowScannerService(provider, klineService: klines, quoteService: quotes);

        List<SparrowV2Candidate> candidates = await scanner.ScanWithFeaturesAsync(
            new List<(string Code, string Name)> { ("600000", "Typed candidate") },
            Parameters(), null, CancellationToken.None);

        SparrowV2Candidate candidate = Assert.Single(candidates);
        Assert.Equal("600000", candidate.Code);
        Assert.Equal(1, quotes.BatchCalls);
        Assert.Equal(1, klines.DailyCalls);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task V2_ProviderAndStaleMetadata_DoNotChangeCandidateOrRankingInputs()
    {
        SparrowV2Candidate fresh = Assert.Single(await ScanAsync(stale: false));
        SparrowV2Candidate stale = Assert.Single(await ScanAsync(stale: true));

        Assert.Equal(fresh.Code, stale.Code);
        Assert.Equal(fresh.Reason, stale.Reason);
        Assert.Equal(fresh.RankingFeatures.Code, stale.RankingFeatures.Code);
        Assert.Equal(fresh.RankingFeatures.RisePercent, stale.RankingFeatures.RisePercent);
        Assert.Equal(fresh.RankingFeatures.Amount, stale.RankingFeatures.Amount);
        Assert.Equal(fresh.RankingFeatures.BuyPressureRatio, stale.RankingFeatures.BuyPressureRatio);
        Assert.Equal(fresh.RankingFeatures.Adhesion, stale.RankingFeatures.Adhesion);
        Assert.Equal(fresh.RankingFeatures.Momentum, stale.RankingFeatures.Momentum);
    }

    [Fact]
    public async Task V2_TypedAcquisition_PreservesRawCompatibilityCandidatesAndRanking()
    {
        var pool = new List<(string Code, string Name)> { ("600000", "A"), ("600001", "B") };
        List<SparrowV2Candidate> raw = await new SparrowScannerService(new CompatibleTransportProvider())
            .ScanWithFeaturesAsync(pool, Parameters(), null, CancellationToken.None);
        List<SparrowV2Candidate> typed = await new SparrowScannerService(
            new RejectingTransportProvider(), klineService: new TypedKlines(stale: false), quoteService: new TypedQuotes(stale: false))
            .ScanWithFeaturesAsync(pool, Parameters(), null, CancellationToken.None);

        Assert.Equal(raw.Select(candidate => candidate.Code), typed.Select(candidate => candidate.Code));
        Assert.Equal(raw.Select(candidate => candidate.Reason), typed.Select(candidate => candidate.Reason));
        Assert.Equal(
            new SparrowRankingEngine().RankV2(raw.Select(candidate => candidate.RankingFeatures), 1, 5, false)
                .Select(candidate => (candidate.Code, candidate.Rank, candidate.TotalScore)),
            new SparrowRankingEngine().RankV2(typed.Select(candidate => candidate.RankingFeatures), 1, 5, false)
                .Select(candidate => (candidate.Code, candidate.Rank, candidate.TotalScore)));
    }

    [Fact]
    public void CandidateEvidence_UsesStableStageAndReasonIdentifiers()
    {
        SparrowQuoteData quote = new(10.3, 10, 3, 100_000_000, 10, 1500, 1000);
        SparrowKlineSnapshot snapshot = Snapshot();
        SparrowV2CandidateEvaluation passed = SparrowV2CandidateEvaluator.Evaluate(
            "600000", quote, snapshot, 0, Parameters());
        SparrowV2CandidateEvaluation unavailable = SparrowV2CandidateEvaluator.Evaluate(
            "600001", quote with { Amount = null }, snapshot, 0, Parameters());

        Assert.True(passed.Passed);
        Assert.Equal("P2", passed.P2.Stage);
        Assert.Equal(SparrowStrategyVersions.V2, passed.StrategyVersion);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, passed.P2.ReasonCode);
        Assert.Equal("P3", passed.P3.Stage);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, passed.P3.ReasonCode);
        Assert.False(unavailable.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P2QuoteDataMissing, unavailable.P2.ReasonCode);
        Assert.Equal(SparrowComparisonReasonCodes.NotRun, unavailable.P3.ReasonCode);
    }

    private static async Task<List<SparrowV2Candidate>> ScanAsync(bool stale)
    {
        var scanner = new SparrowScannerService(
            new RejectingTransportProvider(),
            klineService: new TypedKlines(stale),
            quoteService: new TypedQuotes(stale));
        return await scanner.ScanWithFeaturesAsync(
            new List<(string Code, string Name)> { ("600000", "Typed candidate") },
            Parameters(), null, CancellationToken.None);
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

    private static SparrowKlineSnapshot Snapshot()
    {
        double[] closes = Enumerable.Range(0, 120).Select(index => 8.0 + index * 0.02).Reverse().ToArray();
        return new SparrowKlineSnapshot(closes, closes[0], 0.25);
    }

    private sealed class TypedQuotes : IQuoteService
    {
        private readonly MarketDataMetadata _metadata;
        public int BatchCalls { get; private set; }

        public TypedQuotes(bool stale) => _metadata = Metadata(stale);

        public Task<MarketDataResult<QuoteSnapshot?>> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MarketDataResult<QuoteSnapshot?>(null, _metadata, "Not used"));

        public Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
            IReadOnlyCollection<string> symbols, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            BatchCalls++;
            IReadOnlyList<QuoteSnapshot> values = symbols.Select(Quote).ToArray();
            return Task.FromResult(new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(values, _metadata));
        }

        public Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetAllQuotesAsync(
            bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The small-universe path must use batches.");
    }

    private sealed class TypedKlines : IKlineService
    {
        private readonly MarketDataMetadata _metadata;
        public int DailyCalls { get; private set; }

        public TypedKlines(bool stale) => _metadata = Metadata(stale);

        public Task<MarketDataResult<KlineSeries?>> GetDailyAsync(
            string symbol, int limit, bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            DailyCalls++;
            IReadOnlyList<KlineBar> bars = Enumerable.Range(0, 120).Select(index => new KlineBar(
                new DateTime(2026, 1, 1).AddDays(index), null, null, null, 8.0 + index * 0.02,
                null, null, null, null, null)).ToArray();
            return Task.FromResult(new MarketDataResult<KlineSeries?>(new KlineSeries(symbol, bars), _metadata));
        }
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

    private sealed class CompatibleTransportProvider : IStockDataProvider
    {
        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            string json = request.Path switch
            {
                "/api/quote" => JsonSerializer.Serialize(new
                {
                    data = request.Get("code").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(symbol => new
                    {
                        Code = symbol,
                        Price = Quote(symbol).Price,
                        PreClose = Quote(symbol).PreviousClose,
                        Percent = Quote(symbol).ChangePercent,
                        Amount = Quote(symbol).Amount,
                        Turnover = Quote(symbol).Turnover,
                        OuterVolume = Quote(symbol).OuterVolume,
                        InnerVolume = Quote(symbol).InnerVolume
                    })
                }),
                "/api/kline-all" => JsonSerializer.Serialize(new
                {
                    data = Enumerable.Range(0, 120).Select(index => new { Close = (long)Math.Round((8.0 + index * 0.02) * 1000) })
                }),
                _ => ""
            };
            return Task.FromResult(new StockDataResult
            {
                Endpoint = request.Endpoint,
                Handled = true,
                Success = request.Path is "/api/quote" or "/api/kline-all",
                Json = json,
                Error = "Unsupported test endpoint"
            });
        }
    }

    private static QuoteSnapshot Quote(string symbol)
    {
        bool second = symbol.EndsWith("1", StringComparison.Ordinal);
        return new QuoteSnapshot(symbol, symbol, 10.3, 10, 3, null,
            second ? 200_000_000 : 100_000_000, 10, second ? 3000 : 1500, 1000, null, null, null);
    }

    private static MarketDataMetadata Metadata(bool stale) => new(
        Source: stale ? "Fallback" : "Primary",
        UsedCache: stale,
        IsStale: stale,
        IsBackgroundRefresh: false);
}
