using System.Security.Cryptography;
using System.Text;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

public static class SparrowHistoricalFingerprint
{
    public static string Parameters(SparrowReplayRequest request) => Hash(CanonicalRequest(request));
    public static string Parameters(SparrowBacktestRequest request) => Hash(string.Join('|', request.StrategyMode, request.StrategyVersion, request.TopN, request.RoundTripCostRate.ToString("R"), request.SlippageRate.ToString("R"), string.Join(',', request.Horizons.Order()), request.ClassicParameters, request.V2Parameters));
    /// <summary>Uses the historical schema's own canonical algorithm. V1 stays byte-for-byte compatible.</summary>
    public static string Dataset(HistoricalMarketDataset dataset) => dataset.SchemaVersion <= 1 ? DatasetV1(dataset) : DatasetV2(dataset);

    public static string DatasetV1(HistoricalMarketDataset dataset)
    {
        string value = string.Join('\n', new[] { dataset.DatasetId, dataset.LegacyPriceAdjustmentMode, dataset.Source }
            .Concat(dataset.TradingDates.Select(date => date.ToString("O")))
            .Concat(dataset.Quotes.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Symbol, StringComparer.Ordinal).Select(pair => $"{pair.Key.Date:O}|{pair.Key.Symbol}|{pair.Value.Price:R}|{pair.Value.PreviousClose:R}|{pair.Value.Amount:R}|{pair.Value.Turnover:R}|{pair.Value.OuterVolume:R}|{pair.Value.InnerVolume:R}"))
            .Concat(dataset.Klines.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Bars.Select(bar => $"{pair.Key}|{bar.Date:O}|{bar.Close:R}"))));
        return Hash(value);
    }

    /// <summary>V2 fingerprints every field capable of changing universe, evaluation, ranking, outcome, or data-quality meaning.</summary>
    public static string DatasetV2(HistoricalMarketDataset dataset)
    {
        var rows = new List<string>
        {
            Row("SchemaVersion", dataset.SchemaVersion),
            Row("Dataset", dataset.DatasetId, dataset.Source, dataset.Metadata.UniverseQuality)
        };
        if (dataset.HasExplicitDatasetScope)
            rows.Add(Row("Scope", dataset.DatasetScope.Kind, dataset.DatasetScope.MarketUniverse, string.Join(',', dataset.DatasetScope.Symbols)));
        rows.AddRange(dataset.TradingDates.Order().Select(date => Row("TradingDate", date)));
        rows.AddRange(dataset.Securities.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            HistoricalSecurity s = pair.Value;
            return Row("Security", s.Symbol, s.Name, s.SecurityType, s.Market, s.ListingDate, s.DelistingEffectiveDate, s.LifecycleQuality, s.ObservedHistoryStart);
        }));
        rows.AddRange(dataset.Universes.OrderBy(pair => pair.Key).Select(pair =>
        {
            HistoricalUniverseSnapshot u = pair.Value;
            return Row("Universe", u.TradingDate, u.Quality, u.Source, string.Join(',', u.SecuritySymbols.OrderBy(value => value, StringComparer.Ordinal)), string.Join('\u001e', u.Warnings.OrderBy(value => value, StringComparer.Ordinal)));
        }));
        rows.AddRange(dataset.FieldCapabilities.OrderBy(pair => pair.Key).Select(pair =>
        {
            HistoricalFieldCapability c = pair.Value;
            return Row("FieldCapability", c.Field, c.Origin, c.Coverage, c.Unit);
        }));
        rows.AddRange(dataset.Quotes.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Symbol, StringComparer.Ordinal).Select(pair =>
        {
            QuoteSnapshot q = pair.Value;
            return Row("Quote", pair.Key.Date, pair.Key.Symbol, q.Name, q.Price, q.PreviousClose, q.ChangePercent, q.Volume, q.Amount, q.Turnover, q.OuterVolume, q.InnerVolume, q.Open, q.High, q.Low);
        }));
        rows.AddRange(dataset.Klines.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Bars.OrderBy(bar => bar.Date).Select(bar =>
            Row("Kline", pair.Key, DateOnly.FromDateTime(bar.Date), bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.Amount, bar.ChangePercent, bar.Change, bar.TurnoverRate))));
        rows.AddRange(dataset.PriceSeriesProvenance.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            HistoricalPriceSeriesProvenance p = pair.Value;
            return Row("PriceProvenance", p.Symbol, p.Source, p.AdjustmentMode, p.ObservationStart, p.ObservationEnd);
        }));
        rows.AddRange(dataset.MarketContexts.OrderBy(pair => pair.Key).Select(pair =>
        {
            HistoricalMarketContext c = pair.Value;
            SparrowMarketRegime? r = c.ClassicMarketRegime;
            return Row("MarketContext", c.TradingDate, r?.Shanghai, r?.Csi1000, r?.Defensive, r?.Reason, c.V2ShanghaiDailyPercent);
        }));
        rows.AddRange(dataset.MarketContextProvenance.OrderBy(pair => pair.Key).Select(pair =>
        {
            HistoricalMarketContextProvenance p = pair.Value;
            return Row("MarketContextProvenance", p.TradingDate, p.ClassicMarketRegimeOrigin, p.ClassicMarketRegimeSource, p.V2ShanghaiDailyPercentOrigin, p.V2ShanghaiDailyPercentSource);
        }));
        rows.AddRange(dataset.ObservationDeclarations.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Symbol, StringComparer.Ordinal).Select(pair =>
        {
            HistoricalObservationDeclaration d = pair.Value;
            return Row("Observation", d.TradingDate, d.Symbol, d.SecurityStatus, d.ObservationStatus, d.Reason);
        }));
        rows.AddRange(dataset.RiskStatusObservations.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Symbol, StringComparer.Ordinal).Select(pair =>
        {
            HistoricalRiskStatusObservation s = pair.Value;
            return Row("HistoricalSt", s.TradingDate, s.Symbol, s.IsSt, s.Source);
        }));
        rows.AddRange(dataset.AdjustmentFactors.OrderBy(pair => pair.Key.Date).ThenBy(pair => pair.Key.Symbol, StringComparer.Ordinal).Select(pair =>
        {
            HistoricalAdjustmentFactor factor = pair.Value;
            return Row("AdjustmentFactor", factor.TradingDate, factor.Symbol, factor.Factor, factor.Source);
        }));
        rows.AddRange(dataset.CoverageEvidence.OrderBy(item => item.Endpoint, StringComparer.Ordinal).ThenBy(item => item.Scope.Symbol, StringComparer.Ordinal)
            .ThenBy(item => item.Scope.ListStatus, StringComparer.Ordinal).ThenBy(item => item.Scope.StartDate).ThenBy(item => item.Scope.EndDate).Select(item =>
                Row("Coverage", item.Endpoint, item.Source, item.Scope.Exchange, item.Scope.Symbol, item.Scope.SymbolPartition, item.Scope.IndexCode, item.Scope.ListStatus,
                    item.Scope.StartDate, item.Scope.EndDate, item.MaxRowsPerRequest, item.PaginationSupported, item.PermissionRequirement, item.QueryShape,
                    string.Join('\u001e', item.RequestedChunks.OrderBy(chunk => chunk.StartDate).ThenBy(chunk => chunk.EndDate).Select(chunk => Row("Chunk", chunk.StartDate, chunk.EndDate, chunk.ReturnedRows, chunk.ResponseCapHit))),
                    item.ReturnedRows, item.ResponseCapHit, item.DuplicateRows, item.InvalidRows, item.RateLimitEvents, item.PermissionDeniedEvents,
                    item.ExpectedCount, item.ObservedCount, item.MissingCount, item.CoverageStatus, item.ProofMethod, item.FailureReason)));
        rows.AddRange(dataset.StrategyCapabilities.Values.OrderBy(item => item.Strategy).Select(item =>
            Row("StrategyCapability", item.Strategy, item.Status, string.Join(',', item.ReasonCodes))));
        HistoricalDatasetQualitySummary quality = dataset.QualitySummary;
        if (HasExplicitCoverageEvidence(quality))
            rows.Add(Row("Quality", quality.UniverseQuality, quality.LifecycleQuality, quality.HistoricalStCoverage, quality.SuspensionCoverage,
                quality.TurnoverCoverage, quality.AdjustmentFactorCoverage, quality.IndexCoverage, quality.OuterInnerCoverage,
                string.Join('\u001e', quality.Reasons.OrderBy(value => value, StringComparer.Ordinal))));
        return Hash(string.Join('\n', rows));
    }

    private static string Row(params object?[] values) => string.Join('\u001f', values.Select(Canonical));
    private static bool HasExplicitCoverageEvidence(HistoricalDatasetQualitySummary quality) =>
        quality.HistoricalStCoverage != HistoricalFieldCoverage.Unknown || quality.SuspensionCoverage != HistoricalFieldCoverage.Unknown ||
        quality.AdjustmentFactorCoverage != HistoricalFieldCoverage.Unknown || quality.Reasons.Count > 0;
    private static string Canonical(object? value) => value switch
    {
        null => "<null>",
        DateOnly date => date.ToString("O"),
        DateTime dateTime => dateTime.ToUniversalTime().ToString("O"),
        double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        float number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty))
    };
    private static string CanonicalRequest(SparrowReplayRequest request) => string.Join('|', request.StrategyMode, request.StrategyVersion, request.TradingDate.ToString("O"), request.TopN, request.ClassicParameters, request.V2Parameters);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
