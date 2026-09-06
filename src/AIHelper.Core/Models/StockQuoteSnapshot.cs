using System.Text.Json.Serialization;

namespace AIHelper.Models;

/// <summary>
/// Normalized quote contract used by all stock strategies.
/// Prices are RMB/share, Amount is RMB, Percent/Turnover are percentage points,
/// and Volume/OuterVolume/InnerVolume are hands (100 shares per hand).
/// Null means the upstream field was absent or could not be parsed; zero is a real value.
/// </summary>
public sealed class StockQuoteSnapshot
{
    public const int CurrentSchemaVersion = 2;

    public int QuoteSchemaVersion => CurrentSchemaVersion;
    public string SourceEndpoint { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Price { get; set; }
    public double? PreClose { get; set; }
    public double? Percent { get; set; }
    public double? Amount { get; set; }
    public double? Volume { get; set; }
    public double? Turnover { get; set; }
    public double? OuterVolume { get; set; }
    public double? InnerVolume { get; set; }

    // Compatibility aliases retained for existing views and scanners.
    public double? TotalHand => Volume;
    public double? TotalAmount => Amount;
    public double? Wp => OuterVolume;
    public double? Np => InnerVolume;
    public object[] BuyLevel { get; set; } = Array.Empty<object>();
    public object[] SellLevel { get; set; } = Array.Empty<object>();
    public StockQuotePriceSnapshot K { get; set; } = new();
}

public sealed class StockQuotePriceSnapshot
{
    /// <summary>Legacy wire representation in milli-RMB/share.</summary>
    public long? Close { get; set; }
    public long? Last { get; set; }
    public long? PreClose { get; set; }
    public long? Open { get; set; }
    public long? High { get; set; }
    public long? Low { get; set; }
}
