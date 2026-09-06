using System.Text.Json;
using AIHelper.Models;

namespace AIHelper.Services.StockData;

public static class StockQuoteContractValidator
{
    public static bool IsCurrentSchema(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array
                || data.GetArrayLength() == 0)
            {
                return false;
            }

            return data.EnumerateArray().All(row =>
                row.TryGetProperty("QuoteSchemaVersion", out JsonElement version)
                && version.TryGetInt32(out int value)
                && value == StockQuoteSnapshot.CurrentSchemaVersion
                && row.TryGetProperty("OuterVolume", out _)
                && row.TryGetProperty("InnerVolume", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
