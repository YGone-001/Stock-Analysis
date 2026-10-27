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
    /// <summary>
    /// When true, the scanner additionally fetches the industry-board heat map and injects per-symbol
    /// heat percentiles into the ranking features (Rank-V2 profile). Fail-open: fetch failure leaves
    /// every feature null and the scan proceeds without the factor. Ignored by the frozen Classic rules.
    /// </summary>
    public bool SectorHeatEnabled { get; set; }
    public int MaxConcurrency { get; set; } = 8;
    public bool UseCache { get; set; }
}

/// <summary>Optional V5 weekly-trend gate: the calendar-week resampled MA must be rising so entries follow the multi-month trend.</summary>
public sealed record SparrowWeeklyGateConfig(
    bool RequireWeeklyUptrend = true,
    int WeeklyMaPeriod = 20)
{
    public void Validate()
    {
        if (WeeklyMaPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(WeeklyMaPeriod));
    }
}

/// <summary>Optional V4 volume-pattern gates evaluated on the newest-first volume series after the V3 rules passed.</summary>
/// <param name="VolBreakoutMultiple">Today's volume must reach this multiple of the recent-window mean; null disables the breakout gate.</param>
/// <param name="VolContractionMaxRatio">The pre-breakout window mean must stay at or below this ratio of the baseline-window mean; null disables the contraction gate.</param>
public sealed record SparrowVolumeGateConfig(
    double? VolBreakoutMultiple = 1.5,
    double? VolContractionMaxRatio = 0.8,
    int VolBreakoutLookbackDays = 5,
    int VolContractionLookbackDays = 5,
    int VolContractionBaselineDays = 10)
{
    public void Validate()
    {
        if (VolBreakoutMultiple.HasValue && (!double.IsFinite(VolBreakoutMultiple.Value) || VolBreakoutMultiple.Value <= 0)) throw new ArgumentOutOfRangeException(nameof(VolBreakoutMultiple));
        if (VolContractionMaxRatio.HasValue && (!double.IsFinite(VolContractionMaxRatio.Value) || VolContractionMaxRatio.Value <= 0)) throw new ArgumentOutOfRangeException(nameof(VolContractionMaxRatio));
        if (VolBreakoutLookbackDays <= 0 || VolContractionLookbackDays <= 0 || VolContractionBaselineDays <= 0)
            throw new InvalidOperationException("Volume gate windows are invalid.");
    }
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
    int MacdSignalPeriod = 9,
    double MacdRelativeTolerance = 1e-9)
{
    public void Validate()
    {
        if (RsiMin.HasValue && (!double.IsFinite(RsiMin.Value) || RsiMin.Value < 0 || RsiMin.Value > 100)) throw new ArgumentOutOfRangeException(nameof(RsiMin));
        if (RsiMax.HasValue && (!double.IsFinite(RsiMax.Value) || RsiMax.Value < 0 || RsiMax.Value > 100)) throw new ArgumentOutOfRangeException(nameof(RsiMax));
        if (RsiMin.HasValue && RsiMax.HasValue && RsiMin.Value > RsiMax.Value) throw new ArgumentException("RsiMin must not exceed RsiMax.");
        if (RsiPeriod <= 0 || MacdFastPeriod <= 0 || MacdSlowPeriod <= MacdFastPeriod || MacdSignalPeriod <= 0)
            throw new InvalidOperationException("Momentum gate periods are invalid.");
        if (!double.IsFinite(MacdRelativeTolerance) || MacdRelativeTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(MacdRelativeTolerance));
    }
}
