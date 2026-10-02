using System.Security.Cryptography;
using System.Text;

namespace AIHelper.Core.Sparrow;

/// <summary>Stable contract version for local research report presentation artifacts.</summary>
public static class ResearchReportVersion
{
    public const string Current = "research-report-v1";
}

public enum ResearchReportType { ExperimentSummary, ExperimentComparison, ExperimentLineage }
public enum ResearchReportMetricKind { Percentage, Ratio, Count }

public interface IResearchExperimentReportDocument
{
    ResearchReportIdentity Identity { get; }
}

/// <summary>Semantic identity for a report; it deliberately excludes generated time and output location.</summary>
public sealed class ResearchReportIdentity
{
    public ResearchReportIdentity(string reportVersion, ResearchReportType reportType, IEnumerable<string> experimentFingerprints, string reportFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportFingerprint);
        if (!Enum.IsDefined(reportType)) throw new ArgumentOutOfRangeException(nameof(reportType));
        ReportVersion = reportVersion;
        ReportType = reportType;
        ExperimentFingerprints = Array.AsReadOnly((experimentFingerprints ?? throw new ArgumentNullException(nameof(experimentFingerprints))).ToArray());
        if (ExperimentFingerprints.Count == 0 || ExperimentFingerprints.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A report identity requires experiment fingerprints.", nameof(experimentFingerprints));
        ReportFingerprint = reportFingerprint;
    }

    public string ReportVersion { get; }
    public ResearchReportType ReportType { get; }
    public IReadOnlyList<string> ExperimentFingerprints { get; }
    public string ReportFingerprint { get; }
}

public sealed class ResearchReportMetric
{
    public ResearchReportMetric(string name, ResearchReportMetricKind kind, double? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (value.HasValue && !double.IsFinite(value.Value)) throw new ArgumentOutOfRangeException(nameof(value));
        Name = name; Kind = kind; Value = value;
    }
    public string Name { get; }
    public ResearchReportMetricKind Kind { get; }
    public double? Value { get; }
}

public sealed class ResearchReportComparisonMetric
{
    public ResearchReportComparisonMetric(string name, ResearchReportMetricKind kind, double? experimentA, double? experimentB, bool available)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (available && (!experimentA.HasValue || !experimentB.HasValue)) throw new ArgumentException("Available comparison metrics require both persisted values.");
        Name = name; Kind = kind; ExperimentA = experimentA; ExperimentB = experimentB; Available = available;
    }
    public string Name { get; }
    public ResearchReportMetricKind Kind { get; }
    public double? ExperimentA { get; }
    public double? ExperimentB { get; }
    public bool Available { get; }
}

public sealed class ResearchReportWarning
{
    public ResearchReportWarning(string message) { ArgumentException.ThrowIfNullOrWhiteSpace(message); Message = message; }
    public string Message { get; }
}

public sealed class ResearchReportSection
{
    public ResearchReportSection(string name) { ArgumentException.ThrowIfNullOrWhiteSpace(name); Name = name; }
    public string Name { get; }
}

public sealed class ResearchExperimentLineageReport : IResearchExperimentReportDocument
{
    public ResearchExperimentLineageReport(ResearchReportIdentity identity, string experimentId, string experimentFingerprint, string datasetFingerprint,
        string parameterFingerprint, string artifactFingerprint, string artifactVersion, ResearchExperimentExecutionStatus executionStatus)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId); ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint); ArgumentException.ThrowIfNullOrWhiteSpace(parameterFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint); ArgumentException.ThrowIfNullOrWhiteSpace(artifactVersion);
        if (identity.ReportType != ResearchReportType.ExperimentLineage) throw new ArgumentException("Report identity type is invalid.", nameof(identity));
        Identity = identity; ExperimentId = experimentId; ExperimentFingerprint = experimentFingerprint; DatasetFingerprint = datasetFingerprint;
        ParameterFingerprint = parameterFingerprint; ArtifactFingerprint = artifactFingerprint; ArtifactVersion = artifactVersion; ExecutionStatus = executionStatus;
    }
    public ResearchReportIdentity Identity { get; }
    public string ExperimentId { get; }
    public string ExperimentFingerprint { get; }
    public string DatasetFingerprint { get; }
    public string ParameterFingerprint { get; }
    public string ArtifactFingerprint { get; }
    public string ArtifactVersion { get; }
    public ResearchExperimentExecutionStatus ExecutionStatus { get; }
}

public sealed class ResearchReportLineage
{
    public ResearchReportLineage(string experimentFingerprint, string datasetFingerprint, string parameterFingerprint, string artifactFingerprint, string artifactVersion,
        ResearchExperimentExecutionStatus executionStatus)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint); ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterFingerprint); ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint); ArgumentException.ThrowIfNullOrWhiteSpace(artifactVersion);
        ExperimentFingerprint = experimentFingerprint; DatasetFingerprint = datasetFingerprint; ParameterFingerprint = parameterFingerprint;
        ArtifactFingerprint = artifactFingerprint; ArtifactVersion = artifactVersion; ExecutionStatus = executionStatus;
    }
    public string ExperimentFingerprint { get; }
    public string DatasetFingerprint { get; }
    public string ParameterFingerprint { get; }
    public string ArtifactFingerprint { get; }
    public string ArtifactVersion { get; }
    public ResearchExperimentExecutionStatus ExecutionStatus { get; }
}

public sealed class ResearchExperimentReport : IResearchExperimentReportDocument
{
    public ResearchExperimentReport(ResearchReportIdentity identity, string experimentId, ResearchReportLineage lineage, string experimentVersion, string strategyMode,
        string strategyVersion, string strategyParameterFingerprint, string portfolioConfigurationFingerprint, string analysisConfigurationFingerprint,
        string? benchmarkConfiguration, IReadOnlyList<ResearchReportMetric> performance, IEnumerable<ResearchReportWarning>? warnings, IEnumerable<string>? limitations)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentException.ThrowIfNullOrWhiteSpace(experimentId); ArgumentNullException.ThrowIfNull(lineage);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentVersion); ArgumentException.ThrowIfNullOrWhiteSpace(strategyMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyVersion); ArgumentException.ThrowIfNullOrWhiteSpace(strategyParameterFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(portfolioConfigurationFingerprint); ArgumentException.ThrowIfNullOrWhiteSpace(analysisConfigurationFingerprint);
        ArgumentNullException.ThrowIfNull(performance);
        if (identity.ReportType != ResearchReportType.ExperimentSummary) throw new ArgumentException("Report identity type is invalid.", nameof(identity));
        Identity = identity; ExperimentId = experimentId; Lineage = lineage; ExperimentVersion = experimentVersion; StrategyMode = strategyMode; StrategyVersion = strategyVersion;
        StrategyParameterFingerprint = strategyParameterFingerprint; PortfolioConfigurationFingerprint = portfolioConfigurationFingerprint;
        AnalysisConfigurationFingerprint = analysisConfigurationFingerprint; BenchmarkConfiguration = benchmarkConfiguration;
        Performance = Array.AsReadOnly(performance.ToArray());
        Warnings = CanonicalWarnings(warnings); Limitations = CanonicalText(limitations);
    }
    public ResearchReportIdentity Identity { get; }
    public string ExperimentId { get; }
    public ResearchReportLineage Lineage { get; }
    public string ExperimentVersion { get; }
    public string StrategyMode { get; }
    public string StrategyVersion { get; }
    public string StrategyParameterFingerprint { get; }
    public string PortfolioConfigurationFingerprint { get; }
    public string AnalysisConfigurationFingerprint { get; }
    public string? BenchmarkConfiguration { get; }
    public IReadOnlyList<ResearchReportMetric> Performance { get; }
    public IReadOnlyList<ResearchReportWarning> Warnings { get; }
    public IReadOnlyList<string> Limitations { get; }
    private static IReadOnlyList<ResearchReportWarning> CanonicalWarnings(IEnumerable<ResearchReportWarning>? values) => Array.AsReadOnly((values ?? Array.Empty<ResearchReportWarning>())
        .GroupBy(value => value.Message, StringComparer.Ordinal).Select(group => group.First()).OrderBy(value => value.Message, StringComparer.Ordinal).ToArray());
    private static IReadOnlyList<string> CanonicalText(IEnumerable<string>? values) => Array.AsReadOnly((values ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
}

public sealed class ResearchReportExperimentIdentity
{
    public ResearchReportExperimentIdentity(string experimentId, string experimentFingerprint)
    { ArgumentException.ThrowIfNullOrWhiteSpace(experimentId); ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint); ExperimentId = experimentId; ExperimentFingerprint = experimentFingerprint; }
    public string ExperimentId { get; }
    public string ExperimentFingerprint { get; }
}

public sealed class ResearchExperimentComparisonReport : IResearchExperimentReportDocument
{
    public ResearchExperimentComparisonReport(ResearchReportIdentity identity, ResearchReportExperimentIdentity experimentA, ResearchReportExperimentIdentity experimentB,
        bool datasetComparable, bool strategyComparable, bool portfolioComparable, bool performanceComparisonAvailable,
        IReadOnlyList<ResearchReportComparisonMetric> metrics, IEnumerable<ResearchReportWarning>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(experimentA); ArgumentNullException.ThrowIfNull(experimentB); ArgumentNullException.ThrowIfNull(metrics);
        if (identity.ReportType != ResearchReportType.ExperimentComparison) throw new ArgumentException("Report identity type is invalid.", nameof(identity));
        Identity = identity; ExperimentA = experimentA; ExperimentB = experimentB; DatasetComparable = datasetComparable; StrategyComparable = strategyComparable;
        PortfolioComparable = portfolioComparable; PerformanceComparisonAvailable = performanceComparisonAvailable; Metrics = Array.AsReadOnly(metrics.ToArray());
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<ResearchReportWarning>()).GroupBy(value => value.Message, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(value => value.Message, StringComparer.Ordinal).ToArray());
    }
    public ResearchReportIdentity Identity { get; }
    public ResearchReportExperimentIdentity ExperimentA { get; }
    public ResearchReportExperimentIdentity ExperimentB { get; }
    public bool DatasetComparable { get; }
    public bool StrategyComparable { get; }
    public bool PortfolioComparable { get; }
    public bool PerformanceComparisonAvailable { get; }
    public IReadOnlyList<ResearchReportComparisonMetric> Metrics { get; }
    public IReadOnlyList<ResearchReportWarning> Warnings { get; }
}

/// <summary>SHA-256 identity helper for canonical report semantic facts only.</summary>
public static class ResearchReportFingerprint
{
    public static string Create(IEnumerable<(string Name, string? Value)> facts) => Hash(string.Join('\n', facts.Select(fact => Row(fact.Name, fact.Value))));
    public static string MetricValue(double? value) => value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "<unavailable>";
    private static string Row(string name, string? value) => string.Join('\u001f', Convert.ToBase64String(Encoding.UTF8.GetBytes(name)), Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "<null>")));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
