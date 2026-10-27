using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIHelper.Core.Sparrow;

namespace AIHelper.Models;

public static class SparrowStrategyModeExtensions
{
    public static string ToDisplayName(this SparrowStrategyMode mode) => mode switch
    {
        SparrowStrategyMode.Classic => "Classic（原始麻雀）",
        SparrowStrategyMode.V2 => "V2（东财增强）",
        SparrowStrategyMode.Compare => "Classic + V2 双选",
        SparrowStrategyMode.V3 => "V3（动量门限）",
        SparrowStrategyMode.V4 => "V4（量价形态）",
        SparrowStrategyMode.V5 => "V5（周线共振）",
        SparrowStrategyMode.V6 => "V6（分钟回踩）",
        SparrowStrategyMode.PairCompare => "双版本对照",
        _ => mode.ToString()
    };
}

public sealed record SparrowStrategyModeOption(SparrowStrategyMode Mode, string DisplayName)
{
    public SparrowStrategyModeOption(SparrowStrategyMode mode) : this(mode, mode.ToDisplayName()) { }
    public override string ToString() => DisplayName;
}

public interface ISparrowStrategyUiParameters : INotifyPropertyChanged
{
    bool MacroDef { get; set; }
    double MinRise { get; set; }
    double MaxRise { get; set; }
    double MinAmount { get; set; }
    double VolRatio { get; set; }
    bool CheckMA60 { get; set; }
    double MinAdhesion { get; set; }
    double MaxAdhesion { get; set; }
    string AdhesionRangeText { get; }
    bool IsRecommended { get; }
    void MarkRecommended();
}

public class SparrowStrategyUiParameters : ISparrowStrategyUiParameters
{
    private bool _macroDef;
    private double _minRise;
    private double _maxRise;
    private double _minAmount;
    private double _volRatio;
    private bool _checkMa60;
    private double _minAdhesion;
    private double _maxAdhesion;
    private bool _isRecommended;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool MacroDef { get => _macroDef; set => Set(ref _macroDef, value); }
    public double MinRise { get => _minRise; set => Set(ref _minRise, value); }
    public double MaxRise { get => _maxRise; set => Set(ref _maxRise, value); }
    public double MinAmount { get => _minAmount; set => Set(ref _minAmount, value); }
    public double VolRatio { get => _volRatio; set => Set(ref _volRatio, value); }
    public bool CheckMA60 { get => _checkMa60; set => Set(ref _checkMa60, value); }
    public double MinAdhesion
    {
        get => _minAdhesion;
        set
        {
            if (Set(ref _minAdhesion, value))
            {
                OnPropertyChanged(nameof(AdhesionRangeText));
            }
        }
    }
    public double MaxAdhesion
    {
        get => _maxAdhesion;
        set
        {
            if (Set(ref _maxAdhesion, value))
            {
                OnPropertyChanged(nameof(AdhesionRangeText));
            }
        }
    }
    public string AdhesionRangeText => $"当前范围：{MinAdhesion:F1}% ~ {MaxAdhesion:F1}%";
    public bool IsRecommended => _isRecommended;

    public void MarkRecommended()
    {
        if (_isRecommended) return;
        _isRecommended = true;
        OnPropertyChanged(nameof(IsRecommended));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        if (_isRecommended)
        {
            _isRecommended = false;
            OnPropertyChanged(nameof(IsRecommended));
        }
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>V2 enhancement parameters. Unsealed so V3 can inherit the full V2 field set verbatim.</summary>
public class SparrowV2UiParameters : SparrowStrategyUiParameters
{
    private double _minTurnover;
    private double _maxTurnover;
    private double _momentumThreshold;
    private bool _checkAlpha;
    private bool _strictTurnover;

    public double MinTurnover
    {
        get => _minTurnover;
        set
        {
            if (Set(ref _minTurnover, value))
            {
                OnPropertyChanged(nameof(TurnoverRangeText));
            }
        }
    }
    public double MaxTurnover
    {
        get => _maxTurnover;
        set
        {
            if (Set(ref _maxTurnover, value))
            {
                OnPropertyChanged(nameof(TurnoverRangeText));
            }
        }
    }
    public string TurnoverRangeText => $"当前范围：{MinTurnover:F1}% ~ {MaxTurnover:F1}%";
    public double MomentumThreshold { get => _momentumThreshold; set => Set(ref _momentumThreshold, value); }
    public bool CheckAlpha { get => _checkAlpha; set => Set(ref _checkAlpha, value); }
    public bool StrictTurnover { get => _strictTurnover; set => Set(ref _strictTurnover, value); }
    public bool SectorHeatEnabled { get => _sectorHeatEnabled; set => Set(ref _sectorHeatEnabled, value); }
    private bool _sectorHeatEnabled;
}

/// <summary>V3 inherits every V2 enhancement and adds the close-based momentum-quality gates. Unsealed so V4 can inherit verbatim.</summary>
public class SparrowV3UiParameters : SparrowV2UiParameters
{
    private double? _rsiMin;
    private double? _rsiMax;
    private bool _requireMacdBullish;

    public double? RsiMin
    {
        get => _rsiMin;
        set
        {
            if (Set(ref _rsiMin, value))
            {
                OnPropertyChanged(nameof(RsiBandText));
            }
        }
    }
    public double? RsiMax
    {
        get => _rsiMax;
        set
        {
            if (Set(ref _rsiMax, value))
            {
                OnPropertyChanged(nameof(RsiBandText));
            }
        }
    }
    public bool RequireMacdBullish { get => _requireMacdBullish; set => Set(ref _requireMacdBullish, value); }
    public string RsiBandText => RsiMin.HasValue && RsiMax.HasValue
        ? $"当前范围：{RsiMin.Value:F0} ~ {RsiMax.Value:F0}"
        : "RSI 门限未启用";
}

/// <summary>V4 inherits every V3 enhancement and adds the ratio-based volume-pattern gates. Unsealed so V5 can inherit verbatim.</summary>
public class SparrowV4UiParameters : SparrowV3UiParameters
{
    private bool _volumeGatesEnabled;
    private double _volBreakoutMultiple;
    private double _volContractionMaxRatio;

    /// <summary>Master switch: off maps both gate multipliers to null (gates disabled) regardless of their values.</summary>
    public bool VolumeGatesEnabled { get => _volumeGatesEnabled; set => Set(ref _volumeGatesEnabled, value); }
    public double VolBreakoutMultiple
    {
        get => _volBreakoutMultiple;
        set
        {
            if (Set(ref _volBreakoutMultiple, value))
            {
                OnPropertyChanged(nameof(VolumeGateText));
            }
        }
    }
    public double VolContractionMaxRatio
    {
        get => _volContractionMaxRatio;
        set
        {
            if (Set(ref _volContractionMaxRatio, value))
            {
                OnPropertyChanged(nameof(VolumeGateText));
            }
        }
    }
    public string VolumeGateText => VolumeGatesEnabled
        ? $"突破量 ≥ {VolBreakoutMultiple:F1}×近{SparrowParameterDefaults.VolBreakoutLookbackDays}日均量；蓄势量 ≤ {VolContractionMaxRatio:F2}×基期均量"
        : "量价门限未启用";
}

/// <summary>V5 inherits every V4 enhancement and adds the calendar-week MA trend gate.</summary>
public class SparrowV5UiParameters : SparrowV4UiParameters
{
    private bool _requireWeeklyUptrend;

    public bool RequireWeeklyUptrend { get => _requireWeeklyUptrend; set => Set(ref _requireWeeklyUptrend, value); }
}

/// <summary>V6 inherits every V5 enhancement and adds the intraday pullback gate (live scans only).</summary>
public sealed class SparrowV6UiParameters : SparrowV5UiParameters
{
    private bool _intradayGateEnabled;
    private int _intradayPeriodMinutes;
    private int _intradayMaPeriod;

    public bool IntradayGateEnabled { get => _intradayGateEnabled; set => Set(ref _intradayGateEnabled, value); }
    public int IntradayPeriodMinutes
    {
        get => _intradayPeriodMinutes;
        set
        {
            if (Set(ref _intradayPeriodMinutes, value))
            {
                OnPropertyChanged(nameof(IntradayGateText));
            }
        }
    }
    public int IntradayMaPeriod
    {
        get => _intradayMaPeriod;
        set
        {
            if (Set(ref _intradayMaPeriod, value))
            {
                OnPropertyChanged(nameof(IntradayGateText));
            }
        }
    }
    public string IntradayGateText => IntradayGateEnabled
        ? $"{IntradayPeriodMinutes} 分钟收盘须 > 其 MA{IntradayMaPeriod}"
        : "分钟回踩门限未启用";
}

public sealed class SparrowSystemSettings : INotifyPropertyChanged
{
    private int _maxConcurrency = SparrowParameterDefaults.MaxConcurrency;
    private bool _useCache = SparrowParameterDefaults.UseCache;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (_maxConcurrency == value) return;
            _maxConcurrency = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxConcurrency)));
        }
    }

    public bool UseCache
    {
        get => _useCache;
        set
        {
            if (_useCache == value) return;
            _useCache = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseCache)));
        }
    }
}

public static class SparrowParameterDefaults
{
    public const bool MacroDef = true;
    public const double MinRise = 1.0;
    public const double MaxRise = 5.0;
    public const double MinAmountWan = 5000.0;
    public const double VolRatio = 1.10;
    public const bool CheckMA60 = true;
    public const double MinAdhesionPercent = 0.0;
    public const double MaxAdhesionPercent = 4.0;
    public const double MinTurnover = 3.0;
    public const double MaxTurnover = 30.0;
    public const double MomentumThreshold = 0.0;
    public const bool CheckAlpha = true;
    public const bool StrictTurnover = false;
    public const bool SectorHeatEnabled = false;
    public const double RsiMin = 25.0;
    public const double RsiMax = 75.0;
    public const bool RequireMacdBullish = true;
    public const bool VolumeGatesEnabled = true;
    public const double VolBreakoutMultiple = 1.5;
    public const double VolContractionMaxRatio = 0.8;
    public const int VolBreakoutLookbackDays = 5;
    public const int VolContractionLookbackDays = 5;
    public const int VolContractionBaselineDays = 10;
    public const bool RequireWeeklyUptrend = true;
    public const int WeeklyMaPeriod = 20;
    public const bool IntradayGateEnabled = true;
    public const int IntradayPeriodMinutes = 60;
    public const int IntradayMaPeriod = 10;
    public const int MaxConcurrency = 8;
    public const bool UseCache = true;

    public static SparrowStrategyUiParameters CreateClassic()
    {
        var parameters = new SparrowStrategyUiParameters();
        ResetClassic(parameters);
        return parameters;
    }

    public static SparrowV2UiParameters CreateV2()
    {
        var parameters = new SparrowV2UiParameters();
        ResetV2(parameters);
        return parameters;
    }

    public static SparrowV3UiParameters CreateV3()
    {
        var parameters = new SparrowV3UiParameters();
        ResetV3(parameters);
        return parameters;
    }

    public static SparrowV4UiParameters CreateV4()
    {
        var parameters = new SparrowV4UiParameters();
        ResetV4(parameters);
        return parameters;
    }

    public static SparrowV5UiParameters CreateV5()
    {
        var parameters = new SparrowV5UiParameters();
        ResetV5(parameters);
        return parameters;
    }

    public static SparrowV6UiParameters CreateV6()
    {
        var parameters = new SparrowV6UiParameters();
        ResetV6(parameters);
        return parameters;
    }

    public static SparrowSystemSettings CreateSystemSettings() => new()
    {
        MaxConcurrency = MaxConcurrency,
        UseCache = UseCache
    };

    public static void ResetClassic(SparrowStrategyUiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ApplyBase(parameters);
        parameters.MarkRecommended();
    }

    public static void ResetV2(SparrowV2UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ApplyBase(parameters);
        parameters.MinTurnover = MinTurnover;
        parameters.MaxTurnover = MaxTurnover;
        parameters.MomentumThreshold = MomentumThreshold;
        parameters.CheckAlpha = CheckAlpha;
        parameters.StrictTurnover = StrictTurnover;
        parameters.SectorHeatEnabled = SectorHeatEnabled;
        parameters.MarkRecommended();
    }

    public static void ResetV2Enhancements(SparrowV2UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.MinTurnover = MinTurnover;
        parameters.MaxTurnover = MaxTurnover;
        parameters.MomentumThreshold = MomentumThreshold;
        parameters.CheckAlpha = CheckAlpha;
        parameters.StrictTurnover = StrictTurnover;
        parameters.SectorHeatEnabled = SectorHeatEnabled;
    }

    public static bool AreV2EnhancementsRecommended(SparrowV2UiParameters parameters) =>
        parameters.MinTurnover == MinTurnover
        && parameters.MaxTurnover == MaxTurnover
        && parameters.MomentumThreshold == MomentumThreshold
        && parameters.CheckAlpha == CheckAlpha
        && parameters.StrictTurnover == StrictTurnover
        && parameters.SectorHeatEnabled == SectorHeatEnabled;

    public static void ResetV3(SparrowV3UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ResetV2(parameters);
        ApplyV3Gates(parameters);
        // ApplyV3Gates mutates through the setters, which clears the recommended flag set by ResetV2.
        parameters.MarkRecommended();
    }

    public static void ApplyV3Gates(SparrowV3UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.RsiMin = RsiMin;
        parameters.RsiMax = RsiMax;
        parameters.RequireMacdBullish = RequireMacdBullish;
    }

    public static bool AreV3GatesRecommended(SparrowV3UiParameters parameters) =>
        parameters.RsiMin == RsiMin
        && parameters.RsiMax == RsiMax
        && parameters.RequireMacdBullish == RequireMacdBullish;

    public static void ResetV4(SparrowV4UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ResetV3(parameters);
        ApplyV4Gates(parameters);
        parameters.MarkRecommended();
    }

    public static void ApplyV4Gates(SparrowV4UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.VolumeGatesEnabled = VolumeGatesEnabled;
        parameters.VolBreakoutMultiple = VolBreakoutMultiple;
        parameters.VolContractionMaxRatio = VolContractionMaxRatio;
    }

    public static bool AreV4GatesRecommended(SparrowV4UiParameters parameters) =>
        parameters.VolumeGatesEnabled == VolumeGatesEnabled
        && parameters.VolBreakoutMultiple == VolBreakoutMultiple
        && parameters.VolContractionMaxRatio == VolContractionMaxRatio;

    public static void ResetV5(SparrowV5UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ResetV4(parameters);
        parameters.RequireWeeklyUptrend = RequireWeeklyUptrend;
        parameters.MarkRecommended();
    }

    public static void ResetV6(SparrowV6UiParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ResetV5(parameters);
        parameters.IntradayGateEnabled = IntradayGateEnabled;
        parameters.IntradayPeriodMinutes = IntradayPeriodMinutes;
        parameters.IntradayMaPeriod = IntradayMaPeriod;
        parameters.MarkRecommended();
    }

    private static void ApplyBase(SparrowStrategyUiParameters parameters)
    {
        parameters.MacroDef = MacroDef;
        parameters.MinRise = MinRise;
        parameters.MaxRise = MaxRise;
        parameters.MinAmount = MinAmountWan;
        parameters.VolRatio = VolRatio;
        parameters.CheckMA60 = CheckMA60;
        parameters.MinAdhesion = MinAdhesionPercent;
        parameters.MaxAdhesion = MaxAdhesionPercent;
    }
}

/// <summary>Owns independent Classic/V2/V3 strategy values and shared runtime settings.</summary>
public sealed class SparrowParameterUiState : INotifyPropertyChanged
{
    private SparrowStrategyMode _strategyMode = SparrowStrategyMode.Classic;

    public SparrowParameterUiState()
    {
        ClassicParameters.PropertyChanged += StrategyParametersChanged;
        V2Parameters.PropertyChanged += StrategyParametersChanged;
        V3Parameters.PropertyChanged += StrategyParametersChanged;
        V4Parameters.PropertyChanged += StrategyParametersChanged;
        V5Parameters.PropertyChanged += StrategyParametersChanged;
        V6Parameters.PropertyChanged += StrategyParametersChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SparrowStrategyUiParameters ClassicParameters { get; } = SparrowParameterDefaults.CreateClassic();
    public SparrowV2UiParameters V2Parameters { get; } = SparrowParameterDefaults.CreateV2();
    public SparrowV3UiParameters V3Parameters { get; } = SparrowParameterDefaults.CreateV3();
    public SparrowV4UiParameters V4Parameters { get; } = SparrowParameterDefaults.CreateV4();
    public SparrowV5UiParameters V5Parameters { get; } = SparrowParameterDefaults.CreateV5();
    public SparrowV6UiParameters V6Parameters { get; } = SparrowParameterDefaults.CreateV6();
    public SparrowSystemSettings SystemSettings { get; } = SparrowParameterDefaults.CreateSystemSettings();

    public SparrowStrategyMode StrategyMode
    {
        get => _strategyMode;
        set
        {
            if (_strategyMode == value) return;
            _strategyMode = value;
            RaiseModeProperties();
        }
    }

    public ISparrowStrategyUiParameters ActiveParameters => StrategyMode switch
    {
        SparrowStrategyMode.V2 => V2Parameters,
        SparrowStrategyMode.V3 => V3Parameters,
        SparrowStrategyMode.V4 => V4Parameters,
        SparrowStrategyMode.V5 => V5Parameters,
        SparrowStrategyMode.V6 => V6Parameters,
        _ => ClassicParameters
    };

    /// <summary>The V2-enhancement panel target; V3+ modes edit their own inherited copy so saved V2 state is never mutated.</summary>
    public SparrowV2UiParameters EnhancementParameters => StrategyMode switch
    {
        SparrowStrategyMode.V3 => V3Parameters,
        SparrowStrategyMode.V4 => V4Parameters,
        SparrowStrategyMode.V5 => V5Parameters,
        SparrowStrategyMode.V6 => V6Parameters,
        _ => V2Parameters
    };

    /// <summary>The V3 momentum-gate panel target; later modes edit their own inherited copy so saved V3 state is never mutated.</summary>
    public SparrowV3UiParameters MomentumGateParameters => StrategyMode switch
    {
        SparrowStrategyMode.V4 => V4Parameters,
        SparrowStrategyMode.V5 => V5Parameters,
        SparrowStrategyMode.V6 => V6Parameters,
        _ => V3Parameters
    };

    /// <summary>The V4 volume-gate panel target; later modes edit their own inherited copy so saved V4 state is never mutated.</summary>
    public SparrowV4UiParameters VolumeGateParameters => StrategyMode switch
    {
        SparrowStrategyMode.V5 => V5Parameters,
        SparrowStrategyMode.V6 => V6Parameters,
        _ => V4Parameters
    };

    /// <summary>The V5 weekly-gate panel target; V6 edits its own inherited copy so saved V5 state is never mutated.</summary>
    public SparrowV5UiParameters WeeklyGateParameters => StrategyMode == SparrowStrategyMode.V6 ? V6Parameters : V5Parameters;

    public bool IsClassicMode => StrategyMode == SparrowStrategyMode.Classic;
    public bool IsV2Mode => StrategyMode == SparrowStrategyMode.V2;
    public bool IsCompareMode => StrategyMode == SparrowStrategyMode.Compare;
    public bool IsV3Mode => StrategyMode == SparrowStrategyMode.V3;
    public bool IsV4Mode => StrategyMode == SparrowStrategyMode.V4;
    public bool IsV5Mode => StrategyMode == SparrowStrategyMode.V5;
    public bool IsV6Mode => StrategyMode == SparrowStrategyMode.V6;
    public bool IsPairCompareMode => StrategyMode == SparrowStrategyMode.PairCompare;
    public bool ShowBaseParameters => StrategyMode != SparrowStrategyMode.PairCompare;
    public bool ShowPairComparison => StrategyMode == SparrowStrategyMode.PairCompare;
    public bool ShowV2Parameters => StrategyMode is SparrowStrategyMode.V2 or SparrowStrategyMode.Compare or SparrowStrategyMode.V3 or SparrowStrategyMode.V4 or SparrowStrategyMode.V5 or SparrowStrategyMode.V6;
    public bool ShowV3Parameters => StrategyMode is SparrowStrategyMode.V3 or SparrowStrategyMode.V4 or SparrowStrategyMode.V5 or SparrowStrategyMode.V6;
    public bool ShowV4Parameters => StrategyMode is SparrowStrategyMode.V4 or SparrowStrategyMode.V5 or SparrowStrategyMode.V6;
    public bool ShowV5Parameters => StrategyMode is SparrowStrategyMode.V5 or SparrowStrategyMode.V6;
    public bool ShowV6Parameters => StrategyMode == SparrowStrategyMode.V6;
    public bool ShowSystemParameters => true;

    public string StrategyDescription => StrategyMode switch
    {
        SparrowStrategyMode.Classic => "原始麻雀：涨幅 + 成交额 + 外/内盘 → 均线结构 → 黏合",
        SparrowStrategyMode.V2 => "增强麻雀：Classic + 换手率 + Alpha + MA5 动量",
        SparrowStrategyMode.V3 => "动量门限麻雀：V2 + RSI 区间 + MACD 多头过滤",
        SparrowStrategyMode.V4 => "量价形态麻雀：V3 + 缩量蓄势 + 放量突破",
        SparrowStrategyMode.V5 => "周线共振麻雀：V4 + 周线 MA20 趋势向上",
        SparrowStrategyMode.V6 => "分钟回踩麻雀：V5 + 60 分钟回踩确认（仅实盘）",
        SparrowStrategyMode.PairCompare => "双版本对照：同一行情快照比较 V2~V5 中任意两版，两侧各自使用已保存参数",
        _ => "A/B 对照工具：同一行情快照比较 Classic 与 V2，不是第三套策略"
    };

    public string BaseParametersHeader => StrategyMode switch
    {
        SparrowStrategyMode.Classic => "麻雀 Classic",
        SparrowStrategyMode.V2 => "基础麻雀条件",
        SparrowStrategyMode.V3 => "基础麻雀条件（V3 继承 V2 全部增强）",
        SparrowStrategyMode.V4 => "基础麻雀条件（V4 继承 V3 全部增强）",
        SparrowStrategyMode.V5 => "基础麻雀条件（V5 继承 V4 全部增强）",
        SparrowStrategyMode.V6 => "基础麻雀条件（V6 继承 V5 全部增强）",
        _ => "Classic / V2 共同条件"
    };

    public string V2ParametersHeader => StrategyMode switch
    {
        SparrowStrategyMode.Compare => "仅 V2 生效",
        SparrowStrategyMode.V3 => "V2 增强条件（作用于 V3）",
        SparrowStrategyMode.V4 => "V2 增强条件（作用于 V4）",
        SparrowStrategyMode.V5 => "V2 增强条件（作用于 V5）",
        SparrowStrategyMode.V6 => "V2 增强条件（作用于 V6）",
        _ => "V2 增强条件"
    };

    public string ComparisonHint => IsCompareMode
        ? "Compare 使用同一行情快照；V2 增强条件不会应用到 Classic。"
        : "";

    public string CurrentPresetName
    {
        get
        {
            bool recommended = StrategyMode switch
            {
                SparrowStrategyMode.Classic => ClassicParameters.IsRecommended,
                SparrowStrategyMode.V2 => V2Parameters.IsRecommended,
                SparrowStrategyMode.V3 => V3Parameters.IsRecommended
                    && SparrowParameterDefaults.AreV2EnhancementsRecommended(V3Parameters)
                    && SparrowParameterDefaults.AreV3GatesRecommended(V3Parameters),
                SparrowStrategyMode.V4 => V4Parameters.IsRecommended
                    && SparrowParameterDefaults.AreV2EnhancementsRecommended(V4Parameters)
                    && SparrowParameterDefaults.AreV3GatesRecommended(V4Parameters)
                    && SparrowParameterDefaults.AreV4GatesRecommended(V4Parameters),
                SparrowStrategyMode.V5 => V5Parameters.IsRecommended
                    && SparrowParameterDefaults.AreV2EnhancementsRecommended(V5Parameters)
                    && SparrowParameterDefaults.AreV3GatesRecommended(V5Parameters)
                    && SparrowParameterDefaults.AreV4GatesRecommended(V5Parameters)
                    && V5Parameters.RequireWeeklyUptrend == SparrowParameterDefaults.RequireWeeklyUptrend,
                SparrowStrategyMode.V6 => V6Parameters.IsRecommended
                    && SparrowParameterDefaults.AreV2EnhancementsRecommended(V6Parameters)
                    && SparrowParameterDefaults.AreV3GatesRecommended(V6Parameters)
                    && SparrowParameterDefaults.AreV4GatesRecommended(V6Parameters)
                    && V6Parameters.RequireWeeklyUptrend == SparrowParameterDefaults.RequireWeeklyUptrend
                    && V6Parameters.IntradayGateEnabled == SparrowParameterDefaults.IntradayGateEnabled
                    && V6Parameters.IntradayPeriodMinutes == SparrowParameterDefaults.IntradayPeriodMinutes
                    && V6Parameters.IntradayMaPeriod == SparrowParameterDefaults.IntradayMaPeriod,
                _ => ClassicParameters.IsRecommended
                    && SparrowParameterDefaults.AreV2EnhancementsRecommended(V2Parameters)
            };
            return recommended ? "当前预设：推荐默认（14:30 均衡）" : "当前预设：自定义";
        }
    }

    public string AdhesionRangeText => ActiveParameters.AdhesionRangeText;
    public string TurnoverRangeText => EnhancementParameters.TurnoverRangeText;

    public void ResetRecommendedDefaults()
    {
        switch (StrategyMode)
        {
            case SparrowStrategyMode.Classic:
                SparrowParameterDefaults.ResetClassic(ClassicParameters);
                break;
            case SparrowStrategyMode.V2:
                SparrowParameterDefaults.ResetV2(V2Parameters);
                break;
            case SparrowStrategyMode.Compare:
                SparrowParameterDefaults.ResetClassic(ClassicParameters);
                SparrowParameterDefaults.ResetV2Enhancements(V2Parameters);
                break;
            case SparrowStrategyMode.V3:
                SparrowParameterDefaults.ResetV3(V3Parameters);
                break;
            case SparrowStrategyMode.V4:
                SparrowParameterDefaults.ResetV4(V4Parameters);
                break;
            case SparrowStrategyMode.V5:
                SparrowParameterDefaults.ResetV5(V5Parameters);
                break;
            case SparrowStrategyMode.V6:
                SparrowParameterDefaults.ResetV6(V6Parameters);
                break;
        }
        OnPropertyChanged(nameof(CurrentPresetName));
        OnPropertyChanged(nameof(AdhesionRangeText));
        OnPropertyChanged(nameof(TurnoverRangeText));
    }

    private void StrategyParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentPresetName));
        if (e.PropertyName is nameof(ISparrowStrategyUiParameters.MinAdhesion)
            or nameof(ISparrowStrategyUiParameters.MaxAdhesion)
            or nameof(ISparrowStrategyUiParameters.AdhesionRangeText))
        {
            OnPropertyChanged(nameof(AdhesionRangeText));
        }
        if (e.PropertyName is nameof(SparrowV2UiParameters.MinTurnover)
            or nameof(SparrowV2UiParameters.MaxTurnover)
            or nameof(SparrowV2UiParameters.TurnoverRangeText))
        {
            OnPropertyChanged(nameof(TurnoverRangeText));
        }
    }

    private void RaiseModeProperties()
    {
        OnPropertyChanged(nameof(StrategyMode));
        OnPropertyChanged(nameof(ActiveParameters));
        OnPropertyChanged(nameof(EnhancementParameters));
        OnPropertyChanged(nameof(MomentumGateParameters));
        OnPropertyChanged(nameof(VolumeGateParameters));
        OnPropertyChanged(nameof(WeeklyGateParameters));
        OnPropertyChanged(nameof(IsClassicMode));
        OnPropertyChanged(nameof(IsV2Mode));
        OnPropertyChanged(nameof(IsCompareMode));
        OnPropertyChanged(nameof(IsV3Mode));
        OnPropertyChanged(nameof(IsV4Mode));
        OnPropertyChanged(nameof(IsV5Mode));
        OnPropertyChanged(nameof(IsV6Mode));
        OnPropertyChanged(nameof(IsPairCompareMode));
        OnPropertyChanged(nameof(ShowBaseParameters));
        OnPropertyChanged(nameof(ShowPairComparison));
        OnPropertyChanged(nameof(ShowV2Parameters));
        OnPropertyChanged(nameof(ShowV3Parameters));
        OnPropertyChanged(nameof(ShowV4Parameters));
        OnPropertyChanged(nameof(ShowV5Parameters));
        OnPropertyChanged(nameof(ShowV6Parameters));
        OnPropertyChanged(nameof(ShowSystemParameters));
        OnPropertyChanged(nameof(StrategyDescription));
        OnPropertyChanged(nameof(BaseParametersHeader));
        OnPropertyChanged(nameof(V2ParametersHeader));
        OnPropertyChanged(nameof(ComparisonHint));
        OnPropertyChanged(nameof(CurrentPresetName));
        OnPropertyChanged(nameof(AdhesionRangeText));
        OnPropertyChanged(nameof(TurnoverRangeText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public static class SparrowParameterMapper
{
    public static SparrowClassicScanParameters ToClassic(
        SparrowStrategyUiParameters parameters,
        SparrowSystemSettings system) => new()
    {
        MacroDef = parameters.MacroDef,
        MinRise = parameters.MinRise,
        MaxRise = parameters.MaxRise,
        MinAmount = parameters.MinAmount * 10_000.0,
        VolRatio = parameters.VolRatio,
        CheckMA60 = parameters.CheckMA60,
        MinAdhesion = parameters.MinAdhesion / 100.0,
        MaxAdhesion = parameters.MaxAdhesion / 100.0,
        MaxConcurrency = system.MaxConcurrency,
        UseCache = system.UseCache
    };

    public static SparrowScanParameters ToV2(
        SparrowV2UiParameters parameters,
        SparrowSystemSettings system) => ToV2(parameters, parameters, system);

    public static SparrowScanParameters ToV3(
        SparrowV3UiParameters parameters,
        SparrowSystemSettings system) => ToV2(parameters, parameters, system);

    public static SparrowMomentumGateConfig ToMomentumGates(SparrowV3UiParameters parameters) => new(
        parameters.RsiMin,
        parameters.RsiMax,
        parameters.RequireMacdBullish);

    public static SparrowScanParameters ToV4(
        SparrowV4UiParameters parameters,
        SparrowSystemSettings system) => ToV2(parameters, parameters, system);

    /// <summary>The master switch maps to null multipliers so a disabled gate is carried explicitly, not silently.</summary>
    public static SparrowVolumeGateConfig? ToVolumeGates(SparrowV4UiParameters parameters) => parameters.VolumeGatesEnabled
        ? new SparrowVolumeGateConfig(parameters.VolBreakoutMultiple, parameters.VolContractionMaxRatio)
        : new SparrowVolumeGateConfig(VolBreakoutMultiple: null, VolContractionMaxRatio: null);

    public static SparrowScanParameters ToV5(
        SparrowV5UiParameters parameters,
        SparrowSystemSettings system) => ToV2(parameters, parameters, system);

    public static SparrowWeeklyGateConfig ToWeeklyGates(SparrowV5UiParameters parameters) => new(
        parameters.RequireWeeklyUptrend,
        SparrowParameterDefaults.WeeklyMaPeriod);

    public static SparrowScanParameters ToV6(
        SparrowV6UiParameters parameters,
        SparrowSystemSettings system) => ToV2(parameters, parameters, system);

    public static SparrowIntradayGateConfig ToIntradayGates(SparrowV6UiParameters parameters) => new(
        parameters.IntradayGateEnabled,
        parameters.IntradayPeriodMinutes,
        parameters.IntradayMaPeriod,
        SparrowConstants.V6IntradayBarsLimit);

    /// <summary>
    /// Compare uses one editable common parameter set (Classic) for both sides and the saved
    /// V2 state only for enhancement fields. Neither saved strategy state is mutated.
    /// </summary>
    public static (SparrowClassicScanParameters Classic, SparrowScanParameters V2) ToComparison(
        SparrowStrategyUiParameters commonParameters,
        SparrowV2UiParameters v2Enhancements,
        SparrowSystemSettings system) =>
        (ToClassic(commonParameters, system), ToV2(commonParameters, v2Enhancements, system));

    private static SparrowScanParameters ToV2(
        ISparrowStrategyUiParameters common,
        SparrowV2UiParameters enhancements,
        SparrowSystemSettings system) => new()
    {
        MacroDef = common.MacroDef,
        MinRise = common.MinRise,
        MaxRise = common.MaxRise,
        MinAmount = common.MinAmount * 10_000.0,
        VolRatio = common.VolRatio,
        CheckMA60 = common.CheckMA60,
        MinAdhesion = common.MinAdhesion / 100.0,
        MaxAdhesion = common.MaxAdhesion / 100.0,
        MinTurnover = enhancements.MinTurnover,
        MaxTurnover = enhancements.MaxTurnover,
        MomentumThreshold = enhancements.MomentumThreshold,
        CheckAlpha = enhancements.CheckAlpha,
        StrictTurnover = enhancements.StrictTurnover,
        SectorHeatEnabled = enhancements.SectorHeatEnabled,
        MaxConcurrency = system.MaxConcurrency,
        UseCache = system.UseCache
    };
}

public readonly record struct SparrowParameterValidationResult(bool IsValid, string Error)
{
    public static SparrowParameterValidationResult Valid => new(true, "");
}

public static class SparrowParameterValidator
{
    public static SparrowParameterValidationResult Validate(
        SparrowStrategyMode mode,
        SparrowStrategyUiParameters classic,
        SparrowV2UiParameters v2,
        SparrowSystemSettings system,
        SparrowV3UiParameters? v3 = null,
        SparrowV4UiParameters? v4 = null,
        SparrowV5UiParameters? v5 = null,
        SparrowV6UiParameters? v6 = null)
    {
        ISparrowStrategyUiParameters common = mode switch
        {
            SparrowStrategyMode.V2 => v2,
            SparrowStrategyMode.V3 => v3 ?? throw new ArgumentNullException(nameof(v3), "V3 parameters are required in V3 mode."),
            SparrowStrategyMode.V4 => v4 ?? throw new ArgumentNullException(nameof(v4), "V4 parameters are required in V4 mode."),
            SparrowStrategyMode.V5 => v5 ?? throw new ArgumentNullException(nameof(v5), "V5 parameters are required in V5 mode."),
            SparrowStrategyMode.V6 => v6 ?? throw new ArgumentNullException(nameof(v6), "V6 parameters are required in V6 mode."),
            _ => classic
        };
        if (!Finite(common.MinRise) || !Finite(common.MaxRise) || common.MinRise > common.MaxRise)
        {
            return Invalid("最低涨幅不能大于最高涨幅。");
        }
        if (!Finite(common.MinAdhesion) || !Finite(common.MaxAdhesion)
            || common.MinAdhesion > common.MaxAdhesion)
        {
            return Invalid("最低均线黏合度不能大于最高均线黏合度。");
        }
        if (!Finite(common.VolRatio) || common.VolRatio <= 0)
        {
            return Invalid("外盘/内盘比例必须大于 0。");
        }
        if (!Finite(common.MinAmount) || common.MinAmount < 0)
        {
            return Invalid("最低成交额不能小于 0。");
        }
        if (mode is SparrowStrategyMode.V2 or SparrowStrategyMode.Compare
            && (!Finite(v2.MinTurnover) || !Finite(v2.MaxTurnover) || v2.MinTurnover > v2.MaxTurnover))
        {
            return Invalid("最低换手率不能大于最高换手率。");
        }
        if (mode == SparrowStrategyMode.V3 && v3 is not null)
        {
            if ((v3.RsiMin.HasValue && (!Finite(v3.RsiMin.Value) || v3.RsiMin.Value < 0 || v3.RsiMin.Value > 100))
                || (v3.RsiMax.HasValue && (!Finite(v3.RsiMax.Value) || v3.RsiMax.Value < 0 || v3.RsiMax.Value > 100))
                || (v3.RsiMin.HasValue && v3.RsiMax.HasValue && v3.RsiMin.Value > v3.RsiMax.Value))
            {
                return Invalid("RSI 门限必须在 0~100 之间，且下限不能大于上限。");
            }
        }
        if (mode == SparrowStrategyMode.V4 && v4 is not null)
        {
            if ((v4.RsiMin.HasValue && (!Finite(v4.RsiMin.Value) || v4.RsiMin.Value < 0 || v4.RsiMin.Value > 100))
                || (v4.RsiMax.HasValue && (!Finite(v4.RsiMax.Value) || v4.RsiMax.Value < 0 || v4.RsiMax.Value > 100))
                || (v4.RsiMin.HasValue && v4.RsiMax.HasValue && v4.RsiMin.Value > v4.RsiMax.Value))
            {
                return Invalid("RSI 门限必须在 0~100 之间，且下限不能大于上限。");
            }
            if (v4.VolumeGatesEnabled
                && (!Finite(v4.VolBreakoutMultiple) || v4.VolBreakoutMultiple <= 0 || !Finite(v4.VolContractionMaxRatio) || v4.VolContractionMaxRatio <= 0))
            {
                return Invalid("量价门限倍数必须大于 0。");
            }
        }
        if (mode == SparrowStrategyMode.V5 && v5 is not null)
        {
            if ((v5.RsiMin.HasValue && (!Finite(v5.RsiMin.Value) || v5.RsiMin.Value < 0 || v5.RsiMin.Value > 100))
                || (v5.RsiMax.HasValue && (!Finite(v5.RsiMax.Value) || v5.RsiMax.Value < 0 || v5.RsiMax.Value > 100))
                || (v5.RsiMin.HasValue && v5.RsiMax.HasValue && v5.RsiMin.Value > v5.RsiMax.Value))
            {
                return Invalid("RSI 门限必须在 0~100 之间，且下限不能大于上限。");
            }
            if (v5.VolumeGatesEnabled
                && (!Finite(v5.VolBreakoutMultiple) || v5.VolBreakoutMultiple <= 0 || !Finite(v5.VolContractionMaxRatio) || v5.VolContractionMaxRatio <= 0))
            {
                return Invalid("量价门限倍数必须大于 0。");
            }
        }
        if (mode == SparrowStrategyMode.V6 && v6 is not null)
        {
            if ((v6.RsiMin.HasValue && (!Finite(v6.RsiMin.Value) || v6.RsiMin.Value < 0 || v6.RsiMin.Value > 100))
                || (v6.RsiMax.HasValue && (!Finite(v6.RsiMax.Value) || v6.RsiMax.Value < 0 || v6.RsiMax.Value > 100))
                || (v6.RsiMin.HasValue && v6.RsiMax.HasValue && v6.RsiMin.Value > v6.RsiMax.Value))
            {
                return Invalid("RSI 门限必须在 0~100 之间，且下限不能大于上限。");
            }
            if (v6.VolumeGatesEnabled
                && (!Finite(v6.VolBreakoutMultiple) || v6.VolBreakoutMultiple <= 0 || !Finite(v6.VolContractionMaxRatio) || v6.VolContractionMaxRatio <= 0))
            {
                return Invalid("量价门限倍数必须大于 0。");
            }
            if (v6.IntradayGateEnabled
                && (v6.IntradayPeriodMinutes != 30 && v6.IntradayPeriodMinutes != 60 || v6.IntradayMaPeriod <= 0))
            {
                return Invalid("分钟周期必须为 30 或 60，且回踩均线周期必须大于 0。");
            }
        }
        if (system.MaxConcurrency < 1)
        {
            return Invalid("并发数必须至少为 1。");
        }
        return SparrowParameterValidationResult.Valid;
    }

    private static bool Finite(double value) => double.IsFinite(value);
    private static SparrowParameterValidationResult Invalid(string error) => new(false, error);
}
