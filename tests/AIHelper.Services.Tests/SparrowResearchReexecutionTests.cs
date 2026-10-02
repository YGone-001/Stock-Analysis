using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchReexecutionTests
{
    private static readonly string[] CanonicalCheckCodes =
    {
        ResearchReexecutionCheckCodes.ReadinessVerified,
        ResearchReexecutionCheckCodes.DatasetLoaded,
        ResearchReexecutionCheckCodes.DatasetFingerprintMatch,
        ResearchReexecutionCheckCodes.ExecutableParametersLoaded,
        ResearchReexecutionCheckCodes.ExecutableParameterFingerprintMatch,
        ResearchReexecutionCheckCodes.BacktestRequestReconstructed,
        ResearchReexecutionCheckCodes.BacktestParameterFingerprintMatch,
        ResearchReexecutionCheckCodes.PortfolioRequestMatch,
        ResearchReexecutionCheckCodes.BacktestReexecuted,
        ResearchReexecutionCheckCodes.PortfolioReexecuted,
        ResearchReexecutionCheckCodes.PerformanceReanalyzed,
        ResearchReexecutionCheckCodes.TradeSequenceMatch,
        ResearchReexecutionCheckCodes.PositionSequenceMatch,
        ResearchReexecutionCheckCodes.EquityCurveMatch,
        ResearchReexecutionCheckCodes.AttributionMatch,
        ResearchReexecutionCheckCodes.PerformanceSummaryMatch,
        ResearchReexecutionCheckCodes.StrategyFingerprintMatch,
        ResearchReexecutionCheckCodes.PortfolioConfigurationFingerprintMatch,
        ResearchReexecutionCheckCodes.AnalysisFingerprintMatch,
        ResearchReexecutionCheckCodes.ArtifactFingerprintMatch
    };

    [Fact]
    public async Task ValidateReexecutionAsync_ValidEvidence_ReturnsEquivalentAndAllChecksPass()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            SparrowResearchReexecutionValidator validator = new();

            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Equivalent, result.Status);
            Assert.Equal(20, result.CheckCount);
            Assert.Equal(20, result.PassedCheckCount);
            Assert.Equal(0, result.FailedCheckCount);
            Assert.Equal(0, result.UnsupportedCheckCount);
            Assert.Empty(result.ReasonCodes);
            Assert.Equal(fixture.Record.ExperimentId, result.ExperimentId);
            Assert.Equal(fixture.Artifact.ArtifactFingerprint, result.OriginalArtifactFingerprint);
            Assert.Equal(fixture.Artifact.ArtifactFingerprint, result.ReproducedArtifactFingerprint);
            Assert.Equal(fixture.LoadedDataset.Fingerprint, result.DatasetFingerprint);

            Assert.Equal(CanonicalCheckCodes, result.Checks.Select(c => c.Code));
            Assert.All(result.Checks, check => Assert.Equal(ResearchReexecutionCheckStatus.Pass, check.Status));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_MissingParameters_FailsWithoutUsingDefaults()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            SparrowResearchReexecutionValidator validator = new();

            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                parametersPath: null);

            Assert.Equal(ResearchReexecutionStatus.Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.ExecutableParametersMissing);
            ResearchReexecutionCheck paramCheck = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.ExecutableParametersLoaded);
            Assert.Equal(ResearchReexecutionCheckStatus.Fail, paramCheck.Status);
            Assert.Contains("defaults is prohibited", paramCheck.Message);
            Assert.DoesNotContain(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestReexecuted);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_WrongParameters_FailsBeforeExecution()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            SparrowClassicParameterSnapshot wrongSnapshot = new(true, 2, 10, 2.0, 5, false, 1, .30);
            string wrongParamPath = Path.Combine(directory, "wrong-parameters.json");
            await File.WriteAllTextAsync(wrongParamPath, JsonSerializer.Serialize(wrongSnapshot));

            SparrowResearchReexecutionValidator validator = new();
            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                wrongParamPath);

            Assert.Equal(ResearchReexecutionStatus.Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.ExecutableParameterFingerprintMismatch);
            ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.ExecutableParameterFingerprintMatch);
            Assert.Equal(ResearchReexecutionCheckStatus.Fail, check.Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestReexecuted);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_WrongDataset_FailsBeforeExecution()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            HistoricalDatasetFile wrongFile = CreateDatasetFile("wrong-dataset-id");
            string wrongDatasetPath = Path.Combine(directory, "wrong-dataset.json");
            await File.WriteAllTextAsync(wrongDatasetPath, JsonSerializer.Serialize(wrongFile));

            SparrowResearchReexecutionValidator validator = new();
            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                wrongDatasetPath,
                fixture.ParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.DatasetFingerprintMismatch);
            ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.DatasetFingerprintMatch);
            Assert.Equal(ResearchReexecutionCheckStatus.Fail, check.Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestReexecuted);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_ReadinessFailure_ShortCircuitsBeforeExecution()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            // Tamper artifact JSON so dataset fingerprint does not match, causing Phase 3.9 readiness verification to fail
            string tamperedArtifactJson = await File.ReadAllTextAsync(fixture.ArtifactPath);
            tamperedArtifactJson = tamperedArtifactJson.Replace(fixture.Artifact.DatasetFingerprint, "tampered-dataset-fp");
            string tamperedArtifactPath = Path.Combine(directory, "tampered-artifact.json");
            await File.WriteAllTextAsync(tamperedArtifactPath, tamperedArtifactJson);

            SparrowResearchReexecutionValidator validator = new();
            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                tamperedArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.ReadinessVerificationFailed);
            ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.ReadinessVerified);
            Assert.Equal(ResearchReexecutionCheckStatus.Fail, check.Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestReexecuted);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_LegacyRecord_ReturnsUnsupported()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            PersistedResearchExperimentRecord legacyRecord = new(
                fixture.Record.ExperimentId,
                fixture.Record.ExperimentFingerprint,
                fixture.Record.Definition,
                fixture.Record.ExecutionSummary,
                fixture.Record.ArtifactReference,
                fixture.Record.Lineage,
                fixture.Record.CreatedAt,
                PersistedResearchExperimentRecord.LegacySchemaVersion,
                executionProvenanceBinding: null);

            SparrowResearchReexecutionValidator validator = new();
            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                legacyRecord,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Unsupported, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.ReadinessVerificationUnsupported);
            ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.ReadinessVerified);
            Assert.Equal(ResearchReexecutionCheckStatus.Unsupported, check.Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestReexecuted);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_LegacyArtifact_ReturnsUnsupported()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            string v1ArtifactJson = await File.ReadAllTextAsync(fixture.ArtifactPath);
            v1ArtifactJson = v1ArtifactJson.Replace(SparrowPortfolioResearchArtifact.CurrentArtifactVersion, SparrowPortfolioResearchArtifact.LegacyArtifactVersion);
            string v1ArtifactPath = Path.Combine(directory, "v1-artifact.json");
            await File.WriteAllTextAsync(v1ArtifactPath, v1ArtifactJson);

            SparrowResearchReexecutionValidator validator = new();
            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                v1ArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Unsupported, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.ReadinessVerificationUnsupported);
            ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.ReadinessVerified);
            Assert.Equal(ResearchReexecutionCheckStatus.Unsupported, check.Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestReexecuted);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_BacktestParameterIdentity_MatchesArtifactParameterFingerprint()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            SparrowResearchReexecutionValidator validator = new();

            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Equivalent, result.Status);
            ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.BacktestParameterFingerprintMatch);
            Assert.Equal(ResearchReexecutionCheckStatus.Pass, check.Status);
            Assert.Equal(fixture.Artifact.PortfolioRequest.StrategyParameterFingerprint, check.Expected);
            Assert.Equal(fixture.Artifact.PortfolioRequest.StrategyParameterFingerprint, check.Actual);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ValidateArtifactEquivalence_DivergedTrades_ReturnsDivergedWithDiagnostic()
    {
        var original = CreateMockArtifact();
        PortfolioTrade[] divergedTrades = original.Trades.Select((t, i) => i == 0 ? new PortfolioTrade(t.Symbol, t.TradeDate, t.Side, t.Price + 1, t.Quantity, t.Notional + 1, t.Fee) : t).ToArray();
        var diverged = CreateMockArtifact(trades: divergedTrades);

        SparrowResearchReexecutionValidator validator = new();
        ResearchReexecutionValidationResult result = validator.ValidateArtifactEquivalence(null, original, diverged);

        Assert.Equal(ResearchReexecutionStatus.Diverged, result.Status);
        Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.TradeSequenceDiverged);
        ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.TradeSequenceMatch);
        Assert.Equal(ResearchReexecutionCheckStatus.Fail, check.Status);
    }

    [Fact]
    public void ValidateArtifactEquivalence_DivergedEquityCurve_ReturnsDivergedWithDiagnostic()
    {
        var original = CreateMockArtifact();
        PortfolioEquityPoint[] divergedPoints = original.EquityCurve.Select((p, i) => i == 0 ? new PortfolioEquityPoint(p.Date, p.Cash + 10, p.MarketValue, p.TotalEquity + 10, p.DailyReturn, p.CumulativeReturn) : p).ToArray();
        var diverged = CreateMockArtifact(equityCurve: divergedPoints);

        SparrowResearchReexecutionValidator validator = new();
        ResearchReexecutionValidationResult result = validator.ValidateArtifactEquivalence(null, original, diverged);

        Assert.Equal(ResearchReexecutionStatus.Diverged, result.Status);
        Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.EquityCurveDiverged);
        ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.EquityCurveMatch);
        Assert.Equal(ResearchReexecutionCheckStatus.Fail, check.Status);
    }

    [Fact]
    public void ValidateArtifactEquivalence_DivergedPerformanceMetrics_ReturnsDivergedWithDiagnostic()
    {
        var original = CreateMockArtifact();
        PortfolioPerformanceMetrics divergedMetrics = new(
            original.PerformanceSummary.InitialCapital,
            original.PerformanceSummary.FinalEquity + 100,
            original.PerformanceSummary.TotalReturnPercent + 0.1,
            original.PerformanceSummary.MaximumDrawdownPercent,
            original.PerformanceSummary.MaximumDrawdownDate,
            original.PerformanceSummary.TradeCount,
            original.PerformanceSummary.WinningTradeCount,
            original.PerformanceSummary.LosingTradeCount,
            original.PerformanceSummary.WinRate);
        var diverged = CreateMockArtifact(metrics: divergedMetrics);

        SparrowResearchReexecutionValidator validator = new();
        ResearchReexecutionValidationResult result = validator.ValidateArtifactEquivalence(null, original, diverged);

        Assert.Equal(ResearchReexecutionStatus.Diverged, result.Status);
        Assert.Contains(result.ReasonCodes, r => r == ResearchReexecutionReasonCodes.PerformanceSummaryDiverged);
        ResearchReexecutionCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReexecutionCheckCodes.PerformanceSummaryMatch);
        Assert.Equal(ResearchReexecutionCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task ValidateReexecutionAsync_IsDeterministic_AcrossTwoExecutions()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);
            SparrowResearchReexecutionValidator validator = new();

            ResearchReexecutionValidationResult first = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            ResearchReexecutionValidationResult second = await validator.ValidateReexecutionAsync(
                fixture.Record,
                fixture.ArtifactPath,
                fixture.DatasetPath,
                fixture.ParametersPath);

            Assert.Equal(first.Status, second.Status);
            Assert.Equal(first.OriginalArtifactFingerprint, second.OriginalArtifactFingerprint);
            Assert.Equal(first.ReproducedArtifactFingerprint, second.ReproducedArtifactFingerprint);
            Assert.Equal(first.CheckCount, second.CheckCount);
            Assert.Equal(first.Checks.Select(c => (c.Code, c.Status, c.Message)), second.Checks.Select(c => (c.Code, c.Status, c.Message)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ValidateReexecutionAsync_IsPathIndependent_WhenFilesMoved()
    {
        string dir1 = TemporaryDirectory();
        string dir2 = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(dir1);

            // Copy files to dir2
            string newArtifactPath = Path.Combine(dir2, "moved-artifact.json");
            string newDatasetPath = Path.Combine(dir2, "moved-dataset.json");
            string newParametersPath = Path.Combine(dir2, "moved-parameters.json");
            File.Copy(fixture.ArtifactPath, newArtifactPath);
            File.Copy(fixture.DatasetPath, newDatasetPath);
            File.Copy(fixture.ParametersPath, newParametersPath);

            SparrowResearchReexecutionValidator validator = new();
            ResearchReexecutionValidationResult result = await validator.ValidateReexecutionAsync(
                fixture.Record,
                newArtifactPath,
                newDatasetPath,
                newParametersPath);

            Assert.Equal(ResearchReexecutionStatus.Equivalent, result.Status);
            Assert.Equal(fixture.Artifact.ArtifactFingerprint, result.ReproducedArtifactFingerprint);
        }
        finally
        {
            DeleteDirectory(dir1);
            DeleteDirectory(dir2);
        }
    }

    [Fact]
    public async Task Cli_Subprocess_WithoutGateway_ReturnsZeroAndEquivalent()
    {
        string directory = TemporaryDirectory();
        try
        {
            var fixture = await CreateExecutionFixtureAsync(directory);

            ProcessStartInfo start = new("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = SolutionRoot()
            };
            start.Environment.Remove("HISTORICAL_GATEWAY_URL");
            start.ArgumentList.Add("run");
            start.ArgumentList.Add("--project");
            start.ArgumentList.Add(Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"));
            start.ArgumentList.Add("--configuration");
            start.ArgumentList.Add(TestExecutionConfiguration.Current());
            start.ArgumentList.Add("--no-build");
            start.ArgumentList.Add("--no-restore");
            start.ArgumentList.Add("--");
            start.ArgumentList.Add("--reproduce-experiment");
            start.ArgumentList.Add(fixture.Record.ExperimentId);
            start.ArgumentList.Add("--experiment-store");
            start.ArgumentList.Add(fixture.StorePath);
            start.ArgumentList.Add("--artifact");
            start.ArgumentList.Add(fixture.ArtifactPath);
            start.ArgumentList.Add("--dataset");
            start.ArgumentList.Add(fixture.DatasetPath);
            start.ArgumentList.Add("--parameters");
            start.ArgumentList.Add(fixture.ParametersPath);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("REEXECUTION_STATUS=Equivalent", stdout);
            Assert.Contains($"EXPERIMENT_ID={fixture.Record.ExperimentId}", stdout);
            Assert.Contains($"ORIGINAL_ARTIFACT_FINGERPRINT={fixture.Artifact.ArtifactFingerprint}", stdout);
            Assert.Contains($"REPRODUCED_ARTIFACT_FINGERPRINT={fixture.Artifact.ArtifactFingerprint}", stdout);
            Assert.Contains("CHECK=ARTIFACT_FINGERPRINT_MATCH:Pass", stdout);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Cli_ConflictingModes_ExitsNonZeroWithConflictingModesMessage()
    {
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = SolutionRoot()
        };
        start.Environment.Remove("HISTORICAL_GATEWAY_URL");
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"));
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(TestExecutionConfiguration.Current());
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--reproduce-experiment");
        start.ArgumentList.Add("EXP-001");
        start.ArgumentList.Add("--verify-reproducibility");
        start.ArgumentList.Add("EXP-001");

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("CONFLICTING_OPERATION_MODES", stderr);
    }

    private static SparrowPortfolioResearchArtifact CreateMockArtifact(
        PortfolioTrade[]? trades = null,
        PortfolioPosition[]? positions = null,
        PortfolioEquityPoint[]? equityCurve = null,
        PortfolioAttribution[]? attributions = null,
        PortfolioPerformanceMetrics? metrics = null)
    {
        DateOnly first = new(2026, 1, 2);
        DateOnly second = new(2026, 1, 3);
        PortfolioSimulationRequest request = new(
            "fixture-dataset",
            "dataset-fp",
            SparrowStrategyMode.V2,
            SparrowStrategyVersions.V2,
            "strategy-param-fp",
            first,
            second,
            2,
            1,
            1_000,
            PortfolioPositionSizingMethod.EqualWeight,
            0,
            0);

        PortfolioTrade[] defaultTrades =
        {
            new("600000", first, PortfolioTradeSide.Buy, 100, 1, 100, 0),
            new("600001", second, PortfolioTradeSide.Sell, 110, 1, 110, 0)
        };
        PortfolioPosition[] defaultPositions =
        {
            new("600000", first, 100, 1, 100, 0, PortfolioPositionStatus.Closed, second, 110, 110, 0, 10, 10),
            new("600001", second, 100, 1, 100, 0, PortfolioPositionStatus.Open)
        };
        PortfolioEquityPoint[] defaultCurve =
        {
            new PortfolioEquityPoint(first, 900, 100, 1_000, null, 0),
            new PortfolioEquityPoint(second, 910, 100, 1_010, .01, .01)
        };
        PortfolioAttribution[] defaultAttribution =
        {
            new("600000", first, second, 1, 1, 10, 10, 1, true),
            new("600001", second, second, 0, 1, -5, -5, -.5, false)
        };
        PortfolioPerformanceMetrics defaultMetrics = new(1_000, 1_010, 1, -1, second, 2, 1, 1, .5);

        return new SparrowPortfolioResearchArtifact(
            SparrowPortfolioResearchArtifact.CurrentArtifactVersion,
            "artifact-fp-mock",
            "AIHelper.HistoricalDataTool",
            "dataset-fp",
            SparrowPortfolioResearchFingerprint.Strategy(request),
            SparrowPortfolioResearchFingerprint.Portfolio(request),
            SparrowPortfolioResearchFingerprint.Analysis("dataset-fp", SparrowPortfolioResearchFingerprint.Strategy(request), SparrowPortfolioResearchFingerprint.Portfolio(request)),
            new SparrowPortfolioResearchStrategy(request.StrategyMode.ToString(), request.StrategyVersion, SparrowPortfolioResearchFingerprint.Strategy(request)),
            request,
            new SparrowPortfolioResearchSimulationSummary(2, 1, 1),
            metrics ?? defaultMetrics,
            new SparrowPortfolioResearchBenchmarkSummary("Unavailable", null, null, null),
            trades ?? defaultTrades,
            positions ?? defaultPositions,
            equityCurve ?? defaultCurve,
            attributions ?? defaultAttribution,
            new[] { "warning-a" },
            SparrowPortfolioResearchArtifact.DefaultLimitations);
    }

    private static async Task<(
        PersistedResearchExperimentRecord Record,
        SparrowPortfolioResearchArtifact Artifact,
        HistoricalMarketDataset LoadedDataset,
        string StorePath,
        string ArtifactPath,
        string DatasetPath,
        string ParametersPath)> CreateExecutionFixtureAsync(string directory)
    {
        string storePath = Path.Combine(directory, "experiments");
        string artifactPath = Path.Combine(directory, "artifact.json");
        string datasetPath = Path.Combine(directory, "dataset.json");
        string parametersPath = Path.Combine(directory, "parameters.json");

        HistoricalDatasetFile datasetFile = CreateDatasetFile("fixture-reexecution-dataset");
        await File.WriteAllTextAsync(datasetPath, JsonSerializer.Serialize(datasetFile));

        HistoricalDatasetLoadResult loadResult = await new HistoricalDatasetJsonLoader().LoadAsync(datasetPath);
        Assert.True(loadResult.Success);
        HistoricalMarketDataset dataset = loadResult.Dataset!;

        DateOnly startDate = dataset.TradingDates[60];
        DateOnly endDate = dataset.TradingDates[65];
        SparrowClassicParameterSnapshot classicParams = new(false, 1, 5, 1.1, 1, true, 0, .15);
        await File.WriteAllTextAsync(parametersPath, JsonSerializer.Serialize(classicParams));

        SparrowBacktestRequest backtestRequest = new(
            SparrowStrategyMode.Classic,
            SparrowStrategyVersions.Classic,
            startDate,
            endDate,
            2,
            new[] { 1 },
            ClassicParameters: classicParams);

        SparrowBacktestResult backtest = new SparrowHistoricalBacktestEngine().Run(dataset, backtestRequest);

        PortfolioSimulationRequest portfolioRequest = new(
            dataset.DatasetId,
            dataset.Fingerprint,
            SparrowStrategyMode.Classic,
            SparrowStrategyVersions.Classic,
            backtest.ParameterFingerprint,
            startDate,
            endDate,
            2,
            1,
            1_000_000m,
            PortfolioPositionSizingMethod.EqualWeight,
            0m,
            0m);

        SparrowPortfolioSimulationResult simulation = await new SparrowPortfolioSimulationEngine()
            .SimulateAsync(backtest, portfolioRequest, dataset);

        SparrowPortfolioPerformanceResult performance = await new SparrowPortfolioPerformanceAnalyzer()
            .AnalyzeAsync(simulation, dataset);

        SparrowPortfolioResearchArtifact artifact = await new SparrowPortfolioResearchExporter()
            .ExportAsync(performance, artifactPath);

        ResearchExperimentIdentity identity = new(
            "EXP-REPRO-001",
            ResearchExperimentIdentity.CurrentExperimentVersion,
            "operator-repro",
            new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));

        ExperimentParameterSnapshot frozenParams = new(
            "v1",
            new Dictionary<string, string> { ["MinRise"] = "1", ["MaxRise"] = "5" },
            new Dictionary<string, string> { ["TopN"] = "2", ["Horizon"] = "1" },
            new Dictionary<string, string> { ["Metric"] = "Return" });

        ResearchExperimentDefinition definition = new(
            identity,
            artifact.DatasetFingerprint,
            new ResearchExperimentStrategyIdentity(artifact.Strategy.Mode, artifact.Strategy.Version),
            frozenParams,
            artifact.PortfolioConfigurationFingerprint,
            "analysis-config-001",
            "benchmark:CSI300",
            "Re-execution fixture experiment");

        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), artifact.ArtifactFingerprint);

        ResearchExperimentPerformanceSummary performanceSummary = new(
            artifact.PerformanceSummary.TotalReturnPercent,
            artifact.PerformanceSummary.MaximumDrawdownPercent,
            artifact.PerformanceSummary.TradeCount,
            artifact.PerformanceSummary.WinRate);

        ResearchExperimentExecutionSummary executionSummary = ResearchExperimentExecutionSummary.FromCompletedExecution(execution, performanceSummary);

        ResearchArtifactLineage lineage = new(
            definition.SemanticFingerprint,
            definition.DatasetFingerprint,
            frozenParams.Fingerprint,
            artifact.ArtifactFingerprint);

        ResearchResultArtifactReference artifactRef = new(artifact.ArtifactFingerprint, artifact.ArtifactVersion);
        ResearchExecutionProvenanceBinding binding = ResearchExecutionProvenanceBinding.Create(definition, artifact);

        PersistedResearchExperimentRecord record = new(
            identity.ExperimentId,
            definition.SemanticFingerprint,
            definition,
            executionSummary,
            artifactRef,
            lineage,
            new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero),
            PersistedResearchExperimentRecord.CurrentSchemaVersion,
            binding);

        JsonResearchExperimentRepository repository = new(storePath);
        await repository.SaveAsync(record);

        return (record, artifact, dataset, storePath, artifactPath, datasetPath, parametersPath);
    }

    private static HistoricalDatasetFile CreateDatasetFile(string datasetId = "fixture-dataset")
    {
        DateOnly start = new(2026, 1, 1);
        DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        return new HistoricalDatasetFile
        {
            SchemaVersion = HistoricalDatasetJsonLoader.LegacySchemaVersion,
            DatasetId = datasetId,
            Source = "Test",
            PriceAdjustmentMode = "ForwardAdjusted",
            TradingDates = dates.ToList(),
            Capabilities = HistoricalDataCapabilities.Complete,
            Quotes = dates.SelectMany(date => new[]
            {
                new HistoricalQuoteFile { TradingDate = date, Symbol = "600000", Name = "StockA", Price = 10, PreviousClose = 9.8, Amount = 100_000_000, Turnover = 10, OuterVolume = 2000, InnerVolume = 1000 },
                new HistoricalQuoteFile { TradingDate = date, Symbol = "600001", Name = "StockB", Price = 10.5, PreviousClose = 10.2, Amount = 200_000_000, Turnover = 10, OuterVolume = 3000, InnerVolume = 1000 }
            }).ToList(),
            Klines = new List<HistoricalKlineSeriesFile>
            {
                new HistoricalKlineSeriesFile
                {
                    Symbol = "600000",
                    Bars = dates.Select((date, i) => new HistoricalKlineBarFile
                    {
                        Date = date.ToDateTime(TimeOnly.MinValue),
                        Open = 9.8 + i * 0.02,
                        High = 10.2 + i * 0.02,
                        Low = 9.7 + i * 0.02,
                        Close = 10.0 + i * 0.02,
                        Volume = 1000,
                        Amount = 10000
                    }).ToList()
                },
                new HistoricalKlineSeriesFile
                {
                    Symbol = "600001",
                    Bars = dates.Select((date, i) => new HistoricalKlineBarFile
                    {
                        Date = date.ToDateTime(TimeOnly.MinValue),
                        Open = 10.2 + i * 0.02,
                        High = 10.7 + i * 0.02,
                        Low = 10.1 + i * 0.02,
                        Close = 10.5 + i * 0.02,
                        Volume = 2000,
                        Amount = 20000
                    }).ToList()
                }
            }
        };
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-ReexecutionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string SolutionRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
