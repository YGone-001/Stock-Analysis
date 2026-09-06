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

#pragma warning disable CS8600, CS8602, CS8604
namespace AIHelper.Services.StockData;

public class SparrowLegacyScannerService
{
    private readonly IStockDataProvider _dataProvider;
    private readonly SparrowMarketRegimeService _marketRegimeService;
    private readonly SparrowMarketDataCache _klineCache;

    public SparrowMarketDataCache KlineCache => _klineCache;

    public SparrowLegacyScannerService(
        IStockDataProvider dataProvider,
        SparrowMarketRegimeService? marketRegimeService = null,
        SparrowMarketDataCache? klineCache = null)
    {
        _dataProvider = dataProvider;
        _marketRegimeService = marketRegimeService ?? new SparrowMarketRegimeService(dataProvider);
        _klineCache = klineCache ?? new SparrowMarketDataCache();
    }

    public async Task<List<(string Code, string Name, string Reason)>> ScanAsync(
        List<(string Code, string Name)> targetPool,
        SparrowLegacyScanParameters parameters,
        IProgress<SparrowLegacyScanReport> progress,
        CancellationToken cancellationToken)
    {
        if (!parameters.UseCache)
        {
            _klineCache.Clear();
        }

        // Scan-local session state
        var p2Processed = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        var p2Survivors = new ConcurrentBag<(string Code, string Name)>();
        var p3Winners = new ConcurrentBag<(string Code, string Name, string Reason)>();

        ReportLog(progress, $"🦅 [麻雀 4.0-高速版] 引擎点火！初始标的: {targetPool.Count} 只");

        if (parameters.MacroDef)
        {
            ReportLog(progress, "🛡️ [阶段1] 检测大盘宏观安全度...");
            var regime = await _marketRegimeService.EvaluateAsync(cancellationToken);
            if (regime.Defensive)
            {
                ReportLog(progress, "❌ [熔断] 大盘环境极度恶化，空仓防御！", true);
                return new List<(string, string, string)>();
            }
            if (regime.Shanghai == SparrowMarketState.Unknown || regime.Csi1000 == SparrowMarketState.Unknown)
            {
                ReportLog(progress, "⚠️ 市场数据部分不可用，按历史策略放行。");
            }
            ReportLog(progress, "✅ [第一阶段通过] 允许开启个股海选。");
        }

        var p2Pending = targetPool.Where(s => !p2Processed.ContainsKey(s.Code)).ToList();
        if (p2Pending.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段2] 极速网关并发快照扫描 (待处理:{p2Pending.Count} / 总计:{targetPool.Count})...");
            int p2Completed = 0;
            int p2SampleCount = 0;
            int batchQuoteLoaded = 0;
            int quoteDataUnavailable = 0;
            int coarseSurvivors = 0;
            int outerInnerValid = 0;
            int outerInnerUnavailable = 0;
            int volRatioPassed = 0;
            progress?.Report(new SparrowLegacyScanReport { ProgressMax = targetPool.Count, ProgressValue = p2Processed.Count });

            var batches = p2Pending.Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / 50)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();

            string refreshQuery = parameters.UseCache ? "" : "&refresh=1";
            int p2Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore2 = new SemaphoreSlim(Math.Max(1, Math.Min(8, p2Concurrency)));
            await Task.WhenAll(batches.Select(async batch =>
            {
                await semaphore2.WaitAsync();
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    string codesStr = string.Join(",", batch.Select(x => x.Code));
                    var req = StockDataRequest.Parse("/api/quote?code=" + codesStr + refreshQuery);
                    var res = await _dataProvider.GetDataAsync(req, cancellationToken);
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
                                            var stock = batch.FirstOrDefault(s => s.Code == code);
                                            if (stock.Code != null)
                                            {
                                                p2Processed.TryAdd(stock.Code, true);
                                                Interlocked.Increment(ref batchQuoteLoaded);
                                                if (Interlocked.Increment(ref p2SampleCount) <= 2)
                                                {
                                                    ReportLog(progress, $"📝 [P2抽样] {stock.Name}({stock.Code}) 原始数据片段");
                                                }
                                                if (!SparrowQuoteDataContract.TryParse(item, out SparrowQuoteData quote)
                                                    || quote.Price is not > 0.001
                                                    || !quote.Percent.HasValue
                                                    || !quote.Amount.HasValue)
                                                {
                                                    Interlocked.Increment(ref quoteDataUnavailable);
                                                    continue;
                                                }

                                                if (quote.Percent.Value < parameters.MinRise
                                                    || quote.Percent.Value > parameters.MaxRise
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
                                                    p2Survivors.Add((stock.Code, stock.Name));
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Log.Error(ex, "Failed to parse batch quote JSON"); }
                    }
                }
                catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); }
                finally
                {
                    int c = Interlocked.Add(ref p2Completed, batch.Count);
                    if (c % 50 == 0 || c == p2Pending.Count)
                    {
                        progress?.Report(new SparrowLegacyScanReport { ProgressValue = p2Processed.Count, P2Survivors = p2Survivors.Count });
                    }
                    semaphore2.Release();
                }
            }));

            int missingResponses = Math.Max(0, p2Pending.Count - batchQuoteLoaded);
            ReportLog(progress,
                $"📊 [Sparrow Legacy][P2数据] Batch quote loaded: {batchQuoteLoaded}; " +
                $"Quote data unavailable: {quoteDataUnavailable + missingResponses}; " +
                $"Coarse P2 survivors: {coarseSurvivors}; Detail quote requested: 0; " +
                $"Outer/inner valid: {outerInnerValid}; Outer/inner unavailable: {outerInnerUnavailable}; " +
                $"VolRatio passed: {volRatioPassed}");
        }
        else
        {
            ReportLog(progress, $"\n♻️ [盘口缓存激活] 阶段2瞬间完成。当前幸存者: {p2Survivors.Count} 只");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 已安全暂停！阶段2 进度: {p2Processed.Count}/{targetPool.Count}。勾选[使用缓存]再次启动可断点续传！", true);
            return new List<(string, string, string)>();
        }

        ReportLog(progress, $"\n🔪 [阶段2结束] 进入下阶段: {p2Survivors.Count} 只");
        var p2List = p2Survivors.ToList();

        ReportLog(progress, $"\n🔬 [阶段3] K线极速核验开始 (标的数:{p2List.Count})...");
        int p3Completed = 0;
        int p3SampleCount = 0;
        progress?.Report(new SparrowLegacyScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });

        int p3Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(32, p3Concurrency * 4)));
        await Task.WhenAll(p2List.Select(async stock =>
        {
            await semaphore.WaitAsync();
            try
            {
                string klineJson = null;
                string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, 65, "day");
                if (!_klineCache.TryGet(cacheKey, parameters.UseCache, out klineJson))
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    string refreshParam = parameters.UseCache ? "" : "&refresh=1";
                    var req = StockDataRequest.Parse("/api/kline-all?code=" + stock.Code + "&limit=65" + refreshParam);
                    var res = await _dataProvider.GetDataAsync(req, cancellationToken);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
                    {
                        klineJson = res.Json;
                        _klineCache.Set(cacheKey, klineJson, SparrowDataCachePolicy.DefaultKlineTtl);
                    }
                }

                if (klineJson == null) return;

                if (Interlocked.Increment(ref p3SampleCount) <= 2)
                {
                    ReportLog(progress, $"📝 [P3抽样] {stock.Name}({stock.Code}) K线已就绪");
                }

                var list = ParseKlineClosesEnhanced(klineJson, out double latestPrice);
                if (list.Count < 60)
                {
                    if (list.Count == 0) ReportLog(progress, $"⚠️ [{stock.Name}] K线解析为空，可能已被限流跳过", true);
                }
                else
                {
                    double ma5 = list.Take(5).Average();
                    double ma10 = list.Take(10).Average();
                    double ma20 = list.Take(20).Average();
                    double ma60 = list.Take(60).Average();

                    if (ma5 > ma10 && ma10 > ma20 && (!parameters.CheckMA60 || (latestPrice > ma60 && ma20 > ma60)))
                    {
                        double maxMa = Math.Max(ma5, Math.Max(ma10, ma20));
                        double minMa = Math.Min(ma5, Math.Min(ma10, ma20));
                        double adhesion = (maxMa - minMa) / minMa;
                        
                        if (adhesion >= parameters.MinAdhesion && adhesion <= parameters.MaxAdhesion)
                        {
                            string reason = $"多头 黏合度:{adhesion * 100:F2}%";
                            p3Winners.Add((stock.Code, stock.Name, reason));
                            ReportLog(progress, $"🎯 [入围] {stock.Name}({stock.Code}) 黏合度:{adhesion * 100:F2}%");
                        }
                    }
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); }
            finally
            {
                int c = Interlocked.Increment(ref p3Completed);
                progress?.Report(new SparrowLegacyScanReport { ProgressValue = c, P3Winners = p3Winners.Count });
                semaphore.Release();
            }
        }));

        ReportLog(progress, $"\n✅ P3处理完毕。");

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 已安全暂停！P3 进度: {p3Completed}/{p2List.Count}。", true);
            return new List<(string, string, string)>();
        }

        ReportLog(progress, $"\n🏆 漏斗完成！共诞生长短腿麻雀 {p3Winners.Count} 只！");
        ReportLog(progress, $"🧭 [Sparrow Legacy][Cache] Kline: {_klineCache.Statistics}");
        
        var results = p3Winners.ToList();
        if (results.Count > 0)
        {
            await OutputResultsAsync(results, progress);
        }

        return results;
    }

    private void ReportLog(IProgress<SparrowLegacyScanReport> progress, string msg, bool isHighlight = false)
    {
        progress?.Report(new SparrowLegacyScanReport { LogMessage = msg, IsHighlight = isHighlight });
    }

    private List<double> ParseKlineClosesEnhanced(string json, out double latestPrice)
    {
        var list = new List<double>();
        latestPrice = 0.0;
        if (string.IsNullOrWhiteSpace(json)) return list;
        
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
                        list.Add(closeElem.GetDouble() / 1000.0);
                    }
                }
                if (list.Count > 0)
                {
                    latestPrice = list[0];
                }
            }
        }
        catch { }
        return list;
    }

    private async Task OutputResultsAsync(List<(string Code, string Name, string Reason)> results, IProgress<SparrowLegacyScanReport> progress)
    {
        string text = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(text)) text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        if (!Directory.Exists(text)) Directory.CreateDirectory(text);
        
        string filePath = Path.Combine(text, $"麻雀池高速版_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        using (StreamWriter writer = new StreamWriter(filePath, append: false, Encoding.UTF8))
        {
            await writer.WriteLineAsync($"【麻雀战法 4.0高速版】选股结果\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
            foreach (var item in results)
            {
                await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
            }
        }
        ReportLog(progress, $"\n📁 结果已存至: {filePath}");
    }
}

