using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.StockData;
using AIHelper.Services.StockData;
using CommunityToolkit.Mvvm.Input;

namespace AIHelper.ViewModels;

public partial class SparrowViewModel
{
    private string _researchArchiveRoot = "";
    public string ResearchArchiveRoot { get => _researchArchiveRoot; set { if (SetProperty(ref _researchArchiveRoot, value)) RetainT1ResearchSampleCommand.NotifyCanExecuteChanged(); } }
    public string ResearchRetentionStatus { get; private set; } = "完成排序并导出后，填写独立研究目录，再点击留存。默认不归档；不会重新扫描或取数。";
    public bool IsRetainingResearch { get; private set; }
    private CancellationTokenSource? _retentionCts;
    private long _retentionGeneration;
    private Guid? _researchSelectedRunId;
    private T1RankingArtifactPaths? _lastT1Export;
    private IReadOnlyList<IntradayFeatureResult>? _lastRankedFeatures;
    private DateTimeOffset? _featureCompletedAtUtc, _rankingCompletedAtUtc, _exportCompletedAtUtc;
    internal T1EvidenceOrigin ResearchOrigin { get; set; } = T1EvidenceOrigin.UNVERIFIED;
    internal Func<CancellationToken, Task<T1PreparedResearchSample>>? ResearchPrepareOverride { get; set; }
    private bool CanRetainT1ResearchSample() => !_closed && !IsScanning && !IsCapturing && !IsAnalyzingFeatures && !IsRankingT1 && !IsRetainingResearch
        && !string.IsNullOrWhiteSpace(ResearchArchiveRoot) && _lastT1Export is not null && _lastT1Result is not null
        && LastFeatureResults is not null && ReferenceEquals(LastFeatureResults, _lastRankedFeatures)
        && _researchSelectedRunId == _lastT1Result.Request.RunId && LastSnapshotRunId == _researchSelectedRunId;
    private void InvalidateResearchRetention()
    {
        _retentionGeneration++; _retentionCts?.Cancel(); _lastT1Export = null; _exportCompletedAtUtc = null;
        RetainT1ResearchSampleCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanRetainT1ResearchSample))]
    private async Task RetainT1ResearchSample()
    {
        if (!CanRetainT1ResearchSample()) return;
        string path = LastSnapshotPath!, root = ResearchArchiveRoot; var package = _lastT1Export!; var features = LastFeatureResults!;
        Guid runId = _lastT1Result!.Request.RunId;
        var timings = new T1ResearchTimings(_featureCompletedAtUtc, _rankingCompletedAtUtc, _exportCompletedAtUtc);
        using var cts = new CancellationTokenSource(); _retentionCts = cts; long generation = ++_retentionGeneration;
        bool OwnsRetention() => !_closed && !cts.IsCancellationRequested && generation == _retentionGeneration && _researchSelectedRunId == runId
            && _lastT1Export == package && LastSnapshotPath == path && ResearchArchiveRoot == root;
        IsRetainingResearch = true; RetainT1ResearchSampleCommand.NotifyCanExecuteChanged(); CancelScanCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRetainingResearch)); ResearchRetentionStatus = "正在校验并留存原始研究文件；不重新取数。"; OnPropertyChanged(nameof(ResearchRetentionStatus));
        try
        {
            using var prepared = ResearchPrepareOverride is not null ? await ResearchPrepareOverride(cts.Token)
                : await Task.Run(() => T1ResearchSamples.Prepare(path, package.PackageDirectory, features, root, ResearchOrigin, timings, cts.Token), cts.Token);
            if (!OwnsRetention()) return;
            // Dispatcher continuation: no scan/close event can interleave between ownership check and atomic publish.
            var retained = prepared.Publish(cts.Token);
            ResearchRetentionStatus = $"已留存{(retained.ReusedExisting ? "（复用）" : "")}：{retained.Directory}；运行 {runId}；候选 {retained.Manifest.Content.Candidates.Count}；"
                + $"{(retained.Manifest.Content.OutsideStudyWindow ? "采集在研究窗口之外" : "在14:30–14:55研究窗口内")}；Diagnostic；PIT认证、分钟价格基准和成交能力仍未验证。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { if (!_closed && generation == _retentionGeneration) ResearchRetentionStatus = "研究留存已取消；未发布不完整样本。"; }
        catch (Exception ex)
        { if (OwnsRetention()) ResearchRetentionStatus = "留存失败，原始文件保留：" + ex.GetType().Name + "；请使用独立目录或 Git 忽略的研究目录。"; }
        finally
        {
            if (ReferenceEquals(_retentionCts, cts)) _retentionCts = null; IsRetainingResearch = false;
            if (!_closed) { RetainT1ResearchSampleCommand.NotifyCanExecuteChanged(); CancelScanCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(IsRetainingResearch)); OnPropertyChanged(nameof(ResearchRetentionStatus)); }
        }
    }
}
