namespace AIHelper.Models;

/// <summary>
/// Parameters consumed by the historical Sparrow Classic strategy.
/// Percentages such as MinRise/MaxRise use percentage points; adhesion uses a 0..1 ratio;
/// MinAmount uses yuan.
/// </summary>
public sealed class SparrowClassicScanParameters
{
    public bool MacroDef { get; set; } = true;
    public double MinRise { get; set; }
    public double MaxRise { get; set; } = 9.9;
    public double VolRatio { get; set; } = 1.0;
    public double MinAmount { get; set; } = 50_000_000.0;
    public bool CheckMA60 { get; set; } = true;
    public double MinAdhesion { get; set; }
    public double MaxAdhesion { get; set; } = 0.15;
    public int MaxConcurrency { get; set; } = 8;
    public bool UseCache { get; set; } = true;
}

public sealed class SparrowClassicScanReport
{
    public string? LogMessage { get; set; }
    public bool IsHighlight { get; set; }
    public double? ProgressValue { get; set; }
    public double? ProgressMax { get; set; }
    public int? P2Survivors { get; set; }
    public int? P3Winners { get; set; }
}

public sealed class SparrowClassicCandidate
{
    public const string StrategyName = "SparrowClassic";

    public required string Code { get; init; }
    public required string Name { get; init; }
    public string Strategy => StrategyName;
    public required string Reason { get; init; }
}

public readonly record struct SparrowClassicTechnicalResult(
    bool Passed,
    double MA5,
    double MA10,
    double MA20,
    double MA60,
    double Adhesion);
