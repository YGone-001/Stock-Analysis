using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using AIHelper.ViewModels;
using HandyControl.Controls;

namespace AIHelper.Views;

public partial class TurtleWindowDC : HandyControl.Controls.Window
{
    private readonly TurtleViewModel _vm;

    public TurtleWindowDC(MainViewModel mainVm)
    {
        InitializeComponent();
        _vm = new TurtleViewModel(mainVm);
        this.DataContext = _vm;
        this.Loaded += TurtleWindowDC_Loaded;
    }

    private void TurtleWindowDC_Loaded(object sender, RoutedEventArgs e)
    {
        // Two-way bindings for parameters
        ChkMacroDef.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.MacroDef") { Mode = BindingMode.TwoWay });
        NumN1.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.N1") { Mode = BindingMode.TwoWay });
        NumN2.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.N2") { Mode = BindingMode.TwoWay });
        SldTurnover.SetBinding(RangeSlider.ValueStartProperty, new Binding("Parameters.MinTurnover") { Mode = BindingMode.TwoWay });
        SldTurnover.SetBinding(RangeSlider.ValueEndProperty, new Binding("Parameters.MaxTurnover") { Mode = BindingMode.TwoWay });
        NumMinAmount.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinAmount") { Mode = BindingMode.TwoWay });
        NumConcurrency.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MaxConcurrency") { Mode = BindingMode.TwoWay });
        ChkUseCache.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.UseCache") { Mode = BindingMode.TwoWay });

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
