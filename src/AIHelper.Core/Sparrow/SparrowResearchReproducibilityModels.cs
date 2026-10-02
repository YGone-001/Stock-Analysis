using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

public enum ResearchReproducibilityVerificationStatus
{
    Verified,
    Failed,
    Unsupported
}

public enum ResearchReproducibilityCheckStatus
{
    Pass,
    Fail,
    Unsupported
}

public sealed record ResearchReproducibilityCheck(
    string Code,
    ResearchReproducibilityCheckStatus Status,
    string? Expected,
    string? Actual,
    string Message);

public sealed class ResearchReproducibilityVerificationResult
{
    public ResearchReproducibilityVerificationResult(
        ResearchReproducibilityVerificationStatus status,
        IEnumerable<ResearchReproducibilityCheck> checks,
        string? experimentId = null,
        string? experimentFingerprint = null,
        string? datasetFingerprint = null,
        string? artifactVersion = null,
        string? artifactFingerprint = null,
        IEnumerable<string>? reasonCodes = null)
    {
        Status = status;
        Checks = Array.AsReadOnly(checks.ToArray());
        ExperimentId = experimentId;
        ExperimentFingerprint = experimentFingerprint;
        DatasetFingerprint = datasetFingerprint;
        ArtifactVersion = artifactVersion;
        ArtifactFingerprint = artifactFingerprint;
        ReasonCodes = Array.AsReadOnly((reasonCodes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
    }

    public ResearchReproducibilityVerificationStatus Status { get; }
    public IReadOnlyList<ResearchReproducibilityCheck> Checks { get; }
    public string? ExperimentId { get; }
    public string? ExperimentFingerprint { get; }
    public string? DatasetFingerprint { get; }
    public string? ArtifactVersion { get; }
    public string? ArtifactFingerprint { get; }
    public IReadOnlyList<string> ReasonCodes { get; }

    public int CheckCount => Checks.Count;
    public int FailedCheckCount => Checks.Count(c => c.Status == ResearchReproducibilityCheckStatus.Fail);
    public int UnsupportedCheckCount => Checks.Count(c => c.Status == ResearchReproducibilityCheckStatus.Unsupported);
    public int PassedCheckCount => Checks.Count(c => c.Status == ResearchReproducibilityCheckStatus.Pass);
}

public static class ResearchReproducibilityCheckCodes
{
    public const string ExperimentRecordValid = "EXPERIMENT_RECORD_VALID";
    public const string ExperimentFingerprintValid = "EXPERIMENT_FINGERPRINT_VALID";
    public const string ParameterSnapshotValid = "PARAMETER_SNAPSHOT_VALID";
    public const string LineageValid = "LINEAGE_VALID";

    public const string ArtifactJsonValid = "ARTIFACT_JSON_VALID";
    public const string ArtifactVersionSupported = "ARTIFACT_VERSION_SUPPORTED";
    public const string ArtifactFingerprintPresent = "ARTIFACT_FINGERPRINT_PRESENT";
    public const string ArtifactFingerprintValid = "ARTIFACT_FINGERPRINT_VALID";

    public const string DatasetFingerprintMatch = "DATASET_FINGERPRINT_MATCH";
    public const string ArtifactReferenceVersionMatch = "ARTIFACT_REFERENCE_VERSION_MATCH";
    public const string ArtifactReferenceFingerprintMatch = "ARTIFACT_REFERENCE_FINGERPRINT_MATCH";

    public const string StrategyIdentityValid = "STRATEGY_IDENTITY_VALID";
    public const string StrategyModeMatch = "STRATEGY_MODE_MATCH";
    public const string StrategyVersionMatch = "STRATEGY_VERSION_MATCH";
    public const string StrategyFingerprintValid = "STRATEGY_FINGERPRINT_VALID";

    public const string PortfolioConfigurationFingerprintValid = "PORTFOLIO_CONFIGURATION_FINGERPRINT_VALID";
    public const string PortfolioConfigurationFingerprintMatch = "PORTFOLIO_CONFIGURATION_FINGERPRINT_MATCH";

    public const string AnalysisFingerprintValid = "ANALYSIS_FINGERPRINT_VALID";

    public const string ExecutionProvenanceBindingPresent = "EXECUTION_PROVENANCE_BINDING_PRESENT";
    public const string ExecutionProvenanceBindingValid = "EXECUTION_PROVENANCE_BINDING_VALID";
    public const string ExecutionProvenanceExperimentMatch = "EXECUTION_PROVENANCE_EXPERIMENT_MATCH";
    public const string ExecutionProvenanceParameterMatch = "EXECUTION_PROVENANCE_PARAMETER_MATCH";
    public const string ExecutionProvenanceStrategyParameterMatch = "EXECUTION_PROVENANCE_STRATEGY_PARAMETER_MATCH";
    public const string ExecutionProvenancePortfolioMatch = "EXECUTION_PROVENANCE_PORTFOLIO_MATCH";
    public const string ExecutionProvenanceAnalysisMatch = "EXECUTION_PROVENANCE_ANALYSIS_MATCH";
    public const string ExecutionProvenanceDatasetMatch = "EXECUTION_PROVENANCE_DATASET_MATCH";
    public const string ExecutionProvenanceArtifactMatch = "EXECUTION_PROVENANCE_ARTIFACT_MATCH";

    public const string ArtifactIdentityUnavailableV1 = "ARTIFACT_IDENTITY_UNAVAILABLE_V1";
    public const string ArtifactVersionUnsupported = "ARTIFACT_VERSION_UNSUPPORTED";
    public const string ExecutionProvenanceBindingUnavailableV1 = "EXECUTION_PROVENANCE_BINDING_UNAVAILABLE_V1";
}

public interface IResearchReproducibilityVerifier
{
    Task<ResearchReproducibilityVerificationResult> VerifyAsync(
        PersistedResearchExperimentRecord experiment,
        string artifactPath,
        CancellationToken cancellationToken = default);
}
