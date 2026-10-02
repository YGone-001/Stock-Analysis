using System.Globalization;
using System.Diagnostics;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchExperimentReportingTests
{
    [Fact]
    public void Summary_UsesPersistedFactsWithoutCalculationDependencies()
    {
        PersistedResearchExperimentRecord record = Record("EXP-001", performance: new(12.4, -8.1, 42, .57));
        ResearchExperimentReport report = new ResearchExperimentReportBuilder().BuildExperimentReport(record);
        string markdown = new MarkdownResearchExperimentReportRenderer().Render(report);

        Assert.Equal(ResearchReportVersion.Current, report.Identity.ReportVersion);
        Assert.Contains(record.ExperimentId, markdown); Assert.Contains(record.ExperimentFingerprint, markdown); Assert.Contains(record.Definition.DatasetFingerprint, markdown);
        Assert.Contains(record.Definition.StrategyParameterFingerprint, markdown); Assert.Contains(record.Definition.PortfolioConfigurationFingerprint, markdown);
        Assert.Contains(record.Definition.AnalysisConfigurationFingerprint, markdown); Assert.Contains(record.ArtifactReference.ArtifactFingerprint, markdown);
        Assert.Contains("12.4%", markdown); Assert.Contains("-8.1%", markdown); Assert.Contains("42", markdown); Assert.Contains("57%", markdown);
    }

    [Fact]
    public void MarkdownAndJson_AreDeterministicCultureInvariantAndCanonicallyOrdered()
    {
        PersistedResearchExperimentRecord record = Record("EXP-001", performance: new(12.4, -8.1, 42, .57), warnings: ["z warning", "a warning"]);
        ResearchExperimentReportBuilder builder = new();
        ResearchExperimentReport first = builder.BuildExperimentReport(record);
        ResearchExperimentReport second = builder.BuildExperimentReport(record);
        MarkdownResearchExperimentReportRenderer renderer = new();
        ResearchExperimentReportExporter exporter = new(renderer);
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US"); string us = renderer.Render(first); string jsonUs = exporter.SerializeJson(first);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR"); string fr = renderer.Render(second); string jsonFr = exporter.SerializeJson(second);
            Assert.Equal(us, fr); Assert.Equal(jsonUs, jsonFr); Assert.Equal(us, renderer.Render(first));
            Assert.True(us.IndexOf("a warning", StringComparison.Ordinal) < us.IndexOf("z warning", StringComparison.Ordinal));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void Fingerprint_IgnoresMetadataAndChangesForReportSemantics()
    {
        ResearchExperimentReportBuilder builder = new();
        ResearchExperimentReport baseline = builder.BuildExperimentReport(Record("EXP-001", createdBy: "one", createdAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), performance: new(1, -1, 1, 1)));
        ResearchExperimentReport metadata = builder.BuildExperimentReport(Record("EXP-001", createdBy: "two", createdAt: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), performance: new(1, -1, 1, 1)));
        ResearchExperimentReport dataset = builder.BuildExperimentReport(Record("EXP-001", dataset: "dataset-b", performance: new(1, -1, 1, 1)));
        ResearchExperimentReport metric = builder.BuildExperimentReport(Record("EXP-001", performance: new(2, -1, 1, 1)));
        ResearchExperimentReport artifact = builder.BuildExperimentReport(Record("EXP-001", artifact: "artifact-other", performance: new(1, -1, 1, 1)));
        Assert.Equal(baseline.Identity.ReportFingerprint, metadata.Identity.ReportFingerprint);
        Assert.NotEqual(baseline.Identity.ReportFingerprint, dataset.Identity.ReportFingerprint);
        Assert.NotEqual(baseline.Identity.ReportFingerprint, metric.Identity.ReportFingerprint);
        Assert.NotEqual(baseline.Identity.ReportFingerprint, artifact.Identity.ReportFingerprint);
    }

    [Fact]
    public void Lineage_ContainsOnlyPersistedProvenanceFacts()
    {
        PersistedResearchExperimentRecord record = Record("EXP-001");
        ResearchExperimentLineageReport report = new ResearchExperimentReportBuilder().BuildLineageReport(record);
        Assert.Equal(record.ExperimentId, report.ExperimentId); Assert.Equal(record.Lineage.ExperimentFingerprint, report.ExperimentFingerprint);
        Assert.Equal(record.Lineage.DatasetFingerprint, report.DatasetFingerprint); Assert.Equal(record.Lineage.ParameterFingerprint, report.ParameterFingerprint);
        Assert.Equal(record.Lineage.ArtifactFingerprint, report.ArtifactFingerprint); Assert.Equal(record.ArtifactReference.ArtifactVersion, report.ArtifactVersion);
    }

    [Fact]
    public void Comparison_IsNeutralAndReportsNonComparableDatasetsFactually()
    {
        ResearchExperimentComparisonService comparisonService = new();
        PersistedResearchExperimentRecord first = Record("EXP-002", dataset: "dataset-a", performance: new(4.5, -2.1, 10, .6));
        PersistedResearchExperimentRecord second = Record("EXP-001", dataset: "dataset-b", performance: new(2.5, -3.2, 8, .5));
        ResearchExperimentComparisonReport report = new ResearchExperimentReportBuilder().BuildComparisonReport(comparisonService.Compare(first, second));
        string markdown = new MarkdownResearchExperimentReportRenderer().Render(report);
        Assert.Equal("EXP-001", report.ExperimentA.ExperimentId); Assert.False(report.DatasetComparable); Assert.False(report.PerformanceComparisonAvailable);
        Assert.All(report.Metrics, metric => Assert.False(metric.Available)); Assert.Contains("Performance Comparison Available: false", markdown);
        Assert.Contains("| Total Return | 2.5% | 4.5% | false |", markdown);
        string[] prohibited = ["winner", "best", "recommended", "superior", "inferior", "score", "rank"];
        Assert.DoesNotContain(typeof(ResearchExperimentComparisonReport).GetProperties(), property => prohibited.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Summary_RendersUnavailableMetricsAsTheCanonicalLiteral()
    {
        ResearchExperimentReport report = new ResearchExperimentReportBuilder().BuildExperimentReport(Record("EXP-001", performance: new ResearchExperimentPerformanceSummary()));
        string markdown = new MarkdownResearchExperimentReportRenderer().Render(report);
        Assert.Contains("| Total Return | Unavailable |", markdown); Assert.DoesNotContain("| Total Return | 0% |", markdown);
    }

    [Fact]
    public async Task Export_FailureLeavesNoDestinationOrTemporaryFile()
    {
        string directory = TemporaryDirectory(); string output = Path.Combine(directory, "report.md");
        try
        {
            ResearchExperimentReport report = new ResearchExperimentReportBuilder().BuildExperimentReport(Record("EXP-001"));
            ResearchExperimentReportExporter exporter = new(new MarkdownResearchExperimentReportRenderer(), _ => throw new IOException("simulated write failure"));
            await Assert.ThrowsAsync<IOException>(() => exporter.ExportMarkdownAsync(report, output));
            Assert.False(File.Exists(output)); Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Cli_ExportsLocalReportWithoutHistoricalGateway()
    {
        string directory = TemporaryDirectory(); string output = Path.Combine(directory, "report.md");
        try
        {
            await new JsonResearchExperimentRepository(directory).SaveAsync(Record("EXP-001", performance: new(1.25, -2.5, 4, .5)));
            ProcessStartInfo start = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SolutionRoot() };
            start.Environment.Remove("HISTORICAL_GATEWAY_URL");
            start.ArgumentList.Add("run"); start.ArgumentList.Add("--project"); start.ArgumentList.Add(Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"));
            start.ArgumentList.Add("-c"); start.ArgumentList.Add("Debug"); start.ArgumentList.Add("--no-restore"); start.ArgumentList.Add("--");
            start.ArgumentList.Add("--report-experiment"); start.ArgumentList.Add("EXP-001"); start.ArgumentList.Add("--report-format"); start.ArgumentList.Add("markdown");
            start.ArgumentList.Add("--experiment-store"); start.ArgumentList.Add(directory); start.ArgumentList.Add("--output"); start.ArgumentList.Add(output);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start reporting CLI.");
            string standardOutput = await process.StandardOutput.ReadToEndAsync(); string standardError = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"CLI failed: {standardOutput}\n{standardError}"); Assert.True(File.Exists(output));
            Assert.Contains("Research Experiment Report", await File.ReadAllTextAsync(output));
        }
        finally { DeleteDirectory(directory); }
    }

    private static PersistedResearchExperimentRecord Record(string experimentId, string dataset = "dataset-a", string createdBy = "test", DateTimeOffset? createdAt = null,
        ResearchExperimentPerformanceSummary? performance = null, IEnumerable<string>? warnings = null, string? artifact = null)
    {
        ResearchExperimentIdentity identity = new(experimentId, ResearchExperimentIdentity.CurrentExperimentVersion, createdBy, createdAt);
        ExperimentParameterSnapshot parameters = new("v1", new Dictionary<string, string> { ["TopN"] = "2" }, new Dictionary<string, string> { ["Capital"] = "100" }, new Dictionary<string, string>());
        ResearchExperimentDefinition definition = new(identity, dataset, new ResearchExperimentStrategyIdentity("V2", "v2"), parameters, "portfolio-a", "analysis-a", "benchmark:CSI300");
        string artifactFingerprint = artifact ?? "artifact-" + experimentId;
        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity).Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), artifactFingerprint, warnings);
        ResearchExperimentExecutionSummary summary = ResearchExperimentExecutionSummary.FromCompletedExecution(execution, performance ?? new ResearchExperimentPerformanceSummary(1, -1, 1, 1));
        ResearchArtifactLineage lineage = new(definition.SemanticFingerprint, definition.DatasetFingerprint, parameters.Fingerprint, artifactFingerprint);
        return new PersistedResearchExperimentRecord(experimentId, definition.SemanticFingerprint, definition, summary, new ResearchResultArtifactReference(artifactFingerprint, "portfolio-research-v1"),
            lineage, createdAt ?? new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));
    }

    private static string TemporaryDirectory() { string path = Path.Combine(Path.GetTempPath(), "AIHelper-ResearchReportingTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static void DeleteDirectory(string path) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    private static string SolutionRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIHelper.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root was not found.");
    }
}
