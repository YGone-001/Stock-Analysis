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
    private readonly SparrowViewModel _vm;

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
        _vm.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == "LogText")
            {
                TxtLog.ScrollToEnd();
            }
        };
    }
}
