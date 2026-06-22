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
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.ViewModels;
using HandyControl.Controls;

namespace AIHelper.Views;

public class SparrowWindow : HandyControl.Controls.Window, IComponentConnector
{
	private readonly MainViewModel _mainVm;

	private CancellationTokenSource _cts;

	private static ConcurrentDictionary<string, bool> _p2Processed = new ConcurrentDictionary<string, bool>();

	private static ConcurrentBag<(string Code, string Name)> _p2Survivors = new ConcurrentBag<(string, string)>();

	private static ConcurrentDictionary<string, string> _p3KlineCache = new ConcurrentDictionary<string, string>();

	private static ConcurrentBag<(string Code, string Name, string Reason)> _p3Winners = new ConcurrentBag<(string, string, string)>();

	private int _globalPauseFlag;

	internal CheckBox ChkMacroDef;

	internal NumericUpDown NumMinRise;

	internal NumericUpDown NumMaxRise;

	internal NumericUpDown NumVolRatio;

	internal NumericUpDown NumMinAmount;

	internal CheckBox ChkMA60;

	internal RangeSlider SldAdhesion;

	internal CheckBox ChkUseCache;

	internal Button BtnStart;

	internal NumericUpDown NumConcurrency;

	internal TextBlock TxtProgressDesc;

	internal TextBlock TxtStats;

	internal ProgressBar PbScan;

	internal System.Windows.Controls.TextBox TxtLog;

	private bool _contentLoaded;

	public SparrowWindow(MainViewModel mainVm)
	{
		InitializeComponent();
		_mainVm = mainVm;
		AppendLog("注意：线程越多，扫描速度越快，但是越容易数据错误导致结果异常");
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

	private async void BtnStart_Click(object sender, RoutedEventArgs e)
	{
		if (BtnStart.Content.ToString()!.Contains("停止"))
		{
			_cts?.Cancel();
			BtnStart.IsEnabled = false;
			AppendLog("⚠\ufe0f 正在拉起手刹，停止漏斗扫描并保存当前进度...");
			return;
		}
		Dictionary<string, string> stockNameMap = _mainVm.StockVM.StockNameMap;
		if (stockNameMap == null || stockNameMap.Count == 0)
		{
			HandyControl.Controls.MessageBox.Show("全量代码表尚未加载，请稍等几秒或检查网络！", "提示", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		var targetPool = (from kvp in stockNameMap
			where !string.IsNullOrEmpty(kvp.Value) && !kvp.Value.Contains("ST") && !string.IsNullOrEmpty(kvp.Key) && !kvp.Key.StartsWith("688") && (kvp.Key.StartsWith("60") || kvp.Key.StartsWith("00") || kvp.Key.StartsWith("30"))
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
		int maxConcurrency = (int)NumConcurrency.Value;
		if (!(FindName("ChkUseCache") is CheckBox checkBox) || !checkBox.IsChecked.GetValueOrDefault())
		{
			_p2Processed.Clear();
			_p2Survivors.Clear();
			_p3KlineCache.Clear();
		}
		_p3Winners.Clear();
		BtnStart.Content = "⏹ 停止漏斗选股";
		BtnStart.Style = (Style)FindResource("ButtonDanger");
		TxtLog.Clear();
		_cts = new CancellationTokenSource();
		_globalPauseFlag = 0;
		SparrowWindow sparrowWindow = this;
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(25, 1);
		defaultInterpolatedStringHandler.AppendLiteral("\ud83e\udd85 [麻雀 4.0] 引擎点火！初始标的: ");
		defaultInterpolatedStringHandler.AppendFormatted(targetPool.Count);
		defaultInterpolatedStringHandler.AppendLiteral(" 只");
		sparrowWindow.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
		try
		{
			if (valueOrDefault && _p2Processed.Count == 0)
			{
				AppendLog("\ud83d\udee1\ufe0f [阶段1] 检测大盘宏观安全度...");
				if (await CheckIndexWeakness("1.000001") & await CheckIndexWeakness("1.000852"))
				{
					AppendLog("❌ [熔断] 大盘环境极度恶化，空仓防御！", isHighlight: true);
					return;
				}
				AppendLog("✅ [第一阶段通过] 允许开启个股海选。");
			}
			var p2Pending = targetPool.Where(s => !_p2Processed.ContainsKey(s.Code)).ToList();
			if (p2Pending.Count > 0)
			{
				SparrowWindow sparrowWindow2 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(37, 3);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83c\udf2a\ufe0f [阶段2] 快照扫描 (待处理:");
				defaultInterpolatedStringHandler.AppendFormatted(p2Pending.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" / 总计:");
				defaultInterpolatedStringHandler.AppendFormatted(targetPool.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" / 并发:");
				defaultInterpolatedStringHandler.AppendFormatted(maxConcurrency);
				defaultInterpolatedStringHandler.AppendLiteral(")...");
				sparrowWindow2.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				int p2Completed = 0;
				int p2SampleCount = 0;
				PbScan.Maximum = targetPool.Count;
				PbScan.Value = _p2Processed.Count;
				SemaphoreSlim semaphore2 = new SemaphoreSlim(maxConcurrency);
				try
				{
					await Task.WhenAll(p2Pending.Select(async stock =>
					{
						await semaphore2.WaitAsync();
						try
						{
							string quoteJson = null;
							bool p2Success = false;
							bool networkFatal2 = false;
							string rawJson;
							for (int retry = 0; retry < 5; retry++)
							{
								if (_cts.Token.IsCancellationRequested)
								{
									return;
								}
								rawJson = await NetworkHelper.GetDataAsync("/api/quote?code=" + stock.Code);
								quoteJson = rawJson;
								if (!string.IsNullOrWhiteSpace(quoteJson))
								{
									if (quoteJson.Contains("use of closed network connection"))
									{
										if (!_cts.Token.IsCancellationRequested)
										{
											AppendLog("❌ 上游服务器异常拒绝，请降低线程数重试！", isHighlight: true);
											_cts.Cancel();
										}
										return;
									}
									if (quoteJson.Contains("\"code\":-1") && quoteJson.Contains("超时"))
									{
										if (Interlocked.Exchange(ref _globalPauseFlag, 1) == 0)
										{
											AppendLog("⚠\ufe0f 网络波动、正在尽力尝试 (全员暂停5秒)...", isHighlight: true);
											try
											{
												await Task.Delay(5000, _cts.Token);
											}
											catch
											{
											}
											Interlocked.Exchange(ref _globalPauseFlag, 0);
										}
										else
										{
											while (_globalPauseFlag == 1 && !_cts.Token.IsCancellationRequested)
											{
												await Task.Delay(200);
											}
										}
										continue;
									}
									if (!quoteJson.Contains("\"code\":-1"))
									{
										p2Success = true;
										break;
									}
								}
								await Task.Delay(500);
							}
							if (!networkFatal2 && !_cts.Token.IsCancellationRequested)
							{
								_p2Processed.TryAdd(stock.Code, value: true);
								if (p2Success)
								{
									if (Interlocked.Increment(ref p2SampleCount) <= 2)
									{
										SparrowWindow sparrowWindow12 = this;
										DefaultInterpolatedStringHandler defaultInterpolatedStringHandler4 = new DefaultInterpolatedStringHandler(19, 3);
										defaultInterpolatedStringHandler4.AppendLiteral("\ud83d\udcdd [P2抽样] ");
										defaultInterpolatedStringHandler4.AppendFormatted(stock.Name);
										defaultInterpolatedStringHandler4.AppendLiteral("(");
										defaultInterpolatedStringHandler4.AppendFormatted(stock.Code);
										defaultInterpolatedStringHandler4.AppendLiteral(") 原始数据:\n");
										defaultInterpolatedStringHandler4.AppendFormatted(quoteJson);
										sparrowWindow12.AppendLog(defaultInterpolatedStringHandler4.ToStringAndClear());
									}
									if (ParseSnapshot(quoteJson, out var risePct, out var outerVol, out var innerVol, out var amount, out rawJson) && !(risePct < minRise) && !(risePct > maxRise) && !(amount < minAmount) && !(outerVol <= 0.0) && !(innerVol <= 0.0) && !(outerVol <= innerVol * volRatio))
									{
										_p2Survivors.Add((stock.Code, stock.Name));
									}
								}
							}
						}
						catch (Exception ex3)
						{
							AppendLog("⚠\ufe0f P2扫描异常 [" + stock.Name + "]: " + ex3.Message);
						}
						finally
						{
							int num8 = Interlocked.Increment(ref p2Completed);
							if (num8 % 50 == 0 || num8 == p2Pending.Count)
							{
								base.Dispatcher.Invoke(delegate
								{
									PbScan.Value = _p2Processed.Count;
									TextBlock txtProgressDesc2 = TxtProgressDesc;
									DefaultInterpolatedStringHandler defaultInterpolatedStringHandler5 = new DefaultInterpolatedStringHandler(9, 2);
									defaultInterpolatedStringHandler5.AppendLiteral("快照海选: ");
									defaultInterpolatedStringHandler5.AppendFormatted(_p2Processed.Count);
									defaultInterpolatedStringHandler5.AppendLiteral(" / ");
									defaultInterpolatedStringHandler5.AppendFormatted(targetPool.Count);
									txtProgressDesc2.Text = defaultInterpolatedStringHandler5.ToStringAndClear();
									TextBlock txtStats2 = TxtStats;
									defaultInterpolatedStringHandler5 = new DefaultInterpolatedStringHandler(6, 1);
									defaultInterpolatedStringHandler5.AppendLiteral("幸存: ");
									defaultInterpolatedStringHandler5.AppendFormatted(_p2Survivors.Count);
									defaultInterpolatedStringHandler5.AppendLiteral(" 只");
									txtStats2.Text = defaultInterpolatedStringHandler5.ToStringAndClear();
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
			else
			{
				SparrowWindow sparrowWindow3 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(30, 1);
				defaultInterpolatedStringHandler.AppendLiteral("\n♻\ufe0f [盘口缓存激活] 阶段2瞬间完成。当前幸存者: ");
				defaultInterpolatedStringHandler.AppendFormatted(_p2Survivors.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" 只");
				sparrowWindow3.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			}
			if (_cts.Token.IsCancellationRequested)
			{
				SparrowWindow sparrowWindow4 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(38, 2);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83d\uded1 已安全暂停！阶段2 进度: ");
				defaultInterpolatedStringHandler.AppendFormatted(_p2Processed.Count);
				defaultInterpolatedStringHandler.AppendLiteral("/");
				defaultInterpolatedStringHandler.AppendFormatted(targetPool.Count);
				defaultInterpolatedStringHandler.AppendLiteral("。勾选[使用缓存]再次启动可断点续传！");
				sparrowWindow4.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear(), isHighlight: true);
				return;
			}
			SparrowWindow sparrowWindow5 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(21, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83d\udd2a [阶段2结束] 进入下阶段: ");
			defaultInterpolatedStringHandler.AppendFormatted(_p2Survivors.Count);
			defaultInterpolatedStringHandler.AppendLiteral(" 只");
			sparrowWindow5.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			List<(string Code, string Name)> p2List = _p2Survivors.ToList();
			SparrowWindow sparrowWindow6 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(28, 1);
			defaultInterpolatedStringHandler.AppendLiteral("\n\ud83d\udd2c [阶段3] K线深度体检开始 (标的数:");
			defaultInterpolatedStringHandler.AppendFormatted(p2List.Count);
			defaultInterpolatedStringHandler.AppendLiteral(")...");
			sparrowWindow6.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			int p3Completed = 0;
			int p3SampleCount = 0;
			int p3NetworkHit = 0;
			int p3CacheHit = 0;
			PbScan.Maximum = p2List.Count;
			PbScan.Value = 0.0;
			SemaphoreSlim semaphore = new SemaphoreSlim(maxConcurrency);
			try
			{
				int current;
				await Task.WhenAll(((IEnumerable<(string, string)>)p2List).Select((Func<(string, string), Task>)async delegate((string Code, string Name) stock)
				{
					await semaphore.WaitAsync();
					try
					{
						string klineJson = null;
						if (_p3KlineCache.TryGetValue(stock.Code, out var value))
						{
							klineJson = value;
							Interlocked.Increment(ref p3CacheHit);
						}
						else
						{
							Interlocked.Increment(ref p3NetworkHit);
							bool networkFatal = false;
							for (int i = 1; i <= 5; i++)
							{
								if (_cts.Token.IsCancellationRequested)
								{
									return;
								}
								try
								{
									klineJson = await NetworkHelper.GetDataAsync("/api/kline-all?code=" + stock.Code + "&type=day&limit=65");
									if (!string.IsNullOrWhiteSpace(klineJson))
									{
										if (klineJson.Contains("use of closed network connection"))
										{
											if (!_cts.Token.IsCancellationRequested)
											{
												AppendLog("❌ 上游服务器异常拒绝，请降低线程数重试！", isHighlight: true);
												_cts.Cancel();
											}
											networkFatal = true;
											return;
										}
										if (klineJson.Contains("\"code\":-1") && klineJson.Contains("超时"))
										{
											if (Interlocked.Exchange(ref _globalPauseFlag, 1) == 0)
											{
												AppendLog("⚠\ufe0f 网络波动、正在尽力尝试 (全员暂停5秒)...", isHighlight: true);
												try
												{
													await Task.Delay(5000, _cts.Token);
												}
												catch
												{
												}
												Interlocked.Exchange(ref _globalPauseFlag, 0);
											}
											else
											{
												while (_globalPauseFlag == 1 && !_cts.Token.IsCancellationRequested)
												{
													await Task.Delay(200);
												}
											}
											continue;
										}
										if (klineJson.Contains("{") && klineJson.Contains("["))
										{
											using (JsonDocument.Parse(klineJson))
											{
												_p3KlineCache.TryAdd(stock.Code, klineJson);
											}
											break;
										}
									}
								}
								catch
								{
								}
								if (i < 5)
								{
									await Task.Delay(500 * i);
								}
							}
							if (networkFatal || _cts.Token.IsCancellationRequested)
							{
								return;
							}
						}
						if (Interlocked.Increment(ref p3SampleCount) <= 2)
						{
							SparrowWindow sparrowWindow10 = this;
							DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(21, 3);
							defaultInterpolatedStringHandler2.AppendLiteral("\ud83d\udcdd [P3抽样] ");
							defaultInterpolatedStringHandler2.AppendFormatted(stock.Name);
							defaultInterpolatedStringHandler2.AppendLiteral("(");
							defaultInterpolatedStringHandler2.AppendFormatted(stock.Code);
							defaultInterpolatedStringHandler2.AppendLiteral(") K线数据片段:\n");
							string text = klineJson;
							defaultInterpolatedStringHandler2.AppendFormatted((text != null && text.Length > 100) ? (klineJson.Substring(0, 100) + "...") : klineJson);
							sparrowWindow10.AppendLog(defaultInterpolatedStringHandler2.ToStringAndClear());
						}
						double latestPrice;
						List<double> list = ParseKlineClosesEnhanced(klineJson, out latestPrice);
						if (list.Count < 60)
						{
							if (list.Count == 0)
							{
								AppendLog("⚠\ufe0f [" + stock.Name + "] K线解析为空，可能已被限流跳过", isHighlight: true);
							}
						}
						else
						{
							double num = list.Take(5).Average();
							double num2 = list.Take(10).Average();
							double num3 = list.Take(20).Average();
							double num4 = list.Take(60).Average();
							if (!(num < num2) && !(num2 < num3) && (!checkMA60 || (!(latestPrice <= num4) && !(num3 < num4))))
							{
								double num5 = Math.Max(num, Math.Max(num2, num3));
								double num6 = Math.Min(num, Math.Min(num2, num3));
								double num7 = (num5 - num6) / num6;
								if (num7 >= minAdhesion && num7 <= maxAdhesion)
								{
									DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(8, 1);
									defaultInterpolatedStringHandler2.AppendLiteral("多头 黏合度:");
									defaultInterpolatedStringHandler2.AppendFormatted(num7 * 100.0, "F2");
									defaultInterpolatedStringHandler2.AppendLiteral("%");
									string item = defaultInterpolatedStringHandler2.ToStringAndClear();
									_p3Winners.Add((stock.Code, stock.Name, item));
									SparrowWindow sparrowWindow11 = this;
									defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(16, 3);
									defaultInterpolatedStringHandler2.AppendLiteral("\ud83c\udfaf [入围] ");
									defaultInterpolatedStringHandler2.AppendFormatted(stock.Name);
									defaultInterpolatedStringHandler2.AppendLiteral("(");
									defaultInterpolatedStringHandler2.AppendFormatted(stock.Code);
									defaultInterpolatedStringHandler2.AppendLiteral(") 黏合度:");
									defaultInterpolatedStringHandler2.AppendFormatted(num7 * 100.0, "F2");
									defaultInterpolatedStringHandler2.AppendLiteral("%");
									sparrowWindow11.AppendLog(defaultInterpolatedStringHandler2.ToStringAndClear());
								}
							}
						}
					}
					catch (Exception ex2)
					{
						AppendLog("❌ P3核验异常 [" + stock.Name + "]: " + ex2.Message);
					}
					finally
					{
						current = Interlocked.Increment(ref p3Completed);
						base.Dispatcher.Invoke(delegate
						{
							PbScan.Value = current;
							TextBlock txtProgressDesc = TxtProgressDesc;
							DefaultInterpolatedStringHandler defaultInterpolatedStringHandler3 = new DefaultInterpolatedStringHandler(9, 2);
							defaultInterpolatedStringHandler3.AppendLiteral("深度核验: ");
							defaultInterpolatedStringHandler3.AppendFormatted(current);
							defaultInterpolatedStringHandler3.AppendLiteral(" / ");
							defaultInterpolatedStringHandler3.AppendFormatted(p2List.Count);
							txtProgressDesc.Text = defaultInterpolatedStringHandler3.ToStringAndClear();
							TextBlock txtStats = TxtStats;
							defaultInterpolatedStringHandler3 = new DefaultInterpolatedStringHandler(8, 1);
							defaultInterpolatedStringHandler3.AppendLiteral("终极入围: ");
							defaultInterpolatedStringHandler3.AppendFormatted(_p3Winners.Count);
							defaultInterpolatedStringHandler3.AppendLiteral(" 只");
							txtStats.Text = defaultInterpolatedStringHandler3.ToStringAndClear();
						});
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
			SparrowWindow sparrowWindow7 = this;
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(30, 2);
			defaultInterpolatedStringHandler.AppendLiteral("\n✅ P3处理完毕。(网络抓取: ");
			defaultInterpolatedStringHandler.AppendFormatted(p3NetworkHit);
			defaultInterpolatedStringHandler.AppendLiteral(" 次, 内存闪查: ");
			defaultInterpolatedStringHandler.AppendFormatted(p3CacheHit);
			defaultInterpolatedStringHandler.AppendLiteral(" 次)");
			sparrowWindow7.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
			if (_cts.Token.IsCancellationRequested)
			{
				SparrowWindow sparrowWindow8 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(19, 2);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83d\uded1 已安全暂停！P3 进度: ");
				defaultInterpolatedStringHandler.AppendFormatted(p3Completed);
				defaultInterpolatedStringHandler.AppendLiteral("/");
				defaultInterpolatedStringHandler.AppendFormatted(p2List.Count);
				defaultInterpolatedStringHandler.AppendLiteral("。");
				sparrowWindow8.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear(), isHighlight: true);
			}
			else
			{
				SparrowWindow sparrowWindow9 = this;
				defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(28, 3);
				defaultInterpolatedStringHandler.AppendLiteral("\n\ud83c\udfc6 漏斗完成！【粘合度 ");
				defaultInterpolatedStringHandler.AppendFormatted(SldAdhesion.ValueStart, "F1");
				defaultInterpolatedStringHandler.AppendLiteral("~");
				defaultInterpolatedStringHandler.AppendFormatted(SldAdhesion.ValueEnd, "F1");
				defaultInterpolatedStringHandler.AppendLiteral("】共诞生长短腿麻雀 ");
				defaultInterpolatedStringHandler.AppendFormatted(_p3Winners.Count);
				defaultInterpolatedStringHandler.AppendLiteral(" 只！");
				sparrowWindow9.AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				await OutputResultsAsync(_p3Winners.ToList());
			}
		}
		catch (Exception ex)
		{
			AppendLog("❌ 引擎崩溃: " + ex.Message);
		}
		finally
		{
			BtnStart.Content = "\ud83d\ude80 执行漏斗选股 (14:30专用)";
			BtnStart.Style = (Style)FindResource("ButtonPrimary");
			BtnStart.IsEnabled = true;
		}
	}

	private async Task<bool> CheckIndexWeakness(string secid)
	{
		try
		{
			long value = DateTimeOffset.Now.ToUnixTimeMilliseconds();
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(207, 2);
			defaultInterpolatedStringHandler.AppendLiteral("https://75.push2.eastmoney.com/api/qt/stock/trends2/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58&ut=fa5fd1943c7b386f172d6893dbfba10b&iscr=0&ndays=1&secid=");
			defaultInterpolatedStringHandler.AppendFormatted(secid);
			defaultInterpolatedStringHandler.AppendLiteral("&_=");
			defaultInterpolatedStringHandler.AppendFormatted(value);
			string text = await NetworkHelper.GetDataAsync(defaultInterpolatedStringHandler.ToStringAndClear());
			if (string.IsNullOrWhiteSpace(text))
			{
				return false;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(text);
			if (jsonDocument.RootElement.TryGetProperty("data", out var value2) && value2.TryGetProperty("trends", out var value3))
			{
				List<string> list = (from x in value3.EnumerateArray()
					select x.GetString()).ToList();
				if (list.Count < 5)
				{
					return false;
				}
				string[] array = list.Last().Split(',');
				double num = double.Parse(array[2]);
				double num2 = double.Parse(array[7]);
				double num3 = double.Parse(list[list.Count - 5].Split(',')[7]);
				return num < num2 && num2 < num3;
			}
		}
		catch
		{
		}
		return false;
	}

	private bool ParseSnapshot(string json, out double risePct, out double outerVol, out double innerVol, out double amount, out string rawJson)
	{
		risePct = (outerVol = (innerVol = (amount = 0.0)));
		rawJson = "";
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
			JsonElement value3;
			JsonElement jsonElement = ((rootElement.ValueKind == JsonValueKind.Array) ? rootElement : ((!rootElement.TryGetProperty("data", out value)) ? default(JsonElement) : ((value.ValueKind == JsonValueKind.Array) ? value : (value.TryGetProperty("list", out value2) ? value2 : (value.TryGetProperty("List", out value3) ? value3 : default(JsonElement))))));
			if (jsonElement.ValueKind == JsonValueKind.Array && jsonElement.GetArrayLength() > 0)
			{
				JsonElement jsonElement2 = jsonElement[0];
				rawJson = jsonElement2.GetRawText();
				if (!jsonElement2.TryGetProperty("K", out var value4))
				{
					return false;
				}
				double num = value4.GetProperty("Close").GetDouble() / 1000.0;
				JsonElement value5;
				JsonElement value6;
				double num2 = (value4.TryGetProperty("Last", out value5) ? (value5.GetDouble() / 1000.0) : (value4.TryGetProperty("PreClose", out value6) ? (value6.GetDouble() / 1000.0) : 0.0));
				if (num2 > 0.0)
				{
					risePct = (num - num2) / num2 * 100.0;
				}
				outerVol = (jsonElement2.TryGetProperty("Wp", out var value7) ? value7.GetDouble() : (jsonElement2.TryGetProperty("OuterVolume", out var value8) ? value8.GetDouble() : (jsonElement2.TryGetProperty("OuterDisc", out var value9) ? value9.GetDouble() : 0.0)));
				innerVol = (jsonElement2.TryGetProperty("Np", out var value10) ? value10.GetDouble() : (jsonElement2.TryGetProperty("InnerVolume", out var value11) ? value11.GetDouble() : (jsonElement2.TryGetProperty("InsideDish", out var value12) ? value12.GetDouble() : 0.0)));
				amount = (jsonElement2.TryGetProperty("Amount", out var value13) ? value13.GetDouble() : (jsonElement2.TryGetProperty("TotalAmount", out var value14) ? value14.GetDouble() : 0.0));
				return num > 0.0;
			}
		}
		catch
		{
		}
		return false;
	}

	private List<double> ParseKlineClosesEnhanced(string json, out double latestPrice)
	{
		List<double> list = new List<double>();
		latestPrice = 0.0;
		if (string.IsNullOrWhiteSpace(json))
		{
			return list;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement rootElement = jsonDocument.RootElement;
			JsonElement jsonElement = ((rootElement.ValueKind == JsonValueKind.Array) ? rootElement : default(JsonElement));
			if (jsonElement.ValueKind == JsonValueKind.Undefined && TryGetPropertyIgnoreCase(rootElement, "data", out var value))
			{
				jsonElement = ((value.ValueKind == JsonValueKind.Array) ? value : (TryGetPropertyIgnoreCase(value, "list", out var value2) ? value2 : (TryGetPropertyIgnoreCase(value, "klines", out var value3) ? value3 : default(JsonElement))));
			}
			if (jsonElement.ValueKind == JsonValueKind.Array)
			{
				List<JsonElement> list2 = jsonElement.EnumerateArray().ToList();
				for (int num = list2.Count - 1; num >= 0; num--)
				{
					if (TryGetPropertyIgnoreCase(list2[num], "Close", out var value4))
					{
						list.Add(value4.GetDouble() / 1000.0);
					}
				}
				if (list.Count > 0)
				{
					latestPrice = list[0];
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

	private bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
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
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(8, 1);
		defaultInterpolatedStringHandler.AppendLiteral("麻雀池_");
		defaultInterpolatedStringHandler.AppendFormatted(DateTime.Now, "yyyyMMdd_HHmmss");
		defaultInterpolatedStringHandler.AppendLiteral(".txt");
		string filePath = Path.Combine(path, defaultInterpolatedStringHandler.ToStringAndClear());
		using (StreamWriter writer = new StreamWriter(filePath, append: false, Encoding.UTF8))
		{
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(68, 2);
			defaultInterpolatedStringHandler.AppendLiteral("【麻雀战法 4.0】选股结果\n生成时间: ");
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
		AppendLog("\n\ud83d\udcc1 结果已存至: " + filePath);
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
			defaultInterpolatedStringHandler2.AppendLiteral("选股_");
			defaultInterpolatedStringHandler2.AppendFormatted(DateTime.Now, "yyyyMMdd");
			stockVM2.AddGroup(defaultInterpolatedStringHandler2.ToStringAndClear(), list);
			defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(11, 1);
			defaultInterpolatedStringHandler2.AppendLiteral("已同步入围 ");
			defaultInterpolatedStringHandler2.AppendFormatted(results2.Count);
			defaultInterpolatedStringHandler2.AppendLiteral(" 只标的！");
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
			Uri resourceLocator = new Uri("/AIHelper;component/views/sparrowwindow.xaml", UriKind.Relative);
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
			SldAdhesion = (RangeSlider)target;
			break;
		case 8:
			ChkUseCache = (CheckBox)target;
			break;
		case 9:
			BtnStart = (Button)target;
			BtnStart.Click += BtnStart_Click;
			break;
		case 10:
			NumConcurrency = (NumericUpDown)target;
			break;
		case 11:
			TxtProgressDesc = (TextBlock)target;
			break;
		case 12:
			TxtStats = (TextBlock)target;
			break;
		case 13:
			PbScan = (ProgressBar)target;
			break;
		case 14:
			TxtLog = (System.Windows.Controls.TextBox)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}
