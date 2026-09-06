using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Adds comparison diagnostics while delegating Classic math to its historical evaluator.</summary>
public static class SparrowClassicComparisonEvaluator
{
    public static SparrowTechnicalEvaluation Evaluate(
        SparrowKlineSnapshot? snapshot,
        SparrowClassicScanParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (snapshot == null)
        {
            return Missing(SparrowComparisonReasonCodes.P3KlineMissing, "Kline data is unavailable");
        }

        IReadOnlyList<double> latest65 = snapshot.ClosesNewestFirst.Take(65).ToArray();
        if (latest65.Count < 60)
        {
            return Missing(
                SparrowComparisonReasonCodes.P3KlineInsufficient,
                $"Kline has {latest65.Count} valid closes; 60 required");
        }

        SparrowClassicTechnicalResult technical = SparrowClassicRuleEvaluator.Evaluate(
            latest65, snapshot.LatestPrice, parameters);
        SparrowRuleComparison rule;
        if (technical.MA5 < technical.MA10 || technical.MA10 < technical.MA20)
        {
            rule = SparrowRuleComparison.Reject("P3", SparrowComparisonReasonCodes.P3MaOrder,
                "MA5 >= MA10 >= MA20 is not satisfied");
        }
        else if (parameters.CheckMA60 && (snapshot.LatestPrice <= technical.MA60 || technical.MA20 < technical.MA60))
        {
            rule = SparrowRuleComparison.Reject("P3", SparrowComparisonReasonCodes.P3Ma60,
                "Price > MA60 and MA20 >= MA60 are not both satisfied");
        }
        else if (technical.Adhesion < parameters.MinAdhesion)
        {
            rule = SparrowRuleComparison.Reject("P3", SparrowComparisonReasonCodes.P3AdhesionLow,
                $"Adhesion {technical.Adhesion:F6} is below {parameters.MinAdhesion:F6}");
        }
        else if (technical.Adhesion > parameters.MaxAdhesion)
        {
            rule = SparrowRuleComparison.Reject("P3", SparrowComparisonReasonCodes.P3AdhesionHigh,
                $"Adhesion {technical.Adhesion:F6} is above {parameters.MaxAdhesion:F6}");
        }
        else
        {
            rule = technical.Passed
                ? SparrowRuleComparison.Pass("P3")
                : SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3KlineInsufficient,
                    "Technical inputs are invalid");
        }

        return new SparrowTechnicalEvaluation(
            rule, technical.MA5, technical.MA10, technical.MA20, technical.MA60,
            technical.Adhesion, LatestPercent: snapshot.LatestPercent);
    }

    private static SparrowTechnicalEvaluation Missing(string code, string reason) =>
        new(SparrowRuleComparison.Unavailable("P3", code, reason));
}
