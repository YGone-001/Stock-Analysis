using System;

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.Services;
using Serilog;

namespace AIHelper.Views;

public partial class ExportControl : UserControl
{
	private bool _isUpdatingDate;
	private bool _hasInitializedDate;

	public Func<List<(string Code, string Name)>> GetSelectedStocksFunc { get; set; }

	public Func<string> GetCurrentTabNameFunc { get; set; }

	public Action<string> PrintLogAction { get; set; }

	public ExportControl()
	{
		InitializeComponent();
		DpTargetDate.SelectedDateChanged += DpTargetDate_SelectedDateChanged;
		base.Loaded += ExportControl_Loaded;
	}

	private async void ExportControl_Loaded(object sender, RoutedEventArgs e)
	{
		if (!_hasInitializedDate)
		{
			_hasInitializedDate = true;
			await AlignToActualTradingDateAsync();
		}
	}

	private async Task AlignToActualTradingDateAsync()
	{
		try
		{
			DpTargetDate.IsEnabled = false;
			DateTime netToday = TimeHelper.BeijingNow;
			DateTime value = await DataExportEngine.GetActualTradingDateAsync(netToday);
			_isUpdatingDate = true;
			DpTargetDate.SelectedDate = value;
			_isUpdatingDate = false;
			if (netToday.Date != value.Date)
			{
				ExportControl exportControl = this;
				exportControl.Log($"\ud83d\udcc5 开机自检：服务器显示今日非交易日，已自动定位至有效交易日 {value:yyyy-MM-dd}");
			}
			else
			{
				ExportControl exportControl2 = this;
				exportControl2.Log($"\ud83d\udcc5 开机自检：服务器日历已同步 ({value:yyyy-MM-dd} 交易日)");
			}
		}
		catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }
		finally
		{
			DpTargetDate.IsEnabled = true;
		}
	}

	private async void DpTargetDate_SelectedDateChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isUpdatingDate || !DpTargetDate.SelectedDate.HasValue)
		{
			return;
		}
		DateTime target = DpTargetDate.SelectedDate.Value;
		DpTargetDate.IsEnabled = false;
		try
		{
			DateTime value = await DataExportEngine.GetActualTradingDateAsync(target);
			if (value.Date != target.Date)
			{
				_isUpdatingDate = true;
				DpTargetDate.SelectedDate = value;
				_isUpdatingDate = false;
				ExportControl exportControl = this;
				exportControl.Log($"⚠\ufe0f 选定日期 {target:yyyy-MM-dd} 非交易日，日历已自动对齐至: {value:yyyy-MM-dd}");
			}
		}
		catch (System.Exception ex) { Serilog.Log.Error(ex, "Swallowed exception"); }
		finally
		{
			DpTargetDate.IsEnabled = true;
		}
	}

	public void ApplyConfig(AppConfig config)
	{
		if (config != null)
		{
			ToggleAiCompress.IsChecked = config.IsJsonMinify;
			ToggleHoldingPrompt.IsChecked = config.ExportIncludeHoldingPrompt;
			RdoSingleFile.IsChecked = config.ExportIsSingleFile;
			RdoMultiFile.IsChecked = !config.ExportIsSingleFile;
			TxtKlineDays.Text = config.ExportKlineDays.ToString();
			TxtIndexDays.Text = config.ExportIndexDays.ToString();
			ChkIdxSH.IsChecked = config.ExportIdxSH;
			ChkIdxSZ.IsChecked = config.ExportIdxSZ;
			ChkIdxCY.IsChecked = config.ExportIdxCY;
			ChkIdxHS300.IsChecked = config.ExportIdxHS300;
			ChkComboQuote.IsChecked = config.ExportComboQuote;
			ChkComboMinute.IsChecked = config.ExportComboMinute;
			ChkComboKline.IsChecked = config.ExportComboKline;
			ChkComboTick.IsChecked = config.ExportComboTick;
		}
	}

	public void SyncToConfig(AppConfig config)
	{
		if (config != null)
		{
			config.IsJsonMinify = ToggleAiCompress.IsChecked.GetValueOrDefault();
			config.ExportIsSingleFile = RdoSingleFile.IsChecked.GetValueOrDefault();
			config.ExportIncludeHoldingPrompt = ToggleHoldingPrompt.IsChecked.GetValueOrDefault();
			if (int.TryParse(TxtKlineDays.Text, out var result))
			{
				config.ExportKlineDays = result;
			}
			if (int.TryParse(TxtIndexDays.Text, out var result2))
			{
				config.ExportIndexDays = result2;
			}
			config.ExportIdxSH = ChkIdxSH.IsChecked.GetValueOrDefault();
			config.ExportIdxSZ = ChkIdxSZ.IsChecked.GetValueOrDefault();
			config.ExportIdxCY = ChkIdxCY.IsChecked.GetValueOrDefault();
			config.ExportIdxHS300 = ChkIdxHS300.IsChecked.GetValueOrDefault();
			config.ExportComboQuote = ChkComboQuote.IsChecked.GetValueOrDefault();
			config.ExportComboMinute = ChkComboMinute.IsChecked.GetValueOrDefault();
			config.ExportComboKline = ChkComboKline.IsChecked.GetValueOrDefault();
			config.ExportComboTick = ChkComboTick.IsChecked.GetValueOrDefault();
		}
	}

	private async void BtnFetchQuote_Click(object sender, RoutedEventArgs e)
	{
		await ExecuteExportTaskAsync((Button)sender, quote: true);
	}

	private async void BtnFetchMinute_Click(object sender, RoutedEventArgs e)
	{
		await ExecuteExportTaskAsync((Button)sender, quote: false, minute: true);
	}

	private async void BtnFetchKline_Click(object sender, RoutedEventArgs e)
	{
		await ExecuteExportTaskAsync((Button)sender, quote: false, minute: false, kline: true);
	}

	private async void BtnFetchTick_Click(object sender, RoutedEventArgs e)
	{
		await ExecuteExportTaskAsync((Button)sender, quote: false, minute: false, kline: false, tick: true);
	}

	private async void BtnFetchComposite_Click(object sender, RoutedEventArgs e)
	{
		if (_cts != null)
		{
			Log("⚠️ 正在中止导出任务，请稍候...");
			_cts.Cancel();
			return;
		}

		bool valueOrDefault = ChkComboQuote.IsChecked.GetValueOrDefault();
		bool valueOrDefault2 = ChkComboMinute.IsChecked.GetValueOrDefault();
		bool valueOrDefault3 = ChkComboKline.IsChecked.GetValueOrDefault();
		bool valueOrDefault4 = ChkComboTick.IsChecked.GetValueOrDefault();
		if (!valueOrDefault && !valueOrDefault2 && !valueOrDefault3 && !valueOrDefault4)
		{
			Log("⚠️ 复合模式下，至少需要勾选一项取数维度！");
			return;
		}
		AnalyticsService.Log("4", $"{valueOrDefault},{valueOrDefault2},{valueOrDefault3},{valueOrDefault4}");
		await ExecuteExportTaskAsync((Button)sender, valueOrDefault, valueOrDefault2, valueOrDefault3, valueOrDefault4);
	}

	private CancellationTokenSource _cts;

	private async Task ExecuteExportTaskAsync(Button sourceButton, bool quote = false, bool minute = false, bool kline = false, bool tick = false)
	{
		if (_cts != null)
		{
			Log("⚠️ 正在中止导出任务，请稍候...");
			_cts.Cancel();
			return;
		}
		if (GetSelectedStocksFunc == null)
		{
			Log("❌ 致命错误：未绑定数据源委托 (GetSelectedStocksFunc)。");
			return;
		}
		List<(string, string)> list = GetSelectedStocksFunc();
		if (list == null || !list.Any())
		{
			Log("⚠\ufe0f 当前未勾选任何标的股票，请先在列表中勾选！");
			return;
		}
		SetButtonsEnabled(isEnabled: false);
		string originalContent = sourceButton.Content?.ToString();
		sourceButton.Content = "⏹ 停止取数";
		sourceButton.IsEnabled = true;
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = new CancellationTokenSource();

		Log("==================================================");
		ExportControl exportControl = this;
		exportControl.Log($"取数任务开始... 目标标的数量: {list.Count}");
		try
		{
			int result;
			int klineDays = (int.TryParse(TxtKlineDays.Text, out result) ? Math.Clamp(result, 5, 300) : 100);
			int result2;
			int indexDays = (int.TryParse(TxtIndexDays.Text, out result2) ? Math.Clamp(result2, 3, 60) : 10);
			await DataExportEngine.ExecuteExportAsync(new ExportConfig
			{
				SelectedStocks = list,
				TargetDate = (DpTargetDate.SelectedDate ?? DateTime.Now),
				FetchQuote = quote,
				FetchMinute = minute,
				FetchTick = tick,
				FetchKline = kline,
				KlineDays = klineDays,
				IndexDays = indexDays,
				EnableAiCompression = ToggleAiCompress.IsChecked.GetValueOrDefault(),
				IncludeHoldingPrompt = ToggleHoldingPrompt.IsChecked.GetValueOrDefault(),
				IsSingleFileMode = RdoSingleFile.IsChecked.GetValueOrDefault(),
				TabName = (GetCurrentTabNameFunc?.Invoke() ?? "默认分组"),
				SelectedIndices = GetSelectedIndices()
			}, Log, _cts.Token);
		}
		catch (OperationCanceledException ex_log) { Serilog.Log.Information(ex_log, "任务被取消"); 
			Log("🛑 取数任务已被手动取消。");
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			Log("❌ 取数引擎崩溃: " + ex.Message);
		}
		finally
		{
			_cts?.Dispose();
			_cts = null;
			sourceButton.Content = originalContent;
			SetButtonsEnabled(isEnabled: true);
			Log("==================================================");
		}
	}

	private List<string> GetSelectedIndices()
	{
		List<string> list = new List<string>();
		if (ChkIdxSH.IsChecked.GetValueOrDefault())
		{
			list.Add(ChkIdxSH.Tag.ToString());
		}
		if (ChkIdxSZ.IsChecked.GetValueOrDefault())
		{
			list.Add(ChkIdxSZ.Tag.ToString());
		}
		if (ChkIdxCY.IsChecked.GetValueOrDefault())
		{
			list.Add(ChkIdxCY.Tag.ToString());
		}
		if (ChkIdxHS300.IsChecked.GetValueOrDefault())
		{
			list.Add(ChkIdxHS300.Tag.ToString());
		}
		return list;
	}

	private void Log(string message)
	{
		string message2 = message;
		base.Dispatcher.Invoke(delegate
		{
			PrintLogAction?.Invoke(message2);
		});
	}

	private void SetButtonsEnabled(bool isEnabled)
	{
		BtnFetchQuote.IsEnabled = isEnabled;
		BtnFetchMinute.IsEnabled = isEnabled;
		BtnFetchKline.IsEnabled = isEnabled;
		BtnFetchTick.IsEnabled = isEnabled;
		BtnFetchComposite.IsEnabled = isEnabled;
		ToggleAiCompress.IsEnabled = isEnabled;
		DpTargetDate.IsEnabled = isEnabled;
	}

}
