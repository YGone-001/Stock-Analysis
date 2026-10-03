using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceCheckCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceReasonCodes;
using static AIHelper.Core.Sparrow.ResearchSourceBuildProvenanceVerificationStatus;
using CheckStatus = AIHelper.Core.Sparrow.ResearchReproducibilityCheckStatus;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchSourceBuildProvenanceTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Tree = "89abcdef0123456789abcdef0123456789abcdef";
    private const string ToolMvid = "11111111-1111-1111-1111-111111111111";
    private const string ServicesMvid = "22222222-2222-2222-2222-222222222222";
    private const string CoreMvid = "33333333-3333-3333-3333-333333333333";

    /// <summary>Independent pre-Phase-3.12 value for the frozen binding v1 preimage with the fixed inputs used below.</summary>
    private const string PinnedV1BindingFingerprint = "4D037499DD590A75363D42AC385BDFFFF7A70466AEAE062FB6AD8E9308044260";

    private static readonly string[] CanonicalCheckCodes =
    {
        SourceBuildProvenancePresent, SourceBuildProvenanceFingerprintValid, SourceBuildBindingValid,
        SourceCommitMatch, SourceTreeMatch, SourceStateMatch, BuildConfigurationMatch, TargetFrameworkMatch,
        ToolModuleIdentityMatch, ServicesModuleIdentityMatch, CoreModuleIdentityMatch
    };

    private static ResearchSourceBuildProvenance Provenance(
        string commit = Commit, string tree = Tree, string configuration = "Release", string targetFramework = ".NETCoreApp,Version=v10.0",
        string tool = ToolMvid, string services = ServicesMvid, string core = CoreMvid) =>
        new(ResearchSourceBuildProvenance.CurrentProvenanceVersion, commit, tree, ResearchSourceBuildProvenance.CleanSourceState,
            configuration, targetFramework, tool, services, core);

    // ---- provenance model -------------------------------------------------------------------------------

    [Fact]
    public void Provenance_SameObservedInputs_ProduceSameFingerprint()
    {
        Assert.Equal(Provenance().ProvenanceFingerprint, Provenance().ProvenanceFingerprint);
        Assert.Equal(Provenance().ComputeFingerprint(), Provenance().ProvenanceFingerprint);
        Assert.Equal(64, Provenance().ProvenanceFingerprint.Length);
    }

    [Fact]
    public void Provenance_ExposesOnlySemanticFields_NoPathTimeUserMachineInputs()
    {
        string[] expected =
        {
            nameof(ResearchSourceBuildProvenance.ProvenanceVersion), nameof(ResearchSourceBuildProvenance.SourceCommitSha),
            nameof(ResearchSourceBuildProvenance.SourceTreeSha), nameof(ResearchSourceBuildProvenance.SourceState),
            nameof(ResearchSourceBuildProvenance.BuildConfiguration), nameof(ResearchSourceBuildProvenance.TargetFramework),
            nameof(ResearchSourceBuildProvenance.HistoricalDataToolModuleVersionId), nameof(ResearchSourceBuildProvenance.ServicesModuleVersionId),
            nameof(ResearchSourceBuildProvenance.CoreModuleVersionId), nameof(ResearchSourceBuildProvenance.ProvenanceFingerprint)
        };
        string[] actual = typeof(ResearchSourceBuildProvenance).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), actual);
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("configuration")]
    [InlineData("targetFramework")]
    [InlineData("tool")]
    [InlineData("services")]
    [InlineData("core")]
    public void Provenance_AnySemanticFieldChange_ChangesFingerprint(string field)
    {
        ResearchSourceBuildProvenance baseline = Provenance();
        ResearchSourceBuildProvenance changed = field switch
        {
            "commit" => Provenance(commit: "abcdef0123456789abcdef0123456789abcdef01"),
            "tree" => Provenance(tree: "abcdef0123456789abcdef0123456789abcdef01"),
            "configuration" => Provenance(configuration: "Debug"),
            "targetFramework" => Provenance(targetFramework: ".NETCoreApp,Version=v9.0"),
            "tool" => Provenance(tool: "44444444-4444-4444-4444-444444444444"),
            "services" => Provenance(services: "55555555-5555-5555-5555-555555555555"),
            _ => Provenance(core: "66666666-6666-6666-6666-666666666666")
        };
        Assert.NotEqual(baseline.ProvenanceFingerprint, changed.ProvenanceFingerprint);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Provenance_RejectsNonCanonicalObjectId(string objectId)
    {
        Assert.Throws<ArgumentException>(() => Provenance(commit: objectId));
    }

    [Fact]
    public void Provenance_RejectsNonCanonicalModuleIdentity()
    {
        Assert.Throws<ArgumentException>(() => Provenance(tool: "11111111-1111-1111-1111-11111111111A"));
        Assert.Throws<ArgumentException>(() => Provenance(tool: "not-a-guid"));
    }

    [Fact]
    public void Provenance_RejectsTamperedFingerprint()
    {
        ResearchSourceBuildProvenance valid = Provenance();
        Assert.Throws<ArgumentException>(() => new ResearchSourceBuildProvenance(
            valid.ProvenanceVersion, valid.SourceCommitSha, valid.SourceTreeSha, valid.SourceState, valid.BuildConfiguration,
            valid.TargetFramework, valid.HistoricalDataToolModuleVersionId, valid.ServicesModuleVersionId, valid.CoreModuleVersionId, "00"));
    }

    // ---- provider seams ---------------------------------------------------------------------------------

    [Fact]
    public void Provider_CapturesFromInjectedSeams()
    {
        ResearchSourceBuildProvenanceProvider provider = new(
            new StubGitReader(() => new GitSourceSnapshot(Commit, Tree)),
            new StubBuildReader(new ResearchBuildIdentity("Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid)));

        ResearchSourceBuildProvenance captured = provider.Capture();
        Assert.Equal(Commit, captured.SourceCommitSha);
        Assert.Equal(Tree, captured.SourceTreeSha);
        Assert.Equal("Release", captured.BuildConfiguration);
        Assert.Equal(Provenance().ProvenanceFingerprint, captured.ProvenanceFingerprint);
    }

    [Fact]
    public void Provider_DirtyWorktree_RefusesWithStableReason()
    {
        ResearchSourceBuildProvenanceProvider provider = new(
            new StubGitReader(() => throw new ResearchSourceBuildProvenanceException(SourceWorktreeNotClean, $"{SourceWorktreeNotClean}: dirty")),
            new StubBuildReader(new ResearchBuildIdentity("Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid)));

        ResearchSourceBuildProvenanceException exception = Assert.Throws<ResearchSourceBuildProvenanceException>(() => provider.Capture());
        Assert.Equal(SourceWorktreeNotClean, exception.ReasonCode);
    }

    [Fact]
    public void Provider_GitUnavailable_RefusesWithStableReason()
    {
        ResearchSourceBuildProvenanceProvider provider = new(
            new StubGitReader(() => throw new ResearchSourceBuildProvenanceException(SourceProvenanceUnavailable, "git unavailable")),
            new StubBuildReader(new ResearchBuildIdentity("Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid)));

        ResearchSourceBuildProvenanceException exception = Assert.Throws<ResearchSourceBuildProvenanceException>(() => provider.Capture());
        Assert.Equal(SourceProvenanceUnavailable, exception.ReasonCode);
    }

    // ---- binding versioning -----------------------------------------------------------------------------

    [Fact]
    public void Binding_V1Fingerprint_PinnedRegression()
    {
        string actual = ResearchExecutionProvenanceBinding.ComputeFingerprint(
            ResearchExecutionProvenanceBinding.LegacyBindingVersion, "exp-fp", "param-fp", "exp-strategy-fp", "artifact-strategy-fp",
            "exp-portfolio-fp", "artifact-portfolio-fp", "exp-analysis-fp", "artifact-analysis-fp", "dataset-fp", "portfolio-research-v2", "artifact-fp");
        Assert.Equal(PinnedV1BindingFingerprint, actual);

        string recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "{\"bindingVersion\":\"research-execution-provenance-binding-v1\",\"experimentFingerprint\":\"exp-fp\",\"parameterSnapshotFingerprint\":\"param-fp\"," +
            "\"experimentStrategyParameterFingerprint\":\"exp-strategy-fp\",\"artifactStrategyParameterFingerprint\":\"artifact-strategy-fp\"," +
            "\"experimentPortfolioConfigurationFingerprint\":\"exp-portfolio-fp\",\"artifactPortfolioConfigurationFingerprint\":\"artifact-portfolio-fp\"," +
            "\"experimentAnalysisConfigurationFingerprint\":\"exp-analysis-fp\",\"artifactAnalysisFingerprint\":\"artifact-analysis-fp\"," +
            "\"datasetFingerprint\":\"dataset-fp\",\"artifactVersion\":\"portfolio-research-v2\",\"artifactFingerprint\":\"artifact-fp\"}")));
        Assert.Equal(recomputed, actual);
    }

    [Fact]
    public void Binding_V2_BindsSourceBuildProvenanceFingerprint()
    {
        ResearchSourceBuildProvenance provenance = Provenance();
        PersistedResearchExperimentRecord record = CreateV3Record(provenance);
        Assert.Equal(ResearchExecutionProvenanceBinding.CurrentBindingVersion, record.ExecutionProvenanceBinding!.BindingVersion);
        Assert.Equal(provenance.ProvenanceFingerprint, record.ExecutionProvenanceBinding.SourceBuildProvenanceFingerprint);
        Assert.Equal(record.ExecutionProvenanceBinding.BindingFingerprint, record.ExecutionProvenanceBinding.ComputeFingerprint());
    }

    [Fact]
    public void Binding_V1_RejectsSourceBuildProvenanceFingerprint()
    {
        Assert.Throws<ArgumentException>(() => new ResearchExecutionProvenanceBinding(
            ResearchExecutionProvenanceBinding.LegacyBindingVersion, "exp-fp", "param-fp", "exp-strategy-fp", "artifact-strategy-fp",
            "exp-portfolio-fp", "artifact-portfolio-fp", "exp-analysis-fp", "artifact-analysis-fp", "dataset-fp", "portfolio-research-v2", "artifact-fp", "binding-fp",
            Provenance().ProvenanceFingerprint));
    }

    [Fact]
    public void Binding_RejectsUnknownVersion()
    {
        Assert.Throws<ArgumentException>(() => new ResearchExecutionProvenanceBinding(
            "research-execution-provenance-binding-v99", "exp-fp", "param-fp", "exp-strategy-fp", "artifact-strategy-fp",
            "exp-portfolio-fp", "artifact-portfolio-fp", "exp-analysis-fp", "artifact-analysis-fp", "dataset-fp", "portfolio-research-v2", "artifact-fp", "binding-fp"));
    }

    // ---- record versioning ------------------------------------------------------------------------------

    [Fact]
    public async Task Record_V3_RoundTripsThroughRepository()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(record);

            PersistedResearchExperimentRecord loaded = await repository.GetAsync(record.ExperimentId);
            Assert.Equal(PersistedResearchExperimentRecord.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(record.SourceBuildProvenance!.ProvenanceFingerprint, loaded.SourceBuildProvenance!.ProvenanceFingerprint);
            Assert.Equal(record.ExecutionProvenanceBinding!.BindingFingerprint, loaded.ExecutionProvenanceBinding!.BindingFingerprint);
            Assert.Equal(ResearchExecutionProvenanceBinding.CurrentBindingVersion, loaded.ExecutionProvenanceBinding.BindingVersion);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Record_V3_RequiresSourceBuildProvenance()
    {
        Assert.Throws<ArgumentNullException>(() => CreateRecord(Provenance(), PersistedResearchExperimentRecord.CurrentSchemaVersion, withProvenance: false));
    }

    [Fact]
    public void Record_V2_RejectsSourceBuildProvenance()
    {
        Assert.Throws<ArgumentException>(() => CreateRecord(Provenance(), PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion, withProvenance: true));
    }

    [Fact]
    public void Record_V3_RejectsBindingV1()
    {
        Assert.Throws<ArgumentException>(() => CreateRecord(Provenance(), PersistedResearchExperimentRecord.CurrentSchemaVersion, withProvenance: true, bindingVersion: ResearchExecutionProvenanceBinding.LegacyBindingVersion));
    }

    [Fact]
    public void Record_V3_TamperedSourceBuildProvenance_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new ResearchSourceBuildProvenance(
            ResearchSourceBuildProvenance.CurrentProvenanceVersion, Commit, Tree, ResearchSourceBuildProvenance.CleanSourceState,
            "Debug", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid, Provenance().ProvenanceFingerprint));
    }

    [Fact]
    public async Task Record_UnknownSchemaVersion_IsRejected()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(record);
            string path = Path.Combine(root, record.ExperimentId + ".json");
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace(PersistedResearchExperimentRecord.CurrentSchemaVersion, "research-experiment-record-v99"));
            await Assert.ThrowsAsync<NotSupportedException>(() => repository.GetAsync(record.ExperimentId));
        }
        finally { DeleteDirectory(root); }
    }

    // ---- verifier ---------------------------------------------------------------------------------------

    [Fact]
    public void Verifier_Match_WhenAllObservedFieldsAgree()
    {
        PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
        ResearchSourceBuildProvenanceVerificationResult result = new ResearchSourceBuildProvenanceVerifier(new StubProvider(Provenance())).Verify(record);

        Assert.Equal(Match, result.Status);
        Assert.Equal(0, result.FailedCheckCount);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(check => check.Code));
        Assert.All(result.Checks, check => Assert.Equal(CheckStatus.Pass, check.Status));
        Assert.Equal(record.SourceBuildProvenance!.ProvenanceFingerprint, result.RecordedProvenanceFingerprint);
        Assert.Equal(record.SourceBuildProvenance.ProvenanceFingerprint, result.CurrentProvenanceFingerprint);
    }

    [Theory]
    [InlineData("commit", SourceCommitMatch)]
    [InlineData("tree", SourceTreeMatch)]
    [InlineData("configuration", BuildConfigurationMatch)]
    [InlineData("targetFramework", TargetFrameworkMatch)]
    [InlineData("tool", ToolModuleIdentityMatch)]
    [InlineData("services", ServicesModuleIdentityMatch)]
    [InlineData("core", CoreModuleIdentityMatch)]
    public void Verifier_Mismatch_IdentifiesExactFailingCheck(string field, string expectedFailingCode)
    {
        PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
        ResearchSourceBuildProvenance current = field switch
        {
            "commit" => Provenance(commit: "abcdef0123456789abcdef0123456789abcdef01"),
            "tree" => Provenance(tree: "abcdef0123456789abcdef0123456789abcdef01"),
            "configuration" => Provenance(configuration: "Debug"),
            "targetFramework" => Provenance(targetFramework: ".NETCoreApp,Version=v9.0"),
            "tool" => Provenance(tool: "44444444-4444-4444-4444-444444444444"),
            "services" => Provenance(services: "55555555-5555-5555-5555-555555555555"),
            _ => Provenance(core: "66666666-6666-6666-6666-666666666666")
        };

        ResearchSourceBuildProvenanceVerificationResult result = new ResearchSourceBuildProvenanceVerifier(new StubProvider(current)).Verify(record);
        Assert.Equal(Mismatch, result.Status);
        Assert.Equal(1, result.FailedCheckCount);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, check => check.Code == expectedFailingCode).Status);
        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(check => check.Code));
    }

    [Fact]
    public void Verifier_DirtyCurrentWorktree_ReturnsMismatchWithSourceStateFail()
    {
        PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
        ResearchSourceBuildProvenanceVerifier verifier = new(new StubProvider(() =>
            throw new ResearchSourceBuildProvenanceException(SourceWorktreeNotClean, $"{SourceWorktreeNotClean}: dirty")));

        ResearchSourceBuildProvenanceVerificationResult result = verifier.Verify(record);
        Assert.Equal(Mismatch, result.Status);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, check => check.Code == SourceStateMatch).Status);
        Assert.Contains(SourceWorktreeNotClean, result.ReasonCodes);
    }

    [Fact]
    public void Verifier_GitUnavailable_ReturnsUnsupported()
    {
        PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
        ResearchSourceBuildProvenanceVerifier verifier = new(new StubProvider(() =>
            throw new ResearchSourceBuildProvenanceException(SourceProvenanceUnavailable, "git unavailable")));

        ResearchSourceBuildProvenanceVerificationResult result = verifier.Verify(record);
        Assert.Equal(Unsupported, result.Status);
        Assert.Contains(SourceProvenanceUnavailable, result.ReasonCodes);
        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(check => check.Code));
    }

    [Fact]
    public void Verifier_LegacyRecord_ReturnsUnsupported()
    {
        foreach (string schema in new[] { PersistedResearchExperimentRecord.LegacySchemaVersion, PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion })
        {
            PersistedResearchExperimentRecord record = CreateRecord(Provenance(), schema, withProvenance: false, bindingVersion: ResearchExecutionProvenanceBinding.LegacyBindingVersion);
            ResearchSourceBuildProvenanceVerificationResult result = new ResearchSourceBuildProvenanceVerifier(new StubProvider(Provenance())).Verify(record);
            Assert.Equal(Unsupported, result.Status);
            Assert.Equal(CheckStatus.Unsupported, Assert.Single(result.Checks).Status);
        }
    }

    [Fact]
    public void Verifier_ForgedRecordMissingBinding_ReturnsFailed()
    {
        // Normal construction guarantees the V3 invariants, so the verifier's Failed path is defense in depth.
        // It is exercised here with an object graph that bypassed construction validation.
        PersistedResearchExperimentRecord valid = CreateV3Record(Provenance());
        PersistedResearchExperimentRecord forged = Forge(valid, valid.SourceBuildProvenance, binding: null);

        ResearchSourceBuildProvenanceVerificationResult result = new ResearchSourceBuildProvenanceVerifier(new StubProvider(Provenance())).Verify(forged);
        Assert.Equal(Failed, result.Status);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, check => check.Code == SourceBuildBindingValid).Status);
    }

    [Fact]
    public async Task Record_TamperedProvenanceFingerprint_RejectedAtPersistenceBoundary()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(record);

            string path = Path.Combine(root, record.ExperimentId + ".json");
            string json = await File.ReadAllTextAsync(path);
            string tampered = json.Replace(record.SourceBuildProvenance!.ProvenanceFingerprint, new string('0', 64), StringComparison.Ordinal);
            Assert.NotEqual(json, tampered);
            await File.WriteAllTextAsync(path, tampered);

            await Assert.ThrowsAnyAsync<Exception>(() => repository.GetAsync(record.ExperimentId));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Record_TamperedBindingFingerprint_RejectedAtConstruction()
    {
        PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
        ResearchExecutionProvenanceBinding binding = record.ExecutionProvenanceBinding!;
        Assert.Throws<ArgumentException>(() => new PersistedResearchExperimentRecord(
            record.ExperimentId, record.ExperimentFingerprint, record.Definition, record.ExecutionSummary, record.ArtifactReference, record.Lineage,
            record.CreatedAt, PersistedResearchExperimentRecord.CurrentSchemaVersion,
            new ResearchExecutionProvenanceBinding(binding.BindingVersion, binding.ExperimentFingerprint, binding.ParameterSnapshotFingerprint,
                binding.ExperimentStrategyParameterFingerprint, binding.ArtifactStrategyParameterFingerprint, binding.ExperimentPortfolioConfigurationFingerprint,
                binding.ArtifactPortfolioConfigurationFingerprint, binding.ExperimentAnalysisConfigurationFingerprint, binding.ArtifactAnalysisFingerprint,
                binding.DatasetFingerprint, binding.ArtifactVersion, binding.ArtifactFingerprint, new string('0', 64), binding.SourceBuildProvenanceFingerprint),
            record.SourceBuildProvenance));
    }

    // ---- phase 3.10 separation --------------------------------------------------------------------------

    [Fact]
    public void ResultEquivalence_StatusEnumsRemainIndependent()
    {
        Assert.Equal(new[] { "Equivalent", "Diverged", "Unsupported", "Failed" }, Enum.GetNames<ResearchReexecutionStatus>());
        Assert.DoesNotContain(typeof(ResearchReexecutionValidationResult).GetProperties(),
            property => property.PropertyType.Namespace == typeof(ResearchSourceBuildProvenance).Namespace && property.PropertyType.Name.Contains("Provenance", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ResearchSourceBuildProvenanceVerificationResult).GetProperties(), property => property.PropertyType == typeof(ResearchReexecutionStatus));
        Assert.NotEqual(typeof(ResearchReexecutionStatus), typeof(ResearchSourceBuildProvenanceVerificationStatus));
    }

    // ---- CLI --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cli_ShowSourceBuildProvenance_IsOfflineAndWellFormed()
    {
        using Process process = Process.Start(CreateCliStartInfo("--show-source-build-provenance")) ?? throw new InvalidOperationException("Could not start CLI.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Contains("SOURCE_BUILD_PROVENANCE_STATUS=", stdout);
        if (stdout.Contains("SOURCE_BUILD_PROVENANCE_STATUS=Available", StringComparison.Ordinal))
        {
            Assert.Equal(0, process.ExitCode);
            Assert.Contains($"SOURCE_COMMIT_SHA={CommitOrHead()}", stdout);
            Assert.Contains("SOURCE_STATE=Clean", stdout);
            Assert.Contains("TOOL_MODULE_VERSION_ID=", stdout);
            Assert.Contains("SERVICES_MODULE_VERSION_ID=", stdout);
            Assert.Contains("CORE_MODULE_VERSION_ID=", stdout);
            Assert.Contains("SOURCE_BUILD_PROVENANCE_FINGERPRINT=", stdout);
        }
        else
        {
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains($"REASON={SourceWorktreeNotClean}", stdout);
        }
    }

    [Fact]
    public async Task Cli_VerifySourceBuildProvenance_LegacyRecordUnsupported()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = CreateRecord(Provenance(), PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion, withProvenance: false,
                bindingVersion: ResearchExecutionProvenanceBinding.LegacyBindingVersion);
            await new JsonResearchExperimentRepository(root).SaveAsync(record);

            using Process process = Process.Start(CreateCliStartInfo("--verify-source-build-provenance", record.ExperimentId, "--experiment-store", root)) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("SOURCE_BUILD_VERIFICATION_STATUS=Unsupported", stdout);
            Assert.Contains($"EXPERIMENT_ID={record.ExperimentId}", stdout);
            Assert.Contains("CHECK=SOURCE_BUILD_PROVENANCE_PRESENT:Unsupported", stdout);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Cli_VerifySourceBuildProvenance_RecordedProvenanceMismatch()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = CreateV3Record(Provenance());
            await new JsonResearchExperimentRepository(root).SaveAsync(record);

            using Process process = Process.Start(CreateCliStartInfo("--verify-source-build-provenance", record.ExperimentId, "--experiment-store", root)) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("SOURCE_BUILD_VERIFICATION_STATUS=Mismatch", stdout);
            Assert.Contains($"RECORDED_PROVENANCE_FINGERPRINT={record.SourceBuildProvenance!.ProvenanceFingerprint}", stdout);
            Assert.True(stdout.Contains("CHECK=SOURCE_COMMIT_MATCH:Fail", StringComparison.Ordinal)
                || stdout.Contains("CHECK=SOURCE_STATE_MATCH:Fail", StringComparison.Ordinal), stdout);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Cli_SourceBuildProvenanceConflictsWithOtherModes()
    {
        using Process process = Process.Start(CreateCliStartInfo("--show-source-build-provenance", "--reproduce-experiment", "EXP-001")) ?? throw new InvalidOperationException("Could not start CLI.");
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("CONFLICTING_OPERATION_MODES", stderr);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private static string CommitOrHead() =>
        RunGit("rev-parse", "HEAD") ?? throw new InvalidOperationException("Could not resolve HEAD.");

    private static string? RunGit(params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = SolutionRoot() };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start git.");
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    private static PersistedResearchExperimentRecord CreateV3Record(ResearchSourceBuildProvenance provenance) =>
        CreateRecord(provenance, PersistedResearchExperimentRecord.CurrentSchemaVersion, withProvenance: true);

    /// <summary>Builds an object graph that bypasses constructor validation, used only to exercise defensive checks.</summary>
    private static PersistedResearchExperimentRecord Forge(PersistedResearchExperimentRecord source, ResearchSourceBuildProvenance? provenance, ResearchExecutionProvenanceBinding? binding)
    {
        PersistedResearchExperimentRecord forged = (PersistedResearchExperimentRecord)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PersistedResearchExperimentRecord));
        SetField(forged, "<ExperimentId>k__BackingField", source.ExperimentId);
        SetField(forged, "<SchemaVersion>k__BackingField", source.SchemaVersion);
        SetField(forged, "<SourceBuildProvenance>k__BackingField", provenance);
        SetField(forged, "<ExecutionProvenanceBinding>k__BackingField", binding);
        return forged;
    }

    private static void SetField(object target, string fieldName, object? value) =>
        target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static PersistedResearchExperimentRecord CreateRecord(ResearchSourceBuildProvenance provenance, string schemaVersion, bool withProvenance,
        string bindingVersion = ResearchExecutionProvenanceBinding.CurrentBindingVersion)
    {
        ResearchExperimentIdentity identity = new("EXP-PROV-001", ResearchExperimentIdentity.CurrentExperimentVersion, "test", DateTimeOffset.UnixEpoch);
        ExperimentParameterSnapshot parameters = new("v1", new Dictionary<string, string> { ["TopN"] = "2" },
            new Dictionary<string, string> { ["InitialCapital"] = "1000000" }, new Dictionary<string, string> { ["ReturnBasis"] = "CloseToClose" });
        ResearchExperimentDefinition definition = new(identity, "dataset-fp", new ResearchExperimentStrategyIdentity("V2", "v2"), parameters,
            "portfolio-fp", "analysis-fp", "benchmark:CSI300");
        ResearchExperimentExecutionSummary summary = ResearchExperimentExecutionSummary.FromCompletedExecution(
            ResearchExperimentExecution.Create(identity).Start(DateTimeOffset.UnixEpoch).Complete(DateTimeOffset.UnixEpoch, "artifact-fp"));
        ResearchArtifactLineage lineage = new(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, "artifact-fp");
        ResearchResultArtifactReference artifactReference = new("artifact-fp", SparrowPortfolioResearchArtifact.CurrentArtifactVersion);

        bool v2Binding = string.Equals(bindingVersion, ResearchExecutionProvenanceBinding.CurrentBindingVersion, StringComparison.Ordinal);
        string bindingFingerprint = v2Binding
            ? ResearchExecutionProvenanceBinding.ComputeFingerprint(bindingVersion, definition.SemanticFingerprint, definition.Parameters.Fingerprint,
                definition.StrategyParameterFingerprint, "artifact-strategy-fp", definition.PortfolioConfigurationFingerprint, "artifact-portfolio-fp",
                definition.AnalysisConfigurationFingerprint, "artifact-analysis-fp", definition.DatasetFingerprint, artifactReference.ArtifactVersion,
                artifactReference.ArtifactFingerprint, provenance.ProvenanceFingerprint)
            : ResearchExecutionProvenanceBinding.ComputeFingerprint(bindingVersion, definition.SemanticFingerprint, definition.Parameters.Fingerprint,
                definition.StrategyParameterFingerprint, "artifact-strategy-fp", definition.PortfolioConfigurationFingerprint, "artifact-portfolio-fp",
                definition.AnalysisConfigurationFingerprint, "artifact-analysis-fp", definition.DatasetFingerprint, artifactReference.ArtifactVersion,
                artifactReference.ArtifactFingerprint);

        ResearchExecutionProvenanceBinding binding = new(bindingVersion, definition.SemanticFingerprint, definition.Parameters.Fingerprint,
            definition.StrategyParameterFingerprint, "artifact-strategy-fp", definition.PortfolioConfigurationFingerprint, "artifact-portfolio-fp",
            definition.AnalysisConfigurationFingerprint, "artifact-analysis-fp", definition.DatasetFingerprint, artifactReference.ArtifactVersion,
            artifactReference.ArtifactFingerprint, bindingFingerprint, v2Binding ? provenance.ProvenanceFingerprint : null);

        return new PersistedResearchExperimentRecord(identity.ExperimentId, definition.SemanticFingerprint, definition, summary, artifactReference, lineage,
            DateTimeOffset.UnixEpoch, schemaVersion, binding, withProvenance ? provenance : null);
    }

    private static ProcessStartInfo CreateCliStartInfo(params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SolutionRoot() };
        psi.Environment.Remove("HISTORICAL_GATEWAY_URL");
        foreach (var a in new[] { "run", "--project", Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"),
            "--configuration", TestExecutionConfiguration.Current(), "--no-build", "--no-restore", "--" }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-SourceBuildProvenanceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string SolutionRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private sealed class StubGitReader(Func<GitSourceSnapshot> read) : IGitSourceSnapshotReader
    {
        public GitSourceSnapshot ReadAuthoritative() => read();
    }

    private sealed class StubBuildReader(ResearchBuildIdentity identity) : IResearchBuildIdentityReader
    {
        public ResearchBuildIdentity Read() => identity;
    }

    private sealed class StubProvider : IResearchSourceBuildProvenanceProvider
    {
        private readonly Func<ResearchSourceBuildProvenance> _capture;
        public StubProvider(ResearchSourceBuildProvenance provenance) : this(() => provenance) { }
        public StubProvider(Func<ResearchSourceBuildProvenance> capture) => _capture = capture;
        public ResearchSourceBuildProvenance Capture() => _capture();
    }
}
