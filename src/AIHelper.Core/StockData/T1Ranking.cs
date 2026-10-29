using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Core.StockData;

/// <summary>
/// T1-Rank / PriceOnlyV1: an independent, explainable, evidence-aware re-ranking of the retained
/// intraday diagnostic snapshot. Pure deterministic research baseline — never a trading signal,
/// never a prediction, and never a modification of the original Classic–V6 strategy rankings.
/// </summary>
public enum T1RankingExclusionCode
{
    None, SnapshotIdentityMissing, SymbolIdentityMismatch, TradingDateMismatch, RunIdMismatch, FingerprintMismatch,
    CutoffMismatch, FeatureVersionUnsupported, FeatureResultMissing, EndpointMissing, EndpointOutsideWindow,
    EndpointAfterCutoff, Return5mUnavailable, Return15mUnavailable, PersistenceUnavailable,
    PersistenceDenominatorNonPositive, PullbackUnavailable, MetricOutOfRange, ObservationAcquiredAfterCutoff,
    ObservationProvenanceMissing, MixedSourceBasis
}

public sealed record T1RankingRequest(string RankingModel, string RankingVersion, string SnapshotFingerprint, Guid RunId,
    DateOnly TradingDate, DateTimeOffset Cutoff, string FeatureComputationVersion, int ShortlistTopN,
    string ParameterFingerprint, int CapturedCandidateCount);

/// <summary>Frozen PriceOnlyV1 component scores, each normalized to [0,100]. Percent units for returns/pullback.</summary>
public sealed record T1ComponentScores(decimal M5, decimal M15, decimal Persistence, decimal HighProximity, decimal ChaseModeration);

/// <summary>Per-candidate explanation. Raw decimal score precision is retained; display rounding never reorders.</summary>
public sealed record T1RankingExplanation(DateTimeOffset? DataTime, string Strategy, string StrategyVersion,
    int OriginalRank, double OriginalScore, decimal Return5mPct, decimal Return15mPct, decimal Persistence,
    decimal PullbackPct, T1ComponentScores Components, T1ComponentScores Contributions, decimal FinalScore,
    string EvidenceStatus, string SnapshotFingerprint, IReadOnlyList<string> Warnings);

public sealed record T1RankingCandidate(string Symbol, string Name, int T1Rank, T1RankingExplanation Explanation);
public sealed record T1RankingExclusion(string Symbol, string Name, T1RankingExclusionCode Code, string Reason,
    string Strategy, string StrategyVersion, int OriginalRank, DateTimeOffset? DataTime);

public sealed record T1RankingCoverage(int CapturedCandidates, int EligibleCandidates, int ExcludedCandidates,
    int RequestedShortlistSize, int ActualShortlistSize);

public sealed record T1RankingResult(T1RankingRequest Request, string RankingProfile, string EvidenceClass,
    IReadOnlyList<(string Dimension, string Reason)> NotIncluded, T1RankingCoverage Coverage,
    IReadOnlyList<T1RankingCandidate> Shortlist, IReadOnlyList<T1RankingExclusion> Excluded)
{
    public const string DiagnosticClass = "Diagnostic";
}

/// <summary>Standalone price-only ranking profile. Component weights sum to exactly 100 (percent form).</summary>
public static class T1PriceOnlyRanking
{
    public const string Model = "T1-Rank";
    public const string Version = "PriceOnlyV1";
    public const string DisplayRounding = "0.0001"; // display only; ordering always uses raw decimals

    /// <summary>Frozen initial heuristic weights (percent). Research baseline, not a trained or optimized model.</summary>
    public static readonly (string Component, decimal Weight)[] WeightTable =
        { ("M5", 0.20m), ("M15", 0.20m), ("Persistence", 0.25m), ("HighProximity", 0.20m), ("ChaseModeration", 0.15m) };

    public static readonly IReadOnlyList<string> NotIncludedDimensions = new[] { "RelativeStrength", "Volume", "Vwap", "HistoricalRvol" };

    public static string ParameterFingerprint { get; } = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Model, Version,
        Mapping = "M5=clamp(100*(R5+1)/3);M15=clamp(100*(R15+2)/5);Persistence=clamp(100*P);HighProximity=clamp(100*(PB+3)/3);"
            + "ChaseModeration=clamp(100*(3-max(0,R5))/2);Final=0.20M5+0.20M15+0.25Persistence+0.20HighProximity+0.15ChaseModeration;"
            + "returns/pullback in percent units, persistence in [0,1]",
        Weights = WeightTable
    })));

    public static decimal Clamp(decimal value) => Math.Clamp(value, 0m, 100m);
    public static decimal M5(decimal return5mPct) => Clamp(100m * (return5mPct + 1m) / 3m);
    public static decimal M15(decimal return15mPct) => Clamp(100m * (return15mPct + 2m) / 5m);
    public static decimal Persistence(decimal persistenceFraction) => Clamp(100m * persistenceFraction);
    public static decimal HighProximity(decimal pullbackPct) => Clamp(100m * (pullbackPct + 3m) / 3m);
    public static decimal ChaseModeration(decimal return5mPct) => Clamp(100m * (3m - Math.Max(0m, return5mPct)) / 2m);

    /// <summary>Valid arithmetic bounds; values outside indicate a data error and fail closed.</summary>
    public static bool InBounds(decimal return5mPct, decimal return15mPct, decimal persistence, decimal pullbackPct) =>
        -20m <= return5mPct && return5mPct <= 20m && -20m <= return15mPct && return15mPct <= 20m
        && 0m <= persistence && persistence <= 1m && -100m <= pullbackPct && pullbackPct <= 0m;

    private static T1RankingExclusion Exclude(T1RankingExclusionCode code, IntradayFeatureResult f, SnapshotSymbol source, string detail) =>
        new(f.Symbol, source?.Name ?? f.Name, code, detail, f.Scan.Strategy, f.Scan.StrategyVersion, source?.Rank ?? -1, f.EffectiveObservationTime);

    /// <summary>
    /// Ranks the captured snapshot candidates with real T1-2 feature results. Independently re-verifies the
    /// snapshot fingerprint (fail closed). Each candidate receives a score or an explicit machine-readable
    /// exclusion; zero eligible candidates is a valid outcome. Original strategy Rank/TotalScore are never touched.
    /// </summary>
    public static T1RankingResult Rank(IntradaySnapshot snapshot, IReadOnlyList<IntradayFeatureResult> features,
        int shortlistTopN, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(features);
        if (snapshot.SchemaVersion != 1 || shortlistTopN is < SparrowRankingSettings.MinimumTopN or > SparrowRankingSettings.MaximumTopN)
            throw new ArgumentException("Invalid T1 ranking input");
        // Same canonical serialization as the store and feature analysis; Core cannot reference Services.
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
        token.ThrowIfCancellationRequested();
        var request = new T1RankingRequest(Model, Version, fingerprint, snapshot.Scan.RunId, snapshot.TradingDate,
            snapshot.RequestedAsOf, IntradayFeatureAnalysis.Version, shortlistTopN, ParameterFingerprint, snapshot.Symbols.Count);
        var ranked = new List<T1RankingCandidate>();
        var excluded = new List<T1RankingExclusion>();
        foreach (var source in snapshot.Symbols)
        {
            token.ThrowIfCancellationRequested();
            var feature = features.FirstOrDefault(f => f.Symbol == source.Symbol);
            if (feature is null)
            {
                excluded.Add(new(source.Symbol, source.Name, T1RankingExclusionCode.FeatureResultMissing,
                    "没有对应的特征分析结果", snapshot.Scan.Strategy, snapshot.Scan.StrategyVersion, source.Rank, null));
                continue;
            }
            var failure = Evaluate(feature, source, request, snapshot);
            if (failure != null) { excluded.Add(failure); continue; }
            var r5 = feature.Return5m.Value!.Value;
            var r15 = feature.Return15m.Value!.Value;
            var persistence = feature.PricePersistence.Value!.Value;
            var pullback = feature.PullbackFromObservedHigh.Value!.Value;
            var components = new T1ComponentScores(M5(r5), M15(r15), Persistence(persistence), HighProximity(pullback), ChaseModeration(r5));
            var contributions = new T1ComponentScores(0.20m * components.M5, 0.20m * components.M15, 0.25m * components.Persistence,
                0.20m * components.HighProximity, 0.15m * components.ChaseModeration);
            decimal score = 0.20m * components.M5 + 0.20m * components.M15 + 0.25m * components.Persistence
                + 0.20m * components.HighProximity + 0.15m * components.ChaseModeration;
            ranked.Add(new(source.Symbol, source.Name, 0, new(feature.EffectiveObservationTime, snapshot.Scan.Strategy,
                snapshot.Scan.StrategyVersion, source.Rank, source.Score, r5, r15, persistence, pullback, components,
                contributions, score, feature.EvidenceStatus, fingerprint, feature.Warnings)));
        }
        // Stable total order: score, persistence, 15m return, ordinal symbol code. No dictionary order, no unstable ties.
        var ordered = ranked.OrderByDescending(c => c.Explanation.FinalScore).ThenByDescending(c => c.Explanation.Persistence)
            .ThenByDescending(c => c.Explanation.Return15mPct).ThenBy(c => c.Symbol, StringComparer.Ordinal).ToList();
        var shortlist = ordered.Take(shortlistTopN).Select((c, index) => c with { T1Rank = index + 1 }).ToList();
        var coverage = new T1RankingCoverage(snapshot.Symbols.Count, ordered.Count, excluded.Count, shortlistTopN, shortlist.Count);
        return new(request, Version, T1RankingResult.DiagnosticClass, NotIncluded(features), coverage, shortlist, excluded);
    }

    private static T1RankingExclusion? Evaluate(IntradayFeatureResult f, SnapshotSymbol source, T1RankingRequest request, IntradaySnapshot snapshot)
    {
        if (source.Name != f.Name) return Exclude(T1RankingExclusionCode.SymbolIdentityMismatch, f, source, "特征结果名称与快照记录不一致");
        if (f.TradingDate != snapshot.TradingDate) return Exclude(T1RankingExclusionCode.TradingDateMismatch, f, source, "交易日期与快照不一致");
        if (f.Scan.RunId != snapshot.Scan.RunId) return Exclude(T1RankingExclusionCode.RunIdMismatch, f, source, "扫描 RunId 与快照不一致");
        if (f.SnapshotFingerprint != request.SnapshotFingerprint) return Exclude(T1RankingExclusionCode.FingerprintMismatch, f, source, "特征结果的快照指纹与当前快照不一致");
        if (f.RequestedAsOf != request.Cutoff) return Exclude(T1RankingExclusionCode.CutoffMismatch, f, source, "分析截止时间与快照不一致");
        if (f.ComputationVersion != request.FeatureComputationVersion) return Exclude(T1RankingExclusionCode.FeatureVersionUnsupported, f, source, $"不支持的特征计算版本 {f.ComputationVersion}");
        if (f.EffectiveObservationTime is not { } end) return Exclude(T1RankingExclusionCode.EndpointMissing, f, source, "有效端点缺失，无法定位当期价格");
        var local = end.ToOffset(ChinaExchangeTime.Offset).TimeOfDay;
        if (local < IntradayFeaturePolicy.AnalysisStart || local >= IntradayFeaturePolicy.ContinuousEnd)
            return Exclude(T1RankingExclusionCode.EndpointOutsideWindow, f, source, $"有效端点 {end.ToOffset(ChinaExchangeTime.Offset):HH:mm} 不在 [14:30, 14:57) 连续竞价窗口内");
        if (end > request.Cutoff) return Exclude(T1RankingExclusionCode.EndpointAfterCutoff, f, source, "有效端点晚于分析截止时间");
        if (f.Return5m.Value is not { } r5) return Exclude(T1RankingExclusionCode.Return5mUnavailable, f, source, "5m 收益不可用");
        if (f.Return15m.Value is not { } r15) return Exclude(T1RankingExclusionCode.Return15mUnavailable, f, source, "15m 收益不可用");
        if (f.PricePersistence.Value is not { } persistence) return Exclude(T1RankingExclusionCode.PersistenceUnavailable, f, source, "价格持续性不可用");
        if (f.PricePersistence.EligibleTransitions <= 0) return Exclude(T1RankingExclusionCode.PersistenceDenominatorNonPositive, f, source, "持续性有效转移分母非正");
        if (f.PullbackFromObservedHigh.Value is not { } pullback) return Exclude(T1RankingExclusionCode.PullbackUnavailable, f, source, "观测高点回撤不可用");
        if (!InBounds(r5, r15, persistence, pullback)) return Exclude(T1RankingExclusionCode.MetricOutOfRange, f, source, "指标超出有效算术边界");
        foreach (var metric in new[] { f.LatestPrice, f.Return5m, f.Return15m, f.PricePersistence, f.PullbackFromObservedHigh })
        {
            string? basis = null;
            foreach (var time in metric.ObservationTimes)
            {
                var row = f.SourceProvenance.FirstOrDefault(p => p.ExchangeEventTime == time);
                if (row is null) return Exclude(T1RankingExclusionCode.ObservationProvenanceMissing, f, source, $"观测 {time:O} 在原始证据中缺失");
                if (row.AvailableAtUtc > request.Cutoff || row.ObservedAtUtc > request.Cutoff)
                    return Exclude(T1RankingExclusionCode.ObservationAcquiredAfterCutoff, f, source, $"观测 {time:O} 的原始可用/接收时间晚于分析截止");
                basis ??= row.Provider;
                if (basis != row.Provider) return Exclude(T1RankingExclusionCode.MixedSourceBasis, f, source, "必需指标窗口混用不同来源价格基准");
            }
        }
        return null;
    }

    /// <summary>Missing-dimension governance: reasons name the actual unavailable production source evidence.</summary>
    private static IReadOnlyList<(string Dimension, string Reason)> NotIncluded(IReadOnlyList<IntradayFeatureResult> features)
    {
        bool HasR5 = features.Any(f => f.RelativeStrength5m.Value != null);
        bool HasR15 = features.Any(f => f.RelativeStrength15m.Value != null);
        bool HasVolume = features.Any(f => f.VolumeAcceleration.Value != null);
        bool HasVwap = features.Any(f => f.TradeVwap.Value != null);
        bool HasRvol = features.Any(f => f.HistoricalSameClockRvol.Value != null);
        return new (string Dimension, string Reason)[]
        {
            ("RelativeStrength", HasR5 || HasR15
                ? "PriceOnlyV1 冻结配置不纳入相对强度；本快照虽存在 RS 证据但不入模"
                : "本快照未保存对齐的基准（sh000001）分钟序列，RS 无可用证据且不入模"),
            ("Volume", HasVolume ? "PriceOnlyV1 冻结配置不纳入量维度；本快照量证据不入模" : "分钟成交量单位未经验证，量纲证据缺失；Volume 不入模"),
            ("Vwap", HasVwap ? "PriceOnlyV1 冻结配置不纳入 VWAP；本快照 VWAP 证据不入模" : "真实成交 VWAP 所需经验证的增量成交额/股数证据缺失；不入模"),
            ("HistoricalRvol", HasRvol ? "PriceOnlyV1 冻结配置不纳入历史 RVOL；本快照 RVOL 证据不入模" : "未保留同钟历史会话基线；HistoricalRVOL 不入模"),
        };
    }
}
