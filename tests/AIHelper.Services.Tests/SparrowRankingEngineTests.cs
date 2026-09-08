using System.IO;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowRankingEngineTests
{
    private readonly SparrowRankingEngine _engine = new();

    [Fact]
    public void PercentileRank_CoversSingletonPairsTriplesTiesEqualNegativeAndNull()
    {
        Assert.Equal(new[] { 100.0 }, SparrowRankingEngine.PercentileRank(new double?[] { 7 }));
        Assert.Equal(new[] { 0.0, 100.0 }, SparrowRankingEngine.PercentileRank(new double?[] { 1, 2 }));
        Assert.Equal(new[] { 0.0, 50.0, 100.0 }, SparrowRankingEngine.PercentileRank(new double?[] { 1, 2, 3 }));
        Assert.Equal(new[] { 75.0, 75.0, 0.0 }, SparrowRankingEngine.PercentileRank(new double?[] { 10, 10, 8 }));
        Assert.Equal(new[] { 50.0, 50.0, 50.0 }, SparrowRankingEngine.PercentileRank(new double?[] { 4, 4, 4 }));
        Assert.Equal(new[] { 0.0, 50.0, 100.0 }, SparrowRankingEngine.PercentileRank(new double?[] { -3, -2, -1 }));
        Assert.Equal(new[] { 0.0, 50.0, 100.0 }, SparrowRankingEngine.PercentileRank(new double?[] { 1, null, 2 }));
    }

    [Fact]
    public void PercentileRank_IsOrderIndependentByValue()
    {
        double?[] original = { 5, 1, 5, null, -2, 9 };
        IReadOnlyList<double> originalScores = SparrowRankingEngine.PercentileRank(original);
        int[] permutation = { 3, 5, 0, 4, 2, 1 };
        double?[] shuffled = permutation.Select(index => original[index]).ToArray();
        IReadOnlyList<double> shuffledScores = SparrowRankingEngine.PercentileRank(shuffled);

        for (int shuffledIndex = 0; shuffledIndex < permutation.Length; shuffledIndex++)
        {
            Assert.Equal(originalScores[permutation[shuffledIndex]], shuffledScores[shuffledIndex], 12);
        }
    }

    [Fact]
    public void ClassicRanking_UsesBuyPressureLiquidityAndMidpointRiseQuality()
    {
        SparrowRankingFeatures[] candidates =
        {
            Feature("A", rise: 3.0, amount: 300, buyPressure: 2.0),
            Feature("B", rise: 2.0, amount: 200, buyPressure: 1.5),
            Feature("C", rise: 4.9, amount: 100, buyPressure: 1.1)
        };

        IReadOnlyList<SparrowRankedCandidate> ranking = _engine.RankClassic(candidates, 1, 5);

        Assert.Equal(new[] { "A", "B", "C" }, ranking.Select(candidate => candidate.Code));
        Assert.Equal(new[] { 1, 2, 3 }, ranking.Select(candidate => candidate.Rank));
        Assert.Equal(100, ranking[0].TotalScore, 12);
        Assert.Equal(50, ranking[1].TotalScore, 12);
        Assert.Equal(0, ranking[2].TotalScore, 12);
        Assert.Equal(SparrowRankingWeights.Classic.BuyPressure,
            ranking[0].Factor(SparrowRankingFactorNames.BuyPressure).Weight, 12);
        Assert.DoesNotContain(ranking[0].Factors, factor => factor.Factor is "Adhesion" or "Turnover");
    }

    [Fact]
    public void V2Ranking_MomentumSeparatesOtherwiseEqualCandidates()
    {
        SparrowRankingFeatures[] candidates =
        {
            Feature("A", momentum: 0.04),
            Feature("B", momentum: 0.02),
            Feature("C", momentum: 0.002)
        };

        IReadOnlyList<SparrowRankedCandidate> ranking = _engine.RankV2(candidates, 1, 5, checkAlpha: false);

        Assert.Equal(new[] { "A", "B", "C" }, ranking.Select(candidate => candidate.Code));
        Assert.True(ranking[0].TotalScore > ranking[1].TotalScore);
        Assert.True(ranking[1].TotalScore > ranking[2].TotalScore);
    }

    [Fact]
    public void Ranking_BuyPressureSeparatesOtherwiseEqualCandidates()
    {
        SparrowRankingFeatures[] candidates =
        {
            Feature("Low", buyPressure: 1.1),
            Feature("Mid", buyPressure: 1.5),
            Feature("High", buyPressure: 2.0)
        };

        IReadOnlyList<SparrowRankedCandidate> classic = _engine.RankClassic(candidates, 1, 5);
        IReadOnlyList<SparrowRankedCandidate> v2 = _engine.RankV2(candidates, 1, 5, checkAlpha: false);

        Assert.Equal(new[] { "High", "Mid", "Low" }, classic.Select(candidate => candidate.Code));
        Assert.Equal(new[] { "High", "Mid", "Low" }, v2.Select(candidate => candidate.Code));
    }

    [Fact]
    public void RiseQuality_PrefersMidpointAndUsesMidrankForEqualDistances()
    {
        IReadOnlyList<SparrowRankedCandidate> ranking = _engine.RankClassic(new[]
        {
            Feature("A", rise: 3.0),
            Feature("B", rise: 2.0),
            Feature("C", rise: 4.9)
        }, 1, 5);
        Assert.True(Rise(ranking, "A") > Rise(ranking, "B"));
        Assert.True(Rise(ranking, "B") > Rise(ranking, "C"));

        IReadOnlyList<SparrowRankedCandidate> tied = _engine.RankClassic(new[]
        {
            Feature("B", rise: 2.0),
            Feature("C", rise: 4.0)
        }, 1, 5);
        Assert.Equal(Rise(tied, "B"), Rise(tied, "C"), 12);
    }

    [Fact]
    public void AlphaDisabled_BypassesFactorAndRenormalizesActiveWeights()
    {
        SparrowRankedCandidate candidate = Assert.Single(_engine.RankV2(
            new[] { Feature("A", alpha: null) }, 1, 5, checkAlpha: false));
        SparrowFactorScore alpha = candidate.Factor(SparrowRankingFactorNames.AlphaStrength);

        Assert.True(alpha.Bypassed);
        Assert.Equal(0, alpha.Weight);
        Assert.Equal(1.0, candidate.Factors.Sum(factor => factor.Weight), 12);
        Assert.Equal(0.30 / 0.80,
            candidate.Factor(SparrowRankingFactorNames.Momentum).Weight, 12);
        Assert.InRange(candidate.TotalScore, 0, 100);
        Assert.True(candidate.IsDataComplete);
    }

    [Fact]
    public void V2AlphaEnabled_UsesVersionedOriginalWeights()
    {
        SparrowRankedCandidate candidate = Assert.Single(_engine.RankV2(
            new[] { Feature("A") }, 1, 5, checkAlpha: true));

        Assert.False(candidate.Factor(SparrowRankingFactorNames.AlphaStrength).Bypassed);
        Assert.Equal(SparrowRankingWeights.V2.Momentum,
            candidate.Factor(SparrowRankingFactorNames.Momentum).Weight, 12);
        Assert.Equal(SparrowRankingWeights.V2.BuyPressure,
            candidate.Factor(SparrowRankingFactorNames.BuyPressure).Weight, 12);
        Assert.Equal(SparrowRankingWeights.V2.AlphaStrength,
            candidate.Factor(SparrowRankingFactorNames.AlphaStrength).Weight, 12);
        Assert.Equal(SparrowRankingWeights.V2.Liquidity,
            candidate.Factor(SparrowRankingFactorNames.Liquidity).Weight, 12);
        Assert.Equal(SparrowRankingWeights.V2.RiseQuality,
            candidate.Factor(SparrowRankingFactorNames.RiseQuality).Weight, 12);
        Assert.DoesNotContain(candidate.Factors, factor => factor.Factor is "Adhesion" or "Turnover");
    }

    [Fact]
    public void MissingActiveFactor_IsNeutralAndReportedWithoutRejectingCandidate()
    {
        SparrowRankedCandidate candidate = Assert.Single(_engine.RankClassic(
            new[] { Feature("A", amount: null) }, 1, 5));

        SparrowFactorScore liquidity = candidate.Factor(SparrowRankingFactorNames.Liquidity);
        Assert.Equal(50, liquidity.Percentile);
        Assert.False(candidate.IsDataComplete);
        Assert.Equal(SparrowRankingFactorNames.Liquidity, candidate.MissingFactors);
        Assert.Equal(1, candidate.Rank);
    }

    [Fact]
    public void ZeroInnerVolumeSentinel_IsFiniteAndDeterministic()
    {
        double? ratio = SparrowRankingFeatures.CalculateBuyPressureRatio(100, 0);

        Assert.Equal(SparrowRankingProfileV1.ZeroInnerVolumeRatioSentinel, ratio);
        Assert.True(double.IsFinite(ratio!.Value));
        Assert.Null(SparrowRankingFeatures.CalculateBuyPressureRatio(0, 0));

        SparrowRankingFeatures invalid = Feature("A") with
        {
            Momentum = double.PositiveInfinity,
            AlphaMargin = double.NaN
        };
        SparrowRankedCandidate ranked = Assert.Single(_engine.RankV2(
            new[] { invalid }, 1, 5, checkAlpha: true));
        Assert.Null(ranked.Features.Momentum);
        Assert.Null(ranked.Features.AlphaMargin);
        Assert.True(double.IsFinite(ranked.TotalScore));
    }

    [Fact]
    public void TopN_AlwaysRanksFullPoolAndSafelyTakesRequestedCount()
    {
        SparrowRankingFeatures[] candidates = Enumerable.Range(1, 25)
            .Select(index => Feature(index.ToString("D2"), amount: index, buyPressure: index))
            .ToArray();

        IReadOnlyList<SparrowRankedCandidate> ranking = _engine.RankClassic(candidates, 1, 5);

        Assert.Equal(25, ranking.Count);
        Assert.Equal(10, ranking.Take(10).Count());
        Assert.Single(ranking.Take(1));
        Assert.Equal(20, ranking.Take(20).Count());
        Assert.Equal(7, _engine.RankClassic(candidates.Take(7), 1, 5).Take(10).Count());
    }

    [Fact]
    public void Ranking_IsExactlyDeterministicAcrossOneHundredInputShuffles()
    {
        SparrowRankingFeatures[] candidates = Enumerable.Range(1, 25)
            .Select(index => Feature(
                index.ToString("D2"),
                rise: 1 + index % 5,
                amount: 100 + index % 7,
                buyPressure: 1 + index % 4,
                momentum: index % 6 / 100.0,
                alpha: index % 3 / 10.0))
            .ToArray();
        IReadOnlyList<SparrowRankedCandidate> expected = _engine.RankV2(candidates, 1, 5, checkAlpha: true);
        var random = new Random(20260908);

        for (int iteration = 0; iteration < 100; iteration++)
        {
            SparrowRankingFeatures[] shuffled = candidates.OrderBy(_ => random.Next()).ToArray();
            IReadOnlyList<SparrowRankedCandidate> actual = _engine.RankV2(shuffled, 1, 5, checkAlpha: true);
            Assert.Equal(expected.Select(Item), actual.Select(Item));
            Assert.Equal(expected.Take(10).Select(candidate => candidate.Code),
                actual.Take(10).Select(candidate => candidate.Code));
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    public void RankingSettings_ClampsTopNToOneThroughTwenty(int requested, int expected)
    {
        var settings = new SparrowRankingSettings { TopN = requested };
        Assert.Equal(expected, settings.TopN);
    }

    [Fact]
    public async Task RankingCsv_ContainsFullPoolVersionedProfileAndTopNMarker()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sparrow-ranking-tests", Guid.NewGuid().ToString("N"));
        try
        {
            IReadOnlyList<SparrowRankedCandidate> classic = _engine.RankClassic(new[]
            {
                Feature("A", amount: 300), Feature("B", amount: 200), Feature("C", amount: 100)
            }, 1, 5);
            IReadOnlyList<SparrowRankedCandidate> v2 = _engine.RankV2(new[]
            {
                Feature("A", momentum: 0.03), Feature("B", momentum: 0.02), Feature("C", momentum: 0.01)
            }, 1, 5, checkAlpha: true);

            string classicPath = await SparrowRankingCsvExporter.ExportClassicAsync(
                classic, 2, CancellationToken.None, directory, new DateTimeOffset(2026, 9, 8, 14, 49, 23, TimeSpan.FromHours(8)));
            string v2Path = await SparrowRankingCsvExporter.ExportV2Async(
                v2, 2, CancellationToken.None, directory, new DateTimeOffset(2026, 9, 8, 14, 49, 24, TimeSpan.FromHours(8)));

            Assert.Equal("sparrow_classic_rank_20260908_144923.csv", Path.GetFileName(classicPath));
            Assert.Equal("sparrow_v2_rank_20260908_144924.csv", Path.GetFileName(v2Path));
            string[] classicLines = await File.ReadAllLinesAsync(classicPath);
            string[] v2Lines = await File.ReadAllLinesAsync(v2Path);
            Assert.Equal(4, classicLines.Length);
            Assert.Equal(4, v2Lines.Length);
            Assert.Contains("RankingProfile,Rank,Code,Name,TotalScore", classicLines[0]);
            Assert.Contains("Momentum,MomentumScore", v2Lines[0]);
            Assert.Contains("AlphaMargin,AlphaStrengthScore", v2Lines[0]);
            Assert.All(classicLines.Skip(1), line => Assert.Contains(SparrowRankingProfileV1.Name, line));
            Assert.EndsWith(",true", classicLines[1]);
            Assert.EndsWith(",true", classicLines[2]);
            Assert.EndsWith(",false", classicLines[3]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CompareTopN_UsesCategorySpecificKeysAndNeverOutputsNeither()
    {
        SparrowComparisonRow[] rows =
        {
            Comparison("B1", SparrowComparisonCategory.Both, classicScore: 90, v2Score: 80),
            Comparison("B2", SparrowComparisonCategory.Both, classicScore: 70, v2Score: 90),
            Comparison("B0", SparrowComparisonCategory.Both, classicScore: 95, v2Score: 80),
            Comparison("C1", SparrowComparisonCategory.ClassicOnly, classicScore: 70),
            Comparison("C2", SparrowComparisonCategory.ClassicOnly, classicScore: 80),
            Comparison("V1", SparrowComparisonCategory.V2Only, v2Score: 60),
            Comparison("V2", SparrowComparisonCategory.V2Only, v2Score: 75),
            Comparison("N1", SparrowComparisonCategory.Neither)
        };

        Assert.Equal(new[] { "B2", "B0" }, SparrowRankingEngine.SelectCompareTopN(
            rows, SparrowComparisonCategory.Both, 2).Select(row => row.Code));
        Assert.Equal(new[] { "C2" }, SparrowRankingEngine.SelectCompareTopN(
            rows, SparrowComparisonCategory.ClassicOnly, 1).Select(row => row.Code));
        Assert.Equal(new[] { "V2" }, SparrowRankingEngine.SelectCompareTopN(
            rows, SparrowComparisonCategory.V2Only, 1).Select(row => row.Code));
        Assert.Empty(SparrowRankingEngine.SelectCompareTopN(
            rows, SparrowComparisonCategory.Neither, 20));
    }

    private static (string Code, int Rank, double Score) Item(SparrowRankedCandidate candidate) =>
        (candidate.Code, candidate.Rank, candidate.TotalScore);

    private static double Rise(IReadOnlyList<SparrowRankedCandidate> ranking, string code) =>
        ranking.Single(candidate => candidate.Code == code)
            .Factor(SparrowRankingFactorNames.RiseQuality).Percentile;

    private static SparrowRankingFeatures Feature(
        string code,
        double? rise = 3,
        double? amount = 100,
        double? buyPressure = 1.5,
        double? momentum = 0.01,
        double? alpha = 0.5) => new()
    {
        Code = code,
        Name = code,
        RisePercent = rise,
        Amount = amount,
        OuterVolume = buyPressure.HasValue ? buyPressure * 100 : null,
        InnerVolume = buyPressure.HasValue ? 100 : null,
        BuyPressureRatio = buyPressure,
        Adhesion = 0.03,
        Turnover = 10,
        Momentum = momentum,
        AlphaMargin = alpha
    };

    private static SparrowComparisonRow Comparison(
        string code,
        SparrowComparisonCategory category,
        double? classicScore = null,
        double? v2Score = null)
    {
        var row = new SparrowComparisonRow { Code = code, Name = code, Category = category };
        row.Classic.Score = classicScore;
        row.V2.Score = v2Score;
        return row;
    }
}
