using System.Globalization;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

/// <summary>
/// Maps explicit managed-execution inputs into the authoritative generic experiment parameter snapshot.
/// <para>
/// The mapping is explicit per typed parameter snapshot: property order is never used as canonical semantics.
/// A structural completeness test guards against silently dropping a newly added typed parameter.
/// </para>
/// </summary>
public static class ResearchExperimentParameterSnapshotFactory
{
    /// <summary>Versioned contract for the managed experiment parameter mapping.</summary>
    public const string ParameterVersion = "research-experiment-parameters-v1";

    /// <summary>Established analysis-contract token used by the portfolio research analysis fingerprint preimage.</summary>
    public const string AnalysisAnalyzerContract = "PortfolioPerformanceAnalyzerV1";

    /// <summary>Canonical strategy-section keys owned by the execution envelope (not by a typed snapshot).</summary>
    public static readonly IReadOnlyList<string> StrategyEnvelopeKeys = Array.AsReadOnly(new[]
    {
        "StrategyMode", "StrategyVersion", "StartDate", "EndDate", "TopN", "HorizonTradingDays",
        "BacktestRoundTripCostRate", "BacktestSlippageRate"
    });

    /// <summary>Canonical portfolio-section keys.</summary>
    public static readonly IReadOnlyList<string> PortfolioKeys = Array.AsReadOnly(new[]
    {
        "StartDate", "EndDate", "TopN", "HorizonTradingDays", "InitialCapital", "PositionSizingMethod",
        "CommissionRate", "SlippageRate", "ExecutionModel"
    });

    public static ExperimentParameterSnapshot Create(
        SparrowStrategyMode strategyMode,
        string strategyVersion,
        DateOnly startDate,
        DateOnly endDate,
        int topN,
        int horizonTradingDays,
        double backtestRoundTripCostRate,
        double backtestSlippageRate,
        SparrowClassicParameterSnapshot? classicParameters,
        SparrowV2ParameterSnapshot? v2Parameters,
        decimal initialCapital,
        PortfolioPositionSizingMethod positionSizingMethod,
        decimal commissionRate,
        decimal portfolioSlippageRate,
        PortfolioExecutionModel executionModel)
    {
        Dictionary<string, string> strategy = new(StringComparer.Ordinal)
        {
            ["StrategyMode"] = strategyMode.ToString(),
            ["StrategyVersion"] = strategyVersion,
            ["StartDate"] = Date(startDate),
            ["EndDate"] = Date(endDate),
            ["TopN"] = Integer(topN),
            ["HorizonTradingDays"] = Integer(horizonTradingDays),
            ["BacktestRoundTripCostRate"] = Double(backtestRoundTripCostRate),
            ["BacktestSlippageRate"] = Double(backtestSlippageRate)
        };
        AddClassic(strategy, classicParameters);
        AddV2(strategy, v2Parameters);

        Dictionary<string, string> portfolio = new(StringComparer.Ordinal)
        {
            ["StartDate"] = Date(startDate),
            ["EndDate"] = Date(endDate),
            ["TopN"] = Integer(topN),
            ["HorizonTradingDays"] = Integer(horizonTradingDays),
            ["InitialCapital"] = Decimal(initialCapital),
            ["PositionSizingMethod"] = positionSizingMethod.ToString(),
            ["CommissionRate"] = Decimal(commissionRate),
            ["SlippageRate"] = Decimal(portfolioSlippageRate),
            ["ExecutionModel"] = executionModel.ToString()
        };

        Dictionary<string, string> analysis = new(StringComparer.Ordinal)
        {
            ["AnalyzerContract"] = AnalysisAnalyzerContract
        };

        return new ExperimentParameterSnapshot(ParameterVersion, strategy, portfolio, analysis);
    }

    /// <summary>Explicit Classic mapping. Every semantic field of <see cref="SparrowClassicParameterSnapshot"/> must appear here.</summary>
    public static void AddClassic(IDictionary<string, string> target, SparrowClassicParameterSnapshot? parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (parameters is null) return;
        target["Classic.MacroDef"] = Boolean(parameters.MacroDef);
        target["Classic.MinRise"] = Double(parameters.MinRise);
        target["Classic.MaxRise"] = Double(parameters.MaxRise);
        target["Classic.VolRatio"] = Double(parameters.VolRatio);
        target["Classic.MinAmount"] = Double(parameters.MinAmount);
        target["Classic.CheckMA60"] = Boolean(parameters.CheckMA60);
        target["Classic.MinAdhesion"] = Double(parameters.MinAdhesion);
        target["Classic.MaxAdhesion"] = Double(parameters.MaxAdhesion);
    }

    /// <summary>Explicit V2 mapping. Every semantic field of <see cref="SparrowV2ParameterSnapshot"/> must appear here.</summary>
    public static void AddV2(IDictionary<string, string> target, SparrowV2ParameterSnapshot? parameters)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (parameters is null) return;
        target["V2.MacroDef"] = Boolean(parameters.MacroDef);
        target["V2.MinRise"] = Double(parameters.MinRise);
        target["V2.MaxRise"] = Double(parameters.MaxRise);
        target["V2.VolRatio"] = Double(parameters.VolRatio);
        target["V2.MinAmount"] = Double(parameters.MinAmount);
        target["V2.CheckMA60"] = Boolean(parameters.CheckMA60);
        target["V2.MinAdhesion"] = Double(parameters.MinAdhesion);
        target["V2.MaxAdhesion"] = Double(parameters.MaxAdhesion);
        target["V2.MinTurnover"] = Double(parameters.MinTurnover);
        target["V2.MaxTurnover"] = Double(parameters.MaxTurnover);
        target["V2.MomentumThreshold"] = Double(parameters.MomentumThreshold);
        target["V2.CheckAlpha"] = Boolean(parameters.CheckAlpha);
    }

    /// <summary>Canonical typed-snapshot key prefix for a strategy mode.</summary>
    public static string TypedKeyPrefix(SparrowStrategyMode strategyMode) =>
        strategyMode == SparrowStrategyMode.V2 ? "V2." : "Classic.";

    public static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Double(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    public static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Boolean(bool value) => value ? "true" : "false";
}
