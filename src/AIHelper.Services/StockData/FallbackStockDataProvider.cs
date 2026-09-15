using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

#pragma warning disable CS8618, CS8625
#pragma warning disable CS8618, CS8625
namespace AIHelper.Services.StockData;

public sealed class FallbackStockDataProvider : IStockDataProvider, IStockDataStatusSource, IDisposable
{
	private readonly IStockDataProvider _publicProvider;

	private readonly IStockDataCache _cacheProvider;

	private readonly IDataSourcePolicyProvider _policyProvider;

	private readonly IProviderHealthService? _health;

	private readonly ConcurrentDictionary<string, byte> _backgroundRefreshes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
	
	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	public event Action<StockDataResult>? StatusChanged;

	public FallbackStockDataProvider(IStockDataProvider publicProvider, IStockDataCache cacheProvider,
		IDataSourcePolicyProvider? policyProvider = null, IProviderHealthService? health = null)
	{
		_publicProvider = publicProvider;
		_cacheProvider = cacheProvider;
		_policyProvider = policyProvider ?? new DefaultDataSourcePolicyProvider();
		_health = health;
	}

	public bool CanHandle(StockDataRequest request)
	{
		return _publicProvider.CanHandle(request) || _cacheProvider.CanHandle(request);
	}

	public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
	{
		if (!CanHandle(request))
		{
			return StockDataResult.NotHandled(request.Endpoint);
		}

		DataSourcePolicy policy = _policyProvider.GetPolicy(request.Operation);
		if (policy.PreferCacheBeforeLive && !request.ForceRefresh)
		{
			StockDataResult local = await _cacheProvider.GetDataAsync(request, cancellationToken);
			if (local.Success && (!local.IsStale || policy.AllowStaleCache))
			{
				StockDataLog.Write(request.Path, "-", "-", null, true, "cacheSource=" + local.Source + ", stale=" + local.IsStale);
				if (local.IsStale && policy.RefreshStaleInBackground)
				{
					StartBackgroundRefresh(request, policy);
				}
				StatusChanged?.Invoke(local);
				return local;
			}
		}

		StockDataResult publicResult = await GetPublicWithBudgetAsync(request, policy, cancellationToken);
		if (publicResult.Success)
		{
			StatusChanged?.Invoke(publicResult);
			return publicResult;
		}

		if (_cacheProvider.CanHandle(request))
		{
			StockDataResult local = await _cacheProvider.GetDataAsync(request, cancellationToken);
			if (local.Success)
			{
				StockDataResult fallback = new StockDataResult
				{
					Endpoint = request.Endpoint,
					Handled = true,
					Success = true,
					Json = local.Json,
					Source = local.Source,
					SourceKind = DataSourceKind.LocalCache,
					UsedCache = true,
					IsStale = local.IsStale,
					Error = publicResult.Error,
					FailureKind = publicResult.FailureKind,
					FallbackReason = FallbackReason.LiveSourcesUnavailable,
					CacheFreshness = local.IsStale ? CacheFreshness.Stale : CacheFreshness.Fresh
				};
				StockDataLog.Write(request.Path, "-", "-", null, true, "publicFailure=" + publicResult.Error + ", using local cache");
				StatusChanged?.Invoke(fallback);
				return fallback;
			}
		}

		StatusChanged?.Invoke(publicResult);
		return publicResult;
	}

	private void StartBackgroundRefresh(StockDataRequest request, DataSourcePolicy policy)
	{
		if (!_backgroundRefreshes.TryAdd(request.Path, 0))
		{
			return;
		}
		StockDataRequest refreshRequest = StockDataRequest.Parse(request.Path + "?force=1");
		_ = Task.Run(async () =>
		{
			try
			{
				StockDataResult result = await GetPublicWithBudgetAsync(refreshRequest, policy, _cts.Token);
				StockDataResult backgroundResult = new StockDataResult
				{
					Endpoint = result.Endpoint,
					Handled = result.Handled,
					Success = result.Success,
					Json = result.Json,
					Source = result.Source,
					SourceKind = result.SourceKind,
					UsedCache = result.UsedCache,
					IsStale = result.IsStale,
					Error = result.Error,
					FailureKind = result.FailureKind,
					FallbackReason = FallbackReason.BackgroundRefresh,
					CacheFreshness = result.CacheFreshness,
					IsBackgroundRefresh = true
				};
				StatusChanged?.Invoke(backgroundResult);
			}
			catch (OperationCanceledException ex_log) { Serilog.Log.Information(ex_log, "任务被取消"); 
				// Ignore
			}
			catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
				System.Diagnostics.Trace.WriteLine($"Background refresh failed: {ex}");
			}
			finally
			{
				_backgroundRefreshes.TryRemove(request.Path, out _);
			}
		});
	}

	public void Dispose()
	{
		_cts.Cancel();
		_cts.Dispose();
	}

	private async Task<StockDataResult> GetPublicWithBudgetAsync(StockDataRequest request, DataSourcePolicy policy, CancellationToken cancellationToken)
	{
		if (!policy.LiveProviderBudget.HasValue)
		{
			return await _publicProvider.GetDataAsync(request, cancellationToken);
		}
		using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task<StockDataResult> publicTask = _publicProvider.GetDataAsync(request, requestCancellation.Token);
		Task timeoutTask = Task.Delay(policy.LiveProviderBudget.Value, cancellationToken);
		Task completed = await Task.WhenAny(publicTask, timeoutTask);
		if (completed == publicTask)
		{
			return await publicTask;
		}
		cancellationToken.ThrowIfCancellationRequested();
		requestCancellation.Cancel();
		_ = publicTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
		_health?.RecordFailure(DataSourceKind.EastMoney, request.Operation, ProviderFailureKind.Timeout,
			"Live provider budget exceeded.", policy.LiveProviderBudget.Value);
		Serilog.Log.Warning("Market data provider budget exceeded. Operation={Operation} Provider={Provider} Failure={FailureKind} DurationMs={DurationMs}",
			request.Operation, DataSourceKind.EastMoney, ProviderFailureKind.Timeout, policy.LiveProviderBudget.Value.TotalMilliseconds);
		string json = request.Path == "/api/etf" ? "{\"data\":{\"list\":[]}}" : "{\"data\":{\"codes\":[]}}";
		return new StockDataResult
		{
			Endpoint = request.Endpoint,
			Handled = true,
			Success = false,
			Json = json,
			Source = DataSourceKind.EastMoney.ToLegacySource(),
			SourceKind = DataSourceKind.EastMoney,
			Error = "代码表同步超过 25 秒预算，已切换本地缓存并保留断点。",
			FailureKind = ProviderFailureKind.Timeout
		};
	}
}
