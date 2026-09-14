using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelper.Services.StockData;

public sealed class StockDataGateway : IStockDataGateway
{
	private readonly IStockDataProvider _provider;
	private readonly IStockDataCache _cache;
	private readonly IStockDataStatusSource _statusSource;
	private readonly IHttpClientFactory _httpClientFactory;

	public StockDataGateway(IStockDataProvider provider, IStockDataCache cache, IStockDataStatusSource statusSource, IHttpClientFactory httpClientFactory)
	{
		_provider = provider;
		_cache = cache;
		_statusSource = statusSource;
		_httpClientFactory = httpClientFactory;
	}

	public event Action<StockDataResult>? StockDataStatusChanged
	{
		add => _statusSource.StatusChanged += value;
		remove => _statusSource.StatusChanged -= value;
	}

	public bool CanHandle(StockDataRequest request) => _provider.CanHandle(request);
	public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default) => _provider.GetDataAsync(request, cancellationToken);
	public Task<StockNameCacheSnapshot> GetStockNameCacheSnapshotAsync(CancellationToken cancellationToken = default) => _cache.GetSnapshotAsync(null, cancellationToken);
	public Task MergeStockNameCacheAsync(IReadOnlyDictionary<string, string> items, string source, CancellationToken cancellationToken = default) => _cache.MergeItemsAsync(items, source, cancellationToken);

	public async Task<string> GetEastMoneyAsync(Uri uri, CancellationToken cancellationToken = default)
	{
		if (!uri.Host.Equals("eastmoney.com", StringComparison.OrdinalIgnoreCase)
			&& !uri.Host.EndsWith(".eastmoney.com", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Stock-data requests are restricted to East Money: " + uri);
		}
		using HttpRequestMessage request = new(HttpMethod.Get, uri);
		request.Version = HttpVersion.Version11;
		request.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
		using HttpResponseMessage response = await _httpClientFactory.CreateClient("EastMoneyStockDataProvider").SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync(cancellationToken);
	}
}
