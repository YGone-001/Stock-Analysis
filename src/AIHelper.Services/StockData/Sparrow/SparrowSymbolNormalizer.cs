namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Normalizes raw market-data symbols (e.g. "1.600000") to the bare six-digit
/// A-share code used across the Sparrow scanners.
/// </summary>
public static class SparrowSymbolNormalizer
{
    public static string Normalize(string? code)
    {
        string normalized = (code ?? string.Empty).Trim();
        return normalized.StartsWith("1.", StringComparison.Ordinal)
            || normalized.StartsWith("0.", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }
}
