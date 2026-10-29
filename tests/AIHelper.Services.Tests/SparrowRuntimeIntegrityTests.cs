using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.ViewModels;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowRuntimeIntegrityTests
{
    private static SparrowRankedCandidate Candidate(string code) => new()
    {
        Code = code,
        Name = code,
        RankingProfile = "test",
        Rank = 1,
        Features = new SparrowRankingFeatures { Code = code, Name = code }
    };
    private static TaskCompletionSource<IReadOnlyList<SparrowRankedCandidate>> Pending() => new();
    private static void Pump(Task task)
    {
        if (task.IsCompleted) { task.GetAwaiter().GetResult(); return; }
        var frame = new DispatcherFrame();
        task.GetAwaiter().OnCompleted(() => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Apply(SparrowViewModel vm) => typeof(SparrowViewModel).GetMethod("ApplyCachedTopN", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object?[] { null });

    [Fact]
    public async Task BaselineDualPurposeAsyncCommandCannotReenterToCancel()
    {
        var pending = new TaskCompletionSource();
        int entries = 0;
        var original = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(async () => { entries++; await pending.Task; });
        Task task = original.ExecuteAsync(null);
        Assert.False(original.CanExecute(null));
        Assert.Equal(1, entries);
        pending.SetResult(); await task;
        Assert.True(original.CanExecute(null));
    }

    [Fact]
    public void CommandsCancelRestartAndRejectOldProgress_T01_T06()
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
        {
            vm.Notification = _ => { };
            var pending = Pending();
            IProgress<SparrowScanReport>? oldProgress = null;
            CancellationToken token = default;
            int executions = 0;
            vm.ScanOverride = (ct, progress) => { executions++; token = ct; oldProgress = progress; return pending.Task; };
            Assert.True(vm.StartCommand.CanExecute(null));
            Assert.False(vm.CancelScanCommand.CanExecute(null));
            Task first = vm.StartCommand.ExecuteAsync(null);
            Assert.True(vm.IsScanning);
            Assert.False(vm.StartCommand.CanExecute(null));
            Assert.True(vm.CancelScanCommand.CanExecute(null));
            Pump(vm.StartCommand.ExecuteAsync(null));
            Assert.Equal(1, executions);
            vm.CancelScanCommand.Execute(null);
            vm.CancelScanCommand.Execute(null);
            Assert.True(token.IsCancellationRequested);
            pending.SetResult(new[] { Candidate("600001") });
            Pump(first);
            Assert.Equal("Cancelled", vm.ScanOutcome);
            Assert.Empty(vm.Results);
            var second = Pending();
            vm.ScanOverride = (_, _) => second.Task;
            Task next = vm.StartCommand.ExecuteAsync(null);
            oldProgress!.Report(new SparrowScanReport { ProgressValue = 99 });
            Drain();
            Assert.Equal(0, vm.ProgressValue);
            second.SetResult(new[] { Candidate("600002") });
            Pump(next);
            Assert.Equal("Completed", vm.ScanOutcome);
            Assert.Equal("600002", Assert.Single(vm.Results).Code);
        });
    }

    [Fact]
    public void WindowCleanupIsIdempotentAndSuppressesLatePublication_T07_T09()
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
        {
            vm.Notification = _ => Assert.Fail("Late notification");
            var eventField = typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int subscriptions = ((Delegate)eventField.GetValue(vm)!).GetInvocationList().Length;
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.Equal(subscriptions, ((Delegate)eventField.GetValue(vm)!).GetInvocationList().Length);
            Assert.True(Get<bool>(window, "_attached"));
            var timer = Get<DispatcherTimer>(window, "_autoScanTimer");
            Assert.True(timer.IsEnabled);
            var pending = Pending();
            vm.ScanOverride = (_, _) => pending.Task;
            Task run = vm.StartCommand.ExecuteAsync(null);
            window.Close();
            Assert.False(timer.IsEnabled);
            Assert.False(Get<bool>(window, "_attached"));
            Assert.DoesNotContain(((Delegate?)eventField.GetValue(vm))?.GetInvocationList() ?? Array.Empty<Delegate>(), handler => ReferenceEquals(handler.Target, window));
            int updates = 0;
            vm.PropertyChanged += (_, _) => updates++;
            vm.Close();
            pending.SetResult(new[] { Candidate("600001") });
            Pump(run);
            Assert.Empty(vm.Results);
            Assert.Equal(0, updates);
            Assert.False(vm.StartCommand.CanExecute(null));
        });
    }

    [Fact]
    public void AutoOutcomesAndCapturedIdentity_T10_T18()
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
        {
            var messages = new List<string>(); vm.Notification = messages.Add;
            vm.SystemSettings.AutoScanEnabled = true;
            var now = new DateTime(2026, 10, 29, 14, 0, 0);
            int count = 0;
            vm.ScanOverride = (_, _) => { count++; return Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate("600001") }); };
            vm.Parameters.MinRise = 10; vm.Parameters.MaxRise = 1;
            vm.AutoScanTick(now); Drain();
            Assert.Equal(0, count); Assert.Equal("Skipped", vm.ScanOutcome);
            vm.Parameters.MinRise = 3; vm.Parameters.MaxRise = 5;
            messages.Clear(); vm.AutoScanTick(now); Pump(vm.StartCommand.ExecutionTask!);
            Assert.Empty(messages);
            vm.ScanOverride = (_, _) => Task.FromException<IReadOnlyList<SparrowRankedCandidate>>(new InvalidOperationException("fake"));
            vm.AutoScanTick(now.AddMinutes(5)); Pump(vm.StartCommand.ExecutionTask!);
            Assert.Equal("Failed", vm.ScanOutcome); Assert.Empty(messages);
            var pending = Pending(); vm.ScanOverride = (_, _) => pending.Task;
            vm.AutoScanTick(now.AddMinutes(10)); var cancelled = vm.StartCommand.ExecutionTask!;
            vm.CancelScanCommand.Execute(null); pending.SetResult(new[] { Candidate("600002") }); Pump(cancelled);
            Assert.Empty(messages);
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate("600001"), Candidate("600002") });
            vm.AutoScanTick(now.AddMinutes(15)); Pump(vm.StartCommand.ExecutionTask!);
            Assert.Contains("600002", Assert.Single(messages)); Assert.DoesNotContain("600001", messages[0]);
            messages.Clear();
            vm.TopN = 1;
            pending = Pending(); vm.ScanOverride = (_, _) => pending.Task;
            vm.AutoScanTick(now.AddMinutes(20)); Task captured = vm.StartCommand.ExecutionTask!;
            vm.TopN = 2; vm.StrategyMode = SparrowStrategyMode.V6;
            pending.SetResult(new[] { Candidate("600003"), Candidate("600004") }); Pump(captured);
            Assert.Single(vm.Results); Assert.Empty(messages);
            Assert.Contains("Top1", vm.ResultSummary);
        });
    }

    [Fact]
    public void EntrantNotificationNamesAtMostFiveAndUsesCapturedMode()
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) => {
            var messages = new List<string>(); vm.Notification = messages.Add;
            vm.SystemSettings.AutoScanEnabled = true; vm.StrategyMode = SparrowStrategyMode.V2;
            var now = new DateTime(2026, 10, 29, 14, 0, 0);
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate("old") });
            vm.AutoScanTick(now); Pump(vm.StartCommand.ExecutionTask!);
            var pending = Pending(); vm.ScanOverride = (_, _) => pending.Task;
            vm.AutoScanTick(now.AddMinutes(5)); Task task = vm.StartCommand.ExecutionTask!;
            vm.StrategyMode = SparrowStrategyMode.V6;
            pending.SetResult(Enumerable.Range(1, 8).Select(i => Candidate($"new{i}")).ToArray()); Pump(task);
            string message = Assert.Single(messages);
            Assert.Contains("new5", message); Assert.DoesNotContain("new6", message);
            Assert.Contains("V2", message); Assert.DoesNotContain("V6", message);
        });
    }

    [Fact]
    public void SuccessfulSnapshotsIncludeEmptyAndAreScoped_T13_T18()
    {
        var state = new SparrowAutoScanSnapshot(); var date = new DateTime(2026, 10, 29);
        Assert.Empty(state.Observe("V2/Top2/params", date, new[] { Candidate("a") }));
        Assert.Equal("b", Assert.Single(state.Observe("V2/Top2/params", date, new[] { Candidate("a"), Candidate("b") })).Code);
        Assert.Empty(state.Observe("V2/Top2/params", date, Array.Empty<SparrowRankedCandidate>()));
        Assert.Equal("a", Assert.Single(state.Observe("V2/Top2/params", date, new[] { Candidate("a") })).Code);
        Assert.Empty(state.Observe("V3/Top2/params", date, new[] { Candidate("b") }));
        Assert.Empty(state.Observe("V3/Top1/params", date, new[] { Candidate("c") }));
        Assert.Empty(state.Observe("V3/Top1/changed", date, new[] { Candidate("d") }));
        Assert.Empty(state.Observe("V3/Top1/changed", date.AddDays(1), new[] { Candidate("e") }));
    }

    [Theory]
    [InlineData(SparrowStrategyMode.Classic)]
    [InlineData(SparrowStrategyMode.V2)]
    [InlineData(SparrowStrategyMode.V3)]
    [InlineData(SparrowStrategyMode.V4)]
    [InlineData(SparrowStrategyMode.V5)]
    [InlineData(SparrowStrategyMode.V6)]
    public void EmptyRemovesGroupsButFailureAndCancellationKeepThem_T19_T20(SparrowStrategyMode mode)
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
        {
            vm.Notification = _ => { }; vm.StrategyMode = mode;
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { Candidate("600001") });
            Pump(vm.StartCommand.ExecuteAsync(null));
            var groups = Get<Dictionary<string, StockGroupModel>>(vm, "_activeResultGroups");
            var original = Assert.Single(groups).Value;
            vm.ScanOverride = (_, _) => Task.FromException<IReadOnlyList<SparrowRankedCandidate>>(new Exception("fake"));
            Pump(vm.StartCommand.ExecuteAsync(null)); Assert.Same(original, Assert.Single(groups).Value);
            var pending = Pending(); vm.ScanOverride = (_, _) => pending.Task;
            Task cancelled = vm.StartCommand.ExecuteAsync(null); vm.CancelScanCommand.Execute(null);
            pending.SetResult(new[] { Candidate("600002") }); Pump(cancelled);
            Assert.Same(original, Assert.Single(groups).Value);
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(Array.Empty<SparrowRankedCandidate>());
            Pump(vm.StartCommand.ExecuteAsync(null));
            Assert.Empty(groups); Assert.Empty(vm.Results); Assert.Equal("CompletedEmpty", vm.ScanOutcome);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComparisonsReplaceRowsAndPreserveCategories_T21_T23(bool pair)
    {
        SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
        {
            vm.Notification = _ => { };
            vm.Results.Add(SparrowResultRow.From(Candidate("stale")));
            Set(vm, "_lastCompletedMode", pair ? SparrowStrategyMode.PairCompare : SparrowStrategyMode.Compare);
            Set(vm, "_lastCompletedAt", new DateTime(2026, 10, 29));
            if (pair)
            {
                Set(vm, "_lastPairLeftVersion", "V3"); Set(vm, "_lastPairRightVersion", "V5");
                Set(vm, "_lastPairComparisonRows", new[] {
                    new SparrowPairComparisonRow { Code = "a", Name = "a", Category = SparrowPairComparisonCategory.Both },
                    new SparrowPairComparisonRow { Code = "b", Name = "b", Category = SparrowPairComparisonCategory.LeftOnly },
                    new SparrowPairComparisonRow { Code = "c", Name = "c", Category = SparrowPairComparisonCategory.RightOnly } });
            }
            else Set(vm, "_lastComparisonRows", new[] {
                new SparrowComparisonRow { Code = "a", Name = "a", Category = SparrowComparisonCategory.Both },
                new SparrowComparisonRow { Code = "b", Name = "b", Category = SparrowComparisonCategory.ClassicOnly },
                new SparrowComparisonRow { Code = "c", Name = "c", Category = SparrowComparisonCategory.V2Only } });
            Apply(vm);
            Assert.Equal(new[] { "Both", pair ? "LeftOnly" : "ClassicOnly", pair ? "RightOnly" : "V2Only" }, vm.Results.Select(r => r.Category));
            Assert.All(vm.Results, row => { Assert.Null(row.DisplayScore); Assert.Null(row.DisplayRank); Assert.Equal(pair ? "V3" : "Classic", row.LeftVersion); Assert.Equal(pair ? "V5" : "V2", row.RightVersion); });
            if (pair) Set(vm, "_lastPairComparisonRows", Array.Empty<SparrowPairComparisonRow>());
            else Set(vm, "_lastComparisonRows", Array.Empty<SparrowComparisonRow>());
            Apply(vm); Assert.Empty(vm.Results); Assert.Empty(Get<Dictionary<string, StockGroupModel>>(vm, "_activeResultGroups"));
        });
    }
}
