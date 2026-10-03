namespace AIHelper.Core.Sparrow;

/// <summary>
/// Narrow factory for the current provenance-aware experiment record. It produces a V3 record that binds the
/// observed source/build provenance into the execution provenance binding.
/// <para>
/// Historical V1/V2 records are intentionally not upgradable: source/build facts cannot be reconstructed after
/// the fact, so there is no migration or backfill path.
/// </para>
/// </summary>
public static class ResearchExperimentRecordFactory
{
    /// <summary>Creates a V3 record from completed experiment evidence, the artifact, and observed source/build provenance.</summary>
    public static PersistedResearchExperimentRecord CreateCurrent(
        ResearchExperimentDefinition definition,
        ResearchExperimentExecutionSummary executionSummary,
        SparrowPortfolioResearchArtifact artifact,
        DateTimeOffset createdAt,
        ResearchSourceBuildProvenance sourceBuildProvenance)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(executionSummary);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(sourceBuildProvenance);

        ResearchExecutionProvenanceBinding binding = ResearchExecutionProvenanceBinding.Create(definition, artifact, sourceBuildProvenance);
        ResearchResultArtifactReference artifactReference = new(artifact.ArtifactFingerprint, artifact.ArtifactVersion);
        ResearchArtifactLineage lineage = new(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, artifact.ArtifactFingerprint);

        return new PersistedResearchExperimentRecord(
            definition.Identity.ExperimentId,
            definition.SemanticFingerprint,
            definition,
            executionSummary,
            artifactReference,
            lineage,
            createdAt,
            PersistedResearchExperimentRecord.CurrentSchemaVersion,
            binding,
            sourceBuildProvenance);
    }
}
