using System.Text.Json;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Shared, low-level market-data fetch primitives used by the V2, Classic, and Compare
/// scanners. Encapsulates the retried K-line fetch (with statistics and cache write) and
/// the batch quote fetch-and-parse, so each scanner stops duplicating this innermost plumbing.
/// </summary>
internal static class SparrowMarketDataFetcher
{
    /// <summary>
    /// Fetches one code's daily K-line (already retried), records statistics, and writes a
    /// successful result into the shared cache. Callers keep their own cache-check and loop
    /// structure; pass <paramref name="type"/> = null for the legacy V2 URL shape.
    /// </summary>
    public static async Task<SparrowKlineFetchOutcome> FetchKlineAsync(
        IStockDataProvider dataProvider,
        IKlineService? klineService,
        SparrowMarketDataCache cache,
        SparrowKlineFetchStatistics statistics,
        string code,
        int limit,
        string? type,
        bool useCache,
        CancellationToken ct)
    {
        StockDataRequest request = StockDataRequest.Parse(
            SparrowEndpoints.KlineAll(code, limit, !useCache, type));
        SparrowKlineFetchOutcome outcome = klineService != null
            ? await SparrowKlineFetchHelper.FetchDailyAsync(klineService, code, limit, !useCache, ct)
            : await SparrowKlineFetchHelper.FetchAsync(
                dataProvider, code, request, SparrowKlineFetchHelper.IsUsableKlineJson, ct);
        statistics.Record(outcome);
        if (outcome.Success)
        {
            string cacheKey = SparrowDataCachePolicy.GetKlineKey(code, limit, "day");
            cache.Set(cacheKey, outcome.Json!, SparrowDataCachePolicy.DefaultKlineTtl);
        }
        return outcome;
    }

    /// <summary>
    /// Fetches one batch of quotes and parses them into (code, quote) rows. Returns null when
    /// the request fails through either the typed quote service or the raw data provider.
    /// </summary>
    public static async Task<IReadOnlyList<(string Code, SparrowQuoteData Quote)>?> FetchQuoteBatchAsync(
        IStockDataProvider dataProvider,
        IQuoteService? quoteService,
        IReadOnlyList<string> codes,
        bool forceRefresh,
        CancellationToken ct)
    {
        if (quoteService != null)
        {
            MarketDataResult<IReadOnlyList<QuoteSnapshot>> response = await quoteService.GetQuotesAsync(
                codes.ToArray(), forceRefresh, ct);
            if (!response.Success)
            {
                return null;
            }
            return response.Data.Select(quote =>
                (SparrowSymbolNormalizer.Normalize(quote.Symbol), SparrowQuoteDataContract.FromSnapshot(quote))).ToList();
        }

        string codesStr = string.Join(",", codes);
        StockDataResult result = await dataProvider.GetDataAsync(
            StockDataRequest.Parse(SparrowEndpoints.Quote(codesStr, forceRefresh)), ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Json))
        {
            return null;
        }

        var rows = new List<(string Code, SparrowQuoteData Quote)>();
        foreach (JsonElement item in SparrowJsonElement.EnumerateDataArray(result.Json))
        {
            string code = SparrowSymbolNormalizer.Normalize(SparrowJsonElement.GetString(item, "Code"));
            if (code.Length > 0)
            {
                SparrowQuoteDataContract.TryParse(item, out SparrowQuoteData quote);
                rows.Add((code, quote));
            }
        }
        return rows;
    }
}
