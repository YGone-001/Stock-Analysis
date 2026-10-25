using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIHelper.Models;

public static class SparrowStrategyModeExtensions
{
    public static string ToDisplayName(this SparrowStrategyMode mode) => mode switch
    {
        SparrowStrategyMode.Classic => "Classic（原始麻雀）",
        SparrowStrategyMode.V2 => "V2（东财增强）",
        SparrowStrategyMode.Compare => "Classic + V2 双选",
        SparrowStrategyMode.V3 => "V3（动量门限）",
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
}

/// <summary>V3 inherits every V2 enhancement and adds the close-based momentum-quality gates.</summary>
public sealed class SparrowV3UiParameters : SparrowV2UiParameters
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
    public const double RsiMin = 25.0;
    public const double RsiMax = 75.0;
    public const bool RequireMacdBullish = true;
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
    }

    public static bool AreV2EnhancementsRecommended(SparrowV2UiParameters parameters) =>
        parameters.MinTurnover == MinTurnover
        && parameters.MaxTurnover == MaxTurnover
        && parameters.MomentumThreshold == MomentumThreshold
        && parameters.CheckAlpha == CheckAlpha
        && parameters.StrictTurnover == StrictTurnover;

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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SparrowStrategyUiParameters ClassicParameters { get; } = SparrowParameterDefaults.CreateClassic();
    public SparrowV2UiParameters V2Parameters { get; } = SparrowParameterDefaults.CreateV2();
    public SparrowV3UiParameters V3Parameters { get; } = SparrowParameterDefaults.CreateV3();
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
        _ => ClassicParameters
    };

    /// <summary>The V2-enhancement panel target; V3 mode edits its own inherited copy so saved V2 state is never mutated.</summary>
    public SparrowV2UiParameters EnhancementParameters => StrategyMode == SparrowStrategyMode.V3 ? V3Parameters : V2Parameters;

    public bool IsClassicMode => StrategyMode == SparrowStrategyMode.Classic;
    public bool IsV2Mode => StrategyMode == SparrowStrategyMode.V2;
    public bool IsCompareMode => StrategyMode == SparrowStrategyMode.Compare;
    public bool IsV3Mode => StrategyMode == SparrowStrategyMode.V3;
    public bool ShowBaseParameters => true;
    public bool ShowV2Parameters => StrategyMode is SparrowStrategyMode.V2 or SparrowStrategyMode.Compare or SparrowStrategyMode.V3;
    public bool ShowV3Parameters => StrategyMode == SparrowStrategyMode.V3;
    public bool ShowSystemParameters => true;

    public string StrategyDescription => StrategyMode switch
    {
        SparrowStrategyMode.Classic => "原始麻雀：涨幅 + 成交额 + 外/内盘 → 均线结构 → 黏合",
        SparrowStrategyMode.V2 => "增强麻雀：Classic + 换手率 + Alpha + MA5 动量",
        SparrowStrategyMode.V3 => "动量门限麻雀：V2 + RSI 区间 + MACD 多头过滤",
        _ => "A/B 对照工具：同一行情快照比较 Classic 与 V2，不是第三套策略"
    };

    public string BaseParametersHeader => StrategyMode switch
    {
        SparrowStrategyMode.Classic => "麻雀 Classic",
        SparrowStrategyMode.V2 => "基础麻雀条件",
        SparrowStrategyMode.V3 => "基础麻雀条件（V3 继承 V2 全部增强）",
        _ => "Classic / V2 共同条件"
    };

    public string V2ParametersHeader => StrategyMode switch
    {
        SparrowStrategyMode.Compare => "仅 V2 生效",
        SparrowStrategyMode.V3 => "V2 增强条件（作用于 V3）",
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
        OnPropertyChanged(nameof(IsClassicMode));
        OnPropertyChanged(nameof(IsV2Mode));
        OnPropertyChanged(nameof(IsCompareMode));
        OnPropertyChanged(nameof(IsV3Mode));
        OnPropertyChanged(nameof(ShowBaseParameters));
        OnPropertyChanged(nameof(ShowV2Parameters));
        OnPropertyChanged(nameof(ShowV3Parameters));
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
        SparrowV3UiParameters? v3 = null)
    {
        ISparrowStrategyUiParameters common = mode switch
        {
            SparrowStrategyMode.V2 => v2,
            SparrowStrategyMode.V3 => v3 ?? throw new ArgumentNullException(nameof(v3), "V3 parameters are required in V3 mode."),
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
        if (system.MaxConcurrency < 1)
        {
            return Invalid("并发数必须至少为 1。");
        }
        return SparrowParameterValidationResult.Valid;
    }

    private static bool Finite(double value) => double.IsFinite(value);
    private static SparrowParameterValidationResult Invalid(string error) => new(false, error);
}
