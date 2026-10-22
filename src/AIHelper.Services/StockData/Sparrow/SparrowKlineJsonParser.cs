using System.Text.Json;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Reads daily K-line closes from the yuan-unit cached K-line JSON payload.
/// Closes are returned for the Classic technical evaluator.
/// </summary>
public static class SparrowKlineJsonParser
{
    public static List<double> ParseClosesNewestFirst(string json, out double latestPrice)
    {
        List<double> closes = ParseClosesOldestFirst(json);
        closes.Reverse();
        latestPrice = closes.Count > 0 ? closes[0] : 0;
        return closes;
    }

    public static List<double> ParseClosesOldestFirst(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement data = root.ValueKind == JsonValueKind.Array ? root : default;
        if (data.ValueKind == JsonValueKind.Undefined
            && SparrowJsonElement.TryGetPropertyIgnoreCase(root, "data", out JsonElement wrapped))
        {
            data = wrapped.ValueKind == JsonValueKind.Array
                ? wrapped
                : SparrowJsonElement.TryGetPropertyIgnoreCase(wrapped, "list", out JsonElement list) ? list
                : SparrowJsonElement.TryGetPropertyIgnoreCase(wrapped, "klines", out JsonElement klines) ? klines
                : default;
        }

        var closes = new List<double>();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return closes;
        }

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (SparrowJsonElement.TryGetDouble(item, "Close", out double close))
            {
                closes.Add(close);
            }
        }
        return closes;
    }
}
