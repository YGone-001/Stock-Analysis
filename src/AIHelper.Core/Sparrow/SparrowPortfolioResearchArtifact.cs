using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

public sealed record SparrowPortfolioResearchStrategy(string Mode, string Version, string StrategyFingerprint);
public sealed record SparrowPortfolioResearchSimulationSummary(int TradeCount, int OpenPositionCount, int ClosedPositionCount);
public sealed record SparrowPortfolioResearchBenchmarkSummary(string Status, string? BenchmarkId, string? BenchmarkReturnPercent, string? ExcessReturnPercent);

/// <summary>Immutable, timestamp-free research evidence suitable for deterministic export and comparison.</summary>
public sealed class SparrowPortfolioResearchArtifact
{
    public const string CurrentArtifactVersion = "portfolio-research-v1";
    public SparrowPortfolioResearchArtifact(
        string createdBy,
        SparrowPortfolioPerformanceResult performanceResult,
        IEnumerable<string>? limitations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        ArgumentNullException.ThrowIfNull(performanceResult);
        SparrowPortfolioSimulationResult simulation = performanceResult.SimulationResultReference;
        ArtifactVersion = CurrentArtifactVersion;
        CreatedBy = createdBy;
        DatasetFingerprint = simulation.DatasetFingerprint;
        StrategyFingerprint = SparrowPortfolioResearchFingerprint.Strategy(simulation.Request);
        PortfolioConfigurationFingerprint = SparrowPortfolioResearchFingerprint.Portfolio(simulation.Request);
        AnalysisFingerprint = SparrowPortfolioResearchFingerprint.Analysis(DatasetFingerprint, StrategyFingerprint, PortfolioConfigurationFingerprint);
        Strategy = new SparrowPortfolioResearchStrategy(simulation.Request.StrategyMode.ToString(), simulation.Request.StrategyVersion, StrategyFingerprint);
        PortfolioRequest = simulation.Request;
        SimulationSummary = new SparrowPortfolioResearchSimulationSummary(simulation.Trades.Count,
            simulation.Positions.Count(position => position.Status == PortfolioPositionStatus.Open), simulation.Positions.Count(position => position.Status == PortfolioPositionStatus.Closed));
        PerformanceSummary = performanceResult.Metrics;
        BenchmarkSummary = new SparrowPortfolioResearchBenchmarkSummary("Unavailable", null, null, null);
        Trades = Array.AsReadOnly(simulation.Trades.OrderBy(trade => trade.TradeDate).ThenBy(trade => trade.Symbol, StringComparer.Ordinal).ThenBy(trade => trade.Side).ToArray());
        Positions = Array.AsReadOnly(simulation.Positions.OrderBy(position => position.EntryDate).ThenBy(position => position.Symbol, StringComparer.Ordinal).ToArray());
        EquityCurve = Array.AsReadOnly(performanceResult.EquityCurve.Points.OrderBy(point => point.Date).ToArray());
        Attribution = Array.AsReadOnly(performanceResult.Attribution.OrderBy(item => item.Symbol, StringComparer.Ordinal).ThenBy(item => item.EntryDate).ToArray());
        Warnings = Array.AsReadOnly(performanceResult.Warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Limitations = Array.AsReadOnly((limitations ?? DefaultLimitations).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public string ArtifactVersion { get; }
    public string CreatedBy { get; }
    public string DatasetFingerprint { get; }
    public string StrategyFingerprint { get; }
    public string PortfolioConfigurationFingerprint { get; }
    public string AnalysisFingerprint { get; }
    public SparrowPortfolioResearchStrategy Strategy { get; }
    [JsonPropertyName("portfolio")]
    public PortfolioSimulationRequest PortfolioRequest { get; }
    public SparrowPortfolioResearchSimulationSummary SimulationSummary { get; }
    [JsonPropertyName("performance")]
    public PortfolioPerformanceMetrics PerformanceSummary { get; }
    [JsonPropertyName("benchmark")]
    public SparrowPortfolioResearchBenchmarkSummary BenchmarkSummary { get; }
    public IReadOnlyList<PortfolioTrade> Trades { get; }
    public IReadOnlyList<PortfolioPosition> Positions { get; }
    public IReadOnlyList<PortfolioEquityPoint> EquityCurve { get; }
    public IReadOnlyList<PortfolioAttribution> Attribution { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> Limitations { get; }

    public static readonly IReadOnlyList<string> DefaultLimitations = Array.AsReadOnly(new[]
    {
        "Close based historical simulation only",
        "No intraday execution",
        "No market impact model",
        "No leverage",
        "No optimization",
        "No live trading"
    });
}

public static class SparrowPortfolioResearchFingerprint
{
    public static string Strategy(PortfolioSimulationRequest request) => Hash($"{request.StrategyMode}|{request.StrategyVersion}|{request.StrategyParameterFingerprint}");
    public static string Portfolio(PortfolioSimulationRequest request) => Hash(string.Join('|', request.StartDate.ToString("O"), request.EndDate.ToString("O"), request.TopN, request.HorizonTradingDays,
        request.InitialCapital.ToString(System.Globalization.CultureInfo.InvariantCulture), request.PositionSizingMethod, request.CommissionRate.ToString(System.Globalization.CultureInfo.InvariantCulture), request.SlippageRate.ToString(System.Globalization.CultureInfo.InvariantCulture), request.ExecutionModel));
    public static string Analysis(string datasetFingerprint, string strategyFingerprint, string portfolioConfigurationFingerprint) => Hash($"{SparrowPortfolioResearchArtifact.CurrentArtifactVersion}|PortfolioPerformanceAnalyzerV1|{datasetFingerprint}|{strategyFingerprint}|{portfolioConfigurationFingerprint}");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
