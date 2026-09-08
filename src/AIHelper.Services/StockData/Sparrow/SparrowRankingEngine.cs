using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Pure, request-free, order-independent Phase 5A ranking.</summary>
public sealed class SparrowRankingEngine
{
    private sealed record FactorDefinition(
        string Name,
        double Weight,
        Func<SparrowRankingFeatures, double?> Raw,
        Func<SparrowRankingFeatures, double?> RankingValue,
        bool Bypassed = false);

    private sealed record ScoredCandidate(
        SparrowRankingFeatures Features,
        IReadOnlyList<SparrowFactorScore> Factors,
        double TotalScore,
        bool IsDataComplete,
        string? MissingFactors);

    public IReadOnlyList<SparrowRankedCandidate> RankClassic(
        IEnumerable<SparrowRankingFeatures> source,
        double minRise,
        double maxRise)
    {
        double target = (minRise + maxRise) / 2.0;
        FactorDefinition[] factors =
        {
            new(SparrowRankingFactorNames.BuyPressure, SparrowRankingWeights.Classic.BuyPressure,
                feature => feature.BuyPressureRatio, feature => feature.BuyPressureRatio),
            new(SparrowRankingFactorNames.Liquidity, SparrowRankingWeights.Classic.Liquidity,
                feature => feature.Amount, feature => feature.Amount),
            new(SparrowRankingFactorNames.RiseQuality, SparrowRankingWeights.Classic.RiseQuality,
                feature => feature.RisePercent,
                feature => IsFinite(feature.RisePercent) ? -Math.Abs(feature.RisePercent!.Value - target) : null)
        };
        return Rank(source, factors, v2: false);
    }

    public IReadOnlyList<SparrowRankedCandidate> RankV2(
        IEnumerable<SparrowRankingFeatures> source,
        double minRise,
        double maxRise,
        bool checkAlpha)
    {
        double target = (minRise + maxRise) / 2.0;
        FactorDefinition[] factors =
        {
            new(SparrowRankingFactorNames.Momentum, SparrowRankingWeights.V2.Momentum,
                feature => feature.Momentum, feature => feature.Momentum),
            new(SparrowRankingFactorNames.BuyPressure, SparrowRankingWeights.V2.BuyPressure,
                feature => feature.BuyPressureRatio, feature => feature.BuyPressureRatio),
            new(SparrowRankingFactorNames.AlphaStrength, SparrowRankingWeights.V2.AlphaStrength,
                feature => feature.AlphaMargin, feature => feature.AlphaMargin, Bypassed: !checkAlpha),
            new(SparrowRankingFactorNames.Liquidity, SparrowRankingWeights.V2.Liquidity,
                feature => feature.Amount, feature => feature.Amount),
            new(SparrowRankingFactorNames.RiseQuality, SparrowRankingWeights.V2.RiseQuality,
                feature => feature.RisePercent,
                feature => IsFinite(feature.RisePercent) ? -Math.Abs(feature.RisePercent!.Value - target) : null)
        };
        return Rank(source, factors, v2: true);
    }

    public static IReadOnlyList<double> PercentileRank(IReadOnlyList<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var scores = Enumerable.Repeat(SparrowRankingProfileV1.MissingFactorScore, values.Count).ToArray();
        var present = values
            .Select((value, index) => (Value: value, Index: index))
            .Where(item => IsFinite(item.Value))
            .Select(item => (Value: item.Value!.Value, item.Index))
            .OrderBy(item => item.Value)
            .ThenBy(item => item.Index)
            .ToArray();

        if (present.Length == 0)
        {
            return scores;
        }
        if (present.Length == 1)
        {
            scores[present[0].Index] = 100.0;
            return scores;
        }
        if (present[0].Value.Equals(present[^1].Value))
        {
            foreach ((double _, int index) in present)
            {
                scores[index] = 50.0;
            }
            return scores;
        }

        int start = 0;
        while (start < present.Length)
        {
            int end = start;
            while (end + 1 < present.Length && present[end + 1].Value.Equals(present[start].Value))
            {
                end++;
            }
            double midrank = (start + end) / 2.0;
            double percentile = midrank / (present.Length - 1) * 100.0;
            for (int index = start; index <= end; index++)
            {
                scores[present[index].Index] = percentile;
            }
            start = end + 1;
        }
        return scores;
    }

    public static IReadOnlyList<SparrowComparisonRow> SelectCompareTopN(
        IEnumerable<SparrowComparisonRow> source,
        SparrowComparisonCategory category,
        int topN)
    {
        ArgumentNullException.ThrowIfNull(source);
        int count = Math.Clamp(topN, SparrowRankingSettings.MinimumTopN, SparrowRankingSettings.MaximumTopN);
        IEnumerable<SparrowComparisonRow> rows = source.Where(row => row.Category == category);
        IOrderedEnumerable<SparrowComparisonRow> ordered = category switch
        {
            SparrowComparisonCategory.Both => rows
                .OrderByDescending(row => row.V2.Score)
                .ThenByDescending(row => row.Classic.Score)
                .ThenBy(row => row.Code, StringComparer.Ordinal),
            SparrowComparisonCategory.ClassicOnly => rows
                .OrderByDescending(row => row.Classic.Score)
                .ThenBy(row => row.Code, StringComparer.Ordinal),
            SparrowComparisonCategory.V2Only => rows
                .OrderByDescending(row => row.V2.Score)
                .ThenBy(row => row.Code, StringComparer.Ordinal),
            _ => rows.OrderBy(row => row.Code, StringComparer.Ordinal)
        };
        return category == SparrowComparisonCategory.Neither
            ? Array.Empty<SparrowComparisonRow>()
            : ordered.Take(count).ToArray();
    }

    private static IReadOnlyList<SparrowRankedCandidate> Rank(
        IEnumerable<SparrowRankingFeatures> source,
        IReadOnlyList<FactorDefinition> definitions,
        bool v2)
    {
        ArgumentNullException.ThrowIfNull(source);
        SparrowRankingFeatures[] candidates = source
            .Select(Sanitize)
            .OrderBy(feature => feature.Code, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return Array.Empty<SparrowRankedCandidate>();
        }

        double activeWeight = definitions.Where(factor => !factor.Bypassed).Sum(factor => factor.Weight);
        var percentiles = definitions.ToDictionary(
            factor => factor.Name,
            factor => PercentileRank(candidates.Select(factor.RankingValue).ToArray()),
            StringComparer.Ordinal);

        var scored = new List<ScoredCandidate>(candidates.Length);
        for (int candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
        {
            SparrowRankingFeatures candidate = candidates[candidateIndex];
            var factorScores = new List<SparrowFactorScore>(definitions.Count);
            var missing = new List<string>();
            foreach (FactorDefinition definition in definitions)
            {
                double? rawValue = definition.Raw(candidate);
                double percentile = definition.Bypassed
                    ? SparrowRankingProfileV1.MissingFactorScore
                    : percentiles[definition.Name][candidateIndex];
                double weight = definition.Bypassed ? 0.0 : definition.Weight / activeWeight;
                if (!definition.Bypassed && !IsFinite(definition.RankingValue(candidate)))
                {
                    missing.Add(definition.Name);
                }
                factorScores.Add(new SparrowFactorScore
                {
                    Factor = definition.Name,
                    RawValue = IsFinite(rawValue) ? rawValue : null,
                    Percentile = percentile,
                    Weight = weight,
                    Contribution = percentile * weight,
                    Bypassed = definition.Bypassed
                });
            }
            double total = factorScores.Sum(factor => factor.Contribution);
            scored.Add(new ScoredCandidate(
                candidate,
                factorScores,
                total,
                missing.Count == 0,
                missing.Count == 0 ? null : string.Join(';', missing)));
        }

        IOrderedEnumerable<ScoredCandidate> ordered = scored
            .OrderByDescending(candidate => candidate.TotalScore);
        ordered = v2
            ? ordered.ThenByDescending(candidate => Percentile(candidate, SparrowRankingFactorNames.Momentum))
                .ThenByDescending(candidate => Percentile(candidate, SparrowRankingFactorNames.BuyPressure))
            : ordered.ThenByDescending(candidate => Percentile(candidate, SparrowRankingFactorNames.BuyPressure))
                .ThenByDescending(candidate => Percentile(candidate, SparrowRankingFactorNames.Liquidity));

        return ordered
            .ThenBy(candidate => candidate.Features.Code, StringComparer.Ordinal)
            .Select((candidate, index) => new SparrowRankedCandidate
            {
                RankingProfile = SparrowRankingProfileV1.Name,
                Code = candidate.Features.Code,
                Name = candidate.Features.Name,
                Rank = index + 1,
                TotalScore = candidate.TotalScore,
                Features = candidate.Features,
                Factors = candidate.Factors,
                IsDataComplete = candidate.IsDataComplete,
                MissingFactors = candidate.MissingFactors
            })
            .ToArray();
    }

    private static double Percentile(ScoredCandidate candidate, string factorName) =>
        candidate.Factors.Single(factor => factor.Factor == factorName).Percentile;

    private static bool IsFinite(double? value) => value.HasValue && double.IsFinite(value.Value);

    private static SparrowRankingFeatures Sanitize(SparrowRankingFeatures feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        double? outer = FiniteOrNull(feature.OuterVolume);
        double? inner = FiniteOrNull(feature.InnerVolume);
        double? ratio = FiniteOrNull(feature.BuyPressureRatio)
            ?? SparrowRankingFeatures.CalculateBuyPressureRatio(outer, inner);
        return feature with
        {
            RisePercent = FiniteOrNull(feature.RisePercent),
            Amount = FiniteOrNull(feature.Amount),
            OuterVolume = outer,
            InnerVolume = inner,
            BuyPressureRatio = ratio,
            Adhesion = FiniteOrNull(feature.Adhesion),
            Turnover = FiniteOrNull(feature.Turnover),
            Momentum = FiniteOrNull(feature.Momentum),
            AlphaMargin = FiniteOrNull(feature.AlphaMargin)
        };
    }

    private static double? FiniteOrNull(double? value) => IsFinite(value) ? value : null;
}
