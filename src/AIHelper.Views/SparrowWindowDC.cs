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

public partial class SparrowWindowDC : HandyControl.Controls.Window
{
    private readonly SparrowViewModel _vm;

    public SparrowWindowDC(MainViewModel mainVm)
    {
        InitializeComponent();
        _vm = new SparrowViewModel(mainVm);
        this.DataContext = _vm;
        this.Loaded += SparrowWindowDC_Loaded;
    }

    private void SparrowWindowDC_Loaded(object sender, RoutedEventArgs e)
    {
        // Two-way bindings for parameters
        ChkMacroDef.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.MacroDef") { Mode = BindingMode.TwoWay });
        NumMinRise.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinRise") { Mode = BindingMode.TwoWay });
        NumMaxRise.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MaxRise") { Mode = BindingMode.TwoWay });
        NumVolRatio.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.VolRatio") { Mode = BindingMode.TwoWay });
        NumMinAmount.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinAmount") { Mode = BindingMode.TwoWay });
        ChkMA60.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.CheckMA60") { Mode = BindingMode.TwoWay });
        ChkAlpha.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.CheckAlpha") { Mode = BindingMode.TwoWay });
        ChkUseCache.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.UseCache") { Mode = BindingMode.TwoWay });
        SldAdhesion.SetBinding(RangeSlider.ValueStartProperty, new Binding("Parameters.MinAdhesion") { Mode = BindingMode.TwoWay });
        SldAdhesion.SetBinding(RangeSlider.ValueEndProperty, new Binding("Parameters.MaxAdhesion") { Mode = BindingMode.TwoWay });
        SldTurnover.SetBinding(RangeSlider.ValueStartProperty, new Binding("Parameters.MinTurnover") { Mode = BindingMode.TwoWay });
        SldTurnover.SetBinding(RangeSlider.ValueEndProperty, new Binding("Parameters.MaxTurnover") { Mode = BindingMode.TwoWay });
        NumMomentum.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MomentumThreshold") { Mode = BindingMode.TwoWay });
        NumConcurrency.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MaxConcurrency") { Mode = BindingMode.TwoWay });

        // Bindings for UI elements
        BtnStart.SetBinding(Button.ContentProperty, new Binding("StartButtonText"));
        PbScan.SetBinding(ProgressBar.ValueProperty, new Binding("ProgressValue"));
        PbScan.SetBinding(ProgressBar.MaximumProperty, new Binding("ProgressMax"));
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

    private void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.TestCommand.CanExecute(null))
        {
            _vm.TestCommand.Execute(null);
        }
    }

}
