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

#pragma warning disable CS8604
#pragma warning disable CS8604
namespace AIHelper.Services.StockData;

public class TurtleScannerService
{
    private readonly ConcurrentDictionary<string, string> _p2QuoteCache = new();
    private readonly ConcurrentDictionary<string, string> _p3KlineCache = new();
    private readonly ConcurrentDictionary<string, (string Name, string Reason)> _p3Winners = new();

    public async Task<List<(string Code, string Name, string Reason)>> ScanAsync(
        List<(string Code, string Name)> targetPool,
        TurtleScanParameters parameters,
        IProgress<TurtleScanReport> progress,
        CancellationToken cancellationToken)
    {
        if (!parameters.UseCache)
        {
            _p2QuoteCache.Clear();
            _p3KlineCache.Clear();
        }
        _p3Winners.Clear();

        ReportLog(progress, $"🌊 [海龟法则] 引擎点火！初始标的: {targetPool.Count} 只");

        if (parameters.MacroDef)
        {
            var indexResult = await CheckIndexWeakness();
            if (indexResult.IsWeak)
            {
                ReportLog(progress, "❌ [熔断] 系统判定大盘环境极差，不符合海龟顺势建仓条件！", true);
                return new List<(string, string, string)>();
            }
        }

        // Phase 2
        var p2Missing = targetPool.Where(s => !_p2QuoteCache.ContainsKey(s.Code)).ToList();
        if (p2Missing.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段 2] 网络拉取盘口快照 (待下载:{p2Missing.Count} 只, 并发:{parameters.MaxConcurrency})...");
            int p2Downloaded = 0;
            progress?.Report(new TurtleScanReport { ProgressMax = p2Missing.Count, ProgressValue = 0 });
            
            using var semaphore2 = new SemaphoreSlim(parameters.MaxConcurrency);
            await Task.WhenAll(p2Missing.Select(async stock =>
            {
                await semaphore2.WaitAsync();
                try
                {
                    for (int retry = 0; retry < 3; retry++)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        string text = await NetworkHelper.GetDataAsync("/api/quote?code=" + stock.Code);
                        if (!string.IsNullOrWhiteSpace(text) && !text.Contains("\"code\":-1"))
                        {
                            _p2QuoteCache.TryAdd(stock.Code, text);
                            break;
                        }
                        await Task.Delay(500, cancellationToken);
                    }
                }
                catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                finally
                {
                    int c2 = Interlocked.Increment(ref p2Downloaded);
                    if (c2 % 50 == 0 || c2 == p2Missing.Count)
                    {
                        progress?.Report(new TurtleScanReport { ProgressValue = c2 });
                    }
                    semaphore2.Release();
                }
            }));
        }

        if (cancellationToken.IsCancellationRequested) return new List<(string, string, string)>();

        var p2List = new List<(string Code, string Name)>();
        foreach (var item in targetPool)
        {
            if (_p2QuoteCache.TryGetValue(item.Code, out var value) && 
                ParseSnapshot(value, out var amount, out _) && 
                amount >= parameters.MinAmount)
            {
                p2List.Add((item.Code, item.Name));
            }
        }

        if (p2List.Count == 0) return new List<(string, string, string)>();

        // Phase 3
        var p3Missing = p2List.Where(s => !_p3KlineCache.ContainsKey(s.Code)).ToList();
        if (p3Missing.Count > 0)
        {
            ReportLog(progress, $"\n🔬 [阶段 3] 东财拉取 K 线数据 (待下载:{p3Missing.Count} 只)...");
            int p3Downloaded = 0;
            progress?.Report(new TurtleScanReport { ProgressMax = p3Missing.Count, ProgressValue = 0 });

            using var semaphore = new SemaphoreSlim(parameters.MaxConcurrency);
            await Task.WhenAll(p3Missing.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    for (int j = 1; j <= 3; j++)
                    {
                        if (cancellationToken.IsCancellationRequested) break;
                        try
                        {
                            string prefix = stock.Code.StartsWith("6") ? "1." : "0.";
                            string secid = prefix + stock.Code;
                            string url = $"https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={secid}&klt=101&fqt=1&lmt=100&fields2=f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61";
                            string value = await NetworkHelper.GetDataAsync(url);
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                _p3KlineCache.TryAdd(stock.Code, value);
                                break;
                            }
                        }
                        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                        await Task.Delay(300, cancellationToken);
                    }
                }
                finally
                {
                    int c = Interlocked.Increment(ref p3Downloaded);
                    if (c % 20 == 0 || c == p3Missing.Count)
                    {
                        progress?.Report(new TurtleScanReport { ProgressValue = c });
                    }
                    semaphore.Release();
                }
            }));
        }

        if (cancellationToken.IsCancellationRequested) return new List<(string, string, string)>();

        ReportLog(progress, $"\n🧠 正在根据海龟法则对 {p2List.Count} 只股票进行极速突破计算...");
        int location = 0;
        progress?.Report(new TurtleScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });
        int memCheckCount = 0;

        foreach (var item in p2List)
        {
            memCheckCount++;
            if (memCheckCount % 10 == 0 || memCheckCount == p2List.Count)
            {
                progress?.Report(new TurtleScanReport { ProgressValue = memCheckCount });
            }

            if (!_p3KlineCache.TryGetValue(item.Code, out var klineData)) continue;

            var list = ParseEastMoneyKlineForTurtle(klineData);
            if (list.Count < parameters.N2 + 5)
            {
                if (Interlocked.Increment(ref location) <= 5)
                {
                    ReportLog(progress, $"[探针-K线不足] {item.Name}: 有效K线不足计算突破。");
                }
                continue;
            }

            var lastK = list.Last();
            if (lastK.Turnover < parameters.MinTurnover || lastK.Turnover > parameters.MaxTurnover) continue;

            var sourceN1 = list.Skip(list.Count - 1 - parameters.N1).Take(parameters.N1).ToList();
            var sourceN2 = list.Skip(list.Count - 1 - parameters.N2).Take(parameters.N2).ToList();

            double highN1 = sourceN1.Max(k => k.High);
            double highN2 = sourceN2.Max(k => k.High);

            bool breakN1 = lastK.Close > highN1;
            bool breakN2 = lastK.Close > highN2;

            if (breakN1 || breakN2)
            {
                double sumTr = 0.0;
                int nAtr = 20;
                for (int i = list.Count - nAtr; i < list.Count; i++)
                {
                    double high = list[i].High;
                    double low = list[i].Low;
                    double preClose = list[i - 1].Close;
                    double tr = Math.Max(high - low, Math.Max(Math.Abs(high - preClose), Math.Abs(low - preClose)));
                    sumTr += tr;
                }
                double atr = sumTr / nAtr;
                double atrPct = atr / lastK.Close * 100.0;

                string breakType = !breakN2 ? $"[{parameters.N1}日短突破]" : $"[{parameters.N2}日大突破]";
                string reason = $"{breakType} N值(ATR):{atr:F2}({atrPct:F1}%)";
                _p3Winners[item.Code] = (item.Name, reason);

                ReportLog(progress, $"🌊 {breakType} {item.Name}({item.Code}) 收盘:{lastK.Close:F2} 突破前高:{(breakN2 ? highN2 : highN1):F2}");
            }
        }

        ReportLog(progress, $"\n🏆 海龟出海！共发现破位上行标的 {_p3Winners.Count} 只！");
        var results = _p3Winners.Select(kvp => (kvp.Key, kvp.Value.Name, kvp.Value.Reason)).ToList();
        
        if (results.Count > 0)
        {
            await OutputResultsAsync(results);
        }

        return results;
    }

    private void ReportLog(IProgress<TurtleScanReport> progress, string msg, bool isHighlight = false)
    {
        progress?.Report(new TurtleScanReport { LogMessage = msg, IsHighlight = isHighlight });
    }

    private async Task<(bool IsWeak, double ShIndexPctChg)> CheckIndexWeakness()
    {
        try
        {
            string text = await NetworkHelper.GetDataAsync("https://push2.eastmoney.com/api/qt/stock/get?secid=1.000001&fields=f43,f169,f170,f171");
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
                double pctChg = value.TryGetProperty("f170", out var value2) ? value2.GetDouble() : 0.0;
                return (pctChg <= -2.0, pctChg);
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return (false, 0.0);
    }

    private bool ParseSnapshot(string json, out double amount, out double turnover)
    {
        amount = turnover = 0.0;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using JsonDocument jsonDocument = JsonDocument.Parse(json);
            JsonElement rootElement = jsonDocument.RootElement;
            JsonElement jsonElement = rootElement.ValueKind == JsonValueKind.Array ? rootElement : 
                (rootElement.TryGetProperty("data", out var value) ? 
                    (value.ValueKind == JsonValueKind.Array ? value : (value.TryGetProperty("list", out var value2) ? value2 : default)) 
                    : default);
            
            if (jsonElement.ValueKind == JsonValueKind.Array && jsonElement.GetArrayLength() > 0)
            {
                JsonElement jsonElement2 = jsonElement[0];
                if (!jsonElement2.TryGetProperty("K", out var value3)) return false;
                if (value3.GetProperty("Close").GetDouble() / 1000.0 <= 0.001) return false;
                
                double num = jsonElement2.TryGetProperty("Amount", out var value4) ? value4.GetDouble() : 
                            (jsonElement2.TryGetProperty("TotalAmount", out var value5) ? value5.GetDouble() : 0.0);
                amount = num < 0.001 ? 0.0 : num;
                return true;
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return false;
    }

    private List<(double High, double Low, double Close, double Turnover)> ParseEastMoneyKlineForTurtle(string json)
    {
        var list = new List<(double, double, double, double)>();
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
                        foreach (JsonElement item in value2.EnumerateArray())
                        {
                            try
                            {
                                string[] array = item.GetString()!.Split(',');
                                if (array.Length >= 11)
                                {
                                    double.TryParse(array[3], out var result);
                                    double.TryParse(array[4], out var result2);
                                    double.TryParse(array[2], out var result3);
                                    double.TryParse(array[10], out var result4);
                                    if (result3 > 0.0)
                                    {
                                        list.Add((result, result2, result3, result4));
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

    private async Task OutputResultsAsync(List<(string Code, string Name, string Reason)> results)
    {
        string text = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        }
        if (!Directory.Exists(text))
        {
            Directory.CreateDirectory(text);
        }
        string path = Path.Combine(text, $"海龟突破_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        using (StreamWriter writer = new StreamWriter(path, append: false, Encoding.UTF8))
        {
            await writer.WriteLineAsync($"【海龟法则】突破选股\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
            foreach (var item in results)
            {
                await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
            }
        }
    }
}
