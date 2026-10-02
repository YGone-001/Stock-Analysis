using System.Globalization;
using System.Text;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

public interface IResearchExperimentReportRenderer
{
    string Render(ResearchExperimentReport report);
    string Render(ResearchExperimentLineageReport report);
    string Render(ResearchExperimentComparisonReport report);
}

/// <summary>Culture-invariant deterministic Markdown renderer for structured research reports.</summary>
public sealed class MarkdownResearchExperimentReportRenderer : IResearchExperimentReportRenderer
{
    public string Render(ResearchExperimentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder text = new();
        text.Append("# Research Experiment Report\n\n"); Identity(text, report.Identity);
        Heading(text, "Experiment"); Item(text, "Experiment ID", report.ExperimentId); Item(text, "Experiment Version", report.ExperimentVersion); Item(text, "Experiment Fingerprint", report.Lineage.ExperimentFingerprint);
        Heading(text, "Dataset"); Item(text, "Dataset Fingerprint", report.Lineage.DatasetFingerprint);
        Heading(text, "Strategy"); Item(text, "Mode", report.StrategyMode); Item(text, "Version", report.StrategyVersion); Item(text, "Parameter Fingerprint", report.StrategyParameterFingerprint);
        Heading(text, "Portfolio"); Item(text, "Configuration Fingerprint", report.PortfolioConfigurationFingerprint);
        Heading(text, "Analysis"); Item(text, "Configuration Fingerprint", report.AnalysisConfigurationFingerprint);
        Heading(text, "Benchmark"); Item(text, "Configuration", report.BenchmarkConfiguration);
        Heading(text, "Execution"); Item(text, "Status", report.Lineage.ExecutionStatus.ToString()); Item(text, "Artifact Fingerprint", report.Lineage.ArtifactFingerprint);
        Metrics(text, report.Performance);
        Heading(text, "Lineage"); Item(text, "Experiment Fingerprint", report.Lineage.ExperimentFingerprint); Item(text, "Dataset Fingerprint", report.Lineage.DatasetFingerprint);
        Item(text, "Parameter Fingerprint", report.Lineage.ParameterFingerprint); Item(text, "Artifact Fingerprint", report.Lineage.ArtifactFingerprint); Item(text, "Artifact Version", report.Lineage.ArtifactVersion);
        TextList(text, "Warnings", report.Warnings.Select(warning => warning.Message)); TextList(text, "Limitations", report.Limitations);
        return text.ToString();
    }

    public string Render(ResearchExperimentLineageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder text = new("# Research Experiment Lineage Report\n\n"); Identity(text, report.Identity);
        Heading(text, "Lineage"); Item(text, "Experiment ID", report.ExperimentId); Item(text, "Experiment Fingerprint", report.ExperimentFingerprint);
        Item(text, "Dataset Fingerprint", report.DatasetFingerprint); Item(text, "Parameter Fingerprint", report.ParameterFingerprint);
        Item(text, "Execution Status", report.ExecutionStatus.ToString()); Item(text, "Artifact Fingerprint", report.ArtifactFingerprint); Item(text, "Artifact Version", report.ArtifactVersion);
        return text.ToString();
    }

    public string Render(ResearchExperimentComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        StringBuilder text = new("# Research Experiment Comparison Report\n\n"); Identity(text, report.Identity);
        Heading(text, "Experiments"); Item(text, "Experiment A ID", report.ExperimentA.ExperimentId); Item(text, "Experiment A Fingerprint", report.ExperimentA.ExperimentFingerprint);
        Item(text, "Experiment B ID", report.ExperimentB.ExperimentId); Item(text, "Experiment B Fingerprint", report.ExperimentB.ExperimentFingerprint);
        Heading(text, "Compatibility"); Item(text, "Dataset Comparable", Boolean(report.DatasetComparable)); Item(text, "Strategy Comparable", Boolean(report.StrategyComparable));
        Item(text, "Portfolio Comparable", Boolean(report.PortfolioComparable)); Item(text, "Performance Comparison Available", Boolean(report.PerformanceComparisonAvailable));
        Heading(text, "Performance"); text.Append("| Metric | Experiment A | Experiment B | Available |\n| --- | ---: | ---: | --- |\n");
        foreach (ResearchReportComparisonMetric metric in report.Metrics) text.Append("| ").Append(metric.Name).Append(" | ").Append(Format(metric.Kind, metric.ExperimentA)).Append(" | ")
            .Append(Format(metric.Kind, metric.ExperimentB)).Append(" | ").Append(Boolean(metric.Available)).Append(" |\n");
        text.Append('\n'); TextList(text, "Warnings", report.Warnings.Select(warning => warning.Message));
        return text.ToString();
    }

    private static void Identity(StringBuilder text, ResearchReportIdentity identity)
    { Heading(text, "Report Identity"); Item(text, "Report Version", identity.ReportVersion); Item(text, "Report Type", identity.ReportType.ToString()); Item(text, "Report Fingerprint", identity.ReportFingerprint); }
    private static void Metrics(StringBuilder text, IReadOnlyList<ResearchReportMetric> metrics)
    { Heading(text, "Performance"); text.Append("| Metric | Value |\n| --- | ---: |\n"); foreach (ResearchReportMetric metric in metrics) text.Append("| ").Append(metric.Name).Append(" | ").Append(Format(metric.Kind, metric.Value)).Append(" |\n"); text.Append('\n'); }
    private static void TextList(StringBuilder text, string heading, IEnumerable<string> values)
    { Heading(text, heading); string[] canonical = values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(); if (canonical.Length == 0) text.Append("- Unavailable\n\n"); else foreach (string value in canonical) text.Append("- ").Append(Value(value)).Append('\n'); if (canonical.Length > 0) text.Append('\n'); }
    private static void Heading(StringBuilder text, string value) => text.Append("## ").Append(value).Append("\n\n");
    private static void Item(StringBuilder text, string label, string? value) => text.Append("- ").Append(label).Append(": ").Append(Value(value)).Append("\n");
    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static string Boolean(bool value) => value ? "true" : "false";
    private static string Format(ResearchReportMetricKind kind, double? value) => value is null ? "Unavailable" : kind switch
    {
        ResearchReportMetricKind.Count => value.Value.ToString("0", CultureInfo.InvariantCulture),
        ResearchReportMetricKind.Ratio => (value.Value * 100d).ToString("0.################", CultureInfo.InvariantCulture) + "%",
        _ => value.Value.ToString("0.################", CultureInfo.InvariantCulture) + "%"
    };
}
