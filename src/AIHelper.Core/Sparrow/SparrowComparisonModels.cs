namespace AIHelper.Models;

public enum SparrowComparisonCategory
{
    Both,
    ClassicOnly,
    V2Only,
    Neither
}

public enum SparrowRuleOutcome
{
    NotRun,
    Bypassed,
    Passed,
    RuleRejected,
    DataUnavailable
}

public static class SparrowComparisonReasonCodes
{
    public const string NotRun = "NOT_RUN";
    public const string Pass = "PASS";
    public const string Bypassed = "BYPASSED";
    public const string P1MarketDefensive = "P1_MARKET_DEFENSIVE";
    public const string P1MarketDataUnknown = "P1_MARKET_DATA_UNKNOWN";
    public const string P2QuoteDataMissing = "P2_QUOTE_DATA_MISSING";
    public const string P2RiseBelowMin = "P2_RISE_BELOW_MIN";
    public const string P2RiseAboveMax = "P2_RISE_ABOVE_MAX";
    public const string P2Amount = "P2_AMOUNT";
    public const string P2OuterInnerMissing = "P2_OUTER_INNER_MISSING";
    public const string P2VolRatio = "P2_VOL_RATIO";
    public const string P2Turnover = "P2_TURNOVER";
    public const string P3KlineMissing = "P3_KLINE_MISSING";
    public const string P3KlineInsufficient = "P3_KLINE_INSUFFICIENT";
    public const string P3Alpha = "P3_ALPHA";
    public const string P3MaOrder = "P3_MA_ORDER";
    public const string P3Ma60 = "P3_MA60";
    public const string P3Momentum = "P3_MOMENTUM";
    public const string P3AdhesionLow = "P3_ADHESION_LOW";
    public const string P3AdhesionHigh = "P3_ADHESION_HIGH";
}

public sealed class SparrowRuleComparison
{
    public bool Passed { get; init; }
    public string Stage { get; init; } = "";
    public string ReasonCode { get; init; } = SparrowComparisonReasonCodes.NotRun;
    public string Reason { get; init; } = "";
    public SparrowRuleOutcome Outcome { get; init; } = SparrowRuleOutcome.NotRun;

    public static SparrowRuleComparison NotRun(string stage, string reason = "Not run") => new()
    {
        Stage = stage,
        ReasonCode = SparrowComparisonReasonCodes.NotRun,
        Reason = reason,
        Outcome = SparrowRuleOutcome.NotRun
    };

    public static SparrowRuleComparison Pass(string stage, string reason = "Passed") => new()
    {
        Passed = true,
        Stage = stage,
        ReasonCode = SparrowComparisonReasonCodes.Pass,
        Reason = reason,
        Outcome = SparrowRuleOutcome.Passed
    };

    public static SparrowRuleComparison Bypass(string stage, string reason) => new()
    {
        Passed = true,
        Stage = stage,
        ReasonCode = SparrowComparisonReasonCodes.Bypassed,
        Reason = reason,
        Outcome = SparrowRuleOutcome.Bypassed
    };

    public static SparrowRuleComparison Reject(string stage, string code, string reason) => new()
    {
        Stage = stage,
        ReasonCode = code,
        Reason = reason,
        Outcome = SparrowRuleOutcome.RuleRejected
    };

    public static SparrowRuleComparison Unavailable(string stage, string code, string reason, bool failOpen = false) => new()
    {
        Passed = failOpen,
        Stage = stage,
        ReasonCode = code,
        Reason = reason,
        Outcome = SparrowRuleOutcome.DataUnavailable
    };
}

public sealed class SparrowComparisonSide
{
    public SparrowRuleComparison P1 { get; set; } = SparrowRuleComparison.NotRun("P1");
    public SparrowRuleComparison P2 { get; set; } = SparrowRuleComparison.NotRun("P2");
    public SparrowRuleComparison P3 { get; set; } = SparrowRuleComparison.NotRun("P3");
    public bool FinalPassed { get; set; }
    public string RejectStage { get; set; } = "";
    public string RejectReasonCode { get; set; } = "";
    public string RejectReason { get; set; } = "";
    public double? Rise { get; set; }
    public double? Adhesion { get; set; }
    public double? Momentum { get; set; }
    public double? AlphaMargin { get; set; }
    public int? Rank { get; set; }
    public double? Score { get; set; }

    public void RejectFrom(SparrowRuleComparison result)
    {
        RejectStage = result.Stage;
        RejectReasonCode = result.ReasonCode;
        RejectReason = result.Reason;
    }
}

public sealed class SparrowComparisonRow
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public SparrowComparisonSide Classic { get; } = new();
    public SparrowComparisonSide V2 { get; } = new();
    public SparrowComparisonCategory Category { get; set; } = SparrowComparisonCategory.Neither;
    public double? Amount { get; set; }
    public double? Turnover { get; set; }
    public double? OuterVolume { get; set; }
    public double? InnerVolume { get; set; }
}

public sealed class SparrowComparisonSession
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public DateTimeOffset CapturedAt { get; init; }
    public DateOnly TradeDate { get; init; }
    public bool UseCache { get; init; }
    public int UniverseCount { get; init; }
}

public sealed class SparrowComparisonMarketSnapshot
{
    public SparrowMarketRegime? Classic { get; init; }
    public double? V2ShanghaiDailyPercent { get; init; }
}

public sealed class SparrowComparisonMetrics
{
    public int UniverseCount { get; init; }
    public int ClassicP2Count { get; init; }
    public int V2P2Count { get; init; }
    public int ClassicFinalCount { get; init; }
    public int V2FinalCount { get; init; }
    public int IntersectionCount { get; init; }
    public int ClassicOnlyCount { get; init; }
    public int V2OnlyCount { get; init; }
    public int UnionCount { get; init; }
    public double OverlapRate { get; init; }
    public double ClassicRetention { get; init; }
    public double V2Retention { get; init; }
    public double V2ClassicCandidateRatio { get; init; }
    public IReadOnlyDictionary<string, int> ClassicOnlyV2RejectReasons { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> V2OnlyClassicRejectReasons { get; init; } = new Dictionary<string, int>();
}

public sealed class SparrowComparisonResult
{
    public required SparrowComparisonSession Session { get; init; }
    public required SparrowComparisonMarketSnapshot MarketSnapshot { get; init; }
    public required IReadOnlyList<SparrowComparisonRow> Rows { get; init; }
    public required SparrowComparisonMetrics Metrics { get; init; }
    public IReadOnlyList<SparrowRankedCandidate> ClassicRanking { get; init; } = Array.Empty<SparrowRankedCandidate>();
    public IReadOnlyList<SparrowRankedCandidate> V2Ranking { get; init; } = Array.Empty<SparrowRankedCandidate>();
    public string? CsvPath { get; set; }
}

public sealed class SparrowComparisonProgress
{
    public string? LogMessage { get; init; }
    public bool IsHighlight { get; init; }
    public int? ProgressMax { get; init; }
    public int? ProgressValue { get; init; }
}
