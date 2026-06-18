using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AIHelper.Helpers;
using AIHelper.Views;
using HandyControl.Controls;

namespace AIHelper.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
	private string _mainTitle = "股票数据助手";

	private string _statusLeft = "就绪";

	private string _latencyText = "";

	private string _statusRight = "在线人数: 获取中...";

	private DateTime _selectedDate = DateTime.Now;

	private bool _isAutoOpen;

	private bool _isAiCompress = true;

	private ChatViewModel _chatVM;

	private GridLength _chatColumnWidth = new GridLength(1.0, GridUnitType.Star);

	private Visibility _chatVisibility;

	public string MainTitle
	{
		get
		{
			return _mainTitle;
		}
		set
		{
			_mainTitle = value;
			OnPropertyChanged("MainTitle");
		}
	}

	public string StatusLeft
	{
		get
		{
			return _statusLeft;
		}
		set
		{
			_statusLeft = value;
			OnPropertyChanged("StatusLeft");
		}
	}

	public string LatencyText
	{
		get
		{
			return _latencyText;
		}
		set
		{
			_latencyText = value;
			OnPropertyChanged("LatencyText");
		}
	}

	public string StatusRight
	{
		get
		{
			return _statusRight;
		}
		set
		{
			_statusRight = value;
			OnPropertyChanged("StatusRight");
		}
	}

	public DateTime SelectedDate
	{
		get
		{
			return _selectedDate;
		}
		set
		{
			_selectedDate = value;
			OnPropertyChanged("SelectedDate");
		}
	}

	public bool IsLoading { get; set; }

	public bool IsAutoOpen
	{
		get
		{
			return _isAutoOpen;
		}
		set
		{
			if (_isAutoOpen != value)
			{
				_isAutoOpen = value;
				OnPropertyChanged("IsAutoOpen");
				if (!IsLoading)
				{
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(18, 1);
					defaultInterpolatedStringHandler.AppendLiteral("⚙\ufe0f 设置更改：自动打开目录 -> ");
					defaultInterpolatedStringHandler.AppendFormatted(value);
					AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				}
			}
		}
	}

	public bool IsAiCompress
	{
		get
		{
			return _isAiCompress;
		}
		set
		{
			if (_isAiCompress != value)
			{
				_isAiCompress = value;
				OnPropertyChanged("IsAiCompress");
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
		}
	}

	public ICommand ClearCacheCommand { get; set; }

	public ICommand ClearLogCommand { get; set; }

	public ObservableCollection<MenuItemModel> MenuItems { get; set; }

	public ICommand OpenFolderCommand { get; set; }

	public ICommand ToggleChatCommand { get; set; }

	public ICommand SetProxyCommand { get; set; }

	public ICommand SetDataSourceCommand { get; set; }

	public ICommand OpenHelpCommand { get; set; }

	public ICommand OpenSparrowCommand { get; set; }

	public ICommand OpenWenCaiCommand { get; set; }

	public ICommand OpenDongFangCommand { get; set; }

	public ICommand OpenAnalyzeMenuCommand { get; set; }

	public ICommand ConfigPositionCommand { get; set; }

	public ICommand OpenFiveDayChartCommand { get; set; }

	public ICommand OpenImportExportCommand { get; set; }

	public StockViewModel StockVM { get; set; }

	public ChatViewModel ChatVM
	{
		get
		{
			return _chatVM;
		}
		set
		{
			_chatVM = value;
			OnPropertyChanged("ChatVM");
			if (_chatVM == null)
			{
				return;
			}
			_chatVM.OnlineCountUpdated += delegate(int count)
			{
				Application.Current.Dispatcher.Invoke(delegate
				{
					MainViewModel mainViewModel = this;
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(11, 1);
					defaultInterpolatedStringHandler.AppendLiteral("聊天室活跃摸鱼人数: ");
					defaultInterpolatedStringHandler.AppendFormatted(count);
					mainViewModel.StatusRight = defaultInterpolatedStringHandler.ToStringAndClear();
				});
			};
		}
	}

	public LogViewModel LogVM { get; set; }

	public GridLength ChatColumnWidth
	{
		get
		{
			return _chatColumnWidth;
		}
		set
		{
			_chatColumnWidth = value;
			OnPropertyChanged("ChatColumnWidth");
		}
	}

	public Visibility ChatVisibility
	{
		get
		{
			return _chatVisibility;
		}
		set
		{
			_chatVisibility = value;
			OnPropertyChanged("ChatVisibility");
		}
	}

	public event PropertyChangedEventHandler PropertyChanged;

	public MainViewModel()
	{
		StockVM = new StockViewModel();
		LogVM = new LogViewModel();
		StockVM.LogAction = AppendLog;
		StockVM.LatencyAction = delegate(long ms)
		{
			Application.Current.Dispatcher.Invoke(delegate
			{
				if (ms < 5)
				{
					MainViewModel mainViewModel = this;
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(11, 1);
					defaultInterpolatedStringHandler.AppendLiteral("⚡ 本地缓存 (");
					defaultInterpolatedStringHandler.AppendFormatted(ms);
					defaultInterpolatedStringHandler.AppendLiteral("ms)");
					mainViewModel.LatencyText = defaultInterpolatedStringHandler.ToStringAndClear();
				}
				else if (ms < 500)
				{
					MainViewModel mainViewModel2 = this;
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(12, 1);
					defaultInterpolatedStringHandler.AppendLiteral("\ud83d\udfe2 API延迟: ");
					defaultInterpolatedStringHandler.AppendFormatted(ms);
					defaultInterpolatedStringHandler.AppendLiteral("ms");
					mainViewModel2.LatencyText = defaultInterpolatedStringHandler.ToStringAndClear();
				}
				else if (ms < 2000)
				{
					MainViewModel mainViewModel3 = this;
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(12, 1);
					defaultInterpolatedStringHandler.AppendLiteral("\ud83d\udfe1 API延迟: ");
					defaultInterpolatedStringHandler.AppendFormatted(ms);
					defaultInterpolatedStringHandler.AppendLiteral("ms");
					mainViewModel3.LatencyText = defaultInterpolatedStringHandler.ToStringAndClear();
				}
				else
				{
					MainViewModel mainViewModel4 = this;
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(12, 1);
					defaultInterpolatedStringHandler.AppendLiteral("\ud83d\udd34 API延迟: ");
					defaultInterpolatedStringHandler.AppendFormatted(ms);
					defaultInterpolatedStringHandler.AppendLiteral("ms");
					mainViewModel4.LatencyText = defaultInterpolatedStringHandler.ToStringAndClear();
				}
			});
		};
		InitCommands();
		InitMenu();
		AppendLog("系统初始化完成。");
		InitializeDataService();
	}

	public async void InitializeDataService()
	{
		IsLoading = true;
		AppendLog("☁\ufe0f 正在启动网络自检与数据服务...");
		Task.Run(async delegate
		{
			await TimeHelper.SyncTimeAsync();
			if (TimeHelper.IsSynced)
			{
				Application.Current.Dispatcher.Invoke(delegate
				{
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(10, 1);
					defaultInterpolatedStringHandler.AppendLiteral("\ud83d\udd52 时间已校准: ");
					defaultInterpolatedStringHandler.AppendFormatted(TimeHelper.BeijingNow, "HH:mm:ss");
					AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				});
			}
		});
		await StockVM.LoadBaseCodeNameTable();
		StockVM.StartService();
		AppendLog("\ud83d\ude80 行情心跳引擎已启动！");
		StatusLeft = "数据服务运行中";
		IsLoading = false;
	}

	private void InitCommands()
	{
		OpenImportExportCommand = new RelayCommand(delegate
		{
			ImportExportWindow importExportWindow = new ImportExportWindow(this);
			importExportWindow.Owner = Application.Current.MainWindow;
			importExportWindow.ShowDialog();
		});
		string imageUrl;
		OpenFiveDayChartCommand = new RelayCommand(delegate(object obj)
		{
			if (obj == null)
			{
				return;
			}
			try
			{
				Type type4 = obj.GetType();
				string text8 = type4.GetProperty("Code")?.GetValue(obj)?.ToString() ?? "";
				string value = type4.GetProperty("Name")?.GetValue(obj)?.ToString() ?? "未知股票";
				if (!string.IsNullOrEmpty(text8))
				{
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(16, 2);
					defaultInterpolatedStringHandler2.AppendLiteral("\ud83d\udcc8 正在提取五日分时图: ");
					defaultInterpolatedStringHandler2.AppendFormatted(value);
					defaultInterpolatedStringHandler2.AppendLiteral("(");
					defaultInterpolatedStringHandler2.AppendFormatted(text8);
					defaultInterpolatedStringHandler2.AppendLiteral(")");
					AppendLog(defaultInterpolatedStringHandler2.ToStringAndClear());
					AnalyticsService.Log("2", "5");
					string value2 = (text8.StartsWith("6") ? "1" : "0");
					string value3 = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();
					defaultInterpolatedStringHandler2 = new DefaultInterpolatedStringHandler(81, 3);
					defaultInterpolatedStringHandler2.AppendLiteral("https://webquotepic.eastmoney.com/GetPic.aspx?imageType=t&type=M4&nid=");
					defaultInterpolatedStringHandler2.AppendFormatted(value2);
					defaultInterpolatedStringHandler2.AppendLiteral(".");
					defaultInterpolatedStringHandler2.AppendFormatted(text8);
					defaultInterpolatedStringHandler2.AppendLiteral("&timespan=");
					defaultInterpolatedStringHandler2.AppendFormatted(value3);
					imageUrl = defaultInterpolatedStringHandler2.ToStringAndClear();
					Application.Current.Dispatcher.Invoke(delegate
					{
						try
						{
							ImageBrowser imageBrowser = new ImageBrowser(new Uri(imageUrl));
							imageBrowser.Owner = Application.Current.MainWindow;
							imageBrowser.Show();
						}
						catch (Exception ex7)
						{
							Growl.Error("图表加载异常: " + ex7.Message);
						}
					});
				}
			}
			catch (Exception ex6)
			{
				AppendLog("❌ 五日图命令执行失败: " + ex6.Message);
			}
		});
		OpenWenCaiCommand = new RelayCommand(delegate(object obj)
		{
			if (obj == null)
			{
				return;
			}
			try
			{
				Type type3 = obj.GetType();
				string text6 = type3.GetProperty("Code")?.GetValue(obj)?.ToString() ?? "";
				string text7 = type3.GetProperty("Name")?.GetValue(obj)?.ToString() ?? "未知股票";
				if (!string.IsNullOrEmpty(text6))
				{
					WenCaiWindow wenCaiWindow = new WenCaiWindow(text6, text7);
					wenCaiWindow.Owner = Application.Current.MainWindow;
					wenCaiWindow.Show();
					AppendLog("\ud83d\udd0d 开启问财分析: " + text7);
					AnalyticsService.Log("2", "wc");
				}
			}
			catch (Exception ex5)
			{
				AppendLog("❌ 问财开启失败: " + ex5.Message);
			}
		});
		OpenDongFangCommand = new RelayCommand(delegate(object obj)
		{
			if (obj == null)
			{
				return;
			}
			try
			{
				Type type2 = obj.GetType();
				string text4 = type2.GetProperty("Code")?.GetValue(obj)?.ToString() ?? "";
				string text5 = type2.GetProperty("Name")?.GetValue(obj)?.ToString() ?? "未知股票";
				if (!string.IsNullOrEmpty(text4))
				{
					LiveChartWindow liveChartWindow = new LiveChartWindow(text4, text5);
					liveChartWindow.Owner = Application.Current.MainWindow;
					liveChartWindow.Show();
					AppendLog("\ud83d\udcca 开启东财详情: " + text5);
					AnalyticsService.Log("2", "df");
				}
			}
			catch (Exception ex4)
			{
				AppendLog("❌ 东财开启失败: " + ex4.Message);
			}
		});
		ConfigPositionCommand = new RelayCommand(delegate(object obj)
		{
			try
			{
				Type type = obj.GetType();
				string text3 = type.GetProperty("Code")?.GetValue(obj)?.ToString() ?? "";
				string stockName = type.GetProperty("Name")?.GetValue(obj)?.ToString() ?? "未知股票";
				if (!string.IsNullOrEmpty(text3))
				{
					PositionWindow positionWindow = new PositionWindow(text3, stockName);
					positionWindow.Owner = Application.Current.MainWindow;
					positionWindow.ShowDialog();
					AnalyticsService.Log("15", "0");
				}
			}
			catch (Exception ex3)
			{
				AppendLog("❌ 打开持仓配置失败: " + ex3.Message);
			}
		});
		OpenFolderCommand = new RelayCommand(delegate
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
			catch (Exception ex2)
			{
				AppendLog("❌ 打开目录失败: " + ex2.Message);
			}
		});
		string[] validPrefixes;
		ClearCacheCommand = new RelayCommand(delegate
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
					validPrefixes = new string[8] { "复合取数_", "五档盘口_", "分时走势_", "逐笔明细_", "麻雀", "海龟", "历史K线_", "大盘指数_" };
					List<string> list = (from s in Directory.GetFiles(text, "*.txt")
						where validPrefixes.Any((string p) => Path.GetFileName(s)!.StartsWith(p))
						select s).ToList();
					foreach (string item in list)
					{
						File.Delete(item);
					}
					DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(18, 1);
					defaultInterpolatedStringHandler.AppendLiteral("✅ 清理完成，共剿灭 ");
					defaultInterpolatedStringHandler.AppendFormatted(list.Count);
					defaultInterpolatedStringHandler.AppendLiteral(" 个缓存文件！");
					AppendLog(defaultInterpolatedStringHandler.ToStringAndClear());
				}
			}
			catch (Exception ex)
			{
				AppendLog("❌ 清理出错: " + ex.Message);
			}
		});
		ClearLogCommand = new RelayCommand(delegate
		{
			LogVM.Clear();
		});
		SetProxyCommand = new RelayCommand(delegate
		{
			ProxyWindow proxyWindow = new ProxyWindow();
			proxyWindow.Owner = Application.Current.MainWindow;
			proxyWindow.ShowDialog();
		});
		SetDataSourceCommand = new RelayCommand(delegate
		{
			DataSourceWindow dataSourceWindow = new DataSourceWindow();
			dataSourceWindow.Owner = Application.Current.MainWindow;
			dataSourceWindow.ShowDialog();
		});
		OpenHelpCommand = new RelayCommand(delegate
		{
			try
			{
				Process.Start(new ProcessStartInfo("https://www.ooppp.com/help.html")
				{
					UseShellExecute = true
				});
			}
			catch
			{
			}
		});
		ToggleChatCommand = new RelayCommand(delegate
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
		});
		OpenSparrowCommand = new RelayCommand(delegate
		{
			if (!(TimeHelper.BeijingNow.TimeOfDay < new TimeSpan(14, 30, 0)) || HandyControl.Controls.MessageBox.Show("量化选股建议在 14:30 以后执行，是否强制打开？", "风险确认", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
			{
				AnalyticsService.Log("9", "0");
				SparrowWindow sparrowWindow = new SparrowWindow(this);
				sparrowWindow.Owner = Application.Current.MainWindow;
				sparrowWindow.Show();
			}
		});
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
			Command = new RelayCommand(delegate
			{
				Environment.Exit(0);
			})
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
		MenuItemModel menuItemModel3 = new MenuItemModel
		{
			Header = "设置(_S)"
		};
		menuItemModel3.Children.Add(new MenuItemModel
		{
			Header = "数据源节点设置",
			Icon = "\ud83d\udd0c",
			Command = SetDataSourceCommand
		});
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
			Application.Current.Dispatcher.Invoke(delegate
			{
				OpenFolderCommand.Execute(null);
			});
		});
	}

	protected void OnPropertyChanged([CallerMemberName] string name = null)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}
