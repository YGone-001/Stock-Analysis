using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelper.Services.StockData;

public sealed class FallbackStockDataProvider : IStockDataProvider
{
	private readonly IStockDataProvider _publicProvider;

	private readonly LocalStockCacheProvider _cacheProvider;

	private readonly ConcurrentDictionary<string, byte> _backgroundRefreshes = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

	public event Action<StockDataResult> StatusChanged;

	public FallbackStockDataProvider(IStockDataProvider publicProvider, LocalStockCacheProvider cacheProvider)
	{
		_publicProvider = publicProvider;
		_cacheProvider = cacheProvider;
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

		bool isCodeTable = request.Path == "/api/codes" || request.Path == "/api/etf";
		if (isCodeTable && !request.ForceRefresh)
		{
			StockDataResult local = await _cacheProvider.GetDataAsync(request, cancellationToken);
			if (local.Success)
			{
				StockDataLog.Write(request.Path, "-", "-", null, true, "cacheSource=" + local.Source + ", stale=" + local.IsStale);
				if (local.IsStale)
				{
					StartBackgroundRefresh(request);
				}
				StatusChanged?.Invoke(local);
				return local;
			}
		}

		StockDataResult publicResult = isCodeTable
			? await GetPublicWithBudgetAsync(request, cancellationToken)
			: await _publicProvider.GetDataAsync(request, cancellationToken);
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
					UsedCache = true,
					IsStale = local.IsStale,
					Error = publicResult.Error
				};
				StockDataLog.Write(request.Path, "-", "-", null, true, "publicFailure=" + publicResult.Error + ", using local cache");
				StatusChanged?.Invoke(fallback);
				return fallback;
			}
		}

		StatusChanged?.Invoke(publicResult);
		return publicResult;
	}

	private void StartBackgroundRefresh(StockDataRequest request)
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
				StockDataResult result = await GetPublicWithBudgetAsync(refreshRequest, CancellationToken.None);
				StockDataResult backgroundResult = new StockDataResult
				{
					Endpoint = result.Endpoint,
					Handled = result.Handled,
					Success = result.Success,
					Json = result.Json,
					Source = result.Source,
					UsedCache = result.UsedCache,
					IsStale = result.IsStale,
					Error = result.Error,
					IsBackgroundRefresh = true
				};
				StatusChanged?.Invoke(backgroundResult);
			}
			finally
			{
				_backgroundRefreshes.TryRemove(request.Path, out _);
			}
		});
	}

	private async Task<StockDataResult> GetPublicWithBudgetAsync(StockDataRequest request, CancellationToken cancellationToken)
	{
		using CancellationTokenSource requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task<StockDataResult> publicTask = _publicProvider.GetDataAsync(request, requestCancellation.Token);
		Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(25), cancellationToken);
		Task completed = await Task.WhenAny(publicTask, timeoutTask);
		if (completed == publicTask)
		{
			return await publicTask;
		}
		cancellationToken.ThrowIfCancellationRequested();
		requestCancellation.Cancel();
		_ = publicTask.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
		string json = request.Path == "/api/etf" ? "{\"data\":{\"list\":[]}}" : "{\"data\":{\"codes\":[]}}";
		return new StockDataResult
		{
			Endpoint = request.Endpoint,
			Handled = true,
			Success = false,
			Json = json,
			Source = "EastMoney",
			Error = "代码表同步超过 25 秒预算，已切换本地缓存并保留断点。"
		};
	}
}
