using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Pure quote-stage evaluators used by the comparison path.</summary>
public static class SparrowComparisonRuleEvaluators
{
    public static SparrowRuleComparison EvaluateClassicQuote(
        SparrowQuoteData quote,
        SparrowClassicScanParameters parameters)
    {
        double? rise = quote.PriceDerivedPercent;
        if (!rise.HasValue || !quote.Amount.HasValue)
        {
            return SparrowRuleComparison.Unavailable(
                "P2", SparrowComparisonReasonCodes.P2QuoteDataMissing,
                "Price/pre-close or amount is unavailable");
        }

        if (rise.Value < parameters.MinRise)
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2RiseBelowMin,
                $"Price-derived rise {rise.Value:F4}% is below {parameters.MinRise:F4}%");
        }

        if (rise.Value > parameters.MaxRise)
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2RiseAboveMax,
                $"Price-derived rise {rise.Value:F4}% is above {parameters.MaxRise:F4}%");
        }

        if (quote.Amount.Value < parameters.MinAmount)
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2Amount,
                $"Amount {quote.Amount.Value:F0} is below {parameters.MinAmount:F0}");
        }

        return EvaluateVolume(quote, parameters.VolRatio);
    }

    public static SparrowRuleComparison EvaluateV2Quote(
        SparrowQuoteData quote,
        SparrowScanParameters parameters)
    {
        if (quote.Price is not > 0.001 || !quote.Percent.HasValue || !quote.Amount.HasValue)
        {
            return SparrowRuleComparison.Unavailable(
                "P2", SparrowComparisonReasonCodes.P2QuoteDataMissing,
                "Price, percent or amount is unavailable");
        }

        if (quote.Percent.Value < parameters.MinRise)
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2RiseBelowMin,
                $"Quote percent {quote.Percent.Value:F4}% is below {parameters.MinRise:F4}%");
        }

        if (quote.Percent.Value > parameters.MaxRise)
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2RiseAboveMax,
                $"Quote percent {quote.Percent.Value:F4}% is above {parameters.MaxRise:F4}%");
        }

        if (quote.Amount.Value < parameters.MinAmount)
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2Amount,
                $"Amount {quote.Amount.Value:F0} is below {parameters.MinAmount:F0}");
        }

        SparrowRuleComparison volume = EvaluateVolume(quote, parameters.VolRatio);
        if (!volume.Passed)
        {
            return volume;
        }

        // Deliberately preserves the current V2 fail-open semantics for missing/non-positive turnover.
        if (quote.Turnover.HasValue
            && quote.Turnover.Value > 0
            && (quote.Turnover.Value < parameters.MinTurnover || quote.Turnover.Value > parameters.MaxTurnover))
        {
            return SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2Turnover,
                $"Turnover {quote.Turnover.Value:F4}% is outside [{parameters.MinTurnover:F4}%, {parameters.MaxTurnover:F4}%]");
        }

        return SparrowRuleComparison.Pass("P2");
    }

    private static SparrowRuleComparison EvaluateVolume(SparrowQuoteData quote, double volRatio)
    {
        SparrowVolumeCheckResult result = SparrowQuoteDataContract.EvaluateVolume(
            quote.OuterVolume, quote.InnerVolume, volRatio);
        return result switch
        {
            SparrowVolumeCheckResult.OuterInnerUnavailable => SparrowRuleComparison.Unavailable(
                "P2", SparrowComparisonReasonCodes.P2OuterInnerMissing,
                "Outer or inner volume is unavailable"),
            SparrowVolumeCheckResult.VolRatioFailed => SparrowRuleComparison.Reject(
                "P2", SparrowComparisonReasonCodes.P2VolRatio,
                $"Outer volume is not greater than inner volume × {volRatio:F4}"),
            _ => SparrowRuleComparison.Pass("P2")
        };
    }
}
