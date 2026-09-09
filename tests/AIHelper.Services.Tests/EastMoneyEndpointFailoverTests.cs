using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class EastMoneyEndpointFailoverTests
{
    [Fact]
    public async Task BatchQuote_WhenCanonicalHostFails_RetriesOnSiblingHistoryHost()
    {
        var handler = new FailFirstHandler(
            "{\"data\":{\"diff\":[{\"f12\":\"600519\",\"f14\":\"贵州茅台\",\"f2\":1290.88,\"f3\":-1.41,\"f5\":32226,\"f6\":4168500462,\"f8\":0.26,\"f18\":1309.3,\"f34\":14307,\"f35\":17919}]}}"
        );
        using var client = new HttpClient(handler);
        using var cache = new LocalStockCacheProvider();
        using var provider = new EastMoneyStockDataProvider(client, cache);

        StockDataResult result = await provider.GetDataAsync(
            StockDataRequest.Parse("/api/quote?code=600519,000001")
        );

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("push2.eastmoney.com", handler.Requests[0].Host);
        Assert.Equal("push2his.eastmoney.com", handler.Requests[1].Host);
    }

	[Fact]
	public async Task BatchQuote_WhenAllAttemptsTimeout_ReturnsFailureInsteadOfThrowing()
	{
		var handler = new AlwaysTimeoutHandler();
		using var client = new HttpClient(handler);
		using var cache = new LocalStockCacheProvider();
		using var provider = new EastMoneyStockDataProvider(client, cache);

		StockDataResult result = await provider.GetDataAsync(
			StockDataRequest.Parse("/api/quote?code=600519,000001")
		);

		Assert.False(result.Success);
		Assert.Equal(4, handler.Requests.Count);
		Assert.Contains("all batches failed", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task AllMarketQuote_CombinesTheThreeScreeningMarkets()
	{
		var handler = new AllMarketHandler();
		using var client = new HttpClient(handler);
		using var cache = new LocalStockCacheProvider();
		using var provider = new EastMoneyStockDataProvider(client, cache);

		StockDataResult result = await provider.GetDataAsync(
			StockDataRequest.Parse("/api/quote-all")
		);

		Assert.True(result.Success, result.Error);
		using JsonDocument document = JsonDocument.Parse(result.Json);
		Assert.Equal(3, document.RootElement.GetProperty("data").GetArrayLength());
		Assert.Equal(3, handler.Requests.Count);
	}

    private sealed class FailFirstHandler : HttpMessageHandler
    {
        private readonly string _successJson;

        public FailFirstHandler(string successJson)
        {
            _successJson = successJson;
        }

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!);
            if (Requests.Count == 1)
            {
                throw new HttpRequestException("The response ended prematurely.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_successJson, Encoding.UTF8, "application/json")
            });
        }
    }

	private sealed class AlwaysTimeoutHandler : HttpMessageHandler
	{
		public List<Uri> Requests { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken
		)
		{
			Requests.Add(request.RequestUri!);
			return Task.FromException<HttpResponseMessage>(
				new TaskCanceledException("simulated per-request timeout"));
		}
	}

	private sealed class AllMarketHandler : HttpMessageHandler
	{
		private int _code;
		public List<Uri> Requests { get; } = new();

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken
		)
		{
			Requests.Add(request.RequestUri!);
			string code = Interlocked.Increment(ref _code).ToString("D6");
			string json = "{\"data\":{\"total\":1,\"diff\":[{\"f12\":\"" + code + "\",\"f14\":\"Stock\",\"f2\":10.3,\"f3\":3,\"f5\":100,\"f6\":100000000,\"f8\":10,\"f18\":10,\"f34\":150,\"f35\":100}]}}";
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
			{
				Content = new StringContent(json, Encoding.UTF8, "application/json")
			});
		}
	}
}
