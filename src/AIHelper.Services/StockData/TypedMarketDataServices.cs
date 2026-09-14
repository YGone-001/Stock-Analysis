using System.Globalization;
using System.Text.Json;
using Serilog;

namespace AIHelper.Services.StockData;

public sealed class QuoteService : IQuoteService
{
	private readonly IStockDataProvider _transport;

	public QuoteService(IStockDataProvider transport)
	{
		_transport = transport;
	}

	public async Task<MarketDataResult<QuoteSnapshot?>> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default)
	{
		MarketDataResult<IReadOnlyList<QuoteSnapshot>> result = await GetQuotesAsync(new[] { symbol }, false, cancellationToken);
		QuoteSnapshot? quote = result.Data.FirstOrDefault(item => string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
		return new MarketDataResult<QuoteSnapshot?>(quote, result.Metadata, result.Error);
	}

	public async Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
		IReadOnlyCollection<string> symbols,
		bool forceRefresh = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(symbols);
		string[] normalized = symbols.Where(symbol => !string.IsNullOrWhiteSpace(symbol))
			.Select(symbol => symbol.Trim())
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (normalized.Length == 0)
		{
			return new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(
				Array.Empty<QuoteSnapshot>(), new MarketDataMetadata("", false, false, false));
		}

		string endpoint = "/api/quote?code=" + string.Join(',', normalized) + (forceRefresh ? "&refresh=1" : "");
		StockDataResult response = await _transport.GetDataAsync(StockDataRequest.Parse(endpoint), cancellationToken);
		MarketDataMetadata metadata = ToMetadata(response);
		if (!response.Success)
		{
			return new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(Array.Empty<QuoteSnapshot>(), metadata, response.Error);
		}

		return new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(
			MarketDataContractParser.ParseQuotes(response.Json, response), metadata);
	}

	public async Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetAllQuotesAsync(
		bool forceRefresh = false,
		CancellationToken cancellationToken = default)
	{
		string endpoint = "/api/quote-all" + (forceRefresh ? "?refresh=1" : "");
		StockDataResult response = await _transport.GetDataAsync(StockDataRequest.Parse(endpoint), cancellationToken);
		MarketDataMetadata metadata = ToMetadata(response);
		if (!response.Success)
		{
			return new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(Array.Empty<QuoteSnapshot>(), metadata, response.Error);
		}

		return new MarketDataResult<IReadOnlyList<QuoteSnapshot>>(
			MarketDataContractParser.ParseQuotes(response.Json, response), metadata);
	}

	private static MarketDataMetadata ToMetadata(StockDataResult result) =>
		new(result.Source, result.UsedCache, result.IsStale, result.IsBackgroundRefresh);
}

public sealed class KlineService : IKlineService
{
	private readonly IStockDataProvider _transport;

	public KlineService(IStockDataProvider transport)
	{
		_transport = transport;
	}

	public async Task<MarketDataResult<KlineSeries?>> GetDailyAsync(
		string symbol,
		int limit,
		bool forceRefresh = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
		ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
		string endpoint = $"/api/kline-all?code={Uri.EscapeDataString(symbol.Trim())}&type=day&limit={limit}" + (forceRefresh ? "&refresh=1" : "");
		StockDataResult response = await _transport.GetDataAsync(StockDataRequest.Parse(endpoint), cancellationToken);
		MarketDataMetadata metadata = new(response.Source, response.UsedCache, response.IsStale, response.IsBackgroundRefresh);
		if (!response.Success)
		{
			return new MarketDataResult<KlineSeries?>(null, metadata, response.Error);
		}

		return new MarketDataResult<KlineSeries?>(
			MarketDataContractParser.ParseKlineSeries(symbol.Trim(), response.Json, response), metadata);
	}
}

public sealed class MarketCalendarService : IMarketCalendarService
{
	private readonly IStockDataProvider _transport;

	public MarketCalendarService(IStockDataProvider transport)
	{
		_transport = transport;
	}

	public async Task<MarketDataResult<TradingDayResult?>> GetTradingDayAsync(DateTime date, CancellationToken cancellationToken = default)
	{
		string endpoint = $"/api/workday?date={date:yyyyMMdd}";
		StockDataResult response = await _transport.GetDataAsync(StockDataRequest.Parse(endpoint), cancellationToken);
		MarketDataMetadata metadata = new(response.Source, response.UsedCache, response.IsStale, response.IsBackgroundRefresh);
		if (!response.Success)
		{
			return new MarketDataResult<TradingDayResult?>(null, metadata, response.Error);
		}

		return new MarketDataResult<TradingDayResult?>(
			MarketDataContractParser.ParseTradingDay(date, response.Json, response), metadata);
	}
}

internal static class MarketDataContractParser
{
	public static IReadOnlyList<QuoteSnapshot> ParseQuotes(string json, StockDataResult transport)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (!TryGetPropertyIgnoreCase(document.RootElement, "data", out JsonElement data)
				|| data.ValueKind != JsonValueKind.Array)
			{
				throw Invalid("Quote", transport, "Expected a data array.");
			}

			var quotes = new List<QuoteSnapshot>();
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
				{
					throw Invalid("Quote", transport, "Quote data contains a non-object item.");
				}
				string symbol = ReadString(item, "Code");
				if (string.IsNullOrWhiteSpace(symbol))
				{
					throw Invalid("Quote", transport, "Quote data is missing Code.");
				}
				TryGetPropertyIgnoreCase(item, "K", out JsonElement prices);
				quotes.Add(new QuoteSnapshot(
					symbol,
					ReadString(item, "Name"),
					ReadNullable(item, "Price") ?? DivideMilli(ReadNullable(prices, "Close")),
					ReadNullable(item, "PreClose") ?? DivideMilli(ReadNullable(prices, "Last")) ?? DivideMilli(ReadNullable(prices, "PreClose")),
					ReadNullable(item, "Percent"),
					ReadNullable(item, "Volume") ?? ReadNullable(item, "TotalHand"),
					ReadNullable(item, "Amount") ?? ReadNullable(item, "TotalAmount"),
					ReadNullable(item, "Turnover"),
					ReadNullable(item, "OuterVolume") ?? ReadNullable(item, "Wp"),
					ReadNullable(item, "InnerVolume") ?? ReadNullable(item, "Np"),
					DivideMilli(ReadNullable(prices, "Open")),
					DivideMilli(ReadNullable(prices, "High")),
					DivideMilli(ReadNullable(prices, "Low"))));
			}
			return quotes;
		}
		catch (JsonException ex)
		{
			throw Invalid("Quote", transport, "Malformed JSON.", ex);
		}
	}

	public static KlineSeries ParseKlineSeries(string symbol, string json, StockDataResult transport)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			JsonElement data = UnwrapKlineData(document.RootElement);
			if (data.ValueKind != JsonValueKind.Array)
			{
				throw Invalid("Kline", transport, "Expected a data/list/klines array.");
			}

			var bars = new List<KlineBar>();
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (!TryReadDate(item, out DateTime date))
				{
					throw Invalid("Kline", transport, "A K-line bar has an invalid or missing trading date.");
				}
				bars.Add(new KlineBar(
					date,
					DivideMilli(ReadNullable(item, "Open")),
					DivideMilli(ReadNullable(item, "High")),
					DivideMilli(ReadNullable(item, "Low")),
					DivideMilli(ReadNullable(item, "Close")),
					ReadNullable(item, "Volume"),
					ReadNullable(item, "Amount"),
					ReadNullable(item, "Percent") ?? ReadNullable(item, "ChangePercent"),
					ReadNullable(item, "Change"),
					ReadNullable(item, "Turnover") ?? ReadNullable(item, "TurnoverRate")));
			}
			return new KlineSeries(symbol, bars);
		}
		catch (JsonException ex)
		{
			throw Invalid("Kline", transport, "Malformed JSON.", ex);
		}
	}

	public static TradingDayResult ParseTradingDay(DateTime requestedDate, string json, StockDataResult transport)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			if (!TryGetPropertyIgnoreCase(document.RootElement, "data", out JsonElement data)
				|| data.ValueKind != JsonValueKind.Object
				|| !TryGetPropertyIgnoreCase(data, "is_workday", out JsonElement isWorkday)
				|| isWorkday.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
			{
				throw Invalid("Trading calendar", transport, "Expected data.is_workday.");
			}

			DateTime previous = requestedDate;
			if (!isWorkday.GetBoolean()
				&& TryGetPropertyIgnoreCase(data, "previous", out JsonElement previousEntries)
				&& previousEntries.ValueKind == JsonValueKind.Array
				&& previousEntries.GetArrayLength() > 0
				&& TryGetPropertyIgnoreCase(previousEntries[0], "numeric", out JsonElement numeric)
				&& DateTime.TryParseExact(numeric.GetString(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
			{
				previous = parsed;
			}
			return new TradingDayResult(requestedDate, isWorkday.GetBoolean(), previous);
		}
		catch (JsonException ex)
		{
			throw Invalid("Trading calendar", transport, "Malformed JSON.", ex);
		}
	}

	private static MarketDataContractException Invalid(
		string contract,
		StockDataResult transport,
		string message,
		Exception? innerException = null)
	{
		Log.Warning(innerException,
			"Market-data contract parsing failed. Contract={Contract} Endpoint={Endpoint} Source={Source} Detail={Detail}",
			contract, transport.Endpoint, transport.Source, message);
		return new MarketDataContractException(contract, transport.Endpoint, transport.Source, message, innerException);
	}

	private static JsonElement UnwrapKlineData(JsonElement root)
	{
		if (root.ValueKind == JsonValueKind.Array) return root;
		if (!TryGetPropertyIgnoreCase(root, "data", out JsonElement data)) return default;
		if (data.ValueKind == JsonValueKind.Array) return data;
		if (data.ValueKind != JsonValueKind.Object) return default;
		return TryGetPropertyIgnoreCase(data, "list", out JsonElement list) ? list
			: TryGetPropertyIgnoreCase(data, "klines", out JsonElement klines) ? klines
			: default;
	}

	private static bool TryReadDate(JsonElement item, out DateTime date)
	{
		date = default;
		if (!TryGetPropertyIgnoreCase(item, "Date", out JsonElement value)
			&& !TryGetPropertyIgnoreCase(item, "Time", out value)) return false;
		return value.ValueKind == JsonValueKind.String
			&& DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
	}

	private static double? ReadNullable(JsonElement element, string name)
	{
		if (element.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(element, name, out JsonElement value)
			|| value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)) return double.IsFinite(number) ? number : null;
		return value.ValueKind == JsonValueKind.String
			&& double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)
			&& double.IsFinite(number) ? number : null;
	}

	private static double? DivideMilli(double? value) => value.HasValue ? value.Value / 1000.0 : null;

	private static string ReadString(JsonElement element, string name) =>
		TryGetPropertyIgnoreCase(element, name, out JsonElement value)
			? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
			: "";

	private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					value = property.Value;
					return true;
				}
			}
		}
		value = default;
		return false;
	}
}
