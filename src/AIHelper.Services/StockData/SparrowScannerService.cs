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

namespace AIHelper.Services.StockData;

public class SparrowScanReport
{
    public string LogMessage { get; set; }
    public bool IsHighlight { get; set; }
    public int? ProgressMax { get; set; }
    public int? ProgressValue { get; set; }
}

public class SparrowScannerService
{
    private readonly EastMoneySpiderService _spider;
    private readonly ConcurrentDictionary<string, string> _p2QuoteCache_DC = new();
    private readonly ConcurrentDictionary<string, string> _p3KlineCache_DC = new();
    private readonly ConcurrentDictionary<string, (string Name, string Reason)> _p3Winners_DC = new();

    public SparrowScannerService(EastMoneySpiderService spider)
    {
        _spider = spider;
    }

    public async Task<List<(string Code, string Name, string Reason)>> ScanAsync(
        List<(string Code, string Name)> targetPool,
        SparrowScanParameters parameters,
        IProgress<SparrowScanReport> progress,
        CancellationToken ct)
    {
        if (!parameters.UseCache)
        {
            _p2QuoteCache_DC.Clear();
            _p3KlineCache_DC.Clear();
        }
        _p3Winners_DC.Clear();

        ReportLog(progress, $"🦅 [麻雀-东财精准版] 引擎点火！初始标的: {targetPool.Count} 只");

        double shIndexPctChg = 0;
        if (parameters.MacroDef)
        {
            var indexResult = await CheckIndexWeakness();
            shIndexPctChg = indexResult.ShIndexPctChg;
            if (indexResult.IsWeak)
            {
                ReportLog(progress, "❌ [熔断] 大盘环境恶化，空仓防御！", true);
                return new List<(string, string, string)>();
            }
            ReportLog(progress, $"✅ [第一阶段通过] 上证今日涨幅: {shIndexPctChg:F2}%, 已设为 RPS 参照基准。");
        }

        // Phase 2
        var p2Missing = targetPool.Where(s => !_p2QuoteCache_DC.ContainsKey(s.Code)).ToList();
        if (p2Missing.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段2] 东财直连拉取盘口快照 (待下载:{p2Missing.Count} 只, 并发:{parameters.MaxConcurrency})...");
            int p2Downloaded = 0;
            progress.Report(new SparrowScanReport { ProgressMax = p2Missing.Count, ProgressValue = 0 });
            
            using var semaphore = new SemaphoreSlim(Math.Min(8, parameters.MaxConcurrency));
            await Task.WhenAll(p2Missing.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            string emSecId = stock.Code.StartsWith("6") ? "1." + stock.Code : "0." + stock.Code;
                            await Task.Delay(new Random().Next(50, 200), ct);
                            string text = await _spider.FetchEastMoneyQuoteAsync(emSecId);
                            if (!string.IsNullOrWhiteSpace(text) && text.Contains("data"))
                            {
                                _p2QuoteCache_DC.TryAdd(stock.Code, text);
                                break;
                            }
                        }
                        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                        await Task.Delay(500, ct);
                    }
                }
                finally
                {
                    int c = Interlocked.Increment(ref p2Downloaded);
                    if (c % 50 == 0 || c == p2Missing.Count)
                    {
                        progress.Report(new SparrowScanReport { ProgressValue = c });
                    }
                    semaphore.Release();
                }
            }));
        }

        if (ct.IsCancellationRequested) return new List<(string, string, string)>();

        ReportLog(progress, $"\n🧠 正在根据当前参数对 {targetPool.Count} 只股票进行极速盘口核验...");
        var p2List = new List<(string Code, string Name)>();
        foreach (var item in targetPool)
        {
            if (_p2QuoteCache_DC.TryGetValue(item.Code, out var value) && 
                ParseEastMoneySnapshot(value, out var risePct, out var outerVol, out var innerVol, out var amount, out var turnover) && 
                !(risePct < parameters.MinRise) && !(risePct > parameters.MaxRise) && 
                !(amount < parameters.MinAmount) && !(outerVol <= 0.0) && !(innerVol <= 0.0) && 
                !(outerVol <= innerVol * parameters.VolRatio) && 
                (!(turnover > 0.0) || (!(turnover < parameters.MinTurnover) && !(turnover > parameters.MaxTurnover))))
            {
                p2List.Add((item.Code, item.Name));
            }
        }
        ReportLog(progress, $"✅ 盘口过滤完毕，剩余标的: {p2List.Count} 只");

        if (p2List.Count == 0) return new List<(string, string, string)>();

        // Phase 3
        var p3Missing = p2List.Where(s => !_p3KlineCache_DC.ContainsKey(s.Code)).ToList();
        if (p3Missing.Count > 0)
        {
            ReportLog(progress, $"\n🔭 [阶段3] 东财网络拉取 K 线 (待下载:{p3Missing.Count} 只)...");
            int p3Downloaded = 0;
            progress.Report(new SparrowScanReport { ProgressMax = p3Missing.Count, ProgressValue = 0 });

            using var semaphore = new SemaphoreSlim(Math.Min(4, parameters.MaxConcurrency));
            await Task.WhenAll(p3Missing.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    for (int i = 1; i <= 3; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            string emSecId = stock.Code.StartsWith("6") ? "1." + stock.Code : "0." + stock.Code;
                            await Task.Delay(new Random().Next(200, 600), ct);
                            string text = await _spider.FetchEastMoneyKLineAsync(emSecId);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                _p3KlineCache_DC.TryAdd(stock.Code, text);
                                break;
                            }
                        }
                        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                        await Task.Delay(500, ct);
                    }
                }
                finally
                {
                    int c = Interlocked.Increment(ref p3Downloaded);
                    if (c % 20 == 0 || c == p3Missing.Count)
                    {
                        progress.Report(new SparrowScanReport { ProgressValue = c });
                    }
                    semaphore.Release();
                }
            }));
        }

        if (ct.IsCancellationRequested) return new List<(string, string, string)>();

        ReportLog(progress, $"\n🧠 正在根据当前参数对 {p2List.Count} 只股票进行 K 线深度核验...");
        progress.Report(new SparrowScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });
        int memCheckCount = 0;

        foreach (var item in p2List)
        {
            memCheckCount++;
            if (memCheckCount % 10 == 0 || memCheckCount == p2List.Count)
            {
                progress.Report(new SparrowScanReport { ProgressValue = memCheckCount });
            }

            if (!_p3KlineCache_DC.TryGetValue(item.Code, out var value)) continue;

            var list = ParseEastMoneyKline(value, out var latestPrice, out var latestPctChg);
            if (list.Count < 60)
            {
                // Optionally log probe death
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
                    _p3Winners_DC[item.Code] = (item.Name, reason);
                    ReportLog(progress, $"🎯 [入围] {item.Name}({item.Code}) {reason}");
                }
            }
        }

        ReportLog(progress, $"\n🏆 漏斗完成！共诞生长短腿战斗机 {_p3Winners_DC.Count} 只！");

        var results = _p3Winners_DC.Select(kvp => (kvp.Key, kvp.Value.Name, kvp.Value.Reason)).ToList();
        await OutputResultsToFileAsync(results);
        return results;
    }

    private void ReportLog(IProgress<SparrowScanReport> progress, string msg, bool isHighlight = false)
    {
        progress?.Report(new SparrowScanReport { LogMessage = msg, IsHighlight = isHighlight });
    }

    private async Task<(bool IsWeak, double ShIndexPctChg)> CheckIndexWeakness()
    {
        double shIndexPctChg = 0.0;
        try
        {
            string text = await _spider.FetchEastMoneyQuoteAsync("1.000001");
            if (string.IsNullOrWhiteSpace(text)) return (false, 0.0);
            
            int num = text.IndexOf('{');
            int num2 = text.LastIndexOf('}');
            if (num >= 0 && num2 > num)
            {
                text = text.Substring(num, num2 - num + 1);
            }
            using JsonDocument jsonDocument = JsonDocument.Parse(text);
            if (jsonDocument.RootElement.TryGetProperty("data", out var value))
            {
                shIndexPctChg = value.TryGetProperty("f170", out var value2) ? value2.GetDouble() : 0.0;
                return (shIndexPctChg <= -2.5, shIndexPctChg);
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return (false, 0.0);
    }

    private bool ParseEastMoneySnapshot(string json, out double risePct, out double outerVol, out double innerVol, out double amount, out double turnover)
    {
        risePct = outerVol = innerVol = amount = turnover = 0.0;
        if (string.IsNullOrWhiteSpace(json)) return false;

        int num = json.IndexOf('{');
        int num2 = json.LastIndexOf('}');
        if (num >= 0 && num2 > num)
        {
            json = json.Substring(num, num2 - num + 1);
            try
            {
                using JsonDocument jsonDocument = JsonDocument.Parse(json);
                if (jsonDocument.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object)
                {
                    if ((value.TryGetProperty("f43", out var value2) ? (value2.GetDouble() / 100.0) : 0.0) <= 0.001)
                    {
                        return false;
                    }
                    risePct = value.TryGetProperty("f170", out var v1) ? v1.GetDouble() : 0.0;
                    amount = value.TryGetProperty("f48", out var v2) ? v2.GetDouble() : 0.0;
                    outerVol = value.TryGetProperty("f49", out var v3) ? v3.GetDouble() : 0.0;
                    innerVol = value.TryGetProperty("f161", out var v4) ? v4.GetDouble() : 0.0;
                    turnover = value.TryGetProperty("f168", out var v5) ? v5.GetDouble() : 0.0;
                    return true;
                }
            }
            catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        }
        return false;
    }

    private List<double> ParseEastMoneyKline(string json, out double latestPrice, out double latestPctChg)
    {
        List<double> list = new List<double>();
        latestPrice = 0.0;
        latestPctChg = 0.0;
        if (string.IsNullOrWhiteSpace(json)) return list;

        int num = json.IndexOf('{');
        int num2 = json.LastIndexOf('}');
        if (num >= 0 && num2 > num)
        {
            json = json.Substring(num, num2 - num + 1);
            try
            {
                using JsonDocument jsonDocument = JsonDocument.Parse(json);
                if (jsonDocument.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object)
                {
                    if (value.TryGetProperty("klines", out var value2) && value2.ValueKind == JsonValueKind.Array)
                    {
                        List<string> list2 = value2.EnumerateArray().Select(x => x.GetString()).ToList();
                        for (int num3 = list2.Count - 1; num3 >= 0; num3--)
                        {
                            try
                            {
                                string[] array = list2[num3].Split(',');
                                if (array.Length >= 9)
                                {
                                    if (double.TryParse(array[2], out var result) && result > 0.0)
                                    {
                                        list.Add(result);
                                    }
                                    if (num3 == list2.Count - 1)
                                    {
                                        latestPrice = result;
                                        double.TryParse(array[8], out latestPctChg);
                                    }
                                }
                            }
                            catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                        }
                    }
                }
            }
            catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        }
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

        string fileName = $"东财麻雀池_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        string fullPath = Path.Combine(path, fileName);

        using StreamWriter writer = new StreamWriter(fullPath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【麻雀战法 4.5 - 东财版】选股结果\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (var item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
        }
    }
}
