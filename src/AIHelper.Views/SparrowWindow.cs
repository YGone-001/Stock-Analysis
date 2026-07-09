using System;

using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using AIHelper.ViewModels;
using HandyControl.Controls;

namespace AIHelper.Views;

public partial class SparrowWindow : HandyControl.Controls.Window
{
    private readonly SparrowLegacyViewModel _vm;

    public SparrowWindow(MainViewModel mainVm)
    {
        InitializeComponent();
        _vm = new SparrowLegacyViewModel(mainVm);
        this.DataContext = _vm;
        this.Loaded += SparrowWindow_Loaded;
    }

    private void SparrowWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Two-way bindings for parameters
        ChkMacroDef.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.MacroDef") { Mode = BindingMode.TwoWay });
        NumMinRise.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinRise") { Mode = BindingMode.TwoWay });
        NumMaxRise.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MaxRise") { Mode = BindingMode.TwoWay });
        NumVolRatio.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.VolRatio") { Mode = BindingMode.TwoWay });
        NumMinAmount.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinAmount") { Mode = BindingMode.TwoWay });
        ChkMA60.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.CheckMA60") { Mode = BindingMode.TwoWay });
        SldAdhesion.SetBinding(RangeSlider.ValueStartProperty, new Binding("Parameters.MinAdhesion") { Mode = BindingMode.TwoWay });
        SldAdhesion.SetBinding(RangeSlider.ValueEndProperty, new Binding("Parameters.MaxAdhesion") { Mode = BindingMode.TwoWay });
        NumConcurrency.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MaxConcurrency") { Mode = BindingMode.TwoWay });
        ChkUseCache.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.UseCache") { Mode = BindingMode.TwoWay });

        // Bindings for UI elements
        BtnStart.SetBinding(Button.ContentProperty, new Binding("StartButtonText"));
        PbScan.SetBinding(ProgressBar.ValueProperty, new Binding("ProgressValue"));
        PbScan.SetBinding(ProgressBar.MaximumProperty, new Binding("ProgressMax"));
        TxtProgressDesc.SetBinding(TextBlock.TextProperty, new Binding("ProgressDesc"));
        TxtStats.SetBinding(TextBlock.TextProperty, new Binding("StatsDesc"));
        TxtLog.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding("LogText"));

        _vm.PropertyChanged += (s, ev) =>
        {
            if (ev.PropertyName == "LogText")
            {
                TxtLog.ScrollToEnd();
            }
        };
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.StartCommand.CanExecute(null))
        {
            _vm.StartCommand.Execute(null);
        }
    }

}
