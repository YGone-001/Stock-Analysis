using System;

namespace AIHelper.Models;

/// <summary>
/// Represents the trend state of a market index.
/// </summary>
public enum SparrowMarketState
{
    Unknown,
    Strong,
    Neutral,
    Weak
}

/// <summary>
/// A single point in an intraday trend time series.
/// </summary>
public sealed class SparrowIndexPoint
{
    public DateTime? Time { get; init; }
    public string TimeString { get; init; } = "";
    public double Price { get; init; }
    public double AveragePrice { get; init; }
}

/// <summary>
/// Standardized evaluation snapshot for an index.
/// </summary>
public sealed class SparrowIndexSnapshot
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public SparrowMarketState State { get; init; } = SparrowMarketState.Unknown;
    public double LatestPrice { get; init; }
    public double LatestAverage { get; init; }
    public double EarlierAverage { get; init; }
    public int Samples { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Combined market regime assessment for the Sparrow strategy.
/// </summary>
public sealed class SparrowMarketRegime
{
    private SparrowMarketState _shanghai = SparrowMarketState.Unknown;
    private SparrowMarketState _csi1000 = SparrowMarketState.Unknown;

    public SparrowMarketState Shanghai
    {
        get => ShanghaiSnapshot != null && ShanghaiSnapshot.State != SparrowMarketState.Unknown ? ShanghaiSnapshot.State : _shanghai;
        init => _shanghai = value;
    }

    public SparrowMarketState Csi1000
    {
        get => Csi1000Snapshot != null && Csi1000Snapshot.State != SparrowMarketState.Unknown ? Csi1000Snapshot.State : _csi1000;
        init => _csi1000 = value;
    }

    public SparrowIndexSnapshot ShanghaiSnapshot { get; init; } = new() { Code = "1.000001", Name = "上证指数" };
    public SparrowIndexSnapshot Csi1000Snapshot { get; init; } = new() { Code = "1.000852", Name = "中证1000" };

    public bool Defensive { get; init; }
    public string Reason { get; init; } = "";
}
