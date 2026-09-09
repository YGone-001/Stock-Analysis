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
    private readonly SparrowRankingEngine _rankingEngine = new();
    private readonly SparrowParameterUiState _parameterState = new();
    private readonly SparrowRankingSettings _rankingSettings = new();
    private readonly Dictionary<string, StockGroupModel> _activeResultGroups = new(StringComparer.Ordinal);
    private IReadOnlyList<SparrowRankedCandidate> _lastClassicRanking = Array.Empty<SparrowRankedCandidate>();
    private IReadOnlyList<SparrowRankedCandidate> _lastV2Ranking = Array.Empty<SparrowRankedCandidate>();
    private IReadOnlyList<SparrowComparisonRow> _lastComparisonRows = Array.Empty<SparrowComparisonRow>();
    private SparrowStrategyMode? _lastCompletedMode;
    private DateTime _lastCompletedAt;
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
    public SparrowRankingSettings RankingSettings => _rankingSettings;
    public int TopN
    {
        get => _rankingSettings.TopN;
        set => _rankingSettings.TopN = value;
    }
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
    public string AdhesionRangeText => _parameterState.AdhesionRangeText;
    public string TurnoverRangeText => _parameterState.TurnoverRangeText;

    private string _resultSummary = "尚未生成精选结果";
    public string ResultSummary
    {
        get => _resultSummary;
        private set { _resultSummary = value; OnPropertyChanged(); }
    }

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
        _rankingSettings.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(SparrowRankingSettings.TopN)) return;
            OnPropertyChanged(nameof(TopN));
            if (!IsScanning)
            {
                ApplyCachedTopN();
            }
        };
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
		AppendLog("\n🪺 [网络诊断] 测试正式股票数据容错链路...");
        try
        {
			StockDataResult result = await _mainVm.DataProvider.GetDataAsync(
				StockDataRequest.Parse("/api/kline-all?code=000001&limit=5&refresh=1"));
			string text = result.Json;
			if (!result.Success)
			{
				AppendLog($"❌ [诊断结果] 来源 {result.Source}；{result.Error}", true);
				return;
			}
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("❌ [诊断结果] 返回了空字符串！");
                return;
            }
            string text2 = text.Length > 800 ? (text.Substring(0, 800) + "...\n(为防卡顿已截断)") : text;
            AppendLog("[原始返回值] \n" + text2);
            if (text.Contains("{") && text.Contains("}"))
            {
				AppendLog($"✅ [诊断结论] 股票数据链路畅通；当前来源: {result.Source}。" +
					(result.UsedCache ? "（使用本地缓存）" : ""));
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
                    if (!_cts.Token.IsCancellationRequested)
                    {
                        SparrowRankingFeatures[] frozen = results
                            .Where(item => item.RankingFeatures != null)
                            .Select(item => item.RankingFeatures!)
                            .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                            .ToArray();
                        _lastClassicRanking = _rankingEngine.RankClassic(
                            frozen, classicParams.MinRise, classicParams.MaxRise);
                        _lastV2Ranking = Array.Empty<SparrowRankedCandidate>();
                        _lastComparisonRows = Array.Empty<SparrowComparisonRow>();
                        _lastCompletedMode = SparrowStrategyMode.Classic;
                        _lastCompletedAt = DateTime.Now;
                        string csv = await SparrowRankingCsvExporter.ExportClassicAsync(
                            _lastClassicRanking, TopN, _cts.Token);
                        AppendLog($"Classic Ranking CSV: {csv}");
                        ReportRanking("Classic", _lastClassicRanking);
                        ApplyCachedTopN();
                        Growl.Success($"Sparrow Classic 完成：候选池 {results.Count}，精选 {Math.Min(TopN, results.Count)} 只。");
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
                        _lastClassicRanking = result.ClassicRanking;
                        _lastV2Ranking = result.V2Ranking;
                        _lastComparisonRows = result.Rows;
                        _lastCompletedMode = SparrowStrategyMode.Compare;
                        _lastCompletedAt = result.Session.CapturedAt.DateTime;
                        ReportRanking("Classic Compare", result.ClassicRanking);
                        ReportRanking("V2 Compare", result.V2Ranking);
                        ApplyCachedTopN();
                        Growl.Success(
                            $"A/B 对照完成：候选池 双选 {result.Metrics.IntersectionCount} / " +
                            $"ClassicOnly {result.Metrics.ClassicOnlyCount} / V2Only {result.Metrics.V2OnlyCount}；" +
                            $"各组最多 Top{TopN}。");
                    }
                    break;
                }
                default:
                {
                    List<SparrowV2Candidate> results = await _scannerService.ScanWithFeaturesAsync(
                        targetPool, activeParams, v2Progress, _cts.Token);
                    if (!_cts.Token.IsCancellationRequested)
                    {
                        SparrowRankingFeatures[] frozen = results
                            .Select(item => item.RankingFeatures)
                            .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                            .ToArray();
                        _lastV2Ranking = _rankingEngine.RankV2(
                            frozen, activeParams.MinRise, activeParams.MaxRise, activeParams.CheckAlpha);
                        _lastClassicRanking = Array.Empty<SparrowRankedCandidate>();
                        _lastComparisonRows = Array.Empty<SparrowComparisonRow>();
                        _lastCompletedMode = SparrowStrategyMode.V2;
                        _lastCompletedAt = DateTime.Now;
                        string csv = await SparrowRankingCsvExporter.ExportV2Async(
                            _lastV2Ranking, TopN, _cts.Token);
                        AppendLog($"V2 Ranking CSV: {csv}");
                        ReportRanking("V2", _lastV2Ranking);
                        ApplyCachedTopN();
                        Growl.Success($"Sparrow V2 完成：候选池 {results.Count}，精选 {Math.Min(TopN, results.Count)} 只。");
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

    private void ApplyCachedTopN()
    {
        if (!_lastCompletedMode.HasValue)
        {
            return;
        }

        int topN = TopN;
        switch (_lastCompletedMode.Value)
        {
            case SparrowStrategyMode.Classic:
                if (_lastClassicRanking.Count > 0)
                {
                    SetResultGroup("Classic", $"麻雀_Classic_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastClassicRanking.Take(topN).Select(item => (item.Code, item.Name)));
                }
                ResultSummary = $"Classic 完成 · 候选池 {_lastClassicRanking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastClassicRanking.Count)}只）";
                break;
            case SparrowStrategyMode.V2:
                if (_lastV2Ranking.Count > 0)
                {
                    SetResultGroup("V2", $"麻雀_V2_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastV2Ranking.Take(topN).Select(item => (item.Code, item.Name)));
                }
                ResultSummary = $"V2 完成 · 候选池 {_lastV2Ranking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastV2Ranking.Count)}只）";
                break;
            case SparrowStrategyMode.Compare:
                ApplyComparisonTopN(topN);
                break;
        }
    }

    private void ApplyComparisonTopN(int topN)
    {
        IReadOnlyList<SparrowComparisonRow> both = SparrowRankingEngine.SelectCompareTopN(
            _lastComparisonRows, SparrowComparisonCategory.Both, topN);
        IReadOnlyList<SparrowComparisonRow> classicOnly = SparrowRankingEngine.SelectCompareTopN(
            _lastComparisonRows, SparrowComparisonCategory.ClassicOnly, topN);
        IReadOnlyList<SparrowComparisonRow> v2Only = SparrowRankingEngine.SelectCompareTopN(
            _lastComparisonRows, SparrowComparisonCategory.V2Only, topN);
        SetResultGroupIfAny("Both", $"麻雀_双选_Top{topN}_{_lastCompletedAt:MMdd}", both.Select(row => (row.Code, row.Name)));
        SetResultGroupIfAny("ClassicOnly", $"麻雀_ClassicOnly_Top{topN}_{_lastCompletedAt:MMdd}", classicOnly.Select(row => (row.Code, row.Name)));
        SetResultGroupIfAny("V2Only", $"麻雀_V2Only_Top{topN}_{_lastCompletedAt:MMdd}", v2Only.Select(row => (row.Code, row.Name)));
        ResultSummary = $"Compare 完成 · Top{topN}: 双选 {both.Count} / ClassicOnly {classicOnly.Count} / V2Only {v2Only.Count}";
    }

    private void SetResultGroupIfAny(
        string key,
        string groupName,
        IEnumerable<(string Code, string Name)> results)
    {
        List<(string Code, string Name)> materialized = results.ToList();
        if (materialized.Count > 0)
        {
            SetResultGroup(key, groupName, materialized);
            return;
        }

        if (_activeResultGroups.Remove(key, out StockGroupModel? existing))
        {
            _mainVm.StockVM.RemoveGeneratedResultGroup(existing);
        }
        AppendLog($"ℹ️ {groupName} 本次为 0 只，不创建空分组。");
    }

    private void SetResultGroup(string key, string groupName, IEnumerable<(string Code, string Name)> results)
    {
        List<StockModel> stocks = CreateResultStocks(results);
        if (_activeResultGroups.TryGetValue(key, out StockGroupModel? existing))
        {
            _mainVm.StockVM.UpdateGroup(existing, groupName, stocks);
            return;
        }

        _activeResultGroups[key] = _mainVm.StockVM.AddGroup(groupName, stocks);
    }

    private List<StockModel> CreateResultStocks(IEnumerable<(string Code, string Name)> results)
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
        return list;
    }

    private void ReportRanking(string title, IReadOnlyList<SparrowRankedCandidate> ranking)
    {
        int shown = Math.Min(TopN, ranking.Count);
        AppendLog($"\n========== Sparrow {title} Ranking ==========\n\nCandidate Pool:     {ranking.Count}\nTopN:               {TopN}\n");
        foreach (SparrowRankedCandidate candidate in ranking.Take(TopN))
        {
            AppendLog($"#{candidate.Rank,-2} {candidate.Code} {candidate.Name}  Score={candidate.TotalScore:F1}");
        }
        if (ranking.Count > 0)
        {
            double median = ranking.Count % 2 == 1
                ? ranking[ranking.Count / 2].TotalScore
                : (ranking[ranking.Count / 2 - 1].TotalScore + ranking[ranking.Count / 2].TotalScore) / 2.0;
            AppendLog($"\nScore Range:\nMax = {ranking.Max(item => item.TotalScore):F1}\nMedian = {median:F1}\nMin = {ranking.Min(item => item.TotalScore):F1}");
        }
        AppendLog($"\n精选：{shown}只\n========================================");
    }
}
