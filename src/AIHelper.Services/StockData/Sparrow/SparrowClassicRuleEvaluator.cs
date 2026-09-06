using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Pure implementation of the historical Sparrow moving-average rules.
/// Input closes must be ordered newest first.
/// </summary>
public static class SparrowClassicRuleEvaluator
{
    public static SparrowClassicTechnicalResult Evaluate(
        IReadOnlyList<double> closesNewestFirst,
        double latestPrice,
        SparrowClassicScanParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(closesNewestFirst);
        ArgumentNullException.ThrowIfNull(parameters);

        // The original strategy required 60 daily closes even when the MA60 guard was disabled.
        if (closesNewestFirst.Count < 60)
        {
            return new SparrowClassicTechnicalResult(false, 0, 0, 0, 0, 0, SparrowClassicP3RejectReason.KlineMissing);
        }

        double ma5 = Average(closesNewestFirst, 5);
        double ma10 = Average(closesNewestFirst, 10);
        double ma20 = Average(closesNewestFirst, 20);
        double ma60 = Average(closesNewestFirst, 60);

        if (ma5 < ma10 || ma10 < ma20)
        {
            return new SparrowClassicTechnicalResult(false, ma5, ma10, ma20, ma60, 0, SparrowClassicP3RejectReason.MaOrder);
        }

        if (parameters.CheckMA60 && (latestPrice <= ma60 || ma20 < ma60))
        {
            return new SparrowClassicTechnicalResult(false, ma5, ma10, ma20, ma60, 0, SparrowClassicP3RejectReason.Ma60);
        }

        double minMa = Math.Min(ma5, Math.Min(ma10, ma20));
        if (minMa <= 0)
        {
            return new SparrowClassicTechnicalResult(false, ma5, ma10, ma20, ma60, 0, SparrowClassicP3RejectReason.MinMaInvalid);
        }

        double maxMa = Math.Max(ma5, Math.Max(ma10, ma20));
        double adhesion = (maxMa - minMa) / minMa;
        if (adhesion > parameters.MaxAdhesion)
        {
            return new SparrowClassicTechnicalResult(false, ma5, ma10, ma20, ma60, adhesion, SparrowClassicP3RejectReason.AdhesionHigh);
        }
        if (adhesion < parameters.MinAdhesion)
        {
            return new SparrowClassicTechnicalResult(false, ma5, ma10, ma20, ma60, adhesion, SparrowClassicP3RejectReason.AdhesionLow);
        }

        return new SparrowClassicTechnicalResult(true, ma5, ma10, ma20, ma60, adhesion, SparrowClassicP3RejectReason.None);
    }

    private static double Average(IReadOnlyList<double> values, int count)
    {
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            total += values[i];
        }
        return total / count;
    }
}
