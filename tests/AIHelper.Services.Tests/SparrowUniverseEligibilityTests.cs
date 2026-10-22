using AIHelper.Core.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowUniverseEligibilityTests
{
    [Theory]
    [InlineData("ST康美")]
    [InlineData("*ST康美")]
    [InlineData("SST华新")]
    [InlineData("S*ST前锋")]
    public void Evaluate_ExcludesStPrefixNames(string name)
    {
        SparrowClassicEligibilityResult result = SparrowClassicUniverseEligibility.Evaluate("600001", name);

        Assert.False(result.Eligible);
        Assert.Equal("ST_EXCLUDED", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_ExcludesDelistingNames()
    {
        SparrowClassicEligibilityResult result = SparrowClassicUniverseEligibility.Evaluate("600001", "康美退");

        Assert.False(result.Eligible);
        Assert.Equal("DELISTING_EXCLUDED", result.ReasonCode);
    }

    [Fact]
    public void Evaluate_AllowsNormalNames()
    {
        SparrowClassicEligibilityResult result = SparrowClassicUniverseEligibility.Evaluate("600000", "浦发银行");

        Assert.True(result.Eligible);
    }

    [Fact]
    public void Evaluate_DoesNotExcludeNamesContainingStSubstring()
    {
        // "ST" appears mid-name but is not the ST prefix; the legacy Contains("ST") check would have excluded this.
        SparrowClassicEligibilityResult result = SparrowClassicUniverseEligibility.Evaluate("600000", "某某ST科技");

        Assert.True(result.Eligible);
    }
}
