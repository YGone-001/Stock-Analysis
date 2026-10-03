using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

/// <summary>Stable reason codes for refused or unavailable authoritative source/build provenance capture.</summary>
public static class ResearchSourceBuildProvenanceReasonCodes
{
    public const string SourceProvenanceUnavailable = "SOURCE_PROVENANCE_UNAVAILABLE";
    public const string SourceWorktreeNotClean = "SOURCE_WORKTREE_NOT_CLEAN";
}

/// <summary>
/// Raised when authoritative source/build provenance cannot be captured. The reason code is stable and
/// never substitutes a placeholder identity (no UNKNOWN/CURRENT/LATEST fallback exists).
/// </summary>
public sealed class ResearchSourceBuildProvenanceException : Exception
{
    public ResearchSourceBuildProvenanceException(string reasonCode, string message) : base(message) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

/// <summary>
/// Immutable observation of the source revision and executing module identities present when a completed
/// research execution was recorded.
/// <para>
/// This is observed association evidence only. It does NOT assert that the recorded source tree
/// cryptographically produced the recorded binaries; it is not code signing, trusted timestamping,
/// reproducible-build proof, or supply-chain attestation.
/// </para>
/// </summary>
public sealed class ResearchSourceBuildProvenance
{
    /// <summary>Current provenance contract version.</summary>
    public const string CurrentProvenanceVersion = "research-source-build-provenance-v1";

    /// <summary>The only source state accepted for authoritative V1 provenance.</summary>
    public const string CleanSourceState = "Clean";

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Creates provenance and derives its deterministic fingerprint.</summary>
    public ResearchSourceBuildProvenance(
        string provenanceVersion,
        string sourceCommitSha,
        string sourceTreeSha,
        string sourceState,
        string buildConfiguration,
        string targetFramework,
        string historicalDataToolModuleVersionId,
        string servicesModuleVersionId,
        string coreModuleVersionId)
        : this(provenanceVersion, sourceCommitSha, sourceTreeSha, sourceState, buildConfiguration, targetFramework,
            historicalDataToolModuleVersionId, servicesModuleVersionId, coreModuleVersionId,
            ComputeFingerprint(provenanceVersion, sourceCommitSha, sourceTreeSha, sourceState, buildConfiguration, targetFramework,
                historicalDataToolModuleVersionId, servicesModuleVersionId, coreModuleVersionId))
    {
    }

    [JsonConstructor]
    public ResearchSourceBuildProvenance(
        string provenanceVersion,
        string sourceCommitSha,
        string sourceTreeSha,
        string sourceState,
        string buildConfiguration,
        string targetFramework,
        string historicalDataToolModuleVersionId,
        string servicesModuleVersionId,
        string coreModuleVersionId,
        string provenanceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildConfiguration);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFramework);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceFingerprint);

        if (!string.Equals(provenanceVersion, CurrentProvenanceVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported source/build provenance version '{provenanceVersion}'.", nameof(provenanceVersion));
        if (!string.Equals(sourceState, CleanSourceState, StringComparison.Ordinal))
            throw new ArgumentException($"Authoritative provenance requires SourceState '{CleanSourceState}' but observed '{sourceState}'.", nameof(sourceState));

        ProvenanceVersion = provenanceVersion;
        SourceCommitSha = RequireObjectId(sourceCommitSha, nameof(sourceCommitSha));
        SourceTreeSha = RequireObjectId(sourceTreeSha, nameof(sourceTreeSha));
        SourceState = sourceState;
        BuildConfiguration = buildConfiguration;
        TargetFramework = targetFramework;
        HistoricalDataToolModuleVersionId = RequireModuleVersionId(historicalDataToolModuleVersionId, nameof(historicalDataToolModuleVersionId));
        ServicesModuleVersionId = RequireModuleVersionId(servicesModuleVersionId, nameof(servicesModuleVersionId));
        CoreModuleVersionId = RequireModuleVersionId(coreModuleVersionId, nameof(coreModuleVersionId));
        ProvenanceFingerprint = provenanceFingerprint;

        string recomputed = ComputeFingerprint(provenanceVersion, SourceCommitSha, SourceTreeSha, sourceState, buildConfiguration, targetFramework,
            HistoricalDataToolModuleVersionId, ServicesModuleVersionId, CoreModuleVersionId);
        if (!string.Equals(recomputed, provenanceFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Source/build provenance fingerprint does not match its canonical payload.", nameof(provenanceFingerprint));
    }

    public string ProvenanceVersion { get; }
    public string SourceCommitSha { get; }
    public string SourceTreeSha { get; }
    public string SourceState { get; }
    public string BuildConfiguration { get; }
    public string TargetFramework { get; }
    public string HistoricalDataToolModuleVersionId { get; }
    public string ServicesModuleVersionId { get; }
    public string CoreModuleVersionId { get; }
    public string ProvenanceFingerprint { get; }

    /// <summary>Recomputes the canonical fingerprint for this instance.</summary>
    public string ComputeFingerprint() => ComputeFingerprint(
        ProvenanceVersion, SourceCommitSha, SourceTreeSha, SourceState, BuildConfiguration, TargetFramework,
        HistoricalDataToolModuleVersionId, ServicesModuleVersionId, CoreModuleVersionId);

    /// <summary>Hashes the explicit canonical payload with SHA-256 over UTF-8 canonical JSON.</summary>
    public static string ComputeFingerprint(
        string provenanceVersion,
        string sourceCommitSha,
        string sourceTreeSha,
        string sourceState,
        string buildConfiguration,
        string targetFramework,
        string historicalDataToolModuleVersionId,
        string servicesModuleVersionId,
        string coreModuleVersionId)
    {
        ProvenanceFingerprintPayload payload = new(
            provenanceVersion, sourceCommitSha, sourceTreeSha, sourceState, buildConfiguration, targetFramework,
            historicalDataToolModuleVersionId, servicesModuleVersionId, coreModuleVersionId);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    private static string RequireObjectId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 40 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Source object identity must be a full 40-character lowercase hexadecimal SHA-1 object id.", parameterName);
        return value;
    }

    private static string RequireModuleVersionId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Guid.TryParseExact(value, "D", out Guid parsed))
            throw new ArgumentException("Module identity must be a canonical 'D' format GUID.", parameterName);
        string canonical = parsed.ToString("D");
        if (!string.Equals(canonical, value, StringComparison.Ordinal))
            throw new ArgumentException("Module identity must use canonical lowercase 'D' GUID casing.", parameterName);
        return canonical;
    }

    /// <summary>Property declaration order is the canonical payload order. ProvenanceFingerprint is intentionally absent.</summary>
    private sealed record ProvenanceFingerprintPayload(
        string ProvenanceVersion,
        string SourceCommitSha,
        string SourceTreeSha,
        string SourceState,
        string BuildConfiguration,
        string TargetFramework,
        string HistoricalDataToolModuleVersionId,
        string ServicesModuleVersionId,
        string CoreModuleVersionId);
}

/// <summary>Outcome of comparing recorded V3 source/build provenance with currently observed provenance.</summary>
public enum ResearchSourceBuildProvenanceVerificationStatus
{
    Match,
    Mismatch,
    Unsupported,
    Failed
}

/// <summary>Fixed, deterministic check codes in canonical emission order.</summary>
public static class ResearchSourceBuildProvenanceCheckCodes
{
    public const string SourceBuildProvenancePresent = "SOURCE_BUILD_PROVENANCE_PRESENT",
        SourceBuildProvenanceFingerprintValid = "SOURCE_BUILD_PROVENANCE_FINGERPRINT_VALID",
        SourceBuildBindingValid = "SOURCE_BUILD_BINDING_VALID",
        SourceCommitMatch = "SOURCE_COMMIT_MATCH",
        SourceTreeMatch = "SOURCE_TREE_MATCH",
        SourceStateMatch = "SOURCE_STATE_MATCH",
        BuildConfigurationMatch = "BUILD_CONFIGURATION_MATCH",
        TargetFrameworkMatch = "TARGET_FRAMEWORK_MATCH",
        ToolModuleIdentityMatch = "TOOL_MODULE_IDENTITY_MATCH",
        ServicesModuleIdentityMatch = "SERVICES_MODULE_IDENTITY_MATCH",
        CoreModuleIdentityMatch = "CORE_MODULE_IDENTITY_MATCH";
}

/// <summary>Stable reason codes for non-Match outcomes.</summary>
public static class ResearchSourceBuildProvenanceVerificationReasonCodes
{
    public const string ProvenanceMissingInLegacySchema = "SOURCE_BUILD_PROVENANCE_MISSING_IN_LEGACY_SCHEMA",
        ProvenanceMissingInV3Record = "SOURCE_BUILD_PROVENANCE_MISSING_IN_V3_RECORD",
        ProvenanceFingerprintInvalid = "SOURCE_BUILD_PROVENANCE_FINGERPRINT_INVALID",
        BindingInvalid = "SOURCE_BUILD_BINDING_INVALID",
        SourceCommitMismatch = "SOURCE_COMMIT_MISMATCH",
        SourceTreeMismatch = "SOURCE_TREE_MISMATCH",
        SourceStateMismatch = "SOURCE_STATE_MISMATCH",
        BuildConfigurationMismatch = "BUILD_CONFIGURATION_MISMATCH",
        TargetFrameworkMismatch = "TARGET_FRAMEWORK_MISMATCH",
        ToolModuleIdentityMismatch = "TOOL_MODULE_IDENTITY_MISMATCH",
        ServicesModuleIdentityMismatch = "SERVICES_MODULE_IDENTITY_MISMATCH",
        CoreModuleIdentityMismatch = "CORE_MODULE_IDENTITY_MISMATCH";
}

public sealed class ResearchSourceBuildProvenanceVerificationResult
{
    public ResearchSourceBuildProvenanceVerificationResult(
        ResearchSourceBuildProvenanceVerificationStatus status,
        IEnumerable<ResearchReproducibilityCheck> checks,
        string? experimentId = null,
        string? recordedProvenanceFingerprint = null,
        string? currentProvenanceFingerprint = null,
        IEnumerable<string>? reasonCodes = null)
    {
        Status = status;
        Checks = Array.AsReadOnly(checks.ToArray());
        ExperimentId = experimentId;
        RecordedProvenanceFingerprint = recordedProvenanceFingerprint;
        CurrentProvenanceFingerprint = currentProvenanceFingerprint;
        ReasonCodes = Array.AsReadOnly((reasonCodes ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray());
    }

    public ResearchSourceBuildProvenanceVerificationStatus Status { get; }
    public IReadOnlyList<ResearchReproducibilityCheck> Checks { get; }
    public string? ExperimentId { get; }
    public string? RecordedProvenanceFingerprint { get; }
    public string? CurrentProvenanceFingerprint { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public int CheckCount => Checks.Count;
    public int PassedCheckCount => Checks.Count(check => check.Status == ResearchReproducibilityCheckStatus.Pass);
    public int FailedCheckCount => Checks.Count(check => check.Status == ResearchReproducibilityCheckStatus.Fail);
    public int UnsupportedCheckCount => Checks.Count(check => check.Status == ResearchReproducibilityCheckStatus.Unsupported);
}

/// <summary>Read-only comparison of recorded source/build provenance against the currently observed environment. It never re-executes research.</summary>
public interface IResearchSourceBuildProvenanceVerifier
{
    ResearchSourceBuildProvenanceVerificationResult Verify(PersistedResearchExperimentRecord experimentRecord);
}
