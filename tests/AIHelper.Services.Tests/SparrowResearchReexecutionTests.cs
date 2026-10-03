using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using static AIHelper.Core.Sparrow.ResearchReexecutionCheckCodes;
using static AIHelper.Core.Sparrow.ResearchReexecutionReasonCodes;
using static AIHelper.Core.Sparrow.ResearchReexecutionStatus;
using CheckStatus = AIHelper.Core.Sparrow.ResearchReexecutionCheckStatus;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchReexecutionTests
{
    private static readonly string[] CanonicalCheckCodes = { ReadinessVerified, DatasetLoaded, DatasetFingerprintMatch, ExecutableParametersLoaded, ExecutableParameterFingerprintMatch,
        BacktestRequestReconstructed, BacktestParameterFingerprintMatch, PortfolioRequestMatch, BacktestReexecuted, PortfolioReexecuted, PerformanceReanalyzed, TradeSequenceMatch,
        PositionSequenceMatch, EquityCurveMatch, AttributionMatch, PerformanceSummaryMatch, StrategyFingerprintMatch, PortfolioConfigurationFingerprintMatch,
        AnalysisFingerprintMatch, ArtifactFingerprintMatch };

    // Backtest costs (SparrowBacktestRequest) are distinct from portfolio costs (PortfolioSimulationRequest).
    private const double BtCost = 0.001, BtSlippage = 0.002;
    private const decimal PortfolioCommission = 0.0003m, PortfolioSlippage = 0.0004m;

    private static Task<ResearchReexecutionValidationResult> RunValAsync(Fixture f, string? art = null, string? dat = null, string? par = null, PersistedResearchExperimentRecord? rec = null, double btCost = BtCost, double btSlip = BtSlippage) =>
        new SparrowResearchReexecutionValidator().ValidateReexecutionAsync(rec ?? f.Record, art ?? f.ArtifactPath, dat ?? f.DatasetPath, par ?? f.ParametersPath, btCost, btSlip);

    [Fact]
    public Task ValidateReexecutionAsync_ValidEvidence_ReturnsEquivalentAndAllChecksPass() =>
        WithFixtureAsync(async (f, _) => {
            var result = await RunValAsync(f); Assert.Equal(Equivalent, result.Status);
            Assert.Equal(20, result.CheckCount); Assert.Equal(20, result.PassedCheckCount);
            Assert.Equal(0, result.FailedCheckCount); Assert.Equal(0, result.UnsupportedCheckCount);
            Assert.Empty(result.ReasonCodes); Assert.Equal(f.Record.ExperimentId, result.ExperimentId);
            Assert.Equal(f.Artifact.ArtifactFingerprint, result.OriginalArtifactFingerprint);
            Assert.Equal(f.Artifact.ArtifactFingerprint, result.ReproducedArtifactFingerprint); Assert.Equal(f.LoadedDataset.Fingerprint, result.DatasetFingerprint);
            Assert.Equal(CanonicalCheckCodes, result.Checks.Select(c => c.Code)); Assert.All(result.Checks, check => Assert.Equal(CheckStatus.Pass, check.Status));
        });

    [Fact]
    public Task ValidateReexecutionAsync_MissingParameters_FailsWithoutUsingDefaults() =>
        WithFixtureAsync(async (f, _) => {
            var result = await new SparrowResearchReexecutionValidator().ValidateReexecutionAsync(f.Record, f.ArtifactPath, f.DatasetPath, parametersPath: null, BtCost, BtSlippage);
            Assert.Equal(Failed, result.Status); Assert.Contains(result.ReasonCodes, r => r == ExecutableParametersMissing);
            var check = Assert.Single(result.Checks, c => c.Code == ExecutableParametersLoaded); Assert.Equal(CheckStatus.Fail, check.Status);
            Assert.Contains("defaults is prohibited", check.Message); Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_WrongParameters_FailsBeforeExecution() =>
        WithFixtureAsync(async (f, dir) => {
            string wrongParamPath = Path.Combine(dir, "wrong-parameters.json");
            await File.WriteAllTextAsync(wrongParamPath, JsonSerializer.Serialize(new SparrowClassicParameterSnapshot(true, 2, 10, 2.0, 5, false, 1, .30)));
            var result = await RunValAsync(f, par: wrongParamPath); Assert.Equal(Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ExecutableParameterFingerprintMismatch);
            Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, c => c.Code == ExecutableParameterFingerprintMatch).Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_WrongDataset_FailsBeforeExecution() =>
        WithFixtureAsync(async (f, dir) => {
            string wrongDatasetPath = Path.Combine(dir, "wrong-dataset.json");
            await File.WriteAllTextAsync(wrongDatasetPath, JsonSerializer.Serialize(CreateDatasetFile("wrong-dataset-id")));
            var result = await RunValAsync(f, dat: wrongDatasetPath); Assert.Equal(Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == DatasetFingerprintMismatch);
            Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, c => c.Code == DatasetFingerprintMatch).Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_ReadinessFailure_ShortCircuitsBeforeExecution() =>
        WithFixtureAsync(async (f, dir) => {
            string tampered = (await File.ReadAllTextAsync(f.ArtifactPath)).Replace(f.Artifact.DatasetFingerprint, "tampered-dataset-fp");
            string tamperedPath = Path.Combine(dir, "tampered-artifact.json");
            await File.WriteAllTextAsync(tamperedPath, tampered);
            var result = await RunValAsync(f, art: tamperedPath); Assert.Equal(Failed, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ReadinessVerificationFailed);
            Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, c => c.Code == ReadinessVerified).Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_LegacyRecord_ReturnsUnsupported() =>
        WithFixtureAsync(async (f, _) => {
            var r = f.Record;
            var legacyRecord = new PersistedResearchExperimentRecord(r.ExperimentId, r.ExperimentFingerprint, r.Definition, r.ExecutionSummary, r.ArtifactReference, r.Lineage, r.CreatedAt, PersistedResearchExperimentRecord.LegacySchemaVersion, null);
            var result = await RunValAsync(f, rec: legacyRecord); Assert.Equal(Unsupported, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ReadinessVerificationUnsupported);
            Assert.Equal(CheckStatus.Unsupported, Assert.Single(result.Checks, c => c.Code == ReadinessVerified).Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_LegacyArtifact_ReturnsUnsupported() =>
        WithFixtureAsync(async (f, dir) => {
            string v1Json = (await File.ReadAllTextAsync(f.ArtifactPath)).Replace(SparrowPortfolioResearchArtifact.CurrentArtifactVersion, SparrowPortfolioResearchArtifact.LegacyArtifactVersion);
            string v1Path = Path.Combine(dir, "v1-artifact.json");
            await File.WriteAllTextAsync(v1Path, v1Json);
            var result = await RunValAsync(f, art: v1Path); Assert.Equal(Unsupported, result.Status);
            Assert.Contains(result.ReasonCodes, r => r == ReadinessVerificationUnsupported);
            Assert.Equal(CheckStatus.Unsupported, Assert.Single(result.Checks, c => c.Code == ReadinessVerified).Status);
            Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_BacktestParameterIdentity_MatchesArtifactParameterFingerprint() =>
        WithFixtureAsync(async (f, _) => {
            var result = await RunValAsync(f); Assert.Equal(Equivalent, result.Status);
            var check = Assert.Single(result.Checks, c => c.Code == BacktestParameterFingerprintMatch); Assert.Equal(CheckStatus.Pass, check.Status);
            Assert.Equal(f.Artifact.PortfolioRequest.StrategyParameterFingerprint, check.Expected);
            Assert.Equal(f.Artifact.PortfolioRequest.StrategyParameterFingerprint, check.Actual);
        });

    [Fact]
    public Task ValidateReexecutionAsync_NonZeroBacktestCosts_ReproducesExactly() =>
        WithFixtureAsync(async (f, _) => {
            Assert.NotEqual(0, BtCost); Assert.NotEqual(0, BtSlippage);
            var result = await RunValAsync(f); Assert.Equal(Equivalent, result.Status);
            Assert.Equal(f.Artifact.ArtifactFingerprint, result.OriginalArtifactFingerprint);
            Assert.Equal(f.Artifact.ArtifactFingerprint, result.ReproducedArtifactFingerprint);
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task ValidateReexecutionAsync_WrongBacktestCost_FailsBeforeExecution(bool wrongRoundTrip) =>
        WithFixtureAsync(async (f, _) => {
            var result = wrongRoundTrip ? await RunValAsync(f, btCost: BtCost + 0.005) : await RunValAsync(f, btSlip: BtSlippage + 0.005);
            Assert.Equal(Failed, result.Status); Assert.Contains(result.ReasonCodes, r => r == ExecutableParameterFingerprintMismatch);
            Assert.DoesNotContain(result.Checks, c => c.Code == BacktestReexecuted);
        });

    [Fact]
    public Task ValidateReexecutionAsync_NonZeroPortfolioCommission_IsNotRejected() =>
        WithFixtureAsync(async (f, _) => {
            Assert.True(f.Artifact.PortfolioRequest.CommissionRate > 0);
            var result = await RunValAsync(f); Assert.Equal(Equivalent, result.Status);
            Assert.Equal(f.Artifact.PortfolioRequest.StrategyParameterFingerprint, result.StrategyParameterFingerprint);
        });

    [Fact]
    public Task ValidateReexecutionAsync_NonZeroPortfolioSlippage_IsNotRejected() =>
        WithFixtureAsync(async (f, _) => {
            Assert.True(f.Artifact.PortfolioRequest.SlippageRate > 0);
            var result = await RunValAsync(f); Assert.Equal(Equivalent, result.Status);
            Assert.Equal(f.Artifact.PortfolioConfigurationFingerprint, result.PortfolioConfigurationFingerprint);
        });

    [Fact]
    public Task ValidateReexecutionAsync_BacktestAndPortfolioCostsDiffer_ReproducesExactly() =>
        WithFixtureAsync(async (f, _) => {
            Assert.NotEqual((decimal)BtCost, f.Artifact.PortfolioRequest.CommissionRate);
            Assert.NotEqual((decimal)BtSlippage, f.Artifact.PortfolioRequest.SlippageRate);
            var result = await RunValAsync(f); Assert.Equal(Equivalent, result.Status);
            Assert.Equal(f.Artifact.ArtifactFingerprint, result.ReproducedArtifactFingerprint);
        });

    [Theory]
    [InlineData("--backtest-slippage-rate", "0.002")]
    [InlineData("--backtest-round-trip-cost-rate", "0.001")]
    public async Task Cli_MissingBacktestCost_ExitsNonZeroWithoutDefault(string present, string value)
    {
        var psi = CreateCliStartInfo("--reproduce-experiment", "EXP-001", "--experiment-store", "./experiments", "--artifact", "./a.json", "--dataset", "./d.json", "--parameters", "./p.json", present, value);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start CLI.");
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(); Assert.NotEqual(0, proc.ExitCode);
        Assert.Contains("MISSING_BACKTEST_COST_INPUT", stderr);
    }

    [Fact]
    public void ValidateArtifactEquivalence_DivergedTrades_ReturnsDivergedWithDiagnostic()
    {
        var original = CreateMockArtifact();
        var divergedTrades = original.Trades.Select((t, i) => i == 0 ? new PortfolioTrade(t.Symbol, t.TradeDate, t.Side, t.Price + 1, t.Quantity, t.Notional + 1, t.Fee) : t).ToArray();
        var result = new SparrowResearchReexecutionValidator().ValidateArtifactEquivalence(null, original, CreateMockArtifact(trades: divergedTrades));
        Assert.Equal(Diverged, result.Status); Assert.Contains(result.ReasonCodes, r => r == TradeSequenceDiverged);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, c => c.Code == TradeSequenceMatch).Status);
    }

    [Fact]
    public void ValidateArtifactEquivalence_DivergedEquityCurve_ReturnsDivergedWithDiagnostic()
    {
        var original = CreateMockArtifact();
        var divergedPoints = original.EquityCurve.Select((p, i) => i == 0 ? new PortfolioEquityPoint(p.Date, p.Cash + 10, p.MarketValue, p.TotalEquity + 10, p.DailyReturn, p.CumulativeReturn) : p).ToArray();
        var result = new SparrowResearchReexecutionValidator().ValidateArtifactEquivalence(null, original, CreateMockArtifact(equityCurve: divergedPoints));
        Assert.Equal(Diverged, result.Status); Assert.Contains(result.ReasonCodes, r => r == EquityCurveDiverged);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, c => c.Code == EquityCurveMatch).Status);
    }

    [Fact]
    public void ValidateArtifactEquivalence_DivergedPerformanceMetrics_ReturnsDivergedWithDiagnostic()
    {
        var original = CreateMockArtifact();
        var m = original.PerformanceSummary;
        PortfolioPerformanceMetrics divergedMetrics = new(m.InitialCapital, m.FinalEquity + 100, m.TotalReturnPercent + 0.1, m.MaximumDrawdownPercent, m.MaximumDrawdownDate, m.TradeCount, m.WinningTradeCount, m.LosingTradeCount, m.WinRate);
        var result = new SparrowResearchReexecutionValidator().ValidateArtifactEquivalence(null, original, CreateMockArtifact(metrics: divergedMetrics));
        Assert.Equal(Diverged, result.Status); Assert.Contains(result.ReasonCodes, r => r == PerformanceSummaryDiverged);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, c => c.Code == PerformanceSummaryMatch).Status);
    }

    [Fact]
    public Task ValidateReexecutionAsync_IsDeterministic_AcrossTwoExecutions() =>
        WithFixtureAsync(async (f, _) => {
            var a = await RunValAsync(f);
            var b = await RunValAsync(f); Assert.Equal(a.Status, b.Status);
            Assert.Equal(a.OriginalArtifactFingerprint, b.OriginalArtifactFingerprint);
            Assert.Equal(a.ReproducedArtifactFingerprint, b.ReproducedArtifactFingerprint); Assert.Equal(a.CheckCount, b.CheckCount);
            Assert.Equal(a.Checks.Select(c => (c.Code, c.Status, c.Message)), b.Checks.Select(c => (c.Code, c.Status, c.Message)));
        });

    [Fact]
    public Task ValidateReexecutionAsync_IsPathIndependent_WhenFilesMoved() =>
        WithFixtureAsync(async (f, dir1) => {
            string dir2 = TemporaryDirectory();
            try
            {
                string a = Path.Combine(dir2, "moved-a.json"), d = Path.Combine(dir2, "moved-d.json"), p = Path.Combine(dir2, "moved-p.json");
                File.Copy(f.ArtifactPath, a); File.Copy(f.DatasetPath, d); File.Copy(f.ParametersPath, p);
                var r = await RunValAsync(f, a, d, p); Assert.Equal(Equivalent, r.Status);
                Assert.Equal(f.Artifact.ArtifactFingerprint, r.ReproducedArtifactFingerprint);
            }
            finally { DeleteDirectory(dir2); }
        });

    [Fact]
    public Task Cli_Subprocess_WithoutGateway_ReturnsZeroAndEquivalent() =>
        WithFixtureAsync(async (f, _) => {
            var psi = CreateCliStartInfo("--reproduce-experiment", f.Record.ExperimentId, "--experiment-store", f.StorePath, "--artifact", f.ArtifactPath, "--dataset", f.DatasetPath, "--parameters", f.ParametersPath,
                "--backtest-round-trip-cost-rate", "0.001", "--backtest-slippage-rate", "0.002");
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(); Assert.Equal(0, proc.ExitCode);
            Assert.Contains("REEXECUTION_STATUS=Equivalent", stdout); Assert.Contains("BACKTEST_ROUND_TRIP_COST_RATE=0.001", stdout);
            Assert.Contains("BACKTEST_SLIPPAGE_RATE=0.002", stdout); Assert.Contains($"EXPERIMENT_ID={f.Record.ExperimentId}", stdout);
            Assert.Contains($"ORIGINAL_ARTIFACT_FINGERPRINT={f.Artifact.ArtifactFingerprint}", stdout);
            Assert.Contains($"REPRODUCED_ARTIFACT_FINGERPRINT={f.Artifact.ArtifactFingerprint}", stdout);
            Assert.Contains("CHECK=ARTIFACT_FINGERPRINT_MATCH:Pass", stdout);
        });

    [Fact]
    public async Task Cli_ConflictingModes_ExitsNonZeroWithConflictingModesMessage()
    {
        var psi = CreateCliStartInfo("--reproduce-experiment", "EXP-001", "--verify-reproducibility", "EXP-001");
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start CLI.");
        string stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(); Assert.NotEqual(0, proc.ExitCode);
        Assert.Contains("CONFLICTING_OPERATION_MODES", stderr);
    }

    private sealed record Fixture(PersistedResearchExperimentRecord Record, SparrowPortfolioResearchArtifact Artifact, HistoricalMarketDataset LoadedDataset,
        string StorePath, string ArtifactPath, string DatasetPath, string ParametersPath);

    private static async Task WithFixtureAsync(Func<Fixture, string, Task> action)
    {
        string dir = TemporaryDirectory();
        try { var f = await CreateExecutionFixtureAsync(dir); await action(f, dir); }
        finally { DeleteDirectory(dir); }
    }

    private static ProcessStartInfo CreateCliStartInfo(params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SolutionRoot() };
        psi.Environment.Remove("HISTORICAL_GATEWAY_URL");
        foreach (var a in new[] { "run", "--project", Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"),
            "--configuration", TestExecutionConfiguration.Current(), "--no-build", "--no-restore", "--" }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }

    private static SparrowPortfolioResearchArtifact CreateMockArtifact(PortfolioTrade[]? trades = null, PortfolioPosition[]? positions = null, PortfolioEquityPoint[]? equityCurve = null,
        PortfolioAttribution[]? attributions = null, PortfolioPerformanceMetrics? metrics = null)
    {
        DateOnly f = new(2026, 1, 2), s = new(2026, 1, 3);
        PortfolioSimulationRequest req = new("fixture-dataset", "dataset-fp", SparrowStrategyMode.V2, SparrowStrategyVersions.V2, "strategy-param-fp", f, s, 2, 1, 1_000, PortfolioPositionSizingMethod.EqualWeight, 0, 0);
        string sf = SparrowPortfolioResearchFingerprint.Strategy(req), pf = SparrowPortfolioResearchFingerprint.Portfolio(req);
        return new(SparrowPortfolioResearchArtifact.CurrentArtifactVersion, "artifact-fp-mock", "AIHelper.HistoricalDataTool", "dataset-fp", sf, pf,
            SparrowPortfolioResearchFingerprint.Analysis("dataset-fp", sf, pf), new(req.StrategyMode.ToString(), req.StrategyVersion, sf), req, new(2, 1, 1),
            metrics ?? new(1_000, 1_010, 1, -1, s, 2, 1, 1, .5), new("Unavailable", null, null, null),
            trades ?? new PortfolioTrade[] { new("600000", f, PortfolioTradeSide.Buy, 100, 1, 100, 0), new("600001", s, PortfolioTradeSide.Sell, 110, 1, 110, 0) },
            positions ?? new PortfolioPosition[] { new("600000", f, 100, 1, 100, 0, PortfolioPositionStatus.Closed, s, 110, 110, 0, 10, 10), new("600001", s, 100, 1, 100, 0, PortfolioPositionStatus.Open) },
            equityCurve ?? new[] { new PortfolioEquityPoint(f, 900, 100, 1_000, null, 0), new PortfolioEquityPoint(s, 910, 100, 1_010, .01, .01) },
            attributions ?? new PortfolioAttribution[] { new("600000", f, s, 1, 1, 10, 10, 1, true), new("600001", s, s, 0, 1, -5, -5, -.5, false) },
            new[] { "warning-a" }, SparrowPortfolioResearchArtifact.DefaultLimitations);
    }

    private static async Task<Fixture> CreateExecutionFixtureAsync(string directory)
    {
        string storePath = Path.Combine(directory, "experiments"), artifactPath = Path.Combine(directory, "artifact.json");
        string datasetPath = Path.Combine(directory, "dataset.json"), parametersPath = Path.Combine(directory, "parameters.json");
        await File.WriteAllTextAsync(datasetPath, JsonSerializer.Serialize(CreateDatasetFile("fixture-reexecution-dataset")));
        var dataset = (await new HistoricalDatasetJsonLoader().LoadAsync(datasetPath)).Dataset!;

        DateOnly s = dataset.TradingDates[60], e = dataset.TradingDates[65];
        SparrowClassicParameterSnapshot classicParams = new(false, 1, 5, 1.1, 1, true, 0, .15);
        await File.WriteAllTextAsync(parametersPath, JsonSerializer.Serialize(classicParams));

        var backtest = new SparrowHistoricalBacktestEngine().Run(dataset, new(SparrowStrategyMode.Classic, SparrowStrategyVersions.Classic, s, e, 2, new[] { 1 }, BtCost, BtSlippage, classicParams));
        var sim = await new SparrowPortfolioSimulationEngine().SimulateAsync(backtest, new(dataset.DatasetId, dataset.Fingerprint, SparrowStrategyMode.Classic, SparrowStrategyVersions.Classic, backtest.ParameterFingerprint, s, e, 2, 1, 1e6m, PortfolioPositionSizingMethod.EqualWeight, PortfolioCommission, PortfolioSlippage), dataset);
        var perf = await new SparrowPortfolioPerformanceAnalyzer().AnalyzeAsync(sim, dataset);
        var art = await new SparrowPortfolioResearchExporter().ExportAsync(perf, artifactPath);

        DateTimeOffset ep = DateTimeOffset.UnixEpoch;
        ResearchExperimentIdentity id = new("EXP-REPRO-001", ResearchExperimentIdentity.CurrentExperimentVersion, "operator-repro", ep);
        ExperimentParameterSnapshot fp = new("v1", new Dictionary<string, string> { ["MinRise"] = "1", ["MaxRise"] = "5" }, new Dictionary<string, string> { ["TopN"] = "2", ["Horizon"] = "1" }, new Dictionary<string, string> { ["Metric"] = "Return" });
        ResearchExperimentDefinition def = new(id, art.DatasetFingerprint, new(art.Strategy.Mode, art.Strategy.Version), fp, art.PortfolioConfigurationFingerprint, "analysis-config-001", "benchmark:CSI300", "Re-execution fixture experiment");
        var exec = ResearchExperimentExecution.Create(id).Start(ep).Complete(ep, art.ArtifactFingerprint);
        var summary = ResearchExperimentExecutionSummary.FromCompletedExecution(exec, new(art.PerformanceSummary.TotalReturnPercent, art.PerformanceSummary.MaximumDrawdownPercent, art.PerformanceSummary.TradeCount, art.PerformanceSummary.WinRate));

        PersistedResearchExperimentRecord record = new(id.ExperimentId, def.SemanticFingerprint, def, summary,
            new(art.ArtifactFingerprint, art.ArtifactVersion), new(def.SemanticFingerprint, def.DatasetFingerprint, fp.Fingerprint, art.ArtifactFingerprint),
            ep, PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion, ResearchExecutionProvenanceBinding.Create(def, art));

        await new JsonResearchExperimentRepository(storePath).SaveAsync(record);
        return new(record, art, dataset, storePath, artifactPath, datasetPath, parametersPath);
    }

    private static HistoricalDatasetFile CreateDatasetFile(string id = "fixture-dataset")
    {
        DateOnly start = new(2026, 1, 1);
        DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        return new()
        {
            SchemaVersion = HistoricalDatasetJsonLoader.LegacySchemaVersion, DatasetId = id, Source = "Test", PriceAdjustmentMode = "ForwardAdjusted", TradingDates = dates.ToList(),
            Capabilities = HistoricalDataCapabilities.Complete, Quotes = new(),
            Klines = new[] { "600000", "600001" }.Select(sym => new HistoricalKlineSeriesFile { Symbol = sym, Bars = dates.Select((d, i) => new HistoricalKlineBarFile {
                Date = d.ToDateTime(TimeOnly.MinValue), Open = 9.8 + i * 0.02, High = 10.2 + i * 0.02, Low = 9.7 + i * 0.02, Close = 10.0 + i * 0.02, Volume = 1000, Amount = 10000 }).ToList() }).ToList()
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

    private static string SolutionRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
