namespace AIHelper.Core.StockData;

/// <summary>
/// Canonical price-unit conversion for market data. The domain unit is CNY yuan; providers
/// that deliver milli-yuan prices (EastMoney) are normalized here, exactly once, at the
/// transport boundary. Scanners and evaluators must only ever see yuan values.
/// </summary>
public static class MarketDataUnits
{
    /// <summary>Divisor that normalizes a milli-yuan (×1000) price into yuan.</summary>
    public const double MilliDivisor = 1000.0;

    /// <summary>Converts a milli-yuan price into yuan; null stays null.</summary>
    public static double? DivideMilli(double? value) => value.HasValue ? value.Value / MilliDivisor : null;
}
