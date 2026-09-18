using System.Text.Json.Nodes;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchExperimentPersistenceTests
{
    [Fact]
    public async Task Repository_SavesReloadsAndRejectsDuplicateWithoutChangingJsonSemantics()
    {
        string firstRoot = TemporaryDirectory(); string secondRoot = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = Record("EXP-001");
            JsonResearchExperimentRepository first = new(firstRoot);
            JsonResearchExperimentRepository second = new(secondRoot);
            await first.SaveAsync(record);
            await second.SaveAsync(Record("EXP-001"));
            PersistedResearchExperimentRecord loaded = await first.GetAsync("EXP-001");
            Assert.Equal(record.ExperimentFingerprint, loaded.ExperimentFingerprint);
            Assert.Equal(record.Definition.SemanticFingerprint, loaded.Definition.SemanticFingerprint);
            Assert.Equal(record.Lineage.ArtifactFingerprint, loaded.ArtifactReference.ArtifactFingerprint);
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(firstRoot, "EXP-001.json")), await File.ReadAllTextAsync(Path.Combine(secondRoot, "EXP-001.json")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.SaveAsync(record));
        }
        finally { DeleteDirectory(firstRoot); DeleteDirectory(secondRoot); }
    }

    [Fact]
    public async Task Repository_FailedTemporaryWriteLeavesExistingRecordValid()
    {
        string root = TemporaryDirectory();
        try
        {
            JsonResearchExperimentRepository healthy = new(root);
            PersistedResearchExperimentRecord original = Record("EXP-001");
            await healthy.SaveAsync(original);
            JsonResearchExperimentRepository failing = new(root, _ => throw new IOException("simulated write failure"));
            await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(Record("EXP-002")));
            Assert.Equal(original.ExperimentFingerprint, (await healthy.GetAsync("EXP-001")).ExperimentFingerprint);
            Assert.False(File.Exists(Path.Combine(root, "EXP-002.json")));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Repository_RejectsUnsupportedSchemaAndTamperedExperimentFingerprint()
    {
        string root = TemporaryDirectory();
        try
        {
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(Record("EXP-001"));
            string path = Path.Combine(root, "EXP-001.json");
            JsonObject schema = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            schema["schemaVersion"] = "research-experiment-record-v999";
            await File.WriteAllTextAsync(path, schema.ToJsonString());
            await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetAsync("EXP-001"));

            await File.WriteAllTextAsync(path, JsonNode.Parse(await File.ReadAllTextAsync(path))!.ToJsonString().Replace("research-experiment-record-v999", PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal));
            JsonObject tampered = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            tampered["experimentFingerprint"] = "tampered";
            await File.WriteAllTextAsync(path, tampered.ToJsonString());
            await Assert.ThrowsAnyAsync<Exception>(() => repository.GetAsync("EXP-001"));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Repository_RejectsTamperedArtifactLineage()
    {
        string root = TemporaryDirectory();
        try
        {
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(Record("EXP-001"));
            string path = Path.Combine(root, "EXP-001.json");
            JsonObject rootJson = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            rootJson["lineage"]!.AsObject()["artifactFingerprint"] = "different-artifact";
            await File.WriteAllTextAsync(path, rootJson.ToJsonString());
            await Assert.ThrowsAnyAsync<Exception>(() => repository.GetAsync("EXP-001"));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task History_UsesOrdinalExperimentIdOrder_NotTimestampOrder()
    {
        string root = TemporaryDirectory();
        try
        {
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(Record("EXP-003", createdAt: new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero)));
            await repository.SaveAsync(Record("EXP-001", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            await repository.SaveAsync(Record("EXP-002", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
            ResearchExperimentHistory history = await repository.ListAsync();
            Assert.Equal(3, history.TotalCount);
            Assert.Equal(new[] { "EXP-001", "EXP-002", "EXP-003" }, history.Experiments.Select(item => item.ExperimentId));
            Assert.Equal("EXP-003", history.LatestExperiment!.ExperimentId);
            await repository.DeleteAsync("EXP-002");
            Assert.Equal(new[] { "EXP-001", "EXP-003" }, (await repository.ListAsync()).Experiments.Select(item => item.ExperimentId));
            await Assert.ThrowsAsync<FileNotFoundException>(() => repository.DeleteAsync("EXP-002"));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Comparison_ReportsFactualMetricsWithoutWinnerOrRecommendation()
    {
        ResearchExperimentComparisonService service = new();
        PersistedResearchExperimentRecord left = Record("EXP-001", datasetFingerprint: "dataset-a", performance: new(4.5, -2.1, 10, .6));
        PersistedResearchExperimentRecord sameDataset = Record("EXP-002", datasetFingerprint: "dataset-a", performance: new(2.5, -3.2, 8, .5));
        PersistedResearchExperimentRecord differentDataset = Record("EXP-003", datasetFingerprint: "dataset-b", performance: new(8.5, -1.1, 12, .7));
        ResearchExperimentComparisonResult comparable = service.Compare(left, sameDataset);
        ResearchExperimentComparisonResult incomparable = service.Compare(left, differentDataset);
        Assert.True(comparable.DatasetComparable);
        Assert.True(comparable.StrategyComparable);
        Assert.True(comparable.PortfolioComparable);
        Assert.True(comparable.PerformanceComparisonAvailable);
        Assert.True(comparable.TotalReturnPercent.Available);
        Assert.Equal(4.5, comparable.TotalReturnPercent.ExperimentA);
        Assert.Equal(2.5, comparable.TotalReturnPercent.ExperimentB);
        Assert.False(incomparable.DatasetComparable);
        Assert.False(incomparable.PerformanceComparisonAvailable);
        Assert.False(incomparable.TotalReturnPercent.Available);
        string[] prohibited = ["winner", "best", "recommended"];
        Assert.DoesNotContain(typeof(ResearchExperimentComparisonResult).GetProperties(), property => prohibited.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static PersistedResearchExperimentRecord Record(string experimentId, string datasetFingerprint = "dataset-a", DateTimeOffset? createdAt = null,
        ResearchExperimentPerformanceSummary? performance = null)
    {
        ResearchExperimentIdentity identity = new(experimentId, ResearchExperimentIdentity.CurrentExperimentVersion, "test", createdAt);
        ExperimentParameterSnapshot parameters = new("v1", new Dictionary<string, string> { ["TopN"] = "2" },
            new Dictionary<string, string> { ["InitialCapital"] = "1000000" }, new Dictionary<string, string> { ["ReturnBasis"] = "CloseToClose" });
        ResearchExperimentDefinition definition = new(identity, datasetFingerprint, new ResearchExperimentStrategyIdentity("V2", "v2"), parameters,
            "portfolio-a", "analysis-a", "benchmark:CSI300");
        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), "artifact-" + experimentId);
        ResearchExperimentExecutionSummary summary = ResearchExperimentExecutionSummary.FromCompletedExecution(execution, performance);
        ResearchArtifactLineage lineage = new(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, summary.ArtifactFingerprint);
        return new PersistedResearchExperimentRecord(experimentId, definition.SemanticFingerprint, definition, summary,
            new ResearchResultArtifactReference(summary.ArtifactFingerprint, "portfolio-research-v1"), lineage, createdAt ?? new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-ResearchExperimentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
