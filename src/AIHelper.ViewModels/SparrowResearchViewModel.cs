using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using AIHelper.Models;

namespace AIHelper.ViewModels;

public enum SparrowResearchState { Idle, RunningReplay, RunningBacktest, Completed, Cancelled, Error }
public sealed record SparrowResearchMetricRow(int Horizon, int Available, string Average, string Median, string WinRate);
public sealed record SparrowResearchSelectionRow(DateOnly Date, string Code, int Rank, double Score, string ReasonCode, string Reason, string Return1D, string Return3D, string Return5D, string Return10D, string Return20D);

public partial class SparrowResearchViewModel : ObservableObject
{
    private readonly SparrowHistoricalReplayEngine _replay;
    private readonly SparrowHistoricalBacktestEngine _backtest;
    private readonly IProviderHealthService _health;
    private CancellationTokenSource? _cts;
    private SparrowReplayResult? _replayResult;
    private SparrowBacktestResult? _backtestResult;
    [ObservableProperty] private HistoricalMarketDataset? _dataset;
    [ObservableProperty] private SparrowStrategyMode _strategy = SparrowStrategyMode.V2;
    [ObservableProperty] private DateTime _replayDate = DateTime.Today;
    [ObservableProperty] private DateTime _startDate = DateTime.Today.AddMonths(-3);
    [ObservableProperty] private DateTime _endDate = DateTime.Today;
    [ObservableProperty] private int _topN = 10;
    [ObservableProperty] private double _roundTripCostPercent;
    [ObservableProperty] private double _slippagePerSidePercent;
    [ObservableProperty] private SparrowResearchState _state;
    [ObservableProperty] private string _message = "Load a historical dataset to start a research replay.";
    [ObservableProperty] private string _datasetSummary = "No historical dataset loaded";
    public ObservableCollection<SparrowResearchMetricRow> Metrics { get; } = new();
    public ObservableCollection<SparrowResearchSelectionRow> Selections { get; } = new();
    public IReadOnlyList<ProviderHealthSnapshot> ProviderHealth => _health.GetSnapshots();
    public IReadOnlyList<SparrowStrategyMode> SupportedStrategies { get; } = new[] { SparrowStrategyMode.Classic, SparrowStrategyMode.V2 };
    public string Limitations => "Research Backtest / Selection Study: close-to-close only; no portfolio allocation, execution feasibility, historical-universe completeness, or confirmed corporate-action handling.";
    public SparrowResearchViewModel(SparrowHistoricalReplayEngine replay, SparrowHistoricalBacktestEngine backtest, IProviderHealthService health) { _replay = replay; _backtest = backtest; _health = health; }
    public void SetDataset(HistoricalMarketDataset dataset) { Dataset = dataset; ReplayDate = dataset.TradingDates[^1].ToDateTime(TimeOnly.MinValue); StartDate = dataset.TradingDates[0].ToDateTime(TimeOnly.MinValue); EndDate = ReplayDate; DatasetSummary = $"{dataset.DatasetId} · {dataset.Source} · {dataset.TradingDates[0]:yyyy-MM-dd}—{dataset.TradingDates[^1]:yyyy-MM-dd} · {dataset.Klines.Count} symbols · Adjustment: {dataset.PriceAdjustmentMode} · {dataset.Fingerprint[..12]}…"; }
    [RelayCommand] private async Task RunReplayAsync() => await RunAsync(false);
    [RelayCommand] private async Task RunBacktestAsync() => await RunAsync(true);
    [RelayCommand] private void Cancel() { _cts?.Cancel(); }
    [RelayCommand] private async Task ExportReplayAsync(string path) { if (_replayResult != null) await SparrowHistoricalResultExporter.ExportReplayJsonAsync(_replayResult, path); }
    [RelayCommand] private async Task ExportBacktestAsync(string path) { if (_backtestResult != null) await SparrowHistoricalResultExporter.ExportBacktestJsonAsync(_backtestResult, path); }
    private async Task RunAsync(bool backtest)
    {
        if (Dataset == null) { Message = "Historical dataset is required."; State = SparrowResearchState.Error; return; }
        if (!SupportedStrategies.Contains(Strategy)) { Message = "Historical replay supports Classic and V2 only; Legacy and Compare are unsupported."; State = SparrowResearchState.Error; return; }
        if (TopN is < 1 or > 20 || StartDate.Date > EndDate.Date || RoundTripCostPercent < 0 || SlippagePerSidePercent < 0) { Message = "Check date range, Top-N, cost, and slippage inputs."; State = SparrowResearchState.Error; return; }
        _cts?.Dispose(); _cts = new CancellationTokenSource(); var token = _cts.Token; try { State = backtest ? SparrowResearchState.RunningBacktest : SparrowResearchState.RunningReplay; if (backtest) { SparrowBacktestResult result = await Task.Run(() => _backtest.Run(Dataset, BacktestRequest(), token), token); Present(result); } else { SparrowReplayResult result = await Task.Run(() => _replay.Replay(Dataset, ReplayRequest(), token), token); Present(result); } State = SparrowResearchState.Completed; } catch (OperationCanceledException) { State = SparrowResearchState.Cancelled; Message = "Cancelled"; } catch (Exception ex) { State = SparrowResearchState.Error; Message = ex.Message; } finally { _cts.Dispose(); _cts = null; }
    }
    private SparrowReplayRequest ReplayRequest() => new(Strategy, Strategy == SparrowStrategyMode.Classic ? SparrowStrategyVersions.Classic : SparrowStrategyVersions.V2, DateOnly.FromDateTime(ReplayDate), TopN, Strategy == SparrowStrategyMode.Classic ? Classic() : null, Strategy == SparrowStrategyMode.V2 ? V2() : null);
    private SparrowBacktestRequest BacktestRequest() => new(Strategy, Strategy == SparrowStrategyMode.Classic ? SparrowStrategyVersions.Classic : SparrowStrategyVersions.V2, DateOnly.FromDateTime(StartDate), DateOnly.FromDateTime(EndDate), TopN, new[] { 1, 3, 5, 10, 20 }, RoundTripCostPercent / 100, SlippagePerSidePercent / 100, Strategy == SparrowStrategyMode.Classic ? Classic() : null, Strategy == SparrowStrategyMode.V2 ? V2() : null);
    private static SparrowClassicParameterSnapshot Classic() => new(false, 1, 5, 1.1, 1, true, 0, .15);
    private static SparrowV2ParameterSnapshot V2() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false);
    private void Present(SparrowReplayResult result) { _replayResult = result; Message = string.Join(" ", result.Warnings.DefaultIfEmpty($"{result.Support}: {result.Selections.Count} selections")); Selections.Clear(); foreach (var item in result.Selections) Selections.Add(new(item.RankedCandidate.Features.Code.Length == 0 ? default : result.Request.TradingDate, item.Code, item.RankedCandidate.Rank, item.RankedCandidate.TotalScore, item.ReasonCode, item.Reason, "N/A", "N/A", "N/A", "N/A", "N/A")); }
    private void Present(SparrowBacktestResult result) { _backtestResult = result; Message = string.Join(" ", result.Warnings.DefaultIfEmpty($"{result.Selections.Count} selections")); Metrics.Clear(); Selections.Clear(); foreach (var metric in result.Metrics) Metrics.Add(new(metric.HorizonTradingDays, metric.AvailableCount, P(metric.AverageReturnPercent), P(metric.MedianReturnPercent), P(metric.WinRate, true))); foreach (var item in result.Selections) Selections.Add(new(item.ReplayDate, item.Selection.Code, item.Selection.RankedCandidate.Rank, item.Selection.RankedCandidate.TotalScore, item.Selection.ReasonCode, item.Selection.Reason, Return(item,1), Return(item,3), Return(item,5), Return(item,10), Return(item,20))); }
    private static string Return(SparrowBacktestSelection s, int h) => P(s.Outcomes.SingleOrDefault(x => x.HorizonTradingDays == h)?.ReturnPercent);
    private static string P(double? value, bool percent = false) => value.HasValue ? (percent ? value.Value.ToString("P2") : $"{value.Value:+0.00;-0.00;0.00}%") : "N/A";
}
