#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;
using Serilog;

namespace AIHelper.Services.StockData;

public sealed class KlineDiskCacheStore
 : IDisposable {
	private readonly string _cacheRoot;

	private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

	public KlineDiskCacheStore(string? cacheRoot = null)
	{
		_cacheRoot = string.IsNullOrWhiteSpace(cacheRoot)
			? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "kline-daily")
			: cacheRoot;
	}

	public async Task<string?> GetIfFreshAsync(string code, CancellationToken cancellationToken = default)
	{
		string normalizedCode = NormalizeCode(code);
		if (normalizedCode.Length != 6)
		{
			return null;
		}
		string path = GetPath(normalizedCode);
		if (!File.Exists(path))
		{
			return null;
		}
		SemaphoreSlim fileLock = _fileLocks.GetOrAdd(normalizedCode, _ => new SemaphoreSlim(1, 1));
		await fileLock.WaitAsync(cancellationToken);
		try
		{
			await using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			KlineDiskCacheEntry? entry = await JsonSerializer.DeserializeAsync<KlineDiskCacheEntry>(stream, cancellationToken: cancellationToken);
			if (entry == null || string.IsNullOrWhiteSpace(entry.Payload))
			{
				return null;
			}
			if (entry.UpdatedAt.ToOffset(TimeSpan.FromHours(8)).Date != TimeHelper.BeijingNow.Date)
			{
				return null;
			}
			return entry.Payload;
		}
		catch
		{
			return null;
		}
		finally
		{
			fileLock.Release();
		}
	}

	public async Task SaveAsync(string code, string payload, CancellationToken cancellationToken = default)
	{
		string normalizedCode = NormalizeCode(code);
		if (normalizedCode.Length != 6 || string.IsNullOrWhiteSpace(payload))
		{
			return;
		}
		Directory.CreateDirectory(_cacheRoot);
		string path = GetPath(normalizedCode);
		string temporaryPath = path + ".tmp";
		SemaphoreSlim fileLock = _fileLocks.GetOrAdd(normalizedCode, _ => new SemaphoreSlim(1, 1));
		await fileLock.WaitAsync(cancellationToken);
		try
		{
			KlineDiskCacheEntry entry = new KlineDiskCacheEntry
			{
				Code = normalizedCode,
				UpdatedAt = DateTimeOffset.UtcNow,
				Payload = payload
			};
			await using (FileStream stream = File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
			{
				await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: cancellationToken);
			}
			File.Move(temporaryPath, path, true);
		}
		catch
		{
			try
			{
				if (File.Exists(temporaryPath))
				{
					File.Delete(temporaryPath);
				}
			}
			catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
		}
		finally
		{
			fileLock.Release();
		}
	}

	private string GetPath(string normalizedCode)
	{
		return Path.Combine(_cacheRoot, normalizedCode + ".json");
	}

	private static string NormalizeCode(string code)
	{
		return (code ?? "").Trim().ToLowerInvariant().Replace("sh", "").Replace("sz", "").Replace("bj", "");
	}

	private sealed class KlineDiskCacheEntry
	{
		public string Code { get; set; } = "";

		public DateTimeOffset UpdatedAt { get; set; }

		public string Payload { get; set; } = "";
	}

    public void Dispose()
    {
        foreach (var lockObj in _fileLocks.Values)
        {
            lockObj?.Dispose();
        }
        _fileLocks.Clear();
    }
}