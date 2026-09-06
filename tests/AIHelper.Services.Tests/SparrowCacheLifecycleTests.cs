using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowCacheLifecycleTests
{
    // =========================================================================
    // Test 1: Repeated Scan Rechecks MacroDef (P0 Bug Fix Verification)
    // =========================================================================

    [Fact]
    public async Task Classic_RepeatedScan_RechecksMacroDef_EvenWhenUseCacheIsTrue()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var klineCache = new SparrowMarketDataCache(timeProvider);
        var scanner = new SparrowClassicScanner(fakeProvider, klineCache: klineCache);

        // Scan 1: Market is Strong / Neutral -> Scan continues
        fakeProvider.SetResponse("/api/trend?code=1.000001", BuildTrendJson(100, 101, 102, 103, 104));
        fakeProvider.SetResponse("/api/trend?code=1.000852", BuildTrendJson(100, 101, 102, 103, 104));
        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));

        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = true,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var logs1 = new List<string>();
        var progress1 = new SyncProgress<SparrowClassicScanReport>(r => { if (r.LogMessage != null) logs1.Add(r.LogMessage); });

        var results1 = await scanner.ScanAsync(targetPool, parameters, progress1, CancellationToken.None);
        Assert.Single(results1);
        Assert.Contains(logs1, l => l.Contains("第一阶段通过"));

        // Scan 2: Market deteriorates to WEAK + WEAK
        // In the old develop branch, _p2Processed was not empty, so MacroDef was SKIPPED!
        // In the fixed version, MacroDef MUST be re-evaluated and trigger Defensive mode!
        fakeProvider.SetResponse("/api/trend?code=1.000001", BuildTrendJson(100, 99, 98, 97, 95)); // Weak
        fakeProvider.SetResponse("/api/trend?code=1.000852", BuildTrendJson(100, 99, 98, 97, 95)); // Weak

        var logs2 = new List<string>();
        var progress2 = new SyncProgress<SparrowClassicScanReport>(r => { if (r.LogMessage != null) logs2.Add(r.LogMessage); });

        var results2 = await scanner.ScanAsync(targetPool, parameters, progress2, CancellationToken.None);

        Assert.Empty(results2);
        Assert.Contains(logs2, l => l.Contains("双指数弱势，进入防守模式"));
    }

    // =========================================================================
    // Test 2: P2 Strategy Decisions Do Not Leak Between Scans (Parameter Change)
    // =========================================================================

    [Fact]
    public async Task P2StrategyDecisions_DoNotLeakBetweenScans_WhenParametersChange()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var scanner = new SparrowClassicScanner(fakeProvider);

        // MacroDef disabled to focus strictly on P2 parameter re-evaluation
        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000)); // VolRatio = 1.5
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };

        // Scan 1: VolRatio threshold is 1.0 (1.5 >= 1.0 => Passes P2)
        var params1 = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        var results1 = await scanner.ScanAsync(targetPool, params1, null, CancellationToken.None);
        Assert.Single(results1);

        // Scan 2: User changes VolRatio threshold to 2.0 (1.5 < 2.0 => Must FAIL P2)
        // With UseCache=true, old bug re-used _p2Survivors and let it pass.
        // Fixed behavior: Session-local P2 re-evaluates and rejects it!
        var params2 = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 2.0, // Stricter!
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        var results2 = await scanner.ScanAsync(targetPool, params2, null, CancellationToken.None);
        Assert.Empty(results2);
    }

    // =========================================================================
    // Test 3: Universe Changes Do Not Leak Survivors
    // =========================================================================

    [Fact]
    public async Task TargetUniverse_Changes_DoNotLeakOldSurvivors()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var scanner = new SparrowClassicScanner(fakeProvider);

        fakeProvider.SetResponse("/api/quote?code=600000,600001", BuildMultiQuoteBatchJson(
            ("600000", "股票A", 10.0, 3.0, 100_000_000, 1500, 1000),
            ("600001", "股票B", 10.0, 3.0, 100_000_000, 1500, 1000)));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));
        fakeProvider.SetResponse("/api/kline-all?code=600001&type=day&limit=65", BuildPassingKlineJson(10.0));

        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        // Scan 1: Pool is [600000, 600001]
        var pool1 = new List<(string Code, string Name)> { ("600000", "股票A"), ("600001", "股票B") };
        var results1 = await scanner.ScanAsync(pool1, parameters, null, CancellationToken.None);
        Assert.Equal(2, results1.Count);

        // Scan 2: Pool changes to [600002]
        fakeProvider.SetResponse("/api/quote?code=600002", BuildQuoteBatchJson("600002", "股票C", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600002&type=day&limit=65", BuildPassingKlineJson(10.0));

        var pool2 = new List<(string Code, string Name)> { ("600002", "股票C") };
        var results2 = await scanner.ScanAsync(pool2, parameters, null, CancellationToken.None);

        Assert.Single(results2);
        Assert.Equal("600002", results2[0].Code);
        Assert.DoesNotContain(results2, r => r.Code == "600000" || r.Code == "600001");
    }

    // =========================================================================
    // Test 4: Real-time Quotes Are Never Frozen Across Scans
    // =========================================================================

    [Fact]
    public async Task Quote_Freshness_NotFrozenAcrossScans()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var scanner = new SparrowScannerService(fakeProvider);

        var pool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        fakeProvider.SetResponse("/api/kline-all?code=600000&limit=120", BuildPassingKlineJson(10.0));

        var parameters = new SparrowScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 1.0,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5,
            CheckAlpha = false,
            CheckMA60 = false
        };

        // Scan 1 (e.g. at 14:00): Price rises 3.0% -> Passes
        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        var results1 = await scanner.ScanAsync(pool, parameters, new Progress<SparrowScanReport>(_ => { }), CancellationToken.None);
        Assert.Single(results1);

        // Scan 2 (e.g. at 14:30): Fresh quote falls to -1.0% (fails MinRise)
        // In the old develop branch, _p2QuoteCache_DC froze the 14:00 quote forever!
        // Fixed behavior: Quote is re-fetched and rejected!
        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 9.5, -1.0, 100_000_000, 1500, 1000));
        var results2 = await scanner.ScanAsync(pool, parameters, new Progress<SparrowScanReport>(_ => { }), CancellationToken.None);
        Assert.Empty(results2);
    }

    // =========================================================================
    // Test 5: Kline TTL Cache Hit Within TTL
    // =========================================================================

    [Fact]
    public async Task Kline_CacheHit_WithinTtl()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var klineCache = new SparrowMarketDataCache(timeProvider);
        var scanner = new SparrowClassicScanner(fakeProvider, klineCache: klineCache);

        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        // Scan 1: T = 0
        await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);
        int initialKlineCalls = fakeProvider.CountRequestsMatching("/api/kline-all");
        Assert.Equal(1, initialKlineCalls);

        // Advance time by 100s (TTL is 300s, so it is still fresh)
        timeProvider.Advance(TimeSpan.FromSeconds(100));

        // Scan 2: T = 100s, UseCache = true
        await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);
        int secondKlineCalls = fakeProvider.CountRequestsMatching("/api/kline-all");

        // Kline was reused from cache without hitting upstream!
        Assert.Equal(1, secondKlineCalls);
        Assert.Equal(1, klineCache.Statistics.HitCount);
    }

    // =========================================================================
    // Test 6: Kline Refreshes After TTL Expiration
    // =========================================================================

    [Fact]
    public async Task Kline_Refreshes_AfterTtl()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var klineCache = new SparrowMarketDataCache(timeProvider);
        var scanner = new SparrowClassicScanner(fakeProvider, klineCache: klineCache);

        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        // Scan 1: T = 0
        await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);
        Assert.Equal(1, fakeProvider.CountRequestsMatching("/api/kline-all"));

        // Advance time past 300s TTL (301 seconds)
        timeProvider.Advance(TimeSpan.FromSeconds(301));

        // Scan 2: T = 301s
        await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);

        // Cache expired, refreshed from upstream!
        Assert.Equal(2, fakeProvider.CountRequestsMatching("/api/kline-all"));
        Assert.True(klineCache.Statistics.ExpiredCount >= 1);
    }

    // =========================================================================
    // Test 7: Kline Key Isolation Between Limit=65 and Limit=120
    // =========================================================================

    [Fact]
    public void KlineKey_Limit65_Vs_Limit120_Isolated()
    {
        string keyClassic = SparrowDataCachePolicy.GetKlineKey("sh600519", 65, "day");
        string keyV2 = SparrowDataCachePolicy.GetKlineKey("600519", 120, "day");

        Assert.Equal("kline:600519:day:65", keyClassic);
        Assert.Equal("kline:600519:day:120", keyV2);
        Assert.NotEqual(keyClassic, keyV2);

        var cache = new SparrowMarketDataCache();
        cache.Set(keyClassic, "{\"limit\":65}", TimeSpan.FromSeconds(300));
        cache.Set(keyV2, "{\"limit\":120}", TimeSpan.FromSeconds(300));

        Assert.True(cache.TryGet(keyClassic, true, out string? valClassic));
        Assert.True(cache.TryGet(keyV2, true, out string? valV2));
        Assert.Equal("{\"limit\":65}", valClassic);
        Assert.Equal("{\"limit\":120}", valV2);
    }

    // =========================================================================
    // Test 8: UseCache=False Bypasses Cache and Passes Refresh Parameter
    // =========================================================================

    [Fact]
    public async Task UseCache_False_BypassesCache_AndPassesRefreshParam()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var klineCache = new SparrowMarketDataCache();
        var scanner = new SparrowClassicScanner(fakeProvider, klineCache: klineCache);

        // Pre-fill cache with stale data
        string cacheKey = SparrowDataCachePolicy.GetKlineKey("600000", 65, "day");
        klineCache.Set(cacheKey, "{\"stale\":true}", TimeSpan.FromSeconds(300));

        fakeProvider.SetResponse("/api/quote?code=600000&refresh=1", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65&refresh=1", BuildPassingKlineJson(10.0));

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = false, // Bypass!
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);

        // Verify request had refresh=1
        var klineReq = fakeProvider.RecordedRequests.FirstOrDefault(r => r.Path == "/api/kline-all");
        Assert.NotNull(klineReq);
        Assert.True(klineReq.ForceRefresh);
    }

    // =========================================================================
    // Test 9: Failed Responses Are Not Cached
    // =========================================================================

    [Fact]
    public async Task FailedResponse_NotCached()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var klineCache = new SparrowMarketDataCache();
        var scanner = new SparrowClassicScanner(fakeProvider, klineCache: klineCache);

        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetFailure("/api/kline-all?code=600000&type=day&limit=65", "500 Server Error");

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        // First attempt fails
        var results1 = await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);
        Assert.Empty(results1);

        // Cache must NOT contain any entry for 600000
        string cacheKey = SparrowDataCachePolicy.GetKlineKey("600000", 65, "day");
        Assert.False(klineCache.TryGet(cacheKey, true, out _));

        // When upstream recovers:
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));
        var results2 = await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);
        Assert.Single(results2);
        Assert.True(klineCache.TryGet(cacheKey, true, out _));
    }

    // =========================================================================
    // Test 10: Cancellation Does Not Persist Partial State
    // =========================================================================

    [Fact]
    public async Task Cancellation_DoesNotPersistPartialState()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var scanner = new SparrowClassicScanner(fakeProvider);

        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancelled before or during scan

        var results = await scanner.ScanAsync(targetPool, parameters, null, cts.Token);
        Assert.Empty(results);

        // Next scan with valid token succeeds cleanly
        var resultsNext = await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);
        Assert.Single(resultsNext);
    }

    // =========================================================================
    // Test 11: Quote Schema V2 Null Values Preserved
    // =========================================================================

    [Fact]
    public void QuoteSchemaV2_NullValuesPreserved()
    {
        string jsonWithNulls = """
        {
            "QuoteSchemaVersion": 2,
            "Code": "600000",
            "Price": 10.0,
            "PreClose": 9.8,
            "Amount": 50000000.0,
            "Volume": 50000.0,
            "OuterVolume": null,
            "InnerVolume": null
        }
        """;

        using var doc = JsonDocument.Parse(jsonWithNulls);
        bool parsed = SparrowQuoteDataContract.TryParse(doc.RootElement, out SparrowQuoteData quote);

        Assert.True(parsed);
        Assert.Null(quote.OuterVolume);
        Assert.Null(quote.InnerVolume);
        Assert.Equal(SparrowVolumeCheckResult.OuterInnerUnavailable,
            SparrowQuoteDataContract.EvaluateVolume(quote.OuterVolume, quote.InnerVolume, 1.0));
    }

    // =========================================================================
    // Test 12: Diagnostic Statistics Counters Accurate
    // =========================================================================

    [Fact]
    public void Diagnostics_StatisticsCounters_Accurate()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var cache = new SparrowMarketDataCache(timeProvider);

        // Miss
        Assert.False(cache.TryGet("missing", true, out _));
        Assert.Equal(1, cache.Statistics.MissCount);

        // Hit
        cache.Set("key1", "data1", TimeSpan.FromSeconds(10));
        Assert.True(cache.TryGet("key1", true, out string? val));
        Assert.Equal("data1", val);
        Assert.Equal(1, cache.Statistics.HitCount);

        // Expired
        timeProvider.Advance(TimeSpan.FromSeconds(11));
        Assert.False(cache.TryGet("key1", true, out _));
        Assert.Equal(1, cache.Statistics.ExpiredCount);

        // Bypass
        cache.Set("key2", "data2", TimeSpan.FromSeconds(10));
        Assert.False(cache.TryGet("key2", false, out _));
        Assert.Equal(1, cache.Statistics.BypassCount);
    }

    // =========================================================================
    // Test 13: Fail-open Preserved When Market Data is Unavailable
    // =========================================================================

    [Fact]
    public async Task FailOpen_PreservedWithCache()
    {
        var fakeProvider = new LifecycleFakeStockDataProvider();
        var scanner = new SparrowClassicScanner(fakeProvider);

        // Market trend is unavailable / 500
        fakeProvider.SetFailure("/api/trend?code=1.000001", "Gateway Timeout");
        fakeProvider.SetFailure("/api/trend?code=1.000852", "Gateway Timeout");

        fakeProvider.SetResponse("/api/quote?code=600000", BuildQuoteBatchJson("600000", "浦发银行", 10.0, 3.0, 100_000_000, 1500, 1000));
        fakeProvider.SetResponse("/api/kline-all?code=600000&type=day&limit=65", BuildPassingKlineJson(10.0));

        var targetPool = new List<(string Code, string Name)> { ("600000", "浦发银行") };
        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = true,
            UseCache = true,
            MinRise = 0.5,
            MaxRise = 5.0,
            MinAmount = 10_000_000,
            VolRatio = 1.0,
            MinAdhesion = 0.0,
            MaxAdhesion = 0.5
        };

        var logs = new List<string>();
        var progress = new SyncProgress<SparrowClassicScanReport>(r => { if (r.LogMessage != null) logs.Add(r.LogMessage); });

        var results = await scanner.ScanAsync(targetPool, parameters, progress, CancellationToken.None);

        // Fail-open: Allowed to continue, stock 600000 is selected
        Assert.Single(results);
        Assert.Contains(logs, l => l.Contains("Fail-open"));
    }

    // =========================================================================
    // Helper Classes & JSON Builders
    // =========================================================================

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public ManualTimeProvider(DateTimeOffset initial) => _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class LifecycleFakeStockDataProvider : IStockDataProvider
    {
        private readonly Dictionary<string, (bool Success, string Json, string Error)> _routes =
            new(StringComparer.OrdinalIgnoreCase);

        public List<StockDataRequest> RecordedRequests { get; } = new();

        public void SetResponse(string endpoint, string json)
        {
            _routes[endpoint] = (true, json, "");
        }

        public void SetFailure(string endpoint, string error)
        {
            _routes[endpoint] = (false, "", error);
        }

        public int CountRequestsMatching(string pathPrefix) =>
            RecordedRequests.Count(r => r.Endpoint.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));

        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            RecordedRequests.Add(request);
            if (_routes.TryGetValue(request.Endpoint, out var match))
            {
                return Task.FromResult(new StockDataResult
                {
                    Endpoint = request.Endpoint,
                    Handled = true,
                    Success = match.Success,
                    Json = match.Json,
                    Error = match.Error
                });
            }

            return Task.FromResult(new StockDataResult
            {
                Endpoint = request.Endpoint,
                Handled = true,
                Success = false,
                Json = "{\"data\":[]}",
                Error = "Endpoint not matched: " + request.Endpoint
            });
        }
    }

    private static string BuildTrendJson(params double[] prices)
    {
        var trends = new List<string>();
        for (int i = 0; i < prices.Length; i++)
        {
            double p = prices[i];
            double avg = prices.Take(i + 1).Average();
            trends.Add($"2026-09-06 09:{30 + i:D2},{p},{p},{p},{p},1000,1000000,{avg:F2}");
        }

        return JsonSerializer.Serialize(new
        {
            data = new
            {
                trends = trends
            }
        });
    }

    private static string BuildQuoteBatchJson(string code, string name, double price, double percent, double amount, double outerVol, double innerVol)
    {
        return BuildMultiQuoteBatchJson((code, name, price, percent, amount, outerVol, innerVol));
    }

    private static string BuildMultiQuoteBatchJson(params (string code, string name, double price, double percent, double amount, double outerVol, double innerVol)[] items)
    {
        var list = items.Select(x => new
        {
            QuoteSchemaVersion = 2,
            Code = x.code,
            Name = x.name,
            Price = x.price,
            PreClose = Math.Round(x.price / (1.0 + x.percent / 100.0), 2),
            Percent = x.percent,
            Amount = x.amount,
            Volume = 10000.0,
            OuterVolume = (double?)x.outerVol,
            InnerVolume = (double?)x.innerVol
        }).ToList();

        return JsonSerializer.Serialize(new { data = list });
    }

    private static string BuildPassingKlineJson(double latestPrice)
    {
        // Generates 65 daily bars with MA5 > MA10 > MA20 and tight adhesion (< 1.5%)
        var rows = new List<object>();
        for (int i = 0; i < 65; i++)
        {
            double basePrice = latestPrice - (65 - i) * 0.05;
            rows.Add(new
            {
                Time = $"2026-06-{(i % 28) + 1:D2}",
                Close = (long)Math.Round(basePrice * 1000),
                Open = (long)Math.Round((basePrice - 0.02) * 1000),
                High = (long)Math.Round((basePrice + 0.05) * 1000),
                Low = (long)Math.Round((basePrice - 0.03) * 1000),
                Volume = 50000.0
            });
        }
        return JsonSerializer.Serialize(new { data = rows });
    }
}
