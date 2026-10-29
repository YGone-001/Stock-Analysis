using System;
using System.ComponentModel;
using System.Windows;
using AIHelper.Models;
using AIHelper.ViewModels;
using HandyControl.Controls;
using Serilog;

namespace AIHelper.Views;

/// <summary>
/// The Sparrow scanner window. The full layout lives in SparrowWindow.xaml; this partial class only
/// carries the ViewModel wiring, the log auto-scroll hook, and one compatibility shim for a UI contract
/// left over from the pre-XAML binary-BAML era (see LegacyConcurrencyControl).
/// </summary>
public partial class SparrowWindowDC : HandyControl.Controls.Window
{
    private bool _attached;
    private bool _closed;
    private readonly SparrowViewModel _vm;
    private readonly System.Windows.Threading.DispatcherTimer _autoScanTimer;

    public SparrowWindowDC(MainViewModel mainVm)
    {
        // Compatibility shim (see property docs): detached and collapsed, never part of the visual tree.
        LegacyConcurrencyControl = new NumericUpDown { Visibility = Visibility.Collapsed };
        InitializeComponent();
        // XAML literals mirror these constants; re-applying keeps a single source of truth without
        // an x:Static (which would force the WPF markup-compile second pass, broken under .NET 10).
        NumTopN.Minimum = SparrowRankingSettings.MinimumTopN;
        NumTopN.Maximum = SparrowRankingSettings.MaximumTopN;
        _vm = new SparrowViewModel(mainVm);
        DataContext = _vm;
        Loaded += SparrowWindowDC_Loaded;
        // The window owns the clock: a one-minute UI timer ticks the VM's pure auto-scan rules.
        _autoScanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autoScanTimer.Tick += AutoScanTimer_Tick;
        Unloaded += Window_Unloaded;
        Closed += Window_Closed;
    }

    /// <summary>
    /// Compatibility shim: the old binary-BAML skeleton carried a duplicate concurrency control that the
    /// strategy-aware rebuild used to hide; tests pin that contract (present, not visible, distinct from
    /// the live setting). The XAML window has no such duplicate, so the shim is a detached collapsed control.
    /// </summary>
    internal NumericUpDown? LegacyConcurrencyControl { get; }

    internal static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        return SparrowUiTreeHelper.FindAncestor<T>(current);
    }

    private void SparrowWindowDC_Loaded(object sender, RoutedEventArgs e)
    {
        if (_closed || _attached) return;
        _attached = true;
        _vm.PropertyChanged += ViewModel_PropertyChanged;
        _autoScanTimer.Start();
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_closed && e.PropertyName == "LogText") TxtLog.ScrollToEnd();
    }

    private void AutoScanTimer_Tick(object? sender, EventArgs e)
    {
        if (!_closed) _vm.AutoScanTick(DateTime.Now);
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        _autoScanTimer.Stop();
        if (_attached) _vm.PropertyChanged -= ViewModel_PropertyChanged;
        _attached = false;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;
        Window_Unloaded(this, new RoutedEventArgs());
        _autoScanTimer.Tick -= AutoScanTimer_Tick;
        Loaded -= SparrowWindowDC_Loaded;
        Unloaded -= Window_Unloaded;
        Closed -= Window_Closed;
        _vm.Close();
    }

    /// <summary>Double-clicking a result row opens its WebView2 live chart via the ViewModel command.</summary>
    private void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Header/background double-clicks must not re-open the chart for a stale selection.
        if (SparrowUiTreeHelper.FindAncestor<System.Windows.Controls.DataGridRow>(e.OriginalSource as DependencyObject) is null) return;
        if (ResultsGrid.SelectedItem is SparrowResultRow row)
        {
            _vm.ShowLiveChartCommand.Execute(row);
        }
    }
}
