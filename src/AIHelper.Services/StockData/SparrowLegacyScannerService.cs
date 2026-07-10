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
using Serilog;

#pragma warning disable CS8600, CS8602, CS8604
#pragma warning disable CS8600, CS8602, CS8604
namespace AIHelper.Services.StockData;

public class SparrowLegacyScannerService
{
    private readonly ConcurrentDictionary<string, bool> _p2Processed = new();
    private readonly ConcurrentBag<(string Code, string Name)> _p2Survivors = new();
    private readonly ConcurrentDictionary<string, string> _p3KlineCache = new();
    private readonly ConcurrentBag<(string Code, string Name, string Reason)> _p3Winners = new();

    private int _globalPauseFlag;

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
        _globalPauseFlag = 0;

        ReportLog(progress, $"🦅 [麻雀 4.0] 引擎点火！初始标的: {targetPool.Count} 只");

        if (parameters.MacroDef && _p2Processed.Count == 0)
        {
            ReportLog(progress, "🛡️ [阶段1] 检测大盘宏观安全度...");
            if (await CheckIndexWeakness("1.000001") && await CheckIndexWeakness("1.000852"))
            {
                ReportLog(progress, "❌ [熔断] 大盘环境极度恶化，空仓防御！", true);
                return new List<(string, string, string)>();
            }
            ReportLog(progress, "✅ [第一阶段通过] 允许开启个股海选。");
        }

        var p2Pending = targetPool.Where(s => !_p2Processed.ContainsKey(s.Code)).ToList();
        if (p2Pending.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段2] 快照扫描 (待处理:{p2Pending.Count} / 总计:{targetPool.Count} / 并发:{parameters.MaxConcurrency})...");
            int p2Completed = 0;
            int p2SampleCount = 0;
            progress?.Report(new SparrowLegacyScanReport { ProgressMax = targetPool.Count, ProgressValue = _p2Processed.Count });

            using var semaphore2 = new SemaphoreSlim(parameters.MaxConcurrency);
            await Task.WhenAll(p2Pending.Select(async stock =>
            {
                await semaphore2.WaitAsync();
                try
                {
                    string quoteJson = null;
                    bool p2Success = false;
                    bool networkFatal = false;
                    for (int retry = 0; retry < 5; retry++)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        quoteJson = await NetworkHelper.GetDataAsync("/api/quote?code=" + stock.Code);
                        if (!string.IsNullOrWhiteSpace(quoteJson))
                        {
                            if (quoteJson.Contains("use of closed network connection"))
                            {
                                if (!cancellationToken.IsCancellationRequested)
                                {
                                    ReportLog(progress, "❌ 上游服务器异常拒绝，请降低线程数重试！", true);
                                }
                                networkFatal = true;
                                return;
                            }
                            if (quoteJson.Contains("\"code\":-1") && quoteJson.Contains("超时"))
                            {
                                if (Interlocked.Exchange(ref _globalPauseFlag, 1) == 0)
                                {
                                    ReportLog(progress, "⚠️ 网络波动、正在尽力尝试 (全员暂停5秒)...", true);
                                    try { await Task.Delay(5000, cancellationToken); } catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                                    Interlocked.Exchange(ref _globalPauseFlag, 0);
                                }
                                else
                                {
                                    while (_globalPauseFlag == 1 && !cancellationToken.IsCancellationRequested)
                                    {
                                        await Task.Delay(200);
                                    }
                                }
                                continue;
                            }
                            if (!quoteJson.Contains("\"code\":-1"))
                            {
                                p2Success = true;
                                break;
                            }
                        }
                        await Task.Delay(500, cancellationToken);
                    }

                    if (!networkFatal && !cancellationToken.IsCancellationRequested)
                    {
                        _p2Processed.TryAdd(stock.Code, true);
                        if (p2Success)
                        {
                            if (Interlocked.Increment(ref p2SampleCount) <= 2)
                            {
                                ReportLog(progress, $"📝 [P2抽样] {stock.Name}({stock.Code}) 原始数据:\n{quoteJson}");
                            }
                            if (ParseSnapshot(quoteJson, out var risePct, out var outerVol, out var innerVol, out var amount, out _) &&
                                risePct >= parameters.MinRise && risePct <= parameters.MaxRise &&
                                amount >= parameters.MinAmount && outerVol > 0 && innerVol > 0 &&
                                outerVol > innerVol * parameters.VolRatio)
                            {
                                _p2Survivors.Add((stock.Code, stock.Name));
                            }
                        }
                    }
                }
                catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
                    ReportLog(progress, $"⚠️ P2扫描异常 [{stock.Name}]: {ex.Message}");
                }
                finally
                {
                    int c = Interlocked.Increment(ref p2Completed);
                    if (c % 50 == 0 || c == p2Pending.Count)
                    {
                        progress?.Report(new SparrowLegacyScanReport { ProgressValue = _p2Processed.Count, P2Survivors = _p2Survivors.Count });
                    }
                    semaphore2.Release();
                }
            }));
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

        ReportLog(progress, $"\n🔬 [阶段3] K线深度体检开始 (标的数:{p2List.Count})...");
        int p3Completed = 0;
        int p3SampleCount = 0;
        int p3NetworkHit = 0;
        int p3CacheHit = 0;
        progress?.Report(new SparrowLegacyScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });

        using var semaphore = new SemaphoreSlim(parameters.MaxConcurrency);
        await Task.WhenAll(p2List.Select(async stock =>
        {
            await semaphore.WaitAsync();
            try
            {
                string klineJson = null;
                if (_p3KlineCache.TryGetValue(stock.Code, out var value))
                {
                    klineJson = value;
                    Interlocked.Increment(ref p3CacheHit);
                }
                else
                {
                    Interlocked.Increment(ref p3NetworkHit);
                    bool networkFatal = false;
                    for (int i = 1; i <= 5; i++)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        try
                        {
                            klineJson = await NetworkHelper.GetDataAsync($"/api/kline-all?code={stock.Code}&type=day&limit=65");
                            if (!string.IsNullOrWhiteSpace(klineJson))
                            {
                                if (klineJson.Contains("use of closed network connection"))
                                {
                                    if (!cancellationToken.IsCancellationRequested)
                                    {
                                        ReportLog(progress, "❌ 上游服务器异常拒绝，请降低线程数重试！", true);
                                    }
                                    networkFatal = true;
                                    return;
                                }
                                if (klineJson.Contains("\"code\":-1") && klineJson.Contains("超时"))
                                {
                                    if (Interlocked.Exchange(ref _globalPauseFlag, 1) == 0)
                                    {
                                        ReportLog(progress, "⚠️ 网络波动、正在尽力尝试 (全员暂停5秒)...", true);
                                        try { await Task.Delay(5000, cancellationToken); } catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                                        Interlocked.Exchange(ref _globalPauseFlag, 0);
                                    }
                                    else
                                    {
                                        while (_globalPauseFlag == 1 && !cancellationToken.IsCancellationRequested)
                                        {
                                            await Task.Delay(200);
                                        }
                                    }
                                    continue;
                                }
                                if (klineJson.Contains("{") && klineJson.Contains("["))
                                {
                                    using (JsonDocument.Parse(klineJson))
                                    {
                                        _p3KlineCache.TryAdd(stock.Code, klineJson);
                                    }
                                    break;
                                }
                            }
                        }
                        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                        if (i < 5) await Task.Delay(500 * i, cancellationToken);
                    }
                    if (networkFatal || cancellationToken.IsCancellationRequested) return;
                }

                if (Interlocked.Increment(ref p3SampleCount) <= 2)
                {
                    ReportLog(progress, $"📝 [P3抽样] {stock.Name}({stock.Code}) K线数据片段:\n{(klineJson != null && klineJson.Length > 100 ? klineJson.Substring(0, 100) + "..." : klineJson)}");
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
            catch (Exception ex) { Serilog.Log.Warning(ex, "捕获到未处理异常"); 
                ReportLog(progress, $"❌ P3核验异常 [{stock.Name}]: {ex.Message}");
            }
            finally
            {
                int c = Interlocked.Increment(ref p3Completed);
                progress?.Report(new SparrowLegacyScanReport { ProgressValue = c, P3Winners = _p3Winners.Count });
                semaphore.Release();
            }
        }));

        ReportLog(progress, $"\n✅ P3处理完毕。(网络抓取: {p3NetworkHit} 次, 内存闪查: {p3CacheHit} 次)");

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
            long ts = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            string url = $"https://75.push2.eastmoney.com/api/qt/stock/trends2/get?fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58&ut=fa5fd1943c7b386f172d6893dbfba10b&iscr=0&ndays=1&secid={secid}&_={ts}";
            string text = await NetworkHelper.GetDataAsync(url);
            if (string.IsNullOrWhiteSpace(text)) return false;
            
            using JsonDocument jsonDocument = JsonDocument.Parse(text);
            if (jsonDocument.RootElement.TryGetProperty("data", out var value2) && value2.TryGetProperty("trends", out var value3))
            {
                var list = value3.EnumerateArray().Select(x => x.GetString()).ToList();
                if (list.Count < 5) return false;
                
                string[] array = list.Last().Split(',');
                double current = double.Parse(array[2]);
                double avg = double.Parse(array[7]);
                double oldAvg = double.Parse(list[list.Count - 5].Split(',')[7]);
                return current < avg && avg < oldAvg;
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return false;
    }

    private bool ParseSnapshot(string json, out double risePct, out double outerVol, out double innerVol, out double amount, out string rawJson)
    {
        risePct = outerVol = innerVol = amount = 0.0;
        rawJson = "";
        if (string.IsNullOrWhiteSpace(json)) return false;
        
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(json);
            JsonElement rootElement = jsonDocument.RootElement;
            JsonElement jsonElement = rootElement.ValueKind == JsonValueKind.Array ? rootElement : 
                (rootElement.TryGetProperty("data", out var value) ? 
                    (value.ValueKind == JsonValueKind.Array ? value : (value.TryGetProperty("list", out var value2) ? value2 : (value.TryGetProperty("List", out var value3) ? value3 : default))) 
                    : default);
                    
            if (jsonElement.ValueKind == JsonValueKind.Array && jsonElement.GetArrayLength() > 0)
            {
                JsonElement jsonElement2 = jsonElement[0];
                rawJson = jsonElement2.GetRawText();
                if (!jsonElement2.TryGetProperty("K", out var value4)) return false;
                
                double close = value4.GetProperty("Close").GetDouble() / 1000.0;
                double preClose = value4.TryGetProperty("Last", out var value5) ? value5.GetDouble() / 1000.0 : 
                                 (value4.TryGetProperty("PreClose", out var value6) ? value6.GetDouble() / 1000.0 : 0.0);
                                 
                if (preClose > 0) risePct = (close - preClose) / preClose * 100.0;
                
                outerVol = jsonElement2.TryGetProperty("Wp", out var value7) ? value7.GetDouble() : 
                          (jsonElement2.TryGetProperty("OuterVolume", out var value8) ? value8.GetDouble() : 
                          (jsonElement2.TryGetProperty("OuterDisc", out var value9) ? value9.GetDouble() : 0.0));
                          
                innerVol = jsonElement2.TryGetProperty("Np", out var value10) ? value10.GetDouble() : 
                          (jsonElement2.TryGetProperty("InnerVolume", out var value11) ? value11.GetDouble() : 
                          (jsonElement2.TryGetProperty("InsideDish", out var value12) ? value12.GetDouble() : 0.0));
                          
                amount = jsonElement2.TryGetProperty("Amount", out var value13) ? value13.GetDouble() : 
                        (jsonElement2.TryGetProperty("TotalAmount", out var value14) ? value14.GetDouble() : 0.0);
                        
                return close > 0;
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
            using JsonDocument jsonDocument = JsonDocument.Parse(json);
            JsonElement rootElement = jsonDocument.RootElement;
            JsonElement jsonElement = rootElement.ValueKind == JsonValueKind.Array ? rootElement : default;
            
            if (jsonElement.ValueKind == JsonValueKind.Undefined && TryGetPropertyIgnoreCase(rootElement, "data", out var value))
            {
                jsonElement = value.ValueKind == JsonValueKind.Array ? value : 
                             (TryGetPropertyIgnoreCase(value, "list", out var value2) ? value2 : 
                             (TryGetPropertyIgnoreCase(value, "klines", out var value3) ? value3 : default));
            }
            
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                var list2 = jsonElement.EnumerateArray().ToList();
                for (int i = list2.Count - 1; i >= 0; i--)
                {
                    if (TryGetPropertyIgnoreCase(list2[i], "Close", out var value4))
                    {
                        list.Add(value4.GetDouble() / 1000.0);
                    }
                }
                if (list.Count > 0)
                {
                    latestPrice = list[0];
                    return list;
                }
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return list;
    }

    private bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty item in element.EnumerateObject())
            {
                if (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = item.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private async Task OutputResultsAsync(List<(string Code, string Name, string Reason)> results, IProgress<SparrowLegacyScanReport> progress)
    {
        string text = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(text)) text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        if (!Directory.Exists(text)) Directory.CreateDirectory(text);
        
        string filePath = Path.Combine(text, $"麻雀池_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        using (StreamWriter writer = new StreamWriter(filePath, append: false, Encoding.UTF8))
        {
            await writer.WriteLineAsync($"【麻雀战法 4.0】选股结果\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
            foreach (var item in results)
            {
                await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
            }
        }
        ReportLog(progress, $"\n📁 结果已存至: {filePath}");
    }
}
