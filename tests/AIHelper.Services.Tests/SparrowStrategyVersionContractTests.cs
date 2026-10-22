using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>
/// Locks the strategy-version → rule-implementation mapping so the frozen Classic control group
/// cannot be edited in place without noticing.
/// </summary>
public class SparrowStrategyVersionContractTests
{
    [Fact]
    public void ClassicRuleEvaluator_ImplementsClassicV1()
    {
        Assert.Equal(SparrowStrategyVersions.Classic, SparrowClassicRuleEvaluator.RuleVersion);
    }

    [Fact]
    public void V2RuleEvaluator_ImplementsV2()
    {
        Assert.Equal(SparrowStrategyVersions.V2, SparrowV2RuleEvaluator.RuleVersion);
    }

    [Fact]
    public void ClassicV1_AcceptsEqualMovingAverages_AsFrozenControlGroup()
    {
        // classic-v1 permits MA equality (ma5 >= ma10 >= ma20) - a frozen historical behavior
        // that must not change without publishing a new Classic version.
        var parameters = new SparrowClassicScanParameters
        {
            CheckMA60 = false,
            MinAdhesion = 0,
            MaxAdhesion = 0
        };
        IReadOnlyList<double> closes = Enumerable.Repeat(10.0, 60).ToArray();

        SparrowClassicTechnicalResult result = SparrowClassicRuleEvaluator.Evaluate(closes, 10, parameters);

        Assert.True(result.Passed);
        Assert.Equal(0, result.Adhesion);
    }
}
