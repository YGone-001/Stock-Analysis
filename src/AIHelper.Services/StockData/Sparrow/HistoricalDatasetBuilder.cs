using System.Diagnostics;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;

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
        IReadOnlyDictionary<string, HistoricalSourceCapabilityStatus> capabilities = (await _source.ProbeAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(item => item.Capability, item => item.Status, StringComparer.Ordinal);
        foreach (string required in new[] { "stock_basic", "daily", "trade_cal" })
            if (!capabilities.TryGetValue(required, out HistoricalSourceCapabilityStatus status) || status != HistoricalSourceCapabilityStatus.Available)
                throw new InvalidOperationException($"Mandatory historical capability '{required}' is unavailable: {status}.");

        IReadOnlyList<HistoricalCalendarDay> calendar = await _source.GetCalendarAsync(request.Exchange, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
        DateOnly[] dates = calendar.Where(item => item.IsOpen).Select(item => item.TradingDate).Distinct().OrderBy(item => item).ToArray();
        if (dates.Length == 0) throw new InvalidOperationException("Historical calendar did not return any open trading dates.");
        IReadOnlyList<HistoricalSourceSecurity> master = await _source.GetSecuritiesAsync(request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
        HistoricalSourceSecurity[] eligible = master.Where(item => IsAshare(item) && Overlaps(item, request.StartDate, request.EndDate)).OrderBy(item => item.Symbol, StringComparer.Ordinal).ToArray();
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
                IReadOnlyList<HistoricalDailyPrice> rows = await _source.GetDailyPricesAsync(security.TsCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0) { skipped++; warnings.Add($"No daily price rows returned for {security.Symbol}."); continue; }
                prices[security.Symbol] = rows;
                if (request.IncludeTurnover && capabilities.TryGetValue("daily_basic", out HistoricalSourceCapabilityStatus turnoverStatus) && turnoverStatus == HistoricalSourceCapabilityStatus.Available)
                {
                    try
                    {
                        foreach (HistoricalTurnover item in await _source.GetTurnoverAsync(security.TsCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false))
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
        if (request.IncludeHistoricalSt) warnings.Add("Historical ST acquisition is not implemented in the strict V1 builder; Classic ST eligibility is partial.");
        if (request.IncludeSuspension) warnings.Add("Historical suspension acquisition is not implemented in the strict V1 builder; absent prices remain UnknownDataGap.");

        IReadOnlyList<HistoricalIndexDaily> index = [];
        if (request.IncludeV2IndexContext && capabilities.TryGetValue("index_daily", out HistoricalSourceCapabilityStatus indexStatus) && indexStatus == HistoricalSourceCapabilityStatus.Available)
            index = await _source.GetIndexDailyAsync(request.V2IndexCode, request.StartDate, request.EndDate, cancellationToken).ConfigureAwait(false);
        else if (request.IncludeV2IndexContext) warnings.Add("V2 Shanghai daily-percent context is unavailable because index_daily capability is not available.");

        HistoricalMarketDataset dataset = Construct(request, dates, eligible, prices, turnover, wantsTurnover, index, warnings);
        await _writer.WriteAsync(dataset, request.OutputPath, cancellationToken).ConfigureAwait(false);
        timer.Stop();
        HistoricalDatasetBuildStatistics statistics = new(master.Count, eligible.Length, eligible.Length, prices.Count, skipped, dates.Length,
            prices.Values.Sum(rows => rows.Count), dataset.Quotes.Count, 0, 0, index.Count, warnings.ToArray(), timer.Elapsed);
        return new HistoricalDatasetBuildResult(dataset, statistics, IsPartial: skipped > 0 || !wantsTurnover || request.IncludeHistoricalSt || request.IncludeSuspension);
    }

    private static HistoricalMarketDataset Construct(HistoricalDatasetBuildRequest request, IReadOnlyList<DateOnly> dates, IReadOnlyList<HistoricalSourceSecurity> master,
        IReadOnlyDictionary<string, IReadOnlyList<HistoricalDailyPrice>> prices, IReadOnlyDictionary<(DateOnly Date, string Symbol), HistoricalTurnover> turnover,
        bool wantsTurnover, IReadOnlyList<HistoricalIndexDaily> index, IReadOnlyList<string> warnings)
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
        HistoricalObservationDeclaration[] declarations = universes.SelectMany(universe => universe.SecuritySymbols.Select(symbol => byDate.ContainsKey((universe.TradingDate, symbol))
            ? new HistoricalObservationDeclaration(universe.TradingDate, symbol, HistoricalSecurityStatus.Active, HistoricalObservationStatus.Available, "Tushare daily observed")
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
        return new HistoricalMarketDataset(request.DatasetId, dates, quotes, klines, contexts,
            priceAdjustmentMode: "Raw", source: "tushare", schemaVersion: HistoricalDatasetJsonLoader.CurrentSchemaVersion,
            metadata: new HistoricalDatasetMetadata(request.DatasetId, "tushare", DateTimeOffset.UtcNow, HistoricalUniverseQuality.Partial, warnings), securities: securities,
            universes: universes, fieldCapabilities: fieldCapabilities,
            priceSeriesProvenance: klines.Select(series => new HistoricalPriceSeriesProvenance(series.Symbol, "tushare:daily", HistoricalPriceAdjustmentMode.Raw,
                DateOnly.FromDateTime(series.Bars.First().Date), DateOnly.FromDateTime(series.Bars.Last().Date))), marketContextProvenance: contextProvenance,
            observationDeclarations: declarations);
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
}
