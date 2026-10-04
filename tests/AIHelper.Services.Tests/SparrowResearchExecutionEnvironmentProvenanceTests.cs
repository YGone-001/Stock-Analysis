using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceCheckCodes;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceReasonCodes;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceVerificationReasonCodes;
using static AIHelper.Core.Sparrow.ResearchExecutionEnvironmentProvenanceVerificationStatus;
using CheckStatus = AIHelper.Core.Sparrow.ResearchReproducibilityCheckStatus;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchExecutionEnvironmentProvenanceTests
{
    private const string RuntimeTarget = ".NETCoreApp,Version=v10.0";
    private const string ManifestContractJsonPrefix = "\"runtimeTarget\"";

    private static readonly string[] CanonicalCheckCodes =
    {
        ExecutionEnvironmentProvenancePresent, ExecutionEnvironmentProvenanceFingerprintValid, ExecutionEnvironmentBindingValid,
        FrameworkDescriptionMatch, RuntimeVersionMatch, RuntimeIdentifierMatch, OSPlatformMatch, OSArchitectureMatch,
        ProcessArchitectureMatch, DependencyRuntimeTargetMatch, DependencyManifestFingerprintMatch
    };

    // ---- dependency manifest normalization ---------------------------------------------------------------

    [Fact]
    public void DependencyManifest_PropertyOrderAndWhitespace_DoNotChangeFingerprint()
    {
        string first = TemporaryDirectory();
        string second = TemporaryDirectory();
        try
        {
            string canonical = CanonicalManifest(pretty: true);
            string reordered = ReorderManifest(CanonicalManifest(pretty: false));

            ResearchDependencyManifest a = ReadManifest(WriteManifest(first, canonical));
            ResearchDependencyManifest b = ReadManifest(WriteManifest(second, reordered));

            Assert.Equal(a.RuntimeTarget, b.RuntimeTarget);
            Assert.Equal(
                ResearchExecutionEnvironmentProvenance.ComputeDependencyManifestFingerprint(a.RuntimeTarget, a.Dependencies),
                ResearchExecutionEnvironmentProvenance.ComputeDependencyManifestFingerprint(b.RuntimeTarget, b.Dependencies));
        }
        finally { DeleteDirectory(first); DeleteDirectory(second); }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("added")]
    [InlineData("removed")]
    [InlineData("edge")]
    [InlineData("runtimeTarget")]
    public void DependencyManifest_SemanticChange_ChangesFingerprint(string change)
    {
        string root = TemporaryDirectory();
        try
        {
            ResearchDependencyManifest baseline = ReadManifest(WriteManifest(root, CanonicalManifest(pretty: true), "baseline.deps.json"));
            string baselineFingerprint = ResearchExecutionEnvironmentProvenance.ComputeDependencyManifestFingerprint(baseline.RuntimeTarget, baseline.Dependencies);

            string variant = change switch
            {
                "version" => CanonicalManifest(pretty: true, libraryVersion: "2.0.0"),
                "added" => CanonicalManifest(pretty: true, extraLibrary: true),
                "removed" => CanonicalManifest(pretty: true, includeLibrary: false),
                "edge" => CanonicalManifest(pretty: true, extraEdge: true),
                _ => CanonicalManifest(pretty: true, runtimeTarget: ".NETCoreApp,Version=v9.0")
            };

            ResearchDependencyManifest changed = ReadManifest(WriteManifest(root, variant, "variant.deps.json"));
            Assert.NotEqual(baselineFingerprint, ResearchExecutionEnvironmentProvenance.ComputeDependencyManifestFingerprint(changed.RuntimeTarget, changed.Dependencies));
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void DependencyManifest_NormalizesNamesAndEdges()
    {
        string root = TemporaryDirectory();
        try
        {
            ResearchDependencyManifest manifest = ReadManifest(WriteManifest(root, CanonicalManifest(pretty: true, upperCaseNames: true)));
            ResearchResolvedDependency library = Assert.Single(manifest.Dependencies, dependency => dependency.Name == "lib.a");
            Assert.Equal("package", library.Type);
            Assert.Equal("1.0.0", library.Version);
            Assert.Empty(library.Dependencies);

            ResearchResolvedDependency app = Assert.Single(manifest.Dependencies, dependency => dependency.Name == "app");
            Assert.Equal("project", app.Type);
            Assert.Equal("lib.a", Assert.Single(app.Dependencies).Name);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void DependencyManifest_Location_DoesNotChangeFingerprint()
    {
        string first = TemporaryDirectory();
        string second = TemporaryDirectory();
        try
        {
            ResearchDependencyManifest a = ReadManifest(WriteManifest(first, CanonicalManifest(pretty: true), "one.deps.json"));
            ResearchDependencyManifest b = ReadManifest(WriteManifest(second, CanonicalManifest(pretty: true), "another-name.deps.json"));
            Assert.Equal(
                ResearchExecutionEnvironmentProvenance.ComputeDependencyManifestFingerprint(a.RuntimeTarget, a.Dependencies),
                ResearchExecutionEnvironmentProvenance.ComputeDependencyManifestFingerprint(b.RuntimeTarget, b.Dependencies));
        }
        finally { DeleteDirectory(first); DeleteDirectory(second); }
    }

    [Fact]
    public void DependencyManifest_MissingLibraryType_IsRejected()
    {
        string root = TemporaryDirectory();
        try
        {
            string path = WriteManifest(root, "{\"runtimeTarget\":{\"name\":\"x\"},\"targets\":{\"x\":{\"a/1.0.0\":{}}},\"libraries\":{\"a/1.0.0\":{}}}");
            ResearchExecutionEnvironmentProvenanceException exception = Assert.Throws<ResearchExecutionEnvironmentProvenanceException>(() => ReadManifest(path));
            Assert.Equal(DependencyManifestInvalid, exception.ReasonCode);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void DependencyManifest_MalformedJson_IsRejected()
    {
        string root = TemporaryDirectory();
        try
        {
            string path = WriteManifest(root, "{ not json");
            ResearchExecutionEnvironmentProvenanceException exception = Assert.Throws<ResearchExecutionEnvironmentProvenanceException>(() => ReadManifest(path));
            Assert.Equal(DependencyManifestInvalid, exception.ReasonCode);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void DependencyManifest_Unavailable_IsReportedWithStableReason()
    {
        string root = TemporaryDirectory();
        try
        {
            DotNetDependencyManifestReader reader = new(() => Path.Combine(root, "missing.deps.json"));
            ResearchExecutionEnvironmentProvenanceException exception = Assert.Throws<ResearchExecutionEnvironmentProvenanceException>(() => reader.Read());
            Assert.Equal(DependencyManifestUnavailable, exception.ReasonCode);
        }
        finally { DeleteDirectory(root); }
    }

    // ---- environment fingerprint ------------------------------------------------------------------------

    [Fact]
    public void EnvironmentProvenance_SameNormalizedEnvironment_ProducesSameFingerprint()
    {
        Assert.Equal(Environment().ProvenanceFingerprint, Environment().ProvenanceFingerprint);
        Assert.Equal(Environment().ComputeDependencyManifestFingerprint(), Environment().DependencyManifestFingerprint);
        Assert.Equal(Environment().ComputeProvenanceFingerprint(), Environment().ProvenanceFingerprint);
        Assert.Equal(64, Environment().ProvenanceFingerprint.Length);
    }

    [Theory]
    [InlineData("framework")]
    [InlineData("runtimeVersion")]
    [InlineData("runtimeIdentifier")]
    [InlineData("osPlatform")]
    [InlineData("osArchitecture")]
    [InlineData("processArchitecture")]
    [InlineData("dependencyRuntimeTarget")]
    [InlineData("dependencyFingerprint")]
    public void EnvironmentProvenance_AnySemanticChange_ChangesFingerprint(string field)
    {
        ResearchExecutionEnvironmentProvenance baseline = Environment();
        ResearchExecutionEnvironmentProvenance changed = field switch
        {
            "framework" => Environment(framework: ".NET 9.0.0"),
            "runtimeVersion" => Environment(runtimeVersion: "9.0.0"),
            "runtimeIdentifier" => Environment(runtimeIdentifier: "linux-x64"),
            "osPlatform" => Environment(osPlatform: "Linux"),
            "osArchitecture" => Environment(osArchitecture: "Arm64"),
            "processArchitecture" => Environment(processArchitecture: "Arm64"),
            "dependencyRuntimeTarget" => Environment(dependencyRuntimeTarget: ".NETCoreApp,Version=v9.0"),
            _ => Environment(dependencies: new[]
            {
                new ResearchResolvedDependency("app", "1.0.0", "project", new[] { new ResearchResolvedDependencyReference("lib.a", "2.0.0") }),
                new ResearchResolvedDependency("lib.a", "2.0.0", "package", null)
            })
        };

        Assert.NotEqual(baseline.ProvenanceFingerprint, changed.ProvenanceFingerprint);
        if (field == "dependencyFingerprint") Assert.NotEqual(baseline.DependencyManifestFingerprint, changed.DependencyManifestFingerprint);
    }

    [Fact]
    public void EnvironmentProvenance_IsCultureIndependent()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            ResearchExecutionEnvironmentProvenance german = Environment();
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            ResearchExecutionEnvironmentProvenance english = Environment();
            Assert.Equal(english.ProvenanceFingerprint, german.ProvenanceFingerprint);
            Assert.Equal(english.DependencyManifestFingerprint, german.DependencyManifestFingerprint);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Fact]
    public void EnvironmentProvenance_ExposesOnlySemanticFields()
    {
        string[] expected =
        {
            nameof(ResearchExecutionEnvironmentProvenance.ProvenanceVersion), nameof(ResearchExecutionEnvironmentProvenance.FrameworkDescription),
            nameof(ResearchExecutionEnvironmentProvenance.RuntimeVersion), nameof(ResearchExecutionEnvironmentProvenance.RuntimeIdentifier),
            nameof(ResearchExecutionEnvironmentProvenance.OSPlatform), nameof(ResearchExecutionEnvironmentProvenance.OSArchitecture),
            nameof(ResearchExecutionEnvironmentProvenance.ProcessArchitecture), nameof(ResearchExecutionEnvironmentProvenance.DependencyRuntimeTarget),
            nameof(ResearchExecutionEnvironmentProvenance.Dependencies), nameof(ResearchExecutionEnvironmentProvenance.DependencyManifestFingerprint),
            nameof(ResearchExecutionEnvironmentProvenance.ProvenanceFingerprint)
        };
        string[] actual = typeof(ResearchExecutionEnvironmentProvenance).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToArray();
        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual.OrderBy(name => name, StringComparer.Ordinal));
    }

    // ---- self-validation --------------------------------------------------------------------------------

    [Fact]
    public void EnvironmentProvenance_RejectsTamperedFingerprints()
    {
        ResearchExecutionEnvironmentProvenance valid = Environment();
        Assert.Throws<ArgumentException>(() => new ResearchExecutionEnvironmentProvenance(
            valid.ProvenanceVersion, valid.FrameworkDescription, valid.RuntimeVersion, valid.RuntimeIdentifier, valid.OSPlatform,
            valid.OSArchitecture, valid.ProcessArchitecture, valid.DependencyRuntimeTarget, valid.Dependencies, new string('0', 64), valid.ProvenanceFingerprint));
        Assert.Throws<ArgumentException>(() => new ResearchExecutionEnvironmentProvenance(
            valid.ProvenanceVersion, valid.FrameworkDescription, valid.RuntimeVersion, valid.RuntimeIdentifier, valid.OSPlatform,
            valid.OSArchitecture, valid.ProcessArchitecture, valid.DependencyRuntimeTarget, valid.Dependencies, valid.DependencyManifestFingerprint, new string('0', 64)));
    }

    [Fact]
    public void EnvironmentProvenance_RejectsTamperedDependencyGraph()
    {
        ResearchExecutionEnvironmentProvenance valid = Environment();
        IReadOnlyList<ResearchResolvedDependency> tampered = new[]
        {
            new ResearchResolvedDependency("app", "1.0.0", "project", new[] { new ResearchResolvedDependencyReference("lib.a", "9.9.9") }),
            new ResearchResolvedDependency("lib.a", "1.0.0", "package", null)
        };

        Assert.Throws<ArgumentException>(() => new ResearchExecutionEnvironmentProvenance(
            valid.ProvenanceVersion, valid.FrameworkDescription, valid.RuntimeVersion, valid.RuntimeIdentifier, valid.OSPlatform,
            valid.OSArchitecture, valid.ProcessArchitecture, valid.DependencyRuntimeTarget, tampered,
            valid.DependencyManifestFingerprint, valid.ProvenanceFingerprint));
    }

    // ---- binding versioning -----------------------------------------------------------------------------

    [Fact]
    public void Binding_V1AndV2Preimages_AreFrozen()
    {
        string v1 = ResearchExecutionProvenanceBinding.ComputeFingerprint(
            ResearchExecutionProvenanceBinding.LegacyBindingVersion, "exp-fp", "param-fp", "exp-strategy-fp", "artifact-strategy-fp",
            "exp-portfolio-fp", "artifact-portfolio-fp", "exp-analysis-fp", "artifact-analysis-fp", "dataset-fp", "portfolio-research-v2", "artifact-fp");
        Assert.Equal("4D037499DD590A75363D42AC385BDFFFF7A70466AEAE062FB6AD8E9308044260", v1);

        string v2 = ResearchExecutionProvenanceBinding.ComputeFingerprint(
            ResearchExecutionProvenanceBinding.SourceBuildBindingVersion, "exp-fp", "param-fp", "exp-strategy-fp", "artifact-strategy-fp",
            "exp-portfolio-fp", "artifact-portfolio-fp", "exp-analysis-fp", "artifact-analysis-fp", "dataset-fp", "portfolio-research-v2", "artifact-fp",
            "source-build-fp");
        Assert.Equal(HashLiteral(V2PreimageJson), v2);

        string v3 = ResearchExecutionProvenanceBinding.ComputeFingerprint(
            ResearchExecutionProvenanceBinding.CurrentBindingVersion, "exp-fp", "param-fp", "exp-strategy-fp", "artifact-strategy-fp",
            "exp-portfolio-fp", "artifact-portfolio-fp", "exp-analysis-fp", "artifact-analysis-fp", "dataset-fp", "portfolio-research-v2", "artifact-fp",
            "source-build-fp", "environment-fp");
        Assert.Equal(HashLiteral(V3PreimageJson), v3);
    }

    [Fact]
    public void Binding_V3_ChangesWhenEnvironmentFingerprintChanges()
    {
        ResearchExecutionEnvironmentProvenance environment = Environment();
        ResearchExecutionEnvironmentProvenance other = Environment(runtimeIdentifier: "linux-x64");

        ResearchExecutionProvenanceBinding first = ResearchExecutionProvenanceBinding.Create(
            ResearchExperimentRecordFixture.Definition(), ResearchExperimentRecordFixture.Artifact(), SourceBuild(), environment);
        ResearchExecutionProvenanceBinding second = ResearchExecutionProvenanceBinding.Create(
            ResearchExperimentRecordFixture.Definition(), ResearchExperimentRecordFixture.Artifact(), SourceBuild(), other);

        Assert.Equal(environment.ProvenanceFingerprint, first.ExecutionEnvironmentProvenanceFingerprint);
        Assert.Equal(SourceBuild().ProvenanceFingerprint, first.SourceBuildProvenanceFingerprint);
        Assert.NotEqual(first.BindingFingerprint, second.BindingFingerprint);
    }

    // ---- persistence versioning -------------------------------------------------------------------------

    [Fact]
    public async Task Record_V4_RoundTripsThroughRepository()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = ResearchExperimentRecordFixture.CreateV4Record(SourceBuild(), Environment());
            JsonResearchExperimentRepository repository = new(root);
            await repository.SaveAsync(record);

            PersistedResearchExperimentRecord loaded = await repository.GetAsync(record.ExperimentId);
            Assert.Equal(PersistedResearchExperimentRecord.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(ResearchExecutionProvenanceBinding.CurrentBindingVersion, loaded.ExecutionProvenanceBinding!.BindingVersion);
            Assert.Equal(record.ExecutionEnvironmentProvenance!.ProvenanceFingerprint, loaded.ExecutionEnvironmentProvenance!.ProvenanceFingerprint);
            Assert.Equal(record.ExecutionEnvironmentProvenance.DependencyManifestFingerprint, loaded.ExecutionEnvironmentProvenance.DependencyManifestFingerprint);
            Assert.Equal(record.ExecutionProvenanceBinding!.BindingFingerprint, loaded.ExecutionProvenanceBinding.BindingFingerprint);
            Assert.Equal(record.SourceBuildProvenance!.ProvenanceFingerprint, loaded.SourceBuildProvenance!.ProvenanceFingerprint);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task LegacyRecords_LoadWithNoExecutionEnvironmentProvenance()
    {
        string root = TemporaryDirectory();
        try
        {
            (string SchemaVersion, PersistedResearchExperimentRecord Record)[] cases =
            {
                (PersistedResearchExperimentRecord.LegacySchemaVersion,
                    ResearchExperimentRecordFixture.CreateLegacyRecord(PersistedResearchExperimentRecord.LegacySchemaVersion, "EXP-LEGACY-V1")),
                (PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion,
                    ResearchExperimentRecordFixture.CreateLegacyRecord(PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion, "EXP-LEGACY-V2")),
                (PersistedResearchExperimentRecord.SourceBuildProvenanceSchemaVersion,
                    ResearchExperimentRecordFixture.CreateV3Record(SourceBuild(), "EXP-LEGACY-V3"))
            };

            JsonResearchExperimentRepository repository = new(root);
            foreach ((string schemaVersion, PersistedResearchExperimentRecord record) in cases)
            {
                Assert.Null(record.ExecutionEnvironmentProvenance);
                await repository.SaveAsync(record);
                PersistedResearchExperimentRecord loaded = await repository.GetAsync(record.ExperimentId);

                Assert.Equal(schemaVersion, loaded.SchemaVersion);
                Assert.Null(loaded.ExecutionEnvironmentProvenance);
                Assert.NotEqual(PersistedResearchExperimentRecord.CurrentSchemaVersion, loaded.SchemaVersion);
            }
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public void Record_V4_RequiresExecutionEnvironmentProvenance()
    {
        ResearchExperimentDefinition definition = ResearchExperimentRecordFixture.Definition();
        SparrowPortfolioResearchArtifact artifact = ResearchExperimentRecordFixture.Artifact();
        ResearchExecutionProvenanceBinding binding = ResearchExecutionProvenanceBinding.Create(definition, artifact, SourceBuild(), Environment());

        Assert.Throws<ArgumentNullException>(() => new PersistedResearchExperimentRecord(
            definition.Identity.ExperimentId, definition.SemanticFingerprint, definition, ResearchExperimentRecordFixture.Summary(),
            new ResearchResultArtifactReference(artifact.ArtifactFingerprint, artifact.ArtifactVersion),
            new ResearchArtifactLineage(definition.SemanticFingerprint, definition.DatasetFingerprint, definition.Parameters.Fingerprint, artifact.ArtifactFingerprint),
            DateTimeOffset.UnixEpoch, PersistedResearchExperimentRecord.CurrentSchemaVersion, binding, SourceBuild(), null));
    }

    // ---- verifier ---------------------------------------------------------------------------------------

    [Fact]
    public void Verifier_Match_WhenAllObservedFieldsAgree()
    {
        PersistedResearchExperimentRecord record = ResearchExperimentRecordFixture.CreateV4Record(SourceBuild(), Environment());
        ResearchExecutionEnvironmentProvenanceVerificationResult result = new ResearchExecutionEnvironmentProvenanceVerifier(
            new StubEnvironmentProvider(Environment())).Verify(record);

        Assert.Equal(Match, result.Status);
        Assert.Equal(0, result.FailedCheckCount);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(check => check.Code));
        Assert.All(result.Checks, check => Assert.Equal(CheckStatus.Pass, check.Status));
    }

    [Theory]
    [InlineData("framework", FrameworkDescriptionMatch)]
    [InlineData("runtimeVersion", RuntimeVersionMatch)]
    [InlineData("runtimeIdentifier", RuntimeIdentifierMatch)]
    [InlineData("osPlatform", OSPlatformMatch)]
    [InlineData("osArchitecture", OSArchitectureMatch)]
    [InlineData("processArchitecture", ProcessArchitectureMatch)]
    [InlineData("dependencyRuntimeTarget", DependencyRuntimeTargetMatch)]
    [InlineData("dependency", DependencyManifestFingerprintMatch)]
    public void Verifier_Mismatch_IdentifiesExactFailingCheck(string field, string expectedFailingCode)
    {
        PersistedResearchExperimentRecord record = ResearchExperimentRecordFixture.CreateV4Record(SourceBuild(), Environment());
        ResearchExecutionEnvironmentProvenance current = field switch
        {
            "framework" => Environment(framework: ".NET 9.0.0"),
            "runtimeVersion" => Environment(runtimeVersion: "9.0.0"),
            "runtimeIdentifier" => Environment(runtimeIdentifier: "linux-x64"),
            "osPlatform" => Environment(osPlatform: "Linux"),
            "osArchitecture" => Environment(osArchitecture: "Arm64"),
            "processArchitecture" => Environment(processArchitecture: "Arm64"),
            "dependencyRuntimeTarget" => Environment(dependencyRuntimeTarget: ".NETCoreApp,Version=v9.0"),
            _ => Environment(dependencies: new[] { new ResearchResolvedDependency("app", "1.0.0", "project", null) })
        };

        ResearchExecutionEnvironmentProvenanceVerificationResult result = new ResearchExecutionEnvironmentProvenanceVerifier(
            new StubEnvironmentProvider(current)).Verify(record);

        Assert.Equal(Mismatch, result.Status);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, check => check.Code == expectedFailingCode).Status);
        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(check => check.Code));
    }

    [Fact]
    public void Verifier_LegacyRecord_ReturnsUnsupported()
    {
        PersistedResearchExperimentRecord record = ResearchExperimentRecordFixture.CreateV3Record(SourceBuild());
        ResearchExecutionEnvironmentProvenanceVerificationResult result = new ResearchExecutionEnvironmentProvenanceVerifier(
            new StubEnvironmentProvider(Environment())).Verify(record);

        Assert.Equal(Unsupported, result.Status);
        Assert.Equal(CheckStatus.Unsupported, Assert.Single(result.Checks).Status);
    }

    [Fact]
    public void Verifier_CaptureFailure_ReturnsUnsupportedWithStableReason()
    {
        PersistedResearchExperimentRecord record = ResearchExperimentRecordFixture.CreateV4Record(SourceBuild(), Environment());
        ResearchExecutionEnvironmentProvenanceVerifier verifier = new(new StubEnvironmentProvider(() =>
            throw new ResearchExecutionEnvironmentProvenanceException(DependencyManifestUnavailable, "manifest unavailable")));

        ResearchExecutionEnvironmentProvenanceVerificationResult result = verifier.Verify(record);
        Assert.Equal(Unsupported, result.Status);
        Assert.Contains(ExecutionEnvironmentCaptureFailed, result.ReasonCodes);
        Assert.Contains(DependencyManifestUnavailable, result.ReasonCodes);
        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(check => check.Code));
    }

    [Fact]
    public void Verifier_ForgedRecordMissingBinding_ReturnsFailed()
    {
        PersistedResearchExperimentRecord valid = ResearchExperimentRecordFixture.CreateV4Record(SourceBuild(), Environment());
        PersistedResearchExperimentRecord forged = ResearchExperimentRecordFixture.Forge(valid, environmentProvenance: valid.ExecutionEnvironmentProvenance, binding: null);

        ResearchExecutionEnvironmentProvenanceVerificationResult result = new ResearchExecutionEnvironmentProvenanceVerifier(
            new StubEnvironmentProvider(Environment())).Verify(forged);

        Assert.Equal(Failed, result.Status);
        Assert.Equal(CheckStatus.Fail, Assert.Single(result.Checks, check => check.Code == ExecutionEnvironmentBindingValid).Status);
    }

    // ---- CLI --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cli_ShowExecutionEnvironmentProvenance_IsOfflineAndWellFormed()
    {
        using Process process = Process.Start(CreateCliStartInfo("--show-execution-environment-provenance")) ?? throw new InvalidOperationException("Could not start CLI.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("EXECUTION_ENVIRONMENT_PROVENANCE_STATUS=Available", stdout);
        Assert.Contains("FRAMEWORK_DESCRIPTION=", stdout);
        Assert.Contains("RUNTIME_IDENTIFIER=", stdout);
        Assert.Contains("DEPENDENCY_RUNTIME_TARGET=", stdout);
        Assert.Contains("DEPENDENCY_MANIFEST_FINGERPRINT=", stdout);
        Assert.Contains("EXECUTION_ENVIRONMENT_PROVENANCE_FINGERPRINT=", stdout);
        Assert.DoesNotContain(AppContext.BaseDirectory, stdout);
        Assert.True(int.Parse(Line(stdout, "DEPENDENCY_COUNT"), CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Cli_VerifyExecutionEnvironmentProvenance_LegacyRecordUnsupported()
    {
        string root = TemporaryDirectory();
        try
        {
            PersistedResearchExperimentRecord record = ResearchExperimentRecordFixture.CreateV3Record(SourceBuild());
            await new JsonResearchExperimentRepository(root).SaveAsync(record);

            using Process process = Process.Start(CreateCliStartInfo("--verify-execution-environment-provenance", record.ExperimentId, "--experiment-store", root)) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("EXECUTION_ENVIRONMENT_VERIFICATION_STATUS=Unsupported", stdout);
            Assert.Contains("CHECK=EXECUTION_ENVIRONMENT_PROVENANCE_PRESENT:Unsupported", stdout);
        }
        finally { DeleteDirectory(root); }
    }

    [Fact]
    public async Task Cli_ExecutionEnvironmentModes_ConflictWithOtherModes()
    {
        using Process process = Process.Start(CreateCliStartInfo("--show-execution-environment-provenance", "--reproduce-experiment", "EXP-1")) ?? throw new InvalidOperationException("Could not start CLI.");
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("CONFLICTING_OPERATION_MODES", stderr);
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private const string V2PreimageJson =
        "{\"bindingVersion\":\"research-execution-provenance-binding-v2\",\"experimentFingerprint\":\"exp-fp\",\"parameterSnapshotFingerprint\":\"param-fp\"," +
        "\"experimentStrategyParameterFingerprint\":\"exp-strategy-fp\",\"artifactStrategyParameterFingerprint\":\"artifact-strategy-fp\"," +
        "\"experimentPortfolioConfigurationFingerprint\":\"exp-portfolio-fp\",\"artifactPortfolioConfigurationFingerprint\":\"artifact-portfolio-fp\"," +
        "\"experimentAnalysisConfigurationFingerprint\":\"exp-analysis-fp\",\"artifactAnalysisFingerprint\":\"artifact-analysis-fp\"," +
        "\"datasetFingerprint\":\"dataset-fp\",\"artifactVersion\":\"portfolio-research-v2\",\"artifactFingerprint\":\"artifact-fp\"," +
        "\"sourceBuildProvenanceFingerprint\":\"source-build-fp\"}";

    private const string V3PreimageJson =
        "{\"bindingVersion\":\"research-execution-provenance-binding-v3\",\"experimentFingerprint\":\"exp-fp\",\"parameterSnapshotFingerprint\":\"param-fp\"," +
        "\"experimentStrategyParameterFingerprint\":\"exp-strategy-fp\",\"artifactStrategyParameterFingerprint\":\"artifact-strategy-fp\"," +
        "\"experimentPortfolioConfigurationFingerprint\":\"exp-portfolio-fp\",\"artifactPortfolioConfigurationFingerprint\":\"artifact-portfolio-fp\"," +
        "\"experimentAnalysisConfigurationFingerprint\":\"exp-analysis-fp\",\"artifactAnalysisFingerprint\":\"artifact-analysis-fp\"," +
        "\"datasetFingerprint\":\"dataset-fp\",\"artifactVersion\":\"portfolio-research-v2\",\"artifactFingerprint\":\"artifact-fp\"," +
        "\"sourceBuildProvenanceFingerprint\":\"source-build-fp\",\"executionEnvironmentProvenanceFingerprint\":\"environment-fp\"}";

    private static string HashLiteral(string json) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static ResearchSourceBuildProvenance SourceBuild() => new(
        ResearchSourceBuildProvenance.CurrentProvenanceVersion, new string('a', 40), new string('b', 40),
        ResearchSourceBuildProvenance.CleanSourceState, "Release", ".NETCoreApp,Version=v10.0",
        "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333");

    private static ResearchExecutionEnvironmentProvenance Environment(
        string framework = ".NET 10.0.12", string runtimeVersion = "10.0.12", string runtimeIdentifier = "win-x64",
        string osPlatform = "Windows", string osArchitecture = "X64", string processArchitecture = "X64",
        string dependencyRuntimeTarget = RuntimeTarget, IReadOnlyList<ResearchResolvedDependency>? dependencies = null) =>
        ResearchExecutionEnvironmentProvenance.Create(
            ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion, framework, runtimeVersion, runtimeIdentifier,
            osPlatform, osArchitecture, processArchitecture, dependencyRuntimeTarget, dependencies ?? DefaultDependencies());

    private static IReadOnlyList<ResearchResolvedDependency> DefaultDependencies() => new[]
    {
        new ResearchResolvedDependency("app", "1.0.0", "project", new[] { new ResearchResolvedDependencyReference("lib.a", "1.0.0") }),
        new ResearchResolvedDependency("lib.a", "1.0.0", "package", null)
    };

    private static string CanonicalManifest(bool pretty, string libraryVersion = "1.0.0", bool extraLibrary = false, bool includeLibrary = true,
        bool extraEdge = false, string runtimeTarget = RuntimeTarget, bool upperCaseNames = false)
    {
        string appName = upperCaseNames ? "App" : "app";
        string libraryName = upperCaseNames ? "Lib.A" : "lib.a";
        StringBuilder appDependencies = new();
        if (includeLibrary) appDependencies.Append($"\"{(upperCaseNames ? "Lib.A" : "lib.a")}\": \"{libraryVersion}\"");
        if (extraEdge)
        {
            if (appDependencies.Length > 0) appDependencies.Append(", ");
            appDependencies.Append("\"extra.lib\": \"3.0.0\"");
        }

        List<string> targetEntries = new()
        {
            $"\"{appName}/1.0.0\": {{ \"dependencies\": {{ {appDependencies} }}, \"runtime\": {{ \"{appName}.dll\": {{}} }} }}"
        };
        List<string> libraryEntries = new() { $"\"{appName}/1.0.0\": {{ \"type\": \"project\" }}" };
        if (includeLibrary)
        {
            targetEntries.Add($"\"{libraryName}/{libraryVersion}\": {{ \"runtime\": {{ \"{libraryName}.dll\": {{}} }} }}");
            libraryEntries.Add($"\"{libraryName}/{libraryVersion}\": {{ \"type\": \"package\" }}");
        }
        if (extraLibrary)
        {
            targetEntries.Add("\"extra.lib/3.0.0\": { \"runtime\": { \"extra.lib.dll\": {} } }");
            libraryEntries.Add("\"extra.lib/3.0.0\": { \"type\": \"package\" }");
        }

        string json =
            "{ " + ManifestContractJsonPrefix + ": { \"name\": \"" + runtimeTarget + "\", \"signature\": \"\" }, " +
            "\"targets\": { \"" + runtimeTarget + "\": { " + string.Join(", ", targetEntries) + " } }, " +
            "\"libraries\": { " + string.Join(", ", libraryEntries) + " } }";
        return pretty ? json.Replace("{ ", "{\n  ").Replace(" }", "\n}") : json;
    }

    /// <summary>Rewrites the manifest with reversed object property order and collapsed whitespace.</summary>
    private static string ReorderManifest(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("libraries");
            WriteReversed(writer, root.GetProperty("libraries"));
            writer.WritePropertyName("targets");
            WriteReversed(writer, root.GetProperty("targets"));
            writer.WritePropertyName("runtimeTarget");
            writer.WriteStartObject();
            writer.WriteString("signature", "");
            writer.WriteString("name", root.GetProperty("runtimeTarget").GetProperty("name").GetString());
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteReversed(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartObject();
        foreach (JsonProperty property in element.EnumerateObject().Reverse())
        {
            writer.WritePropertyName(property.Name);
            if (property.Value.ValueKind == JsonValueKind.Object) WriteReversed(writer, property.Value);
            else property.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static string WriteManifest(string root, string json, string fileName = "app.deps.json")
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    private static ResearchDependencyManifest ReadManifest(string path) => new DotNetDependencyManifestReader(() => path).Read();

    private static string Line(string stdout, string key)
    {
        string? match = stdout.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(key + "=", StringComparison.Ordinal));
        Assert.NotNull(match);
        return match![(key.Length + 1)..];
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
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-ExecutionEnvironmentTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string SolutionRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private sealed class StubEnvironmentProvider : IResearchExecutionEnvironmentProvenanceProvider
    {
        private readonly Func<ResearchExecutionEnvironmentProvenance> _capture;
        public StubEnvironmentProvider(ResearchExecutionEnvironmentProvenance provenance) : this(() => provenance) { }
        public StubEnvironmentProvider(Func<ResearchExecutionEnvironmentProvenance> capture) => _capture = capture;
        public ResearchExecutionEnvironmentProvenance Capture() => _capture();
    }
}
