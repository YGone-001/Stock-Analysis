using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHelper.Models;
using AIHelper.Services;
using AIHelper.Services.StockData;
using AIHelper.ViewModels;
using AIHelper.Views;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowWindowRealUiTests
{
    private sealed class DummyDialogService : IDialogService
    {
        public void ShowMessage(string message, string title) { }
        public bool ShowConfirm(string message, string title) => true;
        public string ShowInput(string title, string defaultValue = "") => defaultValue;
        public void ShowChart(string url, string title) { }
        public void ShowImage(string imageUrl) { }
        public void ShowWenCai(string code, string name) { }
        public void ShowLiveChart(string code, string name) { }
        public void ShowPositionConfig(string code, string name) { }
        public void ShowImportExport() { }
        public void ShowProxySettings() { }
        public void ShowSparrowScanner() { }
    }

    private sealed class DummyStockDataProvider : IStockDataProvider
    {
        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new StockDataResult { Success = true, Json = "{}" });
        }
    }

    [Fact]
    public void SparrowWindowDC_InitializesStrategyAwareUi_AndRendersAllThreeModes()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current == null)
                {
                    _ = new Application();
                }

                var dummyData = new DummyStockDataProvider();
                var dummyDialog = new DummyDialogService();
                var stockVm = new StockViewModel(dummyData, dummyDialog);
                var logVm = new LogViewModel();
                var mainVm = new MainViewModel(stockVm, logVm, dummyDialog, dummyData);

                var window = new SparrowWindowDC(mainVm);
                window.Width = 900;
                window.Height = 720;
                window.Show();

                // 1. Verify strategy mode selector exists
                ComboBox? modeSelector = null;
                void FindModeSelector(DependencyObject node)
                {
                    if (node is ComboBox cb && cb.ItemsSource is IReadOnlyList<SparrowStrategyModeOption>)
                    {
                        modeSelector = cb;
                        return;
                    }
                    foreach (object child in LogicalTreeHelper.GetChildren(node))
                    {
                        if (child is DependencyObject d)
                        {
                            FindModeSelector(d);
                            if (modeSelector != null) return;
                        }
                    }
                }
                FindModeSelector(window);
                Assert.NotNull(modeSelector);

                var options = modeSelector.ItemsSource as IReadOnlyList<SparrowStrategyModeOption>;
                Assert.NotNull(options);
                Assert.Equal(3, options.Count);
                Assert.Equal("Classic（原始麻雀）", options[0].DisplayName);
                Assert.Equal("V2（东财增强）", options[1].DisplayName);
                Assert.Equal("Classic + V2 双选", options[2].DisplayName);

                var vm = window.DataContext as SparrowViewModel;
                Assert.NotNull(vm);

                string artifactDir = @"C:\Users\YGone\.gemini\antigravity\brain\61802ae4-a116-4387-b1a8-996a03966bd0";
                Directory.CreateDirectory(artifactDir);

                // 2. Mode: Classic
                vm.StrategyMode = SparrowStrategyMode.Classic;
                window.UpdateLayout();
                Assert.False(vm.ShowV2Parameters);
                Assert.Equal("麻雀 Classic", vm.BaseParametersHeader);
                SaveWindowScreenshot(window, Path.Combine(artifactDir, "sparrow_classic.png"));

                // 3. Mode: V2
                vm.StrategyMode = SparrowStrategyMode.V2;
                window.UpdateLayout();
                Assert.True(vm.ShowV2Parameters);
                Assert.Equal("基础麻雀条件", vm.BaseParametersHeader);
                Assert.Equal("V2 增强条件", vm.V2ParametersHeader);
                SaveWindowScreenshot(window, Path.Combine(artifactDir, "sparrow_v2.png"));

                // 4. Mode: Compare
                vm.StrategyMode = SparrowStrategyMode.Compare;
                window.UpdateLayout();
                Assert.True(vm.ShowV2Parameters);
                Assert.True(vm.IsCompareMode);
                Assert.Equal("Classic / V2 共同条件", vm.BaseParametersHeader);
                Assert.Equal("仅 V2 生效", vm.V2ParametersHeader);
                Assert.Contains("Compare 使用同一行情快照", vm.ComparisonHint);
                SaveWindowScreenshot(window, Path.Combine(artifactDir, "sparrow_compare.png"));

                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static void SaveWindowScreenshot(Window window, string filePath)
    {
        window.Measure(new Size(900, 720));
        window.Arrange(new Rect(0, 0, 900, 720));
        window.UpdateLayout();

        int width = (int)Math.Max(100, window.ActualWidth > 0 ? window.ActualWidth : 900);
        int height = (int)Math.Max(100, window.ActualHeight > 0 ? window.ActualHeight : 720);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var stream = File.Create(filePath);
        encoder.Save(stream);
    }
}
