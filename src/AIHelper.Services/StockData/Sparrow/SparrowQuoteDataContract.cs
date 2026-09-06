using System.Globalization;
using System.Text.Json;

namespace AIHelper.Services.StockData.Sparrow;

public enum SparrowVolumeCheckResult
{
    OuterInnerUnavailable,
    VolRatioFailed,
    Passed
}

public readonly record struct SparrowQuoteData(
    double? Price,
    double? PreClose,
    double? Percent,
    double? Amount,
    double? Turnover,
    double? OuterVolume,
    double? InnerVolume)
{
    public double? PriceDerivedPercent => Price.HasValue && PreClose is > 0
        ? (Price.Value - PreClose.Value) / PreClose.Value * 100.0
        : null;
}

/// <summary>Reads the normalized /api/quote wire contract consumed by Sparrow scanners.</summary>
public static class SparrowQuoteDataContract
{
    public static bool TryParse(JsonElement item, out SparrowQuoteData quote)
    {
        double? price = ReadNullable(item, "Price");
        double? preClose = ReadNullable(item, "PreClose");
        if (TryGetPropertyIgnoreCase(item, "K", out JsonElement k))
        {
            price ??= DivideMilli(ReadNullable(k, "Close"));
            preClose ??= DivideMilli(ReadNullable(k, "Last")) ?? DivideMilli(ReadNullable(k, "PreClose"));
        }

        quote = new SparrowQuoteData(
            price,
            preClose,
            ReadNullable(item, "Percent"),
            ReadNullable(item, "Amount") ?? ReadNullable(item, "TotalAmount"),
            ReadNullable(item, "Turnover"),
            ReadNullable(item, "OuterVolume") ?? ReadNullable(item, "Wp"),
            ReadNullable(item, "InnerVolume") ?? ReadNullable(item, "Np"));
        return price.HasValue;
    }

    public static SparrowVolumeCheckResult EvaluateVolume(
        double? outerVolume,
        double? innerVolume,
        double volRatio)
    {
        if (!outerVolume.HasValue || !innerVolume.HasValue)
        {
            return SparrowVolumeCheckResult.OuterInnerUnavailable;
        }

        return outerVolume > 0
            && innerVolume > 0
            && outerVolume > innerVolume * volRatio
                ? SparrowVolumeCheckResult.Passed
                : SparrowVolumeCheckResult.VolRatioFailed;
    }

    private static double? DivideMilli(double? value) => value.HasValue ? value.Value / 1000.0 : null;

    private static double? ReadNullable(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement value)
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
}
