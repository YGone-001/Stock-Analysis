using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Helpers;
using AIHelper.Models;
using Serilog;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Historical control-group implementation restored from main's SparrowWindow.
/// Do not add V2 scoring or additional technical indicators to this scanner.
/// </summary>
public sealed class SparrowClassicScanner
{
    private readonly IStockDataProvider _dataProvider;
    private readonly SparrowMarketRegimeService _marketRegimeService;
    private readonly SparrowMarketDataCache _klineCache;

    public SparrowMarketDataCache KlineCache => _klineCache;

    public SparrowClassicScanner(
        IStockDataProvider dataProvider,
        SparrowMarketRegimeService? marketRegimeService = null,
        SparrowMarketDataCache? klineCache = null)
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _marketRegimeService = marketRegimeService ?? new SparrowMarketRegimeService(dataProvider);
        _klineCache = klineCache ?? new SparrowMarketDataCache();
    }

    public async Task<List<SparrowClassicCandidate>> ScanAsync(
        IEnumerable<(string Code, string Name)> targetPool,
        SparrowClassicScanParameters parameters,
        IProgress<SparrowClassicScanReport>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetPool);
        ArgumentNullException.ThrowIfNull(parameters);

        var classicPool = targetPool
            .Where(stock => IsEligibleStock(stock.Code, stock.Name))
            .GroupBy(stock => stock.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        // Scan-local session state: never leaks across scans or between different parameter sets
        var p2Processed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var p2Survivors = new ConcurrentDictionary<string, (string Code, string Name)>(StringComparer.Ordinal);
        var rankingQuotes = new ConcurrentDictionary<string, SparrowQuoteData>(StringComparer.Ordinal);
        var p3Winners = new ConcurrentDictionary<string, SparrowClassicCandidate>(StringComparer.Ordinal);

        string scanTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        ReportLog(progress,
            $"🦅 [Sparrow Classic]\n" +
            $"扫描时间: {scanTime}\n\n" +
            $"【实际参数】\n" +
            $"MacroDef       = {(parameters.MacroDef ? "ON" : "OFF")}\n" +
            $"Rise           = {parameters.MinRise:F2}% ~ {parameters.MaxRise:F2}%\n" +
            $"MinAmount      = {(parameters.MinAmount / 10000.0):F0} 万\n" +
            $"Outer/Inner    = {parameters.VolRatio:F2}\n" +
            $"MA60           = {(parameters.CheckMA60 ? "ON" : "OFF")}\n" +
            $"Adhesion       = {parameters.MinAdhesion * 100:F2}% ~ {parameters.MaxAdhesion * 100:F2}%\n\n" +
            $"UseCache       = {(parameters.UseCache ? "ON" : "OFF")}\n" +
            $"Concurrency    = {parameters.MaxConcurrency}\n\n" +
            $"初始标的: {classicPool.Count} 只");

        string p1Status = "OFF";

        // MacroDef is ALWAYS re-evaluated for every new ScanAsync call when MacroDef == true.
        // It is NEVER skipped due to cached data or non-empty p2Processed.
        if (parameters.MacroDef)
        {
            ReportLog(progress, "🧭 [Sparrow Classic][Market] 开始市场环境检查...");
            SparrowMarketRegime regime = await _marketRegimeService.EvaluateAsync(forceRefresh: !parameters.UseCache, cancellationToken);

            ReportLog(progress,
                $"🧭 [Market]\n" +
                $"SH: {regime.Shanghai} (Latest: {regime.ShanghaiSnapshot.LatestPrice:F2}, Average: {regime.ShanghaiSnapshot.LatestAverage:F2}, EarlierAverage: {regime.ShanghaiSnapshot.EarlierAverage:F2}, Samples: {regime.ShanghaiSnapshot.Samples})\n" +
                $"CSI1000: {regime.Csi1000} (Latest: {regime.Csi1000Snapshot.LatestPrice:F2}, Average: {regime.Csi1000Snapshot.LatestAverage:F2}, EarlierAverage: {regime.Csi1000Snapshot.EarlierAverage:F2}, Samples: {regime.Csi1000Snapshot.Samples})\n" +
                $"Final Regime: Defensive = {regime.Defensive}");

            if (regime.Defensive)
            {
                p1Status = "BLOCKED";
                ReportLog(progress, "🛡️ [Sparrow Classic] 双指数弱势，进入防守模式，本次不执行选股。", true);
                OutputFunnelReport(progress, classicPool.Count, p1Status, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                return new List<SparrowClassicCandidate>();
            }

            if (regime.Shanghai == SparrowMarketState.Unknown || regime.Csi1000 == SparrowMarketState.Unknown)
            {
                ReportLog(progress, "⚠️ [Sparrow Classic][Market] 市场数据部分不可用 (Market data unavailable)，按历史策略放行 (Fail-open)。");
            }

            p1Status = "PASS";
            ReportLog(progress, "✅ [Sparrow Classic][第一阶段通过] 允许开启个股海选。");
        }

        int batchQuoteLoaded = 0;
        int quoteDataUnavailable = 0;
        int coarseSurvivors = 0;
        int outerInnerValid = 0;
        int outerInnerUnavailable = 0;
        int volRatioPassed = 0;

        var p2Pending = classicPool.Where(stock => !p2Processed.ContainsKey(stock.Code)).ToList();
        if (p2Pending.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [Sparrow Classic][阶段2] 快照扫描 (待处理:{p2Pending.Count} / 总计:{classicPool.Count})...");
            progress?.Report(new SparrowClassicScanReport { ProgressMax = classicPool.Count, ProgressValue = p2Processed.Count });

            var batches = p2Pending.Select((stock, index) => new { stock, index })
                .GroupBy(item => item.index / 50)
                .Select(group => group.Select(item => item.stock).ToList())
                .ToList();

            string refreshQuery = parameters.UseCache ? "" : "&refresh=1";
            int p2Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(8, p2Concurrency)));
            await Task.WhenAll(batches.Select(async batch =>
            {
                bool entered = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken);
                    entered = true;
                    string codes = string.Join(",", batch.Select(stock => stock.Code));
                    StockDataResult response = await _dataProvider.GetDataAsync(
                        StockDataRequest.Parse("/api/quote?code=" + codes + refreshQuery), cancellationToken);
                    if (!response.Success || string.IsNullOrWhiteSpace(response.Json))
                    {
                        return;
                    }

                    foreach (JsonElement item in EnumerateDataArray(response.Json))
                    {
                        string code = NormalizeCode(GetString(item, "Code"));
                        var stock = batch.FirstOrDefault(candidate => candidate.Code == code);
                        if (string.IsNullOrEmpty(stock.Code))
                        {
                            continue;
                        }

                        p2Processed.TryAdd(stock.Code, true);
                        Interlocked.Increment(ref batchQuoteLoaded);
                        if (!SparrowQuoteDataContract.TryParse(item, out SparrowQuoteData quote)
                            || !quote.PriceDerivedPercent.HasValue
                            || !quote.Amount.HasValue)
                        {
                            Interlocked.Increment(ref quoteDataUnavailable);
                            continue;
                        }

                        double risePct = quote.PriceDerivedPercent.Value;
                        if (risePct < parameters.MinRise
                            || risePct > parameters.MaxRise
                            || quote.Amount.Value < parameters.MinAmount)
                        {
                            continue;
                        }

                        Interlocked.Increment(ref coarseSurvivors);
                        SparrowVolumeCheckResult volumeResult = SparrowQuoteDataContract.EvaluateVolume(
                            quote.OuterVolume, quote.InnerVolume, parameters.VolRatio);
                        if (volumeResult == SparrowVolumeCheckResult.OuterInnerUnavailable)
                        {
                            Interlocked.Increment(ref outerInnerUnavailable);
                            continue;
                        }

                        Interlocked.Increment(ref outerInnerValid);
                        if (volumeResult == SparrowVolumeCheckResult.Passed)
                        {
                            Interlocked.Increment(ref volRatioPassed);
                            p2Survivors[stock.Code] = stock;
                            rankingQuotes[stock.Code] = quote;
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The caller owns cancellation reporting.
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Sparrow Classic P2 quote scan failed");
                }
                finally
                {
                    if (entered)
                    {
                        progress?.Report(new SparrowClassicScanReport
                        {
                            ProgressValue = Math.Min(classicPool.Count, p2Processed.Count),
                            P2Survivors = p2Survivors.Count
                        });
                        semaphore.Release();
                    }
                }
            }));

            int missingResponses = Math.Max(0, p2Pending.Count - batchQuoteLoaded);
            ReportLog(progress,
                $"📊 [Sparrow Classic][P2数据] Batch quote loaded: {batchQuoteLoaded}; " +
                $"Quote data unavailable: {quoteDataUnavailable + missingResponses}; " +
                $"Coarse P2 survivors: {coarseSurvivors}; Detail quote requested: 0; " +
                $"Outer/inner valid: {outerInnerValid}; Outer/inner unavailable: {outerInnerUnavailable}; " +
                $"VolRatio passed: {volRatioPassed}");
        }
        else
        {
            volRatioPassed = p2Survivors.Count;
            ReportLog(progress, $"\n♻️ [Sparrow Classic][盘口缓存] 阶段2完成，幸存者: {p2Survivors.Count} 只");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 [Sparrow Classic] 已暂停，阶段2进度: {p2Processed.Count}/{classicPool.Count}。", true);
            return new List<SparrowClassicCandidate>();
        }

        var poolCodes = classicPool.Select(stock => stock.Code).ToHashSet(StringComparer.Ordinal);
        var p2List = p2Survivors.Values.Where(stock => poolCodes.Contains(stock.Code)).ToList();
        ReportLog(progress, $"\n🔪 [Sparrow Classic][阶段2结束] 进入阶段3: {p2List.Count} 只");
        ReportLog(progress, $"\n🔬 [Sparrow Classic][阶段3] K线核验开始 (标的数:{p2List.Count})...");
        progress?.Report(new SparrowClassicScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });

        int p3Completed = 0;
        int p3KlineValid = 0;
        int p3MaOrderPassed = 0;
        int p3Ma60Passed = 0;
        int p3AdhesionPassed = 0;

        int rejectKlineMissing = 0;
        int rejectMaOrder = 0;
        int rejectMa60 = 0;
        int rejectAdhesionHigh = 0;
        int rejectAdhesionLow = 0;

        int p3Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
        using var klineSemaphore = new SemaphoreSlim(Math.Max(1, Math.Min(32, p3Concurrency * 4)));
        await Task.WhenAll(p2List.Select(async stock =>
        {
            bool entered = false;
            try
            {
                await klineSemaphore.WaitAsync(cancellationToken);
                entered = true;
                string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, 65, "day");
                if (!_klineCache.TryGet(cacheKey, parameters.UseCache, out string? klineJson))
                {
                    string refreshParam = parameters.UseCache ? "" : "&refresh=1";
                    StockDataResult response = await _dataProvider.GetDataAsync(
                        StockDataRequest.Parse("/api/kline-all?code=" + stock.Code + "&type=day&limit=65" + refreshParam),
                        cancellationToken);
                    if (response.Success && !string.IsNullOrWhiteSpace(response.Json))
                    {
                        klineJson = response.Json;
                        _klineCache.Set(cacheKey, klineJson, SparrowDataCachePolicy.DefaultKlineTtl);
                    }
                }

                if (string.IsNullOrWhiteSpace(klineJson))
                {
                    Interlocked.Increment(ref rejectKlineMissing);
                    return;
                }

                List<double> closes = ParseKlineClosesNewestFirst(klineJson, out double latestPrice);
                SparrowClassicTechnicalResult technical = SparrowClassicRuleEvaluator.Evaluate(closes, latestPrice, parameters);
                if (technical.RejectReason == SparrowClassicP3RejectReason.KlineMissing)
                {
                    Interlocked.Increment(ref rejectKlineMissing);
                    return;
                }

                Interlocked.Increment(ref p3KlineValid);
                if (technical.RejectReason == SparrowClassicP3RejectReason.MaOrder)
                {
                    Interlocked.Increment(ref rejectMaOrder);
                    return;
                }

                Interlocked.Increment(ref p3MaOrderPassed);
                if (technical.RejectReason == SparrowClassicP3RejectReason.Ma60)
                {
                    Interlocked.Increment(ref rejectMa60);
                    return;
                }

                Interlocked.Increment(ref p3Ma60Passed);
                if (technical.RejectReason == SparrowClassicP3RejectReason.AdhesionHigh)
                {
                    Interlocked.Increment(ref rejectAdhesionHigh);
                    return;
                }
                if (technical.RejectReason is SparrowClassicP3RejectReason.AdhesionLow
                    or SparrowClassicP3RejectReason.MinMaInvalid)
                {
                    Interlocked.Increment(ref rejectAdhesionLow);
                    return;
                }

                if (!technical.Passed)
                {
                    return;
                }

                Interlocked.Increment(ref p3AdhesionPassed);
                rankingQuotes.TryGetValue(stock.Code, out SparrowQuoteData quote);
                var candidate = new SparrowClassicCandidate
                {
                    Code = stock.Code,
                    Name = stock.Name,
                    Reason = $"多头 黏合度:{technical.Adhesion * 100:F2}%",
                    RankingFeatures = new SparrowRankingFeatures
                    {
                        Code = stock.Code,
                        Name = stock.Name,
                        RisePercent = quote.PriceDerivedPercent,
                        Amount = quote.Amount,
                        OuterVolume = quote.OuterVolume,
                        InnerVolume = quote.InnerVolume,
                        BuyPressureRatio = SparrowRankingFeatures.CalculateBuyPressureRatio(
                            quote.OuterVolume, quote.InnerVolume),
                        Adhesion = technical.Adhesion
                    }
                };
                p3Winners[stock.Code] = candidate;
                ReportLog(progress, $"🎯 [Sparrow Classic][入围] {stock.Name}({stock.Code}) {candidate.Reason}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller owns cancellation reporting.
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Sparrow Classic P3 K-line evaluation failed for {Code}", stock.Code);
            }
            finally
            {
                if (entered)
                {
                    int completed = Interlocked.Increment(ref p3Completed);
                    progress?.Report(new SparrowClassicScanReport { ProgressValue = completed, P3Winners = p3Winners.Count });
                    klineSemaphore.Release();
                }
            }
        }));

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 [Sparrow Classic] 已暂停，阶段3进度: {p3Completed}/{p2List.Count}。", true);
            return new List<SparrowClassicCandidate>();
        }

        List<SparrowClassicCandidate> results = p3Winners.Values.OrderBy(candidate => candidate.Code).ToList();
        ReportLog(progress, $"\n🏆 [Sparrow Classic] 漏斗完成，共入围 {results.Count} 只。Strategy = {SparrowClassicCandidate.StrategyName}");
        ReportLog(progress, $"🧭 [Sparrow Classic][Cache Lifetime Totals] Kline: {_klineCache.Statistics}");

        OutputFunnelReport(progress,
            classicPool.Count,
            p1Status,
            batchQuoteLoaded,
            coarseSurvivors,
            outerInnerValid,
            volRatioPassed,
            p3KlineValid,
            p3MaOrderPassed,
            p3Ma60Passed,
            p3AdhesionPassed,
            results.Count,
            rejectMaOrder,
            rejectMa60,
            rejectAdhesionHigh,
            rejectKlineMissing,
            rejectAdhesionLow);

        if (results.Count > 0)
        {
            await OutputResultsAsync(results, progress);
        }
        return results;
    }

    private static void OutputFunnelReport(
        IProgress<SparrowClassicScanReport>? progress,
        int initialCount,
        string p1Status,
        int p2QuoteLoaded,
        int coarseSurvivors,
        int outerInnerValid,
        int volRatioPassed,
        int p3KlineValid,
        int p3MaOrderPassed,
        int p3Ma60Passed,
        int p3AdhesionPassed,
        int finalWinners,
        int rejectMaOrder,
        int rejectMa60,
        int rejectAdhesionHigh,
        int rejectKlineMissing,
        int rejectAdhesionLow)
    {
        string adhesionLowLine = rejectAdhesionLow > 0
            ? $"ADHESION_LOW    {rejectAdhesionLow}\n"
            : "";

        string funnel =
            "\n========== Classic 漏斗统计 ==========\n\n" +
            $"初始股票:             {initialCount}\n\n" +
            $"P1 大盘防守:\n{p1Status}\n\n" +
            $"P2 Quote loaded:      {p2QuoteLoaded}\n\n" +
            $"涨幅+成交额通过:       {coarseSurvivors}\n" +
            $"外/内盘数据有效:       {outerInnerValid}\n" +
            $"外/内盘比例通过:       {volRatioPassed}\n\n" +
            $"P3 K线有效:            {p3KlineValid}\n" +
            $"MA5/10/20通过:         {p3MaOrderPassed}\n" +
            $"MA60通过:              {p3Ma60Passed}\n" +
            $"黏合度通过:            {p3AdhesionPassed}\n\n" +
            $"最终入围:              {finalWinners}\n\n" +
            "=====================================\n\n" +
            "P3 Reject Reasons\n\n" +
            $"MA_ORDER        {rejectMaOrder}\n" +
            $"MA60            {rejectMa60}\n" +
            $"ADHESION_HIGH   {rejectAdhesionHigh}\n" +
            $"KLINE_MISSING   {rejectKlineMissing}\n" +
            adhesionLowLine;

        ReportLog(progress, funnel);
    }

    public static bool IsEligibleStock(string? code, string? name)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return !name.Contains("ST", StringComparison.OrdinalIgnoreCase)
            && !code.StartsWith("688", StringComparison.Ordinal)
            && (code.StartsWith("60", StringComparison.Ordinal)
                || code.StartsWith("00", StringComparison.Ordinal)
                || code.StartsWith("30", StringComparison.Ordinal));
    }

    private static IEnumerable<JsonElement> EnumerateDataArray(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }
        return data.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static List<double> ParseKlineClosesNewestFirst(string json, out double latestPrice)
    {
        List<double> closes = ParseKlineClosesOldestFirst(json);
        closes.Reverse();
        latestPrice = closes.Count > 0 ? closes[0] : 0;
        return closes;
    }

    private static List<double> ParseKlineClosesOldestFirst(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement data = root.ValueKind == JsonValueKind.Array ? root : default;
        if (data.ValueKind == JsonValueKind.Undefined && TryGetPropertyIgnoreCase(root, "data", out JsonElement wrapped))
        {
            data = wrapped.ValueKind == JsonValueKind.Array
                ? wrapped
                : TryGetPropertyIgnoreCase(wrapped, "list", out JsonElement list) ? list
                : TryGetPropertyIgnoreCase(wrapped, "klines", out JsonElement klines) ? klines
                : default;
        }

        var closes = new List<double>();
        if (data.ValueKind != JsonValueKind.Array)
        {
            return closes;
        }

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (TryGetDouble(item, "Close", out double closeRaw))
            {
                closes.Add(closeRaw / 1000.0);
            }
        }
        return closes;
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
        return property.ValueKind == JsonValueKind.String && double.TryParse(
            property.GetString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!TryGetPropertyIgnoreCase(element, name, out JsonElement property))
        {
            return string.Empty;
        }
        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText();
    }

    private static string NormalizeCode(string code)
    {
        string normalized = code.Trim();
        return normalized.StartsWith("1.", StringComparison.Ordinal) || normalized.StartsWith("0.", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }

    private static void ReportLog(IProgress<SparrowClassicScanReport>? progress, string message, bool isHighlight = false)
    {
        progress?.Report(new SparrowClassicScanReport { LogMessage = message, IsHighlight = isHighlight });
    }

    private static async Task OutputResultsAsync(
        IReadOnlyCollection<SparrowClassicCandidate> results,
        IProgress<SparrowClassicScanReport>? progress)
    {
        string directory = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        }
        Directory.CreateDirectory(directory);

        string filePath = Path.Combine(directory, $"麻雀池_Classic_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
        await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【Sparrow Classic】选股结果\nStrategy: {SparrowClassicCandidate.StrategyName}\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (SparrowClassicCandidate item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t策略: {item.Strategy} \t说明: {item.Reason}");
        }
        ReportLog(progress, $"\n📁 [Sparrow Classic] 结果已存至: {filePath}");
    }
}
