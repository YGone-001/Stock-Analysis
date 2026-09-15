using System.Windows;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Services;
using AIHelper.Services.StockData.Sparrow;
using AIHelper.ViewModels;
using AIHelper.Views;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchWindowTests
{
    [Fact]
    public void Window_ConstructsWithDataContextAndKeyBoundControls()
    {
        SparrowWindowRealUiTests.RunOnSharedUiDispatcher(() =>
        {
            using SparrowResearchViewModel vm = new(new SparrowHistoricalReplayEngine(), new SparrowHistoricalBacktestEngine(), new Health(), new Loader(), new Dialogs(), new Exporter());
            SparrowResearchWindow window = new(vm);
            try
            {
                window.Show();
                window.UpdateLayout();
                Assert.Same(vm, window.DataContext);
                Assert.NotNull(window.FindName("LoadDatasetButton"));
                Assert.NotNull(window.FindName("RunReplayButton"));
                Assert.NotNull(window.FindName("RunBacktestButton"));
                Assert.NotNull(window.FindName("SaveReplayButton"));
                Assert.NotNull(window.FindName("SaveBacktestButton"));
            }
            finally { window.Close(); }
        });
    }

    private sealed class Loader : IHistoricalDatasetLoader { public Task<HistoricalDatasetLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(HistoricalDatasetLoadResult.Failed()); }
    private sealed class Dialogs : IFileDialogService { public string? OpenHistoricalDataset() => null; public string? SaveReplayResult(string defaultFileName) => null; public string? SaveBacktestResult(string defaultFileName) => null; }
    private sealed class Exporter : IHistoricalResultExporter { public Task<string> ExportReplayJsonAsync(SparrowReplayResult result, string path, CancellationToken cancellationToken = default) => Task.FromResult(path); public Task<string> ExportBacktestJsonAsync(SparrowBacktestResult result, string path, CancellationToken cancellationToken = default) => Task.FromResult(path); }
    private sealed class Health : IProviderHealthService { public IReadOnlyList<ProviderHealthSnapshot> GetSnapshots() => Array.Empty<ProviderHealthSnapshot>(); public ProviderHealthSnapshot GetSnapshot(DataSourceKind provider) => new(provider, ProviderHealthState.Unknown, null, null, 0, null); public void RecordSuccess(DataSourceKind provider, MarketDataOperation operation, TimeSpan latency) { } public void RecordFailure(DataSourceKind provider, MarketDataOperation operation, ProviderFailureKind kind, string message, TimeSpan latency) { } }
}
