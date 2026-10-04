using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHelper.Core.Sparrow;

/// <summary>Optional factual performance values copied from a completed artifact; this type never recalculates performance.</summary>
public sealed class ResearchExperimentPerformanceSummary
{
    public ResearchExperimentPerformanceSummary(double? totalReturnPercent = null, double? maximumDrawdownPercent = null, int? tradeCount = null, double? winRate = null)
    {
        if (totalReturnPercent.HasValue && !double.IsFinite(totalReturnPercent.Value)) throw new ArgumentOutOfRangeException(nameof(totalReturnPercent));
        if (maximumDrawdownPercent.HasValue && (!double.IsFinite(maximumDrawdownPercent.Value) || maximumDrawdownPercent.Value > 0)) throw new ArgumentOutOfRangeException(nameof(maximumDrawdownPercent));
        if (tradeCount is < 0) throw new ArgumentOutOfRangeException(nameof(tradeCount));
        if (winRate.HasValue && (!double.IsFinite(winRate.Value) || winRate.Value < 0 || winRate.Value > 1)) throw new ArgumentOutOfRangeException(nameof(winRate));
        if (tradeCount == 0 && winRate.HasValue) throw new ArgumentException("Win rate is unavailable when trade count is zero.", nameof(winRate));
        if (tradeCount > 0 && !winRate.HasValue) throw new ArgumentException("Win rate is required when trade count is available and positive.", nameof(winRate));

        TotalReturnPercent = totalReturnPercent;
        MaximumDrawdownPercent = maximumDrawdownPercent;
        TradeCount = tradeCount;
        WinRate = winRate;
    }

    public double? TotalReturnPercent { get; }
    public double? MaximumDrawdownPercent { get; }
    public int? TradeCount { get; }
    public double? WinRate { get; }
}

/// <summary>Persistence-safe immutable execution facts. It intentionally contains no executable runtime dependency.</summary>
public sealed class ResearchExperimentExecutionSummary
{
    public ResearchExperimentExecutionSummary(ResearchExperimentExecutionStatus status, DateTimeOffset? startedAt, DateTimeOffset? completedAt,
        string? artifactFingerprint, IReadOnlyList<string>? warnings = null, ResearchExperimentPerformanceSummary? performance = null)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (status != ResearchExperimentExecutionStatus.Completed)
            throw new ArgumentException("Only completed executions may be persisted as experiment records.", nameof(status));
        if (startedAt is null || completedAt is null || completedAt < startedAt) throw new ArgumentException("Completed execution summary requires an ordered start and completion timestamp.");
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint);

        Status = status;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ArtifactFingerprint = artifactFingerprint;
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Performance = performance;
    }

    public ResearchExperimentExecutionStatus Status { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public string ArtifactFingerprint { get; }
    public IReadOnlyList<string> Warnings { get; }
    public ResearchExperimentPerformanceSummary? Performance { get; }

    public static ResearchExperimentExecutionSummary FromCompletedExecution(ResearchExperimentExecution execution, ResearchExperimentPerformanceSummary? performance = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return new ResearchExperimentExecutionSummary(execution.Status, execution.StartedAt, execution.CompletedAt, execution.ArtifactFingerprint, execution.Warnings, performance);
    }
}

/// <summary>Authoritative execution provenance binding between experiment definition and artifact execution identities.</summary>
public sealed class ResearchExecutionProvenanceBinding
{
    /// <summary>Binding contract used by V2 experiment records. It has no source/build provenance.</summary>
    public const string LegacyBindingVersion = "research-execution-provenance-binding-v1";
    /// <summary>Binding contract used by V3 experiment records. It additionally binds source/build provenance.</summary>
    public const string SourceBuildBindingVersion = "research-execution-provenance-binding-v2";
    /// <summary>Binding contract used by V4 experiment records. It additionally binds execution-environment provenance.</summary>
    public const string CurrentBindingVersion = "research-execution-provenance-binding-v3";

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [JsonConstructor]
    public ResearchExecutionProvenanceBinding(
        string bindingVersion,
        string experimentFingerprint,
        string parameterSnapshotFingerprint,
        string experimentStrategyParameterFingerprint,
        string artifactStrategyParameterFingerprint,
        string experimentPortfolioConfigurationFingerprint,
        string artifactPortfolioConfigurationFingerprint,
        string experimentAnalysisConfigurationFingerprint,
        string artifactAnalysisFingerprint,
        string datasetFingerprint,
        string artifactVersion,
        string artifactFingerprint,
        string bindingFingerprint,
        string? sourceBuildProvenanceFingerprint = null,
        string? executionEnvironmentProvenanceFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterSnapshotFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentStrategyParameterFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactStrategyParameterFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentPortfolioConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPortfolioConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentAnalysisConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactAnalysisFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(bindingFingerprint);

        if (string.Equals(bindingVersion, CurrentBindingVersion, StringComparison.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceBuildProvenanceFingerprint);
            ArgumentException.ThrowIfNullOrWhiteSpace(executionEnvironmentProvenanceFingerprint);
        }
        else if (string.Equals(bindingVersion, SourceBuildBindingVersion, StringComparison.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceBuildProvenanceFingerprint);
            if (executionEnvironmentProvenanceFingerprint is not null)
                throw new ArgumentException("Binding v2 must not carry an execution environment provenance fingerprint.", nameof(executionEnvironmentProvenanceFingerprint));
        }
        else if (string.Equals(bindingVersion, LegacyBindingVersion, StringComparison.Ordinal))
        {
            if (sourceBuildProvenanceFingerprint is not null)
                throw new ArgumentException("Binding v1 must not carry a source/build provenance fingerprint.", nameof(sourceBuildProvenanceFingerprint));
            if (executionEnvironmentProvenanceFingerprint is not null)
                throw new ArgumentException("Binding v1 must not carry an execution environment provenance fingerprint.", nameof(executionEnvironmentProvenanceFingerprint));
        }
        else
        {
            throw new ArgumentException($"Unsupported execution provenance binding version '{bindingVersion}'.", nameof(bindingVersion));
        }

        BindingVersion = bindingVersion;
        ExperimentFingerprint = experimentFingerprint;
        ParameterSnapshotFingerprint = parameterSnapshotFingerprint;
        ExperimentStrategyParameterFingerprint = experimentStrategyParameterFingerprint;
        ArtifactStrategyParameterFingerprint = artifactStrategyParameterFingerprint;
        ExperimentPortfolioConfigurationFingerprint = experimentPortfolioConfigurationFingerprint;
        ArtifactPortfolioConfigurationFingerprint = artifactPortfolioConfigurationFingerprint;
        ExperimentAnalysisConfigurationFingerprint = experimentAnalysisConfigurationFingerprint;
        ArtifactAnalysisFingerprint = artifactAnalysisFingerprint;
        DatasetFingerprint = datasetFingerprint;
        ArtifactVersion = artifactVersion;
        ArtifactFingerprint = artifactFingerprint;
        BindingFingerprint = bindingFingerprint;
        SourceBuildProvenanceFingerprint = sourceBuildProvenanceFingerprint;
        ExecutionEnvironmentProvenanceFingerprint = executionEnvironmentProvenanceFingerprint;
    }

    public string BindingVersion { get; }
    public string ExperimentFingerprint { get; }
    public string ParameterSnapshotFingerprint { get; }
    public string ExperimentStrategyParameterFingerprint { get; }
    public string ArtifactStrategyParameterFingerprint { get; }
    public string ExperimentPortfolioConfigurationFingerprint { get; }
    public string ArtifactPortfolioConfigurationFingerprint { get; }
    public string ExperimentAnalysisConfigurationFingerprint { get; }
    public string ArtifactAnalysisFingerprint { get; }
    public string DatasetFingerprint { get; }
    public string ArtifactVersion { get; }
    public string ArtifactFingerprint { get; }
    public string BindingFingerprint { get; }
    /// <summary>Only present for binding v2 and v3; it binds the record's source/build provenance into this binding.</summary>
    public string? SourceBuildProvenanceFingerprint { get; }
    /// <summary>Only present for binding v3; it binds the record's execution-environment provenance into this binding.</summary>
    public string? ExecutionEnvironmentProvenanceFingerprint { get; }

    /// <summary>Dispatches to the preimage owned by this binding version. Frozen v1/v2 preimages are never modified.</summary>
    public string ComputeFingerprint()
    {
        if (string.Equals(BindingVersion, LegacyBindingVersion, StringComparison.Ordinal))
            return ComputeFingerprint(
                BindingVersion, ExperimentFingerprint, ParameterSnapshotFingerprint, ExperimentStrategyParameterFingerprint,
                ArtifactStrategyParameterFingerprint, ExperimentPortfolioConfigurationFingerprint, ArtifactPortfolioConfigurationFingerprint,
                ExperimentAnalysisConfigurationFingerprint, ArtifactAnalysisFingerprint, DatasetFingerprint, ArtifactVersion, ArtifactFingerprint);

        if (string.Equals(BindingVersion, SourceBuildBindingVersion, StringComparison.Ordinal))
            return ComputeFingerprint(
                BindingVersion, ExperimentFingerprint, ParameterSnapshotFingerprint, ExperimentStrategyParameterFingerprint,
                ArtifactStrategyParameterFingerprint, ExperimentPortfolioConfigurationFingerprint, ArtifactPortfolioConfigurationFingerprint,
                ExperimentAnalysisConfigurationFingerprint, ArtifactAnalysisFingerprint, DatasetFingerprint, ArtifactVersion, ArtifactFingerprint,
                SourceBuildProvenanceFingerprint ?? throw new InvalidOperationException("Binding v2 requires a source/build provenance fingerprint."));

        return ComputeFingerprint(
            BindingVersion, ExperimentFingerprint, ParameterSnapshotFingerprint, ExperimentStrategyParameterFingerprint,
            ArtifactStrategyParameterFingerprint, ExperimentPortfolioConfigurationFingerprint, ArtifactPortfolioConfigurationFingerprint,
            ExperimentAnalysisConfigurationFingerprint, ArtifactAnalysisFingerprint, DatasetFingerprint, ArtifactVersion, ArtifactFingerprint,
            SourceBuildProvenanceFingerprint ?? throw new InvalidOperationException("Binding v3 requires a source/build provenance fingerprint."),
            ExecutionEnvironmentProvenanceFingerprint ?? throw new InvalidOperationException("Binding v3 requires an execution environment provenance fingerprint."));
    }

    /// <summary>Frozen binding v1 preimage. It must never change.</summary>
    public static string ComputeFingerprint(
        string bindingVersion,
        string experimentFingerprint,
        string parameterSnapshotFingerprint,
        string experimentStrategyParameterFingerprint,
        string artifactStrategyParameterFingerprint,
        string experimentPortfolioConfigurationFingerprint,
        string artifactPortfolioConfigurationFingerprint,
        string experimentAnalysisConfigurationFingerprint,
        string artifactAnalysisFingerprint,
        string datasetFingerprint,
        string artifactVersion,
        string artifactFingerprint)
    {
        BindingFingerprintPayload payload = new(
            bindingVersion,
            experimentFingerprint,
            parameterSnapshotFingerprint,
            experimentStrategyParameterFingerprint,
            artifactStrategyParameterFingerprint,
            experimentPortfolioConfigurationFingerprint,
            artifactPortfolioConfigurationFingerprint,
            experimentAnalysisConfigurationFingerprint,
            artifactAnalysisFingerprint,
            datasetFingerprint,
            artifactVersion,
            artifactFingerprint);

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    /// <summary>Binding v2 preimage: the frozen v1 semantic fields plus the source/build provenance fingerprint.</summary>
    public static string ComputeFingerprint(
        string bindingVersion,
        string experimentFingerprint,
        string parameterSnapshotFingerprint,
        string experimentStrategyParameterFingerprint,
        string artifactStrategyParameterFingerprint,
        string experimentPortfolioConfigurationFingerprint,
        string artifactPortfolioConfigurationFingerprint,
        string experimentAnalysisConfigurationFingerprint,
        string artifactAnalysisFingerprint,
        string datasetFingerprint,
        string artifactVersion,
        string artifactFingerprint,
        string sourceBuildProvenanceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBuildProvenanceFingerprint);
        BindingV2FingerprintPayload payload = new(
            bindingVersion,
            experimentFingerprint,
            parameterSnapshotFingerprint,
            experimentStrategyParameterFingerprint,
            artifactStrategyParameterFingerprint,
            experimentPortfolioConfigurationFingerprint,
            artifactPortfolioConfigurationFingerprint,
            experimentAnalysisConfigurationFingerprint,
            artifactAnalysisFingerprint,
            datasetFingerprint,
            artifactVersion,
            artifactFingerprint,
            sourceBuildProvenanceFingerprint);

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    /// <summary>Binding v3 preimage: the frozen binding-v2 semantic fields plus the execution-environment provenance fingerprint.</summary>
    public static string ComputeFingerprint(
        string bindingVersion,
        string experimentFingerprint,
        string parameterSnapshotFingerprint,
        string experimentStrategyParameterFingerprint,
        string artifactStrategyParameterFingerprint,
        string experimentPortfolioConfigurationFingerprint,
        string artifactPortfolioConfigurationFingerprint,
        string experimentAnalysisConfigurationFingerprint,
        string artifactAnalysisFingerprint,
        string datasetFingerprint,
        string artifactVersion,
        string artifactFingerprint,
        string sourceBuildProvenanceFingerprint,
        string executionEnvironmentProvenanceFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBuildProvenanceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionEnvironmentProvenanceFingerprint);
        BindingV3FingerprintPayload payload = new(
            bindingVersion,
            experimentFingerprint,
            parameterSnapshotFingerprint,
            experimentStrategyParameterFingerprint,
            artifactStrategyParameterFingerprint,
            experimentPortfolioConfigurationFingerprint,
            artifactPortfolioConfigurationFingerprint,
            experimentAnalysisConfigurationFingerprint,
            artifactAnalysisFingerprint,
            datasetFingerprint,
            artifactVersion,
            artifactFingerprint,
            sourceBuildProvenanceFingerprint,
            executionEnvironmentProvenanceFingerprint);

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson)));
    }

    /// <summary>Legacy creation path producing a binding v1 (V2 experiment records). Preserved for existing contracts.</summary>
    public static ResearchExecutionProvenanceBinding Create(ResearchExperimentDefinition definition, SparrowPortfolioResearchArtifact artifact)
    {
        (definition, artifact) = ValidateCreateInputs(definition, artifact);

        string bindingFingerprint = ComputeFingerprint(
            LegacyBindingVersion,
            definition.SemanticFingerprint,
            definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint,
            artifact.PortfolioRequest.StrategyParameterFingerprint,
            definition.PortfolioConfigurationFingerprint,
            artifact.PortfolioConfigurationFingerprint,
            definition.AnalysisConfigurationFingerprint,
            artifact.AnalysisFingerprint,
            definition.DatasetFingerprint,
            artifact.ArtifactVersion,
            artifact.ArtifactFingerprint);

        return new ResearchExecutionProvenanceBinding(
            LegacyBindingVersion,
            definition.SemanticFingerprint,
            definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint,
            artifact.PortfolioRequest.StrategyParameterFingerprint,
            definition.PortfolioConfigurationFingerprint,
            artifact.PortfolioConfigurationFingerprint,
            definition.AnalysisConfigurationFingerprint,
            artifact.AnalysisFingerprint,
            definition.DatasetFingerprint,
            artifact.ArtifactVersion,
            artifact.ArtifactFingerprint,
            bindingFingerprint);
    }

    /// <summary>Authoritative creation path producing a binding v2 that binds source/build provenance (V3 experiment records).</summary>
    public static ResearchExecutionProvenanceBinding Create(
        ResearchExperimentDefinition definition,
        SparrowPortfolioResearchArtifact artifact,
        ResearchSourceBuildProvenance sourceBuildProvenance)
    {
        ArgumentNullException.ThrowIfNull(sourceBuildProvenance);
        (definition, artifact) = ValidateCreateInputs(definition, artifact);

        string bindingFingerprint = ComputeFingerprint(
            SourceBuildBindingVersion,
            definition.SemanticFingerprint,
            definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint,
            artifact.PortfolioRequest.StrategyParameterFingerprint,
            definition.PortfolioConfigurationFingerprint,
            artifact.PortfolioConfigurationFingerprint,
            definition.AnalysisConfigurationFingerprint,
            artifact.AnalysisFingerprint,
            definition.DatasetFingerprint,
            artifact.ArtifactVersion,
            artifact.ArtifactFingerprint,
            sourceBuildProvenance.ProvenanceFingerprint);

        return new ResearchExecutionProvenanceBinding(
            SourceBuildBindingVersion,
            definition.SemanticFingerprint,
            definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint,
            artifact.PortfolioRequest.StrategyParameterFingerprint,
            definition.PortfolioConfigurationFingerprint,
            artifact.PortfolioConfigurationFingerprint,
            definition.AnalysisConfigurationFingerprint,
            artifact.AnalysisFingerprint,
            definition.DatasetFingerprint,
            artifact.ArtifactVersion,
            artifact.ArtifactFingerprint,
            bindingFingerprint,
            sourceBuildProvenance.ProvenanceFingerprint);
    }

    /// <summary>
    /// Authoritative creation path producing a binding v3 that binds both source/build and execution-environment
    /// provenance (V4 experiment records).
    /// </summary>
    public static ResearchExecutionProvenanceBinding Create(
        ResearchExperimentDefinition definition,
        SparrowPortfolioResearchArtifact artifact,
        ResearchSourceBuildProvenance sourceBuildProvenance,
        ResearchExecutionEnvironmentProvenance executionEnvironmentProvenance)
    {
        ArgumentNullException.ThrowIfNull(sourceBuildProvenance);
        ArgumentNullException.ThrowIfNull(executionEnvironmentProvenance);
        (definition, artifact) = ValidateCreateInputs(definition, artifact);

        string bindingFingerprint = ComputeFingerprint(
            CurrentBindingVersion,
            definition.SemanticFingerprint,
            definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint,
            artifact.PortfolioRequest.StrategyParameterFingerprint,
            definition.PortfolioConfigurationFingerprint,
            artifact.PortfolioConfigurationFingerprint,
            definition.AnalysisConfigurationFingerprint,
            artifact.AnalysisFingerprint,
            definition.DatasetFingerprint,
            artifact.ArtifactVersion,
            artifact.ArtifactFingerprint,
            sourceBuildProvenance.ProvenanceFingerprint,
            executionEnvironmentProvenance.ProvenanceFingerprint);

        return new ResearchExecutionProvenanceBinding(
            CurrentBindingVersion,
            definition.SemanticFingerprint,
            definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint,
            artifact.PortfolioRequest.StrategyParameterFingerprint,
            definition.PortfolioConfigurationFingerprint,
            artifact.PortfolioConfigurationFingerprint,
            definition.AnalysisConfigurationFingerprint,
            artifact.AnalysisFingerprint,
            definition.DatasetFingerprint,
            artifact.ArtifactVersion,
            artifact.ArtifactFingerprint,
            bindingFingerprint,
            sourceBuildProvenance.ProvenanceFingerprint,
            executionEnvironmentProvenance.ProvenanceFingerprint);
    }

    private static (ResearchExperimentDefinition Definition, SparrowPortfolioResearchArtifact Artifact) ValidateCreateInputs(
        ResearchExperimentDefinition definition, SparrowPortfolioResearchArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(artifact.PortfolioRequest);

        if (!string.Equals(definition.DatasetFingerprint, artifact.DatasetFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Dataset fingerprint mismatch between experiment definition and artifact.");
        if (!string.Equals(definition.PortfolioConfigurationFingerprint, artifact.PortfolioConfigurationFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Portfolio configuration fingerprint mismatch between experiment definition and artifact.");
        if (artifact.Strategy is not null && (!string.Equals(definition.StrategyIdentity.Mode, artifact.Strategy.Mode, StringComparison.Ordinal)
            || !string.Equals(definition.StrategyIdentity.Version, artifact.Strategy.Version, StringComparison.Ordinal)))
            throw new ArgumentException("Strategy identity mode/version mismatch between experiment definition and artifact.");

        return (definition, artifact);
    }

    private sealed record BindingFingerprintPayload(
        string BindingVersion,
        string ExperimentFingerprint,
        string ParameterSnapshotFingerprint,
        string ExperimentStrategyParameterFingerprint,
        string ArtifactStrategyParameterFingerprint,
        string ExperimentPortfolioConfigurationFingerprint,
        string ArtifactPortfolioConfigurationFingerprint,
        string ExperimentAnalysisConfigurationFingerprint,
        string ArtifactAnalysisFingerprint,
        string DatasetFingerprint,
        string ArtifactVersion,
        string ArtifactFingerprint);

    private sealed record BindingV2FingerprintPayload(
        string BindingVersion,
        string ExperimentFingerprint,
        string ParameterSnapshotFingerprint,
        string ExperimentStrategyParameterFingerprint,
        string ArtifactStrategyParameterFingerprint,
        string ExperimentPortfolioConfigurationFingerprint,
        string ArtifactPortfolioConfigurationFingerprint,
        string ExperimentAnalysisConfigurationFingerprint,
        string ArtifactAnalysisFingerprint,
        string DatasetFingerprint,
        string ArtifactVersion,
        string ArtifactFingerprint,
        string SourceBuildProvenanceFingerprint);

    private sealed record BindingV3FingerprintPayload(
        string BindingVersion,
        string ExperimentFingerprint,
        string ParameterSnapshotFingerprint,
        string ExperimentStrategyParameterFingerprint,
        string ArtifactStrategyParameterFingerprint,
        string ExperimentPortfolioConfigurationFingerprint,
        string ArtifactPortfolioConfigurationFingerprint,
        string ExperimentAnalysisConfigurationFingerprint,
        string ArtifactAnalysisFingerprint,
        string DatasetFingerprint,
        string ArtifactVersion,
        string ArtifactFingerprint,
        string SourceBuildProvenanceFingerprint,
        string ExecutionEnvironmentProvenanceFingerprint);
}

/// <summary>Versioned, immutable local record for one completed research experiment.</summary>
public sealed class PersistedResearchExperimentRecord
{
    /// <summary>Legacy schema: no execution provenance binding and no provenance domains.</summary>
    public const string LegacySchemaVersion = "research-experiment-record-v1";
    /// <summary>Schema with an execution provenance binding v1 and no provenance domains.</summary>
    public const string ExecutionBindingSchemaVersion = "research-experiment-record-v2";
    /// <summary>Schema with execution provenance binding v2 and authoritative source/build provenance.</summary>
    public const string SourceBuildProvenanceSchemaVersion = "research-experiment-record-v3";
    /// <summary>Current schema: binding v3 plus source/build and execution-environment provenance.</summary>
    public const string CurrentSchemaVersion = "research-experiment-record-v4";

    [JsonConstructor]
    public PersistedResearchExperimentRecord(string experimentId, string experimentFingerprint, ResearchExperimentDefinition definition,
        ResearchExperimentExecutionSummary executionSummary, ResearchResultArtifactReference artifactReference, ResearchArtifactLineage lineage,
        DateTimeOffset createdAt, string? schemaVersion = null, ResearchExecutionProvenanceBinding? executionProvenanceBinding = null,
        ResearchSourceBuildProvenance? sourceBuildProvenance = null, ResearchExecutionEnvironmentProvenance? executionEnvironmentProvenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(executionSummary);
        ArgumentNullException.ThrowIfNull(artifactReference);
        ArgumentNullException.ThrowIfNull(lineage);

        schemaVersion ??= executionEnvironmentProvenance is not null ? CurrentSchemaVersion
            : sourceBuildProvenance is not null ? SourceBuildProvenanceSchemaVersion
            : executionProvenanceBinding is not null ? ExecutionBindingSchemaVersion
            : LegacySchemaVersion;
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);

        if (!string.Equals(experimentId, definition.Identity.ExperimentId, StringComparison.Ordinal)
            || !string.Equals(experimentFingerprint, definition.SemanticFingerprint, StringComparison.Ordinal)
            || !string.Equals(experimentFingerprint, lineage.ExperimentFingerprint, StringComparison.Ordinal)
            || !string.Equals(definition.DatasetFingerprint, lineage.DatasetFingerprint, StringComparison.Ordinal)
            || !string.Equals(definition.Parameters.Fingerprint, lineage.ParameterFingerprint, StringComparison.Ordinal)
            || !string.Equals(executionSummary.ArtifactFingerprint, lineage.ArtifactFingerprint, StringComparison.Ordinal)
            || !string.Equals(executionSummary.ArtifactFingerprint, artifactReference.ArtifactFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Persisted experiment record provenance is inconsistent.");

        if (string.Equals(schemaVersion, LegacySchemaVersion, StringComparison.Ordinal))
        {
            // V1 semantics are preserved exactly: a legacy record is not required to carry an execution
            // provenance binding, and any binding supplied with the legacy schema is not validated.
            if (sourceBuildProvenance is not null)
                throw new ArgumentException("Legacy record v1 must not carry source/build provenance.", nameof(sourceBuildProvenance));
            if (executionEnvironmentProvenance is not null)
                throw new ArgumentException("Legacy record v1 must not carry execution environment provenance.", nameof(executionEnvironmentProvenance));
        }
        else if (string.Equals(schemaVersion, ExecutionBindingSchemaVersion, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(executionProvenanceBinding);
            if (sourceBuildProvenance is not null)
                throw new ArgumentException("Record v2 must not carry source/build provenance.", nameof(sourceBuildProvenance));
            if (executionEnvironmentProvenance is not null)
                throw new ArgumentException("Record v2 must not carry execution environment provenance.", nameof(executionEnvironmentProvenance));
            RequireBinding(executionProvenanceBinding, ResearchExecutionProvenanceBinding.LegacyBindingVersion, definition, executionSummary, artifactReference, lineage, experimentFingerprint);
        }
        else if (string.Equals(schemaVersion, SourceBuildProvenanceSchemaVersion, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(executionProvenanceBinding);
            ArgumentNullException.ThrowIfNull(sourceBuildProvenance);
            if (executionEnvironmentProvenance is not null)
                throw new ArgumentException("Record v3 must not carry execution environment provenance.", nameof(executionEnvironmentProvenance));
            RequireBinding(executionProvenanceBinding, ResearchExecutionProvenanceBinding.SourceBuildBindingVersion, definition, executionSummary, artifactReference, lineage, experimentFingerprint);

            if (!string.Equals(executionProvenanceBinding.SourceBuildProvenanceFingerprint, sourceBuildProvenance.ProvenanceFingerprint, StringComparison.Ordinal))
                throw new ArgumentException("Binding source/build provenance fingerprint does not match the record's source/build provenance.");
            if (!string.Equals(sourceBuildProvenance.ProvenanceFingerprint, sourceBuildProvenance.ComputeFingerprint(), StringComparison.Ordinal))
                throw new ArgumentException("Source/build provenance fingerprint mismatch.");
        }
        else if (string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(executionProvenanceBinding);
            ArgumentNullException.ThrowIfNull(sourceBuildProvenance);
            ArgumentNullException.ThrowIfNull(executionEnvironmentProvenance);
            RequireBinding(executionProvenanceBinding, ResearchExecutionProvenanceBinding.CurrentBindingVersion, definition, executionSummary, artifactReference, lineage, experimentFingerprint);

            if (!string.Equals(executionProvenanceBinding.SourceBuildProvenanceFingerprint, sourceBuildProvenance.ProvenanceFingerprint, StringComparison.Ordinal))
                throw new ArgumentException("Binding source/build provenance fingerprint does not match the record's source/build provenance.");
            if (!string.Equals(executionProvenanceBinding.ExecutionEnvironmentProvenanceFingerprint, executionEnvironmentProvenance.ProvenanceFingerprint, StringComparison.Ordinal))
                throw new ArgumentException("Binding execution environment provenance fingerprint does not match the record's execution environment provenance.");
            if (!string.Equals(sourceBuildProvenance.ProvenanceFingerprint, sourceBuildProvenance.ComputeFingerprint(), StringComparison.Ordinal))
                throw new ArgumentException("Source/build provenance fingerprint mismatch.");
            if (!string.Equals(executionEnvironmentProvenance.DependencyManifestFingerprint, executionEnvironmentProvenance.ComputeDependencyManifestFingerprint(), StringComparison.Ordinal))
                throw new ArgumentException("Dependency manifest fingerprint mismatch.");
            if (!string.Equals(executionEnvironmentProvenance.ProvenanceFingerprint, executionEnvironmentProvenance.ComputeProvenanceFingerprint(), StringComparison.Ordinal))
                throw new ArgumentException("Execution environment provenance fingerprint mismatch.");
        }
        else
        {
            throw new NotSupportedException($"Research experiment schema '{schemaVersion}' is unsupported.");
        }

        ExperimentId = experimentId;
        ExperimentFingerprint = experimentFingerprint;
        Definition = definition;
        ExecutionSummary = executionSummary;
        ArtifactReference = artifactReference;
        Lineage = lineage;
        CreatedAt = createdAt;
        SchemaVersion = schemaVersion;
        ExecutionProvenanceBinding = executionProvenanceBinding;
        SourceBuildProvenance = sourceBuildProvenance;
        ExecutionEnvironmentProvenance = executionEnvironmentProvenance;
    }

    public string ExperimentId { get; }
    public string ExperimentFingerprint { get; }
    public ResearchExperimentDefinition Definition { get; }
    public ResearchExperimentExecutionSummary ExecutionSummary { get; }
    public ResearchResultArtifactReference ArtifactReference { get; }
    public ResearchArtifactLineage Lineage { get; }
    /// <summary>Persistence metadata only; it does not change the experiment semantic fingerprint.</summary>
    public DateTimeOffset CreatedAt { get; }
    public string SchemaVersion { get; }
    public ResearchExecutionProvenanceBinding? ExecutionProvenanceBinding { get; }
    /// <summary>Observed source/build association for V3 and V4 records; null for V1 and V2 records.</summary>
    public ResearchSourceBuildProvenance? SourceBuildProvenance { get; }
    /// <summary>Observed execution environment and dependency association for V4 records; null for V1, V2 and V3 records.</summary>
    public ResearchExecutionEnvironmentProvenance? ExecutionEnvironmentProvenance { get; }

    private static void RequireBinding(ResearchExecutionProvenanceBinding binding, string expectedVersion,
        ResearchExperimentDefinition definition, ResearchExperimentExecutionSummary executionSummary,
        ResearchResultArtifactReference artifactReference, ResearchArtifactLineage lineage, string experimentFingerprint)
    {
        if (!string.Equals(binding.BindingVersion, expectedVersion, StringComparison.Ordinal)
            || !string.Equals(binding.ExperimentFingerprint, experimentFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ParameterSnapshotFingerprint, definition.Parameters.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ExperimentStrategyParameterFingerprint, definition.StrategyParameterFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ExperimentPortfolioConfigurationFingerprint, definition.PortfolioConfigurationFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ExperimentAnalysisConfigurationFingerprint, definition.AnalysisConfigurationFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.DatasetFingerprint, definition.DatasetFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ArtifactVersion, artifactReference.ArtifactVersion, StringComparison.Ordinal)
            || !string.Equals(binding.ArtifactFingerprint, artifactReference.ArtifactFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ArtifactFingerprint, executionSummary.ArtifactFingerprint, StringComparison.Ordinal)
            || !string.Equals(binding.ArtifactFingerprint, lineage.ArtifactFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Persisted experiment record execution provenance binding is inconsistent.");

        string recomputedBindingFp = binding.ComputeFingerprint();
        if (!string.Equals(binding.BindingFingerprint, recomputedBindingFp, StringComparison.Ordinal))
            throw new ArgumentException("Execution provenance binding fingerprint mismatch.");
    }
}

public interface IResearchExperimentRepository
{
    Task SaveAsync(PersistedResearchExperimentRecord experiment, CancellationToken cancellationToken = default);
    Task<PersistedResearchExperimentRecord> GetAsync(string experimentId, CancellationToken cancellationToken = default);
    Task<ResearchExperimentHistory> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string experimentId, CancellationToken cancellationToken = default);
}

/// <summary>Canonical history order is ordinal ExperimentId order, never timestamp order.</summary>
public sealed class ResearchExperimentHistory
{
    public ResearchExperimentHistory(IEnumerable<PersistedResearchExperimentRecord> experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        PersistedResearchExperimentRecord[] canonical = experiments.OrderBy(item => item.ExperimentId, StringComparer.Ordinal).ToArray();
        if (canonical.GroupBy(item => item.ExperimentId, StringComparer.Ordinal).Any(group => group.Count() != 1))
            throw new ArgumentException("Experiment history contains duplicate ExperimentId values.", nameof(experiments));
        Experiments = Array.AsReadOnly(canonical);
        TotalCount = canonical.Length;
        LatestExperiment = canonical.LastOrDefault();
    }

    public IReadOnlyList<PersistedResearchExperimentRecord> Experiments { get; }
    public int TotalCount { get; }
    /// <summary>Last record in canonical ExperimentId order, not a timestamp-derived claim.</summary>
    public PersistedResearchExperimentRecord? LatestExperiment { get; }
}

public sealed class ResearchExperimentMetricComparison
{
    public ResearchExperimentMetricComparison(double? experimentA, double? experimentB, bool available)
    {
        if (available && (!experimentA.HasValue || !experimentB.HasValue)) throw new ArgumentException("Available metric comparison requires both values.");
        ExperimentA = experimentA;
        ExperimentB = experimentB;
        Available = available;
    }

    public double? ExperimentA { get; }
    public double? ExperimentB { get; }
    public bool Available { get; }
}

/// <summary>Factual comparison only; no ranking, recommendation, or winner-selection semantics exist here.</summary>
public sealed class ResearchExperimentComparisonResult
{
    public ResearchExperimentComparisonResult(PersistedResearchExperimentRecord experimentA, PersistedResearchExperimentRecord experimentB,
        bool datasetComparable, bool strategyComparable, bool portfolioComparable, bool performanceComparisonAvailable,
        ResearchExperimentMetricComparison totalReturnPercent, ResearchExperimentMetricComparison maximumDrawdownPercent,
        ResearchExperimentMetricComparison tradeCount, ResearchExperimentMetricComparison winRate)
    {
        ArgumentNullException.ThrowIfNull(experimentA);
        ArgumentNullException.ThrowIfNull(experimentB);
        ArgumentNullException.ThrowIfNull(totalReturnPercent);
        ArgumentNullException.ThrowIfNull(maximumDrawdownPercent);
        ArgumentNullException.ThrowIfNull(tradeCount);
        ArgumentNullException.ThrowIfNull(winRate);
        ExperimentA = experimentA;
        ExperimentB = experimentB;
        DatasetComparable = datasetComparable;
        StrategyComparable = strategyComparable;
        PortfolioComparable = portfolioComparable;
        PerformanceComparisonAvailable = performanceComparisonAvailable;
        TotalReturnPercent = totalReturnPercent;
        MaximumDrawdownPercent = maximumDrawdownPercent;
        TradeCount = tradeCount;
        WinRate = winRate;
    }

    public PersistedResearchExperimentRecord ExperimentA { get; }
    public PersistedResearchExperimentRecord ExperimentB { get; }
    public bool DatasetComparable { get; }
    public bool StrategyComparable { get; }
    public bool PortfolioComparable { get; }
    public bool PerformanceComparisonAvailable { get; }
    public ResearchExperimentMetricComparison TotalReturnPercent { get; }
    public ResearchExperimentMetricComparison MaximumDrawdownPercent { get; }
    public ResearchExperimentMetricComparison TradeCount { get; }
    public ResearchExperimentMetricComparison WinRate { get; }
}

public sealed class ResearchExperimentComparisonService
{
    public ResearchExperimentComparisonResult Compare(PersistedResearchExperimentRecord experimentA, PersistedResearchExperimentRecord experimentB)
    {
        ArgumentNullException.ThrowIfNull(experimentA);
        ArgumentNullException.ThrowIfNull(experimentB);
        ResearchExperimentComparison definitionComparison = new(experimentA.Definition, experimentB.Definition);
        bool performanceAvailable = definitionComparison.DatasetComparable && experimentA.ExecutionSummary.Performance is not null && experimentB.ExecutionSummary.Performance is not null;
        ResearchExperimentPerformanceSummary? left = experimentA.ExecutionSummary.Performance;
        ResearchExperimentPerformanceSummary? right = experimentB.ExecutionSummary.Performance;
        return new ResearchExperimentComparisonResult(experimentA, experimentB, definitionComparison.DatasetComparable, definitionComparison.StrategyComparable,
            definitionComparison.PortfolioComparable, performanceAvailable,
            Metric(left?.TotalReturnPercent, right?.TotalReturnPercent, performanceAvailable),
            Metric(left?.MaximumDrawdownPercent, right?.MaximumDrawdownPercent, performanceAvailable),
            Metric(left?.TradeCount, right?.TradeCount, performanceAvailable),
            Metric(left?.WinRate, right?.WinRate, performanceAvailable));
    }

    private static ResearchExperimentMetricComparison Metric(double? first, double? second, bool comparisonAvailable) =>
        new(first, second, comparisonAvailable && first.HasValue && second.HasValue);
    private static ResearchExperimentMetricComparison Metric(int? first, int? second, bool comparisonAvailable) =>
        new(first, second, comparisonAvailable && first.HasValue && second.HasValue);
}
