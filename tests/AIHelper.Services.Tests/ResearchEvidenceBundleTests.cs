using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleCheckCodes;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleReasonCodes;
using Status = AIHelper.Core.Sparrow.ResearchEvidenceBundleVerificationStatus;
using ReexecStatus = AIHelper.Core.Sparrow.ResearchEvidenceBundleReexecutionStatus;

namespace AIHelper.Services.Tests;

public sealed class ResearchEvidenceBundleTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Tree = "89abcdef0123456789abcdef0123456789abcdef";
    private const string ToolMvid = "11111111-1111-1111-1111-111111111111";
    private const string ServicesMvid = "22222222-2222-2222-2222-222222222222";
    private const string CoreMvid = "33333333-3333-3333-3333-333333333333";
    private const double BacktestCost = 0.001;
    private const double BacktestSlippage = 0.002;
    private const decimal PortfolioCommission = 0.0003m;
    private const decimal PortfolioSlippage = 0.0004m;

    private static ResearchSourceBuildProvenance FixedProvenance() => new(
        ResearchSourceBuildProvenance.CurrentProvenanceVersion, Commit, Tree, ResearchSourceBuildProvenance.CleanSourceState,
        "Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid);

    private static ResearchExecutionEnvironmentProvenance FixedEnvironmentProvenance() => ResearchExecutionEnvironmentProvenance.Create(
        ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion, ".NET 10.0.12", "10.0.12", "win-x64", "Windows", "X64", "X64",
        ".NETCoreApp,Version=v10.0",
        new[]
        {
            new ResearchResolvedDependency("AIHelper.HistoricalDataTool", "1.0.0", "project",
                new[] { new ResearchResolvedDependencyReference("AIHelper.Services", "1.0.0") }),
            new ResearchResolvedDependency("AIHelper.Services", "1.0.0", "project", null),
            new ResearchResolvedDependency("Serilog", "4.3.1", "package", null)
        });

    private static ManagedResearchExperimentRunner CreateRunner() => new(
        new StubSourceBuildProvider(FixedProvenance()),
        artifactWriter: null,
        timeProvider: new FixedTimeProvider(DateTimeOffset.UnixEpoch),
        repositoryFactory: null,
        executionEnvironmentProvenanceProvider: new StubEnvironmentProvider(FixedEnvironmentProvenance()));

    // ==============================================================================================
    // 1. Export Tests
    // ==============================================================================================

    [Fact]
    public async Task Export_ClassicManagedExperiment_ProducesCompleteBundle()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            ResearchEvidenceBundleExportResult result = await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            Assert.Equal(harness.Request.ExperimentId, result.ExperimentId);
            Assert.Equal(ResearchEvidenceBundleConstants.BundleVersionV1, result.BundleVersion);
            Assert.True(File.Exists(bundlePath));

            using ZipArchive zip = ZipFile.OpenRead(bundlePath);
            Assert.Equal(ResearchEvidenceBundleConstants.ExactEntryCount, zip.Entries.Count);
            foreach (string entry in ResearchEvidenceBundleConstants.AllAllowedEntries)
            {
                Assert.NotNull(zip.GetEntry(entry));
            }
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_V2ManagedExperiment_ProducesCompleteBundle()
    {
        Harness harness = await CreateHarnessAsync("v2");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            ResearchEvidenceBundleExportResult result = await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            Assert.Equal(harness.Request.ExperimentId, result.ExperimentId);
            Assert.Equal(ResearchEvidenceBundleConstants.BundleVersionV1, result.BundleVersion);
            Assert.True(File.Exists(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_RefusesMissingArtifact()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);
            File.Delete(harness.ArtifactPath);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<FileNotFoundException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.False(File.Exists(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_RefusesMissingDataset()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);
            string missingDataset = Path.Combine(harness.Root, "non-existent-dataset.json");

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, missingDataset,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<FileNotFoundException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.False(File.Exists(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_RefusesMissingParameterSnapshot()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);
            string missingParams = Path.Combine(harness.Root, "non-existent-params.json");

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                missingParams, BacktestCost, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<FileNotFoundException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.False(File.Exists(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Theory]
    [InlineData(-0.001, 0.002)]
    [InlineData(0.001, -0.002)]
    [InlineData(double.NaN, 0.002)]
    [InlineData(0.001, double.PositiveInfinity)]
    public void Export_RefusesInvalidExplicitBacktestCosts(double cost, double slippage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResearchEvidenceBundleExportRequest(
            "EXP-1", "D:\\store", "D:\\art.json", "D:\\ds.json", "D:\\param.json", cost, slippage, "D:\\out.zip"));
    }

    [Fact]
    public async Task Export_RefusesWrongDatasetFingerprint()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string alternateDataset = Path.Combine(harness.Root, "alt-dataset.json");
            HistoricalDatasetFile altFile = CreateDatasetFile("alt-dataset-id");
            altFile.TradingDates = altFile.TradingDates!.Take(10).ToList();
            await File.WriteAllTextAsync(alternateDataset, JsonSerializer.Serialize(altFile));

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, alternateDataset,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.False(File.Exists(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_RefusesWrongParameterFingerprint()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string alternateParams = Path.Combine(harness.Root, "alt-params.json");
            await File.WriteAllTextAsync(alternateParams, JsonSerializer.Serialize(new SparrowClassicParameterSnapshot(true, 2, 10, 2.0, 5, false, 1, .30)));

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                alternateParams, BacktestCost, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.False(File.Exists(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_RefusesExistingOutputZipWithoutAlteringIt()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            await File.WriteAllTextAsync(bundlePath, "pre-existing-content");

            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<IOException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.Equal("pre-existing-content", await File.ReadAllTextAsync(bundlePath));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Export_FailureLeavesNoFalselyCommittedBundle()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            // Pass divergent backtest cost
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, 0.05, BacktestSlippage, bundlePath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => new ResearchEvidenceBundleExporter().ExportAsync(exportRequest));
            Assert.False(File.Exists(bundlePath));
            Assert.Empty(Directory.GetFiles(harness.Root, "*.tmp"));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    // ==============================================================================================
    // 2. Offline Verification Tests
    // ==============================================================================================

    [Fact]
    public async Task Verify_ExportedBundleVerifiesInIndependentLocation_WithoutOriginalFiles()
    {
        Harness harness = await CreateHarnessAsync("classic");
        string independentRoot = TemporaryDirectory();
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string originalBundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, originalBundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string independentBundlePath = Path.Combine(independentRoot, "moved-bundle.zip");
            File.Move(originalBundlePath, independentBundlePath);

            // Delete original experiment store and artifact
            DeleteDirectory(harness.StorePath);
            File.Delete(harness.ArtifactPath);
            File.Delete(harness.DatasetPath);
            File.Delete(harness.ParametersPath);

            // Verify bundle in independent location
            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(independentBundlePath, reexecute: false);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.NotRequested, result.ReexecutionStatus);
            Assert.Equal(0, result.FailedCheckCount);
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("Verified", result.ExperimentRecordStatus);
            Assert.Equal("Verified", result.ArtifactLineageStatus);
            Assert.Equal("Verified", result.DatasetStatus);
            Assert.Equal("Verified", result.ParameterStatus);
        }
        finally
        {
            DeleteDirectory(harness.Root);
            DeleteDirectory(independentRoot);
        }
    }

    [Fact]
    public async Task Verify_ParameterIdentityDomains_AreProperlySeparated()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            // Verify parameter domain distinction in record and artifact
            PersistedResearchExperimentRecord record = await new JsonResearchExperimentRepository(harness.StorePath).GetAsync(harness.Request.ExperimentId);
            string artifactJson = await File.ReadAllTextAsync(harness.ArtifactPath);
            SparrowPortfolioResearchArtifact artifact = JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(artifactJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            string paramJson = await File.ReadAllTextAsync(harness.ParametersPath);
            SparrowClassicParameterSnapshot typedSnapshot = JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(paramJson)!;

            // Recompute executable backtest parameter fingerprint
            var pr = artifact.PortfolioRequest;
            SparrowBacktestRequest backtestReq = new(pr.StrategyMode, pr.StrategyVersion, pr.StartDate, pr.EndDate, pr.TopN,
                new[] { pr.HorizonTradingDays }, BacktestCost, BacktestSlippage, ClassicParameters: typedSnapshot);
            string executableParamFp = SparrowHistoricalFingerprint.Parameters(backtestReq);

            // Executable strategy parameter fingerprint matches artifact and binding
            Assert.Equal(artifact.PortfolioRequest.StrategyParameterFingerprint, executableParamFp);
            Assert.Equal(record.ExecutionProvenanceBinding!.ArtifactStrategyParameterFingerprint, executableParamFp);

            // Executable strategy parameter fingerprint is distinct from generic experiment parameter fingerprint
            Assert.NotEqual(executableParamFp, record.Definition.Parameters.Fingerprint);
            Assert.NotEqual(executableParamFp, record.ExecutionProvenanceBinding.ParameterSnapshotFingerprint);
            Assert.NotEqual(executableParamFp, record.Definition.Parameters.StrategyFingerprint);
            Assert.NotEqual(executableParamFp, record.ExecutionProvenanceBinding.ExperimentStrategyParameterFingerprint);

            // Generic parameter fingerprint matches experiment definition and binding
            Assert.Equal(record.Definition.Parameters.Fingerprint, record.ExecutionProvenanceBinding.ParameterSnapshotFingerprint);
            Assert.Equal(record.Definition.Parameters.StrategyFingerprint, record.ExecutionProvenanceBinding.ExperimentStrategyParameterFingerprint);

            // The bundle verifier validates this separation
            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(bundlePath);
            Assert.Equal(Status.Verified, result.Status);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_StoredProvenance_DoesNotRequireMatchingCurrentEnvironment()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            // Verifier validates internal consistency of stored provenance without inspecting current machine
            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(bundlePath);
            Assert.Equal(Status.Verified, result.Status);
            Assert.Contains(result.Checks, c => c.CheckCode == StoredProvenance && c.Status == "Pass");
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_TamperedArtifact_FailsIntegrityCheck()
    {
        await AssertTamperedEntryFailsAsync(ResearchEvidenceBundleConstants.PortfolioArtifactEntry, "tampered-artifact-content", BundleEntryIntegrityFailed);
    }

    [Fact]
    public async Task Verify_TamperedDataset_FailsIntegrityCheck()
    {
        await AssertTamperedEntryFailsAsync(ResearchEvidenceBundleConstants.HistoricalDatasetEntry, "tampered-dataset-content", BundleEntryIntegrityFailed);
    }

    [Fact]
    public async Task Verify_TamperedParameters_FailsIntegrityCheck()
    {
        await AssertTamperedEntryFailsAsync(ResearchEvidenceBundleConstants.StrategyParametersEntry, "tampered-parameters-content", BundleEntryIntegrityFailed);
    }

    [Fact]
    public async Task Verify_TamperedReexecutionInputs_FailsIntegrityCheck()
    {
        await AssertTamperedEntryFailsAsync(ResearchEvidenceBundleConstants.ReexecutionInputsEntry, "tampered-reexec-inputs", BundleEntryIntegrityFailed);
    }

    [Fact]
    public async Task Verify_TamperedManifestFingerprint_Fails()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            // Tamper manifest fingerprint inside ZIP
            string tamperedPath = Path.Combine(harness.Root, "tampered.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    if (entry.FullName == ResearchEvidenceBundleConstants.ManifestEntry)
                    {
                        using StreamReader reader = new(inS);
                        string manifestText = await reader.ReadToEndAsync();
                        ResearchEvidenceBundleManifest manifest = JsonSerializer.Deserialize<ResearchEvidenceBundleManifest>(manifestText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        ResearchEvidenceBundleManifest tamperedManifest = new(
                            manifest.BundleVersion, manifest.ExperimentId, manifest.ExperimentFingerprint,
                            manifest.DatasetFingerprint, manifest.ArtifactFingerprint, manifest.ExecutionBindingFingerprint,
                            manifest.SourceBuildProvenanceFingerprint, manifest.ExecutionEnvironmentProvenanceFingerprint,
                            manifest.Entries, "0000000000000000000000000000000000000000000000000000000000000000");

                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(tamperedManifest, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        await outS.WriteAsync(bytes);
                    }
                    else
                    {
                        await inS.CopyToAsync(outS);
                    }
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleManifestFingerprintMismatch, result.ReasonCodes);

            // Entry integrity completed; the record, artifact, dataset and parameter stages never ran.
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("NotChecked", result.ExperimentRecordStatus);
            Assert.Equal("NotChecked", result.ArtifactLineageStatus);
            Assert.Equal("NotChecked", result.DatasetStatus);
            Assert.Equal("NotChecked", result.ParameterStatus);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_MissingEntry_FailsArchiveStructure()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "missing-entry.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    if (entry.FullName == ResearchEvidenceBundleConstants.StrategyParametersEntry) continue;
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    await inS.CopyToAsync(outS);
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleArchiveInvalid, result.ReasonCodes);

            // Archive structure failed before any component stage ran.
            Assert.Equal("NotChecked", result.EntryIntegrityStatus);
            Assert.Equal("NotChecked", result.ExperimentRecordStatus);
            Assert.Equal("NotChecked", result.ArtifactLineageStatus);
            Assert.Equal("NotChecked", result.DatasetStatus);
            Assert.Equal("NotChecked", result.ParameterStatus);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_UnexpectedEntry_FailsArchiveStructure()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "unexpected-entry.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    await inS.CopyToAsync(outS);
                }
                ZipArchiveEntry unexpected = dest.CreateEntry("unexpected.txt");
                using Stream uStream = unexpected.Open();
                await uStream.WriteAsync(Encoding.UTF8.GetBytes("unauthorized"));
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleArchiveInvalid, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_PathTraversalEntry_FailsArchiveStructure()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "traversal.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    string name = entry.FullName == ResearchEvidenceBundleConstants.StrategyParametersEntry
                        ? "evidence/../traversal.json"
                        : entry.FullName;
                    ZipArchiveEntry newEntry = dest.CreateEntry(name);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    await inS.CopyToAsync(outS);
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleArchiveInvalid, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_BackslashPath_FailsArchiveStructure()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "backslash.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    string name = entry.FullName.Replace('/', '\\');
                    ZipArchiveEntry newEntry = dest.CreateEntry(name);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    await inS.CopyToAsync(outS);
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleArchiveInvalid, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_UnsupportedBundleVersion_ReturnsUnsupported()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "unsupported-version.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    if (entry.FullName == ResearchEvidenceBundleConstants.ManifestEntry)
                    {
                        using StreamReader reader = new(inS);
                        string manifestText = await reader.ReadToEndAsync();
                        ResearchEvidenceBundleManifest manifest = JsonSerializer.Deserialize<ResearchEvidenceBundleManifest>(manifestText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        ResearchEvidenceBundleManifest tamperedManifest = new(
                            "research-evidence-bundle-v99", manifest.ExperimentId, manifest.ExperimentFingerprint,
                            manifest.DatasetFingerprint, manifest.ArtifactFingerprint, manifest.ExecutionBindingFingerprint,
                            manifest.SourceBuildProvenanceFingerprint, manifest.ExecutionEnvironmentProvenanceFingerprint,
                            manifest.Entries, manifest.BundleManifestFingerprint);

                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(tamperedManifest, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        await outS.WriteAsync(bytes);
                    }
                    else
                    {
                        await inS.CopyToAsync(outS);
                    }
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Unsupported, result.Status);
            Assert.Contains(BundleUnsupportedVersion, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    // ==============================================================================================
    // 3. Full Re-execution Tests
    // ==============================================================================================

    [Fact]
    public async Task Verify_FullReexecution_Classic_ReturnsEquivalent()
    {
        Harness harness = await CreateHarnessAsync("classic");
        string independentRoot = TemporaryDirectory();
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(independentRoot, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            // Re-execution in an independent location without original repo/store
            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(bundlePath, reexecute: true);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Equivalent, result.ReexecutionStatus);
            Assert.Equal(0, result.FailedCheckCount);
        }
        finally
        {
            DeleteDirectory(harness.Root);
            DeleteDirectory(independentRoot);
        }
    }

    [Fact]
    public async Task Verify_FullReexecution_V2_ReturnsEquivalent()
    {
        Harness harness = await CreateHarnessAsync("v2");
        string independentRoot = TemporaryDirectory();
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(independentRoot, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(bundlePath, reexecute: true);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Equivalent, result.ReexecutionStatus);
            Assert.Equal(0, result.FailedCheckCount);
        }
        finally
        {
            DeleteDirectory(harness.Root);
            DeleteDirectory(independentRoot);
        }
    }

    // ==============================================================================================
    // 4. CLI Tests
    // ==============================================================================================

    [Fact]
    public async Task Cli_ExportAndVerify_EndToEnd()
    {
        Harness harness = await CreateHarnessAsync("classic");
        string bundlePath = Path.Combine(harness.Root, "cli-bundle.zip");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            // Export via CLI
            (int exportExit, string exportStdout) = await RunCliAsync(
                "--export-research-evidence-bundle", harness.Request.ExperimentId,
                "--experiment-store", harness.StorePath,
                "--artifact", harness.ArtifactPath,
                "--dataset", harness.DatasetPath,
                "--parameters", harness.ParametersPath,
                "--backtest-round-trip-cost-rate", "0.001",
                "--backtest-slippage-rate", "0.002",
                "--output", bundlePath);

            Assert.Equal(0, exportExit);
            Assert.Contains("RESEARCH_EVIDENCE_BUNDLE_STATUS=Exported", exportStdout);
            Assert.Contains($"EXPERIMENT_ID={harness.Request.ExperimentId}", exportStdout);
            Assert.Contains("BUNDLE_VERSION=research-evidence-bundle-v1", exportStdout);
            Assert.Contains("BUNDLE_MANIFEST_FINGERPRINT=", exportStdout);
            Assert.True(File.Exists(bundlePath));

            // Verify via CLI without re-execution
            (int verifyExit, string verifyStdout) = await RunCliAsync(
                "--verify-research-evidence-bundle", bundlePath);

            Assert.Equal(0, verifyExit);
            Assert.Contains("RESEARCH_EVIDENCE_BUNDLE_VERIFICATION_STATUS=Verified", verifyStdout);
            Assert.Contains($"EXPERIMENT_ID={harness.Request.ExperimentId}", verifyStdout);
            Assert.Contains("ENTRY_INTEGRITY_STATUS=Verified", verifyStdout);
            Assert.Contains("REEXECUTION_STATUS=NotRequested", verifyStdout);

            // Verify via CLI with re-execution
            (int reexecExit, string reexecStdout) = await RunCliAsync(
                "--verify-research-evidence-bundle", bundlePath,
                "--reexecute", "true");

            Assert.Equal(0, reexecExit);
            Assert.Contains("RESEARCH_EVIDENCE_BUNDLE_VERIFICATION_STATUS=Verified", reexecStdout);
            Assert.Contains("REEXECUTION_STATUS=Equivalent", reexecStdout);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Cli_BundleExport_ConflictsWithOtherModes()
    {
        (int exitCode, _) = await RunCliAsync(
            "--export-research-evidence-bundle", "EXP-1",
            "--verify-research-evidence-bundle", "D:\\bundle.zip");

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Cli_BundleVerification_ConflictsWithManagedExperiment()
    {
        (int exitCode, _) = await RunCliAsync(
            "--verify-research-evidence-bundle", "D:\\bundle.zip",
            "--run-research-experiment", "EXP-1");

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Verify_DuplicateEntry_FailsArchiveStructure()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "duplicate-entry.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    await inS.CopyToAsync(outS);
                }
                ZipArchiveEntry duplicate = dest.CreateEntry("Manifest.json");
                using Stream dStream = duplicate.Open();
                await dStream.WriteAsync(Encoding.UTF8.GetBytes("{}"));
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleArchiveInvalid, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_OversizedManifest_FailsSecurityLimit()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "oversized-manifest.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    if (entry.FullName == ResearchEvidenceBundleConstants.ManifestEntry)
                    {
                        byte[] bigManifest = new byte[1100 * 1024];
                        Array.Fill(bigManifest, (byte)' ');
                        await outS.WriteAsync(bigManifest);
                    }
                    else
                    {
                        await inS.CopyToAsync(outS);
                    }
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleSecurityLimitExceeded, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_WrongArtifactFingerprintInManifest_Fails()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "wrong-artifact-fp.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    if (entry.FullName == ResearchEvidenceBundleConstants.ManifestEntry)
                    {
                        string manifestText = await new StreamReader(inS).ReadToEndAsync();
                        ResearchEvidenceBundleManifest manifest = JsonSerializer.Deserialize<ResearchEvidenceBundleManifest>(manifestText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                        ResearchEvidenceBundleManifest modified = ResearchEvidenceBundleManifest.Create(
                            manifest.BundleVersion, manifest.ExperimentId, manifest.ExperimentFingerprint,
                            manifest.DatasetFingerprint, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
                            manifest.ExecutionBindingFingerprint, manifest.SourceBuildProvenanceFingerprint,
                            manifest.ExecutionEnvironmentProvenanceFingerprint, manifest.Entries);

                        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(modified, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                        await outS.WriteAsync(bytes);
                    }
                    else
                    {
                        await inS.CopyToAsync(outS);
                    }
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundlePortfolioArtifactInvalid, result.ReasonCodes);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_FullReexecution_DivergenceReportedSeparatelyFromIntegrity()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchReexecutionValidationResult mockDivergence = new(
                ResearchReexecutionStatus.Diverged,
                new[] { new ResearchReexecutionCheck("METRIC_TOTAL_RETURN", ResearchReexecutionCheckStatus.Fail, "0.10", "0.15", "Return diverged.") },
                harness.Request.ExperimentId, "exp-fp", "art-fp", "repro-fp", "ds-fp", "Classic", "v1", "param-fp", "port-fp",
                new[] { ResearchReexecutionReasonCodes.ArtifactFingerprintDiverged },
                null);

            ResearchEvidenceBundleVerifier verifier = new(reexecutionValidator: new StubReexecutionValidator(mockDivergence));
            ResearchEvidenceBundleVerificationResult result = await verifier.VerifyAsync(bundlePath, reexecute: true);

            // Structural integrity stays Verified; the divergence is reported in the independent domain.
            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Diverged, result.ReexecutionStatus);
            Assert.Contains(BundleReexecutionDiverged, result.ReasonCodes);
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("Verified", result.ExperimentRecordStatus);
            Assert.Equal("Verified", result.ArtifactLineageStatus);
            Assert.Equal("Verified", result.DatasetStatus);
            Assert.Equal("Verified", result.ParameterStatus);

            // The aggregate failed-check count includes the re-execution check and does not redefine Status.
            Assert.True(result.FailedCheckCount > 0);
            Assert.Contains(result.Checks, c => c.CheckCode == Reexecution && c.Status == "Fail");
            Assert.False(result.FullVerificationSucceeded);
            Assert.DoesNotContain(result.Checks, c => c.CheckCode == PortfolioArtifact && c.Status != "Pass");
        }
        finally { DeleteDirectory(harness.Root); }
    }

    // ==============================================================================================
    // 5. Verification Status Contract Tests
    // ==============================================================================================

    [Fact]
    public async Task Verify_StructuralOnly_ReportsVerifiedWithNotRequestedReexecution()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(bundlePath);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.NotRequested, result.ReexecutionStatus);
            Assert.Equal(0, result.FailedCheckCount);
            Assert.True(result.FullVerificationSucceeded);
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("Verified", result.ExperimentRecordStatus);
            Assert.Equal("Verified", result.ArtifactLineageStatus);
            Assert.Equal("Verified", result.DatasetStatus);
            Assert.Equal("Verified", result.ParameterStatus);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_EquivalentReexecution_KeepsStructuralVerified()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchReexecutionValidationResult equivalent = new(
                ResearchReexecutionStatus.Equivalent, Array.Empty<ResearchReexecutionCheck>(), harness.Request.ExperimentId);

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier(
                reexecutionValidator: new StubReexecutionValidator(equivalent)).VerifyAsync(bundlePath, reexecute: true);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Equivalent, result.ReexecutionStatus);
            Assert.Equal(0, result.FailedCheckCount);
            Assert.True(result.FullVerificationSucceeded);
            Assert.Contains(result.Checks, c => c.CheckCode == Reexecution && c.Status == "Pass");
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_UnsupportedReexecution_KeepsStructuralVerified()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchReexecutionValidationResult unsupported = new(
                ResearchReexecutionStatus.Unsupported, Array.Empty<ResearchReexecutionCheck>(), harness.Request.ExperimentId,
                reasonCodes: new[] { ResearchReexecutionReasonCodes.LegacyArtifactUnsupported });

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier(
                reexecutionValidator: new StubReexecutionValidator(unsupported)).VerifyAsync(bundlePath, reexecute: true);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Unsupported, result.ReexecutionStatus);
            Assert.Contains(BundleReexecutionUnsupported, result.ReasonCodes);
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("Verified", result.ParameterStatus);
            Assert.False(result.FullVerificationSucceeded);
            Assert.True(result.FailedCheckCount > 0);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_FailedReexecution_KeepsStructuralVerified()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchReexecutionValidationResult failed = new(
                ResearchReexecutionStatus.Failed, Array.Empty<ResearchReexecutionCheck>(), harness.Request.ExperimentId,
                reasonCodes: new[] { ResearchReexecutionReasonCodes.BacktestExecutionFailed });

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier(
                reexecutionValidator: new StubReexecutionValidator(failed)).VerifyAsync(bundlePath, reexecute: true);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Failed, result.ReexecutionStatus);
            Assert.Contains(BundleReexecutionFailed, result.ReasonCodes);
            Assert.Equal("Verified", result.ArtifactLineageStatus);
            Assert.Equal("Verified", result.DatasetStatus);
            Assert.False(result.FullVerificationSucceeded);
            Assert.True(result.FailedCheckCount > 0);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_ReexecutionException_ProducesStructuredFailureWithoutInvalidatingBundle()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier(
                reexecutionValidator: new ThrowingReexecutionValidator(new InvalidOperationException("simulated execution crash")))
                .VerifyAsync(bundlePath, reexecute: true);

            Assert.Equal(Status.Verified, result.Status);
            Assert.Equal(ReexecStatus.Failed, result.ReexecutionStatus);
            Assert.Contains(BundleReexecutionFailed, result.ReasonCodes);
            Assert.Contains(result.Checks, c => c.CheckCode == Reexecution && c.Status == "Fail");
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("Verified", result.ExperimentRecordStatus);
            Assert.Equal("Verified", result.ArtifactLineageStatus);
            Assert.Equal("Verified", result.DatasetStatus);
            Assert.Equal("Verified", result.ParameterStatus);
            Assert.False(result.FullVerificationSucceeded);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_ReexecutionCancellation_PropagatesInsteadOfReportingSuccess()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");

            ResearchEvidenceBundleVerifier verifier = new(
                reexecutionValidator: new ThrowingReexecutionValidator(new OperationCanceledException()));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(bundlePath, reexecute: true));
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Fact]
    public async Task Verify_RecordValidationFailure_LeavesArtifactDatasetParameterNotChecked()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "bundle.zip");
            string resealedPath = await ResealBundleAfterEntryMutationAsync(bundlePath,
                Path.Combine(harness.Root, "resealed-record.zip"), ResearchEvidenceBundleConstants.ExperimentRecordEntry,
                text =>
                {
                    JsonNode node = JsonNode.Parse(text)!;
                    node["experimentId"] = "EXP-UNRELATED-999";
                    return node.ToJsonString();
                });

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(resealedPath);

            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(BundleExperimentRecordInvalid, result.ReasonCodes);
            Assert.Equal("Verified", result.EntryIntegrityStatus);
            Assert.Equal("Failed", result.ExperimentRecordStatus);
            Assert.Equal("NotChecked", result.ArtifactLineageStatus);
            Assert.Equal("NotChecked", result.DatasetStatus);
            Assert.Equal("NotChecked", result.ParameterStatus);
            Assert.DoesNotContain(result.Checks, c => c.CheckCode == DatasetIdentity);
            Assert.DoesNotContain(result.Checks, c => c.CheckCode == ParameterIdentity);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    [Theory]
    [InlineData(ResearchEvidenceBundleReexecutionStatus.NotRequested, true)]
    [InlineData(ResearchEvidenceBundleReexecutionStatus.Equivalent, true)]
    [InlineData(ResearchEvidenceBundleReexecutionStatus.Diverged, false)]
    [InlineData(ResearchEvidenceBundleReexecutionStatus.Unsupported, false)]
    [InlineData(ResearchEvidenceBundleReexecutionStatus.Failed, false)]
    public void VerificationResult_FullVerificationSucceeded_OnlyAcceptsEquivalentReexecution(
        ResearchEvidenceBundleReexecutionStatus reexecutionStatus, bool expected)
    {
        ResearchEvidenceBundleVerificationResult result = new(
            Status.Verified, reexecutionStatus, "EXP-1", ResearchEvidenceBundleConstants.BundleVersionV1, "manifest-fp",
            Array.Empty<ResearchEvidenceBundleCheck>(), Array.Empty<string>());

        Assert.Equal(Status.Verified, result.Status);
        Assert.Equal(expected, result.FullVerificationSucceeded);
    }

    [Theory]
    [InlineData(Status.Failed)]
    [InlineData(Status.Unsupported)]
    public void VerificationResult_FullVerificationSucceeded_RejectsStructuralFailure(Status structuralStatus)
    {
        ResearchEvidenceBundleVerificationResult result = new(
            structuralStatus, ReexecStatus.NotRequested, "EXP-1", ResearchEvidenceBundleConstants.BundleVersionV1, "manifest-fp",
            Array.Empty<ResearchEvidenceBundleCheck>(), Array.Empty<string>());

        Assert.False(result.FullVerificationSucceeded);
    }

    [Fact]
    public void VerificationResult_UnvisitedStagesDefaultToNotChecked()
    {
        ResearchEvidenceBundleVerificationResult result = new(
            Status.Failed, ReexecStatus.NotRequested, null, null, null,
            Array.Empty<ResearchEvidenceBundleCheck>(), Array.Empty<string>());

        Assert.Equal("NotChecked", result.EntryIntegrityStatus);
        Assert.Equal("NotChecked", result.ExperimentRecordStatus);
        Assert.Equal("NotChecked", result.ArtifactLineageStatus);
        Assert.Equal("NotChecked", result.DatasetStatus);
        Assert.Equal("NotChecked", result.ParameterStatus);
    }

    [Fact]
    public async Task Cli_VerificationExitCodeContract_SeparatesStructuralAndReexecutionStatus()
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            string bundlePath = await ExportBundleAsync(harness, harness.Root, "cli-contract.zip");

            // 1. Structural verification only -> exit 0
            (int structuralExit, string structuralOut) = await RunCliAsync("--verify-research-evidence-bundle", bundlePath);
            Assert.Equal(0, structuralExit);
            Assert.Contains("RESEARCH_EVIDENCE_BUNDLE_VERIFICATION_STATUS=Verified", structuralOut);
            Assert.Contains("ENTRY_INTEGRITY_STATUS=Verified", structuralOut);
            Assert.Contains("EXPERIMENT_RECORD_STATUS=Verified", structuralOut);
            Assert.Contains("ARTIFACT_LINEAGE_STATUS=Verified", structuralOut);
            Assert.Contains("DATASET_STATUS=Verified", structuralOut);
            Assert.Contains("PARAMETER_STATUS=Verified", structuralOut);
            Assert.Contains("REEXECUTION_STATUS=NotRequested", structuralOut);
            Assert.Contains("FAILED_CHECK_COUNT=0", structuralOut);

            // 2. Structural verification with equivalent re-execution -> exit 0
            (int equivalentExit, string equivalentOut) = await RunCliAsync(
                "--verify-research-evidence-bundle", bundlePath, "--reexecute", "true");
            Assert.Equal(0, equivalentExit);
            Assert.Contains("RESEARCH_EVIDENCE_BUNDLE_VERIFICATION_STATUS=Verified", equivalentOut);
            Assert.Contains("REEXECUTION_STATUS=Equivalent", equivalentOut);

            // 3. Structural failure -> exit non-zero, unvisited stages are not claimed as Verified
            string brokenPath = await ResealBundleAfterEntryMutationAsync(bundlePath,
                Path.Combine(harness.Root, "cli-broken.zip"), ResearchEvidenceBundleConstants.ExperimentRecordEntry,
                text =>
                {
                    JsonNode node = JsonNode.Parse(text)!;
                    node["experimentId"] = "EXP-UNRELATED-999";
                    return node.ToJsonString();
                });

            (int failingExit, string failingOut) = await RunCliAsync("--verify-research-evidence-bundle", brokenPath);
            Assert.NotEqual(0, failingExit);
            Assert.Contains("RESEARCH_EVIDENCE_BUNDLE_VERIFICATION_STATUS=Failed", failingOut);
            Assert.Contains("EXPERIMENT_RECORD_STATUS=Failed", failingOut);
            Assert.Contains("DATASET_STATUS=NotChecked", failingOut);
            Assert.Contains("PARAMETER_STATUS=NotChecked", failingOut);
            Assert.Contains("REEXECUTION_STATUS=NotRequested", failingOut);
            Assert.Contains(BundleExperimentRecordInvalid, failingOut);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    // ==============================================================================================
    // Helper Methods
    // ==============================================================================================

    private static async Task<string> ExportBundleAsync(Harness harness, string outputRoot, string fileName)
    {
        await CreateRunner().RunAsync(harness.Request);

        string bundlePath = Path.Combine(outputRoot, fileName);
        ResearchEvidenceBundleExportRequest exportRequest = new(
            harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
            harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

        await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);
        return bundlePath;
    }

    /// <summary>
    /// Rewrites one evidence entry and re-seals the bundle (entry digest, byte length and manifest fingerprint)
    /// so the resulting archive stays structurally valid while the mutated evidence is structurally rejected.
    /// </summary>
    private static async Task<string> ResealBundleAfterEntryMutationAsync(
        string sourcePath, string targetPath, string entryName, Func<string, string> mutate)
    {
        byte[] manifestBytes;
        Dictionary<string, byte[]> payloads = new(StringComparer.Ordinal);
        using (ZipArchive source = ZipFile.OpenRead(sourcePath))
        {
            manifestBytes = ReadEntryContent(source.GetEntry(ResearchEvidenceBundleConstants.ManifestEntry)!);
            foreach (string name in ResearchEvidenceBundleConstants.FixedPayloadEntries)
                payloads[name] = ReadEntryContent(source.GetEntry(name)!);
        }

        payloads[entryName] = Encoding.UTF8.GetBytes(mutate(Encoding.UTF8.GetString(payloads[entryName])));

        ResearchEvidenceBundleManifest original = JsonSerializer.Deserialize<ResearchEvidenceBundleManifest>(
            manifestBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        ResearchEvidenceBundleEntry[] resealedEntries = original.Entries
            .Select(entry => string.Equals(entry.LogicalPath, entryName, StringComparison.Ordinal)
                ? new ResearchEvidenceBundleEntry(entry.LogicalPath, payloads[entry.LogicalPath].LongLength,
                    Convert.ToHexString(SHA256.HashData(payloads[entry.LogicalPath])))
                : entry)
            .ToArray();

        ResearchEvidenceBundleManifest resealed = ResearchEvidenceBundleManifest.Create(
            original.BundleVersion, original.ExperimentId, original.ExperimentFingerprint, original.DatasetFingerprint,
            original.ArtifactFingerprint, original.ExecutionBindingFingerprint, original.SourceBuildProvenanceFingerprint,
            original.ExecutionEnvironmentProvenanceFingerprint, resealedEntries);

        byte[] resealedManifestBytes = JsonSerializer.SerializeToUtf8Bytes(resealed,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        using (FileStream destFs = new(targetPath, FileMode.CreateNew))
        using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifestEntry = dest.CreateEntry(ResearchEvidenceBundleConstants.ManifestEntry);
            using (Stream manifestStream = manifestEntry.Open()) await manifestStream.WriteAsync(resealedManifestBytes);

            foreach (string name in ResearchEvidenceBundleConstants.FixedPayloadEntries)
            {
                ZipArchiveEntry payloadEntry = dest.CreateEntry(name);
                using Stream payloadStream = payloadEntry.Open();
                await payloadStream.WriteAsync(payloads[name]);
            }
        }

        return targetPath;
    }

    private static byte[] ReadEntryContent(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task AssertTamperedEntryFailsAsync(string entryName, string tamperedContent, string expectedReason)
    {
        Harness harness = await CreateHarnessAsync("classic");
        try
        {
            await CreateRunner().RunAsync(harness.Request);

            string bundlePath = Path.Combine(harness.Root, "bundle.zip");
            ResearchEvidenceBundleExportRequest exportRequest = new(
                harness.Request.ExperimentId, harness.StorePath, harness.ArtifactPath, harness.DatasetPath,
                harness.ParametersPath, BacktestCost, BacktestSlippage, bundlePath);

            await new ResearchEvidenceBundleExporter().ExportAsync(exportRequest);

            string tamperedPath = Path.Combine(harness.Root, "tampered.zip");
            using (ZipArchive src = ZipFile.OpenRead(bundlePath))
            using (FileStream destFs = new(tamperedPath, FileMode.CreateNew))
            using (ZipArchive dest = new(destFs, ZipArchiveMode.Create))
            {
                foreach (ZipArchiveEntry entry in src.Entries)
                {
                    ZipArchiveEntry newEntry = dest.CreateEntry(entry.FullName);
                    using Stream inS = entry.Open();
                    using Stream outS = newEntry.Open();
                    if (entry.FullName == entryName)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(tamperedContent);
                        await outS.WriteAsync(bytes);
                    }
                    else
                    {
                        await inS.CopyToAsync(outS);
                    }
                }
            }

            ResearchEvidenceBundleVerificationResult result = await new ResearchEvidenceBundleVerifier().VerifyAsync(tamperedPath);
            Assert.Equal(Status.Failed, result.Status);
            Assert.Contains(expectedReason, result.ReasonCodes);

            // Entry integrity is the stage that failed; no later stage was ever evaluated.
            Assert.Equal("Failed", result.EntryIntegrityStatus);
            Assert.Equal("NotChecked", result.ExperimentRecordStatus);
            Assert.Equal("NotChecked", result.ArtifactLineageStatus);
            Assert.Equal("NotChecked", result.DatasetStatus);
            Assert.Equal("NotChecked", result.ParameterStatus);
            Assert.DoesNotContain(result.Checks, c => c.CheckCode == ExperimentRecord);
            Assert.DoesNotContain(result.Checks, c => c.CheckCode == DatasetIdentity);
            Assert.DoesNotContain(result.Checks, c => c.CheckCode == ParameterIdentity);
        }
        finally { DeleteDirectory(harness.Root); }
    }

    private sealed record Harness(string Root, string DatasetPath, string ParametersPath, string StorePath, string ArtifactPath, ManagedResearchExperimentRequest Request);

    private static async Task<Harness> CreateHarnessAsync(string mode, string experimentId = "EXP-BUNDLE-001")
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

    private static HistoricalDatasetFile CreateDatasetFile(string id)
    {
        DateOnly start = new(2026, 1, 1);
        DateOnly[] dates = Enumerable.Range(0, 90).Select(index => start.AddDays(index)).ToArray();
        return new()
        {
            SchemaVersion = HistoricalDatasetJsonLoader.LegacySchemaVersion,
            DatasetId = id,
            Source = "Test",
            PriceAdjustmentMode = "ForwardAdjusted",
            TradingDates = dates.ToList(),
            Capabilities = HistoricalDataCapabilities.Complete,
            Quotes = new(),
            Klines = new[] { "600000", "600001" }.Select(symbol => new HistoricalKlineSeriesFile
            {
                Symbol = symbol,
                Bars = dates.Select((date, index) => new HistoricalKlineBarFile
                {
                    Date = date.ToDateTime(TimeOnly.MinValue),
                    Open = 9.8 + index * 0.02,
                    High = 10.2 + index * 0.02,
                    Low = 9.7 + index * 0.02,
                    Close = 10.0 + index * 0.02,
                    Volume = 1000,
                    Amount = 10000
                }).ToList()
            }).ToList()
        };
    }

    private static async Task<(int ExitCode, string Stdout)> RunCliAsync(params string[] args)
    {
        using Process process = Process.Start(CreateCliStartInfo(args)) ?? throw new InvalidOperationException("Could not start CLI.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout + "\n" + stderr);
    }

    private static ProcessStartInfo CreateCliStartInfo(params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SolutionRoot() };
        psi.Environment.Remove("HISTORICAL_GATEWAY_URL");
        foreach (var a in new[] { "run", "--project", Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"),
            "--configuration", TestExecutionConfiguration.Current(), "--no-build", "--no-restore", "--" }.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string SolutionRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-EvidenceBundleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); }
            catch { /* Best effort test cleanup */ }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubSourceBuildProvider(ResearchSourceBuildProvenance provenance) : IResearchSourceBuildProvenanceProvider
    {
        public ResearchSourceBuildProvenance Capture() => provenance;
    }

    private sealed class StubEnvironmentProvider(ResearchExecutionEnvironmentProvenance provenance) : IResearchExecutionEnvironmentProvenanceProvider
    {
        public ResearchExecutionEnvironmentProvenance Capture() => provenance;
    }

    private sealed class StubReexecutionValidator(ResearchReexecutionValidationResult result) : ISparrowResearchReexecutionValidator
    {
        public Task<ResearchReexecutionValidationResult> ValidateReexecutionAsync(
            PersistedResearchExperimentRecord experimentRecord,
            string originalArtifactPath,
            string datasetPath,
            string? parametersPath,
            double backtestRoundTripCostRate,
            double backtestSlippageRate,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class ThrowingReexecutionValidator(Exception exception) : ISparrowResearchReexecutionValidator
    {
        public Task<ResearchReexecutionValidationResult> ValidateReexecutionAsync(
            PersistedResearchExperimentRecord experimentRecord,
            string originalArtifactPath,
            string datasetPath,
            string? parametersPath,
            double backtestRoundTripCostRate,
            double backtestSlippageRate,
            CancellationToken cancellationToken = default) => Task.FromException<ResearchReexecutionValidationResult>(exception);
    }
}
