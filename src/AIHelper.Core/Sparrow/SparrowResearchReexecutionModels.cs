using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

public enum ResearchReexecutionStatus
{
    Equivalent,
    Diverged,
    Unsupported,
    Failed
}

public enum ResearchReexecutionCheckStatus
{
    Pass,
    Fail,
    Unsupported
}

public sealed record ResearchReexecutionCheck(
    string Code,
    ResearchReexecutionCheckStatus Status,
    string? Expected,
    string? Actual,
    string Message);

public sealed class ResearchReexecutionValidationResult
{
    public ResearchReexecutionValidationResult(
        ResearchReexecutionStatus status,
        IEnumerable<ResearchReexecutionCheck> checks,
        string? experimentId = null,
        string? experimentFingerprint = null,
        string? originalArtifactFingerprint = null,
        string? reproducedArtifactFingerprint = null,
        string? datasetFingerprint = null,
        string? strategyMode = null,
        string? strategyVersion = null,
        string? strategyParameterFingerprint = null,
        string? portfolioConfigurationFingerprint = null,
        IEnumerable<string>? reasonCodes = null,
        SparrowPortfolioResearchArtifact? reproducedArtifact = null)
    {
        Status = status;
        Checks = Array.AsReadOnly(checks.ToArray());
        ExperimentId = experimentId;
        ExperimentFingerprint = experimentFingerprint;
        OriginalArtifactFingerprint = originalArtifactFingerprint;
        ReproducedArtifactFingerprint = reproducedArtifactFingerprint;
        DatasetFingerprint = datasetFingerprint;
        StrategyMode = strategyMode;
        StrategyVersion = strategyVersion;
        StrategyParameterFingerprint = strategyParameterFingerprint;
        PortfolioConfigurationFingerprint = portfolioConfigurationFingerprint;
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

    [JsonIgnore]
    public SparrowPortfolioResearchArtifact? ReproducedArtifact { get; }

    public int CheckCount => Checks.Count;
    public int FailedCheckCount => Checks.Count(c => c.Status == ResearchReexecutionCheckStatus.Fail);
    public int UnsupportedCheckCount => Checks.Count(c => c.Status == ResearchReexecutionCheckStatus.Unsupported);
    public int PassedCheckCount => Checks.Count(c => c.Status == ResearchReexecutionCheckStatus.Pass);
}

public static class ResearchReexecutionCheckCodes
{
    public const string ReadinessVerified = "READINESS_VERIFIED";
    public const string DatasetLoaded = "DATASET_LOADED";
    public const string DatasetFingerprintMatch = "DATASET_FINGERPRINT_MATCH";
    public const string ExecutableParametersLoaded = "EXECUTABLE_PARAMETERS_LOADED";
    public const string ExecutableParameterFingerprintMatch = "EXECUTABLE_PARAMETER_FINGERPRINT_MATCH";
    public const string BacktestRequestReconstructed = "BACKTEST_REQUEST_RECONSTRUCTED";
    public const string BacktestParameterFingerprintMatch = "BACKTEST_PARAMETER_FINGERPRINT_MATCH";
    public const string PortfolioRequestMatch = "PORTFOLIO_REQUEST_MATCH";
    public const string BacktestReexecuted = "BACKTEST_REEXECUTED";
    public const string PortfolioReexecuted = "PORTFOLIO_REEXECUTED";
    public const string PerformanceReanalyzed = "PERFORMANCE_REANALYZED";
    public const string TradeSequenceMatch = "TRADE_SEQUENCE_MATCH";
    public const string PositionSequenceMatch = "POSITION_SEQUENCE_MATCH";
    public const string EquityCurveMatch = "EQUITY_CURVE_MATCH";
    public const string AttributionMatch = "ATTRIBUTION_MATCH";
    public const string PerformanceSummaryMatch = "PERFORMANCE_SUMMARY_MATCH";
    public const string StrategyFingerprintMatch = "STRATEGY_FINGERPRINT_MATCH";
    public const string PortfolioConfigurationFingerprintMatch = "PORTFOLIO_CONFIGURATION_FINGERPRINT_MATCH";
    public const string AnalysisFingerprintMatch = "ANALYSIS_FINGERPRINT_MATCH";
    public const string ArtifactFingerprintMatch = "ARTIFACT_FINGERPRINT_MATCH";
}

public static class ResearchReexecutionReasonCodes
{
    public const string ReadinessVerificationFailed = "READINESS_VERIFICATION_FAILED";
    public const string ReadinessVerificationUnsupported = "READINESS_VERIFICATION_UNSUPPORTED";
    public const string LegacyRecordUnsupported = "LEGACY_RECORD_UNSUPPORTED";
    public const string LegacyArtifactUnsupported = "LEGACY_ARTIFACT_UNSUPPORTED";
    public const string BenchmarkReproductionUnsupported = "BENCHMARK_REPRODUCTION_UNSUPPORTED";
    public const string DatasetLoadFailed = "DATASET_LOAD_FAILED";
    public const string DatasetFingerprintMismatch = "DATASET_FINGERPRINT_MISMATCH";
    public const string ExecutableParametersMissing = "EXECUTABLE_PARAMETERS_MISSING";
    public const string ExecutableParametersLoadFailed = "EXECUTABLE_PARAMETERS_LOAD_FAILED";
    public const string ExecutableParameterFingerprintMismatch = "EXECUTABLE_PARAMETER_FINGERPRINT_MISMATCH";
    public const string BacktestRequestReconstructionFailed = "BACKTEST_REQUEST_RECONSTRUCTION_FAILED";
    public const string BacktestParameterFingerprintMismatch = "BACKTEST_PARAMETER_FINGERPRINT_MISMATCH";
    public const string PortfolioRequestMismatch = "PORTFOLIO_REQUEST_MISMATCH";
    public const string BacktestExecutionFailed = "BACKTEST_EXECUTION_FAILED";
    public const string PortfolioSimulationFailed = "PORTFOLIO_SIMULATION_FAILED";
    public const string PerformanceAnalysisFailed = "PERFORMANCE_ANALYSIS_FAILED";
    public const string TradeSequenceDiverged = "TRADE_SEQUENCE_DIVERGED";
    public const string PositionSequenceDiverged = "POSITION_SEQUENCE_DIVERGED";
    public const string EquityCurveDiverged = "EQUITY_CURVE_DIVERGED";
    public const string AttributionDiverged = "ATTRIBUTION_DIVERGED";
    public const string PerformanceSummaryDiverged = "PERFORMANCE_SUMMARY_DIVERGED";
    public const string StrategyFingerprintDiverged = "STRATEGY_FINGERPRINT_DIVERGED";
    public const string PortfolioConfigurationFingerprintDiverged = "PORTFOLIO_CONFIGURATION_FINGERPRINT_DIVERGED";
    public const string AnalysisFingerprintDiverged = "ANALYSIS_FINGERPRINT_DIVERGED";
    public const string ArtifactFingerprintDiverged = "ARTIFACT_FINGERPRINT_DIVERGED";
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
