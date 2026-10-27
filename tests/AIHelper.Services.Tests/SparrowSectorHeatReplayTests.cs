using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>
/// Locks the historical sector-heat pipeline: the pure aggregation formula, the schema-version gate
/// (V3 replays rank with heat, V2 replays stay bit-identical), and the ranking effect.
/// </summary>
public sealed class SparrowSectorHeatReplayTests
{
    [Fact]
    public void Compute_RanksIndustriesByMeanDailyRise()
    {
        // 银行 mean 1.0 (two members), 钢铁 mean -2.0: percentiles over {-2, 1} -> 银行 100, 钢铁 0.
        HistoricalMarketSnapshot snapshot = Snapshot(new[]
        {
            ("600000", (string?)"银行", 1.5), ("600001", (string?)"银行", 0.5), ("600002", (string?)"钢铁", -2.0)
        });

        SparrowSectorHeatResult result = SparrowSectorHeatAggregator.Compute(snapshot);

        Assert.True(result.Available);
        Assert.Equal(100d, result.HeatPercentileBySymbol["600000"], 10);
        Assert.Equal(100d, result.HeatPercentileBySymbol["600001"], 10);
        Assert.Equal(0d, result.HeatPercentileBySymbol["600002"], 10);
    }

    [Fact]
    public void Compute_SkipsMissingIndustriesAndNonFiniteRises_AndIsUnavailableWithoutGroups()
    {
        HistoricalMarketSnapshot snapshot = Snapshot(new[]
        {
            ("600000", (string?)null, 5.0), ("600001", (string?)"银行", double.NaN), ("600002", (string?)"钢铁", 1.0)
        });

        SparrowSectorHeatResult result = SparrowSectorHeatAggregator.Compute(snapshot);

        Assert.True(result.Available);
        Assert.Single(result.HeatPercentileBySymbol);
        Assert.Equal(100d, result.HeatPercentileBySymbol["600002"], 10);

        SparrowSectorHeatResult empty = SparrowSectorHeatAggregator.Compute(Snapshot(new[] { ("600000", (string?)null, 5.0) }));
        Assert.False(empty.Available);
    }

    [Fact]
    public void Replay_OnV3Dataset_RanksWithSectorHeat_OnV2Dataset_ItStaysIdenticalToThePreHeatBehavior()
    {
        DateOnly date = V3Dataset().TradingDates[65];
        SparrowReplayRequest request = V2Request(date);

        // The V2 dataset result must equal the frozen pre-heat behavior (the schema gate leaves it untouched).
        HistoricalMarketDataset v2Dataset = V2Dataset();
        SparrowReplayResult fromV2 = new SparrowHistoricalReplayEngine().Replay(v2Dataset, request);
        Assert.NotEmpty(fromV2.Selections);
        Assert.All(fromV2.Selections, item => Assert.Equal(SparrowRankingProfileV1.Name, item.RankedCandidate.RankingProfile));

        // The identical request on the V3 dataset (only industries differ) engages the Rank-V2 profile.
        SparrowReplayResult fromV3 = new SparrowHistoricalReplayEngine().Replay(V3Dataset(), request);
        Assert.All(fromV3.Selections, item => Assert.Equal(SparrowRankingProfileV2.Name, item.RankedCandidate.RankingProfile));
        Assert.All(fromV3.Selections, item => Assert.Contains(item.RankedCandidate.Factors, factor => factor.Factor == SparrowRankingFactorNames.SectorHeat));
    }

    [Fact]
    public void Replay_RequestFingerprint_IsStableAcrossSchemaVersions()
    {
        // The heat gate keys off the dataset, not the request: the parameter fingerprint is identical,
        // so artifacts remain comparable across datasets while results are bound by the dataset fingerprint.
        SparrowReplayRequest request = V2Request(new DateOnly(2026, 3, 10));
        SparrowHistoricalFingerprint.Parameters(request);
        Assert.Equal(SparrowHistoricalFingerprint.Parameters(request), SparrowHistoricalFingerprint.Parameters(V2Request(new DateOnly(2026, 3, 10))));
    }

    private static SparrowReplayRequest V2Request(DateOnly date) => new(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, date, 2,
        V2Parameters: new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false));

    private static HistoricalMarketDataset V2Dataset() => BuildDataset(schemaVersion: 2);
    private static HistoricalMarketDataset V3Dataset() => BuildDataset(schemaVersion: 3);

    private static HistoricalMarketDataset BuildDataset(int schemaVersion)
    {
        DateOnly start = new(2026, 1, 1); DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        // 600000 rides the hot 银行 industry, 600001 the cold 钢铁 industry; closes rise linearly for both.
        string[] codes = { "600000", "600001" };
        string?[] industries = { "银行", "钢铁" };
        KlineSeries[] klines = codes.Select(code => new KlineSeries(code, dates.Select((date, i) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + i * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => codes.Select((code, index) => new HistoricalQuoteObservation(date,
            new QuoteSnapshot(code, code, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        HistoricalSecurity[] securities = codes.Select((code, index) => new HistoricalSecurity(code, code, HistoricalSecurityType.Stock,
            HistoricalSecurityMarket.Shanghai, dates[0], null, HistoricalLifecycleQuality.Partial, dates[0], industries[index])).ToArray();
        HistoricalUniverseSnapshot[] universes = dates.Select(date => new HistoricalUniverseSnapshot(date, codes, HistoricalUniverseQuality.Partial, "fixture")).ToArray();
        HistoricalFieldCapability[] fields = Enum.GetValues<HistoricalField>()
            .Select(field => new HistoricalFieldCapability(field, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full))
            .ToArray();
        HistoricalMarketContext[] context = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, 0.5)).ToArray();
        return new HistoricalMarketDataset("sector-heat-fixture", dates, quotes, klines, context,
            priceAdjustmentMode: "Raw", source: "InMemory", schemaVersion: schemaVersion,
            securities: securities, universes: universes, fieldCapabilities: fields);
    }

    private static HistoricalMarketSnapshot Snapshot((string Code, string? Industry, double Rise)[] rows)
    {
        List<HistoricalSecuritySnapshot> securities = new();
        foreach ((string code, string? industry, double rise) in rows)
        {
            HistoricalSecurity security = new(code, code, HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai, null, null, HistoricalLifecycleQuality.Partial, null, industry);
            QuoteSnapshot quote = new(code, code, 10.3, 10, rise, 1500, 100_000_000, 10, 1500, 1000, null, null, null);
            securities.Add(new HistoricalSecuritySnapshot(security, quote, new KlineSeries(code, Array.Empty<KlineBar>())));
        }
        return new HistoricalMarketSnapshot(new DateOnly(2026, 3, 10), securities, null, HistoricalReplaySupport.Supported, Array.Empty<string>(), null, Array.Empty<HistoricalSecurityObservation>());
    }
}
