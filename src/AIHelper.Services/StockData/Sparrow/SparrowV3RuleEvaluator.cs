using AIHelper.Models;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

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

/// <summary>
/// Pure V3 P3 evaluator: the frozen V2 rule set plus optional close-based momentum-quality gates.
/// v2 behavior is delegated verbatim; the gates only decide after V2 has passed.
/// </summary>
public static class SparrowV3RuleEvaluator
{
    /// <summary>The V3 rule version implemented by this evaluator.</summary>
    public const string RuleVersion = SparrowStrategyVersions.V3;

    public static SparrowTechnicalEvaluation Evaluate(
        SparrowKlineSnapshot? snapshot,
        double? shIndexPctChg,
        SparrowScanParameters parameters,
        SparrowMomentumGateConfig? momentumGates = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        momentumGates?.Validate();

        SparrowTechnicalEvaluation v2 = SparrowV2RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters);
        if (!v2.Rule.Passed || momentumGates is null)
            return v2;

        IReadOnlyList<double> closesOldestFirst = snapshot!.ClosesNewestFirst.Reverse().ToArray();
        if (momentumGates.RsiMin.HasValue || momentumGates.RsiMax.HasValue)
        {
            if (!SparrowTechnicalIndicators.TryRsi(closesOldestFirst, momentumGates.RsiPeriod, out double rsi))
                return Unavailable("RSI is unavailable for the supplied close series");
            bool belowMin = momentumGates.RsiMin.HasValue && rsi < momentumGates.RsiMin.Value;
            bool aboveMax = momentumGates.RsiMax.HasValue && rsi > momentumGates.RsiMax.Value;
            if (belowMin || aboveMax)
                return Reject(SparrowComparisonReasonCodes.P3Rsi,
                    $"RSI({momentumGates.RsiPeriod}) {rsi:F4} is outside [{momentumGates.RsiMin?.ToString() ?? "-"}, {momentumGates.RsiMax?.ToString() ?? "-"}]",
                    v2);
        }

        if (momentumGates.RequireMacdBullish)
        {
            if (!SparrowTechnicalIndicators.TryMacd(closesOldestFirst, momentumGates.MacdFastPeriod, momentumGates.MacdSlowPeriod, momentumGates.MacdSignalPeriod,
                    out double dif, out double dea, out _))
                return Unavailable("MACD is unavailable for the supplied close series");
            if (dif < dea)
                return Reject(SparrowComparisonReasonCodes.P3Macd, $"MACD DIF {dif:F6} is below DEA {dea:F6}", v2);
        }

        return v2;
    }

    private static SparrowTechnicalEvaluation Reject(string code, string reason, SparrowTechnicalEvaluation v2) =>
        new(SparrowRuleComparison.Reject("P3", code, reason), v2.MA5, v2.MA10, v2.MA20, v2.MA60, v2.Adhesion, v2.Momentum, v2.LatestPercent);

    private static SparrowTechnicalEvaluation Unavailable(string reason) =>
        new(SparrowRuleComparison.Unavailable("P3", SparrowComparisonReasonCodes.P3KlineInsufficient, reason));
}
