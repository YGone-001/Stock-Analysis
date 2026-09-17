using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Loads schema-versioned historical research input. It deliberately does not accept replay/result exports.</summary>
public sealed class HistoricalDatasetJsonLoader : IHistoricalDatasetLoader
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
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
            if (dataset.PriceAdjustmentMode == HistoricalPriceAdjustmentMode.Unknown)
                warnings.Add("Price adjustment mode is Unknown; corporate-action comparability may be limited.");
            if (dataset.Metadata.UniverseQuality != HistoricalUniverseQuality.Complete)
                warnings.Add("Historical universe is partial; survivorship bias may remain.");
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
        if (file.SchemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion) errors.Add($"Unsupported historical dataset schema version '{file.SchemaVersion}'. Supported versions: {LegacySchemaVersion}, {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(file.DatasetId)) errors.Add("Historical dataset requires a non-empty datasetId.");
        if (file.TradingDates is not { Count: > 0 }) errors.Add("Historical dataset requires tradingDates.");
        else
        {
            for (int i = 1; i < file.TradingDates.Count; i++)
                if (file.TradingDates[i - 1] >= file.TradingDates[i]) { errors.Add("Trading dates must be strictly ascending with no duplicates."); break; }
        }
        if (file.SchemaVersion == LegacySchemaVersion && file.Capabilities is null) errors.Add("Historical dataset requires an explicit capabilities declaration.");
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
        ValidateBenchmarks(file.Benchmarks, dates, errors);
        if (file.SchemaVersion == CurrentSchemaVersion) ValidateV2(file, dates, errors);
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ValidateV2(HistoricalDatasetFile file, HashSet<DateOnly> dates, List<string> errors)
    {
        if (file.Metadata is null) errors.Add("Schema V2 requires metadata.");
        else if (!string.Equals(file.Metadata.DatasetId, file.DatasetId, StringComparison.Ordinal)) errors.Add("Schema V2 metadata.datasetId must match datasetId.");
        if (file.Securities is not { Count: > 0 }) { errors.Add("Schema V2 requires securities."); return; }
        if (file.Universes is null) errors.Add("Schema V2 requires universes.");
        if (file.FieldCapabilities is null) errors.Add("Schema V2 requires fieldCapabilities.");
        if (file.PriceSeriesProvenance is null) errors.Add("Schema V2 requires priceSeriesProvenance.");
        if (file.MarketContextProvenance is null) errors.Add("Schema V2 requires marketContextProvenance.");
        if (errors.Count > 0) return;

        HashSet<string> securitySymbols = new(StringComparer.Ordinal);
        foreach (HistoricalSecurityFile security in file.Securities)
        {
            if (string.IsNullOrWhiteSpace(security.Symbol)) { errors.Add("Historical security requires a symbol."); continue; }
            if (!securitySymbols.Add(security.Symbol)) errors.Add($"Duplicate historical security '{security.Symbol}'.");
            if (!Enum.IsDefined(security.SecurityType) || !Enum.IsDefined(security.Market) || !Enum.IsDefined(security.LifecycleQuality)) errors.Add($"Historical security '{security.Symbol}' has an invalid enum value.");
            if (security.DelistingEffectiveDate.HasValue && security.ListingDate.HasValue && security.DelistingEffectiveDate <= security.ListingDate)
                errors.Add($"Historical security '{security.Symbol}' has an invalid lifecycle range.");
        }
        foreach (HistoricalQuoteFile quote in file.Quotes!)
        {
            if (!securitySymbols.Contains(quote.Symbol)) errors.Add($"Quote '{quote.Symbol}' does not reference a historical security.");
            HistoricalSecurityFile? security = file.Securities.FirstOrDefault(item => string.Equals(item.Symbol, quote.Symbol, StringComparison.Ordinal));
            if (security is not null && !string.IsNullOrWhiteSpace(quote.Name) && !string.Equals(security.Name, quote.Name, StringComparison.Ordinal)) errors.Add($"Quote name for '{quote.Symbol}' does not match security master.");
        }
        foreach (HistoricalKlineSeriesFile series in file.Klines!)
            if (!securitySymbols.Contains(series.Symbol)) errors.Add($"K-line '{series.Symbol}' does not reference a historical security.");

        Dictionary<DateOnly, HistoricalUniverseSnapshotFile> universes = new();
        foreach (HistoricalUniverseSnapshotFile universe in file.Universes!)
        {
            if (!dates.Contains(universe.TradingDate)) errors.Add($"Universe has a date outside tradingDates: {universe.TradingDate:yyyy-MM-dd}.");
            else if (!universes.TryAdd(universe.TradingDate, universe)) errors.Add($"Duplicate universe for {universe.TradingDate:yyyy-MM-dd}.");
            if (!Enum.IsDefined(universe.Quality)) errors.Add($"Universe '{universe.TradingDate:yyyy-MM-dd}' has an invalid quality value.");
            foreach (string symbol in universe.SecuritySymbols ?? Enumerable.Empty<string>())
                if (!securitySymbols.Contains(symbol)) errors.Add($"Universe '{universe.TradingDate:yyyy-MM-dd}' references unknown security '{symbol}'.");
        }
        foreach (DateOnly date in dates) if (!universes.ContainsKey(date)) errors.Add($"Schema V2 requires an explicit universe for {date:yyyy-MM-dd}.");

        Dictionary<HistoricalField, HistoricalFieldCapability> capabilities = new();
        foreach (HistoricalFieldCapability capability in file.FieldCapabilities!)
        {
            if (!Enum.IsDefined(capability.Field) || !Enum.IsDefined(capability.Origin) || !Enum.IsDefined(capability.Coverage) || !Enum.IsDefined(capability.Unit)) errors.Add("A field capability has an invalid enum value.");
            if (!capabilities.TryAdd(capability.Field, capability)) errors.Add($"Duplicate field capability '{capability.Field}'.");
            if (capability.Origin == HistoricalFieldOrigin.Unavailable && capability.Coverage != HistoricalFieldCoverage.None)
                errors.Add($"Unavailable field '{capability.Field}' must declare None coverage.");
            if (capability.Coverage == HistoricalFieldCoverage.None && capability.Origin != HistoricalFieldOrigin.Unavailable)
                errors.Add($"None coverage field '{capability.Field}' must declare Unavailable origin.");
        }
        foreach (HistoricalField field in Enum.GetValues<HistoricalField>())
            if (!capabilities.ContainsKey(field)) errors.Add($"Schema V2 requires field capability '{field}'.");
        foreach (HistoricalFieldCapability capability in capabilities.Values.Where(item => item.Coverage == HistoricalFieldCoverage.Full))
            if (file.Quotes!.Any(quote => !HasQuoteField(quote, capability.Field))) errors.Add($"Field '{capability.Field}' declares Full coverage but one or more quote observations have no value.");

        HashSet<string> klineSymbols = file.Klines!.Select(series => series.Symbol).ToHashSet(StringComparer.Ordinal);
        HashSet<string> provenanceSymbols = new(StringComparer.Ordinal);
        foreach (HistoricalPriceSeriesProvenance provenance in file.PriceSeriesProvenance!)
        {
            if (!provenanceSymbols.Add(provenance.Symbol)) errors.Add($"Duplicate price-series provenance for '{provenance.Symbol}' indicates unsupported mixed adjustment modes.");
            if (!Enum.IsDefined(provenance.AdjustmentMode)) errors.Add($"Price-series provenance '{provenance.Symbol}' has an invalid adjustment mode.");
            if (!klineSymbols.Contains(provenance.Symbol)) errors.Add($"Price-series provenance '{provenance.Symbol}' has no K-line series.");
            if (provenance.ObservationEnd.HasValue && provenance.ObservationStart.HasValue && provenance.ObservationEnd < provenance.ObservationStart) errors.Add($"Price-series provenance '{provenance.Symbol}' has an invalid observation range.");
        }
        foreach (string symbol in klineSymbols) if (!provenanceSymbols.Contains(symbol)) errors.Add($"Schema V2 requires price-series provenance for '{symbol}'.");

        HashSet<DateOnly> contextDates = (file.MarketContexts ?? Enumerable.Empty<HistoricalMarketContextFile>()).Select(context => context.TradingDate).ToHashSet();
        HashSet<DateOnly> provenanceDates = new();
        foreach (HistoricalMarketContextProvenance provenance in file.MarketContextProvenance!)
        {
            if (!dates.Contains(provenance.TradingDate)) errors.Add($"Market-context provenance has a date outside tradingDates: {provenance.TradingDate:yyyy-MM-dd}.");
            if (!provenanceDates.Add(provenance.TradingDate)) errors.Add($"Duplicate market-context provenance for {provenance.TradingDate:yyyy-MM-dd}.");
        }
        foreach (DateOnly date in contextDates) if (!provenanceDates.Contains(date)) errors.Add($"Market context {date:yyyy-MM-dd} requires provenance.");
        HashSet<(DateOnly Date, string Symbol)> declarationKeys = new();
        foreach (HistoricalObservationDeclaration declaration in file.ObservationDeclarations ?? Enumerable.Empty<HistoricalObservationDeclaration>())
        {
            if (!dates.Contains(declaration.TradingDate)) errors.Add($"Observation declaration has a date outside tradingDates: {declaration.TradingDate:yyyy-MM-dd}.");
            if (!securitySymbols.Contains(declaration.Symbol)) errors.Add($"Observation declaration references unknown security '{declaration.Symbol}'.");
            if (!declarationKeys.Add((declaration.TradingDate, declaration.Symbol))) errors.Add($"Duplicate observation declaration for '{declaration.Symbol}' on {declaration.TradingDate:yyyy-MM-dd}.");
        }
        HashSet<(DateOnly Date, string Symbol)> riskKeys = new();
        foreach (HistoricalRiskStatusObservation observation in file.RiskStatusObservations ?? Enumerable.Empty<HistoricalRiskStatusObservation>())
        {
            if (!dates.Contains(observation.TradingDate)) errors.Add($"Historical ST observation has a date outside tradingDates: {observation.TradingDate:yyyy-MM-dd}.");
            if (!securitySymbols.Contains(observation.Symbol)) errors.Add($"Historical ST observation references unknown security '{observation.Symbol}'.");
            if (!riskKeys.Add((observation.TradingDate, observation.Symbol))) errors.Add($"Duplicate historical ST observation for '{observation.Symbol}' on {observation.TradingDate:yyyy-MM-dd}.");
        }
        HashSet<(DateOnly Date, string Symbol)> factorKeys = new();
        foreach (HistoricalAdjustmentFactor factor in file.AdjustmentFactors ?? Enumerable.Empty<HistoricalAdjustmentFactor>())
        {
            if (!dates.Contains(factor.TradingDate)) errors.Add($"Adjustment factor has a date outside tradingDates: {factor.TradingDate:yyyy-MM-dd}.");
            if (!securitySymbols.Contains(factor.Symbol)) errors.Add($"Adjustment factor references unknown security '{factor.Symbol}'.");
            if (!factorKeys.Add((factor.TradingDate, factor.Symbol))) errors.Add($"Duplicate adjustment factor for '{factor.Symbol}' on {factor.TradingDate:yyyy-MM-dd}.");
            if (double.IsNaN(factor.Factor) || double.IsInfinity(factor.Factor) || factor.Factor <= 0) errors.Add($"Adjustment factor for '{factor.Symbol}' must be positive and finite.");
        }
    }

    private static void ValidateBenchmarks(IEnumerable<HistoricalBenchmarkSeriesFile>? benchmarks, HashSet<DateOnly> dates, List<string> errors)
    {
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (HistoricalBenchmarkSeriesFile benchmark in benchmarks ?? Enumerable.Empty<HistoricalBenchmarkSeriesFile>())
        {
            if (string.IsNullOrWhiteSpace(benchmark.BenchmarkId)) { errors.Add("Benchmark requires a non-empty benchmarkId."); continue; }
            if (!identifiers.Add(benchmark.BenchmarkId)) errors.Add($"Duplicate benchmark '{benchmark.BenchmarkId}'.");
            if (!Enum.IsDefined(benchmark.PriceBasis) || !Enum.IsDefined(benchmark.Coverage)) errors.Add($"Benchmark '{benchmark.BenchmarkId}' has an invalid price basis or coverage.");
            if (string.IsNullOrWhiteSpace(benchmark.Source)) errors.Add($"Benchmark '{benchmark.BenchmarkId}' requires source.");
            if (benchmark.Provenance is null) errors.Add($"Benchmark '{benchmark.BenchmarkId}' requires provenance.");
            else
            {
                try { benchmark.Provenance.Validate(); }
                catch (ArgumentException exception) { errors.Add($"Benchmark '{benchmark.BenchmarkId}' has invalid provenance: {exception.Message}"); }
            }
            HashSet<DateOnly> observationDates = new();
            foreach (HistoricalBenchmarkObservationFile observation in benchmark.Observations ?? Enumerable.Empty<HistoricalBenchmarkObservationFile>())
            {
                if (!dates.Contains(observation.TradingDate)) errors.Add($"Benchmark '{benchmark.BenchmarkId}' has a date outside tradingDates: {observation.TradingDate:yyyy-MM-dd}.");
                if (!observationDates.Add(observation.TradingDate)) errors.Add($"Benchmark '{benchmark.BenchmarkId}' has duplicate date {observation.TradingDate:yyyy-MM-dd}.");
                if (!double.IsFinite(observation.Close) || observation.Close <= 0) errors.Add($"Benchmark '{benchmark.BenchmarkId}' close must be finite and positive.");
            }
            if (benchmark.Coverage == HistoricalFieldCoverage.Full && !observationDates.SetEquals(dates))
                errors.Add($"Benchmark '{benchmark.BenchmarkId}' declares Full coverage but does not cover exactly the dataset trading dates.");
        }
    }

    private static bool HasQuoteField(HistoricalQuoteFile quote, HistoricalField field) => field switch
    {
        HistoricalField.Price => quote.Price.HasValue,
        HistoricalField.PreviousClose => quote.PreviousClose.HasValue,
        HistoricalField.ChangePercent => quote.ChangePercent.HasValue,
        HistoricalField.Amount => quote.Amount.HasValue,
        HistoricalField.Turnover => quote.Turnover.HasValue,
        HistoricalField.OuterVolume => quote.OuterVolume.HasValue,
        HistoricalField.InnerVolume => quote.InnerVolume.HasValue,
        _ => false
    };

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
        if (file.SchemaVersion == LegacySchemaVersion)
            return new HistoricalMarketDataset(file.DatasetId!, file.TradingDates!, quotes, klines, contexts, file.Capabilities!, string.IsNullOrWhiteSpace(file.PriceAdjustmentMode) ? "Unknown" : file.PriceAdjustmentMode, string.IsNullOrWhiteSpace(file.Source) ? "Unknown" : file.Source, LegacySchemaVersion);

        HistoricalDatasetMetadataFile metadata = file.Metadata!;
        HistoricalSecurity[] securities = file.Securities!.Select(item => new HistoricalSecurity(item.Symbol, item.Name ?? item.Symbol, item.SecurityType, item.Market, item.ListingDate, item.DelistingEffectiveDate, item.LifecycleQuality, item.ObservedHistoryStart)).ToArray();
        HistoricalUniverseSnapshot[] universes = file.Universes!.Select(item => new HistoricalUniverseSnapshot(item.TradingDate, (IReadOnlyList<string>?)item.SecuritySymbols ?? Array.Empty<string>(), item.Quality, item.Source ?? string.Empty, (IReadOnlyList<string>?)item.Warnings ?? Array.Empty<string>())).ToArray();
        HistoricalDatasetMetadata runtimeMetadata = new(metadata.DatasetId!, metadata.Source ?? "Unknown", metadata.CreatedAt, metadata.UniverseQuality, metadata.Warnings);
        IReadOnlyList<HistoricalPriceSeriesProvenance> priceProvenance = file.PriceSeriesProvenance!;
        HistoricalBenchmarkSeries[] benchmarks = (file.Benchmarks ?? []).Select(item => new HistoricalBenchmarkSeries(
            item.BenchmarkId,
            item.PriceBasis,
            item.Source ?? string.Empty,
            (item.Observations ?? []).Select(observation => new HistoricalBenchmarkObservation(observation.TradingDate, observation.Close)),
            item.Coverage,
            item.Provenance!,
            item.DisplayName)).ToArray();
        return new HistoricalMarketDataset(
            file.DatasetId!, file.TradingDates!, quotes, klines, contexts,
            capabilities: CompatibilityCapabilities(file.FieldCapabilities!),
            priceAdjustmentMode: priceProvenance.Select(item => item.AdjustmentMode).Distinct().Count() == 1
                ? priceProvenance[0].AdjustmentMode.ToString() : "Unknown",
            source: metadata.Source ?? "Unknown",
            schemaVersion: CurrentSchemaVersion,
            metadata: runtimeMetadata,
            securities: securities,
            universes: universes,
            fieldCapabilities: file.FieldCapabilities,
            priceSeriesProvenance: priceProvenance,
            marketContextProvenance: file.MarketContextProvenance,
            observationDeclarations: file.ObservationDeclarations,
            riskStatusObservations: file.RiskStatusObservations,
            adjustmentFactors: file.AdjustmentFactors,
            qualitySummary: file.QualitySummary,
            datasetScope: file.DatasetScope,
            coverageEvidence: file.CoverageEvidence,
            strategyCapabilities: file.StrategyCapabilities,
            benchmarks: benchmarks);
    }

    private static HistoricalDataCapabilities CompatibilityCapabilities(IEnumerable<HistoricalFieldCapability> capabilities)
    {
        Dictionary<HistoricalField, HistoricalFieldCapability> values = capabilities.ToDictionary(item => item.Field);
        return new(Has(HistoricalField.Amount), Has(HistoricalField.Turnover), Has(HistoricalField.OuterVolume), Has(HistoricalField.InnerVolume), true, true);
        bool Has(HistoricalField field) => values.TryGetValue(field, out HistoricalFieldCapability? value)
            && value.Origin != HistoricalFieldOrigin.Unavailable
            && value.Coverage is not HistoricalFieldCoverage.None and not HistoricalFieldCoverage.Unknown;
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
    public HistoricalDatasetMetadataFile? Metadata { get; set; }
    public List<HistoricalSecurityFile>? Securities { get; set; }
    public List<HistoricalUniverseSnapshotFile>? Universes { get; set; }
    public List<HistoricalFieldCapability>? FieldCapabilities { get; set; }
    public List<HistoricalPriceSeriesProvenance>? PriceSeriesProvenance { get; set; }
    public List<HistoricalMarketContextProvenance>? MarketContextProvenance { get; set; }
    public List<HistoricalObservationDeclaration>? ObservationDeclarations { get; set; }
    public List<HistoricalRiskStatusObservation>? RiskStatusObservations { get; set; }
    public List<HistoricalAdjustmentFactor>? AdjustmentFactors { get; set; }
    public HistoricalDatasetQualitySummary? QualitySummary { get; set; }
    public HistoricalDatasetScope? DatasetScope { get; set; }
    public List<HistoricalCoverageEvidence>? CoverageEvidence { get; set; }
    public List<HistoricalStrategyCapabilityExplanation>? StrategyCapabilities { get; set; }
    public List<HistoricalBenchmarkSeriesFile>? Benchmarks { get; set; }
}
public sealed class HistoricalDatasetMetadataFile
{
    public string? DatasetId { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public HistoricalUniverseQuality UniverseQuality { get; set; }
    public List<string>? Warnings { get; set; }
}
public sealed class HistoricalSecurityFile
{
    public string Symbol { get; set; } = string.Empty;
    public string? Name { get; set; }
    public HistoricalSecurityType SecurityType { get; set; }
    public HistoricalSecurityMarket Market { get; set; }
    public DateOnly? ListingDate { get; set; }
    public DateOnly? DelistingEffectiveDate { get; set; }
    public HistoricalLifecycleQuality LifecycleQuality { get; set; }
    public DateOnly? ObservedHistoryStart { get; set; }
}
public sealed class HistoricalUniverseSnapshotFile
{
    public DateOnly TradingDate { get; set; }
    public List<string>? SecuritySymbols { get; set; }
    public HistoricalUniverseQuality Quality { get; set; }
    public string? Source { get; set; }
    public List<string>? Warnings { get; set; }
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
public sealed class HistoricalBenchmarkSeriesFile
{
    public string BenchmarkId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public HistoricalBenchmarkPriceBasis PriceBasis { get; set; }
    public string? Source { get; set; }
    public HistoricalFieldCoverage Coverage { get; set; }
    public HistoricalBenchmarkProvenance? Provenance { get; set; }
    public List<HistoricalBenchmarkObservationFile>? Observations { get; set; }
}
public sealed class HistoricalBenchmarkObservationFile
{
    public DateOnly TradingDate { get; set; }
    public double Close { get; set; }
}
