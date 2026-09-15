using System.Text.Json;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class HistoricalDatasetJsonLoaderTests
{
    [Fact]
    public async Task LoadAsync_ValidVersionedDataset_LoadsImmutableDataset()
    {
        HistoricalDatasetLoadResult result = await Load(DatasetFile());
        Assert.True(result.Success);
        Assert.NotNull(result.Dataset);
        Assert.Equal("fixture-v1", result.Dataset.DatasetId);
        Assert.Equal(2, result.Dataset.TradingDates.Count);
        Assert.Equal("ForwardAdjusted", result.Dataset.PriceAdjustmentMode);
        Assert.NotEmpty(result.Dataset.Fingerprint);
    }

    [Theory]
    [InlineData(2, "Unsupported")]
    [InlineData(1, "datasetId")]
    public async Task LoadAsync_RejectsSchemaAndMissingIdentity(int version, string expected)
    {
        HistoricalDatasetFile file = DatasetFile();
        if (version == 2) file.SchemaVersion = version; else file.DatasetId = " ";
        HistoricalDatasetLoadResult result = await Load(file);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateAndUnsortedTradingDates()
    {
        HistoricalDatasetFile file = DatasetFile();
        file.TradingDates = new List<DateOnly> { new(2026, 1, 2), new(2026, 1, 1), new(2026, 1, 1) };
        HistoricalDatasetLoadResult result = await Load(file);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("ascending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_RejectsInvalidKlineDateAndOhlc()
    {
        HistoricalDatasetFile file = DatasetFile();
        HistoricalKlineBarFile bar = file.Klines![0].Bars![0];
        bar.Date = new DateTime(2025, 12, 31);
        bar.High = 9;
        bar.Low = 10;
        HistoricalDatasetLoadResult result = await Load(file);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("outside tradingDates", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("High below Low", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_RejectsFingerprintMismatchAndMissingCapabilities()
    {
        HistoricalDatasetFile fingerprintMismatch = DatasetFile(); fingerprintMismatch.Fingerprint = "NOT_THE_DATASET";
        HistoricalDatasetLoadResult mismatch = await Load(fingerprintMismatch);
        Assert.False(mismatch.Success); Assert.Contains(mismatch.Errors, error => error.Contains("fingerprint mismatch", StringComparison.OrdinalIgnoreCase));
        HistoricalDatasetFile noCapabilities = DatasetFile(); noCapabilities.Capabilities = null;
        HistoricalDatasetLoadResult missing = await Load(noCapabilities);
        Assert.False(missing.Success); Assert.Contains(missing.Errors, error => error.Contains("capabilities", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_RespectsCancellation()
    {
        using CancellationTokenSource cts = new(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new HistoricalDatasetJsonLoader().LoadAsync("not-read.json", cts.Token));
    }

    private static HistoricalDatasetFile DatasetFile() => new()
    {
        SchemaVersion = HistoricalDatasetJsonLoader.CurrentSchemaVersion,
        DatasetId = "fixture-v1",
        Source = "Test",
        PriceAdjustmentMode = "ForwardAdjusted",
        TradingDates = new List<DateOnly> { new(2026, 1, 1), new(2026, 1, 2) },
        Capabilities = HistoricalDataCapabilities.Complete,
        Quotes = new List<HistoricalQuoteFile>
        {
            new() { TradingDate = new(2026, 1, 1), Symbol = "600000", Name = "Fixture", Price = 10, PreviousClose = 9.8, Amount = 100000000, Turnover = 10, OuterVolume = 2000, InnerVolume = 1000 },
            new() { TradingDate = new(2026, 1, 2), Symbol = "600000", Name = "Fixture", Price = 10.1, PreviousClose = 10, Amount = 100000000, Turnover = 10, OuterVolume = 2000, InnerVolume = 1000 }
        },
        Klines = new List<HistoricalKlineSeriesFile> { new() { Symbol = "600000", Bars = new List<HistoricalKlineBarFile>
        {
            new() { Date = new DateTime(2026, 1, 1), Open = 9.9, High = 10.1, Low = 9.8, Close = 10, Volume = 1, Amount = 1 },
            new() { Date = new DateTime(2026, 1, 2), Open = 10, High = 10.2, Low = 9.9, Close = 10.1, Volume = 1, Amount = 1 }
        } } }
    };
    private static async Task<HistoricalDatasetLoadResult> Load(HistoricalDatasetFile file)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aihelper-dataset-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file));
            return await new HistoricalDatasetJsonLoader().LoadAsync(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
