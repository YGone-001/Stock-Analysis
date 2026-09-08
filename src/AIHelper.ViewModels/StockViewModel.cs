using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHelper.Helpers;

using AIHelper.Models;
using AIHelper.Services.StockData;

using HandyControl.Controls;
using Serilog;

#pragma warning disable CS8600, CS8602, CS8618
#pragma warning disable CS8600, CS8602, CS8618
namespace AIHelper.ViewModels;

public partial class StockViewModel : ObservableObject, IDisposable
{
	private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };


	private const string DataFileName = "StockGroups.json";

	private const string NameMapCacheFile = "StockNameMap.json";

	private static readonly (string Code, string Name)[] DefaultBlueChipStocks = new (string Code, string Name)[]
	{
		("000001", "平安银行"),
		("600519", "贵州茅台"),
		("601318", "中国平安"),
		("600036", "招商银行"),
		("601398", "工商银行")
	};

	private readonly string _filePath;

	private ObservableCollection<StockModel> _searchResults = new ObservableCollection<StockModel>();

	private bool _isSearchPopupOpen;

	private string _searchText;

	private bool _isAllStocksSelected;

	private StockGroupModel _selectedGroup;

	private StockModel _currentSelectedStock;

	private CancellationTokenSource? _searchCts;

	private CancellationTokenSource? _cts;

	private bool _isSleepingLogged;

	private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);

	private int _consecutiveRefreshFailures;

	private DateTime _quoteBackoffUntil = DateTime.MinValue;

	private bool _quoteBackoffLogged;

	private bool _cacheNoticeShown;

	public Action<string>? LogAction { get; set; }

	public Action<long>? LatencyAction { get; set; }

	public ObservableCollection<StockGroupModel> StockGroups { get; set; } = new ObservableCollection<StockGroupModel>();


	public ConcurrentDictionary<string, string> StockNameMap { get; set; } = new ConcurrentDictionary<string, string>();

	public ConcurrentDictionary<string, StockModel> GlobalStockCache { get; set; } = new ConcurrentDictionary<string, StockModel>();


	public ObservableCollection<StockModel> SearchResults
	{
		get
		{
			return _searchResults;
		}
		set
		{
			_searchResults = value;
			OnPropertyChanged(nameof(SearchResults));
		}
	}

	public bool IsSearchPopupOpen
	{
		get
		{
			return _isSearchPopupOpen;
		}
		set
		{
			_isSearchPopupOpen = value;
			OnPropertyChanged(nameof(IsSearchPopupOpen));
		}
	}

	public string SearchText
	{
		get
		{
			return _searchText;
		}
		set
		{
			_searchText = value;
			OnPropertyChanged(nameof(SearchText));
			DoHybridSearch(value);
		}
	}

	public string CurrentGroupName => SelectedGroup?.Header ?? "默认分组";

	public bool IsAllStocksSelected
	{
		get
		{
			return _isAllStocksSelected;
		}
		set
		{
			if (_isAllStocksSelected == value)
			{
				return;
			}
			_isAllStocksSelected = value;
			OnPropertyChanged(nameof(IsAllStocksSelected));
			if (SelectedGroup == null || SelectedGroup.Stocks == null)
			{
				return;
			}
			foreach (StockModel stock in SelectedGroup.Stocks)
			{
				stock.IsChecked = value;
			}
		}
	}

	public StockGroupModel SelectedGroup
	{
		get
		{
			return _selectedGroup;
		}
		set
		{
			if (_selectedGroup == value)
			{
				return;
			}
			_selectedGroup = value;
			OnPropertyChanged(nameof(SelectedGroup));
			_isAllStocksSelected = (_selectedGroup?.Stocks?.All((StockModel s) => s.IsChecked)).GetValueOrDefault();
			OnPropertyChanged(nameof(IsAllStocksSelected));
			if (_selectedGroup != null && !_selectedGroup.IsOverview)
			{
				Task.Run(async delegate
				{
					await RefreshSelectedStocks();
				});
			}
		}
	}

	public StockModel CurrentSelectedStock
	{
		get
		{
			return _currentSelectedStock;
		}
		set
		{
			_currentSelectedStock = value;
			OnPropertyChanged(nameof(CurrentSelectedStock));
		}
	}

	[RelayCommand]
	private void OpenChart(object o)
	{
		string code = StockNavigationHelper.GetCode(o);
		if (!string.IsNullOrEmpty(code))
		{
			string name = StockNavigationHelper.GetName(o);
			_dialogService.ShowChart(StockNavigationHelper.BuildEastMoneyQuoteUrl(code, fullScreenChart: true), name + " (" + code + ") 图表");
		}
	}

	[RelayCommand]
	private void ConfirmAddStock(object o)
	{
		if (o is StockModel stockModel)
		{
			AddStockInternal(stockModel.Code, stockModel.Name);
			SearchText = "";
			IsSearchPopupOpen = false;
		}
	}

	[RelayCommand]
	private void QuickAddStock()
	{
		if (SearchResults != null && SearchResults.Count > 0)
		{
			StockModel stockModel = SearchResults[0];
			AddStockInternal(stockModel.Code, stockModel.Name);
			SearchText = "";
			IsSearchPopupOpen = false;
		}
		else
		{
			string text = SearchText?.Trim();
			if (!string.IsNullOrEmpty(text))
			{
				string value;
				string name = (StockNameMap.TryGetValue(text, out value) ? value : "--");
				AddStockInternal(text, name);
				SearchText = "";
				IsSearchPopupOpen = false;
			}
		}
	}

	[RelayCommand]
	private void RemoveStock(object o)
	{
		StockModel stock = o as StockModel;
		if (stock != null && SelectedGroup != null && !SelectedGroup.IsOverview)
		{
			SelectedGroup.Stocks.Remove(stock);
			if (!StockGroups.Any((StockGroupModel g) => !g.IsOverview && g.Stocks.Any((StockModel s) => s.Code == stock.Code)))
			{
				StockModel stockModel = StockGroups[0].Stocks.FirstOrDefault((StockModel s) => s.Code == stock.Code);
				if (stockModel != null)
				{
					StockGroups[0].Stocks.Remove(stockModel);
				}
				GlobalStockCache.TryRemove(stock.Code, out _);
			}
			SaveLocalData();
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke("🗑️ 已删除: " + stock.Name);
			});
			AnalyticsService.Log("7", stock.Code ?? "");
		}
	}

	[RelayCommand]
	private void AddGroup(object o)
	{
		string text = o as string;
		if (string.IsNullOrWhiteSpace(text))
		{
			text = $"分组 {StockGroups.Count}";
		}
		StockGroupModel stockGroupModel = new StockGroupModel
		{
			Header = text
		};
		StockGroups.Add(stockGroupModel);
		SelectedGroup = stockGroupModel;
		AnalyticsService.Log("16", "1");
		SaveLocalData();
	}

	[RelayCommand]
	private void RemoveGroup(object o)
	{
		if (o is StockGroupModel stockGroupModel && !stockGroupModel.IsOverview)
		{
			if (HandyControl.Controls.MessageBox.Show($"确定要删除分组 [{stockGroupModel.Header}] 及其下包含的 {stockGroupModel.Stocks.Count} 只股票吗？\n此操作不可逆！", "删组确认", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) == MessageBoxResult.Yes)
			{
				List<StockModel> list = stockGroupModel.Stocks.ToList();
				StockGroups.Remove(stockGroupModel);
				foreach (StockModel stock in list)
				{
					if (!StockGroups.Any((StockGroupModel g) => !g.IsOverview && g.Stocks.Any((StockModel s) => s.Code == stock.Code)))
					{
						StockModel stockModel = StockGroups[0].Stocks.FirstOrDefault((StockModel s) => s.Code == stock.Code);
						if (stockModel != null)
						{
							StockGroups[0].Stocks.Remove(stockModel);
						}
						GlobalStockCache.TryRemove(stock.Code, out _);
					}
				}
				if (StockGroups.Count > 0)
				{
					SelectedGroup = StockGroups[0];
				}
				AnalyticsService.Log("16", "0");
				SaveLocalData();
			}
		}
	}

	[RelayCommand]
	private void RenameGroup(object o)
	{
		StockGroupModel group = o as StockGroupModel;
		if (group == null || group.IsOverview)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke("⚠️ 总览页属于系统层，无法重命名。");
			});
		}
		else
		{
			AnalyticsService.Log("0", "1");
			string result = _dialogService.ShowInput("重命名分组", group.Header);
			if (!string.IsNullOrWhiteSpace(result))
			{
				group.Header = result.Trim();
				SaveLocalData();
				Application.Current?.Dispatcher.Invoke(delegate
				{
					LogAction?.Invoke("✏️ 分组已重命名为: " + group.Header);
				});
			}
		}
	}

	[RelayCommand]
	private async Task ManualRefresh()
	{
		Application.Current?.Dispatcher.Invoke(delegate
		{
			LogAction?.Invoke("🔄 手动刷新数据...");
		});
		AnalyticsService.Log("0", "0");
		ResetQuoteBackoff();
		await RefreshAll();
	}

	[RelayCommand]
	private async Task RefreshCodeTable()
	{
		await RefreshCodeNameCacheAsync(forceRefresh: true);
	}

	[RelayCommand]
	private void MoveStockToGroup(object o)
	{
		StockGroupModel targetGroup = o as StockGroupModel;
		StockModel stockToMove = CurrentSelectedStock;
		if (targetGroup != null && stockToMove != null && SelectedGroup != null && !SelectedGroup.IsOverview && targetGroup != SelectedGroup)
		{
			if (!targetGroup.Stocks.Any((StockModel s) => s.Code == stockToMove.Code))
			{
				targetGroup.Stocks.Add(stockToMove);
			}
			SelectedGroup.Stocks.Remove(stockToMove);
			SaveLocalData();
			AnalyticsService.Log("8", "0");
			Application.Current?.Dispatcher.Invoke(delegate
			{
				Action<string>? logAction = LogAction;
				if (logAction != null)
				{
					logAction!($"🚛 已移动 {stockToMove.Name} 到 [{targetGroup.Header}]");
				}
			});
		}
	}

	private readonly IStockDataProvider _dataProvider;
	private readonly AIHelper.Services.IDialogService _dialogService;

	public StockViewModel(IStockDataProvider dataProvider, AIHelper.Services.IDialogService dialogService)
	{
		_dataProvider = dataProvider;
		_dialogService = dialogService;
		_filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StockGroups.json");
		NetworkHelper.StockDataStatusChanged += OnStockDataStatusChanged;
		LoadLocalData();
	}

	public List<(string Code, string Name)> GetSelectedStocks()
	{
		if (SelectedGroup == null || SelectedGroup.Stocks == null)
		{
			return new List<(string, string)>();
		}
		return (from s in SelectedGroup.Stocks
			where s.IsChecked
			select (s.Code, s.Name)).ToList();
	}

	public StockGroupModel AddGroup(string groupName, List<StockModel> stocks)
	{
		StockGroupModel newGroup = new StockGroupModel
		{
			Header = groupName
		};
		foreach (StockModel stock in stocks)
		{
			if (GlobalStockCache.TryGetValue(stock.PureCode, out var value))
			{
				newGroup.Stocks.Add(value);
				continue;
			}
			GlobalStockCache[stock.PureCode] = stock;
			StockGroups[0].Stocks.Add(stock);
			newGroup.Stocks.Add(stock);
		}
		Application.Current?.Dispatcher.Invoke(delegate
		{
			StockGroups.Add(newGroup);
			SelectedGroup = newGroup;
		});
		SaveLocalData();
		return newGroup;
	}

	public void UpdateGroup(StockGroupModel group, string groupName, List<StockModel> stocks)
	{
		if (group == null || group.IsOverview)
		{
			return;
		}

		var resolved = new List<StockModel>(stocks.Count);
		foreach (StockModel stock in stocks)
		{
			if (GlobalStockCache.TryGetValue(stock.PureCode, out StockModel? cached))
			{
				resolved.Add(cached);
				continue;
			}
			GlobalStockCache[stock.PureCode] = stock;
			StockGroups[0].Stocks.Add(stock);
			resolved.Add(stock);
		}

		Application.Current?.Dispatcher.Invoke(delegate
		{
			group.Header = groupName;
			group.Stocks.Clear();
			foreach (StockModel stock in resolved)
			{
				group.Stocks.Add(stock);
			}
			SelectedGroup = group;
		});
		SaveLocalData();
	}

	public void LoadLocalData()
	{
		StockGroups.Clear();
		StockGroupModel item = new StockGroupModel
		{
			GroupId = "OVERVIEW_ID",
			Header = "总览",
			IsOverview = true
		};
		StockGroups.Add(item);
		if (File.Exists(_filePath))
		{
			try
			{
				List<StockGroupModel> list = JsonSerializer.Deserialize<List<StockGroupModel>>(File.ReadAllText(_filePath));
				if (list != null)
				{
					foreach (StockGroupModel item2 in list)
					{
						if (item2.Stocks == null)
						{
							item2.Stocks = new ObservableCollection<StockModel>();
						}
						StockGroups.Add(item2);
					}
				}
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
		}
		if (StockGroups.Count == 1)
		{
			StockGroupModel stockGroupModel = new StockGroupModel
			{
				Header = "我的自选"
			};
			foreach (var stock in DefaultBlueChipStocks)
			{
				stockGroupModel.Stocks.Add(new StockModel
				{
					Code = stock.Code,
					Name = stock.Name,
					IsChecked = true
				});
			}
			StockGroups.Add(stockGroupModel);
			SaveLocalData();
		}
		if (NormalizeLocalStockNames())
		{
			SaveLocalData();
		}
		RebuildGlobalCacheAndOverview();
	}

	public void RebuildGlobalCacheAndOverview()
	{
		StockGroupModel stockGroupModel = StockGroups[0];
		stockGroupModel.Stocks.Clear();
		GlobalStockCache.Clear();
		for (int i = 1; i < StockGroups.Count; i++)
		{
			StockGroupModel stockGroupModel2 = StockGroups[i];
			for (int j = 0; j < stockGroupModel2.Stocks.Count; j++)
			{
				StockModel stockModel = stockGroupModel2.Stocks[j];
				if (!string.IsNullOrEmpty(stockModel.Code))
				{
					string pureCode = stockModel.PureCode;
					if (GlobalStockCache.TryGetValue(pureCode, out var value))
					{
						stockGroupModel2.Stocks[j] = value;
						continue;
					}
					GlobalStockCache[pureCode] = stockModel;
					stockGroupModel.Stocks.Add(stockModel);
				}
			}
		}
		if (SelectedGroup == null)
		{
			SelectedGroup = StockGroups[0];
		}
	}

	public void SaveLocalData()
	{
		List<StockGroupModel> value = StockGroups.Skip(1).ToList();
		try
		{
			JsonSerializerOptions options = _jsonOptions;
			string contents = JsonSerializer.Serialize(value, options);
			File.WriteAllText(_filePath, contents);
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	public async Task LoadBaseCodeNameTable()
	{
		string cachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StockNameMap.json");
		Stopwatch watch = Stopwatch.StartNew();
		bool flag = false;
		if (TryLoadNameMapCache(cachePath))
		{
			flag = true;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke("📂 读取今日代码表缓存...");
			});
		}
		else
		{
			foreach (string fallbackPath in GetFallbackNameMapCachePaths())
			{
				if (TryLoadNameMapCache(fallbackPath))
				{
					SaveNameMapCache(cachePath);
					flag = true;
					break;
				}
			}
		}
		if (!flag)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke("\ud83c\udf10 同步全量代码 & ETF列表...");
			});
			if (StockNameMap.Count == 0)
			{
				LoadSeedNameMap();
			}
			try
			{
				ParseCodeNameJson((await _dataProvider.GetDataAsync(StockDataRequest.Parse("/api/codes"))).Json);
			}
			catch (Exception ex3) { Serilog.Log.Warning(ex3, "捕获到未处理异常"); 
				Exception ex2 = ex3;
				Application.Current?.Dispatcher.Invoke(delegate
				{
					LogAction?.Invoke("⚠\ufe0f 股票表获取失败: " + ex2.Message);
				});
			}
			try
			{
				ParseEtfJson((await _dataProvider.GetDataAsync(StockDataRequest.Parse("/api/etf?limit=10000"))).Json);
			}
			catch (Exception ex4) { Serilog.Log.Warning(ex4, "捕获到未处理异常"); 
				Exception ex = ex4;
				Application.Current?.Dispatcher.Invoke(delegate
				{
					LogAction?.Invoke("⚠\ufe0f ETF表获取失败: " + ex.Message);
				});
			}
			if (StockNameMap.Count > 0)
			{
				NormalizeKnownStockNameMap();
				try
				{
					SaveNameMapCache(cachePath);
					Application.Current?.Dispatcher.Invoke(delegate
					{
						Action<string>? logAction = LogAction;
						if (logAction != null)
						{
							logAction!($"✅ 代码表更新完成 (股票+ETF 共 {StockNameMap.Count} 条)");
						}
					});
				}
				catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
			}
		}
		watch.Stop();
		LatencyAction?.Invoke(watch.ElapsedMilliseconds);
		RefreshAllNames();
	}

	public async Task RefreshCodeNameCacheAsync(bool forceRefresh)
	{
		Application.Current?.Dispatcher.Invoke(delegate
		{
			LogAction?.Invoke(forceRefresh ? "🔄 正在手动刷新股票代码表..." : "🌐 正在同步股票代码表...");
		});
		string suffix = forceRefresh ? "?force=1" : "";
		Task<StockDataResult> stockTask = _dataProvider.GetDataAsync(StockDataRequest.Parse("/api/codes" + suffix));
		Task<StockDataResult> etfTask = _dataProvider.GetDataAsync(StockDataRequest.Parse("/api/etf" + suffix));
		await Task.WhenAll(stockTask, etfTask);
		StockDataResult stockResult = await stockTask;
		StockDataResult etfResult = await etfTask;
		if (stockResult.Success) ParseCodeNameJson(stockResult.Json);
		if (etfResult.Success) ParseEtfJson(etfResult.Json);
		if (StockNameMap.Count == 0) LoadSeedNameMap();
		NormalizeKnownStockNameMap();
		await NetworkHelper.MergeStockNameCacheAsync(StockNameMap, forceRefresh ? "ManualRefresh" : "StockViewModel");
		RefreshAllNames();
		Application.Current?.Dispatcher.Invoke(delegate
		{
			if (stockResult.UsedCache || etfResult.UsedCache)
			{
				LogAction?.Invoke("⚠️ 公开源暂不可用，正在使用本地代码表缓存。");
			}
			else if (stockResult.Success && etfResult.Success)
			{
				LogAction?.Invoke("✅ 股票代码表刷新完成，共 " + StockNameMap.Count + " 条。");
			}
			else
			{
				LogAction?.Invoke("⚠️ 代码表刷新未完整成功，已保留现有缓存。");
			}
		});
	}

	private void OnStockDataStatusChanged(StockDataResult result)
	{
		if (result == null) return;
		if (result.UsedCache && (!_cacheNoticeShown || !string.IsNullOrWhiteSpace(result.Error)))
		{
			_cacheNoticeShown = true;
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke(result.IsStale ? "📦 正在使用本地代码表缓存，公开源将在后台刷新。" : "📦 正在使用本地代码表缓存。");
			});
		}
		if (result.IsBackgroundRefresh && result.Success && (result.Endpoint.StartsWith("/api/codes") || result.Endpoint.StartsWith("/api/etf")))
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				if (result.Endpoint.StartsWith("/api/etf")) ParseEtfJson(result.Json); else ParseCodeNameJson(result.Json);
				NormalizeKnownStockNameMap();
				RefreshAllNames();
				LogAction?.Invoke("✅ 后台代码表同步完成。");
			});
		}
	}

	private bool TryLoadNameMapCache(string cachePath)
	{
		try
		{
			if (!File.Exists(cachePath))
			{
				return false;
			}
			ConcurrentDictionary<string, string> dictionary = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(File.ReadAllText(cachePath));
			if (dictionary == null || dictionary.Count == 0)
			{
				return false;
			}
			StockNameMap = dictionary;
			return true;
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return false;
		}
	}

	private IEnumerable<string> GetFallbackNameMapCachePaths()
	{
		yield return Path.Combine(Environment.CurrentDirectory, "StockNameMap.json");
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (!string.IsNullOrWhiteSpace(desktop))
		{
			yield return Path.Combine(desktop, "stock", "stock", "StockNameMap.json");
		}
	}

	private void SaveNameMapCache(string cachePath)
	{
		_ = NetworkHelper.MergeStockNameCacheAsync(StockNameMap, "StockViewModel");
	}

	private void LoadSeedNameMap()
	{
		StockNameMap["000001"] = "平安银行";
		StockNameMap["000002"] = "万科A";
		StockNameMap["000300"] = "沪深300";
		StockNameMap["399001"] = "深证成指";
		StockNameMap["399006"] = "创业板指";
		StockNameMap["510300"] = "沪深300ETF";
		StockNameMap["600000"] = "浦发银行";
		StockNameMap["600036"] = "招商银行";
		StockNameMap["600519"] = "贵州茅台";
		StockNameMap["601318"] = "中国平安";
		StockNameMap["601398"] = "工商银行";
	}

	private void NormalizeKnownStockNameMap()
	{
		foreach (var stock in DefaultBlueChipStocks)
		{
			StockNameMap[stock.Code] = stock.Name;
		}
		StockNameMap["000002"] = "万科A";
		StockNameMap["000300"] = "沪深300";
		StockNameMap["399001"] = "深证成指";
		StockNameMap["399006"] = "创业板指";
		StockNameMap["510300"] = "沪深300ETF";
		StockNameMap["600000"] = "浦发银行";
	}

	private bool NormalizeLocalStockNames()
	{
		bool changed = false;
		foreach (StockGroupModel group in StockGroups.Skip(1))
		{
			foreach (StockModel stock in group.Stocks)
			{
				if (stock == null || string.IsNullOrWhiteSpace(stock.PureCode))
				{
					continue;
				}
				string name = GetSeedStockName(stock.PureCode);
				if (!string.IsNullOrEmpty(name) && stock.Name != name)
				{
					stock.Name = name;
					changed = true;
				}
			}
		}
		return changed;
	}

	private static string GetSeedStockName(string code)
	{
		foreach (var stock in DefaultBlueChipStocks)
		{
			if (stock.Code == code)
			{
				return stock.Name;
			}
		}
		return code switch
		{
			"000002" => "万科A",
			"000300" => "沪深300",
			"399001" => "深证成指",
			"399006" => "创业板指",
			"510300" => "沪深300ETF",
			"600000" => "浦发银行",
			_ => ""
		};
	}

	private void ParseCodeNameJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("codes", out var value2) || value2.ValueKind != JsonValueKind.Array)
			{
				return;
			}
			foreach (JsonElement item in value2.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					string text = "";
					string value3 = "";
					if (item.TryGetProperty("code", out var value4) && value4.ValueKind == JsonValueKind.String)
					{
						text = value4.GetString() ?? "";
					}
					if (item.TryGetProperty("name", out var value5) && value5.ValueKind == JsonValueKind.String)
					{
						value3 = value5.GetString() ?? "";
					}
					if (!string.IsNullOrEmpty(text))
					{
						StockNameMap[text] = value3;
					}
				}
			}
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	private void ParseEtfJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("list", out var value2) || value2.ValueKind != JsonValueKind.Array)
			{
				return;
			}
			foreach (JsonElement item in value2.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Object)
				{
					string text = "";
					string value3 = "";
					if (item.TryGetProperty("code", out var value4) && value4.ValueKind == JsonValueKind.String)
					{
						text = value4.GetString() ?? "";
					}
					if (item.TryGetProperty("name", out var value5) && value5.ValueKind == JsonValueKind.String)
					{
						value3 = value5.GetString() ?? "";
					}
					if (!string.IsNullOrEmpty(text))
					{
						StockNameMap[text] = value3;
					}
				}
			}
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	private void RefreshAllNames()
	{
		Application.Current?.Dispatcher.Invoke(delegate
		{
			foreach (StockModel value2 in GlobalStockCache.Values)
			{
				if (StockNameMap.TryGetValue(value2.PureCode, out var value))
				{
					value2.Name = value;
				}
			}
		});
	}

	private void DoHybridSearch(string keyword)
	{
		string keyword2 = keyword;
		_searchCts?.Cancel();
		_searchCts?.Cancel();
		_searchCts?.Dispose();
		_searchCts = new CancellationTokenSource();
		CancellationToken token = _searchCts!.Token;
		if (string.IsNullOrWhiteSpace(keyword2))
		{
			IsSearchPopupOpen = false;
			return;
		}
		List<StockModel> onlineResults;
		Task.Run(async delegate
		{
			List<StockModel> localResults = (from kvp in StockNameMap.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> kvp) => kvp.Key.Contains(keyword2) || kvp.Value.Contains(keyword2)).Take(10)
				select new StockModel
				{
					Code = kvp.Key,
					Name = kvp.Value
				}).ToList();
			Application.Current?.Dispatcher.Invoke(delegate
			{
				SearchResults.Clear();
				foreach (StockModel item in localResults)
				{
					SearchResults.Add(item);
				}
				IsSearchPopupOpen = SearchResults.Count > 0;
			});
			await Task.Delay(300, token);
			if (token.IsCancellationRequested)
			{
				return;
			}
			try
			{
				string json = (await _dataProvider.GetDataAsync(StockDataRequest.Parse("/api/search?keyword=" + Uri.EscapeDataString(keyword2)))).Json;
				if (token.IsCancellationRequested)
				{
					return;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(json);
				JsonElement rootElement = jsonDocument.RootElement;
				if (rootElement.TryGetProperty("code", out var value) && value.GetInt32() == 0 && rootElement.TryGetProperty("data", out var value2))
				{
					onlineResults = new List<StockModel>();
					foreach (JsonElement item2 in value2.EnumerateArray())
					{
						string text = item2.GetProperty("code").GetString() ?? "";
						string name = item2.GetProperty("name").GetString() ?? "";
						if (!string.IsNullOrEmpty(text))
						{
							StockNameMap[text] = name;
							onlineResults.Add(new StockModel
							{
								Code = text,
								Name = name
							});
						}
					}
					Application.Current?.Dispatcher.Invoke(delegate
					{
						foreach (StockModel netItem in onlineResults)
						{
							if (!SearchResults.Any((StockModel s) => s.Code == netItem.Code))
							{
								SearchResults.Add(netItem);
							}
						}
						IsSearchPopupOpen = SearchResults.Count > 0;
					});
					SaveNameMapCache(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StockNameMap.json"));
				}
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
		}, token);
	}

	private void AddStockInternal(string code, string name)
	{
		string code2 = code;
		string name2 = name;
		if (SelectedGroup == null || SelectedGroup.IsOverview)
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke("⚠\ufe0f 请先选择一个具体分组。");
			});
			return;
		}
		if (SelectedGroup.Stocks.Any((StockModel s) => s.PureCode == code2))
		{
			Application.Current?.Dispatcher.Invoke(delegate
			{
				LogAction?.Invoke("⚠\ufe0f " + name2 + " 已在当前组中。");
			});
			return;
		}
		StockModel stockModel;
		if (GlobalStockCache.TryGetValue(code2, out var value))
		{
			stockModel = value;
		}
		else
		{
			stockModel = new StockModel
			{
				Code = code2,
				Name = name2,
				IsChecked = true
			};
			GlobalStockCache[code2] = stockModel;
			StockGroups[0].Stocks.Add(stockModel);
		}
		SelectedGroup.Stocks.Add(stockModel);
		SaveLocalData();
		Task.Run(async delegate
		{
			await RefreshSelectedStocks();
		});
		Application.Current?.Dispatcher.Invoke(delegate
		{
			LogAction?.Invoke("✅ 已添加: " + name2);
		});
		AnalyticsService.Log("6", code2 ?? "");
	}

	public bool AddStockFromImport(string code)
	{
		string code2 = code;
		if (string.IsNullOrWhiteSpace(code2))
		{
			return false;
		}
		string value;
		string name = (StockNameMap.TryGetValue(code2, out value) ? value : "--");
		if (SelectedGroup == null || SelectedGroup.IsOverview)
		{
			if (StockGroups.Count <= 1)
			{
				return false;
			}
			Application.Current?.Dispatcher.Invoke(() => SelectedGroup = StockGroups[1]);
		}
		if (SelectedGroup.Stocks.Any((StockModel s) => s.PureCode == code2))
		{
			return false;
		}
		AddStockInternal(code2, name);
		return true;
	}

	public string GetExportText(bool exportAllTabs)
	{
		StringBuilder stringBuilder = new StringBuilder();
		List<StockGroupModel> list = new List<StockGroupModel>();
		if (exportAllTabs)
		{
			list = StockGroups.Where((StockGroupModel g) => !g.IsOverview).ToList();
		}
		else if (SelectedGroup != null && !SelectedGroup.IsOverview)
		{
			list.Add(SelectedGroup);
		}
		foreach (StockGroupModel item in list)
		{
			List<StockModel> list2 = item.Stocks.Where((StockModel s) => s.IsChecked).ToList();
			if (list2.Count == 0)
			{
				continue;
			}
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(3, 1, stringBuilder2);
			handler.AppendLiteral("\nG");
			handler.AppendFormatted(item.Header);
			handler.AppendLiteral("G");
			stringBuilder2.AppendLine(ref handler);
			foreach (StockModel item2 in list2)
			{
				stringBuilder.AppendLine(item2.Code);
			}
		}
		return stringBuilder.ToString().Trim();
	}

	public void AddStockFromChat(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return;
		}
		string value;
		string name = (StockNameMap.TryGetValue(code, out value) ? value : "--");
		StockGroupModel targetGroup = StockGroups.FirstOrDefault((StockGroupModel g) => g.Header == "聊天室");
		if (targetGroup == null)
		{
			targetGroup = new StockGroupModel
			{
				Header = "聊天室"
			};
			Application.Current?.Dispatcher.Invoke(delegate
			{
				StockGroups.Add(targetGroup);
			});
		}
		SelectedGroup = targetGroup;
		AddStockInternal(code, name);
		AnalyticsService.Log("6", "C" + code);
	}

	public async Task RefreshAll()
	{
		await RefreshSelectedStocks();
	}

	public async Task RefreshSelectedStocks()
	{
		List<StockModel> list = GlobalStockCache.Values.ToList();
		if (list.Count == 0)
		{
			return;
		}
		if (TimeHelper.BeijingNow < _quoteBackoffUntil)
		{
			return;
		}
		if (!await _refreshGate.WaitAsync(0))
		{
			return;
		}
		Stopwatch watch = Stopwatch.StartNew();
		bool[] results = Array.Empty<bool>();
		try
		{
			results = await Task.WhenAll(list.Chunk(50).Select(async delegate(StockModel[] chunk)
			{
				try
				{
					string codes = string.Join(",", chunk.Select((StockModel stock) => stock.PureCode));
					StockDataResult result = await _dataProvider.GetDataAsync(StockDataRequest.Parse("/api/quote?code=" + codes));
					return result.Success && UpdateBatchStockUI(result.Json) > 0;
				}
				catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
					return false;
				}
			}));
		}
		finally
		{
			_refreshGate.Release();
		}
		watch.Stop();
		LatencyAction?.Invoke(watch.ElapsedMilliseconds);
		if (results.Length > 0 && results.All(success => success))
		{
			bool recovered = _consecutiveRefreshFailures > 0 || _quoteBackoffLogged;
			_consecutiveRefreshFailures = 0;
			_quoteBackoffUntil = DateTime.MinValue;
			_quoteBackoffLogged = false;
			if (recovered) Application.Current?.Dispatcher.Invoke(delegate { LogAction?.Invoke("✅ 行情公开源已恢复，刷新间隔恢复为 3 秒。"); });
		}
		else
		{
			_consecutiveRefreshFailures++;
			if (_consecutiveRefreshFailures >= 3)
			{
				_quoteBackoffUntil = TimeHelper.BeijingNow.AddSeconds(30);
				if (!_quoteBackoffLogged)
				{
					_quoteBackoffLogged = true;
					Application.Current?.Dispatcher.Invoke(delegate { LogAction?.Invoke("⚠️ 行情连续失败，暂停自动请求 30 秒。"); });
				}
			}
		}
	}

	private int UpdateBatchStockUI(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return 0;
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Array)
			{
				return 0;
			}
			List<(string Code, double Price, double LastClose, double Percent, double Open, double High, double Low)> updates = new List<(string, double, double, double, double, double, double)>();
			foreach (JsonElement item5 in value.EnumerateArray())
			{
				if (item5.ValueKind != JsonValueKind.Object)
				{
					continue;
				}
				string text = "";
				if (item5.TryGetProperty("Code", out var value2) && value2.ValueKind == JsonValueKind.String)
				{
					text = value2.GetString() ?? "";
				}
				if (!string.IsNullOrEmpty(text) && item5.TryGetProperty("K", out var value3) && value3.ValueKind == JsonValueKind.Object)
				{
					double num = 0.0;
					double num2 = 0.0;
					double item = 0.0;
					double item2 = 0.0;
					double item3 = 0.0;
					double item4 = 0.0;
					if (value3.TryGetProperty("Close", out var value4) && value4.ValueKind == JsonValueKind.Number)
					{
						num = value4.GetDouble() / 1000.0;
					}
					JsonElement value6;
					if (value3.TryGetProperty("Last", out var value5) && value5.ValueKind == JsonValueKind.Number)
					{
						num2 = value5.GetDouble() / 1000.0;
					}
					else if (value3.TryGetProperty("PreClose", out value6) && value6.ValueKind == JsonValueKind.Number)
					{
						num2 = value6.GetDouble() / 1000.0;
					}
					if (num2 > 0.0 && num > 0.0)
					{
						item = (num - num2) / num2 * 100.0;
					}
					if (value3.TryGetProperty("Open", out var value7) && value7.ValueKind == JsonValueKind.Number)
					{
						item2 = value7.GetDouble() / 1000.0;
					}
					if (value3.TryGetProperty("High", out var value8) && value8.ValueKind == JsonValueKind.Number)
					{
						item3 = value8.GetDouble() / 1000.0;
					}
					if (value3.TryGetProperty("Low", out var value9) && value9.ValueKind == JsonValueKind.Number)
					{
						item4 = value9.GetDouble() / 1000.0;
					}
					updates.Add((text, num, num2, item, item2, item3, item4));
				}
			}
			if (updates.Count <= 0)
			{
				return 0;
			}
			Application.Current?.Dispatcher.Invoke(delegate
			{
				foreach (var item6 in updates)
				{
					if (GlobalStockCache.TryGetValue(item6.Code, out var value10))
					{
						value10.Price = item6.Price;
						value10.LastClose = item6.LastClose;
						value10.Percent = item6.Percent;
						value10.Open = item6.Open;
						value10.High = item6.High;
						value10.Low = item6.Low;
					}
				}
			});
			return updates.Count;
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return 0;
		}
	}

	private void ResetQuoteBackoff()
	{
		_consecutiveRefreshFailures = 0;
		_quoteBackoffUntil = DateTime.MinValue;
		_quoteBackoffLogged = false;
	}

	public void StartService()
	{
		if (_cts != null)
		{
			return;
		}
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts!.Token;
		Task.Run(async delegate
		{
			await RefreshAll();
			while (!token.IsCancellationRequested)
			{
				if (IsTradingTime())
				{
					_isSleepingLogged = false;
					await RefreshAll();
					try
					{
						TimeSpan delay = _quoteBackoffUntil > TimeHelper.BeijingNow ? _quoteBackoffUntil - TimeHelper.BeijingNow : TimeSpan.FromSeconds(3);
						await Task.Delay(delay, token);
					}
					catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
						break;
					}
				}
				else
				{
					if (!_isSleepingLogged)
					{
						Application.Current?.Dispatcher.Invoke(delegate
						{
							LogAction?.Invoke("\ud83d\udca4 非交易时间，暂停自动刷新...");
						});
						_isSleepingLogged = true;
					}
					try
					{
						await Task.Delay(1000, token);
					}
					catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
						break;
					}
				}
			}
		}, token);
	}

	private bool IsTradingTime()
	{
		DateTime beijingNow = TimeHelper.BeijingNow;
		if (beijingNow.DayOfWeek == DayOfWeek.Saturday || beijingNow.DayOfWeek == DayOfWeek.Sunday)
		{
			return false;
		}
		TimeSpan timeOfDay = beijingNow.TimeOfDay;
		if (!(timeOfDay >= new TimeSpan(9, 15, 0)) || !(timeOfDay <= new TimeSpan(11, 30, 0)))
		{
			if (timeOfDay >= new TimeSpan(13, 0, 0))
			{
				return timeOfDay <= new TimeSpan(15, 0, 0);
			}
			return false;
		}
		return true;
	}

	public void Dispose()
	{
		NetworkHelper.StockDataStatusChanged -= OnStockDataStatusChanged;
		if (_searchCts != null)
		{
			_searchCts.Cancel();
			_searchCts.Dispose();
			_searchCts = null;
		}
		if (_cts != null)
		{
			_cts.Cancel();
			_cts.Dispose();
			_cts = null;
		}
		_refreshGate?.Dispose();
	}

	
}
