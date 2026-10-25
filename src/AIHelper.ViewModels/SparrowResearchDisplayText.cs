using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.ViewModels;

/// <summary>
/// 研究回放/回测窗口的界面文案。数据层的枚举名与机器码（ReasonCode、字段名、版本号）保持原样，
/// 只在这里转换成给人看的中文；导出结果与指纹因此不受界面语言影响。
/// </summary>
internal static class SparrowResearchDisplayText
{
    public const string None = "无";
    public const string Unknown = "未知";

    public static string Strategy(SparrowStrategyMode mode) => mode switch
    {
        SparrowStrategyMode.Classic => "经典策略",
        SparrowStrategyMode.V2 => "V2 策略",
        SparrowStrategyMode.V3 => "V3 动量门限策略",
        SparrowStrategyMode.V4 => "V4 量价形态策略",
        SparrowStrategyMode.Compare => "对比",
        _ => mode.ToString()
    };

    public static string UniverseQuality(HistoricalUniverseQuality value) => value switch
    {
        HistoricalUniverseQuality.Unknown => Unknown,
        HistoricalUniverseQuality.CurrentUniverseFallback => "当前股票池回填",
        HistoricalUniverseQuality.DerivedFromObservations => "由观测数据推导",
        HistoricalUniverseQuality.Partial => "不完整",
        HistoricalUniverseQuality.Complete => "完整",
        _ => value.ToString()
    };

    public static string LifecycleQuality(HistoricalLifecycleQuality value) => value switch
    {
        HistoricalLifecycleQuality.Unknown => Unknown,
        HistoricalLifecycleQuality.CurrentUniverseFallback => "当前股票池回填",
        HistoricalLifecycleQuality.Partial => "不完整",
        HistoricalLifecycleQuality.ListingKnown => "上市日期已知",
        HistoricalLifecycleQuality.Complete => "完整",
        _ => value.ToString()
    };

    public static string PriceAdjustment(string value) => value switch
    {
        "Unknown" or "" => Unknown,
        "Raw" => "不复权",
        "ForwardAdjusted" => "前复权",
        "BackwardAdjusted" => "后复权",
        _ => value
    };

    public static string Provider(DataSourceKind value) => value switch
    {
        DataSourceKind.Unknown => Unknown,
        DataSourceKind.ExternalGateway => "外部网关",
        DataSourceKind.EastMoney => "东方财富",
        DataSourceKind.LocalCache => "本地缓存",
        _ => value.ToString()
    };

    public static string ProviderHealth(ProviderHealthState value) => value switch
    {
        ProviderHealthState.Unknown => "未探测",
        ProviderHealthState.Healthy => "正常",
        ProviderHealthState.Degraded => "降级",
        ProviderHealthState.Unavailable => "不可用",
        _ => value.ToString()
    };

    public static string FailureKind(ProviderFailureKind value) => value switch
    {
        ProviderFailureKind.None => "无",
        ProviderFailureKind.Timeout => "超时",
        ProviderFailureKind.Network => "网络异常",
        ProviderFailureKind.HttpError => "HTTP 错误",
        ProviderFailureKind.Contract => "数据契约不符",
        ProviderFailureKind.Unavailable => "服务不可用",
        ProviderFailureKind.Cancelled => "已取消",
        ProviderFailureKind.Unexpected => "未知异常",
        _ => value.ToString()
    };

    public static string Support(HistoricalReplaySupport value) => value switch
    {
        HistoricalReplaySupport.Supported => "支持",
        HistoricalReplaySupport.Partial => "部分支持",
        HistoricalReplaySupport.Unsupported => "不支持",
        _ => value.ToString()
    };

    /// <summary>规则结论只翻译固定的占位原因文本，其余（如机器码原因）保持原样。</summary>
    public static string Reason(string reasonCode, string reason) => reasonCode switch
    {
        SparrowComparisonReasonCodes.Pass when string.IsNullOrWhiteSpace(reason) || reason == "Passed" => "通过",
        SparrowComparisonReasonCodes.NotRun when string.IsNullOrWhiteSpace(reason) || reason == "Not run" => "未运行",
        SparrowComparisonReasonCodes.Bypassed when string.IsNullOrWhiteSpace(reason) => "已跳过",
        _ => string.IsNullOrWhiteSpace(reason) ? None : reason
    };
}
