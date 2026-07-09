using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;
using Serilog;
using Microsoft.Extensions.Caching.Memory;

namespace AIHelper.Services.StockData;

public sealed class LocalStockCacheProvider : IStockDataProvider
, IDisposable {
	private readonly string _cachePath;
	private readonly IMemoryCache _memoryCache;
	private const string CacheKey = "LocalStockCacheDocument";

	private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

	private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public LocalStockCacheProvider(IMemoryCache memoryCache = null, string cachePath = null)
	{
		_memoryCache = memoryCache;
		_cachePath = string.IsNullOrWhiteSpace(cachePath)
			? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "StockNameMap.json")
			: cachePath;
	}

	public bool CanHandle(StockDataRequest request)
	{
		return request.Path == "/api/codes" || request.Path == "/api/etf" || request.Path == "/api/search";
	}

	public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
	{
		if (!CanHandle(request))
		{
			return StockDataResult.NotHandled(request.Endpoint);
		}
		StockNameCacheSnapshot snapshot = await GetSnapshotAsync(request.Path == "/api/etf" ? "etf" : null, cancellationToken);
		if (!snapshot.Exists)
		{
			return new StockDataResult
			{
				Endpoint = request.Endpoint,
				Handled = true,
				Success = false,
				Source = "LocalStockCache",
				UsedCache = true,
				IsStale = true,
				Error = "StockNameMap cache is empty",
				Json = EmptyJson(request.Path)
			};
		}

		if (request.Path == "/api/search")
		{
			string keyword = request.Get("keyword").Trim();
			var rows = snapshot.Items
				.Where(item => item.Key.Contains(keyword, StringComparison.OrdinalIgnoreCase) || item.Value.Contains(keyword, StringComparison.OrdinalIgnoreCase))
				.Take(20)
				.Select(item => new { code = item.Key, name = item.Value })
				.ToList();
			return SuccessResult(request, JsonSerializer.Serialize(new { code = 0, data = rows }), snapshot);
		}

		var data = snapshot.Items.OrderBy(item => item.Key).Select(item => new { code = item.Key, name = item.Value }).ToList();
		string json = request.Path == "/api/etf"
			? JsonSerializer.Serialize(new { data = new { list = data } })
			: JsonSerializer.Serialize(new { data = new { codes = data } });
		return SuccessResult(request, json, snapshot);
	}

	public async Task<StockNameCacheSnapshot> GetSnapshotAsync(string kind = null, CancellationToken cancellationToken = default)
	{
		await _cacheLock.WaitAsync(cancellationToken);
		try
		{
			StockNameCacheDocument document = await LoadDocumentAsync();
			if (document.Source == "LegacyLocal")
			{
				await SaveDocumentAsync(document);
			}
			Dictionary<string, string> items = document.Items ?? new Dictionary<string, string>();
			if (!string.IsNullOrWhiteSpace(kind))
			{
				var segmented = document.Markets.Values
					.Where(market => string.Equals(market.Kind, kind, StringComparison.OrdinalIgnoreCase))
					.SelectMany(market => market.Items)
					.GroupBy(item => item.Key)
					.ToDictionary(group => group.Key, group => group.Last().Value);
				if (segmented.Count > 0)
				{
					items = segmented;
				}
			}
			bool updatedToday = document.UpdatedAt != default && document.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).Date == TimeHelper.BeijingNow.Date;
			string requiredKind = kind ?? "stock";
			List<StockMarketCache> relevantMarkets = document.Markets.Values.Where(market => string.Equals(market.Kind, requiredKind, StringComparison.OrdinalIgnoreCase)).ToList();
			bool segmentsComplete = relevantMarkets.Count >= 4 && relevantMarkets.All(market => market.Completed && market.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).Date == TimeHelper.BeijingNow.Date);
			bool legacyLooksComplete = document.Source == "LegacyLocal" && document.Items.Count >= 1000 && updatedToday;
			return new StockNameCacheSnapshot
			{
				Items = new Dictionary<string, string>(items),
				UpdatedAt = document.UpdatedAt,
				Source = document.Source,
				IsStale = !(segmentsComplete || legacyLooksComplete)
			};
		}
		finally
		{
			_cacheLock.Release();
		}
	}

	public async Task<StockMarketCache> GetMarketStateAsync(string marketKey, CancellationToken cancellationToken = default)
	{
		await _cacheLock.WaitAsync(cancellationToken);
		try
		{
			StockNameCacheDocument document = await LoadDocumentAsync();
			if (!document.Markets.TryGetValue(marketKey, out var state))
			{
				return new StockMarketCache();
			}
			return CloneMarket(state);
		}
		finally
		{
			_cacheLock.Release();
		}
	}

	public async Task ResetMarketAsync(string marketKey, string kind, string filter, CancellationToken cancellationToken = default)
	{
		await _cacheLock.WaitAsync(cancellationToken);
		try
		{
			StockNameCacheDocument document = await LoadDocumentAsync();
			document.Markets[marketKey] = new StockMarketCache
			{
				Kind = kind,
				Filter = filter,
				NextPage = 1,
				Completed = false,
				UpdatedAt = DateTimeOffset.UtcNow
			};
			await SaveDocumentAsync(document);
		}
		finally
		{
			_cacheLock.Release();
		}
	}

	public async Task MergeMarketPageAsync(string marketKey, string kind, string filter, int nextPage, bool completed, IReadOnlyDictionary<string, string> pageItems, CancellationToken cancellationToken = default)
	{
		await _cacheLock.WaitAsync(cancellationToken);
		try
		{
			StockNameCacheDocument document = await LoadDocumentAsync();
			if (!document.Markets.TryGetValue(marketKey, out var state))
			{
				state = new StockMarketCache();
				document.Markets[marketKey] = state;
			}
			state.Kind = kind;
			state.Filter = filter;
			state.NextPage = nextPage;
			state.Completed = completed;
			state.UpdatedAt = DateTimeOffset.UtcNow;
			foreach (var item in pageItems)
			{
				state.Items[item.Key] = item.Value;
				document.Items[item.Key] = item.Value;
			}
			document.Version = StockNameCacheDocument.CurrentVersion;
			document.UpdatedAt = DateTimeOffset.UtcNow;
			document.Source = "EastMoney";
			await SaveDocumentAsync(document);
		}
		finally
		{
			_cacheLock.Release();
		}
	}

	public async Task MergeItemsAsync(IReadOnlyDictionary<string, string> items, string source, CancellationToken cancellationToken = default)
	{
		if (items == null || items.Count == 0)
		{
			return;
		}
		await _cacheLock.WaitAsync(cancellationToken);
		try
		{
			StockNameCacheDocument document = await LoadDocumentAsync();
			foreach (var item in items)
			{
				document.Items[item.Key] = item.Value;
			}
			document.Version = StockNameCacheDocument.CurrentVersion;
			document.UpdatedAt = DateTimeOffset.UtcNow;
			document.Source = string.IsNullOrWhiteSpace(source) ? "Local" : source;
			await SaveDocumentAsync(document);
		}
		finally
		{
			_cacheLock.Release();
		}
	}

	private async Task<StockNameCacheDocument> LoadDocumentAsync()
	{
		if (_memoryCache != null && _memoryCache.TryGetValue(CacheKey, out StockNameCacheDocument cachedDocument))
		{
			return cachedDocument;
		}

		StockNameCacheDocument document = await ReadDocumentFromDiskAsync();
		if (_memoryCache != null && document != null)
		{
			_memoryCache.Set(CacheKey, document, TimeSpan.FromMinutes(30));
		}
		return document;
	}

	private async Task<StockNameCacheDocument> ReadDocumentFromDiskAsync()
	{
		foreach (string path in GetCandidatePaths())
		{
			try
			{
				if (!File.Exists(path))
				{
					continue;
				}
				string json = await File.ReadAllTextAsync(path);
				using JsonDocument parsed = JsonDocument.Parse(json);
				StockNameCacheDocument document;
				if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("Version", out _))
				{
					document = JsonSerializer.Deserialize<StockNameCacheDocument>(json, _jsonOptions);
				}
				else
				{
					Dictionary<string, string> legacy = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOptions);
					document = new StockNameCacheDocument
					{
						UpdatedAt = File.GetLastWriteTimeUtc(path),
						Source = "LegacyLocal",
						Items = legacy ?? new Dictionary<string, string>()
					};
				}
				if (document != null)
				{
					document.Items ??= new Dictionary<string, string>();
					document.Markets ??= new Dictionary<string, StockMarketCache>();
					foreach (StockMarketCache market in document.Markets.Values)
					{
						market.Items ??= new Dictionary<string, string>();
					}
					return document;
				}
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
		}
		return new StockNameCacheDocument();
	}

	private async Task SaveDocumentAsync(StockNameCacheDocument document)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? AppDomain.CurrentDomain.BaseDirectory);
		string temporaryPath = _cachePath + ".tmp";
		document.Source = "Local";
		await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(document, _jsonOptions));
		File.Move(temporaryPath, _cachePath, true);
		if (_memoryCache != null)
		{
			_memoryCache.Set(CacheKey, document, TimeSpan.FromMinutes(30));
		}
	}

	private IEnumerable<string> GetCandidatePaths()
	{
		yield return _cachePath;
		string current = Path.Combine(Environment.CurrentDirectory, "StockNameMap.json");
		if (!string.Equals(current, _cachePath, StringComparison.OrdinalIgnoreCase))
		{
			yield return current;
		}
		string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		if (!string.IsNullOrWhiteSpace(desktop))
		{
			yield return Path.Combine(desktop, "stock", "stock", "StockNameMap.json");
		}
	}

	private static StockMarketCache CloneMarket(StockMarketCache state)
	{
		return new StockMarketCache
		{
			Kind = state.Kind,
			Filter = state.Filter,
			NextPage = state.NextPage,
			Completed = state.Completed,
			UpdatedAt = state.UpdatedAt,
			Items = new Dictionary<string, string>(state.Items)
		};
	}

	private static StockDataResult SuccessResult(StockDataRequest request, string json, StockNameCacheSnapshot snapshot)
	{
		return new StockDataResult
		{
			Endpoint = request.Endpoint,
			Handled = true,
			Success = true,
			Json = json,
			Source = string.IsNullOrWhiteSpace(snapshot.Source) ? "LocalStockCache" : snapshot.Source,
			UsedCache = true,
			IsStale = snapshot.IsStale
		};
	}

	private static string EmptyJson(string path)
	{
		return path switch
		{
			"/api/search" => "{\"code\":0,\"data\":[]}",
			"/api/etf" => "{\"data\":{\"list\":[]}}",
			_ => "{\"data\":{\"codes\":[]}}"
		};
	}

    public void Dispose()
    {
        _cacheLock?.Dispose();
    }
}