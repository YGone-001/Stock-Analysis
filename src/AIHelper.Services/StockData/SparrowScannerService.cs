using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
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
    private readonly IKlineService _klineService;
    private readonly IQuoteService _quoteService;
    private readonly SparrowMarketDataCache _klineCache;
    private readonly SparrowMarketRegimeService _marketRegimeService;
    private readonly SparrowBoardHeatService _boardHeatService;

    public SparrowMarketDataCache KlineCache => _klineCache;

    public SparrowScannerService(
        IStockDataProvider dataProvider,
        SparrowMarketDataCache? klineCache = null,
        IKlineService? klineService = null,
        IQuoteService? quoteService = null,
        SparrowMarketRegimeService? marketRegimeService = null,
        SparrowBoardHeatService? boardHeatService = null)
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _klineCache = klineCache ?? new SparrowMarketDataCache();
        _klineService = klineService ?? new KlineService(dataProvider);
        _quoteService = quoteService ?? new QuoteService(dataProvider);
        _marketRegimeService = marketRegimeService ?? new SparrowMarketRegimeService(dataProvider);
        _boardHeatService = boardHeatService ?? new SparrowBoardHeatService(dataProvider, _klineCache);
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
        CancellationToken ct,
        SparrowMomentumGateConfig? momentumGates = null,
        SparrowVolumeGateConfig? volumeGates = null,
        SparrowWeeklyGateConfig? weeklyGates = null,
        SparrowIntradayGateConfig? intradayGates = null)
    {
        // Scan-local session state: quotes are fresh per scan and never frozen across scans
        var quoteCache = new ConcurrentDictionary<string, SparrowQuoteData>(StringComparer.Ordinal);
        var p3Winners = new ConcurrentDictionary<string, SparrowV2Candidate>(StringComparer.Ordinal);

        ReportLog(progress, $"🦅 [麻雀-全景高速版] 引擎点火！初始标的: {targetPool.Count} 只");

        double? shIndexPctChg = null;
        if (parameters.MacroDef)
        {
            // P1 熔断：双指数分时弱势（与 Classic 对齐），数据缺失按历史策略 fail-open。
            SparrowMarketRegime regime = await _marketRegimeService.EvaluateAsync(forceRefresh: !parameters.UseCache, ct);
            if (regime.Defensive)
            {
                ReportLog(progress, "❌ [熔断] 大盘环境恶化（双指数弱势），空仓防御！", true);
                return new List<SparrowV2Candidate>();
            }

            // Alpha 基准：上证日涨跌幅，独立于熔断取数；缺失时显式记为 null（跳过 Alpha、AlphaMargin 置空）。
            shIndexPctChg = await GetShanghaiDailyPercentAsync(!parameters.UseCache, ct);
            ReportLog(progress, $"✅ [第一阶段通过] 大盘环境正常；上证今日涨幅: {(shIndexPctChg.HasValue ? shIndexPctChg.Value.ToString("F2") + "%" : "N/A")}, 已设为 Alpha 参照基准。");
        }

        // 板块热度（可选加成）：与大盘基准同为 scan 级上下文；fail-open，失败时保持 Unavailable。
        SparrowBoardHeatResult boardHeat = SparrowBoardHeatResult.Unavailable;
        if (parameters.SectorHeatEnabled)
        {
            boardHeat = await _boardHeatService.GetAsync(parameters.UseCache, ct);
            ReportLog(progress, boardHeat.Available
                ? $"✅ [板块热度] 行业热度图已加载（覆盖 {boardHeat.HeatPercentileBySymbol.Count} 只）。"
                : "⚠️ [板块热度] 数据不可用，本次按无板块加成处理。");
        }

        if (targetPool.Count >= SparrowConstants.FullMarketQuoteThreshold)
        {
            ReportLog(progress, "\n🌐 [阶段2] 优先加载全市场盘口快照...");
            try
            {
                if (_quoteService != null)
                {
                    MarketDataResult<IReadOnlyList<QuoteSnapshot>> snapshot = await _quoteService.GetAllQuotesAsync(
                        forceRefresh: !parameters.UseCache, cancellationToken: ct);
                    if (snapshot.Success)
                    {
                        AddQuotesToCache(snapshot.Data, quoteCache);
                    }
                }
                else
                {
                    StockDataResult snapshot = await _dataProvider.GetDataAsync(
                        StockDataRequest.Parse(SparrowEndpoints.QuoteAll(!parameters.UseCache)), ct);
                    if (snapshot.Success)
                    {
                        AddQuotesToCache(snapshot.Json, quoteCache);
                    }
                }
                if (!quoteCache.IsEmpty)
                {
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
            int p2FailedStocks = 0;
            var p2FailedCodes = new ConcurrentQueue<string>();
            progress?.Report(new SparrowScanReport { ProgressMax = p2Missing.Count, ProgressValue = 0 });
            
            var batches = p2Missing.Select((x, i) => new { Index = i, Value = x })
                .GroupBy(x => x.Index / SparrowConstants.QuoteBatchSize)
                .Select(x => x.Select(v => v.Value).ToList())
                .ToList();

            int p2Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(SparrowConstants.MaxP2Concurrency, p2Concurrency)));
            await Task.WhenAll(batches.Select(async batch =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    string[] codes = batch.Select(x => x.Code).ToArray();

                    SparrowRetryOutcome retry = await SparrowRetryPolicy.RetryAsync(
                        token => AttemptQuoteBatchAsync(codes, parameters, quoteCache, token),
                        SparrowRetryPolicy.DefaultMaxAttempts,
                        i => SparrowRetryPolicy.ExponentialBackoff(i),
                        ct);

                    if (!retry.Success)
                    {
                        Interlocked.Increment(ref p2FailedBatches);
                        Interlocked.Add(ref p2FailedStocks, batch.Count);
                        foreach ((string code, _) in batch)
                        {
                            p2FailedCodes.Enqueue(code);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref p2FailedBatches);
                    Interlocked.Add(ref p2FailedStocks, batch.Count);
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
					$"⚠️ [阶段2网络] 批次失败: {p2FailedBatches}/{batches.Count}，涉及 {p2FailedStocks} 只（每批重试最多 {SparrowRetryPolicy.DefaultMaxAttempts} 次）；" +
					$"失败示例: {string.Join(", ", p2FailedCodes.Take(5))}；已隔离故障并继续处理其余批次。",
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
            if (!quoteCache.TryGetValue(item.Code, out SparrowQuoteData quote))
            {
                quoteDataUnavailable++;
                continue;
            }

            SparrowRuleComparison quoteRule = SparrowV2CandidateEvaluator.EvaluateQuote(quote, parameters);
            if (quoteRule.ReasonCode == SparrowComparisonReasonCodes.P2QuoteDataMissing)
            {
                quoteDataUnavailable++;
                continue;
            }

            if (quoteRule.ReasonCode is SparrowComparisonReasonCodes.P2RiseBelowMin
                or SparrowComparisonReasonCodes.P2RiseAboveMax
                or SparrowComparisonReasonCodes.P2Amount)
            {
                continue;
            }

            coarseSurvivors++;
            if (quoteRule.ReasonCode == SparrowComparisonReasonCodes.P2OuterInnerMissing)
            {
                outerInnerUnavailable++;
                continue;
            }

            outerInnerValid++;
            if (quoteRule.ReasonCode == SparrowComparisonReasonCodes.P2VolRatio)
            {
                continue;
            }

            volRatioPassed++;
            if (quoteRule.Passed)
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

        // Scan-local typed snapshots: cache serialization remains legacy-compatible, evaluation is not.
        var klineData = new ConcurrentDictionary<string, SparrowKlineSnapshot>(StringComparer.Ordinal);
        var klineStatistics = new SparrowKlineFetchStatistics();
        var klineStopwatch = Stopwatch.StartNew();
        int klineCacheHits = 0;

        var p3Missing = new List<(string Code, string Name)>();
        foreach (var stock in p2List)
        {
            string cacheKey = SparrowDataCachePolicy.GetKlineKey(stock.Code, SparrowConstants.V2KlineLimit, "day");
            if (parameters.UseCache
                && _klineCache.TryGet(cacheKey, true, out var cachedJson)
                && !string.IsNullOrWhiteSpace(cachedJson)
                && SparrowKlineFetchHelper.IsUsableKlineJson(cachedJson))
            {
                SparrowKlineSnapshot? snapshot = SparrowKlineSnapshotFactory.FromCachedJson(cachedJson);
                if (snapshot != null)
                {
                    klineData[stock.Code] = snapshot;
                    klineCacheHits++;
                }
                else
                {
                    p3Missing.Add(stock);
                }
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

            int p3Concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
            using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(SparrowConstants.MaxP3Concurrency, p3Concurrency * SparrowConstants.P3ConcurrencyMultiplier)));
            await Task.WhenAll(p3Missing.Select(async stock =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    SparrowKlineFetchOutcome outcome = await SparrowMarketDataFetcher.FetchKlineAsync(
                        _klineService, _klineCache, klineStatistics,
                        stock.Code, SparrowConstants.V2KlineLimit, parameters.UseCache, ct);
                    if (outcome.Success)
                    {
                        SparrowKlineSnapshot? snapshot = outcome.Series != null
                            ? SparrowKlineSnapshotFactory.FromSeries(outcome.Series)
                            : SparrowKlineSnapshotFactory.FromCachedJson(outcome.Json!);
                        if (snapshot == null)
                        {
                            throw new InvalidOperationException("Usable Sparrow Kline response produced no snapshot.");
                        }
                        klineData[stock.Code] = snapshot;
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

            SparrowKlineSnapshot snapshot = klineData[item.Code];
            SparrowQuoteData quote = rankingQuotes[item.Code];
            // V2 stays on the frozen evaluator; gate configs route P3 through V3 (momentum), V4 (+volume) or V5 (+weekly trend).
            SparrowRuleComparison p2 = SparrowV2CandidateEvaluator.EvaluateQuote(quote, parameters);
            SparrowTechnicalEvaluation technical = !p2.Passed
                ? new SparrowTechnicalEvaluation(SparrowRuleComparison.NotRun("P3", "P2 did not pass"))
                : weeklyGates is null
                    ? volumeGates is null
                        ? momentumGates is null
                            ? SparrowV2RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters)
                            : SparrowV3RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters, momentumGates)
                        : SparrowV4RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters, momentumGates, volumeGates)
                    : SparrowV5RuleEvaluator.Evaluate(snapshot, shIndexPctChg, parameters, momentumGates, volumeGates, weeklyGates);
            SparrowV2CandidateEvaluation evidence = new(item.Code, p2, technical.Rule,
                StrategyVersion: intradayGates is not null ? SparrowStrategyVersions.V6
                    : weeklyGates is not null ? SparrowStrategyVersions.V5
                    : volumeGates is not null ? SparrowStrategyVersions.V4
                    : momentumGates is null ? SparrowStrategyVersions.V2 : SparrowStrategyVersions.V3);
            if (evidence.Passed)
            {
                string reason = $"黏合:{technical.Adhesion * 100.0:F1}% 动量:{technical.Momentum * 100.0:F1}%";
                p3Winners[item.Code] = new SparrowV2Candidate
                {
                    Code = item.Code,
                    Name = item.Name,
                    Reason = reason,
                    RankingFeatures = new SparrowRankingFeatures
                    {
                        Code = item.Code,
                        Name = item.Name,
                        RisePercent = quote.PriceDerivedPercent,
                        Amount = quote.Amount,
                        OuterVolume = quote.OuterVolume,
                        InnerVolume = quote.InnerVolume,
                        BuyPressureRatio = SparrowRankingFeatures.CalculateBuyPressureRatio(
                            quote.OuterVolume, quote.InnerVolume),
                        Adhesion = technical.Adhesion,
                        Turnover = quote.Turnover,
                        Momentum = technical.Momentum,
                        AlphaMargin = parameters.CheckAlpha && shIndexPctChg.HasValue
                            ? technical.LatestPercent - shIndexPctChg.Value
                            : null,
                        SectorHeatPercentile = boardHeat.Available && boardHeat.HeatPercentileBySymbol.TryGetValue(item.Code, out double sectorHeat)
                            ? sectorHeat
                            : null
                    }
                };
                ReportLog(progress, $"🎯 [入围] {item.Name}({item.Code}) {reason}");
            }
        }

        // V6 two-stage intraday filter: only daily-pass survivors are fetched, keeping the intraday request
        // budget at the survivor count rather than the whole P2 pool.
        if (intradayGates is { Enabled: true } && p3Winners.Count > 0)
        {
            ReportLog(progress, $"\n⏱ [V6 分钟回踩] 对 {p3Winners.Count} 只日线通过者进行 {intradayGates.PeriodMinutes} 分钟确认...");
            string[] survivors = p3Winners.Keys.ToArray();
            var semaphore = new SemaphoreSlim(SparrowConstants.MaxP3Concurrency, SparrowConstants.MaxP3Concurrency);
            await Task.WhenAll(survivors.Select(async code =>
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);
                try
                {
                    string cacheKey = SparrowDataCachePolicy.GetKlineKey(code, SparrowConstants.V6IntradayBarsLimit, intradayGates.PeriodMinutes == 30 ? "30min" : "60min");
                    SparrowKlineSnapshot? cachedIntraday = null;
                    if (parameters.UseCache
                        && _klineCache.TryGet(cacheKey, true, out string? cachedJson)
                        && !string.IsNullOrWhiteSpace(cachedJson))
                    {
                        cachedIntraday = SparrowKlineSnapshotFactory.FromCachedJson(cachedJson);
                    }

                    SparrowKlineSnapshot? intradaySnapshot = cachedIntraday;
                    if (intradaySnapshot is null)
                    {
                        SparrowKlineFetchOutcome outcome = await SparrowKlineFetchHelper.FetchIntradayAsync(
                            _klineService, code, intradayGates.PeriodMinutes, SparrowConstants.V6IntradayBarsLimit, !parameters.UseCache, ct);
                        if (outcome.Success && outcome.Json is not null)
                        {
                            _klineCache.Set(cacheKey, outcome.Json, SparrowDataCachePolicy.MinuteKlineTtl);
                            intradaySnapshot = SparrowKlineSnapshotFactory.FromSeries(outcome.Series!);
                        }
                    }

                    SparrowRuleComparison gate = SparrowV6RuleEvaluator.EvaluateIntradayGateOnly(intradaySnapshot, intradayGates);
                    if (!gate.Passed)
                    {
                        p3Winners.TryRemove(code, out _);
                        ReportLog(progress, $"⏹ [分钟淘汰] {code}: {gate.Reason}");
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }));
            ReportLog(progress, $"⏱ [V6 分钟回踩] 确认完成，剩余 {p3Winners.Count} 只。");
        }

        ReportLog(progress, $"\n🏆 漏斗完成！共诞生长短腿战斗机 {p3Winners.Count} 只！");
        ReportLog(progress, $"🧭 [Sparrow V2][Cache Lifetime Totals] Kline: {_klineCache.Statistics}");

        var results = p3Winners.Values.OrderBy(candidate => candidate.Code, StringComparer.Ordinal).ToList();
        if (results.Count > 0)
        {
            await SparrowResultFileWriter.WriteV2Async(results);
        }
        return results;
    }

    private void ReportLog(IProgress<SparrowScanReport>? progress, string msg, bool isHighlight = false)
    {
        progress?.Report(new SparrowScanReport { LogMessage = msg, IsHighlight = isHighlight });
    }

    private async Task<bool> AttemptQuoteBatchAsync(
        string[] codes,
        SparrowScanParameters parameters,
        ConcurrentDictionary<string, SparrowQuoteData> quoteCache,
        CancellationToken ct)
    {
        IReadOnlyList<(string Code, SparrowQuoteData Quote)>? rows = await SparrowMarketDataFetcher.FetchQuoteBatchAsync(
            _quoteService, codes, !parameters.UseCache, ct);
        if (rows == null)
        {
            return false;
        }
        foreach ((string code, SparrowQuoteData quote) in rows)
        {
            if (!string.IsNullOrEmpty(code) && quote.Price.HasValue)
            {
                quoteCache.TryAdd(code, quote);
            }
        }
        return true;
    }

    private static void AddQuotesToCache(
        string json,
        ConcurrentDictionary<string, SparrowQuoteData> quoteCache)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("Code", out JsonElement codeElement)
                || !SparrowQuoteDataContract.TryParse(item, out SparrowQuoteData quote)) continue;
            string code = SparrowSymbolNormalizer.Normalize(codeElement.GetString());
            if (!string.IsNullOrEmpty(code))
            {
                quoteCache.TryAdd(code, quote);
            }
        }
    }

    private static void AddQuotesToCache(
        IEnumerable<QuoteSnapshot> quotes,
        ConcurrentDictionary<string, SparrowQuoteData> quoteCache)
    {
        foreach (QuoteSnapshot snapshot in quotes)
        {
            string code = SparrowSymbolNormalizer.Normalize(snapshot.Symbol);
            if (!string.IsNullOrEmpty(code))
            {
                quoteCache.TryAdd(code, SparrowQuoteDataContract.FromSnapshot(snapshot));
            }
        }
    }

    private async Task<double?> GetShanghaiDailyPercentAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        double? shIndexPctChg = null;
        try
        {
            string url = SparrowEndpoints.Index(SparrowConstants.ShanghaiIndexCode, 2, forceRefresh);
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
                        double prevClose = elements[elements.Count - 2].TryGetProperty("Close", out var pc) ? pc.GetDouble() / MarketDataUnits.MilliDivisor : 0.0;
                        double close = elements[elements.Count - 1].TryGetProperty("Close", out var c) ? c.GetDouble() / MarketDataUnits.MilliDivisor : 0.0;
                        if (prevClose > 0)
                        {
                            shIndexPctChg = (close - prevClose) / prevClose * 100.0;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Exception ex) { Log.Error(ex, "Swallowed exception"); }
        return shIndexPctChg;
    }
}

