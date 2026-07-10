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

public class SparrowWindow : HandyControl.Controls.Window, IComponentConnector
{
    private bool _contentLoaded;
    private readonly SparrowLegacyViewModel _vm;

    internal CheckBox ChkMacroDef;
    internal NumericUpDown NumMinRise;
    internal NumericUpDown NumMaxRise;
    internal NumericUpDown NumVolRatio;
    internal NumericUpDown NumMinAmount;
    internal CheckBox ChkMA60;
    internal RangeSlider SldAdhesion;
    internal CheckBox ChkUseCache;
    internal Button BtnStart;
    internal NumericUpDown NumConcurrency;
    internal TextBlock TxtProgressDesc;
    internal TextBlock TxtStats;
    internal ProgressBar PbScan;
    internal System.Windows.Controls.TextBox TxtLog;

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

    public void InitializeComponent()
    {
        if (!_contentLoaded)
        {
            _contentLoaded = true;
            Uri resourceLocator = new Uri("/AIHelper;component/views/sparrowwindow.xaml", UriKind.Relative);
            Application.LoadComponent(this, resourceLocator);
        }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    void IComponentConnector.Connect(int connectionId, object target)
    {
        switch (connectionId)
        {
            case 1: ChkMacroDef = (CheckBox)target; break;
            case 2: NumMinRise = (NumericUpDown)target; break;
            case 3: NumMaxRise = (NumericUpDown)target; break;
            case 4: NumVolRatio = (NumericUpDown)target; break;
            case 5: NumMinAmount = (NumericUpDown)target; break;
            case 6: ChkMA60 = (CheckBox)target; break;
            case 7: SldAdhesion = (RangeSlider)target; break;
            case 8: ChkUseCache = (CheckBox)target; break;
            case 9: BtnStart = (Button)target; BtnStart.Click += BtnStart_Click; break;
            case 10: NumConcurrency = (NumericUpDown)target; break;
            case 11: TxtProgressDesc = (TextBlock)target; break;
            case 12: TxtStats = (TextBlock)target; break;
            case 13: PbScan = (ProgressBar)target; break;
            case 14: TxtLog = (System.Windows.Controls.TextBox)target; break;
            default: _contentLoaded = true; break;
        }
    }
}
