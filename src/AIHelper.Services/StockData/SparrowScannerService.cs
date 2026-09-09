using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.Sparrow;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Serilog;

#pragma warning disable CS8618, CS8619
namespace AIHelper.Services.StockData;

public class SparrowScanReport
{
    public string LogMessage { get; set; } = null!;
    public bool IsHighlight { get; set; }
    public int? ProgressMax { get; set; }
    public int? ProgressValue { get; set; }
}

public class SparrowScannerService
{
    private readonly IStockDataProvider _dataProvider;
    private readonly SparrowMarketDataCache _klineCache;

    public SparrowMarketDataCache KlineCache => _klineCache;

    public SparrowScannerService(IStockDataProvider dataProvider, SparrowMarketDataCache? klineCache = null)
    {
        _dataProvider = dataProvider;
        _klineCache = klineCache ?? new SparrowMarketDataCache();
    }

    public async Task<List<(string Code, string Name, string Reason)>> ScanAsync(
        List<(string Code, string Name)> targetPool,
        SparrowScanParameters parameters,
        IProgress<SparrowScanReport>? progress,
        CancellationToken ct)
    {
        List<SparrowV2Candidate> candidates = await ScanWithFeaturesAsync(targetPool, parameters, progress, ct);
        return candidates.Select(candidate => (candidate.Code, candidate.Name, candidate.Reason)).ToList();
    }

    public async Task<List<SparrowV2Candidate>> ScanWithFeaturesAsync(
        List<(string Code, string Name)> targetPool,
        SparrowScanParameters parameters,
        IProgress<SparrowScanReport>? progress,
        CancellationToken ct)
    {
        // Scan-local session state: quotes are fresh per scan and never frozen across scans
        var quoteCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var p3Winners = new ConcurrentDictionary<string, SparrowV2Candidate>(StringComparer.Ordinal);

        ReportLog(progress, $"🦅 [麻雀-全景高速版] 引擎点火！初始标的: {targetPool.Count} 只");

        double shIndexPctChg = 0;
        if (parameters.MacroDef)
        {
            var indexResult = await CheckIndexWeakness(forceRefresh: !parameters.UseCache, ct);
            if (ct.IsCancellationRequested) return new List<SparrowV2Candidate>();
            shIndexPctChg = indexResult.ShIndexPctChg;
            if (indexResult.IsWeak)
            {
                ReportLog(progress, "❌ [熔断] 大盘环境恶化，空仓防御！", true);
                return new List<SparrowV2Candidate>();
            }
            ReportLog(progress, $"✅ [第一阶段通过] 上证今日涨幅: {shIndexPctChg:F2}%, 已设为 RPS 参照基准。");
        }

        if (targetPool.Count >= 500)
        {
            ReportLog(progress, "\n🌐 [阶段2] 优先加载全市场盘口快照...");
            try
            {
                string refreshQuery = parameters.UseCache ? "" : "?refresh=1";
                StockDataResult snapshot = await _dataProvider.GetDataAsync(
                    StockDataRequest.Parse("/api/quote-all" + refreshQuery), ct);
                if (snapshot.Success)
                {
                    AddQuotesToCache(snapshot.Json, quoteCache);
                    ReportLog(progress, $"✅ [阶段2快照] 已加载 {quoteCache.Count} 只股票；缺失项将自动批量补取。");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Sparrow V2 all-market quote snapshot failed; falling back to batches");
            }
        }

        var p2Missing = targetPool.Where(s => !quoteCache.ContainsKey(s.Code)).ToList();
        if (p2Missing.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段2] 极速网关并发拉取盘口快照 (待下载:{p2Missing.Count} 只)...");
            int p2Downloaded = 0;
            int p2FailedBatches = 0;
            progress?.Report(new SparrowScanReport { ProgressMax = p2Missing.Count, ProgressValue = 0 });
            
            var batches = p2Missing.Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / 50)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();

            string refreshQuery = parameters.UseCache ? "" : "&refresh=1";
            int p2Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(8, p2Concurrency)));
            await Task.WhenAll(batches.Select(async batch =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    string codesStr = string.Join(",", batch.Select(x => x.Code));
                    var req = StockDataRequest.Parse("/api/quote?code=" + codesStr + refreshQuery);
                    var res = await _dataProvider.GetDataAsync(req, ct);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
                    {
                        try
                        {
                            AddQuotesToCache(res.Json, quoteCache);
                        }
                        catch (Exception ex) { Log.Error(ex, "Failed to parse batch quote JSON"); }
                    }
					else
					{
						Interlocked.Increment(ref p2FailedBatches);
					}
				}
				catch (OperationCanceledException) when (ct.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex)
				{
					Interlocked.Increment(ref p2FailedBatches);
					Log.Warning(ex, "Sparrow V2 P2 quote batch failed; continuing with remaining batches");
                }
                finally
                {
                    int c = Interlocked.Add(ref p2Downloaded, batch.Count);
                    progress?.Report(new SparrowScanReport { ProgressValue = c });
                    semaphore.Release();
                }
            }));

			if (p2FailedBatches > 0)
			{
				ReportLog(progress,
					$"⚠️ [阶段2网络] 批次失败: {p2FailedBatches}/{batches.Count}；已隔离故障并继续处理其余批次。",
					true);
			}
        }

        if (ct.IsCancellationRequested) return new List<SparrowV2Candidate>();

        ReportLog(progress, $"\n🧠 正在根据当前参数对 {targetPool.Count} 只股票进行极速盘口核验...");
        var p2List = new List<(string Code, string Name)>();
        var rankingQuotes = new Dictionary<string, SparrowQuoteData>(StringComparer.Ordinal);
        int quoteDataUnavailable = 0;
        int coarseSurvivors = 0;
        int outerInnerUnavailable = 0;
        int outerInnerValid = 0;
        int volRatioPassed = 0;
        foreach (var item in targetPool)
        {
            if (!quoteCache.TryGetValue(item.Code, out string? value))
            {
                quoteDataUnavailable++;
                continue;
            }

            using JsonDocument quoteDocument = JsonDocument.Parse(value);
            if (!SparrowQuoteDataContract.TryParse(quoteDocument.RootElement, out SparrowQuoteData quote)
                || quote.Price is not > 0.001
                || !quote.Percent.HasValue
                || !quote.Amount.HasValue)
            {
                quoteDataUnavailable++;
                continue;
            }

            if (quote.Percent.Value < parameters.MinRise
                || quote.Percent.Value > parameters.MaxRise
                || quote.Amount.Value < parameters.MinAmount)
            {
                continue;
            }

            coarseSurvivors++;
            SparrowVolumeCheckResult volumeResult = SparrowQuoteDataContract.EvaluateVolume(
                quote.OuterVolume, quote.InnerVolume, parameters.VolRatio);
            if (volumeResult == SparrowVolumeCheckResult.OuterInnerUnavailable)
            {
                outerInnerUnavailable++;
                continue;
            }

            outerInnerValid++;
            if (volumeResult != SparrowVolumeCheckResult.Passed)
            {
                continue;
            }

            volRatioPassed++;
            if (!quote.Turnover.HasValue
                || quote.Turnover.Value <= 0
                || quote.Turnover.Value >= parameters.MinTurnover
                    && quote.Turnover.Value <= parameters.MaxTurnover)
            {
                p2List.Add((item.Code, item.Name));
                rankingQuotes[item.Code] = quote;
            }
        }
        ReportLog(progress,
            $"📊 [Sparrow V2][P2数据] Batch quote loaded: {quoteCache.Count}; " +
            $"Quote data unavailable: {quoteDataUnavailable}; Coarse P2 survivors: {coarseSurvivors}; " +
            $"Detail quote requested: 0; Outer/inner valid: {outerInnerValid}; " +
            $"Outer/inner unavailable: {outerInnerUnavailable}; VolRatio passed: {volRatioPassed}");
        ReportLog(progress, $"✅ 盘口过滤完毕，剩余标的: {p2List.Count} 只");

        if (p2List.Count == 0) return new List<SparrowV2Candidate>();

        // Scan-local Kline dictionary: stores data to evaluate during this scan session
        var klineData = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var klineStatistics = new SparrowKlineFetchStatistics();
        var klineStopwatch = Stopwatch.StartNew();
        int klineCacheHits = 0;

        var p3Missing = new List<(string Code, string Name)>();
        foreach (var stock in p2List)
        {
            string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, 120, "day");
            if (parameters.UseCache
                && _klineCache.TryGet(cacheKey, true, out var cachedJson)
                && !string.IsNullOrWhiteSpace(cachedJson)
                && SparrowKlineFetchHelper.IsUsableKlineJson(cachedJson))
            {
                klineData[stock.Code] = cachedJson;
                klineCacheHits++;
            }
            else
            {
                p3Missing.Add(stock);
            }
        }

        if (p3Missing.Count > 0)
        {
            ReportLog(progress, $"\n🔭 [阶段3] 极速网关拉取 K 线 (待下载:{p3Missing.Count} 只)...");
            int fetchCompleted = 0;
            progress?.Report(new SparrowScanReport { ProgressMax = p3Missing.Count, ProgressValue = 0 });

            string refreshParam = parameters.UseCache ? "" : "&refresh=1";
            int p3Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(32, p3Concurrency * 4)));
            await Task.WhenAll(p3Missing.Select(async stock =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    StockDataRequest request = StockDataRequest.Parse(
                        "/api/kline-all?code=" + stock.Code + "&limit=120" + refreshParam);
                    SparrowKlineFetchOutcome outcome = await SparrowKlineFetchHelper.FetchAsync(
                        _dataProvider,
                        stock.Code,
                        request,
                        SparrowKlineFetchHelper.IsUsableKlineJson,
                        ct);
                    klineStatistics.Record(outcome);
                    if (outcome.Success)
                    {
                        string json = outcome.Json!;
                        klineData[stock.Code] = json;
                        string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, 120, "day");
                        _klineCache.Set(cacheKey, json, SparrowDataCachePolicy.DefaultKlineTtl);
                    }
                }
                finally
                {
                    int c = Interlocked.Increment(ref fetchCompleted);
                    if (c % 10 == 0 || c == p3Missing.Count)
                    {
                        progress?.Report(new SparrowScanReport { ProgressValue = c });
                    }
                    semaphore.Release();
                }
            }));
        }

        ct.ThrowIfCancellationRequested();
        klineStopwatch.Stop();
        ReportLog(progress, klineStatistics.Format(
            "Sparrow V2",
            p2List.Count,
            klineCacheHits,
            p3Missing.Count,
            klineData.Count,
            klineStopwatch.Elapsed));

        List<(string Code, string Name)> p3Available = p2List
            .Where(stock => klineData.ContainsKey(stock.Code))
            .ToList();
        ReportLog(progress, $"\n🧠 正在根据当前参数对 {p3Available.Count} 只股票进行 K 线深度核验...");
        progress?.Report(new SparrowScanReport { ProgressMax = p3Available.Count, ProgressValue = 0 });
        int memCheckCount = 0;

        foreach (var item in p3Available)
        {
            memCheckCount++;
            if (memCheckCount % 10 == 0 || memCheckCount == p3Available.Count)
            {
                progress?.Report(new SparrowScanReport { ProgressValue = memCheckCount });
            }

            string value = klineData[item.Code];

            SparrowKlineSnapshot? snapshot = SparrowV2RuleEvaluator.ParseKline(value);
            SparrowTechnicalEvaluation technical = SparrowV2RuleEvaluator.Evaluate(
                snapshot, shIndexPctChg, parameters);
            if (technical.Rule.Passed)
            {
                string reason = $"黏合:{technical.Adhesion * 100.0:F1}% 动量:{technical.Momentum * 100.0:F1}%";
                SparrowQuoteData quote = rankingQuotes[item.Code];
                p3Winners[item.Code] = new SparrowV2Candidate
                {
                    Code = item.Code,
                    Name = item.Name,
                    Reason = reason,
                    RankingFeatures = new SparrowRankingFeatures
                    {
                        Code = item.Code,
                        Name = item.Name,
                        RisePercent = quote.Percent,
                        Amount = quote.Amount,
                        OuterVolume = quote.OuterVolume,
                        InnerVolume = quote.InnerVolume,
                        BuyPressureRatio = SparrowRankingFeatures.CalculateBuyPressureRatio(
                            quote.OuterVolume, quote.InnerVolume),
                        Adhesion = technical.Adhesion,
                        Turnover = quote.Turnover,
                        Momentum = technical.Momentum,
                        AlphaMargin = parameters.CheckAlpha
                            ? technical.LatestPercent - shIndexPctChg
                            : null
                    }
                };
                ReportLog(progress, $"🎯 [入围] {item.Name}({item.Code}) {reason}");
            }
        }

        ReportLog(progress, $"\n🏆 漏斗完成！共诞生长短腿战斗机 {p3Winners.Count} 只！");
        ReportLog(progress, $"🧭 [Sparrow V2][Cache Lifetime Totals] Kline: {_klineCache.Statistics}");

        var results = p3Winners.Values.OrderBy(candidate => candidate.Code, StringComparer.Ordinal).ToList();
        await OutputResultsToFileAsync(results);
        return results;
    }

    private void ReportLog(IProgress<SparrowScanReport>? progress, string msg, bool isHighlight = false)
    {
        progress?.Report(new SparrowScanReport { LogMessage = msg, IsHighlight = isHighlight });
    }

    private static void AddQuotesToCache(
        string json,
        ConcurrentDictionary<string, string> quoteCache)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("Code", out JsonElement codeElement)) continue;
            string code = codeElement.GetString() ?? "";
            if (code.StartsWith("1.", StringComparison.Ordinal)
                || code.StartsWith("0.", StringComparison.Ordinal))
            {
                code = code.Substring(2);
            }
            if (!string.IsNullOrEmpty(code))
            {
                quoteCache.TryAdd(code, item.GetRawText());
            }
        }
    }

    private async Task<(bool IsWeak, double ShIndexPctChg)> CheckIndexWeakness(bool forceRefresh, CancellationToken cancellationToken)
    {
        double shIndexPctChg = 0.0;
        try
        {
            string url = forceRefresh ? "/api/index?code=sh000001&limit=2&refresh=1" : "/api/index?code=sh000001&limit=2";
            var req = StockDataRequest.Parse(url);
            var res = await _dataProvider.GetDataAsync(req, cancellationToken);
            if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
            {
                using JsonDocument doc = JsonDocument.Parse(res.Json);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    var elements = dataArr.EnumerateArray().ToList();
                    if (elements.Count >= 2)
                    {
                        double prevClose = elements[elements.Count - 2].TryGetProperty("Close", out var pc) ? pc.GetDouble() / 1000.0 : 0.0;
                        double close = elements[elements.Count - 1].TryGetProperty("Close", out var c) ? c.GetDouble() / 1000.0 : 0.0;
                        if (prevClose > 0)
                        {
                            shIndexPctChg = (close - prevClose) / prevClose * 100.0;
                            return (shIndexPctChg <= -2.5, shIndexPctChg);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, 0.0);
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return (false, 0.0);
    }

    private async Task OutputResultsToFileAsync(IReadOnlyCollection<SparrowV2Candidate> results)
    {
        if (results.Count == 0) return;

        string path = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        }
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        string fileName = $"麻雀池高速筛选结果_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.txt";
        string fullPath = Path.Combine(path, fileName);

        using StreamWriter writer = new StreamWriter(fullPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【麻雀战法高速版】选股结果\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (var item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
        }
    }
}

