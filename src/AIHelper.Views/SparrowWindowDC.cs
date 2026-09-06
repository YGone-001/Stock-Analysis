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

public class SparrowWindowDC : HandyControl.Controls.Window, IComponentConnector
{
    private bool _contentLoaded;
    private bool _strategyModeSelectorAdded;
    private readonly SparrowViewModel _vm;

    internal CheckBox ChkMacroDef;
    internal NumericUpDown NumMinRise;
    internal NumericUpDown NumMaxRise;
    internal NumericUpDown NumVolRatio;
    internal NumericUpDown NumMinAmount;
    internal CheckBox ChkMA60;
    internal CheckBox ChkAlpha;
    internal CheckBox ChkUseCache;
    internal RangeSlider SldAdhesion;
    internal RangeSlider SldTurnover;
    internal NumericUpDown NumMomentum;
    internal Button BtnStart;
    internal Button BtnTest;
    internal NumericUpDown NumConcurrency;
    internal TextBlock TxtProgressDesc;
    internal TextBlock TxtStats;
    internal ProgressBar PbScan;
    internal System.Windows.Controls.TextBox TxtLog;

    public SparrowWindowDC(MainViewModel mainVm)
    {
        InitializeComponent();
        _vm = new SparrowViewModel(mainVm);
        this.DataContext = _vm;
        this.Loaded += SparrowWindowDC_Loaded;
    }

    private void SparrowWindowDC_Loaded(object sender, RoutedEventArgs e)
    {
        InsertStrategyModeSelector();

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

    private void InsertStrategyModeSelector()
    {
        if (_strategyModeSelectorAdded
            || !TryFindLinearLayoutParent(ChkMacroDef, out Panel? parent, out UIElement? sibling)
                && !TryFindLinearLayoutParent(BtnStart, out parent, out sibling))
        {
            return;
        }

        var modePanel = new StackPanel
        {
            Name = "SparrowStrategyModePanel",
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        modePanel.Children.Add(new TextBlock
        {
            Text = "策略模式",
            Width = 88,
            VerticalAlignment = VerticalAlignment.Center
        });
        var modeSelector = new System.Windows.Controls.ComboBox
        {
            Width = 160,
            MinHeight = 28
        };
        modeSelector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("AvailableStrategyModes"));
        modeSelector.SetBinding(System.Windows.Controls.ComboBox.SelectedItemProperty, new Binding("StrategyMode")
        {
            Mode = BindingMode.TwoWay
        });
        modePanel.Children.Add(modeSelector);

        int index = parent.Children.IndexOf(sibling);
        parent.Children.Insert(Math.Max(0, index), modePanel);
        _strategyModeSelectorAdded = true;
    }

    private static bool TryFindLinearLayoutParent(
        FrameworkElement element,
        out Panel? parent,
        out UIElement? directChild)
    {
        DependencyObject current = element;
        while (LogicalTreeHelper.GetParent(current) is DependencyObject candidate)
        {
            if (candidate is StackPanel or WrapPanel or DockPanel)
            {
                parent = (Panel)candidate;
                directChild = current as UIElement;
                return directChild != null;
            }
            current = candidate;
        }

        parent = null;
        directChild = null;
        return false;
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

    public void InitializeComponent()
    {
        if (!_contentLoaded)
        {
            _contentLoaded = true;
            Uri resourceLocator = new Uri("/AIHelper;component/views/sparrowwindowdc.xaml", UriKind.Relative);
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
            case 7: ChkAlpha = (CheckBox)target; break;
            case 8: ChkUseCache = (CheckBox)target; break;
            case 9: SldAdhesion = (RangeSlider)target; break;
            case 10: SldTurnover = (RangeSlider)target; break;
            case 11: NumMomentum = (NumericUpDown)target; break;
            case 12: BtnStart = (Button)target; BtnStart.Click += BtnStart_Click; break;
            case 13: BtnTest = (Button)target; BtnTest.Click += BtnTest_Click; break;
            case 14: NumConcurrency = (NumericUpDown)target; break;
            case 15: TxtProgressDesc = (TextBlock)target; break;
            case 16: TxtStats = (TextBlock)target; break;
            case 17: PbScan = (ProgressBar)target; break;
            case 18: TxtLog = (System.Windows.Controls.TextBox)target; break;
            default: _contentLoaded = true; break;
        }
    }
}
