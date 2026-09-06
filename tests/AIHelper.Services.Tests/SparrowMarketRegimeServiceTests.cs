using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowMarketRegimeServiceTests
{
    // ==========================================
    // Case 1 to 6: Core Market Regime Combinations
    // ==========================================

    [Fact]
    public void Combine_Case1_BothStrong_ReturnsDefensiveFalse()
    {
        var sh = new SparrowIndexSnapshot { State = SparrowMarketState.Strong };
        var csi = new SparrowIndexSnapshot { State = SparrowMarketState.Strong };

        SparrowMarketRegime regime = SparrowMarketRegimeService.Combine(sh, csi);

        Assert.False(regime.Defensive);
        Assert.Equal(SparrowMarketState.Strong, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Strong, regime.Csi1000);
    }

    [Fact]
    public void Combine_Case2_ShanghaiWeakAndCsiStrong_ReturnsDefensiveFalse()
    {
        var sh = new SparrowIndexSnapshot { State = SparrowMarketState.Weak };
        var csi = new SparrowIndexSnapshot { State = SparrowMarketState.Strong };

        SparrowMarketRegime regime = SparrowMarketRegimeService.Combine(sh, csi);

        Assert.False(regime.Defensive);
        Assert.Equal(SparrowMarketState.Weak, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Strong, regime.Csi1000);
    }

    [Fact]
    public void Combine_Case3_ShanghaiStrongAndCsiWeak_ReturnsDefensiveFalse()
    {
        var sh = new SparrowIndexSnapshot { State = SparrowMarketState.Strong };
        var csi = new SparrowIndexSnapshot { State = SparrowMarketState.Weak };

        SparrowMarketRegime regime = SparrowMarketRegimeService.Combine(sh, csi);

        Assert.False(regime.Defensive);
        Assert.Equal(SparrowMarketState.Strong, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Weak, regime.Csi1000);
    }

    [Fact]
    public void Combine_Case4_BothWeak_ReturnsDefensiveTrue()
    {
        var sh = new SparrowIndexSnapshot { State = SparrowMarketState.Weak };
        var csi = new SparrowIndexSnapshot { State = SparrowMarketState.Weak };

        SparrowMarketRegime regime = SparrowMarketRegimeService.Combine(sh, csi);

        Assert.True(regime.Defensive);
        Assert.Equal(SparrowMarketState.Weak, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Weak, regime.Csi1000);
        Assert.Contains("双指数弱势熔断", regime.Reason);
    }

    [Fact]
    public void Combine_Case5_ShanghaiUnknownAndCsiWeak_ReturnsDefensiveFalse_FailOpen()
    {
        var sh = new SparrowIndexSnapshot { State = SparrowMarketState.Unknown };
        var csi = new SparrowIndexSnapshot { State = SparrowMarketState.Weak };

        SparrowMarketRegime regime = SparrowMarketRegimeService.Combine(sh, csi);

        Assert.False(regime.Defensive);
        Assert.Equal(SparrowMarketState.Unknown, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Weak, regime.Csi1000);
        Assert.Contains("Fail-open", regime.Reason);
    }

    [Fact]
    public void Combine_Case6_BothUnknown_ReturnsDefensiveFalse_FailOpenWithUnavailableNotice()
    {
        var sh = new SparrowIndexSnapshot { State = SparrowMarketState.Unknown };
        var csi = new SparrowIndexSnapshot { State = SparrowMarketState.Unknown };

        SparrowMarketRegime regime = SparrowMarketRegimeService.Combine(sh, csi);

        Assert.False(regime.Defensive);
        Assert.Equal(SparrowMarketState.Unknown, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Unknown, regime.Csi1000);
        Assert.Contains("Market data unavailable", regime.Reason);
    }

    // ==========================================
    // Case 7: Regression Tests for limit=5 Bug
    // ==========================================

    [Fact]
    public void Regression_EvaluateFromPrices_WithOnly5Samples_ReturnsUnknown_GuardsAgainstOverlappingWindows()
    {
        // When exactly 5 samples are provided for a windowSize=5 calculation,
        // TakeLast(5) and Take(5) would overlap completely.
        // The algorithm MUST reject this as insufficient samples (needs at least 10).
        var fiveCloses = new double[] { 3000.0, 2990.0, 2980.0, 2970.0, 2960.0 };

        SparrowIndexSnapshot snapshot = SparrowMarketRegimeService.EvaluateFromPrices(fiveCloses, windowSize: 5);

        Assert.Equal(SparrowMarketState.Unknown, snapshot.State);
        Assert.Equal(5, snapshot.Samples);
        Assert.Contains("Insufficient samples", snapshot.Message);
    }

    [Fact]
    public void Regression_EvaluateTrendPoints_WithLessThan5Samples_ReturnsUnknown()
    {
        var fourPoints = new List<SparrowIndexPoint>
        {
            new() { TimeString = "09:30", Price = 3000, AveragePrice = 3000 },
            new() { TimeString = "09:31", Price = 2990, AveragePrice = 2995 },
            new() { TimeString = "09:32", Price = 2980, AveragePrice = 2990 },
            new() { TimeString = "09:33", Price = 2970, AveragePrice = 2985 }
        };

        SparrowIndexSnapshot snapshot = SparrowMarketRegimeService.EvaluateTrendPoints(fourPoints);

        Assert.Equal(SparrowMarketState.Unknown, snapshot.State);
        Assert.Equal(4, snapshot.Samples);
        Assert.Contains("Insufficient samples", snapshot.Message);
    }

    // ==========================================
    // Case 8: Time Series Ordering Tests
    // ==========================================

    [Fact]
    public void TimeSeries_OldestFirstAndNewestFirst_ProduceIdenticalEvaluationResults()
    {
        // Oldest-first sequence: 09:30 -> 09:34 (prices 100 -> 96, average 100 -> 98)
        var oldestFirst = new List<SparrowIndexPoint>
        {
            new() { TimeString = "09:30", Price = 100, AveragePrice = 100.0 },
            new() { TimeString = "09:31", Price = 99, AveragePrice = 99.5 },
            new() { TimeString = "09:32", Price = 98, AveragePrice = 99.0 },
            new() { TimeString = "09:33", Price = 97, AveragePrice = 98.5 },
            new() { TimeString = "09:34", Price = 96, AveragePrice = 98.0 }
        };

        // Newest-first sequence: 09:34 -> 09:30 (reversed order)
        var newestFirst = oldestFirst.AsEnumerable().Reverse().ToList();

        SparrowIndexSnapshot evalOldest = SparrowMarketRegimeService.EvaluateTrendPoints(oldestFirst);
        SparrowIndexSnapshot evalNewest = SparrowMarketRegimeService.EvaluateTrendPoints(newestFirst);

        Assert.Equal(SparrowMarketState.Weak, evalOldest.State);
        Assert.Equal(SparrowMarketState.Weak, evalNewest.State);
        Assert.Equal(evalOldest.LatestPrice, evalNewest.LatestPrice);
        Assert.Equal(evalOldest.LatestAverage, evalNewest.LatestAverage);
        Assert.Equal(evalOldest.EarlierAverage, evalNewest.EarlierAverage);
    }

    // ==========================================
    // Case 9: Historical Math Definition Check
    // ==========================================

    [Fact]
    public void EvaluateTrendPoints_WeakWhenLatestPriceBelowAverageAndAverageFalling()
    {
        // latestPrice (95) < latestAverage (98) && latestAverage (98) < earlierAverage (100) => Weak
        var points = new List<SparrowIndexPoint>
        {
            new() { TimeString = "09:30", Price = 101, AveragePrice = 100.0 },
            new() { TimeString = "09:31", Price = 99, AveragePrice = 99.5 },
            new() { TimeString = "09:32", Price = 98, AveragePrice = 99.0 },
            new() { TimeString = "09:33", Price = 97, AveragePrice = 98.5 },
            new() { TimeString = "09:34", Price = 95, AveragePrice = 98.0 }
        };

        SparrowIndexSnapshot snapshot = SparrowMarketRegimeService.EvaluateTrendPoints(points);

        Assert.Equal(SparrowMarketState.Weak, snapshot.State);
        Assert.Equal(95.0, snapshot.LatestPrice);
        Assert.Equal(98.0, snapshot.LatestAverage);
        Assert.Equal(100.0, snapshot.EarlierAverage);
    }

    [Fact]
    public void EvaluateTrendPoints_StrongWhenLatestPriceAboveAverageAndAverageRising()
    {
        // latestPrice (105) > latestAverage (102) && latestAverage (102) > earlierAverage (100) => Strong
        var points = new List<SparrowIndexPoint>
        {
            new() { TimeString = "09:30", Price = 99, AveragePrice = 100.0 },
            new() { TimeString = "09:31", Price = 101, AveragePrice = 100.5 },
            new() { TimeString = "09:32", Price = 102, AveragePrice = 101.0 },
            new() { TimeString = "09:33", Price = 103, AveragePrice = 101.5 },
            new() { TimeString = "09:34", Price = 105, AveragePrice = 102.0 }
        };

        SparrowIndexSnapshot snapshot = SparrowMarketRegimeService.EvaluateTrendPoints(points);

        Assert.Equal(SparrowMarketState.Strong, snapshot.State);
    }

    [Fact]
    public void EvaluateTrendPoints_NeutralWhenPriceAboveAverageButAverageFalling()
    {
        // latestPrice (99) > latestAverage (98), but latestAverage (98) < earlierAverage (100) => Neutral
        var points = new List<SparrowIndexPoint>
        {
            new() { TimeString = "09:30", Price = 101, AveragePrice = 100.0 },
            new() { TimeString = "09:31", Price = 99, AveragePrice = 99.5 },
            new() { TimeString = "09:32", Price = 98, AveragePrice = 99.0 },
            new() { TimeString = "09:33", Price = 97, AveragePrice = 98.5 },
            new() { TimeString = "09:34", Price = 99, AveragePrice = 98.0 }
        };

        SparrowIndexSnapshot snapshot = SparrowMarketRegimeService.EvaluateTrendPoints(points);

        Assert.Equal(SparrowMarketState.Neutral, snapshot.State);
    }

    // ==========================================
    // Case 10: JSON Parsing of EastMoney Format
    // ==========================================

    [Fact]
    public void ParseTrendPointsFromJson_ParsesEastMoneyTrendsFormatCorrectly()
    {
        string json = @"
{
    ""rc"": 0,
    ""data"": {
        ""code"": ""000001"",
        ""name"": ""上证指数"",
        ""trends"": [
            ""2026-09-04 09:30,3050.00,3051.00,3052.00,3049.00,10000,50000000,3050.50"",
            ""2026-09-04 09:31,3051.00,3052.00,3053.00,3050.00,12000,60000000,3051.20"",
            ""2026-09-04 09:32,3052.00,3053.00,3054.00,3051.00,15000,75000000,3051.80"",
            ""2026-09-04 09:33,3053.00,3054.00,3055.00,3052.00,18000,90000000,3052.40"",
            ""2026-09-04 09:34,3054.00,3055.00,3056.00,3053.00,20000,100000000,3053.00""
        ]
    }
}";

        List<SparrowIndexPoint> points = SparrowMarketRegimeService.ParseTrendPointsFromJson(json);

        Assert.Equal(5, points.Count);
        Assert.Equal("2026-09-04 09:30", points[0].TimeString);
        Assert.Equal(3051.00, points[0].Price);
        Assert.Equal(3050.50, points[0].AveragePrice);
        Assert.Equal(3055.00, points[^1].Price);
        Assert.Equal(3053.00, points[^1].AveragePrice);
    }

    // ==========================================
    // Case 11 & 12: End-to-End Service & Scanner Tests
    // ==========================================

    [Fact]
    public async Task EvaluateAsync_WhenBothWeak_ReturnsDefensiveTrue()
    {
        string weakJson = @"
{
    ""data"": {
        ""trends"": [
            ""2026-09-04 09:30,3000,3000,3005,2995,100,500,3000.0"",
            ""2026-09-04 09:31,3000,2990,3000,2985,100,500,2995.0"",
            ""2026-09-04 09:32,2990,2980,2990,2975,100,500,2990.0"",
            ""2026-09-04 09:33,2980,2970,2980,2965,100,500,2985.0"",
            ""2026-09-04 09:34,2970,2950,2970,2945,100,500,2980.0""
        ]
    }
}";

        var fakeProvider = new FakeStockDataProvider();
        fakeProvider.SetResponse("/api/trend?code=1.000001", weakJson);
        fakeProvider.SetResponse("/api/trend?code=1.000852", weakJson);

        var service = new SparrowMarketRegimeService(fakeProvider);
        SparrowMarketRegime regime = await service.EvaluateAsync();

        Assert.True(regime.Defensive);
        Assert.Equal(SparrowMarketState.Weak, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Weak, regime.Csi1000);
    }

    [Fact]
    public async Task EvaluateAsync_WhenNetworkFails_ReturnsDefensiveFalse_FailOpen()
    {
        var fakeProvider = new FakeStockDataProvider();
        // Return failure for both indices
        fakeProvider.SetFailure("/api/trend?code=1.000001", "Connection timeout");
        fakeProvider.SetFailure("/api/trend?code=1.000852", "Connection timeout");

        var service = new SparrowMarketRegimeService(fakeProvider);
        SparrowMarketRegime regime = await service.EvaluateAsync();

        Assert.False(regime.Defensive);
        Assert.Equal(SparrowMarketState.Unknown, regime.Shanghai);
        Assert.Equal(SparrowMarketState.Unknown, regime.Csi1000);
        Assert.Contains("Fail-open", regime.Reason);
    }

    [Fact]
    public async Task Scanner_WhenMacroDefIsFalse_BypassesMarketRegimeCheckEntirely()
    {
        var fakeProvider = new FakeStockDataProvider();
        // Do NOT set any responses for /api/trend; if called, it would record requests
        var scanner = new SparrowClassicScanner(fakeProvider);

        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = false, // DISABLED
            UseCache = false
        };

        var targetPool = new List<(string Code, string Name)>
        {
            ("600000", "浦发银行")
        };

        var results = await scanner.ScanAsync(targetPool, parameters, null, CancellationToken.None);

        // Verify zero calls were made to /api/trend
        Assert.DoesNotContain(fakeProvider.RequestedEndpoints, ep => ep.StartsWith("/api/trend"));
    }

    [Fact]
    public async Task Scanner_WhenMacroDefIsTrueAndBothWeak_EntersDefensiveAndReturnsEmpty()
    {
        string weakJson = @"
{
    ""data"": {
        ""trends"": [
            ""2026-09-04 09:30,3000,3000,3005,2995,100,500,3000.0"",
            ""2026-09-04 09:31,3000,2990,3000,2985,100,500,2995.0"",
            ""2026-09-04 09:32,2990,2980,2990,2975,100,500,2990.0"",
            ""2026-09-04 09:33,2980,2970,2980,2965,100,500,2985.0"",
            ""2026-09-04 09:34,2970,2950,2970,2945,100,500,2980.0""
        ]
    }
}";
        var fakeProvider = new FakeStockDataProvider();
        fakeProvider.SetResponse("/api/trend?code=1.000001", weakJson);
        fakeProvider.SetResponse("/api/trend?code=1.000852", weakJson);

        var scanner = new SparrowClassicScanner(fakeProvider);

        var parameters = new SparrowClassicScanParameters
        {
            MacroDef = true,
            UseCache = false
        };

        var targetPool = new List<(string Code, string Name)>
        {
            ("600000", "浦发银行")
        };

        var logs = new List<string>();
        var progress = new Progress<SparrowClassicScanReport>(report =>
        {
            if (!string.IsNullOrEmpty(report.LogMessage))
            {
                logs.Add(report.LogMessage);
            }
        });

        var results = await scanner.ScanAsync(targetPool, parameters, progress, CancellationToken.None);

        Assert.Empty(results);
        Assert.Contains(logs, log => log.Contains("双指数弱势，进入防守模式，本次不执行选股"));
    }

    private sealed class FakeStockDataProvider : IStockDataProvider
    {
        private readonly Dictionary<string, (bool Success, string Json, string Error)> _routes =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> RequestedEndpoints { get; } = new();

        public void SetResponse(string endpoint, string json)
        {
            _routes[endpoint] = (true, json, "");
        }

        public void SetFailure(string endpoint, string error)
        {
            _routes[endpoint] = (false, "", error);
        }

        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            RequestedEndpoints.Add(request.Endpoint);
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
                Error = "Endpoint not configured in fake"
            });
        }
    }
}
