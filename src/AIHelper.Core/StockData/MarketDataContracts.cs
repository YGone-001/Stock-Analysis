using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelper.Core.StockData;

/// <summary>Source and cache provenance carried from the compatibility transport boundary.</summary>
public sealed record MarketDataMetadata(
	string Source,
	bool UsedCache,
	bool IsStale,
	bool IsBackgroundRefresh);

/// <summary>
/// A typed market-data response. Provider failures remain values so existing callers can retain
/// their fault-isolation behavior; a successful but malformed payload raises MarketDataContractException.
/// </summary>
public sealed record MarketDataResult<T>(T Data, MarketDataMetadata Metadata, string Error = "")
{
	public bool Success => string.IsNullOrWhiteSpace(Error);
}

/// <summary>Normalized quote values. Null means unavailable; zero remains a real upstream value.</summary>
public sealed record QuoteSnapshot(
	string Symbol,
	string Name,
	double? Price,
	double? PreviousClose,
	double? ChangePercent,
	double? Volume,
	double? Amount,
	double? Turnover,
	double? OuterVolume,
	double? InnerVolume,
	double? Open,
	double? High,
	double? Low);

/// <summary>One daily K-line bar. Date is the exchange-local trading date supplied by the provider.</summary>
public sealed record KlineBar(
	DateTime Date,
	double? Open,
	double? High,
	double? Low,
	double? Close,
	double? Volume,
	double? Amount,
	double? ChangePercent,
	double? Change,
	double? TurnoverRate);

public sealed record KlineSeries(string Symbol, IReadOnlyList<KlineBar> Bars);

public sealed record TradingDayResult(DateTime RequestedDate, bool IsTradingDay, DateTime PreviousTradingDay);

public interface IQuoteService
{
	Task<MarketDataResult<QuoteSnapshot?>> GetQuoteAsync(string symbol, CancellationToken cancellationToken = default);
	Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetQuotesAsync(
		IReadOnlyCollection<string> symbols,
		bool forceRefresh = false,
		CancellationToken cancellationToken = default);
	Task<MarketDataResult<IReadOnlyList<QuoteSnapshot>>> GetAllQuotesAsync(
		bool forceRefresh = false,
		CancellationToken cancellationToken = default);
}

public interface IKlineService
{
	Task<MarketDataResult<KlineSeries?>> GetDailyAsync(
		string symbol,
		int limit,
		bool forceRefresh = false,
		CancellationToken cancellationToken = default);
}

public interface IMarketCalendarService
{
	Task<MarketDataResult<TradingDayResult?>> GetTradingDayAsync(
		DateTime date,
		CancellationToken cancellationToken = default);
}

public sealed class MarketDataContractException : Exception
{
	public MarketDataContractException(string contractName, string endpoint, string source, string message, Exception? innerException = null)
		: base($"{contractName} contract failed for {endpoint} from {source}: {message}", innerException)
	{
		ContractName = contractName;
		Endpoint = endpoint;
		ProviderSource = source;
	}

	public string ContractName { get; }
	public string Endpoint { get; }
	public string ProviderSource { get; }
}
