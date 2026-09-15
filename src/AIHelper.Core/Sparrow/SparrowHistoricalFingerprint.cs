using System.Security.Cryptography;
using System.Text;

namespace AIHelper.Core.Sparrow;

public static class SparrowHistoricalFingerprint
{
    public static string Parameters(SparrowReplayRequest request) => Hash(CanonicalRequest(request));
    public static string Parameters(SparrowBacktestRequest request) => Hash(string.Join('|', request.StrategyMode, request.StrategyVersion, request.TopN, request.RoundTripCostRate.ToString("R"), request.SlippageRate.ToString("R"), string.Join(',', request.Horizons.Order()), request.ClassicParameters, request.V2Parameters));
    public static string Dataset(HistoricalMarketDataset dataset)
    {
        string value = string.Join('\n', new[] { dataset.DatasetId, dataset.PriceAdjustmentMode, dataset.Source }
            .Concat(dataset.TradingDates.Select(date => date.ToString("O")))
            .Concat(dataset.Quotes.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Symbol, StringComparer.Ordinal).Select(pair => $"{pair.Key.Date:O}|{pair.Key.Symbol}|{pair.Value.Price:R}|{pair.Value.PreviousClose:R}|{pair.Value.Amount:R}|{pair.Value.Turnover:R}|{pair.Value.OuterVolume:R}|{pair.Value.InnerVolume:R}"))
            .Concat(dataset.Klines.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Bars.Select(bar => $"{pair.Key}|{bar.Date:O}|{bar.Close:R}"))));
        return Hash(value);
    }
    private static string CanonicalRequest(SparrowReplayRequest request) => string.Join('|', request.StrategyMode, request.StrategyVersion, request.TradingDate.ToString("O"), request.TopN, request.ClassicParameters, request.V2Parameters);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
