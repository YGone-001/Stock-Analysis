using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class IntradaySnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 6, 45, 0, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 10, 29);
    private static readonly MarketDataMetadata Metadata = new("EastMoney", false, false, false, DataSourceKind.EastMoney);
    private sealed class Clock : TimeProvider { public DateTimeOffset Value = Now; public override DateTimeOffset GetUtcNow() => Value; }
    private sealed class Provider : IStockDataProvider
    {
        public string MinuteJson = "{\"data\":{\"List\":[{\"Time\":\"2026-10-29 14:45\",\"Price\":12340,\"Number\":12,\"Amount\":1000}]}}";
        public string CalendarJson = "{\"data\":{\"is_workday\":true}}";
        public bool Fail, Cache;
        public TaskCompletionSource<StockDataResult>? PendingMinute;
        public int MinuteCalls;
        public bool CanHandle(StockDataRequest request) => true;
        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Path == "/api/minute") { MinuteCalls++; if (PendingMinute != null) return PendingMinute.Task; }
            return Task.FromResult(new StockDataResult { Success = !Fail, UsedCache = Cache, Source = "EastMoney", SourceKind = DataSourceKind.EastMoney,
                Json = request.Path == "/api/minute" ? MinuteJson : request.Path == "/api/workday" ? CalendarJson : "{\"data\":[{\"Code\":\"600001\",\"Name\":\"fixture\",\"Price\":12.34}]}" });
        }
    }
    private static SparrowRankedCandidate Candidate(string code = "600001") => new() { Code = code, Name = code, Rank = 1, TotalScore = 20, RankingProfile = "fixture", Features = new SparrowRankingFeatures { Code = code, Name = code } };
    private static MinuteObservation Row() => new("600001", Now, Now, Now, 12.34m, 12, 1000, "shares", "CNY", Metadata, "one minute ending at source timestamp", EventTimeSemanticsVerified: true);
    private static IntradayScanContext Context() => new(Guid.NewGuid(), "V2", "v2", "fixture-parameters", 1, Now.AddMinutes(-15), Now.AddMinutes(-3));
    private static IntradaySnapshotCapture Capture(Provider provider, string directory, Clock? clock = null) => new(new MinuteObservationService(provider, clock ?? new Clock()), new QuoteService(provider), new MarketCalendarService(provider), new IntradaySnapshotStore(directory), clock ?? new Clock());
    private static string Temp() => Path.Combine(Path.GetTempPath(), "AIHelper-Intraday-Tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("2026-10-28T17:00:00-07:00", 29)]
    [InlineData("2026-10-29T15:59:59Z", 29)]
    [InlineData("2026-10-29T16:00:00Z", 30)]
    public void ExchangeDateDoesNotUseHostZone_T01_T02(string instant, int day)
    {
        var time = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(day, ChinaExchangeTime.Date(time).Day);
        Assert.Equal(TimeSpan.FromHours(8), time.ToOffset(ChinaExchangeTime.Offset).Offset);
    }

    [Theory]
    [InlineData("valid", IntradayQuality.Valid)]
    [InlineData("missing", IntradayQuality.MissingEventTime)]
    [InlineData("future", IntradayQuality.FutureData)]
    [InlineData("date", IntradayQuality.WrongTradingDate)]
    [InlineData("receipt", IntradayQuality.FutureData)]
    [InlineData("cache", IntradayQuality.Unavailable)]
    [InlineData("stale", IntradayQuality.Stale)]
    [InlineData("units", IntradayQuality.Partial)]
    [InlineData("number", IntradayQuality.Partial)]
    public void CutoffFailsClosed_T03_T13(string condition, IntradayQuality expected)
    {
        var row = Row();
        row = condition switch {
            "missing" => row with { ExchangeEventTime = null }, "future" => row with { ExchangeEventTime = Now.AddMinutes(1) },
            "date" => row with { ExchangeEventTime = Now.AddDays(-1) }, "receipt" => row with { AvailableAtUtc = Now.AddMinutes(1) },
            "cache" => row with { AvailableAtUtc = null, Provenance = Metadata with { UsedCache = true } },
            "stale" => row with { ExchangeEventTime = Now.AddMinutes(-6) }, "units" => row with { VolumeUnit = "unknown" },
            "number" => row with { PriceYuan = -1 }, _ => row };
        Assert.Equal(expected, IntradayCutoff.Validate(row, Date, Now, IntradayFreshnessPolicy.Default));
        Assert.Equal(IntradayQuality.FutureData, IntradayCutoff.Validate(Row(), Date, Now.AddMinutes(-15), IntradayFreshnessPolicy.Default));
    }

    [Fact]
    public async Task AdapterAuditsPriceAndUnknownUnitsAndCache_T07_T10_T12()
    {
        var provider = new Provider(); var service = new MinuteObservationService(provider, new Clock());
        var result = await service.GetAsync("600001", Date);
        var row = Assert.Single(result.Rows);
        Assert.Equal(12.34m, row.PriceYuan); Assert.Equal(12, row.SourceNumber); Assert.Equal(1000, row.SourceAmount);
        Assert.Equal("unknown", row.VolumeUnit); Assert.Equal("unknown", row.AmountUnit);
        Assert.Equal(Now, row.AvailableAtUtc); Assert.Equal("EastMoney/kline/klt=1", row.Provider);
        provider.Cache = true;
        Assert.Null(Assert.Single((await service.GetAsync("600001", Date)).Rows).AvailableAtUtc);
        provider.MinuteJson = "{\"data\":{\"List\":[{\"Time\":\"bad\",\"Price\":\"NaN\"},{\"Time\":\"2026-10-29 14:45\"},{\"Time\":\"2026-10-29 14:45\"}]}}";
        result = await service.GetAsync("600001", Date);
        Assert.Equal(3, result.Rows.Count); Assert.Contains(result.Rows, r => r.ExchangeEventTime is null && r.PriceYuan is null);
        Assert.Equal(IntradayQuality.Partial, result.Status);
    }
    [Fact]
    public async Task WrongDateEmptyAndFailureAreExplicit_T06_T14_T17()
    {
        var provider = new Provider(); var service = new MinuteObservationService(provider, new Clock());
        Assert.Equal(IntradayQuality.WrongTradingDate, (await service.GetAsync("600001", Date.AddDays(-1))).Status);
        provider.MinuteJson = "{\"data\":{\"List\":[]}}";
        Assert.Equal(IntradayQuality.Unavailable, (await service.GetAsync("600001", Date.AddDays(-1))).Status);
        provider.Fail = true;
        Assert.Equal(IntradayQuality.SourceFailure, (await service.GetAsync("600001", Date)).Status);
        provider.Fail = false; provider.MinuteJson = "{broken";
        Assert.Equal(IntradayQuality.SourceFailure, (await service.GetAsync("600001", Date)).Status);
    }
    [Theory]
    [InlineData("{\"data\":{\"is_workday\":true}}", SessionEvidence.TradingDay)]
    [InlineData("{\"data\":{\"is_workday\":false}}", SessionEvidence.NonTradingDay)]
    [InlineData("{\"data\":{\"is_workday\":true,\"source\":\"weekday-heuristic\"}}", SessionEvidence.Unavailable)]
    [InlineData("{\"data\":{\"is_workday\":false,\"calendar_evidence\":\"unavailable\"}}", SessionEvidence.Unavailable)]
    [InlineData("{}", SessionEvidence.Unavailable)]
    public async Task CalendarKnownAndUnknownAreSeparate_T15_T16(string json, SessionEvidence expected)
    {
        string dir = Temp();
        try { var saved = await Capture(new Provider { CalendarJson = json }, dir).CaptureAsync(Context(), new[] { Candidate() }, default); Assert.Equal(expected, saved.Snapshot.Calendar); Assert.False(saved.Snapshot.Certified); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task PersistedObservationRoundTripsAndRejectsCorruption_T19_T20_T23_T25()
    {
        string dir = Temp();
        try {
            var context = Context(); var provider = new Provider();
            var saved = await Capture(provider, dir).CaptureAsync(context, new[] { Candidate(), Candidate("600002") }, default);
            Assert.Equal(1, provider.MinuteCalls); Assert.True(File.Exists(saved.Path));
            var store = new IntradaySnapshotStore(dir); var loaded = store.Load(saved.Path);
            Assert.Equal(context, loaded.Scan); Assert.Equal(Now, loaded.RequestedAsOf); Assert.Equal(Now, loaded.CapturedAtUtc);
            Assert.False(loaded.Certified); Assert.Null(Assert.Single(loaded.Symbols).QuoteExchangeEventTime);
            Assert.Equal(IntradayQuality.MissingEventTime, loaded.Symbols[0].QuoteQuality);
            Assert.Equal(IntradaySnapshotStore.Fingerprint(saved.Snapshot), IntradaySnapshotStore.Fingerprint(loaded));
            Assert.Equal(saved.Path, store.Save(loaded));
            Assert.Throws<InvalidDataException>(() => store.Save(loaded with { Coverage = "changed" }));
            File.WriteAllText(saved.Path, File.ReadAllText(saved.Path).Replace("fixture-parameters", "tampered"));
            Assert.Throws<InvalidDataException>(() => store.Load(saved.Path));
            File.WriteAllText(saved.Path, "{"); Assert.Throws<InvalidDataException>(() => store.Load(saved.Path));
            File.WriteAllText(saved.Path, JsonSerializer.Serialize(new SnapshotEnvelope(2, "bad", loaded)));
            Assert.Throws<InvalidDataException>(() => store.Load(saved.Path));
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData(24, false)] [InlineData(25, false)] // Saturday/Sunday
    [InlineData(1, false)] // configured national holiday; weekday is not sufficient
    [InlineData(29, true)]
    public async Task FixtureCalendarControlsEligibilityOnWeekendsAndHolidays(int day, bool open)
    {
        var provider = new Provider { CalendarJson = "{\"data\":{\"is_workday\":" + (open ? "true" : "false") + "}}" };
        var clock = new Clock { Value = new DateTimeOffset(2026, 10, day, 6, 45, 0, TimeSpan.Zero) };
        string dir = Temp();
        try { var saved = await Capture(provider, dir, clock).CaptureAsync(Context(), Array.Empty<SparrowRankedCandidate>(), default); Assert.Equal(open ? SessionEvidence.TradingDay : SessionEvidence.NonTradingDay, saved.Snapshot.Calendar); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task CancellationAndRetentionAreBounded()
    {
        string dir = Temp();
        try {
            var saved = await Capture(new Provider(), dir).CaptureAsync(Context(), new[] { Candidate() }, default);
            var store = new IntradaySnapshotStore(dir);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => store.Save(saved.Snapshot with { Scan = Context() }, cancelled.Token));
            Assert.Single(Directory.GetFiles(dir, "*.json"));
            for (int i = 0; i < 102; i++) store.Save(saved.Snapshot with { Scan = Context() });
            Assert.Equal(IntradaySnapshotStore.MaximumFiles, Directory.GetFiles(dir, "*.json").Length);
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Throws<InvalidDataException>(() => store.Save(saved.Snapshot with { Scan = Context(), Coverage = new string('x', IntradaySnapshotStore.MaximumBytes) }));
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void CancellingAcquisitionPreservesSuccessfulScanButWritesNoSnapshot()
    {
        string dir = Temp();
        SparrowWindowRealUiTests.RunWindowTest((window, vm) => {
            var provider = new Provider { PendingMinute = new() }; var clock = new Clock();
            vm.Notification = _ => { }; vm.SnapshotClock = clock; vm.SnapshotCapture = Capture(provider, dir, clock);
            vm.EnableIntradaySnapshotCapture = true;
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate() });
            Task task = vm.StartCommand.ExecuteAsync(null);
            Assert.False(vm.IsScanning); Assert.True(vm.IsCapturing); Assert.Single(vm.Results);
            vm.CancelScanCommand.Execute(null);
            provider.PendingMinute.SetResult(new StockDataResult { Success = true, SourceKind = DataSourceKind.EastMoney, Json = provider.MinuteJson });
            Pump(task);
            Assert.Single(vm.Results); Assert.Contains("快照采集已取消", vm.SnapshotStatus);
            Assert.Equal("Completed", vm.ScanOutcome);
            Assert.False(Directory.Exists(dir));
        });
    }
    [Fact]
    public async Task NetworkFailureIsNeitherHolidayNorCertifiedSnapshot()
    {
        string dir = Temp();
        var result = await Capture(new Provider { Fail = true }, dir).CaptureAsync(Context(), new[] { Candidate() }, default);
        try
        {
            Assert.Equal(SessionEvidence.Unavailable, result.Snapshot.Calendar);
            Assert.Equal(IntradayQuality.SourceFailure, Assert.Single(result.Snapshot.Symbols).Quality);
            Assert.False(result.Snapshot.Certified);
        }
        finally { File.Delete(result.Path); Directory.Delete(dir); }
    }
    private static void Pump(Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame(); task.GetAwaiter().OnCompleted(() => frame.Continue = false); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
    [Fact]
    public void ExistingScanCaptureWorkflowIsOptInAndCapturesStartSettings_T18_T22_T27()
    {
        string dir = Temp();
        try {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) => {
                Assert.False(vm.EnableIntradaySnapshotCapture); vm.Notification = _ => { };
                var provider = new Provider(); var clock = new Clock(); vm.SnapshotClock = clock; vm.SnapshotCapture = Capture(provider, dir, clock);
                vm.StrategyMode = SparrowStrategyMode.V2; vm.TopN = 1;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate() });
                Pump(vm.StartCommand.ExecuteAsync(null)); Assert.False(Directory.Exists(dir));
                vm.EnableIntradaySnapshotCapture = true;
                var pending = new TaskCompletionSource<IReadOnlyList<SparrowRankedCandidate>>();
                vm.ScanOverride = (_, _) => pending.Task;
                Task run = vm.StartCommand.ExecuteAsync(null);
                vm.TopN = 2; vm.StrategyMode = SparrowStrategyMode.V6; vm.EnableIntradaySnapshotCapture = false;
                clock.Value = Now.AddMinutes(1); pending.SetResult(new[] { Candidate(), Candidate("600002") }); Pump(run);
                var artifact = Assert.Single(Directory.GetFiles(dir, "*.json")); var snapshot = new IntradaySnapshotStore(dir).Load(artifact);
                Assert.Equal("V2", snapshot.Scan.Strategy); Assert.Equal(1, snapshot.Scan.TopN); Assert.Single(snapshot.Symbols);
                Assert.Equal(Now, snapshot.Scan.ScanStartedAtUtc); Assert.Equal(Now.AddMinutes(1), snapshot.Scan.ScanCompletedAtUtc);
                Assert.Contains("Asia/Shanghai", vm.SnapshotStatus); Assert.Contains(artifact, vm.SnapshotStatus);
                Assert.Equal(artifact, vm.LastSnapshotPath); Assert.True(vm.OpenSnapshotCommand.CanExecute(null));
                Assert.Contains("非 T+1", vm.SnapshotStatus); Assert.Equal("Completed", vm.ScanOutcome);
                vm.EnableIntradaySnapshotCapture = true; pending = new(); vm.ScanOverride = (_, _) => pending.Task;
                run = vm.StartCommand.ExecuteAsync(null); vm.CancelScanCommand.Execute(null); pending.SetResult(new[] { Candidate() }); Pump(run);
                Assert.Single(Directory.GetFiles(dir, "*.json"));
                vm.ScanOverride = (_, _) => Task.FromException<IReadOnlyList<SparrowRankedCandidate>>(new Exception("fixture"));
                Pump(vm.StartCommand.ExecuteAsync(null)); Assert.Single(Directory.GetFiles(dir, "*.json"));
            });
        } finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
