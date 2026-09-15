using System.Collections.Concurrent;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class ProviderPolicyTests
{
	[Fact]
	public void DefaultPolicy_PreservesExistingExternalAndDirectoryBudgets()
	{
		var policies = new DefaultDataSourcePolicyProvider();

		DataSourcePolicy quote = policies.GetPolicy(MarketDataOperation.Quote);
		DataSourcePolicy directory = policies.GetPolicy(MarketDataOperation.SecurityDirectory);
		Assert.Equal(TimeSpan.FromSeconds(15), quote.ExternalProviderTimeout);
		Assert.Null(quote.LiveProviderBudget);
		Assert.False(quote.PreferCacheBeforeLive);
		Assert.Equal(TimeSpan.FromSeconds(15), directory.ExternalProviderTimeout);
		Assert.Equal(TimeSpan.FromSeconds(25), directory.LiveProviderBudget);
		Assert.True(directory.PreferCacheBeforeLive);
		Assert.True(directory.AllowStaleCache);
		Assert.True(directory.RefreshStaleInBackground);
	}

	[Theory]
	[InlineData("/api/quote", MarketDataOperation.Quote)]
	[InlineData("/api/quote-all", MarketDataOperation.QuoteBatch)]
	[InlineData("/api/codes", MarketDataOperation.SecurityDirectory)]
	[InlineData("/api/etf", MarketDataOperation.SecurityDirectory)]
	[InlineData("/api/workday", MarketDataOperation.TradingCalendar)]
	[InlineData("/api/index", MarketDataOperation.MarketIndex)]
	[InlineData("/api/search", MarketDataOperation.RawCompatibility)]
	public void OperationClassifier_MapsExistingEndpoints(string endpoint, MarketDataOperation expected)
	{
		Assert.Equal(expected, StockDataRequest.Parse(endpoint).Operation);
	}

	[Fact]
	public void OperationClassifier_MapsDailyKline_AndKeepsOtherKlineRawCompatible()
	{
		Assert.Equal(MarketDataOperation.DailyKline,
			StockDataRequest.Parse("/api/kline-all?code=600519&type=day").Operation);
		Assert.Equal(MarketDataOperation.RawCompatibility,
			StockDataRequest.Parse("/api/kline-all?code=600519&type=week").Operation);
	}

	[Fact]
	public async Task PreferredProvider_ExternalSuccess_DoesNotCallEastMoney_AndRecordsHealthyLatency()
	{
		var external = new StubProvider(DataSourceKind.ExternalGateway, Success(DataSourceKind.ExternalGateway));
		var eastMoney = new StubProvider(DataSourceKind.EastMoney, Success(DataSourceKind.EastMoney));
		var health = new ProviderHealthService();
		var provider = new PreferredStockDataProvider(external, eastMoney, health);

		StockDataResult result = await provider.GetDataAsync(StockDataRequest.Parse("/api/quote?code=600519"));

		Assert.True(result.Success);
		Assert.Equal(1, external.Calls);
		Assert.Equal(0, eastMoney.Calls);
		ProviderHealthSnapshot snapshot = health.GetSnapshot(DataSourceKind.ExternalGateway);
		Assert.Equal(ProviderHealthState.Healthy, snapshot.State);
		Assert.NotNull(snapshot.LastLatency);
	}

	[Fact]
	public async Task PreferredProvider_ExternalTimeout_FallsBackToEastMoney_AndRetainsReason()
	{
		var external = new StubProvider(DataSourceKind.ExternalGateway,
			Failure(DataSourceKind.ExternalGateway, ProviderFailureKind.Timeout, "external timeout"));
		var eastMoney = new StubProvider(DataSourceKind.EastMoney, Success(DataSourceKind.EastMoney));
		var health = new ProviderHealthService();
		var provider = new PreferredStockDataProvider(external, eastMoney, health);

		StockDataResult result = await provider.GetDataAsync(StockDataRequest.Parse("/api/quote?code=600519"));

		Assert.True(result.Success);
		Assert.Equal(DataSourceKind.EastMoney, result.SourceKind);
		Assert.Equal(FallbackReason.PrimaryTimeout, result.FallbackReason);
		Assert.Equal(1, external.Calls);
		Assert.Equal(1, eastMoney.Calls);
		Assert.Equal(ProviderHealthState.Degraded, health.GetSnapshot(DataSourceKind.ExternalGateway).State);
		Assert.Equal(ProviderHealthState.Healthy, health.GetSnapshot(DataSourceKind.EastMoney).State);
	}

	[Fact]
	public async Task PreferredProvider_WhenPrimaryCannotHandle_DoesNotInventFallbackReason()
	{
		var external = new NonHandlingProvider();
		var eastMoney = new StubProvider(DataSourceKind.EastMoney, Success(DataSourceKind.EastMoney));
		var provider = new PreferredStockDataProvider(external, eastMoney);

		StockDataResult result = await provider.GetDataAsync(StockDataRequest.Parse("/api/quote-all"));

		Assert.True(result.Success);
		Assert.Equal(FallbackReason.None, result.FallbackReason);
		Assert.Equal(1, eastMoney.Calls);
	}

	[Fact]
	public async Task LiveSourceFailures_FallBackToLocalCache_WithFailureContext()
	{
		var external = new StubProvider(DataSourceKind.ExternalGateway,
			Failure(DataSourceKind.ExternalGateway, ProviderFailureKind.Network, "external offline"));
		var eastMoney = new StubProvider(DataSourceKind.EastMoney,
			Failure(DataSourceKind.EastMoney, ProviderFailureKind.HttpError, "503"));
		var cache = new StubCache(Success(DataSourceKind.LocalCache, usedCache: true));
		using var provider = new FallbackStockDataProvider(
			new PreferredStockDataProvider(external, eastMoney), cache);

		StockDataResult result = await provider.GetDataAsync(StockDataRequest.Parse("/api/search?keyword=600519"));

		Assert.True(result.Success);
		Assert.True(result.UsedCache);
		Assert.Equal(DataSourceKind.LocalCache, result.SourceKind);
		Assert.Equal(FallbackReason.LiveSourcesUnavailable, result.FallbackReason);
		Assert.Contains("ExternalGateway", result.Error, StringComparison.Ordinal);
		Assert.Contains("EastMoney", result.Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task CallerCancellation_IsRethrown_DoesNotCallFallback_AndDoesNotMarkProviderUnhealthy()
	{
		var external = new BlockingProvider(DataSourceKind.ExternalGateway);
		var eastMoney = new StubProvider(DataSourceKind.EastMoney, Success(DataSourceKind.EastMoney));
		var health = new ProviderHealthService();
		var provider = new PreferredStockDataProvider(external, eastMoney, health);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.GetDataAsync(StockDataRequest.Parse("/api/quote?code=600519"), cancellation.Token));

		Assert.Equal(0, eastMoney.Calls);
		Assert.Equal(ProviderHealthState.Unknown, health.GetSnapshot(DataSourceKind.ExternalGateway).State);
	}

	[Fact]
	public void HealthService_RecordsFailureAndRecovery_WithConsecutiveFailureReset()
	{
		var health = new ProviderHealthService();

		health.RecordFailure(DataSourceKind.ExternalGateway, MarketDataOperation.Quote,
			ProviderFailureKind.Network, "offline", TimeSpan.FromMilliseconds(12));
		ProviderHealthSnapshot degraded = health.GetSnapshot(DataSourceKind.ExternalGateway);
		Assert.Equal(ProviderHealthState.Degraded, degraded.State);
		Assert.Equal(1, degraded.ConsecutiveFailures);
		Assert.Equal(ProviderFailureKind.Network, degraded.LastFailure!.Kind);
		health.RecordFailure(DataSourceKind.ExternalGateway, MarketDataOperation.Quote,
			ProviderFailureKind.Timeout, "timeout", TimeSpan.FromMilliseconds(15));
		ProviderHealthSnapshot unavailable = health.GetSnapshot(DataSourceKind.ExternalGateway);
		Assert.Equal(ProviderHealthState.Unavailable, unavailable.State);
		Assert.Equal(2, unavailable.ConsecutiveFailures);

		health.RecordSuccess(DataSourceKind.ExternalGateway, MarketDataOperation.Quote, TimeSpan.FromMilliseconds(4));
		ProviderHealthSnapshot healthy = health.GetSnapshot(DataSourceKind.ExternalGateway);
		Assert.Equal(ProviderHealthState.Healthy, healthy.State);
		Assert.Equal(0, healthy.ConsecutiveFailures);
		Assert.NotNull(healthy.LastSuccessUtc);
		Assert.Equal(TimeSpan.FromMilliseconds(4), healthy.LastLatency);
	}

	[Fact]
	public async Task StaleSecurityDirectoryCache_ReturnsImmediately_AndStartsExactlyOneBackgroundRefresh()
	{
		var live = new BlockingProvider(DataSourceKind.EastMoney);
		var cache = new StubCache(Success(DataSourceKind.LocalCache, usedCache: true, stale: true));
		using var provider = new FallbackStockDataProvider(live, cache);
		StockDataRequest request = StockDataRequest.Parse("/api/codes");

		StockDataResult[] results = await Task.WhenAll(provider.GetDataAsync(request), provider.GetDataAsync(request));

		Assert.All(results, result =>
		{
			Assert.True(result.Success);
			Assert.True(result.IsStale);
			Assert.Equal(FallbackReason.StaleCache, result.FallbackReason);
		});
		await live.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal(1, live.Calls);
		live.Release.TrySetResult(Success(DataSourceKind.EastMoney));
	}

	[Fact]
	public async Task ProviderBudgetTimeout_IsNotCallerCancellation_AndUsesCacheFallback()
	{
		var live = new BlockingProvider(DataSourceKind.EastMoney);
		var cache = new StubCache(Success(DataSourceKind.LocalCache, usedCache: true));
		var policy = new FixedPolicyProvider(new DataSourcePolicy(TimeSpan.FromSeconds(15), TimeSpan.Zero, false, false, false));
		using var provider = new FallbackStockDataProvider(live, cache, policy);

		StockDataResult result = await provider.GetDataAsync(StockDataRequest.Parse("/api/search?keyword=x"));

		Assert.True(result.Success);
		Assert.True(result.UsedCache);
		Assert.Equal(ProviderFailureKind.Timeout, result.FailureKind);
		Assert.True(live.LastToken.IsCancellationRequested);
	}

	[Fact]
	public async Task TypedQuoteMetadata_PropagatesSourceCacheAndFallbackObservability()
	{
		var transport = new StubProvider(DataSourceKind.LocalCache, new StockDataResult
		{
			Endpoint = "/api/quote?code=600519", Handled = true, Success = true,
			Json = "{\"data\":[{\"Code\":\"600519\"}]}",
			Source = DataSourceKind.LocalCache.ToLegacySource(), SourceKind = DataSourceKind.LocalCache,
			UsedCache = true, IsStale = true, CacheFreshness = CacheFreshness.Stale,
			FallbackReason = FallbackReason.LiveSourcesUnavailable
		});
		var service = new QuoteService(transport);

		MarketDataResult<IReadOnlyList<QuoteSnapshot>> result = await service.GetQuotesAsync(new[] { "600519" });

		Assert.Equal(DataSourceKind.LocalCache, result.Metadata.SourceKind);
		Assert.True(result.Metadata.UsedCache);
		Assert.True(result.Metadata.IsStale);
		Assert.Equal(CacheFreshness.Stale, result.Metadata.CacheFreshness);
		Assert.Equal(FallbackReason.LiveSourcesUnavailable, result.Metadata.FallbackReason);
	}

	private static StockDataResult Success(DataSourceKind source, bool usedCache = false, bool stale = false) => new()
	{
		Endpoint = "/api/test", Handled = true, Success = true, Json = "{\"data\":[]}",
		Source = source.ToLegacySource(), SourceKind = source, UsedCache = usedCache, IsStale = stale,
		CacheFreshness = usedCache ? stale ? CacheFreshness.Stale : CacheFreshness.Fresh : CacheFreshness.NotApplicable,
		FallbackReason = usedCache ? stale ? FallbackReason.StaleCache : FallbackReason.FreshCache : FallbackReason.None
	};

	private static StockDataResult Failure(DataSourceKind source, ProviderFailureKind kind, string error) => new()
	{
		Endpoint = "/api/test", Handled = true, Success = false, Json = "{\"data\":[]}",
		Source = source.ToLegacySource(), SourceKind = source, Error = error, FailureKind = kind
	};

	private sealed class FixedPolicyProvider : IDataSourcePolicyProvider
	{
		private readonly DataSourcePolicy _policy;
		public FixedPolicyProvider(DataSourcePolicy policy) => _policy = policy;
		public DataSourcePolicy GetPolicy(MarketDataOperation operation) => _policy;
	}

	private sealed class StubProvider : IStockDataProvider
	{
		private readonly StockDataResult _result;
		public StubProvider(DataSourceKind source, StockDataResult result) => _result = result;
		public int Calls { get; private set; }
		public bool CanHandle(StockDataRequest request) => true;
		public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
		{
			Calls++;
			return Task.FromResult(_result);
		}
	}

	private sealed class BlockingProvider : IStockDataProvider
	{
		private readonly DataSourceKind _source;
		public BlockingProvider(DataSourceKind source) => _source = source;
		public int Calls { get; private set; }
		public CancellationToken LastToken { get; private set; }
		public TaskCompletionSource<bool> FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<StockDataResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool CanHandle(StockDataRequest request) => true;
		public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
		{
			Calls++;
			LastToken = cancellationToken;
			FirstCall.TrySetResult(true);
			return await Release.Task.WaitAsync(cancellationToken);
		}
	}

	private sealed class NonHandlingProvider : IStockDataProvider
	{
		public bool CanHandle(StockDataRequest request) => false;
		public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default) =>
			Task.FromResult(StockDataResult.NotHandled(request.Endpoint));
	}

	private sealed class StubCache : IStockDataCache
	{
		private readonly StockDataResult _result;
		public StubCache(StockDataResult result) => _result = result;
		public bool CanHandle(StockDataRequest request) => true;
		public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default) => Task.FromResult(_result);
		public Task<StockNameCacheSnapshot> GetSnapshotAsync(string? kind = null, CancellationToken cancellationToken = default) => Task.FromResult(new StockNameCacheSnapshot());
		public Task<StockMarketCache> GetMarketStateAsync(string marketKey, CancellationToken cancellationToken = default) => Task.FromResult(new StockMarketCache());
		public Task ResetMarketAsync(string marketKey, string kind, string filter, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task MergeMarketPageAsync(string marketKey, string kind, string filter, int nextPage, bool completed, IReadOnlyDictionary<string, string> pageItems, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task MergeItemsAsync(IReadOnlyDictionary<string, string> items, string source, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}
}
