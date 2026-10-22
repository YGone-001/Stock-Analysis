using System.Globalization;
using System.Text.Json;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Shared, dependency-free JSON reading helpers used by the Sparrow scanners and
/// wire-contract parsers. Field lookups are case-insensitive except for the
/// top-level "data" array extraction.
/// </summary>
public static class SparrowJsonElement
{
    /// <summary>Enumerates the "data" array from a standard market-data JSON envelope.</summary>
    public static IEnumerable<JsonElement> EnumerateDataArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }
        return data.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    public static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
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

    public static bool TryGetDouble(JsonElement element, string name, out double value)
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

    public static string GetString(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement property))
        {
            return string.Empty;
        }
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.GetRawText();
    }
}
