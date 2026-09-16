using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class HistoricalHttpMarketDataSourceTests
{
    [Fact]
    public async Task Client_MapsTypedDailyResponseAndLocksRawTushareRange()
    {
        RecordingHandler handler = new("""
            {"source":"tushare","start_date":"2024-01-02","end_date":"2024-01-04","data":[{"symbol":"600000","ts_code":"600000.SH","trading_date":"2024-01-02","open":10,"high":11,"low":9,"close":10.5,"previous_close":10,"change":0.5,"percent":5,"volume":100,"amount":250,"source":"tushare","adjustment_mode":"Raw"}]}
            """);
        using HttpClient http = new(handler) { BaseAddress = new Uri("https://historical.test/") };
        IHistoricalMarketDataSource source = new HistoricalHttpMarketDataSource(http);

        IReadOnlyList<HistoricalDailyPrice> result = await source.GetDailyPricesAsync("600000.SH", new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 4));

        HistoricalDailyPrice item = Assert.Single(result);
        Assert.Equal(HistoricalPriceAdjustmentMode.Raw, item.AdjustmentMode);
        Assert.Equal(250, item.Amount);
        Assert.Equal(100, item.Volume);
        Assert.Contains("source=tushare", handler.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("adjustment=raw", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("start_date=2024-01-02", handler.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("end_date=2024-01-04", handler.RequestUri.Query, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(string json) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
