using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Shared, low-level market-data fetch primitives used by the V2, Classic, and Compare
/// scanners. Encapsulates the retried K-line fetch (with statistics and cache write) and the
/// batch quote fetch, all through the typed market-data contract.
/// </summary>
internal static class SparrowMarketDataFetcher
{
    /// <summary>
    /// Fetches one code's daily K-line (already retried), records statistics, and writes a
    /// successful result into the shared cache. Callers keep their own cache-check and loop structure.
    /// </summary>
    public static async Task<SparrowKlineFetchOutcome> FetchKlineAsync(
        IKlineService klineService,
        SparrowMarketDataCache cache,
        SparrowKlineFetchStatistics statistics,
        string code,
        int limit,
        bool useCache,
        CancellationToken ct)
    {
        SparrowKlineFetchOutcome outcome = await SparrowKlineFetchHelper.FetchDailyAsync(
            klineService, code, limit, !useCache, ct);
        statistics.Record(outcome);
        if (outcome.Success)
        {
            string cacheKey = SparrowDataCachePolicy.GetKlineKey(code, limit, "day");
            cache.Set(cacheKey, outcome.Json!, SparrowDataCachePolicy.DefaultKlineTtl);
        }
        return outcome;
    }

    /// <summary>
    /// Fetches one batch of quotes via the typed quote service. Returns null when the request fails.
    /// </summary>
    public static async Task<IReadOnlyList<(string Code, SparrowQuoteData Quote)>?> FetchQuoteBatchAsync(
        IQuoteService quoteService,
        IReadOnlyList<string> codes,
        bool forceRefresh,
        CancellationToken ct)
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
}
