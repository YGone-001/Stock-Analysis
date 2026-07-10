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
namespace AIHelper.Services.StockData;

public class TurtleScannerService
{
    private readonly IStockDataProvider _dataProvider;
    private readonly ConcurrentDictionary<string, string> _p2QuoteCache = new();
    private readonly ConcurrentDictionary<string, string> _p3KlineCache = new();
    private readonly ConcurrentDictionary<string, (string Name, string Reason)> _p3Winners = new();

    public TurtleScannerService(IStockDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

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

        ReportLog(progress, $"🌊 [海龟法则-高速版] 引擎点火！初始标的: {targetPool.Count} 只");

        if (parameters.MacroDef)
        {
            var indexResult = await CheckIndexWeakness();
            if (indexResult.IsWeak)
            {
                ReportLog(progress, "❌ [熔断] 系统判定大盘环境极差，不符合海龟顺势建仓条件！", true);
                return new List<(string, string, string)>();
            }
        }

        var p2Missing = targetPool.Where(s => !_p2QuoteCache.ContainsKey(s.Code)).ToList();
        if (p2Missing.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [阶段 2] 极速网关并发拉取盘口快照 (待下载:{p2Missing.Count} 只)...");
            int p2Downloaded = 0;
            progress?.Report(new TurtleScanReport { ProgressMax = p2Missing.Count, ProgressValue = 0 });
            
            var batches = p2Missing.Select((x, i) => new { Index = i, Value = x })
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
                                            _p2QuoteCache.TryAdd(code, item.GetRawText());
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Log.Error(ex, "Failed to parse batch quote JSON"); }
                    }
                }
                catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                finally
                {
                    int c2 = Interlocked.Add(ref p2Downloaded, batch.Count);
                    progress?.Report(new TurtleScanReport { ProgressValue = c2 });
                    semaphore2.Release();
                }
            }));
        }

        if (cancellationToken.IsCancellationRequested) return new List<(string, string, string)>();

        var p2List = new List<(string Code, string Name)>();
        foreach (var item in targetPool)
        {
            if (_p2QuoteCache.TryGetValue(item.Code, out var value) && 
                ParseSnapshot(value, out var amount, out var turnover) && 
                amount >= parameters.MinAmount && turnover >= parameters.MinTurnover && turnover <= parameters.MaxTurnover)
            {
                p2List.Add((item.Code, item.Name));
            }
        }

        if (p2List.Count == 0) return new List<(string, string, string)>();

        var p3Missing = p2List.Where(s => !_p3KlineCache.ContainsKey(s.Code)).ToList();
        if (p3Missing.Count > 0)
        {
            ReportLog(progress, $"\n🔬 [阶段 3] 极速网关拉取 K 线数据 (待下载:{p3Missing.Count} 只)...");
            int p3Downloaded = 0;
            progress?.Report(new TurtleScanReport { ProgressMax = p3Missing.Count, ProgressValue = 0 });

            using var semaphore = new SemaphoreSlim(Math.Min(32, parameters.MaxConcurrency * 4));
            await Task.WhenAll(p3Missing.Select(async stock =>
            {
                await semaphore.WaitAsync();
                try
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    var req = StockDataRequest.Parse("/api/kline-all?code=" + stock.Code + "&limit=120");
                    var res = await _dataProvider.GetDataAsync(req, cancellationToken);
                    if (res.Success && !string.IsNullOrWhiteSpace(res.Json))
                    {
                        _p3KlineCache.TryAdd(stock.Code, res.Json);
                    }
                }
                catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
                finally
                {
                    int c = Interlocked.Increment(ref p3Downloaded);
                    if (c % 10 == 0 || c == p3Missing.Count)
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

            var list = ParseKlineForTurtle(klineData);
            if (list.Count < parameters.N2 + 5)
            {
                if (Interlocked.Increment(ref location) <= 5)
                {
                    ReportLog(progress, $"[探针-K线不足] {item.Name}: 有效K线不足计算突破。");
                }
                continue;
            }

            var lastK = list.Last();

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
            var req = StockDataRequest.Parse("/api/index?code=sh000001&limit=2");
            var res = await _dataProvider.GetDataAsync(req, CancellationToken.None);
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
                            double pctChg = (close - prevClose) / prevClose * 100.0;
                            return (pctChg <= -2.0, pctChg);
                        }
                    }
                }
            }
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return (false, 0.0);
    }

    private bool ParseSnapshot(string json, out double amount, out double turnover)
    {
        amount = turnover = 0.0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var item = doc.RootElement;
            if (item.TryGetProperty("K", out var kElem) && kElem.TryGetProperty("Close", out var closeElem))
            {
                double close = closeElem.GetDouble() / 1000.0;
                if (close <= 0.001) return false;
            }

            amount = item.TryGetProperty("Amount", out var a) ? a.GetDouble() : 0.0;
            turnover = item.TryGetProperty("Turnover", out var t) ? t.GetDouble() : 0.0;
            return true;
        }
        catch { }
        return false;
    }

    private List<(double High, double Low, double Close)> ParseKlineForTurtle(string json)
    {
        var list = new List<(double, double, double)>();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in dataArr.EnumerateArray())
                {
                    if (item.TryGetProperty("High", out var highElem) &&
                        item.TryGetProperty("Low", out var lowElem) &&
                        item.TryGetProperty("Close", out var closeElem))
                    {
                        double high = highElem.GetDouble() / 1000.0;
                        double low = lowElem.GetDouble() / 1000.0;
                        double close = closeElem.GetDouble() / 1000.0;
                        if (close > 0)
                        {
                            list.Add((high, low, close));
                        }
                    }
                }
            }
        }
        catch { }
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
        string path = Path.Combine(text, $"海龟突破高速版_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        using (StreamWriter writer = new StreamWriter(path, append: false, Encoding.UTF8))
        {
            await writer.WriteLineAsync($"【海龟法则高速版】突破选股\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
            foreach (var item in results)
            {
                await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t说明: {item.Reason}");
            }
        }
    }
}

