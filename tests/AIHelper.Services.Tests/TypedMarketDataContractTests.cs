using System.Collections.Concurrent;
using System.Text.Json;
using AIHelper.Services.StockData;
using AIHelper.Services;
using AIHelper.Services.StockData.Sparrow;
using AIHelper.Models;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class TypedMarketDataContractTests
{
	[Fact]
	public async Task QuoteService_UsesExistingBatchEndpoint_AndPreservesZeroAndNull()
	{
		var transport = new RecordingTransport("""
		{"data":[
		  {"Code":"600519","Name":"贵州茅台","Price":0,"PreClose":null,"Percent":0,"Volume":0,"Amount":0,"Turnover":null,"OuterVolume":null,"InnerVolume":0,"K":{"Open":0,"High":0,"Low":0}}
		]}
		""");
		var service = new QuoteService(transport);

		MarketDataResult<IReadOnlyList<QuoteSnapshot>> result = await service.GetQuotesAsync(
			new[] { "600519", "600519" }, forceRefresh: true);

		Assert.True(result.Success);
		Assert.Equal("/api/quote?code=600519&refresh=1", Assert.Single(transport.Requests).Endpoint);
		QuoteSnapshot quote = Assert.Single(result.Data);
		Assert.Equal(0, quote.Price);
		Assert.Null(quote.PreviousClose);
		Assert.Equal(0, quote.Volume);
		Assert.Null(quote.OuterVolume);
		Assert.Equal(0, quote.InnerVolume);
	}

	[Fact]
	public async Task QuoteService_MalformedSuccessfulPayload_ThrowsContractException()
	{
		var service = new QuoteService(new RecordingTransport("{\"data\":{}}"));

		MarketDataContractException error = await Assert.ThrowsAsync<MarketDataContractException>(
			() => service.GetQuotesAsync(new[] { "600519" }));

		Assert.Equal("Quote", error.ContractName);
		Assert.Contains("/api/quote?code=600519", error.Endpoint, StringComparison.Ordinal);
	}

	[Fact]
	public async Task QuoteService_ProviderFailure_RemainsTypedFailureInsteadOfEmptySuccess()
	{
		var service = new QuoteService(new RecordingTransport("", success: false, error: "provider unavailable"));

		MarketDataResult<IReadOnlyList<QuoteSnapshot>> result = await service.GetQuotesAsync(new[] { "600519" });

		Assert.False(result.Success);
		Assert.Empty(result.Data);
		Assert.Equal("provider unavailable", result.Error);
	}

	[Fact]
	public async Task KlineService_UsesExistingDailyEndpoint_AndPreservesOrderAndNulls()
	{
		var transport = new RecordingTransport("""
		{"data":[
		  {"Date":"2026-01-02","Open":10000,"High":11000,"Low":9000,"Close":10500,"Volume":0,"Amount":null},
		  {"Date":"2026-01-05","Open":10500,"High":12000,"Low":10000,"Close":11500,"Volume":100,"Amount":2000}
		]}
		""");
		var service = new KlineService(transport);

		MarketDataResult<KlineSeries?> result = await service.GetDailyAsync("600519", 65, forceRefresh: true);

		Assert.True(result.Success);
		Assert.Equal("/api/kline-all?code=600519&type=day&limit=65&refresh=1", Assert.Single(transport.Requests).Endpoint);
		KlineSeries series = Assert.IsType<KlineSeries>(result.Data);
		Assert.Equal(new DateTime(2026, 1, 2), series.Bars[0].Date);
		Assert.Equal(10, series.Bars[0].Open);
		Assert.Equal(0, series.Bars[0].Volume);
		Assert.Null(series.Bars[0].Amount);
		Assert.Equal(11.5, series.Bars[1].Close);
	}

	[Fact]
	public async Task KlineService_InvalidDate_ThrowsContractException()
	{
		var service = new KlineService(new RecordingTransport("{\"data\":[{\"Date\":\"not-a-date\",\"Close\":10000}]}"));

		MarketDataContractException error = await Assert.ThrowsAsync<MarketDataContractException>(
			() => service.GetDailyAsync("600519", 65));

		Assert.Equal("Kline", error.ContractName);
	}

	[Fact]
	public async Task KlineService_EmptyData_IsSuccessfulButContainsNoBars()
	{
		var service = new KlineService(new RecordingTransport("{\"data\":[]}"));

		MarketDataResult<KlineSeries?> result = await service.GetDailyAsync("600519", 65);

		Assert.True(result.Success);
		Assert.Empty(Assert.IsType<KlineSeries>(result.Data).Bars);
	}

	[Fact]
	public async Task CalendarService_ParsesTradingAndPreviousTradingDay_WithoutChangingEndpoint()
	{
		var transport = new RecordingTransport("""
		{"data":{"is_workday":false,"previous":[{"numeric":"20260102"}]}}
		""");
		var service = new MarketCalendarService(transport);

		MarketDataResult<TradingDayResult?> result = await service.GetTradingDayAsync(new DateTime(2026, 1, 3));

		Assert.True(result.Success);
		Assert.Equal("/api/workday?date=20260103", Assert.Single(transport.Requests).Endpoint);
		TradingDayResult day = Assert.IsType<TradingDayResult>(result.Data);
		Assert.False(day.IsTradingDay);
		Assert.Equal(new DateTime(2026, 1, 2), day.PreviousTradingDay);
	}

	[Fact]
	public async Task ExportTradingDate_UsesTypedCalendarResult()
	{
		DateTime requested = new(2026, 1, 3);
		var calendar = new StubCalendar(new TradingDayResult(requested, false, new DateTime(2026, 1, 2)));

		DateTime actual = await DataExportEngine.GetActualTradingDateAsync(calendar, requested);

		Assert.Equal(new DateTime(2026, 1, 2), actual);
		Assert.Equal(requested, calendar.RequestedDate);
	}

	[Fact]
	public async Task TypedServices_ForwardTheCallerCancellationToken()
	{
		using var cancellation = new CancellationTokenSource();
		var transport = new RecordingTransport("{\"data\":[]}");
		var service = new QuoteService(transport);

		await service.GetQuotesAsync(new[] { "600519" }, cancellationToken: cancellation.Token);

		Assert.Equal(cancellation.Token, transport.LastCancellationToken);
	}

	[Fact]
	public async Task ClassicSparrow_UsesTypedQuoteAndKlineServices_WithExistingEndpoints()
	{
		var transport = new RoutedTransport();
		var scanner = new SparrowClassicScanner(
			transport,
			klineService: new KlineService(transport),
			quoteService: new QuoteService(transport));
		var parameters = new SparrowClassicScanParameters
		{
			MacroDef = false,
			UseCache = true,
			MinRise = 0,
			MaxRise = 9.9,
			MinAmount = 1,
			VolRatio = 1,
			MinAdhesion = 0,
			MaxAdhesion = 1,
			CheckMA60 = false,
			MaxConcurrency = 1
		};

		List<SparrowClassicCandidate> candidates = await scanner.ScanAsync(
			new[] { ("600000", "浦发银行") }, parameters, null, CancellationToken.None);

		Assert.Single(candidates);
		Assert.Contains(transport.Requests, request => request.Endpoint == "/api/quote?code=600000");
		Assert.Contains(transport.Requests, request => request.Endpoint == "/api/kline-all?code=600000&type=day&limit=65");
	}

	private sealed class RecordingTransport : IStockDataProvider
	{
		private readonly string _json;
		private readonly bool _success;
		private readonly string _error;

		public RecordingTransport(string json, bool success = true, string error = "")
		{
			_json = json;
			_success = success;
			_error = error;
		}

		public ConcurrentQueue<StockDataRequest> Requests { get; } = new();
		public CancellationToken LastCancellationToken { get; private set; }
		public bool CanHandle(StockDataRequest request) => true;

		public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
		{
			Requests.Enqueue(request);
			LastCancellationToken = cancellationToken;
			return Task.FromResult(new StockDataResult
			{
				Endpoint = request.Endpoint,
				Handled = true,
				Success = _success,
				Json = _json,
				Source = "TestProvider",
				Error = _error,
				UsedCache = true,
				IsStale = false
			});
		}
	}

	private sealed class StubCalendar : IMarketCalendarService
	{
		private readonly TradingDayResult _result;

		public StubCalendar(TradingDayResult result)
		{
			_result = result;
		}

		public DateTime RequestedDate { get; private set; }

		public Task<MarketDataResult<TradingDayResult?>> GetTradingDayAsync(DateTime date, CancellationToken cancellationToken = default)
		{
			RequestedDate = date;
			return Task.FromResult(new MarketDataResult<TradingDayResult?>(
				_result, new MarketDataMetadata("TestProvider", false, false, false)));
		}
	}

	private sealed class RoutedTransport : IStockDataProvider
	{
		public List<StockDataRequest> Requests { get; } = new();
		public bool CanHandle(StockDataRequest request) => true;

		public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
		{
			Requests.Add(request);
			string json = request.Path switch
			{
				"/api/quote" => """
				{"data":[{"Code":"600000","Name":"浦发银行","Price":10,"PreClose":9.8,"Percent":2.04,"Amount":100000000,"Turnover":10,"OuterVolume":1500,"InnerVolume":1000}]}
				""",
				"/api/kline-all" => JsonSerializer.Serialize(new
				{
					data = Enumerable.Range(0, 65).Select(index => new
					{
						Date = new DateTime(2026, 1, 1).AddDays(index).ToString("yyyy-MM-dd"),
						Close = 10_000L + index * 10L
					})
				}),
				_ => "{\"data\":[]}"
			};
			return Task.FromResult(new StockDataResult
			{
				Endpoint = request.Endpoint,
				Handled = true,
				Success = true,
				Json = json,
				Source = "TestProvider"
			});
		}
	}
}
