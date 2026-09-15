using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace AIHelper.Services.StockData;

/// <summary>Centralizes existing policy constants; it deliberately does not alter their defaults.</summary>
public sealed class DefaultDataSourcePolicyProvider : IDataSourcePolicyProvider
{
	private static readonly DataSourcePolicy Default = new(
		ExternalProviderTimeout: TimeSpan.FromSeconds(15),
		LiveProviderBudget: null,
		PreferCacheBeforeLive: false,
		AllowStaleCache: false,
		RefreshStaleInBackground: false);

	private static readonly DataSourcePolicy SecurityDirectory = new(
		ExternalProviderTimeout: TimeSpan.FromSeconds(15),
		LiveProviderBudget: TimeSpan.FromSeconds(25),
		PreferCacheBeforeLive: true,
		AllowStaleCache: true,
		RefreshStaleInBackground: true);

	public DataSourcePolicy GetPolicy(MarketDataOperation operation) =>
		operation == MarketDataOperation.SecurityDirectory ? SecurityDirectory : Default;
}

/// <summary>Runtime-only provider health ledger. It has no routing or circuit-breaker behavior.</summary>
public sealed class ProviderHealthService : IProviderHealthService
{
	private sealed record MutableSnapshot(
		ProviderHealthState State,
		DateTimeOffset? LastSuccessUtc,
		ProviderFailure? LastFailure,
		int ConsecutiveFailures,
		TimeSpan? LastLatency);

	private readonly ConcurrentDictionary<DataSourceKind, MutableSnapshot> _snapshots = new();
	private readonly TimeProvider _timeProvider;

	public ProviderHealthService(TimeProvider? timeProvider = null)
	{
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public IReadOnlyList<ProviderHealthSnapshot> GetSnapshots() =>
		Enum.GetValues<DataSourceKind>()
			.Where(provider => provider is DataSourceKind.ExternalGateway or DataSourceKind.EastMoney)
			.Select(GetSnapshot)
			.ToArray();

	public ProviderHealthSnapshot GetSnapshot(DataSourceKind provider)
	{
		MutableSnapshot snapshot = _snapshots.GetOrAdd(provider, _ => new MutableSnapshot(
			ProviderHealthState.Unknown, null, null, 0, null));
		return new ProviderHealthSnapshot(provider, snapshot.State, snapshot.LastSuccessUtc,
			snapshot.LastFailure, snapshot.ConsecutiveFailures, snapshot.LastLatency);
	}

	public void RecordSuccess(DataSourceKind provider, MarketDataOperation operation, TimeSpan latency)
	{
		if (!IsLiveProvider(provider)) return;
		DateTimeOffset now = _timeProvider.GetUtcNow();
		_snapshots.AddOrUpdate(provider,
			_ => new MutableSnapshot(ProviderHealthState.Healthy, now, null, 0, latency),
			(_, current) => current with
			{
				State = ProviderHealthState.Healthy,
				LastSuccessUtc = now,
				ConsecutiveFailures = 0,
				LastLatency = latency
			});
	}

	public void RecordFailure(DataSourceKind provider, MarketDataOperation operation, ProviderFailureKind kind, string message, TimeSpan latency)
	{
		if (!IsLiveProvider(provider)) return;
		DateTimeOffset now = _timeProvider.GetUtcNow();
		_snapshots.AddOrUpdate(provider,
			_ => CreateFailure(provider, operation, kind, message, now, 1, latency),
			(_, current) => CreateFailure(provider, operation, kind, message, now,
				current.ConsecutiveFailures + 1, latency, current.LastSuccessUtc));
	}

	private static MutableSnapshot CreateFailure(DataSourceKind provider, MarketDataOperation operation,
		ProviderFailureKind kind, string message, DateTimeOffset now, int consecutiveFailures,
		TimeSpan latency, DateTimeOffset? lastSuccessUtc = null) => new(
		consecutiveFailures >= 2 ? ProviderHealthState.Unavailable : ProviderHealthState.Degraded,
		lastSuccessUtc,
		new ProviderFailure(provider, operation, kind, Sanitize(message), now),
		consecutiveFailures,
		latency);

	private static bool IsLiveProvider(DataSourceKind provider) =>
		provider is DataSourceKind.ExternalGateway or DataSourceKind.EastMoney;

	private static string Sanitize(string message) => string.IsNullOrWhiteSpace(message)
		? "Provider request failed."
		: message.Length <= 512 ? message : message[..512];
}

internal static class ProviderFailureClassifier
{
	public static ProviderFailureKind Classify(Exception? exception, string? error = null)
	{
		if (exception is TimeoutException or TaskCanceledException or OperationCanceledException)
		{
			return ProviderFailureKind.Timeout;
		}
		if (exception is HttpRequestException http)
		{
			return http.StatusCode.HasValue ? ProviderFailureKind.HttpError : ProviderFailureKind.Network;
		}
		if (exception is JsonException)
		{
			return ProviderFailureKind.Contract;
		}

		string text = error ?? exception?.Message ?? string.Empty;
		if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("超时", StringComparison.OrdinalIgnoreCase)) return ProviderFailureKind.Timeout;
		if (text.Contains("schema", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("contract", StringComparison.OrdinalIgnoreCase)) return ProviderFailureKind.Contract;
		if (text.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
			|| text.Contains("no valid", StringComparison.OrdinalIgnoreCase)) return ProviderFailureKind.Unavailable;
		return exception is null ? ProviderFailureKind.Unavailable : ProviderFailureKind.Unexpected;
	}

	public static FallbackReason ToFallbackReason(ProviderFailureKind failure) => failure switch
	{
		ProviderFailureKind.Timeout => FallbackReason.PrimaryTimeout,
		ProviderFailureKind.Contract => FallbackReason.PrimaryContractFailure,
		_ => FallbackReason.PrimaryFailure
	};

	public static TimeSpan Elapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp);
}
