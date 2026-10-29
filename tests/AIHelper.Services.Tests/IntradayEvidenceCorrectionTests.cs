using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class IntradayEvidenceCorrectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 6, 45, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 10, 29);
    private static readonly MarketDataMetadata Metadata = new("EastMoney", false, false, false, DataSourceKind.EastMoney);
    private static MinuteObservation Row(int age = 0) => new("600001", Now.AddMinutes(-age), Now, Now, 12m, 0, 0, "shares", "CNY", Metadata, "one minute ending at source timestamp", Now, EventTimeSemanticsVerified: true);
    private static MinuteQualitySummary Summary(params MinuteObservation[] rows) => MinuteQualityAggregation.Summarize(new(rows, IntradayQuality.Partial, "fixture", Metadata), Date, Now, IntradayFreshnessPolicy.Default);

    [Fact]
    public void LatestQualityAndEarlierIntegrityAreIndependent_T01_T06()
    {
        var result = Summary(Row(20), Row());
        Assert.Equal(1, result.LatestRowIndex); Assert.Equal(IntradayQuality.Valid, result.AggregateQuality);
        Assert.Contains(result.Anomalies, a => a.RowIndex == 0 && a.Quality == IntradayQuality.Stale);
        Assert.Equal(IntradayQuality.Stale, Summary(Row(20)).LatestQuality);
        result = Summary(Row(1) with { PriceYuan = -1 }, Row());
        Assert.Equal(IntradayQuality.Valid, result.LatestQuality);
        Assert.Contains(result.Anomalies, a => a.RowIndex == 0 && a.Quality == IntradayQuality.Partial);
        result = Summary(Row(20), Row() with { VolumeUnit = "unknown" });
        Assert.Equal(IntradayQuality.Partial, result.LatestQuality);
        Assert.Equal(IntradayQuality.Partial, result.AggregateQuality);
        result = Summary(Row(), Row() with { ExchangeEventTime = Now.AddMinutes(1) });
        Assert.Equal(0, result.LatestRowIndex); Assert.Equal(IntradayQuality.FutureData, result.AggregateQuality);
        result = Summary(Row(1) with { ExchangeEventTime = Now.AddDays(-1) }, Row());
        Assert.Equal(1, result.LatestRowIndex); Assert.Equal(IntradayQuality.WrongTradingDate, result.AggregateQuality);
        result = Summary(Row(1) with { ExchangeEventTime = null }, Row());
        Assert.Equal(IntradayQuality.MissingEventTime, result.AggregateQuality);
        Assert.Contains(result.Anomalies, a => a.Quality == IntradayQuality.MissingEventTime);
        Assert.Equal(IntradayQuality.Unavailable, Summary().AggregateQuality);
        result = MinuteQualityAggregation.Summarize(new(Array.Empty<MinuteObservation>(), IntradayQuality.SourceFailure, "offline", Metadata), Date, Now, IntradayFreshnessPolicy.Default);
        Assert.Equal(IntradayQuality.SourceFailure, result.AggregateQuality);
        result = Summary(Row(), Row() with { PriceYuan = 13 });
        Assert.Null(result.LatestRowIndex); Assert.Equal(IntradayQuality.InconsistentEvidence, result.AggregateQuality);
        Assert.Equal(2, result.Anomalies.Count);
    }

    [Theory]
    [InlineData("observation", IntradayQuality.InconsistentEvidence)]
    [InlineData("availability", IntradayQuality.InconsistentEvidence)]
    [InlineData("transport-observation", IntradayQuality.InconsistentEvidence)]
    [InlineData("transport-availability", IntradayQuality.InconsistentEvidence)]
    [InlineData("unverified", IntradayQuality.Partial)]
    [InlineData("ambiguous", IntradayQuality.Partial)]
    public void ImpossibleCausalityAndAmbiguousLabels_T07_T09(string change, IntradayQuality expected)
    {
        var row = Row();
        row = change switch {
            "observation" => row with { ObservedAtUtc = Now.AddMinutes(-1) },
            "availability" => row with { AvailableAtUtc = Now.AddMinutes(-1) },
            "transport-observation" => row with { ExchangeEventTime = Now.AddMinutes(-2), AvailableAtUtc = Now.AddMinutes(-2), TransportReceivedAtUtc = Now.AddMinutes(-1) },
            "transport-availability" => row with { ExchangeEventTime = Now.AddMinutes(-2), ObservedAtUtc = Now.AddMinutes(-2), TransportReceivedAtUtc = Now.AddMinutes(-1) },
            "unverified" => row with { EventTimeSemanticsVerified = false, ObservedAtUtc = Now.AddMinutes(-1), AvailableAtUtc = Now.AddMinutes(-1) },
            "ambiguous" => row with { Interval = "unverified period-start label", ObservedAtUtc = Now.AddMinutes(-1), AvailableAtUtc = Now.AddMinutes(-1) }, _ => row };
        Assert.Equal(expected, IntradayCutoff.Validate(row, Date, Now, IntradayFreshnessPolicy.Default));
    }

    private sealed class HttpFixture(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
    [Fact]
    public async Task NativeMinuteParsingDoesNotZeroFill_T10_T13()
    {
        var lines = new[] { "2026-10-29 14:40,1,bad,1,1,bad,bad", "2026-10-29 14:41,1,0,1,1,0,0", "2026-10-29 14:42,1,12.34,1,1,5,100", "2026-10-29 14:43,1,NaN,1,1,Infinity,-1", "2026-10-29 14:44,1,-1,1,1,-2,NaN" };
        using var http = new HttpClient(new HttpFixture(JsonSerializer.Serialize(new { data = new { klines = lines } })));
        using var cache = new LocalStockCacheProvider(cachePath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "cache.json"));
        using var native = new EastMoneyStockDataProvider(http, cache);
        var response = await native.GetDataAsync(StockDataRequest.Parse("/api/minute?code=600001&date=20261029"));
        Assert.True(response.Success);
        using var json = JsonDocument.Parse(response.Json);
        var list = json.RootElement.GetProperty("data").GetProperty("List");
        Assert.Equal(JsonValueKind.Null, list[0].GetProperty("Price").ValueKind); Assert.Equal(JsonValueKind.Null, list[0].GetProperty("Number").ValueKind);
        Assert.Equal(0, list[1].GetProperty("Price").GetInt64()); Assert.Equal(0, list[1].GetProperty("Number").GetDouble());
        Assert.Equal(12340, list[2].GetProperty("Price").GetInt64());
        Assert.Equal(JsonValueKind.Null, list[3].GetProperty("Price").ValueKind); Assert.Equal(JsonValueKind.Null, list[4].GetProperty("Number").ValueKind);
        var normalized = await new MinuteObservationService(native, new Clock()).GetAsync("600001", Date);
        Assert.Null(normalized.Rows[0].PriceYuan); Assert.Null(normalized.Rows[0].SourceNumber);
        Assert.Equal(0, normalized.Rows[1].PriceYuan); Assert.Equal(12.34m, normalized.Rows[2].PriceYuan);
        Assert.All(normalized.Rows, row => { Assert.Equal("EastMoney/kline/klt=1", row.Provider); Assert.Equal("unknown", row.VolumeUnit); Assert.Equal(DataSourceKind.EastMoney, row.Provenance.SourceKind); });
    }
    private sealed class Clock : TimeProvider
    {
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed;
            public void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan due, TimeSpan period) => true;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
        private Timer? _timer;
        public TimeSpan Due;
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { Due = dueTime; return _timer = new Timer(callback, state); }
        public void Expire() => _timer!.Fire();
    }
    private sealed class Provider : IStockDataProvider
    {
        public TaskCompletionSource<StockDataResult>? Pending;
        public CancellationToken CaptureToken;
        public int Calls;
        public DataSourceKind Kind = DataSourceKind.EastMoney;
        public string MinuteJson = "{\"data\":{\"List\":[]}}";
        public bool CanHandle(StockDataRequest request) => true;
        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken token = default)
        {
            if (request.Path == "/api/minute") { Calls++; CaptureToken = token; if (Pending != null) return Pending.Task; }
            return Task.FromResult(new StockDataResult { Success = true, SourceKind = Kind, Source = Kind.ToString(), Json = request.Path == "/api/workday" ? "{\"data\":{\"is_workday\":true}}" : request.Path == "/api/minute" ? MinuteJson : "{\"data\":[]}" });
        }
    }
    [Fact]
    public async Task GatewayMinuteRetainsPythonSourceCacheAndOriginalReceipt_T13()
    {
        var provider = new Provider { Kind = DataSourceKind.ExternalGateway, MinuteJson = """
            {"data":{"List":[{"Time":"2026-10-29 14:44","Price":12340,"Number":2,"Amount":100}]},
             "observation":{"provider":"EastMoney/trends2/ndays=1","observed_at_utc":"2026-10-29T06:44:00+00:00","used_cache":true}}
            """ };
        var result = await new MinuteObservationService(provider, new Clock()).GetAsync("600001", Date);
        var row = Assert.Single(result.Rows);
        Assert.Equal("EastMoney/trends2/ndays=1", row.Provider); Assert.Equal(DataSourceKind.ExternalGateway, row.Provenance.SourceKind);
        Assert.True(row.Provenance.UsedCache); Assert.Equal(Now.AddMinutes(-1), row.ObservedAtUtc);
        Assert.Equal(Now.AddMinutes(-1), row.AvailableAtUtc); Assert.Equal(Now, row.TransportReceivedAtUtc);
        Assert.Equal(12.34m, row.PriceYuan); Assert.Equal("unknown", row.VolumeUnit); Assert.False(row.EventTimeSemanticsVerified);
    }
    private static SparrowRankedCandidate Candidate() => new() { Code = "600001", Name = "fixture", Rank = 1, RankingProfile = "fixture", Features = new SparrowRankingFeatures { Code = "600001", Name = "fixture" } };
    private static void Pump(Task task)
    {
        if (!task.IsCompleted) { var frame = new DispatcherFrame(); task.GetAwaiter().OnCompleted(() => frame.Continue = false); Dispatcher.PushFrame(frame); }
        task.GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BoundedCaptureIsSeparateAndCannotRelabelNextRun_T14_T19(bool close)
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) => {
            string dir = Path.Combine(Path.GetTempPath(), "AIHelper-Evidence", Guid.NewGuid().ToString("N"));
            var clock = new Clock(); var provider = new Provider { Pending = new() };
            vm.SnapshotClock = clock; vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true; vm.CaptureTimeout = TimeSpan.FromSeconds(20);
            vm.SnapshotCapture = new(new MinuteObservationService(provider, clock), new QuoteService(provider), new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate() });
            vm.SystemSettings.AutoScanEnabled = true;
            vm.AutoScanTick(new DateTime(2026, 10, 29, 14, 0, 0)); var first = vm.StartCommand.ExecutionTask!;
            Assert.False(vm.IsScanning); Assert.True(vm.IsCapturing); Assert.Equal("Completed", vm.ScanOutcome);
            Assert.False(vm.StartCommand.CanExecute(null)); Assert.Equal(TimeSpan.FromSeconds(20), clock.Due);
            Assert.Single(vm.Results);
            if (close) {
                window.Close(); Assert.True(provider.CaptureToken.IsCancellationRequested);
                int updates = 0; vm.PropertyChanged += (_, _) => updates++;
                Pump(first); provider.Pending.SetResult(new StockDataResult { Success = false });
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, updates); Assert.Null(vm.LastSnapshotPath); return;
            }
            clock.Expire(); Pump(first);
            Assert.Contains("快照采集超时", vm.SnapshotStatus); Assert.False(vm.IsCapturing); Assert.True(provider.CaptureToken.IsCancellationRequested);
            Assert.Equal("Completed", vm.ScanOutcome); Assert.Single(vm.Results);
            // A source that ignored cancellation must not accumulate another acquisition job.
            Pump(vm.StartCommand.ExecuteAsync(null));
            Assert.Contains("快照采集失败", vm.SnapshotStatus); Assert.Equal(1, provider.Calls);
            vm.EnableIntradaySnapshotCapture = false;
            vm.AutoScanTick(new DateTime(2026, 10, 29, 14, 5, 0)); Pump(vm.StartCommand.ExecutionTask!);
            string secondStatus = vm.SnapshotStatus; Assert.Contains("本次未启用", secondStatus);
            provider.Pending.SetResult(new StockDataResult { Success = false });
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(secondStatus, vm.SnapshotStatus); Assert.Null(vm.LastSnapshotPath);
            Assert.Equal(1, provider.Calls); Assert.False(Directory.Exists(dir));
        });
    }
    [Fact]
    public void PreviousArtifactRemainsClearlyOwnedAndLegacyFingerprintLoads_T19_T20()
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) => {
            string dir = Path.Combine(Path.GetTempPath(), "AIHelper-Evidence", Guid.NewGuid().ToString("N"));
            try {
                var clock = new Clock(); var provider = new Provider();
                vm.SnapshotClock = clock; vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.SnapshotCapture = new(new MinuteObservationService(provider, clock), new QuoteService(provider), new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate() });
                Pump(vm.StartCommand.ExecuteAsync(null)); string path = vm.LastSnapshotPath!; var id = vm.LastSnapshotRunId;
                vm.EnableIntradaySnapshotCapture = false; Pump(vm.StartCommand.ExecuteAsync(null));
                Assert.Equal(path, vm.LastSnapshotPath); Assert.True(vm.OpenSnapshotCommand.CanExecute(null));
                Assert.Contains("本次未启用", vm.SnapshotStatus); Assert.Contains($"上次文件所属运行 {id}", vm.SnapshotStatus);
                var envelope = JsonNode.Parse(File.ReadAllText(path))!;
                foreach (var symbol in envelope["Snapshot"]!["Symbols"]!.AsArray()) symbol!.AsObject().Remove("MinuteSummary");
                // Typed DateTimeOffset formatting preserves the original serializer's raw '+';
                // JsonNode would re-escape it, which is not an original schema-1 fingerprint.
                string legacyJson = JsonSerializer.Serialize(JsonSerializer.Deserialize<IntradaySnapshot>(envelope["Snapshot"]!.ToJsonString()));
                Assert.DoesNotContain("MinuteSummary", legacyJson);
                Assert.DoesNotContain("EventTimeSemanticsVerified", legacyJson);
                envelope["Fingerprint"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyJson)));
                File.WriteAllText(path, envelope.ToJsonString());
                var loaded = new IntradaySnapshotStore(dir).Load(path);
                Assert.Null(Assert.Single(loaded.Symbols).MinuteSummary); Assert.False(loaded.Certified);
                Assert.Equal(envelope["Fingerprint"]!.GetValue<string>(), IntradaySnapshotStore.Fingerprint(loaded));
            } finally { if (Directory.Exists(dir)) { foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); } }
        });
    }
}
