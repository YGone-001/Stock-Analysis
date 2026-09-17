using System.Diagnostics;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Builds immutable Schema-V2 research datasets from the historical source only.</summary>
public sealed class HistoricalDatasetBuilder
{
    private readonly IHistoricalMarketDataSource _source;
    private readonly HistoricalDatasetJsonWriter _writer;

    public HistoricalDatasetBuilder(IHistoricalMarketDataSource source, HistoricalDatasetJsonWriter? writer = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _writer = writer ?? new HistoricalDatasetJsonWriter();
    }

    public async Task<HistoricalDatasetBuildResult> BuildAsync(HistoricalDatasetBuildRequest request, CancellationToken cancellationToken = default)
    {
        request.Validate();
        Stopwatch timer = Stopwatch.StartNew();
        List<string> warnings = [];
        List<HistoricalCoverageEvidence> evidence = [];
        IReadOnlyDictionary<string, HistoricalSourceCapabilityStatus> capabilities = (await _source.ProbeAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(item => item.Capability, item => item.Status, StringComparer.Ordinal);
        foreach (string required in new[] { "stock_basic", "daily", "trade_cal" })
            if (!capabilities.TryGetValue(required, out HistoricalSourceCapabilityStatus status) || status != HistoricalSourceCapabilityStatus.Available)
                throw new InvalidOperationException($"Mandatory historical capability '{required}' is unavailable: {status}.");

        HistoricalAcquisitionResult<HistoricalCalendarDay> calendarResult = await _source.AcquireCalendarAsync(request.Exchange, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
        evidence.AddRange(calendarResult.CoverageEvidence);
        IReadOnlyList<HistoricalCalendarDay> calendar = calendarResult.Data;
        DateOnly[] dates = calendar.Where(item => item.IsOpen).Select(item => item.TradingDate).Distinct().OrderBy(item => item).ToArray();
        if (dates.Length == 0) throw new InvalidOperationException("Historical calendar did not return any open trading dates.");
        HistoricalAcquisitionResult<HistoricalSourceSecurity> masterResult = await _source.AcquireSecuritiesAsync(request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
        evidence.AddRange(masterResult.CoverageEvidence);
        IReadOnlyList<HistoricalSourceSecurity> master = masterResult.Data;
        HashSet<string>? selectedSymbols = request.ExplicitSymbols is { Count: > 0 } ? request.ExplicitSymbols.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        HistoricalSourceSecurity[] eligible = master.Where(item => IsAshare(item) && Overlaps(item, request.StartDate, request.EndDate) && (selectedSymbols is null || selectedSymbols.Contains(item.TsCode) || selectedSymbols.Contains(item.Symbol))).OrderBy(item => item.Symbol, StringComparer.Ordinal).ToArray();
        if (eligible.Length == 0) throw new InvalidOperationException("Historical security master did not return an in-range A-share security.");
        warnings.Add("Tushare delist_date is mapped directly without a +1-day adjustment; source effective-date semantics remain unverified.");
        warnings.Add("Historical security names are stock_basic source records, not asserted name-at-T evidence.");

        Dictionary<string, IReadOnlyList<HistoricalDailyPrice>> prices = new(StringComparer.Ordinal);
        Dictionary<(DateOnly Date, string Symbol), HistoricalTurnover> turnover = new();
        int skipped = 0;
        foreach (HistoricalSourceSecurity security in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                HistoricalAcquisitionResult<HistoricalDailyPrice> dailyResult = await _source.AcquireDailyPricesAsync(security.TsCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
                evidence.AddRange(dailyResult.CoverageEvidence);
                IReadOnlyList<HistoricalDailyPrice> rows = dailyResult.Data;
                if (rows.Count == 0) { skipped++; warnings.Add($"No daily price rows returned for {security.Symbol}."); continue; }
                prices[security.Symbol] = rows;
                if (request.IncludeTurnover && capabilities.TryGetValue("daily_basic", out HistoricalSourceCapabilityStatus turnoverStatus) && turnoverStatus == HistoricalSourceCapabilityStatus.Available)
                {
                    try
                    {
                        HistoricalAcquisitionResult<HistoricalTurnover> turnoverResult = await _source.AcquireTurnoverAsync(security.TsCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
                        evidence.AddRange(turnoverResult.CoverageEvidence);
                        foreach (HistoricalTurnover item in turnoverResult.Data)
                            turnover[(item.TradingDate, security.Symbol)] = item;
                    }
                    catch (Exception) { warnings.Add($"Turnover acquisition failed for {security.Symbol}; field coverage is partial."); }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                skipped++;
                warnings.Add($"Daily price acquisition failed for {security.Symbol}; symbol was skipped.");
            }
        }
        bool wantsTurnover = request.IncludeTurnover && capabilities.TryGetValue("daily_basic", out HistoricalSourceCapabilityStatus dailyBasic) && dailyBasic == HistoricalSourceCapabilityStatus.Available;
        if (request.IncludeTurnover && !wantsTurnover) warnings.Add("Historical turnover is unavailable because daily_basic capability is not available.");
        IReadOnlyList<HistoricalStStatus> st = [];
        bool stAvailable = request.IncludeHistoricalSt && capabilities.TryGetValue("stock_st", out HistoricalSourceCapabilityStatus stStatus) && stStatus == HistoricalSourceCapabilityStatus.Available;
        if (stAvailable)
        {
            HistoricalAcquisitionResult<HistoricalStStatus> result = await _source.AcquireStStatusesAsync(request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
            st = result.Data; evidence.AddRange(result.CoverageEvidence);
        }
        else if (request.IncludeHistoricalSt) warnings.Add("Historical ST status is unavailable because stock_st capability is not available.");
        IReadOnlyList<HistoricalSuspension> suspensions = [];
        bool suspensionAvailable = request.IncludeSuspension && capabilities.TryGetValue("suspend_d", out HistoricalSourceCapabilityStatus suspensionStatus) && suspensionStatus == HistoricalSourceCapabilityStatus.Available;
        if (suspensionAvailable)
        {
            HistoricalAcquisitionResult<HistoricalSuspension> result = await _source.AcquireSuspensionsAsync(request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
            suspensions = result.Data; evidence.AddRange(result.CoverageEvidence);
        }
        else if (request.IncludeSuspension) warnings.Add("Historical suspension evidence is unavailable because suspend_d capability is not available.");
        Dictionary<(DateOnly Date, string Symbol), HistoricalSourceAdjustmentFactor> factors = new();
        bool factorAvailable = request.IncludeAdjustmentFactors && capabilities.TryGetValue("adj_factor", out HistoricalSourceCapabilityStatus factorStatus) && factorStatus == HistoricalSourceCapabilityStatus.Available;
        if (factorAvailable)
        {
            foreach (HistoricalSourceSecurity security in eligible.Where(item => prices.ContainsKey(item.Symbol)))
            {
                HistoricalAcquisitionResult<HistoricalSourceAdjustmentFactor> factorResult = await _source.AcquireAdjustmentFactorsAsync(security.TsCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
                evidence.AddRange(factorResult.CoverageEvidence);
                foreach (HistoricalSourceAdjustmentFactor factor in factorResult.Data)
                    factors.Add((factor.TradingDate, factor.Symbol), factor);
            }
        }
        else if (request.IncludeAdjustmentFactors) warnings.Add("Adjustment-factor evidence is unavailable because adj_factor capability is not available.");

        IReadOnlyList<HistoricalIndexDaily> index = [];
        if (request.IncludeV2IndexContext && capabilities.TryGetValue("index_daily", out HistoricalSourceCapabilityStatus indexStatus) && indexStatus == HistoricalSourceCapabilityStatus.Available)
        {
            HistoricalAcquisitionResult<HistoricalIndexDaily> result = await _source.AcquireIndexDailyAsync(request.V2IndexCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
            index = result.Data; evidence.AddRange(result.CoverageEvidence);
        }
        else if (request.IncludeV2IndexContext) warnings.Add("V2 Shanghai daily-percent context is unavailable because index_daily capability is not available.");

        Dictionary<string, IReadOnlyList<HistoricalIndexDaily>> benchmarkRows = new(StringComparer.Ordinal);
        if (request.BenchmarkIds.Count > 0)
        {
            if (!capabilities.TryGetValue("index_daily", out HistoricalSourceCapabilityStatus benchmarkStatus) || benchmarkStatus != HistoricalSourceCapabilityStatus.Available)
                warnings.Add("Requested benchmark series are unavailable because index_daily capability is not available.");
            else
            {
                foreach (string benchmarkId in request.BenchmarkIds.OrderBy(value => value, StringComparer.Ordinal))
                {
                    HistoricalAcquisitionResult<HistoricalIndexDaily> result = await _source.AcquireIndexDailyAsync(benchmarkId, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
                    benchmarkRows.Add(benchmarkId, result.Data);
                    evidence.AddRange(result.CoverageEvidence);
                }
            }
        }

        ValidateIndexDates(index, request.V2IndexCode, dates, evidence, warnings);
        HistoricalDatasetScope scope = request.Scope ?? (selectedSymbols is null ? new HistoricalDatasetScope() : new HistoricalDatasetScope(HistoricalDatasetScopeKind.ExplicitSymbolSet, Symbols: selectedSymbols.OrderBy(value => value, StringComparer.Ordinal).ToArray()));
        HistoricalMarketDataset dataset = Construct(request, dates, eligible, prices, turnover, wantsTurnover, index, benchmarkRows, st, stAvailable, suspensions, suspensionAvailable, factors.Values, factorAvailable, warnings, scope, evidence);
        await _writer.WriteAsync(dataset, request.OutputPath, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        HistoricalDatasetBuildStatistics statistics = new(master.Count, eligible.Length, eligible.Length, prices.Count, skipped, dates.Length,
            prices.Values.Sum(rows => rows.Count), dataset.Quotes.Count, suspensions.Count, st.Count, index.Count, warnings.ToArray(), timer.Elapsed,
            factors.Count, eligible.Count(item => string.Equals(item.ListStatus, "D", StringComparison.OrdinalIgnoreCase)), eligible.Count(item => !item.ListingDate.HasValue),
            stAvailable ? 0 : eligible.Length, suspensionAvailable ? 0 : eligible.Length * dates.Length);
        HistoricalStrategyCapabilityExplanation[] strategyCapabilities = Capabilities(dataset);
        return new HistoricalDatasetBuildResult(dataset, statistics, IsPartial: skipped > 0 || evidence.Any(item => item.CoverageStatus != HistoricalCoverageAcquisitionStatus.Full) || !wantsTurnover || !stAvailable || !suspensionAvailable || !factorAvailable, evidence, strategyCapabilities);
    }

    private static HistoricalMarketDataset Construct(HistoricalDatasetBuildRequest request, IReadOnlyList<DateOnly> dates, IReadOnlyList<HistoricalSourceSecurity> master,
        IReadOnlyDictionary<string, IReadOnlyList<HistoricalDailyPrice>> prices, IReadOnlyDictionary<(DateOnly Date, string Symbol), HistoricalTurnover> turnover,
        bool wantsTurnover, IReadOnlyList<HistoricalIndexDaily> index, IReadOnlyDictionary<string, IReadOnlyList<HistoricalIndexDaily>> benchmarkRows, IReadOnlyList<HistoricalStStatus> st, bool stAvailable,
        IReadOnlyList<HistoricalSuspension> suspensions, bool suspensionAvailable, IEnumerable<HistoricalSourceAdjustmentFactor> factors, bool factorAvailable, List<string> warnings, HistoricalDatasetScope scope, IReadOnlyList<HistoricalCoverageEvidence> evidence)
    {
        HistoricalSecurity[] securities = master.Select(item => new HistoricalSecurity(item.Symbol, item.Name, HistoricalSecurityType.Stock, Market(item.Exchange),
            item.ListingDate, item.DelistingDate, HistoricalLifecycleQuality.Partial, prices.TryGetValue(item.Symbol, out IReadOnlyList<HistoricalDailyPrice>? rows) ? rows.Min(row => row.TradingDate) : null)).ToArray();
        Dictionary<(DateOnly Date, string Symbol), HistoricalDailyPrice> byDate = prices.Values.SelectMany(rows => rows).ToDictionary(item => (item.TradingDate, item.Symbol));
        HistoricalQuoteObservation[] quotes = byDate.Values.OrderBy(item => item.TradingDate).ThenBy(item => item.Symbol, StringComparer.Ordinal).Select(item =>
        {
            turnover.TryGetValue((item.TradingDate, item.Symbol), out HistoricalTurnover? rate);
            return new HistoricalQuoteObservation(item.TradingDate, new QuoteSnapshot(item.Symbol,
                securities.Single(security => security.Symbol == item.Symbol).Name, item.Close, item.PreviousClose, item.Percent, item.Volume, item.Amount,
                rate?.TurnoverRate, null, null, item.Open, item.High, item.Low));
        }).ToArray();
        KlineSeries[] klines = prices.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new KlineSeries(item.Key, item.Value.OrderBy(row => row.TradingDate).Select(row =>
            new KlineBar(row.TradingDate.ToDateTime(TimeOnly.MinValue), row.Open, row.High, row.Low, row.Close, row.Volume, row.Amount, row.Percent, row.Change,
                turnover.TryGetValue((row.TradingDate, row.Symbol), out HistoricalTurnover? rate) ? rate.TurnoverRate : null)).ToArray())).ToArray();
        HistoricalUniverseSnapshot[] universes = dates.Select(day => new HistoricalUniverseSnapshot(day,
            securities.Where(security => IsActive(security, day)).Select(security => security.Symbol).OrderBy(symbol => symbol, StringComparer.Ordinal).ToArray(),
            HistoricalUniverseQuality.Partial, "tushare:stock_basic", new[] { "Historical universe is source-backed but completeness is not independently verified." })).ToArray();
        HashSet<(DateOnly Date, string Symbol)> suspended = suspensions.Where(item => string.Equals(item.Action, "S", StringComparison.OrdinalIgnoreCase)).Select(item => (item.TradingDate, item.Symbol)).ToHashSet();
        HistoricalObservationDeclaration[] declarations = universes.SelectMany(universe => universe.SecuritySymbols.Select(symbol => byDate.ContainsKey((universe.TradingDate, symbol))
            ? new HistoricalObservationDeclaration(universe.TradingDate, symbol, HistoricalSecurityStatus.Active, HistoricalObservationStatus.Available, "Tushare daily observed")
            : suspended.Contains((universe.TradingDate, symbol))
                ? new HistoricalObservationDeclaration(universe.TradingDate, symbol, HistoricalSecurityStatus.Suspended, HistoricalObservationStatus.Suspended, "Tushare suspend_d observed suspension")
                : new HistoricalObservationDeclaration(universe.TradingDate, symbol, HistoricalSecurityStatus.Unknown, HistoricalObservationStatus.UnknownDataGap, "No daily row; not inferred as suspended"))).ToArray();
        HistoricalMarketContext[] contexts = index.Where(row => dates.Contains(row.TradingDate)).Select(row => new HistoricalMarketContext(row.TradingDate, null, row.Percent)).ToArray();
        HistoricalMarketContextProvenance[] contextProvenance = contexts.Select(context => new HistoricalMarketContextProvenance(context.TradingDate,
            HistoricalFieldOrigin.Unavailable, "Historical intraday Classic market regime is unavailable", HistoricalFieldOrigin.Observed, "tushare:index_daily")).ToArray();
        HistoricalFieldCapability[] fieldCapabilities = [
            new(HistoricalField.Price, HistoricalFieldOrigin.Observed, Coverage(quotes, quote => quote.Price.HasValue)),
            new(HistoricalField.PreviousClose, HistoricalFieldOrigin.Observed, Coverage(quotes, quote => quote.PreviousClose.HasValue)),
            new(HistoricalField.ChangePercent, HistoricalFieldOrigin.Observed, Coverage(quotes, quote => quote.ChangePercent.HasValue), HistoricalValueUnit.Percentage),
            new(HistoricalField.Amount, HistoricalFieldOrigin.Observed, Coverage(quotes, quote => quote.Amount.HasValue), HistoricalValueUnit.CurrencyThousands),
            new(HistoricalField.Turnover, wantsTurnover ? HistoricalFieldOrigin.Observed : HistoricalFieldOrigin.Unavailable, wantsTurnover ? Coverage(quotes, quote => quote.Turnover.HasValue) : HistoricalFieldCoverage.None, HistoricalValueUnit.Percentage),
            new(HistoricalField.OuterVolume, HistoricalFieldOrigin.Unavailable, HistoricalFieldCoverage.None, HistoricalValueUnit.Hands),
            new(HistoricalField.InnerVolume, HistoricalFieldOrigin.Unavailable, HistoricalFieldCoverage.None, HistoricalValueUnit.Hands)];
        HistoricalDatasetQualitySummary quality = new(HistoricalUniverseQuality.Partial, HistoricalLifecycleQuality.Partial,
                StCoverage(stAvailable, dates, evidence),
                suspensionAvailable ? HistoricalFieldCoverage.Partial : HistoricalFieldCoverage.None,
                fieldCapabilities.Single(item => item.Field == HistoricalField.Turnover).Coverage,
                factorAvailable ? CoverageFactors(factors) : HistoricalFieldCoverage.None,
                IndexCoverage(index, dates, request.V2IndexCode, evidence),
                HistoricalFieldCoverage.None, warnings);
        HistoricalBenchmarkSeries[] benchmarks = benchmarkRows.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => Benchmark(
            item.Key, item.Value, dates, request.StartDate, request.EndDate, evidence, warnings)).ToArray();
        return new HistoricalMarketDataset(request.DatasetId, dates, quotes, klines, contexts,
            priceAdjustmentMode: "Raw", source: "tushare", schemaVersion: HistoricalDatasetJsonLoader.CurrentSchemaVersion,
            metadata: new HistoricalDatasetMetadata(request.DatasetId, "tushare", DateTimeOffset.UtcNow, HistoricalUniverseQuality.Partial, warnings), securities: securities,
            universes: universes, fieldCapabilities: fieldCapabilities,
            priceSeriesProvenance: klines.Select(series => new HistoricalPriceSeriesProvenance(series.Symbol, "tushare:daily", HistoricalPriceAdjustmentMode.Raw,
                DateOnly.FromDateTime(series.Bars.First().Date), DateOnly.FromDateTime(series.Bars.Last().Date))), marketContextProvenance: contextProvenance,
            observationDeclarations: declarations,
            riskStatusObservations: st.Select(item => new HistoricalRiskStatusObservation(item.TradingDate, item.Symbol, true, item.Source)),
            adjustmentFactors: factors.Select(item => new HistoricalAdjustmentFactor(item.Symbol, item.TradingDate, item.Factor, item.Source, item.RetrievedAtUtc)),
            qualitySummary: quality, datasetScope: scope, coverageEvidence: evidence,
            strategyCapabilities: Capabilities(quality), benchmarks: benchmarks);
    }

    private static HistoricalBenchmarkSeries Benchmark(string benchmarkId, IReadOnlyList<HistoricalIndexDaily> rows, IReadOnlyList<DateOnly> dates,
        DateOnly startDate, DateOnly endDate, IReadOnlyList<HistoricalCoverageEvidence> evidence, List<string> warnings)
    {
        if (rows.Any(row => !string.Equals(row.IndexCode, benchmarkId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Historical index response does not match requested benchmark '{benchmarkId}'.");
        HistoricalIndexDaily[] validRows = rows.Where(row => row.Close is double value && value > 0 && double.IsFinite(value)).ToArray();
        if (validRows.Length != rows.Count)
            warnings.Add($"Benchmark '{benchmarkId}' contains missing or invalid close values and is partial.");
        HistoricalBenchmarkObservation[] observations = validRows.Select(row => new HistoricalBenchmarkObservation(row.TradingDate, row.Close!.Value)).ToArray();
        bool dateSetMatches = observations.Select(item => item.TradingDate).ToHashSet().SetEquals(dates);
        HistoricalCoverageEvidence[] benchmarkEvidence = evidence.Where(item => item.Endpoint == "index_daily" && string.Equals(item.Scope.IndexCode, benchmarkId, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool sourceFull = benchmarkEvidence.Length > 0 && benchmarkEvidence.All(item => item.CoverageStatus == HistoricalCoverageAcquisitionStatus.Full);
        HistoricalFieldCoverage coverage = dateSetMatches && sourceFull ? HistoricalFieldCoverage.Full : observations.Length > 0 ? HistoricalFieldCoverage.Partial : HistoricalFieldCoverage.None;
        string source = rows.Select(row => row.Source).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "tushare";
        return new HistoricalBenchmarkSeries(benchmarkId, HistoricalBenchmarkPriceBasis.IndexClose, source, observations, coverage,
            new HistoricalBenchmarkProvenance(source, startDate, endDate, "dataset-trading-date-set equality"));
    }

    private static bool IsAshare(HistoricalSourceSecurity item) => item.Exchange is "SSE" or "SZSE" or "BSE" || item.TsCode.EndsWith(".SH", StringComparison.Ordinal) || item.TsCode.EndsWith(".SZ", StringComparison.Ordinal) || item.TsCode.EndsWith(".BJ", StringComparison.Ordinal);
    private static bool Overlaps(HistoricalSourceSecurity item, DateOnly start, DateOnly end) => (!item.ListingDate.HasValue || item.ListingDate <= end) && (!item.DelistingDate.HasValue || item.DelistingDate > start);
    private static bool IsActive(HistoricalSecurity security, DateOnly date) => (!security.ListingDate.HasValue || security.ListingDate <= date) && (!security.DelistingEffectiveDate.HasValue || security.DelistingEffectiveDate > date);
    private static HistoricalSecurityMarket Market(string? exchange) => exchange?.ToUpperInvariant() switch { "SSE" => HistoricalSecurityMarket.Shanghai, "SZSE" => HistoricalSecurityMarket.Shenzhen, "BSE" => HistoricalSecurityMarket.Beijing, _ => HistoricalSecurityMarket.Unknown };
    private static HistoricalFieldCoverage Coverage(IEnumerable<HistoricalQuoteObservation> observations, Func<QuoteSnapshot, bool> hasValue)
    {
        bool[] values = observations.Select(item => hasValue(item.Quote)).ToArray();
        return values.Length == 0 || values.All(item => !item) ? HistoricalFieldCoverage.None : values.All(item => item) ? HistoricalFieldCoverage.Full : HistoricalFieldCoverage.Partial;
    }
    private static HistoricalFieldCoverage CoverageFactors(IEnumerable<HistoricalSourceAdjustmentFactor> factors)
    {
        int count = factors.Count();
        return count == 0 ? HistoricalFieldCoverage.None : HistoricalFieldCoverage.Partial;
    }

    private static HistoricalFieldCoverage IndexCoverage(IReadOnlyList<HistoricalIndexDaily> index, IReadOnlyList<DateOnly> dates, string code, IReadOnlyList<HistoricalCoverageEvidence> evidence)
    {
        HistoricalCoverageEvidence[] contextEvidence = evidence.Where(item => item.Endpoint == "index_daily" && string.Equals(item.Scope.IndexCode, code, StringComparison.OrdinalIgnoreCase)).ToArray();
        return index.Count > 0 && index.All(item => string.Equals(item.IndexCode, code, StringComparison.OrdinalIgnoreCase)) && index.Select(item => item.TradingDate).ToHashSet().SetEquals(dates)
            && contextEvidence.Length > 0 && contextEvidence.All(item => item.CoverageStatus == HistoricalCoverageAcquisitionStatus.Full)
            ? HistoricalFieldCoverage.Full : index.Count > 0 ? HistoricalFieldCoverage.Partial : HistoricalFieldCoverage.None;
    }

    private static HistoricalFieldCoverage StCoverage(bool available, IReadOnlyList<DateOnly> dates, IReadOnlyList<HistoricalCoverageEvidence> evidence)
    {
        if (!available) return HistoricalFieldCoverage.None;
        HistoricalCoverageEvidence[] daily = evidence.Where(item => item.Endpoint == "stock_st").ToArray();
        return daily.Length == dates.Count && daily.All(item => item.CoverageStatus == HistoricalCoverageAcquisitionStatus.Full && !item.ResponseCapHit)
            && daily.Select(item => item.Scope.StartDate).ToHashSet().SetEquals(dates) ? HistoricalFieldCoverage.Full : HistoricalFieldCoverage.Partial;
    }

    private static void ValidateIndexDates(IReadOnlyList<HistoricalIndexDaily> index, string code, IReadOnlyList<DateOnly> dates, List<HistoricalCoverageEvidence> evidence, List<string> warnings)
    {
        if (index.Count == 0) return;
        if (!index.All(item => string.Equals(item.IndexCode, code, StringComparison.OrdinalIgnoreCase)) || !index.Select(item => item.TradingDate).ToHashSet().SetEquals(dates))
        {
            warnings.Add("Index context date set does not equal the trading-date set; index coverage is partial.");
            for (int i = 0; i < evidence.Count; i++) if (evidence[i].Endpoint == "index_daily" && string.Equals(evidence[i].Scope.IndexCode, code, StringComparison.OrdinalIgnoreCase) && evidence[i].CoverageStatus == HistoricalCoverageAcquisitionStatus.Full)
                evidence[i] = evidence[i] with { CoverageStatus = HistoricalCoverageAcquisitionStatus.Partial, FailureReason = "index_date_set_mismatch" };
        }
    }

    private static HistoricalStrategyCapabilityExplanation[] Capabilities(HistoricalMarketDataset dataset)
        => Capabilities(dataset.QualitySummary);

    private static HistoricalStrategyCapabilityExplanation[] Capabilities(HistoricalDatasetQualitySummary quality)
    {
        var classic = new List<string> { "UNIVERSE_PARTIAL", "LIFECYCLE_PARTIAL", "CLASSIC_REGIME_UNAVAILABLE" };
        var v2 = new List<string> { "UNIVERSE_PARTIAL", "OUTER_VOLUME_UNAVAILABLE", "INNER_VOLUME_UNAVAILABLE" };
        if (quality.HistoricalStCoverage != HistoricalFieldCoverage.Full) classic.Add("HISTORICAL_ST_PARTIAL");
        if (quality.SuspensionCoverage != HistoricalFieldCoverage.Full) { classic.Add("SUSPENSION_PARTIAL"); v2.Add("SUSPENSION_PARTIAL"); }
        if (quality.TurnoverCoverage != HistoricalFieldCoverage.Full) v2.Add("TURNOVER_PARTIAL");
        if (quality.IndexCoverage != HistoricalFieldCoverage.Full) v2.Add("INDEX_CONTEXT_PARTIAL");
        return [new(SparrowStrategyMode.Classic, HistoricalReplaySupport.Partial, classic), new(SparrowStrategyMode.V2, HistoricalReplaySupport.Partial, v2)];
    }
}
