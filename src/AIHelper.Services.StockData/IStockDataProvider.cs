using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AIHelper.Services.StockData;

public interface IStockDataProvider
{
	bool CanHandle(StockDataRequest request);

	Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default);
}

public sealed class StockDataRequest
{
	public string Endpoint { get; }

	public string Path { get; }

	public IReadOnlyDictionary<string, string> Query { get; }

	public bool ForceRefresh => GetBoolean("force") || GetBoolean("refresh");

	private StockDataRequest(string endpoint, string path, IReadOnlyDictionary<string, string> query)
	{
		Endpoint = endpoint;
		Path = path;
		Query = query;
	}

	public string Get(string key)
	{
		return Query.TryGetValue(key, out var value) ? value : "";
	}

	private bool GetBoolean(string key)
	{
		string value = Get(key);
		return value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
	}

	public static StockDataRequest Parse(string endpoint)
	{
		string value = endpoint ?? "";
		string path = value;
		string queryText = "";
		int separator = value.IndexOf('?');
		if (separator >= 0)
		{
			path = value.Substring(0, separator);
			queryText = value.Substring(separator + 1);
		}
		var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string pair in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			int equals = pair.IndexOf('=');
			string key = Uri.UnescapeDataString(equals >= 0 ? pair.Substring(0, equals) : pair);
			string itemValue = Uri.UnescapeDataString(equals >= 0 ? pair.Substring(equals + 1) : "");
			query[key] = itemValue;
		}
		return new StockDataRequest(value, path, query);
	}
}

public sealed class StockDataResult
{
	public string Endpoint { get; init; } = "";

	public bool Handled { get; init; }

	public bool Success { get; init; }

	public string Json { get; init; } = "";

	public string Source { get; init; } = "";

	public bool UsedCache { get; init; }

	public bool IsStale { get; init; }

	public bool IsBackgroundRefresh { get; init; }

	public string Error { get; init; } = "";

	public static StockDataResult NotHandled(string endpoint)
	{
		return new StockDataResult { Endpoint = endpoint, Handled = false };
	}
}
