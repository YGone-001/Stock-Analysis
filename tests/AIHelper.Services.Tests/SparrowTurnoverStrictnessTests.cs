using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowTurnoverStrictnessTests
{
    private static SparrowQuoteData Quote(double? turnover) => new(
        Price: 10.3, PreClose: 10.0, Percent: 3.0, Amount: 100_000_000,
        Turnover: turnover, OuterVolume: 1500, InnerVolume: 1000);

    private static SparrowScanParameters Parameters(bool strictTurnover) => new()
    {
        MinRise = 1,
        MaxRise = 5,
        MinAmount = 1,
        VolRatio = 1.1,
        MinTurnover = 3,
        MaxTurnover = 30,
        StrictTurnover = strictTurnover
    };

    [Fact]
    public void MissingTurnover_FailOpen_WhenStrictTurnoverIsOff()
    {
        SparrowRuleComparison result = SparrowComparisonRuleEvaluators.EvaluateV2Quote(
            Quote(turnover: null), Parameters(strictTurnover: false));

        Assert.True(result.Passed);
    }

    [Fact]
    public void MissingTurnover_Rejects_WhenStrictTurnoverIsOn()
    {
        SparrowRuleComparison result = SparrowComparisonRuleEvaluators.EvaluateV2Quote(
            Quote(turnover: null), Parameters(strictTurnover: true));

        Assert.False(result.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P2Turnover, result.ReasonCode);
    }

    [Fact]
    public void OutOfRangeTurnover_Rejects_RegardlessOfStrictness()
    {
        SparrowRuleComparison strict = SparrowComparisonRuleEvaluators.EvaluateV2Quote(
            Quote(turnover: 1.0), Parameters(strictTurnover: true));
        SparrowRuleComparison lenient = SparrowComparisonRuleEvaluators.EvaluateV2Quote(
            Quote(turnover: 1.0), Parameters(strictTurnover: false));

        Assert.False(strict.Passed);
        Assert.False(lenient.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P2Turnover, strict.ReasonCode);
        Assert.Equal(SparrowComparisonReasonCodes.P2Turnover, lenient.ReasonCode);
    }

    [Fact]
    public void ValidTurnover_Passes_WhenStrictTurnoverIsOn()
    {
        SparrowRuleComparison result = SparrowComparisonRuleEvaluators.EvaluateV2Quote(
            Quote(turnover: 10.0), Parameters(strictTurnover: true));

        Assert.True(result.Passed);
    }
}
