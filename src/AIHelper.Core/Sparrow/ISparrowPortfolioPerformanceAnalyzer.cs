namespace AIHelper.Core.Sparrow;

/// <summary>Read-only downstream portfolio performance analysis boundary.</summary>
public interface ISparrowPortfolioPerformanceAnalyzer
{
    Task<SparrowPortfolioPerformanceResult> AnalyzeAsync(
        SparrowPortfolioSimulationResult simulationResult,
        HistoricalMarketDataset dataset,
        CancellationToken cancellationToken = default);
}
