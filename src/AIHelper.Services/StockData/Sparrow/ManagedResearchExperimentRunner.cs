using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Publishes managed artifacts and can remove one created by the current attempt.</summary>
public interface IManagedResearchArtifactWriter
{
    /// <summary>Atomically publishes a new artifact. The destination must not already exist.</summary>
    Task PublishNewAsync(string artifactJson, string outputPath, CancellationToken cancellationToken = default);

    /// <summary>Best-effort removal of an artifact this managed attempt created.</summary>
    void TryDeleteOwnedArtifact(string outputPath);
}

/// <summary>Default writer over the established artifact exporter's atomic, non-overwriting path.</summary>
public sealed class AtomicManagedResearchArtifactWriter : IManagedResearchArtifactWriter
{
    private readonly SparrowPortfolioResearchExporter _exporter = new();

    public Task PublishNewAsync(string artifactJson, string outputPath, CancellationToken cancellationToken = default) =>
        _exporter.ExportNewAtomicAsync(artifactJson, outputPath, cancellationToken);

    public void TryDeleteOwnedArtifact(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
        catch (IOException)
        {
            throw;
        }
    }
}

/// <summary>
/// Runs exactly one explicitly supplied research configuration and commits its authoritative evidence:
/// artifact V2, experiment record V3, execution binding V2 and source/build provenance V1.
/// <para>
/// The experiment record is the commit point: the artifact is published first, and a record never becomes
/// discoverable while its artifact write failed.
/// </para>
/// </summary>
public sealed class ManagedResearchExperimentRunner : IManagedResearchExperimentRunner
{
    /// <summary>Stable reason code reported when a failed record save could not clean up its own artifact.</summary>
    public const string OrphanArtifactReasonCode = "MANAGED_EXPERIMENT_ORPHAN_ARTIFACT";

    private readonly IResearchSourceBuildProvenanceProvider _provenanceProvider;
    private readonly IManagedResearchArtifactWriter _artifactWriter;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, IResearchExperimentRepository> _repositoryFactory;
    private readonly SparrowHistoricalBacktestEngine _backtestEngine = new();
    private readonly SparrowPortfolioSimulationEngine _simulationEngine = new();
    private readonly SparrowPortfolioPerformanceAnalyzer _performanceAnalyzer = new();
    private readonly SparrowResearchReproducibilityVerifier _readinessVerifier = new();

    public ManagedResearchExperimentRunner(
        IResearchSourceBuildProvenanceProvider? provenanceProvider = null,
        IManagedResearchArtifactWriter? artifactWriter = null,
        TimeProvider? timeProvider = null,
        Func<string, IResearchExperimentRepository>? repositoryFactory = null)
    {
        _provenanceProvider = provenanceProvider ?? new ResearchSourceBuildProvenanceProvider();
        _artifactWriter = artifactWriter ?? new AtomicManagedResearchArtifactWriter();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _repositoryFactory = repositoryFactory ?? (store => new JsonResearchExperimentRepository(store));
    }

    public async Task<ManagedResearchExperimentResult> RunAsync(ManagedResearchExperimentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Preconditions are checked before any expensive research work.
        if (File.Exists(request.ArtifactOutputPath))
            throw new IOException($"Artifact output '{request.ArtifactOutputPath}' already exists; authoritative managed evidence is never overwritten.");

        IResearchExperimentRepository repository = _repositoryFactory(request.ExperimentStore);
        await EnsureExperimentIdAvailableAsync(repository, request.ExperimentId, cancellationToken).ConfigureAwait(false);

        HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(request.DatasetPath, cancellationToken).ConfigureAwait(false);
        if (!loaded.Success || loaded.Dataset is null)
            throw new InvalidOperationException($"Dataset load failed: {string.Join("; ", loaded.Errors)}");
        HistoricalMarketDataset dataset = loaded.Dataset;

        (SparrowClassicParameterSnapshot? classicParameters, SparrowV2ParameterSnapshot? v2Parameters) =
            await LoadTypedParametersAsync(request, cancellationToken).ConfigureAwait(false);

        string strategyVersion = request.StrategyMode == SparrowStrategyMode.V2 ? SparrowStrategyVersions.V2 : SparrowStrategyVersions.Classic;
        SparrowBacktestRequest backtestRequest = new(request.StrategyMode, strategyVersion, request.StartDate, request.EndDate, request.TopN,
            new[] { request.HorizonTradingDays }, request.BacktestRoundTripCostRate, request.BacktestSlippageRate,
            ClassicParameters: classicParameters, V2Parameters: v2Parameters);

        // Authoritative source/build provenance is captured before research execution and before any output side effect.
        ResearchSourceBuildProvenance provenance = _provenanceProvider.Capture();

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        ResearchExperimentIdentity identity = new(request.ExperimentId, ResearchExperimentIdentity.CurrentExperimentVersion, "AIHelper.HistoricalDataTool", startedAt);
        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity).Start(startedAt);

        // Each existing engine is invoked exactly once; no alternate calculation and no metadata rerun.
        SparrowBacktestResult backtest = _backtestEngine.Run(dataset, backtestRequest, cancellationToken);
        PortfolioSimulationRequest portfolioRequest = new(dataset.DatasetId, dataset.Fingerprint, request.StrategyMode, strategyVersion,
            backtest.ParameterFingerprint, request.StartDate, request.EndDate, request.TopN, request.HorizonTradingDays,
            request.InitialCapital, request.PositionSizingMethod, request.CommissionRate, request.PortfolioSlippageRate, request.ExecutionModel);
        SparrowPortfolioSimulationResult simulation = await _simulationEngine.SimulateAsync(backtest, portfolioRequest, dataset, cancellationToken).ConfigureAwait(false);
        SparrowPortfolioPerformanceResult performance = await _performanceAnalyzer.AnalyzeAsync(simulation, dataset, cancellationToken).ConfigureAwait(false);

        SparrowPortfolioResearchArtifact artifact = new("AIHelper.HistoricalDataTool", performance);
        DateTimeOffset completedAt = _timeProvider.GetUtcNow();
        execution = execution.Complete(completedAt, artifact.ArtifactFingerprint);

        ExperimentParameterSnapshot parameters = ResearchExperimentParameterSnapshotFactory.Create(
            request.StrategyMode, strategyVersion, request.StartDate, request.EndDate, request.TopN, request.HorizonTradingDays,
            request.BacktestRoundTripCostRate, request.BacktestSlippageRate, classicParameters, v2Parameters,
            request.InitialCapital, request.PositionSizingMethod, request.CommissionRate, request.PortfolioSlippageRate, request.ExecutionModel);

        // Experiment analysis identity is the experiment parameter-analysis fingerprint, deliberately not the artifact analysis fingerprint.
        ResearchExperimentDefinition definition = new(execution.ExperimentIdentity, dataset.Fingerprint,
            new ResearchExperimentStrategyIdentity(request.StrategyMode.ToString(), strategyVersion), parameters,
            artifact.PortfolioConfigurationFingerprint, parameters.AnalysisFingerprint);

        ResearchExperimentExecutionSummary summary = ResearchExperimentExecutionSummary.FromCompletedExecution(
            execution, new ResearchExperimentPerformanceSummary(artifact.PerformanceSummary.TotalReturnPercent,
                artifact.PerformanceSummary.MaximumDrawdownPercent, artifact.PerformanceSummary.TradeCount, artifact.PerformanceSummary.WinRate));

        PersistedResearchExperimentRecord record = ResearchExperimentRecordFactory.CreateCurrent(definition, summary, artifact, completedAt, provenance);

        // In-memory validation before anything becomes discoverable.
        string artifactJson = SparrowPortfolioResearchExporter.Serialize(artifact);
        ResearchReproducibilityVerificationResult readiness = _readinessVerifier.VerifyContent(record, artifactJson);
        if (readiness.Status != ResearchReproducibilityVerificationStatus.Verified)
            throw new InvalidOperationException($"Managed experiment readiness verification failed: {string.Join(", ", readiness.ReasonCodes)}");

        await _artifactWriter.PublishNewAsync(artifactJson, request.ArtifactOutputPath, cancellationToken).ConfigureAwait(false);
        try
        {
            await repository.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception saveFailure)
        {
            try
            {
                _artifactWriter.TryDeleteOwnedArtifact(request.ArtifactOutputPath);
            }
            catch (Exception cleanupFailure)
            {
                throw new InvalidOperationException(
                    $"{OrphanArtifactReasonCode}: the experiment record could not be persisted and the artifact created by this attempt could not be removed. Artifact: '{request.ArtifactOutputPath}'.",
                    new AggregateException(saveFailure, cleanupFailure));
            }

            throw;
        }

        // The result reports the artifact-side executable strategy parameter fingerprint, which is the
        // identity bound into the execution binding. artifact.StrategyFingerprint is the distinct
        // higher-level portfolio-research strategy wrapper and is deliberately not projected here.
        return new ManagedResearchExperimentResult(
            request.ExperimentId, definition.SemanticFingerprint, artifact.DatasetFingerprint, artifact.PortfolioRequest.StrategyParameterFingerprint,
            artifact.PortfolioConfigurationFingerprint, artifact.AnalysisFingerprint, artifact.ArtifactFingerprint,
            provenance.ProvenanceFingerprint, record.ExecutionProvenanceBinding!.BindingFingerprint,
            artifact.ArtifactVersion, record.SchemaVersion, request.ArtifactOutputPath);
    }

    private static async Task EnsureExperimentIdAvailableAsync(IResearchExperimentRepository repository, string experimentId, CancellationToken cancellationToken)
    {
        try
        {
            await repository.GetAsync(experimentId, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return;
        }

        throw new InvalidOperationException($"Experiment '{experimentId}' already exists; authoritative managed evidence is never overwritten.");
    }

    private static async Task<(SparrowClassicParameterSnapshot? Classic, SparrowV2ParameterSnapshot? V2)> LoadTypedParametersAsync(
        ManagedResearchExperimentRequest request, CancellationToken cancellationToken)
    {
        string json = await File.ReadAllTextAsync(request.ParameterSnapshotPath, cancellationToken).ConfigureAwait(false);
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
        try
        {
            if (request.StrategyMode == SparrowStrategyMode.Classic)
                return (JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(json, options)
                    ?? throw new InvalidOperationException("Classic strategy parameter snapshot is empty."), null);
            if (request.StrategyMode == SparrowStrategyMode.V2)
                return (null, JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(json, options)
                    ?? throw new InvalidOperationException("V2 strategy parameter snapshot is empty."));
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Executable strategy parameter snapshot could not be loaded.", exception);
        }

        throw new InvalidOperationException($"Unsupported strategy mode: {request.StrategyMode}");
    }
}
