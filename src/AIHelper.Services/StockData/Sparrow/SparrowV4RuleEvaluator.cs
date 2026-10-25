using AIHelper.Models;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Pure V4 P3 evaluator: the frozen V3 rule set (V2 rules + momentum gates) plus close-companion volume-pattern
/// gates. v3 behavior is delegated verbatim; the volume gates only decide after V3 has passed. All gate math is
/// ratio-based, so the hands-vs-shares unit of the volume series never matters.
/// </summary>
public static class SparrowV4RuleEvaluator
{
    /// <summary>The V4 rule version implemented by this evaluator.</summary>
    public const string RuleVersion = SparrowStrategyVersions.V4;

    public static SparrowTechnicalEvaluation Evaluate(
        SparrowKlineSnapshot? snapshot,
        double? shIndexPctChg,
        SparrowScanParameters parameters,
        SparrowMomentumGateConfig? momentumGates = null,
        SparrowVolumeGateConfig? volumeGates = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        volumeGates?.Validate();

        SparrowTechnicalEvaluation v3 = SparrowV3RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters, momentumGates);
        if (!v3.Rule.Passed || volumeGates is null)
            return v3;

        bool wantsBreakout = volumeGates.VolBreakoutMultiple.HasValue;
        bool wantsContraction = volumeGates.VolContractionMaxRatio.HasValue;
        if (!wantsBreakout && !wantsContraction)
            return v3;

        IReadOnlyList<double> volumes = snapshot!.Volumes;
        if (!snapshot.HasVolumeData || double.IsNaN(volumes[0]))
            return Unavailable("Volume data is unavailable for the volume-pattern gates");

        if (wantsBreakout)
        {
            if (!SparrowTechnicalIndicators.TryWindowAverage(volumes, 1, volumeGates.VolBreakoutLookbackDays, out double recentAverage))
                return Unavailable($"Volume history is incomplete over the breakout lookback of {volumeGates.VolBreakoutLookbackDays} bars");
            if (!(volumes[0] >= volumeGates.VolBreakoutMultiple!.Value * recentAverage))
                return Reject(SparrowComparisonReasonCodes.P3VolBreakout,
                    $"Breakout volume {volumes[0]:F2} is below {volumeGates.VolBreakoutMultiple.Value:R} x {recentAverage:F2}", v3);
        }

        if (wantsContraction)
        {
            int lookback = volumeGates.VolContractionLookbackDays;
            if (!SparrowTechnicalIndicators.TryWindowAverage(volumes, 1, lookback, out double contractionAverage)
                || !SparrowTechnicalIndicators.TryWindowAverage(volumes, 1 + lookback, volumeGates.VolContractionBaselineDays, out double baselineAverage))
                return Unavailable("Volume history is incomplete over the contraction and baseline windows");
            if (!(contractionAverage <= volumeGates.VolContractionMaxRatio!.Value * baselineAverage))
                return Reject(SparrowComparisonReasonCodes.P3VolContraction,
                    $"Contraction mean {contractionAverage:F2} exceeds {volumeGates.VolContractionMaxRatio.Value:R} x baseline {baselineAverage:F2}", v3);
        }

        return v3;
    }

    private static SparrowTechnicalEvaluation Reject(string code, string reason, SparrowTechnicalEvaluation v3) =>
        new(SparrowRuleComparison.Reject("P3", code, reason), v3.MA5, v3.MA10, v3.MA20, v3.MA60, v3.Adhesion, v3.Momentum, v3.LatestPercent);

    private static SparrowTechnicalEvaluation Unavailable(string reason) =>
        new(SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3VolUnavailable, reason));
}
