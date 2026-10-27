using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Loads schema-versioned historical research input. It deliberately does not accept replay/result exports.</summary>
public sealed class HistoricalDatasetJsonLoader : IHistoricalDatasetLoader
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<HistoricalDatasetLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path)) return HistoricalDatasetLoadResult.Failed("必须提供历史数据集文件路径。");
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await LoadStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException ex) { return HistoricalDatasetLoadResult.Failed($"无法读取历史数据集：{ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return HistoricalDatasetLoadResult.Failed($"无法访问历史数据集：{ex.Message}"); }
        catch (Exception ex) { return HistoricalDatasetLoadResult.Failed($"历史数据集加载出现未知错误：{ex.Message}"); }
    }

    public async Task<HistoricalDatasetLoadResult> LoadStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            HistoricalDatasetFile? file = await JsonSerializer.DeserializeAsync<HistoricalDatasetFile>(stream, JsonOptions, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null) return HistoricalDatasetLoadResult.Failed("数据集文件无效：JSON 内容为空。");
            List<string> errors = Validate(file);
            if (errors.Count > 0) return new HistoricalDatasetLoadResult(false, null, errors, Array.Empty<string>());

            HistoricalMarketDataset dataset = Build(file);
            if (!string.IsNullOrWhiteSpace(file.Fingerprint) && !string.Equals(file.Fingerprint, dataset.Fingerprint, StringComparison.OrdinalIgnoreCase))
                return HistoricalDatasetLoadResult.Failed("数据集指纹不匹配：文件内容与声明的指纹不一致。");
            List<string> warnings = new();
            if (dataset.PriceAdjustmentMode == HistoricalPriceAdjustmentMode.Unknown)
                warnings.Add("复权方式为「未知」，公司行为（除权除息）前后的可比性可能受限。");
            if (dataset.Metadata.UniverseQuality != HistoricalUniverseQuality.Complete)
                warnings.Add("历史股票池不完整，可能仍存在幸存者偏差。");
            return HistoricalDatasetLoadResult.Loaded(dataset, warnings);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex) { return HistoricalDatasetLoadResult.Failed($"数据集模式无效：{ex.Message}"); }
        catch (Exception ex) { return HistoricalDatasetLoadResult.Failed($"历史数据集加载出现未知错误：{ex.Message}"); }
    }

    private static List<string> Validate(HistoricalDatasetFile file)
    {
        List<string> errors = new();
        if (file.SchemaVersion is not LegacySchemaVersion and not CurrentSchemaVersion and not 2) errors.Add($"不支持的历史数据集模式版本「{file.SchemaVersion}」，当前支持：{LegacySchemaVersion}、2、{CurrentSchemaVersion}。");
        if (string.IsNullOrWhiteSpace(file.DatasetId)) errors.Add("历史数据集必须提供非空的 datasetId。");
        if (file.TradingDates is not { Count: > 0 }) errors.Add("历史数据集必须提供 tradingDates。");
        else
        {
            for (int i = 1; i < file.TradingDates.Count; i++)
                if (file.TradingDates[i - 1] >= file.TradingDates[i]) { errors.Add("交易日必须严格升序且不能重复。"); break; }
        }
        if (file.SchemaVersion == LegacySchemaVersion && file.Capabilities is null) errors.Add("历史数据集必须显式声明 capabilities。");
        if (file.Quotes is null) errors.Add("历史数据集必须提供 quotes 集合。");
        if (file.Klines is null) errors.Add("历史数据集必须提供 klines 集合。");
        if (errors.Count > 0) return errors;

        HashSet<DateOnly> dates = file.TradingDates!.ToHashSet();
        HashSet<(DateOnly Date, string Symbol)> quoteKeys = new();
        foreach (HistoricalQuoteFile quote in file.Quotes!)
        {
            if (!dates.Contains(quote.TradingDate)) errors.Add($"行情「{quote.Symbol}」的日期不在 tradingDates 范围内。");
            if (string.IsNullOrWhiteSpace(quote.Symbol)) errors.Add("行情必须提供 symbol。");
            else if (!quoteKeys.Add((quote.TradingDate, quote.Symbol))) errors.Add($"{quote.TradingDate:yyyy-MM-dd} 存在重复的行情观测「{quote.Symbol}」。");
            ValidateNonNegative(quote.Volume, "行情成交量", errors);
            ValidateNonNegative(quote.Amount, "行情成交额", errors);
            ValidateNonNegative(quote.Turnover, "行情换手率", errors);
            ValidateNonNegative(quote.OuterVolume, "行情外盘量", errors);
            ValidateNonNegative(quote.InnerVolume, "行情内盘量", errors);
        }
        HashSet<string> symbols = new(StringComparer.Ordinal);
        foreach (HistoricalKlineSeriesFile series in file.Klines!)
        {
            if (string.IsNullOrWhiteSpace(series.Symbol)) { errors.Add("K 线序列必须提供 symbol。"); continue; }
            if (!symbols.Add(series.Symbol)) errors.Add($"「{series.Symbol}」存在重复的 K 线序列。");
            if (series.Bars is null || series.Bars.Count == 0) { errors.Add($"K 线序列「{series.Symbol}」必须包含 bars。"); continue; }
            DateOnly? previous = null;
            foreach (HistoricalKlineBarFile bar in series.Bars)
            {
                DateOnly date = DateOnly.FromDateTime(bar.Date);
                if (bar.Date.TimeOfDay != TimeSpan.Zero) errors.Add($"K 线「{series.Symbol}」的时间戳带有时间部分，只允许日期。");
                if (!dates.Contains(date)) errors.Add($"K 线「{series.Symbol}」的日期不在 tradingDates 范围内。");
                if (previous.HasValue && previous.Value >= date) errors.Add($"K 线「{series.Symbol}」的日期必须严格升序且不能重复。");
                previous = date;
                ValidateBar(series.Symbol, bar, errors);
            }
        }
        foreach (HistoricalMarketContextFile context in file.MarketContexts ?? Enumerable.Empty<HistoricalMarketContextFile>())
            if (!dates.Contains(context.TradingDate)) errors.Add($"大盘上下文的日期不在 tradingDates 范围内：{context.TradingDate:yyyy-MM-dd}。");
        ValidateBenchmarks(file.Benchmarks, dates, errors);
        if (file.SchemaVersion == CurrentSchemaVersion) ValidateV2(file, dates, errors);
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void ValidateV2(HistoricalDatasetFile file, HashSet<DateOnly> dates, List<string> errors)
    {
        if (file.Metadata is null) errors.Add("模式 V2 必须提供 metadata。");
        else if (!string.Equals(file.Metadata.DatasetId, file.DatasetId, StringComparison.Ordinal)) errors.Add("模式 V2 的 metadata.datasetId 必须与 datasetId 一致。");
        if (file.Securities is not { Count: > 0 }) { errors.Add("模式 V2 必须提供 securities。"); return; }
        if (file.Universes is null) errors.Add("模式 V2 必须提供 universes。");
        if (file.FieldCapabilities is null) errors.Add("模式 V2 必须提供 fieldCapabilities。");
        if (file.PriceSeriesProvenance is null) errors.Add("模式 V2 必须提供 priceSeriesProvenance。");
        if (file.MarketContextProvenance is null) errors.Add("模式 V2 必须提供 marketContextProvenance。");
        if (errors.Count > 0) return;

        HashSet<string> securitySymbols = new(StringComparer.Ordinal);
        foreach (HistoricalSecurityFile security in file.Securities)
        {
            if (string.IsNullOrWhiteSpace(security.Symbol)) { errors.Add("历史证券必须提供 symbol。"); continue; }
            if (!securitySymbols.Add(security.Symbol)) errors.Add($"「{security.Symbol}」存在重复的历史证券记录。");
            if (!Enum.IsDefined(security.SecurityType) || !Enum.IsDefined(security.Market) || !Enum.IsDefined(security.LifecycleQuality)) errors.Add($"历史证券「{security.Symbol}」包含无效的枚举值。");
            if (security.DelistingEffectiveDate.HasValue && security.ListingDate.HasValue && security.DelistingEffectiveDate <= security.ListingDate)
                errors.Add($"历史证券「{security.Symbol}」的上市/退市区间无效。");
        }
        foreach (HistoricalQuoteFile quote in file.Quotes!)
        {
            if (!securitySymbols.Contains(quote.Symbol)) errors.Add($"行情「{quote.Symbol}」没有引用任何历史证券。");
            HistoricalSecurityFile? security = file.Securities.FirstOrDefault(item => string.Equals(item.Symbol, quote.Symbol, StringComparison.Ordinal));
            if (security is not null && !string.IsNullOrWhiteSpace(quote.Name) && !string.Equals(security.Name, quote.Name, StringComparison.Ordinal)) errors.Add($"行情「{quote.Symbol}」的名称与证券主数据不一致。");
        }
        foreach (HistoricalKlineSeriesFile series in file.Klines!)
            if (!securitySymbols.Contains(series.Symbol)) errors.Add($"K 线「{series.Symbol}」没有引用任何历史证券。");

        Dictionary<DateOnly, HistoricalUniverseSnapshotFile> universes = new();
        foreach (HistoricalUniverseSnapshotFile universe in file.Universes!)
        {
            if (!dates.Contains(universe.TradingDate)) errors.Add($"股票池的日期不在 tradingDates 范围内：{universe.TradingDate:yyyy-MM-dd}。");
            else if (!universes.TryAdd(universe.TradingDate, universe)) errors.Add($"{universe.TradingDate:yyyy-MM-dd} 存在重复的股票池快照。");
            if (!Enum.IsDefined(universe.Quality)) errors.Add($"股票池「{universe.TradingDate:yyyy-MM-dd}」的质量取值无效。");
            foreach (string symbol in universe.SecuritySymbols ?? Enumerable.Empty<string>())
                if (!securitySymbols.Contains(symbol)) errors.Add($"股票池「{universe.TradingDate:yyyy-MM-dd}」引用了未知证券「{symbol}」。");
        }
        foreach (DateOnly date in dates) if (!universes.ContainsKey(date)) errors.Add($"模式 V2 要求为 {date:yyyy-MM-dd} 提供明确的股票池快照。");

        Dictionary<HistoricalField, HistoricalFieldCapability> capabilities = new();
        foreach (HistoricalFieldCapability capability in file.FieldCapabilities!)
        {
            if (!Enum.IsDefined(capability.Field) || !Enum.IsDefined(capability.Origin) || !Enum.IsDefined(capability.Coverage) || !Enum.IsDefined(capability.Unit)) errors.Add("存在字段能力声明包含无效的枚举值。");
            if (!capabilities.TryAdd(capability.Field, capability)) errors.Add($"字段「{capability.Field}」存在重复的能力声明。");
            if (capability.Origin == HistoricalFieldOrigin.Unavailable && capability.Coverage != HistoricalFieldCoverage.None)
                errors.Add($"不可用字段「{capability.Field}」的覆盖度必须声明为 None。");
            if (capability.Coverage == HistoricalFieldCoverage.None && capability.Origin != HistoricalFieldOrigin.Unavailable)
                errors.Add($"覆盖度为 None 的字段「{capability.Field}」必须声明来源为 Unavailable。");
        }
        foreach (HistoricalField field in Enum.GetValues<HistoricalField>())
            // Kline Volume joined the vocabulary after schema V2 shipped; legacy files without the row stay loadable and simply declare nothing.
            if (field != HistoricalField.Volume && !capabilities.ContainsKey(field)) errors.Add($"模式 V2 必须为字段「{field}」提供能力声明。");
        foreach (HistoricalFieldCapability capability in capabilities.Values.Where(item => item.Coverage == HistoricalFieldCoverage.Full))
            if (file.Quotes!.Any(quote => !HasQuoteField(quote, capability.Field))) errors.Add($"字段「{capability.Field}」声明为完整覆盖（Full），但存在缺少该字段取值的行情观测。");

        HashSet<string> klineSymbols = file.Klines!.Select(series => series.Symbol).ToHashSet(StringComparer.Ordinal);
        HashSet<string> provenanceSymbols = new(StringComparer.Ordinal);
        foreach (HistoricalPriceSeriesProvenance provenance in file.PriceSeriesProvenance!)
        {
            if (!provenanceSymbols.Add(provenance.Symbol)) errors.Add($"「{provenance.Symbol}」存在重复的价格序列来源声明，说明混用了不支持的复权方式。");
            if (!Enum.IsDefined(provenance.AdjustmentMode)) errors.Add($"价格序列来源「{provenance.Symbol}」的复权方式无效。");
            if (!klineSymbols.Contains(provenance.Symbol)) errors.Add($"价格序列来源「{provenance.Symbol}」没有对应的 K 线序列。");
            if (provenance.ObservationEnd.HasValue && provenance.ObservationStart.HasValue && provenance.ObservationEnd < provenance.ObservationStart) errors.Add($"价格序列来源「{provenance.Symbol}」的观测区间无效。");
        }
        foreach (string symbol in klineSymbols) if (!provenanceSymbols.Contains(symbol)) errors.Add($"模式 V2 必须为「{symbol}」提供价格序列来源声明。");

        HashSet<DateOnly> contextDates = (file.MarketContexts ?? Enumerable.Empty<HistoricalMarketContextFile>()).Select(context => context.TradingDate).ToHashSet();
        HashSet<DateOnly> provenanceDates = new();
        foreach (HistoricalMarketContextProvenance provenance in file.MarketContextProvenance!)
        {
            if (!dates.Contains(provenance.TradingDate)) errors.Add($"大盘上下文来源的日期不在 tradingDates 范围内：{provenance.TradingDate:yyyy-MM-dd}。");
            if (!provenanceDates.Add(provenance.TradingDate)) errors.Add($"{provenance.TradingDate:yyyy-MM-dd} 存在重复的大盘上下文来源声明。");
        }
        foreach (DateOnly date in contextDates) if (!provenanceDates.Contains(date)) errors.Add($"大盘上下文 {date:yyyy-MM-dd} 必须提供来源声明。");
        HashSet<(DateOnly Date, string Symbol)> declarationKeys = new();
        foreach (HistoricalObservationDeclaration declaration in file.ObservationDeclarations ?? Enumerable.Empty<HistoricalObservationDeclaration>())
        {
            if (!dates.Contains(declaration.TradingDate)) errors.Add($"观测声明的日期不在 tradingDates 范围内：{declaration.TradingDate:yyyy-MM-dd}。");
            if (!securitySymbols.Contains(declaration.Symbol)) errors.Add($"观测声明引用了未知证券「{declaration.Symbol}」。");
            if (!declarationKeys.Add((declaration.TradingDate, declaration.Symbol))) errors.Add($"「{declaration.Symbol}」在 {declaration.TradingDate:yyyy-MM-dd} 存在重复的观测声明。");
        }
        HashSet<(DateOnly Date, string Symbol)> riskKeys = new();
        foreach (HistoricalRiskStatusObservation observation in file.RiskStatusObservations ?? Enumerable.Empty<HistoricalRiskStatusObservation>())
        {
            if (!dates.Contains(observation.TradingDate)) errors.Add($"历史 ST 观测的日期不在 tradingDates 范围内：{observation.TradingDate:yyyy-MM-dd}。");
            if (!securitySymbols.Contains(observation.Symbol)) errors.Add($"历史 ST 观测引用了未知证券「{observation.Symbol}」。");
            if (!riskKeys.Add((observation.TradingDate, observation.Symbol))) errors.Add($"「{observation.Symbol}」在 {observation.TradingDate:yyyy-MM-dd} 存在重复的历史 ST 观测。");
        }
        HashSet<(DateOnly Date, string Symbol)> factorKeys = new();
        foreach (HistoricalAdjustmentFactor factor in file.AdjustmentFactors ?? Enumerable.Empty<HistoricalAdjustmentFactor>())
        {
            if (!dates.Contains(factor.TradingDate)) errors.Add($"复权因子的日期不在 tradingDates 范围内：{factor.TradingDate:yyyy-MM-dd}。");
            if (!securitySymbols.Contains(factor.Symbol)) errors.Add($"复权因子引用了未知证券「{factor.Symbol}」。");
            if (!factorKeys.Add((factor.TradingDate, factor.Symbol))) errors.Add($"「{factor.Symbol}」在 {factor.TradingDate:yyyy-MM-dd} 存在重复的复权因子。");
            if (double.IsNaN(factor.Factor) || double.IsInfinity(factor.Factor) || factor.Factor <= 0) errors.Add($"「{factor.Symbol}」的复权因子必须是有限的正数。");
        }
    }

    private static void ValidateBenchmarks(IEnumerable<HistoricalBenchmarkSeriesFile>? benchmarks, HashSet<DateOnly> dates, List<string> errors)
    {
        HashSet<string> identifiers = new(StringComparer.Ordinal);
        foreach (HistoricalBenchmarkSeriesFile benchmark in benchmarks ?? Enumerable.Empty<HistoricalBenchmarkSeriesFile>())
        {
            if (string.IsNullOrWhiteSpace(benchmark.BenchmarkId)) { errors.Add("基准必须提供非空的 benchmarkId。"); continue; }
            if (!identifiers.Add(benchmark.BenchmarkId)) errors.Add($"基准「{benchmark.BenchmarkId}」重复。");
            if (!Enum.IsDefined(benchmark.PriceBasis) || !Enum.IsDefined(benchmark.Coverage)) errors.Add($"基准「{benchmark.BenchmarkId}」的价格基准或覆盖度无效。");
            if (string.IsNullOrWhiteSpace(benchmark.Source)) errors.Add($"基准「{benchmark.BenchmarkId}」必须提供 source。");
            if (benchmark.Provenance is null) errors.Add($"基准「{benchmark.BenchmarkId}」必须提供来源声明。");
            else
            {
                try { benchmark.Provenance.Validate(); }
                catch (ArgumentException exception) { errors.Add($"基准「{benchmark.BenchmarkId}」的来源声明无效：{exception.Message}"); }
            }
            HashSet<DateOnly> observationDates = new();
            foreach (HistoricalBenchmarkObservationFile observation in benchmark.Observations ?? Enumerable.Empty<HistoricalBenchmarkObservationFile>())
            {
                if (!dates.Contains(observation.TradingDate)) errors.Add($"基准「{benchmark.BenchmarkId}」的日期不在 tradingDates 范围内：{observation.TradingDate:yyyy-MM-dd}。");
                if (!observationDates.Add(observation.TradingDate)) errors.Add($"基准「{benchmark.BenchmarkId}」在 {observation.TradingDate:yyyy-MM-dd} 存在重复日期。");
                if (!double.IsFinite(observation.Close) || observation.Close <= 0) errors.Add($"基准「{benchmark.BenchmarkId}」的收盘价必须是有限的正数。");
            }
            if (benchmark.Coverage == HistoricalFieldCoverage.Full && !observationDates.SetEquals(dates))
                errors.Add($"基准「{benchmark.BenchmarkId}」声明为完整覆盖（Full），但未完整覆盖数据集的全部交易日。");
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
        HistoricalField.Volume => quote.Volume.HasValue,
        _ => false
    };

    private static void ValidateBar(string symbol, HistoricalKlineBarFile bar, List<string> errors)
    {
        ValidateNonNegative(bar.Volume, $"K 线「{symbol}」成交量", errors);
        ValidateNonNegative(bar.Amount, $"K 线「{symbol}」成交额", errors);
        double? high = bar.High, low = bar.Low;
        if (high.HasValue && low.HasValue && high < low) errors.Add($"K 线「{symbol}」在 {bar.Date:yyyy-MM-dd} 的最高价低于最低价。");
        foreach (double? value in new[] { bar.Open, bar.Close })
        {
            if (high.HasValue && value.HasValue && high < value) errors.Add($"K 线「{symbol}」在 {bar.Date:yyyy-MM-dd} 的最高价低于开盘价或收盘价。");
            if (low.HasValue && value.HasValue && low > value) errors.Add($"K 线「{symbol}」在 {bar.Date:yyyy-MM-dd} 的最低价高于开盘价或收盘价。");
        }
    }

    private static void ValidateNonNegative(double? value, string label, List<string> errors)
    {
        if (value is < 0) errors.Add($"{label}不能为负数。");
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
        HistoricalSecurity[] securities = file.Securities!.Select(item => new HistoricalSecurity(item.Symbol, item.Name ?? item.Symbol, item.SecurityType, item.Market, item.ListingDate, item.DelistingEffectiveDate, item.LifecycleQuality, item.ObservedHistoryStart, file.SchemaVersion >= 3 ? item.Industry : null)).ToArray();
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
            schemaVersion: file.SchemaVersion,
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
    /// <summary>Schema-V3 field; absent or null in V2 files, which load with a null industry.</summary>
    public string? Industry { get; set; }
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
