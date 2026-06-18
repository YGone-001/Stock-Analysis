using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.ViewModels;
using HandyControl.Controls;

namespace AIHelper.Views;

public class TurtleWindowDC : HandyControl.Controls.Window, IComponentConnector
{
	private readonly MainViewModel _mainVm;

	private CancellationTokenSource _cts;

	private static ConcurrentDictionary<string, string> _p2QuoteCache_Turtle = new ConcurrentDictionary<string, string>();

	private static ConcurrentDictionary<string, string> _p3KlineCache_Turtle = new ConcurrentDictionary<string, string>();

	private static ConcurrentDictionary<string, (string Name, string Reason)> _p3Winners_Turtle = new ConcurrentDictionary<string, (string, string)>();

	private int _globalPauseFlag;

	internal CheckBox ChkMacroDef;

	internal NumericUpDown NumN1;

	internal NumericUpDown NumN2;

	internal RangeSlider SldTurnover;

	internal NumericUpDown NumMinAmount;

	internal NumericUpDown NumConcurrency;

	internal CheckBox ChkUseCache;

	internal Button BtnStart;

	internal Button BtnTest;

	internal TextBlock TxtProgressDesc;

	internal TextBlock TxtStats;

	internal ProgressBar PbScan;

	internal System.Windows.Controls.TextBox TxtLog;

	private bool _contentLoaded;

	public TurtleWindowDC(MainViewModel mainVm)
	{
		InitializeComponent();
		_mainVm = mainVm;
		AppendLog("⚡ 海龟引擎已就绪！已回归 NetworkHelper，请点击【接口诊断】查看原始数据。");
	}

	private void AppendLog(string msg, bool isHighlight = false)
	{
		string msg2 = msg;
		base.Dispatcher.Invoke(delegate
		{
			if (isHighlight)
			{
				System.Windows.Controls.TextBox txtLog = TxtLog;
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(7, 2);
				defaultInterpolatedStringHandler.AppendLiteral("[");
				defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now, "HH:mm:ss");
				defaultInterpolatedStringHandler.AppendLiteral("] \ud83d\udc22 ");
				defaultInterpolatedStringHandler.AppendFormatted(msg2);
				defaultInterpolatedStringHandler.AppendLiteral("\n");
				txtLog.AppendText(defaultInterpolatedStringHandler.ToStringAndClear());
			}
			else
			{
				System.Windows.Controls.TextBox txtLog2 = TxtLog;
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(4, 2);
				defaultInterpolatedStringHandler.AppendLiteral("[");
				defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now, "HH:mm:ss");
				defaultInterpolatedStringHandler.AppendLiteral("] ");
				defaultInterpolatedStringHandler.AppendFormatted(msg2);
				defaultInterpolatedStringHandler.AppendLiteral("\n");
				txtLog2.AppendText(defaultInterpolatedStringHandler.ToStringAndClear());
			}
			TxtLog.ScrollToEnd();
		});
	}

	private async void BtnTest_Click(object sender, RoutedEventArgs e)
	{
		AppendLog("\n\ud83e\ude7a [网络诊断] 正在向东方财富发送 000001(平安银行) K线请求...");
		BtnTest.IsEnabled = false;
		try
		{
			string text = await NetworkHelper.GetDataAsync("https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=0.000001&klt=101&fqt=1&lmt=10&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61");
			if (string.IsNullOrWhiteSpace(text))
			{
				AppendLog("❌ [诊断结果] NetworkHelper 返回了空字符串！");
				return;
			}
			string text2 = ((text.Length > 800) ? (text.Substring(0, 800) + "...\n(为防卡顿已截断)") : text);
			AppendLog("[原始返回值] \n" + text2);
			int num = text.IndexOf('{');
			int num2 = text.LastIndexOf('}');
			if (num >= 0 && num2 > num)
			{
				AppendLog("✅ [诊断结论] 接口畅通！且成功定位到 JSONP 内部的核心数据！");
			}
			else
			{
				AppendLog("❌ [诊断结论] 数据异常！没有找到 { } 包裹的 JSON 数据！", isHighlight: true);
			}
		}
		catch (Exception ex)
		{
			AppendLog("❌ [诊断异常] " + ex.GetType().Name + ": " + ex.Message, isHighlight: true);
		}
		finally
		{
			BtnTest.IsEnabled = true;
		}
	}

	private async void BtnStart_Click(object sender, RoutedEventArgs e)
	{
		if (BtnStart.Content.ToString()!.Contains("停止"))
		{
			_cts?.Cancel();
			BtnStart.IsEnabled = false;
			AppendLog("⚠\ufe0f 正在拉起手刹...");
			return;
		}
		Dictionary<string, string> stockNameMap = _mainVm.StockVM.StockNameMap;
		if (stockNameMap == null || stockNameMap.Count == 0)
		{
			return;
		}
		var targetPool = (from kvp in stockNameMap
			where !string.IsNullOrEmpty(kvp.Value) && !string.IsNullOrEmpty(kvp.Key) && !kvp.Value.Contains("ST") && !kvp.Key.StartsWith("688") && (kvp.Key.StartsWith("60") || kvp.Key.StartsWith("00") || kvp.Key.StartsWith("30"))
			select new
			{
				Code = kvp.Key,
				Name = kvp.Value
			}).ToList();
		bool valueOrDefault = ChkMacroDef.IsChecked.GetValueOrDefault();
		int n1 = (int)NumN1.Value;
		int n2 = (int)NumN2.Value;
		double minTurnover = SldTurnover.ValueStart;
		double maxTurnover = SldTurnover.ValueEnd;
		double minAmount = NumMinAmount.Value * 10000.0;
		int maxConcurrency = (int)NumConcurrency.Value;
		if (!ChkUseCache.IsChecked.GetValueOrDefault())
		{
			_p2QuoteCache_Turtle.Clear();
			_p3KlineCache_Turtle.Clear();
		}
		_p3Winners_Turtle.Clear();
		BtnStart.Content = "⏹ 停止扫描";
		BtnStart.Background = new SolidColorBrush(Colors.IndianRed);
		TxtLog.Clear();
		_cts = new CancellationTokenSource();
		_globalPauseFlag = 0;
		TurtleWindowDC turtleWindowDC = this;
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(23, 1);
		defaultInterpolatedStringHandler.AppendLiteral("\ud83c\udf0a [海龟法则] 引擎点火！初始标的: ");
		defaultInterpolatedStringHandler.AppendFormatted(targetPool.Count);
		defaultInterpolatedStringHandler.AppendLiteral(" 只");
		turtleWindowDC.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
		try
		{
			if (valueOrDefault && await CheckIndexWeakness())
			{
				AppendLog("❌ [熔断] 系统判定大盘环境极差，不符合海龟顺势建仓条件！", isHighlight: true);
				return;
			}
			var p2Missing = targetPool.Where(s => !_p2QuoteCache_Turtle.ContainsKey(s.Code)).ToList();
			if (p2Missing.Count > 0)
			{
				TurtleWindowDC turtleWindowDC2 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(37, 2);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83c\udf2a\ufe0f [阶段 2] 网络拉取盘口快照 (待下载:");
				defaultInterpolatedStringHandler.AppendFormatted(p2Missing.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" 只, 并发:");
				defaultInterpolatedStringHandler.AppendFormatted(maxConcurrency);
				defaultInterpolatedStringHandler.AppendLiteral(")...");
				turtleWindowDC2.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				int p2Downloaded = 0;
				PbScan.Maximum = p2Missing.Count;
				PbScan.Value = 0.0;
				SemaphoreSlim semaphore2 = new SemaphoreSlim(maxConcurrency);
				try
				{
					int c2;
					await Task.WhenAll(p2Missing.Select(async stock =>
					{
						await semaphore2.WaitAsync();
						try
						{
							for (int retry = 0; retry < 3; retry++)
							{
								if (_cts.Token.IsCancellationRequested)
								{
									break;
								}
								string text3 = await NetworkHelper.GetDataAsync("/api/quote?code=" + stock.Code);
								if (!string.IsNullOrWhiteSpace(text3) && !text3.Contains("\"code\":-1"))
								{
									_p2QuoteCache_Turtle.TryAdd(stock.Code, text3);
									break;
								}
								await Task.Delay(500);
							}
						}
						catch
						{
						}
						finally
						{
							c2 = Interlocked.Increment(ref p2Downloaded);
							if (c2 % 50 == 0 || c2 == p2Missing.Count)
							{
								base.Dispatcher.Invoke(delegate
								{
									PbScan.Value = c2;
								});
							}
							semaphore2.Release();
						}
					}));
				}
				finally
				{
					if (semaphore2 != null)
					{
						((IDisposable)semaphore2).Dispose();
					}
				}
			}
			if (_cts.Token.IsCancellationRequested)
			{
				return;
			}
			List<(string Code, string Name)> p2List = new List<(string, string)>();
			foreach (var item5 in targetPool)
			{
				if (_p2QuoteCache_Turtle.TryGetValue(item5.Code, out var value) && ParseSnapshot(value, out var amount, out var _) && amount >= minAmount)
				{
					p2List.Add((item5.Code, item5.Name));
				}
			}
			if (p2List.Count == 0)
			{
				return;
			}
			List<(string Code, string Name)> p3Missing = p2List.Where<(string, string)>(((string Code, string Name) s) => !_p3KlineCache_Turtle.ContainsKey(s.Code)).ToList();
			if (p3Missing.Count > 0)
			{
				TurtleWindowDC turtleWindowDC3 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(33, 1);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83d\udd2c [阶段 3] 东财拉取 K 线数据 (待下载:");
				defaultInterpolatedStringHandler.AppendFormatted(p3Missing.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" 只)...");
				turtleWindowDC3.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				int p3Downloaded = 0;
				PbScan.Maximum = p3Missing.Count;
				PbScan.Value = 0.0;
				SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrency);
				try
				{
					int c;
					await Task.WhenAll(((IEnumerable<(string, string)>)p3Missing).Select((Func<(string, string), Task>)async delegate((string Code, string Name) stock)
					{
						await semaphore.WaitAsync();
						try
						{
							for (int j = 1; j <= 3; j++)
							{
								if (_cts.Token.IsCancellationRequested)
								{
									break;
								}
								try
								{
									string text2 = (stock.Code.StartsWith("6") ? ("1." + stock.Code) : ("0." + stock.Code));
									string value5 = await NetworkHelper.GetDataAsync("https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + text2 + "&klt=101&fqt=1&lmt=100&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61");
									if (!string.IsNullOrWhiteSpace(value5))
									{
										_p3KlineCache_Turtle.TryAdd(stock.Code, value5);
										break;
									}
								}
								catch
								{
								}
								await Task.Delay(300);
							}
						}
						finally
						{
							c = Interlocked.Increment(ref p3Downloaded);
							if (c % 20 == 0 || c == p3Missing.Count)
							{
								base.Dispatcher.Invoke(() => PbScan.Value = c);
							}
							semaphore.Release();
						}
					}));
				}
				finally
				{
					if (semaphore != null)
					{
						((IDisposable)semaphore).Dispose();
					}
				}
			}
			if (_cts.Token.IsCancellationRequested)
			{
				return;
			}
			TurtleWindowDC turtleWindowDC4 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(29, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83e\udde0 正在根据海龟法则对 ");
			defaultInterpolatedStringHandler.AppendFormatted(p2List.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只股票进行极速突破计算...");
			turtleWindowDC4.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			int location = 0;
			PbScan.Maximum = p2List.Count;
			PbScan.Value = 0.0;
			int memCheckCount = 0;
			foreach (var item6 in p2List)
			{
				memCheckCount++;
				if (memCheckCount % 10 == 0 || memCheckCount == p2List.Count)
				{
					base.Dispatcher.Invoke(() => PbScan.Value = memCheckCount);
				}
				if (!_p3KlineCache_Turtle.TryGetValue(item6.Code, out var value2))
				{
					continue;
				}
				List<(double, double, double, double)> list = ParseEastMoneyKlineForTurtle(value2);
				if (list.Count < n2 + 5)
				{
					if (Interlocked.Increment(ref location) <= 5)
					{
						AppendLog("[探针-K线不足] " + item6.Name + ": 有效K线不足计算突破。");
					}
					continue;
				}
				(double, double, double, double) tuple = list.Last();
				if (tuple.Item4 < minTurnover || tuple.Item4 > maxTurnover)
				{
					continue;
				}
				List<(double, double, double, double)> source = list.Skip(list.Count - 1 - n1).Take(n1).ToList();
				List<(double, double, double, double)> source2 = list.Skip(list.Count - 1 - n2).Take(n2).ToList();
				double num = source.Max<(double, double, double, double)>(((double High, double Low, double Close, double Turnover) k) => k.High);
				double num2 = source2.Max<(double, double, double, double)>(((double High, double Low, double Close, double Turnover) k) => k.High);
				bool num3 = tuple.Item3 > num;
				bool flag = tuple.Item3 > num2;
				if (num3 || flag)
				{
					double num4 = 0.0;
					int num5 = 20;
					for (int i = list.Count - num5; i < list.Count; i++)
					{
						double item = list[i].Item1;
						double item2 = list[i].Item2;
						double item3 = list[i - 1].Item3;
						double num6 = Math.Max(item - item2, Math.Max(Math.Abs(item - item3), Math.Abs(item2 - item3)));
						num4 += num6;
					}
					double num7 = num4 / (double)num5;
					double value3 = num7 / tuple.Item3 * 100.0;
					string text;
					if (!flag)
					{
						defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(6, 1);
						defaultInterpolatedStringHandler.AppendLiteral("[");
						defaultInterpolatedStringHandler.AppendFormatted(n1);
						defaultInterpolatedStringHandler.AppendLiteral("日短突破]");
						text = defaultInterpolatedStringHandler.ToStringAndClear();
					}
					else
					{
						defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(6, 1);
						defaultInterpolatedStringHandler.AppendLiteral("[");
						defaultInterpolatedStringHandler.AppendFormatted(n2);
						defaultInterpolatedStringHandler.AppendLiteral("日大突破]");
						text = defaultInterpolatedStringHandler.ToStringAndClear();
					}
					string value4 = text;
					defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(12, 3);
					defaultInterpolatedStringHandler.AppendFormatted(value4);
					defaultInterpolatedStringHandler.AppendLiteral(" N值(ATR):");
					defaultInterpolatedStringHandler.AppendFormatted(num7, "F2");
					defaultInterpolatedStringHandler.AppendLiteral("(");
					defaultInterpolatedStringHandler.AppendFormatted(value3, "F1");
					defaultInterpolatedStringHandler.AppendLiteral("%)");
					string item4 = defaultInterpolatedStringHandler.ToStringAndClear();
					_p3Winners_Turtle[item6.Code] = (item6.Name, item4);
					TurtleWindowDC turtleWindowDC5 = this;
					defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(16, 5);
					defaultInterpolatedStringHandler.AppendLiteral("\ud83c\udf0a ");
					defaultInterpolatedStringHandler.AppendFormatted(value4);
					defaultInterpolatedStringHandler.AppendLiteral(" ");
					defaultInterpolatedStringHandler.AppendFormatted(item6.Name);
					defaultInterpolatedStringHandler.AppendLiteral("(");
					defaultInterpolatedStringHandler.AppendFormatted(item6.Code);
					defaultInterpolatedStringHandler.AppendLiteral(") 收盘:");
					defaultInterpolatedStringHandler.AppendFormatted(tuple.Item3, "F2");
					defaultInterpolatedStringHandler.AppendLiteral(" 突破前高:");
					defaultInterpolatedStringHandler.AppendFormatted(num, "F2");
					turtleWindowDC5.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				}
			}
			TurtleWindowDC turtleWindowDC6 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(22, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83c\udfc6 海龟出海！共发现破位上行标的 ");
			defaultInterpolatedStringHandler.AppendFormatted(_p3Winners_Turtle.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只！");
			turtleWindowDC6.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			List<(string, string, string)> results = _p3Winners_Turtle.Select<KeyValuePair<string, (string, string)>, (string, string, string)>((KeyValuePair<string, (string Name, string Reason)> kvp) => (kvp.Key, kvp.Value.Name, kvp.Value.Reason)).ToList();
			await OutputResultsAsync(results);
		}
		catch (Exception ex)
		{
			AppendLog("❌ 引擎崩溃: " + ex.Message);
		}
		finally
		{
			BtnStart.Content = "\ud83d\ude80 执行海龟突破选股 (东财高速通道)";
			BtnStart.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
			BtnStart.IsEnabled = true;
		}
	}

	private async Task<bool> CheckIndexWeakness()
	{
		try
		{
			string text = await NetworkHelper.GetDataAsync("https://push2.eastmoney.com/api/qt/stock/get?secid=1.000001&fields=f43,f169,f170,f171");
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			int num = text.IndexOf('{');
			int num2 = text.LastIndexOf('}');
			if (num >= 0 && num2 > num)
			{
				text = text.Substring(num, num2 - num + 1);
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			JsonElement value2;
			if (jsonDocument.RootElement.TryGetProperty("data", out var value))
			{
				return (value.TryGetProperty("f170", out value2) ? value2.GetDouble() : 0.0) <= -2.0;
			}
		}
		catch
		{
		}
		return false;
	}

	private bool ParseSnapshot(string json, out double amount, out double turnover)
	{
		amount = (turnover = 0.0);
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			JsonElement value;
			JsonElement value2;
			JsonElement jsonElement = ((rootElement.ValueKind == JsonValueKind.Array) ? rootElement : ((!rootElement.TryGetProperty("data", out value)) ? default(JsonElement) : ((value.ValueKind == JsonValueKind.Array) ? value : (value.TryGetProperty("list", out value2) ? value2 : default(JsonElement)))));
			if (jsonElement.ValueKind == JsonValueKind.Array && jsonElement.GetArrayLength() > 0)
			{
				JsonElement jsonElement2 = jsonElement[0];
				if (!jsonElement2.TryGetProperty("K", out var value3))
				{
					return false;
				}
				if (value3.GetProperty("Close").GetDouble() / 1000.0 <= 0.001)
				{
					return false;
				}
				JsonElement value4;
				JsonElement value5;
				double num = (jsonElement2.TryGetProperty("Amount", out value4) ? value4.GetDouble() : (jsonElement2.TryGetProperty("TotalAmount", out value5) ? value5.GetDouble() : 0.0));
				amount = ((num < 0.001) ? 0.0 : num);
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private List<(double High, double Low, double Close, double Turnover)> ParseEastMoneyKlineForTurtle(string json)
	{
		List<(double, double, double, double)> list = new List<(double, double, double, double)>();
		if (string.IsNullOrWhiteSpace(json))
		{
			return list;
		}
		int num = json.IndexOf('{');
		int num2 = json.LastIndexOf('}');
		if (num >= 0 && num2 > num)
		{
			json = json.Substring(num, num2 - num + 1);
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(json);
				if (jsonDocument.RootElement.TryGetProperty("data", out var value))
				{
					if (value.ValueKind == JsonValueKind.Object)
					{
						if (value.TryGetProperty("klines", out var value2))
						{
							if (value2.ValueKind == JsonValueKind.Array)
							{
								foreach (JsonElement item in value2.EnumerateArray())
								{
									try
									{
										string[] array = item.GetString()!.Split(',');
										if (array.Length >= 11)
										{
											double.TryParse(array[3], out var result);
											double.TryParse(array[4], out var result2);
											double.TryParse(array[2], out var result3);
											double.TryParse(array[10], out var result4);
											if (result3 > 0.0)
											{
												list.Add((result, result2, result3, result4));
											}
										}
									}
									catch
									{
									}
								}
								return list;
							}
							return list;
						}
						return list;
					}
					return list;
				}
				return list;
			}
			catch
			{
				return list;
			}
		}
		return list;
	}

	private async Task OutputResultsAsync(List<(string Code, string Name, string Reason)> results)
	{
		List<(string Code, string Name, string Reason)> results2 = results;
		if (results2.Count == 0)
		{
			return;
		}
		string text = ConfigManager.Load().DataSavePath;
		if (string.IsNullOrWhiteSpace(text))
		{
			text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
		}
		if (!Directory.Exists(text))
		{
			Directory.CreateDirectory(text);
		}
		string path = text;
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(9, 1);
		defaultInterpolatedStringHandler.AppendLiteral("海龟突破_");
		defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now, "yyyyMMdd_HHmmss");
		defaultInterpolatedStringHandler.AppendLiteral(".txt");
		string path2 = Path.Combine(path, defaultInterpolatedStringHandler.ToStringAndClear());
		using (StreamWriter writer = new StreamWriter(path2, append: false, Encoding.UTF8))
		{
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(64, 2);
			defaultInterpolatedStringHandler.AppendLiteral("【海龟法则】突破选股\n生成时间: ");
			defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now);
			defaultInterpolatedStringHandler.AppendLiteral("\n入围数量: ");
			defaultInterpolatedStringHandler.AppendFormatted(results2.Count);
			defaultInterpolatedStringHandler.AppendLiteral("\n=======================================");
			await writer.WriteLineAsync(defaultInterpolatedStringHandler.ToStringAndClear());
			foreach (var item in results2)
			{
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(16, 3);
				defaultInterpolatedStringHandler.AppendLiteral("代码: ");
				defaultInterpolatedStringHandler.AppendFormatted(item.Code);
				defaultInterpolatedStringHandler.AppendLiteral(" \t名称: ");
				defaultInterpolatedStringHandler.AppendFormatted(item.Name);
				defaultInterpolatedStringHandler.AppendLiteral(" \t说明: ");
				defaultInterpolatedStringHandler.AppendFormatted(item.Reason);
				await writer.WriteLineAsync(defaultInterpolatedStringHandler.ToStringAndClear());
			}
		}
		base.Dispatcher.Invoke(delegate
		{
			List<StockModel> list = new List<StockModel>();
			foreach (var item2 in results2)
			{
				int num = 0;
				try
				{
					object stockVM = _mainVm.StockVM;
					foreach (object item3 in ((dynamic)stockVM).StockGroups)
					{
						foreach (object item4 in ((dynamic)item3).Stocks)
						{
							if (((dynamic)item4).Code == item2.Code)
							{
								string text2 = ((dynamic)item3).Header;
								if (!string.IsNullOrEmpty(text2) && text2.Contains("选股"))
								{
									num = 2;
									break;
								}
								if (num == 0)
								{
									num = 1;
								}
							}
						}
						if (num == 2)
						{
							break;
						}
					}
				}
				catch
				{
				}
				list.Add(new StockModel
				{
					Code = item2.Code,
					Name = item2.Name,
					IsChecked = true,
					HighlightLevel = num
				});
			}
			StockViewModel stockVM2 = _mainVm.StockVM;
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(3, 1);
			defaultInterpolatedStringHandler2.AppendLiteral("海龟_");
			defaultInterpolatedStringHandler2.AppendFormatted(DateTime.Now, "MMdd");
			stockVM2.AddGroup(defaultInterpolatedStringHandler2.ToStringAndClear(), list);
			defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(15, 1);
			defaultInterpolatedStringHandler2.AppendLiteral("突破检测完毕，擒获 ");
			defaultInterpolatedStringHandler2.AppendFormatted(results2.Count);
			defaultInterpolatedStringHandler2.AppendLiteral(" 只海龟！");
			Growl.Success(defaultInterpolatedStringHandler2.ToStringAndClear());
		});
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/AIHelper;component/views/turtlewindowdc.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			ChkMacroDef = (CheckBox)target;
			break;
		case 2:
			NumN1 = (NumericUpDown)target;
			break;
		case 3:
			NumN2 = (NumericUpDown)target;
			break;
		case 4:
			SldTurnover = (RangeSlider)target;
			break;
		case 5:
			NumMinAmount = (NumericUpDown)target;
			break;
		case 6:
			NumConcurrency = (NumericUpDown)target;
			break;
		case 7:
			ChkUseCache = (CheckBox)target;
			break;
		case 8:
			BtnStart = (Button)target;
			BtnStart.Click += BtnStart_Click;
			break;
		case 9:
			BtnTest = (Button)target;
			BtnTest.Click += BtnTest_Click;
			break;
		case 10:
			TxtProgressDesc = (TextBlock)target;
			break;
		case 11:
			TxtStats = (TextBlock)target;
			break;
		case 12:
			PbScan = (ProgressBar)target;
			break;
		case 13:
			TxtLog = (System.Windows.Controls.TextBox)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
