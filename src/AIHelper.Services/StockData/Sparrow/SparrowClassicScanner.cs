using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
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
    private readonly ConcurrentDictionary<string, bool> _p2Processed = new();
    private readonly ConcurrentDictionary<string, (string Code, string Name)> _p2Survivors = new();
    private readonly ConcurrentDictionary<string, string> _p3KlineCache = new();
    private readonly ConcurrentDictionary<string, SparrowClassicCandidate> _p3Winners = new();

    public SparrowClassicScanner(IStockDataProvider dataProvider)
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
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

        if (!parameters.UseCache)
        {
            _p2Processed.Clear();
            _p2Survivors.Clear();
            _p3KlineCache.Clear();
        }
        _p3Winners.Clear();

        ReportLog(progress, $"🦅 [Sparrow Classic] 开始扫描。Strategy = {SparrowClassicCandidate.StrategyName}，初始标的: {classicPool.Count} 只");

        if (parameters.MacroDef && _p2Processed.IsEmpty)
        {
            ReportLog(progress, "🛡️ [阶段1] 检测大盘宏观安全度...");
            bool shWeak = await CheckIndexWeaknessAsync("sh000001", cancellationToken);
            bool csi1000Weak = await CheckIndexWeaknessAsync("sh000852", cancellationToken);
            if (shWeak && csi1000Weak)
            {
                ReportLog(progress, "❌ [Sparrow Classic][熔断] 上证指数与中证1000同时恶化，空仓防御！", true);
                return new List<SparrowClassicCandidate>();
            }
            ReportLog(progress, "✅ [Sparrow Classic][第一阶段通过] 允许开启个股海选。");
        }

        var p2Pending = classicPool.Where(stock => !_p2Processed.ContainsKey(stock.Code)).ToList();
        if (p2Pending.Count > 0)
        {
            ReportLog(progress, $"\n🌪️ [Sparrow Classic][阶段2] 快照扫描 (待处理:{p2Pending.Count} / 总计:{classicPool.Count})...");
            progress?.Report(new SparrowClassicScanReport { ProgressMax = classicPool.Count, ProgressValue = _p2Processed.Count });
            int batchQuoteLoaded = 0;
            int quoteDataUnavailable = 0;
            int coarseSurvivors = 0;
            int outerInnerValid = 0;
            int outerInnerUnavailable = 0;
            int volRatioPassed = 0;

            var batches = p2Pending.Select((stock, index) => new { stock, index })
                .GroupBy(item => item.index / 50)
                .Select(group => group.Select(item => item.stock).ToList())
                .ToList();

            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(8, parameters.MaxConcurrency)));
            await Task.WhenAll(batches.Select(async batch =>
            {
                bool entered = false;
                try
                {
                    await semaphore.WaitAsync(cancellationToken);
                    entered = true;
                    string codes = string.Join(",", batch.Select(stock => stock.Code));
                    StockDataResult response = await _dataProvider.GetDataAsync(
                        StockDataRequest.Parse("/api/quote?code=" + codes), cancellationToken);
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

                        _p2Processed.TryAdd(stock.Code, true);
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
                            _p2Survivors[stock.Code] = stock;
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
                            ProgressValue = Math.Min(classicPool.Count, _p2Processed.Count),
                            P2Survivors = _p2Survivors.Count
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
            ReportLog(progress, $"\n♻️ [Sparrow Classic][盘口缓存] 阶段2完成，幸存者: {_p2Survivors.Count} 只");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 [Sparrow Classic] 已暂停，阶段2进度: {_p2Processed.Count}/{classicPool.Count}。", true);
            return new List<SparrowClassicCandidate>();
        }

        var poolCodes = classicPool.Select(stock => stock.Code).ToHashSet(StringComparer.Ordinal);
        var p2List = _p2Survivors.Values.Where(stock => poolCodes.Contains(stock.Code)).ToList();
        ReportLog(progress, $"\n🔪 [Sparrow Classic][阶段2结束] 进入阶段3: {p2List.Count} 只");
        ReportLog(progress, $"\n🔬 [Sparrow Classic][阶段3] K线核验开始 (标的数:{p2List.Count})...");
        progress?.Report(new SparrowClassicScanReport { ProgressMax = p2List.Count, ProgressValue = 0 });

        int p3Completed = 0;
        using var klineSemaphore = new SemaphoreSlim(Math.Max(1, Math.Min(32, parameters.MaxConcurrency * 4)));
        await Task.WhenAll(p2List.Select(async stock =>
        {
            bool entered = false;
            try
            {
                await klineSemaphore.WaitAsync(cancellationToken);
                entered = true;
                string? klineJson = null;
                if (!_p3KlineCache.TryGetValue(stock.Code, out klineJson))
                {
                    StockDataResult response = await _dataProvider.GetDataAsync(
                        StockDataRequest.Parse("/api/kline-all?code=" + stock.Code + "&type=day&limit=65"),
                        cancellationToken);
                    if (response.Success && !string.IsNullOrWhiteSpace(response.Json))
                    {
                        klineJson = response.Json;
                        _p3KlineCache.TryAdd(stock.Code, klineJson);
                    }
                }

                if (string.IsNullOrWhiteSpace(klineJson))
                {
                    return;
                }

                List<double> closes = ParseKlineClosesNewestFirst(klineJson, out double latestPrice);
                SparrowClassicTechnicalResult technical = SparrowClassicRuleEvaluator.Evaluate(closes, latestPrice, parameters);
                if (!technical.Passed)
                {
                    return;
                }

                var candidate = new SparrowClassicCandidate
                {
                    Code = stock.Code,
                    Name = stock.Name,
                    Reason = $"多头 黏合度:{technical.Adhesion * 100:F2}%"
                };
                _p3Winners[stock.Code] = candidate;
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
                    progress?.Report(new SparrowClassicScanReport { ProgressValue = completed, P3Winners = _p3Winners.Count });
                    klineSemaphore.Release();
                }
            }
        }));

        if (cancellationToken.IsCancellationRequested)
        {
            ReportLog(progress, $"\n🛑 [Sparrow Classic] 已暂停，阶段3进度: {p3Completed}/{p2List.Count}。", true);
            return new List<SparrowClassicCandidate>();
        }

        List<SparrowClassicCandidate> results = _p3Winners.Values.OrderBy(candidate => candidate.Code).ToList();
        ReportLog(progress, $"\n🏆 [Sparrow Classic] 漏斗完成，共入围 {results.Count} 只。Strategy = {SparrowClassicCandidate.StrategyName}");
        if (results.Count > 0)
        {
            await OutputResultsAsync(results, progress);
        }
        return results;
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

    private async Task<bool> CheckIndexWeaknessAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            StockDataResult response = await _dataProvider.GetDataAsync(
                StockDataRequest.Parse("/api/index?code=" + code + "&limit=5"), cancellationToken);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Json))
            {
                return false;
            }

            List<double> closes = ParseKlineClosesOldestFirst(response.Json);
            if (closes.Count < 5)
            {
                return false;
            }

            double current = closes[^1];
            double latestAverage = closes.TakeLast(5).Average();
            double oldAverage = closes.Take(5).Average();
            return current < latestAverage && latestAverage < oldAverage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sparrow Classic market check failed for {Code}", code);
            return false;
        }
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

        string filePath = Path.Combine(directory, $"麻雀池_Classic_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        await using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
        await writer.WriteLineAsync($"【Sparrow Classic】选股结果\nStrategy: {SparrowClassicCandidate.StrategyName}\n生成时间: {DateTime.Now}\n入围数量: {results.Count}\n=======================================");
        foreach (SparrowClassicCandidate item in results)
        {
            await writer.WriteLineAsync($"代码: {item.Code} \t名称: {item.Name} \t策略: {item.Strategy} \t说明: {item.Reason}");
        }
        ReportLog(progress, $"\n📁 [Sparrow Classic] 结果已存至: {filePath}");
    }
}
