using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public class SparrowP2RiseBasisTests
{
    [Fact]
    public void EvaluateV2Quote_UsesPriceDerivedRise_NotDataSourcePercent()
    {
        // Derived rise = (10.0 - 9.5) / 9.5 = 5.26% >= MinRise 5.0, but data-source Percent = 2.0% would fail.
        var quote = new SparrowQuoteData(
            Price: 10.0, PreClose: 9.5, Percent: 2.0,
            Amount: 100_000_000, Turnover: 10.0, OuterVolume: 1500, InnerVolume: 1000);
        var parameters = Parameters(MinRise: 5.0, MaxRise: 10.0);

        SparrowRuleComparison result = SparrowComparisonRuleEvaluators.EvaluateV2Quote(quote, parameters);

        Assert.True(result.Passed);
    }

    [Fact]
    public void EvaluateV2Quote_RejectsOnPriceDerivedRise_EvenWhenDataSourcePercentPasses()
    {
        // Derived rise = (10.0 - 9.8) / 9.8 = 2.04% < MinRise 5.0, but data-source Percent = 6.0% would pass.
        var quote = new SparrowQuoteData(
            Price: 10.0, PreClose: 9.8, Percent: 6.0,
            Amount: 100_000_000, Turnover: 10.0, OuterVolume: 1500, InnerVolume: 1000);
        var parameters = Parameters(MinRise: 5.0, MaxRise: 10.0);

        SparrowRuleComparison result = SparrowComparisonRuleEvaluators.EvaluateV2Quote(quote, parameters);

        Assert.Equal(SparrowComparisonReasonCodes.P2RiseBelowMin, result.ReasonCode);
    }

    private static SparrowScanParameters Parameters(double MinRise, double MaxRise) => new()
    {
        MinRise = MinRise,
        MaxRise = MaxRise,
        MinAmount = 1,
        VolRatio = 1.0,
        MinTurnover = 3,
        MaxTurnover = 30,
        StrictTurnover = false
    };
}
