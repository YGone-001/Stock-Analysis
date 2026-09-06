using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using AIHelper.Models;
using AIHelper.ViewModels;
using HandyControl.Controls;
using Serilog;
using System.Windows.Media;

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
        BuildStrategyAwareParameterUi(reportFailure: false);
        this.Loaded += SparrowWindowDC_Loaded;
    }

    internal static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        return SparrowUiTreeHelper.FindAncestor<T>(current);
    }

    private void SparrowWindowDC_Loaded(object sender, RoutedEventArgs e)
    {
        BuildStrategyAwareParameterUi(reportFailure: true);

        if (!_strategyModeSelectorAdded)
        {
            Growl.Error("麻雀策略参数界面初始化失败，请重新打开窗口。");
            BtnStart.IsEnabled = false;
            return;
        }

        // Two-way bindings for parameters
        ChkMacroDef.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.MacroDef") { Mode = BindingMode.TwoWay });
        NumMinRise.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinRise") { Mode = BindingMode.TwoWay });
        NumMaxRise.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MaxRise") { Mode = BindingMode.TwoWay });
        NumVolRatio.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.VolRatio") { Mode = BindingMode.TwoWay });
        NumMinAmount.SetBinding(NumericUpDown.ValueProperty, new Binding("Parameters.MinAmount") { Mode = BindingMode.TwoWay });
        ChkMA60.SetBinding(CheckBox.IsCheckedProperty, new Binding("Parameters.CheckMA60") { Mode = BindingMode.TwoWay });
        ChkAlpha.SetBinding(CheckBox.IsCheckedProperty, new Binding("V2Parameters.CheckAlpha") { Mode = BindingMode.TwoWay });
        ChkUseCache.SetBinding(CheckBox.IsCheckedProperty, new Binding("SystemSettings.UseCache") { Mode = BindingMode.TwoWay });
        SldAdhesion.SetBinding(RangeSlider.ValueStartProperty, new Binding("Parameters.MinAdhesion") { Mode = BindingMode.TwoWay });
        SldAdhesion.SetBinding(RangeSlider.ValueEndProperty, new Binding("Parameters.MaxAdhesion") { Mode = BindingMode.TwoWay });
        SldTurnover.SetBinding(RangeSlider.ValueStartProperty, new Binding("V2Parameters.MinTurnover") { Mode = BindingMode.TwoWay });
        SldTurnover.SetBinding(RangeSlider.ValueEndProperty, new Binding("V2Parameters.MaxTurnover") { Mode = BindingMode.TwoWay });
        NumMomentum.SetBinding(NumericUpDown.ValueProperty, new Binding("V2Parameters.MomentumThreshold") { Mode = BindingMode.TwoWay });
        NumConcurrency.SetBinding(NumericUpDown.ValueProperty, new Binding("SystemSettings.MaxConcurrency") { Mode = BindingMode.TwoWay });

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

    private void BuildStrategyAwareParameterUi(bool reportFailure = true)
    {
        if (_strategyModeSelectorAdded)
            return;

        GroupBox? parameterGroup = FindAncestor<GroupBox>(ChkMacroDef);
        Border? parameterBorder = null;
        if (parameterGroup == null)
        {
            // In SparrowWindowDC.baml, parameters are hosted inside the Row 0 Border of the layout Grid.
            DependencyObject? node = ChkMacroDef;
            while (node != null)
            {
                if (node is Border b && b.Parent is Grid && Grid.GetRow(b) == 0)
                {
                    parameterBorder = b;
                    break;
                }
                node = LogicalTreeHelper.GetParent(node) ?? (node is Visual v ? VisualTreeHelper.GetParent(v) : null);
            }
        }

        if (parameterGroup == null && parameterBorder == null)
        {
            if (reportFailure)
            {
                Log.Error(
                    "Failed to locate Sparrow parameter GroupBox; " +
                    "strategy-aware UI was not created.");
                Growl.Error("麻雀策略参数界面初始化失败，请重新打开窗口。");
                BtnStart.IsEnabled = false;
            }
            return;
        }

        Title = "麻雀策略";
        if (parameterGroup != null)
        {
            parameterGroup.Header = "麻雀策略参数";
        }

        // Recreate only the parameter controls so each can be placed in the strategy-aware layout below.
        ChkMacroDef = new CheckBox { Margin = new Thickness(0, 0, 15, 10) };
        ChkMA60 = new CheckBox { Margin = new Thickness(0, 0, 15, 10) };
        ChkAlpha = new CheckBox { Margin = new Thickness(0, 0, 15, 10) };
        ChkUseCache = new CheckBox { Margin = new Thickness(0, 0, 15, 10) };
        NumMinRise = new NumericUpDown { Minimum = -20, Maximum = 20, Width = 65 };
        NumMaxRise = new NumericUpDown { Minimum = -20, Maximum = 20, Width = 65 };
        NumVolRatio = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 75, Increment = 0.1 };
        NumMinAmount = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Width = 90 };
        NumMomentum = new NumericUpDown { Minimum = 0, Maximum = 100, Width = 75, Increment = 0.01 };
        NumConcurrency = new NumericUpDown { Minimum = 1, Maximum = 32, Width = 65 };
        SldAdhesion = new RangeSlider { Minimum = 0, Maximum = 100, Width = 150 };
        SldTurnover = new RangeSlider { Minimum = 0, Maximum = 50, Width = 150 };

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
            Width = 180,
            MinHeight = 28,
            DisplayMemberPath = nameof(SparrowStrategyModeOption.DisplayName),
            SelectedValuePath = nameof(SparrowStrategyModeOption.Mode)
        };
        modeSelector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("AvailableStrategyModeOptions"));
        modeSelector.SetBinding(System.Windows.Controls.ComboBox.SelectedValueProperty, new Binding("StrategyMode")
        {
            Mode = BindingMode.TwoWay
        });
        modePanel.Children.Add(modeSelector);

        var description = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 8)
        };
        description.SetBinding(TextBlock.TextProperty, new Binding("StrategyDescription"));

        ChkMacroDef.Content = "大盘防守";
        ChkMacroDef.ToolTip = "按当前策略定义检查市场环境。";
        ChkMA60.Content = "MA60 趋势确认";
        ChkMA60.ToolTip = "要求价格与 MA20 满足当前策略的 MA60 趋势条件。";
        NumMinRise.ToolTip = "最低当日涨幅；提高会减少未启动标的。";
        NumMaxRise.ToolTip = "最高当日涨幅；降低可减少追高标的。";
        NumMinAmount.ToolTip = "最低成交额，单位万元。";
        NumVolRatio.ToolTip = "要求外盘 > 内盘 × 此比例。";
        SldAdhesion.ToolTip = "MA5/MA10/MA20 最大离散程度；越低代表均线越紧。";
        SldAdhesion.Maximum = 100;

        var marketParameters = new WrapPanel();
        marketParameters.Children.Add(ChkMacroDef);
        marketParameters.Children.Add(CreateRangeEditor("涨幅范围（%）", NumMinRise, NumMaxRise,
            "最低/最高当日涨幅；用于排除未启动或过度追高标的。"));
        marketParameters.Children.Add(CreateEditor("最低成交额（万元）", NumMinAmount,
            "最低成交额，单位万元。"));
        marketParameters.Children.Add(CreateEditor("外盘 / 内盘比", NumVolRatio,
            "要求外盘 > 内盘 × 此比例。"));

        var trendParameters = new WrapPanel();
        trendParameters.Children.Add(ChkMA60);
        trendParameters.Children.Add(CreateEditor("均线黏合度（%）", SldAdhesion,
            "MA5/MA10/MA20 最大离散程度；越低代表均线越紧。"));

        var baseContent = new StackPanel();
        baseContent.Children.Add(SectionLabel("市场与资金"));
        baseContent.Children.Add(marketParameters);
        baseContent.Children.Add(SectionLabel("趋势结构"));
        baseContent.Children.Add(trendParameters);
        var baseGroup = new GroupBox
        {
            Content = baseContent,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8)
        };
        baseGroup.SetBinding(HeaderedContentControl.HeaderProperty, new Binding("BaseParametersHeader"));

        ChkAlpha.Content = "Alpha 基准确认";
        ChkAlpha.ToolTip = "仅 V2：要求个股当日表现不弱于上证基准。";
        SldTurnover.ToolTip = "仅 V2：换手率范围。";
        NumMomentum.ToolTip = "仅 V2：MA5 当前值相对前一窗口的动量阈值。";
        var v2Panel = new WrapPanel();
        v2Panel.Children.Add(CreateEditor("换手率范围（%）", SldTurnover, "仅 V2：换手率范围。"));
        v2Panel.Children.Add(ChkAlpha);
        v2Panel.Children.Add(CreateEditor("MA5 动量阈值", NumMomentum,
            "仅 V2：MA5 当前值相对前一窗口的动量阈值。"));
        var v2Group = new GroupBox
        {
            Content = v2Panel,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(8)
        };
        v2Group.SetBinding(HeaderedContentControl.HeaderProperty, new Binding("V2ParametersHeader"));
        v2Group.SetBinding(UIElement.VisibilityProperty, new Binding("ShowV2Parameters")
        {
            Converter = new BooleanToVisibilityConverter()
        });

        var compareHint = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.SteelBlue,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        compareHint.SetBinding(TextBlock.TextProperty, new Binding("ComparisonHint"));
        compareHint.SetBinding(UIElement.VisibilityProperty, new Binding("IsCompareMode")
        {
            Converter = new BooleanToVisibilityConverter()
        });

        var presetPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8)
        };
        var presetText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        presetText.SetBinding(TextBlock.TextProperty, new Binding("CurrentPresetName"));
        var resetButton = new Button
        {
            Content = "恢复推荐默认",
            Padding = new Thickness(12, 4, 12, 4)
        };
        resetButton.SetBinding(Button.CommandProperty, new Binding("ResetRecommendedDefaultsCommand"));
        presetPanel.Children.Add(presetText);
        presetPanel.Children.Add(resetButton);

        ChkUseCache.Content = "使用缓存";
        ChkUseCache.ToolTip = "运行时数据缓存设置，不改变选股规则。";
        NumConcurrency.ToolTip = "网络并发数，只影响执行速度，不改变选股质量。";
        var systemPanel = new WrapPanel();
        systemPanel.Children.Add(ChkUseCache);
        systemPanel.Children.Add(CreateEditor("并发数", NumConcurrency,
            "网络并发数，只影响执行速度，不改变选股质量。"));
        var systemExpander = new Expander
        {
            Header = "高级 / 系统设置",
            IsExpanded = false,
            Content = systemPanel
        };

        var root = new StackPanel();
        root.Children.Add(modePanel);
        root.Children.Add(description);
        root.Children.Add(baseGroup);
        root.Children.Add(v2Group);
        root.Children.Add(compareHint);
        root.Children.Add(presetPanel);
        root.Children.Add(systemExpander);
        if (parameterGroup != null)
        {
            parameterGroup.Content = root;
        }
        else if (parameterBorder != null)
        {
            parameterBorder.Child = root;
        }
        _strategyModeSelectorAdded = true;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 2, 0, 6)
    };

    private static StackPanel CreateEditor(string label, UIElement editor, string toolTip)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 15, 10),
            ToolTip = toolTip
        };
        panel.Children.Add(new TextBlock
        {
            Text = label + "：",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        });
        panel.Children.Add(editor);
        return panel;
    }

    private static StackPanel CreateRangeEditor(
        string label,
        UIElement minimum,
        UIElement maximum,
        string toolTip)
    {
        StackPanel panel = CreateEditor(label, minimum, toolTip);
        panel.Children.Add(new TextBlock
        {
            Text = " — ",
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(maximum);
        return panel;
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
