namespace AIHelper.Models;

/// <summary>Membership of a stock across the two compared strategy sides.</summary>
public enum SparrowPairComparisonCategory { Both, LeftOnly, RightOnly }

/// <summary>Identity of one side of a pair comparison; the version string is the stable label used in evidence.</summary>
public sealed record SparrowPairComparisonSide(string Mode, string Version)
{
    public override string ToString() => Version;
}

/// <summary>Neutral diff row for one candidate; no winner or preference semantics exist here.</summary>
public sealed class SparrowPairComparisonRow
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int? LeftRank { get; init; }
    public double? LeftScore { get; init; }
    public string? LeftReason { get; init; }
    public int? RightRank { get; init; }
    public double? RightScore { get; init; }
    public string? RightReason { get; init; }
    public SparrowPairComparisonCategory Category { get; init; }
}

public sealed class SparrowPairComparisonMetrics
{
    public SparrowPairComparisonMetrics(int universeCount, int leftCount, int rightCount, int bothCount, int leftOnlyCount, int rightOnlyCount)
    {
        UniverseCount = universeCount;
        LeftCount = leftCount;
        RightCount = rightCount;
        BothCount = bothCount;
        LeftOnlyCount = leftOnlyCount;
        RightOnlyCount = rightOnlyCount;
    }

    public int UniverseCount { get; }
    public int LeftCount { get; }
    public int RightCount { get; }
    public int BothCount { get; }
    public int LeftOnlyCount { get; }
    public int RightOnlyCount { get; }
}

/// <summary>Immutable pair-comparison evidence; rows and rankings are frozen at capture time.</summary>
public sealed class SparrowPairComparisonResult
{
    public SparrowPairComparisonResult(
        SparrowPairComparisonSide left,
        SparrowPairComparisonSide right,
        DateTimeOffset capturedAt,
        IReadOnlyList<SparrowPairComparisonRow> rows,
        IReadOnlyList<SparrowRankedCandidate> leftRanking,
        IReadOnlyList<SparrowRankedCandidate> rightRanking,
        SparrowPairComparisonMetrics metrics)
    {
        Left = left ?? throw new ArgumentNullException(nameof(left));
        Right = right ?? throw new ArgumentNullException(nameof(right));
        CapturedAt = capturedAt;
        Rows = rows;
        LeftRanking = leftRanking;
        RightRanking = rightRanking;
        Metrics = metrics;
    }

    public SparrowPairComparisonSide Left { get; }
    public SparrowPairComparisonSide Right { get; }
    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyList<SparrowPairComparisonRow> Rows { get; }
    public IReadOnlyList<SparrowRankedCandidate> LeftRanking { get; }
    public IReadOnlyList<SparrowRankedCandidate> RightRanking { get; }
    public SparrowPairComparisonMetrics Metrics { get; }
    public string? CsvPath { get; set; }
}

/// <summary>Selectable preset of two comparable strategy modes (the V2-family gates are parameter-compatible).</summary>
public sealed record SparrowPairComparisonPreset(SparrowStrategyMode Left, SparrowStrategyMode Right)
{
    public string DisplayName => $"{Left.ToDisplayName()} vs {Right.ToDisplayName()}";
    public override string ToString() => DisplayName;
}
