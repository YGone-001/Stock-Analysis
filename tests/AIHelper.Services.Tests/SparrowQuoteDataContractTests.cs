using System.Text.Json;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowQuoteDataContractTests
{
    [Theory]
    [InlineData(null, 100.0)]
    [InlineData(150.0, null)]
    [InlineData(null, null)]
    public void EvaluateVolume_ReportsUnavailableSeparately(double? outer, double? inner)
    {
        Assert.Equal(
            SparrowVolumeCheckResult.OuterInnerUnavailable,
            SparrowQuoteDataContract.EvaluateVolume(outer, inner, 1.2));
    }

    [Theory]
    [InlineData(150, 100, SparrowVolumeCheckResult.Passed)]
    [InlineData(110, 100, SparrowVolumeCheckResult.VolRatioFailed)]
    [InlineData(120, 100, SparrowVolumeCheckResult.VolRatioFailed)]
    [InlineData(0, 0, SparrowVolumeCheckResult.VolRatioFailed)]
    public void EvaluateVolume_PreservesStrictHistoricalComparison(
        double outer,
        double inner,
        SparrowVolumeCheckResult expected)
    {
        Assert.Equal(expected, SparrowQuoteDataContract.EvaluateVolume(outer, inner, 1.2));
    }

    [Fact]
    public void TryParse_PreservesMissingOuterInnerAsNull()
    {
        using JsonDocument document = JsonDocument.Parse("""
            { "Price":10.5, "PreClose":10, "Amount":50000000, "OuterVolume":null, "InnerVolume":null }
            """);

        bool parsed = SparrowQuoteDataContract.TryParse(document.RootElement, out SparrowQuoteData quote);

        Assert.True(parsed);
        Assert.Null(quote.OuterVolume);
        Assert.Null(quote.InnerVolume);
        Assert.Equal(
            SparrowVolumeCheckResult.OuterInnerUnavailable,
            SparrowQuoteDataContract.EvaluateVolume(quote.OuterVolume, quote.InnerVolume, 1.2));
    }
}
