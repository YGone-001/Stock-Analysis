using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using static AIHelper.Core.Sparrow.ResearchReexecutionCheckCodes;
using static AIHelper.Core.Sparrow.ResearchReexecutionReasonCodes;
using static AIHelper.Core.Sparrow.ResearchReexecutionStatus;

namespace AIHelper.Services.StockData.Sparrow;

public sealed class SparrowResearchReexecutionValidator : ISparrowResearchReexecutionValidator
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    public async Task<ResearchReexecutionValidationResult> ValidateReexecutionAsync(PersistedResearchExperimentRecord experimentRecord,
        string originalArtifactPath, string datasetPath, string? parametersPath, double backtestRoundTripCostRate, double backtestSlippageRate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experimentRecord);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalArtifactPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetPath);
        cancellationToken.ThrowIfCancellationRequested();

        List<ResearchReexecutionCheck> checks = new();
        List<string> reasonCodes = new();
        SparrowPortfolioResearchArtifact? originalArtifact = null;
        HistoricalMarketDataset? loadedDataset = null;

        void Pass(string code, string? exp, string? act, string msg) =>
            checks.Add(new(code, ResearchReexecutionCheckStatus.Pass, exp, act, msg));
        void Fail(string code, string? exp, string? act, string msg, string reason) { checks.Add(new(code, ResearchReexecutionCheckStatus.Fail, exp, act, msg)); reasonCodes.Add(reason); }
        void Unsup(string code, string? exp, string? act, string msg, string reason) { checks.Add(new(code, ResearchReexecutionCheckStatus.Unsupported, exp, act, msg)); reasonCodes.Add(reason); }

        ResearchReexecutionValidationResult Result(ResearchReexecutionStatus status, SparrowPortfolioResearchArtifact? repro = null)
        {
            var p = originalArtifact?.PortfolioRequest;
            return new(status, checks, experimentRecord.ExperimentId, experimentRecord.ExperimentFingerprint, originalArtifact?.ArtifactFingerprint, repro?.ArtifactFingerprint,
                loadedDataset?.Fingerprint ?? originalArtifact?.DatasetFingerprint, p?.StrategyMode.ToString(), p?.StrategyVersion, p?.StrategyParameterFingerprint,
                originalArtifact?.PortfolioConfigurationFingerprint, reasonCodes, repro);
        }

        if (!File.Exists(originalArtifactPath))
            throw new FileNotFoundException($"Portfolio research artifact file was not found: '{originalArtifactPath}'.", originalArtifactPath);

        var readiness = await new SparrowResearchReproducibilityVerifier().VerifyAsync(experimentRecord, originalArtifactPath, cancellationToken).ConfigureAwait(false);
        if (readiness.Status == ResearchReproducibilityVerificationStatus.Failed)
        {
            Fail(ReadinessVerified, "Verified", readiness.Status.ToString(), $"Readiness verification failed ({readiness.FailedCheckCount} failed).", ReadinessVerificationFailed);
            return Result(Failed);
        }
        if (readiness.Status == ResearchReproducibilityVerificationStatus.Unsupported)
        {
            Unsup(ReadinessVerified, "Verified", readiness.Status.ToString(), "Readiness verification returned unsupported.", ReadinessVerificationUnsupported);
            return Result(Unsupported);
        }
        Pass(ReadinessVerified, "Verified", "Verified", "Reproducibility readiness verified.");

        string originalArtifactJson = await File.ReadAllTextAsync(originalArtifactPath, cancellationToken).ConfigureAwait(false);
        originalArtifact = JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(originalArtifactJson, JsonOpts) ?? throw new InvalidOperationException("Failed to deserialize portfolio research artifact.");

        if (originalArtifact.BenchmarkSummary is not null && !string.Equals(originalArtifact.BenchmarkSummary.Status, "Unavailable", StringComparison.OrdinalIgnoreCase))
        {
            Unsup("BENCHMARK_STATUS_SUPPORTED", "Unavailable", originalArtifact.BenchmarkSummary.Status, "Benchmark reproduction is not supported for close-based re-execution.", BenchmarkReproductionUnsupported);
            return Result(Unsupported);
        }

        if (!File.Exists(datasetPath))
        {
            Fail(DatasetLoaded, "FileExists", "FileNotFound", $"Dataset file '{datasetPath}' not found.", DatasetLoadFailed);
            return Result(Failed);
        }

        var datasetResult = await new HistoricalDatasetJsonLoader().LoadAsync(datasetPath, cancellationToken).ConfigureAwait(false);
        if (!datasetResult.Success || datasetResult.Dataset is null)
        {
            Fail(DatasetLoaded, "Success", "Failed", string.Join("; ", datasetResult.Errors), DatasetLoadFailed);
            return Result(Failed);
        }

        loadedDataset = datasetResult.Dataset;
        Pass(DatasetLoaded, "Success", "Success", "Historical dataset loaded successfully.");

        bool datasetFpMatches = string.Equals(loadedDataset.Fingerprint, originalArtifact.DatasetFingerprint, StringComparison.Ordinal)
            && string.Equals(loadedDataset.Fingerprint, experimentRecord.Definition.DatasetFingerprint, StringComparison.Ordinal);
        if (!datasetFpMatches) {
            Fail(DatasetFingerprintMatch, originalArtifact.DatasetFingerprint, loadedDataset.Fingerprint, "Loaded dataset fingerprint does not match artifact and record.", DatasetFingerprintMismatch);
            return Result(Failed);
        }
        Pass(DatasetFingerprintMatch, originalArtifact.DatasetFingerprint, loadedDataset.Fingerprint, "Loaded dataset fingerprint matches artifact and record.");

        if (string.IsNullOrWhiteSpace(parametersPath) || !File.Exists(parametersPath))
        {
            Fail(ExecutableParametersLoaded, "FileExists", parametersPath ?? "<null>", "Executable strategy parameter snapshot file was not provided or not found. Fallback to current defaults is prohibited.", ExecutableParametersMissing);
            return Result(Failed);
        }

        string parametersJson = await File.ReadAllTextAsync(parametersPath, cancellationToken).ConfigureAwait(false);
        SparrowClassicParameterSnapshot? classicParameters = null;
        SparrowV2ParameterSnapshot? v2Parameters = null;

        try
        {
            if (originalArtifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.Classic)
                classicParameters = JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("Classic snapshot was null.");
            else if (originalArtifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V2)
                v2Parameters = JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V2 snapshot was null.");
            else
            {
                Unsup(ExecutableParametersLoaded, "Classic or V2", originalArtifact.PortfolioRequest.StrategyMode.ToString(), $"Unsupported strategy mode: {originalArtifact.PortfolioRequest.StrategyMode}", ExecutableParametersLoadFailed);
                return Result(Unsupported);
            }
        }
        catch (Exception ex)
        {
            Fail(ExecutableParametersLoaded, "ValidParameterSnapshotJson", "DeserializationFailed", $"Failed to load parameter snapshot: {ex.Message}", ExecutableParametersLoadFailed);
            return Result(Failed);
        }
        Pass(ExecutableParametersLoaded, "Loaded", "Loaded", "Executable strategy parameters loaded.");

        // Historical backtest identity preimage includes RoundTripCostRate and SlippageRate: supplied explicitly, never inferred.
        var pr = originalArtifact.PortfolioRequest;
        SparrowBacktestRequest backtestRequest = new(pr.StrategyMode, pr.StrategyVersion, pr.StartDate, pr.EndDate, pr.TopN, new[] { pr.HorizonTradingDays },
            backtestRoundTripCostRate, backtestSlippageRate, ClassicParameters: classicParameters, V2Parameters: v2Parameters);

        string candidateParamFp = SparrowHistoricalFingerprint.Parameters(backtestRequest);
        bool paramFpMatches = string.Equals(candidateParamFp, pr.StrategyParameterFingerprint, StringComparison.Ordinal)
            && string.Equals(candidateParamFp, experimentRecord.ExecutionProvenanceBinding!.ArtifactStrategyParameterFingerprint, StringComparison.Ordinal);
        if (!paramFpMatches)
        {
            Fail(ExecutableParameterFingerprintMatch, pr.StrategyParameterFingerprint, candidateParamFp, "Executable parameter fingerprint does not match artifact and binding.", ExecutableParameterFingerprintMismatch);
            return Result(Failed);
        }
        Pass(ExecutableParameterFingerprintMatch, pr.StrategyParameterFingerprint, candidateParamFp, "Executable parameter fingerprint matches artifact and binding.");

        Pass(BacktestRequestReconstructed, "Reconstructed", "Reconstructed", "Historical backtest request reconstructed with authoritative inputs.");

        SparrowBacktestResult backtestResult;
        try { backtestResult = new SparrowHistoricalBacktestEngine().Run(loadedDataset, backtestRequest, cancellationToken); }
        catch (Exception ex)
        {
            Fail(BacktestReexecuted, "Completed", "ExecutionFailed", $"Backtest execution failed: {ex.Message}", BacktestExecutionFailed);
            return Result(Failed);
        }

        bool backtestFpMatches = string.Equals(backtestResult.ParameterFingerprint, pr.StrategyParameterFingerprint, StringComparison.Ordinal);
        if (!backtestFpMatches)
        {
            Fail(BacktestParameterFingerprintMatch, pr.StrategyParameterFingerprint, backtestResult.ParameterFingerprint, "Re-executed backtest parameter fingerprint does not match artifact strategy parameter fingerprint.", BacktestParameterFingerprintMismatch);
            return Result(Failed);
        }
        Pass(BacktestParameterFingerprintMatch, pr.StrategyParameterFingerprint, backtestResult.ParameterFingerprint, "Re-executed backtest parameter fingerprint matches artifact fingerprint.");

        // Reconstructed from persisted evidence: portfolio commission/slippage are read directly, never inferred from backtest costs.
        PortfolioSimulationRequest rr = new(loadedDataset.DatasetId, loadedDataset.Fingerprint, pr.StrategyMode, pr.StrategyVersion, backtestResult.ParameterFingerprint,
            pr.StartDate, pr.EndDate, pr.TopN, pr.HorizonTradingDays, pr.InitialCapital, pr.PositionSizingMethod, pr.CommissionRate, pr.SlippageRate, pr.ExecutionModel);

        bool reqMatch = pr.DatasetId == rr.DatasetId && pr.DatasetFingerprint == rr.DatasetFingerprint && pr.StrategyMode == rr.StrategyMode && pr.StrategyVersion == rr.StrategyVersion
            && pr.StrategyParameterFingerprint == rr.StrategyParameterFingerprint && pr.StartDate == rr.StartDate && pr.EndDate == rr.EndDate && pr.TopN == rr.TopN
            && pr.HorizonTradingDays == rr.HorizonTradingDays && pr.InitialCapital == rr.InitialCapital && pr.PositionSizingMethod == rr.PositionSizingMethod
            && pr.CommissionRate == rr.CommissionRate && pr.SlippageRate == rr.SlippageRate && pr.ExecutionModel == rr.ExecutionModel
            && string.Equals(SparrowPortfolioResearchFingerprint.Portfolio(rr), originalArtifact.PortfolioConfigurationFingerprint, StringComparison.Ordinal);

        if (!reqMatch)
        {
            Fail(PortfolioRequestMatch, "MatchesPersisted", "Differs", "Reconstructed portfolio request does not match the persisted original request.", PortfolioRequestMismatch);
            return Result(Failed);
        }
        Pass(PortfolioRequestMatch, "MatchesPersisted", "MatchesPersisted", "Reconstructed portfolio request matches the persisted original request.");
        Pass(BacktestReexecuted, "Completed", "Completed", "Historical backtest engine executed successfully.");

        SparrowPortfolioSimulationResult simulationResult;
        try { simulationResult = await new SparrowPortfolioSimulationEngine().SimulateAsync(backtestResult, rr, loadedDataset, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Fail(PortfolioReexecuted, "Completed", "SimulationFailed", $"Portfolio simulation failed: {ex.Message}", PortfolioSimulationFailed);
            return Result(Failed);
        }
        Pass(PortfolioReexecuted, "Completed", "Completed", "Portfolio simulation engine executed successfully.");

        SparrowPortfolioPerformanceResult performanceResult;
        try { performanceResult = await new SparrowPortfolioPerformanceAnalyzer().AnalyzeAsync(simulationResult, loadedDataset, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            Fail(PerformanceReanalyzed, "Completed", "AnalysisFailed", $"Portfolio performance analysis failed: {ex.Message}", PerformanceAnalysisFailed);
            return Result(Failed);
        }
        Pass(PerformanceReanalyzed, "Completed", "Completed", "Portfolio performance analyzer executed successfully.");

        SparrowPortfolioResearchArtifact reproducedArtifact = new(originalArtifact.CreatedBy, performanceResult, originalArtifact.Limitations);
        return ValidateArtifactEquivalence(experimentRecord, originalArtifact, reproducedArtifact, checks);
    }

    public ResearchReexecutionValidationResult ValidateArtifactEquivalence(PersistedResearchExperimentRecord? experimentRecord, SparrowPortfolioResearchArtifact originalArtifact,
        SparrowPortfolioResearchArtifact reproducedArtifact, IEnumerable<ResearchReexecutionCheck>? precedingChecks = null)
    {
        ArgumentNullException.ThrowIfNull(originalArtifact);
        ArgumentNullException.ThrowIfNull(reproducedArtifact);

        List<ResearchReexecutionCheck> checks = precedingChecks is not null ? new(precedingChecks) : new();
        List<string> reasonCodes = new();

        void CheckEq(string code, bool match, string exp, string act, string passMsg, string failMsg, string reason)
        {
            if (match) checks.Add(new(code, ResearchReexecutionCheckStatus.Pass, exp, act, passMsg));
            else { checks.Add(new(code, ResearchReexecutionCheckStatus.Fail, exp, act, failMsg)); reasonCodes.Add(reason); }
        }

        void CheckFp(string code, string orig, string repro, string name, string reason) =>
            CheckEq(code, orig == repro, orig, repro, $"{name} fingerprints match.", $"{name} fingerprints differ.", reason);

        bool tradesMatch = SeqEq(originalArtifact.Trades, reproducedArtifact.Trades, out string tradesMsg);
        CheckEq(TradeSequenceMatch, tradesMatch, $"{originalArtifact.Trades.Count} trades", $"{reproducedArtifact.Trades.Count} trades", "Trade sequences are identical.", $"Trade sequences differ: {tradesMsg}", TradeSequenceDiverged);

        bool positionsMatch = SeqEq(originalArtifact.Positions, reproducedArtifact.Positions, out string posMsg);
        CheckEq(PositionSequenceMatch, positionsMatch, $"{originalArtifact.Positions.Count} positions", $"{reproducedArtifact.Positions.Count} positions", "Position sequences are identical.", $"Position sequences differ: {posMsg}", PositionSequenceDiverged);

        bool equityCurveMatch = SeqEq(originalArtifact.EquityCurve, reproducedArtifact.EquityCurve, out string eqMsg);
        CheckEq(EquityCurveMatch, equityCurveMatch, $"{originalArtifact.EquityCurve.Count} points", $"{reproducedArtifact.EquityCurve.Count} points", "Equity curves are identical.", $"Equity curves differ: {eqMsg}", EquityCurveDiverged);

        bool attributionMatch = SeqEq(originalArtifact.Attribution, reproducedArtifact.Attribution, out string attrMsg);
        CheckEq(AttributionMatch, attributionMatch, $"{originalArtifact.Attribution.Count} attributions", $"{reproducedArtifact.Attribution.Count} attributions", "Portfolio attributions are identical.", $"Portfolio attributions differ: {attrMsg}", AttributionDiverged);

        bool perfMatch = SeqEq(originalArtifact.PerformanceSummary, reproducedArtifact.PerformanceSummary, out string perfMsg);
        CheckEq(PerformanceSummaryMatch, perfMatch, "MetricsMatch", perfMatch ? "MetricsMatch" : "MetricsDiverged", "Performance summary metrics are identical.", $"Performance summary metrics differ: {perfMsg}", PerformanceSummaryDiverged);

        CheckFp(StrategyFingerprintMatch, originalArtifact.StrategyFingerprint, reproducedArtifact.StrategyFingerprint, "Strategy", StrategyFingerprintDiverged);

        CheckFp(PortfolioConfigurationFingerprintMatch, originalArtifact.PortfolioConfigurationFingerprint, reproducedArtifact.PortfolioConfigurationFingerprint, "Portfolio configuration", PortfolioConfigurationFingerprintDiverged);

        CheckFp(AnalysisFingerprintMatch, originalArtifact.AnalysisFingerprint, reproducedArtifact.AnalysisFingerprint, "Analysis", AnalysisFingerprintDiverged);

        CheckFp(ArtifactFingerprintMatch, originalArtifact.ArtifactFingerprint, reproducedArtifact.ArtifactFingerprint, "Artifact", ArtifactFingerprintDiverged);

        var p = originalArtifact.PortfolioRequest;
        return new(reasonCodes.Count == 0 ? Equivalent : Diverged, checks, experimentRecord?.ExperimentId, experimentRecord?.ExperimentFingerprint,
            originalArtifact.ArtifactFingerprint, reproducedArtifact.ArtifactFingerprint, originalArtifact.DatasetFingerprint, p.StrategyMode.ToString(),
            p.StrategyVersion, p.StrategyParameterFingerprint, originalArtifact.PortfolioConfigurationFingerprint, reasonCodes, reproducedArtifact);
    }

    private static bool SeqEq<T>(T a, T b, out string msg)
    {
        string sa = JsonSerializer.Serialize(a, JsonOpts), sb = JsonSerializer.Serialize(b, JsonOpts);
        msg = sa == sb ? string.Empty : "Serialized payload diverged.";
        return sa == sb;
    }
}
