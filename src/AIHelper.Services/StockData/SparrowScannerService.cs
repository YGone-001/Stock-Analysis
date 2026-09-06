using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        // Scan-local session state: quotes are fresh per scan and never frozen across scans
        var quoteCache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var p3Winners = new ConcurrentDictionary<string, (string Name, string Reason)>(StringComparer.Ordinal);

        ReportLog(progress, $"🦅 [麻雀-全景高速版] 引擎点火！初始标的: {targetPool.Count} 只");

        double shIndexPctChg = 0;
        if (parameters.MacroDef)
        {
            var indexResult = await CheckIndexWeakness(forceRefresh: !parameters.UseCache, ct);
            if (ct.IsCancellationRequested) return new List<(string, string, string)>();
            shIndexPctChg = indexResult.ShIndexPctChg;
            if (indexResult.IsWeak)
            {
                ReportLog(progress, "❌ [熔断] 大盘环境恶化，空仓防御！", true);
                return new List<(string, string, string)>();
            }
            ReportLog(progress, $"✅ [第一阶段通过] 上证今日涨幅: {shIndexPctChg:F2}%, 已设为 RPS 参照基准。");
        }

        var p2Missing = targetPool.Where(s => !quoteCache.ContainsKey(s.Code)).ToList();
        if (p2Missing.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段2] 极速网关并发拉取盘口快照 (待下载:{p2Missing.Count} 只)...");
            int p2Downloaded = 0;
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
                await semaphore.WaitAsync();
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
                            using JsonDocument doc = JsonDocument.Parse(res.Json);
                            if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in dataArr.EnumerateArray())
                                {
                                    if (item.TryGetProperty("Code", out var codeElem))
                                    {
                                        string code = codeElem.GetString() ?? "";
                                        if (code.StartsWith("1.") || code.StartsWith("0.")) code = code.Substring(2);
                                        if (!string.IsNullOrEmpty(code))
                                        {
                                            quoteCache.TryAdd(code, item.GetRawText());
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Log.Error(ex, "Failed to parse batch quote JSON"); }
                    }
                }
                finally
                {
                    int c = Interlocked.Add(ref p2Downloaded, batch.Count);
                    progress?.Report(new SparrowScanReport { ProgressValue = c });
                    semaphore.Release();
                }
            }));
        }

        if (ct.IsCancellationRequested) return new List<(string, string, string)>();

        ReportLog(progress, $"\n🧠 正在根据当前参数对 {targetPool.Count} 只股票进行极速盘口核验...");
        var p2List = new List<(string Code, string Name)>();
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
            }
        }
        ReportLog(progress,
            $"📊 [Sparrow V2][P2数据] Batch quote loaded: {quoteCache.Count}; " +
            $"Quote data unavailable: {quoteDataUnavailable}; Coarse P2 survivors: {coarseSurvivors}; " +
            $"Detail quote requested: 0; Outer/inner valid: {outerInnerValid}; " +
            $"Outer/inner unavailable: {outerInnerUnavailable}; VolRatio passed: {volRatioPassed}");
        ReportLog(progress, $"✅ 盘口过滤完毕，剩余标的: {p2List.Count} 只");

        if (p2List.Count == 0) return new List<(string, string, string)>();

        // Scan-local Kline dictionary: stores data to evaluate during this scan session
        var klineData = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        var p3Missing = new List<(string Code, string Name)>();
        foreach (var stock in p2List)
        {
            string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, 120, "day");
            if (parameters.UseCache && _klineCache.TryGet(cacheKey, true, out var cachedJson) && !string.IsNullOrWhiteSpace(cachedJson))
            {
                klineData[stock.Code] = cachedJson;
            }
            else
            {
                p3Missing.Add(stock);
            }
        }

        if (p3Missing.Count > 0)
        {
            ReportLog(progress, $"\n🔭 [阶段3] 极速网关拉取 K 线 (待下载:{p3Missing.Count} 只)...");
            int p3Downloaded = 0;
            progress?.Report(new SparrowScanReport { ProgressMax = p3Missing.Count, ProgressValue = 0 });

            string refreshParam = parameters.UseCache ? "" : "&refresh=1";
            int p3Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(32, p3Concurrency * 4)));
            await Task.WhenAll(p3Missing.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var req = StockDataRequest.Parse("/api/kline-all?code=" + stock.Code + "&limit=120" + refreshParam);
                    var res = await _dataProvider.GetDataAsync(req, ct);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
                    {
                        klineData[stock.Code] = res.Json;
                        string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, 120, "day");
                        _klineCache.Set(cacheKey, res.Json, SparrowDataCachePolicy.DefaultKlineTtl);
                    }
                }
                finally
                {
                    int c = Interlocked.Increment(ref p3Downloaded);
                    if (c % 10 == 0 || c == p3Missing.Count)
                    {
                        progress?.Report(new SparrowScanReport { ProgressValue = c });
                    }
                    semaphore.Release();
                }
            }));
        }

        if (ct.IsCancellationRequested) return new List<(string, string, string)>();

        ReportLog(progress, $"\n🧠 正在根据当前参数对 {p2List.Count} 只股票进行 K 线深度核验...");
        progress?.Report(new SparrowScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });
        int memCheckCount = 0;

        foreach (var item in p2List)
        {
            memCheckCount++;
            if (memCheckCount % 10 == 0 || memCheckCount == p2List.Count)
            {
                progress?.Report(new SparrowScanReport { ProgressValue = memCheckCount });
            }

            if (!klineData.TryGetValue(item.Code, out var value) || string.IsNullOrWhiteSpace(value)) continue;

            var list = ParseKline(value, out var latestPrice, out var latestPctChg);
            if (list.Count < 60)
            {
                continue;
            }

            if (parameters.CheckAlpha && latestPctChg < shIndexPctChg) continue;

            double ma5 = list.Take(5).Average();
            double ma10 = list.Take(10).Average();
            double ma20 = list.Take(20).Average();
            double ma60 = list.Take(60).Average();

            if (ma5 < ma10 || ma10 < ma20 || (parameters.CheckMA60 && (latestPrice <= ma60 || ma20 < ma60))) continue;

            double prevMa5 = list.Skip(3).Take(5).Average();
            double momentum = (ma5 - prevMa5) / prevMa5;

            if (momentum > parameters.MomentumThreshold)
            {
                double maxMa = Math.Max(ma5, Math.Max(ma10, ma20));
                double minMa = Math.Min(ma5, Math.Min(ma10, ma20));
                double adhesion = (maxMa - minMa) / minMa;

                if (adhesion >= parameters.MinAdhesion && adhesion <= parameters.MaxAdhesion)
                {
                    string reason = $"黏合:{adhesion * 100.0:F1}% 动量:{momentum * 100.0:F1}%";
                    p3Winners[item.Code] = (item.Name, reason);
                    ReportLog(progress, $"🎯 [入围] {item.Name}({item.Code}) {reason}");
                }
            }
        }

        ReportLog(progress, $"\n🏆 漏斗完成！共诞生长短腿战斗机 {p3Winners.Count} 只！");
        ReportLog(progress, $"🧭 [Sparrow V2][Cache Lifetime Totals] Kline: {_klineCache.Statistics}");

        var results = p3Winners.Select(kvp => (kvp.Key, kvp.Value.Name, kvp.Value.Reason)).ToList();
        await OutputResultsToFileAsync(results);
        return results;
    }

    private void ReportLog(IProgress<SparrowScanReport>? progress, string msg, bool isHighlight = false)
    {
        progress?.Report(new SparrowScanReport { LogMessage = msg, IsHighlight = isHighlight });
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

    private List<double> ParseKline(string json, out double latestPrice, out double latestPctChg)
    {
        List<double> list = new List<double>();
        latestPrice = 0.0;
        latestPctChg = 0.0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                var elements = dataArr.EnumerateArray().ToList();
                for (int i = elements.Count - 1; i >= 0; i--)
                {
                    var item = elements[i];
                    if (item.TryGetProperty("Close", out var closeElem))
                    {
                        double close = closeElem.GetDouble() / 1000.0;
                        if (close > 0)
                        {
                            list.Add(close);
                        }
                        if (i == elements.Count - 1)
                        {
                            latestPrice = close;
                            if (i > 0 && elements[i - 1].TryGetProperty("Close", out var prevCloseElem))
                            {
                                double prevClose = prevCloseElem.GetDouble() / 1000.0;
                                latestPctChg = prevClose > 0 ? (close - prevClose) / prevClose * 100.0 : 0.0;
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return list;
    }

    private async Task OutputResultsToFileAsync(List<(string Code, string Name, string Reason)> results)
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

        string fileName = $"麻雀池高速筛选结果_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        string fullPath = Path.Combine(path, fileName);

        using StreamWriter writer = new StreamWriter(fullPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【麻雀战法高速版】选股结果\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (var item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
        }
    }
}

