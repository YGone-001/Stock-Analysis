using System.Globalization;
using System.Text.Json;
using AIHelper.Core.StockData;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Compatibility boundary between market-data payloads and the V2 technical evaluator.
/// The evaluator deliberately receives only this immutable, transport-free snapshot.
/// </summary>
public static class SparrowKlineSnapshotFactory
{
    public static SparrowKlineSnapshot? FromSeries(KlineSeries? series)
    {
        if (series == null)
        {
            return null;
        }

        IReadOnlyList<KlineBar> bars = series.Bars;
        var closesNewestFirst = new List<double>();
        var volumesNewestFirst = new List<double>();
        var datesNewestFirst = new List<DateOnly>();
        double latestPrice = 0;
        double latestPercent = 0;
        for (int index = bars.Count - 1; index >= 0; index--)
        {
            double? close = bars[index].Close;
            if (!close.HasValue)
            {
                continue;
            }

            if (close.Value > 0)
            {
                closesNewestFirst.Add(close.Value);
                double? volume = bars[index].Volume;
                volumesNewestFirst.Add(volume.HasValue && double.IsFinite(volume.Value) && volume.Value >= 0 ? volume.Value : double.NaN);
                datesNewestFirst.Add(DateOnly.FromDateTime(bars[index].Date));
            }
            if (index == bars.Count - 1)
            {
                latestPrice = close.Value;
                double? previous = index > 0 ? bars[index - 1].Close : null;
                latestPercent = previous is > 0
                    ? (close.Value - previous.Value) / previous.Value * 100.0
                    : 0;
            }
        }

        return new SparrowKlineSnapshot(closesNewestFirst, latestPrice, latestPercent, volumesNewestFirst, datesNewestFirst);
    }

    /// <summary>Reads a yuan-unit cached K-line payload; never call from a rule evaluator.</summary>
    public static SparrowKlineSnapshot? FromCachedJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonElement data = root.ValueKind == JsonValueKind.Array
                ? root
                : TryGetPropertyIgnoreCase(root, "data", out JsonElement wrapped) ? wrapped : default;
            if (data.ValueKind == JsonValueKind.Object)
            {
                data = TryGetPropertyIgnoreCase(data, "list", out JsonElement list) ? list
                    : TryGetPropertyIgnoreCase(data, "klines", out JsonElement klines) ? klines
                    : default;
            }
            if (data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement[] elements = data.EnumerateArray().ToArray();
            var closesNewestFirst = new List<double>();
            var volumesNewestFirst = new List<double>();
            var datesNewestFirst = new List<DateOnly>();
            double latestPrice = 0;
            double latestPercent = 0;
            for (int index = elements.Length - 1; index >= 0; index--)
            {
                if (!TryGetDouble(elements[index], "Close", out double close))
                {
                    continue;
                }

                if (close > 0)
                {
                    closesNewestFirst.Add(close);
                    // Older cache entries predate the volume field; a missing or invalid value becomes NaN.
                    volumesNewestFirst.Add(TryGetDouble(elements[index], "Volume", out double volume) && double.IsFinite(volume) && volume >= 0 ? volume : double.NaN);
                    if (TryGetDate(elements[index], out DateOnly barDate)) datesNewestFirst.Add(barDate);
                }
                if (index == elements.Length - 1)
                {
                    latestPrice = close;
                    if (index > 0 && TryGetDouble(elements[index - 1], "Close", out double previous))
                    {
                        latestPercent = previous > 0 ? (close - previous) / previous * 100.0 : 0;
                    }
                }
            }

            // Dates are only usable when they align with every close; partial capture drops them entirely.
            IReadOnlyList<DateOnly>? alignedDates = datesNewestFirst.Count == closesNewestFirst.Count ? datesNewestFirst : null;
            return new SparrowKlineSnapshot(closesNewestFirst, latestPrice, latestPercent, volumesNewestFirst, alignedDates);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement property))
        {
            return false;
        }
        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out value);
        }
        return property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryGetDate(JsonElement element, out DateOnly date)
    {
        date = default;
        if (!TryGetPropertyIgnoreCase(element, "Date", out JsonElement property))
        {
            return false;
        }
        string? raw = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        return raw is not null
            && (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                || DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out date));
    }
}
