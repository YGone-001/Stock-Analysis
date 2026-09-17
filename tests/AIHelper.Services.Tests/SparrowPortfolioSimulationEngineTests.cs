using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowPortfolioSimulationEngineTests
{
    [Fact]
    public async Task SimulateAsync_AllocatesAvailableCapitalEquallyAndCreatesCloseBasedTrades()
    {
        DateOnly entry = new(2026, 1, 2); DateOnly exit = new(2026, 1, 5);
        string[] symbols = Enumerable.Range(0, 10).Select(index => $"6000{index:00}").ToArray();
        HistoricalMarketDataset dataset = Dataset(new[] { entry, exit }, symbols.ToDictionary(symbol => symbol, _ => new[] { 100d, 110d }));
        SparrowBacktestResult backtest = Backtest(dataset, symbols.Select((symbol, index) => Selection(entry, symbol, index + 1)));

        SparrowPortfolioSimulationResult result = await Simulate(dataset, backtest, horizon: 1);

        PortfolioTrade[] buys = result.Trades.Where(trade => trade.Side == PortfolioTradeSide.Buy).ToArray();
        Assert.Equal(10, buys.Length);
        Assert.All(buys, trade =>
        {
            Assert.Equal(entry, trade.TradeDate);
            Assert.Equal(100m, trade.Price);
            Assert.Equal(1_000, trade.Quantity);
            Assert.Equal(100_000m, trade.Notional);
            Assert.Equal(0m, trade.Fee);
        });
        Assert.Equal(symbols, buys.Select(trade => trade.Symbol));
        Assert.Equal(10, result.Trades.Count(trade => trade.Side == PortfolioTradeSide.Sell));
        Assert.All(result.Positions, position => Assert.Equal(PortfolioPositionStatus.Closed, position.Status));
    }

    [Fact]
    public async Task SimulateAsync_FloorsQuantityAndAppliesExplicitSlippageAndCommission()
    {
        DateOnly entry = new(2026, 1, 2); DateOnly exit = new(2026, 1, 5);
        HistoricalMarketDataset floorDataset = Dataset(new[] { entry, exit }, new Dictionary<string, double[]> { ["600000"] = new[] { 123d, 123d } });
        SparrowBacktestResult floorBacktest = Backtest(floorDataset, new[] { Selection(entry, "600000", 1) });
        SparrowPortfolioSimulationResult floored = await Simulate(floorDataset, floorBacktest, initialCapital: 100_000, horizon: 1);
        Assert.Equal(813, Assert.Single(floored.Trades, trade => trade.Side == PortfolioTradeSide.Buy).Quantity);

        HistoricalMarketDataset costDataset = Dataset(new[] { entry, exit }, new Dictionary<string, double[]> { ["600000"] = new[] { 100d, 200d } });
        SparrowBacktestResult costBacktest = Backtest(costDataset, new[] { Selection(entry, "600000", 1) });
        SparrowPortfolioSimulationResult costed = await Simulate(costDataset, costBacktest, initialCapital: 10_000, horizon: 1, commissionRate: .01m, slippageRate: .10m);

        PortfolioTrade buy = Assert.Single(costed.Trades, trade => trade.Side == PortfolioTradeSide.Buy);
        PortfolioTrade sell = Assert.Single(costed.Trades, trade => trade.Side == PortfolioTradeSide.Sell);
        Assert.Equal(110m, buy.Price); Assert.Equal(90, buy.Quantity); Assert.Equal(99m, buy.Fee);
        Assert.Equal(180m, sell.Price); Assert.Equal(162m, sell.Fee);
        Assert.Equal(6_039m, Assert.Single(costed.Positions).RealizedPnL);
    }

    [Fact]
    public async Task SimulateAsync_UsesDatasetTradingDateIndexRatherThanCalendarDays()
    {
        DateOnly friday = new(2026, 1, 2); DateOnly tuesday = new(2026, 1, 6);
        HistoricalMarketDataset dataset = Dataset(new[] { friday, tuesday }, new Dictionary<string, double[]> { ["600000"] = new[] { 100d, 110d } });
        SparrowBacktestResult backtest = Backtest(dataset, new[] { Selection(friday, "600000", 1) });

        SparrowPortfolioSimulationResult result = await Simulate(dataset, backtest, horizon: 1);

        PortfolioTrade sell = Assert.Single(result.Trades, trade => trade.Side == PortfolioTradeSide.Sell);
        Assert.Equal(tuesday, sell.TradeDate);
        Assert.Equal(tuesday, Assert.Single(result.Positions).ExitDate);
    }

    [Fact]
    public async Task SimulateAsync_IgnoresSignalForAlreadyOpenPosition()
    {
        DateOnly first = new(2026, 1, 2); DateOnly second = new(2026, 1, 5); DateOnly third = new(2026, 1, 6);
        HistoricalMarketDataset dataset = Dataset(new[] { first, second, third }, new Dictionary<string, double[]> { ["600000"] = new[] { 100d, 105d, 110d } });
        SparrowBacktestResult backtest = Backtest(dataset, new[] { Selection(first, "600000", 1), Selection(second, "600000", 1) });

        SparrowPortfolioSimulationResult result = await Simulate(dataset, backtest, horizon: 2);

        Assert.Single(result.Trades, trade => trade.Side == PortfolioTradeSide.Buy);
        Assert.Contains(result.Warnings, warning => warning.Contains("OpenPositionSignalIgnored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SimulateAsync_FuturePricesDoNotChangeSelectionsOrEntryTrades()
    {
        DateOnly entry = new(2026, 1, 2); DateOnly exit = new(2026, 1, 5);
        HistoricalMarketDataset baseline = Dataset(new[] { entry, exit }, new Dictionary<string, double[]> { ["600000"] = new[] { 100d, 110d } });
        HistoricalMarketDataset changedFuture = Dataset(new[] { entry, exit }, new Dictionary<string, double[]> { ["600000"] = new[] { 100d, 1_100d } });
        SparrowBacktestResult baselineBacktest = Backtest(baseline, new[] { Selection(entry, "600000", 1) });
        SparrowBacktestResult changedBacktest = Backtest(changedFuture, new[] { Selection(entry, "600000", 1) });

        SparrowPortfolioSimulationResult first = await Simulate(baseline, baselineBacktest, horizon: 1);
        SparrowPortfolioSimulationResult second = await Simulate(changedFuture, changedBacktest, horizon: 1);

        Assert.Equal(baselineBacktest.Selections.Select(selection => (selection.ReplayDate, selection.Selection.Code, selection.Selection.RankedCandidate.Rank, selection.Selection.RankedCandidate.TotalScore)),
            changedBacktest.Selections.Select(selection => (selection.ReplayDate, selection.Selection.Code, selection.Selection.RankedCandidate.Rank, selection.Selection.RankedCandidate.TotalScore)));
        Assert.Equal(first.Trades.Where(trade => trade.Side == PortfolioTradeSide.Buy).Select(trade => (trade.Symbol, trade.TradeDate, trade.Price, trade.Quantity)),
            second.Trades.Where(trade => trade.Side == PortfolioTradeSide.Buy).Select(trade => (trade.Symbol, trade.TradeDate, trade.Price, trade.Quantity)));
        Assert.NotEqual(Assert.Single(first.Positions).RealizedPnL, Assert.Single(second.Positions).RealizedPnL);
    }

    [Fact]
    public async Task SimulateAsync_IsDeterministicAndUsesOnlyPreloadedDatasetData()
    {
        DateOnly entry = new(2026, 1, 2); DateOnly exit = new(2026, 1, 5);
        HistoricalMarketDataset dataset = Dataset(new[] { entry, exit }, new Dictionary<string, double[]> { ["600000"] = new[] { 100d, 110d } });
        SparrowBacktestResult backtest = Backtest(dataset, new[] { Selection(entry, "600000", 1) });

        SparrowPortfolioSimulationResult first = await Simulate(dataset, backtest, horizon: 1);
        SparrowPortfolioSimulationResult second = await Simulate(dataset, backtest, horizon: 1);

        Assert.Equal(first.Trades.Select(trade => (trade.Symbol, trade.TradeDate, trade.Side, trade.Price, trade.Quantity, trade.Notional, trade.Fee)),
            second.Trades.Select(trade => (trade.Symbol, trade.TradeDate, trade.Side, trade.Price, trade.Quantity, trade.Notional, trade.Fee)));
        Assert.Equal(first.Positions.Select(position => (position.Symbol, position.EntryDate, position.ExitDate, position.Status, position.RealizedPnL, position.ReturnPercent)),
            second.Positions.Select(position => (position.Symbol, position.EntryDate, position.ExitDate, position.Status, position.RealizedPnL, position.ReturnPercent)));
        Assert.Empty(typeof(SparrowPortfolioSimulationEngine).GetConstructors().Single().GetParameters());
    }

    private static async Task<SparrowPortfolioSimulationResult> Simulate(HistoricalMarketDataset dataset, SparrowBacktestResult backtest, decimal initialCapital = 1_000_000, int horizon = 1, decimal commissionRate = 0, decimal slippageRate = 0)
    {
        PortfolioSimulationRequest request = new(dataset.DatasetId, dataset.Fingerprint, backtest.Request.StrategyMode, backtest.Request.StrategyVersion,
            backtest.ParameterFingerprint, backtest.Request.StartDate, backtest.Request.EndDate, backtest.Request.TopN, horizon, initialCapital,
            PortfolioPositionSizingMethod.EqualWeight, commissionRate, slippageRate);
        return await new SparrowPortfolioSimulationEngine().SimulateAsync(backtest, request, dataset, CancellationToken.None);
    }

    private static SparrowBacktestResult Backtest(HistoricalMarketDataset dataset, IEnumerable<SparrowBacktestSelection> selections)
    {
        DateOnly start = dataset.TradingDates[0]; DateOnly end = dataset.TradingDates[^1];
        SparrowBacktestRequest request = new(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, start, end, 10, new[] { 1 });
        return new SparrowBacktestResult(request, SparrowHistoricalFingerprint.Parameters(request), dataset.DatasetId, dataset.Fingerprint,
            selections.ToArray(), Array.Empty<SparrowHorizonMetrics>(), Array.Empty<string>());
    }

    private static SparrowBacktestSelection Selection(DateOnly date, string symbol, int rank)
    {
        SparrowRankingFeatures features = new() { Code = symbol, Name = symbol };
        SparrowRankedCandidate candidate = new()
        {
            RankingProfile = SparrowRankingProfileV1.Name,
            Code = symbol,
            Name = symbol,
            Rank = rank,
            TotalScore = 100 - rank,
            Features = features,
            IsDataComplete = true
        };
        return new SparrowBacktestSelection(date, new SparrowReplaySelection(symbol, symbol, candidate, "FIXTURE", "fixture"), Array.Empty<ForwardReturn>());
    }

    private static HistoricalMarketDataset Dataset(IReadOnlyList<DateOnly> dates, IReadOnlyDictionary<string, double[]> closes) => new(
        "portfolio-fixture",
        dates,
        Array.Empty<HistoricalQuoteObservation>(),
        closes.Select(pair => new KlineSeries(pair.Key, pair.Value.Select((close, index) =>
            new KlineBar(dates[index].ToDateTime(TimeOnly.MinValue), null, null, null, close, null, null, null, null, null)).ToArray())),
        source: "InMemory");
}
