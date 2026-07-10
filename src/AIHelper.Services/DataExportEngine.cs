using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;
using AIHelper.Models;

#pragma warning disable CS8600, CS8602, CS8604
#pragma warning disable CS8600, CS8602, CS8604
namespace AIHelper.Services;

public static class DataExportEngine
{
	private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
	private static readonly SemaphoreSlim _apiSemaphore = new SemaphoreSlim(3, 3);

	public static async Task ExecuteExportAsync(ExportConfig config, Action<string> logCallback, CancellationToken ct = default)
	{
		string exportDir = ConfigManager.Load().DataSavePath;
		if (string.IsNullOrWhiteSpace(exportDir))
		{
			exportDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
		}
		if (!Directory.Exists(exportDir))
		{
			Directory.CreateDirectory(exportDir);
		}
		logCallback("⏳ 开始初始化取数引擎...");
		DateTime actualEndDate = await GetActualTradingDateAsync(config.TargetDate, ct);
		if (actualEndDate.Date != config.TargetDate.Date)
		{
			logCallback($"⚠\ufe0f 选定日期 {config.TargetDate:yyyy-MM-dd} 非交易日或无数据，已自动回推至: {actualEndDate:yyyy-MM-dd}");
		}
		string dateStr = actualEndDate.ToString("yyyyMMdd");
		string randomSuffix = new Random().Next(1000, 9999).ToString();
		int num = (config.FetchQuote ? 1 : 0) + (config.FetchMinute ? 1 : 0) + (config.FetchTick ? 1 : 0) + (config.FetchKline ? 1 : 0);
		string prefix = "数据提取";
		if (num > 1)
		{
			prefix = "复合取数";
		}
		else if (config.FetchQuote)
		{
			prefix = "五档盘口";
		}
		else if (config.FetchMinute)
		{
			prefix = "分时走势";
		}
		else if (config.FetchTick)
		{
			prefix = "逐笔明细";
		}
		else if (config.FetchKline)
		{
			prefix = "历史K线";
		}
		else if (config.SelectedIndices.Any())
		{
			prefix = "大盘指数";
		}
		string indexDataBlock = "";
		if (config.SelectedIndices.Any())
		{
			logCallback("\ud83d\udcc8 开始预拉取大盘指数参照数据...");
			StringBuilder idxSb = new StringBuilder();
			idxSb.AppendLine("\n=======================================================");
			StringBuilder stringBuilder = idxSb;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(25, 2, stringBuilder);
			handler.AppendLiteral("【大盘指数对照区间】:最近 ");
			handler.AppendFormatted(config.IndexDays);
			handler.AppendLiteral(" 个交易日 (截至 ");
			handler.AppendFormatted(dateStr);
			handler.AppendLiteral(")");
			stringBuilder.AppendLine(ref handler);
			idxSb.AppendLine("=======================================================\n");
			int totalIndices = config.SelectedIndices.Count;
			int currentIndex = 0;
			foreach (string selectedIndex in config.SelectedIndices)
			{
				currentIndex++;
				logCallback($"⬇\ufe0f 正在预拉取指数 [{selectedIndex}]... (进度: {currentIndex}/{totalIndices})");
				StringBuilder stringBuilder2 = idxSb;
				stringBuilder2.AppendLine(await FetchIndexDataStringAsync(selectedIndex, actualEndDate, config.IndexDays, config.EnableAiCompression, ct));
			}
			indexDataBlock = idxSb.ToString();
		}
		StreamWriter mergedWriter = null;
		try
		{
			if (config.IsSingleFileMode)
			{
			string mergedFileName = $"{prefix}_{config.TabName}_{dateStr}_{randomSuffix}.txt";
			mergedWriter = new StreamWriter(Path.Combine(exportDir, mergedFileName), append: false, Encoding.UTF8);
			await WriteFileHeaderAsync(mergedWriter, config, actualEndDate, ct);
			logCallback("\ud83d\udcc4 采用聚合模式，输出文件: " + mergedFileName);
		}
		int totalStocks = config.SelectedStocks.Count;
		int currentStock = 0;
		foreach (var stock in config.SelectedStocks)
		{
			ct.ThrowIfCancellationRequested();
			currentStock++;
			logCallback($"⬇\ufe0f 正在拉取 [{stock.Name} - {stock.Code}] 的数据... (进度: {currentStock}/{totalStocks})");
			StreamWriter writer = mergedWriter;
			bool isPerStockWriter = !config.IsSingleFileMode;
			try
			{
				if (isPerStockWriter)
				{
					string path = $"{prefix}_{stock.Code}_{dateStr}_{randomSuffix}.txt";
					writer = new StreamWriter(Path.Combine(exportDir, path), append: false, Encoding.UTF8);
					await WriteFileHeaderAsync(writer, config, actualEndDate, ct);
				}
			await writer.WriteLineAsync("\n=======================================================");
			await writer.WriteLineAsync($"【当前标的】:{stock.Name}(代码:{stock.Code})");
			if (config.IncludeHoldingPrompt)
			{
				HoldingInfo holding = HoldingsManager.GetHolding(stock.Code);
				if (holding == null || holding.Volume <= 0)
				{
					await writer.WriteLineAsync("【我的实际持仓】: 当前空仓 (0股)。");
					await writer.WriteLineAsync("【系统强制指令】: AI 请评估该股当前的安全性，并直接告诉我是否建议建仓，以及具体的入场点位防守线！");
				}
				else
				{
					await writer.WriteLineAsync($"【我的实际持仓】: 当前持有数量 {holding.Volume} 股，买入成本价 {holding.CostPrice:F3} 元。");
					await writer.WriteLineAsync("【系统强制指令】: AI 请务必结合我的实际持仓成本与数量，测算盈亏比例。并据此给出极具针对性的【加仓、减仓、割肉止损、或落袋为安】的操作建议！并给出理论支撑。");
				}
			}
			await writer.WriteLineAsync("=======================================================\n");
			if (config.FetchQuote)
			{
				await FetchAndWriteQuoteAsync(writer, stock.Code, config.EnableAiCompression, ct);
			}
			if (config.FetchMinute)
			{
				await FetchAndWriteMinuteAsync(writer, stock.Code, dateStr, config.EnableAiCompression, ct);
			}
			if (config.FetchTick)
			{
				await FetchAndWriteTickAsync(writer, stock.Code, dateStr, config.EnableAiCompression, ct);
			}
			if (config.FetchKline)
			{
				await FetchAndWriteKlineAsync(writer, stock.Code, dateStr, config.KlineDays, config.EnableAiCompression, ct);
			}
			if (isPerStockWriter && !string.IsNullOrEmpty(indexDataBlock))
			{
				await writer.WriteAsync(indexDataBlock);
			}
			}
			finally
			{
				if (isPerStockWriter && writer != null)
				{
					writer.Dispose();
				}
			}
		}
		if (config.IsSingleFileMode && mergedWriter != null && !string.IsNullOrEmpty(indexDataBlock))
		{
			await mergedWriter.WriteAsync(indexDataBlock);
		}
		logCallback("✅ 所有数据拉取与 AI 语料预处理完成！文件已存入 " + exportDir + " 目录。");
		}
		finally
		{
			if (config.IsSingleFileMode && mergedWriter != null)
			{
				mergedWriter.Dispose();
			}
		}
	}

	private static async Task FetchAndWriteQuoteAsync(StreamWriter writer, string code, bool aiCompress, CancellationToken ct = default)
	{
		await _apiSemaphore.WaitAsync(ct);
		try
		{
			string url = "/api/quote?code=" + code;
			string json = null;
			Exception lastEx = null;
			for (int i = 1; i <= 3; i++)
			{
				try
				{
					json = await NetworkHelper.GetDataAsync(url, ct);
					if (string.IsNullOrWhiteSpace(json))
					{
						throw new Exception("接口返回空值");
					}
					using (JsonDocument.Parse(json))
					{
						lastEx = null;
					}
				}
				catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
					lastEx = ex;
					if (i < 3)
					{
						await Task.Delay(1000, ct);
					}
					continue;
				}
				break;
			}
			if (lastEx != null)
			{
				await writer.WriteLineAsync("⚠\ufe0f取数异常: [五档实时盘口]网络请求尝试3次后失败。原因:" + lastEx.Message);
				return;
			}
			await writer.WriteLineAsync("---以下是[五档实时盘口]数据(注意:此为取数时刻快照,非历史收盘态)---");
			if (!aiCompress)
			{
				await writer.WriteLineAsync(MinifyJson(json));
				return;
			}
			using JsonDocument doc = JsonDocument.Parse(json);
			if (!TryGetArrayNode(doc.RootElement, out var arrayNode) || arrayNode.GetArrayLength() == 0)
			{
				return;
			}
			JsonElement jsonElement = arrayNode[0];
			double value = jsonElement.GetProperty("K").GetProperty("Close").GetDouble() / 1000.0;
			int value2 = (int)jsonElement.GetProperty("TotalHand").GetDouble();
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(19, 2, stringBuilder2);
			handler.AppendLiteral("[实时基础]:最新价=");
			handler.AppendFormatted(value, "F2");
			handler.AppendLiteral("元,总成交量=");
			handler.AppendFormatted(value2);
			handler.AppendLiteral("手");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder.AppendLine("[买五档]:");
			foreach (JsonElement item in jsonElement.GetProperty("BuyLevel").EnumerateArray())
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(9, 2, stringBuilder2);
				handler.AppendLiteral("价格:");
				handler.AppendFormatted(item.GetProperty("Price").GetDouble() / 1000.0, "F2");
				handler.AppendLiteral("元,挂单:");
				handler.AppendFormatted((int)item.GetProperty("Number").GetDouble());
				handler.AppendLiteral("手");
				stringBuilder4.AppendLine(ref handler);
			}
			stringBuilder.AppendLine("[卖五档]:");
			foreach (JsonElement item2 in jsonElement.GetProperty("SellLevel").EnumerateArray())
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder5 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(9, 2, stringBuilder2);
				handler.AppendLiteral("价格:");
				handler.AppendFormatted(item2.GetProperty("Price").GetDouble() / 1000.0, "F2");
				handler.AppendLiteral("元,挂单:");
				handler.AppendFormatted((int)item2.GetProperty("Number").GetDouble());
				handler.AppendLiteral("手");
				stringBuilder5.AppendLine(ref handler);
			}
			await writer.WriteLineAsync(stringBuilder.ToString());
		}
		catch (Exception ex2) { Serilog.Log.Warning(ex2, "捕获到未处理异常"); 
			await writer.WriteLineAsync("⚠\ufe0f取数异常: 拉取五档失败内部逻辑错误:" + ex2.Message);
		}
		finally
		{
			_apiSemaphore.Release();
		}
	}

	private static async Task FetchAndWriteMinuteAsync(StreamWriter writer, string code, string dateStr, bool aiCompress, CancellationToken ct = default)
	{
		await _apiSemaphore.WaitAsync(ct);
		try
		{
			string text = TimeHelper.BeijingNow.ToString("yyyyMMdd");
			string url = "/api/minute?code=" + code;
			if (dateStr != text)
			{
				url = url + "&date=" + dateStr;
			}
			string json = null;
			Exception lastEx = null;
			for (int i = 1; i <= 3; i++)
			{
				try
				{
					json = await NetworkHelper.GetDataAsync(url, ct);
					if (string.IsNullOrWhiteSpace(json))
					{
						throw new Exception("接口返回空值");
					}
					using (JsonDocument.Parse(json))
					{
						lastEx = null;
					}
				}
				catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
					lastEx = ex;
					if (i < 3)
					{
						await Task.Delay(1000, ct);
					}
					continue;
				}
				break;
			}
			if (lastEx != null)
			{
				await writer.WriteLineAsync("⚠\ufe0f取数异常: [分时走势]网络请求尝试3次后失败。原因:" + lastEx.Message);
				return;
			}
			await writer.WriteLineAsync("---以下是[分时走势]数据---");
			if (!aiCompress)
			{
				await writer.WriteLineAsync(MinifyJson(json));
				return;
			}
			using JsonDocument doc = JsonDocument.Parse(json);
			if (!TryGetPropertyIgnoreCase(doc.RootElement, "data", out var value) || !TryGetPropertyIgnoreCase(value, "List", out var value2))
			{
				return;
			}
			if (value2.ValueKind == JsonValueKind.Null || value2.ValueKind != JsonValueKind.Array)
			{
				await writer.WriteLineAsync("⚠\ufe0f当前日期无分时数据 (可能为非交易日或标的无成交)");
				return;
			}
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("[Header]:Time,Price(元),Volume(手)");
			foreach (JsonElement item in value2.EnumerateArray())
			{
				string @string = item.GetProperty("Time").GetString();
				double value3 = item.GetProperty("Price").GetDouble() / 1000.0;
				int value4 = (int)item.GetProperty("Number").GetDouble();
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(2, 3, stringBuilder2);
				handler.AppendFormatted(@string);
				handler.AppendLiteral(",");
				handler.AppendFormatted(value3, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value4);
				stringBuilder2.AppendLine(ref handler);
			}
			await writer.WriteLineAsync(stringBuilder.ToString());
		}
		catch (Exception ex2) { Serilog.Log.Warning(ex2, "捕获到未处理异常"); 
			await writer.WriteLineAsync("⚠\ufe0f取数异常: 拉取分时内部逻辑错误:" + ex2.Message);
		}
		finally
		{
			_apiSemaphore.Release();
		}
	}

	private static async Task FetchAndWriteTickAsync(StreamWriter writer, string code, string dateStr, bool aiCompress, CancellationToken ct = default)
	{
		await _apiSemaphore.WaitAsync(ct);
		try
		{
			string text = TimeHelper.BeijingNow.ToString("yyyyMMdd");
			string url = "/api/minute-trade-all?code=" + code;
			if (dateStr != text)
			{
				url = url + "&date=" + dateStr;
			}
			string json = null;
			Exception lastEx = null;
			for (int i = 1; i <= 3; i++)
			{
				try
				{
					json = await NetworkHelper.GetDataAsync(url, ct);
					if (string.IsNullOrWhiteSpace(json))
					{
						throw new Exception("接口返回空值");
					}
					using (JsonDocument.Parse(json))
					{
						lastEx = null;
					}
				}
				catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
					lastEx = ex;
					if (i < 3)
					{
						await Task.Delay(1000, ct);
					}
					continue;
				}
				break;
			}
			if (lastEx != null)
			{
				await writer.WriteLineAsync("⚠\ufe0f取数异常: [全天逐笔明细]网络请求尝试3次后失败。原因:" + lastEx.Message);
				return;
			}
			await writer.WriteLineAsync("---以下是[全天逐笔明细(Tick)]数据---");
			if (!aiCompress)
			{
				await writer.WriteLineAsync(MinifyJson(json));
				return;
			}
			using JsonDocument doc = JsonDocument.Parse(json);
			if (!TryGetPropertyIgnoreCase(doc.RootElement, "data", out var value) || !TryGetPropertyIgnoreCase(value, "List", out var value2))
			{
				return;
			}
			if (value2.ValueKind == JsonValueKind.Null || value2.ValueKind != JsonValueKind.Array)
			{
				await writer.WriteLineAsync("⚠\ufe0f当前日期无分笔明细数据 (可能为非交易日或标的无成交)");
				return;
			}
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("[Header]:Time,Price(元),Volume(手),Status");
			foreach (JsonElement item in value2.EnumerateArray())
			{
				DateTime value3 = DateTime.Parse(item.GetProperty("Time").GetString(), System.Globalization.CultureInfo.InvariantCulture).ToLocalTime();
				double value4 = item.GetProperty("Price").GetDouble() / 1000.0;
				int value5 = (int)item.GetProperty("Volume").GetDouble();
				int @int = item.GetProperty("Status").GetInt32();
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 4, stringBuilder2);
				handler.AppendFormatted(value3, "HH:mm");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value4, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value5);
				handler.AppendLiteral(",");
				handler.AppendFormatted(@int);
				stringBuilder2.AppendLine(ref handler);
			}
			await writer.WriteLineAsync(stringBuilder.ToString());
		}
		catch (Exception ex2) { Serilog.Log.Warning(ex2, "捕获到未处理异常"); 
			await writer.WriteLineAsync("⚠\ufe0f取数异常: 拉取分笔内部逻辑错误:" + ex2.Message);
		}
		finally
		{
			_apiSemaphore.Release();
		}
	}

	private static async Task FetchAndWriteKlineAsync(StreamWriter writer, string code, string end, int days, bool aiCompress, CancellationToken ct = default)
	{
		await _apiSemaphore.WaitAsync(ct);
		try
		{
			DateTime endDate = DateTime.Now;
			DateTime.TryParseExact(end, "yyyyMMdd", null, DateTimeStyles.None, out endDate);
			string url = "/api/kline-all?code=" + code + "&type=day";
			int days2 = (TimeHelper.BeijingNow.Date - endDate.Date).Days;
			if (days2 >= 0)
			{
				int value = days2 + days + 30;
				string text = url;
				url = text + $"&limit={value}";
			}
			string json = null;
			Exception lastEx = null;
			for (int i = 1; i <= 3; i++)
			{
				try
				{
					json = await NetworkHelper.GetDataAsync(url, ct);
					if (string.IsNullOrWhiteSpace(json))
					{
						throw new Exception("接口返回空值");
					}
					using (JsonDocument.Parse(json))
					{
						lastEx = null;
					}
				}
				catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
					lastEx = ex;
					if (i < 3)
					{
						await Task.Delay(1000, ct);
					}
					continue;
				}
				break;
			}
			if (lastEx != null)
			{
				await writer.WriteLineAsync("⚠\ufe0f取数异常: [历史日K线]网络请求尝试3次后失败。原因:" + lastEx.Message);
				return;
			}
			await writer.WriteLineAsync("---以下是[历史日K线]数据---");
			using JsonDocument doc = JsonDocument.Parse(json);
			if (!TryGetArrayNode(doc.RootElement, out var arrayNode) || arrayNode.ValueKind == JsonValueKind.Null)
			{
				await writer.WriteLineAsync("⚠\ufe0f未获取到K线数据。");
				return;
			}
			List<JsonElement> list = new List<JsonElement>();
			for (int num = arrayNode.GetArrayLength() - 1; num >= 0; num--)
			{
				JsonElement jsonElement = arrayNode[num];
				if (TryGetPropertyIgnoreCase(jsonElement, "Time", out var value2) && DateTime.TryParse(value2.GetString(), out var result) && !(result.Date > endDate.Date))
				{
					list.Add(jsonElement);
					if (list.Count >= days)
					{
						break;
					}
				}
			}
			list.Reverse();
			if (!aiCompress)
			{
				await writer.WriteLineAsync(MinifyJson(JsonSerializer.Serialize(list)));
				return;
			}
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("[Header]:Date,Open(元),High(元),Low(元),Close(元),Volume(手)");
			foreach (JsonElement item in list)
			{
				TryGetPropertyIgnoreCase(item, "Time", out var value3);
				TryGetPropertyIgnoreCase(item, "Open", out var value4);
				TryGetPropertyIgnoreCase(item, "High", out var value5);
				TryGetPropertyIgnoreCase(item, "Low", out var value6);
				TryGetPropertyIgnoreCase(item, "Close", out var value7);
				TryGetPropertyIgnoreCase(item, "Volume", out var value8);
				DateTime value9 = DateTime.Parse(value3.GetString(), System.Globalization.CultureInfo.InvariantCulture);
				double value10 = value4.GetDouble() / 1000.0;
				double value11 = value5.GetDouble() / 1000.0;
				double value12 = value6.GetDouble() / 1000.0;
				double value13 = value7.GetDouble() / 1000.0;
				int value14 = (int)value8.GetDouble();
				StringBuilder stringBuilder2 = stringBuilder;
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(5, 6, stringBuilder2);
				handler.AppendFormatted(value9, "yyyy-MM-dd");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value10, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value11, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value12, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value13, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value14);
				stringBuilder2.AppendLine(ref handler);
			}
			await writer.WriteLineAsync(stringBuilder.ToString());
		}
		catch (Exception ex2) { Serilog.Log.Warning(ex2, "捕获到未处理异常"); 
			await writer.WriteLineAsync("⚠\ufe0f取数异常: 拉取K线内部逻辑错误:" + ex2.Message);
		}
		finally
		{
			_apiSemaphore.Release();
		}
	}

	private static async Task<string> FetchIndexDataStringAsync(string code, DateTime targetDate, int days, bool aiCompress, CancellationToken ct = default)
	{
		await _apiSemaphore.WaitAsync(ct);
		try
		{
			string formattedCode = code.ToLower();
			if (!formattedCode.StartsWith("sh") && !formattedCode.StartsWith("sz"))
			{
				string text = (formattedCode.StartsWith("0") ? "sh" : "sz");
				formattedCode = text + formattedCode;
			}
			string url = "/api/index?code=" + formattedCode + "&type=day";
			int days2 = (TimeHelper.BeijingNow.Date - targetDate.Date).Days;
			if (days2 >= 0)
			{
				int value = days2 + days + 30;
				string text2 = url;
				url = text2 + $"&limit={value}";
			}
			string json = null;
			Exception lastEx = null;
			for (int i = 1; i <= 3; i++)
			{
				try
				{
					json = await NetworkHelper.GetDataAsync(url, ct);
					if (string.IsNullOrWhiteSpace(json))
					{
						throw new Exception("接口返回空值");
					}
					using (JsonDocument.Parse(json))
					{
						lastEx = null;
					}
				}
				catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
					lastEx = ex;
					if (i < 3)
					{
						await Task.Delay(1000, ct);
					}
					continue;
				}
				break;
			}
			if (lastEx != null)
			{
				return $"⚠\ufe0f取数异常: [大盘指数] {formattedCode} 网络请求尝试3次后失败。原因:{lastEx.Message}\n";
			}
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
			handler.AppendLiteral("---以下是[大盘指数]");
			handler.AppendFormatted(formattedCode);
			handler.AppendLiteral("的K线数据---");
			stringBuilder3.AppendLine(ref handler);
			using JsonDocument jsonDocument2 = JsonDocument.Parse(json);
			if (!TryGetArrayNode(jsonDocument2.RootElement, out var arrayNode) || arrayNode.ValueKind == JsonValueKind.Null)
			{
				return stringBuilder.ToString() + "⚠\ufe0f未能解析到有效的数据节点(可能接口返回了空数组或 Null)\n";
			}
			List<JsonElement> list = new List<JsonElement>();
			foreach (JsonElement item in arrayNode.EnumerateArray())
			{
				if (TryGetPropertyIgnoreCase(item, "Time", out var value2) && DateTime.TryParse(value2.GetString(), out var result) && result.Date <= targetDate.Date)
				{
					list.Add(item.Clone());
				}
			}
			List<JsonElement> list2 = list.TakeLast(days).ToList();
			if (!aiCompress)
			{
				stringBuilder.AppendLine(MinifyJson(JsonSerializer.Serialize(list2)));
				return stringBuilder.ToString();
			}
			stringBuilder.AppendLine("[Header]:Date,Open,High,Low,Close,Volume,UpCount,DownCount");
			foreach (JsonElement item2 in list2)
			{
				TryGetPropertyIgnoreCase(item2, "Time", out var value3);
				TryGetPropertyIgnoreCase(item2, "Open", out var value4);
				TryGetPropertyIgnoreCase(item2, "High", out var value5);
				TryGetPropertyIgnoreCase(item2, "Low", out var value6);
				TryGetPropertyIgnoreCase(item2, "Close", out var value7);
				TryGetPropertyIgnoreCase(item2, "Volume", out var value8);
				string value9 = DateTime.Parse(value3.GetString(), System.Globalization.CultureInfo.InvariantCulture).ToString("yyyy-MM-dd");
				double value10 = value4.GetDouble() / 1000.0;
				double value11 = value5.GetDouble() / 1000.0;
				double value12 = value6.GetDouble() / 1000.0;
				double value13 = value7.GetDouble() / 1000.0;
				double @double = value8.GetDouble();
				JsonElement value14;
				int value15 = ((TryGetPropertyIgnoreCase(item2, "UpCount", out value14) && value14.ValueKind == JsonValueKind.Number) ? value14.GetInt32() : 0);
				JsonElement value16;
				int value17 = ((TryGetPropertyIgnoreCase(item2, "DownCount", out value16) && value16.ValueKind == JsonValueKind.Number) ? value16.GetInt32() : 0);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder4 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(7, 8, stringBuilder2);
				handler.AppendFormatted(value9);
				handler.AppendLiteral(",");
				handler.AppendFormatted(value10, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value11, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value12, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(value13, "F2");
				handler.AppendLiteral(",");
				handler.AppendFormatted(@double);
				handler.AppendLiteral(",");
				handler.AppendFormatted(value15);
				handler.AppendLiteral(",");
				handler.AppendFormatted(value17);
				stringBuilder4.AppendLine(ref handler);
			}
			return stringBuilder.ToString();
		}
		catch (Exception ex2) { Serilog.Log.Warning(ex2, "捕获到未处理异常"); 
			return "⚠\ufe0f取数异常: 拉取指数内部逻辑错误:" + ex2.Message + "\n";
		}
		finally
		{
			_apiSemaphore.Release();
		}
	}

	public static async Task<DateTime> GetActualTradingDateAsync(DateTime target, CancellationToken ct = default)
	{
		for (int i = 1; i <= 3; i++)
		{
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(await NetworkHelper.GetDataAsync($"/api/workday?date={target:yyyyMMdd}", ct));
				if (jsonDocument.RootElement.TryGetProperty("data", out var value))
				{
					if (value.TryGetProperty("is_workday", out var value2) && value2.GetBoolean())
					{
						return target;
					}
					if (value.TryGetProperty("previous", out var value3) && value3.ValueKind == JsonValueKind.Array && value3.GetArrayLength() > 0 && value3[0].TryGetProperty("numeric", out var value4) && DateTime.TryParseExact(value4.GetString(), "yyyyMMdd", null, DateTimeStyles.None, out var result))
					{
						return result;
					}
				}
			}
			catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
				if (i < 3)
				{
					await Task.Delay(1000, ct);
				}
				continue;
			}
			break;
		}
		return target;
	}

	private static async Task WriteFileHeaderAsync(StreamWriter writer, ExportConfig config, DateTime actualDate, CancellationToken ct = default)
	{
		await writer.WriteLineAsync("***********************************************************************************");
		await writer.WriteLineAsync("*【AI语料系统说明与单位映射表】");
		await writer.WriteLineAsync($"*基准取数日期:{actualDate:yyyy-MM-dd}");
		await writer.WriteLineAsync("*系统已开启自动单位换算规则，所有JSON脏数据已被物理清洗为标准量级:");
		await writer.WriteLineAsync("*1.价格单位:统一换算为【元】");
		await writer.WriteLineAsync("*2.成交量单位:个股统一强行回归为【手】");
		await writer.WriteLineAsync("*3. UpCount上涨家数/DownCount下跌家数 ，Status 0=买入, 1=卖出, 2=中性");
		if (config.EnableAiCompression)
		{
			await writer.WriteLineAsync("*4.结构状态:【LiteTable极致降维模式已开启】");
			await writer.WriteLineAsync("*5.阅读指引:数据已剥离一切多余的空格，请严格参照[Header]表头逗号顺位映射[Data]字段。");
		}
		await writer.WriteLineAsync("***********************************************************************************");
	}

	private static string MinifyJson(string json)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			return JsonSerializer.Serialize(jsonDocument.RootElement, new JsonSerializerOptions
			{
				WriteIndented = false
			});
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return json.Replace("\r", "").Replace("\n", "").Replace(" ", "");
		}
	}

	private static bool TryGetArrayNode(JsonElement root, out JsonElement arrayNode)
	{
		if (root.ValueKind == JsonValueKind.Array)
		{
			arrayNode = root;
			return true;
		}
		if (TryGetPropertyIgnoreCase(root, "data", out var value))
		{
			if (value.ValueKind == JsonValueKind.Array)
			{
				arrayNode = value;
				return true;
			}
			if (TryGetPropertyIgnoreCase(value, "list", out var value2) && value2.ValueKind == JsonValueKind.Array)
			{
				arrayNode = value2;
				return true;
			}
			if (TryGetPropertyIgnoreCase(value, "List", out var value3) && value3.ValueKind == JsonValueKind.Array)
			{
				arrayNode = value3;
				return true;
			}
		}
		arrayNode = default(JsonElement);
		return false;
	}

	private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty item in element.EnumerateObject())
			{
				if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					value = item.Value;
					return true;
				}
			}
		}
		value = default(JsonElement);
		return false;
	}
}
