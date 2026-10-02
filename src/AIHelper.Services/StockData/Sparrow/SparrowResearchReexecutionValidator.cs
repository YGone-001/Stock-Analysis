using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Offline research re-execution engine and deterministic result equivalence validator.</summary>
public sealed class SparrowResearchReexecutionValidator : ISparrowResearchReexecutionValidator
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ParameterJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<ResearchReexecutionValidationResult> ValidateReexecutionAsync(
        PersistedResearchExperimentRecord experimentRecord,
        string originalArtifactPath,
        string datasetPath,
        string? parametersPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experimentRecord);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalArtifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        cancellationToken.ThrowIfCancellationRequested();

        List<ResearchReexecutionCheck> checks = new();
        List<string> reasonCodes = new();

        // 1. READINESS_VERIFIED
        if (!File.Exists(originalArtifactPath))
            throw new FileNotFoundException($"Portfolio research artifact file was not found: '{originalArtifactPath}'.", originalArtifactPath);

        SparrowResearchReproducibilityVerifier readinessVerifier = new();
        ResearchReproducibilityVerificationResult readinessResult = await readinessVerifier
            .VerifyAsync(experimentRecord, originalArtifactPath, cancellationToken)
            .ConfigureAwait(false);

        if (readinessResult.Status == ResearchReproducibilityVerificationStatus.Failed)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ReadinessVerified,
                ResearchReexecutionCheckStatus.Fail,
                ResearchReproducibilityVerificationStatus.Verified.ToString(),
                readinessResult.Status.ToString(),
                $"Reproducibility readiness verification failed with {readinessResult.FailedCheckCount} failed check(s)."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.ReadinessVerificationFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                reasonCodes);
        }

        if (readinessResult.Status == ResearchReproducibilityVerificationStatus.Unsupported)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ReadinessVerified,
                ResearchReexecutionCheckStatus.Unsupported,
                ResearchReproducibilityVerificationStatus.Verified.ToString(),
                readinessResult.Status.ToString(),
                "Reproducibility readiness verification returned unsupported."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.ReadinessVerificationUnsupported);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Unsupported,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.ReadinessVerified,
            ResearchReexecutionCheckStatus.Pass,
            ResearchReproducibilityVerificationStatus.Verified.ToString(),
            ResearchReproducibilityVerificationStatus.Verified.ToString(),
            "Reproducibility readiness verified."));

        // Load original artifact
        string originalArtifactJson = await File.ReadAllTextAsync(originalArtifactPath, cancellationToken).ConfigureAwait(false);
        SparrowPortfolioResearchArtifact originalArtifact = JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(originalArtifactJson, ArtifactJsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize portfolio research artifact.");

        if (originalArtifact.BenchmarkSummary is not null && !string.Equals(originalArtifact.BenchmarkSummary.Status, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new ResearchReexecutionCheck(
                "BENCHMARK_STATUS_SUPPORTED",
                ResearchReexecutionCheckStatus.Unsupported,
                "Unavailable",
                originalArtifact.BenchmarkSummary.Status,
                "Benchmark reproduction is not supported for close-based research re-execution."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.BenchmarkReproductionUnsupported);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Unsupported,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                originalArtifact.DatasetFingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        // 2. DATASET_LOADED
        if (!File.Exists(datasetPath))
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.DatasetLoaded,
                ResearchReexecutionCheckStatus.Fail,
                "FileExists",
                "FileNotFound",
                $"Dataset file '{datasetPath}' not found."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.DatasetLoadFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                originalArtifact.DatasetFingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        HistoricalDatasetLoadResult datasetResult = await new HistoricalDatasetJsonLoader()
            .LoadAsync(datasetPath, cancellationToken)
            .ConfigureAwait(false);

        if (!datasetResult.Success || datasetResult.Dataset is null)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.DatasetLoaded,
                ResearchReexecutionCheckStatus.Fail,
                "Success",
                "Failed",
                string.Join("; ", datasetResult.Errors)));
            reasonCodes.Add(ResearchReexecutionReasonCodes.DatasetLoadFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                originalArtifact.DatasetFingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.DatasetLoaded,
            ResearchReexecutionCheckStatus.Pass,
            "Success",
            "Success",
            "Historical dataset loaded successfully."));

        // 3. DATASET_FINGERPRINT_MATCH
        HistoricalMarketDataset loadedDataset = datasetResult.Dataset;
        bool datasetFingerprintMatches = string.Equals(loadedDataset.Fingerprint, originalArtifact.DatasetFingerprint, StringComparison.Ordinal)
            && string.Equals(loadedDataset.Fingerprint, experimentRecord.Definition.DatasetFingerprint, StringComparison.Ordinal);

        if (!datasetFingerprintMatches)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.DatasetFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.DatasetFingerprint,
                loadedDataset.Fingerprint,
                "Loaded dataset fingerprint does not match artifact and experiment record."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.DatasetFingerprintMismatch);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.DatasetFingerprintMatch,
            ResearchReexecutionCheckStatus.Pass,
            originalArtifact.DatasetFingerprint,
            loadedDataset.Fingerprint,
            "Loaded dataset fingerprint matches artifact and experiment record."));

        // 4. EXECUTABLE_PARAMETERS_LOADED
        if (string.IsNullOrWhiteSpace(parametersPath) || !File.Exists(parametersPath))
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ExecutableParametersLoaded,
                ResearchReexecutionCheckStatus.Fail,
                "FileExists",
                parametersPath ?? "<null>",
                "Executable strategy parameter snapshot file was not provided or not found. Fallback to current defaults is prohibited."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.ExecutableParametersMissing);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        string parametersJson = await File.ReadAllTextAsync(parametersPath, cancellationToken).ConfigureAwait(false);
        SparrowClassicParameterSnapshot? classicParameters = null;
        SparrowV2ParameterSnapshot? v2Parameters = null;

        try
        {
            if (originalArtifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.Classic)
            {
                classicParameters = JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(parametersJson, ParameterJsonOptions);
                if (classicParameters is null)
                    throw new InvalidOperationException("Classic parameter snapshot deserialized to null.");
            }
            else if (originalArtifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V2)
            {
                v2Parameters = JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(parametersJson, ParameterJsonOptions);
                if (v2Parameters is null)
                    throw new InvalidOperationException("V2 parameter snapshot deserialized to null.");
            }
            else
            {
                checks.Add(new ResearchReexecutionCheck(
                    ResearchReexecutionCheckCodes.ExecutableParametersLoaded,
                    ResearchReexecutionCheckStatus.Unsupported,
                    "Classic or V2",
                    originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                    $"Unsupported strategy mode: {originalArtifact.PortfolioRequest.StrategyMode}"));
                reasonCodes.Add(ResearchReexecutionReasonCodes.ExecutableParametersLoadFailed);
                return new ResearchReexecutionValidationResult(
                    ResearchReexecutionStatus.Unsupported,
                    checks,
                    experimentRecord.ExperimentId,
                    experimentRecord.ExperimentFingerprint,
                    originalArtifact.ArtifactFingerprint,
                    null,
                    loadedDataset.Fingerprint,
                    originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                    originalArtifact.PortfolioRequest.StrategyVersion,
                    originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                    originalArtifact.PortfolioConfigurationFingerprint,
                    reasonCodes);
            }
        }
        catch (Exception ex)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ExecutableParametersLoaded,
                ResearchReexecutionCheckStatus.Fail,
                "ValidParameterSnapshotJson",
                "DeserializationFailed",
                $"Failed to load parameter snapshot: {ex.Message}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.ExecutableParametersLoadFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.ExecutableParametersLoaded,
            ResearchReexecutionCheckStatus.Pass,
            "Loaded",
            "Loaded",
            "Executable strategy parameters loaded."));

        // 5. EXECUTABLE_PARAMETER_FINGERPRINT_MATCH
        SparrowBacktestRequest backtestRequest = new(
            originalArtifact.PortfolioRequest.StrategyMode,
            originalArtifact.PortfolioRequest.StrategyVersion,
            originalArtifact.PortfolioRequest.StartDate,
            originalArtifact.PortfolioRequest.EndDate,
            originalArtifact.PortfolioRequest.TopN,
            new[] { originalArtifact.PortfolioRequest.HorizonTradingDays },
            RoundTripCostRate: 0,
            SlippageRate: 0,
            ClassicParameters: classicParameters,
            V2Parameters: v2Parameters);

        string candidateParamFp = SparrowHistoricalFingerprint.Parameters(backtestRequest);
        bool paramFpMatches = string.Equals(candidateParamFp, originalArtifact.PortfolioRequest.StrategyParameterFingerprint, StringComparison.Ordinal)
            && string.Equals(candidateParamFp, experimentRecord.ExecutionProvenanceBinding!.ArtifactStrategyParameterFingerprint, StringComparison.Ordinal);

        if (!paramFpMatches)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ExecutableParameterFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                candidateParamFp,
                "Executable strategy parameter fingerprint does not match artifact and binding."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.ExecutableParameterFingerprintMismatch);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.ExecutableParameterFingerprintMatch,
            ResearchReexecutionCheckStatus.Pass,
            originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
            candidateParamFp,
            "Executable strategy parameter fingerprint matches artifact and binding."));

        // 6. BACKTEST_REQUEST_RECONSTRUCTED
        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.BacktestRequestReconstructed,
            ResearchReexecutionCheckStatus.Pass,
            "Reconstructed",
            "Reconstructed",
            "Historical backtest request reconstructed with authoritative inputs."));

        // 7. BACKTEST_REEXECUTED
        SparrowBacktestResult backtestResult;
        try
        {
            backtestResult = new SparrowHistoricalBacktestEngine().Run(loadedDataset, backtestRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.BacktestReexecuted,
                ResearchReexecutionCheckStatus.Fail,
                "Completed",
                "ExecutionFailed",
                $"Historical backtest execution failed: {ex.Message}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.BacktestExecutionFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        // 8. BACKTEST_PARAMETER_FINGERPRINT_MATCH
        bool backtestFpMatches = string.Equals(backtestResult.ParameterFingerprint, originalArtifact.PortfolioRequest.StrategyParameterFingerprint, StringComparison.Ordinal);
        if (!backtestFpMatches)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.BacktestParameterFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                backtestResult.ParameterFingerprint,
                "Re-executed backtest parameter fingerprint does not match artifact strategy parameter fingerprint."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.BacktestParameterFingerprintMismatch);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.BacktestParameterFingerprintMatch,
            ResearchReexecutionCheckStatus.Pass,
            originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
            backtestResult.ParameterFingerprint,
            "Re-executed backtest parameter fingerprint matches expected artifact fingerprint."));

        // 9. PORTFOLIO_REQUEST_MATCH
        bool portfolioReqMatches = string.Equals(originalArtifact.PortfolioRequest.DatasetId, loadedDataset.DatasetId, StringComparison.Ordinal)
            && string.Equals(originalArtifact.PortfolioRequest.DatasetFingerprint, loadedDataset.Fingerprint, StringComparison.Ordinal)
            && originalArtifact.PortfolioRequest.StrategyMode == backtestRequest.StrategyMode
            && string.Equals(originalArtifact.PortfolioRequest.StrategyVersion, backtestRequest.StrategyVersion, StringComparison.Ordinal)
            && string.Equals(originalArtifact.PortfolioRequest.StrategyParameterFingerprint, backtestResult.ParameterFingerprint, StringComparison.Ordinal)
            && originalArtifact.PortfolioRequest.StartDate == backtestRequest.StartDate
            && originalArtifact.PortfolioRequest.EndDate == backtestRequest.EndDate
            && originalArtifact.PortfolioRequest.TopN == backtestRequest.TopN
            && originalArtifact.PortfolioRequest.HorizonTradingDays == backtestRequest.Horizons[0]
            && originalArtifact.PortfolioRequest.CommissionRate == 0m
            && originalArtifact.PortfolioRequest.SlippageRate == 0m
            && originalArtifact.PortfolioRequest.PositionSizingMethod == PortfolioPositionSizingMethod.EqualWeight
            && originalArtifact.PortfolioRequest.ExecutionModel == PortfolioExecutionModel.CloseBased;

        if (!portfolioReqMatches)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PortfolioRequestMatch,
                ResearchReexecutionCheckStatus.Fail,
                "MatchesExecution",
                "Differs",
                "Original portfolio request does not match re-execution inputs."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.PortfolioRequestMismatch);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.PortfolioRequestMatch,
            ResearchReexecutionCheckStatus.Pass,
            "MatchesExecution",
            "MatchesExecution",
            "Original portfolio request matches re-execution inputs."));

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.BacktestReexecuted,
            ResearchReexecutionCheckStatus.Pass,
            "Completed",
            "Completed",
            "Historical backtest engine executed successfully."));

        // 10. PORTFOLIO_REEXECUTED
        SparrowPortfolioSimulationResult simulationResult;
        try
        {
            simulationResult = await new SparrowPortfolioSimulationEngine()
                .SimulateAsync(backtestResult, originalArtifact.PortfolioRequest, loadedDataset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PortfolioReexecuted,
                ResearchReexecutionCheckStatus.Fail,
                "Completed",
                "SimulationFailed",
                $"Portfolio simulation execution failed: {ex.Message}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.PortfolioSimulationFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.PortfolioReexecuted,
            ResearchReexecutionCheckStatus.Pass,
            "Completed",
            "Completed",
            "Portfolio simulation engine executed successfully."));

        // 11. PERFORMANCE_REANALYZED
        SparrowPortfolioPerformanceResult performanceResult;
        try
        {
            performanceResult = await new SparrowPortfolioPerformanceAnalyzer()
                .AnalyzeAsync(simulationResult, loadedDataset, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PerformanceReanalyzed,
                ResearchReexecutionCheckStatus.Fail,
                "Completed",
                "AnalysisFailed",
                $"Portfolio performance analysis failed: {ex.Message}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.PerformanceAnalysisFailed);
            return new ResearchReexecutionValidationResult(
                ResearchReexecutionStatus.Failed,
                checks,
                experimentRecord.ExperimentId,
                experimentRecord.ExperimentFingerprint,
                originalArtifact.ArtifactFingerprint,
                null,
                loadedDataset.Fingerprint,
                originalArtifact.PortfolioRequest.StrategyMode.ToString(),
                originalArtifact.PortfolioRequest.StrategyVersion,
                originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
                originalArtifact.PortfolioConfigurationFingerprint,
                reasonCodes);
        }

        checks.Add(new ResearchReexecutionCheck(
            ResearchReexecutionCheckCodes.PerformanceReanalyzed,
            ResearchReexecutionCheckStatus.Pass,
            "Completed",
            "Completed",
            "Portfolio performance analyzer executed successfully."));

        // 12. Create reproduced artifact
        SparrowPortfolioResearchArtifact reproducedArtifact = new(
            originalArtifact.CreatedBy,
            performanceResult,
            originalArtifact.Limitations);

        // 13. Equivalence and structural diagnostics
        return ValidateArtifactEquivalence(experimentRecord, originalArtifact, reproducedArtifact, checks);
    }

    public ResearchReexecutionValidationResult ValidateArtifactEquivalence(
        PersistedResearchExperimentRecord? experimentRecord,
        SparrowPortfolioResearchArtifact originalArtifact,
        SparrowPortfolioResearchArtifact reproducedArtifact,
        IEnumerable<ResearchReexecutionCheck>? precedingChecks = null)
    {
        ArgumentNullException.ThrowIfNull(originalArtifact);
        ArgumentNullException.ThrowIfNull(reproducedArtifact);

        List<ResearchReexecutionCheck> checks = precedingChecks is not null ? new(precedingChecks) : new();
        List<string> reasonCodes = new();

        // 12. TRADE_SEQUENCE_MATCH
        bool tradesMatch = CompareTrades(originalArtifact.Trades, reproducedArtifact.Trades, out string tradesMessage);
        if (tradesMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.TradeSequenceMatch,
                ResearchReexecutionCheckStatus.Pass,
                $"{originalArtifact.Trades.Count} trades",
                $"{reproducedArtifact.Trades.Count} trades",
                "Trade sequences are identical."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.TradeSequenceMatch,
                ResearchReexecutionCheckStatus.Fail,
                $"{originalArtifact.Trades.Count} trades",
                $"{reproducedArtifact.Trades.Count} trades",
                $"Trade sequences differ: {tradesMessage}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.TradeSequenceDiverged);
        }

        // 13. POSITION_SEQUENCE_MATCH
        bool positionsMatch = ComparePositions(originalArtifact.Positions, reproducedArtifact.Positions, out string positionsMessage);
        if (positionsMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PositionSequenceMatch,
                ResearchReexecutionCheckStatus.Pass,
                $"{originalArtifact.Positions.Count} positions",
                $"{reproducedArtifact.Positions.Count} positions",
                "Position sequences are identical."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PositionSequenceMatch,
                ResearchReexecutionCheckStatus.Fail,
                $"{originalArtifact.Positions.Count} positions",
                $"{reproducedArtifact.Positions.Count} positions",
                $"Position sequences differ: {positionsMessage}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.PositionSequenceDiverged);
        }

        // 14. EQUITY_CURVE_MATCH
        bool equityCurveMatch = CompareEquityCurves(originalArtifact.EquityCurve, reproducedArtifact.EquityCurve, out string equityMessage);
        if (equityCurveMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.EquityCurveMatch,
                ResearchReexecutionCheckStatus.Pass,
                $"{originalArtifact.EquityCurve.Count} points",
                $"{reproducedArtifact.EquityCurve.Count} points",
                "Equity curves are identical."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.EquityCurveMatch,
                ResearchReexecutionCheckStatus.Fail,
                $"{originalArtifact.EquityCurve.Count} points",
                $"{reproducedArtifact.EquityCurve.Count} points",
                $"Equity curves differ: {equityMessage}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.EquityCurveDiverged);
        }

        // 15. ATTRIBUTION_MATCH
        bool attributionMatch = CompareAttributions(originalArtifact.Attribution, reproducedArtifact.Attribution, out string attributionMessage);
        if (attributionMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.AttributionMatch,
                ResearchReexecutionCheckStatus.Pass,
                $"{originalArtifact.Attribution.Count} attributions",
                $"{reproducedArtifact.Attribution.Count} attributions",
                "Portfolio attributions are identical."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.AttributionMatch,
                ResearchReexecutionCheckStatus.Fail,
                $"{originalArtifact.Attribution.Count} attributions",
                $"{reproducedArtifact.Attribution.Count} attributions",
                $"Portfolio attributions differ: {attributionMessage}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.AttributionDiverged);
        }

        // 16. PERFORMANCE_SUMMARY_MATCH
        bool performanceMatch = ComparePerformanceMetrics(originalArtifact.PerformanceSummary, reproducedArtifact.PerformanceSummary, out string performanceMessage);
        if (performanceMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PerformanceSummaryMatch,
                ResearchReexecutionCheckStatus.Pass,
                "MetricsMatch",
                "MetricsMatch",
                "Performance summary metrics are identical."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PerformanceSummaryMatch,
                ResearchReexecutionCheckStatus.Fail,
                "MetricsMatch",
                "MetricsDiverged",
                $"Performance summary metrics differ: {performanceMessage}"));
            reasonCodes.Add(ResearchReexecutionReasonCodes.PerformanceSummaryDiverged);
        }

        // 17. STRATEGY_FINGERPRINT_MATCH
        bool strategyFpMatch = string.Equals(originalArtifact.StrategyFingerprint, reproducedArtifact.StrategyFingerprint, StringComparison.Ordinal);
        if (strategyFpMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.StrategyFingerprintMatch,
                ResearchReexecutionCheckStatus.Pass,
                originalArtifact.StrategyFingerprint,
                reproducedArtifact.StrategyFingerprint,
                "Strategy fingerprints match."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.StrategyFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.StrategyFingerprint,
                reproducedArtifact.StrategyFingerprint,
                "Strategy fingerprints differ."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.StrategyFingerprintDiverged);
        }

        // 18. PORTFOLIO_CONFIGURATION_FINGERPRINT_MATCH
        bool portfolioConfigFpMatch = string.Equals(originalArtifact.PortfolioConfigurationFingerprint, reproducedArtifact.PortfolioConfigurationFingerprint, StringComparison.Ordinal);
        if (portfolioConfigFpMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PortfolioConfigurationFingerprintMatch,
                ResearchReexecutionCheckStatus.Pass,
                originalArtifact.PortfolioConfigurationFingerprint,
                reproducedArtifact.PortfolioConfigurationFingerprint,
                "Portfolio configuration fingerprints match."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.PortfolioConfigurationFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.PortfolioConfigurationFingerprint,
                reproducedArtifact.PortfolioConfigurationFingerprint,
                "Portfolio configuration fingerprints differ."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.PortfolioConfigurationFingerprintDiverged);
        }

        // 19. ANALYSIS_FINGERPRINT_MATCH
        bool analysisFpMatch = string.Equals(originalArtifact.AnalysisFingerprint, reproducedArtifact.AnalysisFingerprint, StringComparison.Ordinal);
        if (analysisFpMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.AnalysisFingerprintMatch,
                ResearchReexecutionCheckStatus.Pass,
                originalArtifact.AnalysisFingerprint,
                reproducedArtifact.AnalysisFingerprint,
                "Analysis fingerprints match."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.AnalysisFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.AnalysisFingerprint,
                reproducedArtifact.AnalysisFingerprint,
                "Analysis fingerprints differ."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.AnalysisFingerprintDiverged);
        }

        // 20. ARTIFACT_FINGERPRINT_MATCH
        bool artifactFpMatch = string.Equals(originalArtifact.ArtifactFingerprint, reproducedArtifact.ArtifactFingerprint, StringComparison.Ordinal);
        if (artifactFpMatch)
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ArtifactFingerprintMatch,
                ResearchReexecutionCheckStatus.Pass,
                originalArtifact.ArtifactFingerprint,
                reproducedArtifact.ArtifactFingerprint,
                "Artifact fingerprints match."));
        }
        else
        {
            checks.Add(new ResearchReexecutionCheck(
                ResearchReexecutionCheckCodes.ArtifactFingerprintMatch,
                ResearchReexecutionCheckStatus.Fail,
                originalArtifact.ArtifactFingerprint,
                reproducedArtifact.ArtifactFingerprint,
                "Artifact fingerprints differ."));
            reasonCodes.Add(ResearchReexecutionReasonCodes.ArtifactFingerprintDiverged);
        }

        ResearchReexecutionStatus status = reasonCodes.Count == 0
            ? ResearchReexecutionStatus.Equivalent
            : ResearchReexecutionStatus.Diverged;

        return new ResearchReexecutionValidationResult(
            status,
            checks,
            experimentRecord?.ExperimentId,
            experimentRecord?.ExperimentFingerprint,
            originalArtifact.ArtifactFingerprint,
            reproducedArtifact.ArtifactFingerprint,
            originalArtifact.DatasetFingerprint,
            originalArtifact.PortfolioRequest.StrategyMode.ToString(),
            originalArtifact.PortfolioRequest.StrategyVersion,
            originalArtifact.PortfolioRequest.StrategyParameterFingerprint,
            originalArtifact.PortfolioConfigurationFingerprint,
            reasonCodes,
            reproducedArtifact);
    }

    private static bool CompareTrades(IReadOnlyList<PortfolioTrade> original, IReadOnlyList<PortfolioTrade> reproduced, out string message)
    {
        if (original.Count != reproduced.Count)
        {
            message = $"Count mismatch: original has {original.Count}, reproduced has {reproduced.Count}.";
            return false;
        }

        for (int i = 0; i < original.Count; i++)
        {
            PortfolioTrade orig = original[i];
            PortfolioTrade repro = reproduced[i];
            if (!string.Equals(orig.Symbol, repro.Symbol, StringComparison.Ordinal)
                || orig.TradeDate != repro.TradeDate
                || orig.Side != repro.Side
                || orig.Price != repro.Price
                || orig.Quantity != repro.Quantity
                || orig.Notional != repro.Notional
                || orig.Fee != repro.Fee)
            {
                message = $"Difference at index {i}: original [{orig.TradeDate:yyyy-MM-dd} {orig.Symbol} {orig.Side} price={orig.Price} qty={orig.Quantity}], reproduced [{repro.TradeDate:yyyy-MM-dd} {repro.Symbol} {repro.Side} price={repro.Price} qty={repro.Quantity}].";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool ComparePositions(IReadOnlyList<PortfolioPosition> original, IReadOnlyList<PortfolioPosition> reproduced, out string message)
    {
        if (original.Count != reproduced.Count)
        {
            message = $"Count mismatch: original has {original.Count}, reproduced has {reproduced.Count}.";
            return false;
        }

        for (int i = 0; i < original.Count; i++)
        {
            PortfolioPosition orig = original[i];
            PortfolioPosition repro = reproduced[i];
            if (!string.Equals(orig.Symbol, repro.Symbol, StringComparison.Ordinal)
                || orig.EntryDate != repro.EntryDate
                || orig.EntryPrice != repro.EntryPrice
                || orig.Quantity != repro.Quantity
                || orig.EntryNotional != repro.EntryNotional
                || orig.EntryFee != repro.EntryFee
                || orig.Status != repro.Status
                || orig.ExitDate != repro.ExitDate
                || orig.ExitPrice != repro.ExitPrice
                || orig.ExitNotional != repro.ExitNotional
                || orig.ExitFee != repro.ExitFee
                || orig.RealizedPnL != repro.RealizedPnL
                || orig.ReturnPercent != repro.ReturnPercent)
            {
                message = $"Difference at index {i}: original [{orig.Symbol} entry={orig.EntryDate:yyyy-MM-dd} status={orig.Status} pnl={orig.RealizedPnL}], reproduced [{repro.Symbol} entry={repro.EntryDate:yyyy-MM-dd} status={repro.Status} pnl={repro.RealizedPnL}].";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool CompareEquityCurves(IReadOnlyList<PortfolioEquityPoint> original, IReadOnlyList<PortfolioEquityPoint> reproduced, out string message)
    {
        if (original.Count != reproduced.Count)
        {
            message = $"Count mismatch: original has {original.Count}, reproduced has {reproduced.Count}.";
            return false;
        }

        for (int i = 0; i < original.Count; i++)
        {
            PortfolioEquityPoint orig = original[i];
            PortfolioEquityPoint repro = reproduced[i];
            if (orig.Date != repro.Date
                || orig.Cash != repro.Cash
                || orig.MarketValue != repro.MarketValue
                || orig.TotalEquity != repro.TotalEquity
                || orig.DailyReturn != repro.DailyReturn
                || orig.CumulativeReturn != repro.CumulativeReturn)
            {
                message = $"Difference at index {i}: original [{orig.Date:yyyy-MM-dd} equity={orig.TotalEquity} cum={orig.CumulativeReturn}], reproduced [{repro.Date:yyyy-MM-dd} equity={repro.TotalEquity} cum={repro.CumulativeReturn}].";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool CompareAttributions(IReadOnlyList<PortfolioAttribution> original, IReadOnlyList<PortfolioAttribution> reproduced, out string message)
    {
        if (original.Count != reproduced.Count)
        {
            message = $"Count mismatch: original has {original.Count}, reproduced has {reproduced.Count}.";
            return false;
        }

        for (int i = 0; i < original.Count; i++)
        {
            PortfolioAttribution orig = original[i];
            PortfolioAttribution repro = reproduced[i];
            if (!string.Equals(orig.Symbol, repro.Symbol, StringComparison.Ordinal)
                || orig.EntryDate != repro.EntryDate
                || orig.ExitDate != repro.ExitDate
                || orig.HoldingPeriodTradingDays != repro.HoldingPeriodTradingDays
                || orig.Quantity != repro.Quantity
                || orig.RealizedPnL != repro.RealizedPnL
                || orig.ReturnPercent != repro.ReturnPercent
                || orig.ContributionPercent != repro.ContributionPercent
                || orig.Winning != repro.Winning)
            {
                message = $"Difference at index {i}: original [{orig.Symbol} pnl={orig.RealizedPnL} ret={orig.ReturnPercent}], reproduced [{repro.Symbol} pnl={repro.RealizedPnL} ret={repro.ReturnPercent}].";
                return false;
            }
        }

        message = string.Empty;
        return true;
    }

    private static bool ComparePerformanceMetrics(PortfolioPerformanceMetrics original, PortfolioPerformanceMetrics reproduced, out string message)
    {
        if (original.InitialCapital != reproduced.InitialCapital)
        {
            message = $"InitialCapital differed: original={original.InitialCapital}, reproduced={reproduced.InitialCapital}.";
            return false;
        }
        if (original.FinalEquity != reproduced.FinalEquity)
        {
            message = $"FinalEquity differed: original={original.FinalEquity}, reproduced={reproduced.FinalEquity}.";
            return false;
        }
        if (original.TotalReturnPercent != reproduced.TotalReturnPercent)
        {
            message = $"TotalReturnPercent differed: original={original.TotalReturnPercent}, reproduced={reproduced.TotalReturnPercent}.";
            return false;
        }
        if (original.MaximumDrawdownPercent != reproduced.MaximumDrawdownPercent)
        {
            message = $"MaximumDrawdownPercent differed: original={original.MaximumDrawdownPercent}, reproduced={reproduced.MaximumDrawdownPercent}.";
            return false;
        }
        if (original.MaximumDrawdownDate != reproduced.MaximumDrawdownDate)
        {
            message = $"MaximumDrawdownDate differed: original={original.MaximumDrawdownDate:yyyy-MM-dd}, reproduced={reproduced.MaximumDrawdownDate:yyyy-MM-dd}.";
            return false;
        }
        if (original.TradeCount != reproduced.TradeCount)
        {
            message = $"TradeCount differed: original={original.TradeCount}, reproduced={reproduced.TradeCount}.";
            return false;
        }
        if (original.WinningTradeCount != reproduced.WinningTradeCount)
        {
            message = $"WinningTradeCount differed: original={original.WinningTradeCount}, reproduced={reproduced.WinningTradeCount}.";
            return false;
        }
        if (original.LosingTradeCount != reproduced.LosingTradeCount)
        {
            message = $"LosingTradeCount differed: original={original.LosingTradeCount}, reproduced={reproduced.LosingTradeCount}.";
            return false;
        }
        if (original.WinRate != reproduced.WinRate)
        {
            message = $"WinRate differed: original={original.WinRate}, reproduced={reproduced.WinRate}.";
            return false;
        }

        message = string.Empty;
        return true;
    }
}
