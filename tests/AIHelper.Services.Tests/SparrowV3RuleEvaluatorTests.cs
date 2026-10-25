using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowV3RuleEvaluatorTests
{
    [Fact]
    public void Evaluate_DelegatesVerbatimToV2_WhenGatesAreDisabled()
    {
        SparrowKlineSnapshot snapshot = AlphaRejectionSnapshot();
        SparrowTechnicalEvaluation v2 = SparrowV2RuleEvaluator.Evaluate(snapshot, 10, Parameters());
        SparrowTechnicalEvaluation v3 = SparrowV3RuleEvaluator.Evaluate(snapshot, 10, Parameters());

        Assert.Equal(v2.Rule.Passed, v3.Rule.Passed);
        Assert.Equal(v2.Rule.ReasonCode, v3.Rule.ReasonCode);
        Assert.Equal(SparrowComparisonReasonCodes.P3Alpha, v3.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_WithoutGates_PassesTheSameBullishSeriesAsV2()
    {
        SparrowTechnicalEvaluation result = SparrowV3RuleEvaluator.Evaluate(BullishSnapshot(), 0, Parameters());

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_RejectsRsiAboveTheGateBand()
    {
        // An uninterrupted rally leaves RSI(14) at 100, outside the default [25, 75] band.
        SparrowTechnicalEvaluation result = SparrowV3RuleEvaluator.Evaluate(BullishSnapshot(), 0, Parameters(), new SparrowMomentumGateConfig());

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3Rsi, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_AcceptsZigzagRallyInsideTheRsiBand()
    {
        // Gains of 0.2 with dips of 0.15 keep Wilder RSI(14) near 72.7 while the V2 uptrend rules still pass.
        SparrowTechnicalEvaluation result = SparrowV3RuleEvaluator.Evaluate(ZigzagSnapshot(), 0, LooseParameters(),
            new SparrowMomentumGateConfig(RsiMin: 25, RsiMax: 75, RequireMacdBullish: false));

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_RejectsMacdCrossdownAfterMomentumFades()
    {
        // A steep rally flattening to a near-zero slope keeps the MA order and momentum alive,
        // but the shrinking DIF falls below its own signal line.
        SparrowTechnicalEvaluation result = SparrowV3RuleEvaluator.Evaluate(FadingRallySnapshot(), 0, LooseParameters(),
            new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: true));

        Assert.False(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3Macd, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_AcceptsCalmRallyUnderTheMacdGate()
    {
        // A slow, gently accelerating grind keeps DIF above DEA.
        double[] calm = Enumerable.Range(0, 120).Select(index => 20d + index * 0.01 + index * index * 0.0001).Reverse().ToArray();
        var snapshot = new SparrowKlineSnapshot(calm, calm[0], 0);

        SparrowTechnicalEvaluation result = SparrowV3RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(),
            new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: true));

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);
    }

    [Fact]
    public void Evaluate_MacdGateToleratesFloatNoiseAtExactEquality()
    {
        // A perfectly linear series leaves DIF mathematically equal to DEA; the strict comparison could flip on
        // floating-point noise, so the relative tolerance must accept it deterministically.
        double[] linear = Enumerable.Range(0, 120).Select(index => 20d + index * 0.01).Reverse().ToArray();
        var snapshot = new SparrowKlineSnapshot(linear, linear[0], 0);

        SparrowTechnicalEvaluation result = SparrowV3RuleEvaluator.Evaluate(snapshot, 0, LooseParameters(),
            new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: true));

        Assert.True(result.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.Pass, result.Rule.ReasonCode);

        // A clearly separated cross-down still rejects.
        SparrowTechnicalEvaluation rejected = SparrowV3RuleEvaluator.Evaluate(FadingRallySnapshot(), 0, LooseParameters(),
            new SparrowMomentumGateConfig(RsiMin: null, RsiMax: null, RequireMacdBullish: true));
        Assert.False(rejected.Rule.Passed);
        Assert.Equal(SparrowComparisonReasonCodes.P3Macd, rejected.Rule.ReasonCode);
    }

    private static SparrowScanParameters Parameters() => new()
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
        MomentumThreshold = 0,
        CheckAlpha = true,
        MaxConcurrency = 2,
        UseCache = true
    };

    private static SparrowScanParameters LooseParameters()
    {
        SparrowScanParameters parameters = Parameters();
        parameters.CheckAlpha = false;
        parameters.MaxAdhesion = 0.05;
        return parameters;
    }

    private static SparrowKlineSnapshot BullishSnapshot()
    {
        double[] closes = Enumerable.Range(0, 120).Select(index => 8.0 + index * 0.2).Reverse().ToArray();
        return new SparrowKlineSnapshot(closes, closes[0], 0.25);
    }

    private static SparrowKlineSnapshot AlphaRejectionSnapshot()
    {
        double[] closes = Enumerable.Range(0, 120).Select(index => 8.0 + index * 0.02).Reverse().ToArray();
        return new SparrowKlineSnapshot(closes, closes[0], 0.25);
    }

    private static SparrowKlineSnapshot ZigzagSnapshot()
    {
        double[] cycle = { .2, .2, -.15, .2, .2, -.15 };
        var closes = new List<double>(121) { 20 };
        for (int index = 0; index < 120; index++) closes.Add(closes[^1] + cycle[index % cycle.Length]);
        closes.Reverse();
        return new SparrowKlineSnapshot(closes, closes[0], 0);
    }

    private static SparrowKlineSnapshot FadingRallySnapshot()
    {
        var closes = new List<double>(120) { 8 };
        for (int index = 1; index < 120; index++) closes.Add(closes[^1] + (index <= 100 ? 0.3 : 0.01));
        closes.Reverse();
        return new SparrowKlineSnapshot(closes, closes[0], 0);
    }
}
