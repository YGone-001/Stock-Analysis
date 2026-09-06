using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;

#pragma warning disable CS8600, CS8603
#pragma warning disable CS8600, CS8603
namespace AIHelper.Services.StockData;

public sealed class StockDataDiagnostics
{
	public async Task<IReadOnlyList<StockDataDiagnosticItem>> RunAsync(string sampleCode = "600519", CancellationToken cancellationToken = default)
	{
		var results = new List<StockDataDiagnosticItem>();
		StockNameCacheSnapshot cache = await NetworkHelper.GetStockNameCacheSnapshotAsync(cancellationToken);
		results.Add(new StockDataDiagnosticItem("代码表缓存", cache.Exists, cache.Exists ? cache.Items.Count + " 条；版本 2；来源 " + cache.Source + (cache.IsStale ? "；已过期" : "；有效") : "缓存为空或不可读"));

		StockDataResult search = await NetworkHelper.GetDataResultAsync("/api/search?keyword=" + Uri.EscapeDataString(sampleCode), cancellationToken);
		results.Add(CreateArrayResult("股票搜索", search, "data"));

		StockDataResult quote = await NetworkHelper.GetDataResultAsync("/api/quote?code=" + sampleCode, cancellationToken);
		
		int quoteRows = 0;
		bool hasFiveLevels = false;
		bool hasDepthValues = false;
		StockDataDiagnosticItem sparrowQuoteResult = new StockDataDiagnosticItem("麻雀选股盘口字段", false, BuildMessage(quote, 0) + "；JSON 解析失败");
		try
		{
			using JsonDocument document = JsonDocument.Parse(quote.Json);
			if (document.RootElement.TryGetProperty("data", out JsonElement dataElement) && dataElement.ValueKind == JsonValueKind.Array)
			{
				quoteRows = dataElement.GetArrayLength();
				if (quoteRows > 0)
				{
					JsonElement first = dataElement[0];
					JsonElement sl = default;
					hasFiveLevels = first.TryGetProperty("BuyLevel", out var bl) && bl.ValueKind == JsonValueKind.Array && bl.GetArrayLength() == 5 &&
									first.TryGetProperty("SellLevel", out sl) && sl.ValueKind == JsonValueKind.Array && sl.GetArrayLength() == 5 &&
									first.TryGetProperty("TotalHand", out _);
					if (hasFiveLevels)
					{
						hasDepthValues = bl.EnumerateArray().Concat(sl.EnumerateArray()).Any(level => level.TryGetProperty("Available", out var available) && available.ValueKind == JsonValueKind.True);
					}
					
					double? amount = GetNullableNumber(first, "Amount");
					double? outer = GetNullableNumber(first, "OuterVolume") ?? GetNullableNumber(first, "Wp");
					double? inner = GetNullableNumber(first, "InnerVolume") ?? GetNullableNumber(first, "Np");
					double? turnover = GetNullableNumber(first, "Turnover");
					bool ok = quote.Success && amount > 0 && outer.HasValue && inner.HasValue;
					string message = BuildMessage(quote, 1)
						+ "；成交额 " + FormatNullable(amount, "F0")
						+ "；外盘 " + FormatNullable(outer, "F0")
						+ "；内盘 " + FormatNullable(inner, "F0")
						+ "；换手 " + FormatNullable(turnover, "F2")
						+ ((!outer.HasValue || !inner.HasValue) ? "；OuterInnerUnavailable" : "");
					sparrowQuoteResult = new StockDataDiagnosticItem("麻雀选股盘口字段", ok, message);
				}
				else
				{
				    sparrowQuoteResult = new StockDataDiagnosticItem("麻雀选股盘口字段", false, BuildMessage(quote, 0) + "；缺少数据节点");
				}
			}
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			sparrowQuoteResult = new StockDataDiagnosticItem("麻雀选股盘口字段", false, BuildMessage(quote, 0) + "；解析或字段缺失");
		}
		results.Add(new StockDataDiagnosticItem("实时行情/五档", quote.Success && quoteRows > 0 && hasFiveLevels, BuildMessage(quote, quoteRows) + (hasFiveLevels ? (hasDepthValues ? "；买卖五档完整且有值" : "；买卖五档结构完整，当前时段无档位值") : "；买卖五档不完整")));
		results.Add(sparrowQuoteResult);

		StockDataResult kline = await NetworkHelper.GetDataResultAsync("/api/kline-all?code=" + sampleCode + "&type=day&limit=5", cancellationToken);
		results.Add(CreateArrayResult("日 K", kline, "data"));
		string tradingDate = GetLatestKlineDate(kline.Json) ?? TimeHelper.BeijingNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

		StockDataResult minute = await NetworkHelper.GetDataResultAsync("/api/minute?code=" + sampleCode + "&date=" + tradingDate, cancellationToken);
		results.Add(CreateNestedArrayResult("分时", minute, "data", "List"));

		StockDataResult ticks = await NetworkHelper.GetDataResultAsync("/api/minute-trade-all?code=" + sampleCode + "&date=" + tradingDate, cancellationToken);
		results.Add(CreateNestedArrayResult("逐笔", ticks, "data", "List"));
		return results;
	}

	private static StockDataDiagnosticItem CreateArrayResult(string name, StockDataResult result, string property)
	{
		int count = CountArray(result.Json, property);
		return new StockDataDiagnosticItem(name, result.Success && count > 0, BuildMessage(result, count));
	}

	private static StockDataDiagnosticItem CreateNestedArrayResult(string name, StockDataResult result, string parent, string property)
	{
		int count = CountArray(result.Json, parent, property);
		return new StockDataDiagnosticItem(name, result.Success && count > 0, BuildMessage(result, count));
	}

	private static double? GetNullableNumber(JsonElement item, string property)
	{
		return item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
			? value.GetDouble()
			: null;
	}

	private static string FormatNullable(double? value, string format)
	{
		return value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "unavailable";
	}

	private static string BuildMessage(StockDataResult result, int count)
	{
		string message = count + " 条；来源 " + result.Source;
		if (result.UsedCache) message += "；本地缓存";
		if (!string.IsNullOrWhiteSpace(result.Error)) message += "；" + result.Error;
		return message;
	}

	private static int CountArray(string json, params string[] path)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			JsonElement value = document.RootElement;
			foreach (string property in path)
			{
				if (!value.TryGetProperty(property, out value)) return 0;
			}
			return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return 0;
		}
	}

	private static string GetLatestKlineDate(string json)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(json);
			JsonElement data = document.RootElement.GetProperty("data");
			if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) return null;
			string value = data.EnumerateArray().Last().GetProperty("Time").GetString();
			return DateTime.TryParse(value, out var date) ? date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : null;
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return null;
		}
	}
}

public sealed record StockDataDiagnosticItem(string Name, bool Success, string Message);
