using AIHelper.Core.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchExperimentTests
{
    [Fact]
    public void Definition_RequiresCompleteSemanticIdentity()
    {
        ResearchExperimentIdentity identity = new("experiment-a", ResearchExperimentIdentity.CurrentExperimentVersion, "test");
        ExperimentParameterSnapshot parameters = Parameters();
        Assert.Throws<ArgumentException>(() => new ResearchExperimentDefinition(identity, "", Strategy(), parameters, "portfolio", "analysis"));
        Assert.Throws<ArgumentException>(() => new ResearchExperimentDefinition(identity, "dataset", Strategy(), parameters, "", "analysis"));
        Assert.Throws<ArgumentException>(() => new ResearchExperimentDefinition(identity, "dataset", Strategy(), parameters, "portfolio", ""));
    }

    [Fact]
    public void SemanticFingerprint_IsStableForEquivalentInput_AndExcludesCreatedAtAndOperatorMetadata()
    {
        ResearchExperimentDefinition first = Definition("experiment-a", new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), "operator-a", "first description");
        ResearchExperimentDefinition later = Definition("experiment-a", new DateTimeOffset(2026, 2, 1, 8, 0, 0, TimeSpan.Zero), "operator-b", "changed description");
        Assert.Equal(first.Identity.SemanticFingerprint, later.Identity.SemanticFingerprint);
        Assert.Equal(first.SemanticFingerprint, later.SemanticFingerprint);
    }

    [Fact]
    public void SemanticFingerprint_ChangesWhenPortfolioParameterChanges()
    {
        ResearchExperimentDefinition baseline = Definition("experiment-a");
        ExperimentParameterSnapshot changedParameters = Parameters(portfolio: new Dictionary<string, string> { ["InitialCapital"] = "2000000", ["TopN"] = "2" });
        ResearchExperimentDefinition changed = new(new ResearchExperimentIdentity("experiment-a", ResearchExperimentIdentity.CurrentExperimentVersion, "test"), "dataset-a", Strategy(), changedParameters, "portfolio-a", "analysis-a");
        Assert.NotEqual(baseline.Parameters.Fingerprint, changed.Parameters.Fingerprint);
        Assert.NotEqual(baseline.SemanticFingerprint, changed.SemanticFingerprint);
    }

    [Fact]
    public void ParameterSnapshot_CopiesSourceCollectionsAndCanonicalizesOrdering()
    {
        Dictionary<string, string> source = new() { ["Z"] = "3", ["A"] = "1" };
        ExperimentParameterSnapshot snapshot = new("v1", source, new Dictionary<string, string>(), new Dictionary<string, string>());
        source["A"] = "changed";
        source["B"] = "2";
        Assert.Equal(new[] { "A", "Z" }, snapshot.StrategyParameters.Keys);
        Assert.Equal("1", snapshot.StrategyParameters["A"]);
        Assert.False(snapshot.StrategyParameters.ContainsKey("B"));
    }

    [Fact]
    public void Execution_EnforcesCreatedRunningCompletedLifecycle_AndRequiresArtifactFingerprint()
    {
        ResearchExperimentIdentity identity = new("experiment-a", ResearchExperimentIdentity.CurrentExperimentVersion, "test");
        ResearchExperimentExecution created = ResearchExperimentExecution.Create(identity);
        Assert.Equal(ResearchExperimentExecutionStatus.Created, created.Status);
        Assert.Throws<InvalidOperationException>(() => created.Complete(DateTimeOffset.UtcNow, "artifact-a"));
        ResearchExperimentExecution running = created.Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero));
        Assert.Equal(ResearchExperimentExecutionStatus.Running, running.Status);
        Assert.Throws<ArgumentException>(() => running.Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), ""));
        ResearchExperimentExecution completed = running.Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "artifact-a");
        Assert.Equal(ResearchExperimentExecutionStatus.Completed, completed.Status);
        Assert.Equal("artifact-a", completed.ArtifactFingerprint);
    }

    [Fact]
    public void Result_RejectsInconsistentLineage()
    {
        ResearchExperimentDefinition definition = Definition("experiment-a");
        ResearchExperimentExecution completed = ResearchExperimentExecution.Create(definition.Identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "artifact-a");
        ResearchArtifactLineage invalid = new(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, "artifact-b");
        Assert.Throws<ArgumentException>(() => new ResearchExperimentResult(definition, completed, invalid, new ResearchResultArtifactReference("artifact-a", "portfolio-research-v1")));
    }

    [Fact]
    public void Result_AcceptsCompleteLineage_AndRejectsMissingFingerprint()
    {
        ResearchExperimentDefinition definition = Definition("experiment-a");
        ResearchExperimentExecution completed = ResearchExperimentExecution.Create(definition.Identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "artifact-a");
        ResearchArtifactLineage lineage = new(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, "artifact-a");
        ResearchExperimentResult result = new(definition, completed, lineage, new ResearchResultArtifactReference("artifact-a", "portfolio-research-v1"),
            warnings: ["z-warning", "a-warning"], limitations: ["local only"]);
        Assert.Equal("artifact-a", result.ResultArtifactReference.ArtifactFingerprint);
        Assert.Equal(new[] { "a-warning", "z-warning" }, result.Warnings);
        Assert.Throws<ArgumentException>(() => new ResearchArtifactLineage("", definition.DatasetFingerprint, definition.Parameters.Fingerprint, "artifact-a"));
    }

    [Fact]
    public void Registry_RetrievesExperimentsInDeterministicOrdinalOrder()
    {
        IResearchExperimentRegistry registry = new InMemoryResearchExperimentRegistry();
        ResearchExperimentDefinition zulu = Definition("zulu");
        ResearchExperimentDefinition alpha = Definition("alpha");
        registry.RegisterExperiment(zulu);
        registry.RegisterExperiment(alpha);
        Assert.Same(alpha, registry.GetExperiment("alpha"));
        Assert.Equal(new[] { "alpha", "zulu" }, registry.ListExperiments().Select(item => item.Identity.ExperimentId));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterExperiment(Definition("alpha")));
    }

    [Fact]
    public void Comparison_ReportsDatasetCompatibilityWithoutRankingExperiments()
    {
        ResearchExperimentDefinition first = Definition("experiment-a", datasetFingerprint: "dataset-a");
        ResearchExperimentDefinition sameDataset = Definition("experiment-b", datasetFingerprint: "dataset-a");
        ResearchExperimentDefinition differentDataset = Definition("experiment-c", datasetFingerprint: "dataset-b");
        ResearchExperimentComparison comparable = new(first, sameDataset, performanceComparisonAvailable: true);
        ResearchExperimentComparison incomparable = new(first, differentDataset);
        Assert.True(comparable.DatasetComparable);
        Assert.True(comparable.PerformanceComparisonAvailable);
        Assert.False(incomparable.DatasetComparable);
        Assert.False(incomparable.PerformanceComparisonAvailable);
        Assert.Throws<ArgumentException>(() => new ResearchExperimentComparison(first, differentDataset, performanceComparisonAvailable: true));
    }

    private static ResearchExperimentDefinition Definition(string experimentId, DateTimeOffset? createdAt = null, string createdBy = "test", string? description = null,
        string datasetFingerprint = "dataset-a") => new(new ResearchExperimentIdentity(experimentId, ResearchExperimentIdentity.CurrentExperimentVersion, createdBy, createdAt),
        datasetFingerprint, Strategy(), Parameters(), "portfolio-a", "analysis-a", "benchmark:CSI300", description);

    private static ResearchExperimentStrategyIdentity Strategy() => new("V2", "v2");

    private static ExperimentParameterSnapshot Parameters(IReadOnlyDictionary<string, string>? portfolio = null) => new("v1",
        new Dictionary<string, string> { ["MinimumHistoryBars"] = "60", ["TopN"] = "2" },
        portfolio ?? new Dictionary<string, string> { ["InitialCapital"] = "1000000", ["TopN"] = "2" },
        new Dictionary<string, string> { ["ReturnBasis"] = "CloseToClose" });
}
