using System.Globalization;
using System.Text.Json;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

public sealed record SparrowKlineSnapshot(
    IReadOnlyList<double> ClosesNewestFirst,
    double LatestPrice,
    double LatestPercent);

public sealed record SparrowTechnicalEvaluation(
    SparrowRuleComparison Rule,
    double MA5 = 0,
    double MA10 = 0,
    double MA20 = 0,
    double MA60 = 0,
    double Adhesion = 0,
    double Momentum = 0,
    double LatestPercent = 0);

/// <summary>Pure V2 P3 evaluator. Formulas intentionally mirror SparrowScannerService.</summary>
public static class SparrowV2RuleEvaluator
{
    public static SparrowTechnicalEvaluation Evaluate(
        SparrowKlineSnapshot? snapshot,
        double shIndexPctChg,
        SparrowScanParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (snapshot == null)
        {
            return Missing(SparrowComparisonReasonCodes.P3KlineMissing, "Kline data is unavailable");
        }

        IReadOnlyList<double> list = snapshot.ClosesNewestFirst;
        if (list.Count < 60)
        {
            return Missing(
                SparrowComparisonReasonCodes.P3KlineInsufficient,
                $"Kline has {list.Count} valid closes; 60 required");
        }

        double ma5 = Average(list, 0, 5);
        double ma10 = Average(list, 0, 10);
        double ma20 = Average(list, 0, 20);
        double ma60 = Average(list, 0, 60);

        if (parameters.CheckAlpha && snapshot.LatestPercent < shIndexPctChg)
        {
            return Reject(SparrowComparisonReasonCodes.P3Alpha,
                $"Stock latest percent {snapshot.LatestPercent:F4}% is below Shanghai {shIndexPctChg:F4}%",
                ma5, ma10, ma20, ma60, latestPercent: snapshot.LatestPercent);
        }

        if (ma5 < ma10 || ma10 < ma20)
        {
            return Reject(SparrowComparisonReasonCodes.P3MaOrder,
                "MA5 >= MA10 >= MA20 is not satisfied",
                ma5, ma10, ma20, ma60, latestPercent: snapshot.LatestPercent);
        }

        if (parameters.CheckMA60 && (snapshot.LatestPrice <= ma60 || ma20 < ma60))
        {
            return Reject(SparrowComparisonReasonCodes.P3Ma60,
                "Price > MA60 and MA20 >= MA60 are not both satisfied",
                ma5, ma10, ma20, ma60, latestPercent: snapshot.LatestPercent);
        }

        double prevMa5 = Average(list, 3, 5);
        double momentum = (ma5 - prevMa5) / prevMa5;
        if (!(momentum > parameters.MomentumThreshold))
        {
            return Reject(SparrowComparisonReasonCodes.P3Momentum,
                $"Momentum {momentum:F6} is not strictly greater than {parameters.MomentumThreshold:F6}",
                ma5, ma10, ma20, ma60, momentum: momentum, latestPercent: snapshot.LatestPercent);
        }

        double maxMa = Math.Max(ma5, Math.Max(ma10, ma20));
        double minMa = Math.Min(ma5, Math.Min(ma10, ma20));
        double adhesion = (maxMa - minMa) / minMa;
        if (adhesion < parameters.MinAdhesion)
        {
            return Reject(SparrowComparisonReasonCodes.P3AdhesionLow,
                $"Adhesion {adhesion:F6} is below {parameters.MinAdhesion:F6}",
                ma5, ma10, ma20, ma60, adhesion, momentum, snapshot.LatestPercent);
        }

        if (adhesion > parameters.MaxAdhesion)
        {
            return Reject(SparrowComparisonReasonCodes.P3AdhesionHigh,
                $"Adhesion {adhesion:F6} is above {parameters.MaxAdhesion:F6}",
                ma5, ma10, ma20, ma60, adhesion, momentum, snapshot.LatestPercent);
        }

        return new SparrowTechnicalEvaluation(
            SparrowRuleComparison.Pass("P3"), ma5, ma10, ma20, ma60,
            adhesion, momentum, snapshot.LatestPercent);
    }

    public static SparrowKlineSnapshot? ParseKline(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            JsonElement data = root.ValueKind == JsonValueKind.Array
                ? root
                : TryGetPropertyIgnoreCase(root, "data", out JsonElement wrapped) ? wrapped : default;
            if (data.ValueKind == JsonValueKind.Object)
            {
                data = TryGetPropertyIgnoreCase(data, "list", out JsonElement list) ? list
                    : TryGetPropertyIgnoreCase(data, "klines", out JsonElement klines) ? klines
                    : default;
            }
            if (data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement[] elements = data.EnumerateArray().ToArray();
            var closesNewestFirst = new List<double>();
            double latestPrice = 0;
            double latestPercent = 0;
            for (int index = elements.Length - 1; index >= 0; index--)
            {
                if (TryGetDouble(elements[index], "Close", out double rawClose))
                {
                    double close = rawClose / 1000.0;
                    if (close > 0)
                    {
                        closesNewestFirst.Add(close);
                    }
                    if (index == elements.Length - 1)
                    {
                        latestPrice = close;
                        if (index > 0 && TryGetDouble(elements[index - 1], "Close", out double previousRaw))
                        {
                            double previous = previousRaw / 1000.0;
                            latestPercent = previous > 0 ? (close - previous) / previous * 100.0 : 0;
                        }
                    }
                }
            }

            if (closesNewestFirst.Count == 0)
            {
                return new SparrowKlineSnapshot(Array.Empty<double>(), 0, 0);
            }
            return new SparrowKlineSnapshot(closesNewestFirst, latestPrice, latestPercent);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SparrowTechnicalEvaluation Missing(string code, string reason) =>
        new(SparrowRuleComparison.Unavailable("P3", code, reason));

    private static SparrowTechnicalEvaluation Reject(
        string code,
        string reason,
        double ma5,
        double ma10,
        double ma20,
        double ma60,
        double adhesion = 0,
        double momentum = 0,
        double latestPercent = 0) =>
        new(SparrowRuleComparison.Reject("P3", code, reason), ma5, ma10, ma20, ma60,
            adhesion, momentum, latestPercent);

    private static double Average(IReadOnlyList<double> values, int offset, int count)
    {
        double sum = 0;
        for (int index = offset; index < offset + count; index++)
        {
            sum += values[index];
        }
        return sum / count;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
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

    private static bool TryGetDouble(JsonElement element, string name, out double value)
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
}
