using System.ComponentModel;

namespace AIHelper.Models;

public static class SparrowRankingProfileV1
{
    public const string Name = "Sparrow-Rank-V1";
    public const double MissingFactorScore = 50.0;
    public const double ZeroInnerVolumeRatioSentinel = 1_000_000.0;
}

public static class SparrowRankingWeights
{
    public static class Classic
    {
        public const double BuyPressure = 0.45;
        public const double Liquidity = 0.30;
        public const double RiseQuality = 0.25;
    }

    public static class V2
    {
        public const double Momentum = 0.30;
        public const double BuyPressure = 0.25;
        public const double AlphaStrength = 0.20;
        public const double Liquidity = 0.15;
        public const double RiseQuality = 0.10;
    }
}

public static class SparrowRankingFactorNames
{
    public const string BuyPressure = "BuyPressure";
    public const string Liquidity = "Liquidity";
    public const string RiseQuality = "RiseQuality";
    public const string Momentum = "Momentum";
    public const string AlphaStrength = "AlphaStrength";
}

public sealed record SparrowRankingFeatures
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public double? RisePercent { get; init; }
    public double? Amount { get; init; }
    public double? OuterVolume { get; init; }
    public double? InnerVolume { get; init; }
    public double? BuyPressureRatio { get; init; }
    public double? Adhesion { get; init; }
    public double? Turnover { get; init; }
    public double? Momentum { get; init; }
    public double? AlphaMargin { get; init; }

    public static double? CalculateBuyPressureRatio(double? outerVolume, double? innerVolume)
    {
        if (!outerVolume.HasValue || !innerVolume.HasValue
            || !double.IsFinite(outerVolume.Value) || !double.IsFinite(innerVolume.Value)
            || outerVolume.Value < 0 || innerVolume.Value < 0)
        {
            return null;
        }

        if (innerVolume.Value > 0)
        {
            double ratio = outerVolume.Value / innerVolume.Value;
            return double.IsFinite(ratio)
                ? Math.Min(ratio, SparrowRankingProfileV1.ZeroInnerVolumeRatioSentinel)
                : SparrowRankingProfileV1.ZeroInnerVolumeRatioSentinel;
        }

        return outerVolume.Value > 0
            ? SparrowRankingProfileV1.ZeroInnerVolumeRatioSentinel
            : null;
    }
}

public sealed record SparrowFactorScore
{
    public required string Factor { get; init; }
    public double? RawValue { get; init; }
    public double Percentile { get; init; }
    public double Weight { get; init; }
    public double Contribution { get; init; }
    public bool Bypassed { get; init; }
}

public sealed record SparrowRankedCandidate
{
    public required string RankingProfile { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public int Rank { get; init; }
    public double TotalScore { get; init; }
    public required SparrowRankingFeatures Features { get; init; }
    public IReadOnlyList<SparrowFactorScore> Factors { get; init; } = Array.Empty<SparrowFactorScore>();
    public bool IsDataComplete { get; init; }
    public string? MissingFactors { get; init; }

    public SparrowFactorScore Factor(string name) =>
        Factors.Single(factor => string.Equals(factor.Factor, name, StringComparison.Ordinal));
}

public sealed class SparrowRankingSettings : INotifyPropertyChanged
{
    public const int MinimumTopN = 1;
    public const int MaximumTopN = 20;
    public const int DefaultTopN = 10;

    private int _topN = DefaultTopN;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int TopN
    {
        get => _topN;
        set
        {
            int normalized = Math.Clamp(value, MinimumTopN, MaximumTopN);
            if (_topN == normalized)
            {
                return;
            }
            _topN = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TopN)));
        }
    }
}

public sealed class SparrowV2Candidate
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Reason { get; init; }
    public required SparrowRankingFeatures RankingFeatures { get; init; }

    public void Deconstruct(out string code, out string name, out string reason) =>
        (code, name, reason) = (Code, Name, Reason);
}
