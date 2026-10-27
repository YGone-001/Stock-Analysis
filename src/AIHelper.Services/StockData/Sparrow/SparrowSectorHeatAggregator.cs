using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Immutable per-date sector-heat outcome: a symbol-to-percentile map or null when industries cannot be aggregated.</summary>
public sealed record SparrowSectorHeatResult(bool Available, IReadOnlyDictionary<string, double> HeatPercentileBySymbol)
{
    public static readonly SparrowSectorHeatResult Unavailable = new(false, new Dictionary<string, double>(StringComparer.Ordinal));
}

/// <summary>
/// Pure, in-dataset sector-heat aggregation for historical replay: group the day's securities by their industry,
/// average each industry's quote percent change, then take the cross-sectional midrank percentile per industry
/// (same 0-100 endpoint convention as the ranking engine and the live board-heat service). A pure function of
/// dataset content — no I/O and no provider dependency, so replay evidence stays deterministic.
/// </summary>
public static class SparrowSectorHeatAggregator
{
    public static SparrowSectorHeatResult Compute(HistoricalMarketSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Dictionary<string, List<double>> risesByIndustry = new(StringComparer.Ordinal);
        foreach (HistoricalSecuritySnapshot security in snapshot.Securities)
        {
            string? industry = security.Security.Industry;
            if (string.IsNullOrWhiteSpace(industry)) continue;
            if (security.Quote.ChangePercent is not { } rise || !double.IsFinite(rise)) continue;
            if (!risesByIndustry.TryGetValue(industry, out List<double>? bucket))
            {
                bucket = new List<double>();
                risesByIndustry[industry] = bucket;
            }
            bucket.Add(rise);
        }

        if (risesByIndustry.Count == 0)
            return SparrowSectorHeatResult.Unavailable;

        Dictionary<string, double> meanByIndustry = risesByIndustry.ToDictionary(pair => pair.Key, pair => pair.Value.Average(), StringComparer.Ordinal);
        double[] sorted = meanByIndustry.Values.Order().ToArray();
        double denominator = sorted.Length > 1 ? sorted.Length - 1 : 1;
        Dictionary<string, double> percentileByIndustry = meanByIndustry.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                int less = 0;
                int equal = 0;
                foreach (double item in sorted)
                {
                    if (item < pair.Value) less++;
                    else if (item == pair.Value) equal++;
                }
                return sorted.Length == 1 ? 100.0 : (less + (equal - 1) / 2.0) / denominator * 100.0;
            },
            StringComparer.Ordinal);

        var heat = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (HistoricalSecuritySnapshot security in snapshot.Securities)
        {
            string? industry = security.Security.Industry;
            if (!string.IsNullOrWhiteSpace(industry) && percentileByIndustry.TryGetValue(industry, out double percentile))
                heat[security.Security.Symbol] = percentile;
        }
        return heat.Count == 0 ? SparrowSectorHeatResult.Unavailable : new SparrowSectorHeatResult(true, heat);
    }
}
