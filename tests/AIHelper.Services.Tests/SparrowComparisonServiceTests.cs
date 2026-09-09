using System.Collections.Concurrent;
using System.Text.Json;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowComparisonServiceTests
{
    [Fact]
    public async Task Compare_UsesOneSharedQuoteBatchAndOneKlinePerUnionStock()
    {
        var provider = new FakeProvider();
        provider.Set("/api/quote?code=600000,600001", QuoteJson(
            Quote("600000", turnover: 10),
            Quote("600001", outer: 500, inner: 1000, turnover: 10)));
        provider.Set("/api/kline-all?code=600000&type=day&limit=120", KlineJson(PassingCloses(120)));
        var service = new SparrowComparisonService(provider);

        SparrowComparisonResult result = await service.CompareAsync(
            new[] { ("600000", "A"), ("600001", "B") },
            Classic(), V2(), null, CancellationToken.None, exportCsv: false);

        Assert.Equal(1, provider.Count("/api/quote"));
        Assert.Equal(1, provider.Count("/api/kline-all"));
        Assert.Equal(0, provider.Count("/api/trend"));
        Assert.Equal(0, provider.Count("/api/index"));
        Assert.Equal(SparrowComparisonCategory.Both, result.Rows.Single(row => row.Code == "600000").Category);
        Assert.Equal(SparrowComparisonCategory.Neither, result.Rows.Single(row => row.Code == "600001").Category);
        Assert.Equal(1, result.Metrics.IntersectionCount);
        Assert.Single(result.ClassicRanking);
        Assert.Single(result.V2Ranking);
        Assert.Equal(1, result.Rows.Single(row => row.Code == "600000").Classic.Rank);
        Assert.Equal(1, result.Rows.Single(row => row.Code == "600000").V2.Rank);
        Assert.Equal(1, provider.Count("/api/quote"));
        Assert.Equal(1, provider.Count("/api/kline-all"));
    }

    [Fact]
    public async Task Compare_LargeUniverse_UsesFullMarketSnapshotWithoutQuoteBatchesWhenComplete()
    {
        var provider = new FakeProvider();
        var universe = Enumerable.Range(0, 501)
            .Select(index => ($"600{index:D3}", $"公司 {index}"))
            .ToArray();
        object[] quotes = universe
            .Select(stock => Quote(stock.Item1, outer: 500, inner: 1000, turnover: 10))
            .ToArray();
        provider.Set("/api/quote-all", QuoteJson(quotes));

        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            universe, Classic(), V2(), null, CancellationToken.None, false);

        Assert.Equal(1, provider.Count("/api/quote-all"));
        Assert.Equal(0, provider.Count("/api/quote"));
        Assert.Equal(501, result.Rows.Count);
        Assert.Equal(0, result.Metrics.IntersectionCount);
        Assert.All(result.Rows, row => Assert.Equal(SparrowComparisonReasonCodes.P2VolRatio, row.Classic.RejectReasonCode));
    }

    [Fact]
    public void Classic_Latest65From120_IsEquivalentToOriginal65()
    {
        double[] latest65 = PassingCloses(65).Reverse().ToArray();
        var original = new SparrowKlineSnapshot(latest65, latest65[0], 1);
        var shared120 = new SparrowKlineSnapshot(
            latest65.Concat(Enumerable.Repeat(latest65[^1] - 1, 55)).ToArray(),
            latest65[0], 1);

        SparrowTechnicalEvaluation expected = SparrowClassicComparisonEvaluator.Evaluate(original, Classic());
        SparrowTechnicalEvaluation actual = SparrowClassicComparisonEvaluator.Evaluate(shared120, Classic());

        Assert.Equal(expected.Rule.Passed, actual.Rule.Passed);
        Assert.Equal(expected.Rule.ReasonCode, actual.Rule.ReasonCode);
        Assert.Equal(expected.MA5, actual.MA5);
        Assert.Equal(expected.MA10, actual.MA10);
        Assert.Equal(expected.MA20, actual.MA20);
        Assert.Equal(expected.MA60, actual.MA60);
        Assert.Equal(expected.Adhesion, actual.Adhesion);
    }

    [Fact]
    public async Task Compare_ReportsClassicOnlyWhenV2TurnoverRejects()
    {
        var provider = ProviderForSingle(Quote("600000", turnover: 40), PassingCloses(120));
        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "A") }, Classic(), V2(), null, CancellationToken.None, false);

        SparrowComparisonRow row = Assert.Single(result.Rows);
        Assert.True(row.Classic.FinalPassed);
        Assert.False(row.V2.FinalPassed);
        Assert.Equal(SparrowComparisonReasonCodes.P2Turnover, row.V2.RejectReasonCode);
        Assert.Equal(SparrowComparisonCategory.ClassicOnly, row.Category);
        Assert.Equal(1, result.Metrics.ClassicOnlyV2RejectReasons[SparrowComparisonReasonCodes.P2Turnover]);
    }

    [Fact]
    public async Task Compare_ReportsClassicOnlyWhenV2AlphaRejects()
    {
        double[] closes = PassingCloses(120);
        closes[^1] = closes[^2] * 1.01;
        var provider = ProviderForSingle(Quote("600000", turnover: 10), closes);
        SparrowScanParameters v2 = V2();
        v2.CheckAlpha = true;
        provider.Set("/api/trend?code=1.000001", TrendJson(100, 101, 102, 103, 104));
        provider.Set("/api/trend?code=1.000852", TrendJson(100, 101, 102, 103, 104));
        provider.Set("/api/index?code=sh000001&limit=2", IndexJson(3000, 3060));
        SparrowClassicScanParameters classic = Classic();
        classic.MacroDef = true;
        v2.MacroDef = true;

        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "A") }, classic, v2, null, CancellationToken.None, false);

        SparrowComparisonRow row = Assert.Single(result.Rows);
        Assert.True(row.Classic.FinalPassed);
        Assert.Equal(SparrowComparisonReasonCodes.P3Alpha, row.V2.RejectReasonCode);
        Assert.Equal(SparrowComparisonCategory.ClassicOnly, row.Category);
    }

    [Fact]
    public async Task Compare_ReportsClassicOnlyWhenV2MomentumRejectsAtStrictBoundary()
    {
        var provider = ProviderForSingle(Quote("600000", turnover: 10), PassingCloses(120));
        SparrowScanParameters v2 = V2();
        SparrowKlineSnapshot snapshot = SparrowV2RuleEvaluator.ParseKline(KlineJson(PassingCloses(120)))!;
        SparrowTechnicalEvaluation baseline = SparrowV2RuleEvaluator.Evaluate(snapshot, 0, v2);
        v2.MomentumThreshold = baseline.Momentum;

        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "A") }, Classic(), v2, null, CancellationToken.None, false);

        SparrowComparisonRow row = Assert.Single(result.Rows);
        Assert.True(row.Classic.FinalPassed);
        Assert.Equal(SparrowComparisonReasonCodes.P3Momentum, row.V2.RejectReasonCode);
        Assert.Equal(SparrowComparisonCategory.ClassicOnly, row.Category);
    }

    [Fact]
    public async Task Compare_ClassicDefensive_DoesNotStopV2()
    {
        var provider = ProviderForSingle(Quote("600000", turnover: 10), PassingCloses(120));
        provider.Set("/api/trend?code=1.000001", TrendJson(100, 99, 98, 97, 95));
        provider.Set("/api/trend?code=1.000852", TrendJson(100, 99, 98, 97, 95));
        provider.Set("/api/index?code=sh000001&limit=2", IndexJson(3000, 3030));
        SparrowClassicScanParameters classic = Classic();
        SparrowScanParameters v2 = V2();
        classic.MacroDef = true;
        v2.MacroDef = true;

        SparrowComparisonResult result = await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "A") }, classic, v2, null, CancellationToken.None, false);
        SparrowComparisonRow row = Assert.Single(result.Rows);

        Assert.Equal(SparrowComparisonReasonCodes.P1MarketDefensive, row.Classic.RejectReasonCode);
        Assert.True(row.V2.FinalPassed);
        Assert.Equal(SparrowComparisonCategory.V2Only, row.Category);
        Assert.Equal(1, result.Metrics.V2OnlyClassicRejectReasons[SparrowComparisonReasonCodes.P1MarketDefensive]);
    }

    [Fact]
    public async Task Compare_V2Defensive_DoesNotStopClassic()
    {
        var provider = ProviderForSingle(Quote("600000", turnover: 10), PassingCloses(120));
        provider.Set("/api/trend?code=1.000001", TrendJson(100, 101, 102, 103, 104));
        provider.Set("/api/trend?code=1.000852", TrendJson(100, 101, 102, 103, 104));
        provider.Set("/api/index?code=sh000001&limit=2", IndexJson(3000, 2900));
        SparrowClassicScanParameters classic = Classic();
        SparrowScanParameters v2 = V2();
        classic.MacroDef = true;
        v2.MacroDef = true;

        SparrowComparisonRow row = Assert.Single((await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "A") }, classic, v2, null, CancellationToken.None, false)).Rows);

        Assert.True(row.Classic.FinalPassed);
        Assert.Equal(SparrowComparisonReasonCodes.P1MarketDefensive, row.V2.RejectReasonCode);
        Assert.Equal(SparrowComparisonCategory.ClassicOnly, row.Category);
    }

    [Fact]
    public async Task Compare_DistinguishesMissingQuoteAndMissingKlineFromRuleRejects()
    {
        var provider = new FakeProvider();
        provider.Set("/api/quote?code=600000,600001", QuoteJson(Quote("600001", turnover: 10)));
        var result = await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "NoQuote"), ("600001", "NoKline") },
            Classic(), V2(), null, CancellationToken.None, false);

        SparrowComparisonRow noQuote = result.Rows.Single(row => row.Code == "600000");
        SparrowComparisonRow noKline = result.Rows.Single(row => row.Code == "600001");
        Assert.Equal(SparrowRuleOutcome.DataUnavailable, noQuote.Classic.P2.Outcome);
        Assert.Equal(SparrowComparisonReasonCodes.P2QuoteDataMissing, noQuote.V2.RejectReasonCode);
        Assert.Equal(SparrowRuleOutcome.DataUnavailable, noKline.Classic.P3.Outcome);
        Assert.Equal(SparrowComparisonReasonCodes.P3KlineMissing, noKline.V2.RejectReasonCode);
    }

    [Fact]
    public void Metrics_CalculateIntersectionUnionAndDistributions()
    {
        var rows = new List<SparrowComparisonRow>();
        rows.AddRange(MetricRows(3, SparrowComparisonCategory.Both));
        rows.AddRange(MetricRows(4, SparrowComparisonCategory.ClassicOnly, "P2_TURNOVER"));
        rows.AddRange(MetricRows(1, SparrowComparisonCategory.V2Only, "P3_MA60"));

        SparrowComparisonMetrics metrics = SparrowComparisonService.CalculateMetrics(rows, 8);

        Assert.Equal(7, metrics.ClassicFinalCount);
        Assert.Equal(4, metrics.V2FinalCount);
        Assert.Equal(3, metrics.IntersectionCount);
        Assert.Equal(8, metrics.UnionCount);
        Assert.Equal(3.0 / 8.0, metrics.OverlapRate, 10);
        Assert.Equal(4, metrics.ClassicOnlyV2RejectReasons["P2_TURNOVER"]);
        Assert.Equal(1, metrics.V2OnlyClassicRejectReasons["P3_MA60"]);
    }

    [Fact]
    public async Task Compare_UseCacheFalse_RefreshesEverySharedInputExactlyOnce()
    {
        var provider = new FakeProvider();
        provider.Set("/api/trend?code=1.000001&refresh=1", TrendJson(100, 101, 102, 103, 104));
        provider.Set("/api/trend?code=1.000852&refresh=1", TrendJson(100, 101, 102, 103, 104));
        provider.Set("/api/index?code=sh000001&limit=2&refresh=1", IndexJson(3000, 3030));
        provider.Set("/api/quote?code=600000&refresh=1", QuoteJson(Quote("600000", turnover: 10)));
        provider.Set("/api/kline-all?code=600000&type=day&limit=120&refresh=1", KlineJson(PassingCloses(120)));
        SparrowClassicScanParameters classic = Classic();
        SparrowScanParameters v2 = V2();
        classic.MacroDef = true;
        classic.UseCache = false;
        v2.MacroDef = true;
        v2.UseCache = false;

        await new SparrowComparisonService(provider).CompareAsync(
            new[] { ("600000", "A") }, classic, v2, null, CancellationToken.None, false);

        Assert.Equal(2, provider.Requests.Count(request => request.Path == "/api/trend" && request.ForceRefresh));
        Assert.Single(provider.Requests, request => request.Path == "/api/index" && request.ForceRefresh);
        Assert.Single(provider.Requests, request => request.Path == "/api/quote" && request.ForceRefresh);
        Assert.Single(provider.Requests, request => request.Path == "/api/kline-all" && request.ForceRefresh);
    }

    [Fact]
    public async Task Compare_CancellationStopsBeforeExport()
    {
        var provider = new FakeProvider { DelayQuote = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SparrowComparisonService(provider).CompareAsync(
                new[] { ("600000", "A") }, Classic(), V2(), null, cancellation.Token));

        Assert.True(provider.CancellationObserved);
    }

    [Fact]
    public async Task SingleClassicAndV2Modes_KeepTheirOwnKlineRequests()
    {
        var classicProvider = new FakeProvider();
        classicProvider.Set("/api/quote?code=600000", QuoteJson(Quote("600000", turnover: 10)));
        await new SparrowClassicScanner(classicProvider).ScanAsync(
            new[] { ("600000", "A") }, Classic(), null, CancellationToken.None);
        Assert.Contains(classicProvider.Requests,
            request => request.Endpoint == "/api/kline-all?code=600000&type=day&limit=65");
        Assert.DoesNotContain(classicProvider.Requests, request => request.Endpoint.Contains("limit=120"));

        var v2Provider = new FakeProvider();
        v2Provider.Set("/api/quote?code=600000", QuoteJson(Quote("600000", turnover: 10)));
        await new SparrowScannerService(v2Provider).ScanAsync(
            new List<(string Code, string Name)> { ("600000", "A") }, V2(), null, CancellationToken.None);
        Assert.Contains(v2Provider.Requests,
            request => request.Endpoint == "/api/kline-all?code=600000&limit=120");
        Assert.DoesNotContain(v2Provider.Requests, request => request.Endpoint.Contains("limit=65"));
    }

    [Fact]
    public async Task RankingAfterStandaloneScans_DoesNotTriggerAnyAdditionalMarketRequest()
    {
        var classicProvider = new FakeProvider();
        classicProvider.Set("/api/quote?code=600000", QuoteJson(Quote("600000", turnover: 10)));
        classicProvider.Set("/api/kline-all?code=600000&type=day&limit=65", KlineJson(PassingCloses(65)));
        List<SparrowClassicCandidate> classicCandidates = await new SparrowClassicScanner(classicProvider).ScanAsync(
            new[] { ("600000", "A") }, Classic(), null, CancellationToken.None);
        int classicRequestsAfterScan = classicProvider.Requests.Count;

        var engine = new SparrowRankingEngine();
        IReadOnlyList<SparrowRankedCandidate> classicRanking = engine.RankClassic(
            classicCandidates.Select(candidate => candidate.RankingFeatures!), 0, 9.9);

        Assert.Single(classicRanking);
        Assert.Equal(classicCandidates.Select(candidate => candidate.Code),
            classicRanking.Select(candidate => candidate.Code));
        Assert.Equal(classicRequestsAfterScan, classicProvider.Requests.Count);

        var rankingSettings = new SparrowRankingSettings { TopN = 5 };
        Assert.Single(classicRanking.Take(rankingSettings.TopN));
        Assert.Equal(classicRequestsAfterScan, classicProvider.Requests.Count);

        var v2Provider = new FakeProvider();
        v2Provider.Set("/api/quote?code=600000", QuoteJson(Quote("600000", turnover: 10)));
        v2Provider.Set("/api/kline-all?code=600000&limit=120", KlineJson(PassingCloses(120)));
        List<SparrowV2Candidate> v2Candidates = await new SparrowScannerService(v2Provider).ScanWithFeaturesAsync(
            new List<(string Code, string Name)> { ("600000", "A") }, V2(), null, CancellationToken.None);
        int v2RequestsAfterScan = v2Provider.Requests.Count;

        IReadOnlyList<SparrowRankedCandidate> v2Ranking = engine.RankV2(
            v2Candidates.Select(candidate => candidate.RankingFeatures), 0, 9.9, checkAlpha: false);

        Assert.Single(v2Ranking);
        Assert.Equal(v2Candidates.Select(candidate => candidate.Code),
            v2Ranking.Select(candidate => candidate.Code));
        Assert.Equal(v2RequestsAfterScan, v2Provider.Requests.Count);
    }

    [Fact]
    public void StrategyMode_ContainsOnlyClassicV2AndCompare()
    {
        Assert.Equal(
            new[] { SparrowStrategyMode.Classic, SparrowStrategyMode.V2, SparrowStrategyMode.Compare },
            Enum.GetValues<SparrowStrategyMode>());
    }

    private static FakeProvider ProviderForSingle(object quote, double[] closes)
    {
        var provider = new FakeProvider();
        provider.Set("/api/quote?code=600000", QuoteJson(quote));
        provider.Set("/api/kline-all?code=600000&type=day&limit=120", KlineJson(closes));
        return provider;
    }

    private static SparrowClassicScanParameters Classic() => new()
    {
        MacroDef = false,
        MinRise = 0,
        MaxRise = 9.9,
        VolRatio = 1,
        MinAmount = 1,
        CheckMA60 = true,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MaxConcurrency = 4,
        UseCache = true
    };

    private static SparrowScanParameters V2() => new()
    {
        MacroDef = false,
        MinRise = 0,
        MaxRise = 9.9,
        VolRatio = 1,
        MinAmount = 1,
        CheckMA60 = true,
        MinAdhesion = 0,
        MaxAdhesion = 0.15,
        MinTurnover = 3,
        MaxTurnover = 30,
        MomentumThreshold = 0,
        CheckAlpha = false,
        MaxConcurrency = 4,
        UseCache = true
    };

    private static object Quote(
        string code,
        double outer = 1500,
        double inner = 1000,
        double turnover = 10) => new
    {
        QuoteSchemaVersion = 2,
        Code = code,
        Name = code,
        Price = 10.3,
        PreClose = 10.0,
        Percent = 3.0,
        Amount = 100_000_000.0,
        Turnover = turnover,
        OuterVolume = (double?)outer,
        InnerVolume = (double?)inner
    };

    private static string QuoteJson(params object[] items) => JsonSerializer.Serialize(new { data = items });

    private static double[] PassingCloses(int count) =>
        Enumerable.Range(0, count).Select(index => 8.0 + index * 0.02).ToArray();

    private static string KlineJson(double[] closes) => JsonSerializer.Serialize(new
    {
        data = closes.Select((close, index) => new
        {
            Time = $"2026-01-{index + 1:D3}",
            Close = (long)Math.Round(close * 1000)
        })
    });

    private static string TrendJson(params double[] prices) => JsonSerializer.Serialize(new
    {
        data = new
        {
            trends = prices.Select((price, index) =>
                $"2026-09-06 09:{30 + index:D2},{price},{price},{price},{price},1000,1000000,{prices.Take(index + 1).Average():F2}")
        }
    });

    private static string IndexJson(double previous, double latest) => JsonSerializer.Serialize(new
    {
        data = new[]
        {
            new { Close = (long)Math.Round(previous * 1000) },
            new { Close = (long)Math.Round(latest * 1000) }
        }
    });

    private static IEnumerable<SparrowComparisonRow> MetricRows(
        int count,
        SparrowComparisonCategory category,
        string reason = "")
    {
        for (int index = 0; index < count; index++)
        {
            var row = new SparrowComparisonRow { Code = $"{index:D6}", Name = "X", Category = category };
            row.Classic.P2 = SparrowRuleComparison.Pass("P2");
            row.V2.P2 = SparrowRuleComparison.Pass("P2");
            row.Classic.FinalPassed = category is SparrowComparisonCategory.Both or SparrowComparisonCategory.ClassicOnly;
            row.V2.FinalPassed = category is SparrowComparisonCategory.Both or SparrowComparisonCategory.V2Only;
            if (category == SparrowComparisonCategory.ClassicOnly) row.V2.RejectReasonCode = reason;
            if (category == SparrowComparisonCategory.V2Only) row.Classic.RejectReasonCode = reason;
            yield return row;
        }
    }

    private sealed class FakeProvider : IStockDataProvider
    {
        private readonly ConcurrentDictionary<string, string> _routes = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<StockDataRequest> Requests { get; } = new();
        public bool DelayQuote { get; init; }
        public bool CancellationObserved { get; private set; }

        public void Set(string endpoint, string json) => _routes[endpoint] = json;
        public int Count(string path) => Requests.Count(request => request.Path == path);
        public bool CanHandle(StockDataRequest request) => true;

        public async Task<StockDataResult> GetDataAsync(
            StockDataRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (DelayQuote && request.Path == "/api/quote")
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            bool found = _routes.TryGetValue(request.Endpoint, out string? json);
            return new StockDataResult
            {
                Endpoint = request.Endpoint,
                Handled = true,
                Success = found,
                Json = json ?? "",
                Error = found ? "" : "not configured"
            };
        }
    }
}
