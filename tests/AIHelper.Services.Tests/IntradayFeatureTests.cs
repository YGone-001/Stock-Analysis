using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class IntradayFeatureTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 29, 6, 45, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 29);
    private static readonly MarketDataMetadata Meta = new("fixture", false, false, false, DataSourceKind.EastMoney);
    private static MinuteObservation Row(DateTimeOffset t, decimal price = 10, decimal volume = 1, string symbol = "600001") => new(symbol, t, t, t, price, volume, price * volume, "shares", "CNY", Meta, "one minute ending at source timestamp", t, "verified-fixture", true);
    private static MinuteObservation[] Rows(decimal last = 10.2m, string symbol = "600001") => Enumerable.Range(0, 16).Select(i => Row(T.AddMinutes(i - 15), i == 15 ? last : 10, symbol: symbol)).ToArray();
    private static MinuteObservationResult Series(IEnumerable<MinuteObservation> rows) => new(rows.ToArray(), IntradayQuality.Partial, "fixture", Meta);
    private static IntradaySnapshot Snapshot(IEnumerable<MinuteObservation>? rows = null, DateTimeOffset? cutoff = null)
    {
        var list = (rows ?? Rows()).ToArray(); var end = cutoff ?? T;
        return new(1, new(Guid.NewGuid(), "V2", "v2", "fixture-params", 1, end.AddMinutes(-20), end.AddMinutes(-1)), ChinaExchangeTime.Date(end), end, end, end, SessionEvidence.TradingDay, Meta,
            new[] { new SnapshotSymbol("600001", "fixture", 1, 50, "fixture", null, null, end, Meta, IntradayQuality.MissingEventTime, Series(list), IntradayQuality.Partial) }, false);
    }
    private static IntradayQuantityEvidence Quantity() => new("verified-fixture", "600001", "shares", "CNY", 1, 1, true, true, true, "unadjusted shares", "reviewed deterministic trade-amount/share fixture; no live-provider guarantee", TimeSpan.FromMinutes(1), true);
    private static IntradayFeatureResult Analyze(IntradaySnapshot snapshot, IntradayBenchmarkEvidence? benchmark = null, IntradayQuantityEvidence? quantity = null, IReadOnlyList<HistoricalClockSession>? history = null, IntradayFeaturePolicy? policy = null, DateTimeOffset? cutoff = null) => Assert.Single(IntradayFeatureAnalysis.Analyze(snapshot, IntradaySnapshotStore.Fingerprint(snapshot), cutoff ?? snapshot.RequestedAsOf, benchmark, quantity, history, policy));

    [Theory]
    [InlineData("10.2", "2")] [InlineData("9.8", "-2")] [InlineData("10", "0")]
    public void PriceReturnsUseActualFiveAndFifteenMinuteWindows_T01_T04(string price, string expected)
    {
        var r = Analyze(Snapshot(Rows(decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture))));
        decimal value = decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(value, r.Return5m.Value); Assert.Equal(value, r.Return15m.Value);
        Assert.Equal(T.AddMinutes(-5), r.Return5m.WindowStart); Assert.Equal(T, r.Return5m.WindowEnd);
        Assert.Equal(FeatureAvailability.Diagnostic, r.Return5m.Availability); Assert.Equal("%", r.Return5m.Unit);
    }
    [Theory]
    [InlineData(5)] [InlineData(15)] [InlineData(2)]
    public void MissingReferenceOrInternalGapIsUnavailable_T05_T07(int missing)
    {
        var r = Analyze(Snapshot(Rows().Where(row => row.ExchangeEventTime != T.AddMinutes(-missing))));
        if (missing != 15) Assert.Null(r.Return5m.Value);
        Assert.Null(r.Return15m.Value);
    }
    [Fact]
    public void ToleranceIsExplicitEarlierOnlyAndBounded()
    {
        var rows = Rows(); rows[10] = rows[10] with { ExchangeEventTime = T.AddMinutes(-5).AddSeconds(-1) };
        Assert.Null(Analyze(Snapshot(rows)).Return5m.Value);
        var tolerated = Analyze(Snapshot(rows), policy: new(TimeSpan.FromSeconds(1)));
        Assert.Equal(2, tolerated.Return5m.Value); Assert.Equal(T.AddMinutes(-5).AddSeconds(-1), tolerated.Return5m.WindowStart);
        rows[10] = rows[10] with { ExchangeEventTime = T.AddMinutes(-5).AddSeconds(1) };
        Assert.Null(Analyze(Snapshot(rows), policy: new(TimeSpan.FromSeconds(1))).Return5m.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => Analyze(Snapshot(), policy: new(TimeSpan.FromSeconds(31))));
    }
    [Fact]
    public void WrongDayFutureAndPostCutoffAcquisitionCannotContribute_T08_T10()
    {
        var snapshot = Snapshot(Rows().Concat(new[] { Row(T.AddDays(-1), 1000), Row(T.AddMinutes(1), 2000) }));
        var r = Analyze(snapshot);
        Assert.Equal(10.2m, r.LatestPrice.Value); Assert.Equal(0, r.PullbackFromObservedHigh.Value);
        Assert.Contains(r.Warnings, w => w.Contains("WrongTradingDate")); Assert.Contains(r.Warnings, w => w.Contains("FutureData"));
        var acquiredLater = Rows().Select(row => row with { ObservedAtUtc = T.AddMinutes(1), AvailableAtUtc = T.AddMinutes(1), TransportReceivedAtUtc = T.AddMinutes(1) });
        Assert.Null(Analyze(Snapshot(acquiredLater)).LatestPrice.Value);
        Assert.Null(Analyze(Snapshot(), cutoff: T.AddMinutes(1)).LatestPrice.Value);
        Assert.Null(Analyze(Snapshot(Rows().Select(row => row with { ObservedAtUtc = T, AvailableAtUtc = T, TransportReceivedAtUtc = T })), cutoff: T.AddMinutes(-5)).LatestPrice.Value);
    }
    [Theory] [InlineData("zero")] [InlineData("negative")] [InlineData("null")] [InlineData("conflict")]
    public void InvalidPricesAndConflictingDuplicatesCannotSupplyReferences_T11_T12(string invalid)
    {
        var rows = Rows().ToList(); var reference = rows[10];
        if (invalid == "conflict") rows.Add(reference with { PriceYuan = 99 });
        else rows[10] = reference with { PriceYuan = invalid == "zero" ? 0 : invalid == "negative" ? -1 : null };
        var r = Analyze(Snapshot(rows)); Assert.Null(r.Return5m.Value); Assert.NotEmpty(r.Warnings);
        var duplicate = Analyze(Snapshot(Rows().Concat(new[] { Rows()[10] })));
        Assert.Equal(2, duplicate.Return5m.Value); Assert.Equal(15, duplicate.PricePersistence.EligibleTransitions);
    }
    [Fact]
    public void PersistenceDenominatorExcludesMissingIntervals_T13_T14()
    {
        var rows = Rows(10); rows[15] = rows[15] with { PriceYuan = 9 };
        var r = Analyze(Snapshot(rows)); Assert.Equal(15, r.PricePersistence.EligibleTransitions);
        Assert.Equal(14m / 15m, r.PricePersistence.Value);
        r = Analyze(Snapshot(rows.Where(row => row.ExchangeEventTime != T.AddMinutes(-7))));
        Assert.Equal(13, r.PricePersistence.EligibleTransitions); Assert.Equal(12m / 13m, r.PricePersistence.Value);
    }
    [Fact]
    public void PullbackUsesObservedAfternoonHighAndNeverQuoteDailyHigh_T15_T16()
    {
        var snapshot = Snapshot(Rows(9));
        var quote = new QuoteSnapshot("600001", "fixture", 9, 10, -10, null, null, null, null, null, null, 1000, null);
        snapshot = snapshot with { Symbols = new[] { snapshot.Symbols[0] with { Quote = quote } } };
        var r = Analyze(snapshot); Assert.Equal(-10, r.PullbackFromObservedHigh.Value);
        Assert.Equal(T.AddMinutes(-15), r.PullbackFromObservedHigh.WindowStart); Assert.Equal(T, r.PullbackFromObservedHigh.WindowEnd);
    }
    [Theory]
    [InlineData("valid")] [InlineData("ambiguous")] [InlineData("wrong-day")] [InlineData("missing")] [InlineData("shifted")]
    [InlineData("wrong-rows")] [InlineData("acquired-later")]
    public void RelativeStrengthRequiresIdentityDateAndExactMatchingEndpoints_T17_T21(string mode)
    {
        var benchmark = mode == "missing" ? null : new IntradayBenchmarkEvidence(mode == "ambiguous" ? "000001" : "sh000001", mode == "wrong-day" ? Day.AddDays(-1) : Day,
            Series(Rows(10.1m, "sh000001").Select(row => mode switch {
                "shifted" => row with { ExchangeEventTime = row.ExchangeEventTime!.Value.AddMinutes(-1) },
                "wrong-rows" => row with { ExchangeEventTime = row.ExchangeEventTime!.Value.AddDays(-1) },
                "acquired-later" => row with { ObservedAtUtc = T.AddMinutes(1), AvailableAtUtc = T.AddMinutes(1), TransportReceivedAtUtc = T.AddMinutes(1) }, _ => row })));
        var r = Analyze(Snapshot(), benchmark);
        if (mode == "valid") { Assert.Equal(1, r.RelativeStrength5m.Value); Assert.Equal(1, r.RelativeStrength15m.Value); Assert.Contains("sh000001", r.RelativeStrength5m.Reason); Assert.Contains("versus", r.RelativeStrength5m.Source); }
        else { Assert.Null(r.RelativeStrength5m.Value); Assert.False(string.IsNullOrWhiteSpace(r.RelativeStrength5m.Reason)); Assert.Equal(2, r.Return5m.Value); }
    }
    [Fact]
    public void VerifiedIncrementalVolumeAndTradeAmountAreRequired_T22_T28()
    {
        var rows = Rows().Select(row => row with { SourceNumber = row.ExchangeEventTime > T.AddMinutes(-5) ? 2 : 1, SourceAmount = (row.ExchangeEventTime > T.AddMinutes(-5) ? 2 : 1) * 11 }).ToArray();
        var snapshot = Snapshot(rows);
        var unsupported = Analyze(snapshot); Assert.Null(unsupported.VolumeAcceleration.Value); Assert.Null(unsupported.TradeVwap.Value); Assert.Null(unsupported.HistoricalSameClockRvol.Value);
        var supported = Analyze(snapshot, quantity: Quantity()); Assert.Equal(2, supported.VolumeAcceleration.Value); Assert.Equal(11, supported.TradeVwap.Value);
        Assert.NotEqual(rows.Sum(row => row.PriceYuan * row.SourceNumber) / rows.Sum(row => row.SourceNumber), supported.TradeVwap.Value);
        Assert.Null(Analyze(snapshot, quantity: Quantity() with { TradeAmountVerified = false }).TradeVwap.Value);
        var unrelatedOldBadQuantity = rows.ToArray(); unrelatedOldBadQuantity[0] = unrelatedOldBadQuantity[0] with { SourceNumber = null };
        Assert.Equal(2, Analyze(Snapshot(unrelatedOldBadQuantity), quantity: Quantity()).VolumeAcceleration.Value);
        Assert.Null(Analyze(snapshot, quantity: Quantity() with { Incremental = false }).VolumeAcceleration.Value);
        Assert.Null(Analyze(snapshot, quantity: Quantity() with { LabelsAreIntervalEnds = false }).TradeVwap.Value);
        Assert.Null(Analyze(Snapshot(rows.Select(row => row with { VolumeUnit = "unknown", AmountUnit = "unknown" })), quantity: Quantity()).TradeVwap.Value);
        var zero = Analyze(Snapshot(rows.Select(row => row with { SourceNumber = row.ExchangeEventTime <= T.AddMinutes(-5) ? 0 : 2 })), quantity: Quantity());
        Assert.Equal(FeatureAvailability.Undefined, zero.VolumeAcceleration.Availability); Assert.Null(zero.VolumeAcceleration.Value);
        Assert.Contains("weighted", unsupported.TradeVwap.Reason);
    }
    [Fact]
    public void VerifiedFixtureConversionFactorsAreExplicitAndRetained()
    {
        var rows = Rows().Select(row => row with { VolumeUnit = "verified-lot", AmountUnit = "verified-thousand-CNY", SourceNumber = 1, SourceAmount = 1 });
        var evidence = Quantity() with { VolumeUnit = "verified-lot", AmountUnit = "verified-thousand-CNY", SharesPerUnit = 100, CnyPerAmountUnit = 1000 };
        var result = Analyze(Snapshot(rows), quantity: evidence);
        Assert.Equal(10, result.TradeVwap.Value);
        Assert.Equal(evidence, result.QuantityEvidence);
        Assert.Equal(IntradayFeaturePolicy.Default, result.Policy);
    }
    [Fact]
    public void SameClockRvolNeedsGenuineDistinctCompleteComparableSessions_T29()
    {
        var end = T.AddMinutes(-15); // 14:30 China
        MinuteObservation[] Session(DateTimeOffset cutoff, decimal volume) => Enumerable.Range(0, 90).Select(i => Row(cutoff.AddMinutes(i - 89), 10, volume)).ToArray();
        var snapshot = Snapshot(Session(end, 2), end);
        var history = Enumerable.Range(1, 3).Select(i => new HistoricalClockSession("600001", Day.AddDays(-i), end.AddDays(-i), SessionEvidence.TradingDay, Series(Session(end.AddDays(-i), 1)), Quantity())).ToArray();
        var result = Analyze(snapshot, quantity: Quantity(), history: history);
        Assert.Equal(2, result.HistoricalSameClockRvol.Value); Assert.Equal(3, result.HistoricalSameClockRvol.BaselineSamples);
        Assert.Equal(3, result.HistoricalEvidence.Count);
        Assert.Null(Analyze(snapshot, quantity: Quantity(), history: history.Select(h => h with { Cutoff = h.Cutoff.AddMinutes(1) }).ToArray()).HistoricalSameClockRvol.Value);
        Assert.Null(Analyze(snapshot, quantity: Quantity(), history: history.Select(h => h with { Calendar = SessionEvidence.NonTradingDay }).ToArray()).HistoricalSameClockRvol.Value);
        Assert.Null(Analyze(snapshot, quantity: Quantity(), history: history.Select(h => h with { Minutes = Series(h.Minutes.Rows.Select(row => row with { AvailableAtUtc = T, ObservedAtUtc = T, TransportReceivedAtUtc = T })) }).ToArray()).HistoricalSameClockRvol.Value);
    }
    [Fact]
    public void EndpointLagAndProviderCompatibilityAreExplicit()
    {
        Assert.Equal(2, Analyze(Snapshot(Rows(), T.AddMinutes(1))).Return5m.Value);
        Assert.Null(Analyze(Snapshot(Rows(), T.AddMinutes(1).AddSeconds(1))).Return5m.Value);
        var mixed = Rows(); mixed[10] = mixed[10] with { Provider = "other-unverified-adjustment-basis" };
        Assert.Null(Analyze(Snapshot(mixed)).Return5m.Value);
        Assert.Null(Analyze(Snapshot(mixed)).PullbackFromObservedHigh.Value);
    }
    [Fact]
    public void UnverifiedMinuteLabelsPermitOnlyPriceDiagnosticsAndClosingAuctionIsExcluded_T30_T31()
    {
        var snapshot = Snapshot(Rows().Select(row => row with { EventTimeSemanticsVerified = false, VolumeUnit = "unknown", AmountUnit = "unknown", Interval = "source label; unverified interval" }));
        string hash = IntradaySnapshotStore.Fingerprint(snapshot); var r = Analyze(snapshot, quantity: Quantity());
        Assert.Equal(2, r.Return5m.Value); Assert.Null(r.VolumeAcceleration.Value); Assert.Equal("Diagnostic", r.EvidenceStatus); Assert.False(r.SourceSnapshotCertified);
        Assert.Equal(hash, r.SnapshotFingerprint); Assert.Equal(hash, IntradaySnapshotStore.Fingerprint(snapshot));
        Assert.Throws<ArgumentException>(() => IntradayFeatureAnalysis.Analyze(snapshot, "wrong-fingerprint", T));
        var auction = T.AddMinutes(15); var withAuction = Snapshot(new[] { Row(auction, 100) }, auction);
        Assert.Null(Analyze(withAuction).LatestPrice.Value);
        var auctionStart = T.AddMinutes(12);
        Assert.Null(Analyze(Snapshot(new[] { Row(auctionStart) }, auctionStart)).LatestPrice.Value);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => T; }
    private sealed class Provider : IStockDataProvider
    {
        public int Minutes;
        public bool CanHandle(StockDataRequest request) => true;
        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken token = default)
        {
            if (request.Path == "/api/minute") Minutes++;
            string json = request.Path == "/api/workday" ? "{\"data\":{\"is_workday\":true}}" : request.Path == "/api/minute" ? JsonSerializer.Serialize(new { data = new { List = Rows().Select(row => new { Time = row.ExchangeEventTime!.Value.ToOffset(ChinaExchangeTime.Offset).ToString("yyyy-MM-dd HH:mm"), Price = row.PriceYuan * 1000, Number = 1, Amount = 10 }) } }) : "{\"data\":[]}";
            return Task.FromResult(new StockDataResult { Success = true, Source = "EastMoney", SourceKind = DataSourceKind.EastMoney, Json = json });
        }
    }
    private static void Pump(Task task) { if (!task.IsCompleted) { var frame = new DispatcherFrame(); task.GetAwaiter().OnCompleted(() => frame.Continue = false); Dispatcher.PushFrame(frame); } task.GetAwaiter().GetResult(); }
    [Theory]
    [InlineData("success")] [InlineData("cancel")] [InlineData("new-scan")] [InlineData("close")] [InlineData("failure")]
    public void RealScanSnapshotFeatureWindowAndOwnershipWorkflow_T32_T35(string mode)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIHelper-Features", Guid.NewGuid().ToString("N"));
        try {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) => {
                var clock = new Clock(); var provider = new Provider();
                vm.SnapshotClock = clock; vm.SnapshotCapture = new(new MinuteObservationService(provider, clock), new QuoteService(provider), new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { new SparrowRankedCandidate { Code = "600001", Name = "fixture", Rank = 1, RankingProfile = "fixture", Features = new SparrowRankingFeatures { Code = "600001", Name = "fixture" } } });
                Pump(vm.StartCommand.ExecuteAsync(null)); Assert.Equal("Completed", vm.ScanOutcome);
                string path = vm.LastSnapshotPath!; byte[] original = File.ReadAllBytes(path);
                Assert.True(vm.AnalyzeFeaturesCommand.CanExecute(null));
                if (mode == "success") {
                    Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                    var displayed = Assert.Single(vm.FeatureRows); Assert.Equal(2, displayed.Result.Return5m.Value);
                    var grid = Assert.IsType<DataGrid>(window.FindName("FeatureGrid")); Assert.Same(vm.FeatureRows, grid.ItemsSource); Assert.Single(grid.Items.Cast<object>());
                    Assert.Contains("benchmark", displayed.Details); Assert.Contains("Diagnostic", vm.FeatureStatus); Assert.Contains(displayed.Result.SnapshotFingerprint, vm.FeatureStatus);
                    Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(1, provider.Minutes); return;
                }
                if (mode == "failure") {
                    File.WriteAllText(path, "{corrupted"); Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                    Assert.Contains("失败", vm.FeatureStatus); Assert.Equal("Completed", vm.ScanOutcome); Assert.Single(vm.Results); return;
                }
                var pending = new TaskCompletionSource<IReadOnlyList<IntradayFeatureResult>>(); CancellationToken captured = default;
                vm.FeatureAnalysisOverride = (snapshot, hash, cutoff, token) => { captured = token; return pending.Task; };
                var analysis = vm.AnalyzeFeaturesCommand.ExecuteAsync(null);
                if (mode == "cancel") { vm.CancelScanCommand.Execute(null); vm.CancelScanCommand.Execute(null); }
                else if (mode == "new-scan") { vm.EnableIntradaySnapshotCapture = false; Pump(vm.StartCommand.ExecuteAsync(null)); }
                else window.Close();
                Assert.True(captured.IsCancellationRequested);
                int updates = 0; if (mode == "close") vm.PropertyChanged += (_, _) => updates++;
                var retained = new IntradaySnapshotStore(dir).Load(path); pending.SetResult(IntradayFeatureAnalysis.Analyze(retained, IntradaySnapshotStore.Fingerprint(retained), T)); Pump(analysis);
                Assert.Empty(vm.FeatureRows); Assert.Equal("Completed", vm.ScanOutcome); Assert.Single(vm.Results);
                if (mode == "cancel") Assert.Contains("取消", vm.FeatureStatus);
                if (mode == "new-scan") Assert.Contains("尚未分析", vm.FeatureStatus);
                if (mode == "close") Assert.Equal(0, updates);
            });
        } finally { if (Directory.Exists(dir)) { foreach (var file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); } }
    }
    private sealed class HttpFixture : HttpMessageHandler
    {
        public string? SecId;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { SecId = request.RequestUri!.Query; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"klines\":[]}}") }); }
    }
    [Fact]
    public async Task ExplicitSseBenchmarkMapsToMarketOne()
    {
        var handler = new HttpFixture(); using var http = new HttpClient(handler);
        using var cache = new LocalStockCacheProvider(cachePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cache.json"));
        using var native = new EastMoneyStockDataProvider(http, cache);
        await native.GetDataAsync(StockDataRequest.Parse("/api/minute?code=sh000001&date=20261029"));
        Assert.Contains("secid=1.000001", handler.SecId);
    }
}
