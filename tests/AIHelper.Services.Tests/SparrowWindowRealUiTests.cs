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
        RunWindowTest((window, vm) =>
        {
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

            string artifactDir = @"C:\Users\YGone\.gemini\antigravity\brain\61802ae4-a116-4387-b1a8-996a03966bd0";
            Directory.CreateDirectory(artifactDir);

            // 2. Mode: Classic
            vm.StrategyMode = SparrowStrategyMode.Classic;
            window.UpdateLayout();
            Assert.False(vm.ShowV2Parameters);
            Assert.Equal("麻雀 Classic", vm.BaseParametersHeader);
            Assert.Equal("当前范围：0.0% ~ 4.0%", vm.AdhesionRangeText);
            SaveWindowScreenshot(window, Path.Combine(artifactDir, "sparrow_classic.png"));

            // 3. Mode: V2
            vm.StrategyMode = SparrowStrategyMode.V2;
            window.UpdateLayout();
            Assert.True(vm.ShowV2Parameters);
            Assert.Equal("基础麻雀条件", vm.BaseParametersHeader);
            Assert.Equal("V2 增强条件", vm.V2ParametersHeader);
            Assert.Equal("当前范围：3.0% ~ 30.0%", vm.TurnoverRangeText);
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
        });
    }

    [Fact]
    public void LegacyConcurrencyControl_IsNotVisible()
    {
        RunWindowTest((window, _) =>
        {
            Assert.NotNull(window.LegacyConcurrencyControl);
            Assert.NotEqual(Visibility.Visible, window.LegacyConcurrencyControl.Visibility);
            Assert.NotSame(window.NumConcurrency, window.LegacyConcurrencyControl);
            Assert.Equal(Visibility.Visible, window.NumConcurrency.Visibility);

            if (window.Content is Grid mainGrid)
            {
                foreach (UIElement child in mainGrid.Children)
                {
                    if (Grid.GetRow(child) == 1)
                    {
                        AssertNoVisibleConcurrencyText(child);
                    }
                }
            }
        });
    }

    [Fact]
    public void TopN_IsAnIndependentExecutionSetting_BoundFromOneToTwenty()
    {
        RunWindowTest((window, vm) =>
        {
            Assert.Equal(10, vm.TopN);
            Assert.Equal(10.0, window.NumTopN.Value);
            Assert.Equal(1.0, window.NumTopN.Minimum);
            Assert.Equal(20.0, window.NumTopN.Maximum);

            window.NumTopN.Value = 5;
            window.UpdateLayout();
            Assert.Equal(5, vm.TopN);
            Assert.Equal(5, vm.RankingSettings.TopN);

            vm.TopN = 20;
            window.UpdateLayout();
            Assert.Equal(20.0, window.NumTopN.Value);
            Assert.IsNotType<SparrowClassicUiParameters>(vm.RankingSettings);
            Assert.IsNotType<SparrowV2UiParameters>(vm.RankingSettings);
        });
    }

    [Fact]
    public void AdhesionNumericToSliderSync_And_SliderToNumericSync()
    {
        RunWindowTest((window, vm) =>
        {
            Assert.Equal(0.0, window.NumMinAdhesion.Value);
            Assert.Equal(4.0, window.NumMaxAdhesion.Value);
            Assert.Equal(0.0, window.SldAdhesion.ValueStart);
            Assert.Equal(4.0, window.SldAdhesion.ValueEnd);
            Assert.Equal("当前范围：0.0% ~ 4.0%", vm.AdhesionRangeText);

            // 1. Numeric Max -> Slider End + Model
            window.NumMaxAdhesion.Value = 3.5;
            window.UpdateLayout();
            Assert.Equal(3.5, window.SldAdhesion.ValueEnd);
            Assert.Equal(3.5, vm.Parameters.MaxAdhesion);
            Assert.Equal("当前范围：0.0% ~ 3.5%", vm.AdhesionRangeText);

            // 2. Slider Start -> Numeric Min + Model
            window.SldAdhesion.ValueStart = 1.2;
            window.UpdateLayout();
            Assert.Equal(1.2, window.NumMinAdhesion.Value);
            Assert.Equal(1.2, vm.Parameters.MinAdhesion);
            Assert.Equal("当前范围：1.2% ~ 3.5%", vm.AdhesionRangeText);
        });
    }

    [Fact]
    public void TurnoverNumericToSliderSync_And_SliderToNumericSync()
    {
        RunWindowTest((window, vm) =>
        {
            vm.StrategyMode = SparrowStrategyMode.V2;
            window.UpdateLayout();

            Assert.Equal(3.0, window.NumMinTurnover.Value);
            Assert.Equal(30.0, window.NumMaxTurnover.Value);
            Assert.Equal(3.0, window.SldTurnover.ValueStart);
            Assert.Equal(30.0, window.SldTurnover.ValueEnd);
            Assert.Equal("当前范围：3.0% ~ 30.0%", vm.TurnoverRangeText);

            // 1. Numeric Min -> Slider Start + Model
            window.NumMinTurnover.Value = 5.0;
            window.UpdateLayout();
            Assert.Equal(5.0, window.SldTurnover.ValueStart);
            Assert.Equal(5.0, vm.V2Parameters.MinTurnover);
            Assert.Equal("当前范围：5.0% ~ 30.0%", vm.TurnoverRangeText);

            // 2. Slider End -> Numeric Max + Model
            window.SldTurnover.ValueEnd = 25.0;
            window.UpdateLayout();
            Assert.Equal(25.0, window.NumMaxTurnover.Value);
            Assert.Equal(25.0, vm.V2Parameters.MaxTurnover);
            Assert.Equal("当前范围：5.0% ~ 25.0%", vm.TurnoverRangeText);
        });
    }

    [Fact]
    public void ResetRecommendedDefaults_ResetsAdhesionAndTurnoverInUi()
    {
        RunWindowTest((window, vm) =>
        {
            // Modify Adhesion
            window.NumMinAdhesion.Value = 2.0;
            window.NumMaxAdhesion.Value = 8.0;
            window.UpdateLayout();
            Assert.Equal("当前范围：2.0% ~ 8.0%", vm.AdhesionRangeText);

            vm.ResetRecommendedDefaultsCommand.Execute(null);
            window.UpdateLayout();

            Assert.Equal(0.0, window.NumMinAdhesion.Value);
            Assert.Equal(4.0, window.NumMaxAdhesion.Value);
            Assert.Equal(0.0, window.SldAdhesion.ValueStart);
            Assert.Equal(4.0, window.SldAdhesion.ValueEnd);
            Assert.Equal("当前范围：0.0% ~ 4.0%", vm.AdhesionRangeText);

            // Modify Turnover in V2
            vm.StrategyMode = SparrowStrategyMode.V2;
            window.UpdateLayout();
            window.NumMinTurnover.Value = 5.0;
            window.NumMaxTurnover.Value = 20.0;
            window.UpdateLayout();
            Assert.Equal("当前范围：5.0% ~ 20.0%", vm.TurnoverRangeText);

            vm.ResetRecommendedDefaultsCommand.Execute(null);
            window.UpdateLayout();

            Assert.Equal(3.0, window.NumMinTurnover.Value);
            Assert.Equal(30.0, window.NumMaxTurnover.Value);
            Assert.Equal(3.0, window.SldTurnover.ValueStart);
            Assert.Equal(30.0, window.SldTurnover.ValueEnd);
            Assert.Equal("当前范围：3.0% ~ 30.0%", vm.TurnoverRangeText);
        });
    }

    private static readonly object s_staLock = new();
    private static Thread? s_staThread;
    private static System.Windows.Threading.Dispatcher? s_dispatcher;

    private static void EnsureStaThread()
    {
        lock (s_staLock)
        {
            if (s_staThread == null || !s_staThread.IsAlive || s_dispatcher == null)
            {
                var tcs = new TaskCompletionSource<System.Windows.Threading.Dispatcher>();
                s_staThread = new Thread(() =>
                {
                    if (Application.Current == null)
                    {
                        var app = new AIHelper.App();
                        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        app.InitializeComponent();
                    }
                    else
                    {
                        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    }
                    tcs.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                    System.Windows.Threading.Dispatcher.Run();
                });
                s_staThread.SetApartmentState(ApartmentState.STA);
                s_staThread.IsBackground = true;
                s_staThread.Start();
                s_dispatcher = tcs.Task.Result;
            }
        }
    }

    private static void RunWindowTest(Action<SparrowWindowDC, SparrowViewModel> action)
    {
        EnsureStaThread();
        lock (s_staLock)
        {
            s_dispatcher!.Invoke(() =>
            {
                var dummyData = new DummyStockDataProvider();
                var dummyDialog = new DummyDialogService();
                var stockVm = new StockViewModel(dummyData, dummyDialog);
                var logVm = new LogViewModel();
                var mainVm = new MainViewModel(stockVm, logVm, dummyDialog, dummyData);

                var window = new SparrowWindowDC(mainVm);
                window.Width = 900;
                window.Height = 720;
                window.Show();

                var vm = (SparrowViewModel)window.DataContext;
                try
                {
                    action(window, vm);
                }
                finally
                {
                    window.Close();
                }
            });
        }
    }

    private static void AssertNoVisibleConcurrencyText(DependencyObject node)
    {
        if (node is UIElement ui && ui.Visibility != Visibility.Visible)
            return;

        if (node is TextBlock tb && tb.Visibility == Visibility.Visible)
        {
            Assert.DoesNotContain("并发线程", tb.Text);
        }
        else if (node is Label lbl && lbl.Visibility == Visibility.Visible && lbl.Content != null)
        {
            Assert.DoesNotContain("并发线程", lbl.Content.ToString()!);
        }

        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d)
            {
                AssertNoVisibleConcurrencyText(d);
            }
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
