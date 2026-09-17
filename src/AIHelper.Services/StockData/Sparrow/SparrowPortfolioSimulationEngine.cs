using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Pure, offline, close-based portfolio simulator. It consumes the immutable backtest selection
/// stream and never evaluates strategy rules, ranks candidates, or queries market-data providers.
/// </summary>
public sealed class SparrowPortfolioSimulationEngine : ISparrowPortfolioSimulationEngine
{
    public Task<SparrowPortfolioSimulationResult> SimulateAsync(
        SparrowBacktestResult backtestResult,
        PortfolioSimulationRequest request,
        HistoricalMarketDataset dataset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backtestResult);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dataset);
        ValidateIdentity(backtestResult, request, dataset);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<DateOnly, IReadOnlyList<SparrowBacktestSelection>> selectionsByDate = GroupSelections(backtestResult, dataset);
        var trades = new List<PortfolioTrade>();
        var positions = new List<PortfolioPosition>();
        var warnings = new List<string>();
        var openPositions = new Dictionary<string, PositionState>(StringComparer.Ordinal);
        var scheduledExits = new Dictionary<DateOnly, List<PositionState>>();
        decimal cash = request.InitialCapital;

        for (int dateIndex = 0; dateIndex < dataset.TradingDates.Count; dateIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateOnly date = dataset.TradingDates[dateIndex];

            if (scheduledExits.TryGetValue(date, out List<PositionState>? due))
            {
                foreach (PositionState position in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!openPositions.TryGetValue(position.Symbol, out PositionState? current) || !ReferenceEquals(current, position))
                        continue;

                    if (!TryGetClose(dataset, position.Symbol, date, out decimal close))
                    {
                        warnings.Add($"{date:O}: {position.Symbol}: ExitCloseUnavailable; position remains open.");
                        continue;
                    }

                    decimal executionPrice = close * (1m - request.SlippageRate);
                    if (executionPrice <= 0)
                    {
                        warnings.Add($"{date:O}: {position.Symbol}: ExitExecutionPriceInvalid; position remains open.");
                        continue;
                    }

                    decimal notional = executionPrice * position.Quantity;
                    decimal fee = notional * request.CommissionRate;
                    trades.Add(new PortfolioTrade(position.Symbol, date, PortfolioTradeSide.Sell, executionPrice, position.Quantity, notional, fee));
                    cash += notional - fee;
                    decimal realizedPnL = notional - fee - position.EntryNotional - position.EntryFee;
                    double returnPercent = (double)(realizedPnL / (position.EntryNotional + position.EntryFee) * 100m);
                    positions.Add(position.Close(date, executionPrice, notional, fee, realizedPnL, returnPercent));
                    openPositions.Remove(position.Symbol);
                    ValidateBalances(date, cash, openPositions, dataset);
                }
            }

            if (!selectionsByDate.TryGetValue(date, out IReadOnlyList<SparrowBacktestSelection>? selections))
                continue;

            List<EntryCandidate> candidates = BuildEntryCandidates(selections, dataset, date, request.SlippageRate, openPositions, warnings);
            if (candidates.Count == 0)
                continue;

            decimal allocation = cash / candidates.Count;
            foreach (EntryCandidate candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long quantity = (long)decimal.Floor(allocation / candidate.ExecutionPrice);
                quantity = ReduceToAffordableQuantity(quantity, candidate.ExecutionPrice, request.CommissionRate, cash);
                if (quantity <= 0)
                {
                    warnings.Add($"{date:O}: {candidate.Symbol}: InsufficientCashForMinimumQuantity.");
                    continue;
                }

                decimal notional = candidate.ExecutionPrice * quantity;
                decimal fee = notional * request.CommissionRate;
                decimal cost = notional + fee;
                if (cost > cash)
                    throw new InvalidOperationException("Portfolio quantity calculation produced negative cash.");

                cash -= cost;
                trades.Add(new PortfolioTrade(candidate.Symbol, date, PortfolioTradeSide.Buy, candidate.ExecutionPrice, quantity, notional, fee));
                DateOnly? exitDate = dateIndex + request.HorizonTradingDays < dataset.TradingDates.Count
                    ? dataset.TradingDates[dateIndex + request.HorizonTradingDays]
                    : null;
                var position = new PositionState(candidate.Symbol, date, candidate.ExecutionPrice, quantity, notional, fee, exitDate);
                openPositions.Add(candidate.Symbol, position);
                if (exitDate.HasValue)
                {
                    if (!scheduledExits.TryGetValue(exitDate.Value, out List<PositionState>? scheduled))
                    {
                        scheduled = new List<PositionState>();
                        scheduledExits.Add(exitDate.Value, scheduled);
                    }

                    scheduled.Add(position);
                }
                else
                {
                    warnings.Add($"{date:O}: {candidate.Symbol}: ExitTradingDateUnavailable; position remains open.");
                }

                ValidateBalances(date, cash, openPositions, dataset);
            }
        }

        positions.AddRange(openPositions.Values
            .OrderBy(position => position.EntryDate)
            .ThenBy(position => position.Symbol, StringComparer.Ordinal)
            .Select(position => position.Open()));

        return Task.FromResult(new SparrowPortfolioSimulationResult(request, dataset.Fingerprint, trades, positions, Array.Empty<PortfolioEquityPoint>(), Array.Empty<PortfolioAttribution>(), warnings));
    }

    private static Dictionary<DateOnly, IReadOnlyList<SparrowBacktestSelection>> GroupSelections(SparrowBacktestResult result, HistoricalMarketDataset dataset)
    {
        DateOnly? previous = null;
        var grouped = new Dictionary<DateOnly, List<SparrowBacktestSelection>>();
        foreach (SparrowBacktestSelection selection in result.Selections)
        {
            if (!dataset.TradingDates.Contains(selection.ReplayDate))
                throw new ArgumentException("Backtest selection date is not a dataset trading date.", nameof(result));
            if (previous.HasValue && selection.ReplayDate < previous.Value)
                throw new ArgumentException("Backtest selections must retain chronological replay-date order.", nameof(result));
            previous = selection.ReplayDate;
            if (!grouped.TryGetValue(selection.ReplayDate, out List<SparrowBacktestSelection>? selections))
            {
                selections = new List<SparrowBacktestSelection>();
                grouped.Add(selection.ReplayDate, selections);
            }

            selections.Add(selection);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<SparrowBacktestSelection>)pair.Value.AsReadOnly());
    }

    private static List<EntryCandidate> BuildEntryCandidates(
        IReadOnlyList<SparrowBacktestSelection> selections,
        HistoricalMarketDataset dataset,
        DateOnly date,
        decimal slippageRate,
        IReadOnlyDictionary<string, PositionState> openPositions,
        ICollection<string> warnings)
    {
        var candidates = new List<EntryCandidate>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        foreach (SparrowBacktestSelection selection in selections)
        {
            string symbol = selection.Selection.Code;
            if (!seenSymbols.Add(symbol))
            {
                warnings.Add($"{date:O}: {symbol}: DuplicateSameDaySignalIgnored.");
                continue;
            }
            if (openPositions.ContainsKey(symbol))
            {
                warnings.Add($"{date:O}: {symbol}: OpenPositionSignalIgnored.");
                continue;
            }
            if (!TryGetClose(dataset, symbol, date, out decimal close))
            {
                warnings.Add($"{date:O}: {symbol}: EntryCloseUnavailable.");
                continue;
            }

            decimal executionPrice = close * (1m + slippageRate);
            candidates.Add(new EntryCandidate(symbol, executionPrice));
        }

        return candidates;
    }

    private static long ReduceToAffordableQuantity(long quantity, decimal price, decimal commissionRate, decimal cash)
    {
        while (quantity > 0 && price * quantity * (1m + commissionRate) > cash)
            quantity--;
        return quantity;
    }

    private static bool TryGetClose(HistoricalMarketDataset dataset, string symbol, DateOnly date, out decimal close)
    {
        close = default;
        if (!dataset.Klines.TryGetValue(symbol, out KlineSeries? series))
            return false;
        double? value = series.Bars.SingleOrDefault(bar => DateOnly.FromDateTime(bar.Date) == date)?.Close;
        if (value is not > 0 || !double.IsFinite(value.Value))
            return false;
        try
        {
            close = (decimal)value.Value;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ValidateBalances(DateOnly date, decimal cash, IReadOnlyDictionary<string, PositionState> openPositions, HistoricalMarketDataset dataset)
    {
        decimal marketValue = openPositions.Values.Sum(position =>
            TryGetClose(dataset, position.Symbol, date, out decimal close) ? close * position.Quantity : position.EntryNotional);
        _ = new PortfolioSnapshot(date, cash, marketValue, cash + marketValue);
    }

    private static void ValidateIdentity(SparrowBacktestResult backtest, PortfolioSimulationRequest request, HistoricalMarketDataset dataset)
    {
        if (!string.Equals(backtest.DatasetId, dataset.DatasetId, StringComparison.Ordinal) || !string.Equals(backtest.DatasetFingerprint, dataset.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Backtest result does not belong to the supplied dataset.", nameof(backtest));
        if (!string.Equals(request.DatasetId, dataset.DatasetId, StringComparison.Ordinal) || !string.Equals(request.DatasetFingerprint, dataset.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Portfolio request does not belong to the supplied dataset.", nameof(request));
        if (request.StrategyMode != backtest.Request.StrategyMode || !string.Equals(request.StrategyVersion, backtest.Request.StrategyVersion, StringComparison.Ordinal))
            throw new ArgumentException("Portfolio request strategy identity does not match the backtest result.", nameof(request));
        if (!string.Equals(request.StrategyParameterFingerprint, backtest.ParameterFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Portfolio request parameter fingerprint does not match the backtest result.", nameof(request));
        if (request.StartDate != backtest.Request.StartDate || request.EndDate != backtest.Request.EndDate || request.TopN != backtest.Request.TopN)
            throw new ArgumentException("Portfolio request schedule does not match the backtest result.", nameof(request));
        if (request.PositionSizingMethod != PortfolioPositionSizingMethod.EqualWeight)
            throw new NotSupportedException($"Unsupported portfolio position sizing method '{request.PositionSizingMethod}'.");
        if (request.ExecutionModel != PortfolioExecutionModel.CloseBased)
            throw new NotSupportedException($"Unsupported portfolio execution model '{request.ExecutionModel}'.");
    }

    private sealed record EntryCandidate(string Symbol, decimal ExecutionPrice);

    private sealed record PositionState(string Symbol, DateOnly EntryDate, decimal EntryPrice, long Quantity, decimal EntryNotional, decimal EntryFee, DateOnly? ExitDate)
    {
        public PortfolioPosition Open() => new(Symbol, EntryDate, EntryPrice, Quantity, EntryNotional, EntryFee, PortfolioPositionStatus.Open);
        public PortfolioPosition Close(DateOnly exitDate, decimal exitPrice, decimal exitNotional, decimal exitFee, decimal realizedPnL, double returnPercent) =>
            new(Symbol, EntryDate, EntryPrice, Quantity, EntryNotional, EntryFee, PortfolioPositionStatus.Closed, exitDate, exitPrice, exitNotional, exitFee, realizedPnL, returnPercent);
    }
}
