using AIHelper.Core.Sparrow;
using AIHelper.Models;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Locks the intraday auto-scan rules and the TopN entrant diff.</summary>
public sealed class SparrowAutoScanTests
{
    [Theory]
    [InlineData("2026-10-28 13:59", false)]   // one minute before the window
    [InlineData("2026-10-28 14:00", true)]    // inclusive start
    [InlineData("2026-10-28 14:25", true)]    // mid window
    [InlineData("2026-10-28 14:50", true)]    // inclusive end
    [InlineData("2026-10-28 14:51", false)]   // one minute after
    [InlineData("2026-10-31 14:25", false)]   // Saturday
    [InlineData("2026-11-01 14:25", false)]   // Sunday
    public void IsDue_GatesOnWeekdayWindow(string moment, bool expected)
    {
        DateTime now = DateTime.Parse(moment);
        Assert.Equal(expected, SparrowAutoScan.IsDue(now, lastScanAt: null));
    }

    [Fact]
    public void IsDue_EnforcesTheIntervalBetweenTicks()
    {
        DateTime first = new(2026, 10, 28, 14, 5, 0);
        Assert.True(SparrowAutoScan.IsDue(first, null));

        Assert.False(SparrowAutoScan.IsDue(first.AddMinutes(4), first));
        Assert.True(SparrowAutoScan.IsDue(first.AddMinutes(5), first));
        Assert.True(SparrowAutoScan.IsDue(first.AddMinutes(30), first));
    }

    [Fact]
    public void NewEntrants_ReportsOnlyFirstTimeCodesInRankingOrder()
    {
        SparrowRankedCandidate[] current =
        {
            Ranked("600000"), Ranked("600519"), Ranked("600001")
        };
        IReadOnlyList<SparrowRankedCandidate> entrants = SparrowAutoScan.NewEntrants(
            new[] { "600519" }, current);

        Assert.Equal(new[] { "600000", "600001" }, entrants.Select(item => item.Code));
    }

    [Fact]
    public void NewEntrants_EmptyPreviousSnapshot_IsSilentToAvoidAFirstRunFlood()
    {
        SparrowRankedCandidate[] current = { Ranked("600000"), Ranked("600001") };

        Assert.Empty(SparrowAutoScan.NewEntrants(Array.Empty<string>(), current));
    }

    private static SparrowRankedCandidate Ranked(string code) => new()
    {
        RankingProfile = SparrowRankingProfileV1.Name,
        Code = code,
        Name = code,
        Rank = 1,
        TotalScore = 50,
        Features = new SparrowRankingFeatures { Code = code, Name = code },
        IsDataComplete = true
    };
}
