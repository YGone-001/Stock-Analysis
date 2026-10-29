using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
using AIHelper.Core.Sparrow;
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
    private IReadOnlyList<SparrowPairComparisonRow> _lastPairComparisonRows = Array.Empty<SparrowPairComparisonRow>();
    private string _lastPairLeftVersion = "";
    private string _lastPairRightVersion = "";
    private readonly SparrowAutoScanSnapshot _snapshot = new();
    private bool _closed;
    private Guid? _ownedRun;
    private DateTime? _automaticRequest;
    public string ScanOutcome { get; private set; } = "Idle";
    // Internal execution/notification seams keep lifecycle tests offline on the existing WPF dispatcher.
    internal Func<CancellationToken, IProgress<SparrowScanReport>, Task<IReadOnlyList<SparrowRankedCandidate>>>? ScanOverride { get; set; }
    internal Action<string> Notification { get; set; } = message => Growl.Info(message);
    private DateTime? _lastAutoScanAt;
    private SparrowStrategyMode? _lastCompletedMode;
    private DateTime _lastCompletedAt;
    private CancellationTokenSource? _cts;

    public IReadOnlyList<SparrowStrategyMode> AvailableStrategyModes { get; } =
        Enum.GetValues<SparrowStrategyMode>();

    public IReadOnlyList<SparrowStrategyModeOption> AvailableStrategyModeOptions { get; } =
        new SparrowStrategyModeOption[]
        {
            new(SparrowStrategyMode.Classic),
            new(SparrowStrategyMode.V2),
            new(SparrowStrategyMode.Compare),
            new(SparrowStrategyMode.V3),
            new(SparrowStrategyMode.V4),
            new(SparrowStrategyMode.V5),
            new(SparrowStrategyMode.V6),
            new(SparrowStrategyMode.PairCompare)
        };

    public IReadOnlyList<SparrowPairComparisonPreset> AvailablePairComparisonPresets { get; } =
        new SparrowPairComparisonPreset[]
        {
            new(SparrowStrategyMode.V2, SparrowStrategyMode.V3),
            new(SparrowStrategyMode.V2, SparrowStrategyMode.V4),
            new(SparrowStrategyMode.V2, SparrowStrategyMode.V5),
            new(SparrowStrategyMode.V3, SparrowStrategyMode.V4),
            new(SparrowStrategyMode.V3, SparrowStrategyMode.V5),
            new(SparrowStrategyMode.V4, SparrowStrategyMode.V5)
        };

    private SparrowPairComparisonPreset _selectedPairComparisonPreset = new(SparrowStrategyMode.V3, SparrowStrategyMode.V4);
    public SparrowPairComparisonPreset SelectedPairComparisonPreset
    {
        get => _selectedPairComparisonPreset;
        set
        {
            if (Equals(_selectedPairComparisonPreset, value)) return;
            _selectedPairComparisonPreset = value;
            OnPropertyChanged();
        }
    }

    public SparrowStrategyMode StrategyMode
    {
        get => _parameterState.StrategyMode;
        set => _parameterState.StrategyMode = value;
    }

    public ISparrowStrategyUiParameters Parameters => _parameterState.ActiveParameters;
    public SparrowStrategyUiParameters ClassicParameters => _parameterState.ClassicParameters;
    public SparrowV2UiParameters V2Parameters => _parameterState.V2Parameters;
    public SparrowV3UiParameters V3Parameters => _parameterState.V3Parameters;
    public SparrowV4UiParameters V4Parameters => _parameterState.V4Parameters;
    public SparrowV5UiParameters V5Parameters => _parameterState.V5Parameters;
    public SparrowV6UiParameters V6Parameters => _parameterState.V6Parameters;
    public SparrowV2UiParameters EnhancementParameters => _parameterState.EnhancementParameters;
    public SparrowV3UiParameters MomentumGateParameters => _parameterState.MomentumGateParameters;
    public SparrowV4UiParameters VolumeGateParameters => _parameterState.VolumeGateParameters;
    public SparrowV5UiParameters WeeklyGateParameters => _parameterState.WeeklyGateParameters;
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
    public bool IsV3Mode => _parameterState.IsV3Mode;
    public bool IsV4Mode => _parameterState.IsV4Mode;
    public bool IsV5Mode => _parameterState.IsV5Mode;
    public bool IsV6Mode => _parameterState.IsV6Mode;
    public bool IsPairCompareMode => _parameterState.IsPairCompareMode;
    public bool ShowPairComparison => _parameterState.ShowPairComparison;
    public bool ShowBaseParameters => _parameterState.ShowBaseParameters;
    public bool ShowV2Parameters => _parameterState.ShowV2Parameters;
    public bool ShowV3Parameters => _parameterState.ShowV3Parameters;
    public bool ShowV4Parameters => _parameterState.ShowV4Parameters;
    public bool ShowV5Parameters => _parameterState.ShowV5Parameters;
    public bool ShowV6Parameters => _parameterState.ShowV6Parameters;
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

    /// <summary>Flat, binding-friendly rows backing the result grid. Populated on every completed scan.</summary>
    public ObservableCollection<SparrowResultRow> Results { get; } = new();

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
                StartCommand.NotifyCanExecuteChanged();
                CancelScanCommand.NotifyCanExecuteChanged();
                AnalyzeFeaturesCommand.NotifyCanExecuteChanged();
                RunT1RankingCommand.NotifyCanExecuteChanged();
                ExportT1RankingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StartButtonText => IsScanning
        ? "扫描中…"
        : StrategyMode switch
        {
            SparrowStrategyMode.Classic => "🚀 执行麻雀 Classic (14:30)",
            SparrowStrategyMode.Compare => "🧪 执行 Classic / V2 对照",
            _ => $"🚀 执行{StrategyMode.ToDisplayName()}"
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
        var sharedKlineCache = new SparrowMarketDataCache(persistFile: GetKlineCachePersistPath());
        _scannerService = new SparrowScannerService(mainVm.DataProvider, sharedKlineCache, mainVm.KlineService, mainVm.QuoteService);
        _classicScanner = new SparrowClassicScanner(mainVm.DataProvider, klineCache: sharedKlineCache, klineService: mainVm.KlineService, quoteService: mainVm.QuoteService);
        _comparisonService = new SparrowComparisonService(mainVm.DataProvider, klineCache: sharedKlineCache, klineService: mainVm.KlineService, quoteService: mainVm.QuoteService);
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

    private static string GetKlineCachePersistPath()
    {
        string directory = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = AppDomain.CurrentDomain.BaseDirectory;
        }
        return Path.Combine(directory, "sparrow-kline-cache.json");
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

    /// <summary>Opens the live WebView2 chart for a result row; wired to the result grid's double-click.</summary>
    [RelayCommand]
    private void ShowLiveChart(SparrowResultRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.Code)) return;
        _mainVm.ShowLiveChart(row.Code, row.Name);
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
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "捕获到未处理异常");
            AppendLog($"❌ [诊断异常] {ex.Message}{(ex.InnerException != null ? " -> 底层原因: " + ex.InnerException.Message : "")}", true);
        }
    }

    private bool _enableIntradaySnapshotCapture;
    public bool EnableIntradaySnapshotCapture { get => _enableIntradaySnapshotCapture; set => SetProperty(ref _enableIntradaySnapshotCapture, value); }
    public string? LastSnapshotPath { get; private set; }
    public Guid? LastSnapshotRunId { get; private set; }
    public bool IsCapturing { get; private set; }
    private TimeSpan _captureTimeout = TimeSpan.FromSeconds(30);
    public TimeSpan CaptureTimeout
    {
        get => _captureTimeout;
        set => _captureTimeout = value > TimeSpan.Zero && value <= TimeSpan.FromMinutes(1) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    private CancellationTokenSource? _captureCts;
    private string PreviousSnapshotLabel => LastSnapshotPath == null ? "无上次文件" : $"上次文件所属运行 {LastSnapshotRunId}: {LastSnapshotPath}";
    private void CaptureStateChanged() { OnPropertyChanged(nameof(IsCapturing)); StartCommand.NotifyCanExecuteChanged(); CancelScanCommand.NotifyCanExecuteChanged(); AnalyzeFeaturesCommand.NotifyCanExecuteChanged(); RunT1RankingCommand.NotifyCanExecuteChanged(); ExportT1RankingCommand.NotifyCanExecuteChanged(); }
    private bool CanOpenSnapshot() => LastSnapshotPath != null && File.Exists(LastSnapshotPath);
    [RelayCommand(CanExecute = nameof(CanOpenSnapshot))]
    private void OpenSnapshot()
    {
        if (CanOpenSnapshot()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LastSnapshotPath!) { UseShellExecute = true });
    }
    public string SnapshotStatus { get; private set; } = "快照采集关闭；普通策略结果不等于 T+1 时点认证";
    internal TimeProvider SnapshotClock { get; set; } = TimeProvider.System;
    internal IntradaySnapshotCapture? SnapshotCapture { get; set; }

    private static string IntradaySnapshotStoreHash(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private bool CanStart() => !_closed && !IsScanning && !IsCapturing;
    private bool CanCancelScan() => !_closed && (IsScanning || IsCapturing || IsAnalyzingFeatures || IsRankingT1);

    [RelayCommand(CanExecute = nameof(CanCancelScan))]
    private void CancelScan()
    {
        if (IsCapturing) { _captureCts?.Cancel(); return; }
        if (!IsScanning && IsAnalyzingFeatures) { _featureCts?.Cancel(); return; }
        if (!IsScanning && IsRankingT1) { _t1Cts?.Cancel(); return; }
        if (!IsScanning || _cts == null || _cts.IsCancellationRequested) return;
        ScanOutcome = "Cancelling";
        OnPropertyChanged(nameof(ScanOutcome));
        _cts.Cancel();
    }

    public void Close()
    {
        if (_closed) return;
        CancelScan();
        _closed = true;
        _featureGeneration++;
        _featureCts?.Cancel();
        AnalyzeFeaturesCommand.NotifyCanExecuteChanged();
        _t1Generation++;
        _t1Cts?.Cancel();
        RunT1RankingCommand.NotifyCanExecuteChanged();
        ExportT1RankingCommand.NotifyCanExecuteChanged();
        _ownedRun = null;
        _automaticRequest = null;
        StartCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (!CanStart()) return;
        DateTime startedAt = _automaticRequest ?? DateTime.Now;
        bool automatic = _automaticRequest.HasValue;
        _automaticRequest = null;
        var mode = StrategyMode;
        var pair = SelectedPairComparisonPreset;
        int topN = TopN;
        bool captureEnabled = EnableIntradaySnapshotCapture;
        DateTimeOffset scanStartedUtc = SnapshotClock.GetUtcNow();
        SparrowParameterValidationResult validation = SparrowParameterValidator.Validate(
            mode, ClassicParameters, V2Parameters, SystemSettings,
            mode == SparrowStrategyMode.V3 ? V3Parameters : null,
            mode == SparrowStrategyMode.V4 ? V4Parameters : null,
            mode == SparrowStrategyMode.V5 ? V5Parameters : null,
            mode == SparrowStrategyMode.V6 ? V6Parameters : null);
        if (!validation.IsValid)
        {
            AppendLog("❌ 参数校验失败：" + validation.Error, true);
            ScanOutcome = "Skipped";
            OnPropertyChanged(nameof(ScanOutcome));
            Notification(validation.Error);
            return;
        }

        var stockNameMap = _mainVm.StockVM?.StockNameMap;
        if ((stockNameMap == null || stockNameMap.Count == 0) && ScanOverride == null) { ScanOutcome = "Skipped"; OnPropertyChanged(nameof(ScanOutcome)); return; }

        var targetPool = (stockNameMap ?? new System.Collections.Concurrent.ConcurrentDictionary<string, string>())
            .Where(kvp => SparrowClassicUniverseEligibility.Evaluate(kvp.Key, kvp.Value).Eligible)
            .Select(kvp => (Code: kvp.Key, Name: kvp.Value))
            .ToList();

        using var cts = new CancellationTokenSource();
        var token = cts.Token;
        var runId = Guid.NewGuid();
        _cts = cts;
        _ownedRun = runId;
        InvalidateFeatures(runId);
        SnapshotStatus = $"当前运行 {runId}: {(captureEnabled ? "等待扫描完成后采集" : "本次未启用采集")}；{PreviousSnapshotLabel}";
        OnPropertyChanged(nameof(SnapshotStatus));
        bool OwnsRun() => !_closed && _ownedRun == runId && !token.IsCancellationRequested;
        IsScanning = true;
        ScanOutcome = "Running";
        OnPropertyChanged(nameof(ScanOutcome));
        if (automatic) _lastAutoScanAt = startedAt;
        LogText = "";
        ProgressValue = 0;
        ResultSummary = $"{mode.ToDisplayName()} 扫描中；显示上次完成结果";
        IReadOnlyList<SparrowRankedCandidate> completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
        IReadOnlyList<SparrowRankedCandidate> completedV2Ranking = Array.Empty<SparrowRankedCandidate>();
        IReadOnlyList<SparrowComparisonRow> completedComparisonRows = Array.Empty<SparrowComparisonRow>();
        IReadOnlyList<SparrowPairComparisonRow> completedPairComparisonRows = Array.Empty<SparrowPairComparisonRow>();
        string completedPairLeftVersion = "", completedPairRightVersion = "";
        SparrowStrategyMode? completedMode = null;
        DateTime completedAt = startedAt;
        string completionMessage = "";
        bool completed = false;
        try
        {
            SparrowClassicScanParameters classicParams;
            SparrowScanParameters activeParams;
            SparrowMomentumGateConfig? momentumGates = null;
            SparrowVolumeGateConfig? volumeGates = null;
            SparrowWeeklyGateConfig? weeklyGates = null;
            SparrowIntradayGateConfig? intradayGates = null;
            if (mode == SparrowStrategyMode.Compare)
            {
                (classicParams, activeParams) = SparrowParameterMapper.ToComparison(
                    ClassicParameters, V2Parameters, SystemSettings);
            }
            else if (mode == SparrowStrategyMode.V3)
            {
                classicParams = SparrowParameterMapper.ToClassic(ClassicParameters, SystemSettings);
                activeParams = SparrowParameterMapper.ToV3(V3Parameters, SystemSettings);
                momentumGates = SparrowParameterMapper.ToMomentumGates(V3Parameters);
            }
            else if (mode == SparrowStrategyMode.V4)
            {
                classicParams = SparrowParameterMapper.ToClassic(ClassicParameters, SystemSettings);
                activeParams = SparrowParameterMapper.ToV4(V4Parameters, SystemSettings);
                momentumGates = SparrowParameterMapper.ToMomentumGates(V4Parameters);
                volumeGates = SparrowParameterMapper.ToVolumeGates(V4Parameters);
            }
            else if (mode == SparrowStrategyMode.V5)
            {
                classicParams = SparrowParameterMapper.ToClassic(ClassicParameters, SystemSettings);
                activeParams = SparrowParameterMapper.ToV5(V5Parameters, SystemSettings);
                momentumGates = SparrowParameterMapper.ToMomentumGates(V5Parameters);
                volumeGates = SparrowParameterMapper.ToVolumeGates(V5Parameters);
                weeklyGates = SparrowParameterMapper.ToWeeklyGates(V5Parameters);
            }
            else if (mode == SparrowStrategyMode.V6)
            {
                classicParams = SparrowParameterMapper.ToClassic(ClassicParameters, SystemSettings);
                activeParams = SparrowParameterMapper.ToV6(V6Parameters, SystemSettings);
                momentumGates = SparrowParameterMapper.ToMomentumGates(V6Parameters);
                volumeGates = SparrowParameterMapper.ToVolumeGates(V6Parameters);
                weeklyGates = SparrowParameterMapper.ToWeeklyGates(V6Parameters);
                intradayGates = SparrowParameterMapper.ToIntradayGates(V6Parameters);
            }
            else
            {
                classicParams = SparrowParameterMapper.ToClassic(ClassicParameters, SystemSettings);
                activeParams = SparrowParameterMapper.ToV2(V2Parameters, SystemSettings);
            }

            var v2Progress = new Progress<SparrowScanReport>(report =>
            {
                if (!OwnsRun()) return;
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

            // Resolved mapper outputs are private to this invocation; capture identity before any await.
            string parameterIdentity = System.Text.Json.JsonSerializer.Serialize(new
            {
                mode,
                Pair = mode == SparrowStrategyMode.PairCompare ? pair : null,
                topN,
                classicParams,
                activeParams,
                momentumGates,
                volumeGates,
                weeklyGates,
                intradayGates,
                PairParameters = mode == SparrowStrategyMode.PairCompare ? System.Text.Json.JsonSerializer.Serialize(new { V2Parameters, V3Parameters, V4Parameters, V5Parameters, SystemSettings }) : ""
            });
            if (ScanOverride != null)
            {
                var ranking = await ScanOverride(token, v2Progress);
                if (mode == SparrowStrategyMode.Classic) completedClassicRanking = ranking;
                else completedV2Ranking = ranking;
                completedMode = mode;
                completed = true;
            }
            else
            {
                switch (mode)
                {
                    case SparrowStrategyMode.Classic:
                        {
                            AppendLog("Classic ignores V2-only parameters: Turnover, Momentum, Alpha.");
                            var classicProgress = new Progress<SparrowClassicScanReport>(report =>
                            {
                                if (!OwnsRun()) return;
                                if (report.LogMessage != null)
                                {
                                    AppendLog(report.LogMessage, report.IsHighlight);
                                }
                                if (report.ProgressMax.HasValue) ProgressMax = report.ProgressMax.Value;
                                if (report.ProgressValue.HasValue) ProgressValue = report.ProgressValue.Value;
                            });
                            List<SparrowClassicCandidate> results = await _classicScanner.ScanAsync(
                                targetPool, classicParams, classicProgress, token);
                            if (OwnsRun())
                            {
                                SparrowRankingFeatures[] frozen = results
                                    .Where(item => item.RankingFeatures != null)
                                    .Select(item => item.RankingFeatures!)
                                    .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                                    .ToArray();
                                completedClassicRanking = _rankingEngine.RankClassic(
                                    frozen, classicParams.MinRise, classicParams.MaxRise);
                                completedV2Ranking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.Classic;
                                completedAt = DateTime.Now;
                                string csv = await SparrowRankingCsvExporter.ExportClassicAsync(
                                    completedClassicRanking, topN, token);
                                if (!OwnsRun()) token.ThrowIfCancellationRequested();
                                AppendLog($"Classic Ranking CSV: {csv}");
                                ReportRanking("Classic", completedClassicRanking, topN);
                                completed = true;
                                completionMessage = $"Sparrow Classic 完成：候选池 {results.Count}，精选 {Math.Min(topN, results.Count)} 只。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.Compare:
                        {
                            var compareProgress = new Progress<SparrowComparisonProgress>(report =>
                            {
                                if (!OwnsRun()) return;
                                if (report.LogMessage != null)
                                {
                                    AppendLog(report.LogMessage, report.IsHighlight);
                                }
                                if (report.ProgressMax.HasValue) ProgressMax = report.ProgressMax.Value;
                                if (report.ProgressValue.HasValue) ProgressValue = report.ProgressValue.Value;
                            });
                            SparrowComparisonResult result = await _comparisonService.CompareAsync(
                                targetPool, classicParams, activeParams, compareProgress, token);
                            if (OwnsRun())
                            {
                                completedClassicRanking = result.ClassicRanking;
                                completedV2Ranking = result.V2Ranking;
                                completedComparisonRows = result.Rows;
                                completedMode = SparrowStrategyMode.Compare;
                                completedAt = result.Session.CapturedAt.DateTime;
                                ReportRanking("Classic Compare", result.ClassicRanking, topN);
                                ReportRanking("V2 Compare", result.V2Ranking, topN);
                                completed = true;
                                completionMessage =
                                    $"A/B 对照完成：候选池 双选 {result.Metrics.IntersectionCount} / " +
                                    $"ClassicOnly {result.Metrics.ClassicOnlyCount} / V2Only {result.Metrics.V2OnlyCount}；" +
                                    $"各组最多 Top{topN}。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.V2:
                    default:
                        {
                            List<SparrowV2Candidate> results = await _scannerService.ScanWithFeaturesAsync(
                                targetPool, activeParams, v2Progress, token);
                            if (OwnsRun())
                            {
                                SparrowRankingFeatures[] frozen = results
                                    .Select(item => item.RankingFeatures)
                                    .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                                    .ToArray();
                                completedV2Ranking = _rankingEngine.RankV2(
                                    frozen, activeParams.MinRise, activeParams.MaxRise, activeParams.CheckAlpha,
                                    sectorHeatEnabled: activeParams.SectorHeatEnabled && frozen.Any(feature => feature.SectorHeatPercentile.HasValue));
                                completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.V2;
                                completedAt = DateTime.Now;
                                string csv = await SparrowRankingCsvExporter.ExportV2Async(
                                    completedV2Ranking, topN, token);
                                if (!OwnsRun()) token.ThrowIfCancellationRequested();
                                AppendLog($"V2 Ranking CSV: {csv}");
                                ReportRanking("V2", completedV2Ranking, topN);
                                completed = true;
                                completionMessage = $"Sparrow V2 完成：候选池 {results.Count}，精选 {Math.Min(topN, results.Count)} 只。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.V3:
                        {
                            List<SparrowV2Candidate> results = await _scannerService.ScanWithFeaturesAsync(
                                targetPool, activeParams, v2Progress, token, momentumGates);
                            if (OwnsRun())
                            {
                                SparrowRankingFeatures[] frozen = results
                                    .Select(item => item.RankingFeatures)
                                    .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                                    .ToArray();
                                completedV2Ranking = _rankingEngine.RankV2(
                                    frozen, activeParams.MinRise, activeParams.MaxRise, activeParams.CheckAlpha,
                                    sectorHeatEnabled: activeParams.SectorHeatEnabled && frozen.Any(feature => feature.SectorHeatPercentile.HasValue));
                                completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.V3;
                                completedAt = DateTime.Now;
                                string csv = await SparrowRankingCsvExporter.ExportV2Async(
                                    completedV2Ranking, topN, token);
                                if (!OwnsRun()) token.ThrowIfCancellationRequested();
                                AppendLog($"V3 Ranking CSV: {csv}");
                                ReportRanking("V3", completedV2Ranking, topN);
                                completed = true;
                                completionMessage = $"Sparrow V3 完成：候选池 {results.Count}，精选 {Math.Min(topN, results.Count)} 只。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.V4:
                        {
                            List<SparrowV2Candidate> results = await _scannerService.ScanWithFeaturesAsync(
                                targetPool, activeParams, v2Progress, token, momentumGates, volumeGates);
                            if (OwnsRun())
                            {
                                SparrowRankingFeatures[] frozen = results
                                    .Select(item => item.RankingFeatures)
                                    .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                                    .ToArray();
                                completedV2Ranking = _rankingEngine.RankV2(
                                    frozen, activeParams.MinRise, activeParams.MaxRise, activeParams.CheckAlpha,
                                    sectorHeatEnabled: activeParams.SectorHeatEnabled && frozen.Any(feature => feature.SectorHeatPercentile.HasValue));
                                completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.V4;
                                completedAt = DateTime.Now;
                                string csv = await SparrowRankingCsvExporter.ExportV2Async(
                                    completedV2Ranking, topN, token);
                                if (!OwnsRun()) token.ThrowIfCancellationRequested();
                                AppendLog($"V4 Ranking CSV: {csv}");
                                ReportRanking("V4", completedV2Ranking, topN);
                                completed = true;
                                completionMessage = $"Sparrow V4 完成：候选池 {results.Count}，精选 {Math.Min(topN, results.Count)} 只。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.V5:
                        {
                            List<SparrowV2Candidate> results = await _scannerService.ScanWithFeaturesAsync(
                                targetPool, activeParams, v2Progress, token, momentumGates, volumeGates, weeklyGates);
                            if (OwnsRun())
                            {
                                SparrowRankingFeatures[] frozen = results
                                    .Select(item => item.RankingFeatures)
                                    .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                                    .ToArray();
                                completedV2Ranking = _rankingEngine.RankV2(
                                    frozen, activeParams.MinRise, activeParams.MaxRise, activeParams.CheckAlpha,
                                    sectorHeatEnabled: activeParams.SectorHeatEnabled && frozen.Any(feature => feature.SectorHeatPercentile.HasValue));
                                completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.V5;
                                completedAt = DateTime.Now;
                                string csv = await SparrowRankingCsvExporter.ExportV2Async(
                                    completedV2Ranking, topN, token);
                                if (!OwnsRun()) token.ThrowIfCancellationRequested();
                                AppendLog($"V5 Ranking CSV: {csv}");
                                ReportRanking("V5", completedV2Ranking, topN);
                                completed = true;
                                completionMessage = $"Sparrow V5 完成：候选池 {results.Count}，精选 {Math.Min(topN, results.Count)} 只。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.V6:
                        {
                            List<SparrowV2Candidate> results = await _scannerService.ScanWithFeaturesAsync(
                                targetPool, activeParams, v2Progress, token, momentumGates, volumeGates, weeklyGates, intradayGates);
                            if (OwnsRun())
                            {
                                SparrowRankingFeatures[] frozen = results
                                    .Select(item => item.RankingFeatures)
                                    .OrderBy(feature => feature.Code, StringComparer.Ordinal)
                                    .ToArray();
                                completedV2Ranking = _rankingEngine.RankV2(
                                    frozen, activeParams.MinRise, activeParams.MaxRise, activeParams.CheckAlpha,
                                    sectorHeatEnabled: activeParams.SectorHeatEnabled && frozen.Any(feature => feature.SectorHeatPercentile.HasValue));
                                completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.V6;
                                completedAt = DateTime.Now;
                                string csv = await SparrowRankingCsvExporter.ExportV2Async(
                                    completedV2Ranking, topN, token);
                                if (!OwnsRun()) token.ThrowIfCancellationRequested();
                                AppendLog($"V6 Ranking CSV: {csv}");
                                ReportRanking("V6", completedV2Ranking, topN);
                                completed = true;
                                completionMessage = $"Sparrow V6 完成：候选池 {results.Count}，精选 {Math.Min(topN, results.Count)} 只。";
                            }
                            break;
                        }
                    case SparrowStrategyMode.PairCompare:
                        {
                            (SparrowScanParameters, SparrowMomentumGateConfig?, SparrowVolumeGateConfig?, SparrowWeeklyGateConfig?, string) Side(SparrowStrategyMode mode) => mode switch
                            {
                                SparrowStrategyMode.V3 => (SparrowParameterMapper.ToV3(V3Parameters, SystemSettings),
                                    SparrowParameterMapper.ToMomentumGates(V3Parameters), null, null, SparrowStrategyVersions.V3),
                                SparrowStrategyMode.V4 => (SparrowParameterMapper.ToV4(V4Parameters, SystemSettings),
                                    SparrowParameterMapper.ToMomentumGates(V4Parameters), SparrowParameterMapper.ToVolumeGates(V4Parameters), null, SparrowStrategyVersions.V4),
                                SparrowStrategyMode.V5 => (SparrowParameterMapper.ToV5(V5Parameters, SystemSettings),
                                    SparrowParameterMapper.ToMomentumGates(V5Parameters), SparrowParameterMapper.ToVolumeGates(V5Parameters),
                                    SparrowParameterMapper.ToWeeklyGates(V5Parameters), SparrowStrategyVersions.V5),
                                _ => (SparrowParameterMapper.ToV2(V2Parameters, SystemSettings), null, null, null, SparrowStrategyVersions.V2)
                            };
                            (SparrowScanParameters leftParams, SparrowMomentumGateConfig? leftMomentum, SparrowVolumeGateConfig? leftVolume, SparrowWeeklyGateConfig? leftWeekly, string leftVersion) = Side(pair.Left);
                            (SparrowScanParameters rightParams, SparrowMomentumGateConfig? rightMomentum, SparrowVolumeGateConfig? rightVolume, SparrowWeeklyGateConfig? rightWeekly, string rightVersion) = Side(pair.Right);
                            var pairService = new SparrowPairComparisonService(_scannerService, _rankingEngine);
                            SparrowPairComparisonResult result = await pairService.CompareAsync(targetPool,
                                new(pair.Left.ToString(), leftVersion), leftParams, leftMomentum, leftVolume, leftWeekly,
                                new(pair.Right.ToString(), rightVersion), rightParams, rightMomentum, rightVolume, rightWeekly,
                                v2Progress, token);
                            if (OwnsRun())
                            {
                                completedPairComparisonRows = result.Rows;
                                completedPairLeftVersion = leftVersion;
                                completedPairRightVersion = rightVersion;
                                completedV2Ranking = Array.Empty<SparrowRankedCandidate>();
                                completedClassicRanking = Array.Empty<SparrowRankedCandidate>();
                                completedComparisonRows = Array.Empty<SparrowComparisonRow>();
                                completedMode = SparrowStrategyMode.PairCompare;
                                completedAt = result.CapturedAt.LocalDateTime;
                                AppendLog($"Pair Comparison CSV: {result.CsvPath}");
                                completed = true;
                                completionMessage = $"双版本对照完成：双选 {result.Metrics.BothCount} / 仅{leftVersion} {result.Metrics.LeftOnlyCount} / 仅{rightVersion} {result.Metrics.RightOnlyCount}。";
                            }
                            break;
                        }
                }
            }
            if (!OwnsRun()) token.ThrowIfCancellationRequested();
            // Publish only after execution AND export succeed; all earlier assignments are local.
            if (completed && OwnsRun())
            {
                this._lastClassicRanking = completedClassicRanking;
                this._lastV2Ranking = completedV2Ranking;
                this._lastComparisonRows = completedComparisonRows;
                this._lastPairComparisonRows = completedPairComparisonRows;
                this._lastPairLeftVersion = completedPairLeftVersion;
                this._lastPairRightVersion = completedPairRightVersion;
                this._lastCompletedMode = completedMode;
                this._lastCompletedAt = completedAt;
                ApplyCachedTopN(topN);
                var ranking = mode == SparrowStrategyMode.Classic ? completedClassicRanking : completedV2Ranking;
                ScanOutcome = Results.Count == 0 ? "CompletedEmpty" : "Completed";
                if (completionMessage.Length > 0) Notification(completionMessage);
                if (mode is not (SparrowStrategyMode.Compare or SparrowStrategyMode.PairCompare))
                {
                    if (captureEnabled)
                    {
                        var scanCompletedUtc = SnapshotClock.GetUtcNow();
                        using var budget = new CancellationTokenSource(CaptureTimeout, SnapshotClock);
                        using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
                        _captureCts = captureCts;
                        IsCapturing = true;
                        IsScanning = false;
                        CaptureStateChanged();
                        SnapshotStatus = $"当前运行 {runId}: 策略扫描已完成；正在采集分钟快照；{PreviousSnapshotLabel}";
                        OnPropertyChanged(nameof(SnapshotStatus));
                        try
                        {
                            string directory = Path.Combine(Path.GetDirectoryName(GetKlineCachePersistPath())!, "intraday-snapshots");
                            var capture = SnapshotCapture ??= new IntradaySnapshotCapture(new MinuteObservationService(_mainVm.DataProvider, SnapshotClock),
                                _mainVm.QuoteService ?? new QuoteService(_mainVm.DataProvider),
                                _mainVm.MarketCalendar ?? new MarketCalendarService(_mainVm.DataProvider), new IntradaySnapshotStore(directory), SnapshotClock);
                            var context = new IntradayScanContext(runId, mode.ToString(), mode switch {
                                SparrowStrategyMode.Classic => SparrowStrategyVersions.Classic, SparrowStrategyMode.V2 => SparrowStrategyVersions.V2,
                                SparrowStrategyMode.V3 => SparrowStrategyVersions.V3, SparrowStrategyMode.V4 => SparrowStrategyVersions.V4,
                                SparrowStrategyMode.V5 => SparrowStrategyVersions.V5, _ => SparrowStrategyVersions.V6 },
                                IntradaySnapshotStoreHash(parameterIdentity), topN, scanStartedUtc, scanCompletedUtc);
                            var saved = await capture.CaptureAsync(context, ranking, captureCts.Token);
                            if (OwnsRun()) { LastSnapshotPath = saved.Path; LastSnapshotRunId = runId; OpenSnapshotCommand.NotifyCanExecuteChanged(); SnapshotStatus = $"当前运行 {runId}: 已保存诊断快照（非 T+1 认证）；交易日 {saved.Snapshot.TradingDate} Asia/Shanghai UTC+08；采集 {saved.Snapshot.CapturedAtUtc:O}；日历 {saved.Snapshot.Calendar}；{saved.Snapshot.Symbols.Count}只；质量 {string.Join(", ", saved.Snapshot.Symbols.Select(s => s.Quality).Distinct())}；单位/区间或报价事件时间证据不足。文件：{saved.Path}"; }
                        }
                        catch (OperationCanceledException) when (captureCts.IsCancellationRequested)
                        { if (!_closed) SnapshotStatus = $"当前运行 {runId}: {(budget.IsCancellationRequested ? "快照采集超时" : "快照采集已取消")}；策略扫描结果保留；{PreviousSnapshotLabel}"; }
                        catch (Exception ex)
                        { if (!_closed) SnapshotStatus = $"当前运行 {runId}: 快照采集失败；策略扫描结果保留：{ex.GetType().Name}；{PreviousSnapshotLabel}"; }
                        finally
                        {
                            _captureCts = null;
                            IsCapturing = false;
                            if (!_closed) CaptureStateChanged();
                        }
                        if (!_closed) OnPropertyChanged(nameof(SnapshotStatus));
                    }
                    if (!OwnsRun()) return;
                    var entrants = _snapshot.Observe(parameterIdentity, startedAt.Date, ranking.Take(topN).ToArray());
                    if (automatic && entrants.Count > 0)
                    {
                        string names = string.Join("、", entrants.Take(SparrowAutoScan.MaxNotifications).Select(item => $"{item.Name}({item.Code})"));
                        Notification($"盘中异动：{names} 首次进入 {mode.ToDisplayName()} Top{topN}。");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ScanOutcome = "Cancelled";
            if (!_closed) { SnapshotStatus = $"当前运行 {runId}: 扫描已取消，本次未采集；{PreviousSnapshotLabel}"; OnPropertyChanged(nameof(SnapshotStatus)); }
            if (!_closed) ResultSummary = "扫描已取消；保留上次完成结果";
            if (!_closed) AppendLog(mode == SparrowStrategyMode.Compare
                ? "Comparison cancelled; no incomplete CSV or stock groups were created."
                : "扫描已取消。");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "捕获到未处理异常");
            ScanOutcome = "Failed";
            if (!_closed) { SnapshotStatus = $"当前运行 {runId}: 扫描失败，本次未采集；{PreviousSnapshotLabel}"; OnPropertyChanged(nameof(SnapshotStatus)); }
            if (!_closed) { ResultSummary = "扫描失败；保留上次完成结果"; AppendLog("❌ 引擎崩溃: " + ex.Message); }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            if (_ownedRun == runId) _ownedRun = null;
            if (_closed) _isScanning = false;
            else { IsScanning = false; OnPropertyChanged(nameof(ScanOutcome)); }
        }

    }

    /// <summary>
    /// Auto-scan tick, driven by the window's minute timer. Starts a scan when the switch is on, the
    /// 14:00-14:50 weekday window is active, the interval has elapsed, and no scan is running.
    /// </summary>
    public void AutoScanTick(DateTime now)
    {
        if (_closed || !SystemSettings.AutoScanEnabled || IsScanning) return;
        if (!SparrowAutoScan.IsDue(now, _lastAutoScanAt) || !StartCommand.CanExecute(null)) return;
        _automaticRequest = now;
        StartCommand.Execute(null);
    }

    private void PopulateResults(IReadOnlyList<SparrowRankedCandidate> ranking, int topN)
    {
        Results.Clear();
        foreach (SparrowRankedCandidate candidate in ranking.Take(topN))
        {
            Results.Add(SparrowResultRow.From(candidate));
        }
    }

    private void ApplyCachedTopN(int? capturedTopN = null)
    {
        if (!_lastCompletedMode.HasValue)
        {
            return;
        }

        int topN = capturedTopN ?? TopN;
        switch (_lastCompletedMode.Value)
        {
            case SparrowStrategyMode.Classic:
                PopulateResults(_lastClassicRanking, topN);
                SetResultGroupIfAny("Classic", $"麻雀_Classic_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastClassicRanking.Take(topN).Select(item => (item.Code, item.Name)));
                ResultSummary = $"Classic 完成 · 候选池 {_lastClassicRanking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastClassicRanking.Count)}只）";
                break;
            case SparrowStrategyMode.V2:
                PopulateResults(_lastV2Ranking, topN);
                SetResultGroupIfAny("V2", $"麻雀_V2_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastV2Ranking.Take(topN).Select(item => (item.Code, item.Name)));
                ResultSummary = $"V2 完成 · 候选池 {_lastV2Ranking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastV2Ranking.Count)}只）";
                break;
            case SparrowStrategyMode.V3:
                PopulateResults(_lastV2Ranking, topN);
                SetResultGroupIfAny("V3", $"麻雀_V3_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastV2Ranking.Take(topN).Select(item => (item.Code, item.Name)));
                ResultSummary = $"V3 完成 · 候选池 {_lastV2Ranking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastV2Ranking.Count)}只）";
                break;
            case SparrowStrategyMode.V4:
                PopulateResults(_lastV2Ranking, topN);
                SetResultGroupIfAny("V4", $"麻雀_V4_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastV2Ranking.Take(topN).Select(item => (item.Code, item.Name)));
                ResultSummary = $"V4 完成 · 候选池 {_lastV2Ranking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastV2Ranking.Count)}只）";
                break;
            case SparrowStrategyMode.V5:
                PopulateResults(_lastV2Ranking, topN);
                SetResultGroupIfAny("V5", $"麻雀_V5_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastV2Ranking.Take(topN).Select(item => (item.Code, item.Name)));
                ResultSummary = $"V5 完成 · 候选池 {_lastV2Ranking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastV2Ranking.Count)}只）";
                break;
            case SparrowStrategyMode.V6:
                PopulateResults(_lastV2Ranking, topN);
                SetResultGroupIfAny("V6", $"麻雀_V6_Top{topN}_{_lastCompletedAt:MMdd}",
                        _lastV2Ranking.Take(topN).Select(item => (item.Code, item.Name)));
                ResultSummary = $"V6 完成 · 候选池 {_lastV2Ranking.Count} · Top{topN} 已生成（{Math.Min(topN, _lastV2Ranking.Count)}只）";
                break;
            case SparrowStrategyMode.Compare:
                ApplyComparisonTopN(topN);
                break;
            case SparrowStrategyMode.PairCompare:
                ApplyPairComparisonTopN(topN);
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
        Results.Clear();
        foreach (var row in both.Concat(classicOnly).Concat(v2Only))
            Results.Add(new SparrowResultRow { Code = row.Code, Name = row.Name, Category = row.Category.ToString(), LeftVersion = "Classic", RightVersion = "V2" });
        SetResultGroupIfAny("Both", $"麻雀_双选_Top{topN}_{_lastCompletedAt:MMdd}", both.Select(row => (row.Code, row.Name)));
        SetResultGroupIfAny("ClassicOnly", $"麻雀_ClassicOnly_Top{topN}_{_lastCompletedAt:MMdd}", classicOnly.Select(row => (row.Code, row.Name)));
        SetResultGroupIfAny("V2Only", $"麻雀_V2Only_Top{topN}_{_lastCompletedAt:MMdd}", v2Only.Select(row => (row.Code, row.Name)));
        ResultSummary = $"Compare 完成 · Top{topN}: 双选 {both.Count} / ClassicOnly {classicOnly.Count} / V2Only {v2Only.Count}";
    }

    private void ApplyPairComparisonTopN(int topN)
    {
        List<SparrowPairComparisonRow> both = _lastPairComparisonRows.Where(row => row.Category == SparrowPairComparisonCategory.Both).Take(topN).ToList();
        List<SparrowPairComparisonRow> leftOnly = _lastPairComparisonRows.Where(row => row.Category == SparrowPairComparisonCategory.LeftOnly).Take(topN).ToList();
        List<SparrowPairComparisonRow> rightOnly = _lastPairComparisonRows.Where(row => row.Category == SparrowPairComparisonCategory.RightOnly).Take(topN).ToList();
        Results.Clear();
        foreach (var row in both.Concat(leftOnly).Concat(rightOnly))
            Results.Add(new SparrowResultRow { Code = row.Code, Name = row.Name, Category = row.Category.ToString(), LeftVersion = _lastPairLeftVersion, RightVersion = _lastPairRightVersion });
        SetResultGroupIfAny("PairBoth", $"麻雀_双选_{_lastPairLeftVersion}_{_lastPairRightVersion}_Top{topN}_{_lastCompletedAt:MMdd}", both.Select(row => (row.Code, row.Name)));
        SetResultGroupIfAny("PairLeftOnly", $"麻雀_仅{_lastPairLeftVersion}_Top{topN}_{_lastCompletedAt:MMdd}", leftOnly.Select(row => (row.Code, row.Name)));
        SetResultGroupIfAny("PairRightOnly", $"麻雀_仅{_lastPairRightVersion}_Top{topN}_{_lastCompletedAt:MMdd}", rightOnly.Select(row => (row.Code, row.Name)));
        ResultSummary = $"双版本对照 · Top{topN}: 双选 {both.Count} / 仅{_lastPairLeftVersion} {leftOnly.Count} / 仅{_lastPairRightVersion} {rightOnly.Count}";
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

    private void ReportRanking(string title, IReadOnlyList<SparrowRankedCandidate> ranking, int TopN)
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

/// <summary>Flat, binding-friendly projection of a ranked candidate for the result grid.</summary>
public sealed class SparrowResultRow
{
    public string Category { get; init; } = "";
    public string LeftVersion { get; init; } = "";
    public string RightVersion { get; init; } = "";
    public double? DisplayScore => Category.Length == 0 ? TotalScore : null;
    public int? DisplayRank => Category.Length == 0 ? Rank : null;
    public int Rank { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public double TotalScore { get; init; }
    public double? RisePercent { get; init; }
    public double? AmountWan { get; init; }
    public double? BuyPressureRatio { get; init; }
    public double? AdhesionPercent { get; init; }
    public double? Turnover { get; init; }
    public double? MomentumPercent { get; init; }
    public double? AlphaMargin { get; init; }

    public static SparrowResultRow From(SparrowRankedCandidate candidate)
    {
        SparrowRankingFeatures features = candidate.Features;
        return new SparrowResultRow
        {
            Rank = candidate.Rank,
            Code = candidate.Code,
            Name = candidate.Name,
            TotalScore = candidate.TotalScore,
            RisePercent = features.RisePercent,
            AmountWan = features.Amount.HasValue ? features.Amount.Value / 10_000.0 : null,
            BuyPressureRatio = features.BuyPressureRatio,
            AdhesionPercent = features.Adhesion.HasValue ? features.Adhesion.Value * 100.0 : null,
            Turnover = features.Turnover,
            MomentumPercent = features.Momentum.HasValue ? features.Momentum.Value * 100.0 : null,
            AlphaMargin = features.AlphaMargin
        };
    }
}
