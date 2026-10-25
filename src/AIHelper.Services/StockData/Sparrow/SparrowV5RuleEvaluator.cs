using AIHelper.Models;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Pure V5 P3 evaluator: the frozen V4 rule set (V2 rules + momentum + volume-pattern gates) plus a
/// calendar-week MA trend gate so entries follow the multi-month trend. v4 behavior is delegated verbatim.
/// </summary>
public static class SparrowV5RuleEvaluator
{
    /// <summary>The V5 rule version implemented by this evaluator.</summary>
    public const string RuleVersion = SparrowStrategyVersions.V5;

    public static SparrowTechnicalEvaluation Evaluate(
        SparrowKlineSnapshot? snapshot,
        double? shIndexPctChg,
        SparrowScanParameters parameters,
        SparrowMomentumGateConfig? momentumGates = null,
        SparrowVolumeGateConfig? volumeGates = null,
        SparrowWeeklyGateConfig? weeklyGates = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        weeklyGates?.Validate();

        SparrowTechnicalEvaluation v4 = SparrowV4RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters, momentumGates, volumeGates);
        if (!v4.Rule.Passed || weeklyGates is null || !weeklyGates.RequireWeeklyUptrend)
            return v4;

        if (snapshot is null || !snapshot.HasDates)
            return Unavailable("Bar dates are unavailable for the weekly-trend gate");

        if (!SparrowTechnicalIndicators.TryWeeklyMaRising(snapshot.Dates, snapshot.ClosesNewestFirst, weeklyGates.WeeklyMaPeriod, out bool rising))
            return Unavailable($"Weekly history is shorter than {weeklyGates.WeeklyMaPeriod + 1} weeks");

        if (!rising)
            return Reject(SparrowComparisonReasonCodes.P3WeeklyTrend,
                $"Weekly MA{weeklyGates.WeeklyMaPeriod} is not rising", v4);

        return v4;
    }

    private static SparrowTechnicalEvaluation Reject(string code, string reason, SparrowTechnicalEvaluation v4) =>
        new(SparrowRuleComparison.Reject("P3", code, reason), v4.MA5, v4.MA10, v4.MA20, v4.MA60, v4.Adhesion, v4.Momentum, v4.LatestPercent);

    private static SparrowTechnicalEvaluation Unavailable(string reason) =>
        new(SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3WeeklyUnavailable, reason));
}
