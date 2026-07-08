using System;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Serilog;

namespace AIHelper.Services.StockData;

public class EastMoneySpiderService : IDisposable
{
	private CookieContainer _cookieContainer = new CookieContainer();
	private HttpClient _emClient;
	private readonly object _clientLock = new object();

	public EastMoneySpiderService()
	{
		_emClient = CreateSmartClient();
	}

	private HttpClient CreateSmartClient()
	{
		_cookieContainer = new CookieContainer();
		HttpClient httpClient = new HttpClient(new HttpClientHandler
		{
			UseProxy = true,
			CookieContainer = _cookieContainer,
			UseCookies = true,
			AutomaticDecompression = (DecompressionMethods.GZip | DecompressionMethods.Deflate)
		});
		httpClient.Timeout = TimeSpan.FromSeconds(15.0);
		httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
		httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
		httpClient.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");
		httpClient.DefaultRequestHeaders.ConnectionClose = false;
		return httpClient;
	}

	private void RenewEastMoneyClient()
	{
		lock (_clientLock)
		{
			try
			{
				_emClient?.Dispose();
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
			_emClient = CreateSmartClient();
		}
	}

	private async Task<string> FetchEastMoneyDataAsync(string url)
	{
		for (int i = 0; i < 2; i++)
		{
			try
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
				request.Version = HttpVersion.Version11;
				request.Headers.Add("Referer", "http://quote.eastmoney.com/");
				using HttpResponseMessage response = await _emClient.SendAsync(request);
				response.EnsureSuccessStatusCode();
				return await response.Content.ReadAsStringAsync();
			}
			catch (Exception)
			{
				if (i == 0)
				{
					RenewEastMoneyClient();
					await Task.Delay(new Random().Next(300, 800));
					continue;
				}
				throw;
			}
		}
		return null;
	}

	public async Task<string> FetchEastMoneyKLineAsync(string secId)
	{
		long value = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		string url = $"http://push2his.eastmoney.com/api/qt/stock/kline/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61&beg=0&end=20500101&ut=fa5fd1943c7b386f172d6893dbfba10b&rtntype=6&secid={secId}&klt=101&fqt=1&cb=jsonp{value}";
		return await FetchEastMoneyDataAsync(url);
	}

	public async Task<string> FetchEastMoneyQuoteAsync(string secId)
	{
		long value = DateTimeOffset.Now.ToUnixTimeMilliseconds();
		string url = $"http://push2.eastmoney.com/api/qt/stock/get?ut=fa5fd1943c7b386f172d6893dbfba10b&fltt=2&invt=2&fields=f43,f44,f45,f46,f47,f48,f49,f60,f161,f168,f170&secid={secId}&cb=jQuery{value}";
		return await FetchEastMoneyDataAsync(url);
	}

	public void Dispose()
	{
		lock (_clientLock)
		{
			_emClient?.Dispose();
		}
	}
}
