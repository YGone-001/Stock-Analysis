using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using static AIHelper.Core.Sparrow.ResearchReexecutionCheckCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceReasonCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceVerificationStatus;

namespace AIHelper.Services.Tests;

public sealed class ManagedResearchExperimentTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Tree = "89abcdef0123456789abcdef0123456789abcdef";
    private const string ToolMvid = "11111111-1111-1111-1111-111111111111";
    private const string ServicesMvid = "22222222-2222-2222-2222-222222222222";
    private const string CoreMvid = "33333333-3333-3333-3333-333333333333";
    private const double BacktestCost = 0.001, BacktestSlippage = 0.002;
    private const decimal PortfolioCommission = 0.0003m, PortfolioSlippage = 0.0004m;

    private static ResearchSourceBuildProvenance FixedProvenance() => new(
        ResearchSourceBuildProvenance.CurrentProvenanceVersion, Commit, Tree, ResearchSourceBuildProvenance.CleanSourceState,
        "Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid);

    // ---- end-to-end -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("classic")]
    [InlineData("v2")]
    public async Task ManagedExecution_ProducesEvidenceConsumableByEveryDownstreamLayer(string mode)
    {
        Harness harness = await CreateHarnessAsync(mode);
        try
        {
            ManagedResearchExperimentResult result = await Runner().RunAsync(harness.Request);

            Assert.Equal(harness.Request.ExperimentId, result.ExperimentId);
            Assert.Equal(SparrowPortfolioResearchArtifact.CurrentArtifactVersion, result.ArtifactVersion);
            Assert.Equal(PersistedResearchExperimentRecord.CurrentSchemaVersion, result.ExperimentRecordSchemaVersion);
            Assert.Equal(FixedProvenance().ProvenanceFingerprint, result.SourceBuildProvenanceFingerprint);
            Assert.True(File.Exists(harness.ArtifactPath));

            // Phase 3.7 query compatibility.
            JsonResearchExperimentRepository repository = new(harness.StorePath);
            PersistedResearchExperimentRecord record = await repository.GetAsync(harness.Request.ExperimentId);
            Assert.Equal(PersistedResearchExperimentRecord.CurrentSchemaVersion, record.SchemaVersion);
            Assert.Equal(ResearchExecutionProvenanceBinding.CurrentBindingVersion, record.ExecutionProvenanceBinding!.BindingVersion);
            Assert.Equal(FixedProvenance().ProvenanceFingerprint, record.SourceBuildProvenance!.ProvenanceFingerprint);
            Assert.Equal(result.ExecutionBindingFingerprint, record.ExecutionProvenanceBinding.BindingFingerprint);
            ResearchExperimentHistory history = await repository.ListAsync();
            Assert.Contains(history.Experiments, item => string.Equals(item.ExperimentId, record.ExperimentId, StringComparison.Ordinal));

            // Phase 3.8 reporting compatibility.
            ResearchExperimentReportBuilder reports = new();
            Assert.Equal(record.ExperimentId, reports.BuildExperimentReport(record).ExperimentId);
            Assert.Equal(record.ExperimentId, reports.BuildLineageReport(record).ExperimentId);

            // Strategy parameter identity chain: the result reports the artifact-side executable parameter
            // fingerprint, never the distinct higher-level strategy wrapper fingerprint.
            SparrowPortfolioResearchArtifact artifact = await ReadArtifactAsync(harness.ArtifactPath);
            Assert.Equal(artifact.PortfolioRequest.StrategyParameterFingerprint, result.StrategyParameterFingerprint);
            Assert.Equal(record.ExecutionProvenanceBinding.ArtifactStrategyParameterFingerprint, result.StrategyParameterFingerprint);
            Assert.NotEqual(artifact.StrategyFingerprint, result.StrategyParameterFingerprint);
            Assert.Equal(SparrowPortfolioResearchFingerprint.Strategy(artifact.PortfolioRequest), artifact.StrategyFingerprint);

            // Phase 3.9 readiness compatibility.
            ResearchReproducibilityVerificationResult readiness = await new SparrowResearchReproducibilityVerifier().VerifyAsync(record, harness.ArtifactPath);
            Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, readiness.Status);

            // Phase 3.10 deterministic re-execution compatibility.
            ResearchReexecutionValidationResult reexecution = await new SparrowResearchReexecutionValidator()
                .ValidateReexecutionAsync(record, harness.ArtifactPath, harness.DatasetPath, harness.ParametersPath, BacktestCost, BacktestSlippage);
            Assert.Equal(ResearchReexecutionStatus.Equivalent, reexecution.Status);
            Assert.Equal(ResearchReexecutionCheckStatus.Pass,
                Assert.Single(reexecution.Checks, check => check.Code == ResearchReexecutionCheckCodes.ExecutableParameterFingerprintMatch).Status);
            Assert.Equal(ResearchReexecutionCheckStatus.Pass,
                Assert.Single(reexecution.Checks, check => check.Code == ResearchReexecutionCheckCodes.BacktestParameterFingerprintMatch).Status);

            // Phase 3.12 source/build provenance compatibility.
            ResearchSourceBuildProvenanceVerificationResult provenance = new ResearchSourceBuildProvenanceVerifier(new StubProvider(FixedProvenance())).Verify(record);
            Assert.Equal(Match, provenance.Status);
            Assert.Equal(0, provenance.FailedCheckCount);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("v2")]
    public async Task ManagedResult_StrategyParameterFingerprint_MatchesArtifactAndBinding(string mode)
    {
        Harness harness = await CreateHarnessAsync(mode);
        try
        {
            ManagedResearchExperimentResult result = await Runner().RunAsync(harness.Request);
            SparrowPortfolioResearchArtifact artifact = await ReadArtifactAsync(harness.ArtifactPath);
            PersistedResearchExperimentRecord record = await new JsonResearchExperimentRepository(harness.StorePath).GetAsync(harness.Request.ExperimentId);
            ResearchReexecutionValidationResult reexecution = await new SparrowResearchReexecutionValidator()
                .ValidateReexecutionAsync(record, harness.ArtifactPath, harness.DatasetPath, harness.ParametersPath, BacktestCost, BacktestSlippage);

            // Corrected chain: managed result == artifact portfolio parameter identity == binding == executed backtest identity.
            Assert.Equal(artifact.PortfolioRequest.StrategyParameterFingerprint, result.StrategyParameterFingerprint);
            Assert.Equal(record.ExecutionProvenanceBinding!.ArtifactStrategyParameterFingerprint, result.StrategyParameterFingerprint);
            Assert.Equal(ResearchReexecutionStatus.Equivalent, reexecution.Status);
            Assert.Equal(ResearchReexecutionCheckStatus.Pass, Assert.Single(reexecution.Checks, check => check.Code == BacktestParameterFingerprintMatch).Status);

            // The higher-level strategy wrapper identity stays a separate domain and still recomputes through the frozen contract.
            Assert.NotEqual(artifact.StrategyFingerprint, result.StrategyParameterFingerprint);
            Assert.Equal(artifact.Strategy.StrategyFingerprint, artifact.StrategyFingerprint);
            Assert.Equal(SparrowPortfolioResearchFingerprint.Strategy(artifact.PortfolioRequest), artifact.StrategyFingerprint);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task ManagedExecution_NonZeroSeparatedCosts_ReproduceExactly()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await Runner().RunAsync(harness.Request);
            PersistedResearchExperimentRecord record = await new JsonResearchExperimentRepository(harness.StorePath).GetAsync(harness.Request.ExperimentId);
            SparrowPortfolioResearchArtifact artifact = await ReadArtifactAsync(harness.ArtifactPath);

            Assert.NotEqual((decimal)BacktestCost, artifact.PortfolioRequest.CommissionRate);
            Assert.NotEqual((decimal)BacktestSlippage, artifact.PortfolioRequest.SlippageRate);
            Assert.Equal(PortfolioCommission, artifact.PortfolioRequest.CommissionRate);
            Assert.Equal(PortfolioSlippage, artifact.PortfolioRequest.SlippageRate);

            ResearchReexecutionValidationResult reexecution = await new SparrowResearchReexecutionValidator()
                .ValidateReexecutionAsync(record, harness.ArtifactPath, harness.DatasetPath, harness.ParametersPath, BacktestCost, BacktestSlippage);
            Assert.Equal(ResearchReexecutionStatus.Equivalent, reexecution.Status);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    // ---- parameter contract -----------------------------------------------------------------------------

    [Theory]
    [InlineData("classic")]
    [InlineData("v2")]
    public void ParameterMapping_RepresentsEveryTypedParameterProperty(string mode)
    {
        ExperimentParameterSnapshot snapshot = BuildSnapshot(mode);
        Type typed = mode == "v2" ? typeof(SparrowV2ParameterSnapshot) : typeof(SparrowClassicParameterSnapshot);
        string prefix = mode == "v2" ? "V2." : "Classic.";

        PropertyInfo[] properties = typed.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);
        foreach (PropertyInfo property in properties)
            Assert.Contains(prefix + property.Name, snapshot.StrategyParameters.Keys);
    }

    [Fact]
    public void ParameterMapping_CarriesEveryExecutionEnvelopeInput()
    {
        ExperimentParameterSnapshot snapshot = BuildSnapshot("classic");
        foreach (string key in ResearchExperimentParameterSnapshotFactory.StrategyEnvelopeKeys) Assert.Contains(key, snapshot.StrategyParameters.Keys);
        foreach (string key in ResearchExperimentParameterSnapshotFactory.PortfolioKeys) Assert.Contains(key, snapshot.PortfolioParameters.Keys);
        Assert.Equal(ResearchExperimentParameterSnapshotFactory.ParameterVersion, snapshot.ParameterVersion);
        Assert.Equal("PortfolioPerformanceAnalyzerV1", snapshot.AnalysisParameters["AnalyzerContract"]);
    }

    [Fact]
    public void ParameterSnapshot_IsDeterministic_AcrossCultureAndPaths()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ExperimentParameterSnapshot german = BuildSnapshot("v2");
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            ExperimentParameterSnapshot english = BuildSnapshot("v2");
            Assert.Equal(english.Fingerprint, german.Fingerprint);
            Assert.Equal(english.StrategyFingerprint, german.StrategyFingerprint);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    // ---- identity domains -------------------------------------------------------------------------------

    [Fact]
    public async Task ExperimentId_ChangesExperimentIdentityButNotArtifactIdentity()
    {
        Harness first = await CreateHarnessAsync("classic", "EXP-MANAGED-A");
        Harness second = await CreateHarnessAsync("classic", "EXP-MANAGED-B");
        try
        {
            ManagedResearchExperimentResult a = await Runner().RunAsync(first.Request);
            ManagedResearchExperimentResult b = await Runner().RunAsync(second.Request);

            Assert.NotEqual(a.ExperimentFingerprint, b.ExperimentFingerprint);
            Assert.Equal(a.ArtifactFingerprint, b.ArtifactFingerprint);
            Assert.Equal(a.DatasetFingerprint, b.DatasetFingerprint);
            Assert.Equal(a.PortfolioConfigurationFingerprint, b.PortfolioConfigurationFingerprint);
            Assert.Equal(a.AnalysisFingerprint, b.AnalysisFingerprint);
        }
        finally { DeleteDirectory(first.Root); DeleteDirectory(second.Root); }
    }

    [Fact]
    public async Task Paths_DoNotAffectSemanticIdentities()
    {
        Harness first = await CreateHarnessAsync("classic");
        Harness second = await CreateHarnessAsync("classic");
        try
        {
            ManagedResearchExperimentResult a = await Runner().RunAsync(first.Request);
            ManagedResearchExperimentResult b = await Runner().RunAsync(second.Request);

            Assert.NotEqual(first.Root, second.Root);
            Assert.Equal(a.ExperimentFingerprint, b.ExperimentFingerprint);
            Assert.Equal(a.ArtifactFingerprint, b.ArtifactFingerprint);
            Assert.Equal(a.ExecutionBindingFingerprint, b.ExecutionBindingFingerprint);
            Assert.Equal(a.SourceBuildProvenanceFingerprint, b.SourceBuildProvenanceFingerprint);
        }
        finally { DeleteDirectory(first.Root); DeleteDirectory(second.Root); }
    }

    // ---- persistence safety -----------------------------------------------------------------------------

    [Fact]
    public async Task DuplicateExperimentId_IsRejected_WithoutOverwrite()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await Runner().RunAsync(harness.Request);
            string artifactBefore = await File.ReadAllTextAsync(harness.ArtifactPath);
            string recordBefore = await File.ReadAllTextAsync(Path.Combine(harness.StorePath, harness.Request.ExperimentId + ".json"));

            string secondArtifact = Path.Combine(harness.Root, "second-artifact.json");
            ManagedResearchExperimentRequest duplicate = Clone(harness.Request, artifactOutputPath: secondArtifact);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Runner().RunAsync(duplicate));

            Assert.False(File.Exists(secondArtifact));
            Assert.Equal(artifactBefore, await File.ReadAllTextAsync(harness.ArtifactPath));
            Assert.Equal(recordBefore, await File.ReadAllTextAsync(Path.Combine(harness.StorePath, harness.Request.ExperimentId + ".json")));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task ExistingArtifactOutput_IsRejected_AndLeftUnchanged()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await File.WriteAllTextAsync(harness.ArtifactPath, "pre-existing-evidence");
            await Assert.ThrowsAsync<IOException>(() => Runner().RunAsync(harness.Request));

            Assert.Equal("pre-existing-evidence", await File.ReadAllTextAsync(harness.ArtifactPath));
            Assert.False(Directory.Exists(harness.StorePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task ArtifactWriteFailure_LeavesNoRecord()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            List<string> events = new();
            ManagedResearchExperimentRunner runner = Runner(new RecordingArtifactWriter(events, failPublish: true));
            await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(harness.Request));

            Assert.False(File.Exists(harness.ArtifactPath));
            Assert.False(File.Exists(Path.Combine(harness.StorePath, harness.Request.ExperimentId + ".json")));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task RecordSaveFailure_RemovesNewArtifact_AndLeavesNoRecord()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            List<string> events = new();
            RecordingArtifactWriter writer = new(events);
            ManagedResearchExperimentRunner runner = new(new StubProvider(FixedProvenance()), writer, new FixedTimeProvider(DateTimeOffset.UnixEpoch),
                store => new FailingRepository(new JsonResearchExperimentRepository(store)));

            await Assert.ThrowsAsync<IOException>(() => runner.RunAsync(harness.Request));

            Assert.True(writer.Deleted);
            Assert.False(File.Exists(harness.ArtifactPath));
            Assert.False(File.Exists(Path.Combine(harness.StorePath, harness.Request.ExperimentId + ".json")));
            Assert.Equal(new[] { "publish", "cleanup" }, events);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task ProvenanceCapture_HappensBeforePublishAndRecordSave()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            List<string> events = new();
            ManagedResearchExperimentRunner runner = Runner(new RecordingArtifactWriter(events), events);
            await runner.RunAsync(harness.Request);

            Assert.Equal(new[] { "capture", "publish" }, events);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task DirtySource_BlocksExecution_BeforeResearchAndOutput()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            List<string> events = new();
            RecordingArtifactWriter writer = new(events);
            ResearchSourceBuildProvenanceProvider provider = new(
                new StubGitReader(() => throw new ResearchSourceBuildProvenanceException(SourceWorktreeNotClean, SourceWorktreeNotClean)),
                new StubBuildReader(new ResearchBuildIdentity("Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid)));
            ManagedResearchExperimentRunner runner = new(provider, writer, new FixedTimeProvider(DateTimeOffset.UnixEpoch));

            ResearchSourceBuildProvenanceException exception = await Assert.ThrowsAsync<ResearchSourceBuildProvenanceException>(() => runner.RunAsync(harness.Request));
            Assert.Equal(SourceWorktreeNotClean, exception.ReasonCode);
            Assert.Empty(events);
            Assert.False(File.Exists(harness.ArtifactPath));
            Assert.False(Directory.Exists(harness.StorePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    // ---- CLI --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cli_ManagedExperiment_RunsOfflineAndRequiresExplicitInputs()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            bool clean = RunGit("status", "--porcelain=v1", "--untracked-files=all") is { Length: 0 };
            string[] required =
            {
                "--run-research-experiment", harness.Request.ExperimentId,
                "--experiment-store", harness.StorePath,
                "--dataset", harness.DatasetPath,
                "--parameters", harness.ParametersPath,
                "--strategy", "classic",
                "--start", "2026-03-01", "--end", "2026-03-10",
                "--top-n", "2", "--horizon", "1",
                "--backtest-round-trip-cost-rate", "0.001", "--backtest-slippage-rate", "0.002",
                "--initial-capital", "1000000",
                "--portfolio-commission-rate", "0.0003", "--portfolio-slippage-rate", "0.0004",
                "--position-sizing", "EqualWeight", "--execution-model", "CloseBased",
                "--output", harness.ArtifactPath
            };

            (int exitCode, string stdout) = await RunCliAsync(required);
            if (clean)
            {
                Assert.Equal(0, exitCode);
                Assert.Contains("MANAGED_EXPERIMENT_STATUS=Completed", stdout);
                Assert.Contains($"EXPERIMENT_ID={harness.Request.ExperimentId}", stdout);
                Assert.Contains("ARTIFACT_VERSION=portfolio-research-v2", stdout);
                Assert.Contains("EXPERIMENT_RECORD_VERSION=research-experiment-record-v3", stdout);
                Assert.Contains("SOURCE_BUILD_PROVENANCE_FINGERPRINT=", stdout);
                Assert.Contains("EXECUTION_BINDING_FINGERPRINT=", stdout);
            }
            else
            {
                Assert.NotEqual(0, exitCode);
                Assert.False(File.Exists(harness.ArtifactPath));
            }
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Cli_ManagedExperiment_EmitsArtifactStrategyParameterFingerprint()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            (int exitCode, string stdout) = await RunCliAsync(
                "--run-research-experiment", harness.Request.ExperimentId,
                "--experiment-store", harness.StorePath,
                "--dataset", harness.DatasetPath,
                "--parameters", harness.ParametersPath,
                "--strategy", "classic",
                "--start", "2026-03-01", "--end", "2026-03-10",
                "--top-n", "2", "--horizon", "1",
                "--backtest-round-trip-cost-rate", "0.001", "--backtest-slippage-rate", "0.002",
                "--initial-capital", "1000000",
                "--portfolio-commission-rate", "0.0003", "--portfolio-slippage-rate", "0.0004",
                "--position-sizing", "EqualWeight", "--execution-model", "CloseBased",
                "--output", harness.ArtifactPath);

            if (exitCode != 0)
            {
                // Managed execution requires a clean worktree, so a dirty checkout must refuse instead of emitting identity.
                Assert.False(File.Exists(harness.ArtifactPath));
                return;
            }

            string emitted = Line(stdout, "STRATEGY_PARAMETER_FINGERPRINT");
            SparrowPortfolioResearchArtifact artifact = await ReadArtifactAsync(harness.ArtifactPath);
            PersistedResearchExperimentRecord record = await new JsonResearchExperimentRepository(harness.StorePath).GetAsync(harness.Request.ExperimentId);

            Assert.Equal(artifact.PortfolioRequest.StrategyParameterFingerprint, emitted);
            Assert.Equal(record.ExecutionProvenanceBinding!.ArtifactStrategyParameterFingerprint, emitted);
            Assert.NotEqual(artifact.StrategyFingerprint, emitted);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Cli_ManagedExperiment_RejectsMissingParametersAndCosts()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string[] baseArguments =
            {
                "--run-research-experiment", harness.Request.ExperimentId,
                "--experiment-store", harness.StorePath,
                "--dataset", harness.DatasetPath,
                "--strategy", "classic",
                "--start", "2026-03-01", "--end", "2026-03-10",
                "--top-n", "2", "--horizon", "1",
                "--backtest-round-trip-cost-rate", "0.001", "--backtest-slippage-rate", "0.002",
                "--initial-capital", "1000000",
                "--portfolio-commission-rate", "0.0003", "--portfolio-slippage-rate", "0.0004",
                "--position-sizing", "EqualWeight", "--execution-model", "CloseBased",
                "--output", harness.ArtifactPath
            };

            (int withoutParameters, _) = await RunCliAsync(baseArguments);
            Assert.NotEqual(0, withoutParameters);

            string[] withoutCosts = baseArguments.Where(argument => argument is not ("--backtest-slippage-rate" or "0.002")).ToArray();
            (int missingCost, _) = await RunCliAsync(withoutCosts);
            Assert.NotEqual(0, missingCost);

            Assert.False(File.Exists(harness.ArtifactPath));
            Assert.False(Directory.Exists(harness.StorePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Cli_ManagedExperiment_ConflictsWithOtherModes()
    {
        (int exitCode, _) = await RunCliAsync("--run-research-experiment", "EXP-1", "--export-portfolio-research", "true");
        Assert.NotEqual(0, exitCode);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static ExperimentParameterSnapshot BuildSnapshot(string mode) => ResearchExperimentParameterSnapshotFactory.Create(
        mode == "v2" ? SparrowStrategyMode.V2 : SparrowStrategyMode.Classic,
        mode == "v2" ? SparrowStrategyVersions.V2 : SparrowStrategyVersions.Classic,
        new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10), 2, 1, BacktestCost, BacktestSlippage,
        mode == "v2" ? null : new SparrowClassicParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15),
        mode == "v2" ? new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 0.5, 3.0, 0.02, true) : null,
        1_000_000m, PortfolioPositionSizingMethod.EqualWeight, PortfolioCommission, PortfolioSlippage, PortfolioExecutionModel.CloseBased);

    private static ManagedResearchExperimentRunner Runner(
        IManagedResearchArtifactWriter? writer = null,
        List<string>? events = null) => new(
        events is null ? new StubProvider(FixedProvenance()) : new RecordingProvenanceProvider(events, FixedProvenance),
        writer,
        new FixedTimeProvider(DateTimeOffset.UnixEpoch));

    private static ManagedResearchExperimentRequest Clone(ManagedResearchExperimentRequest source, string artifactOutputPath) => new(
        source.ExperimentId, source.DatasetPath, source.ParameterSnapshotPath, source.StrategyMode, source.StartDate, source.EndDate,
        source.TopN, source.HorizonTradingDays, source.BacktestRoundTripCostRate, source.BacktestSlippageRate, source.InitialCapital,
        source.PositionSizingMethod, source.CommissionRate, source.PortfolioSlippageRate, source.ExecutionModel, artifactOutputPath, source.ExperimentStore);

    private sealed record Harness(string Root, string DatasetPath, string ParametersPath, string StorePath, string ArtifactPath, ManagedResearchExperimentRequest Request);

    private static async Task<Harness> CreateHarnessAsync(string mode, string experimentId = "EXP-MANAGED-001")
    {
        string root = TemporaryDirectory();
        string datasetPath = Path.Combine(root, "dataset.json");
        string parametersPath = Path.Combine(root, "parameters.json");
        string storePath = Path.Combine(root, "experiments");
        string artifactPath = Path.Combine(root, "artifact.json");
        await File.WriteAllTextAsync(datasetPath, JsonSerializer.Serialize(CreateDatasetFile("managed-dataset")));
        await File.WriteAllTextAsync(parametersPath, mode == "v2"
            ? JsonSerializer.Serialize(new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 0.5, 3.0, 0.02, true))
            : JsonSerializer.Serialize(new SparrowClassicParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15)));

        ManagedResearchExperimentRequest request = new(experimentId, datasetPath, parametersPath,
            mode == "v2" ? SparrowStrategyMode.V2 : SparrowStrategyMode.Classic,
            new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10), 2, 1, BacktestCost, BacktestSlippage,
            1_000_000m, PortfolioPositionSizingMethod.EqualWeight, PortfolioCommission, PortfolioSlippage, PortfolioExecutionModel.CloseBased,
            artifactPath, storePath);
        return new Harness(root, datasetPath, parametersPath, storePath, artifactPath, request);
    }

    private static async Task<SparrowPortfolioResearchArtifact> ReadArtifactAsync(string path) =>
        JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static string Line(string stdout, string key)
    {
        string? match = stdout.Split('\n').Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal));
        Assert.NotNull(match);
        return match![(key.Length + 1)..];
    }

    private static HistoricalDatasetFile CreateDatasetFile(string id)
    {
        DateOnly start = new(2026, 1, 1);
        DateOnly[] dates = Enumerable.Range(0, 90).Select(index => start.AddDays(index)).ToArray();
        return new()
        {
            SchemaVersion = HistoricalDatasetJsonLoader.LegacySchemaVersion, DatasetId = id, Source = "Test", PriceAdjustmentMode = "ForwardAdjusted",
            TradingDates = dates.ToList(), Capabilities = HistoricalDataCapabilities.Complete, Quotes = new(),
            Klines = new[] { "600000", "600001" }.Select(symbol => new HistoricalKlineSeriesFile
            {
                Symbol = symbol,
                Bars = dates.Select((date, index) => new HistoricalKlineBarFile
                {
                    Date = date.ToDateTime(TimeOnly.MinValue), Open = 9.8 + index * 0.02, High = 10.2 + index * 0.02,
                    Low = 9.7 + index * 0.02, Close = 10.0 + index * 0.02, Volume = 1000, Amount = 10000
                }).ToList()
            }).ToList()
        };
    }

    private static async Task<(int ExitCode, string Stdout)> RunCliAsync(params string[] args)
    {
        using Process process = Process.Start(CreateCliStartInfo(args)) ?? throw new InvalidOperationException("Could not start CLI.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout);
    }

    private static ProcessStartInfo CreateCliStartInfo(params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SolutionRoot() };
        psi.Environment.Remove("HISTORICAL_GATEWAY_URL");
        foreach (var a in new[] { "run", "--project", Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"),
            "--configuration", TestExecutionConfiguration.Current(), "--no-build", "--no-restore", "--" }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string? RunGit(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = SolutionRoot() };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-ManagedExperimentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string SolutionRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubProvider : IResearchSourceBuildProvenanceProvider
    {
        private readonly Func<ResearchSourceBuildProvenance> _capture;
        public StubProvider(ResearchSourceBuildProvenance provenance) : this(() => provenance) { }
        public StubProvider(Func<ResearchSourceBuildProvenance> capture) => _capture = capture;
        public ResearchSourceBuildProvenance Capture() => _capture();
    }

    private sealed class RecordingProvenanceProvider : IResearchSourceBuildProvenanceProvider
    {
        private readonly List<string> _events;
        private readonly Func<ResearchSourceBuildProvenance> _capture;
        public RecordingProvenanceProvider(List<string> events, Func<ResearchSourceBuildProvenance> capture) { _events = events; _capture = capture; }
        public ResearchSourceBuildProvenance Capture() { _events.Add("capture"); return _capture(); }
    }

    private sealed class StubGitReader(Func<GitSourceSnapshot> read) : IGitSourceSnapshotReader
    {
        public GitSourceSnapshot ReadAuthoritative() => read();
    }

    private sealed class StubBuildReader(ResearchBuildIdentity identity) : IResearchBuildIdentityReader
    {
        public ResearchBuildIdentity Read() => identity;
    }

    private sealed class RecordingArtifactWriter : IManagedResearchArtifactWriter
    {
        private readonly List<string> _events;
        private readonly bool _failPublish;
        private readonly AtomicManagedResearchArtifactWriter _inner = new();

        public RecordingArtifactWriter(List<string> events, bool failPublish = false) { _events = events; _failPublish = failPublish; }

        public bool Deleted { get; private set; }

        public async Task PublishNewAsync(string artifactJson, string outputPath, CancellationToken cancellationToken = default)
        {
            _events.Add("publish");
            if (_failPublish) throw new IOException("Injected artifact publish failure.");
            await _inner.PublishNewAsync(artifactJson, outputPath, cancellationToken);
        }

        public void TryDeleteOwnedArtifact(string outputPath)
        {
            _events.Add("cleanup");
            Deleted = true;
            _inner.TryDeleteOwnedArtifact(outputPath);
        }
    }

    private sealed class FailingRepository(IResearchExperimentRepository inner) : IResearchExperimentRepository
    {
        public Task SaveAsync(PersistedResearchExperimentRecord experiment, CancellationToken cancellationToken = default) =>
            throw new IOException("Injected record save failure.");

        public Task<PersistedResearchExperimentRecord> GetAsync(string experimentId, CancellationToken cancellationToken = default) => inner.GetAsync(experimentId, cancellationToken);
        public Task<ResearchExperimentHistory> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task DeleteAsync(string experimentId, CancellationToken cancellationToken = default) => inner.DeleteAsync(experimentId, cancellationToken);
    }
}
