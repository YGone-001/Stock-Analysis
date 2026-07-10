using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CS8600, CS8603, CS8604, CS8618, CS8625
#pragma warning disable CS8600, CS8603, CS8604, CS8618, CS8625
namespace AIHelper.Helpers;

public static class NetworkHelper
{
	public static IServiceProvider ServiceProvider { get; set; }

	private static HttpClient _client => ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient("EastMoneyStockDataProvider");

	public static HttpClient SharedHttpClient => ServiceProvider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient();

	private static LocalStockCacheProvider _localCacheProvider => ServiceProvider.GetRequiredService<LocalStockCacheProvider>();

	private static IStockDataProvider _stockDataProvider => ServiceProvider.GetRequiredService<IStockDataProvider>();

	public static event Action<StockDataResult> StockDataStatusChanged
	{
		add
		{
			if (ServiceProvider != null)
			{
				var fallback = (dynamic)ServiceProvider.GetRequiredService(Type.GetType("AIHelper.Services.StockData.FallbackStockDataProvider, AIHelper.Services"));
				fallback.StatusChanged += value;
			}
		}
		remove
		{
			if (ServiceProvider != null)
			{
				var fallback = (dynamic)ServiceProvider.GetRequiredService(Type.GetType("AIHelper.Services.StockData.FallbackStockDataProvider, AIHelper.Services"));
				fallback.StatusChanged -= value;
			}
		}
	}

	static NetworkHelper()
	{
	}

	public static WebProxy GetWebProxy(AppConfig config)
	{
		if (!config.IsProxyEnabled || string.IsNullOrWhiteSpace(config.ProxyAddress)) return null;
		string address = config.ProxyAddress.Trim();
		if (!address.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !address.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
		{
			address = "http://" + address;
		}
		string portStr = string.IsNullOrWhiteSpace(config.ProxyPort) ? "" : ":" + config.ProxyPort.Trim();
		WebProxy proxy = new WebProxy(new Uri(address + portStr));
		if (!string.IsNullOrEmpty(config.ProxyUserName))
		{
			proxy.Credentials = new NetworkCredential(config.ProxyUserName, config.ProxyPassword);
		}
		return proxy;
	}

	public static void ReloadProxySettings()
	{
		// With DI, HttpMessageHandler configuration is handled in App.cs
	}


	public static async Task<string> GetDataAsync(string endpoint, CancellationToken cancellationToken = default)
	{
		return (await GetDataResultAsync(endpoint, cancellationToken)).Json;
	}

	public static async Task<StockDataResult> GetDataResultAsync(string endpoint, CancellationToken cancellationToken = default)
	{
		if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
		{
			StockDataRequest request = StockDataRequest.Parse(endpoint);
			IStockDataProvider provider = _stockDataProvider;
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
		request.Version = HttpVersion.Version11;
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
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return false;
		}
	}

	public static async Task<string> CheckUrlStatus(string url)
	{
		try
		{
			AppConfig config = ConfigManager.Load();
			HttpClientHandler handler = new HttpClientHandler { UseProxy = false };
			WebProxy proxy = GetWebProxy(config);
			if (proxy != null)
			{
				handler.Proxy = proxy;
				handler.UseProxy = true;
			}
			using HttpClient client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Head, url);
			using HttpResponseMessage response = await client.SendAsync(request);
			return response.IsSuccessStatusCode ? "200" : response.StatusCode.ToString();
		}
		catch (System.Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
			return "Error";
		}
	}
}
