namespace AIHelper.Core.Sparrow;

/// <summary>Immutable daily valuation sequence derived from a completed portfolio simulation.</summary>
public sealed class PortfolioEquityCurve
{
    public PortfolioEquityCurve(IEnumerable<PortfolioEquityPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        PortfolioEquityPoint[] copied = points.ToArray();
        if (copied.Length == 0) throw new ArgumentException("Equity curve requires at least one point.", nameof(points));
        if (copied.Zip(copied.Skip(1)).Any(pair => pair.First.Date >= pair.Second.Date))
            throw new ArgumentException("Equity curve dates must be strictly ascending.", nameof(points));
        Points = Array.AsReadOnly(copied);
    }

    public IReadOnlyList<PortfolioEquityPoint> Points { get; }
}

/// <summary>V1 non-annualized portfolio performance and realized-trade metrics.</summary>
public sealed class PortfolioPerformanceMetrics
{
    public PortfolioPerformanceMetrics(
        decimal initialCapital,
        decimal finalEquity,
        double totalReturnPercent,
        double maximumDrawdownPercent,
        DateOnly maximumDrawdownDate,
        int tradeCount,
        int winningTradeCount,
        int losingTradeCount,
        double? winRate)
    {
        if (initialCapital <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapital));
        if (finalEquity < 0) throw new ArgumentOutOfRangeException(nameof(finalEquity));
        if (!double.IsFinite(totalReturnPercent)) throw new ArgumentOutOfRangeException(nameof(totalReturnPercent));
        if (!double.IsFinite(maximumDrawdownPercent) || maximumDrawdownPercent > 0) throw new ArgumentOutOfRangeException(nameof(maximumDrawdownPercent));
        if (tradeCount < 0 || winningTradeCount < 0 || losingTradeCount < 0 || winningTradeCount + losingTradeCount > tradeCount)
            throw new ArgumentOutOfRangeException(nameof(tradeCount));
        if (winRate.HasValue && (!double.IsFinite(winRate.Value) || winRate.Value < 0 || winRate.Value > 1)) throw new ArgumentOutOfRangeException(nameof(winRate));
        if (tradeCount == 0 && winRate.HasValue) throw new ArgumentException("Win rate is unavailable when no trades are closed.", nameof(winRate));
        if (tradeCount > 0 && !winRate.HasValue) throw new ArgumentException("Win rate is required when trades are closed.", nameof(winRate));

        InitialCapital = initialCapital;
        FinalEquity = finalEquity;
        TotalReturnPercent = totalReturnPercent;
        MaximumDrawdownPercent = maximumDrawdownPercent;
        MaximumDrawdownDate = maximumDrawdownDate;
        TradeCount = tradeCount;
        WinningTradeCount = winningTradeCount;
        LosingTradeCount = losingTradeCount;
        WinRate = winRate;
    }

    public decimal InitialCapital { get; }
    public decimal FinalEquity { get; }
    public double TotalReturnPercent { get; }
    public double MaximumDrawdownPercent { get; }
    public DateOnly MaximumDrawdownDate { get; }
    /// <summary>Number of closed positions; open positions do not have realized P&amp;L.</summary>
    public int TradeCount { get; }
    public int WinningTradeCount { get; }
    public int LosingTradeCount { get; }
    /// <summary>Winning closed positions divided by all closed positions; null when no position is closed.</summary>
    public double? WinRate { get; }
}

/// <summary>Immutable downstream performance analysis. It retains, but never mutates, its simulation evidence.</summary>
public sealed class SparrowPortfolioPerformanceResult
{
    public SparrowPortfolioPerformanceResult(
        SparrowPortfolioSimulationResult simulationResultReference,
        PortfolioEquityCurve equityCurve,
        PortfolioPerformanceMetrics metrics,
        IEnumerable<PortfolioAttribution>? attribution = null,
        IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(simulationResultReference);
        ArgumentNullException.ThrowIfNull(equityCurve);
        ArgumentNullException.ThrowIfNull(metrics);
        SimulationResultReference = simulationResultReference;
        EquityCurve = equityCurve;
        Metrics = metrics;
        Attribution = Array.AsReadOnly((attribution ?? Array.Empty<PortfolioAttribution>()).ToArray());
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).ToArray());
    }

    public SparrowPortfolioSimulationResult SimulationResultReference { get; }
    public PortfolioEquityCurve EquityCurve { get; }
    public PortfolioPerformanceMetrics Metrics { get; }
    public IReadOnlyList<PortfolioAttribution> Attribution { get; }
    public IReadOnlyList<string> Warnings { get; }
}
