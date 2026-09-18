namespace AIHelper.Core.Sparrow;

public sealed class ResearchExperimentCreatedDateRange
{
    public ResearchExperimentCreatedDateRange(DateOnly startDate, DateOnly endDate)
    {
        if (startDate > endDate) throw new ArgumentException("Created date range is invalid.");
        StartDate = startDate;
        EndDate = endDate;
    }

    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }
    public bool Includes(DateTimeOffset timestamp)
    {
        DateOnly date = DateOnly.FromDateTime(timestamp.UtcDateTime);
        return date >= StartDate && date <= EndDate;
    }
}

/// <summary>All populated filters are combined with AND; null means that field is not filtered.</summary>
public sealed class ResearchExperimentQueryRequest
{
    public ResearchExperimentQueryRequest(string? experimentId = null, string? datasetFingerprint = null, ResearchExperimentStrategyIdentity? strategyIdentity = null,
        string? experimentFingerprint = null, ResearchExperimentCreatedDateRange? createdDateRange = null,
        ResearchExperimentExecutionStatus? status = null, string? benchmarkId = null)
    {
        ExperimentId = experimentId;
        DatasetFingerprint = datasetFingerprint;
        StrategyIdentity = strategyIdentity;
        ExperimentFingerprint = experimentFingerprint;
        CreatedDateRange = createdDateRange;
        Status = status;
        BenchmarkId = benchmarkId;
    }

    public string? ExperimentId { get; }
    public string? DatasetFingerprint { get; }
    public ResearchExperimentStrategyIdentity? StrategyIdentity { get; }
    public string? ExperimentFingerprint { get; }
    public ResearchExperimentCreatedDateRange? CreatedDateRange { get; }
    public ResearchExperimentExecutionStatus? Status { get; }
    public string? BenchmarkId { get; }
}

public sealed class ResearchExperimentQueryResult
{
    public ResearchExperimentQueryResult(IEnumerable<PersistedResearchExperimentRecord> experiments, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        Experiments = Array.AsReadOnly(experiments.OrderBy(item => item.ExperimentId, StringComparer.Ordinal).ToArray());
        MatchedCount = Experiments.Count;
        Warnings = Array.AsReadOnly((warnings ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    public int MatchedCount { get; }
    public IReadOnlyList<PersistedResearchExperimentRecord> Experiments { get; }
    public IReadOnlyList<string> Warnings { get; }
}

public interface IResearchExperimentQueryService
{
    Task<ResearchExperimentQueryResult> QueryAsync(ResearchExperimentQueryRequest request, CancellationToken cancellationToken = default);
    Task<ResearchExperimentQueryResult> FindByFingerprintAsync(string experimentFingerprint, CancellationToken cancellationToken = default);
    Task<ResearchExperimentQueryResult> FindByDatasetAsync(string datasetFingerprint, CancellationToken cancellationToken = default);
    Task<ResearchExperimentQueryResult> FindByStrategyAsync(ResearchExperimentStrategyIdentity strategyIdentity, CancellationToken cancellationToken = default);
}

/// <summary>Repository-backed local query service. It never reads files or mutates repository records directly.</summary>
public sealed class ResearchExperimentQueryService : IResearchExperimentQueryService
{
    private readonly IResearchExperimentRepository _repository;
    public ResearchExperimentQueryService(IResearchExperimentRepository repository) => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<ResearchExperimentQueryResult> QueryAsync(ResearchExperimentQueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ResearchExperimentHistory history = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<PersistedResearchExperimentRecord> query = history.Experiments;
        if (request.ExperimentId is not null) query = query.Where(item => string.Equals(item.ExperimentId, request.ExperimentId, StringComparison.Ordinal));
        if (request.DatasetFingerprint is not null) query = query.Where(item => string.Equals(item.Definition.DatasetFingerprint, request.DatasetFingerprint, StringComparison.Ordinal));
        if (request.ExperimentFingerprint is not null) query = query.Where(item => string.Equals(item.ExperimentFingerprint, request.ExperimentFingerprint, StringComparison.Ordinal));
        if (request.CreatedDateRange is not null) query = query.Where(item => request.CreatedDateRange.Includes(item.CreatedAt));
        if (request.Status is not null) query = query.Where(item => item.ExecutionSummary.Status == request.Status.Value);
        if (request.BenchmarkId is not null) query = query.Where(item => string.Equals(item.Definition.BenchmarkConfiguration, request.BenchmarkId, StringComparison.Ordinal));
        if (request.StrategyIdentity is not null)
            query = query.Where(item => string.Equals(item.Definition.StrategyIdentity.Mode, request.StrategyIdentity.Mode, StringComparison.Ordinal)
                && string.Equals(item.Definition.StrategyIdentity.Version, request.StrategyIdentity.Version, StringComparison.Ordinal));
        return new ResearchExperimentQueryResult(query);
    }

    public Task<ResearchExperimentQueryResult> FindByFingerprintAsync(string experimentFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        return QueryAsync(new ResearchExperimentQueryRequest(experimentFingerprint: experimentFingerprint), cancellationToken);
    }
    public Task<ResearchExperimentQueryResult> FindByDatasetAsync(string datasetFingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        return QueryAsync(new ResearchExperimentQueryRequest(datasetFingerprint: datasetFingerprint), cancellationToken);
    }
    public Task<ResearchExperimentQueryResult> FindByStrategyAsync(ResearchExperimentStrategyIdentity strategyIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(strategyIdentity);
        return QueryAsync(new ResearchExperimentQueryRequest(strategyIdentity: strategyIdentity), cancellationToken);
    }
}

/// <summary>Reads persisted factual values only; it never invokes a simulation, analyzer, or strategy evaluator.</summary>
public sealed class ResearchExperimentSummaryAnalyzer
{
    public ResearchExperimentSummary Analyze(PersistedResearchExperimentRecord experiment)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ResearchExperimentPerformanceSummary? performance = experiment.ExecutionSummary.Performance;
        return new ResearchExperimentSummary(experiment.ExperimentId, experiment.Definition.DatasetFingerprint, experiment.Definition.StrategyIdentity,
            experiment.Definition.PortfolioConfigurationFingerprint, performance?.TotalReturnPercent, performance?.MaximumDrawdownPercent,
            performance?.TradeCount, performance?.WinRate, !string.IsNullOrWhiteSpace(experiment.ArtifactReference.ArtifactFingerprint));
    }
}

public sealed class ResearchExperimentSummary
{
    public ResearchExperimentSummary(string experimentId, string datasetFingerprint, ResearchExperimentStrategyIdentity strategy, string portfolioConfiguration,
        double? totalReturn, double? maximumDrawdown, int? tradeCount, double? winRate, bool artifactAvailable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentException.ThrowIfNullOrWhiteSpace(portfolioConfiguration);
        ExperimentId = experimentId; DatasetFingerprint = datasetFingerprint; Strategy = strategy; PortfolioConfiguration = portfolioConfiguration;
        TotalReturn = totalReturn; MaximumDrawdown = maximumDrawdown; TradeCount = tradeCount; WinRate = winRate; ArtifactAvailable = artifactAvailable;
    }
    public string ExperimentId { get; }
    public string DatasetFingerprint { get; }
    public ResearchExperimentStrategyIdentity Strategy { get; }
    public string PortfolioConfiguration { get; }
    public double? TotalReturn { get; }
    public double? MaximumDrawdown { get; }
    public int? TradeCount { get; }
    public double? WinRate { get; }
    public bool ArtifactAvailable { get; }
}

/// <summary>Timestamp-independent view of persisted experiments, ordered only by ordinal ExperimentId.</summary>
public sealed class ResearchExperimentTimeline
{
    public ResearchExperimentTimeline(IEnumerable<PersistedResearchExperimentRecord> experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        Experiments = Array.AsReadOnly(experiments.OrderBy(item => item.ExperimentId, StringComparer.Ordinal).ToArray());
    }
    public IReadOnlyList<PersistedResearchExperimentRecord> Experiments { get; }
}

public sealed class ResearchExperimentComparableGroup
{
    public ResearchExperimentComparableGroup(string datasetFingerprint, ResearchExperimentStrategyIdentity strategy, IEnumerable<PersistedResearchExperimentRecord> experiments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint); ArgumentNullException.ThrowIfNull(strategy); ArgumentNullException.ThrowIfNull(experiments);
        DatasetFingerprint = datasetFingerprint; Strategy = strategy;
        Experiments = Array.AsReadOnly(experiments.OrderBy(item => item.ExperimentId, StringComparer.Ordinal).ToArray());
    }
    public string DatasetFingerprint { get; }
    public ResearchExperimentStrategyIdentity Strategy { get; }
    public IReadOnlyList<PersistedResearchExperimentRecord> Experiments { get; }
}

public sealed class ResearchExperimentMetricAvailability
{
    public ResearchExperimentMetricAvailability(bool totalReturn, bool maximumDrawdown, bool tradeCount, bool winRate)
    { TotalReturn = totalReturn; MaximumDrawdown = maximumDrawdown; TradeCount = tradeCount; WinRate = winRate; }
    public bool TotalReturn { get; } public bool MaximumDrawdown { get; } public bool TradeCount { get; } public bool WinRate { get; }
}

/// <summary>Neutral multi-experiment grouping; no score, ranking, winner, or recommendation is produced.</summary>
public sealed class ResearchExperimentBatchComparison
{
    public ResearchExperimentBatchComparison(IEnumerable<PersistedResearchExperimentRecord> experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        PersistedResearchExperimentRecord[] canonical = experiments.OrderBy(item => item.ExperimentId, StringComparer.Ordinal).ToArray();
        Experiments = Array.AsReadOnly(canonical);
        ComparableGroups = Array.AsReadOnly(canonical.GroupBy(item => new { item.Definition.DatasetFingerprint, item.Definition.StrategyIdentity.Mode, item.Definition.StrategyIdentity.Version, item.Definition.StrategyParameterFingerprint })
            .OrderBy(group => group.Key.DatasetFingerprint, StringComparer.Ordinal).ThenBy(group => group.Key.Mode, StringComparer.Ordinal).ThenBy(group => group.Key.Version, StringComparer.Ordinal)
            .Select(group => new ResearchExperimentComparableGroup(group.Key.DatasetFingerprint, new ResearchExperimentStrategyIdentity(group.Key.Mode, group.Key.Version), group)).ToArray());
        ResearchExperimentPerformanceSummary?[] metrics = canonical.Select(item => item.ExecutionSummary.Performance).ToArray();
        MetricAvailability = new ResearchExperimentMetricAvailability(metrics.All(item => item?.TotalReturnPercent is not null), metrics.All(item => item?.MaximumDrawdownPercent is not null),
            metrics.All(item => item?.TradeCount is not null), metrics.All(item => item?.WinRate is not null));
    }
    public IReadOnlyList<PersistedResearchExperimentRecord> Experiments { get; }
    public IReadOnlyList<ResearchExperimentComparableGroup> ComparableGroups { get; }
    public ResearchExperimentMetricAvailability MetricAvailability { get; }
}
