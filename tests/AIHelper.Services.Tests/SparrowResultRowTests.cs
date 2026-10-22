using AIHelper.Models;
using AIHelper.ViewModels;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResultRowTests
{
    [Fact]
    public void From_ProjectsAndScalesFeatures()
    {
        var candidate = new SparrowRankedCandidate
        {
            RankingProfile = SparrowRankingProfileV1.Name,
            Code = "600000",
            Name = "浦发银行",
            Rank = 3,
            TotalScore = 68.5,
            Features = new SparrowRankingFeatures
            {
                Code = "600000",
                Name = "浦发银行",
                RisePercent = 3.5,
                Amount = 150_000_000,
                BuyPressureRatio = 1.5,
                Adhesion = 0.025,
                Turnover = 8.5,
                Momentum = 0.0012,
                AlphaMargin = 1.2
            }
        };

        SparrowResultRow row = SparrowResultRow.From(candidate);

        Assert.Equal(3, row.Rank);
        Assert.Equal("600000", row.Code);
        Assert.Equal("浦发银行", row.Name);
        Assert.Equal(68.5, row.TotalScore, 10);
        Assert.Equal(3.5, row.RisePercent!.Value, 10);
        Assert.Equal(15000.0, row.AmountWan!.Value, 6);
        Assert.Equal(1.5, row.BuyPressureRatio!.Value, 10);
        Assert.Equal(2.5, row.AdhesionPercent!.Value, 6);
        Assert.Equal(8.5, row.Turnover!.Value, 10);
        Assert.Equal(0.12, row.MomentumPercent!.Value, 6);
        Assert.Equal(1.2, row.AlphaMargin!.Value, 10);
    }

    [Fact]
    public void From_PreservesNullsForClassicOnlyFactors()
    {
        var candidate = new SparrowRankedCandidate
        {
            RankingProfile = SparrowRankingProfileV1.Name,
            Code = "600000",
            Name = "浦发银行",
            Rank = 1,
            TotalScore = 80,
            Features = new SparrowRankingFeatures
            {
                Code = "600000",
                Name = "浦发银行",
                RisePercent = 3.0,
                Amount = 100_000_000,
                BuyPressureRatio = 1.2,
                Adhesion = 0.02
            }
        };

        SparrowResultRow row = SparrowResultRow.From(candidate);

        Assert.Null(row.Turnover);
        Assert.Null(row.MomentumPercent);
        Assert.Null(row.AlphaMargin);
        Assert.Equal(2.0, row.AdhesionPercent!.Value, 6);
    }
}
