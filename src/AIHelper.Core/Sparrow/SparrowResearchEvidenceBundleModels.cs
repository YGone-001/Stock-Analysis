using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

public static class ResearchEvidenceBundleConstants
{
    public const string BundleVersionV1 = "research-evidence-bundle-v1";
    public const string ManifestEntry = "manifest.json";
    public const string ExperimentRecordEntry = "evidence/experiment-record.json";
    public const string PortfolioArtifactEntry = "evidence/portfolio-artifact.json";
    public const string HistoricalDatasetEntry = "evidence/historical-dataset.json";
    public const string StrategyParametersEntry = "evidence/strategy-parameters.json";
    public const string ReexecutionInputsEntry = "inputs/reexecution-inputs.json";

    public const long MaxManifestDecompressedBytes = 1024 * 1024; // 1 MiB
    public const long MaxSingleEntryDecompressedBytes = 128 * 1024 * 1024; // 128 MiB
    public const long MaxTotalDecompressedBytes = 256 * 1024 * 1024; // 256 MiB
    public const int ExactEntryCount = 6;

    public static readonly IReadOnlyList<string> FixedPayloadEntries = Array.AsReadOnly(new[]
    {
        ExperimentRecordEntry,
        HistoricalDatasetEntry,
        PortfolioArtifactEntry,
        StrategyParametersEntry,
        ReexecutionInputsEntry
    });

    public static readonly IReadOnlyList<string> AllAllowedEntries = Array.AsReadOnly(new[]
    {
        ManifestEntry,
        ExperimentRecordEntry,
        HistoricalDatasetEntry,
        PortfolioArtifactEntry,
        StrategyParametersEntry,
        ReexecutionInputsEntry
    });
}

public sealed class ResearchEvidenceBundleEntry
{
    [JsonConstructor]
    public ResearchEvidenceBundleEntry(string logicalPath, long byteLength, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (byteLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        LogicalPath = logicalPath;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    public string LogicalPath { get; }
    public long ByteLength { get; }
    public string Sha256 { get; }
}

public sealed class ResearchEvidenceReexecutionInputs
{
    public const string CurrentInputsVersion = "research-evidence-reexecution-inputs-v1";

    [JsonConstructor]
    public ResearchEvidenceReexecutionInputs(string inputsVersion, double backtestRoundTripCostRate, double backtestSlippageRate)
    {
        if (!string.Equals(inputsVersion, CurrentInputsVersion, StringComparison.Ordinal))
            throw new ArgumentException($"InputsVersion must be '{CurrentInputsVersion}', got '{inputsVersion}'.", nameof(inputsVersion));
        if (!double.IsFinite(backtestRoundTripCostRate) || backtestRoundTripCostRate < 0)
            throw new ArgumentOutOfRangeException(nameof(backtestRoundTripCostRate), "BacktestRoundTripCostRate must be a finite, non-negative number.");
        if (!double.IsFinite(backtestSlippageRate) || backtestSlippageRate < 0)
            throw new ArgumentOutOfRangeException(nameof(backtestSlippageRate), "BacktestSlippageRate must be a finite, non-negative number.");

        InputsVersion = inputsVersion;
        BacktestRoundTripCostRate = backtestRoundTripCostRate;
        BacktestSlippageRate = backtestSlippageRate;
    }

    public ResearchEvidenceReexecutionInputs(double backtestRoundTripCostRate, double backtestSlippageRate)
        : this(CurrentInputsVersion, backtestRoundTripCostRate, backtestSlippageRate) { }

    public string InputsVersion { get; }
    public double BacktestRoundTripCostRate { get; }
    public double BacktestSlippageRate { get; }
}

public sealed class ResearchEvidenceBundleManifest
{
    public const string CurrentBundleVersion = ResearchEvidenceBundleConstants.BundleVersionV1;

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [JsonConstructor]
    public ResearchEvidenceBundleManifest(
        string bundleVersion,
        string experimentId,
        string experimentFingerprint,
        string datasetFingerprint,
        string artifactFingerprint,
        string executionBindingFingerprint,
        string sourceBuildProvenanceFingerprint,
        string executionEnvironmentProvenanceFingerprint,
        IReadOnlyList<ResearchEvidenceBundleEntry> entries,
        string bundleManifestFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionBindingFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBuildProvenanceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionEnvironmentProvenanceFingerprint);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleManifestFingerprint);

        BundleVersion = bundleVersion;
        ExperimentId = experimentId;
        ExperimentFingerprint = experimentFingerprint;
        DatasetFingerprint = datasetFingerprint;
        ArtifactFingerprint = artifactFingerprint;
        ExecutionBindingFingerprint = executionBindingFingerprint;
        SourceBuildProvenanceFingerprint = sourceBuildProvenanceFingerprint;
        ExecutionEnvironmentProvenanceFingerprint = executionEnvironmentProvenanceFingerprint;
        Entries = Array.AsReadOnly(entries.OrderBy(e => e.LogicalPath, StringComparer.Ordinal).ToArray());
        BundleManifestFingerprint = bundleManifestFingerprint;
    }

    public static ResearchEvidenceBundleManifest Create(
        string bundleVersion,
        string experimentId,
        string experimentFingerprint,
        string datasetFingerprint,
        string artifactFingerprint,
        string executionBindingFingerprint,
        string sourceBuildProvenanceFingerprint,
        string executionEnvironmentProvenanceFingerprint,
        IEnumerable<ResearchEvidenceBundleEntry> entries)
    {
        var orderedEntries = entries.OrderBy(e => e.LogicalPath, StringComparer.Ordinal).ToArray();
        string fingerprint = ComputeFingerprint(
            bundleVersion, experimentId, experimentFingerprint, datasetFingerprint, artifactFingerprint,
            executionBindingFingerprint, sourceBuildProvenanceFingerprint, executionEnvironmentProvenanceFingerprint,
            orderedEntries);

        return new ResearchEvidenceBundleManifest(
            bundleVersion, experimentId, experimentFingerprint, datasetFingerprint, artifactFingerprint,
            executionBindingFingerprint, sourceBuildProvenanceFingerprint, executionEnvironmentProvenanceFingerprint,
            orderedEntries, fingerprint);
    }

    public string BundleVersion { get; }
    public string ExperimentId { get; }
    public string ExperimentFingerprint { get; }
    public string DatasetFingerprint { get; }
    public string ArtifactFingerprint { get; }
    public string ExecutionBindingFingerprint { get; }
    public string SourceBuildProvenanceFingerprint { get; }
    public string ExecutionEnvironmentProvenanceFingerprint { get; }
    public IReadOnlyList<ResearchEvidenceBundleEntry> Entries { get; }
    public string BundleManifestFingerprint { get; }

    public string ComputeFingerprint() => ComputeFingerprint(
        BundleVersion, ExperimentId, ExperimentFingerprint, DatasetFingerprint, ArtifactFingerprint,
        ExecutionBindingFingerprint, SourceBuildProvenanceFingerprint, ExecutionEnvironmentProvenanceFingerprint,
        Entries);

    public static string ComputeFingerprint(
        string bundleVersion,
        string experimentId,
        string experimentFingerprint,
        string datasetFingerprint,
        string artifactFingerprint,
        string executionBindingFingerprint,
        string sourceBuildProvenanceFingerprint,
        string executionEnvironmentProvenanceFingerprint,
        IReadOnlyList<ResearchEvidenceBundleEntry> entries)
    {
        ManifestFingerprintPayload payload = new(
            bundleVersion,
            experimentId,
            experimentFingerprint,
            datasetFingerprint,
            artifactFingerprint,
            executionBindingFingerprint,
            sourceBuildProvenanceFingerprint,
            executionEnvironmentProvenanceFingerprint,
            entries.OrderBy(e => e.LogicalPath, StringComparer.Ordinal).ToArray());

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    private sealed record ManifestFingerprintPayload(
        string BundleVersion,
        string ExperimentId,
        string ExperimentFingerprint,
        string DatasetFingerprint,
        string ArtifactFingerprint,
        string ExecutionBindingFingerprint,
        string SourceBuildProvenanceFingerprint,
        string ExecutionEnvironmentProvenanceFingerprint,
        IReadOnlyList<ResearchEvidenceBundleEntry> Entries);
}

public static class ResearchEvidenceBundleCheckCodes
{
    public const string ArchiveStructure = "ARCHIVE_STRUCTURE";
    public const string EntryIntegrity = "ENTRY_INTEGRITY";
    public const string ManifestFingerprint = "MANIFEST_FINGERPRINT";
    public const string ExperimentRecord = "EXPERIMENT_RECORD";
    public const string PortfolioArtifact = "PORTFOLIO_ARTIFACT";
    public const string ArtifactLineage = "ARTIFACT_LINEAGE";
    public const string DatasetIdentity = "DATASET_IDENTITY";
    public const string ParameterIdentity = "PARAMETER_IDENTITY";
    public const string ReexecutionInputs = "REEXECUTION_INPUTS";
    public const string StoredProvenance = "STORED_PROVENANCE";
    public const string Reexecution = "REEXECUTION";
}

public static class ResearchEvidenceBundleReasonCodes
{
    public const string BundleArchiveInvalid = "BUNDLE_ARCHIVE_INVALID";
    public const string BundleSecurityLimitExceeded = "BUNDLE_SECURITY_LIMIT_EXCEEDED";
    public const string BundleUnsupportedVersion = "BUNDLE_UNSUPPORTED_VERSION";
    public const string BundleEntryIntegrityFailed = "BUNDLE_ENTRY_INTEGRITY_FAILED";
    public const string BundleManifestFingerprintMismatch = "BUNDLE_MANIFEST_FINGERPRINT_MISMATCH";
    public const string BundleExperimentRecordInvalid = "BUNDLE_EXPERIMENT_RECORD_INVALID";
    public const string BundlePortfolioArtifactInvalid = "BUNDLE_PORTFOLIO_ARTIFACT_INVALID";
    public const string BundleLineageFailed = "BUNDLE_LINEAGE_FAILED";
    public const string BundleDatasetInvalid = "BUNDLE_DATASET_INVALID";
    public const string BundleParametersInvalid = "BUNDLE_PARAMETERS_INVALID";
    public const string BundleReexecutionInputsInvalid = "BUNDLE_REEXECUTION_INPUTS_INVALID";
    public const string BundleStoredProvenanceInvalid = "BUNDLE_STORED_PROVENANCE_INVALID";
    public const string BundleReexecutionDiverged = "BUNDLE_REEXECUTION_DIVERGED";
    public const string BundleReexecutionFailed = "BUNDLE_REEXECUTION_FAILED";
    public const string BundleReexecutionUnsupported = "BUNDLE_REEXECUTION_UNSUPPORTED";
}

public static class ResearchEvidenceBundleCheckStatus
{
    public const string Pass = "Pass";
    public const string Fail = "Fail";
    public const string Unsupported = "Unsupported";
}

public sealed class ResearchEvidenceBundleCheck
{
    public ResearchEvidenceBundleCheck(string checkCode, string status, string? expected, string? actual, string message)
    {
        CheckCode = checkCode;
        Status = status;
        Expected = expected;
        Actual = actual;
        Message = message;
    }

    public string CheckCode { get; }
    public string Status { get; }
    public string? Expected { get; }
    public string? Actual { get; }
    public string Message { get; }
}

/// <summary>
/// Structural verification status of a portable research evidence bundle. This domain describes the
/// bundle evidence only and is independent from <see cref="ResearchEvidenceBundleReexecutionStatus"/>.
/// </summary>
public enum ResearchEvidenceBundleVerificationStatus
{
    /// <summary>All required structural evidence checks passed.</summary>
    Verified,

    /// <summary>At least one required structural verification failed.</summary>
    Failed,

    /// <summary>Structural evidence uses an unsupported contract.</summary>
    Unsupported
}

/// <summary>
/// Per-stage structural status. A stage that was never executed must report <see cref="NotChecked"/>;
/// no stage is ever assumed to have passed.
/// </summary>
public static class ResearchEvidenceBundleStageStatus
{
    /// <summary>The stage was never executed; nothing about it is known.</summary>
    public const string NotChecked = "NotChecked";

    /// <summary>The stage completed and all of its structural requirements passed.</summary>
    public const string Verified = "Verified";

    /// <summary>The stage completed and at least one of its structural requirements failed.</summary>
    public const string Failed = "Failed";

    /// <summary>The stage completed but the stored contract is not supported by this verifier.</summary>
    public const string Unsupported = "Unsupported";
}

public enum ResearchEvidenceBundleReexecutionStatus
{
    NotRequested,
    Equivalent,
    Diverged,
    Unsupported,
    Failed
}

/// <summary>
/// Outcome of verifying a portable research evidence bundle. <see cref="Status"/> reports structural
/// evidence validity only; <see cref="ReexecutionStatus"/> reports the optional computational
/// re-execution. The two domains never modify each other.
/// </summary>
public sealed class ResearchEvidenceBundleVerificationResult
{
    public ResearchEvidenceBundleVerificationResult(
        ResearchEvidenceBundleVerificationStatus status,
        ResearchEvidenceBundleReexecutionStatus reexecutionStatus,
        string? experimentId,
        string? bundleVersion,
        string? bundleManifestFingerprint,
        IReadOnlyList<ResearchEvidenceBundleCheck> checks,
        IReadOnlyList<string> reasonCodes,
        string entryIntegrityStatus = ResearchEvidenceBundleStageStatus.NotChecked,
        string experimentRecordStatus = ResearchEvidenceBundleStageStatus.NotChecked,
        string artifactLineageStatus = ResearchEvidenceBundleStageStatus.NotChecked,
        string datasetStatus = ResearchEvidenceBundleStageStatus.NotChecked,
        string parameterStatus = ResearchEvidenceBundleStageStatus.NotChecked)
    {
        Status = status;
        ReexecutionStatus = reexecutionStatus;
        ExperimentId = experimentId;
        BundleVersion = bundleVersion;
        BundleManifestFingerprint = bundleManifestFingerprint;
        Checks = checks;
        ReasonCodes = reasonCodes;
        FailedCheckCount = checks.Count(c => c.Status != ResearchEvidenceBundleCheckStatus.Pass);
        EntryIntegrityStatus = entryIntegrityStatus;
        ExperimentRecordStatus = experimentRecordStatus;
        ArtifactLineageStatus = artifactLineageStatus;
        DatasetStatus = datasetStatus;
        ParameterStatus = parameterStatus;
    }

    /// <summary>Structural evidence status only. Never derived from <see cref="ReexecutionStatus"/>.</summary>
    public ResearchEvidenceBundleVerificationStatus Status { get; }

    /// <summary>Optional computational re-execution status only. Never derived from <see cref="Status"/>.</summary>
    public ResearchEvidenceBundleReexecutionStatus ReexecutionStatus { get; }

    public string? ExperimentId { get; }
    public string? BundleVersion { get; }
    public string? BundleManifestFingerprint { get; }
    public IReadOnlyList<ResearchEvidenceBundleCheck> Checks { get; }
    public IReadOnlyList<string> ReasonCodes { get; }

    /// <summary>
    /// Aggregate count of every non-passing check, including optional re-execution checks. A structurally
    /// valid bundle with diverged re-execution therefore reports a count greater than zero while
    /// <see cref="Status"/> remains <see cref="ResearchEvidenceBundleVerificationStatus.Verified"/>.
    /// </summary>
    public int FailedCheckCount { get; }

    /// <summary>Stage status: NotChecked, Verified, Failed or Unsupported.</summary>
    public string EntryIntegrityStatus { get; }

    /// <summary>Stage status: NotChecked, Verified, Failed or Unsupported.</summary>
    public string ExperimentRecordStatus { get; }

    /// <summary>Stage status: NotChecked, Verified, Failed or Unsupported.</summary>
    public string ArtifactLineageStatus { get; }

    /// <summary>Stage status: NotChecked, Verified, Failed or Unsupported.</summary>
    public string DatasetStatus { get; }

    /// <summary>Stage status: NotChecked, Verified, Failed or Unsupported.</summary>
    public string ParameterStatus { get; }

    /// <summary>
    /// Command-level success contract used by callers that choose a process exit code: structural evidence is
    /// <see cref="ResearchEvidenceBundleVerificationStatus.Verified"/> and a requested re-execution did not
    /// fail (NotRequested or Equivalent). Deliberately separate from <see cref="Status"/>.
    /// </summary>
    public bool FullVerificationSucceeded =>
        Status == ResearchEvidenceBundleVerificationStatus.Verified
        && ReexecutionStatus is ResearchEvidenceBundleReexecutionStatus.NotRequested or ResearchEvidenceBundleReexecutionStatus.Equivalent;
}

public sealed class ResearchEvidenceBundleExportRequest
{
    public ResearchEvidenceBundleExportRequest(
        string experimentId,
        string experimentStore,
        string artifactPath,
        string datasetPath,
        string parametersPath,
        double backtestRoundTripCostRate,
        double backtestSlippageRate,
        string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parametersPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!double.IsFinite(backtestRoundTripCostRate) || backtestRoundTripCostRate < 0)
            throw new ArgumentOutOfRangeException(nameof(backtestRoundTripCostRate));
        if (!double.IsFinite(backtestSlippageRate) || backtestSlippageRate < 0)
            throw new ArgumentOutOfRangeException(nameof(backtestSlippageRate));

        ExperimentId = experimentId;
        ExperimentStore = experimentStore;
        ArtifactPath = artifactPath;
        DatasetPath = datasetPath;
        ParametersPath = parametersPath;
        BacktestRoundTripCostRate = backtestRoundTripCostRate;
        BacktestSlippageRate = backtestSlippageRate;
        OutputPath = outputPath;
    }

    public string ExperimentId { get; }
    public string ExperimentStore { get; }
    public string ArtifactPath { get; }
    public string DatasetPath { get; }
    public string ParametersPath { get; }
    public double BacktestRoundTripCostRate { get; }
    public double BacktestSlippageRate { get; }
    public string OutputPath { get; }
}

public sealed class ResearchEvidenceBundleExportResult
{
    public ResearchEvidenceBundleExportResult(
        string experimentId,
        string bundleVersion,
        string experimentFingerprint,
        string datasetFingerprint,
        string artifactFingerprint,
        string executionBindingFingerprint,
        string sourceBuildProvenanceFingerprint,
        string executionEnvironmentProvenanceFingerprint,
        string bundleManifestFingerprint,
        string outputPath)
    {
        ExperimentId = experimentId;
        BundleVersion = bundleVersion;
        ExperimentFingerprint = experimentFingerprint;
        DatasetFingerprint = datasetFingerprint;
        ArtifactFingerprint = artifactFingerprint;
        ExecutionBindingFingerprint = executionBindingFingerprint;
        SourceBuildProvenanceFingerprint = sourceBuildProvenanceFingerprint;
        ExecutionEnvironmentProvenanceFingerprint = executionEnvironmentProvenanceFingerprint;
        BundleManifestFingerprint = bundleManifestFingerprint;
        OutputPath = outputPath;
    }

    public string ExperimentId { get; }
    public string BundleVersion { get; }
    public string ExperimentFingerprint { get; }
    public string DatasetFingerprint { get; }
    public string ArtifactFingerprint { get; }
    public string ExecutionBindingFingerprint { get; }
    public string SourceBuildProvenanceFingerprint { get; }
    public string ExecutionEnvironmentProvenanceFingerprint { get; }
    public string BundleManifestFingerprint { get; }
    public string OutputPath { get; }
}
