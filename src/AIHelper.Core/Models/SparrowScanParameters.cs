namespace AIHelper.Models;

public class SparrowScanParameters
{
    public bool MacroDef { get; set; }
    public double MinRise { get; set; }
    public double MaxRise { get; set; }
    public double VolRatio { get; set; }
    public double MinAmount { get; set; }
    public bool CheckMA60 { get; set; }
    public double MinAdhesion { get; set; }
    public double MaxAdhesion { get; set; }
    public double MinTurnover { get; set; }
    public double MaxTurnover { get; set; }
    public double MomentumThreshold { get; set; }
    public bool CheckAlpha { get; set; }
    /// <summary>
    /// When true, a missing or non-positive turnover rejects the candidate instead of fail-open.
    /// </summary>
    public bool StrictTurnover { get; set; }
    public int MaxConcurrency { get; set; } = 8;
    public bool UseCache { get; set; }
}

/// <summary>Optional V3 momentum-quality gates evaluated on the close series that already passed the V2 rules.</summary>
/// <param name="RsiMin">Inclusive lower RSI bound; null disables the gate.</param>
/// <param name="RsiMax">Inclusive upper RSI bound; null disables the gate.</param>
/// <param name="RequireMacdBullish">Requires DIF &gt;= DEA at the newest close.</param>
public sealed record SparrowMomentumGateConfig(
    double? RsiMin = 25,
    double? RsiMax = 75,
    bool RequireMacdBullish = true,
    int RsiPeriod = 14,
    int MacdFastPeriod = 12,
    int MacdSlowPeriod = 26,
    int MacdSignalPeriod = 9)
{
    public void Validate()
    {
        if (RsiMin.HasValue && (!double.IsFinite(RsiMin.Value) || RsiMin.Value < 0 || RsiMin.Value > 100)) throw new ArgumentOutOfRangeException(nameof(RsiMin));
        if (RsiMax.HasValue && (!double.IsFinite(RsiMax.Value) || RsiMax.Value < 0 || RsiMax.Value > 100)) throw new ArgumentOutOfRangeException(nameof(RsiMax));
        if (RsiMin.HasValue && RsiMax.HasValue && RsiMin.Value > RsiMax.Value) throw new ArgumentException("RsiMin must not exceed RsiMax.");
        if (RsiPeriod <= 0 || MacdFastPeriod <= 0 || MacdSlowPeriod <= MacdFastPeriod || MacdSignalPeriod <= 0)
            throw new InvalidOperationException("Momentum gate periods are invalid.");
    }
}
