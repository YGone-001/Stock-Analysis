using System;
using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.ViewModels;
using HandyControl.Controls;

namespace AIHelper.Views;

public class SparrowWindowDC : HandyControl.Controls.Window, IComponentConnector
{
	private readonly MainViewModel _mainVm;

	private CancellationTokenSource _cts;

	private static CookieContainer _cookieContainer = new CookieContainer();

	private static HttpClient _emClient = CreateSmartClient();

	private static readonly object _clientLock = new object();

	private static ConcurrentDictionary<string, string> _p2QuoteCache_DC = new ConcurrentDictionary<string, string>();

	private static ConcurrentDictionary<string, string> _p3KlineCache_DC = new ConcurrentDictionary<string, string>();

	private static ConcurrentDictionary<string, (string Name, string Reason)> _p3Winners_DC = new ConcurrentDictionary<string, (string, string)>();

	private int _globalPauseFlag;

	private double _shIndexPctChg;

	internal CheckBox ChkMacroDef;

	internal NumericUpDown NumMinRise;

	internal NumericUpDown NumMaxRise;

	internal NumericUpDown NumVolRatio;

	internal NumericUpDown NumMinAmount;

	internal CheckBox ChkMA60;

	internal CheckBox ChkAlpha;

	internal CheckBox ChkUseCache;

	internal RangeSlider SldAdhesion;

	internal RangeSlider SldTurnover;

	internal NumericUpDown NumMomentum;

	internal Button BtnStart;

	internal Button BtnTest;

	internal NumericUpDown NumConcurrency;

	internal TextBlock TxtProgressDesc;

	internal TextBlock TxtStats;

	internal ProgressBar PbScan;

	internal System.Windows.Controls.TextBox TxtLog;

	private bool _contentLoaded;

	private static HttpClient CreateSmartClient()
	{
		_cookieContainer = new CookieContainer();
		HttpClient httpClient = new HttpClient(new HttpClientHandler
		{
			UseProxy = false,
			CookieContainer = _cookieContainer,
			UseCookies = true,
			AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate),
			ServerCertificateCustomValidationCallback = (HttpRequestMessage message, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors) => true
		});
		httpClient.Timeout = TimeSpan.FromSeconds(15.0);
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
		httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
		httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
		httpClient.DefaultRequestHeaders.ConnectionClose = false;
		return httpClient;
	}

	private static void RenewEastMoneyClient()
	{
		lock (_clientLock)
		{
			try
			{
				_emClient?.Dispose();
			}
			catch
			{
			}
			_emClient = CreateSmartClient();
		}
	}

	private async Task<string> FetchEastMoneyDataAsync(string url)
	{
		for (int i = 0; i < 2; i++)
		{
			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Headers.Add("Referer", "http://quote.eastmoney.com/");
				using HttpResponseMessage response = await _emClient.SendAsync(request);
				response.EnsureSuccessStatusCode();
				return await response.Content.ReadAsStringAsync();
			}
			catch (Exception)
			{
				if (i == 0)
				{
					RenewEastMoneyClient();
					await Task.Delay(new Random().Next(300, 800));
					continue;
				}
				throw;
			}
		}
		return null;
	}

	private async Task<string> FetchEastMoneyKLineAsync(string secId)
	{
		long value = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(250, 2);
		defaultInterpolatedStringHandler.AppendLiteral("http://push2his.eastmoney.com/api/qt/stock/kline/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61&beg=0&end=20500101&ut=fa5fd1943c7b386f172d6893dbfba10b&rtntype=6&secid=");
		defaultInterpolatedStringHandler.AppendFormatted(secId);
		defaultInterpolatedStringHandler.AppendLiteral("&klt=101&fqt=1&cb=jsonp");
		defaultInterpolatedStringHandler.AppendFormatted(value);
		string url = defaultInterpolatedStringHandler.ToStringAndClear();
		return await FetchEastMoneyDataAsync(url);
	}

	private async Task<string> FetchEastMoneyQuoteAsync(string secId)
	{
		long value = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(164, 2);
		defaultInterpolatedStringHandler.AppendLiteral("http://push2.eastmoney.com/api/qt/stock/get?ut=fa5fd1943c7b386f172d6893dbfba10b&fltt=2&invt=2&fields=f43,f44,f45,f46,f47,f48,f49,f60,f161,f168,f170&secid=");
		defaultInterpolatedStringHandler.AppendFormatted(secId);
		defaultInterpolatedStringHandler.AppendLiteral("&cb=jQuery");
		defaultInterpolatedStringHandler.AppendFormatted(value);
		string url = defaultInterpolatedStringHandler.ToStringAndClear();
		return await FetchEastMoneyDataAsync(url);
	}

	public SparrowWindowDC(MainViewModel mainVm)
	{
		InitializeComponent();
		_mainVm = mainVm;
		ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
		AppendLog("⚡ 东财引擎已就绪！【智能 Cookie 轮换系统】已实装，随时准备金蝉脱壳！");
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
				defaultInterpolatedStringHandler.AppendLiteral("] \ud83d\udd0d ");
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
		AppendLog("\n\ud83e\ude7a [网络诊断] 测试抗封锁智能客户端...");
		BtnTest.IsEnabled = false;
		try
		{
			string text = await FetchEastMoneyKLineAsync("0.000001");
			if (string.IsNullOrWhiteSpace(text))
			{
				AppendLog("❌ [诊断结果] 返回了空字符串！");
				return;
			}
			string text2 = ((text.Length > 800) ? (text.Substring(0, 800) + "...\n(为防卡顿已截断)") : text);
			AppendLog("[原始返回值] \n" + text2);
			int num = text.IndexOf('{');
			int num2 = text.LastIndexOf('}');
			if (num >= 0 && num2 > num)
			{
				AppendLog("✅ [诊断结论] 接口完全畅通！WAF 的 Cookie 挑战已被攻破！");
			}
			else
			{
				AppendLog("❌ [诊断结论] 数据异常！没有找到 { } 包裹的 JSON 数据！", isHighlight: true);
			}
		}
		catch (Exception ex)
		{
			string text3 = ex.Message;
			if (ex.InnerException != null)
			{
				text3 = text3 + " -> 底层原因: " + ex.InnerException!.Message;
			}
			AppendLog("❌ [诊断异常] " + text3, isHighlight: true);
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
		double minRise = NumMinRise.Value;
		double maxRise = NumMaxRise.Value;
		double volRatio = NumVolRatio.Value;
		double minAmount = NumMinAmount.Value * 10000.0;
		bool checkMA60 = ChkMA60.IsChecked.GetValueOrDefault();
		double minAdhesion = SldAdhesion.ValueStart / 100.0;
		double maxAdhesion = SldAdhesion.ValueEnd / 100.0;
		double minTurnover = SldTurnover.ValueStart;
		double maxTurnover = SldTurnover.ValueEnd;
		double momentumThreshold = NumMomentum.Value;
		bool checkAlpha = ChkAlpha.IsChecked.GetValueOrDefault();
		int maxConcurrency = (int)NumConcurrency.Value;
		if (!ChkUseCache.IsChecked.GetValueOrDefault())
		{
			_p2QuoteCache_DC.Clear();
			_p3KlineCache_DC.Clear();
		}
		_p3Winners_DC.Clear();
		BtnStart.Content = "⏹ 停止扫描";
		TxtLog.Clear();
		_cts = new CancellationTokenSource();
		_globalPauseFlag = 0;
		SparrowWindowDC sparrowWindowDC = this;
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(27, 1);
		defaultInterpolatedStringHandler.AppendLiteral("\ud83e\udd85 [麻雀-东财精准版] 引擎点火！初始标的: ");
		defaultInterpolatedStringHandler.AppendFormatted(targetPool.Count);
		defaultInterpolatedStringHandler.AppendLiteral(" 只");
		sparrowWindowDC.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
		try
		{
			if (valueOrDefault)
			{
				if (await CheckIndexWeakness())
				{
					AppendLog("❌ [熔断] 大盘环境恶化，空仓防御！", isHighlight: true);
					return;
				}
				SparrowWindowDC sparrowWindowDC2 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(35, 1);
				defaultInterpolatedStringHandler.AppendLiteral("✅ [第一阶段通过] 上证今日涨幅: ");
				defaultInterpolatedStringHandler.AppendFormatted(_shIndexPctChg, "F2");
				defaultInterpolatedStringHandler.AppendLiteral("%, 已设为 RPS 参照基准。");
				sparrowWindowDC2.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			}
			var p2Missing = targetPool.Where(s => !_p2QuoteCache_DC.ContainsKey(s.Code)).ToList();
			if (p2Missing.Count > 0)
			{
				SparrowWindowDC sparrowWindowDC3 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(38, 2);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83c\udf2a\ufe0f [阶段2] 东财直连拉取盘口快照 (待下载:");
				defaultInterpolatedStringHandler.AppendFormatted(p2Missing.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" 只, 并发:");
				defaultInterpolatedStringHandler.AppendFormatted(maxConcurrency);
				defaultInterpolatedStringHandler.AppendLiteral(")...");
				sparrowWindowDC3.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				int p2Downloaded = 0;
				PbScan.Maximum = p2Missing.Count;
				PbScan.Value = 0.0;
				int initialCount = Math.Min(8, maxConcurrency);
				SemaphoreSlim semaphore2 = new SemaphoreSlim(initialCount);
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
								try
								{
									string emSecId2 = (stock.Code.StartsWith("6") ? ("1." + stock.Code) : ("0." + stock.Code));
									await Task.Delay(new Random().Next(50, 200));
									string text2 = await FetchEastMoneyQuoteAsync(emSecId2);
									if (!string.IsNullOrWhiteSpace(text2) && text2.Contains("data"))
									{
										_p2QuoteCache_DC.TryAdd(stock.Code, text2);
										break;
									}
								}
								catch
								{
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
			SparrowWindowDC sparrowWindowDC4 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(29, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83e\udde0 正在根据当前参数对 ");
			defaultInterpolatedStringHandler.AppendFormatted(targetPool.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只股票进行极速盘口核验...");
			sparrowWindowDC4.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			List<(string Code, string Name)> p2List = new List<(string, string)>();
			foreach (var item in targetPool)
			{
				if (_p2QuoteCache_DC.TryGetValue(item.Code, out var value) && ParseEastMoneySnapshot(value, out var risePct, out var outerVol, out var innerVol, out var amount, out var turnover) && !(risePct < minRise) && !(risePct > maxRise) && !(amount < minAmount) && !(outerVol <= 0.0) && !(innerVol <= 0.0) && !(outerVol <= innerVol * volRatio) && (!(turnover > 0.0) || (!(turnover < minTurnover) && !(turnover > maxTurnover))))
				{
					p2List.Add((item.Code, item.Name));
				}
			}
			SparrowWindowDC sparrowWindowDC5 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(17, 1);
			defaultInterpolatedStringHandler.AppendLiteral("✅ 盘口过滤完毕，剩余标的: ");
			defaultInterpolatedStringHandler.AppendFormatted(p2List.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只");
			sparrowWindowDC5.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			if (p2List.Count == 0)
			{
				return;
			}
			List<(string Code, string Name)> p3Missing = p2List.Where<(string, string)>(((string Code, string Name) s) => !_p3KlineCache_DC.ContainsKey(s.Code)).ToList();
			if (p3Missing.Count > 0)
			{
				SparrowWindowDC sparrowWindowDC6 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(32, 1);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83d\udd2c [阶段3] 东财网络拉取 K 线 (待下载:");
				defaultInterpolatedStringHandler.AppendFormatted(p3Missing.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" 只)...");
				sparrowWindowDC6.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				int p3Downloaded = 0;
				PbScan.Maximum = p3Missing.Count;
				PbScan.Value = 0.0;
				int initialCount2 = Math.Min(4, maxConcurrency);
				SemaphoreSlim semaphore = new SemaphoreSlim(initialCount2);
				try
				{
					int c;
					await Task.WhenAll(((IEnumerable<(string, string)>)p3Missing).Select((Func<(string, string), Task>)async delegate((string Code, string Name) stock)
					{
						await semaphore.WaitAsync();
						try
						{
							for (int i = 1; i <= 3; i++)
							{
								if (_cts.Token.IsCancellationRequested)
								{
									break;
								}
								try
								{
									string emSecId = (stock.Code.StartsWith("6") ? ("1." + stock.Code) : ("0." + stock.Code));
									await Task.Delay(new Random().Next(200, 600));
									string value3 = await FetchEastMoneyKLineAsync(emSecId);
									if (!string.IsNullOrWhiteSpace(value3))
									{
										_p3KlineCache_DC.TryAdd(stock.Code, value3);
										break;
									}
								}
								catch
								{
								}
								await Task.Delay(500);
							}
						}
						catch
						{
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
			SparrowWindowDC sparrowWindowDC7 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(31, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83e\udde0 正在根据当前参数对 ");
			defaultInterpolatedStringHandler.AppendFormatted(p2List.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只股票进行 K 线深度核验...");
			sparrowWindowDC7.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			int location = 0;
			PbScan.Maximum = p2List.Count;
			PbScan.Value = 0.0;
			int memCheckCount = 0;
			foreach (var item2 in p2List)
			{
				memCheckCount++;
				if (memCheckCount % 10 == 0 || memCheckCount == p2List.Count)
				{
					base.Dispatcher.Invoke(() => PbScan.Value = memCheckCount);
				}
				if (!_p3KlineCache_DC.TryGetValue(item2.Code, out var value2))
				{
					continue;
				}
				double latestPrice;
				double latestPctChg;
				List<double> list = ParseEastMoneyKline(value2, out latestPrice, out latestPctChg);
				if (list.Count < 60)
				{
					if (Interlocked.Increment(ref location) <= 5)
					{
						SparrowWindowDC sparrowWindowDC8 = this;
						defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(23, 2);
						defaultInterpolatedStringHandler.AppendLiteral("[探针-死因] ");
						defaultInterpolatedStringHandler.AppendFormatted(item2.Name);
						defaultInterpolatedStringHandler.AppendLiteral(": K线数量(");
						defaultInterpolatedStringHandler.AppendFormatted(list.Count);
						defaultInterpolatedStringHandler.AppendLiteral(")不足 60 根");
						sparrowWindowDC8.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
					}
				}
				else
				{
					if (checkAlpha && latestPctChg < _shIndexPctChg)
					{
						continue;
					}
					double num = list.Take(5).Average();
					double num2 = list.Take(10).Average();
					double num3 = list.Take(20).Average();
					double num4 = list.Take(60).Average();
					if (num < num2 || num2 < num3 || (checkMA60 && (latestPrice <= num4 || num3 < num4)))
					{
						continue;
					}
					double num5 = list.Skip(3).Take(5).Average();
					double num6 = (num - num5) / num5;
					if (!(num6 <= momentumThreshold))
					{
						double num7 = Math.Max(num, Math.Max(num2, num3));
						double num8 = Math.Min(num, Math.Min(num2, num3));
						double num9 = (num7 - num8) / num8;
						if (num9 >= minAdhesion && num9 <= maxAdhesion)
						{
							defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(9, 2);
							defaultInterpolatedStringHandler.AppendLiteral("黏合:");
							defaultInterpolatedStringHandler.AppendFormatted(num9 * 100.0, "F1");
							defaultInterpolatedStringHandler.AppendLiteral("% 动量:");
							defaultInterpolatedStringHandler.AppendFormatted(num6 * 100.0, "F1");
							defaultInterpolatedStringHandler.AppendLiteral("%");
							string text = defaultInterpolatedStringHandler.ToStringAndClear();
							_p3Winners_DC[item2.Code] = (item2.Name, text);
							SparrowWindowDC sparrowWindowDC9 = this;
							defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(11, 3);
							defaultInterpolatedStringHandler.AppendLiteral("\ud83c\udfaf [入围] ");
							defaultInterpolatedStringHandler.AppendFormatted(item2.Name);
							defaultInterpolatedStringHandler.AppendLiteral("(");
							defaultInterpolatedStringHandler.AppendFormatted(item2.Code);
							defaultInterpolatedStringHandler.AppendLiteral(") ");
							defaultInterpolatedStringHandler.AppendFormatted(text);
							sparrowWindowDC9.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
						}
					}
				}
			}
			SparrowWindowDC sparrowWindowDC10 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(22, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83c\udfc6 漏斗完成！共诞生长短腿战斗机 ");
			defaultInterpolatedStringHandler.AppendFormatted(_p3Winners_DC.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只！");
			sparrowWindowDC10.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			List<(string, string, string)> results = _p3Winners_DC.Select<KeyValuePair<string, (string, string)>, (string, string, string)>((KeyValuePair<string, (string Name, string Reason)> kvp) => (kvp.Key, kvp.Value.Name, kvp.Value.Reason)).ToList();
			await OutputResultsAsync(results);
		}
		catch (Exception ex)
		{
			AppendLog("❌ 引擎崩溃: " + ex.Message);
		}
		finally
		{
			BtnStart.Content = "\ud83d\ude80 执行漏斗选股 (东财直连引擎)";
			BtnStart.IsEnabled = true;
		}
	}

	private async Task<bool> CheckIndexWeakness()
	{
		try
		{
			string text = await FetchEastMoneyQuoteAsync("1.000001");
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
			if (jsonDocument.RootElement.TryGetProperty("data", out var value))
			{
				_shIndexPctChg = (value.TryGetProperty("f170", out var value2) ? value2.GetDouble() : 0.0);
				return _shIndexPctChg <= -2.5;
			}
		}
		catch
		{
		}
		return false;
	}

	private bool ParseEastMoneySnapshot(string json, out double risePct, out double outerVol, out double innerVol, out double amount, out double turnover)
	{
		risePct = (outerVol = (innerVol = (amount = (turnover = 0.0))));
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}
		int num = json.IndexOf('{');
		int num2 = json.LastIndexOf('}');
		if (num >= 0 && num2 > num)
		{
			json = json.Substring(num, num2 - num + 1);
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(json);
				if (jsonDocument.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object)
				{
					if ((value.TryGetProperty("f43", out var value2) ? (value2.GetDouble() / 100.0) : 0.0) <= 0.001)
					{
						return false;
					}
					risePct = (value.TryGetProperty("f170", out var value3) ? value3.GetDouble() : 0.0);
					amount = (value.TryGetProperty("f48", out var value4) ? value4.GetDouble() : 0.0);
					outerVol = (value.TryGetProperty("f49", out var value5) ? value5.GetDouble() : 0.0);
					innerVol = (value.TryGetProperty("f161", out var value6) ? value6.GetDouble() : 0.0);
					turnover = (value.TryGetProperty("f168", out var value7) ? value7.GetDouble() : 0.0);
					return true;
				}
			}
			catch
			{
			}
			return false;
		}
		return false;
	}

	private List<double> ParseEastMoneyKline(string json, out double latestPrice, out double latestPctChg)
	{
		List<double> list = new List<double>();
		latestPrice = 0.0;
		latestPctChg = 0.0;
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
								List<string> list2 = (from x in value2.EnumerateArray()
									select x.GetString()).ToList();
								for (int num3 = list2.Count - 1; num3 >= 0; num3--)
								{
									try
									{
										string[] array = list2[num3].Split(',');
										if (array.Length >= 9)
										{
											double.TryParse(array[2], out var result);
											if (result > 0.0)
											{
												list.Add(result);
											}
											if (num3 == list2.Count - 1)
											{
												latestPrice = result;
												double.TryParse(array[8], out latestPctChg);
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
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(10, 1);
		defaultInterpolatedStringHandler.AppendLiteral("东财麻雀池_");
		defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now, "yyyyMMdd_HHmmss");
		defaultInterpolatedStringHandler.AppendLiteral(".txt");
		string path2 = Path.Combine(path, defaultInterpolatedStringHandler.ToStringAndClear());
		using (StreamWriter writer = new StreamWriter(path2, append: false, Encoding.UTF8))
		{
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(74, 2);
			defaultInterpolatedStringHandler.AppendLiteral("【麻雀战法 4.5 - 东财版】选股结果\n生成时间: ");
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
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(5, 1);
			defaultInterpolatedStringHandler2.AppendLiteral("DC选股_");
			defaultInterpolatedStringHandler2.AppendFormatted(DateTime.Now, "MMdd");
			stockVM2.AddGroup(defaultInterpolatedStringHandler2.ToStringAndClear(), list);
			defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(15, 1);
			defaultInterpolatedStringHandler2.AppendLiteral("东财引擎执行完毕，入围 ");
			defaultInterpolatedStringHandler2.AppendFormatted(results2.Count);
			defaultInterpolatedStringHandler2.AppendLiteral(" 只！");
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
			Uri resourceLocator = new Uri("/AIHelper;component/views/sparrowwindowdc.xaml", UriKind.Relative);
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
			NumMinRise = (NumericUpDown)target;
			break;
		case 3:
			NumMaxRise = (NumericUpDown)target;
			break;
		case 4:
			NumVolRatio = (NumericUpDown)target;
			break;
		case 5:
			NumMinAmount = (NumericUpDown)target;
			break;
		case 6:
			ChkMA60 = (CheckBox)target;
			break;
		case 7:
			ChkAlpha = (CheckBox)target;
			break;
		case 8:
			ChkUseCache = (CheckBox)target;
			break;
		case 9:
			SldAdhesion = (RangeSlider)target;
			break;
		case 10:
			SldTurnover = (RangeSlider)target;
			break;
		case 11:
			NumMomentum = (NumericUpDown)target;
			break;
		case 12:
			BtnStart = (Button)target;
			BtnStart.Click += BtnStart_Click;
			break;
		case 13:
			BtnTest = (Button)target;
			BtnTest.Click += BtnTest_Click;
			break;
		case 14:
			NumConcurrency = (NumericUpDown)target;
			break;
		case 15:
			TxtProgressDesc = (TextBlock)target;
			break;
		case 16:
			TxtStats = (TextBlock)target;
			break;
		case 17:
			PbScan = (ProgressBar)target;
			break;
		case 18:
			TxtLog = (System.Windows.Controls.TextBox)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
