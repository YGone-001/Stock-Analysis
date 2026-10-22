using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using static AIHelper.Core.Sparrow.ResearchEvidenceBatchAuditReasonCodes;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleCheckCodes;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleReasonCodes;
using BatchStatus = AIHelper.Core.Sparrow.ResearchEvidenceBatchAuditStatus;
using BatchMode = AIHelper.Core.Sparrow.ResearchEvidenceBatchAuditMode;
using ReexecStatus = AIHelper.Core.Sparrow.ResearchEvidenceBundleReexecutionStatus;
using Status = AIHelper.Core.Sparrow.ResearchEvidenceBundleVerificationStatus;

namespace AIHelper.Services.Tests;

public sealed class ResearchEvidenceBatchAuditTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Tree = "89abcdef0123456789abcdef0123456789abcdef";
    private const string ToolMvid = "11111111-1111-1111-1111-111111111111";
    private const string ServicesMvid = "22222222-2222-2222-2222-222222222222";
    private const string CoreMvid = "33333333-3333-3333-3333-333333333333";

    // ==============================================================================================
    // 1. Deterministic discovery
    // ==============================================================================================

    [Fact]
    public void Discovery_OrdersBundleNamesOrdinally_IndependentOfEnumerationOrder()
    {
        string[] names = { "b.zip", "A.zip", "c.ZIP", "notes.txt", "a2.zip", "archive.tar" };

        IReadOnlyList<string> ordered = ResearchEvidenceBatchAuditor.DiscoverBundleFileNames(names);

        Assert.Equal(new[] { "A.zip", "a2.zip", "b.zip", "c.ZIP" }, ordered);
        Assert.Equal(ordered, ResearchEvidenceBatchAuditor.DiscoverBundleFileNames(names.Reverse()));
        Assert.Equal(ordered, ResearchEvidenceBatchAuditor.DiscoverBundleFileNames(ordered));
    }

    [Fact]
    public void Discovery_RejectsAmbiguousCaseCollidingBundleNames()
    {
        ResearchEvidenceBatchAuditException exception = Assert.Throws<ResearchEvidenceBatchAuditException>(
            () => ResearchEvidenceBatchAuditor.DiscoverBundleFileNames(new[] { "EXP-001.zip", "exp-001.zip" }));

        Assert.Equal(DirectoryInvalid, exception.ReasonCode);
        Assert.Contains("EXP-001.zip", exception.Message);
    }

    [Fact]
    public async Task Audit_IgnoresNonZipFilesAndSubdirectories()
    {
        string directory = TemporaryDirectory();
        string nested = Path.Combine(directory, "nested");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "EXP-001.zip"), Array.Empty<byte>());
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "not a bundle");
            File.WriteAllBytes(Path.Combine(nested, "EXP-002.zip"), Array.Empty<byte>());

            ResearchEvidenceBatchAuditReport report = await CreateStubAuditor().AuditAsync(directory);

            Assert.Equal(1, report.Summary.TotalBundles);
            Assert.Equal("EXP-001.zip", Assert.Single(report.Bundles).BundleFileName);
        }
        finally { DeleteDirectory(directory); }
    }

    // ==============================================================================================
    // 2. Batch status and summary counts
    // ==============================================================================================

    [Fact]
    public async Task Audit_ThreeValidBundles_ReportsVerifiedBatchWithExactCounts()
    {
        string directory = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip", "EXP-003.zip");
        try
        {
            ResearchEvidenceBatchAuditReport report = await CreateStubAuditor().AuditAsync(directory);

            Assert.Equal(BatchStatus.Verified, report.BatchStatus);
            Assert.Equal(BatchMode.StructuralOnly, report.AuditMode);
            Assert.Equal(ResearchEvidenceBatchAuditConstants.ReportVersionV1, report.ReportVersion);
            Assert.Equal(3, report.Summary.TotalBundles);
            Assert.Equal(3, report.Summary.StructurallyVerified);
            Assert.Equal(0, report.Summary.StructurallyFailed);
            Assert.Equal(0, report.Summary.StructurallyUnsupported);
            Assert.Equal(3, report.Summary.ReexecutionNotRequested);
            Assert.Equal(3, report.Summary.FullySuccessful);
            Assert.Equal(0, report.Summary.NotFullySuccessful);
            Assert.Equal(report.Summary.TotalBundles,
                report.Summary.StructurallyVerified + report.Summary.StructurallyFailed + report.Summary.StructurallyUnsupported);
            Assert.Equal(new[] { "EXP-001.zip", "EXP-002.zip", "EXP-003.zip" }, report.Bundles.Select(b => b.BundleFileName));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_OneInvalidBundleAmongValidOnes_ContinuesAndReportsEachOutcome()
    {
        string directory = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip", "EXP-003.zip");
        try
        {
            StubBundleVerifier verifier = new((name, _) => string.Equals(name, "EXP-002.zip", StringComparison.Ordinal)
                ? VerifierResult(Status.Failed, experimentId: "EXP-002")
                : VerifierResult(experimentId: Path.GetFileNameWithoutExtension(name)));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory);

            Assert.Equal(BatchStatus.Failed, report.BatchStatus);
            Assert.Equal(3, report.Summary.TotalBundles);
            Assert.Equal(2, report.Summary.StructurallyVerified);
            Assert.Equal(1, report.Summary.StructurallyFailed);
            Assert.Equal(2, report.Summary.FullySuccessful);
            Assert.Equal(1, report.Summary.NotFullySuccessful);
            Assert.False(report.BatchSucceeded);

            // A failure never stops the remaining bundles.
            Assert.Equal(new[] { "EXP-001.zip", "EXP-002.zip", "EXP-003.zip" }, verifier.Calls);
            Assert.Equal(Status.Verified, report.Bundles[2].StructuralStatus);
            Assert.Contains(BundleEntryIntegrityFailed, report.Bundles[1].ReasonCodes);
        }
        finally { DeleteDirectory(directory); }
    }

    // ==============================================================================================
    // 3. Structural and re-execution domains stay separate
    // ==============================================================================================

    [Fact]
    public async Task Audit_StructuralOnlyMode_DoesNotRequestReexecution()
    {
        string directory = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip");
        try
        {
            StubBundleVerifier verifier = new((name, _) => VerifierResult(experimentId: Path.GetFileNameWithoutExtension(name)));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory);

            Assert.All(verifier.ReexecuteFlags, flag => Assert.False(flag));
            Assert.Equal(BatchMode.StructuralOnly, report.AuditMode);
            Assert.All(report.Bundles, b => Assert.Equal(ReexecStatus.NotRequested, b.ReexecutionStatus));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_ReexecutionMode_PassesReexecuteToExistingVerifier()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        try
        {
            StubBundleVerifier verifier = new((name, _) => VerifierResult(reexec: ReexecStatus.Equivalent));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory, reexecute: true);

            Assert.All(verifier.ReexecuteFlags, flag => Assert.True(flag));
            Assert.Equal(BatchMode.StructuralAndReexecution, report.AuditMode);
            Assert.Equal(BatchStatus.Verified, report.BatchStatus);
            Assert.Equal(1, report.Summary.ReexecutionEquivalent);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_EquivalentAndDiverged_RemainDistinctAndDivergedIsNotFullySuccessful()
    {
        string directory = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip");
        try
        {
            StubBundleVerifier verifier = new((name, _) => string.Equals(name, "EXP-002.zip", StringComparison.Ordinal)
                ? VerifierResult(reexec: ReexecStatus.Diverged, experimentId: "EXP-002")
                : VerifierResult(reexec: ReexecStatus.Equivalent, experimentId: "EXP-001"));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory, reexecute: true);

            Assert.Equal(BatchStatus.Failed, report.BatchStatus);
            Assert.Equal(2, report.Summary.StructurallyVerified);
            Assert.Equal(1, report.Summary.ReexecutionEquivalent);
            Assert.Equal(1, report.Summary.ReexecutionDiverged);
            Assert.Equal(1, report.Summary.FullySuccessful);
            Assert.Equal(1, report.Summary.NotFullySuccessful);

            // Structural validity is preserved verbatim for the diverged bundle.
            ResearchEvidenceBatchAuditBundleResult diverged = report.Bundles.Single(b => b.BundleFileName == "EXP-002.zip");
            Assert.Equal(Status.Verified, diverged.StructuralStatus);
            Assert.Equal(ReexecStatus.Diverged, diverged.ReexecutionStatus);
            Assert.False(diverged.FullVerificationSucceeded);
            Assert.Contains(BundleReexecutionDiverged, diverged.ReasonCodes);
            Assert.True(diverged.FailedCheckCount > 0);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_UnsupportedReexecution_CountsStructurallyVerifiedButNotFullySuccessful()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        try
        {
            StubBundleVerifier verifier = new((_, _) => VerifierResult(reexec: ReexecStatus.Unsupported));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory, reexecute: true);

            Assert.Equal(BatchStatus.Failed, report.BatchStatus);
            Assert.Equal(1, report.Summary.StructurallyVerified);
            Assert.Equal(1, report.Summary.ReexecutionUnsupported);
            Assert.Equal(0, report.Summary.FullySuccessful);
            Assert.Contains(BundleReexecutionUnsupported, report.Bundles[0].ReasonCodes);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_FailedReexecution_CountsStructurallyVerifiedButNotFullySuccessful()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        try
        {
            StubBundleVerifier verifier = new((_, _) => VerifierResult(reexec: ReexecStatus.Failed));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory, reexecute: true);

            Assert.Equal(BatchStatus.Failed, report.BatchStatus);
            Assert.Equal(1, report.Summary.StructurallyVerified);
            Assert.Equal(1, report.Summary.ReexecutionFailed);
            Assert.Equal(0, report.Summary.FullySuccessful);
            Assert.Equal(Status.Verified, report.Bundles[0].StructuralStatus);
        }
        finally { DeleteDirectory(directory); }
    }

    // ==============================================================================================
    // 4. Per-bundle error isolation
    // ==============================================================================================

    [Fact]
    public async Task Audit_VerifierException_IsClassifiedWithoutFabricatingStructuralSuccess()
    {
        string directory = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip");
        try
        {
            StubBundleVerifier verifier = new((name, _) => string.Equals(name, "EXP-001.zip", StringComparison.Ordinal)
                ? throw new InvalidOperationException("simulated verifier crash at C:\\secret\\EXP-001.zip")
                : VerifierResult(experimentId: "EXP-002"));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory);

            Assert.Equal(BatchStatus.Failed, report.BatchStatus);
            Assert.Equal(1, report.Summary.StructurallyFailed);
            Assert.Equal(1, report.Summary.StructurallyVerified);

            ResearchEvidenceBatchAuditBundleResult broken = report.Bundles[0];
            Assert.Equal(Status.Failed, broken.StructuralStatus);
            Assert.False(broken.FullVerificationSucceeded);
            Assert.Equal("NotChecked", broken.EntryIntegrityStatus);
            Assert.Contains(BundleVerificationException, broken.ReasonCodes);
            Assert.DoesNotContain(broken.Checks, check => check.Message.Contains("C:\\secret", StringComparison.Ordinal));
            Assert.Equal(Status.Verified, report.Bundles[1].StructuralStatus);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_FileSystemIoException_IsClassifiedAsBatchIoFailure()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        try
        {
            StubBundleVerifier verifier = new((_, _) => throw new IOException("The process cannot access the file."));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory);

            Assert.Contains(BundleIoFailed, report.Bundles[0].ReasonCodes);
            Assert.Equal(Status.Failed, report.Bundles[0].StructuralStatus);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_Cancellation_Propagates()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        try
        {
            StubBundleVerifier verifier = new((_, _) => throw new OperationCanceledException());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory));
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_EmptyDirectory_ReportsEmptyBatchThatIsNotASuccess()
    {
        string directory = TemporaryDirectory();
        try
        {
            ResearchEvidenceBatchAuditReport report = await CreateStubAuditor().AuditAsync(directory);

            Assert.Equal(BatchStatus.Empty, report.BatchStatus);
            Assert.False(report.BatchSucceeded);
            Assert.Equal(0, report.Summary.TotalBundles);
            Assert.Empty(report.Bundles);
            Assert.Contains("No evidence bundles were discovered",
                new ResearchEvidenceBatchReportExporter().RenderMarkdown(report), StringComparison.Ordinal);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_MissingDirectory_IsRejectedWithStableReasonCode()
    {
        string missing = Path.Combine(Path.GetTempPath(), "AIHelper-BatchAudit-missing-" + Guid.NewGuid().ToString("N"));

        ResearchEvidenceBatchAuditException exception = await Assert.ThrowsAsync<ResearchEvidenceBatchAuditException>(
            () => CreateStubAuditor().AuditAsync(missing));

        Assert.Equal(DirectoryInvalid, exception.ReasonCode);
    }

    [Fact]
    public async Task Audit_BundleCountAboveLimit_IsRejectedWithoutVerifyingAnything()
    {
        string directory = TemporaryDirectory();
        try
        {
            for (int index = 0; index <= ResearchEvidenceBatchAuditConstants.MaxBundlesPerInvocation; index++)
                File.WriteAllBytes(Path.Combine(directory, $"EXP-{index:D4}.zip"), Array.Empty<byte>());

            StubBundleVerifier verifier = new((_, _) => VerifierResult());

            ResearchEvidenceBatchAuditException exception = await Assert.ThrowsAsync<ResearchEvidenceBatchAuditException>(
                () => new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory));

            Assert.Equal(BundleLimitExceeded, exception.ReasonCode);
            Assert.Empty(verifier.Calls);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_ReparsePointBundle_IsRejectedWithoutFollowingIt()
    {
        string root = TemporaryDirectory();
        try
        {
            string target = Path.Combine(root, "outside-bundle.zip");
            File.WriteAllBytes(target, Array.Empty<byte>());
            string link = Path.Combine(root, "linked.zip");
            try
            {
                File.CreateSymbolicLink(link, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // Symbolic link creation is not permitted on this host.
            }

            // Some hosts silently refuse to materialize links; the policy remains covered where links exist.
            if (!File.Exists(link)) return;

            ResearchEvidenceBatchAuditReport report = await CreateStubAuditor().AuditAsync(root);

            ResearchEvidenceBatchAuditBundleResult linked = report.Bundles.Single(b => b.BundleFileName == "linked.zip");
            Assert.Equal(Status.Failed, linked.StructuralStatus);
            Assert.Contains(BundleIoFailed, linked.ReasonCodes);
        }
        finally { DeleteDirectory(root); }
    }

    // ==============================================================================================
    // 5. Report rendering and atomic publication
    // ==============================================================================================

    [Fact]
    public async Task Exporter_JsonIsDeterministicAndExcludesMachinePaths()
    {
        string first = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip");
        string second = TemporaryDirectory();
        try
        {
            foreach (string name in new[] { "EXP-001.zip", "EXP-002.zip" })
                File.WriteAllBytes(Path.Combine(second, name), Array.Empty<byte>());

            ResearchEvidenceBatchReportExporter exporter = new();
            string firstJson = exporter.SerializeJson(await CreateStubAuditor().AuditAsync(first));
            string secondJson = exporter.SerializeJson(await CreateStubAuditor().AuditAsync(second));

            Assert.Equal(firstJson, secondJson);
            Assert.Equal(firstJson, exporter.SerializeJson(await CreateStubAuditor().AuditAsync(first)));

            // Relocating the input directory must not change the report's factual contents.
            Assert.DoesNotContain(first, firstJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(second, firstJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.UserName, firstJson, StringComparison.OrdinalIgnoreCase);

            using JsonDocument document = JsonDocument.Parse(firstJson);
            Assert.Equal(ResearchEvidenceBatchAuditConstants.ReportVersionV1,
                document.RootElement.GetProperty("reportVersion").GetString());
            Assert.Equal("StructuralOnly", document.RootElement.GetProperty("auditMode").GetString());
            Assert.Equal("Verified", document.RootElement.GetProperty("batchStatus").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("summary").GetProperty("totalBundles").GetInt32());
            JsonElement bundle = document.RootElement.GetProperty("bundles")[0];
            Assert.Equal("EXP-001.zip", bundle.GetProperty("bundleFileName").GetString());
            Assert.Equal("Verified", bundle.GetProperty("structuralStatus").GetString());
            Assert.Equal("NotRequested", bundle.GetProperty("reexecutionStatus").GetString());
            Assert.True(bundle.GetProperty("fullVerificationSucceeded").GetBoolean());
        }
        finally
        {
            DeleteDirectory(first);
            DeleteDirectory(second);
        }
    }

    [Fact]
    public async Task Exporter_MarkdownIsDeterministicAndReadable()
    {
        string directory = CreateBundleDirectory("EXP-001.zip", "EXP-002.zip");
        try
        {
            StubBundleVerifier verifier = new((name, _) => string.Equals(name, "EXP-002.zip", StringComparison.Ordinal)
                ? VerifierResult(Status.Failed, experimentId: "EXP-002")
                : VerifierResult(experimentId: "EXP-001"));

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor(verifier).AuditAsync(directory);
            ResearchEvidenceBatchReportExporter exporter = new();
            string markdown = exporter.RenderMarkdown(report);

            Assert.Equal(markdown, exporter.RenderMarkdown(report));
            Assert.Contains("# Research Evidence Batch Audit", markdown, StringComparison.Ordinal);
            Assert.Contains("| Audit mode | `StructuralOnly` |", markdown, StringComparison.Ordinal);
            Assert.Contains("| Total bundles | 2 |", markdown, StringComparison.Ordinal);
            Assert.Contains("| EXP-001.zip |", markdown, StringComparison.Ordinal);
            Assert.Contains("## Failure details", markdown, StringComparison.Ordinal);
            Assert.Contains(BundleEntryIntegrityFailed, markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, markdown, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Exporter_RefusesToOverwriteExistingReportAndLeavesNoTemporaryFiles()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        string outputDirectory = TemporaryDirectory();
        try
        {
            ResearchEvidenceBatchAuditReport report = await CreateStubAuditor().AuditAsync(directory);
            string reportPath = Path.Combine(outputDirectory, "audit.json");
            await File.WriteAllTextAsync(reportPath, "existing-report-content");

            ResearchEvidenceBatchReportExporter exporter = new();
            ResearchEvidenceBatchAuditException exception = await Assert.ThrowsAsync<ResearchEvidenceBatchAuditException>(
                () => exporter.WriteAsync(report, ResearchEvidenceBatchAuditReportFormat.Json, reportPath));

            Assert.Equal(OutputExists, exception.ReasonCode);
            Assert.Equal("existing-report-content", await File.ReadAllTextAsync(reportPath));
            Assert.Empty(Directory.GetFiles(outputDirectory, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task Exporter_PublishesAtomicallyAndCleansTemporaryFiles()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        string outputDirectory = TemporaryDirectory();
        try
        {
            ResearchEvidenceBatchAuditReport report = await CreateStubAuditor().AuditAsync(directory);
            string reportPath = Path.Combine(outputDirectory, "reports", "audit.md");

            string published = await new ResearchEvidenceBatchReportExporter()
                .WriteAsync(report, ResearchEvidenceBatchAuditReportFormat.Markdown, reportPath);

            Assert.Equal(Path.GetFullPath(reportPath), published);
            Assert.True(File.Exists(reportPath));
            Assert.Contains("# Research Evidence Batch Audit", await File.ReadAllTextAsync(reportPath), StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(reportPath)!, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(outputDirectory);
        }
    }

    // ==============================================================================================
    // 6. Real Classic / V2 end-to-end
    // ==============================================================================================

    [Fact]
    public async Task Audit_RealClassicAndV2BundlesInOneDirectory_ReportsVerifiedBatch()
    {
        string directory = TemporaryDirectory();
        try
        {
            await CreateRealBundleAsync(directory, "EXP-BATCH-CLASSIC", "classic");
            await CreateRealBundleAsync(directory, "EXP-BATCH-V2", "v2");

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor().AuditAsync(directory);

            Assert.Equal(BatchStatus.Verified, report.BatchStatus);
            Assert.Equal(2, report.Summary.TotalBundles);
            Assert.Equal(2, report.Summary.StructurallyVerified);
            Assert.Equal(2, report.Summary.FullySuccessful);
            Assert.All(report.Bundles, bundle =>
            {
                Assert.Equal(Status.Verified, bundle.StructuralStatus);
                Assert.Equal(ReexecStatus.NotRequested, bundle.ReexecutionStatus);
                Assert.Equal(ResearchEvidenceBundleConstants.BundleVersionV1, bundle.BundleVersion);
                Assert.False(string.IsNullOrWhiteSpace(bundle.BundleManifestFingerprint));
            });

            ResearchEvidenceBatchReportExporter exporter = new();
            string markdown = exporter.RenderMarkdown(report);
            Assert.Contains("EXP-BATCH-CLASSIC", markdown, StringComparison.Ordinal);
            Assert.Contains("EXP-BATCH-V2", markdown, StringComparison.Ordinal);
            Assert.DoesNotContain(directory, markdown, StringComparison.OrdinalIgnoreCase);
        }
        finally { DeleteDirectory(directory); }
    }

    [Fact]
    public async Task Audit_RealClassicAndV2BundlesWithFullReexecution_ReportsEquivalent()
    {
        string directory = TemporaryDirectory();
        try
        {
            await CreateRealBundleAsync(directory, "EXP-BATCH-REEXEC-CLASSIC", "classic");
            await CreateRealBundleAsync(directory, "EXP-BATCH-REEXEC-V2", "v2");

            ResearchEvidenceBatchAuditReport report = await new ResearchEvidenceBatchAuditor().AuditAsync(directory, reexecute: true);

            Assert.Equal(BatchMode.StructuralAndReexecution, report.AuditMode);
            Assert.Equal(BatchStatus.Verified, report.BatchStatus);
            Assert.Equal(2, report.Summary.ReexecutionEquivalent);
            Assert.Equal(2, report.Summary.FullySuccessful);
            Assert.All(report.Bundles, bundle => Assert.Equal(ReexecStatus.Equivalent, bundle.ReexecutionStatus));
        }
        finally { DeleteDirectory(directory); }
    }

    // ==============================================================================================
    // 7. CLI behaviour
    // ==============================================================================================

    [Fact]
    public async Task Cli_BatchAudit_WritesMarkdownThenJsonAndExitsZero()
    {
        string directory = TemporaryDirectory();
        string outputDirectory = TemporaryDirectory();
        try
        {
            await CreateRealBundleAsync(directory, "EXP-CLI-CLASSIC", "classic");
            await CreateRealBundleAsync(directory, "EXP-CLI-V2", "v2");

            string markdownPath = Path.Combine(outputDirectory, "audit.md");
            (int markdownExit, string markdownOutput) = await RunCliAsync(
                "--audit-research-evidence-directory", directory,
                "--format", "markdown",
                "--output", markdownPath);

            Assert.Equal(0, markdownExit);
            Assert.Contains("RESEARCH_EVIDENCE_BATCH_AUDIT_STATUS=Verified", markdownOutput);
            Assert.Contains("AUDIT_MODE=StructuralOnly", markdownOutput);
            Assert.Contains("TOTAL_BUNDLES=2", markdownOutput);
            Assert.Contains("STRUCTURALLY_VERIFIED=2", markdownOutput);
            Assert.Contains("FULLY_SUCCESSFUL=2", markdownOutput);
            Assert.Contains("REPORT_FORMAT=markdown", markdownOutput);
            Assert.True(File.Exists(markdownPath));

            string jsonPath = Path.Combine(outputDirectory, "audit.json");
            (int jsonExit, string jsonOutput) = await RunCliAsync(
                "--audit-research-evidence-directory", directory,
                "--format", "json",
                "--output", jsonPath);

            Assert.Equal(0, jsonExit);
            Assert.Contains("REPORT_FORMAT=json", jsonOutput);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath));
            Assert.Equal("Verified", document.RootElement.GetProperty("batchStatus").GetString());
            Assert.Equal(2, document.RootElement.GetProperty("summary").GetProperty("totalBundles").GetInt32());
            Assert.Equal(2, document.RootElement.GetProperty("bundles").GetArrayLength());
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task Cli_BatchAuditWithInvalidBundle_ExitsNonZeroButStillWritesReport()
    {
        string directory = TemporaryDirectory();
        string outputDirectory = TemporaryDirectory();
        try
        {
            await CreateRealBundleAsync(directory, "EXP-CLI-VALID", "classic");
            await File.WriteAllTextAsync(Path.Combine(directory, "EXP-CLI-BROKEN.zip"), "not a zip archive");

            string reportPath = Path.Combine(outputDirectory, "audit.json");
            (int exitCode, string output) = await RunCliAsync(
                "--audit-research-evidence-directory", directory,
                "--format", "json",
                "--output", reportPath);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("RESEARCH_EVIDENCE_BATCH_AUDIT_STATUS=Failed", output);
            Assert.Contains("TOTAL_BUNDLES=2", output);
            Assert.Contains("STRUCTURALLY_VERIFIED=1", output);
            Assert.Contains("STRUCTURALLY_FAILED=1", output);
            Assert.Contains("NOT_FULLY_SUCCESSFUL_BUNDLE=EXP-CLI-BROKEN.zip", output);
            Assert.True(File.Exists(reportPath));
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task Cli_BatchAuditOnEmptyDirectory_ExitsNonZero()
    {
        string directory = TemporaryDirectory();
        string outputDirectory = TemporaryDirectory();
        try
        {
            string reportPath = Path.Combine(outputDirectory, "audit.json");
            (int exitCode, string output) = await RunCliAsync(
                "--audit-research-evidence-directory", directory,
                "--format", "json",
                "--output", reportPath);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("RESEARCH_EVIDENCE_BATCH_AUDIT_STATUS=Empty", output);
            Assert.Contains("TOTAL_BUNDLES=0", output);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.Equal("Empty", document.RootElement.GetProperty("batchStatus").GetString());
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task Cli_BatchAuditMissingDirectory_ReportsStableReasonCode()
    {
        string outputDirectory = TemporaryDirectory();
        try
        {
            string missing = Path.Combine(outputDirectory, "absent");
            (int exitCode, string output) = await RunCliAsync(
                "--audit-research-evidence-directory", missing,
                "--format", "json",
                "--output", Path.Combine(outputDirectory, "audit.json"));

            Assert.NotEqual(0, exitCode);
            Assert.Contains($"RESEARCH_EVIDENCE_BATCH_AUDIT_FAILED={DirectoryInvalid}", output);
        }
        finally { DeleteDirectory(outputDirectory); }
    }

    [Fact]
    public async Task Cli_BatchAuditDoesNotOverwriteExistingReport()
    {
        string directory = CreateBundleDirectory("EXP-001.zip");
        string outputDirectory = TemporaryDirectory();
        try
        {
            string reportPath = Path.Combine(outputDirectory, "audit.json");
            await File.WriteAllTextAsync(reportPath, "existing-report-content");

            (int exitCode, string output) = await RunCliAsync(
                "--audit-research-evidence-directory", directory,
                "--format", "json",
                "--output", reportPath);

            Assert.NotEqual(0, exitCode);
            Assert.Contains($"RESEARCH_EVIDENCE_BATCH_AUDIT_FAILED={OutputExists}", output);
            Assert.Equal("existing-report-content", await File.ReadAllTextAsync(reportPath));
        }
        finally
        {
            DeleteDirectory(directory);
            DeleteDirectory(outputDirectory);
        }
    }

    [Fact]
    public async Task Cli_BatchAudit_ConflictsWithOtherOperationModes()
    {
        (int exitCode, string output) = await RunCliAsync(
            "--audit-research-evidence-directory", "D:\\bundles",
            "--verify-research-evidence-bundle", "D:\\bundle.zip");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("CONFLICTING_OPERATION_MODES", output);
    }

    // ==============================================================================================
    // Helpers
    // ==============================================================================================

    private static ResearchEvidenceBatchAuditor CreateStubAuditor() =>
        new(new StubBundleVerifier((name, _) => VerifierResult(experimentId: Path.GetFileNameWithoutExtension(name))));

    private static ResearchEvidenceBundleVerificationResult VerifierResult(
        Status structural = Status.Verified,
        ReexecStatus reexec = ReexecStatus.NotRequested,
        string experimentId = "EXP-STUB")
    {
        List<ResearchEvidenceBundleCheck> checks = new();
        List<string> reasons = new();

        if (structural == Status.Verified)
        {
            checks.Add(new(EntryIntegrity, "Pass", "Verified", "Verified", "Entry integrity verified."));
            switch (reexec)
            {
                case ReexecStatus.Equivalent:
                    checks.Add(new(Reexecution, "Pass", "Equivalent", "Equivalent", "Equivalent."));
                    break;
                case ReexecStatus.Diverged:
                    checks.Add(new(Reexecution, "Fail", "Equivalent", "Diverged", "Diverged."));
                    reasons.Add(BundleReexecutionDiverged);
                    break;
                case ReexecStatus.Unsupported:
                    checks.Add(new(Reexecution, "Unsupported", "Equivalent", "Unsupported", "Unsupported."));
                    reasons.Add(BundleReexecutionUnsupported);
                    break;
                case ReexecStatus.Failed:
                    checks.Add(new(Reexecution, "Fail", "Equivalent", "Failed", "Failed."));
                    reasons.Add(BundleReexecutionFailed);
                    break;
            }
        }
        else
        {
            checks.Add(new(EntryIntegrity, "Fail", "Valid", "Invalid", "Entry integrity failed."));
            reasons.Add(BundleEntryIntegrityFailed);
        }

        string verifiedOrNot = structural == Status.Verified ? "Verified" : "NotChecked";
        return new ResearchEvidenceBundleVerificationResult(
            structural, reexec, experimentId, ResearchEvidenceBundleConstants.BundleVersionV1, "FP-" + experimentId,
            checks, reasons,
            structural == Status.Verified ? "Verified" : "Failed", verifiedOrNot, verifiedOrNot, verifiedOrNot, verifiedOrNot);
    }

    private sealed class StubBundleVerifier(Func<string, bool, ResearchEvidenceBundleVerificationResult> factory) : IResearchEvidenceBundleVerifier
    {
        public List<string> Calls { get; } = new();
        public List<bool> ReexecuteFlags { get; } = new();

        public Task<ResearchEvidenceBundleVerificationResult> VerifyAsync(string bundleZipPath, bool reexecute = false, CancellationToken cancellationToken = default)
        {
            string fileName = Path.GetFileName(bundleZipPath);
            Calls.Add(fileName);
            ReexecuteFlags.Add(reexecute);
            return Task.FromResult(factory(fileName, reexecute));
        }
    }

    private static string CreateBundleDirectory(params string[] fileNames)
    {
        string directory = TemporaryDirectory();
        foreach (string name in fileNames) File.WriteAllBytes(Path.Combine(directory, name), Array.Empty<byte>());
        return directory;
    }

    private static async Task<string> CreateRealBundleAsync(string bundleDirectory, string experimentId, string mode)
    {
        string root = TemporaryDirectory();
        try
        {
            string datasetPath = Path.Combine(root, "dataset.json");
            string parametersPath = Path.Combine(root, "parameters.json");
            string storePath = Path.Combine(root, "experiments");
            string artifactPath = Path.Combine(root, "artifact.json");

            await File.WriteAllTextAsync(datasetPath, JsonSerializer.Serialize(CreateDatasetFile("batch-" + experimentId)));
            await File.WriteAllTextAsync(parametersPath, mode == "v2"
                ? JsonSerializer.Serialize(new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15, 0.5, 3.0, 0.02, true))
                : JsonSerializer.Serialize(new SparrowClassicParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15)));

            ManagedResearchExperimentRequest request = new(experimentId, datasetPath, parametersPath,
                mode == "v2" ? SparrowStrategyMode.V2 : SparrowStrategyMode.Classic,
                new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 10), 2, 1, 0.001, 0.002,
                1_000_000m, PortfolioPositionSizingMethod.EqualWeight, 0.0003m, 0.0004m, PortfolioExecutionModel.CloseBased,
                artifactPath, storePath);

            await CreateRunner().RunAsync(request);

            Directory.CreateDirectory(bundleDirectory);
            string bundlePath = Path.Combine(bundleDirectory, experimentId + ".zip");
            await new ResearchEvidenceBundleExporter().ExportAsync(new ResearchEvidenceBundleExportRequest(
                experimentId, storePath, artifactPath, datasetPath, parametersPath, 0.001, 0.002, bundlePath));
            return bundlePath;
        }
        finally { DeleteDirectory(root); }
    }

    private static ManagedResearchExperimentRunner CreateRunner() => new(
        new StubSourceBuildProvider(new ResearchSourceBuildProvenance(
            ResearchSourceBuildProvenance.CurrentProvenanceVersion, Commit, Tree, ResearchSourceBuildProvenance.CleanSourceState,
            "Release", ".NETCoreApp,Version=v10.0", ToolMvid, ServicesMvid, CoreMvid)),
        artifactWriter: null,
        timeProvider: new FixedTimeProvider(DateTimeOffset.UnixEpoch),
        repositoryFactory: null,
        executionEnvironmentProvenanceProvider: new StubEnvironmentProvider(ResearchExecutionEnvironmentProvenance.Create(
            ResearchExecutionEnvironmentProvenance.CurrentProvenanceVersion, ".NET 10.0.12", "10.0.12", "win-x64", "Windows", "X64", "X64",
            ".NETCoreApp,Version=v10.0",
            new[]
            {
                new ResearchResolvedDependency("AIHelper.HistoricalDataTool", "1.0.0", "project",
                    new[] { new ResearchResolvedDependencyReference("AIHelper.Services", "1.0.0") }),
                new ResearchResolvedDependency("AIHelper.Services", "1.0.0", "project", null),
                new ResearchResolvedDependency("Serilog", "4.3.1", "package", null)
            })));

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

    private static async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        using Process process = Process.Start(CreateCliStartInfo(args)) ?? throw new InvalidOperationException("Could not start CLI.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout + "\n" + await stderr);
    }

    private static ProcessStartInfo CreateCliStartInfo(params string[] args)
    {
        ProcessStartInfo psi = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = SolutionRoot() };
        psi.Environment.Remove("HISTORICAL_GATEWAY_URL");
        foreach (string argument in new[] { "run", "--project", Path.Combine(SolutionRoot(), "tools", "AIHelper.HistoricalDataTool", "AIHelper.HistoricalDataTool.csproj"),
            "--configuration", TestExecutionConfiguration.Current(), "--no-build", "--no-restore", "--" }.Concat(args)) psi.ArgumentList.Add(argument);
        return psi;
    }

    private static string SolutionRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-BatchAuditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch { /* Best effort test cleanup */ }
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
}
