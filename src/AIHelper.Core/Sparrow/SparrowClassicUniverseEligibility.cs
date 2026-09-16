namespace AIHelper.Core.Sparrow;

public sealed record SparrowClassicEligibilityResult(bool Eligible, string ReasonCode, string Reason);

/// <summary>Stable Classic candidate-universe policy shared by live scans and historical replay.</summary>
public static class SparrowClassicUniverseEligibility
{
    public static SparrowClassicEligibilityResult Evaluate(string? symbol, string? name)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(name))
            return new(false, "IDENTITY_MISSING", "Security code or name is missing.");
        if (name.Contains("ST", StringComparison.OrdinalIgnoreCase))
            return new(false, "ST_EXCLUDED", "ST securities are excluded by Classic universe policy.");
        if (symbol.StartsWith("688", StringComparison.Ordinal))
            return new(false, "STAR_EXCLUDED", "STAR Market securities are excluded by Classic universe policy.");
        if (!symbol.StartsWith("60", StringComparison.Ordinal)
            && !symbol.StartsWith("00", StringComparison.Ordinal)
            && !symbol.StartsWith("30", StringComparison.Ordinal))
            return new(false, "MARKET_EXCLUDED", "Security code is outside the Classic market universe.");
        return new(true, "ELIGIBLE", "Eligible for the Classic universe.");
    }
}
