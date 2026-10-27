using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Immutable sector-heat outcome: either a symbol-to-percentile map or an explicit unavailable state.</summary>
public sealed record SparrowBoardHeatResult(bool Available, IReadOnlyDictionary<string, double> HeatPercentileBySymbol)
{
    public static readonly SparrowBoardHeatResult Unavailable = new(false, new Dictionary<string, double>(StringComparer.Ordinal));
}

/// <summary>
/// Sector-heat provider for the live scan: joins the industry-board ranking (clist m:90+t:2) with the
/// full-market symbol-to-industry mapping (clist f100) into a per-symbol board-heat percentile (0-100,
/// midrank over board percent changes). Everything is fail-open: any fetch or parse failure returns
/// <see cref="SparrowBoardHeatResult.Unavailable"/> so scans proceed without the bonus factor.
/// The combined snapshot is cached for <see cref="SparrowDataCachePolicy.BoardHeatTtl"/>.
/// </summary>
public sealed class SparrowBoardHeatService
{
    private readonly IStockDataProvider _provider;
    private readonly SparrowMarketDataCache _cache;

    public SparrowBoardHeatService(IStockDataProvider provider, SparrowMarketDataCache? cache = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _cache = cache ?? new SparrowMarketDataCache();
    }

    public async Task<SparrowBoardHeatResult> GetAsync(bool useCache, CancellationToken cancellationToken)
    {
        if (useCache && _cache.TryGet(SparrowDataCachePolicy.BoardHeatCacheKey, useCache, out string? cached) && !string.IsNullOrWhiteSpace(cached))
        {
            SparrowBoardHeatResult? parsed = Parse(cached);
            if (parsed is { Available: true }) return parsed;
        }

        Task<StockDataResult> boardsTask = _provider.GetDataAsync(StockDataRequest.Parse(SparrowEndpoints.BoardHeat(!useCache)), cancellationToken);
        Task<StockDataResult> mapTask = _provider.GetDataAsync(StockDataRequest.Parse(SparrowEndpoints.IndustryMap(!useCache)), cancellationToken);
        await boardsTask;
        await mapTask;

        StockDataResult boards = boardsTask.Result;
        StockDataResult map = mapTask.Result;
        if (!boards.Success || !map.Success)
            return SparrowBoardHeatResult.Unavailable;

        Dictionary<string, double> percentileByBoardName = PercentileByBoardName(boards.Json);
        Dictionary<string, string> industryBySymbol = IndustryBySymbol(map.Json);
        if (percentileByBoardName.Count == 0 || industryBySymbol.Count == 0)
            return SparrowBoardHeatResult.Unavailable;

        var heat = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string symbol, string industry) in industryBySymbol)
        {
            if (percentileByBoardName.TryGetValue(industry, out double percentile))
                heat[symbol] = percentile;
        }
        if (heat.Count == 0)
            return SparrowBoardHeatResult.Unavailable;

        var result = new SparrowBoardHeatResult(true, heat);
        _cache.Set(SparrowDataCachePolicy.BoardHeatCacheKey, JsonSerializer.Serialize(new BoardHeatSnapshotPayload(heat)), SparrowDataCachePolicy.BoardHeatTtl);
        return result;
    }

    /// <summary>Midrank percentile (0-100) of each board's percent change across all ranked boards.</summary>
    private static Dictionary<string, double> PercentileByBoardName(string? json)
    {
        var percentByName = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json ?? "");
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return percentByName;
            foreach (JsonElement item in data.EnumerateArray())
            {
                string? name = TryGetString(item, "Name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (item.TryGetProperty("Percent", out JsonElement percent) && percent.ValueKind == JsonValueKind.Number)
                    percentByName[name] = percent.GetDouble();
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        if (percentByName.Count == 0) return percentByName;
        double[] sorted = percentByName.Values.Order().ToArray();
        // Same midrank convention as the ranking engine's PercentileRank: 0..100 with the endpoints reached.
        double denominator = sorted.Length > 1 ? sorted.Length - 1 : 1;
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string name, double value) in percentByName)
        {
            int less = 0;
            int equal = 0;
            foreach (double item in sorted)
            {
                if (item < value) less++;
                else if (item == value) equal++;
            }
            result[name] = sorted.Length == 1 ? 100.0 : (less + (equal - 1) / 2.0) / denominator * 100.0;
        }
        return result;
    }

    private static Dictionary<string, string> IndustryBySymbol(string? json)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json ?? "");
            if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return map;
            foreach (JsonElement item in data.EnumerateArray())
            {
                string? code = TryGetString(item, "Code");
                string? industry = TryGetString(item, "Industry");
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(industry)) continue;
                map[SparrowSymbolNormalizer.Normalize(code)] = industry;
            }
        }
        catch (JsonException)
        {
        }
        return map;
    }

    /// <summary>Parses a persisted board-heat snapshot back into a result; null for malformed or empty payloads.</summary>
    public static SparrowBoardHeatResult? ParseCached(string json)
    {
        try
        {
            BoardHeatSnapshotPayload? payload = JsonSerializer.Deserialize<BoardHeatSnapshotPayload>(json);
            if (payload?.Heat is not { Count: > 0 }) return null;
            return new SparrowBoardHeatResult(true, new Dictionary<string, double>(payload.Heat, StringComparer.Ordinal));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SparrowBoardHeatResult? Parse(string json) => ParseCached(json);

    private static string? TryGetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private sealed record BoardHeatSnapshotPayload(Dictionary<string, double> Heat);
}
