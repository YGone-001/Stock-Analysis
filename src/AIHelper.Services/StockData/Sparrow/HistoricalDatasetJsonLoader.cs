using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Loads schema-versioned historical research input. It deliberately does not accept replay/result exports.</summary>
public sealed class HistoricalDatasetJsonLoader : IHistoricalDatasetLoader
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<HistoricalDatasetLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return HistoricalDatasetLoadResult.Failed("A historical dataset file path is required.");
        try
        {
            await using FileStream stream = File.OpenRead(path);
            HistoricalDatasetFile? file = await JsonSerializer.DeserializeAsync<HistoricalDatasetFile>(stream, JsonOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null) return HistoricalDatasetLoadResult.Failed("Invalid dataset file: JSON document is empty.");
            List<string> errors = Validate(file);
            if (errors.Count > 0) return new HistoricalDatasetLoadResult(false, null, errors, Array.Empty<string>());

            HistoricalMarketDataset dataset = Build(file);
            if (!string.IsNullOrWhiteSpace(file.Fingerprint) && !string.Equals(file.Fingerprint, dataset.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return HistoricalDatasetLoadResult.Failed("Dataset fingerprint mismatch. The file contents do not match its declared fingerprint.");
            List<string> warnings = new();
            if (string.Equals(dataset.PriceAdjustmentMode, "Unknown", StringComparison.OrdinalIgnoreCase))
                warnings.Add("Price adjustment mode is Unknown; corporate-action comparability may be limited.");
            return HistoricalDatasetLoadResult.Loaded(dataset, warnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { return HistoricalDatasetLoadResult.Failed($"Invalid dataset schema: {ex.Message}"); }
        catch (IOException ex) { return HistoricalDatasetLoadResult.Failed($"Unable to read historical dataset: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return HistoricalDatasetLoadResult.Failed($"Unable to access historical dataset: {ex.Message}"); }
        catch (Exception ex) { return HistoricalDatasetLoadResult.Failed($"Unexpected dataset load failure: {ex.Message}"); }
    }

    private static List<string> Validate(HistoricalDatasetFile file)
    {
        List<string> errors = new();
        if (file.SchemaVersion != CurrentSchemaVersion) errors.Add($"Unsupported historical dataset schema version '{file.SchemaVersion}'. Supported version: {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(file.DatasetId)) errors.Add("Historical dataset requires a non-empty datasetId.");
        if (file.TradingDates is not { Count: > 0 }) errors.Add("Historical dataset requires tradingDates.");
        else
        {
            for (int i = 1; i < file.TradingDates.Count; i++)
                if (file.TradingDates[i - 1] >= file.TradingDates[i]) { errors.Add("Trading dates must be strictly ascending with no duplicates."); break; }
        }
        if (file.Capabilities is null) errors.Add("Historical dataset requires an explicit capabilities declaration.");
        if (file.Quotes is null) errors.Add("Historical dataset requires a quotes collection.");
        if (file.Klines is null) errors.Add("Historical dataset requires a klines collection.");
        if (errors.Count > 0) return errors;

        HashSet<DateOnly> dates = file.TradingDates!.ToHashSet();
        HashSet<(DateOnly Date, string Symbol)> quoteKeys = new();
        foreach (HistoricalQuoteFile quote in file.Quotes!)
        {
            if (!dates.Contains(quote.TradingDate)) errors.Add($"Quote '{quote.Symbol}' has a date outside tradingDates.");
            if (string.IsNullOrWhiteSpace(quote.Symbol)) errors.Add("Quotes must provide a symbol.");
            else if (!quoteKeys.Add((quote.TradingDate, quote.Symbol))) errors.Add($"Duplicate quote observation for '{quote.Symbol}' on {quote.TradingDate:yyyy-MM-dd}.");
            ValidateNonNegative(quote.Volume, "Quote volume", errors);
            ValidateNonNegative(quote.Amount, "Quote amount", errors);
            ValidateNonNegative(quote.Turnover, "Quote turnover", errors);
            ValidateNonNegative(quote.OuterVolume, "Quote outerVolume", errors);
            ValidateNonNegative(quote.InnerVolume, "Quote innerVolume", errors);
        }
        HashSet<string> symbols = new(StringComparer.Ordinal);
        foreach (HistoricalKlineSeriesFile series in file.Klines!)
        {
            if (string.IsNullOrWhiteSpace(series.Symbol)) { errors.Add("K-line series must provide a symbol."); continue; }
            if (!symbols.Add(series.Symbol)) errors.Add($"Duplicate K-line series for '{series.Symbol}'.");
            if (series.Bars is null || series.Bars.Count == 0) { errors.Add($"K-line series '{series.Symbol}' requires bars."); continue; }
            DateOnly? previous = null;
            foreach (HistoricalKlineBarFile bar in series.Bars)
            {
                DateOnly date = DateOnly.FromDateTime(bar.Date);
                if (bar.Date.TimeOfDay != TimeSpan.Zero) errors.Add($"K-line '{series.Symbol}' has a non-date timestamp.");
                if (!dates.Contains(date)) errors.Add($"K-line '{series.Symbol}' has a date outside tradingDates.");
                if (previous.HasValue && previous.Value >= date) errors.Add($"K-line dates for '{series.Symbol}' must be strictly ascending with no duplicates.");
                previous = date;
                ValidateBar(series.Symbol, bar, errors);
            }
        }
        foreach (HistoricalMarketContextFile context in file.MarketContexts ?? Enumerable.Empty<HistoricalMarketContextFile>())
            if (!dates.Contains(context.TradingDate)) errors.Add($"Market context has a date outside tradingDates: {context.TradingDate:yyyy-MM-dd}.");
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ValidateBar(string symbol, HistoricalKlineBarFile bar, List<string> errors)
    {
        ValidateNonNegative(bar.Volume, $"K-line volume for '{symbol}'", errors);
        ValidateNonNegative(bar.Amount, $"K-line amount for '{symbol}'", errors);
        double? high = bar.High, low = bar.Low;
        if (high.HasValue && low.HasValue && high < low) errors.Add($"K-line '{symbol}' has High below Low on {bar.Date:yyyy-MM-dd}.");
        foreach (double? value in new[] { bar.Open, bar.Close })
        {
            if (high.HasValue && value.HasValue && high < value) errors.Add($"K-line '{symbol}' has High below Open/Close on {bar.Date:yyyy-MM-dd}.");
            if (low.HasValue && value.HasValue && low > value) errors.Add($"K-line '{symbol}' has Low above Open/Close on {bar.Date:yyyy-MM-dd}.");
        }
    }

    private static void ValidateNonNegative(double? value, string label, List<string> errors)
    {
        if (value is < 0) errors.Add($"{label} cannot be negative.");
    }

    private static HistoricalMarketDataset Build(HistoricalDatasetFile file)
    {
        HistoricalQuoteObservation[] quotes = file.Quotes!.Select(q => new HistoricalQuoteObservation(q.TradingDate,
            new QuoteSnapshot(q.Symbol, q.Name ?? q.Symbol, q.Price, q.PreviousClose, q.ChangePercent, q.Volume, q.Amount, q.Turnover, q.OuterVolume, q.InnerVolume, q.Open, q.High, q.Low))).ToArray();
        KlineSeries[] klines = file.Klines!.Select(series => new KlineSeries(series.Symbol, series.Bars!.Select(bar =>
            new KlineBar(bar.Date, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.Amount, bar.ChangePercent, bar.Change, bar.TurnoverRate)).ToArray())).ToArray();
        HistoricalMarketContext[] contexts = (file.MarketContexts ?? Enumerable.Empty<HistoricalMarketContextFile>()).Select(context => new HistoricalMarketContext(
            context.TradingDate,
            context.ClassicMarketRegime is null ? null : new SparrowMarketRegime { Shanghai = context.ClassicMarketRegime.Shanghai, Csi1000 = context.ClassicMarketRegime.Csi1000, Defensive = context.ClassicMarketRegime.Defensive, Reason = context.ClassicMarketRegime.Reason ?? string.Empty },
            context.V2ShanghaiDailyPercent)).ToArray();
        return new HistoricalMarketDataset(file.DatasetId!, file.TradingDates!, quotes, klines, contexts, file.Capabilities!, string.IsNullOrWhiteSpace(file.PriceAdjustmentMode) ? "Unknown" : file.PriceAdjustmentMode, string.IsNullOrWhiteSpace(file.Source) ? "Unknown" : file.Source);
    }
}

// File DTOs intentionally remain separate from immutable runtime models and exported replay/backtest results.
public sealed class HistoricalDatasetFile
{
    public int SchemaVersion { get; set; }
    public string? DatasetId { get; set; }
    public string? Fingerprint { get; set; }
    public string? Source { get; set; }
    public string? PriceAdjustmentMode { get; set; }
    public List<DateOnly>? TradingDates { get; set; }
    public HistoricalDataCapabilities? Capabilities { get; set; }
    public List<HistoricalQuoteFile>? Quotes { get; set; }
    public List<HistoricalKlineSeriesFile>? Klines { get; set; }
    public List<HistoricalMarketContextFile>? MarketContexts { get; set; }
}
public sealed class HistoricalQuoteFile
{
    public DateOnly TradingDate { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string? Name { get; set; }
    public double? Price { get; set; }
    public double? PreviousClose { get; set; }
    public double? ChangePercent { get; set; }
    public double? Volume { get; set; }
    public double? Amount { get; set; }
    public double? Turnover { get; set; }
    public double? OuterVolume { get; set; }
    public double? InnerVolume { get; set; }
    public double? Open { get; set; }
    public double? High { get; set; }
    public double? Low { get; set; }
}
public sealed class HistoricalKlineSeriesFile { public string Symbol { get; set; } = string.Empty; public List<HistoricalKlineBarFile>? Bars { get; set; } }
public sealed class HistoricalKlineBarFile
{
    public DateTime Date { get; set; }
    public double? Open { get; set; }
    public double? High { get; set; }
    public double? Low { get; set; }
    public double? Close { get; set; }
    public double? Volume { get; set; }
    public double? Amount { get; set; }
    public double? ChangePercent { get; set; }
    public double? Change { get; set; }
    public double? TurnoverRate { get; set; }
}
public sealed class HistoricalMarketContextFile
{
    public DateOnly TradingDate { get; set; }
    public HistoricalClassicMarketRegimeFile? ClassicMarketRegime { get; set; }
    public double? V2ShanghaiDailyPercent { get; set; }
}
public sealed class HistoricalClassicMarketRegimeFile
{
    public SparrowMarketState Shanghai { get; set; }
    public SparrowMarketState Csi1000 { get; set; }
    public bool Defensive { get; set; }
    public string? Reason { get; set; }
}
