using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;
using AIHelper.Models;

namespace AIHelper.Services.StockData;

public sealed class ExternalStockDataProvider : IStockDataProvider
{
	private readonly HttpClient _client;

	private static readonly HashSet<string> SupportedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"/api/quote", "/api/kline-all", "/api/index", "/api/minute", "/api/minute-trade-all", "/api/search", "/api/codes", "/api/etf", "/api/workday"
	};

	public ExternalStockDataProvider(HttpClient client)
	{
		_client = client ?? throw new ArgumentNullException(nameof(client));
	}

	public bool CanHandle(StockDataRequest request)
	{
		return SupportedPaths.Contains(request.Path) && TryGetBaseUrl(out _);
	}

	public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
	{
		if (!SupportedPaths.Contains(request.Path) || !TryGetBaseUrl(out string baseUrl))
		{
			return StockDataResult.NotHandled(request.Endpoint);
		}

		string url = baseUrl + request.Endpoint;
		try
		{
			using HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Get, url);
			using HttpResponseMessage response = await _client.SendAsync(message, cancellationToken);
			response.EnsureSuccessStatusCode();
			string json = await response.Content.ReadAsStringAsync(cancellationToken);
			StockDataLog.Write(request.Path, request.Get("code"), url, null, false, "external gateway");
			return new StockDataResult { Endpoint = request.Endpoint, Handled = true, Success = true, Json = json, Source = "ExternalGateway" };
		}
		catch (Exception ex)
		{
			string json = EmptyJsonFor(request.Path);
			StockDataLog.Write(request.Path, request.Get("code"), url, ex, false, "external gateway failed");
			return new StockDataResult { Endpoint = request.Endpoint, Handled = true, Success = false, Json = json, Source = "ExternalGateway", Error = ex.Message };
		}
	}

	private static bool TryGetBaseUrl(out string baseUrl)
	{
		AppConfig config = ConfigManager.Load();
		baseUrl = (config.AkServerUrl ?? "").Trim().TrimEnd('/');
		if (string.IsNullOrWhiteSpace(baseUrl) || IsLegacy98daBaseUrl(baseUrl))
		{
			baseUrl = "";
			return false;
		}
		return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
	}

	private static bool IsLegacy98daBaseUrl(string url)
	{
		return url.Contains("98da.com", StringComparison.OrdinalIgnoreCase);
	}

	private static string EmptyJsonFor(string path)
	{
		return path switch
		{
			"/api/search" => "{\"code\":0,\"data\":[]}",
			"/api/codes" => "{\"data\":{\"codes\":[]}}",
			"/api/etf" => "{\"data\":{\"list\":[]}}",
			"/api/minute" or "/api/minute-trade-all" => "{\"data\":{\"List\":[]}}",
			"/api/workday" => "{\"data\":{\"is_workday\":false,\"previous\":[]}}",
			_ => "{\"data\":[]}"
		};
	}
}
