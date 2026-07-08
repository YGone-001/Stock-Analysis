using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;

namespace AIHelper.Services.StockData;

public sealed class EastMoneyStockDataProvider : IStockDataProvider
, IDisposable {
	private const int CodePageSize = 100;

	private readonly HttpClient _client;

	private readonly LocalStockCacheProvider _cache;

	private readonly SemaphoreSlim _requestThrottle = new SemaphoreSlim(4, 4);

	private static readonly HashSet<string> SupportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"/api/quote", "/api/kline-all", "/api/index", "/api/minute", "/api/minute-trade-all", "/api/search", "/api/codes", "/api/etf", "/api/workday"
	};

	public EastMoneyStockDataProvider(HttpClient client, LocalStockCacheProvider cache)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
		_cache = cache ?? throw new ArgumentNullException(nameof(cache));
	}

	public bool CanHandle(StockDataRequest request)
	{
		return SupportedPaths.Contains(request.Path);
	}

	public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
	{
		if (!CanHandle(request))
		{
			return StockDataResult.NotHandled(request.Endpoint);
		}
		return request.Path switch
		{
			"/api/quote" => await GetQuoteAsync(request, cancellationToken),
			"/api/kline-all" or "/api/index" => await GetKlineAsync(request, cancellationToken),
			"/api/minute" => await GetMinuteAsync(request, cancellationToken),
			"/api/minute-trade-all" => await GetTicksAsync(request, cancellationToken),
			"/api/search" => await SearchAsync(request, cancellationToken),
			"/api/codes" => await SyncCodeTableAsync(request, "stock", cancellationToken),
			"/api/etf" => await SyncCodeTableAsync(request, "etf", cancellationToken),
			"/api/workday" => await GetWorkdayAsync(request, cancellationToken),
			_ => StockDataResult.NotHandled(request.Endpoint)
		};
	}

	private async Task<StockDataResult> GetQuoteAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		List<string> codes = request.Get("code").Split(',', StringSplitOptions.RemoveEmptyEntries)
			.Select(NormalizeCode).Where(code => code.Length == 6).Distinct().ToList();
		if (codes.Count == 0)
		{
			return Failure(request, "{\"data\":[]}", "No valid stock code");
		}
		if (codes.Count == 1)
		{
			return await GetSingleQuoteAsync(request, codes[0], cancellationToken);
		}

		var rows = new List<object>();
		string lastUrl = "";
		int batchSize = 50;

		for (int i = 0; i < codes.Count; i += batchSize)
		{
			var batchCodes = codes.Skip(i).Take(batchSize).ToList();
			string url = "https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2&fields=f12,f14,f2,f3,f5,f6,f8,f15,f16,f17,f18,f19,f20,f21,f22,f23,f24,f25,f26,f27,f28,f31,f32,f33,f34,f35,f36,f37,f38,f39,f40&secids=" + string.Join(",", batchCodes.Select(ToSecId));
			lastUrl = url;
			try
			{
				using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(url, cancellationToken));
				if (!document.RootElement.TryGetProperty("data", out var dataElement) || !dataElement.TryGetProperty("diff", out var diffElement) || diffElement.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				foreach (JsonElement item in diffElement.EnumerateArray())
				{
					string code = GetString(item, "f12");
					if (string.IsNullOrWhiteSpace(code))
					{
						continue;
					}
					double close = GetDouble(item, "f2");
					double previousClose = GetDouble(item, "f18");
					rows.Add(new
					{
						Code = code,
						Name = GetString(item, "f14"),
						TotalHand = GetDouble(item, "f5"),
						Amount = GetQuoteAmount(item),
						Wp = GetFirstPositive(item, "f49", "f34"),
						Np = GetFirstPositive(item, "f161", "f35"),
						Turnover = GetDouble(item, "f8"),
						Percent = GetDouble(item, "f3"),
						BuyLevel = BuildUnavailableLevels(),
						SellLevel = BuildUnavailableLevels(),
						K = new
						{
							Close = ToMilli(close),
							Last = ToMilli(previousClose),
							PreClose = ToMilli(previousClose),
							Open = ToMilli(GetDouble(item, "f17")),
							High = ToMilli(GetDouble(item, "f15")),
							Low = ToMilli(GetDouble(item, "f16"))
						}
					});
				}
			}
			catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
				System.Diagnostics.Trace.WriteLine($"EastMoney GetQuoteAsync batch failed: {ex.Message}");
			}
		}

		if (rows.Count == 0)
		{
			return Failure(request, "{\"data\":[]}", "Unexpected quote response schema or all batches failed", lastUrl, null, string.Join(",", codes));
		}

		return Success(request, JsonSerializer.Serialize(new { data = rows }), lastUrl, string.Join(",", codes), "rows=" + rows.Count);
	}

	private async Task<StockDataResult> GetSingleQuoteAsync(StockDataRequest request, string code, CancellationToken cancellationToken)
	{
		string url = "https://push2.eastmoney.com/api/qt/stock/get?fltt=2&invt=2&secid=" + ToSecId(code) + "&fields=f57,f58,f43,f44,f45,f46,f47,f48,f49,f60,f161,f168,f170,f19,f20,f21,f22,f23,f24,f25,f26,f27,f28,f31,f32,f33,f34,f35,f36,f37,f38,f39,f40";
		try
		{
			using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(url, cancellationToken));
			if (!document.RootElement.TryGetProperty("data", out var item) || item.ValueKind != JsonValueKind.Object)
			{
				return Failure(request, "{\"data\":[]}", "Unexpected single quote response schema", url, null, code);
			}
			double close = GetDouble(item, "f43");
			double previousClose = GetDouble(item, "f60");
			object[] buyLevels = BuildLevels(item, new string[5] { "f19", "f21", "f23", "f25", "f27" }, new string[5] { "f20", "f22", "f24", "f26", "f28" });
			object[] sellLevels = BuildLevels(item, new string[5] { "f39", "f37", "f35", "f33", "f31" }, new string[5] { "f40", "f38", "f36", "f34", "f32" });
			// EastMoney no longer exposes reliable five-level depth in these public quote responses.
			// Keep the fixed shape and mark levels unavailable instead of rendering mismatched fields.
			var row = new
			{
				Code = GetString(item, "f57"),
				Name = GetString(item, "f58"),
				TotalHand = GetDouble(item, "f47"),
				Amount = GetDouble(item, "f48"),
				Wp = GetDouble(item, "f49"),
				Np = GetDouble(item, "f161"),
				Turnover = GetDouble(item, "f168"),
				Percent = GetDouble(item, "f170"),
				BuyLevel = buyLevels,
				SellLevel = sellLevels,
				K = new
				{
					Close = ToMilli(close),
					Last = ToMilli(previousClose),
					PreClose = ToMilli(previousClose),
					Open = ToMilli(GetDouble(item, "f46")),
					High = ToMilli(GetDouble(item, "f44")),
					Low = ToMilli(GetDouble(item, "f45"))
				}
			};
			return Success(request, JsonSerializer.Serialize(new { data = new object[] { row } }), url, code, "rows=1, buyLevels=" + buyLevels.Length + ", sellLevels=" + sellLevels.Length);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return Failure(request, "{\"data\":[]}", ex.Message, url, ex, code);
		}
	}

	private async Task<StockDataResult> GetKlineAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		string rawCode = request.Get("code");
		string code = NormalizeCode(rawCode);
		if (code.Length != 6)
		{
			return Failure(request, "{\"data\":[]}", "Invalid stock code");
		}
		if (!int.TryParse(request.Get("limit"), out int limit) || limit <= 0)
		{
			limit = 120;
		}
		string baseUrl = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + ToSecId(rawCode, code) + "&klt=101&fqt=1&end=20500101&lmt=" + limit + "&ut=fa5fd1943c7b386f172d6893dbfba10b&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61";
		string url = baseUrl;
		try
		{
			List<KlineRow> rows = ParseKlineRows(await SendGetWithRetryAsync(url, cancellationToken), limit);
			if (rows.Count == 0)
			{
				await Task.Delay(250, cancellationToken);
				rows = ParseKlineRows(await SendGetWithRetryAsync(url, cancellationToken), limit);
			}
			if (rows.Count == 0)
			{
				url = baseUrl + "&beg=0&end=20500101";
				rows = ParseKlineRows(await SendGetWithRetryAsync(url, cancellationToken), limit);
			}
			if (rows.Count == 0)
			{
				return Failure(request, "{\"data\":[]}", "Public source returned no K-line rows", url, null, code);
			}
			return Success(request, JsonSerializer.Serialize(new { data = rows }), url, code, "rows=" + rows.Count);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return Failure(request, "{\"data\":[]}", ex.Message, url, ex, code);
		}
	}

	private async Task<StockDataResult> GetMinuteAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		string rawCode = request.Get("code");
		string code = NormalizeCode(rawCode);
		if (code.Length != 6 || !TryResolveDate(request.Get("date"), out var targetDate))
		{
			return Failure(request, "{\"data\":{\"List\":[]}}", "Invalid code or date");
		}
		string date = targetDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
		string url = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + ToSecId(rawCode, code) + "&klt=1&fqt=1&beg=" + date + "&end=" + date + "&lmt=1000000&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61";
		try
		{
			using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(url, cancellationToken));
			var rows = new List<object>();
			if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("klines", out var klines) && klines.ValueKind == JsonValueKind.Array)
			{
				string expectedDate = targetDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
				foreach (JsonElement item in klines.EnumerateArray())
				{
					string[] parts = (item.GetString() ?? "").Split(',');
					if (parts.Length < 6 || !parts[0].StartsWith(expectedDate, StringComparison.Ordinal)) continue;
					rows.Add(new { Time = parts[0], Price = ToMilli(ParseDouble(parts[2])), Number = ParseDouble(parts[5]) });
				}
			}
			return Success(request, JsonSerializer.Serialize(new { data = new { List = rows } }), url, code, "rows=" + rows.Count);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return Failure(request, "{\"data\":{\"List\":[]}}", ex.Message, url, ex, code);
		}
	}

	private async Task<StockDataResult> GetTicksAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		string rawCode = request.Get("code");
		string code = NormalizeCode(rawCode);
		if (code.Length != 6 || !TryResolveDate(request.Get("date"), out var targetDate))
		{
			return Failure(request, "{\"data\":{\"List\":[]}}", "Invalid code or date");
		}
		string secId = ToSecId(rawCode, code);
		string quoteUrl = "https://push2.eastmoney.com/api/qt/stock/get?secid=" + secId + "&fields=f57,f58,f86";
		string detailsUrl = "https://push2.eastmoney.com/api/qt/stock/details/get?secid=" + secId + "&pos=-1000000&iscca=1&fields1=f1,f2,f3,f4,f5&fields2=f51,f52,f53,f54,f55";
		try
		{
			using JsonDocument quoteDocument = JsonDocument.Parse(await SendGetWithRetryAsync(quoteUrl, cancellationToken));
			if (!quoteDocument.RootElement.TryGetProperty("data", out var quoteData) || quoteData.ValueKind != JsonValueKind.Object || !quoteData.TryGetProperty("f86", out var timestampElement) || !timestampElement.TryGetInt64(out long timestamp))
			{
				return Failure(request, "{\"data\":{\"List\":[]}}", "Latest trading date unavailable", quoteUrl, null, code);
			}
			DateTime tradingDate = DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8)).Date;
			if (tradingDate != targetDate.Date)
			{
				return Success(request, "{\"data\":{\"List\":[]}}", quoteUrl, code, "requestedDate=" + targetDate.ToString("yyyyMMdd") + ", latestTradingDate=" + tradingDate.ToString("yyyyMMdd"));
			}
			using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(detailsUrl, cancellationToken));
			var rows = new List<object>();
			if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in details.EnumerateArray())
				{
					string[] parts = (item.GetString() ?? "").Split(',');
					if (parts.Length < 5 || !int.TryParse(parts[4], out int status)) continue;
					rows.Add(new { Time = targetDate.ToString("yyyy-MM-dd") + "T" + parts[0] + "+08:00", Price = ToMilli(ParseDouble(parts[1])), Volume = ParseDouble(parts[2]), Status = status });
				}
			}
			return Success(request, JsonSerializer.Serialize(new { data = new { List = rows } }), detailsUrl, code, "rows=" + rows.Count + ", dateCheckUrl=" + quoteUrl);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return Failure(request, "{\"data\":{\"List\":[]}}", ex.Message, detailsUrl, ex, code);
		}
	}

	private async Task<StockDataResult> SearchAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		string keyword = request.Get("keyword").Trim();
		if (keyword.Length == 0)
		{
			return Failure(request, "{\"code\":0,\"data\":[]}", "Empty keyword");
		}
		string url = "https://searchapi.eastmoney.com/api/suggest/get?type=14&count=20&token=D43BF722C8E33BDC906FB84D85E326E8&input=" + Uri.EscapeDataString(keyword);
		try
		{
			using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(url, cancellationToken));
			if (!document.RootElement.TryGetProperty("QuotationCodeTable", out var table) || !table.TryGetProperty("Data", out var resultData) || resultData.ValueKind != JsonValueKind.Array)
			{
				return Failure(request, "{\"code\":0,\"data\":[]}", "Unexpected search response schema", url);
			}
			var items = new Dictionary<string, string>();
			foreach (JsonElement item in resultData.EnumerateArray())
			{
				string code = NormalizeCode(GetString(item, "Code"));
				string name = GetString(item, "Name");
				string quoteId = GetString(item, "QuoteID");
				string classify = GetString(item, "Classify");
				string securityTypeName = GetString(item, "SecurityTypeName");
				if (code.Length == 6 && !string.IsNullOrWhiteSpace(name) && IsCnExchangeQuote(quoteId) && IsSupportedSearchItem(classify, securityTypeName))
				{
					items[code] = name;
				}
			}
			await _cache.MergeItemsAsync(items, "EastMoneySearch", cancellationToken);
			var rows = items.Select(item => new { code = item.Key, name = item.Value }).ToList();
			return Success(request, JsonSerializer.Serialize(new { code = 0, data = rows }), url, keyword, "rows=" + rows.Count);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return Failure(request, "{\"code\":0,\"data\":[]}", ex.Message, url, ex, keyword);
		}
	}

	private async Task<StockDataResult> SyncCodeTableAsync(StockDataRequest request, string kind, CancellationToken cancellationToken)
	{
		using CancellationTokenSource syncTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		syncTimeout.CancelAfter(TimeSpan.FromSeconds(25));
		CancellationToken syncToken = syncTimeout.Token;
		MarketDefinition[] markets = kind == "etf"
			? new MarketDefinition[] { new("etf-sh", kind, "b:MK0021"), new("etf-sz", kind, "b:MK0022"), new("etf-cross", kind, "b:MK0023"), new("etf-other", kind, "b:MK0024") }
			: new MarketDefinition[] { new("sz-main", kind, "m:0+t:6"), new("sz-gem", kind, "m:0+t:80"), new("sh-main", kind, "m:1+t:2"), new("sh-star", kind, "m:1+t:23") };
		string currentUrl = "https://push2.eastmoney.com/api/qt/clist/get";
		try
		{
			await Task.WhenAll(markets.Select(market => SyncMarketAsync(market, request.ForceRefresh, syncToken)));
			StockNameCacheSnapshot snapshot = await _cache.GetSnapshotAsync(kind == "etf" ? "etf" : null, syncToken);
			var rows = snapshot.Items.OrderBy(item => item.Key).Select(item => new { code = item.Key, name = item.Value }).ToList();
			string json = kind == "etf" ? JsonSerializer.Serialize(new { data = new { list = rows } }) : JsonSerializer.Serialize(new { data = new { codes = rows } });
			return Success(request, json, currentUrl, "-", "rows=" + rows.Count + ", paged=true");
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			string empty = kind == "etf" ? "{\"data\":{\"list\":[]}}" : "{\"data\":{\"codes\":[]}}";
			return Failure(request, empty, ex.Message, currentUrl, ex);
		}
	}

	private async Task SyncMarketAsync(MarketDefinition market, bool forceRefresh, CancellationToken cancellationToken)
	{
		StockMarketCache state = await _cache.GetMarketStateAsync(market.Key, cancellationToken);
		bool stateIsToday = state.UpdatedAt != default && state.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).Date == TimeHelper.BeijingNow.Date;
		if (forceRefresh || !stateIsToday || !string.Equals(state.Filter, market.Filter, StringComparison.Ordinal))
		{
			await _cache.ResetMarketAsync(market.Key, market.Kind, market.Filter, cancellationToken);
			state = await _cache.GetMarketStateAsync(market.Key, cancellationToken);
		}
		if (state.Completed) return;
		for (int page = Math.Max(1, state.NextPage); page <= 100; page++)
		{
			string url = "https://push2.eastmoney.com/api/qt/clist/get?po=1&np=1&fltt=2&invt=2&fid=f12&fields=f12,f14&pn=" + page + "&pz=" + CodePageSize + "&fs=" + Uri.EscapeDataString(market.Filter);
			using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(url, cancellationToken, 2, 5));
			if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("diff", out var diff) || diff.ValueKind != JsonValueKind.Array)
			{
				throw new InvalidOperationException("Unexpected code table response schema for " + market.Key);
			}
			var pageItems = new Dictionary<string, string>();
			foreach (JsonElement item in diff.EnumerateArray())
			{
				string code = NormalizeCode(GetString(item, "f12"));
				string name = GetString(item, "f14");
				if (code.Length == 6 && !string.IsNullOrWhiteSpace(name)) pageItems[code] = name;
			}
			int total = data.TryGetProperty("total", out var totalElement) && totalElement.TryGetInt32(out int parsedTotal) ? parsedTotal : 0;
			bool completed = pageItems.Count < CodePageSize || total > 0 && page * CodePageSize >= total;
			await _cache.MergeMarketPageAsync(market.Key, market.Kind, market.Filter, page + 1, completed, pageItems, cancellationToken);
			if (completed) return;
			await Task.Delay(100, cancellationToken);
		}
		throw new InvalidOperationException("Code table page limit reached for " + market.Key);
	}

	private async Task<StockDataResult> GetWorkdayAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		if (!TryResolveDate(request.Get("date"), out var targetDate))
		{
			return Failure(request, "{\"data\":{\"is_workday\":false,\"previous\":[]}}", "Invalid date");
		}
		string date = targetDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
		string url = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=1.000001&klt=101&fqt=0&end=" + date + "&lmt=1&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56";
		try
		{
			using JsonDocument document = JsonDocument.Parse(await SendGetWithRetryAsync(url, cancellationToken));
			string actual = "";
			if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("klines", out var klines) && klines.ValueKind == JsonValueKind.Array && klines.GetArrayLength() > 0)
			{
				actual = (klines[0].GetString() ?? "").Split(',')[0].Replace("-", "");
			}
			bool isWorkday = actual == date;
			var previous = string.IsNullOrWhiteSpace(actual) ? Array.Empty<object>() : new object[] { new { numeric = actual } };
			return Success(request, JsonSerializer.Serialize(new { data = new { is_workday = isWorkday, previous } }), url, "000001", "target=" + date + ", actual=" + actual);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			return Failure(request, "{\"data\":{\"is_workday\":false,\"previous\":[]}}", ex.Message, url, ex, "000001");
		}
	}

	private async Task<string> SendGetWithRetryAsync(string url, CancellationToken cancellationToken, int maxAttempts = 2, int timeoutSeconds = 5)
	{
		Exception lastException = null;
		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			try
			{
				await _requestThrottle.WaitAsync(cancellationToken);
				try
				{
					using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
					request.Version = HttpVersion.Version11;
					request.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
					using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
					using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token);
					response.EnsureSuccessStatusCode();
					return await response.Content.ReadAsStringAsync(timeout.Token);
				}
				finally
				{
					_requestThrottle.Release();
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
			{
				lastException = ex;
				if (attempt < maxAttempts) await Task.Delay(300 * attempt * attempt, cancellationToken);
			}
		}
		throw lastException ?? new HttpRequestException("Public source request failed");
	}

	private static object[] BuildLevels(JsonElement item, string[] priceFields, string[] volumeFields)
	{
		var levels = new List<object>();
		for (int index = 0; index < priceFields.Length; index++)
		{
			double price = GetDouble(item, priceFields[index]);
			double volume = GetDouble(item, volumeFields[index]);
			levels.Add(new { Price = ToMilli(price), Number = volume, Available = price > 0 || volume > 0 });
		}
		return levels.ToArray();
	}

	private static object[] BuildUnavailableLevels()
	{
		return Enumerable.Range(0, 5).Select(_ => new { Price = 0, Number = 0.0, Available = false }).ToArray<object>();
	}

	private static List<KlineRow> ParseKlineRows(string json, int limit)
	{
		using JsonDocument document = JsonDocument.Parse(json);
		if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("klines", out var klines) || klines.ValueKind != JsonValueKind.Array)
		{
			return new List<KlineRow>();
		}
		var rows = new List<KlineRow>();
		foreach (JsonElement item in klines.EnumerateArray())
		{
			string[] parts = (item.GetString() ?? "").Split(',');
			if (parts.Length < 6) continue;
			rows.Add(new KlineRow
			{
				Time = parts[0],
				Open = ToMilli(ParseDouble(parts[1])),
				Close = ToMilli(ParseDouble(parts[2])),
				High = ToMilli(ParseDouble(parts[3])),
				Low = ToMilli(ParseDouble(parts[4])),
				Volume = ParseDouble(parts[5])
			});
		}
		return rows.Count > limit ? rows.TakeLast(limit).ToList() : rows;
	}

	private static StockDataResult Success(StockDataRequest request, string json, string url, string code, string note)
	{
		StockDataLog.Write(request.Path, code, url, null, false, note);
		return new StockDataResult { Endpoint = request.Endpoint, Handled = true, Success = true, Json = json, Source = "EastMoney" };
	}

	private static StockDataResult Failure(StockDataRequest request, string json, string error, string url = "-", Exception exception = null, string code = "-")
	{
		StockDataLog.Write(request.Path, code, url, exception, false, error);
		return new StockDataResult { Endpoint = request.Endpoint, Handled = true, Success = false, Json = json, Source = "EastMoney", Error = error };
	}

	private static string NormalizeCode(string code) => (code ?? "").Trim().ToLowerInvariant().Replace("sh", "").Replace("sz", "").Replace("bj", "");

	private static string ToSecId(string code) => (code.StartsWith("6") || code.StartsWith("5") ? "1." : "0.") + code;

	private static string ToSecId(string rawCode, string code)
	{
		string raw = (rawCode ?? "").Trim().ToLowerInvariant();
		if (raw.StartsWith("sh")) return "1." + code;
		if (raw.StartsWith("sz") || raw.StartsWith("bj")) return "0." + code;
		return ToSecId(code);
	}

	private static bool TryResolveDate(string value, out DateTime date)
	{
		if (string.IsNullOrWhiteSpace(value)) { date = TimeHelper.BeijingNow.Date; return true; }
		return DateTime.TryParseExact(value.Trim(), new string[2] { "yyyyMMdd", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
	}

	private static double ParseDouble(string value) => double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double result) ? result : 0;

	private static double GetDouble(JsonElement item, string name)
	{
		if (!item.TryGetProperty(name, out var value)) return 0;
		if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
		return value.ValueKind == JsonValueKind.String ? ParseDouble(value.GetString()) : 0;
	}

	private static double GetFirstPositive(JsonElement item, params string[] names)
	{
		foreach (string name in names)
		{
			double value = GetDouble(item, name);
			if (value > 0) return value;
		}
		return 0;
	}

	private static double GetQuoteAmount(JsonElement item)
	{
		double amount = GetDouble(item, "f48");
		if (amount > 10000) return amount;
		amount = GetDouble(item, "f6");
		if (amount > 10000) return amount;
		double volumeHands = GetDouble(item, "f5");
		double price = GetDouble(item, "f2");
		return volumeHands > 0 && price > 0 ? volumeHands * price * 100 : 0;
	}

	private static string GetString(JsonElement item, string name)
	{
		if (!item.TryGetProperty(name, out var value)) return "";
		return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
	}

	private static long ToMilli(double value) => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? 0 : (long)Math.Round(value * 1000);

	private static bool IsCnExchangeQuote(string quoteId) => quoteId.StartsWith("0.", StringComparison.Ordinal) || quoteId.StartsWith("1.", StringComparison.Ordinal);

	private static bool IsSupportedSearchItem(string classify, string securityTypeName)
	{
		return classify.Equals("AStock", StringComparison.OrdinalIgnoreCase) || classify.Equals("Fund", StringComparison.OrdinalIgnoreCase) || securityTypeName == "沪A" || securityTypeName == "深A" || securityTypeName == "基金";
	}

	private sealed record MarketDefinition(string Key, string Kind, string Filter);

	private sealed class KlineRow
	{
		public string Time { get; init; } = "";
		public long Open { get; init; }
		public long Close { get; init; }
		public long High { get; init; }
		public long Low { get; init; }
		public double Volume { get; init; }
	}

    public void Dispose()
    {
        _requestThrottle?.Dispose();
    }
}