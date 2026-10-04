using System.Globalization;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

/// <summary>
/// Immutable request for one authoritative managed research experiment execution.
/// <para>
/// Paths are operational locators only. They never enter an experiment, parameter, binding, provenance or artifact fingerprint.
/// </para>
/// </summary>
public sealed class ManagedResearchExperimentRequest
{
    public ManagedResearchExperimentRequest(
        string experimentId,
        string datasetPath,
        string parameterSnapshotPath,
        SparrowStrategyMode strategyMode,
        DateOnly startDate,
        DateOnly endDate,
        int topN,
        int horizonTradingDays,
        double backtestRoundTripCostRate,
        double backtestSlippageRate,
        decimal initialCapital,
        PortfolioPositionSizingMethod positionSizingMethod,
        decimal commissionRate,
        decimal portfolioSlippageRate,
        PortfolioExecutionModel executionModel,
        string artifactOutputPath,
        string experimentStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterSnapshotPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactOutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentStore);

        if (!string.Equals(Path.GetFileName(experimentId), experimentId, StringComparison.Ordinal)
            || experimentId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("ExperimentId must be a safe local filename.", nameof(experimentId));
        if (endDate < startDate)
            throw new ArgumentException("End date must not precede start date.", nameof(endDate));
        if (topN <= 0) throw new ArgumentOutOfRangeException(nameof(topN));
        if (horizonTradingDays <= 0) throw new ArgumentOutOfRangeException(nameof(horizonTradingDays));
        if (!double.IsFinite(backtestRoundTripCostRate) || backtestRoundTripCostRate < 0) throw new ArgumentOutOfRangeException(nameof(backtestRoundTripCostRate));
        if (!double.IsFinite(backtestSlippageRate) || backtestSlippageRate < 0) throw new ArgumentOutOfRangeException(nameof(backtestSlippageRate));
        if (initialCapital <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapital));
        if (commissionRate < 0) throw new ArgumentOutOfRangeException(nameof(commissionRate));
        if (portfolioSlippageRate < 0) throw new ArgumentOutOfRangeException(nameof(portfolioSlippageRate));

        ExperimentId = experimentId;
        DatasetPath = datasetPath;
        ParameterSnapshotPath = parameterSnapshotPath;
        StrategyMode = strategyMode;
        StartDate = startDate;
        EndDate = endDate;
        TopN = topN;
        HorizonTradingDays = horizonTradingDays;
        BacktestRoundTripCostRate = backtestRoundTripCostRate;
        BacktestSlippageRate = backtestSlippageRate;
        InitialCapital = initialCapital;
        PositionSizingMethod = positionSizingMethod;
        CommissionRate = commissionRate;
        PortfolioSlippageRate = portfolioSlippageRate;
        ExecutionModel = executionModel;
        ArtifactOutputPath = artifactOutputPath;
        ExperimentStore = experimentStore;
    }

    public string ExperimentId { get; }
    public string DatasetPath { get; }
    public string ParameterSnapshotPath { get; }
    public SparrowStrategyMode StrategyMode { get; }
    public DateOnly StartDate { get; }
    public DateOnly EndDate { get; }
    public int TopN { get; }
    public int HorizonTradingDays { get; }
    public double BacktestRoundTripCostRate { get; }
    public double BacktestSlippageRate { get; }
    public decimal InitialCapital { get; }
    public PortfolioPositionSizingMethod PositionSizingMethod { get; }
    public decimal CommissionRate { get; }
    public decimal PortfolioSlippageRate { get; }
    public PortfolioExecutionModel ExecutionModel { get; }
    public string ArtifactOutputPath { get; }
    public string ExperimentStore { get; }

    /// <summary>Stable single-line description used by diagnostics; contains no absolute-path-dependent identity.</summary>
    public string Describe() => string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3:yyyy-MM-dd}|{4:yyyy-MM-dd}|{5}|{6}",
        ExperimentId, StrategyMode, StrategyMode == SparrowStrategyMode.V2 ? SparrowStrategyVersions.V2 : SparrowStrategyVersions.Classic,
        StartDate, EndDate, TopN, HorizonTradingDays);
}

/// <summary>Semantic identity facts produced by a committed managed research experiment. Paths are locators, not identity.</summary>
public sealed class ManagedResearchExperimentResult
{
    public ManagedResearchExperimentResult(
        string experimentId,
        string experimentFingerprint,
        string datasetFingerprint,
        string strategyParameterFingerprint,
        string portfolioConfigurationFingerprint,
        string analysisFingerprint,
        string artifactFingerprint,
        string sourceBuildProvenanceFingerprint,
        string executionEnvironmentProvenanceFingerprint,
        string executionBindingFingerprint,
        string artifactVersion,
        string experimentRecordSchemaVersion,
        string artifactOutputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyParameterFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(portfolioConfigurationFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(analysisFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBuildProvenanceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionEnvironmentProvenanceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionBindingFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentRecordSchemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactOutputPath);

        ExperimentId = experimentId;
        ExperimentFingerprint = experimentFingerprint;
        DatasetFingerprint = datasetFingerprint;
        StrategyParameterFingerprint = strategyParameterFingerprint;
        PortfolioConfigurationFingerprint = portfolioConfigurationFingerprint;
        AnalysisFingerprint = analysisFingerprint;
        ArtifactFingerprint = artifactFingerprint;
        SourceBuildProvenanceFingerprint = sourceBuildProvenanceFingerprint;
        ExecutionEnvironmentProvenanceFingerprint = executionEnvironmentProvenanceFingerprint;
        ExecutionBindingFingerprint = executionBindingFingerprint;
        ArtifactVersion = artifactVersion;
        ExperimentRecordSchemaVersion = experimentRecordSchemaVersion;
        ArtifactOutputPath = artifactOutputPath;
    }

    public string ExperimentId { get; }
    public string ExperimentFingerprint { get; }
    public string DatasetFingerprint { get; }
    /// <summary>Artifact-side strategy parameter fingerprint. It is deliberately not the experiment parameter-snapshot fingerprint.</summary>
    public string StrategyParameterFingerprint { get; }
    public string PortfolioConfigurationFingerprint { get; }
    public string AnalysisFingerprint { get; }
    public string ArtifactFingerprint { get; }
    public string SourceBuildProvenanceFingerprint { get; }
    /// <summary>Observed execution-environment and dependency-graph identity bound into the execution binding.</summary>
    public string ExecutionEnvironmentProvenanceFingerprint { get; }
    public string ExecutionBindingFingerprint { get; }
    public string ArtifactVersion { get; }
    public string ExperimentRecordSchemaVersion { get; }
    public string ArtifactOutputPath { get; }
}

/// <summary>Runs exactly one explicitly supplied research configuration and commits its authoritative evidence.</summary>
public interface IManagedResearchExperimentRunner
{
    Task<ManagedResearchExperimentResult> RunAsync(ManagedResearchExperimentRequest request, CancellationToken cancellationToken = default);
}
