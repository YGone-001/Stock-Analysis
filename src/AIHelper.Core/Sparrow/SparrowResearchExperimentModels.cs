using System.Security.Cryptography;
using System.Text;

namespace AIHelper.Core.Sparrow;

/// <summary>Immutable metadata identity for a locally managed research experiment.</summary>
public sealed class ResearchExperimentIdentity
{
    public const string CurrentExperimentVersion = "research-experiment-v1";

    public ResearchExperimentIdentity(string experimentId, string experimentVersion, string createdBy, DateTimeOffset? createdAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        ExperimentId = experimentId;
        ExperimentVersion = experimentVersion;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        SemanticFingerprint = ResearchExperimentFingerprint.Identity(experimentId, experimentVersion);
    }

    public string ExperimentId { get; }
    public string ExperimentVersion { get; }
    public string CreatedBy { get; }
    /// <summary>Diagnostic metadata only; it never participates in semantic identity.</summary>
    public DateTimeOffset? CreatedAt { get; }
    public string SemanticFingerprint { get; }
}

/// <summary>Stable strategy identity without any evaluator or provider dependency.</summary>
public sealed class ResearchExperimentStrategyIdentity
{
    public ResearchExperimentStrategyIdentity(string mode, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Mode = mode;
        Version = version;
    }

    public string Mode { get; }
    public string Version { get; }
}

/// <summary>Frozen, canonical parameter values that are sufficient to reproduce a local research execution.</summary>
public sealed class ExperimentParameterSnapshot
{
    public ExperimentParameterSnapshot(
        string parameterVersion,
        IReadOnlyDictionary<string, string> strategyParameters,
        IReadOnlyDictionary<string, string> portfolioParameters,
        IReadOnlyDictionary<string, string> analysisParameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterVersion);
        ParameterVersion = parameterVersion;
        StrategyParameters = CanonicalParameters(strategyParameters, nameof(strategyParameters));
        PortfolioParameters = CanonicalParameters(portfolioParameters, nameof(portfolioParameters));
        AnalysisParameters = CanonicalParameters(analysisParameters, nameof(analysisParameters));
        StrategyFingerprint = ResearchExperimentFingerprint.Parameters(parameterVersion, "strategy", StrategyParameters);
        PortfolioFingerprint = ResearchExperimentFingerprint.Parameters(parameterVersion, "portfolio", PortfolioParameters);
        AnalysisFingerprint = ResearchExperimentFingerprint.Parameters(parameterVersion, "analysis", AnalysisParameters);
        Fingerprint = ResearchExperimentFingerprint.ParameterSnapshot(parameterVersion, StrategyParameters, PortfolioParameters, AnalysisParameters);
    }

    public string ParameterVersion { get; }
    public IReadOnlyDictionary<string, string> StrategyParameters { get; }
    public IReadOnlyDictionary<string, string> PortfolioParameters { get; }
    public IReadOnlyDictionary<string, string> AnalysisParameters { get; }
    public string StrategyFingerprint { get; }
    public string PortfolioFingerprint { get; }
    public string AnalysisFingerprint { get; }
    public string Fingerprint { get; }

    private static IReadOnlyDictionary<string, string> CanonicalParameters(IReadOnlyDictionary<string, string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
            throw new ArgumentException("Parameter keys must be non-empty and values must not be null.", parameterName);
        return new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
    }
}

/// <summary>Immutable execution definition. Its semantic fingerprint deliberately excludes operator and timestamp metadata.</summary>
public sealed class ResearchExperimentDefinition
{
    public ResearchExperimentDefinition(
        ResearchExperimentIdentity identity,
        string datasetFingerprint,
        ResearchExperimentStrategyIdentity strategyIdentity,
        ExperimentParameterSnapshot parameters,
        string portfolioConfigurationFingerprint,
        string analysisConfigurationFingerprint,
        string? benchmarkConfiguration = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentNullException.ThrowIfNull(strategyIdentity);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(portfolioConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisConfigurationFingerprint);

        Identity = identity;
        DatasetFingerprint = datasetFingerprint;
        StrategyIdentity = strategyIdentity;
        Parameters = parameters;
        StrategyParameterFingerprint = parameters.StrategyFingerprint;
        PortfolioConfigurationFingerprint = portfolioConfigurationFingerprint;
        AnalysisConfigurationFingerprint = analysisConfigurationFingerprint;
        BenchmarkConfiguration = benchmarkConfiguration;
        Description = description;
        SemanticFingerprint = ResearchExperimentFingerprint.Definition(this);
    }

    public ResearchExperimentIdentity Identity { get; }
    public string DatasetFingerprint { get; }
    public ResearchExperimentStrategyIdentity StrategyIdentity { get; }
    public ExperimentParameterSnapshot Parameters { get; }
    public string StrategyParameterFingerprint { get; }
    public string PortfolioConfigurationFingerprint { get; }
    public string AnalysisConfigurationFingerprint { get; }
    public string? BenchmarkConfiguration { get; }
    /// <summary>Human-readable metadata only; it never changes what is executed.</summary>
    public string? Description { get; }
    public string SemanticFingerprint { get; }
}

public enum ResearchExperimentExecutionStatus { Created, Running, Completed, Failed }

/// <summary>Immutable lifecycle record. Transition methods reject invalid experiment state changes.</summary>
public sealed class ResearchExperimentExecution
{
    private ResearchExperimentExecution(ResearchExperimentIdentity experimentIdentity, DateTimeOffset? startedAt, DateTimeOffset? completedAt,
        ResearchExperimentExecutionStatus status, string? artifactFingerprint, IEnumerable<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(experimentIdentity);
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (status == ResearchExperimentExecutionStatus.Created && (startedAt is not null || completedAt is not null || artifactFingerprint is not null))
            throw new ArgumentException("Created execution cannot contain run facts.");
        if (status == ResearchExperimentExecutionStatus.Running && (startedAt is null || completedAt is not null || artifactFingerprint is not null))
            throw new ArgumentException("Running execution requires only StartedAt.");
        if (status == ResearchExperimentExecutionStatus.Completed && (startedAt is null || completedAt is null || string.IsNullOrWhiteSpace(artifactFingerprint)))
            throw new ArgumentException("Completed execution requires timestamps and an artifact fingerprint.");
        if (status == ResearchExperimentExecutionStatus.Failed && (startedAt is null || completedAt is null || artifactFingerprint is not null))
            throw new ArgumentException("Failed execution requires timestamps and no artifact fingerprint.");
        if (startedAt is not null && completedAt is not null && completedAt < startedAt)
            throw new ArgumentException("CompletedAt must not precede StartedAt.");

        ExperimentIdentity = experimentIdentity;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Status = status;
        ArtifactFingerprint = artifactFingerprint;
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public ResearchExperimentIdentity ExperimentIdentity { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public ResearchExperimentExecutionStatus Status { get; }
    public string? ArtifactFingerprint { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static ResearchExperimentExecution Create(ResearchExperimentIdentity identity) =>
        new(identity, null, null, ResearchExperimentExecutionStatus.Created, null, Array.Empty<string>());

    public ResearchExperimentExecution Start(DateTimeOffset startedAt) => Status == ResearchExperimentExecutionStatus.Created
        ? new(ExperimentIdentity, startedAt, null, ResearchExperimentExecutionStatus.Running, null, Warnings)
        : throw new InvalidOperationException("Only a created execution may start.");

    public ResearchExperimentExecution Complete(DateTimeOffset completedAt, string artifactFingerprint, IEnumerable<string>? warnings = null) => Status == ResearchExperimentExecutionStatus.Running
        ? new(ExperimentIdentity, StartedAt, completedAt, ResearchExperimentExecutionStatus.Completed, artifactFingerprint, warnings ?? Warnings)
        : throw new InvalidOperationException("Only a running execution may complete.");

    public ResearchExperimentExecution Fail(DateTimeOffset completedAt, IEnumerable<string>? warnings = null) => Status == ResearchExperimentExecutionStatus.Running
        ? new(ExperimentIdentity, StartedAt, completedAt, ResearchExperimentExecutionStatus.Failed, null, warnings ?? Warnings)
        : throw new InvalidOperationException("Only a running execution may fail.");
}

/// <summary>Immutable provenance bridge from a managed experiment to its immutable research artifact.</summary>
public sealed class ResearchArtifactLineage
{
    public ResearchArtifactLineage(string experimentFingerprint, string datasetFingerprint, string parameterFingerprint, string artifactFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint);
        ExperimentFingerprint = experimentFingerprint;
        DatasetFingerprint = datasetFingerprint;
        ParameterFingerprint = parameterFingerprint;
        ArtifactFingerprint = artifactFingerprint;
    }

    public string ExperimentFingerprint { get; }
    public string DatasetFingerprint { get; }
    public string ParameterFingerprint { get; }
    public string ArtifactFingerprint { get; }
}

/// <summary>Reference-only artifact identity; no filesystem path is retained in experiment semantics.</summary>
public sealed class ResearchResultArtifactReference
{
    public ResearchResultArtifactReference(string artifactFingerprint, string artifactVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactVersion);
        ArtifactFingerprint = artifactFingerprint;
        ArtifactVersion = artifactVersion;
    }

    public string ArtifactFingerprint { get; }
    public string ArtifactVersion { get; }
}

public sealed class ResearchExperimentResult
{
    public ResearchExperimentResult(ResearchExperimentDefinition experimentDefinition, ResearchExperimentExecution execution,
        ResearchArtifactLineage artifactLineage, ResearchResultArtifactReference resultArtifactReference,
        IEnumerable<string>? warnings = null, IEnumerable<string>? limitations = null)
    {
        ArgumentNullException.ThrowIfNull(experimentDefinition);
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(artifactLineage);
        ArgumentNullException.ThrowIfNull(resultArtifactReference);
        if (execution.Status != ResearchExperimentExecutionStatus.Completed)
            throw new ArgumentException("Experiment result requires a completed execution.", nameof(execution));
        if (!string.Equals(experimentDefinition.Identity.SemanticFingerprint, execution.ExperimentIdentity.SemanticFingerprint, StringComparison.Ordinal)
            || !string.Equals(experimentDefinition.SemanticFingerprint, artifactLineage.ExperimentFingerprint, StringComparison.Ordinal)
            || !string.Equals(experimentDefinition.DatasetFingerprint, artifactLineage.DatasetFingerprint, StringComparison.Ordinal)
            || !string.Equals(experimentDefinition.Parameters.Fingerprint, artifactLineage.ParameterFingerprint, StringComparison.Ordinal)
            || !string.Equals(execution.ArtifactFingerprint, artifactLineage.ArtifactFingerprint, StringComparison.Ordinal)
            || !string.Equals(execution.ArtifactFingerprint, resultArtifactReference.ArtifactFingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Experiment result provenance is inconsistent.");

        ExperimentDefinition = experimentDefinition;
        Execution = execution;
        ArtifactLineage = artifactLineage;
        ResultArtifactReference = resultArtifactReference;
        Warnings = CanonicalText(warnings);
        Limitations = CanonicalText(limitations);
    }

    public ResearchExperimentDefinition ExperimentDefinition { get; }
    public ResearchExperimentExecution Execution { get; }
    public ResearchArtifactLineage ArtifactLineage { get; }
    public ResearchResultArtifactReference ResultArtifactReference { get; }
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<string> Limitations { get; }

    private static IReadOnlyList<string> CanonicalText(IEnumerable<string>? values) => Array.AsReadOnly((values ?? Array.Empty<string>())
        .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
}

public interface IResearchExperimentRegistry
{
    void RegisterExperiment(ResearchExperimentDefinition experiment);
    ResearchExperimentDefinition? GetExperiment(string experimentId);
    IReadOnlyList<ResearchExperimentDefinition> ListExperiments();
}

/// <summary>Process-local registry only. It deliberately has no database, file, cloud, or UI dependency.</summary>
public sealed class InMemoryResearchExperimentRegistry : IResearchExperimentRegistry
{
    private readonly Dictionary<string, ResearchExperimentDefinition> _experiments = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void RegisterExperiment(ResearchExperimentDefinition experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        lock (_gate)
        {
            if (!_experiments.TryAdd(experiment.Identity.ExperimentId, experiment))
                throw new InvalidOperationException($"Experiment '{experiment.Identity.ExperimentId}' is already registered.");
        }
    }

    public ResearchExperimentDefinition? GetExperiment(string experimentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        lock (_gate) return _experiments.GetValueOrDefault(experimentId);
    }

    public IReadOnlyList<ResearchExperimentDefinition> ListExperiments()
    {
        lock (_gate) return Array.AsReadOnly(_experiments.Values.OrderBy(value => value.Identity.ExperimentId, StringComparer.Ordinal).ToArray());
    }
}

/// <summary>Comparison metadata only. It never ranks experiments or selects a winner.</summary>
public sealed class ResearchExperimentComparison
{
    public ResearchExperimentComparison(ResearchExperimentDefinition experimentA, ResearchExperimentDefinition experimentB, bool performanceComparisonAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(experimentA);
        ArgumentNullException.ThrowIfNull(experimentB);
        ExperimentA = experimentA;
        ExperimentB = experimentB;
        DatasetComparable = string.Equals(experimentA.DatasetFingerprint, experimentB.DatasetFingerprint, StringComparison.Ordinal);
        StrategyComparable = experimentA.StrategyIdentity.Mode == experimentB.StrategyIdentity.Mode
            && experimentA.StrategyIdentity.Version == experimentB.StrategyIdentity.Version
            && experimentA.StrategyParameterFingerprint == experimentB.StrategyParameterFingerprint;
        PortfolioComparable = string.Equals(experimentA.PortfolioConfigurationFingerprint, experimentB.PortfolioConfigurationFingerprint, StringComparison.Ordinal);
        if (performanceComparisonAvailable && !DatasetComparable)
            throw new ArgumentException("Performance comparison requires the same dataset fingerprint.", nameof(performanceComparisonAvailable));
        PerformanceComparisonAvailable = performanceComparisonAvailable;
    }

    public ResearchExperimentDefinition ExperimentA { get; }
    public ResearchExperimentDefinition ExperimentB { get; }
    public bool DatasetComparable { get; }
    public bool StrategyComparable { get; }
    public bool PortfolioComparable { get; }
    public bool PerformanceComparisonAvailable { get; }
}

public static class ResearchExperimentFingerprint
{
    public static string Identity(string experimentId, string experimentVersion) => Hash(Row("identity", experimentId, experimentVersion));

    public static string Parameters(string parameterVersion, string section, IReadOnlyDictionary<string, string> values) =>
        Hash(string.Join('\n', new[] { Row("parameter-version", parameterVersion), Row("section", section) }.Concat(ParameterRows(values))));

    public static string ParameterSnapshot(string parameterVersion, IReadOnlyDictionary<string, string> strategy, IReadOnlyDictionary<string, string> portfolio,
        IReadOnlyDictionary<string, string> analysis) => Hash(string.Join('\n', new[] { Row("parameter-version", parameterVersion) }
            .Concat(SectionRows("strategy", strategy)).Concat(SectionRows("portfolio", portfolio)).Concat(SectionRows("analysis", analysis))));

    public static string Definition(ResearchExperimentDefinition definition) => Hash(string.Join('\n', new[]
    {
        Row("experiment", definition.Identity.SemanticFingerprint),
        Row("dataset", definition.DatasetFingerprint),
        Row("strategy", definition.StrategyIdentity.Mode, definition.StrategyIdentity.Version, definition.StrategyParameterFingerprint),
        Row("parameters", definition.Parameters.Fingerprint),
        Row("portfolio-configuration", definition.PortfolioConfigurationFingerprint),
        Row("analysis-configuration", definition.AnalysisConfigurationFingerprint),
        Row("benchmark-configuration", definition.BenchmarkConfiguration)
    }));

    private static IEnumerable<string> SectionRows(string section, IReadOnlyDictionary<string, string> values) =>
        new[] { Row("section", section) }.Concat(ParameterRows(values));
    private static IEnumerable<string> ParameterRows(IReadOnlyDictionary<string, string> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => Row("parameter", pair.Key, pair.Value));
    private static string Row(params string?[] values) => string.Join('\u001f', values.Select(value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? "<null>"))));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
