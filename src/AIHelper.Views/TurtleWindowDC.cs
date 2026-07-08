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

public class TurtleWindowDC : HandyControl.Controls.Window, IComponentConnector
{
    private bool _contentLoaded;
    private readonly TurtleViewModel _vm;

    internal CheckBox ChkMacroDef;
    internal NumericUpDown NumN1;
    internal NumericUpDown NumN2;
    internal RangeSlider SldTurnover;
    internal NumericUpDown NumMinAmount;
    internal NumericUpDown NumConcurrency;
    internal CheckBox ChkUseCache;
    internal Button BtnStart;
    internal Button BtnTest;
    internal TextBlock TxtProgressDesc;
    internal TextBlock TxtStats;
    internal ProgressBar PbScan;
    internal System.Windows.Controls.TextBox TxtLog;

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

    [DebuggerNonUserCode]
    [GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
    public void InitializeComponent()
    {
        if (!_contentLoaded)
        {
            _contentLoaded = true;
            Uri resourceLocator = new Uri("/AIHelper;component/views/turtlewindowdc.xaml", UriKind.Relative);
            Application.LoadComponent(this, resourceLocator);
        }
    }

    [DebuggerNonUserCode]
    [GeneratedCode("PresentationBuildTasks", "8.0.6.0")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    void IComponentConnector.Connect(int connectionId, object target)
    {
        switch (connectionId)
        {
            case 1: ChkMacroDef = (CheckBox)target; break;
            case 2: NumN1 = (NumericUpDown)target; break;
            case 3: NumN2 = (NumericUpDown)target; break;
            case 4: SldTurnover = (RangeSlider)target; break;
            case 5: NumMinAmount = (NumericUpDown)target; break;
            case 6: NumConcurrency = (NumericUpDown)target; break;
            case 7: ChkUseCache = (CheckBox)target; break;
            case 8: BtnStart = (Button)target; BtnStart.Click += BtnStart_Click; break;
            case 9: BtnTest = (Button)target; BtnTest.Click += BtnTest_Click; break;
            case 10: TxtProgressDesc = (TextBlock)target; break;
            case 11: TxtStats = (TextBlock)target; break;
            case 12: PbScan = (ProgressBar)target; break;
            case 13: TxtLog = (System.Windows.Controls.TextBox)target; break;
            default: _contentLoaded = true; break;
        }
    }
}
