using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Models;
using Serilog;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Evaluates market regime for the Sparrow Classic strategy by restoring historical main
/// intraday trend defense semantics.
/// </summary>
public sealed class SparrowMarketRegimeService
{
    public const string DefaultShanghaiCode = "1.000001";
    public const string DefaultCsi1000Code = "1.000852";
    public const int MinTrendSamples = 5;

    private readonly IStockDataProvider _dataProvider;

    public SparrowMarketRegimeService(IStockDataProvider dataProvider)
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
    }

    /// <summary>
    /// Evaluates both Shanghai and CSI 1000 indices and returns the combined market regime.
    /// </summary>
    public Task<SparrowMarketRegime> EvaluateAsync(CancellationToken cancellationToken = default)
        => EvaluateAsync(forceRefresh: false, cancellationToken);

    /// <summary>
    /// Evaluates both Shanghai and CSI 1000 indices with explicit refresh control and returns the combined market regime.
    /// </summary>
    public async Task<SparrowMarketRegime> EvaluateAsync(bool forceRefresh, CancellationToken cancellationToken = default)
    {
        SparrowIndexSnapshot shanghai = await EvaluateIndexAsync(DefaultShanghaiCode, "上证指数", forceRefresh, cancellationToken);
        SparrowIndexSnapshot csi1000 = await EvaluateIndexAsync(DefaultCsi1000Code, "中证1000", forceRefresh, cancellationToken);
        return Combine(shanghai, csi1000);
    }

    /// <summary>
    /// Evaluates an individual index by fetching its intraday trend data.
    /// </summary>
    public Task<SparrowIndexSnapshot> EvaluateIndexAsync(
        string code,
        string name,
        CancellationToken cancellationToken = default)
        => EvaluateIndexAsync(code, name, forceRefresh: false, cancellationToken);

    /// <summary>
    /// Evaluates an individual index by fetching its intraday trend data with explicit refresh control.
    /// </summary>
    public async Task<SparrowIndexSnapshot> EvaluateIndexAsync(
        string code,
        string name,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string url = forceRefresh ? $"/api/trend?code={code}&refresh=1" : $"/api/trend?code={code}";
            var request = StockDataRequest.Parse(url);
            StockDataResult result = await _dataProvider.GetDataAsync(request, cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Json))
            {
                return new SparrowIndexSnapshot
                {
                    Code = code,
                    Name = name,
                    State = SparrowMarketState.Unknown,
                    Message = $"Market data unavailable: {result.Error ?? "empty response"}"
                };
            }

            List<SparrowIndexPoint> points = ParseTrendPointsFromJson(result.Json);
            return EvaluateTrendPoints(points, code, name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to evaluate market index trend for {Code}", code);
            return new SparrowIndexSnapshot
            {
                Code = code,
                Name = name,
                State = SparrowMarketState.Unknown,
                Message = $"Market data unavailable: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Evaluates trend points using historical main logic:
    /// Weak: latestPrice < latestAverage && latestAverage < earlierAverage (5 bars ago)
    /// Strong: latestPrice > latestAverage && latestAverage > earlierAverage (5 bars ago)
    /// </summary>
    public static SparrowIndexSnapshot EvaluateTrendPoints(
        IEnumerable<SparrowIndexPoint> rawPoints,
        string code = "",
        string name = "")
    {
        List<SparrowIndexPoint> points = EnsureOldestFirst(rawPoints);
        if (points.Count < MinTrendSamples)
        {
            return new SparrowIndexSnapshot
            {
                Code = code,
                Name = name,
                State = SparrowMarketState.Unknown,
                Samples = points.Count,
                Message = $"Insufficient samples ({points.Count} < {MinTrendSamples})"
            };
        }

        SparrowIndexPoint latest = points[^1];
        SparrowIndexPoint earlier = points[^MinTrendSamples];

        double latestPrice = latest.Price;
        double latestAverage = latest.AveragePrice;
        double earlierAverage = earlier.AveragePrice;

        if (latestPrice <= 0 || latestAverage <= 0 || earlierAverage <= 0)
        {
            return new SparrowIndexSnapshot
            {
                Code = code,
                Name = name,
                State = SparrowMarketState.Unknown,
                LatestPrice = latestPrice,
                LatestAverage = latestAverage,
                EarlierAverage = earlierAverage,
                Samples = points.Count,
                Message = "Invalid price or average values in trend data"
            };
        }

        bool isWeak = latestPrice < latestAverage && latestAverage < earlierAverage;
        bool isStrong = latestPrice > latestAverage && latestAverage > earlierAverage;

        SparrowMarketState state = isWeak
            ? SparrowMarketState.Weak
            : (isStrong ? SparrowMarketState.Strong : SparrowMarketState.Neutral);

        return new SparrowIndexSnapshot
        {
            Code = code,
            Name = name,
            State = state,
            LatestPrice = latestPrice,
            LatestAverage = latestAverage,
            EarlierAverage = earlierAverage,
            Samples = points.Count,
            Message = $"Evaluated state: {state}"
        };
    }

    /// <summary>
    /// Evaluates trend from raw price series (e.g. daily closes) using moving average windows.
    /// Strictly enforces non-overlapping windows to guard against the limit=5 bug.
    /// </summary>
    public static SparrowIndexSnapshot EvaluateFromPrices(
        IEnumerable<double> rawPrices,
        string code = "",
        string name = "",
        int windowSize = 5,
        bool isSourceOldestFirst = true)
    {
        var prices = rawPrices.ToList();
        if (!isSourceOldestFirst)
        {
            prices.Reverse();
        }

        int requiredSamples = windowSize * 2;
        if (prices.Count < requiredSamples)
        {
            return new SparrowIndexSnapshot
            {
                Code = code,
                Name = name,
                State = SparrowMarketState.Unknown,
                Samples = prices.Count,
                Message = $"Insufficient samples to separate current and previous windows (requires {requiredSamples}, got {prices.Count})"
            };
        }

        double latestPrice = prices[^1];
        double latestAverage = prices.TakeLast(windowSize).Average();
        double earlierAverage = prices.Skip(prices.Count - requiredSamples).Take(windowSize).Average();

        if (latestPrice <= 0 || latestAverage <= 0 || earlierAverage <= 0)
        {
            return new SparrowIndexSnapshot
            {
                Code = code,
                Name = name,
                State = SparrowMarketState.Unknown,
                LatestPrice = latestPrice,
                LatestAverage = latestAverage,
                EarlierAverage = earlierAverage,
                Samples = prices.Count,
                Message = "Invalid prices in series"
            };
        }

        bool isWeak = latestPrice < latestAverage && latestAverage < earlierAverage;
        bool isStrong = latestPrice > latestAverage && latestAverage > earlierAverage;

        SparrowMarketState state = isWeak
            ? SparrowMarketState.Weak
            : (isStrong ? SparrowMarketState.Strong : SparrowMarketState.Neutral);

        return new SparrowIndexSnapshot
        {
            Code = code,
            Name = name,
            State = state,
            LatestPrice = latestPrice,
            LatestAverage = latestAverage,
            EarlierAverage = earlierAverage,
            Samples = prices.Count,
            Message = $"Evaluated price window state: {state}"
        };
    }

    /// <summary>
    /// Combines Shanghai and CSI 1000 index states into the overall market regime.
    /// Follows the historical fail-open policy: only triggers defensive when BOTH indices are Weak.
    /// </summary>
    public static SparrowMarketRegime Combine(SparrowIndexSnapshot shanghai, SparrowIndexSnapshot csi1000)
    {
        ArgumentNullException.ThrowIfNull(shanghai);
        ArgumentNullException.ThrowIfNull(csi1000);

        bool shWeak = shanghai.State == SparrowMarketState.Weak;
        bool csiWeak = csi1000.State == SparrowMarketState.Weak;
        bool defensive = shWeak && csiWeak;

        string reason;
        if (defensive)
        {
            reason = "双指数弱势熔断 (SH Weak & CSI1000 Weak)";
        }
        else if (shanghai.State == SparrowMarketState.Unknown || csi1000.State == SparrowMarketState.Unknown)
        {
            reason = $"市场数据部分不可用 (Market data unavailable: SH={shanghai.State}, CSI1000={csi1000.State})，按历史策略放行 (Fail-open)";
        }
        else
        {
            reason = $"市场环境正常 (SH: {shanghai.State}, CSI1000: {csi1000.State})";
        }

        return new SparrowMarketRegime
        {
            ShanghaiSnapshot = shanghai,
            Csi1000Snapshot = csi1000,
            Defensive = defensive,
            Reason = reason
        };
    }

    /// <summary>
    /// Normalizes points to ascending chronological order (oldest to newest).
    /// </summary>
    public static List<SparrowIndexPoint> EnsureOldestFirst(
        IEnumerable<SparrowIndexPoint> points,
        bool isSourceOldestFirst = true)
    {
        var list = points.ToList();
        if (list.Count <= 1)
        {
            return list;
        }

        SparrowIndexPoint first = list[0];
        SparrowIndexPoint last = list[^1];

        if (first.Time.HasValue && last.Time.HasValue)
        {
            return first.Time.Value > last.Time.Value ? list.AsEnumerable().Reverse().ToList() : list;
        }

        if (!string.IsNullOrEmpty(first.TimeString) && !string.IsNullOrEmpty(last.TimeString))
        {
            int cmp = string.CompareOrdinal(first.TimeString, last.TimeString);
            if (cmp != 0)
            {
                return cmp > 0 ? list.AsEnumerable().Reverse().ToList() : list;
            }
        }

        if (!isSourceOldestFirst)
        {
            list.Reverse();
        }

        return list;
    }

    /// <summary>
    /// Parses trend points from EastMoney JSON or structured JSON.
    /// </summary>
    public static List<SparrowIndexPoint> ParseTrendPointsFromJson(string json)
    {
        var results = new List<SparrowIndexPoint>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            JsonElement data = root;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement dataElem))
            {
                data = dataElem;
            }

            // Case 1: data.trends array of strings (standard EastMoney trends2 format)
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("trends", out JsonElement trendsElem)
                && trendsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in trendsElem.EnumerateArray())
                {
                    string? raw = item.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    string[] parts = raw.Split(',');
                    if (parts.Length < 8) continue;

                    if (double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double price)
                        && double.TryParse(parts[7], NumberStyles.Any, CultureInfo.InvariantCulture, out double avgPrice))
                    {
                        string timeStr = parts[0];
                        DateTime? dt = DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDt)
                            ? parsedDt
                            : null;

                        results.Add(new SparrowIndexPoint
                        {
                            Time = dt,
                            TimeString = timeStr,
                            Price = price,
                            AveragePrice = avgPrice
                        });
                    }
                }
                return results;
            }

            // Case 2: data as an array of objects
            JsonElement arrayElem = data.ValueKind == JsonValueKind.Array ? data : default;
            if (arrayElem.ValueKind == JsonValueKind.Undefined && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("list", out JsonElement listElem) && listElem.ValueKind == JsonValueKind.Array)
                {
                    arrayElem = listElem;
                }
                else if (data.TryGetProperty("List", out JsonElement listUpper) && listUpper.ValueKind == JsonValueKind.Array)
                {
                    arrayElem = listUpper;
                }
            }

            if (arrayElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in arrayElem.EnumerateArray())
                {
                    double price = GetDouble(item, "Price", "Close", "f43", "f53");
                    double avgPrice = GetDouble(item, "AveragePrice", "Average", "Avg", "AvgPrice", "f58");
                    string timeStr = GetString(item, "Time", "Date", "f51");

                    DateTime? dt = DateTime.TryParse(timeStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedDt)
                        ? parsedDt
                        : null;

                    if (price > 0 && avgPrice > 0)
                    {
                        results.Add(new SparrowIndexPoint
                        {
                            Time = dt,
                            TimeString = timeStr,
                            Price = price,
                            AveragePrice = avgPrice
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to parse trend JSON");
        }

        return results;
    }

    private static double GetDouble(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return 0;
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double val))
                {
                    return val;
                }
                if (prop.ValueKind == JsonValueKind.String
                    && double.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }
            }
        }
        return 0;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement prop))
            {
                if (prop.ValueKind == JsonValueKind.String)
                {
                    return prop.GetString() ?? "";
                }
                return prop.GetRawText();
            }
        }
        return "";
    }
}
