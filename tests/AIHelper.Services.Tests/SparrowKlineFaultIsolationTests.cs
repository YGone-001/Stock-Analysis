using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowKlineFaultIsolationTests
{
    [Fact]
    public async Task V2_KlineTimeout_DoesNotAbortWholeScan()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.Success),
            ("600001", KlineBehavior.OperationCanceled),
            ("600002", KlineBehavior.Success));
        var progress = new CaptureProgress<SparrowScanReport>();

        List<SparrowV2Candidate> results = await new SparrowScannerService(provider).ScanWithFeaturesAsync(
            Pool(3), V2(), progress, CancellationToken.None);

        Assert.Equal(new[] { "600000", "600002" }, results.Select(candidate => candidate.Code));
        Assert.Equal(3, provider.KlineRequestCount);
        Assert.Equal(1, provider.CountForCode("600001"));
        Assert.Contains("Timeout:            1", progress.Log);
        Assert.Contains("Unavailable:        1", progress.Log);
        Assert.Contains("Usable Kline:       2", progress.Log);
    }

    [Fact]
    public async Task V2_TaskCanceledException_IsRequestTimeoutNotCallerCancellation()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.Success),
            ("600001", KlineBehavior.TaskCanceled),
            ("600002", KlineBehavior.Success));

        List<SparrowV2Candidate> results = await new SparrowScannerService(provider).ScanWithFeaturesAsync(
            Pool(3), V2(), null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(results, candidate => candidate.Code == "600001");
        Assert.Equal(3, provider.KlineRequestCount);
    }

    [Fact]
    public async Task CallerCancellation_StillAbortsWholeScan()
    {
        var provider = ProviderFor(("600000", KlineBehavior.WaitForCallerCancellation));
        using var cancellation = new CancellationTokenSource();
        Task<List<SparrowV2Candidate>> scan = new SparrowScannerService(provider).ScanWithFeaturesAsync(
            Pool(1), V2(), null, cancellation.Token);
        await provider.KlineStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        Assert.Equal(1, provider.KlineRequestCount);
    }

    [Fact]
    public async Task V2_ExceptionFailureEmptyAndMalformed_AreIsolatedAndClassified()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.Success),
            ("600001", KlineBehavior.HttpException),
            ("600002", KlineBehavior.FailureResult),
            ("600003", KlineBehavior.EmptyResponse),
            ("600004", KlineBehavior.MalformedResponse));
        var progress = new CaptureProgress<SparrowScanReport>();

        List<SparrowV2Candidate> results = await new SparrowScannerService(provider).ScanWithFeaturesAsync(
            Pool(5), V2(), progress, CancellationToken.None);

        Assert.Equal("600000", Assert.Single(results).Code);
        Assert.Equal(5, provider.KlineRequestCount);
        Assert.All(Pool(5), stock => Assert.Equal(1, provider.CountForCode(stock.Code)));
        Assert.Contains("Downloaded:         1", progress.Log);
        Assert.Contains("Request failed:     3", progress.Log);
        Assert.Contains("Empty response:     1", progress.Log);
        Assert.Contains("Unavailable:        4", progress.Log);
    }

    [Fact]
    public async Task V2_AllKlinesFail_CompletesWithNoCandidateOrRanking()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.OperationCanceled),
            ("600001", KlineBehavior.HttpException),
            ("600002", KlineBehavior.FailureResult));
        var progress = new CaptureProgress<SparrowScanReport>();

        List<SparrowV2Candidate> candidates = await new SparrowScannerService(provider).ScanWithFeaturesAsync(
            Pool(3), V2(), progress, CancellationToken.None);
        IReadOnlyList<SparrowRankedCandidate> ranking = new SparrowRankingEngine().RankV2(
            candidates.Select(candidate => candidate.RankingFeatures), 1, 5, checkAlpha: false);

        Assert.Empty(candidates);
        Assert.Empty(ranking);
        Assert.Equal(3, provider.KlineRequestCount);
        Assert.Contains("Usable Kline:       0", progress.Log);
        Assert.Contains("Unavailable:        3", progress.Log);
    }

    [Fact]
    public async Task V2_ProgressCompletesForSuccessTimeoutAndFailure()
    {
        (string Code, KlineBehavior Behavior)[] behaviors = Enumerable.Range(0, 10)
            .Select(index => ($"6000{index:D2}", index switch
            {
                8 => KlineBehavior.OperationCanceled,
                9 => KlineBehavior.FailureResult,
                _ => KlineBehavior.Success
            }))
            .ToArray();
        var provider = ProviderFor(behaviors);
        var progress = new CaptureProgress<SparrowScanReport>();

        await new SparrowScannerService(provider).ScanWithFeaturesAsync(
            Pool(10), V2(), progress, CancellationToken.None);

        int klineStage = progress.Items.FindIndex(item =>
            item.LogMessage?.Contains("[阶段3]", StringComparison.Ordinal) == true);
        int networkStart = progress.Items.FindIndex(klineStage + 1,
            item => item.ProgressMax == 10 && item.ProgressValue == 0);
        int networkComplete = progress.Items.FindIndex(networkStart + 1, item => item.ProgressValue == 10);
        Assert.True(klineStage >= 0);
        Assert.True(networkStart >= 0);
        Assert.True(networkComplete > networkStart);
        Assert.Equal(10, provider.KlineRequestCount);
    }

    [Fact]
    public async Task V2_CacheHitsReduceNetworkAndTimeoutDoesNotPolluteCache()
    {
        var cache = new SparrowMarketDataCache();
        string validJson = KlineJson(120);
        cache.Set(SparrowDataCachePolicy.GetKlineKey("600000", 120, "day"), validJson, TimeSpan.FromMinutes(30));
        cache.Set(SparrowDataCachePolicy.GetKlineKey("600001", 120, "day"), validJson, TimeSpan.FromMinutes(30));
        var provider = ProviderFor(
            ("600002", KlineBehavior.Success),
            ("600003", KlineBehavior.OperationCanceled),
            ("600004", KlineBehavior.Success));
        var progress = new CaptureProgress<SparrowScanReport>();

        List<SparrowV2Candidate> results = await new SparrowScannerService(provider, cache).ScanWithFeaturesAsync(
            Pool(5), V2(), progress, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(3, provider.KlineRequestCount);
        Assert.Contains("Cache hit:          2", progress.Log);
        Assert.Contains("Network requested:  3", progress.Log);
        Assert.Contains("Downloaded:         2", progress.Log);
        Assert.Contains("Timeout:            1", progress.Log);
        Assert.Contains("Usable Kline:       4", progress.Log);
        Assert.False(cache.TryGet(
            SparrowDataCachePolicy.GetKlineKey("600003", 120, "day"), true, out _));
    }

    [Fact]
    public async Task FailedFreshFetch_DoesNotClearExistingLegalCache()
    {
        var cache = new SparrowMarketDataCache();
        string key = SparrowDataCachePolicy.GetKlineKey("600000", 120, "day");
        string validJson = KlineJson(120);
        cache.Set(key, validJson, TimeSpan.FromMinutes(30));
        var provider = ProviderFor(("600000", KlineBehavior.OperationCanceled));
        SparrowScanParameters parameters = V2();
        parameters.UseCache = false;

        List<SparrowV2Candidate> results = await new SparrowScannerService(provider, cache).ScanWithFeaturesAsync(
            Pool(1), parameters, null, CancellationToken.None);

        Assert.Empty(results);
        Assert.True(cache.TryGet(key, true, out string? cached));
        Assert.Equal(validJson, cached);
    }

    [Fact]
    public async Task Classic_KlineTimeout_DoesNotAbortWholeScan()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.Success),
            ("600001", KlineBehavior.OperationCanceled),
            ("600002", KlineBehavior.Success));
        var progress = new CaptureProgress<SparrowClassicScanReport>();
        SparrowClassicScanParameters parameters = Classic();
        parameters.MinAdhesion = 1;

        List<SparrowClassicCandidate> results = await new SparrowClassicScanner(provider).ScanAsync(
            Pool(3), parameters, progress, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(3, provider.KlineRequestCount);
        Assert.Contains("Sparrow Classic Kline Fetch", progress.Log);
        Assert.Contains("Timeout:            1", progress.Log);
        Assert.Contains("P3 K线有效:            2", progress.Log);
    }

    [Fact]
    public async Task Compare_KlineTimeout_IsDataUnavailableOnBothSidesAndSnapshotContinues()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.Success),
            ("600001", KlineBehavior.OperationCanceled),
            ("600002", KlineBehavior.Success));
        var progress = new CaptureProgress<SparrowComparisonProgress>();

        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            Pool(3), Classic(), V2(), progress, CancellationToken.None, exportCsv: false);

        SparrowComparisonRow failed = result.Rows.Single(row => row.Code == "600001");
        Assert.Equal(SparrowRuleOutcome.DataUnavailable, failed.Classic.P3.Outcome);
        Assert.Equal(SparrowRuleOutcome.DataUnavailable, failed.V2.P3.Outcome);
        Assert.Equal(SparrowComparisonReasonCodes.P3KlineMissing, failed.Classic.P3.ReasonCode);
        Assert.Equal(SparrowComparisonReasonCodes.P3KlineMissing, failed.V2.P3.ReasonCode);
        Assert.Equal(SparrowComparisonCategory.Neither, failed.Category);
        Assert.Equal(2, result.Metrics.IntersectionCount);
        Assert.Equal(3, provider.KlineRequestCount);
        Assert.All(Pool(3), stock => Assert.Equal(1, provider.CountForCode(stock.Code)));
        Assert.Contains("Sparrow Compare Kline Fetch", progress.Log);
    }

    [Fact]
    public async Task AllSuccess_PreservesEligibilityAndSparrowRankV1()
    {
        var provider = ProviderFor(
            ("600000", KlineBehavior.Success),
            ("600001", KlineBehavior.Success),
            ("600002", KlineBehavior.Success));

        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            Pool(3), Classic(), V2(), null, CancellationToken.None, exportCsv: false);

        Assert.All(result.Rows, row => Assert.Equal(SparrowComparisonCategory.Both, row.Category));
        Assert.Equal(3, result.ClassicRanking.Count);
        Assert.Equal(3, result.V2Ranking.Count);
        Assert.All(result.ClassicRanking, candidate =>
            Assert.Equal(SparrowRankingProfileV1.Name, candidate.RankingProfile));
        Assert.All(result.V2Ranking, candidate =>
            Assert.Equal(SparrowRankingProfileV1.Name, candidate.RankingProfile));
    }

    private static FaultProvider ProviderFor(params (string Code, KlineBehavior Behavior)[] behaviors) =>
        new(behaviors.ToDictionary(item => item.Code, item => item.Behavior, StringComparer.Ordinal));

    private static List<(string Code, string Name)> Pool(int count) => Enumerable.Range(0, count)
        .Select(index => ($"6000{index:D2}", $"Candidate {index}"))
        .ToList();

    private static SparrowScanParameters V2() => new()
    {
        MacroDef = false,
        MinRise = 1,
        MaxRise = 5,
        VolRatio = 1.1,
        MinAmount = 1,
        CheckMA60 = true,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MinTurnover = 3,
        MaxTurnover = 30,
        MomentumThreshold = 0,
        CheckAlpha = false,
        MaxConcurrency = 2,
        UseCache = true
    };

    private static SparrowClassicScanParameters Classic() => new()
    {
        MacroDef = false,
        MinRise = 1,
        MaxRise = 5,
        VolRatio = 1.1,
        MinAmount = 1,
        CheckMA60 = true,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MaxConcurrency = 2,
        UseCache = true
    };

    private static string QuoteJson(IEnumerable<string> codes) => JsonSerializer.Serialize(new
    {
        data = codes.Select(code => new
        {
            QuoteSchemaVersion = 2,
            Code = code,
            Name = code,
            Price = 10.3,
            PreClose = 10.0,
            Percent = 3.0,
            Amount = 100_000_000.0,
            Turnover = 10.0,
            OuterVolume = 1500.0,
            InnerVolume = 1000.0
        })
    });

    private static string KlineJson(int count) => JsonSerializer.Serialize(new
    {
        data = Enumerable.Range(0, count).Select(index => new
        {
            Time = $"2026-01-{index + 1:D3}",
            Close = (long)Math.Round((8.0 + index * 0.02) * 1000)
        })
    });

    private enum KlineBehavior
    {
        Success,
        OperationCanceled,
        TaskCanceled,
        HttpException,
        FailureResult,
        EmptyResponse,
        MalformedResponse,
        WaitForCallerCancellation
    }

    private sealed class FaultProvider : IStockDataProvider
    {
        private readonly IReadOnlyDictionary<string, KlineBehavior> _behaviors;
        public ConcurrentBag<StockDataRequest> Requests { get; } = new();
        public TaskCompletionSource KlineStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FaultProvider(IReadOnlyDictionary<string, KlineBehavior> behaviors)
        {
            _behaviors = behaviors;
        }

        public int KlineRequestCount => Requests.Count(request => request.Path == "/api/kline-all");
        public int CountForCode(string code) => Requests.Count(request =>
            request.Path == "/api/kline-all" && request.Get("code") == code);
        public bool CanHandle(StockDataRequest request) => true;

        public async Task<StockDataResult> GetDataAsync(
            StockDataRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Path == "/api/quote")
            {
                return Success(request, QuoteJson(request.Get("code").Split(',', StringSplitOptions.RemoveEmptyEntries)));
            }
            if (request.Path != "/api/kline-all")
            {
                return Failure(request, "not configured");
            }

            string code = request.Get("code");
            KlineStarted.TrySetResult();
            KlineBehavior behavior = _behaviors.TryGetValue(code, out KlineBehavior configured)
                ? configured
                : KlineBehavior.FailureResult;
            switch (behavior)
            {
                case KlineBehavior.Success:
                    return Success(request, KlineJson(120));
                case KlineBehavior.OperationCanceled:
                    throw new OperationCanceledException("simulated request timeout");
                case KlineBehavior.TaskCanceled:
                    throw new TaskCanceledException("simulated HttpClient timeout");
                case KlineBehavior.HttpException:
                    throw new HttpRequestException("simulated network failure");
                case KlineBehavior.FailureResult:
                    return Failure(request, "simulated provider failure");
                case KlineBehavior.EmptyResponse:
                    return Success(request, "");
                case KlineBehavior.MalformedResponse:
                    return Success(request, "{malformed");
                case KlineBehavior.WaitForCallerCancellation:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Success(request, KlineJson(120));
                default:
                    throw new InvalidOperationException("Unknown behavior");
            }
        }

        private static StockDataResult Success(StockDataRequest request, string json) => new()
        {
            Endpoint = request.Endpoint,
            Handled = true,
            Success = true,
            Json = json
        };

        private static StockDataResult Failure(StockDataRequest request, string error) => new()
        {
            Endpoint = request.Endpoint,
            Handled = true,
            Success = false,
            Error = error
        };
    }

    private sealed class CaptureProgress<T> : IProgress<T>
    {
        private readonly object _gate = new();
        private readonly List<T> _items = new();

        public List<T> Items
        {
            get
            {
                lock (_gate)
                {
                    return _items.ToList();
                }
            }
        }

        public string Log => string.Join('\n', Items.Select(item => item switch
        {
            SparrowScanReport report => report.LogMessage,
            SparrowClassicScanReport report => report.LogMessage,
            SparrowComparisonProgress report => report.LogMessage,
            _ => ""
        }).Where(message => !string.IsNullOrWhiteSpace(message)));

        public void Report(T value)
        {
            lock (_gate)
            {
                _items.Add(value);
            }
        }
    }
}
