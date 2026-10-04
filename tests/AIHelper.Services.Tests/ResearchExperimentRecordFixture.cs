using System.Reflection;
using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Services.Tests;

/// <summary>Shared deterministic experiment-record builders for provenance tests. No filesystem or machine state is involved.</summary>
internal static class ResearchExperimentRecordFixture
{
    public const string DatasetFingerprint = "dataset-fp";
    public const string ArtifactFingerprint = "artifact-fp-mock";

    public static SparrowPortfolioResearchArtifact Artifact()
    {
        DateOnly entryDate = new(2026, 1, 2), exitDate = new(2026, 1, 3);
        PortfolioSimulationRequest request = new("fixture-dataset", DatasetFingerprint, SparrowStrategyMode.V2, SparrowStrategyVersions.V2,
            "strategy-param-fp", entryDate, exitDate, 2, 1, 1_000, PortfolioPositionSizingMethod.EqualWeight, 0, 0);
        string strategyFingerprint = SparrowPortfolioResearchFingerprint.Strategy(request);
        string portfolioFingerprint = SparrowPortfolioResearchFingerprint.Portfolio(request);

        return new SparrowPortfolioResearchArtifact(
            SparrowPortfolioResearchArtifact.CurrentArtifactVersion, ArtifactFingerprint, "AIHelper.HistoricalDataTool", DatasetFingerprint,
            strategyFingerprint, portfolioFingerprint,
            SparrowPortfolioResearchFingerprint.Analysis(DatasetFingerprint, strategyFingerprint, portfolioFingerprint),
            new(request.StrategyMode.ToString(), request.StrategyVersion, strategyFingerprint), request, new(1, 0, 1),
            new(1_000, 1_010, 1, -1, exitDate, 1, 1, 0, 1.0), new("Unavailable", null, null, null),
            new PortfolioTrade[] { new("600000", entryDate, PortfolioTradeSide.Buy, 100, 1, 100, 0) },
            new PortfolioPosition[] { new("600000", entryDate, 100, 1, 100, 0, PortfolioPositionStatus.Closed, exitDate, 110, 110, 0, 10, 10) },
            new[] { new PortfolioEquityPoint(entryDate, 900, 100, 1_000, null, 0) },
            new PortfolioAttribution[] { new("600000", entryDate, exitDate, 1, 1, 10, 10, 1, true) },
            new[] { "warning-a" }, SparrowPortfolioResearchArtifact.DefaultLimitations);
    }

    public static ResearchExperimentDefinition Definition(string experimentId = "EXP-ENV-001")
    {
        SparrowPortfolioResearchArtifact artifact = Artifact();
        ResearchExperimentIdentity identity = new(experimentId, ResearchExperimentIdentity.CurrentExperimentVersion, "test", DateTimeOffset.UnixEpoch);
        ExperimentParameterSnapshot parameters = new("research-experiment-parameters-v1",
            new Dictionary<string, string> { ["StrategyMode"] = artifact.Strategy.Mode },
            new Dictionary<string, string> { ["InitialCapital"] = "1000" },
            new Dictionary<string, string> { ["AnalyzerContract"] = "PortfolioPerformanceAnalyzerV1" });

        return new ResearchExperimentDefinition(identity, artifact.DatasetFingerprint,
            new ResearchExperimentStrategyIdentity(artifact.Strategy.Mode, artifact.Strategy.Version), parameters,
            artifact.PortfolioConfigurationFingerprint, parameters.AnalysisFingerprint);
    }

    public static PersistedResearchExperimentRecord CreateV4Record(
        ResearchSourceBuildProvenance sourceBuild, ResearchExecutionEnvironmentProvenance environment, string experimentId = "EXP-ENV-001")
    {
        ResearchExperimentDefinition definition = Definition(experimentId);
        SparrowPortfolioResearchArtifact artifact = Artifact();
        ResearchExecutionProvenanceBinding binding = ResearchExecutionProvenanceBinding.Create(definition, artifact, sourceBuild, environment);
        return Build(experimentId, definition, artifact, PersistedResearchExperimentRecord.CurrentSchemaVersion, binding, sourceBuild, environment);
    }

    public static PersistedResearchExperimentRecord CreateV3Record(ResearchSourceBuildProvenance sourceBuild, string experimentId = "EXP-ENV-V3")
    {
        ResearchExperimentDefinition definition = Definition(experimentId);
        SparrowPortfolioResearchArtifact artifact = Artifact();
        ResearchExecutionProvenanceBinding binding = ResearchExecutionProvenanceBinding.Create(definition, artifact, sourceBuild);
        return Build(experimentId, definition, artifact, PersistedResearchExperimentRecord.SourceBuildProvenanceSchemaVersion, binding, sourceBuild, null);
    }

    /// <summary>Creates a historical V1 or V2 record exactly as that schema requires, with no provenance domains.</summary>
    public static PersistedResearchExperimentRecord CreateLegacyRecord(string schemaVersion, string experimentId)
    {
        ResearchExperimentDefinition definition = Definition(experimentId);
        SparrowPortfolioResearchArtifact artifact = Artifact();
        ResearchExecutionProvenanceBinding? binding = string.Equals(schemaVersion, PersistedResearchExperimentRecord.LegacySchemaVersion, StringComparison.Ordinal)
            ? null
            : ResearchExecutionProvenanceBinding.Create(definition, artifact);
        return Build(experimentId, definition, artifact, schemaVersion, binding, null, null);
    }

    public static ResearchExperimentExecutionSummary Summary(string experimentId = "EXP-ENV-001")
    {
        ResearchExperimentIdentity identity = new(experimentId, ResearchExperimentIdentity.CurrentExperimentVersion, "test", DateTimeOffset.UnixEpoch);
        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity)
            .Start(DateTimeOffset.UnixEpoch)
            .Complete(DateTimeOffset.UnixEpoch, ArtifactFingerprint);
        return ResearchExperimentExecutionSummary.FromCompletedExecution(execution);
    }

    /// <summary>Builds an object graph that bypasses constructor validation, used only to exercise defensive checks.</summary>
    public static PersistedResearchExperimentRecord Forge(
        PersistedResearchExperimentRecord source, ResearchExecutionEnvironmentProvenance? environmentProvenance, ResearchExecutionProvenanceBinding? binding)
    {
        PersistedResearchExperimentRecord forged = (PersistedResearchExperimentRecord)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PersistedResearchExperimentRecord));
        SetField(forged, "<ExperimentId>k__BackingField", source.ExperimentId);
        SetField(forged, "<SchemaVersion>k__BackingField", source.SchemaVersion);
        SetField(forged, "<ExecutionEnvironmentProvenance>k__BackingField", environmentProvenance);
        SetField(forged, "<ExecutionProvenanceBinding>k__BackingField", binding);
        return forged;
    }

    private static PersistedResearchExperimentRecord Build(string experimentId, ResearchExperimentDefinition definition,
        SparrowPortfolioResearchArtifact artifact, string schemaVersion, ResearchExecutionProvenanceBinding? binding,
        ResearchSourceBuildProvenance? sourceBuild, ResearchExecutionEnvironmentProvenance? environment)
    {
        return new PersistedResearchExperimentRecord(
            experimentId, definition.SemanticFingerprint, definition, Summary(experimentId),
            new ResearchResultArtifactReference(artifact.ArtifactFingerprint, artifact.ArtifactVersion),
            new ResearchArtifactLineage(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, artifact.ArtifactFingerprint),
            DateTimeOffset.UnixEpoch, schemaVersion, binding, sourceBuild, environment);
    }

    private static void SetField(object target, string fieldName, object? value) =>
        target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
