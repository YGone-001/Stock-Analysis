using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowPortfolioPerformanceAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_BuildsDailyEquityCurveReturnsDrawdownAndAttribution()
    {
        DateOnly[] dates = Dates(4);
        HistoricalMarketDataset dataset = Dataset(dates, new[] { 100d, 120d, 90d, 130d });
        PortfolioPosition position = ClosedPosition(dates[0], dates[3], 1, 100, 130, 30, 30);
        SparrowPortfolioSimulationResult simulation = Simulation(dataset, 100, new[]
        {
            new PortfolioTrade("600000", dates[0], PortfolioTradeSide.Buy, 100, 1, 100, 0),
            new PortfolioTrade("600000", dates[3], PortfolioTradeSide.Sell, 130, 1, 130, 0)
        }, new[] { position });

        SparrowPortfolioPerformanceResult result = await Analyze(simulation, dataset);

        Assert.Equal(new[] { 100m, 120m, 90m, 130m }, result.EquityCurve.Points.Select(point => point.TotalEquity));
        Assert.Null(result.EquityCurve.Points[0].DailyReturn);
        Assert.Equal(.2, result.EquityCurve.Points[1].DailyReturn!.Value, 10);
        Assert.Equal(-.25, result.EquityCurve.Points[2].DailyReturn!.Value, 10);
        Assert.Equal(.3, result.EquityCurve.Points[^1].CumulativeReturn, 10);
        Assert.Equal(30d, result.Metrics.TotalReturnPercent, 10);
        Assert.Equal(-25d, result.Metrics.MaximumDrawdownPercent, 10);
        Assert.Equal(dates[2], result.Metrics.MaximumDrawdownDate);
        PortfolioAttribution attribution = Assert.Single(result.Attribution);
        Assert.Equal(3, attribution.HoldingPeriodTradingDays);
        Assert.Equal(30m, attribution.RealizedPnL);
        Assert.Equal(30d, attribution.ContributionPercent, 10);
    }

    [Fact]
    public async Task AnalyzeAsync_UsesActualDailyPortfolioEquityWithoutFabricatingFirstDayReturn()
    {
        DateOnly[] dates = Dates(3);
        HistoricalMarketDataset dataset = Dataset(dates, new[] { 100d, 110d, 90d });
        SparrowPortfolioSimulationResult simulation = Simulation(dataset, 1_000_000, new[]
        {
            new PortfolioTrade("600000", dates[0], PortfolioTradeSide.Buy, 100, 10_000, 1_000_000, 0),
            new PortfolioTrade("600000", dates[2], PortfolioTradeSide.Sell, 90, 10_000, 900_000, 0)
        }, new[] { ClosedPosition(dates[0], dates[2], 10_000, 100, 90, -100_000, -10) });

        SparrowPortfolioPerformanceResult result = await Analyze(simulation, dataset);

        Assert.Equal(1_000_000m, result.EquityCurve.Points[0].TotalEquity);
        Assert.Equal(1_100_000m, result.EquityCurve.Points[1].TotalEquity);
        Assert.Equal(900_000m, result.EquityCurve.Points[2].TotalEquity);
        Assert.Null(result.EquityCurve.Points[0].DailyReturn);
        Assert.Equal(.1, result.EquityCurve.Points[1].DailyReturn!.Value, 10);
        Assert.Equal(-.1818181818181818, result.EquityCurve.Points[2].DailyReturn!.Value, 10);
        Assert.Equal(900_000m, result.Metrics.FinalEquity);
        Assert.Equal(-10d, result.Metrics.TotalReturnPercent, 10);
    }

    [Fact]
    public async Task AnalyzeAsync_ComputesRealizedWinRateAndHandlesNoClosedTrades()
    {
        DateOnly[] dates = Dates(2);
        HistoricalMarketDataset dataset = Dataset(dates, new[] { 100d, 100d });
        PortfolioPosition[] positions = new[]
        {
            ClosedPosition(dates[0], dates[1], 1, 100, 110, 10, 10),
            ClosedPosition(dates[0], dates[1], 1, 100, 110, 5, 5),
            ClosedPosition(dates[0], dates[1], 1, 100, 110, 1, 1),
            ClosedPosition(dates[0], dates[1], 1, 100, 90, -2, -2),
            ClosedPosition(dates[0], dates[1], 1, 100, 90, -4, -4)
        };
        SparrowPortfolioPerformanceResult result = await Analyze(Simulation(dataset, 1_000, Array.Empty<PortfolioTrade>(), positions), dataset);

        Assert.Equal(5, result.Metrics.TradeCount);
        Assert.Equal(3, result.Metrics.WinningTradeCount);
        Assert.Equal(2, result.Metrics.LosingTradeCount);
        Assert.Equal(.6, result.Metrics.WinRate!.Value, 10);
        Assert.Equal(5, result.Attribution.Count);

        SparrowPortfolioPerformanceResult openOnly = await Analyze(Simulation(dataset, 1_000, Array.Empty<PortfolioTrade>(), new[]
        {
            new PortfolioPosition("600000", dates[0], 100, 1, 100, 0, PortfolioPositionStatus.Open)
        }), dataset);
        Assert.Equal(0, openOnly.Metrics.TradeCount);
        Assert.Null(openOnly.Metrics.WinRate);
        Assert.Empty(openOnly.Attribution);
    }

    [Fact]
    public async Task AnalyzeAsync_IsValidWithoutBenchmarkSeries()
    {
        DateOnly[] dates = Dates(2);
        HistoricalMarketDataset dataset = Dataset(dates, new[] { 100d, 110d });
        SparrowPortfolioPerformanceResult result = await Analyze(Simulation(dataset, 1_000, Array.Empty<PortfolioTrade>(), Array.Empty<PortfolioPosition>()), dataset);

        Assert.Empty(dataset.Benchmarks);
        Assert.Equal(2, result.EquityCurve.Points.Count);
        Assert.Empty(result.Attribution);
    }

    [Fact]
    public async Task AnalyzeAsync_FuturePricesChangeOnlyFuturePerformanceAndNeverMutateSimulationEvidence()
    {
        DateOnly[] dates = Dates(3);
        HistoricalMarketDataset baseline = Dataset(dates, new[] { 100d, 110d, 120d });
        HistoricalMarketDataset changed = Dataset(dates, new[] { 100d, 110d, 1_200d });
        PortfolioTrade[] baselineTrades =
        {
            new("600000", dates[0], PortfolioTradeSide.Buy, 100, 10, 1_000, 0),
            new("600000", dates[2], PortfolioTradeSide.Sell, 120, 10, 1_200, 0)
        };
        PortfolioPosition[] baselinePositions = { ClosedPosition(dates[0], dates[2], 10, 100, 120, 200, 20) };
        SparrowPortfolioSimulationResult firstSimulation = Simulation(baseline, 1_000, baselineTrades, baselinePositions);
        SparrowPortfolioSimulationResult secondSimulation = Simulation(changed, 1_000,
            new[] { new PortfolioTrade("600000", dates[0], PortfolioTradeSide.Buy, 100, 10, 1_000, 0), new PortfolioTrade("600000", dates[2], PortfolioTradeSide.Sell, 1_200, 10, 12_000, 0) },
            new[] { ClosedPosition(dates[0], dates[2], 10, 100, 1_200, 11_000, 1_100) });

        (string Symbol, DateOnly Date, PortfolioTradeSide Side, decimal Price, long Quantity)[] originalTrades = firstSimulation.Trades
            .Select(trade => (trade.Symbol, trade.TradeDate, trade.Side, trade.Price, trade.Quantity)).ToArray();
        (string Symbol, DateOnly Entry, DateOnly? Exit, long Quantity, decimal? PnL)[] originalPositions = firstSimulation.Positions
            .Select(position => (position.Symbol, position.EntryDate, position.ExitDate, position.Quantity, position.RealizedPnL)).ToArray();

        SparrowPortfolioPerformanceResult first = await Analyze(firstSimulation, baseline);
        SparrowPortfolioPerformanceResult second = await Analyze(secondSimulation, changed);

        Assert.Equal(originalTrades, firstSimulation.Trades.Select(trade => (trade.Symbol, trade.TradeDate, trade.Side, trade.Price, trade.Quantity)));
        Assert.Equal(originalPositions, firstSimulation.Positions.Select(position => (position.Symbol, position.EntryDate, position.ExitDate, position.Quantity, position.RealizedPnL)));
        Assert.Equal(firstSimulation.Trades.Where(trade => trade.Side == PortfolioTradeSide.Buy).Select(trade => (trade.Symbol, trade.TradeDate, trade.Price, trade.Quantity)),
            secondSimulation.Trades.Where(trade => trade.Side == PortfolioTradeSide.Buy).Select(trade => (trade.Symbol, trade.TradeDate, trade.Price, trade.Quantity)));
        Assert.Equal(firstSimulation.Positions.Select(position => (position.Symbol, position.EntryDate, position.Quantity)),
            secondSimulation.Positions.Select(position => (position.Symbol, position.EntryDate, position.Quantity)));
        Assert.Equal(first.EquityCurve.Points.Take(2).Select(point => point.TotalEquity), second.EquityCurve.Points.Take(2).Select(point => point.TotalEquity));
        Assert.NotEqual(first.Metrics.FinalEquity, second.Metrics.FinalEquity);
    }

    [Fact]
    public async Task AnalyzeAsync_IsDeterministicAndDoesNotDependOnProviders()
    {
        DateOnly[] dates = Dates(2);
        HistoricalMarketDataset dataset = Dataset(dates, new[] { 100d, 110d });
        SparrowPortfolioSimulationResult simulation = Simulation(dataset, 100, new[]
        {
            new PortfolioTrade("600000", dates[0], PortfolioTradeSide.Buy, 100, 1, 100, 0),
            new PortfolioTrade("600000", dates[1], PortfolioTradeSide.Sell, 110, 1, 110, 0)
        }, new[] { ClosedPosition(dates[0], dates[1], 1, 100, 110, 10, 10) });

        SparrowPortfolioPerformanceResult first = await Analyze(simulation, dataset);
        SparrowPortfolioPerformanceResult second = await Analyze(simulation, dataset);

        Assert.Equal(first.EquityCurve.Points.Select(point => (point.Date, point.Cash, point.MarketValue, point.TotalEquity, point.DailyReturn, point.CumulativeReturn)),
            second.EquityCurve.Points.Select(point => (point.Date, point.Cash, point.MarketValue, point.TotalEquity, point.DailyReturn, point.CumulativeReturn)));
        Assert.Empty(typeof(SparrowPortfolioPerformanceAnalyzer).GetConstructors().Single().GetParameters());
    }

    private static async Task<SparrowPortfolioPerformanceResult> Analyze(SparrowPortfolioSimulationResult simulation, HistoricalMarketDataset dataset) =>
        await new SparrowPortfolioPerformanceAnalyzer().AnalyzeAsync(simulation, dataset, CancellationToken.None);

    private static PortfolioPosition ClosedPosition(DateOnly entry, DateOnly exit, long quantity, decimal entryPrice, decimal exitPrice, decimal pnl, double returnPercent) =>
        new("600000", entry, entryPrice, quantity, entryPrice * quantity, 0, PortfolioPositionStatus.Closed,
            exit, exitPrice, exitPrice * quantity, 0, pnl, returnPercent);

    private static SparrowPortfolioSimulationResult Simulation(HistoricalMarketDataset dataset, decimal initialCapital, IReadOnlyList<PortfolioTrade> trades, IReadOnlyList<PortfolioPosition> positions)
    {
        PortfolioSimulationRequest request = new(dataset.DatasetId, dataset.Fingerprint, SparrowStrategyMode.V2, SparrowStrategyVersions.V2,
            "performance-fixture", dataset.TradingDates[0], dataset.TradingDates[^1], 10, 1, initialCapital,
            PortfolioPositionSizingMethod.EqualWeight, 0, 0);
        return new SparrowPortfolioSimulationResult(request, dataset.Fingerprint, trades, positions);
    }

    private static DateOnly[] Dates(int count) => Enumerable.Range(0, count).Select(index => new DateOnly(2026, 1, 2).AddDays(index)).ToArray();

    private static HistoricalMarketDataset Dataset(IReadOnlyList<DateOnly> dates, IReadOnlyList<double> closes) => new(
        "performance-fixture", dates, Array.Empty<HistoricalQuoteObservation>(),
        new[] { new KlineSeries("600000", closes.Select((close, index) => new KlineBar(dates[index].ToDateTime(TimeOnly.MinValue), null, null, null, close, null, null, null, null, null)).ToArray()) },
        source: "InMemory");
}
