using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Helpers;
using AIHelper.Models;
using Serilog;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Builds one immutable market-data snapshot and evaluates Classic and V2 against it.
/// It never invokes the two production scanners sequentially.
/// </summary>
public sealed class SparrowComparisonService
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    private readonly IStockDataProvider _dataProvider;
    private readonly SparrowMarketRegimeService _marketRegimeService;
    private readonly SparrowMarketDataCache _klineCache;
    private readonly TimeProvider _timeProvider;

    public SparrowComparisonService(
        IStockDataProvider dataProvider,
        SparrowMarketRegimeService? marketRegimeService = null,
        SparrowMarketDataCache? klineCache = null,
        TimeProvider? timeProvider = null)
    {
        _dataProvider = dataProvider ?? throw new ArgumentNullException(nameof(dataProvider));
        _marketRegimeService = marketRegimeService ?? new SparrowMarketRegimeService(dataProvider);
        _klineCache = klineCache ?? new SparrowMarketDataCache(timeProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SparrowComparisonResult> CompareAsync(
        IEnumerable<(string Code, string Name)> targetPool,
        SparrowClassicScanParameters classicParameters,
        SparrowScanParameters v2Parameters,
        IProgress<SparrowComparisonProgress>? progress,
        CancellationToken cancellationToken,
        bool exportCsv = true)
    {
        ArgumentNullException.ThrowIfNull(targetPool);
        ArgumentNullException.ThrowIfNull(classicParameters);
        ArgumentNullException.ThrowIfNull(v2Parameters);

        var universe = targetPool
            .Where(stock => SparrowClassicScanner.IsEligibleStock(stock.Code, stock.Name))
            .GroupBy(stock => stock.Code, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(stock => stock.Code, StringComparer.Ordinal)
            .ToList();
        DateTimeOffset capturedAt = _timeProvider.GetUtcNow().ToOffset(BeijingOffset);
        var session = new SparrowComparisonSession
        {
            CapturedAt = capturedAt,
            TradeDate = DateOnly.FromDateTime(capturedAt.DateTime),
            UseCache = v2Parameters.UseCache,
            UniverseCount = universe.Count
        };
        var rows = universe.ToDictionary(
            stock => stock.Code,
            stock => new SparrowComparisonRow { Code = stock.Code, Name = stock.Name },
            StringComparer.Ordinal);

        Report(progress, "🧪 [Sparrow Compare]");
        Report(progress, $"Session: {session.SessionId}");
        Report(progress, $"Snapshot captured at: {session.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        Report(progress, $"Universe: {session.UniverseCount}; UseCache: {session.UseCache}");
        Report(progress, "Classic ignores V2-only parameters: Turnover, Momentum, Alpha.");

        SparrowComparisonMarketSnapshot marketSnapshot = await EvaluateMarketsAsync(
            rows.Values, classicParameters, v2Parameters, progress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        bool classicContinues = rows.Values.All(row => row.Classic.P1.Passed);
        bool v2Continues = rows.Values.All(row => row.V2.P1.Passed);
        if (classicContinues || v2Continues)
        {
            IReadOnlyDictionary<string, SparrowQuoteData> quotes = await LoadQuotesAsync(
                universe, v2Parameters, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EvaluateP2(rows.Values, quotes, classicParameters, v2Parameters, classicContinues, v2Continues);

            var p3Codes = rows.Values
                .Where(row => row.Classic.P2.Passed || row.V2.P2.Passed)
                .Select(row => row.Code)
                .ToHashSet(StringComparer.Ordinal);
            Report(progress,
                $"[P2] Classic: {rows.Values.Count(row => row.Classic.P2.Passed)}; " +
                $"V2: {rows.Values.Count(row => row.V2.P2.Passed)}; Union: {p3Codes.Count}");

            IReadOnlyDictionary<string, SparrowKlineSnapshot> klines = await LoadKlinesAsync(
                p3Codes, v2Parameters, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            EvaluateP3(rows.Values, klines, classicParameters, v2Parameters,
                marketSnapshot.V2ShanghaiDailyPercent ?? 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        FinalizeRows(rows.Values);
        SparrowComparisonMetrics metrics = CalculateMetrics(rows.Values, universe.Count);
        var result = new SparrowComparisonResult
        {
            Session = session,
            MarketSnapshot = marketSnapshot,
            Rows = rows.Values.OrderBy(row => row.Code, StringComparer.Ordinal).ToArray(),
            Metrics = metrics
        };

        ReportSummary(progress, metrics);
        if (exportCsv)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.CsvPath = await ExportCsvAsync(result, cancellationToken);
            Report(progress, $"CSV: {result.CsvPath}");
        }

        return result;
    }

    private async Task<SparrowComparisonMarketSnapshot> EvaluateMarketsAsync(
        IEnumerable<SparrowComparisonRow> rows,
        SparrowClassicScanParameters classicParameters,
        SparrowScanParameters v2Parameters,
        IProgress<SparrowComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        SparrowRuleComparison classicP1;
        SparrowRuleComparison v2P1;
        SparrowMarketRegime? classicMarket = null;
        double? v2DailyPercent = null;

        if (!classicParameters.MacroDef && !v2Parameters.MacroDef)
        {
            classicP1 = SparrowRuleComparison.Bypass("P1", "MacroDef is disabled");
            v2P1 = SparrowRuleComparison.Bypass("P1", "MacroDef is disabled");
        }
        else
        {
            Task<SparrowMarketRegime?> classicTask = classicParameters.MacroDef
                ? LoadClassicMarketAsync(!v2Parameters.UseCache, cancellationToken)
                : Task.FromResult<SparrowMarketRegime?>(null);
            Task<double?> v2Task = v2Parameters.MacroDef
                ? LoadV2MarketPercentAsync(!v2Parameters.UseCache, cancellationToken)
                : Task.FromResult<double?>(null);
            await Task.WhenAll(classicTask, v2Task);

            classicMarket = await classicTask;
            v2DailyPercent = await v2Task;
            classicP1 = !classicParameters.MacroDef
                ? SparrowRuleComparison.Bypass("P1", "MacroDef is disabled")
                : EvaluateClassicMarket(classicMarket);
            v2P1 = !v2Parameters.MacroDef
                ? SparrowRuleComparison.Bypass("P1", "MacroDef is disabled")
                : EvaluateV2Market(v2DailyPercent);
        }

        foreach (SparrowComparisonRow row in rows)
        {
            row.Classic.P1 = classicP1;
            row.V2.P1 = v2P1;
            if (!classicP1.Passed)
            {
                row.Classic.RejectFrom(classicP1);
            }
            if (!v2P1.Passed)
            {
                row.V2.RejectFrom(v2P1);
            }
        }

        Report(progress,
            $"[P1] Classic: {FormatRule(classicP1)}; V2: {FormatRule(v2P1)}");
        return new SparrowComparisonMarketSnapshot
        {
            Classic = classicMarket,
            V2ShanghaiDailyPercent = v2DailyPercent
        };
    }

    private async Task<SparrowMarketRegime?> LoadClassicMarketAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        return await _marketRegimeService.EvaluateAsync(forceRefresh, cancellationToken);
    }

    private async Task<double?> LoadV2MarketPercentAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        try
        {
            string refresh = forceRefresh ? "&refresh=1" : "";
            StockDataResult result = await _dataProvider.GetDataAsync(
                StockDataRequest.Parse("/api/index?code=sh000001&limit=2" + refresh), cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Json))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(result.Json);
            if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            JsonElement[] elements = data.EnumerateArray().ToArray();
            if (elements.Length < 2
                || !TryGetDouble(elements[^2], "Close", out double previousRaw)
                || !TryGetDouble(elements[^1], "Close", out double latestRaw))
            {
                return null;
            }
            double previous = previousRaw / 1000.0;
            double latest = latestRaw / 1000.0;
            return previous > 0 ? (latest - previous) / previous * 100.0 : null;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse V2 Shanghai market snapshot");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load V2 Shanghai market snapshot");
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, SparrowQuoteData>> LoadQuotesAsync(
        IReadOnlyList<(string Code, string Name)> universe,
        SparrowScanParameters parameters,
        IProgress<SparrowComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        var quotes = new ConcurrentDictionary<string, SparrowQuoteData>(StringComparer.Ordinal);
        var batches = universe.Select((stock, index) => (stock, index))
            .GroupBy(item => item.index / 50)
            .Select(group => group.Select(item => item.stock).ToArray())
            .ToArray();
        int completed = 0;
        int concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(8, concurrency)));
        await Task.WhenAll(batches.Select(async batch =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                string refresh = parameters.UseCache ? "" : "&refresh=1";
                string codes = string.Join(',', batch.Select(stock => stock.Code));
                StockDataResult response = await _dataProvider.GetDataAsync(
                    StockDataRequest.Parse("/api/quote?code=" + codes + refresh), cancellationToken);
                if (!response.Success || string.IsNullOrWhiteSpace(response.Json))
                {
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(response.Json);
                if (!document.RootElement.TryGetProperty("data", out JsonElement data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    return;
                }
                foreach (JsonElement item in data.EnumerateArray())
                {
                    string code = NormalizeCode(GetString(item, "Code"));
                    if (code.Length > 0 && SparrowQuoteDataContract.TryParse(item, out SparrowQuoteData quote))
                    {
                        quotes[code] = quote;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load shared Sparrow quote batch");
            }
            finally
            {
                int count = Interlocked.Add(ref completed, batch.Length);
                progress?.Report(new SparrowComparisonProgress
                {
                    ProgressMax = universe.Count,
                    ProgressValue = Math.Min(count, universe.Count)
                });
                semaphore.Release();
            }
        }));
        Report(progress, $"Shared Quote snapshot loaded once per batch: {quotes.Count}/{universe.Count}");
        return quotes;
    }

    private static void EvaluateP2(
        IEnumerable<SparrowComparisonRow> rows,
        IReadOnlyDictionary<string, SparrowQuoteData> quotes,
        SparrowClassicScanParameters classicParameters,
        SparrowScanParameters v2Parameters,
        bool classicContinues,
        bool v2Continues)
    {
        foreach (SparrowComparisonRow row in rows)
        {
            if (!quotes.TryGetValue(row.Code, out SparrowQuoteData quote))
            {
                if (classicContinues)
                {
                    row.Classic.P2 = SparrowRuleComparison.Unavailable(
                        "P2", SparrowComparisonReasonCodes.P2QuoteDataMissing, "Quote snapshot is unavailable");
                    row.Classic.RejectFrom(row.Classic.P2);
                }
                if (v2Continues)
                {
                    row.V2.P2 = SparrowRuleComparison.Unavailable(
                        "P2", SparrowComparisonReasonCodes.P2QuoteDataMissing, "Quote snapshot is unavailable");
                    row.V2.RejectFrom(row.V2.P2);
                }
                continue;
            }

            row.Classic.Rise = quote.PriceDerivedPercent;
            row.V2.Rise = quote.Percent;
            row.Amount = quote.Amount;
            row.Turnover = quote.Turnover;
            row.OuterVolume = quote.OuterVolume;
            row.InnerVolume = quote.InnerVolume;

            if (classicContinues)
            {
                row.Classic.P2 = SparrowComparisonRuleEvaluators.EvaluateClassicQuote(quote, classicParameters);
                if (!row.Classic.P2.Passed)
                {
                    row.Classic.RejectFrom(row.Classic.P2);
                }
            }
            if (v2Continues)
            {
                row.V2.P2 = SparrowComparisonRuleEvaluators.EvaluateV2Quote(quote, v2Parameters);
                if (!row.V2.P2.Passed)
                {
                    row.V2.RejectFrom(row.V2.P2);
                }
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, SparrowKlineSnapshot>> LoadKlinesAsync(
        IReadOnlySet<string> codes,
        SparrowScanParameters parameters,
        IProgress<SparrowComparisonProgress>? progress,
        CancellationToken cancellationToken)
    {
        var snapshots = new ConcurrentDictionary<string, SparrowKlineSnapshot>(StringComparer.Ordinal);
        int completed = 0;
        int concurrency = parameters.MaxConcurrency > 0 ? parameters.MaxConcurrency : 8;
        using var semaphore = new SemaphoreSlim(Math.Max(1, Math.Min(32, concurrency * 4)));
        await Task.WhenAll(codes.Select(async code =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                string cacheKey = SparrowDataCachePolicy.GetKlineKey(code, 120, "day");
                string? json = null;
                if (!_klineCache.TryGet(cacheKey, parameters.UseCache, out json))
                {
                    string refresh = parameters.UseCache ? "" : "&refresh=1";
                    StockDataResult response = await _dataProvider.GetDataAsync(
                        StockDataRequest.Parse($"/api/kline-all?code={code}&type=day&limit=120{refresh}"),
                        cancellationToken);
                    if (response.Success && !string.IsNullOrWhiteSpace(response.Json))
                    {
                        json = response.Json;
                        _klineCache.Set(cacheKey, json, SparrowDataCachePolicy.DefaultKlineTtl);
                    }
                }

                SparrowKlineSnapshot? snapshot = SparrowV2RuleEvaluator.ParseKline(json);
                if (snapshot != null)
                {
                    snapshots[code] = snapshot;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load shared Sparrow Kline for {Code}", code);
            }
            finally
            {
                int count = Interlocked.Increment(ref completed);
                progress?.Report(new SparrowComparisonProgress
                {
                    ProgressMax = codes.Count,
                    ProgressValue = count
                });
                semaphore.Release();
            }
        }));
        Report(progress, $"Shared Kline snapshot loaded once per P2-union stock: {snapshots.Count}/{codes.Count}");
        return snapshots;
    }

    private static void EvaluateP3(
        IEnumerable<SparrowComparisonRow> rows,
        IReadOnlyDictionary<string, SparrowKlineSnapshot> klines,
        SparrowClassicScanParameters classicParameters,
        SparrowScanParameters v2Parameters,
        double shIndexPctChg)
    {
        foreach (SparrowComparisonRow row in rows)
        {
            klines.TryGetValue(row.Code, out SparrowKlineSnapshot? snapshot);
            if (row.Classic.P2.Passed)
            {
                SparrowTechnicalEvaluation technical = SparrowClassicComparisonEvaluator.Evaluate(snapshot, classicParameters);
                row.Classic.P3 = technical.Rule;
                row.Classic.Adhesion = technical.Adhesion;
                if (!technical.Rule.Passed)
                {
                    row.Classic.RejectFrom(technical.Rule);
                }
            }
            if (row.V2.P2.Passed)
            {
                SparrowTechnicalEvaluation technical = SparrowV2RuleEvaluator.Evaluate(snapshot, shIndexPctChg, v2Parameters);
                row.V2.P3 = technical.Rule;
                row.V2.Adhesion = technical.Adhesion;
                row.V2.Momentum = technical.Momentum;
                if (!technical.Rule.Passed)
                {
                    row.V2.RejectFrom(technical.Rule);
                }
            }
        }
    }

    private static void FinalizeRows(IEnumerable<SparrowComparisonRow> rows)
    {
        foreach (SparrowComparisonRow row in rows)
        {
            row.Classic.FinalPassed = row.Classic.P1.Passed && row.Classic.P2.Passed && row.Classic.P3.Passed;
            row.V2.FinalPassed = row.V2.P1.Passed && row.V2.P2.Passed && row.V2.P3.Passed;
            row.Category = (row.Classic.FinalPassed, row.V2.FinalPassed) switch
            {
                (true, true) => SparrowComparisonCategory.Both,
                (true, false) => SparrowComparisonCategory.ClassicOnly,
                (false, true) => SparrowComparisonCategory.V2Only,
                _ => SparrowComparisonCategory.Neither
            };
        }
    }

    public static SparrowComparisonMetrics CalculateMetrics(
        IEnumerable<SparrowComparisonRow> source,
        int universeCount)
    {
        SparrowComparisonRow[] rows = source.ToArray();
        int classicP2 = rows.Count(row => row.Classic.P2.Passed);
        int v2P2 = rows.Count(row => row.V2.P2.Passed);
        int both = rows.Count(row => row.Category == SparrowComparisonCategory.Both);
        int classicOnly = rows.Count(row => row.Category == SparrowComparisonCategory.ClassicOnly);
        int v2Only = rows.Count(row => row.Category == SparrowComparisonCategory.V2Only);
        int classicFinal = both + classicOnly;
        int v2Final = both + v2Only;
        int union = both + classicOnly + v2Only;
        return new SparrowComparisonMetrics
        {
            UniverseCount = universeCount,
            ClassicP2Count = classicP2,
            V2P2Count = v2P2,
            ClassicFinalCount = classicFinal,
            V2FinalCount = v2Final,
            IntersectionCount = both,
            ClassicOnlyCount = classicOnly,
            V2OnlyCount = v2Only,
            UnionCount = union,
            OverlapRate = union == 0 ? 0 : (double)both / union,
            ClassicRetention = classicP2 == 0 ? 0 : (double)classicFinal / classicP2,
            V2Retention = v2P2 == 0 ? 0 : (double)v2Final / v2P2,
            V2ClassicCandidateRatio = classicFinal == 0 ? 0 : (double)v2Final / classicFinal,
            ClassicOnlyV2RejectReasons = ReasonDistribution(
                rows.Where(row => row.Category == SparrowComparisonCategory.ClassicOnly)
                    .Select(row => row.V2.RejectReasonCode)),
            V2OnlyClassicRejectReasons = ReasonDistribution(
                rows.Where(row => row.Category == SparrowComparisonCategory.V2Only)
                    .Select(row => row.Classic.RejectReasonCode))
        };
    }

    private static IReadOnlyDictionary<string, int> ReasonDistribution(IEnumerable<string> reasons) =>
        reasons.Where(reason => !string.IsNullOrWhiteSpace(reason))
            .GroupBy(reason => reason, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static SparrowRuleComparison EvaluateClassicMarket(SparrowMarketRegime? market)
    {
        if (market == null
            || market.Shanghai == SparrowMarketState.Unknown
            || market.Csi1000 == SparrowMarketState.Unknown)
        {
            return SparrowRuleComparison.Unavailable(
                "P1", SparrowComparisonReasonCodes.P1MarketDataUnknown,
                "Classic market trend data is unavailable; historical fail-open applies", failOpen: true);
        }
        return market.Defensive
            ? SparrowRuleComparison.Reject("P1", SparrowComparisonReasonCodes.P1MarketDefensive, market.Reason)
            : SparrowRuleComparison.Pass("P1", market.Reason);
    }

    private static SparrowRuleComparison EvaluateV2Market(double? dailyPercent)
    {
        if (!dailyPercent.HasValue)
        {
            return SparrowRuleComparison.Unavailable(
                "P1", SparrowComparisonReasonCodes.P1MarketDataUnknown,
                "V2 Shanghai daily data is unavailable; current fail-open applies", failOpen: true);
        }
        return dailyPercent.Value <= -2.5
            ? SparrowRuleComparison.Reject("P1", SparrowComparisonReasonCodes.P1MarketDefensive,
                $"Shanghai daily percent {dailyPercent.Value:F4}% <= -2.5%")
            : SparrowRuleComparison.Pass("P1", $"Shanghai daily percent {dailyPercent.Value:F4}%");
    }

    private static void ReportSummary(
        IProgress<SparrowComparisonProgress>? progress,
        SparrowComparisonMetrics metrics)
    {
        Report(progress,
            $"[P3] Classic Final: {metrics.ClassicFinalCount}; V2 Final: {metrics.V2FinalCount}");
        Report(progress,
            $"[Compare] Both: {metrics.IntersectionCount}; ClassicOnly: {metrics.ClassicOnlyCount}; " +
            $"V2Only: {metrics.V2OnlyCount}; Overlap: {metrics.OverlapRate:P1}", true);
        ReportDistribution(progress, "ClassicOnly → V2 reject", metrics.ClassicOnlyV2RejectReasons);
        ReportDistribution(progress, "V2Only → Classic reject", metrics.V2OnlyClassicRejectReasons);
    }

    private static void ReportDistribution(
        IProgress<SparrowComparisonProgress>? progress,
        string title,
        IReadOnlyDictionary<string, int> distribution)
    {
        Report(progress, $"[{title}]");
        if (distribution.Count == 0)
        {
            Report(progress, "(none)");
            return;
        }
        foreach ((string reason, int count) in distribution)
        {
            Report(progress, $"{reason}: {count}");
        }
    }

    private static async Task<string> ExportCsvAsync(
        SparrowComparisonResult result,
        CancellationToken cancellationToken)
    {
        string directory = ConfigManager.Load().DataSavePath;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GPSJ");
        }
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"麻雀AB对照_{result.Session.CapturedAt:yyyyMMdd_HHmmss}.csv");
        var lines = new List<string>
        {
            "Code,Name,Category,ClassicP1,ClassicP2,ClassicP3,ClassicPassed,ClassicRejectStage,ClassicRejectReasonCode,ClassicRejectReason,V2P1,V2P2,V2P3,V2Passed,V2RejectStage,V2RejectReasonCode,V2RejectReason,ClassicRise,V2Rise,Amount,Turnover,OuterVolume,InnerVolume,ClassicAdhesion,V2Adhesion,V2Momentum"
        };
        foreach (SparrowComparisonRow row in result.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.Add(string.Join(',', new[]
            {
                Csv(row.Code), Csv(row.Name), Csv(row.Category.ToString()),
                Csv(row.Classic.P1.Outcome.ToString()), Csv(row.Classic.P2.Outcome.ToString()), Csv(row.Classic.P3.Outcome.ToString()),
                Csv(row.Classic.FinalPassed.ToString()), Csv(row.Classic.RejectStage), Csv(row.Classic.RejectReasonCode), Csv(row.Classic.RejectReason),
                Csv(row.V2.P1.Outcome.ToString()), Csv(row.V2.P2.Outcome.ToString()), Csv(row.V2.P3.Outcome.ToString()),
                Csv(row.V2.FinalPassed.ToString()), Csv(row.V2.RejectStage), Csv(row.V2.RejectReasonCode), Csv(row.V2.RejectReason),
                Number(row.Classic.Rise), Number(row.V2.Rise), Number(row.Amount), Number(row.Turnover),
                Number(row.OuterVolume), Number(row.InnerVolume), Number(row.Classic.Adhesion),
                Number(row.V2.Adhesion), Number(row.V2.Momentum)
            }));
        }
        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllLinesAsync(
                temporaryPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            throw;
        }
    }

    private static string Number(double? value) =>
        value?.ToString("G17", CultureInfo.InvariantCulture) ?? "";

    private static string Csv(string value) =>
        '"' + value.Replace("\"", "\"\"") + '"';

    private static string FormatRule(SparrowRuleComparison rule) =>
        $"{rule.Outcome} ({rule.ReasonCode})";

    private static void Report(
        IProgress<SparrowComparisonProgress>? progress,
        string message,
        bool highlight = false) =>
        progress?.Report(new SparrowComparisonProgress { LogMessage = message, IsHighlight = highlight });

    private static string NormalizeCode(string code)
    {
        string normalized = code.Trim();
        return normalized.StartsWith("1.", StringComparison.Ordinal)
            || normalized.StartsWith("0.", StringComparison.Ordinal)
            ? normalized[2..]
            : normalized;
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement property))
        {
            return "";
        }
        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : property.GetRawText();
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out JsonElement property))
        {
            return false;
        }
        return property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value)
            || property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
