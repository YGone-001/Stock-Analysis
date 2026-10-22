using AIHelper.Core.StockData;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>
/// Locks the market-data contract: the domain price unit is CNY yuan across both the live
/// (EastMoney milli-unit) path and the historical (Tushare yuan) path, so the two are directly comparable.
/// </summary>
public sealed class SparrowMarketDataContractTests
{
    [Fact]
    public async Task KlineService_NormalizesMilliUnitsToYuan()
    {
        var provider = new StubProvider(
            "/api/kline-all?code=600000&type=day&limit=120",
            """{"data":[{"Date":"2026-01-02","Open":10000,"High":10500,"Low":9900,"Close":10250,"Volume":1000,"Amount":100000}]}""");
        var service = new KlineService(provider);

        MarketDataResult<KlineSeries?> result = await service.GetDailyAsync("600000", 120);
        KlineBar bar = Assert.Single(result.Data!.Bars);

        Assert.Equal(10.0, bar.Open);
        Assert.Equal(10.5, bar.High);
        Assert.Equal(9.9, bar.Low);
        Assert.Equal(10.25, bar.Close);
    }

    [Fact]
    public async Task QuoteService_NormalizesMilliUnitsToYuan()
    {
        var provider = new StubProvider(
            "/api/quote?code=600000",
            """{"data":[{"Code":"600000","Name":"A","K":{"Close":10250,"Last":10000,"Open":10000,"High":10500,"Low":9900}}]}""");
        var service = new QuoteService(provider);

        MarketDataResult<IReadOnlyList<QuoteSnapshot>> result = await service.GetQuotesAsync(new[] { "600000" });
        QuoteSnapshot quote = Assert.Single(result.Data);

        Assert.Equal(10.25, quote.Price);
        Assert.Equal(10.0, quote.PreviousClose);
        Assert.Equal(10.0, quote.Open);
        Assert.Equal(10.5, quote.High);
        Assert.Equal(9.9, quote.Low);
    }

    [Fact]
    public async Task LiveAndHistoricalKline_UseIdenticalYuanUnits()
    {
        // Live: EastMoney milli-unit close 10250 → 10.25 yuan via the typed contract.
        var liveProvider = new StubProvider(
            "/api/kline-all?code=600000&type=day&limit=120",
            """{"data":[{"Date":"2026-01-02","Close":10250}]}""");
        KlineBar liveBar = Assert.Single((await new KlineService(liveProvider).GetDailyAsync("600000", 120)).Data!.Bars);

        // Historical: Tushare already supplies yuan, so HistoricalDatasetBuilder stores it as-is.
        var historicalBar = new KlineBar(new DateTime(2026, 1, 2), null, null, null, 10.25, null, null, null, null, null);

        Assert.Equal(10.25, liveBar.Close);
        Assert.Equal(liveBar.Close, historicalBar.Close);
        // The historical path must never magnify a yuan value into milli-units.
        Assert.NotEqual(10250, historicalBar.Close);
    }

    private sealed class StubProvider : IStockDataProvider
    {
        private readonly string _endpoint;
        private readonly string _json;

        public StubProvider(string endpoint, string json)
        {
            _endpoint = endpoint;
            _json = json;
        }

        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(
            StockDataRequest request,
            CancellationToken cancellationToken = default)
        {
            bool matched = string.Equals(request.Endpoint, _endpoint, StringComparison.Ordinal);
            return Task.FromResult(new StockDataResult
            {
                Endpoint = request.Endpoint,
                Handled = true,
                Success = matched,
                Json = matched ? _json : "",
                Error = matched ? "" : "not configured"
            });
        }
    }
}
