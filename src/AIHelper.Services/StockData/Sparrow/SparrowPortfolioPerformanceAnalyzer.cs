using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Pure downstream valuation and realized-position analysis. It never changes simulation evidence,
/// calls a provider, or duplicates strategy and benchmark calculation logic.
/// </summary>
public sealed class SparrowPortfolioPerformanceAnalyzer : ISparrowPortfolioPerformanceAnalyzer
{
    public Task<SparrowPortfolioPerformanceResult> AnalyzeAsync(
        SparrowPortfolioSimulationResult simulationResult,
        HistoricalMarketDataset dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(simulationResult);
        ArgumentNullException.ThrowIfNull(dataset);
        if (!string.Equals(simulationResult.Request.DatasetId, dataset.DatasetId, StringComparison.Ordinal)
            || !string.Equals(simulationResult.DatasetFingerprint, dataset.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Simulation result does not belong to the supplied dataset.", nameof(simulationResult));

        Dictionary<DateOnly, IReadOnlyList<PortfolioTrade>> tradesByDate = GroupTrades(simulationResult.Trades, dataset);
        var warnings = new List<string>(simulationResult.Warnings);
        var points = new List<PortfolioEquityPoint>(dataset.TradingDates.Count);
        decimal cash = simulationResult.Request.InitialCapital;
        decimal? previousEquity = null;

        foreach (DateOnly date in dataset.TradingDates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tradesByDate.TryGetValue(date, out IReadOnlyList<PortfolioTrade>? dateTrades))
            {
                foreach (PortfolioTrade trade in dateTrades)
                    cash += trade.Side == PortfolioTradeSide.Buy ? -(trade.Notional + trade.Fee) : trade.Notional - trade.Fee;
            }

            if (cash < 0) throw new InvalidOperationException("Simulation trades produce negative cash during performance analysis.");
            decimal marketValue = MarketValue(simulationResult.Positions, dataset, date);
            decimal totalEquity = cash + marketValue;
            double? dailyReturn = previousEquity.HasValue && previousEquity.Value > 0
                ? (double)(totalEquity / previousEquity.Value - 1m)
                : null;
            double cumulativeReturn = (double)(totalEquity / simulationResult.Request.InitialCapital - 1m);
            points.Add(new PortfolioEquityPoint(date, cash, marketValue, totalEquity, dailyReturn, cumulativeReturn));
            previousEquity = totalEquity;
        }

        PortfolioEquityCurve curve = new(points);
        IReadOnlyList<PortfolioAttribution> attribution = BuildAttribution(simulationResult.Positions, simulationResult.Request.InitialCapital, dataset, warnings);
        PortfolioPerformanceMetrics metrics = BuildMetrics(curve, attribution, simulationResult.Request.InitialCapital);
        return Task.FromResult(new SparrowPortfolioPerformanceResult(simulationResult, curve, metrics, attribution, warnings));
    }

    private static Dictionary<DateOnly, IReadOnlyList<PortfolioTrade>> GroupTrades(IReadOnlyList<PortfolioTrade> trades, HistoricalMarketDataset dataset)
    {
        var groups = new Dictionary<DateOnly, List<PortfolioTrade>>();
        foreach (PortfolioTrade trade in trades)
        {
            if (!dataset.TradingDates.Contains(trade.TradeDate))
                throw new ArgumentException("Simulation trade date is not a dataset trading date.", nameof(trades));
            if (!groups.TryGetValue(trade.TradeDate, out List<PortfolioTrade>? dateTrades))
            {
                dateTrades = new List<PortfolioTrade>();
                groups.Add(trade.TradeDate, dateTrades);
            }
            dateTrades.Add(trade);
        }
        return groups.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<PortfolioTrade>)pair.Value.AsReadOnly());
    }

    private static decimal MarketValue(IReadOnlyList<PortfolioPosition> positions, HistoricalMarketDataset dataset, DateOnly date)
    {
        decimal value = 0;
        foreach (PortfolioPosition position in positions)
        {
            if (position.EntryDate > date || (position.Status == PortfolioPositionStatus.Closed && position.ExitDate <= date))
                continue;
            if (!TryGetClose(dataset, position.Symbol, date, out decimal close))
                throw new InvalidOperationException($"No historical close is available for open position '{position.Symbol}' on {date:O}.");
            value += close * position.Quantity;
        }
        return value;
    }

    private static IReadOnlyList<PortfolioAttribution> BuildAttribution(IReadOnlyList<PortfolioPosition> positions, decimal initialCapital, HistoricalMarketDataset dataset, ICollection<string> warnings)
    {
        var attribution = new List<PortfolioAttribution>();
        foreach (PortfolioPosition position in positions.Where(position => position.Status == PortfolioPositionStatus.Closed)
                     .OrderBy(position => position.ExitDate).ThenBy(position => position.Symbol, StringComparer.Ordinal))
        {
            if (!position.ExitDate.HasValue || !position.RealizedPnL.HasValue || !position.ReturnPercent.HasValue)
                throw new InvalidOperationException("A closed portfolio position is missing required realized facts.");
            int entryIndex = IndexOf(dataset.TradingDates, position.EntryDate);
            int exitIndex = IndexOf(dataset.TradingDates, position.ExitDate.Value);
            if (entryIndex < 0 || exitIndex < 0)
            {
                warnings.Add($"{position.Symbol}: AttributionUnavailableBecauseLifecycleDateIsOutsideDataset.");
                continue;
            }
            attribution.Add(new PortfolioAttribution(position.Symbol, position.EntryDate, position.ExitDate.Value, exitIndex - entryIndex,
                position.Quantity, position.RealizedPnL.Value, position.ReturnPercent.Value,
                (double)(position.RealizedPnL.Value / initialCapital * 100m), position.RealizedPnL.Value > 0));
        }
        return attribution.AsReadOnly();
    }

    private static PortfolioPerformanceMetrics BuildMetrics(PortfolioEquityCurve curve, IReadOnlyList<PortfolioAttribution> attribution, decimal initialCapital)
    {
        decimal peak = curve.Points[0].TotalEquity;
        decimal finalEquity = curve.Points[^1].TotalEquity;
        double maximumDrawdownPercent = 0;
        DateOnly maximumDrawdownDate = curve.Points[0].Date;
        foreach (PortfolioEquityPoint point in curve.Points)
        {
            if (point.TotalEquity > peak) peak = point.TotalEquity;
            double drawdownPercent = peak > 0 ? (double)((point.TotalEquity - peak) / peak * 100m) : 0;
            if (drawdownPercent < maximumDrawdownPercent)
            {
                maximumDrawdownPercent = drawdownPercent;
                maximumDrawdownDate = point.Date;
            }
        }
        int tradeCount = attribution.Count;
        int winning = attribution.Count(item => item.RealizedPnL > 0);
        int losing = attribution.Count(item => item.RealizedPnL < 0);
        return new PortfolioPerformanceMetrics(initialCapital, finalEquity, (double)(finalEquity / initialCapital * 100m - 100m),
            maximumDrawdownPercent, maximumDrawdownDate, tradeCount, winning, losing,
            tradeCount == 0 ? null : winning / (double)tradeCount);
    }

    private static bool TryGetClose(HistoricalMarketDataset dataset, string symbol, DateOnly date, out decimal close)
    {
        close = default;
        if (!dataset.Klines.TryGetValue(symbol, out KlineSeries? series)) return false;
        KlineBar? bar = series.Bars
            .SingleOrDefault(item => DateOnly.FromDateTime(item.Date) == date && item.Close is > 0 && double.IsFinite(item.Close.Value));
        if (bar?.Close is not double value) return false;
        try { close = (decimal)value; }
        catch (OverflowException) { return false; }
        return true;
    }

    private static int IndexOf(IReadOnlyList<DateOnly> dates, DateOnly date)
    {
        for (int index = 0; index < dates.Count; index++) if (dates[index] == date) return index;
        return -1;
    }
}
