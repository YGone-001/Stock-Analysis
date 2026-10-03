using AIHelper.Core.Sparrow;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceCheckCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceReasonCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceVerificationReasonCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceVerificationStatus;
using CheckStatus = AIHelper.Core.Sparrow.ResearchReproducibilityCheckStatus;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Compares a record's recorded source/build provenance with the currently observed environment.
/// It is read-only: it never re-executes research, never mutates persistence, and never contacts a remote.
/// </summary>
public sealed class ResearchSourceBuildProvenanceVerifier : IResearchSourceBuildProvenanceVerifier
{
    private readonly IResearchSourceBuildProvenanceProvider _currentProvenance;

    public ResearchSourceBuildProvenanceVerifier(IResearchSourceBuildProvenanceProvider? currentProvenance = null) =>
        _currentProvenance = currentProvenance ?? new ResearchSourceBuildProvenanceProvider();

    public ResearchSourceBuildProvenanceVerificationResult Verify(PersistedResearchExperimentRecord experimentRecord)
    {
        ArgumentNullException.ThrowIfNull(experimentRecord);

        List<ResearchReproducibilityCheck> checks = new();
        List<string> reasons = new();

        void Add(string code, CheckStatus status, string? expected, string? actual, string message) =>
            checks.Add(new ResearchReproducibilityCheck(code, status, expected, actual, message));

        ResearchSourceBuildProvenanceVerificationResult Result(ResearchSourceBuildProvenanceVerificationStatus status, string? recorded, string? current) =>
            new(status, checks, experimentRecord.ExperimentId, recorded, current, reasons);

        ResearchSourceBuildProvenance? recorded = experimentRecord.SourceBuildProvenance;
        bool isCurrentSchema = string.Equals(experimentRecord.SchemaVersion, PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal);

        // 1. SOURCE_BUILD_PROVENANCE_PRESENT
        if (recorded is null)
        {
            if (isCurrentSchema)
            {
                Add(SourceBuildProvenancePresent, CheckStatus.Fail, ResearchSourceBuildProvenance.CurrentProvenanceVersion, "<null>", "Record v3 requires source/build provenance.");
                reasons.Add(ProvenanceMissingInV3Record);
                return Result(Failed, null, null);
            }
            Add(SourceBuildProvenancePresent, CheckStatus.Unsupported, ResearchSourceBuildProvenance.CurrentProvenanceVersion, "<null>",
                $"Record schema '{experimentRecord.SchemaVersion}' predates source/build provenance.");
            reasons.Add(ProvenanceMissingInLegacySchema);
            return Result(Unsupported, null, null);
        }
        Add(SourceBuildProvenancePresent, CheckStatus.Pass, ResearchSourceBuildProvenance.CurrentProvenanceVersion, recorded.ProvenanceVersion, "Source/build provenance is present.");

        // 2. SOURCE_BUILD_PROVENANCE_FINGERPRINT_VALID
        string recomputed = recorded.ComputeFingerprint();
        if (!string.Equals(recorded.ProvenanceFingerprint, recomputed, StringComparison.Ordinal))
        {
            Add(SourceBuildProvenanceFingerprintValid, CheckStatus.Fail, recomputed, recorded.ProvenanceFingerprint, "Source/build provenance fingerprint mismatch (tampering detected).");
            reasons.Add(ProvenanceFingerprintInvalid);
            return Result(Failed, recorded.ProvenanceFingerprint, null);
        }
        Add(SourceBuildProvenanceFingerprintValid, CheckStatus.Pass, recomputed, recorded.ProvenanceFingerprint, "Source/build provenance fingerprint recomputation verified.");

        // 3. SOURCE_BUILD_BINDING_VALID
        ResearchExecutionProvenanceBinding? binding = experimentRecord.ExecutionProvenanceBinding;
        bool bindingValid = binding is not null
            && string.Equals(binding.BindingVersion, ResearchExecutionProvenanceBinding.CurrentBindingVersion, StringComparison.Ordinal)
            && string.Equals(binding.SourceBuildProvenanceFingerprint, recorded.ProvenanceFingerprint, StringComparison.Ordinal)
            && string.Equals(binding.BindingFingerprint, binding.ComputeFingerprint(), StringComparison.Ordinal);
        if (!bindingValid)
        {
            Add(SourceBuildBindingValid, CheckStatus.Fail, ResearchExecutionProvenanceBinding.CurrentBindingVersion, binding?.BindingVersion ?? "<null>",
                "Execution provenance binding does not bind this source/build provenance.");
            reasons.Add(BindingInvalid);
            return Result(Failed, recorded.ProvenanceFingerprint, null);
        }
        Add(SourceBuildBindingValid, CheckStatus.Pass, binding!.BindingFingerprint, binding.ComputeFingerprint(), "Execution provenance binding v2 binds this source/build provenance.");

        ResearchSourceBuildProvenance current;
        try
        {
            current = _currentProvenance.Capture();
        }
        catch (ResearchSourceBuildProvenanceException exception) when (exception.ReasonCode == SourceProvenanceUnavailable)
        {
            AddUnsupported(SourceCommitMatch, recorded.SourceCommitSha, "Source commit cannot be observed.");
            AddUnsupported(SourceTreeMatch, recorded.SourceTreeSha, "Source tree cannot be observed.");
            AddUnsupported(SourceStateMatch, recorded.SourceState, "Source state cannot be observed.");
            AddUnsupported(BuildConfigurationMatch, recorded.BuildConfiguration, "Current build identity is unavailable.");
            AddUnsupported(TargetFrameworkMatch, recorded.TargetFramework, "Current build identity is unavailable.");
            AddUnsupported(ToolModuleIdentityMatch, recorded.HistoricalDataToolModuleVersionId, "Current build identity is unavailable.");
            AddUnsupported(ServicesModuleIdentityMatch, recorded.ServicesModuleVersionId, "Current build identity is unavailable.");
            AddUnsupported(CoreModuleIdentityMatch, recorded.CoreModuleVersionId, "Current build identity is unavailable.");
            reasons.Add(SourceProvenanceUnavailable);
            return Result(Unsupported, recorded.ProvenanceFingerprint, null);
        }
        catch (ResearchSourceBuildProvenanceException exception) when (exception.ReasonCode == SourceWorktreeNotClean)
        {
            // Authoritative capture is refused for a dirty worktree, so no current source identity exists to compare.
            AddUnsupported(SourceCommitMatch, recorded.SourceCommitSha, "Source commit cannot be observed while the worktree is not clean.");
            AddUnsupported(SourceTreeMatch, recorded.SourceTreeSha, "Source tree cannot be observed while the worktree is not clean.");
            Add(SourceStateMatch, CheckStatus.Fail, ResearchSourceBuildProvenance.CleanSourceState, "Dirty", $"{SourceWorktreeNotClean}: current worktree is not clean.");
            AddUnsupported(BuildConfigurationMatch, recorded.BuildConfiguration, "Current build identity is unavailable while authoritative source capture is refused.");
            AddUnsupported(TargetFrameworkMatch, recorded.TargetFramework, "Current build identity is unavailable while authoritative source capture is refused.");
            AddUnsupported(ToolModuleIdentityMatch, recorded.HistoricalDataToolModuleVersionId, "Current build identity is unavailable while authoritative source capture is refused.");
            AddUnsupported(ServicesModuleIdentityMatch, recorded.ServicesModuleVersionId, "Current build identity is unavailable while authoritative source capture is refused.");
            AddUnsupported(CoreModuleIdentityMatch, recorded.CoreModuleVersionId, "Current build identity is unavailable while authoritative source capture is refused.");
            reasons.Add(SourceWorktreeNotClean);
            return Result(Mismatch, recorded.ProvenanceFingerprint, null);
        }

        void AddUnsupported(string code, string? expected, string message) =>
            Add(code, CheckStatus.Unsupported, expected, "<unavailable>", message);

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

        Compare(SourceCommitMatch, recorded.SourceCommitSha, current.SourceCommitSha, "Source commit identity", SourceCommitMismatch);
        Compare(SourceTreeMatch, recorded.SourceTreeSha, current.SourceTreeSha, "Source root tree identity", SourceTreeMismatch);
        Compare(SourceStateMatch, recorded.SourceState, current.SourceState, "Source state", SourceStateMismatch);
        Compare(BuildConfigurationMatch, recorded.BuildConfiguration, current.BuildConfiguration, "Build configuration", BuildConfigurationMismatch);
        Compare(TargetFrameworkMatch, recorded.TargetFramework, current.TargetFramework, "Target framework", TargetFrameworkMismatch);
        Compare(ToolModuleIdentityMatch, recorded.HistoricalDataToolModuleVersionId, current.HistoricalDataToolModuleVersionId, "HistoricalDataTool module identity", ToolModuleIdentityMismatch);
        Compare(ServicesModuleIdentityMatch, recorded.ServicesModuleVersionId, current.ServicesModuleVersionId, "Services module identity", ServicesModuleIdentityMismatch);
        Compare(CoreModuleIdentityMatch, recorded.CoreModuleVersionId, current.CoreModuleVersionId, "Core module identity", CoreModuleIdentityMismatch);

        return Result(reasons.Count == 0 ? Match : Mismatch, recorded.ProvenanceFingerprint, current.ProvenanceFingerprint);
    }
}
