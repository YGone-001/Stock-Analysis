using System;
using System.Collections.Generic;

namespace AIHelper.Services.StockData;

public sealed class StockNameCacheDocument
{
	public const int CurrentVersion = 2;

	public int Version { get; set; } = CurrentVersion;

	public DateTimeOffset UpdatedAt { get; set; }

	public string Source { get; set; } = "Local";

	public Dictionary<string, string> Items { get; set; } = new Dictionary<string, string>();

	public Dictionary<string, StockMarketCache> Markets { get; set; } = new Dictionary<string, StockMarketCache>();
}

public sealed class StockMarketCache
{
	public string Kind { get; set; } = "stock";

	public string Filter { get; set; } = "";

	public int NextPage { get; set; } = 1;

	public bool Completed { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }

	public Dictionary<string, string> Items { get; set; } = new Dictionary<string, string>();
}

public sealed class StockNameCacheSnapshot
{
	public Dictionary<string, string> Items { get; init; } = new Dictionary<string, string>();

	public DateTimeOffset UpdatedAt { get; init; }

	public string Source { get; init; } = "";

	public bool IsStale { get; init; }

	public bool Exists => Items.Count > 0;
}
