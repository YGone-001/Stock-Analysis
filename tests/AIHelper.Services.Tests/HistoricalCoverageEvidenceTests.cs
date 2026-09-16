using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Services.StockData.Sparrow;
using System.IO;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class HistoricalCoverageEvidenceTests
{
    [Fact]
    public void FullEvidenceRejectsCapHitAndDefinedMissingRows()
    {
        HistoricalCoverageEvidence invalid = Evidence() with { ResponseCapHit = true };
        Assert.Throws<ArgumentException>(() => Dataset(invalid));
        Assert.Throws<ArgumentException>(() => Dataset(Evidence() with { MissingCount = 1 }));
    }

    [Fact]
    public void SemanticEvidenceAndScopeAreFingerprintedButRetrievedTimestampIsNot()
    {
        HistoricalCoverageEvidence first = Evidence() with { RetrievedAtUtc = new DateTimeOffset(2024, 1, 2, 1, 0, 0, TimeSpan.Zero) };
        HistoricalCoverageEvidence second = first with { RetrievedAtUtc = first.RetrievedAtUtc!.Value.AddDays(5) };
        Assert.Equal(Dataset(first).Fingerprint, Dataset(second).Fingerprint);
        Assert.NotEqual(Dataset(first).Fingerprint, Dataset(first, new HistoricalDatasetScope(HistoricalDatasetScopeKind.ExplicitSymbolSet, Symbols: ["600000.SH"])).Fingerprint);
        Assert.NotEqual(Dataset(first).Fingerprint, Dataset(first with { ProofMethod = "different proof" }).Fingerprint);
    }

    [Fact]
    public void PartialStEvidenceCannotBePromotedToNegativeEvidence()
    {
        HistoricalDatasetQualitySummary partial = new(HistoricalUniverseQuality.Partial, HistoricalLifecycleQuality.Partial,
            HistoricalFieldCoverage.Partial, HistoricalFieldCoverage.Partial, HistoricalFieldCoverage.Partial,
            HistoricalFieldCoverage.None, HistoricalFieldCoverage.Partial, HistoricalFieldCoverage.None);
        HistoricalDatasetQualitySummary full = partial with { HistoricalStCoverage = HistoricalFieldCoverage.Full };
        Assert.NotEqual(HistoricalFieldCoverage.Full, partial.HistoricalStCoverage);
        Assert.Equal(HistoricalFieldCoverage.Full, full.HistoricalStCoverage);
    }

    [Fact]
    public async Task CoverageEvidenceRoundTripsThroughCompatibleSchemaV2Json()
    {
        string path = Path.Combine(Path.GetTempPath(), $"coverage-evidence-{Guid.NewGuid():N}.json");
        try
        {
            HistoricalMarketDataset original = Dataset(Evidence());
            await new HistoricalDatasetJsonWriter().WriteAsync(original, path);
            HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(path);
            Assert.True(loaded.Success);
            Assert.Single(loaded.Dataset!.CoverageEvidence);
            Assert.Equal(original.Fingerprint, loaded.Dataset.Fingerprint);
            Assert.Equal(HistoricalDatasetScopeKind.MarketUniverse, loaded.Dataset.DatasetScope.Kind);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static HistoricalCoverageEvidence Evidence() => new("stock_st", "tushare",
        new HistoricalCoverageScope(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 2), SymbolPartition: "daily"), 1000, false, "3000 points", "one query per trading date", 1,
        [new HistoricalCoverageChunk(new DateOnly(2024, 1, 2), new DateOnly(2024, 1, 2), 2)], 2, false, 0, 0, 0, 0, 0, null, 2, null,
        HistoricalCoverageAcquisitionStatus.Full, "per-trading-date response below documented cap");

    private static HistoricalMarketDataset Dataset(HistoricalCoverageEvidence evidence, HistoricalDatasetScope? scope = null)
    {
        DateOnly day = new(2024, 1, 2);
        QuoteSnapshot quote = new("600000", "Fixture", 10, 9, 11, 100, 1000, 1, null, null, 10, 10, 10);
        return new HistoricalMarketDataset("coverage", [day], [new HistoricalQuoteObservation(day, quote)],
            [new KlineSeries("600000", [new KlineBar(day.ToDateTime(TimeOnly.MinValue), 10, 10, 10, 10, 100, 1000, 0, 0, null)])],
            schemaVersion: 2, source: "tushare", priceAdjustmentMode: "Raw", metadata: new HistoricalDatasetMetadata("coverage", "tushare", null, HistoricalUniverseQuality.Partial),
            securities: [new HistoricalSecurity("600000", "Fixture", HistoricalSecurityType.Stock)],
            universes: [new HistoricalUniverseSnapshot(day, ["600000"], HistoricalUniverseQuality.Partial, "tushare")],
            fieldCapabilities: [
                new(HistoricalField.Price, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
                new(HistoricalField.PreviousClose, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
                new(HistoricalField.ChangePercent, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
                new(HistoricalField.Amount, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
                new(HistoricalField.Turnover, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
                new(HistoricalField.OuterVolume, HistoricalFieldOrigin.Unavailable, HistoricalFieldCoverage.None),
                new(HistoricalField.InnerVolume, HistoricalFieldOrigin.Unavailable, HistoricalFieldCoverage.None)],
            priceSeriesProvenance: [new HistoricalPriceSeriesProvenance("600000", "tushare", HistoricalPriceAdjustmentMode.Raw, day, day)],
            coverageEvidence: [evidence], datasetScope: scope);
    }
}
