using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowClassicRuleEvaluatorTests
{
    [Fact]
    public void Evaluate_AcceptsBullishMovingAverageOrder()
    {
        var parameters = NewParameters(maxAdhesion: 0.50);
        IReadOnlyList<double> closes = BuildCloses(15, 12, 10, 8);

        SparrowClassicTechnicalResult result = SparrowClassicRuleEvaluator.Evaluate(closes, 15, parameters);

        Assert.True(result.Passed);
        Assert.True(result.MA5 > result.MA10);
        Assert.True(result.MA10 > result.MA20);
    }

    [Fact]
    public void Evaluate_RejectsWhenMa5IsBelowMa10()
    {
        var parameters = NewParameters(maxAdhesion: 1.0);
        IReadOnlyList<double> closes = BuildCloses(10, 12, 9, 8);

        SparrowClassicTechnicalResult result = SparrowClassicRuleEvaluator.Evaluate(closes, 10, parameters);

        Assert.False(result.Passed);
        Assert.True(result.MA5 < result.MA10);
    }

    [Fact]
    public void Evaluate_RejectsPriceAtOrBelowMa60WhenEnabled()
    {
        var parameters = NewParameters(maxAdhesion: 0.50, checkMa60: true);
        IReadOnlyList<double> closes = BuildCloses(15, 12, 10, 8);
        double ma60 = closes.Take(60).Average();

        SparrowClassicTechnicalResult result = SparrowClassicRuleEvaluator.Evaluate(closes, ma60, parameters);

        Assert.False(result.Passed);
        Assert.Equal(ma60, result.MA60, 10);
    }

    [Fact]
    public void Evaluate_RejectsAdhesionAboveMaximum()
    {
        var parameters = NewParameters(maxAdhesion: 0.10);
        IReadOnlyList<double> closes = BuildCloses(15, 12, 10, 8);

        SparrowClassicTechnicalResult result = SparrowClassicRuleEvaluator.Evaluate(closes, 15, parameters);

        Assert.False(result.Passed);
        Assert.True(result.Adhesion > parameters.MaxAdhesion);
    }

    [Fact]
    public void Evaluate_AcceptsEqualMovingAveragesLikeHistoricalStrategy()
    {
        var parameters = NewParameters(maxAdhesion: 0, checkMa60: false);
        IReadOnlyList<double> closes = Enumerable.Repeat(10.0, 60).ToArray();

        SparrowClassicTechnicalResult result = SparrowClassicRuleEvaluator.Evaluate(closes, 10, parameters);

        Assert.True(result.Passed);
        Assert.Equal(0, result.Adhesion);
    }

    [Theory]
    [InlineData("600000", "浦发银行", true)]
    [InlineData("000001", "平安银行", true)]
    [InlineData("300750", "宁德时代", true)]
    [InlineData("688001", "华兴源创", false)]
    [InlineData("600001", "*ST示例", false)]
    [InlineData("430001", "北交示例", false)]
    public void IsEligibleStock_RestoresHistoricalUniverse(string code, string name, bool expected)
    {
        Assert.Equal(expected, SparrowClassicScanner.IsEligibleStock(code, name));
    }

    [Fact]
    public void Candidate_AlwaysIdentifiesClassicStrategy()
    {
        var candidate = new SparrowClassicCandidate { Code = "600000", Name = "示例", Reason = "测试" };

        Assert.Equal("SparrowClassic", candidate.Strategy);
    }

    [Theory]
    [InlineData("Turnover")]
    [InlineData("Momentum")]
    [InlineData("Alpha")]
    public void Parameters_DoNotExposeV2HardFilters(string propertyName)
    {
        Assert.Null(typeof(SparrowClassicScanParameters).GetProperty(propertyName));
    }

    private static SparrowClassicScanParameters NewParameters(double maxAdhesion, bool checkMa60 = false)
    {
        return new SparrowClassicScanParameters
        {
            CheckMA60 = checkMa60,
            MinAdhesion = 0,
            MaxAdhesion = maxAdhesion
        };
    }

    private static IReadOnlyList<double> BuildCloses(
        double newestFive,
        double nextFive,
        double nextTen,
        double oldestForty)
    {
        return Enumerable.Repeat(newestFive, 5)
            .Concat(Enumerable.Repeat(nextFive, 5))
            .Concat(Enumerable.Repeat(nextTen, 10))
            .Concat(Enumerable.Repeat(oldestForty, 40))
            .ToArray();
    }
}
