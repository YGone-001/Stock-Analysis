using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Helpers;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Serilog;

#pragma warning disable CS8600, CS8602, CS8604
namespace AIHelper.Services.StockData;

public class SparrowLegacyScannerService
{
    private readonly IStockDataProvider _dataProvider;
    private readonly ConcurrentDictionary<string, bool> _p2Processed = new();
    private readonly ConcurrentBag<(string Code, string Name)> _p2Survivors = new();
    private readonly ConcurrentDictionary<string, string> _p3KlineCache = new();
    private readonly ConcurrentBag<(string Code, string Name, string Reason)> _p3Winners = new();

    public SparrowLegacyScannerService(IStockDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    public async Task<List<(string Code, string Name, string Reason)>> ScanAsync(
        List<(string Code, string Name)> targetPool,
        SparrowLegacyScanParameters parameters,
        IProgress<SparrowLegacyScanReport> progress,
        CancellationToken cancellationToken)
    {
        if (!parameters.UseCache)
        {
            _p2Processed.Clear();
            _p2Survivors.Clear();
            _p3KlineCache.Clear();
        }
        _p3Winners.Clear();

        ReportLog(progress, $"🦅 [麻雀 4.0-高速版] 引擎点火！初始标的: {targetPool.Count} 只");

        if (parameters.MacroDef && _p2Processed.Count == 0)
        {
            ReportLog(progress, "🛡️ [阶段1] 检测大盘宏观安全度...");
            if (await CheckIndexWeakness("sh000001") && await CheckIndexWeakness("sh000852"))
            {
                ReportLog(progress, "❌ [熔断] 大盘环境极度恶化，空仓防御！", true);
                return new List<(string, string, string)>();
            }
            ReportLog(progress, "✅ [第一阶段通过] 允许开启个股海选。");
        }

        var p2Pending = targetPool.Where(s => !_p2Processed.ContainsKey(s.Code)).ToList();
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
            progress?.Report(new SparrowLegacyScanReport { ProgressMax = targetPool.Count, ProgressValue = _p2Processed.Count });

            var batches = p2Pending.Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / 50)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();

            using var semaphore2 = new SemaphoreSlim(Math.Min(8, parameters.MaxConcurrency));
            await Task.WhenAll(batches.Select(async batch =>
            {
                await semaphore2.WaitAsync();
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    string codesStr = string.Join(",", batch.Select(x => x.Code));
                    var req = StockDataRequest.Parse("/api/quote?code=" + codesStr);
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
                                                _p2Processed.TryAdd(stock.Code, true);
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
                                                    _p2Survivors.Add((stock.Code, stock.Name));
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
                        progress?.Report(new SparrowLegacyScanReport { ProgressValue = _p2Processed.Count, P2Survivors = _p2Survivors.Count });
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
            ReportLog(progress, $"\n♻️ [盘口缓存激活] 阶段2瞬间完成。当前幸存者: {_p2Survivors.Count} 只");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 已安全暂停！阶段2 进度: {_p2Processed.Count}/{targetPool.Count}。勾选[使用缓存]再次启动可断点续传！", true);
            return new List<(string, string, string)>();
        }

        ReportLog(progress, $"\n🔪 [阶段2结束] 进入下阶段: {_p2Survivors.Count} 只");
        var p2List = _p2Survivors.ToList();

        ReportLog(progress, $"\n🔬 [阶段3] K线极速核验开始 (标的数:{p2List.Count})...");
        int p3Completed = 0;
        int p3SampleCount = 0;
        progress?.Report(new SparrowLegacyScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });

        using var semaphore = new SemaphoreSlim(Math.Min(32, parameters.MaxConcurrency * 4));
        await Task.WhenAll(p2List.Select(async stock =>
        {
            await semaphore.WaitAsync();
            try
            {
                string klineJson = null;
                if (_p3KlineCache.TryGetValue(stock.Code, out var value))
                {
                    klineJson = value;
                }
                else
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    var req = StockDataRequest.Parse("/api/kline-all?code=" + stock.Code + "&limit=65");
                    var res = await _dataProvider.GetDataAsync(req, cancellationToken);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
                    {
                        klineJson = res.Json;
                        _p3KlineCache.TryAdd(stock.Code, klineJson);
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
                            _p3Winners.Add((stock.Code, stock.Name, reason));
                            ReportLog(progress, $"🎯 [入围] {stock.Name}({stock.Code}) 黏合度:{adhesion * 100:F2}%");
                        }
                    }
                }
            }
            catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); }
            finally
            {
                int c = Interlocked.Increment(ref p3Completed);
                progress?.Report(new SparrowLegacyScanReport { ProgressValue = c, P3Winners = _p3Winners.Count });
                semaphore.Release();
            }
        }));

        ReportLog(progress, $"\n✅ P3处理完毕。");

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 已安全暂停！P3 进度: {p3Completed}/{p2List.Count}。", true);
            return new List<(string, string, string)>();
        }

        ReportLog(progress, $"\n🏆 漏斗完成！共诞生长短腿麻雀 {_p3Winners.Count} 只！");
        
        var results = _p3Winners.ToList();
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

    private async Task<bool> CheckIndexWeakness(string secid)
    {
        try
        {
            var req = StockDataRequest.Parse("/api/index?code=" + secid + "&limit=5");
            var res = await _dataProvider.GetDataAsync(req, CancellationToken.None);
            if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
            {
                using JsonDocument doc = JsonDocument.Parse(res.Json);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
                {
                    var elements = dataArr.EnumerateArray().ToList();
                    if (elements.Count >= 5)
                    {
                        double current = elements.Last().TryGetProperty("Close", out var c1) ? c1.GetDouble() / 1000.0 : 0.0;
                        double avg = elements.Skip(elements.Count - 5).Average(x => x.TryGetProperty("Close", out var c2) ? c2.GetDouble() / 1000.0 : 0.0);
                        double oldAvg = elements.Take(5).Average(x => x.TryGetProperty("Close", out var c3) ? c3.GetDouble() / 1000.0 : 0.0); // Rough approximation of 5 days ago if we only requested 5.
                        return current < avg && avg < oldAvg;
                    }
                }
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return false;
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

