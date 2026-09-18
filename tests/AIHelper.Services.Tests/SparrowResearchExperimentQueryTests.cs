using AIHelper.Core.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchExperimentQueryTests
{
    [Fact]
    public async Task Query_UsesExactAndConditionsAndReturnsOrdinalOrder()
    {
        ResearchExperimentQueryService service = Service(Record("EXP-003", "dataset-a", "V2"), Record("EXP-001", "dataset-a", "V2"), Record("EXP-002", "dataset-b", "Classic"));
        ResearchExperimentQueryResult all = await service.QueryAsync(new ResearchExperimentQueryRequest());
        ResearchExperimentQueryResult exact = await service.QueryAsync(new ResearchExperimentQueryRequest(experimentId: "EXP-001", datasetFingerprint: "dataset-a", status: ResearchExperimentExecutionStatus.Completed, benchmarkId: "CSI300"));
        ResearchExperimentQueryResult noMatch = await service.QueryAsync(new ResearchExperimentQueryRequest(experimentId: "EXP-001", datasetFingerprint: "dataset-b"));
        Assert.Equal(new[] { "EXP-001", "EXP-002", "EXP-003" }, all.Experiments.Select(item => item.ExperimentId));
        Assert.Equal(1, exact.MatchedCount); Assert.Equal("EXP-001", exact.Experiments[0].ExperimentId);
        Assert.Equal(0, noMatch.MatchedCount); Assert.Empty(noMatch.Experiments);
    }

    [Fact]
    public async Task Query_FindsOnlyMatchingDatasetStrategyAndFingerprint()
    {
        PersistedResearchExperimentRecord v2 = Record("EXP-001", "dataset-a", "V2");
        PersistedResearchExperimentRecord classic = Record("EXP-002", "dataset-a", "Classic");
        ResearchExperimentQueryService service = Service(v2, classic, Record("EXP-003", "dataset-b", "V2"));
        Assert.Equal(new[] { "EXP-001", "EXP-003" }, (await service.FindByStrategyAsync(new ResearchExperimentStrategyIdentity("V2", "v2"))).Experiments.Select(item => item.ExperimentId));
        Assert.Equal(new[] { "EXP-001", "EXP-002" }, (await service.FindByDatasetAsync("dataset-a")).Experiments.Select(item => item.ExperimentId));
        Assert.Equal("EXP-001", (await service.FindByFingerprintAsync(v2.ExperimentFingerprint)).Experiments.Single().ExperimentId);
    }

    [Fact]
    public void Summary_ReadsPersistedFactsWithoutRecalculation()
    {
        PersistedResearchExperimentRecord record = Record("EXP-001", "dataset-a", "V2", new ResearchExperimentPerformanceSummary(7.5, -3.25, 12, .75));
        ResearchExperimentSummary summary = new ResearchExperimentSummaryAnalyzer().Analyze(record);
        Assert.Equal(7.5, summary.TotalReturn); Assert.Equal(-3.25, summary.MaximumDrawdown); Assert.Equal(12, summary.TradeCount); Assert.Equal(.75, summary.WinRate);
        Assert.True(summary.ArtifactAvailable); Assert.Equal("portfolio-a", summary.PortfolioConfiguration);
    }

    [Fact]
    public void TimelineAndBatchComparison_AreDeterministicAndNeutral()
    {
        PersistedResearchExperimentRecord first = Record("EXP-003", "dataset-a", "V2", new ResearchExperimentPerformanceSummary(3, -2, 4, .5));
        PersistedResearchExperimentRecord sameGroup = Record("EXP-001", "dataset-a", "V2", new ResearchExperimentPerformanceSummary(2, -3, 6, .5));
        PersistedResearchExperimentRecord differentDataset = Record("EXP-002", "dataset-b", "V2", new ResearchExperimentPerformanceSummary(1, -1, 2, .5));
        ResearchExperimentTimeline timeline = new([first, sameGroup, differentDataset]);
        ResearchExperimentBatchComparison batch = new([first, sameGroup, differentDataset]);
        Assert.Equal(new[] { "EXP-001", "EXP-002", "EXP-003" }, timeline.Experiments.Select(item => item.ExperimentId));
        Assert.Equal(2, batch.ComparableGroups.Count);
        Assert.Equal(new[] { "EXP-001", "EXP-003" }, batch.ComparableGroups.Single(group => group.DatasetFingerprint == "dataset-a").Experiments.Select(item => item.ExperimentId));
        Assert.True(batch.MetricAvailability.TotalReturn);
        string[] prohibited = ["winner", "best", "recommended"];
        Assert.DoesNotContain(typeof(ResearchExperimentBatchComparison).GetProperties(), property => prohibited.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static ResearchExperimentQueryService Service(params PersistedResearchExperimentRecord[] records) => new(new TestRepository(records));
    private static PersistedResearchExperimentRecord Record(string id, string dataset, string mode, ResearchExperimentPerformanceSummary? performance = null)
    {
        ResearchExperimentIdentity identity = new(id, ResearchExperimentIdentity.CurrentExperimentVersion, "test", new DateTimeOffset(2026, 1, int.Parse(id[^1].ToString()), 0, 0, 0, TimeSpan.Zero));
        ExperimentParameterSnapshot parameters = new("v1", new Dictionary<string, string> { ["TopN"] = "2" }, new Dictionary<string, string> { ["Capital"] = "100" }, new Dictionary<string, string>());
        ResearchExperimentDefinition definition = new(identity, dataset, new ResearchExperimentStrategyIdentity(mode, mode == "V2" ? "v2" : "classic-v1"), parameters, "portfolio-a", "analysis-a", "CSI300");
        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity).Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero)).Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "artifact-" + id);
        ResearchExperimentExecutionSummary summary = ResearchExperimentExecutionSummary.FromCompletedExecution(execution, performance ?? new ResearchExperimentPerformanceSummary(1, -1, 1, 1));
        ResearchArtifactLineage lineage = new(definition.SemanticFingerprint, dataset, parameters.Fingerprint, summary.ArtifactFingerprint);
        return new PersistedResearchExperimentRecord(id, definition.SemanticFingerprint, definition, summary, new ResearchResultArtifactReference(summary.ArtifactFingerprint, "v1"), lineage, identity.CreatedAt!.Value);
    }

    private sealed class TestRepository : IResearchExperimentRepository
    {
        private readonly ResearchExperimentHistory _history;
        public TestRepository(IEnumerable<PersistedResearchExperimentRecord> records) => _history = new ResearchExperimentHistory(records);
        public Task SaveAsync(PersistedResearchExperimentRecord experiment, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersistedResearchExperimentRecord> GetAsync(string experimentId, CancellationToken cancellationToken = default) => Task.FromResult(_history.Experiments.Single(item => item.ExperimentId == experimentId));
        public Task<ResearchExperimentHistory> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(_history);
        public Task DeleteAsync(string experimentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
