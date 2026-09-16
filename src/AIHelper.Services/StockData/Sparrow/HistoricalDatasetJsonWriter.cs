using System.Text.Json;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Schema-V2 writer with a verified temporary file; timestamps are exported but not semantic fingerprint input.</summary>
public sealed class HistoricalDatasetJsonWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly IHistoricalDatasetLoader _loader;

    public HistoricalDatasetJsonWriter(IHistoricalDatasetLoader? loader = null) => _loader = loader ?? new HistoricalDatasetJsonLoader();

    public async Task WriteAsync(HistoricalMarketDataset dataset, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            HistoricalDatasetFile file = ToFile(dataset);
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, file, Json, cancellationToken).ConfigureAwait(false);
            HistoricalDatasetLoadResult check = await _loader.LoadAsync(temporary, cancellationToken).ConfigureAwait(false);
            if (!check.Success || check.Dataset is null || !string.Equals(check.Dataset.Fingerprint, dataset.Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Historical dataset export failed loader/fingerprint verification.");
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static HistoricalDatasetFile ToFile(HistoricalMarketDataset dataset) => new()
    {
        SchemaVersion = HistoricalDatasetJsonLoader.CurrentSchemaVersion,
        DatasetId = dataset.DatasetId,
        Fingerprint = dataset.Fingerprint,
        Source = dataset.Source,
        PriceAdjustmentMode = dataset.PriceAdjustmentMode.ToString(),
        TradingDates = dataset.TradingDates.ToList(),
        Capabilities = dataset.Capabilities,
        Quotes = dataset.Quotes.OrderBy(item => item.Key.Date).ThenBy(item => item.Key.Symbol, StringComparer.Ordinal).Select(item =>
        {
            QuoteSnapshot quote = item.Value;
            return new HistoricalQuoteFile { TradingDate = item.Key.Date, Symbol = quote.Symbol, Name = quote.Name, Price = quote.Price, PreviousClose = quote.PreviousClose,
                ChangePercent = quote.ChangePercent, Volume = quote.Volume, Amount = quote.Amount, Turnover = quote.Turnover, OuterVolume = quote.OuterVolume,
                InnerVolume = quote.InnerVolume, Open = quote.Open, High = quote.High, Low = quote.Low };
        }).ToList(),
        Klines = dataset.Klines.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new HistoricalKlineSeriesFile { Symbol = item.Key,
            Bars = item.Value.Bars.Select(bar => new HistoricalKlineBarFile { Date = bar.Date, Open = bar.Open, High = bar.High, Low = bar.Low, Close = bar.Close,
                Volume = bar.Volume, Amount = bar.Amount, ChangePercent = bar.ChangePercent, Change = bar.Change, TurnoverRate = bar.TurnoverRate }).ToList() }).ToList(),
        MarketContexts = dataset.MarketContexts.OrderBy(item => item.Key).Select(item => new HistoricalMarketContextFile { TradingDate = item.Key, V2ShanghaiDailyPercent = item.Value.V2ShanghaiDailyPercent }).ToList(),
        Metadata = new HistoricalDatasetMetadataFile { DatasetId = dataset.Metadata.DatasetId, Source = dataset.Metadata.Source, CreatedAt = dataset.Metadata.CreatedAt,
            UniverseQuality = dataset.Metadata.UniverseQuality, Warnings = dataset.Metadata.Warnings.ToList() },
        Securities = dataset.Securities.Values.OrderBy(item => item.Symbol, StringComparer.Ordinal).Select(item => new HistoricalSecurityFile { Symbol = item.Symbol, Name = item.Name,
            SecurityType = item.SecurityType, Market = item.Market, ListingDate = item.ListingDate, DelistingEffectiveDate = item.DelistingEffectiveDate,
            LifecycleQuality = item.LifecycleQuality, ObservedHistoryStart = item.ObservedHistoryStart }).ToList(),
        Universes = dataset.Universes.Values.OrderBy(item => item.TradingDate).Select(item => new HistoricalUniverseSnapshotFile { TradingDate = item.TradingDate,
            SecuritySymbols = item.SecuritySymbols.ToList(), Quality = item.Quality, Source = item.Source, Warnings = item.Warnings.ToList() }).ToList(),
        FieldCapabilities = dataset.FieldCapabilities.Values.OrderBy(item => item.Field).ToList(),
        PriceSeriesProvenance = dataset.PriceSeriesProvenance.Values.OrderBy(item => item.Symbol, StringComparer.Ordinal).ToList(),
        MarketContextProvenance = dataset.MarketContextProvenance.Values.OrderBy(item => item.TradingDate).ToList(),
        ObservationDeclarations = dataset.ObservationDeclarations.Values.OrderBy(item => item.TradingDate).ThenBy(item => item.Symbol, StringComparer.Ordinal).ToList(),
        RiskStatusObservations = dataset.RiskStatusObservations.Values.OrderBy(item => item.TradingDate).ThenBy(item => item.Symbol, StringComparer.Ordinal).ToList(),
        AdjustmentFactors = dataset.AdjustmentFactors.Values.OrderBy(item => item.TradingDate).ThenBy(item => item.Symbol, StringComparer.Ordinal).ToList(),
        QualitySummary = dataset.QualitySummary
    };
}
