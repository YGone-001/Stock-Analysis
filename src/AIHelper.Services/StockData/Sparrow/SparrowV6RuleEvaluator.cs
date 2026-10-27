using AIHelper.Models;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Pure V6 P3 evaluator: the frozen V5 rule set (V2 rules + momentum + volume + weekly gates) plus an optional
/// intraday pullback gate on 30/60-minute bars. v5 behavior is delegated verbatim; the intraday gate only decides
/// after V5 has passed and only when a usable intraday snapshot is supplied.
/// </summary>
public static class SparrowV6RuleEvaluator
{
    /// <summary>The V6 rule version implemented by this evaluator.</summary>
    public const string RuleVersion = SparrowStrategyVersions.V6;

    public static SparrowTechnicalEvaluation Evaluate(
        SparrowKlineSnapshot? snapshot,
        double? shIndexPctChg,
        SparrowScanParameters parameters,
        SparrowMomentumGateConfig? momentumGates = null,
        SparrowVolumeGateConfig? volumeGates = null,
        SparrowWeeklyGateConfig? weeklyGates = null,
        SparrowIntradayGateConfig? intradayGates = null,
        SparrowKlineSnapshot? intradaySnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        intradayGates?.Validate();

        SparrowTechnicalEvaluation v5 = SparrowV5RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters, momentumGates, volumeGates, weeklyGates);
        if (!v5.Rule.Passed || intradayGates is null || !intradayGates.Enabled)
            return v5;

        SparrowRuleComparison gate = EvaluateIntradayGateOnly(intradaySnapshot, intradayGates);
        return gate.ReasonCode switch
        {
            SparrowComparisonReasonCodes.Pass => v5,
            SparrowComparisonReasonCodes.P3IntradayUnavailable => Unavailable(gate.Reason),
            _ => Reject(gate.ReasonCode, gate.Reason, v5)
        };
    }

    /// <summary>
    /// The intraday gate alone, shared by the full evaluator and the scanner's two-stage filter (which already
    /// holds V5 evidence and must not re-evaluate the daily rules). Returns Pass, Reject or Unavailable.
    /// </summary>
    public static SparrowRuleComparison EvaluateIntradayGateOnly(SparrowKlineSnapshot? intradaySnapshot, SparrowIntradayGateConfig gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        gates.Validate();
        if (intradaySnapshot is null || intradaySnapshot.ClosesNewestFirst.Count == 0)
            return SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3IntradayUnavailable, "Intraday bars are unavailable for the pullback gate");
        if (intradaySnapshot.ClosesNewestFirst.Count < gates.MaPeriod)
            return SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3IntradayUnavailable,
                $"Intraday history is shorter than the MA period of {gates.MaPeriod} bars");

        double ma = intradaySnapshot.ClosesNewestFirst.Take(gates.MaPeriod).Average();
        double newest = intradaySnapshot.ClosesNewestFirst[0];
        if (!(newest > ma))
            return SparrowRuleComparison.Reject("P3", SparrowComparisonReasonCodes.P3IntradayPullback,
                $"Intraday close {newest:F2} is at or below its MA{gates.MaPeriod} {ma:F2} on the {gates.PeriodMinutes}-minute chart");
        return SparrowRuleComparison.Pass("P3");
    }

    private static SparrowTechnicalEvaluation Reject(string code, string reason, SparrowTechnicalEvaluation v5) =>
        new(SparrowRuleComparison.Reject("P3", code, reason), v5.MA5, v5.MA10, v5.MA20, v5.MA60, v5.Adhesion, v5.Momentum, v5.LatestPercent);

    private static SparrowTechnicalEvaluation Unavailable(string reason) =>
        new(SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3IntradayUnavailable, reason));
}
