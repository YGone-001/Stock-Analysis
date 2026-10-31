using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using AIHelper.Core.StockData;
using AIHelper.Services.StockData;
using CommunityToolkit.Mvvm.Input;

namespace AIHelper.ViewModels;

/// <summary>
/// T1-Rank / PriceOnlyV1 research ranking workflow: consumes the completed intraday feature analysis
/// and the retained snapshot, re-verifies everything fail-closed, and produces a diagnostic research
/// shortlist plus a reproducible artifact. Separate generation/CTS ownership; never mutates legacy
/// strategy results, the original snapshot, or the feature table.
/// </summary>
public partial class SparrowViewModel
{
    public ObservableCollection<T1RankDisplayRow> T1RankRows { get; } = new();
    private T1RankDisplayRow? _selectedT1RankRow;
    public T1RankDisplayRow? SelectedT1RankRow { get => _selectedT1RankRow; set => SetProperty(ref _selectedT1RankRow, value); }
    public bool IsRankingT1 { get; private set; }
    public string T1RankStatus { get; private set; } = "先完成盘中特征分析，再运行 T1-Rank / PriceOnlyV1 排序；仅诊断研究用途，未对照次日结果评估，不构成交易建议。";
    private CancellationTokenSource? _t1Cts;
    private long _t1Generation;
    private T1RankingResult? _lastT1Result;
    internal Func<IntradaySnapshot, string, IReadOnlyList<IntradayFeatureResult>, int, CancellationToken, Task<T1RankingResult>>? T1RankingOverride { get; set; }
    internal string? T1ExportDirectory { get; set; }
    private bool CanRunT1Ranking() => !_closed && !IsScanning && !IsCapturing && !IsAnalyzingFeatures && !IsRankingT1 && CanOpenSnapshot() && LastFeatureResults != null;
    private bool CanExportT1Ranking() => !_closed && !IsScanning && !IsCapturing && !IsAnalyzingFeatures && !IsRankingT1
        && _lastT1Result != null && ReferenceEquals(LastFeatureResults, _lastRankedFeatures);
    private void T1CommandsChanged()
    {
        RunT1RankingCommand.NotifyCanExecuteChanged(); ExportT1RankingCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsRankingT1));
        RetainT1ResearchSampleCommand.NotifyCanExecuteChanged();
    }
    private void InvalidateT1Ranking(Guid runId)
    {
        _t1Generation++;
        _researchSelectedRunId = runId;
        InvalidateResearchRetention(); _featureCompletedAtUtc = null; _rankingCompletedAtUtc = null;
        _t1Cts?.Cancel();
        _lastT1Result = null;
        _lastRankedFeatures = null;
        T1RankRows.Clear(); SelectedT1RankRow = null;
        T1RankStatus = $"当前扫描 {runId}: 新扫描已使旧排序失效；请重新完成特征分析后再运行 PriceOnlyV1 排序。";
        OnPropertyChanged(nameof(T1RankStatus));
        T1CommandsChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRunT1Ranking))]
    private async Task RunT1Ranking()
    {
        if (!CanRunT1Ranking()) return;
        string path = LastSnapshotPath!;
        int topN = TopN;
        var features = LastFeatureResults!;
        using var cts = new CancellationTokenSource();
        _t1Cts = cts;
        long generation = ++_t1Generation;
        bool OwnsRanking() => !_closed && !cts.IsCancellationRequested && generation == _t1Generation && path == LastSnapshotPath;
        IsRankingT1 = true; T1CommandsChanged();
        InvalidateResearchRetention(); _rankingCompletedAtUtc = null;
        T1RankRows.Clear(); SelectedT1RankRow = null; _lastT1Result = null;
        T1RankStatus = $"正在对已分析快照执行 PriceOnlyV1 排序（本地确定性计算，不取数）；{path}";
        OnPropertyChanged(nameof(T1RankStatus));
        try
        {
            // Load on the dispatcher like the feature workflow, so the override seam (and its captured
            // token) is invoked synchronously before the first await; only the pure rank goes to the pool.
            var snapshot = new IntradaySnapshotStore(Path.GetDirectoryName(path)!).Load(path);
            string fingerprint = IntradaySnapshotStore.Fingerprint(snapshot);
            var result = T1RankingOverride != null
                ? await T1RankingOverride(snapshot, fingerprint, features, topN, cts.Token)
                : await Task.Run(() => T1PriceOnlyRanking.Rank(snapshot, features, topN, cts.Token), cts.Token);
            if (!OwnsRanking()) return;
            _lastT1Result = result;
            _lastRankedFeatures = features;
            _rankingCompletedAtUtc = SnapshotClock.GetUtcNow();
            foreach (var candidate in result.Shortlist) T1RankRows.Add(new T1RankDisplayRow(candidate));
            foreach (var exclusion in result.Excluded) T1RankRows.Add(T1RankDisplayRow.From(exclusion));
            SelectedT1RankRow = T1RankRows.FirstOrDefault();
            T1RankStatus = $"PriceOnlyV1 研究排序（{result.EvidenceClass}，非认证）；运行 {result.Request.RunId}；快照 {result.Request.SnapshotFingerprint}；"
                + $"截止 {result.Request.Cutoff:O}；覆盖：快照候选 {result.Coverage.CapturedCandidates} / 可评分 {result.Coverage.EligibleCandidates} / 排除 {result.Coverage.ExcludedCandidates}；"
                + $"短名单请求 Top{result.Coverage.RequestedShortlistSize}，实际 {result.Coverage.ActualShortlistSize}；"
                + $"未纳入：{string.Join("；", result.NotIncluded.Select(n => $"{n.Dimension} — {n.Reason}"))}。"
                + "未对照次日结果评估；非交易信号；非全市场最佳短名单。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { if (OwnsRanking()) T1RankStatus = "T1 排序已取消；策略结果保留。"; }
        catch (Exception ex)
        { if (OwnsRanking()) T1RankStatus = "T1 排序失败（fail-closed）；策略结果保留：" + ex.GetType().Name; }
        finally
        {
            if (ReferenceEquals(_t1Cts, cts)) _t1Cts = null;
            IsRankingT1 = false;
            if (!_closed) { T1CommandsChanged(); OnPropertyChanged(nameof(T1RankStatus)); }
        }
    }
    [RelayCommand(CanExecute = nameof(CanExportT1Ranking))]
    private async Task ExportT1Ranking()
    {
        if (!CanExportT1Ranking()) return;
        var previousExport = _lastT1Export; var previousExportTime = _exportCompletedAtUtc;
        InvalidateResearchRetention();
        var result = _lastT1Result!;
        using var cts = new CancellationTokenSource();
        _t1Cts = cts;
        long generation = ++_t1Generation;
        bool OwnsExport() => !_closed && !cts.IsCancellationRequested && generation == _t1Generation;
        IsRankingT1 = true; T1CommandsChanged();
        T1RankStatus = "正在导出可复现研究工件（CSV + JSON sidecar）…"; OnPropertyChanged(nameof(T1RankStatus));
        try
        {
            var paths = await Task.Run(() => T1RankingArtifact.Write(result, T1ExportDirectory, cts.Token), cts.Token);
            if (!OwnsExport()) return;
            _lastT1Export = paths;
            _exportCompletedAtUtc = previousExport?.ContentFingerprint == paths.ContentFingerprint && previousExportTime is not null ? previousExportTime : SnapshotClock.GetUtcNow();
            T1RankStatus = $"已导出（{(paths.ReusedExisting ? "复用现有有效包" : "新发布不可变包")}）：{paths.PackageDirectory}；"
                + $"内容指纹 {paths.ContentFingerprint}；CSV 与 JSON sidecar 原子成对发布，可用独立校验器验证。"
                + "同一不可变快照与固定配置逐字节复现；未包含易变导出时间戳。";
            AppendLog($"T1 Ranking artifact: {paths.PackageDirectory}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { if (OwnsExport()) T1RankStatus = "T1 导出已取消；未发布不完整工件。"; }
        catch (Exception ex)
        { if (OwnsExport()) T1RankStatus = "T1 导出失败：" + ex.GetType().Name; }
        finally
        {
            if (ReferenceEquals(_t1Cts, cts)) _t1Cts = null;
            IsRankingT1 = false;
            if (!_closed) { T1CommandsChanged(); OnPropertyChanged(nameof(T1RankStatus)); }
        }
    }
}

/// <summary>Flat binding row: ranked shortlist entries first, then exclusions with reasons (single diagnostic view).</summary>
public sealed class T1RankDisplayRow
{
    public int? T1Rank { get; init; }
    public string Status { get; init; } = "";
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string OriginalStrategy { get; init; } = "";
    public int OriginalRank { get; init; }
    public string Score { get; init; } = "";
    public string Return5m { get; init; } = "";
    public string Return15m { get; init; } = "";
    public string Persistence { get; init; } = "";
    public string Pullback { get; init; } = "";
    public string ChaseModeration { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string ExclusionReason { get; init; } = "";
    public string Details { get; init; } = "";

    private static string D(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    public T1RankDisplayRow(T1RankingCandidate c)
    {
        var e = c.Explanation;
        T1Rank = c.T1Rank; Status = "入选"; Code = c.Symbol; Name = c.Name;
        OriginalStrategy = $"{e.Strategy}/{e.StrategyVersion}"; OriginalRank = e.OriginalRank;
        Score = D(e.FinalScore); Return5m = D(e.Return5mPct); Return15m = D(e.Return15mPct);
        Persistence = D(e.Persistence); Pullback = D(e.PullbackPct); ChaseModeration = D(e.Components.ChaseModeration);
        Evidence = e.EvidenceStatus; ExclusionReason = "";
        Details = $"T1 研究排名 #{c.T1Rank}（PriceOnlyV1，Diagnostic 诊断证据；未对照次日结果评估）\n"
            + $"代码 {c.Symbol}；数据时间 {e.DataTime:O}；原策略 {e.Strategy}/{e.StrategyVersion} 原排名 #{e.OriginalRank} 原评分 {e.OriginalScore}（保持不变）\n"
            + $"原始精度评分 {e.FinalScore}（显示四舍五入 {D(e.FinalScore)}，排序始终使用原始值）\n"
            + $"5m收益 {D(e.Return5mPct)}%；15m收益 {D(e.Return15mPct)}%；持续性 {D(e.Persistence)}；距观测高点 {D(e.PullbackPct)}%\n"
            + $"组件（权重×分值=贡献）：M5 20%×{D(e.Components.M5)}={D(e.Contributions.M5)}；M15 20%×{D(e.Components.M15)}={D(e.Contributions.M15)}；"
            + $"持续性 25%×{D(e.Components.Persistence)}={D(e.Contributions.Persistence)}；高点接近度 20%×{D(e.Components.HighProximity)}={D(e.Contributions.HighProximity)}；"
            + $"追高风险缓和 15%×{D(e.Components.ChaseModeration)}={D(e.Contributions.ChaseModeration)}\n"
            + $"证据状态 {e.EvidenceStatus}；快照指纹 {e.SnapshotFingerprint}\n"
            + "数据质量警告：\n" + string.Join("\n", e.Warnings);
    }
    public static T1RankDisplayRow From(T1RankingExclusion x) => new()
    {
        Status = "排除", Code = x.Symbol, Name = x.Name, OriginalStrategy = $"{x.Strategy}/{x.StrategyVersion}",
        OriginalRank = x.OriginalRank, Score = "", Return5m = "", Return15m = "", Persistence = "", Pullback = "",
        ChaseModeration = "", Evidence = "", ExclusionReason = $"{x.Code}: {x.Reason}",
        Details = $"未评分（fail-closed）：{x.Code}: {x.Reason}；原策略 {x.Strategy}/{x.StrategyVersion} 原排名 #{x.OriginalRank} 保持不变；数据时间 {x.DataTime:O}"
    };
    private T1RankDisplayRow() { }
}
