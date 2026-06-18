using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Models;

namespace AIHelper.Helpers;

public static class NetworkHelper
{
	private static HttpClient _client;

	private static string _basicAuthValue;

	private static bool _isAuthLoaded;

	private static readonly SemaphoreSlim _authSemaphore;

	static NetworkHelper()
	{
		_basicAuthValue = null;
		_isAuthLoaded = false;
		_authSemaphore = new SemaphoreSlim(1, 1);
		ReloadProxySettings();
	}

	public static void ReloadProxySettings()
	{
		AppConfig appConfig = ConfigManager.Load();
		HttpClientHandler httpClientHandler = new HttpClientHandler();
		if (appConfig.IsProxyEnabled)
		{
			string text = appConfig.ProxyAddress.Trim();
			if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
			{
				text = "http://" + text;
			}
			DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(1, 2);
			defaultInterpolatedStringHandler.AppendFormatted(text);
			defaultInterpolatedStringHandler.AppendLiteral(":");
			defaultInterpolatedStringHandler.AppendFormatted(appConfig.ProxyPort);
			WebProxy webProxy = new WebProxy(new Uri(defaultInterpolatedStringHandler.ToStringAndClear()));
			if (!string.IsNullOrEmpty(appConfig.ProxyUserName))
			{
				webProxy.Credentials = new NetworkCredential(appConfig.ProxyUserName, appConfig.ProxyPassword);
			}
			httpClientHandler.Proxy = webProxy;
			httpClientHandler.UseProxy = true;
		}
		else
		{
			httpClientHandler.UseProxy = false;
		}
		HttpClient client = _client;
		_client = new HttpClient(httpClientHandler)
		{
			Timeout = TimeSpan.FromSeconds((appConfig.TimeoutSeconds > 0) ? appConfig.TimeoutSeconds : 10)
		};
		_client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
		client?.Dispose();
	}

	private static async Task LoadAuthIfNeededAsync()
	{
		if (_isAuthLoaded)
		{
			return;
		}
		await _authSemaphore.WaitAsync();
		try
		{
			if (_isAuthLoaded)
			{
				return;
			}
			using HttpClient tempClient = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(10.0)
			};
			using JsonDocument jsonDocument = JsonDocument.Parse(await tempClient.GetStringAsync("https://www.ooppp.com/soft/psw.json"));
			JsonElement rootElement = jsonDocument.RootElement;
			string? @string = rootElement.GetProperty("u").GetString();
			string string2 = rootElement.GetProperty("p").GetString();
			string text = SecurityHelper.Decrypt(@string);
			string text2 = SecurityHelper.Decrypt(string2);
			if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(text2))
			{
				string s = text + ":" + text2;
				_basicAuthValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
			}
			_isAuthLoaded = true;
		}
		catch
		{
		}
		finally
		{
			_authSemaphore.Release();
		}
	}

	public static async Task<string> GetDataAsync(string endpoint)
	{
		if (!endpoint.StartsWith("http", StringComparison.OrdinalIgnoreCase) && TryHandleLocalOrPublicEndpoint(endpoint, out var routedTask))
		{
			return await routedTask;
		}
		AppConfig appConfig = ConfigManager.Load();
		string text = (string.IsNullOrWhiteSpace(appConfig.AkServerUrl) ? "https://www.98da.com" : appConfig.AkServerUrl.TrimEnd('/'));
		string url = (endpoint.StartsWith("http") ? endpoint : (text + endpoint));
		bool isTargetDomain = url.Contains("98da.com");
		if (isTargetDomain)
		{
			await LoadAuthIfNeededAsync();
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
		if (isTargetDomain && !string.IsNullOrEmpty(_basicAuthValue))
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuthValue);
		}
		using HttpResponseMessage response = await _client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync();
	}

	private static bool TryHandleLocalOrPublicEndpoint(string endpoint, out Task<string> task)
	{
		string path = endpoint;
		string query = "";
		int num = endpoint.IndexOf('?');
		if (num >= 0)
		{
			path = endpoint.Substring(0, num);
			query = endpoint.Substring(num + 1);
		}
		switch (path)
		{
		case "/api/quote":
			task = GetPublicQuoteAsync(GetQueryValue(query, "code"));
			return true;
		case "/api/kline-all":
		case "/api/index":
			task = GetPublicKlineAsync(GetQueryValue(query, "code"), GetQueryValue(query, "limit"));
			return true;
		case "/api/search":
			task = GetPublicSearchAsync(GetQueryValue(query, "keyword"));
			return true;
		case "/api/codes":
			task = GetPublicCodeTableAsync(isEtf: false);
			return true;
		case "/api/etf":
			task = GetPublicCodeTableAsync(isEtf: true);
			return true;
		default:
			task = null;
			return false;
		}
	}

	private static async Task<string> GetPublicQuoteAsync(string codes)
	{
		List<string> list = (codes ?? "").Split(new char[1] { ',' }, StringSplitOptions.RemoveEmptyEntries)
			.Select(NormalizeCode)
			.Where((string c) => c.Length == 6)
			.Distinct()
			.ToList();
		if (list.Count == 0)
		{
			return "{\"data\":[]}";
		}
		string secids = string.Join(",", list.Select(ToEastMoneySecId));
		string url = "https://push2.eastmoney.com/api/qt/ulist.np/get?fltt=2&invt=2&fields=f12,f14,f2,f3,f5,f15,f16,f17,f18&secids=" + secids;
		try
		{
			string json = await SendPublicGetAsync(url);
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || !value.TryGetProperty("diff", out var value2) || value2.ValueKind != JsonValueKind.Array)
			{
				return "{\"data\":[]}";
			}
			var data = new List<object>();
			foreach (JsonElement item in value2.EnumerateArray())
			{
				string code = GetString(item, "f12");
				if (string.IsNullOrWhiteSpace(code))
				{
					continue;
				}
				double close = GetDouble(item, "f2");
				double lastClose = GetDouble(item, "f18");
				data.Add(new
				{
					Code = code,
					Name = GetString(item, "f14"),
					TotalHand = GetDouble(item, "f5"),
					BuyLevel = Array.Empty<object>(),
					SellLevel = Array.Empty<object>(),
					K = new
					{
						Close = ToMilli(close),
						Last = ToMilli(lastClose),
						PreClose = ToMilli(lastClose),
						Open = ToMilli(GetDouble(item, "f17")),
						High = ToMilli(GetDouble(item, "f15")),
						Low = ToMilli(GetDouble(item, "f16"))
					}
				});
			}
			return JsonSerializer.Serialize(new { data });
		}
		catch
		{
			return "{\"data\":[]}";
		}
	}

	private static async Task<string> GetPublicKlineAsync(string code, string limitText)
	{
		string rawCode = code ?? "";
		code = NormalizeCode(rawCode);
		if (code.Length != 6)
		{
			return "{\"data\":[]}";
		}
		if (!int.TryParse(limitText, out var limit) || limit <= 0)
		{
			limit = 120;
		}
		string url = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid=" + ToEastMoneySecId(rawCode, code) + "&klt=101&fqt=1&lmt=" + limit + "&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61";
		try
		{
			string json = await SendPublicGetAsync(url);
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("data", out var dataElement) || !dataElement.TryGetProperty("klines", out var klineElement) || klineElement.ValueKind != JsonValueKind.Array)
			{
				return "{\"data\":[]}";
			}
			var list = new List<object>();
			foreach (JsonElement item in klineElement.EnumerateArray())
			{
				string[] parts = (item.GetString() ?? "").Split(',');
				if (parts.Length < 6)
				{
					continue;
				}
				list.Add(new
				{
					Time = parts[0],
					Open = ToMilli(ParseDouble(parts[1])),
					Close = ToMilli(ParseDouble(parts[2])),
					High = ToMilli(ParseDouble(parts[3])),
					Low = ToMilli(ParseDouble(parts[4])),
					Volume = ParseDouble(parts[5])
				});
			}
			return JsonSerializer.Serialize(new { data = list });
		}
		catch
		{
			return "{\"data\":[]}";
		}
	}

	private static async Task<string> GetPublicSearchAsync(string keyword)
	{
		keyword = (keyword ?? "").Trim();
		if (keyword.Length == 0)
		{
			return "{\"code\":0,\"data\":[]}";
		}
		try
		{
			string url = "https://searchapi.eastmoney.com/api/suggest/get?type=14&count=20&token=D43BF722C8E33BDC906FB84D85E326E8&input=" + Uri.EscapeDataString(keyword);
			string json = await SendPublicGetAsync(url);
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("QuotationCodeTable", out var value) || !value.TryGetProperty("Data", out var value2) || value2.ValueKind != JsonValueKind.Array)
			{
				return "{\"code\":0,\"data\":[]}";
			}
			var data = new List<object>();
			Dictionary<string, string> cache = ReadNameMapCache();
			foreach (JsonElement item in value2.EnumerateArray())
			{
				string code = NormalizeCode(GetString(item, "Code"));
				string name = GetString(item, "Name");
				string quoteId = GetString(item, "QuoteID");
				string classify = GetString(item, "Classify");
				string securityTypeName = GetString(item, "SecurityTypeName");
				if (code.Length != 6 || string.IsNullOrWhiteSpace(name) || !IsCnExchangeQuote(quoteId) || !IsSupportedSearchItem(classify, securityTypeName))
				{
					continue;
				}
				cache[code] = name;
				data.Add(new { code, name });
			}
			if (cache.Count > 0)
			{
				WriteNameMapCache(cache);
			}
			return JsonSerializer.Serialize(new { code = 0, data });
		}
		catch
		{
			return "{\"code\":0,\"data\":[]}";
		}
	}

	private static async Task<string> GetPublicCodeTableAsync(bool isEtf)
	{
		Dictionary<string, string> map = ReadNameMapCache();
		if (map.Count == 0)
		{
			try
			{
				string[] fsList = isEtf
					? new string[4] { "b:MK0021", "b:MK0022", "b:MK0023", "b:MK0024" }
					: new string[4] { "m:0+t:6", "m:0+t:80", "m:1+t:2", "m:1+t:23" };
				foreach (string fs in fsList)
				{
					await AppendEastMoneyListAsync(map, fs);
				}
			}
			catch
			{
			}
		}
		if (map.Count == 0)
		{
			map = CreateSeedNameMap();
		}
		if (map.Count > 0)
		{
			WriteNameMapCache(map);
		}
		var rows = map.OrderBy((KeyValuePair<string, string> kvp) => kvp.Key)
			.Select((KeyValuePair<string, string> kvp) => new { code = kvp.Key, name = kvp.Value })
			.ToList();
		if (isEtf)
		{
			return JsonSerializer.Serialize(new { data = new { list = rows } });
		}
		return JsonSerializer.Serialize(new { data = new { codes = rows } });
	}

	private static async Task AppendEastMoneyListAsync(Dictionary<string, string> map, string fs)
	{
		const int pageSize = 5;
		for (int page = 1; page <= 20; page++)
		{
			string url = "https://20.push2.eastmoney.com/api/qt/clist/get?po=1&np=1&fltt=2&invt=2&fid=f12&fields=f12,f14&pn=" + page + "&pz=" + pageSize + "&fs=" + fs;
			string json = await SendPublicGetAsync(url);
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("data", out var dataElement) || !dataElement.TryGetProperty("diff", out var diffElement) || diffElement.ValueKind != JsonValueKind.Array)
			{
				break;
			}
			int count = 0;
			foreach (JsonElement item in diffElement.EnumerateArray())
			{
				count++;
				string code = NormalizeCode(GetString(item, "f12"));
				string name = GetString(item, "f14");
				if (code.Length == 6 && !string.IsNullOrWhiteSpace(name))
				{
					map[code] = name;
				}
			}
			if (count < pageSize)
			{
				break;
			}
		}
	}

	private static async Task<string> SendPublicGetAsync(string url)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
		request.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
		using HttpResponseMessage response = await _client.SendAsync(request);
		response.EnsureSuccessStatusCode();
		return await response.Content.ReadAsStringAsync();
	}

	private static string GetQueryValue(string query, string key)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			return "";
		}
		foreach (string item in query.Split('&'))
		{
			int num = item.IndexOf('=');
			string text = (num >= 0) ? item.Substring(0, num) : item;
			if (string.Equals(Uri.UnescapeDataString(text), key, StringComparison.OrdinalIgnoreCase))
			{
				return Uri.UnescapeDataString((num >= 0) ? item.Substring(num + 1) : "");
			}
		}
		return "";
	}

	private static string NormalizeCode(string code)
	{
		return (code ?? "").Trim().ToLowerInvariant().Replace("sh", "").Replace("sz", "").Replace("bj", "");
	}

	private static string ToEastMoneySecId(string code)
	{
		return ((code.StartsWith("6") || code.StartsWith("5")) ? "1." : "0.") + code;
	}

	private static string ToEastMoneySecId(string rawCode, string normalizedCode)
	{
		rawCode = (rawCode ?? "").Trim().ToLowerInvariant();
		if (rawCode.StartsWith("sh"))
		{
			return "1." + normalizedCode;
		}
		if (rawCode.StartsWith("sz") || rawCode.StartsWith("bj"))
		{
			return "0." + normalizedCode;
		}
		return ToEastMoneySecId(normalizedCode);
	}

	private static bool IsCnExchangeQuote(string quoteId)
	{
		return quoteId.StartsWith("0.", StringComparison.Ordinal) || quoteId.StartsWith("1.", StringComparison.Ordinal);
	}

	private static bool IsSupportedSearchItem(string classify, string securityTypeName)
	{
		if (string.Equals(classify, "AStock", StringComparison.OrdinalIgnoreCase) || string.Equals(classify, "Fund", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		return securityTypeName == "\u6CAAA" || securityTypeName == "\u6DF1A" || securityTypeName == "\u57FA\u91D1";
	}

	private static long ToMilli(double value)
	{
		if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
		{
			return 0L;
		}
		return (long)Math.Round(value * 1000.0);
	}

	private static double GetDouble(JsonElement item, string propertyName)
	{
		if (item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number)
		{
			return value.GetDouble();
		}
		return 0.0;
	}

	private static double ParseDouble(string value)
	{
		if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result))
		{
			return result;
		}
		return 0.0;
	}

	private static string GetString(JsonElement item, string propertyName)
	{
		if (item.TryGetProperty(propertyName, out var value))
		{
			return value.ValueKind switch
			{
				JsonValueKind.String => value.GetString() ?? "",
				JsonValueKind.Number => value.GetRawText(),
				_ => "",
			};
		}
		return "";
	}

	private static Dictionary<string, string> ReadNameMapCache()
	{
		foreach (string path in GetNameMapCachePaths())
		{
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				Dictionary<string, string> dictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
				if (dictionary != null && dictionary.Count > 0)
				{
					return dictionary;
				}
			}
			catch
			{
			}
		}
		return new Dictionary<string, string>();
	}

	private static void WriteNameMapCache(Dictionary<string, string> map)
	{
		try
		{
			string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StockNameMap.json");
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				WriteIndented = true
			};
			File.WriteAllText(path, JsonSerializer.Serialize(map, options));
		}
		catch
		{
		}
	}

	private static IEnumerable<string> GetNameMapCachePaths()
	{
		yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StockNameMap.json");
		yield return Path.Combine(Environment.CurrentDirectory, "StockNameMap.json");
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (!string.IsNullOrWhiteSpace(desktop))
		{
			yield return Path.Combine(desktop, "strock", "strock", "StockNameMap.json");
		}
	}

	private static Dictionary<string, string> CreateSeedNameMap()
	{
		return new Dictionary<string, string>
		{
			["000001"] = "Ping An Bank",
			["000002"] = "Vanke A",
			["000300"] = "CSI 300",
			["399001"] = "SZSE Component",
			["399006"] = "ChiNext Index",
			["510300"] = "CSI 300 ETF",
			["600000"] = "SPD Bank",
			["600519"] = "Kweichow Moutai",
			["601318"] = "Ping An Insurance",
			["601398"] = "ICBC"
		};
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
		_ = 1;
		try
		{
			AppConfig appConfig = ConfigManager.Load();
			HttpClientHandler httpClientHandler = new HttpClientHandler
			{
				UseProxy = false
			};
			if (appConfig.IsProxyEnabled)
			{
				string text = appConfig.ProxyAddress.Trim();
				if (!text.StartsWith("http") && !text.StartsWith("socks5"))
				{
					text = "http://" + text;
				}
				DefaultInterpolatedStringHandler defaultInterpolatedStringHandler = new DefaultInterpolatedStringHandler(1, 2);
				defaultInterpolatedStringHandler.AppendFormatted(text);
				defaultInterpolatedStringHandler.AppendLiteral(":");
				defaultInterpolatedStringHandler.AppendFormatted(appConfig.ProxyPort);
				WebProxy webProxy = new WebProxy(new Uri(defaultInterpolatedStringHandler.ToStringAndClear()));
				if (!string.IsNullOrEmpty(appConfig.ProxyUserName))
				{
					webProxy.Credentials = new NetworkCredential(appConfig.ProxyUserName, appConfig.ProxyPassword);
				}
				httpClientHandler.Proxy = webProxy;
				httpClientHandler.UseProxy = true;
			}
			using HttpClient client = new HttpClient(httpClientHandler)
			{
				Timeout = TimeSpan.FromSeconds(3.0)
			};
			bool isTargetDomain = url.Contains("98da.com");
			if (isTargetDomain)
			{
				await LoadAuthIfNeededAsync();
			}
			HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Head, url);
			if (isTargetDomain && !string.IsNullOrEmpty(_basicAuthValue))
			{
				httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicAuthValue);
			}
			HttpResponseMessage httpResponseMessage = await client.SendAsync(httpRequestMessage);
			if (httpResponseMessage.IsSuccessStatusCode)
			{
				return "200";
			}
			return httpResponseMessage.StatusCode.ToString();
		}
		catch
		{
			return "Error";
		}
	}
}
