using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

/// <summary>Stable reason codes for refused or unavailable authoritative execution-environment provenance capture.</summary>
public static class ResearchExecutionEnvironmentProvenanceReasonCodes
{
    public const string ExecutionEnvironmentProvenanceUnavailable = "EXECUTION_ENVIRONMENT_PROVENANCE_UNAVAILABLE";
    public const string DependencyManifestUnavailable = "DEPENDENCY_MANIFEST_UNAVAILABLE";
    public const string DependencyManifestAmbiguous = "DEPENDENCY_MANIFEST_AMBIGUOUS";
    public const string DependencyManifestInvalid = "DEPENDENCY_MANIFEST_INVALID";
}

/// <summary>Raised when authoritative execution-environment provenance cannot be captured. No placeholder identity is substituted.</summary>
public sealed class ResearchExecutionEnvironmentProvenanceException : Exception
{
    public ResearchExecutionEnvironmentProvenanceException(string reasonCode, string message) : base(message) => ReasonCode = reasonCode;

    public string ReasonCode { get; }
}

/// <summary>A canonical dependency edge: the referenced library identity and the version the manifest recorded for it.</summary>
public sealed class ResearchResolvedDependencyReference
{
    [JsonConstructor]
    public ResearchResolvedDependencyReference(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Name = ResearchDependencyCanonical.Name(name);
        Version = ResearchDependencyCanonical.Version(version);
    }

    /// <summary>Canonical lowercase-invariant dependency name.</summary>
    public string Name { get; }
    public string Version { get; }

    public override string ToString() => Name + "/" + Version;
}

/// <summary>One resolved dependency library from the executing tool's authoritative dependency manifest.</summary>
public sealed class ResearchResolvedDependency
{
    [JsonConstructor]
    public ResearchResolvedDependency(string name, string version, string type, IReadOnlyList<ResearchResolvedDependencyReference>? dependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Name = ResearchDependencyCanonical.Name(name);
        Version = ResearchDependencyCanonical.Version(version);
        Type = ResearchDependencyCanonical.Type(type);
        Dependencies = ResearchDependencyCanonical.Edges(dependencies);
    }

    /// <summary>Canonical lowercase-invariant dependency name.</summary>
    public string Name { get; }
    public string Version { get; }
    /// <summary>Factual manifest dependency type, canonicalized to lowercase (for example 'package' or 'project').</summary>
    public string Type { get; }
    /// <summary>Deterministically ordered, exact-duplicate-free dependency edges.</summary>
    public IReadOnlyList<ResearchResolvedDependencyReference> Dependencies { get; }

    public override string ToString() => Name + "/" + Version + ":" + Type;
}

/// <summary>
/// Immutable observation of the runtime environment and normalized dependency graph of the executing research tool.
/// <para>
/// This is observed environment evidence only. It is not reproducible-build proof, binary-to-source proof, code signing,
/// trusted timestamping, SBOM attestation, or dependency authenticity verification.
/// </para>
/// </summary>
public sealed class ResearchExecutionEnvironmentProvenance
{
    /// <summary>Current execution-environment provenance contract version.</summary>
    public const string CurrentProvenanceVersion = "research-execution-environment-provenance-v1";

    /// <summary>Canonical contract token for the normalized .NET dependency manifest identity.</summary>
    public const string DependencyManifestContractVersion = "research-dotnet-dependency-manifest-v1";

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>Creates provenance from observed facts and derives both fingerprints.</summary>
    public static ResearchExecutionEnvironmentProvenance Create(
        string provenanceVersion,
        string frameworkDescription,
        string runtimeVersion,
        string runtimeIdentifier,
        string osPlatform,
        string osArchitecture,
        string processArchitecture,
        string dependencyRuntimeTarget,
        IReadOnlyList<ResearchResolvedDependency>? dependencies)
    {
        IReadOnlyList<ResearchResolvedDependency> canonical = ResearchDependencyCanonical.Libraries(dependencies);
        string dependencyManifestFingerprint = ComputeDependencyManifestFingerprint(dependencyRuntimeTarget, canonical);
        string provenanceFingerprint = ComputeProvenanceFingerprint(provenanceVersion, frameworkDescription, runtimeVersion, runtimeIdentifier,
            osPlatform, osArchitecture, processArchitecture, dependencyRuntimeTarget, dependencyManifestFingerprint);

        return new ResearchExecutionEnvironmentProvenance(provenanceVersion, frameworkDescription, runtimeVersion, runtimeIdentifier,
            osPlatform, osArchitecture, processArchitecture, dependencyRuntimeTarget, canonical,
            dependencyManifestFingerprint, provenanceFingerprint);
    }

    [JsonConstructor]
    public ResearchExecutionEnvironmentProvenance(
        string provenanceVersion,
        string frameworkDescription,
        string runtimeVersion,
        string runtimeIdentifier,
        string osPlatform,
        string osArchitecture,
        string processArchitecture,
        string dependencyRuntimeTarget,
        IReadOnlyList<ResearchResolvedDependency>? dependencies,
        string dependencyManifestFingerprint,
        string provenanceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkDescription);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(osPlatform);
        ArgumentException.ThrowIfNullOrWhiteSpace(osArchitecture);
        ArgumentException.ThrowIfNullOrWhiteSpace(processArchitecture);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyRuntimeTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyManifestFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenanceFingerprint);

        if (!string.Equals(provenanceVersion, CurrentProvenanceVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported execution environment provenance version '{provenanceVersion}'.", nameof(provenanceVersion));

        ProvenanceVersion = provenanceVersion;
        FrameworkDescription = frameworkDescription;
        RuntimeVersion = runtimeVersion;
        RuntimeIdentifier = runtimeIdentifier;
        OSPlatform = osPlatform;
        OSArchitecture = osArchitecture;
        ProcessArchitecture = processArchitecture;
        DependencyRuntimeTarget = dependencyRuntimeTarget;
        Dependencies = ResearchDependencyCanonical.Libraries(dependencies);
        DependencyManifestFingerprint = dependencyManifestFingerprint;
        ProvenanceFingerprint = provenanceFingerprint;

        // Stored hashes are never trusted: both fingerprints are recomputed from canonical semantic content.
        string recomputedManifest = ComputeDependencyManifestFingerprint(DependencyRuntimeTarget, Dependencies);
        if (!string.Equals(recomputedManifest, dependencyManifestFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Dependency manifest fingerprint does not match its canonical payload.", nameof(dependencyManifestFingerprint));

        string recomputedProvenance = ComputeProvenanceFingerprint(provenanceVersion, frameworkDescription, runtimeVersion, runtimeIdentifier,
            osPlatform, osArchitecture, processArchitecture, dependencyRuntimeTarget, dependencyManifestFingerprint);
        if (!string.Equals(recomputedProvenance, provenanceFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Execution environment provenance fingerprint does not match its canonical payload.", nameof(provenanceFingerprint));
    }

    public string ProvenanceVersion { get; }
    /// <summary>Observed framework description, for example '.NET 10.0.12'.</summary>
    public string FrameworkDescription { get; }
    /// <summary>Observed runtime version, for example '10.0.12'.</summary>
    public string RuntimeVersion { get; }
    /// <summary>Observed runtime identifier, for example 'win-x64'.</summary>
    public string RuntimeIdentifier { get; }
    /// <summary>Observed operating system platform family (Windows, Linux, OSX, FreeBSD or Unknown).</summary>
    public string OSPlatform { get; }
    public string OSArchitecture { get; }
    public string ProcessArchitecture { get; }
    /// <summary>Semantic runtime target name of the authoritative dependency manifest.</summary>
    public string DependencyRuntimeTarget { get; }
    /// <summary>Deterministically ordered normalized resolved dependency libraries.</summary>
    public IReadOnlyList<ResearchResolvedDependency> Dependencies { get; }
    public string DependencyManifestFingerprint { get; }
    public string ProvenanceFingerprint { get; }

    public string ComputeDependencyManifestFingerprint() => ComputeDependencyManifestFingerprint(DependencyRuntimeTarget, Dependencies);

    public string ComputeProvenanceFingerprint() => ComputeProvenanceFingerprint(ProvenanceVersion, FrameworkDescription, RuntimeVersion,
        RuntimeIdentifier, OSPlatform, OSArchitecture, ProcessArchitecture, DependencyRuntimeTarget, DependencyManifestFingerprint);

    /// <summary>Hashes the normalized dependency graph. Raw manifest bytes, whitespace and manifest paths are excluded.</summary>
    public static string ComputeDependencyManifestFingerprint(string dependencyRuntimeTarget, IReadOnlyList<ResearchResolvedDependency>? dependencies)
    {
        DependencyManifestPayload payload = new(DependencyManifestContractVersion, dependencyRuntimeTarget, ResearchDependencyCanonical.Libraries(dependencies));
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    /// <summary>Hashes the observed runtime environment plus the dependency manifest identity.</summary>
    public static string ComputeProvenanceFingerprint(string provenanceVersion, string frameworkDescription, string runtimeVersion,
        string runtimeIdentifier, string osPlatform, string osArchitecture, string processArchitecture, string dependencyRuntimeTarget,
        string dependencyManifestFingerprint)
    {
        ProvenanceFingerprintPayload payload = new(provenanceVersion, frameworkDescription, runtimeVersion, runtimeIdentifier, osPlatform,
            osArchitecture, processArchitecture, dependencyRuntimeTarget, dependencyManifestFingerprint);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    /// <summary>Property declaration order is the canonical payload order. Fingerprints are intentionally absent.</summary>
    private sealed record DependencyManifestPayload(
        string ContractVersion,
        string RuntimeTarget,
        IReadOnlyList<ResearchResolvedDependency> Dependencies);

    private sealed record ProvenanceFingerprintPayload(
        string ProvenanceVersion,
        string FrameworkDescription,
        string RuntimeVersion,
        string RuntimeIdentifier,
        string OSPlatform,
        string OSArchitecture,
        string ProcessArchitecture,
        string DependencyRuntimeTarget,
        string DependencyManifestFingerprint);

}

/// <summary>Canonicalization rules shared by the dependency model types. Names are case-insensitive by NuGet package ID semantics.</summary>
internal static class ResearchDependencyCanonical
{
    public static string Name(string value) => value.Trim().ToLowerInvariant();
    public static string Version(string value) => value.Trim();
    public static string Type(string value) => value.Trim().ToLowerInvariant();

    public static IReadOnlyList<ResearchResolvedDependencyReference> Edges(IReadOnlyList<ResearchResolvedDependencyReference>? edges) =>
        Array.AsReadOnly((edges ?? Array.Empty<ResearchResolvedDependencyReference>())
            .DistinctBy(edge => edge.Name + "/" + edge.Version, StringComparer.Ordinal)
            .OrderBy(edge => edge.Name, StringComparer.Ordinal)
            .ThenBy(edge => edge.Version, StringComparer.Ordinal)
            .ToArray());

    public static IReadOnlyList<ResearchResolvedDependency> Libraries(IReadOnlyList<ResearchResolvedDependency>? libraries) =>
        Array.AsReadOnly((libraries ?? Array.Empty<ResearchResolvedDependency>())
            .DistinctBy(library => library.Name + "/" + library.Version + ":" + library.Type, StringComparer.Ordinal)
            .OrderBy(library => library.Name, StringComparer.Ordinal)
            .ThenBy(library => library.Version, StringComparer.Ordinal)
            .ThenBy(library => library.Type, StringComparer.Ordinal)
            .ToArray());
}

/// <summary>Outcome of comparing recorded execution-environment provenance with the currently observed environment.</summary>
public enum ResearchExecutionEnvironmentProvenanceVerificationStatus
{
    Match,
    Mismatch,
    Unsupported,
    Failed
}

/// <summary>Fixed, deterministic check codes in canonical emission order.</summary>
public static class ResearchExecutionEnvironmentProvenanceCheckCodes
{
    public const string ExecutionEnvironmentProvenancePresent = "EXECUTION_ENVIRONMENT_PROVENANCE_PRESENT",
        ExecutionEnvironmentProvenanceFingerprintValid = "EXECUTION_ENVIRONMENT_PROVENANCE_FINGERPRINT_VALID",
        ExecutionEnvironmentBindingValid = "EXECUTION_ENVIRONMENT_BINDING_VALID",
        FrameworkDescriptionMatch = "FRAMEWORK_DESCRIPTION_MATCH",
        RuntimeVersionMatch = "RUNTIME_VERSION_MATCH",
        RuntimeIdentifierMatch = "RUNTIME_IDENTIFIER_MATCH",
        OSPlatformMatch = "OS_PLATFORM_MATCH",
        OSArchitectureMatch = "OS_ARCHITECTURE_MATCH",
        ProcessArchitectureMatch = "PROCESS_ARCHITECTURE_MATCH",
        DependencyRuntimeTargetMatch = "DEPENDENCY_RUNTIME_TARGET_MATCH",
        DependencyManifestFingerprintMatch = "DEPENDENCY_MANIFEST_FINGERPRINT_MATCH";
}

/// <summary>Stable reason codes for non-Match outcomes.</summary>
public static class ResearchExecutionEnvironmentProvenanceVerificationReasonCodes
{
    public const string ProvenanceMissingInLegacySchema = "EXECUTION_ENVIRONMENT_PROVENANCE_MISSING_IN_LEGACY_SCHEMA",
        ProvenanceMissingInV4Record = "EXECUTION_ENVIRONMENT_PROVENANCE_MISSING_IN_V4_RECORD",
        ProvenanceFingerprintInvalid = "EXECUTION_ENVIRONMENT_PROVENANCE_FINGERPRINT_INVALID",
        BindingInvalid = "EXECUTION_ENVIRONMENT_BINDING_INVALID",
        FrameworkDescriptionMismatch = "FRAMEWORK_DESCRIPTION_MISMATCH",
        RuntimeVersionMismatch = "RUNTIME_VERSION_MISMATCH",
        RuntimeIdentifierMismatch = "RUNTIME_IDENTIFIER_MISMATCH",
        OSPlatformMismatch = "OS_PLATFORM_MISMATCH",
        OSArchitectureMismatch = "OS_ARCHITECTURE_MISMATCH",
        ProcessArchitectureMismatch = "PROCESS_ARCHITECTURE_MISMATCH",
        DependencyRuntimeTargetMismatch = "DEPENDENCY_RUNTIME_TARGET_MISMATCH",
        DependencyManifestFingerprintMismatch = "DEPENDENCY_MANIFEST_FINGERPRINT_MISMATCH",
        ExecutionEnvironmentCaptureFailed = "EXECUTION_ENVIRONMENT_CAPTURE_FAILED";
}

public sealed class ResearchExecutionEnvironmentProvenanceVerificationResult
{
    public ResearchExecutionEnvironmentProvenanceVerificationResult(
        ResearchExecutionEnvironmentProvenanceVerificationStatus status,
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

    public ResearchExecutionEnvironmentProvenanceVerificationStatus Status { get; }
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

/// <summary>Read-only comparison of recorded execution-environment provenance against the currently observed environment.</summary>
public interface IResearchExecutionEnvironmentProvenanceVerifier
{
    ResearchExecutionEnvironmentProvenanceVerificationResult Verify(PersistedResearchExperimentRecord experimentRecord);
}
