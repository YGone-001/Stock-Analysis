using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AIHelper.Helpers;

using AIHelper.Services.StockData;

using HandyControl.Controls;
using Serilog;

#pragma warning disable CS8618
#pragma warning disable CS8618
namespace AIHelper.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
	[ObservableProperty]
	private string _mainTitle = "股票数据助手";

	[ObservableProperty]
	private string _statusLeft = "就绪";

	[ObservableProperty]
	private string _latencyText = "";

	[ObservableProperty]
	private string _statusRight = "在线人数: 获取中...";

	[ObservableProperty]
	private DateTime _selectedDate = DateTime.Now;

	[ObservableProperty]
	private bool _isLoading;

	[ObservableProperty]
	private bool _isAutoOpen;

	partial void OnIsAutoOpenChanged(bool value)
	{
		if (!IsLoading)
		{
			AppendLog($"⚙\ufe0f 设置更改：自动打开目录 -> {value}");
		}
	}

	[ObservableProperty]
	private bool _isAiCompress = true;

	partial void OnIsAiCompressChanged(bool value)
	{
		DefaultInterpolatedStringHandler defaultInterpolatedStringHandler;
		if (!IsLoading)
		{
			defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(18, 1);
			defaultInterpolatedStringHandler.AppendLiteral("⚙\ufe0f 设置更改：AI数据压缩 -> ");
			defaultInterpolatedStringHandler.AppendFormatted(value);
			AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
		}
		defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(0, 1);
		defaultInterpolatedStringHandler.AppendFormatted(value);
		AnalyticsService.Log("13", defaultInterpolatedStringHandler.ToStringAndClear());
	}

	private ChatViewModel _chatVM;

	private GridLength _chatColumnWidth = new GridLength(1.0, GridUnitType.Star);

	private Visibility _chatVisibility;

	public ObservableCollection<MenuItemModel> MenuItems { get; set; }

	public StockViewModel StockVM { get; set; }

	public ChatViewModel ChatVM
	{
		get
		{
			return _chatVM;
		}
		set
		{
			if (_chatVM != null)
			{
				_chatVM.OnlineCountUpdated -= OnChatVMOnlineCountUpdated;
			}
			_chatVM = value;
			OnPropertyChanged(nameof(ChatVM));
			if (_chatVM != null)
			{
				_chatVM.OnlineCountUpdated += OnChatVMOnlineCountUpdated;
			}
		}
	}

	private void OnChatVMOnlineCountUpdated(int count)
	{
		Application.Current?.Dispatcher.Invoke(delegate
		{
			MainViewModel mainViewModel = this;
			mainViewModel.StatusRight = $"聊天室活跃摸鱼人数: {count}";
		});
	}

	public LogViewModel LogVM { get; set; }

	public GridLength ChatColumnWidth
	{
		get => _chatColumnWidth;
		set
		{
			_chatColumnWidth = value;
			OnPropertyChanged(nameof(ChatColumnWidth));
		}
	}

	public Visibility ChatVisibility
	{
		get => _chatVisibility;
		set
		{
			_chatVisibility = value;
			OnPropertyChanged(nameof(ChatVisibility));
		}
	}

		

	private readonly AIHelper.Services.IDialogService _dialogService;

	public MainViewModel(StockViewModel stockVm, LogViewModel logVm, AIHelper.Services.IDialogService dialogService)
	{
		_dialogService = dialogService;
		StockVM = stockVm;
		LogVM = logVm;
		StockVM.LogAction = AppendLog;
		StockVM.LatencyAction = delegate(long ms)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				if (ms < 5)
				{
					MainViewModel mainViewModel = this;
					mainViewModel.LatencyText = $"⚡ 本地缓存 ({ms}ms)";
				}
				else if (ms < 500)
				{
					MainViewModel mainViewModel2 = this;
					mainViewModel2.LatencyText = $"\ud83d\udfe2 API延迟: {ms}ms";
				}
				else if (ms < 2000)
				{
					MainViewModel mainViewModel3 = this;
					mainViewModel3.LatencyText = $"\ud83d\udfe1 API延迟: {ms}ms";
				}
				else
				{
					MainViewModel mainViewModel4 = this;
					mainViewModel4.LatencyText = $"\ud83d\udd34 API延迟: {ms}ms";
				}
			});
		};

		InitMenu();
		AppendLog("系统初始化完成。");
		InitializeDataService().SafeFireAndForget();
	}

	public async Task InitializeDataService()
	{
		IsLoading = true;
		AppendLog("☁\ufe0f 正在启动网络自检与数据服务...");
		Task.Run(async delegate
		{
			await TimeHelper.SyncTimeAsync();
			if (TimeHelper.IsSynced)
			{
				Application.Current?.Dispatcher.Invoke(delegate
				{
					AppendLog($"\ud83d\udd52 时间已校准: {TimeHelper.BeijingNow:HH:mm:ss}");
				});
			}
		}).SafeFireAndForget();
		await StockVM.LoadBaseCodeNameTable();
		StockVM.StartService();
		AppendLog("\ud83d\ude80 行情心跳引擎已启动！");
		StatusLeft = "数据服务运行中";
		IsLoading = false;
	}

	[RelayCommand]
	private async Task DiagnoseDataSources()
	{
		AppendLog("🩺 开始诊断股票数据源...");
		try
		{
			IReadOnlyList<StockDataDiagnosticItem> results = await new StockDataDiagnostics().RunAsync();
			foreach (StockDataDiagnosticItem item in results)
			{
				AppendLog((item.Success ? "✅ " : "❌ ") + item.Name + "：" + item.Message);
			}
			int passed = results.Count(item => item.Success);
			_dialogService.ShowMessage("数据源诊断完成：" + passed + "/" + results.Count + " 项通过。\n详细结果已写入日志窗口。", "数据源诊断");
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			AppendLog("❌ 数据源诊断失败：" + ex.Message);
		}
	}

	[RelayCommand]
	private void OpenImportExport()
	{
		_dialogService.ShowImportExport();
	}

	[RelayCommand]
	private void OpenFiveDayChart(object obj)
	{
		if (obj == null)
		{
			return;
		}
		try
		{
			string text8 = StockNavigationHelper.GetCode(obj);
			string value = StockNavigationHelper.GetName(obj);
			if (!string.IsNullOrEmpty(text8))
			{
				AppendLog($"📈 正在提取五日分时图: {value}({text8})");
				AnalyticsService.Log("2", "5");
				string imageUrl = StockNavigationHelper.BuildEastMoneyFiveDayImageUrl(text8);
				Application.Current?.Dispatcher.Invoke(delegate
				{
					try
					{
						_dialogService.ShowImage(imageUrl);
					}
					catch (Exception ex7) { Serilog.Log.Warning(ex7, "捕获到未处理异常"); 
						_dialogService.ShowMessage("图表加载异常: " + ex7.Message, "错误");
					}
				});
			}
		}
		catch (Exception ex6) { Serilog.Log.Warning(ex6, "捕获到未处理异常"); 
			AppendLog("❌ 五日图命令执行失败: " + ex6.Message);
		}
	}

	[RelayCommand]
	private void OpenWenCai(object obj)
	{
		if (obj == null)
		{
			return;
		}
		try
		{
			string text6 = StockNavigationHelper.GetCode(obj);
			string text7 = StockNavigationHelper.GetName(obj);
			if (!string.IsNullOrEmpty(text6))
			{
				_dialogService.ShowWenCai(text6, text7);
				AppendLog("🔍 开启问财分析: " + text7);
				AnalyticsService.Log("2", "wc");
			}
		}
		catch (Exception ex5) { Serilog.Log.Warning(ex5, "捕获到未处理异常"); 
			AppendLog("❌ 问财开启失败: " + ex5.Message);
		}
	}

	[RelayCommand]
	private void OpenDongFang(object obj)
	{
		if (obj == null)
		{
			return;
		}
		try
		{
			string text4 = StockNavigationHelper.GetCode(obj);
			string text5 = StockNavigationHelper.GetName(obj);
			if (!string.IsNullOrEmpty(text4))
			{
				_dialogService.ShowLiveChart(text4, text5);
				AppendLog("📊 开启东财详情: " + text5);
				AnalyticsService.Log("2", "df");
			}
		}
		catch (Exception ex4) { Serilog.Log.Warning(ex4, "捕获到未处理异常"); 
			AppendLog("❌ 东财开启失败: " + ex4.Message);
		}
	}

	[RelayCommand]
	private void ConfigPosition(object obj)
	{
		try
		{
			Type type = obj.GetType();
			string text3 = type.GetProperty("Code")?.GetValue(obj)?.ToString() ?? "";
			string stockName = type.GetProperty("Name")?.GetValue(obj)?.ToString() ?? "未知股票";
			if (!string.IsNullOrEmpty(text3))
			{
				_dialogService.ShowPositionConfig(text3, stockName);
				AnalyticsService.Log("15", "0");
			}
		}
		catch (Exception ex3) { Serilog.Log.Warning(ex3, "捕获到未处理异常"); 
			AppendLog("❌ 打开持仓配置失败: " + ex3.Message);
		}
	}

	[RelayCommand]
	private void OpenFolder()
	{
		string text2 = ConfigManager.Load().DataSavePath;
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
		}
		if (!Directory.Exists(text2))
		{
			Directory.CreateDirectory(text2);
		}
		try
		{
			string name = new DirectoryInfo(text2).Name;
			if (Win32Helper.FocusFolderWindow(name))
			{
				StatusLeft = "激活：" + name;
			}
			else
			{
				Process.Start("explorer.exe", text2);
			}
		}
		catch (Exception ex2) { Serilog.Log.Warning(ex2, "捕获到未处理异常"); 
			AppendLog("❌ 打开目录失败: " + ex2.Message);
		}
	}

	[RelayCommand]
	private void ClearCache()
	{
		try
		{
			string text = ConfigManager.Load().DataSavePath;
			if (string.IsNullOrWhiteSpace(text))
			{
				text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
			}
			if (Directory.Exists(text))
			{
				string[] validPrefixes = new string[8] { "复合取数_", "五档盘口_", "分时走势_", "逐笔明细_", "麻雀", "海龟", "历史K线_", "大盘指数_" };
				List<string> list = (from s in Directory.GetFiles(text, "*.txt")
					where validPrefixes.Any((string p) => Path.GetFileName(s)!.StartsWith(p))
					select s).ToList();
				foreach (string item in list)
				{
					File.Delete(item);
				}
				AppendLog($"✅ 清理完成，共剿灭 {list.Count} 个缓存文件！");
			}
		}
		catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			AppendLog("❌ 清理出错: " + ex.Message);
		}
	}

	[RelayCommand]
	private void ClearLog()
	{
		LogVM.Clear();
	}

	[RelayCommand]
	private void SetProxy()
	{
		_dialogService.ShowProxySettings();
	}

	[RelayCommand]
	private void OpenHelp()
	{
		try
		{
			Process.Start(new ProcessStartInfo("https://www.ooppp.com/help.html")
			{
				UseShellExecute = true
			});
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	[RelayCommand]
	private void ToggleChat()
	{
		if (ChatVisibility == Visibility.Visible)
		{
			ChatVisibility = Visibility.Collapsed;
			ChatColumnWidth = new GridLength(0.0);
		}
		else
		{
			ChatVisibility = Visibility.Visible;
			ChatColumnWidth = new GridLength(1.0, GridUnitType.Star);
		}
	}

	[RelayCommand]
	private void OpenSparrow()
	{
		if (!(TimeHelper.BeijingNow.TimeOfDay < new TimeSpan(14, 30, 0)) || _dialogService.ShowConfirm("量化选股建议在 14:30 以后执行，是否强制打开？", "风险确认"))
		{
			AnalyticsService.Log("9", "0");
			_dialogService.ShowSparrowScanner();
		}
	}

	private void InitMenu()
	{
		MenuItems = new ObservableCollection<MenuItemModel>();
		MenuItemModel menuItemModel = new MenuItemModel
		{
			Header = "文件(_F)"
		};
		menuItemModel.Children.Add(new MenuItemModel
		{
			Header = "打开目录",
			Icon = "\ud83d\udcc2",
			Command = OpenFolderCommand
		});
		menuItemModel.Children.Add(new MenuItemModel
		{
			Header = "退出",
			Icon = "❌",
			Command = ExitAppCommand
		});
		MenuItemModel menuItemModel2 = new MenuItemModel
		{
			Header = "数据(_D)"
		};
		menuItemModel2.Children.Add(new MenuItemModel
		{
			Header = "导入/导出股票",
			Icon = "\ud83d\udce5",
			Command = OpenImportExportCommand
		});
		menuItemModel2.Children.Add(new MenuItemModel
		{
			Header = "手动刷新股票代码表",
			Icon = "🔄",
			Command = StockVM.RefreshCodeTableCommand
		});
		menuItemModel2.Children.Add(new MenuItemModel
		{
			Header = "诊断股票数据源",
			Icon = "🩺",
			Command = DiagnoseDataSourcesCommand
		});
		MenuItemModel menuItemModel3 = new MenuItemModel
		{
			Header = "设置(_S)"
		};
		menuItemModel3.Children.Add(new MenuItemModel
		{
			Header = "API接口代理",
			Icon = "\ud83c\udf10",
			Command = SetProxyCommand
		});
		menuItemModel3.Children.Add(new MenuItemModel
		{
			Header = "显示/隐藏聊天室",
			Icon = "\ud83d\udcac",
			Command = ToggleChatCommand
		});
		MenuItems.Add(menuItemModel);
		MenuItems.Add(menuItemModel2);
		MenuItems.Add(menuItemModel3);
		MenuItems.Add(new MenuItemModel
		{
			Header = "帮助(_H)",
			Command = OpenHelpCommand
		});
	}

	public void AppendLog(string message)
	{
		LogVM.Append(message);
		if (!IsAutoOpen || string.IsNullOrEmpty(message) || !message.Contains("所有数据拉取与 AI 语料预处理完成"))
		{
			return;
		}
		Task.Delay(500).ContinueWith(delegate
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				OpenFolderCommand.Execute(null);
			});
		});
	}

	public void Dispose()
	{
		StockVM?.Dispose();
		_chatVM?.Dispose();
	}

	[RelayCommand]
	private void ExitApp()
	{
		Application.Current?.Shutdown();
	}
}
