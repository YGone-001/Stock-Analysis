using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

/// <summary>Versioned reporting contract for offline research evidence batch audits.</summary>
public static class ResearchEvidenceBatchAuditConstants
{
    public const string ReportVersionV1 = "research-evidence-batch-audit-report-v1";

    /// <summary>Only immediate child files with this extension are audited (ordinal, case-insensitive comparison).</summary>
    public const string BundleFileExtension = ".zip";

    /// <summary>Upper bound of bundles processed by one invocation; exceeding it rejects the whole audit.</summary>
    public const int MaxBundlesPerInvocation = 500;
}

public enum ResearchEvidenceBatchAuditMode
{
    StructuralOnly,
    StructuralAndReexecution
}

public enum ResearchEvidenceBatchAuditStatus
{
    /// <summary>Every audited bundle passed the requested verification depth.</summary>
    Verified,

    /// <summary>At least one audited bundle did not pass the requested verification depth.</summary>
    Failed,

    /// <summary>No bundle was discovered; this is not a successful evidence audit.</summary>
    Empty
}

public enum ResearchEvidenceBatchAuditReportFormat
{
    Json,
    Markdown
}

/// <summary>Batch-level reason codes. Per-bundle structural reasons reuse the Phase 3.15 codes verbatim.</summary>
public static class ResearchEvidenceBatchAuditReasonCodes
{
    public const string BundleIoFailed = "BATCH_BUNDLE_IO_FAILED";
    public const string BundleVerificationException = "BATCH_BUNDLE_VERIFICATION_EXCEPTION";
    public const string DirectoryInvalid = "BATCH_DIRECTORY_INVALID";
    public const string BundleLimitExceeded = "BATCH_BUNDLE_LIMIT_EXCEEDED";
    public const string OutputExists = "BATCH_OUTPUT_EXISTS";
}

/// <summary>Raised when a batch audit cannot produce a report; carries a stable reason code for the CLI.</summary>
public sealed class ResearchEvidenceBatchAuditException : Exception
{
    public ResearchEvidenceBatchAuditException(string reasonCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>A non-passing verification check recorded for one audited bundle.</summary>
public sealed record ResearchEvidenceBatchAuditCheck(string Code, string Status, string Message);

/// <summary>
/// Factual audit outcome of a single bundle. <see cref="BundleFileName"/> is a relative audit locator,
/// not research identity; no absolute machine path is retained.
/// </summary>
public sealed class ResearchEvidenceBatchAuditBundleResult
{
    public ResearchEvidenceBatchAuditBundleResult(
        string bundleFileName,
        string? experimentId,
        string? bundleVersion,
        string? bundleManifestFingerprint,
        ResearchEvidenceBundleVerificationStatus structuralStatus,
        ResearchEvidenceBundleReexecutionStatus reexecutionStatus,
        bool fullVerificationSucceeded,
        string entryIntegrityStatus,
        string experimentRecordStatus,
        string artifactLineageStatus,
        string datasetStatus,
        string parameterStatus,
        int failedCheckCount,
        IEnumerable<string> reasonCodes,
        IEnumerable<ResearchEvidenceBatchAuditCheck>? checks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleFileName);
        ArgumentNullException.ThrowIfNull(reasonCodes);

        BundleFileName = bundleFileName;
        ExperimentId = experimentId;
        BundleVersion = bundleVersion;
        BundleManifestFingerprint = bundleManifestFingerprint;
        StructuralStatus = structuralStatus;
        ReexecutionStatus = reexecutionStatus;
        FullVerificationSucceeded = fullVerificationSucceeded;
        EntryIntegrityStatus = entryIntegrityStatus;
        ExperimentRecordStatus = experimentRecordStatus;
        ArtifactLineageStatus = artifactLineageStatus;
        DatasetStatus = datasetStatus;
        ParameterStatus = parameterStatus;
        FailedCheckCount = failedCheckCount;
        ReasonCodes = Array.AsReadOnly(reasonCodes.OrderBy(code => code, StringComparer.Ordinal).ToArray());
        Checks = Array.AsReadOnly((checks ?? Array.Empty<ResearchEvidenceBatchAuditCheck>()).ToArray());
    }

    public string BundleFileName { get; }
    public string? ExperimentId { get; }
    public string? BundleVersion { get; }
    public string? BundleManifestFingerprint { get; }

    public ResearchEvidenceBundleVerificationStatus StructuralStatus { get; }
    public ResearchEvidenceBundleReexecutionStatus ReexecutionStatus { get; }
    public bool FullVerificationSucceeded { get; }

    public string EntryIntegrityStatus { get; }
    public string ExperimentRecordStatus { get; }
    public string ArtifactLineageStatus { get; }
    public string DatasetStatus { get; }
    public string ParameterStatus { get; }

    public int FailedCheckCount { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public IReadOnlyList<ResearchEvidenceBatchAuditCheck> Checks { get; }
}

/// <summary>Aggregate counts derived from actual per-bundle results; never assumed.</summary>
public sealed class ResearchEvidenceBatchAuditSummary
{
    [JsonConstructor]
    public ResearchEvidenceBatchAuditSummary(
        int totalBundles,
        int structurallyVerified,
        int structurallyFailed,
        int structurallyUnsupported,
        int reexecutionNotRequested,
        int reexecutionEquivalent,
        int reexecutionDiverged,
        int reexecutionUnsupported,
        int reexecutionFailed,
        int fullySuccessful,
        int notFullySuccessful)
    {
        TotalBundles = totalBundles;
        StructurallyVerified = structurallyVerified;
        StructurallyFailed = structurallyFailed;
        StructurallyUnsupported = structurallyUnsupported;
        ReexecutionNotRequested = reexecutionNotRequested;
        ReexecutionEquivalent = reexecutionEquivalent;
        ReexecutionDiverged = reexecutionDiverged;
        ReexecutionUnsupported = reexecutionUnsupported;
        ReexecutionFailed = reexecutionFailed;
        FullySuccessful = fullySuccessful;
        NotFullySuccessful = notFullySuccessful;
    }

    public ResearchEvidenceBatchAuditSummary(IReadOnlyList<ResearchEvidenceBatchAuditBundleResult> bundles)
        : this(
            bundles.Count,
            bundles.Count(b => b.StructuralStatus == ResearchEvidenceBundleVerificationStatus.Verified),
            bundles.Count(b => b.StructuralStatus == ResearchEvidenceBundleVerificationStatus.Failed),
            bundles.Count(b => b.StructuralStatus == ResearchEvidenceBundleVerificationStatus.Unsupported),
            bundles.Count(b => b.ReexecutionStatus == ResearchEvidenceBundleReexecutionStatus.NotRequested),
            bundles.Count(b => b.ReexecutionStatus == ResearchEvidenceBundleReexecutionStatus.Equivalent),
            bundles.Count(b => b.ReexecutionStatus == ResearchEvidenceBundleReexecutionStatus.Diverged),
            bundles.Count(b => b.ReexecutionStatus == ResearchEvidenceBundleReexecutionStatus.Unsupported),
            bundles.Count(b => b.ReexecutionStatus == ResearchEvidenceBundleReexecutionStatus.Failed),
            bundles.Count(b => b.FullVerificationSucceeded),
            bundles.Count(b => !b.FullVerificationSucceeded))
    {
    }

    public int TotalBundles { get; }
    public int StructurallyVerified { get; }
    public int StructurallyFailed { get; }
    public int StructurallyUnsupported { get; }

    public int ReexecutionNotRequested { get; }
    public int ReexecutionEquivalent { get; }
    public int ReexecutionDiverged { get; }
    public int ReexecutionUnsupported { get; }
    public int ReexecutionFailed { get; }

    public int FullySuccessful { get; }
    public int NotFullySuccessful { get; }
}

/// <summary>
/// Versioned, deterministic batch audit report. Contains no timestamps, machine identity, absolute paths
/// or Git revisions, so relocating the input directory cannot change its factual contents.
/// </summary>
public sealed class ResearchEvidenceBatchAuditReport
{
    public ResearchEvidenceBatchAuditReport(
        ResearchEvidenceBatchAuditMode auditMode,
        IEnumerable<ResearchEvidenceBatchAuditBundleResult> bundles)
    {
        ArgumentNullException.ThrowIfNull(bundles);

        AuditMode = auditMode;
        Bundles = Array.AsReadOnly(bundles.ToArray());
        Summary = new ResearchEvidenceBatchAuditSummary(Bundles);
        BatchStatus = Summary.TotalBundles == 0
            ? ResearchEvidenceBatchAuditStatus.Empty
            : Summary.FullySuccessful == Summary.TotalBundles
                ? ResearchEvidenceBatchAuditStatus.Verified
                : ResearchEvidenceBatchAuditStatus.Failed;
    }

    public string ReportVersion => ResearchEvidenceBatchAuditConstants.ReportVersionV1;
    public ResearchEvidenceBatchAuditMode AuditMode { get; }
    public ResearchEvidenceBatchAuditStatus BatchStatus { get; }
    public ResearchEvidenceBatchAuditSummary Summary { get; }
    public IReadOnlyList<ResearchEvidenceBatchAuditBundleResult> Bundles { get; }

    /// <summary>True only when the whole requested batch audit succeeded.</summary>
    [JsonIgnore]
    public bool BatchSucceeded => BatchStatus == ResearchEvidenceBatchAuditStatus.Verified;
}
