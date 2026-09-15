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
    [ObservableProperty] private string _datasetDisplayName = "No historical dataset loaded";
    [ObservableProperty] private string _datasetStatus = "Choose a versioned historical dataset JSON file.";
    [ObservableProperty] private string _datasetId = "N/A";
    [ObservableProperty] private string _datasetSource = "N/A";
    [ObservableProperty] private string _datasetDateRange = "N/A";
    [ObservableProperty] private int _datasetSymbolCount;
    [ObservableProperty] private string _datasetFingerprintShort = "N/A";
    [ObservableProperty] private string _priceAdjustmentMode = "Unknown";
    [ObservableProperty] private string _capabilitiesSummary = "N/A";
    [ObservableProperty] private string _datasetSummary = "No historical dataset loaded";
    [ObservableProperty] private SparrowStrategyMode _strategy = SparrowStrategyMode.V2;
    [ObservableProperty] private DateTime _replayDate = DateTime.Today;
    [ObservableProperty] private DateTime _startDate = DateTime.Today.AddMonths(-3);
    [ObservableProperty] private DateTime _endDate = DateTime.Today;
    [ObservableProperty] private int _topN = 10;
    [ObservableProperty] private double _roundTripCostPercent;
    [ObservableProperty] private double _slippagePerSidePercent;
    [ObservableProperty] private SparrowResearchState _state;
    [ObservableProperty] private string _message = "Load a historical dataset to start a research replay.";
    [ObservableProperty] private string _strategyVersionDisplay = SparrowStrategyVersions.V2;
    [ObservableProperty] private string _strategySupportMessage = "Load a dataset to evaluate historical strategy compatibility.";

    public ObservableCollection<SparrowResearchMetricRow> Metrics { get; } = new();
    public ObservableCollection<SparrowResearchSelectionRow> Selections { get; } = new();
    public ObservableCollection<SparrowProviderHealthRow> ProviderHealth { get; } = new();
    public IReadOnlyList<SparrowStrategyMode> SupportedStrategies { get; } = new[] { SparrowStrategyMode.Classic, SparrowStrategyMode.V2 };
    public string Limitations => "Research Backtest / Selection Study: close-to-close only; no portfolio allocation, execution feasibility, complete historical universe, delisted-security coverage, or confirmed corporate-action handling.";
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
        StrategyVersionDisplay = value == SparrowStrategyMode.Classic ? SparrowStrategyVersions.Classic : SparrowStrategyVersions.V2;
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
        DatasetStatus = "Loading and validating historical dataset…"; Message = DatasetStatus;
        try
        {
            HistoricalDatasetLoadResult result = await _loader.LoadAsync(path, _cts.Token);
            if (!result.Success || result.Dataset is null)
            {
                Dataset = null; DatasetLoadState = HistoricalDatasetLoadState.Invalid;
                DatasetStatus = string.Join(" ", result.Errors.DefaultIfEmpty("Historical dataset is invalid.")); Message = DatasetStatus; return;
            }
            SetDataset(result.Dataset); DatasetLoadState = HistoricalDatasetLoadState.Ready;
            DatasetStatus = string.Join(" ", result.Warnings.DefaultIfEmpty("Historical dataset is ready.")); Message = DatasetStatus;
        }
        catch (OperationCanceledException) { DatasetLoadState = Dataset is null ? HistoricalDatasetLoadState.NoDataset : HistoricalDatasetLoadState.Ready; State = SparrowResearchState.Cancelled; Message = "Dataset loading cancelled."; }
        catch (Exception ex) { Dataset = null; DatasetLoadState = HistoricalDatasetLoadState.Invalid; DatasetStatus = ex.Message; Message = DatasetStatus; }
        finally { DisposeActiveOperation(); if (State == SparrowResearchState.LoadingDataset) State = SparrowResearchState.Idle; }
    }

    public void SetDataset(HistoricalMarketDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset); Dataset = dataset;
        ReplayDate = dataset.TradingDates[^1].ToDateTime(TimeOnly.MinValue); StartDate = dataset.TradingDates[0].ToDateTime(TimeOnly.MinValue); EndDate = ReplayDate;
        DatasetDisplayName = string.IsNullOrWhiteSpace(DatasetPath) ? dataset.DatasetId : Path.GetFileName(DatasetPath);
        DatasetId = dataset.DatasetId; DatasetSource = dataset.Source; DatasetDateRange = $"{dataset.TradingDates[0]:yyyy-MM-dd} — {dataset.TradingDates[^1]:yyyy-MM-dd}";
        DatasetSymbolCount = dataset.Klines.Count; DatasetFingerprintShort = ShortFingerprint(dataset.Fingerprint); PriceAdjustmentMode = dataset.PriceAdjustmentMode;
        CapabilitiesSummary = Capabilities(dataset.Capabilities); DatasetSummary = $"{DatasetId} · {DatasetSource} · {DatasetDateRange} · {DatasetSymbolCount} symbols · Adjustment: {PriceAdjustmentMode} · {DatasetFingerprintShort}";
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
            ProviderHealth.Add(new SparrowProviderHealthRow(health.Provider.ToString(), health.State.ToString(), FormatLatency(health.LastLatency), FormatActivity(health)));
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
        catch (OperationCanceledException) { State = SparrowResearchState.Cancelled; Message = "Cancelled"; }
        catch (Exception ex) { State = SparrowResearchState.Error; Message = ex.Message; }
        finally { DisposeActiveOperation(); }
    }
    private bool ValidateRun(bool backtest)
    {
        if (!IsDatasetReady) return Fail("Historical dataset is required.");
        if (!IsStrategySupported) return Fail(StrategySupportMessage);
        if (TopN is < 1 or > 20) return Fail("Top-N must be between 1 and 20.");
        if (RoundTripCostPercent < 0) return Fail("Round-trip cost cannot be negative.");
        if (SlippagePerSidePercent < 0) return Fail("Per-side slippage cannot be negative.");
        if (!Dataset!.TradingDates.Contains(DateOnly.FromDateTime(ReplayDate))) return Fail("Replay date must be a dataset trading date.");
        if (backtest && (StartDate.Date > EndDate.Date || !Dataset.TradingDates.Contains(DateOnly.FromDateTime(StartDate)) || !Dataset.TradingDates.Contains(DateOnly.FromDateTime(EndDate)))) return Fail("Backtest dates must be dataset trading dates with Start on or before End.");
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
        StrategySupportMessage = !IsDatasetReady ? "Load a dataset to evaluate historical strategy compatibility."
            : HasRequiredQuoteCapabilities(Dataset!.Capabilities) ? $"{StrategyVersionDisplay} is supported by the dataset's declared quote capabilities."
            : "Historical data does not provide required amount, outer-volume, and inner-volume fields for this strategy.";
        OnPropertyChanged(nameof(IsStrategySupported));
    }
    private static bool HasRequiredQuoteCapabilities(HistoricalDataCapabilities capabilities) => capabilities.HasAmount && capabilities.HasOuterVolume && capabilities.HasInnerVolume;
    private SparrowReplayRequest ReplayRequest() => new(Strategy, StrategyVersionDisplay, DateOnly.FromDateTime(ReplayDate), TopN, Strategy == SparrowStrategyMode.Classic ? Classic() : null, Strategy == SparrowStrategyMode.V2 ? V2() : null);
    private SparrowBacktestRequest BacktestRequest() => new(Strategy, StrategyVersionDisplay, DateOnly.FromDateTime(StartDate), DateOnly.FromDateTime(EndDate), TopN, new[] { 1, 3, 5, 10, 20 }, RoundTripCostPercent / 100, SlippagePerSidePercent / 100, Strategy == SparrowStrategyMode.Classic ? Classic() : null, Strategy == SparrowStrategyMode.V2 ? V2() : null);
    private static SparrowClassicParameterSnapshot Classic() => new(false, 1, 5, 1.1, 1, true, 0, .15);
    private static SparrowV2ParameterSnapshot V2() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false);
    private void Present(SparrowReplayResult result) { _replayResult = result; Message = string.Join(" ", result.Warnings.DefaultIfEmpty($"{result.Support}: {result.Selections.Count} selections")); Selections.Clear(); foreach (SparrowReplaySelection item in result.Selections) Selections.Add(new(result.Request.TradingDate, item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore, item.ReasonCode, item.Reason, "N/A", "N/A", "N/A", "N/A", "N/A")); NotifyCommandState(); }
    private void Present(SparrowBacktestResult result) { _backtestResult = result; Message = string.Join(" ", result.Warnings.DefaultIfEmpty($"{result.Selections.Count} selections")); Metrics.Clear(); Selections.Clear(); foreach (SparrowHorizonMetrics metric in result.Metrics) Metrics.Add(new(metric.HorizonTradingDays, metric.AvailableCount, P(metric.AverageReturnPercent), P(metric.MedianReturnPercent), P(metric.WinRate, true))); foreach (SparrowBacktestSelection item in result.Selections) Selections.Add(new(item.ReplayDate, item.Selection.Code, item.Selection.RankedCandidate.Rank, item.Selection.RankedCandidate.TotalScore, item.Selection.ReasonCode, item.Selection.Reason, Return(item, 1), Return(item, 3), Return(item, 5), Return(item, 10), Return(item, 20))); NotifyCommandState(); }
    private static string Return(SparrowBacktestSelection selection, int horizon) => P(selection.Outcomes.SingleOrDefault(value => value.HorizonTradingDays == horizon)?.ReturnPercent);
    private static string P(double? value, bool fraction = false) => value.HasValue ? (fraction ? value.Value.ToString("P2") : $"{value.Value:+0.00;-0.00;0.00}%") : "N/A";
    private static string ShortFingerprint(string value) => value.Length <= 12 ? value : $"{value[..12]}…";
    private static string Capabilities(HistoricalDataCapabilities value) => $"Amount: {value.HasAmount}; Turnover: {value.HasTurnover}; Outer volume: {value.HasOuterVolume}; Inner volume: {value.HasInnerVolume}; Classic regime: {value.HasClassicMarketRegime}; V2 Shanghai change: {value.HasV2ShanghaiDailyPercent}";
    private static string FormatLatency(TimeSpan? value) => value.HasValue ? $"{value.Value.TotalMilliseconds:0} ms" : "N/A";
    private static string FormatActivity(ProviderHealthSnapshot health) => health.LastFailure is not null ? $"Last failure: {health.LastFailure.Kind}" : health.LastSuccessUtc.HasValue ? $"Last success: {health.LastSuccessUtc.Value.LocalDateTime:yyyy-MM-dd HH:mm}" : "No activity";
    private void CancelAndDisposeActiveOperation() { _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
    private void DisposeActiveOperation() { _cts?.Dispose(); _cts = null; }
    public void Dispose() => CancelAndDisposeActiveOperation();
}
