using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Serilog;

namespace AIHelper.Models;

public static class HoldingsManager
{
	private static readonly string FilePath;
	private static readonly object _lockObj = new object();
	private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	private static ConcurrentDictionary<string, HoldingInfo> _holdings;

	static HoldingsManager()
	{
		FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "holdings.json");
		_holdings = new ConcurrentDictionary<string, HoldingInfo>();
		Load();
	}

	public static void Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				var dict = JsonSerializer.Deserialize<Dictionary<string, HoldingInfo>>(File.ReadAllText(FilePath));
				_holdings = dict != null ? new ConcurrentDictionary<string, HoldingInfo>(dict) : new ConcurrentDictionary<string, HoldingInfo>();
			}
		}
		catch
		{
			_holdings = new ConcurrentDictionary<string, HoldingInfo>();
		}
	}

	public static void Save()
	{
		try
		{
			lock (_lockObj)
			{
				string contents = JsonSerializer.Serialize(_holdings, _jsonOptions);
				File.WriteAllText(FilePath, contents);
			}
		}
		catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
	}

	public static HoldingInfo GetHolding(string code)
	{
		if (_holdings.TryGetValue(code, out var value))
		{
			return value;
		}
		return null;
	}

	public static void SetHolding(string code, double costPrice, int volume)
	{
		if (volume <= 0)
		{
			_holdings.TryRemove(code, out _);
		}
		else
		{
			_holdings[code] = new HoldingInfo
			{
				CostPrice = costPrice,
				Volume = volume
			};
		}
		Save();
	}
}
