using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AIHelper.Helpers;

using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using HandyControl.Controls;
using Serilog;

#pragma warning disable CS8602, CS8618
#pragma warning disable CS8602, CS8618
namespace AIHelper.ViewModels;

public partial class SparrowViewModel : ObservableObject
{
    private readonly MainViewModel _mainVm;
    private readonly SparrowScannerService _scannerService;
    private readonly SparrowClassicScanner _classicScanner;
    private readonly SparrowComparisonService _comparisonService;
    private readonly SparrowParameterUiState _parameterState = new();
    private CancellationTokenSource _cts;

    public IReadOnlyList<SparrowStrategyMode> AvailableStrategyModes { get; } =
        Enum.GetValues<SparrowStrategyMode>();

    public IReadOnlyList<SparrowStrategyModeOption> AvailableStrategyModeOptions { get; } =
        new SparrowStrategyModeOption[]
        {
            new(SparrowStrategyMode.Classic),
            new(SparrowStrategyMode.V2),
            new(SparrowStrategyMode.Compare)
        };

    public SparrowStrategyMode StrategyMode
    {
        get => _parameterState.StrategyMode;
        set => _parameterState.StrategyMode = value;
    }

    public ISparrowStrategyUiParameters Parameters => _parameterState.ActiveParameters;
    public SparrowClassicUiParameters ClassicParameters => _parameterState.ClassicParameters;
    public SparrowV2UiParameters V2Parameters => _parameterState.V2Parameters;
    public SparrowSystemSettings SystemSettings => _parameterState.SystemSettings;
    public bool IsClassicMode => _parameterState.IsClassicMode;
    public bool IsV2Mode => _parameterState.IsV2Mode;
    public bool IsCompareMode => _parameterState.IsCompareMode;
    public bool ShowBaseParameters => _parameterState.ShowBaseParameters;
    public bool ShowV2Parameters => _parameterState.ShowV2Parameters;
    public bool ShowSystemParameters => _parameterState.ShowSystemParameters;
    public string StrategyDescription => _parameterState.StrategyDescription;
    public string BaseParametersHeader => _parameterState.BaseParametersHeader;
    public string V2ParametersHeader => _parameterState.V2ParametersHeader;
    public string ComparisonHint => _parameterState.ComparisonHint;
    public string CurrentPresetName => _parameterState.CurrentPresetName;

    // UI States
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set
        {
            if (_isScanning != value)
            {
                _isScanning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartButtonText));
            }
        }
    }

    public string StartButtonText => IsScanning
        ? "⏹ 停止扫描"
        : StrategyMode switch
        {
            SparrowStrategyMode.Classic => "🚀 执行麻雀 Classic (14:30)",
            SparrowStrategyMode.Compare => "🧪 执行 Classic / V2 对照",
            _ => "🚀 执行麻雀 V2"
        };

    private string _logText = "⚡ 东财引擎已就绪！【智能 Cookie 轮换系统】已实装，随时准备金蝉脱壳！\n";
    public string LogText
    {
        get => _logText;
        set { _logText = value; OnPropertyChanged(); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    private double _progressMax = 100;
    public double ProgressMax
    {
        get => _progressMax;
        set { _progressMax = value; OnPropertyChanged(); }
    }



    public SparrowViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _parameterState.PropertyChanged += (_, args) =>
        {
            OnPropertyChanged(args.PropertyName);
            if (args.PropertyName == nameof(SparrowParameterUiState.ActiveParameters))
            {
                OnPropertyChanged(nameof(Parameters));
            }
            if (args.PropertyName == nameof(SparrowParameterUiState.StrategyMode))
            {
                OnPropertyChanged(nameof(StartButtonText));
            }
        };
        var sharedKlineCache = new SparrowMarketDataCache();
        _scannerService = new SparrowScannerService(mainVm.DataProvider, sharedKlineCache);
        _classicScanner = new SparrowClassicScanner(mainVm.DataProvider, klineCache: sharedKlineCache);
        _comparisonService = new SparrowComparisonService(mainVm.DataProvider, klineCache: sharedKlineCache);
    }

    private void AppendLog(string msg, bool isHighlight = false)
    {
        string prefix = $"[{DateTime.Now:HH:mm:ss}] {(isHighlight ? "🔍 " : "")}";
        LogText += $"{prefix}{msg}\n";
    }

    [RelayCommand]
    private void ResetRecommendedDefaults()
    {
        _parameterState.ResetRecommendedDefaults();
        OnPropertyChanged(nameof(Parameters));
        AppendLog("已恢复当前模式的推荐默认（14:30 均衡）；缓存与并发设置保持不变。");
    }

    [RelayCommand]
    private async Task Test()
    {
        AppendLog("\n🪺 [网络诊断] 测试抗封锁智能客户端...");
        try
        {
            using var spider = new EastMoneySpiderService();
            string text = await spider.FetchEastMoneyKLineAsync("0.000001");
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("❌ [诊断结果] 返回了空字符串！");
                return;
            }
            string text2 = text.Length > 800 ? (text.Substring(0, 800) + "...\n(为防卡顿已截断)") : text;
            AppendLog("[原始返回值] \n" + text2);
            if (text.Contains("{") && text.Contains("}"))
            {
                AppendLog("✅ [诊断结论] 接口完全畅通！WAF 的 Cookie 挑战已被攻破！");
            }
            else
            {
                AppendLog("❌ [诊断结论] 数据异常！没有找到 { } 包裹的 JSON 数据！", true);
            }
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
            AppendLog($"❌ [诊断异常] {ex.Message}{(ex.InnerException != null ? " -> 底层原因: " + ex.InnerException.Message : "")}", true);
        }
    }

    [RelayCommand]
    private async Task Start()
    {
        if (IsScanning)
        {
            _cts?.Cancel();
            AppendLog("⚠️ 正在拉起手刹...");
            return;
        }

        SparrowParameterValidationResult validation = SparrowParameterValidator.Validate(
            StrategyMode, ClassicParameters, V2Parameters, SystemSettings);
        if (!validation.IsValid)
        {
            AppendLog("❌ 参数校验失败：" + validation.Error, true);
            Growl.Warning(validation.Error);
            return;
        }

        var stockNameMap = _mainVm.StockVM?.StockNameMap;
        if (stockNameMap == null || stockNameMap.Count == 0) return;

        var targetPool = stockNameMap
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value) && !string.IsNullOrEmpty(kvp.Key) && 
                          !kvp.Value.Contains("ST") && !kvp.Key.StartsWith("688") && 
                          (kvp.Key.StartsWith("60") || kvp.Key.StartsWith("00") || kvp.Key.StartsWith("30")))
            .Select(kvp => (Code: kvp.Key, Name: kvp.Value))
            .ToList();

        IsScanning = true;
        LogText = ""; // Clear log
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        ProgressValue = 0;

        SparrowClassicScanParameters classicParams;
        SparrowScanParameters activeParams;
        if (StrategyMode == SparrowStrategyMode.Compare)
        {
            (classicParams, activeParams) = SparrowParameterMapper.ToComparison(
                ClassicParameters, V2Parameters, SystemSettings);
        }
        else
        {
            classicParams = SparrowParameterMapper.ToClassic(ClassicParameters, SystemSettings);
            activeParams = SparrowParameterMapper.ToV2(V2Parameters, SystemSettings);
        }

        var v2Progress = new Progress<SparrowScanReport>(report =>
        {
            if (report.LogMessage != null)
            {
                AppendLog(report.LogMessage, report.IsHighlight);
            }
            if (report.ProgressMax.HasValue)
            {
                ProgressMax = report.ProgressMax.Value;
            }
            if (report.ProgressValue.HasValue)
            {
                ProgressValue = report.ProgressValue.Value;
            }
        });

        try
        {
            switch (StrategyMode)
            {
                case SparrowStrategyMode.Classic:
                {
                    AppendLog("Classic ignores V2-only parameters: Turnover, Momentum, Alpha.");
                    var classicProgress = new Progress<SparrowClassicScanReport>(report =>
                    {
                        if (report.LogMessage != null)
                        {
                            AppendLog(report.LogMessage, report.IsHighlight);
                        }
                        if (report.ProgressMax.HasValue) ProgressMax = report.ProgressMax.Value;
                        if (report.ProgressValue.HasValue) ProgressValue = report.ProgressValue.Value;
                    });
                    List<SparrowClassicCandidate> results = await _classicScanner.ScanAsync(
                        targetPool, classicParams, classicProgress, _cts.Token);
                    if (!_cts.Token.IsCancellationRequested && results.Count > 0)
                    {
                        AddResultGroup($"麻雀_Classic_{DateTime.Now:MMdd}",
                            results.Select(item => (item.Code, item.Name)));
                        Growl.Success($"Sparrow Classic 执行完毕，入围 {results.Count} 只！");
                    }
                    break;
                }
                case SparrowStrategyMode.Compare:
                {
                    var compareProgress = new Progress<SparrowComparisonProgress>(report =>
                    {
                        if (report.LogMessage != null)
                        {
                            AppendLog(report.LogMessage, report.IsHighlight);
                        }
                        if (report.ProgressMax.HasValue) ProgressMax = report.ProgressMax.Value;
                        if (report.ProgressValue.HasValue) ProgressValue = report.ProgressValue.Value;
                    });
                    SparrowComparisonResult result = await _comparisonService.CompareAsync(
                        targetPool, classicParams, activeParams, compareProgress, _cts.Token);
                    if (!_cts.Token.IsCancellationRequested)
                    {
                        AddResultGroup($"麻雀_双选_{DateTime.Now:MMdd}",
                            result.Rows.Where(row => row.Category == SparrowComparisonCategory.Both)
                                .Select(row => (row.Code, row.Name)));
                        AddResultGroup($"麻雀_ClassicOnly_{DateTime.Now:MMdd}",
                            result.Rows.Where(row => row.Category == SparrowComparisonCategory.ClassicOnly)
                                .Select(row => (row.Code, row.Name)));
                        AddResultGroup($"麻雀_V2Only_{DateTime.Now:MMdd}",
                            result.Rows.Where(row => row.Category == SparrowComparisonCategory.V2Only)
                                .Select(row => (row.Code, row.Name)));
                        Growl.Success(
                            $"A/B 对照完成：双选 {result.Metrics.IntersectionCount}，" +
                            $"ClassicOnly {result.Metrics.ClassicOnlyCount}，V2Only {result.Metrics.V2OnlyCount}。");
                    }
                    break;
                }
                default:
                {
                    List<(string Code, string Name, string Reason)> results = await _scannerService.ScanAsync(
                        targetPool, activeParams, v2Progress, _cts.Token);
                    if (!_cts.Token.IsCancellationRequested && results.Count > 0)
                    {
                        AddResultGroup($"麻雀_V2_{DateTime.Now:MMdd}",
                            results.Select(item => (item.Code, item.Name)));
                        Growl.Success($"Sparrow V2 执行完毕，入围 {results.Count} 只！");
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (_cts.Token.IsCancellationRequested)
        {
            AppendLog(StrategyMode == SparrowStrategyMode.Compare
                ? "Comparison cancelled; no incomplete CSV or stock groups were created."
                : "扫描已取消。");
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
            AppendLog("❌ 引擎崩溃: " + ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void AddResultGroup(string groupName, IEnumerable<(string Code, string Name)> results)
    {
        var list = new List<StockModel>();
        foreach ((string code, string name) in results)
        {
            int level = 0;
            try
            {
                foreach (StockGroupModel group in _mainVm.StockVM.StockGroups)
                {
                    foreach (StockModel stock in group.Stocks)
                    {
                        if (stock.Code != code) continue;
                        if (group.Header?.Contains("选股") == true)
                        {
                            level = 2;
                            break;
                        }
                        if (level == 0) level = 1;
                    }
                    if (level == 2) break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to calculate Sparrow result highlight");
            }

            list.Add(new StockModel
            {
                Code = code,
                Name = name,
                IsChecked = true,
                HighlightLevel = level
            });
        }
        _mainVm.StockVM.AddGroup(groupName, list);
    }
}
