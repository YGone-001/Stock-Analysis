using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

public interface IResearchExperimentReportBuilder
{
    ResearchExperimentReport BuildExperimentReport(PersistedResearchExperimentRecord experiment);
    ResearchExperimentLineageReport BuildLineageReport(PersistedResearchExperimentRecord experiment);
    ResearchExperimentComparisonReport BuildComparisonReport(ResearchExperimentComparisonResult comparison);
}

/// <summary>Builds report presentation contracts from already-persisted experiment and comparison facts only.</summary>
public sealed class ResearchExperimentReportBuilder : IResearchExperimentReportBuilder
{
    private static readonly IReadOnlyList<string> PlatformLimitations = Array.AsReadOnly(new[]
    {
        "Close based historical simulation only", "No intraday execution", "No leverage", "No live trading", "No market impact model", "No optimization"
    }.OrderBy(value => value, StringComparer.Ordinal).ToArray());

    public ResearchExperimentReport BuildExperimentReport(PersistedResearchExperimentRecord experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ResearchExperimentPerformanceSummary? performance = experiment.ExecutionSummary.Performance;
        IReadOnlyList<ResearchReportMetric> metrics = Metrics(performance);
        IReadOnlyList<ResearchReportWarning> warnings = Warnings(experiment, performance);
        ResearchReportIdentity identity = Identity(ResearchReportType.ExperimentSummary, new[] { experiment.ExperimentFingerprint }, SummaryFacts(experiment, metrics, warnings));
        ResearchReportLineage lineage = Lineage(experiment);
        return new ResearchExperimentReport(identity, experiment.ExperimentId, lineage, experiment.Definition.Identity.ExperimentVersion,
            experiment.Definition.StrategyIdentity.Mode, experiment.Definition.StrategyIdentity.Version, experiment.Definition.StrategyParameterFingerprint,
            experiment.Definition.PortfolioConfigurationFingerprint, experiment.Definition.AnalysisConfigurationFingerprint, experiment.Definition.BenchmarkConfiguration,
            metrics, warnings, PlatformLimitations);
    }

    public ResearchExperimentLineageReport BuildLineageReport(PersistedResearchExperimentRecord experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ResearchReportIdentity identity = Identity(ResearchReportType.ExperimentLineage, new[] { experiment.ExperimentFingerprint }, new (string Name, string? Value)[]
        {
            ("experiment", experiment.ExperimentFingerprint), ("dataset", experiment.Lineage.DatasetFingerprint), ("parameters", experiment.Lineage.ParameterFingerprint),
            ("artifact", experiment.Lineage.ArtifactFingerprint), ("artifact-version", experiment.ArtifactReference.ArtifactVersion), ("execution-status", experiment.ExecutionSummary.Status.ToString())
        });
        return new ResearchExperimentLineageReport(identity, experiment.ExperimentId, experiment.Lineage.ExperimentFingerprint, experiment.Lineage.DatasetFingerprint,
            experiment.Lineage.ParameterFingerprint, experiment.Lineage.ArtifactFingerprint, experiment.ArtifactReference.ArtifactVersion, experiment.ExecutionSummary.Status);
    }

    public ResearchExperimentComparisonReport BuildComparisonReport(ResearchExperimentComparisonResult comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        bool reversed = StringComparer.Ordinal.Compare(comparison.ExperimentA.ExperimentId, comparison.ExperimentB.ExperimentId) > 0;
        PersistedResearchExperimentRecord first = reversed ? comparison.ExperimentB : comparison.ExperimentA;
        PersistedResearchExperimentRecord second = reversed ? comparison.ExperimentA : comparison.ExperimentB;
        IReadOnlyList<ResearchReportComparisonMetric> metrics = new[]
        {
            Metric("Total Return", ResearchReportMetricKind.Percentage, comparison.TotalReturnPercent, reversed),
            Metric("Maximum Drawdown", ResearchReportMetricKind.Percentage, comparison.MaximumDrawdownPercent, reversed),
            Metric("Trade Count", ResearchReportMetricKind.Count, comparison.TradeCount, reversed),
            Metric("Win Rate", ResearchReportMetricKind.Ratio, comparison.WinRate, reversed)
        };
        IReadOnlyList<ResearchReportWarning> warnings = comparison.PerformanceComparisonAvailable ? Array.Empty<ResearchReportWarning>() :
            new[] { new ResearchReportWarning("Performance comparison unavailable") };
        ResearchReportIdentity identity = Identity(ResearchReportType.ExperimentComparison, new[] { first.ExperimentFingerprint, second.ExperimentFingerprint },
            ComparisonFacts(first, second, comparison, metrics, warnings));
        return new ResearchExperimentComparisonReport(identity, new ResearchReportExperimentIdentity(first.ExperimentId, first.ExperimentFingerprint),
            new ResearchReportExperimentIdentity(second.ExperimentId, second.ExperimentFingerprint), comparison.DatasetComparable, comparison.StrategyComparable,
            comparison.PortfolioComparable, comparison.PerformanceComparisonAvailable, metrics, warnings);
    }

    private static ResearchReportLineage Lineage(PersistedResearchExperimentRecord experiment) => new(experiment.Lineage.ExperimentFingerprint,
        experiment.Lineage.DatasetFingerprint, experiment.Lineage.ParameterFingerprint, experiment.Lineage.ArtifactFingerprint,
        experiment.ArtifactReference.ArtifactVersion, experiment.ExecutionSummary.Status);

    private static IReadOnlyList<ResearchReportMetric> Metrics(ResearchExperimentPerformanceSummary? performance) => new[]
    {
        new ResearchReportMetric("Total Return", ResearchReportMetricKind.Percentage, performance?.TotalReturnPercent),
        new ResearchReportMetric("Maximum Drawdown", ResearchReportMetricKind.Percentage, performance?.MaximumDrawdownPercent),
        new ResearchReportMetric("Trade Count", ResearchReportMetricKind.Count, performance?.TradeCount),
        new ResearchReportMetric("Win Rate", ResearchReportMetricKind.Ratio, performance?.WinRate)
    };

    private static IReadOnlyList<ResearchReportWarning> Warnings(PersistedResearchExperimentRecord experiment, ResearchExperimentPerformanceSummary? performance)
    {
        IEnumerable<string> facts = experiment.ExecutionSummary.Warnings;
        if (performance is null || performance.TotalReturnPercent is null || performance.MaximumDrawdownPercent is null || performance.TradeCount is null || performance.WinRate is null)
            facts = facts.Append("Performance metrics unavailable");
        if (string.IsNullOrWhiteSpace(experiment.Definition.BenchmarkConfiguration)) facts = facts.Append("Benchmark configuration unavailable");
        return facts.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).Select(value => new ResearchReportWarning(value)).ToArray();
    }

    private static ResearchReportComparisonMetric Metric(string name, ResearchReportMetricKind kind, ResearchExperimentMetricComparison metric, bool reversed) =>
        new(name, kind, reversed ? metric.ExperimentB : metric.ExperimentA, reversed ? metric.ExperimentA : metric.ExperimentB, metric.Available);

    private static ResearchReportIdentity Identity(ResearchReportType type, IReadOnlyList<string> fingerprints, IEnumerable<(string Name, string? Value)> facts)
    {
        List<(string Name, string? Value)> canonical = new() { ("report-version", ResearchReportVersion.Current), ("report-type", type.ToString()) };
        canonical.AddRange(fingerprints.Select((value, index) => (Name: $"experiment-{index}", Value: (string?)value)));
        canonical.AddRange(facts);
        return new ResearchReportIdentity(ResearchReportVersion.Current, type, fingerprints, ResearchReportFingerprint.Create(canonical));
    }

    private static IEnumerable<(string Name, string? Value)> SummaryFacts(PersistedResearchExperimentRecord experiment, IReadOnlyList<ResearchReportMetric> metrics,
        IReadOnlyList<ResearchReportWarning> warnings)
    {
        yield return ("dataset", experiment.Definition.DatasetFingerprint); yield return ("strategy-mode", experiment.Definition.StrategyIdentity.Mode);
        yield return ("strategy-version", experiment.Definition.StrategyIdentity.Version); yield return ("strategy-parameters", experiment.Definition.StrategyParameterFingerprint);
        yield return ("portfolio", experiment.Definition.PortfolioConfigurationFingerprint); yield return ("analysis", experiment.Definition.AnalysisConfigurationFingerprint);
        yield return ("benchmark", experiment.Definition.BenchmarkConfiguration); yield return ("execution-status", experiment.ExecutionSummary.Status.ToString());
        yield return ("artifact", experiment.ArtifactReference.ArtifactFingerprint); yield return ("artifact-version", experiment.ArtifactReference.ArtifactVersion);
        yield return ("parameters", experiment.Lineage.ParameterFingerprint);
        foreach (ResearchReportMetric metric in metrics) yield return ($"metric-{metric.Name}", ResearchReportFingerprint.MetricValue(metric.Value));
        foreach (ResearchReportWarning warning in warnings) yield return ("warning", warning.Message);
        foreach (string limitation in PlatformLimitations) yield return ("limitation", limitation);
    }

    private static IEnumerable<(string Name, string? Value)> ComparisonFacts(PersistedResearchExperimentRecord first, PersistedResearchExperimentRecord second,
        ResearchExperimentComparisonResult comparison, IReadOnlyList<ResearchReportComparisonMetric> metrics, IReadOnlyList<ResearchReportWarning> warnings)
    {
        yield return ("experiment-a", first.ExperimentFingerprint); yield return ("experiment-b", second.ExperimentFingerprint);
        yield return ("dataset-comparable", comparison.DatasetComparable.ToString()); yield return ("strategy-comparable", comparison.StrategyComparable.ToString());
        yield return ("portfolio-comparable", comparison.PortfolioComparable.ToString()); yield return ("performance-comparison-available", comparison.PerformanceComparisonAvailable.ToString());
        foreach (ResearchReportComparisonMetric metric in metrics)
        {
            yield return ($"metric-{metric.Name}-a", ResearchReportFingerprint.MetricValue(metric.ExperimentA));
            yield return ($"metric-{metric.Name}-b", ResearchReportFingerprint.MetricValue(metric.ExperimentB));
            yield return ($"metric-{metric.Name}-available", metric.Available.ToString());
        }
        foreach (ResearchReportWarning warning in warnings) yield return ("warning", warning.Message);
    }
}
