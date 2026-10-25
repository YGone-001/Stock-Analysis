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

/// <summary>V2 portfolio performance and realized-trade metrics; annualized statistics require at least two equity points.</summary>
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
        double? winRate,
        double? annualizedReturnPercent = null,
        double? annualizedVolatilityPercent = null,
        double? sharpeRatio = null,
        double? sortinoRatio = null,
        double? profitLossRatio = null,
        int? maximumDrawdownRecoveryTradingDays = null,
        double? calmarRatio = null)
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
        if (annualizedReturnPercent.HasValue && !double.IsFinite(annualizedReturnPercent.Value)) throw new ArgumentOutOfRangeException(nameof(annualizedReturnPercent));
        if (annualizedVolatilityPercent.HasValue && (!double.IsFinite(annualizedVolatilityPercent.Value) || annualizedVolatilityPercent.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(annualizedVolatilityPercent));
        if (sharpeRatio.HasValue && !double.IsFinite(sharpeRatio.Value)) throw new ArgumentOutOfRangeException(nameof(sharpeRatio));
        if (sortinoRatio.HasValue && !double.IsFinite(sortinoRatio.Value)) throw new ArgumentOutOfRangeException(nameof(sortinoRatio));
        if (profitLossRatio.HasValue && (!double.IsFinite(profitLossRatio.Value) || profitLossRatio.Value <= 0)) throw new ArgumentOutOfRangeException(nameof(profitLossRatio));
        if (maximumDrawdownRecoveryTradingDays.HasValue && maximumDrawdownRecoveryTradingDays.Value < 0) throw new ArgumentOutOfRangeException(nameof(maximumDrawdownRecoveryTradingDays));
        if (calmarRatio.HasValue && !double.IsFinite(calmarRatio.Value)) throw new ArgumentOutOfRangeException(nameof(calmarRatio));
        if (annualizedVolatilityPercent.HasValue && !annualizedReturnPercent.HasValue) throw new ArgumentException("Annualized volatility requires annualized return.", nameof(annualizedVolatilityPercent));
        if (sharpeRatio.HasValue && !(annualizedVolatilityPercent > 0)) throw new ArgumentException("Sharpe ratio requires positive annualized volatility.", nameof(sharpeRatio));
        if (sortinoRatio.HasValue && !(annualizedVolatilityPercent > 0)) throw new ArgumentException("Sortino ratio requires positive annualized volatility.", nameof(sortinoRatio));
        if (profitLossRatio.HasValue && (winningTradeCount == 0 || losingTradeCount == 0)) throw new ArgumentException("Profit/loss ratio requires at least one winning and one losing trade.", nameof(profitLossRatio));
        if (maximumDrawdownRecoveryTradingDays is > 0 && maximumDrawdownPercent == 0) throw new ArgumentException("A positive recovery period requires a non-zero drawdown.", nameof(maximumDrawdownRecoveryTradingDays));
        if (calmarRatio.HasValue && (!annualizedReturnPercent.HasValue || maximumDrawdownPercent >= 0)) throw new ArgumentException("Calmar ratio requires annualized return and a non-zero drawdown.", nameof(calmarRatio));

        InitialCapital = initialCapital;
        FinalEquity = finalEquity;
        TotalReturnPercent = totalReturnPercent;
        MaximumDrawdownPercent = maximumDrawdownPercent;
        MaximumDrawdownDate = maximumDrawdownDate;
        TradeCount = tradeCount;
        WinningTradeCount = winningTradeCount;
        LosingTradeCount = losingTradeCount;
        WinRate = winRate;
        AnnualizedReturnPercent = annualizedReturnPercent;
        AnnualizedVolatilityPercent = annualizedVolatilityPercent;
        SharpeRatio = sharpeRatio;
        SortinoRatio = sortinoRatio;
        ProfitLossRatio = profitLossRatio;
        MaximumDrawdownRecoveryTradingDays = maximumDrawdownRecoveryTradingDays;
        CalmarRatio = calmarRatio;
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
    /// <summary>Geometric annualization of the full-period return over observed daily returns; null when fewer than one daily return exists.</summary>
    public double? AnnualizedReturnPercent { get; }
    /// <summary>Sample standard deviation of daily returns scaled by the square root of trading days per year; null when fewer than two daily returns exist.</summary>
    public double? AnnualizedVolatilityPercent { get; }
    /// <summary>Annualized mean daily return over sample volatility with a zero risk-free assumption; null when volatility is zero or unavailable.</summary>
    public double? SharpeRatio { get; }
    /// <summary>Annualized mean daily return over downside deviation against a zero target; null when no downside return exists.</summary>
    public double? SortinoRatio { get; }
    /// <summary>Average winning realized P&amp;L divided by the average losing magnitude; null when either side is empty.</summary>
    public double? ProfitLossRatio { get; }
    /// <summary>Trading days from the maximum-drawdown trough until equity first regains its prior peak; null when the peak is never regained, zero when no drawdown occurred.</summary>
    public int? MaximumDrawdownRecoveryTradingDays { get; }
    /// <summary>Annualized return percent divided by the absolute maximum drawdown percent; null without a non-zero drawdown.</summary>
    public double? CalmarRatio { get; }
}

/// <summary>Immutable benchmark valuation point normalized to the portfolio's initial capital.</summary>
public sealed class PortfolioBenchmarkPoint
{
    public PortfolioBenchmarkPoint(DateOnly date, double benchmarkClose, decimal benchmarkEquity, double? dailyReturn, double cumulativeReturn)
    {
        if (benchmarkClose <= 0 || !double.IsFinite(benchmarkClose)) throw new ArgumentOutOfRangeException(nameof(benchmarkClose));
        if (benchmarkEquity <= 0) throw new ArgumentOutOfRangeException(nameof(benchmarkEquity));
        if (dailyReturn.HasValue && !double.IsFinite(dailyReturn.Value)) throw new ArgumentOutOfRangeException(nameof(dailyReturn));
        if (!double.IsFinite(cumulativeReturn)) throw new ArgumentOutOfRangeException(nameof(cumulativeReturn));
        Date = date;
        BenchmarkClose = benchmarkClose;
        BenchmarkEquity = benchmarkEquity;
        DailyReturn = dailyReturn;
        CumulativeReturn = cumulativeReturn;
    }

    public DateOnly Date { get; }
    public double BenchmarkClose { get; }
    public decimal BenchmarkEquity { get; }
    /// <summary>Null for the first observation; subsequent values are decimal return ratios, not percentages.</summary>
    public double? DailyReturn { get; }
    public double CumulativeReturn { get; }
}

/// <summary>
/// Immutable portfolio-level benchmark overlay on the same initial-capital basis as the portfolio equity curve.
/// Excess return compares the portfolio's full-dataset return with the benchmark's own observation span; partial
/// benchmark coverage therefore compares different date spans and is disclosed through analyzer warnings.
/// </summary>
public sealed class PortfolioBenchmarkComparison
{
    public PortfolioBenchmarkComparison(
        string benchmarkId,
        string? displayName,
        string source,
        HistoricalBenchmarkProvenance provenance,
        IReadOnlyList<PortfolioBenchmarkPoint> points,
        decimal initialCapital,
        double totalReturnPercent,
        double excessReturnPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(points);
        provenance.Validate();
        if (initialCapital <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapital));
        if (points.Count == 0) throw new ArgumentException("Benchmark comparison requires at least one point.", nameof(points));
        if (points.Zip(points.Skip(1)).Any(pair => pair.First.Date >= pair.Second.Date))
            throw new ArgumentException("Benchmark comparison dates must be strictly ascending.", nameof(points));
        if (points[0].BenchmarkEquity != initialCapital) throw new ArgumentException("The first benchmark point must be normalized to the initial capital.", nameof(points));
        if (points[0].DailyReturn.HasValue) throw new ArgumentException("The first benchmark point must not carry a daily return.", nameof(points));
        if (!double.IsFinite(totalReturnPercent)) throw new ArgumentOutOfRangeException(nameof(totalReturnPercent));
        if (!double.IsFinite(excessReturnPercent)) throw new ArgumentOutOfRangeException(nameof(excessReturnPercent));

        BenchmarkId = benchmarkId;
        DisplayName = displayName;
        Source = source;
        Provenance = provenance;
        Points = Array.AsReadOnly(points.ToArray());
        InitialCapital = initialCapital;
        TotalReturnPercent = totalReturnPercent;
        ExcessReturnPercent = excessReturnPercent;
    }

    public string BenchmarkId { get; }
    public string? DisplayName { get; }
    public string Source { get; }
    public HistoricalBenchmarkProvenance Provenance { get; }
    public IReadOnlyList<PortfolioBenchmarkPoint> Points { get; }
    public decimal InitialCapital { get; }
    public double TotalReturnPercent { get; }
    /// <summary>Portfolio total return percent minus benchmark total return percent.</summary>
    public double ExcessReturnPercent { get; }
}

/// <summary>Immutable downstream performance analysis. It retains, but never mutates, its simulation evidence.</summary>
public sealed class SparrowPortfolioPerformanceResult
{
    public SparrowPortfolioPerformanceResult(
        SparrowPortfolioSimulationResult simulationResultReference,
        PortfolioEquityCurve equityCurve,
        PortfolioPerformanceMetrics metrics,
        IEnumerable<PortfolioAttribution>? attribution = null,
        IEnumerable<string>? warnings = null,
        PortfolioBenchmarkComparison? benchmarkComparison = null)
    {
        ArgumentNullException.ThrowIfNull(simulationResultReference);
        ArgumentNullException.ThrowIfNull(equityCurve);
        ArgumentNullException.ThrowIfNull(metrics);
        SimulationResultReference = simulationResultReference;
        EquityCurve = equityCurve;
        Metrics = metrics;
        Attribution = Array.AsReadOnly((attribution ?? Array.Empty<PortfolioAttribution>()).ToArray());
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).ToArray());
        BenchmarkComparison = benchmarkComparison;
    }

    public SparrowPortfolioSimulationResult SimulationResultReference { get; }
    public PortfolioEquityCurve EquityCurve { get; }
    public PortfolioPerformanceMetrics Metrics { get; }
    public IReadOnlyList<PortfolioAttribution> Attribution { get; }
    public IReadOnlyList<string> Warnings { get; }
    /// <summary>Portfolio-level benchmark overlay; null when no benchmark was requested or none is usable.</summary>
    public PortfolioBenchmarkComparison? BenchmarkComparison { get; }
}
