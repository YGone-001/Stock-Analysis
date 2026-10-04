using AIHelper.Core.Sparrow;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceCheckCodes;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceReasonCodes;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceVerificationReasonCodes;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceVerificationStatus;
using CheckStatus = AIHelper.Core.Sparrow.ResearchReproducibilityCheckStatus;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Compares a record's recorded execution-environment provenance with the currently observed environment.
/// Read-only: no research calculation, no network, no record mutation.
/// </summary>
public sealed class ResearchExecutionEnvironmentProvenanceVerifier : IResearchExecutionEnvironmentProvenanceVerifier
{
    private readonly IResearchExecutionEnvironmentProvenanceProvider _currentProvenance;

    public ResearchExecutionEnvironmentProvenanceVerifier(IResearchExecutionEnvironmentProvenanceProvider? currentProvenance = null) =>
        _currentProvenance = currentProvenance ?? new ResearchExecutionEnvironmentProvenanceProvider();

    public ResearchExecutionEnvironmentProvenanceVerificationResult Verify(PersistedResearchExperimentRecord experimentRecord)
    {
        ArgumentNullException.ThrowIfNull(experimentRecord);

        List<ResearchReproducibilityCheck> checks = new();
        List<string> reasons = new();

        void Add(string code, CheckStatus status, string? expected, string? actual, string message) =>
            checks.Add(new ResearchReproducibilityCheck(code, status, expected, actual, message));

        void AddUnsupported(string code, string? expected, string message) =>
            Add(code, CheckStatus.Unsupported, expected, "<unavailable>", message);

        ResearchExecutionEnvironmentProvenanceVerificationResult Result(ResearchExecutionEnvironmentProvenanceVerificationStatus status, string? recorded, string? current) =>
            new(status, checks, experimentRecord.ExperimentId, recorded, current, reasons);

        ResearchExecutionEnvironmentProvenance? recorded = experimentRecord.ExecutionEnvironmentProvenance;
        bool isCurrentSchema = string.Equals(experimentRecord.SchemaVersion, PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal);

        // 1. EXECUTION_ENVIRONMENT_PROVENANCE_PRESENT
        if (recorded is null)
        {
            if (isCurrentSchema)
            {
                Add(ExecutionEnvironmentProvenancePresent, CheckStatus.Fail, ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion, "<null>", "Record v4 requires execution environment provenance.");
                reasons.Add(ProvenanceMissingInV4Record);
                return Result(Failed, null, null);
            }
            Add(ExecutionEnvironmentProvenancePresent, CheckStatus.Unsupported, ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion, "<null>",
                $"Record schema '{experimentRecord.SchemaVersion}' predates execution environment provenance.");
            reasons.Add(ProvenanceMissingInLegacySchema);
            return Result(Unsupported, null, null);
        }
        Add(ExecutionEnvironmentProvenancePresent, CheckStatus.Pass, ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion, recorded.ProvenanceVersion, "Execution environment provenance is present.");

        // 2. EXECUTION_ENVIRONMENT_PROVENANCE_FINGERPRINT_VALID
        string recomputedManifest = recorded.ComputeDependencyManifestFingerprint();
        string recomputedProvenance = recorded.ComputeProvenanceFingerprint();
        if (!string.Equals(recorded.DependencyManifestFingerprint, recomputedManifest, StringComparison.Ordinal)
            || !string.Equals(recorded.ProvenanceFingerprint, recomputedProvenance, StringComparison.Ordinal))
        {
            Add(ExecutionEnvironmentProvenanceFingerprintValid, CheckStatus.Fail, recomputedProvenance, recorded.ProvenanceFingerprint,
                "Execution environment provenance fingerprint mismatch (tampering detected).");
            reasons.Add(ProvenanceFingerprintInvalid);
            return Result(Failed, recorded.ProvenanceFingerprint, null);
        }
        Add(ExecutionEnvironmentProvenanceFingerprintValid, CheckStatus.Pass, recomputedProvenance, recorded.ProvenanceFingerprint,
            "Execution environment and dependency manifest fingerprint recomputation verified.");

        // 3. EXECUTION_ENVIRONMENT_BINDING_VALID
        ResearchExecutionProvenanceBinding? binding = experimentRecord.ExecutionProvenanceBinding;
        bool bindingValid = binding is not null
            && string.Equals(binding.BindingVersion, ResearchExecutionProvenanceBinding.CurrentBindingVersion, StringComparison.Ordinal)
            && string.Equals(binding.ExecutionEnvironmentProvenanceFingerprint, recorded.ProvenanceFingerprint, StringComparison.Ordinal)
            && string.Equals(binding.BindingFingerprint, binding.ComputeFingerprint(), StringComparison.Ordinal);
        if (!bindingValid)
        {
            Add(ExecutionEnvironmentBindingValid, CheckStatus.Fail, ResearchExecutionProvenanceBinding.CurrentBindingVersion, binding?.BindingVersion ?? "<null>",
                "Execution provenance binding does not bind this execution environment provenance.");
            reasons.Add(BindingInvalid);
            return Result(Failed, recorded.ProvenanceFingerprint, null);
        }
        Add(ExecutionEnvironmentBindingValid, CheckStatus.Pass, binding!.BindingFingerprint, binding.ComputeFingerprint(),
            "Execution provenance binding v3 binds this execution environment provenance.");

        ResearchExecutionEnvironmentProvenance current;
        try
        {
            current = _currentProvenance.Capture();
        }
        catch (ResearchExecutionEnvironmentProvenanceException exception)
        {
            foreach (string code in new[]
            {
                FrameworkDescriptionMatch, RuntimeVersionMatch, RuntimeIdentifierMatch, OSPlatformMatch, OSArchitectureMatch,
                ProcessArchitectureMatch, DependencyRuntimeTargetMatch, DependencyManifestFingerprintMatch
            })
            {
                AddUnsupported(code, null, "Current execution environment is unavailable.");
            }

            reasons.Add(ExecutionEnvironmentCaptureFailed);
            reasons.Add(exception.ReasonCode);
            return Result(Unsupported, recorded.ProvenanceFingerprint, null);
        }

        void Compare(string code, string expected, string actual, string name, string reason)
        {
            if (string.Equals(expected, actual, StringComparison.Ordinal))
                Add(code, CheckStatus.Pass, expected, actual, $"{name} matches.");
            else
            {
                Add(code, CheckStatus.Fail, expected, actual, $"{name} differs.");
                reasons.Add(reason);
            }
        }

        Compare(FrameworkDescriptionMatch, recorded.FrameworkDescription, current.FrameworkDescription, "Framework description", FrameworkDescriptionMismatch);
        Compare(RuntimeVersionMatch, recorded.RuntimeVersion, current.RuntimeVersion, "Runtime version", RuntimeVersionMismatch);
        Compare(RuntimeIdentifierMatch, recorded.RuntimeIdentifier, current.RuntimeIdentifier, "Runtime identifier", RuntimeIdentifierMismatch);
        Compare(OSPlatformMatch, recorded.OSPlatform, current.OSPlatform, "Operating system platform", OSPlatformMismatch);
        Compare(OSArchitectureMatch, recorded.OSArchitecture, current.OSArchitecture, "Operating system architecture", OSArchitectureMismatch);
        Compare(ProcessArchitectureMatch, recorded.ProcessArchitecture, current.ProcessArchitecture, "Process architecture", ProcessArchitectureMismatch);
        Compare(DependencyRuntimeTargetMatch, recorded.DependencyRuntimeTarget, current.DependencyRuntimeTarget, "Dependency runtime target", DependencyRuntimeTargetMismatch);
        Compare(DependencyManifestFingerprintMatch, recorded.DependencyManifestFingerprint, current.DependencyManifestFingerprint, "Dependency manifest identity", DependencyManifestFingerprintMismatch);

        return Result(reasons.Count == 0 ? Match : Mismatch, recorded.ProvenanceFingerprint, current.ProvenanceFingerprint);
    }
}
