using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowV2RuleEvaluatorTests
{
    [Fact]
    public void Evaluate_RejectsWhenPrevMa5IsNonPositive_WithoutThrowing()
    {
        // All-zero closes drive prevMA5 to 0, which previously produced a NaN/Infinity momentum.
        var closes = Enumerable.Repeat(0.0, 60).ToArray();
        var snapshot = new SparrowKlineSnapshot(closes, 0, 0);
        SparrowScanParameters parameters = Parameters(momentumThreshold: 0);

        SparrowTechnicalEvaluation result = SparrowV2RuleEvaluator.Evaluate(snapshot, 0, parameters);

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3Momentum, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_RejectsWhenMinimumMaIsNonPositive()
    {
        // closes[0..7] keep MA5/prevMA5 positive (passes MA order + momentum),
        // closes[8..19] drive MA10/MA20 non-positive so minMa <= 0.
        double[] closes = Enumerable.Repeat(100.0, 60).ToArray();
        for (int i = 8; i < 20; i++)
        {
            closes[i] = -1000.0;
        }
        var snapshot = new SparrowKlineSnapshot(closes, closes[0], 0);
        SparrowScanParameters parameters = Parameters(momentumThreshold: -0.1);

        SparrowTechnicalEvaluation result = SparrowV2RuleEvaluator.Evaluate(snapshot, 0, parameters);

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3MaInvalid, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_StillPassesNormalBullishSeries()
    {
        double[] closes = Enumerable.Range(0, 120).Select(index => 8.0 + index * 0.02).Reverse().ToArray();
        var snapshot = new SparrowKlineSnapshot(closes, closes[0], 0.25);
        SparrowScanParameters parameters = Parameters(momentumThreshold: 0);

        SparrowTechnicalEvaluation result = SparrowV2RuleEvaluator.Evaluate(snapshot, 0, parameters);

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);
    }

    private static SparrowScanParameters Parameters(double momentumThreshold) => new()
    {
        MacroDef = false,
        MinRise = 1,
        MaxRise = 5,
        VolRatio = 1.1,
        MinAmount = 1,
        CheckMA60 = false,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MinTurnover = 3,
        MaxTurnover = 30,
        MomentumThreshold = momentumThreshold,
        CheckAlpha = false,
        MaxConcurrency = 2,
        UseCache = true
    };
}
