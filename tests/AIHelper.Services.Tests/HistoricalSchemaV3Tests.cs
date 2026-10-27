using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>
/// Locks schema-V3 semantics: the security industry row joins the fingerprint only on V3 datasets,
/// V2 datasets round-trip byte-identically, and files written as V2 keep ignoring the industry payload.
/// </summary>
public sealed class HistoricalSchemaV3Tests
{
    [Fact]
    public async Task V3Dataset_RoundTripsWithIndustryAndStableFingerprint()
    {
        HistoricalMarketDataset dataset = Dataset(schemaVersion: 3, industry: "银行");
        string path = TempPath();
        try
        {
            await new HistoricalDatasetJsonWriter().WriteAsync(dataset, path);
            HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(path);
            Assert.True(loaded.Success, string.Join("; ", loaded.Errors));
            Assert.Equal(3, loaded.Dataset!.SchemaVersion);
            Assert.Equal("银行", loaded.Dataset.Securities["600000"].Industry);
            Assert.Equal(dataset.Fingerprint, loaded.Dataset.Fingerprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task V2Dataset_RoundTripsIgnoringTheIndustryPayload()
    {
        HistoricalMarketDataset dataset = Dataset(schemaVersion: 2, industry: "银行");
        string path = TempPath();
        try
        {
            await new HistoricalDatasetJsonWriter().WriteAsync(dataset, path);
            HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(path);
            Assert.True(loaded.Success, string.Join("; ", loaded.Errors));
            Assert.Equal(2, loaded.Dataset!.SchemaVersion);
            // V2 semantics exclude industry entirely: the runtime model stays null and the fingerprint is the V2 preimage.
            Assert.Null(loaded.Dataset.Securities["600000"].Industry);
            Assert.Equal(SparrowHistoricalFingerprint.DatasetV2(dataset), loaded.Dataset.Fingerprint);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DatasetFingerprint_BranchesBySchemaVersionOnTheInstance()
    {
        HistoricalMarketDataset v2 = Dataset(schemaVersion: 2, industry: "银行");
        HistoricalMarketDataset v3 = Dataset(schemaVersion: 3, industry: "银行");
        HistoricalMarketDataset v3NoIndustry = Dataset(schemaVersion: 3, industry: null);

        Assert.Equal(SparrowHistoricalFingerprint.DatasetV2(v2), v2.Fingerprint);
        Assert.Equal(SparrowHistoricalFingerprint.DatasetV3(v3), v3.Fingerprint);
        // Same content except the schema declaration: V3 differs from V2, and a missing industry changes the V3 preimage.
        Assert.NotEqual(v2.Fingerprint, v3.Fingerprint);
        Assert.NotEqual(v3.Fingerprint, v3NoIndustry.Fingerprint);
    }

    private static HistoricalMarketDataset Dataset(int schemaVersion, string? industry)
    {
        DateOnly[] dates = { new(2026, 1, 2), new(2026, 1, 5) };
        HistoricalQuoteObservation[] quotes = dates.Select(date => new HistoricalQuoteObservation(date,
            new QuoteSnapshot("600000", "浦发银行", 10.3, 10, 3, 1500, 100_000_000, 10, 1500, 1000, null, null, null))).ToArray();
        KlineSeries klines = new("600000", dates.Select(date => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 10.3, 1500, null, null, null, null)).ToArray());
        HistoricalSecurity security = new("600000", "浦发银行", HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai,
            dates[0], null, HistoricalLifecycleQuality.Partial, dates[0], industry);
        HistoricalFieldCapability[] fields = Enum.GetValues<HistoricalField>()
            .Select(field => new HistoricalFieldCapability(field, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full))
            .ToArray();
        return new HistoricalMarketDataset("schema-v3-fixture", dates, quotes, new[] { klines },
            securities: new[] { security },
            fieldCapabilities: fields,
            priceSeriesProvenance: new[] { new HistoricalPriceSeriesProvenance("600000", "InMemory", HistoricalPriceAdjustmentMode.Raw, dates[0], dates[^1]) },
            priceAdjustmentMode: "Raw", source: "InMemory", schemaVersion: schemaVersion);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"schema-v3-{Guid.NewGuid():N}.json");
}
