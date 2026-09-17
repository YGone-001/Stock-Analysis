namespace AIHelper.Core.Sparrow;

/// <summary>
/// Offline boundary for turning an already-computed historical backtest selection stream into
/// deterministic portfolio trades and positions. It never participates in strategy evaluation.
/// </summary>
public interface ISparrowPortfolioSimulationEngine
{
    Task<SparrowPortfolioSimulationResult> SimulateAsync(
        SparrowBacktestResult backtestResult,
        PortfolioSimulationRequest request,
        HistoricalMarketDataset dataset,
        CancellationToken cancellationToken = default);
}
