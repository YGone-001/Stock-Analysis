using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

/// <summary>Lifecycle state for a simulated long-only portfolio position.</summary>
public enum PortfolioPositionStatus { Open, Closed }

/// <summary>Supported simulated transaction directions.</summary>
public enum PortfolioTradeSide { Buy, Sell }

/// <summary>Phase 3.4 V1 portfolio allocation method.</summary>
public enum PortfolioPositionSizingMethod { EqualWeight }

/// <summary>Phase 3.4 V1 execution assumption: historical close plus explicit slippage.</summary>
public enum PortfolioExecutionModel { CloseBased }

/// <summary>Immutable input identity and assumptions for a future deterministic portfolio simulation.</summary>
public sealed class PortfolioSimulationRequest
{
    public PortfolioSimulationRequest(
        string datasetId,
        string datasetFingerprint,
        SparrowStrategyMode strategyMode,
        string strategyVersion,
        string strategyParameterFingerprint,
        DateOnly startDate,
        DateOnly endDate,
        int topN,
        int horizonTradingDays,
        decimal initialCapital,
        PortfolioPositionSizingMethod positionSizingMethod,
        decimal commissionRate,
        decimal slippageRate,
        PortfolioExecutionModel executionModel = PortfolioExecutionModel.CloseBased)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyParameterFingerprint);
        if (!Enum.IsDefined(strategyMode)) throw new ArgumentOutOfRangeException(nameof(strategyMode));
        if (!Enum.IsDefined(positionSizingMethod)) throw new ArgumentOutOfRangeException(nameof(positionSizingMethod));
        if (!Enum.IsDefined(executionModel)) throw new ArgumentOutOfRangeException(nameof(executionModel));
        if (startDate > endDate) throw new ArgumentException("StartDate must not be after EndDate.");
        if (topN <= 0) throw new ArgumentOutOfRangeException(nameof(topN));
        if (horizonTradingDays <= 0) throw new ArgumentOutOfRangeException(nameof(horizonTradingDays));
        if (initialCapital <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapital));
        if (commissionRate < 0) throw new ArgumentOutOfRangeException(nameof(commissionRate));
        if (slippageRate < 0) throw new ArgumentOutOfRangeException(nameof(slippageRate));

        DatasetId = datasetId;
        DatasetFingerprint = datasetFingerprint;
        StrategyMode = strategyMode;
        StrategyVersion = strategyVersion;
        StrategyParameterFingerprint = strategyParameterFingerprint;
        StartDate = startDate;
        EndDate = endDate;
        TopN = topN;
        HorizonTradingDays = horizonTradingDays;
        InitialCapital = initialCapital;
        PositionSizingMethod = positionSizingMethod;
        CommissionRate = commissionRate;
        SlippageRate = slippageRate;
        ExecutionModel = executionModel;
    }

    public string DatasetId { get; }
    public string DatasetFingerprint { get; }
    public SparrowStrategyMode StrategyMode { get; }
    public string StrategyVersion { get; }
    public string StrategyParameterFingerprint { get; }
    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }
    public int TopN { get; }
    public int HorizonTradingDays { get; }
    public decimal InitialCapital { get; }
    public PortfolioPositionSizingMethod PositionSizingMethod { get; }
    public decimal CommissionRate { get; }
    public decimal SlippageRate { get; }
    public PortfolioExecutionModel ExecutionModel { get; }
}

/// <summary>Immutable simulated transaction. It contains no provider, broker, or execution-service dependency.</summary>
public sealed class PortfolioTrade
{
    public PortfolioTrade(string symbol, DateOnly tradeDate, PortfolioTradeSide side, decimal price, long quantity, decimal notional, decimal fee)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (notional <= 0) throw new ArgumentOutOfRangeException(nameof(notional));
        if (fee < 0) throw new ArgumentOutOfRangeException(nameof(fee));

        Symbol = symbol;
        TradeDate = tradeDate;
        Side = side;
        Price = price;
        Quantity = quantity;
        Notional = notional;
        Fee = fee;
    }

    public string Symbol { get; }
    public DateOnly TradeDate { get; }
    public PortfolioTradeSide Side { get; }
    public decimal Price { get; }
    public long Quantity { get; }
    public decimal Notional { get; }
    public decimal Fee { get; }
}

/// <summary>Immutable long-only simulated position. Closed positions require their essential exit facts.</summary>
public sealed class PortfolioPosition
{
    public PortfolioPosition(
        string symbol,
        DateOnly entryDate,
        decimal entryPrice,
        long quantity,
        decimal entryNotional,
        decimal entryFee,
        PortfolioPositionStatus status,
        DateOnly? exitDate = null,
        decimal? exitPrice = null,
        decimal? exitNotional = null,
        decimal? exitFee = null,
        decimal? realizedPnL = null,
        double? returnPercent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (entryPrice <= 0) throw new ArgumentOutOfRangeException(nameof(entryPrice));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (entryNotional <= 0) throw new ArgumentOutOfRangeException(nameof(entryNotional));
        if (entryFee < 0) throw new ArgumentOutOfRangeException(nameof(entryFee));
        if (exitPrice is <= 0) throw new ArgumentOutOfRangeException(nameof(exitPrice));
        if (exitNotional is <= 0) throw new ArgumentOutOfRangeException(nameof(exitNotional));
        if (exitFee is < 0) throw new ArgumentOutOfRangeException(nameof(exitFee));
        if (returnPercent.HasValue && !double.IsFinite(returnPercent.Value)) throw new ArgumentOutOfRangeException(nameof(returnPercent));
        if (exitDate.HasValue && exitDate.Value < entryDate) throw new ArgumentException("ExitDate must not precede EntryDate.");

        if (status == PortfolioPositionStatus.Open && (exitDate.HasValue || exitPrice.HasValue || exitNotional.HasValue || exitFee.HasValue || realizedPnL.HasValue || returnPercent.HasValue))
            throw new ArgumentException("An open position must not contain exit facts.");
        if (status == PortfolioPositionStatus.Closed && (!exitDate.HasValue || !exitPrice.HasValue || !realizedPnL.HasValue))
            throw new ArgumentException("A closed position requires ExitDate, ExitPrice, and RealizedPnL.");

        Symbol = symbol;
        EntryDate = entryDate;
        EntryPrice = entryPrice;
        Quantity = quantity;
        EntryNotional = entryNotional;
        EntryFee = entryFee;
        Status = status;
        ExitDate = exitDate;
        ExitPrice = exitPrice;
        ExitNotional = exitNotional;
        ExitFee = exitFee;
        RealizedPnL = realizedPnL;
        ReturnPercent = returnPercent;
    }

    public string Symbol { get; }
    public DateOnly EntryDate { get; }
    public decimal EntryPrice { get; }
    public long Quantity { get; }
    public decimal EntryNotional { get; }
    public decimal EntryFee { get; }
    public PortfolioPositionStatus Status { get; }
    public DateOnly? ExitDate { get; }
    public decimal? ExitPrice { get; }
    public decimal? ExitNotional { get; }
    public decimal? ExitFee { get; }
    public decimal? RealizedPnL { get; }
    public double? ReturnPercent { get; }
}

/// <summary>Immutable cash and marked-value snapshot; no pricing logic is embedded in this model.</summary>
public sealed class PortfolioSnapshot
{
    public PortfolioSnapshot(DateOnly date, decimal cash, decimal marketValue, decimal totalEquity)
    {
        if (cash < 0) throw new ArgumentOutOfRangeException(nameof(cash));
        if (marketValue < 0) throw new ArgumentOutOfRangeException(nameof(marketValue));
        if (totalEquity < 0) throw new ArgumentOutOfRangeException(nameof(totalEquity));
        if (totalEquity != cash + marketValue) throw new ArgumentException("TotalEquity must equal Cash plus MarketValue.");
        Date = date;
        Cash = cash;
        MarketValue = marketValue;
        TotalEquity = totalEquity;
    }

    public DateOnly Date { get; }
    public decimal Cash { get; }
    public decimal MarketValue { get; }
    public decimal TotalEquity { get; }
}

/// <summary>Immutable daily equity-curve point. Drawdown is derived by the downstream performance analyzer.</summary>
public sealed class PortfolioEquityPoint
{
    public PortfolioEquityPoint(DateOnly date, decimal cash, decimal marketValue, decimal totalEquity, double? dailyReturn, double cumulativeReturn)
    {
        if (cash < 0) throw new ArgumentOutOfRangeException(nameof(cash));
        if (marketValue < 0) throw new ArgumentOutOfRangeException(nameof(marketValue));
        if (totalEquity < 0) throw new ArgumentOutOfRangeException(nameof(totalEquity));
        if (totalEquity != cash + marketValue) throw new ArgumentException("TotalEquity must equal Cash plus MarketValue.");
        if (dailyReturn.HasValue && !double.IsFinite(dailyReturn.Value)) throw new ArgumentOutOfRangeException(nameof(dailyReturn));
        if (!double.IsFinite(cumulativeReturn)) throw new ArgumentOutOfRangeException(nameof(cumulativeReturn));
        Date = date;
        Cash = cash;
        MarketValue = marketValue;
        TotalEquity = totalEquity;
        DailyReturn = dailyReturn;
        CumulativeReturn = cumulativeReturn;
    }

    public DateOnly Date { get; }
    public decimal Cash { get; }
    public decimal MarketValue { get; }
    public decimal TotalEquity { get; }
    /// <summary>Null for the first observed trading date; subsequent values are decimal return ratios, not percentages.</summary>
    public double? DailyReturn { get; }
    public double CumulativeReturn { get; }
}

/// <summary>Immutable realized contribution of a closed portfolio position.</summary>
public sealed class PortfolioAttribution
{
    public PortfolioAttribution(string symbol, DateOnly entryDate, DateOnly exitDate, int holdingPeriodTradingDays, long quantity, decimal realizedPnL, double returnPercent, double contributionPercent, bool winning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (exitDate < entryDate) throw new ArgumentException("ExitDate must not precede EntryDate.");
        if (holdingPeriodTradingDays < 0) throw new ArgumentOutOfRangeException(nameof(holdingPeriodTradingDays));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!double.IsFinite(returnPercent)) throw new ArgumentOutOfRangeException(nameof(returnPercent));
        if (!double.IsFinite(contributionPercent)) throw new ArgumentOutOfRangeException(nameof(contributionPercent));
        Symbol = symbol;
        EntryDate = entryDate;
        ExitDate = exitDate;
        HoldingPeriodTradingDays = holdingPeriodTradingDays;
        Quantity = quantity;
        RealizedPnL = realizedPnL;
        ReturnPercent = returnPercent;
        ContributionPercent = contributionPercent;
        Winning = winning;
    }

    public string Symbol { get; }
    public DateOnly EntryDate { get; }
    public DateOnly ExitDate { get; }
    public int HoldingPeriodTradingDays { get; }
    public long Quantity { get; }
    public decimal RealizedPnL { get; }
    public double ReturnPercent { get; }
    public double ContributionPercent { get; }
    /// <summary>Compatibility alias for the Phase 3.4-A contribution field; values are percentage points.</summary>
    public double Contribution => ContributionPercent;
    public bool Winning { get; }
}

/// <summary>Immutable container only. Portfolio simulation, pricing, drawdown, and attribution calculations belong to later phases.</summary>
public sealed class SparrowPortfolioSimulationResult
{
    public SparrowPortfolioSimulationResult(
        PortfolioSimulationRequest request,
        string datasetFingerprint,
        IReadOnlyList<PortfolioTrade>? trades = null,
        IReadOnlyList<PortfolioPosition>? positions = null,
        IReadOnlyList<PortfolioEquityPoint>? equityCurve = null,
        IReadOnlyList<PortfolioAttribution>? attributions = null,
        IReadOnlyList<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        if (!string.Equals(request.DatasetFingerprint, datasetFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Result DatasetFingerprint must match the request.", nameof(datasetFingerprint));

        Request = request;
        DatasetFingerprint = datasetFingerprint;
        Trades = Array.AsReadOnly((trades ?? Array.Empty<PortfolioTrade>()).ToArray());
        Positions = Array.AsReadOnly((positions ?? Array.Empty<PortfolioPosition>()).ToArray());
        EquityCurve = Array.AsReadOnly((equityCurve ?? Array.Empty<PortfolioEquityPoint>()).ToArray());
        Attributions = Array.AsReadOnly((attributions ?? Array.Empty<PortfolioAttribution>()).ToArray());
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).ToArray());
    }

    public PortfolioSimulationRequest Request { get; }
    public string DatasetFingerprint { get; }
    public IReadOnlyList<PortfolioTrade> Trades { get; }
    public IReadOnlyList<PortfolioPosition> Positions { get; }
    public IReadOnlyList<PortfolioEquityPoint> EquityCurve { get; }
    public IReadOnlyList<PortfolioAttribution> Attributions { get; }
    public IReadOnlyList<string> Warnings { get; }
}
