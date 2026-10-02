using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

public enum ResearchReexecutionStatus { Equivalent, Diverged, Unsupported, Failed }
public enum ResearchReexecutionCheckStatus { Pass, Fail, Unsupported }

public sealed record ResearchReexecutionCheck(string Code, ResearchReexecutionCheckStatus Status, string? Expected, string? Actual, string Message);

public sealed class ResearchReexecutionValidationResult
{
    public ResearchReexecutionValidationResult(
        ResearchReexecutionStatus status, IEnumerable<ResearchReexecutionCheck> checks,
        string? experimentId = null, string? experimentFingerprint = null,
        string? originalArtifactFingerprint = null, string? reproducedArtifactFingerprint = null,
        string? datasetFingerprint = null, string? strategyMode = null, string? strategyVersion = null,
        string? strategyParameterFingerprint = null, string? portfolioConfigurationFingerprint = null,
        IEnumerable<string>? reasonCodes = null, SparrowPortfolioResearchArtifact? reproducedArtifact = null)
    {
        Status = status; Checks = Array.AsReadOnly(checks.ToArray());
        ExperimentId = experimentId; ExperimentFingerprint = experimentFingerprint;
        OriginalArtifactFingerprint = originalArtifactFingerprint; ReproducedArtifactFingerprint = reproducedArtifactFingerprint;
        DatasetFingerprint = datasetFingerprint; StrategyMode = strategyMode; StrategyVersion = strategyVersion;
        StrategyParameterFingerprint = strategyParameterFingerprint; PortfolioConfigurationFingerprint = portfolioConfigurationFingerprint;
        ReasonCodes = Array.AsReadOnly((reasonCodes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
        ReproducedArtifact = reproducedArtifact;
    }

    public ResearchReexecutionStatus Status { get; }
    public IReadOnlyList<ResearchReexecutionCheck> Checks { get; }
    public string? ExperimentId { get; }
    public string? ExperimentFingerprint { get; }
    public string? OriginalArtifactFingerprint { get; }
    public string? ReproducedArtifactFingerprint { get; }
    public string? DatasetFingerprint { get; }
    public string? StrategyMode { get; }
    public string? StrategyVersion { get; }
    public string? StrategyParameterFingerprint { get; }
    public string? PortfolioConfigurationFingerprint { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    [JsonIgnore] public SparrowPortfolioResearchArtifact? ReproducedArtifact { get; }
    public int CheckCount => Checks.Count;
    public int FailedCheckCount => Checks.Count(c => c.Status == ResearchReexecutionCheckStatus.Fail);
    public int UnsupportedCheckCount => Checks.Count(c => c.Status == ResearchReexecutionCheckStatus.Unsupported);
    public int PassedCheckCount => Checks.Count(c => c.Status == ResearchReexecutionCheckStatus.Pass);
}

public static class ResearchReexecutionCheckCodes
{
    public const string ReadinessVerified = "READINESS_VERIFIED", DatasetLoaded = "DATASET_LOADED", DatasetFingerprintMatch = "DATASET_FINGERPRINT_MATCH",
        ExecutableParametersLoaded = "EXECUTABLE_PARAMETERS_LOADED", ExecutableParameterFingerprintMatch = "EXECUTABLE_PARAMETER_FINGERPRINT_MATCH",
        BacktestRequestReconstructed = "BACKTEST_REQUEST_RECONSTRUCTED", BacktestParameterFingerprintMatch = "BACKTEST_PARAMETER_FINGERPRINT_MATCH",
        PortfolioRequestMatch = "PORTFOLIO_REQUEST_MATCH", BacktestReexecuted = "BACKTEST_REEXECUTED", PortfolioReexecuted = "PORTFOLIO_REEXECUTED",
        PerformanceReanalyzed = "PERFORMANCE_REANALYZED", TradeSequenceMatch = "TRADE_SEQUENCE_MATCH", PositionSequenceMatch = "POSITION_SEQUENCE_MATCH",
        EquityCurveMatch = "EQUITY_CURVE_MATCH", AttributionMatch = "ATTRIBUTION_MATCH", PerformanceSummaryMatch = "PERFORMANCE_SUMMARY_MATCH",
        StrategyFingerprintMatch = "STRATEGY_FINGERPRINT_MATCH", PortfolioConfigurationFingerprintMatch = "PORTFOLIO_CONFIGURATION_FINGERPRINT_MATCH",
        AnalysisFingerprintMatch = "ANALYSIS_FINGERPRINT_MATCH", ArtifactFingerprintMatch = "ARTIFACT_FINGERPRINT_MATCH";
}

public static class ResearchReexecutionReasonCodes
{
    public const string ReadinessVerificationFailed = "READINESS_VERIFICATION_FAILED", ReadinessVerificationUnsupported = "READINESS_VERIFICATION_UNSUPPORTED",
        LegacyRecordUnsupported = "LEGACY_RECORD_UNSUPPORTED", LegacyArtifactUnsupported = "LEGACY_ARTIFACT_UNSUPPORTED",
        BenchmarkReproductionUnsupported = "BENCHMARK_REPRODUCTION_UNSUPPORTED", DatasetLoadFailed = "DATASET_LOAD_FAILED",
        DatasetFingerprintMismatch = "DATASET_FINGERPRINT_MISMATCH", ExecutableParametersMissing = "EXECUTABLE_PARAMETERS_MISSING",
        ExecutableParametersLoadFailed = "EXECUTABLE_PARAMETERS_LOAD_FAILED", ExecutableParameterFingerprintMismatch = "EXECUTABLE_PARAMETER_FINGERPRINT_MISMATCH",
        BacktestRequestReconstructionFailed = "BACKTEST_REQUEST_RECONSTRUCTION_FAILED", BacktestParameterFingerprintMismatch = "BACKTEST_PARAMETER_FINGERPRINT_MISMATCH",
        PortfolioRequestMismatch = "PORTFOLIO_REQUEST_MISMATCH", BacktestExecutionFailed = "BACKTEST_EXECUTION_FAILED",
        PortfolioSimulationFailed = "PORTFOLIO_SIMULATION_FAILED", PerformanceAnalysisFailed = "PERFORMANCE_ANALYSIS_FAILED",
        TradeSequenceDiverged = "TRADE_SEQUENCE_DIVERGED", PositionSequenceDiverged = "POSITION_SEQUENCE_DIVERGED",
        EquityCurveDiverged = "EQUITY_CURVE_DIVERGED", AttributionDiverged = "ATTRIBUTION_DIVERGED",
        PerformanceSummaryDiverged = "PERFORMANCE_SUMMARY_DIVERGED", StrategyFingerprintDiverged = "STRATEGY_FINGERPRINT_DIVERGED",
        PortfolioConfigurationFingerprintDiverged = "PORTFOLIO_CONFIGURATION_FINGERPRINT_DIVERGED", AnalysisFingerprintDiverged = "ANALYSIS_FINGERPRINT_DIVERGED",
        ArtifactFingerprintDiverged = "ARTIFACT_FINGERPRINT_DIVERGED";
}

public interface ISparrowResearchReexecutionValidator
{
    Task<ResearchReexecutionValidationResult> ValidateReexecutionAsync(
        PersistedResearchExperimentRecord experimentRecord,
        string originalArtifactPath,
        string datasetPath,
        string? parametersPath,
        CancellationToken cancellationToken = default);
}
