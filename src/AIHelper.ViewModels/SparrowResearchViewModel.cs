using System.Collections.ObjectModel;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services;
using AIHelper.Services.StockData.Sparrow;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AIHelper.ViewModels;

public enum SparrowResearchState { Idle, LoadingDataset, RunningReplay, RunningBacktest, Completed, Cancelled, Error }
public sealed record SparrowResearchMetricRow(int Horizon, int Available, string Average, string Median, string WinRate);
public sealed record SparrowResearchSelectionRow(DateOnly Date, string Code, int Rank, double Score, string ReasonCode, string Reason, string Return1D, string Return3D, string Return5D, string Return10D, string Return20D);
public sealed record SparrowProviderHealthRow(string Provider, string State, string LastLatency, string LastActivity);
/// <summary>策略下拉项：值走枚举，显示走中文，避免把枚举名直接摆在界面上。</summary>
public sealed record SparrowStrategyOption(SparrowStrategyMode Value, string DisplayName);

/// <summary>Presentation-only orchestrator. It never reads files, opens dialogs, or calls live market providers.</summary>
public partial class SparrowResearchViewModel : ObservableObject, IDisposable
{
    private readonly SparrowHistoricalReplayEngine _replay;
    private readonly SparrowHistoricalBacktestEngine _backtest;
    private readonly IProviderHealthService _health;
    private readonly IHistoricalDatasetLoader _loader;
    private readonly IFileDialogService _fileDialogs;
    private readonly IHistoricalResultExporter _exporter;
    private CancellationTokenSource? _cts;
    private SparrowReplayResult? _replayResult;
    private SparrowBacktestResult? _backtestResult;

    [ObservableProperty] private HistoricalMarketDataset? _dataset;
    [ObservableProperty] private HistoricalDatasetLoadState _datasetLoadState = HistoricalDatasetLoadState.NoDataset;
    [ObservableProperty] private string _datasetPath = string.Empty;
    [ObservableProperty] private string _datasetDisplayName = "尚未加载历史数据集";
    [ObservableProperty] private string _datasetStatus = "请选择一份带版本号的历史数据集 JSON 文件。";
    [ObservableProperty] private string _datasetId = SparrowResearchDisplayText.None;
    [ObservableProperty] private string _datasetSource = SparrowResearchDisplayText.None;
    [ObservableProperty] private string _datasetDateRange = SparrowResearchDisplayText.None;
    [ObservableProperty] private int _datasetSymbolCount;
    [ObservableProperty] private string _datasetFingerprintShort = SparrowResearchDisplayText.None;
    [ObservableProperty] private string _datasetSchema = SparrowResearchDisplayText.None;
    [ObservableProperty] private string _universeQuality = SparrowResearchDisplayText.Unknown;
    [ObservableProperty] private string _lifecycleQuality = SparrowResearchDisplayText.Unknown;
    [ObservableProperty] private string _dataQualityWarning = string.Empty;
    [ObservableProperty] private string _priceAdjustmentMode = SparrowResearchDisplayText.Unknown;
    [ObservableProperty] private string _capabilitiesSummary = SparrowResearchDisplayText.None;
    [ObservableProperty] private string _datasetSummary = string.Empty;
    [ObservableProperty] private SparrowStrategyMode _strategy = SparrowStrategyMode.V2;
    [ObservableProperty] private DateTime _replayDate = DateTime.Today;
    [ObservableProperty] private DateTime _startDate = DateTime.Today.AddMonths(-3);
    [ObservableProperty] private DateTime _endDate = DateTime.Today;
    [ObservableProperty] private int _topN = 10;
    [ObservableProperty] private double _roundTripCostPercent;
    [ObservableProperty] private double _slippagePerSidePercent;
    [ObservableProperty] private SparrowResearchState _state;
    [ObservableProperty] private string _message = "加载历史数据集后即可开始研究回放。";
    [ObservableProperty] private string _strategyVersionDisplay = SparrowStrategyVersions.V2;
    [ObservableProperty] private string _strategySupportMessage = "加载数据集后可评估历史策略的兼容性。";

    public ObservableCollection<SparrowResearchMetricRow> Metrics { get; } = new();
    public ObservableCollection<SparrowResearchSelectionRow> Selections { get; } = new();
    public ObservableCollection<SparrowProviderHealthRow> ProviderHealth { get; } = new();
    public IReadOnlyList<SparrowStrategyOption> SupportedStrategies { get; } = new[]
    {
        new SparrowStrategyOption(SparrowStrategyMode.Classic, SparrowResearchDisplayText.Strategy(SparrowStrategyMode.Classic)),
        new SparrowStrategyOption(SparrowStrategyMode.V2, SparrowResearchDisplayText.Strategy(SparrowStrategyMode.V2)),
        new SparrowStrategyOption(SparrowStrategyMode.V3, SparrowResearchDisplayText.Strategy(SparrowStrategyMode.V3))
    };
    public string Limitations => "研究回测 / 选股研究：仅按收盘价对收盘价计算；不包含组合资金分配、可执行性校验、完整历史股票池、退市证券覆盖，也未确认公司行为（除权除息）的处理方式。";
    public bool IsDatasetReady => DatasetLoadState == HistoricalDatasetLoadState.Ready && Dataset is not null;
    public bool IsStrategySupported => IsDatasetReady && HasRequiredQuoteCapabilities(Dataset!.Capabilities);
    public bool IsBusy => State is SparrowResearchState.LoadingDataset or SparrowResearchState.RunningReplay or SparrowResearchState.RunningBacktest;

    public SparrowResearchViewModel(SparrowHistoricalReplayEngine replay, SparrowHistoricalBacktestEngine backtest, IProviderHealthService health, IHistoricalDatasetLoader loader, IFileDialogService fileDialogs, IHistoricalResultExporter exporter)
    {
        _replay = replay; _backtest = backtest; _health = health; _loader = loader; _fileDialogs = fileDialogs; _exporter = exporter;
        RefreshProviderHealth();
    }

    partial void OnStrategyChanged(SparrowStrategyMode value)
    {
        StrategyVersionDisplay = value switch
        {
            SparrowStrategyMode.Classic => SparrowStrategyVersions.Classic,
            SparrowStrategyMode.V3 => SparrowStrategyVersions.V3,
            _ => SparrowStrategyVersions.V2
        };
        RefreshStrategyCompatibility(); NotifyCommandState();
    }
    partial void OnDatasetLoadStateChanged(HistoricalDatasetLoadState value) { OnPropertyChanged(nameof(IsDatasetReady)); OnPropertyChanged(nameof(IsStrategySupported)); RefreshStrategyCompatibility(); NotifyCommandState(); }
    partial void OnDatasetChanged(HistoricalMarketDataset? value) { OnPropertyChanged(nameof(IsDatasetReady)); OnPropertyChanged(nameof(IsStrategySupported)); RefreshStrategyCompatibility(); NotifyCommandState(); }
    partial void OnStateChanged(SparrowResearchState value) { OnPropertyChanged(nameof(IsBusy)); NotifyCommandState(); }

    [RelayCommand(CanExecute = nameof(CanLoadDataset))]
    private async Task LoadDatasetAsync()
    {
        string? path = _fileDialogs.OpenHistoricalDataset();
        if (!string.IsNullOrWhiteSpace(path)) await LoadDatasetFromPathAsync(path);
    }

    /// <summary>Parsing stays behind IHistoricalDatasetLoader even when a host already owns the selected path.</summary>
    public async Task LoadDatasetFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        CancelAndDisposeActiveOperation(); _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        DatasetLoadState = HistoricalDatasetLoadState.Loading; State = SparrowResearchState.LoadingDataset; DatasetPath = path;
        DatasetStatus = "正在加载并校验历史数据集…"; Message = DatasetStatus;
        try
        {
            HistoricalDatasetLoadResult result = await _loader.LoadAsync(path, _cts.Token);
            if (!result.Success || result.Dataset is null)
            {
                Dataset = null; DatasetLoadState = HistoricalDatasetLoadState.Invalid;
                DatasetStatus = string.Join(" ", result.Errors.DefaultIfEmpty("历史数据集无效。")); Message = DatasetStatus; return;
            }
            SetDataset(result.Dataset); DatasetLoadState = HistoricalDatasetLoadState.Ready;
            DatasetStatus = string.Join(" ", result.Warnings.DefaultIfEmpty("历史数据集已就绪。")); Message = DatasetStatus;
        }
        catch (OperationCanceledException) { DatasetLoadState = Dataset is null ? HistoricalDatasetLoadState.NoDataset : HistoricalDatasetLoadState.Ready; State = SparrowResearchState.Cancelled; Message = "已取消数据集加载。"; }
        catch (Exception ex) { Dataset = null; DatasetLoadState = HistoricalDatasetLoadState.Invalid; DatasetStatus = ex.Message; Message = DatasetStatus; }
        finally { DisposeActiveOperation(); if (State == SparrowResearchState.LoadingDataset) State = SparrowResearchState.Idle; }
    }

    public void SetDataset(HistoricalMarketDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset); Dataset = dataset;
        ReplayDate = dataset.TradingDates[^1].ToDateTime(TimeOnly.MinValue); StartDate = dataset.TradingDates[0].ToDateTime(TimeOnly.MinValue); EndDate = ReplayDate;
        DatasetDisplayName = string.IsNullOrWhiteSpace(DatasetPath) ? dataset.DatasetId : Path.GetFileName(DatasetPath);
        DatasetId = dataset.DatasetId; DatasetSource = dataset.Source; DatasetDateRange = $"{dataset.TradingDates[0]:yyyy-MM-dd} — {dataset.TradingDates[^1]:yyyy-MM-dd}";
        DatasetSymbolCount = dataset.Klines.Count; DatasetFingerprintShort = ShortFingerprint(dataset.Fingerprint);
        PriceAdjustmentMode = SparrowResearchDisplayText.PriceAdjustment(dataset.PriceAdjustmentMode.ToString());
        DatasetSchema = $"V{dataset.SchemaVersion}"; UniverseQuality = SparrowResearchDisplayText.UniverseQuality(dataset.Metadata.UniverseQuality);
        LifecycleQuality = string.Join("、", dataset.Securities.Values.Select(value => value.LifecycleQuality).Distinct().OrderBy(value => value).Select(SparrowResearchDisplayText.LifecycleQuality));
        DataQualityWarning = dataset.Metadata.UniverseQuality == HistoricalUniverseQuality.Complete
            ? string.Empty : "历史股票池不完整，可能仍存在幸存者偏差。";
        CapabilitiesSummary = Capabilities(dataset.Capabilities); DatasetSummary = $"{DatasetId} · {DatasetSource} · {DatasetDateRange} · {DatasetSymbolCount} 只标的 · 复权方式：{PriceAdjustmentMode} · 指纹 {DatasetFingerprintShort}";
        RefreshStrategyCompatibility();
    }

    [RelayCommand(CanExecute = nameof(CanRun))] private Task RunReplayAsync() => RunAsync(false);
    [RelayCommand(CanExecute = nameof(CanRun))] private Task RunBacktestAsync() => RunAsync(true);
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() => _cts?.Cancel();
    [RelayCommand(CanExecute = nameof(CanExportReplay))]
    private async Task ExportReplayAsync()
    {
        if (_replayResult is null) return;
        string? path = _fileDialogs.SaveReplayResult($"sparrow-replay-{_replayResult.Request.StrategyVersion}-{_replayResult.Request.TradingDate:yyyyMMdd}.json");
        if (!string.IsNullOrWhiteSpace(path)) await _exporter.ExportReplayJsonAsync(_replayResult, path);
    }
    [RelayCommand(CanExecute = nameof(CanExportBacktest))]
    private async Task ExportBacktestAsync()
    {
        if (_backtestResult is null) return;
        string? path = _fileDialogs.SaveBacktestResult($"sparrow-backtest-{_backtestResult.Request.StrategyVersion}-{_backtestResult.Request.StartDate:yyyyMMdd}-{_backtestResult.Request.EndDate:yyyyMMdd}.json");
        if (!string.IsNullOrWhiteSpace(path)) await _exporter.ExportBacktestJsonAsync(_backtestResult, path);
    }
    [RelayCommand]
    public void RefreshProviderHealth()
    {
        ProviderHealth.Clear();
        foreach (ProviderHealthSnapshot health in _health.GetSnapshots().OrderBy(item => item.Provider))
            ProviderHealth.Add(new SparrowProviderHealthRow(
                SparrowResearchDisplayText.Provider(health.Provider),
                SparrowResearchDisplayText.ProviderHealth(health.State),
                FormatLatency(health.LastLatency),
                FormatActivity(health)));
    }

    private async Task RunAsync(bool backtest)
    {
        if (!ValidateRun(backtest)) return;
        CancelAndDisposeActiveOperation(); _cts = new CancellationTokenSource();
        try
        {
            State = backtest ? SparrowResearchState.RunningBacktest : SparrowResearchState.RunningReplay;
            if (backtest) Present(await Task.Run(() => _backtest.Run(Dataset!, BacktestRequest(), _cts.Token), _cts.Token));
            else Present(await Task.Run(() => _replay.Replay(Dataset!, ReplayRequest(), _cts.Token), _cts.Token));
            State = SparrowResearchState.Completed; RefreshProviderHealth();
        }
        catch (OperationCanceledException) { State = SparrowResearchState.Cancelled; Message = "已取消。"; }
        catch (Exception ex) { State = SparrowResearchState.Error; Message = ex.Message; }
        finally { DisposeActiveOperation(); }
    }
    private bool ValidateRun(bool backtest)
    {
        if (!IsDatasetReady) return Fail("请先加载历史数据集。");
        if (!IsStrategySupported) return Fail(StrategySupportMessage);
        if (TopN is < 1 or > 20) return Fail("选股数量（Top-N）必须在 1 到 20 之间。");
        if (RoundTripCostPercent < 0) return Fail("双边总成本不能为负数。");
        if (SlippagePerSidePercent < 0) return Fail("单边滑点不能为负数。");
        if (!Dataset!.TradingDates.Contains(DateOnly.FromDateTime(ReplayDate))) return Fail("回放日期必须是数据集内的交易日。");
        if (backtest && (StartDate.Date > EndDate.Date || !Dataset.TradingDates.Contains(DateOnly.FromDateTime(StartDate)) || !Dataset.TradingDates.Contains(DateOnly.FromDateTime(EndDate)))) return Fail("回测的开始与结束日期都必须落在数据集的交易日上，且开始日期不能晚于结束日期。");
        return true;
    }
    private bool Fail(string message) { State = SparrowResearchState.Error; Message = message; return false; }
    private bool CanLoadDataset() => !IsBusy;
    private bool CanRun() => !IsBusy && IsDatasetReady && IsStrategySupported;
    private bool CanCancel() => IsBusy;
    private bool CanExportReplay() => !IsBusy && _replayResult is not null;
    private bool CanExportBacktest() => !IsBusy && _backtestResult is not null;
    private void NotifyCommandState() { LoadDatasetCommand.NotifyCanExecuteChanged(); RunReplayCommand.NotifyCanExecuteChanged(); RunBacktestCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ExportReplayCommand.NotifyCanExecuteChanged(); ExportBacktestCommand.NotifyCanExecuteChanged(); }
    private void RefreshStrategyCompatibility()
    {
        StrategySupportMessage = !IsDatasetReady ? "加载数据集后可评估历史策略的兼容性。"
            : HasRequiredQuoteCapabilities(Dataset!.Capabilities) ? $"{StrategyVersionDisplay} 已由数据集声明的行情能力支持。"
            : "历史数据缺少该策略所需的成交额、外盘量、内盘量字段。";
        OnPropertyChanged(nameof(IsStrategySupported));
    }
    private static bool HasRequiredQuoteCapabilities(HistoricalDataCapabilities capabilities) => capabilities.HasAmount && capabilities.HasOuterVolume && capabilities.HasInnerVolume;
    private SparrowReplayRequest ReplayRequest() => new(Strategy, StrategyVersionDisplay, DateOnly.FromDateTime(ReplayDate), TopN,
        Strategy == SparrowStrategyMode.Classic ? Classic() : null,
        Strategy == SparrowStrategyMode.V2 ? V2() : null,
        Strategy == SparrowStrategyMode.V3 ? V3() : null);
    private SparrowBacktestRequest BacktestRequest() => new(Strategy, StrategyVersionDisplay, DateOnly.FromDateTime(StartDate), DateOnly.FromDateTime(EndDate), TopN, new[] { 1, 3, 5, 10, 20 }, RoundTripCostPercent / 100, SlippagePerSidePercent / 100,
        Strategy == SparrowStrategyMode.Classic ? Classic() : null,
        Strategy == SparrowStrategyMode.V2 ? V2() : null,
        Strategy == SparrowStrategyMode.V3 ? V3() : null);
    private static SparrowClassicParameterSnapshot Classic() => new(false, 1, 5, 1.1, 1, true, 0, .15);
    private static SparrowV2ParameterSnapshot V2() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false);
    private static SparrowV3ParameterSnapshot V3() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false,
        SparrowParameterDefaults.RsiMin, SparrowParameterDefaults.RsiMax, SparrowParameterDefaults.RequireMacdBullish, 14, 12, 26, 9);
    private void Present(SparrowReplayResult result) { _replayResult = result; Message = string.Join(" ", result.Warnings.DefaultIfEmpty($"{SparrowResearchDisplayText.Support(result.Support)}：本次入选 {result.Selections.Count} 只")); Selections.Clear(); foreach (SparrowReplaySelection item in result.Selections) Selections.Add(new(result.Request.TradingDate, item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore, item.ReasonCode, SparrowResearchDisplayText.Reason(item.ReasonCode, item.Reason), SparrowResearchDisplayText.None, SparrowResearchDisplayText.None, SparrowResearchDisplayText.None, SparrowResearchDisplayText.None, SparrowResearchDisplayText.None)); NotifyCommandState(); }
    private void Present(SparrowBacktestResult result) { _backtestResult = result; Message = string.Join(" ", result.Warnings.DefaultIfEmpty($"回测完成：共 {result.Selections.Count} 条入选记录")); Metrics.Clear(); Selections.Clear(); foreach (SparrowHorizonMetrics metric in result.Metrics) Metrics.Add(new(metric.HorizonTradingDays, metric.AvailableCount, P(metric.AverageReturnPercent), P(metric.MedianReturnPercent), P(metric.WinRate, true))); foreach (SparrowBacktestSelection item in result.Selections) Selections.Add(new(item.ReplayDate, item.Selection.Code, item.Selection.RankedCandidate.Rank, item.Selection.RankedCandidate.TotalScore, item.Selection.ReasonCode, SparrowResearchDisplayText.Reason(item.Selection.ReasonCode, item.Selection.Reason), Return(item, 1), Return(item, 3), Return(item, 5), Return(item, 10), Return(item, 20))); NotifyCommandState(); }
    private static string Return(SparrowBacktestSelection selection, int horizon) => P(selection.Outcomes.SingleOrDefault(value => value.HorizonTradingDays == horizon)?.ReturnPercent);
    private static string P(double? value, bool fraction = false) => value.HasValue ? (fraction ? value.Value.ToString("P2") : $"{value.Value:+0.00;-0.00;0.00}%") : SparrowResearchDisplayText.None;
    private static string ShortFingerprint(string value) => value.Length <= 12 ? value : $"{value[..12]}…";
    private static string Capabilities(HistoricalDataCapabilities value) => $"成交额：{YesNo(value.HasAmount)}；换手率：{YesNo(value.HasTurnover)}；外盘量：{YesNo(value.HasOuterVolume)}；内盘量：{YesNo(value.HasInnerVolume)}；经典大盘状态：{YesNo(value.HasClassicMarketRegime)}；V2 沪指涨跌幅：{YesNo(value.HasV2ShanghaiDailyPercent)}";
    private static string YesNo(bool value) => value ? "有" : "无";
    private static string FormatLatency(TimeSpan? value) => value.HasValue ? $"{value.Value.TotalMilliseconds:0} 毫秒" : SparrowResearchDisplayText.None;
    private static string FormatActivity(ProviderHealthSnapshot health) => health.LastFailure is not null
        ? $"最近失败：{SparrowResearchDisplayText.FailureKind(health.LastFailure.Kind)}"
        : health.LastSuccessUtc.HasValue ? $"最近成功：{health.LastSuccessUtc.Value.LocalDateTime:yyyy-MM-dd HH:mm}" : "无活动记录";
    private void CancelAndDisposeActiveOperation() { _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
    private void DisposeActiveOperation() { _cts?.Dispose(); _cts = null; }
    public void Dispose() => CancelAndDisposeActiveOperation();
}
