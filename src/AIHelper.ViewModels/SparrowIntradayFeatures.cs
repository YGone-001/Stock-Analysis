using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.StockData;
using AIHelper.Services.StockData;
using CommunityToolkit.Mvvm.Input;

namespace AIHelper.ViewModels;

public partial class SparrowViewModel
{
    public ObservableCollection<IntradayFeatureDisplayRow> FeatureRows { get; } = new();
    private IntradayFeatureDisplayRow? _selectedFeatureRow;
    public IntradayFeatureDisplayRow? SelectedFeatureRow { get => _selectedFeatureRow; set => SetProperty(ref _selectedFeatureRow, value); }
    public bool IsAnalyzingFeatures { get; private set; }
    public string FeatureStatus { get; private set; } = "保存快照后点击分析；仅诊断，不产生评分或交易建议。RS 需要同窗指数；量价单位、历史基线缺失时显示 Unavailable。";
    private CancellationTokenSource? _featureCts;
    private long _featureGeneration;
    internal Func<IntradaySnapshot, string, DateTimeOffset, CancellationToken, Task<IReadOnlyList<IntradayFeatureResult>>>? FeatureAnalysisOverride { get; set; }
    /// <summary>Retained results of the last completed feature analysis; input of the T1 ranking workflow.</summary>
    internal IReadOnlyList<IntradayFeatureResult>? LastFeatureResults { get; private set; }
    private bool CanAnalyzeFeatures() => !_closed && !IsScanning && !IsCapturing && !IsAnalyzingFeatures && !IsRankingT1 && CanOpenSnapshot();
    private void FeatureCommandsChanged()
    {
        AnalyzeFeaturesCommand.NotifyCanExecuteChanged(); CancelScanCommand.NotifyCanExecuteChanged();
        RunT1RankingCommand.NotifyCanExecuteChanged(); ExportT1RankingCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAnalyzingFeatures));
    }
    private void InvalidateFeatures(Guid runId)
    {
        _featureGeneration++;
        _featureCts?.Cancel();
        FeatureRows.Clear(); SelectedFeatureRow = null; LastFeatureResults = null;
        FeatureStatus = $"当前扫描 {runId}: 尚未分析；若打开上次快照，分析结果将明确显示上次运行身份。";
        OnPropertyChanged(nameof(FeatureStatus));
        InvalidateT1Ranking(runId);
    }
    [RelayCommand(CanExecute = nameof(CanAnalyzeFeatures))]
    private async Task AnalyzeFeatures()
    {
        if (!CanAnalyzeFeatures()) return;
        string path = LastSnapshotPath!;
        using var cts = new CancellationTokenSource();
        _featureCts = cts;
        long generation = ++_featureGeneration;
        bool OwnsAnalysis() => !_closed && !cts.IsCancellationRequested && generation == _featureGeneration && path == LastSnapshotPath;
        IsAnalyzingFeatures = true; FeatureCommandsChanged();
        InvalidateResearchRetention(); _featureCompletedAtUtc = null;
        FeatureRows.Clear(); SelectedFeatureRow = null; LastFeatureResults = null;
        FeatureStatus = $"正在分析文件 {path}；不重新采集行情"; OnPropertyChanged(nameof(FeatureStatus));
        try
        {
            var snapshot = new IntradaySnapshotStore(Path.GetDirectoryName(path)!).Load(path);
            string fingerprint = IntradaySnapshotStore.Fingerprint(snapshot);
            var results = FeatureAnalysisOverride != null
                ? await FeatureAnalysisOverride(snapshot, fingerprint, snapshot.RequestedAsOf, cts.Token)
                : await Task.Run(() => IntradayFeatureAnalysis.Analyze(snapshot, fingerprint, snapshot.RequestedAsOf, token: cts.Token), cts.Token);
            if (!OwnsAnalysis()) return;
            LastFeatureResults = results;
            _featureCompletedAtUtc = SnapshotClock.GetUtcNow();
            foreach (var result in results) FeatureRows.Add(new(result));
            SelectedFeatureRow = FeatureRows.FirstOrDefault();
            FeatureStatus = $"Diagnostic（非认证）；运行 {snapshot.Scan.RunId}；{snapshot.Scan.Strategy}/{snapshot.Scan.StrategyVersion}；截止 {snapshot.RequestedAsOf:O}；Asia/Shanghai UTC+08；快照 {fingerprint}；{FeatureRows.Count}只；缺失基准/单位/历史数据各项单独说明。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { if (!_closed && generation == _featureGeneration) FeatureStatus = "特征分析已取消；策略结果保留。"; }
        catch (Exception ex)
        { if (OwnsAnalysis()) FeatureStatus = "特征分析失败；策略结果保留：" + ex.GetType().Name; }
        finally
        {
            if (!_closed && generation == _featureGeneration && cts.IsCancellationRequested) FeatureStatus = "特征分析已取消；策略结果保留。";
            if (ReferenceEquals(_featureCts, cts)) _featureCts = null;
            IsAnalyzingFeatures = false;
            if (!_closed) { FeatureCommandsChanged(); OnPropertyChanged(nameof(FeatureStatus)); }
        }
    }
}

public sealed record IntradayFeatureDisplayRow(IntradayFeatureResult Result)
{
    public string Code => Result.Symbol;
    public string Name => Result.Name;
    public string DataTime => Result.EffectiveObservationTime?.ToOffset(ChinaExchangeTime.Offset).ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "Unavailable";
    public string Return5m => Result.Return5m.Display;
    public string Return15m => Result.Return15m.Display;
    public string Persistence => Result.PricePersistence.Display;
    public string Pullback => Result.PullbackFromObservedHigh.Display;
    public string RS5m => Result.RelativeStrength5m.Display;
    public string RS15m => Result.RelativeStrength15m.Display;
    public string VolumeAcceleration => Result.VolumeAcceleration.Display;
    public string Vwap => Result.TradeVwap.Display;
    public string Rvol => Result.HistoricalSameClockRvol.Display;
    public string Evidence => Result.EvidenceStatus;
    public string Details => $"版本 {Result.ComputationVersion}；分析政策 {Result.Policy}；量价换算证据 {Result.QuantityEvidence}；运行 {Result.Scan}；快照 {Result.SnapshotFingerprint}；截止 {Result.RequestedAsOf:O}；原快照 Certified={Result.SourceSnapshotCertified}；分析仍为 Diagnostic\n" + string.Join("\n", new[] {
        ("LatestPrice", Result.LatestPrice), ("Return5m", Result.Return5m), ("Return15m", Result.Return15m), ("Persistence", Result.PricePersistence),
        ("Pullback", Result.PullbackFromObservedHigh), ("RS5m", Result.RelativeStrength5m), ("RS15m", Result.RelativeStrength15m),
        ("VolumeAcceleration", Result.VolumeAcceleration), ("TradeVwap", Result.TradeVwap), ("HistoricalSameClockRvol", Result.HistoricalSameClockRvol)
    }.Select(p => $"{p.Item1}: {p.Item2.Display}; 窗口 {p.Item2.WindowStart:O} — {p.Item2.WindowEnd:O}; {p.Item2.Source}; {p.Item2.Reason}; 样本/转移 {p.Item2.BaselineSamples}/{p.Item2.EligibleTransitions}")) + "\n" + string.Join("\n", Result.Warnings) + "\n原始观测证据：\n" + string.Join("\n", Result.SourceProvenance.Select(r => $"{r.Symbol}: 标签 {r.ExchangeEventTime:O}; 原始接收 {r.ObservedAtUtc:O}; 可用 {r.AvailableAtUtc:O}; 传输接收 {r.TransportReceivedAtUtc:O}; {r.Provider}; {r.Provenance}; 单位 {r.VolumeUnit}/{r.AmountUnit}; 区间 {r.Interval}"));
}
