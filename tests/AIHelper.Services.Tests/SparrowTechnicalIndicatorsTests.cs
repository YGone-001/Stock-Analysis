using AIHelper.Core.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowTechnicalIndicatorsTests
{
    [Fact]
    public void TryRsi_MatchesHandComputedWilderValue()
    {
        // Changes +1,+1,-1,+1,+1 with period 3: seed averages 2/3 and 1/3, Wilder updates to 23/27 and 4/27.
        double[] closes = { 10, 11, 12, 11, 12, 13 };

        Assert.True(SparrowTechnicalIndicators.TryRsi(closes, 3, out double rsi));

        double expected = 100 - 100 / (1 + (23d / 27) / (4d / 27));
        Assert.Equal(expected, rsi, 10);
    }

    [Fact]
    public void TryRsi_ResolvesBoundarySeriesAndRejectsInsufficientInput()
    {
        double[] rising = { 1, 2, 3, 4, 5 };
        Assert.True(SparrowTechnicalIndicators.TryRsi(rising, 4, out double allGains));
        Assert.Equal(100, allGains, 10);

        double[] flat = { 5, 5, 5, 5, 5 };
        Assert.True(SparrowTechnicalIndicators.TryRsi(flat, 4, out double neutral));
        Assert.Equal(50, neutral, 10);

        Assert.False(SparrowTechnicalIndicators.TryRsi(new[] { 1d, 2d, 3d }, 3, out _));
        Assert.False(SparrowTechnicalIndicators.TryRsi(new[] { 1d, -2d, 3d, 4d, 5d }, 3, out _));
        Assert.False(SparrowTechnicalIndicators.TryRsi(rising, 0, out _));
    }

    [Fact]
    public void TryMacd_MatchesHandComputedLinearSeries()
    {
        // Linear growth with fast 2 / slow 3 / signal 2 yields a constant DIF of 0.5 and a zero histogram.
        double[] closes = { 1, 2, 3, 4, 5 };

        Assert.True(SparrowTechnicalIndicators.TryMacd(closes, 2, 3, 2, out double dif, out double dea, out double histogram));

        Assert.Equal(.5, dif, 10);
        Assert.Equal(.5, dea, 10);
        Assert.Equal(0, histogram, 10);
    }

    [Fact]
    public void TryMacd_MatchesHandComputedAcceleratingSeries()
    {
        double[] closes = { 1, 2, 4, 8, 16 };

        Assert.True(SparrowTechnicalIndicators.TryMacd(closes, 2, 3, 2, out double dif, out double dea, out double histogram));

        Assert.Equal(2.212963, dif, 4);
        Assert.Equal(1.817901, dea, 4);
        Assert.Equal(.395062, histogram, 4);
        Assert.True(dif > dea);
    }

    [Fact]
    public void TryMacd_ReturnsZeroSpreadForFlatSeriesAndRejectsInvalidInput()
    {
        double[] flat = { 5, 5, 5, 5, 5, 5, 5, 5 };
        Assert.True(SparrowTechnicalIndicators.TryMacd(flat, 3, 5, 2, out double dif, out double dea, out double histogram));
        Assert.Equal(0, dif, 10);
        Assert.Equal(0, dea, 10);
        Assert.Equal(0, histogram, 10);

        Assert.False(SparrowTechnicalIndicators.TryMacd(new[] { 1d, 2d, 3d }, 2, 3, 2, out _, out _, out _));
        Assert.False(SparrowTechnicalIndicators.TryMacd(new[] { 1d, 2d, 0d, 4d, 5d }, 2, 3, 2, out _, out _, out _));
        Assert.False(SparrowTechnicalIndicators.TryMacd(new[] { 1d, 2d, 3d, 4d, 5d }, 3, 2, 2, out _, out _, out _));
    }

    [Fact]
    public void TryAtr_MatchesHandComputedTrueRanges()
    {
        // TR2 = max(3, 3, 0) = 3; TR3 = max(1.5, 1, 0.5) = 1.5; ATR(2) = 2.25.
        (double, double, double)[] bars = { (10, 8, 9), (12, 9, 10), (11, 9.5, 10.5) };

        Assert.True(SparrowTechnicalIndicators.TryAtr(bars, 2, out double atr));

        Assert.Equal(2.25, atr, 10);
    }

    [Fact]
    public void TryAtr_RejectsInsufficientOrInvalidBars()
    {
        (double, double, double)[] twoBars = { (10, 8, 9), (12, 9, 10) };
        Assert.False(SparrowTechnicalIndicators.TryAtr(twoBars, 2, out _));
        Assert.False(SparrowTechnicalIndicators.TryAtr(twoBars, 0, out _));

        (double, double, double)[] withNullishGap = { (10, 8, 9), (0, 9, 10), (11, 9.5, 10.5) };
        // The invalid middle bar is dropped; two valid bars remain, which cannot feed ATR(2).
        Assert.False(SparrowTechnicalIndicators.TryAtr(withNullishGap, 2, out _));
    }
}
