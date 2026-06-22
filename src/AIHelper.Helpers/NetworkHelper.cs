using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Models;
using AIHelper.Services.StockData;

namespace AIHelper.Helpers;

public static class NetworkHelper
{
	private static HttpClient _client;

	private static LocalStockCacheProvider _localCacheProvider;

	private static FallbackStockDataProvider _stockDataProvider;

	public static event Action<StockDataResult> StockDataStatusChanged;

	static NetworkHelper()
	{
		ReloadProxySettings();
	}

	public static void ReloadProxySettings()
	{
		AppConfig config = ConfigManager.Load();
		HttpClientHandler handler = new HttpClientHandler();
		if (config.IsProxyEnabled)
		{
			string address = config.ProxyAddress.Trim();
			if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
			{
				address = "http://" + address;
			}
			WebProxy proxy = new WebProxy(new Uri(address + ":" + config.ProxyPort));
			if (!string.IsNullOrEmpty(config.ProxyUserName))
			{
				proxy.Credentials = new NetworkCredential(config.ProxyUserName, config.ProxyPassword);
			}
			handler.Proxy = proxy;
			handler.UseProxy = true;
		}
		else
		{
			handler.UseProxy = false;
		}

		HttpClient previousClient = _client;
		_client = new HttpClient(handler)
		{
			Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds > 0 ? config.TimeoutSeconds : 10)
		};
		_client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
		_localCacheProvider ??= new LocalStockCacheProvider();
		var eastMoney = new EastMoneyStockDataProvider(_client, _localCacheProvider);
		_stockDataProvider = new FallbackStockDataProvider(eastMoney, _localCacheProvider);
		_stockDataProvider.StatusChanged += result => StockDataStatusChanged?.Invoke(result);
		previousClient?.Dispose();
	}

	public static async Task<string> GetDataAsync(string endpoint)
	{
		return (await GetDataResultAsync(endpoint)).Json;
	}

	public static async Task<StockDataResult> GetDataResultAsync(string endpoint, CancellationToken cancellationToken = default)
	{
		if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
		{
			StockDataRequest request = StockDataRequest.Parse(endpoint);
			FallbackStockDataProvider provider = _stockDataProvider;
			if (provider != null && provider.CanHandle(request))
			{
				return await provider.GetDataAsync(request, cancellationToken);
			}
			throw new NotSupportedException("Unsupported stock-data endpoint: " + endpoint);
		}

		if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) || !IsEastMoneyHost(uri.Host))
		{
			throw new InvalidOperationException("Stock-data requests are restricted to East Money: " + endpoint);
		}

		string json = await SendEastMoneyGetAsync(uri, cancellationToken);
		return new StockDataResult
		{
			Endpoint = endpoint,
			Handled = true,
			Success = true,
			Json = json,
			Source = "EastMoney"
		};
	}

	public static Task<StockNameCacheSnapshot> GetStockNameCacheSnapshotAsync(CancellationToken cancellationToken = default)
	{
		return _localCacheProvider.GetSnapshotAsync(null, cancellationToken);
	}

	public static Task MergeStockNameCacheAsync(IReadOnlyDictionary<string, string> items, string source, CancellationToken cancellationToken = default)
	{
		return _localCacheProvider.MergeItemsAsync(items, source, cancellationToken);
	}

	private static bool IsEastMoneyHost(string host)
	{
		return host.Equals("eastmoney.com", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".eastmoney.com", StringComparison.OrdinalIgnoreCase);
	}

	private static async Task<string> SendEastMoneyGetAsync(Uri uri, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
		request.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
		using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync(cancellationToken);
	}

	public static bool PingServer(string host)
	{
		try
		{
			using Ping ping = new Ping();
			return ping.Send(host, 1000).Status == IPStatus.Success;
		}
		catch
		{
			return false;
		}
	}

	public static async Task<string> CheckUrlStatus(string url)
	{
		try
		{
			AppConfig config = ConfigManager.Load();
			HttpClientHandler handler = new HttpClientHandler { UseProxy = false };
			if (config.IsProxyEnabled)
			{
				string address = config.ProxyAddress.Trim();
				if (!address.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("socks5", StringComparison.OrdinalIgnoreCase)) address = "http://" + address;
				WebProxy proxy = new WebProxy(new Uri(address + ":" + config.ProxyPort));
				if (!string.IsNullOrEmpty(config.ProxyUserName)) proxy.Credentials = new NetworkCredential(config.ProxyUserName, config.ProxyPassword);
				handler.Proxy = proxy;
				handler.UseProxy = true;
			}
			using HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, url);
			using HttpResponseMessage response = await client.SendAsync(request);
			return response.IsSuccessStatusCode ? "200" : response.StatusCode.ToString();
		}
		catch
		{
			return "Error";
		}
	}
}
