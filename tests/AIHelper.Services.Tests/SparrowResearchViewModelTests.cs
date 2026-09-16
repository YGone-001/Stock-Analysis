using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services;
using AIHelper.Services.StockData.Sparrow;
using AIHelper.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchViewModelTests
{
    [Fact]
    public void InitialState_HasNoDatasetAndRunAndExportAreDisabled()
    {
        using SparrowResearchViewModel vm = ViewModel();
        Assert.Equal(HistoricalDatasetLoadState.NoDataset, vm.DatasetLoadState);
        Assert.False(vm.RunReplayCommand.CanExecute(null));
        Assert.False(vm.RunBacktestCommand.CanExecute(null));
        Assert.False(vm.ExportReplayCommand.CanExecute(null));
        Assert.False(vm.ExportBacktestCommand.CanExecute(null));
    }

    [Fact]
    public async Task DatasetLoad_MapsMetadataAndFailureIsVisible()
    {
        HistoricalMarketDataset dataset = Dataset();
        using SparrowResearchViewModel vm = ViewModel(loader: new FakeLoader(HistoricalDatasetLoadResult.Loaded(dataset)));
        await vm.LoadDatasetFromPathAsync("C:\\datasets\\fixture.json");
        Assert.Equal(HistoricalDatasetLoadState.Ready, vm.DatasetLoadState);
        Assert.Equal("fixture", vm.DatasetId); Assert.Equal(2, vm.DatasetSymbolCount);
        Assert.Contains("fixture.json", vm.DatasetDisplayName); Assert.True(vm.RunReplayCommand.CanExecute(null));
        await vm.LoadDatasetFromPathAsync("bad.json", CancellationToken.None);
        // The same fake loader still succeeds: independent failure behavior is covered without disk I/O below.
        using SparrowResearchViewModel invalid = ViewModel(loader: new FakeLoader(HistoricalDatasetLoadResult.Failed("Invalid schema")));
        await invalid.LoadDatasetFromPathAsync("bad.json");
        Assert.Equal(HistoricalDatasetLoadState.Invalid, invalid.DatasetLoadState); Assert.Contains("Invalid schema", invalid.DatasetStatus);
    }

    [Fact]
    public async Task CapabilityAndInputValidation_RejectBeforeEngineExecution()
    {
        HistoricalMarketDataset unsupported = Dataset(new HistoricalDataCapabilities(true, true, false, false, true, true));
        using SparrowResearchViewModel vm = ViewModel(loader: new FakeLoader(HistoricalDatasetLoadResult.Loaded(unsupported)));
        await vm.LoadDatasetFromPathAsync("unsupported.json");
        Assert.False(vm.IsStrategySupported); Assert.False(vm.RunReplayCommand.CanExecute(null));
        vm.TopN = 0;
        Assert.Contains("required", vm.StrategySupportMessage, StringComparison.OrdinalIgnoreCase);

        using SparrowResearchViewModel valid = ReadyViewModel();
        valid.ReplayDate = new DateTime(2026, 4, 1); await ((IAsyncRelayCommand)valid.RunReplayCommand).ExecuteAsync(null);
        Assert.Contains("dataset trading date", valid.Message, StringComparison.OrdinalIgnoreCase);
        valid.ReplayDate = Dataset().TradingDates[^1].ToDateTime(TimeOnly.MinValue); valid.StartDate = valid.ReplayDate; valid.EndDate = Dataset().TradingDates[0].ToDateTime(TimeOnly.MinValue);
        await ((IAsyncRelayCommand)valid.RunBacktestCommand).ExecuteAsync(null);
        Assert.Contains("Start", valid.Message, StringComparison.OrdinalIgnoreCase);
        valid.EndDate = valid.ReplayDate; valid.RoundTripCostPercent = -1;
        await ((IAsyncRelayCommand)valid.RunReplayCommand).ExecuteAsync(null);
        Assert.Contains("cannot be negative", valid.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplayAndBacktest_PresentResultsAndPreserveNa()
    {
        using SparrowResearchViewModel vm = ReadyViewModel();
        await ((IAsyncRelayCommand)vm.RunReplayCommand).ExecuteAsync(null);
        Assert.Equal(SparrowResearchState.Completed, vm.State);
        Assert.All(vm.Selections, selection => Assert.Equal("N/A", selection.Return1D));
        await ((IAsyncRelayCommand)vm.RunBacktestCommand).ExecuteAsync(null);
        Assert.Equal(SparrowResearchState.Completed, vm.State);
        Assert.NotEmpty(vm.Metrics);
        Assert.All(vm.Selections.Where(row => row.Return20D == "N/A"), row => Assert.NotEqual("0.00%", row.Return20D));
    }

    [Fact]
    public async Task Export_DelegatesResultToExporter()
    {
        FakeExporter exporter = new();
        using SparrowResearchViewModel vm = ReadyViewModel(exporter: exporter, dialogs: new FakeDialogs { ReplayPath = "replay.json", BacktestPath = "backtest.json" });
        await ((IAsyncRelayCommand)vm.RunReplayCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)vm.ExportReplayCommand).ExecuteAsync(null);
        Assert.NotNull(exporter.Replay);
        await ((IAsyncRelayCommand)vm.RunBacktestCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)vm.ExportBacktestCommand).ExecuteAsync(null);
        Assert.NotNull(exporter.Backtest);
    }

    [Fact]
    public async Task Cancellation_IsNotReportedAsError()
    {
        TaskCompletionSource<HistoricalDatasetLoadResult> pending = new();
        using SparrowResearchViewModel vm = ViewModel(loader: new BlockingLoader());
        Task loading = vm.LoadDatasetFromPathAsync("slow.json");
        await Task.Delay(10); vm.CancelCommand.Execute(null);
        await loading;
        Assert.Equal(SparrowResearchState.Cancelled, vm.State);
        Assert.DoesNotContain("error", vm.Message, StringComparison.OrdinalIgnoreCase);
        _ = pending;
    }

    [Fact]
    public void ProviderHealth_MapsAllStatesAndStrategyVersion()
    {
        FakeHealth health = new(
            new ProviderHealthSnapshot(DataSourceKind.ExternalGateway, ProviderHealthState.Healthy, DateTimeOffset.UtcNow, null, 0, TimeSpan.FromMilliseconds(12)),
            new ProviderHealthSnapshot(DataSourceKind.EastMoney, ProviderHealthState.Degraded, null, new ProviderFailure(DataSourceKind.EastMoney, MarketDataOperation.Quote, ProviderFailureKind.Timeout, "hidden", DateTimeOffset.UtcNow), 2, null),
            new ProviderHealthSnapshot(DataSourceKind.LocalCache, ProviderHealthState.Unavailable, null, null, 0, null),
            new ProviderHealthSnapshot(DataSourceKind.Unknown, ProviderHealthState.Unknown, null, null, 0, null));
        using SparrowResearchViewModel vm = ViewModel(health: health);
        Assert.Equal(new[] { "Degraded", "Healthy", "Unavailable", "Unknown" }, vm.ProviderHealth.Select(row => row.State).Order());
        vm.Strategy = SparrowStrategyMode.Classic; Assert.Equal(SparrowStrategyVersions.Classic, vm.StrategyVersionDisplay);
        vm.Strategy = SparrowStrategyMode.V2; Assert.Equal(SparrowStrategyVersions.V2, vm.StrategyVersionDisplay);
    }

    private static SparrowResearchViewModel ReadyViewModel(FakeExporter? exporter = null, FakeDialogs? dialogs = null)
    {
        SparrowResearchViewModel vm = ViewModel(loader: new FakeLoader(HistoricalDatasetLoadResult.Loaded(Dataset())), exporter: exporter, dialogs: dialogs);
        vm.LoadDatasetFromPathAsync("fixture.json").GetAwaiter().GetResult(); return vm;
    }
    private static SparrowResearchViewModel ViewModel(IHistoricalDatasetLoader? loader = null, FakeExporter? exporter = null, FakeDialogs? dialogs = null, IProviderHealthService? health = null) => new(new SparrowHistoricalReplayEngine(), new SparrowHistoricalBacktestEngine(), health ?? new FakeHealth(), loader ?? new FakeLoader(HistoricalDatasetLoadResult.Loaded(Dataset())), dialogs ?? new FakeDialogs(), exporter ?? new FakeExporter());
    private sealed class FakeLoader(HistoricalDatasetLoadResult result) : IHistoricalDatasetLoader { public Task<HistoricalDatasetLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(result); }
    private sealed class BlockingLoader : IHistoricalDatasetLoader { public async Task<HistoricalDatasetLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return HistoricalDatasetLoadResult.Failed(); } }
    private sealed class FakeDialogs : IFileDialogService { public string? ReplayPath { get; init; } public string? BacktestPath { get; init; } public string? OpenHistoricalDataset() => null; public string? SaveReplayResult(string defaultFileName) => ReplayPath; public string? SaveBacktestResult(string defaultFileName) => BacktestPath; }
    private sealed class FakeExporter : IHistoricalResultExporter { public SparrowReplayResult? Replay { get; private set; } public SparrowBacktestResult? Backtest { get; private set; } public Task<string> ExportReplayJsonAsync(SparrowReplayResult result, string path, CancellationToken cancellationToken = default) { Replay = result; return Task.FromResult(path); } public Task<string> ExportBacktestJsonAsync(SparrowBacktestResult result, string path, CancellationToken cancellationToken = default) { Backtest = result; return Task.FromResult(path); } public Task<string> ExportLegacyReplayCapabilityJsonAsync(SparrowLegacyHistoricalReplayResult result, string path, CancellationToken cancellationToken = default) => Task.FromResult(path); }
    private sealed class FakeHealth(params ProviderHealthSnapshot[] snapshots) : IProviderHealthService { public IReadOnlyList<ProviderHealthSnapshot> GetSnapshots() => snapshots; public ProviderHealthSnapshot GetSnapshot(DataSourceKind provider) => snapshots.SingleOrDefault(x => x.Provider == provider) ?? new(provider, ProviderHealthState.Unknown, null, null, 0, null); public void RecordSuccess(DataSourceKind provider, MarketDataOperation operation, TimeSpan latency) { } public void RecordFailure(DataSourceKind provider, MarketDataOperation operation, ProviderFailureKind kind, string message, TimeSpan latency) { } }
    private static HistoricalMarketDataset Dataset(HistoricalDataCapabilities? capabilities = null)
    {
        DateOnly start = new(2026, 1, 1); DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        KlineSeries[] klines = new[] { "600000", "600001" }.Select(code => new KlineSeries(code, dates.Select((date, i) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + i * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => new[] { "600000", "600001" }.Select((code, index) => new HistoricalQuoteObservation(date, new QuoteSnapshot(code, code, 10.3, 10, 3, null, 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        HistoricalMarketContext[] contexts = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, .5)).ToArray();
        return new HistoricalMarketDataset("fixture", dates, quotes, klines, contexts, capabilities, "Unknown", "Test");
    }
}
