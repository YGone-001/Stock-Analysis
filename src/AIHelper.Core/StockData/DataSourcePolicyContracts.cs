using System.Collections.Generic;

namespace AIHelper.Core.StockData;

/// <summary>Stable operation categories used for orchestration policy; transport endpoints remain compatible.</summary>
public enum MarketDataOperation
{
	RawCompatibility,
	Quote,
	QuoteBatch,
	DailyKline,
	SecurityDirectory,
	TradingCalendar,
	MarketIndex
}

/// <summary>Application-visible source identity. Backend implementation details do not leak here.</summary>
public enum DataSourceKind
{
	Unknown,
	ExternalGateway,
	EastMoney,
	LocalCache
}

public enum ProviderFailureKind
{
	None,
	Timeout,
	Network,
	HttpError,
	Contract,
	Unavailable,
	Cancelled,
	Unexpected
}

public enum FallbackReason
{
	None,
	PrimaryTimeout,
	PrimaryFailure,
	PrimaryContractFailure,
	LiveSourcesUnavailable,
	FreshCache,
	StaleCache,
	BackgroundRefresh
}

public enum CacheFreshness
{
	NotApplicable,
	Fresh,
	Stale,
	Missing
}

public enum ProviderHealthState
{
	Unknown,
	Healthy,
	Degraded,
	Unavailable
}

/// <summary>
/// Existing operation policy, made explicit without changing defaults. A null live budget means
/// the operation historically delegated its timeout ownership to the provider implementation.
/// </summary>
public sealed record DataSourcePolicy(
	TimeSpan ExternalProviderTimeout,
	TimeSpan? LiveProviderBudget,
	bool PreferCacheBeforeLive,
	bool AllowStaleCache,
	bool RefreshStaleInBackground);

public interface IDataSourcePolicyProvider
{
	DataSourcePolicy GetPolicy(MarketDataOperation operation);
}

public sealed record ProviderFailure(
	DataSourceKind Provider,
	MarketDataOperation Operation,
	ProviderFailureKind Kind,
	string Message,
	DateTimeOffset Timestamp);

public sealed record ProviderHealthSnapshot(
	DataSourceKind Provider,
	ProviderHealthState State,
	DateTimeOffset? LastSuccessUtc,
	ProviderFailure? LastFailure,
	int ConsecutiveFailures,
	TimeSpan? LastLatency);

/// <summary>Thread-safe runtime observability. Health never controls provider routing in Phase 2.3.</summary>
public interface IProviderHealthService
{
	IReadOnlyList<ProviderHealthSnapshot> GetSnapshots();
	ProviderHealthSnapshot GetSnapshot(DataSourceKind provider);
	void RecordSuccess(DataSourceKind provider, MarketDataOperation operation, TimeSpan latency);
	void RecordFailure(DataSourceKind provider, MarketDataOperation operation, ProviderFailureKind kind, string message, TimeSpan latency);
}

public static class MarketDataOperationClassifier
{
	public static MarketDataOperation Classify(string path, IReadOnlyDictionary<string, string>? query = null)
	{
		return path switch
		{
			"/api/quote" => MarketDataOperation.Quote,
			"/api/quote-all" => MarketDataOperation.QuoteBatch,
			"/api/kline-all" when query is not null
				&& query.TryGetValue("type", out string? type)
				&& string.Equals(type, "day", StringComparison.OrdinalIgnoreCase) => MarketDataOperation.DailyKline,
			"/api/codes" or "/api/etf" => MarketDataOperation.SecurityDirectory,
			"/api/workday" => MarketDataOperation.TradingCalendar,
			"/api/index" => MarketDataOperation.MarketIndex,
			_ => MarketDataOperation.RawCompatibility
		};
	}
}

public static class DataSourceKindExtensions
{
	public static string ToLegacySource(this DataSourceKind source) => source switch
	{
		DataSourceKind.ExternalGateway => "ExternalGateway",
		DataSourceKind.EastMoney => "EastMoney",
		DataSourceKind.LocalCache => "LocalStockCache",
		_ => "Unknown"
	};

	public static DataSourceKind ToDataSourceKind(this string? source) => source switch
	{
		"ExternalGateway" => DataSourceKind.ExternalGateway,
		"EastMoney" => DataSourceKind.EastMoney,
		"LocalStockCache" or "Local" or "LegacyLocal" => DataSourceKind.LocalCache,
		_ => DataSourceKind.Unknown
	};
}
