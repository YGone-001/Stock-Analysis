using System.Globalization;
using System.Text.Json;
using AIHelper.Models;

namespace AIHelper.Services.StockData;

/// <summary>
/// Endpoint-specific EastMoney quote mappings. Field identifiers are not interchangeable
/// between ulist.np/get and stock/get.
/// </summary>
public static class EastMoneyQuoteMapper
{
    public const string BatchEndpoint = "ulist.np/get";
    public const string SingleEndpoint = "stock/get";
    public const string BatchOuterVolumeField = "f34";
    public const string BatchInnerVolumeField = "f35";
    public const string SingleOuterVolumeField = "f49";
    public const string SingleInnerVolumeField = "f161";

    public static StockQuoteSnapshot MapBatch(JsonElement item)
    {
        double? price = GetNullableDouble(item, "f2");
        double? preClose = GetNullableDouble(item, "f18");
        return new StockQuoteSnapshot
        {
            SourceEndpoint = BatchEndpoint,
            Code = GetString(item, "f12"),
            Name = GetString(item, "f14"),
            Price = price,
            PreClose = preClose,
            Percent = GetNullableDouble(item, "f3"),
            Volume = GetNullableDouble(item, "f5"),
            Amount = GetNullableDouble(item, "f6"),
            Turnover = GetNullableDouble(item, "f8"),
            // In ulist.np/get these two fields are outer/inner volume. Never fall back to
            // stock/get's f49/f161 because the same field IDs have endpoint-specific schemas.
            OuterVolume = GetNullableDouble(item, BatchOuterVolumeField),
            InnerVolume = GetNullableDouble(item, BatchInnerVolumeField),
            K = new StockQuotePriceSnapshot
            {
                Close = ToNullableMilli(price),
                Last = ToNullableMilli(preClose),
                PreClose = ToNullableMilli(preClose),
                Open = ToNullableMilli(GetNullableDouble(item, "f17")),
                High = ToNullableMilli(GetNullableDouble(item, "f15")),
                Low = ToNullableMilli(GetNullableDouble(item, "f16"))
            }
        };
    }

    public static StockQuoteSnapshot MapSingle(JsonElement item)
    {
        double? price = GetNullableDouble(item, "f43");
        double? preClose = GetNullableDouble(item, "f60");
        return new StockQuoteSnapshot
        {
            SourceEndpoint = SingleEndpoint,
            Code = GetString(item, "f57"),
            Name = GetString(item, "f58"),
            Price = price,
            PreClose = preClose,
            Percent = GetNullableDouble(item, "f170"),
            Volume = GetNullableDouble(item, "f47"),
            Amount = GetNullableDouble(item, "f48"),
            Turnover = GetNullableDouble(item, "f168"),
            // stock/get's only accepted outer/inner fields. f34/f35 are order-book fields
            // on this endpoint and must never be used as fallbacks.
            OuterVolume = GetNullableDouble(item, SingleOuterVolumeField),
            InnerVolume = GetNullableDouble(item, SingleInnerVolumeField),
            K = new StockQuotePriceSnapshot
            {
                Close = ToNullableMilli(price),
                Last = ToNullableMilli(preClose),
                PreClose = ToNullableMilli(preClose),
                Open = ToNullableMilli(GetNullableDouble(item, "f46")),
                High = ToNullableMilli(GetNullableDouble(item, "f44")),
                Low = ToNullableMilli(GetNullableDouble(item, "f45"))
            }
        };
    }

    private static double? GetNullableDouble(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return double.IsFinite(number) ? number : null;
        }
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
        {
            return double.IsFinite(number) ? number : null;
        }
        return null;
    }

    private static string GetString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value))
        {
            return "";
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    }

    private static long? ToNullableMilli(double? value)
    {
        return value is > 0 ? (long)Math.Round(value.Value * 1000) : value == 0 ? 0 : null;
    }
}
