using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AIHelper.Models;

public static class HoldingsManager
{
	private static readonly string FilePath;

	private static Dictionary<string, HoldingInfo> _holdings;

	static HoldingsManager()
	{
		FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "holdings.json");
		_holdings = new Dictionary<string, HoldingInfo>();
		Load();
	}

	public static void Load()
	{
		try
		{
			if (File.Exists(FilePath))
			{
				_holdings = JsonSerializer.Deserialize<Dictionary<string, HoldingInfo>>(File.ReadAllText(FilePath)) ?? new Dictionary<string, HoldingInfo>();
			}
		}
		catch
		{
			_holdings = new Dictionary<string, HoldingInfo>();
		}
	}

	public static void Save()
	{
		try
		{
			string contents = JsonSerializer.Serialize(_holdings, new JsonSerializerOptions
			{
				WriteIndented = true
			});
			File.WriteAllText(FilePath, contents);
		}
		catch
		{
		}
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
			_holdings.Remove(code);
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
