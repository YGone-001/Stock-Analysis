using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowResearchReproducibilityVerifierTests
{
    private static readonly string[] CanonicalCheckCodes =
    {
        ResearchReproducibilityCheckCodes.ExperimentRecordValid,
        ResearchReproducibilityCheckCodes.ExperimentFingerprintValid,
        ResearchReproducibilityCheckCodes.ParameterSnapshotValid,
        ResearchReproducibilityCheckCodes.LineageValid,
        ResearchReproducibilityCheckCodes.ArtifactJsonValid,
        ResearchReproducibilityCheckCodes.ArtifactVersionSupported,
        ResearchReproducibilityCheckCodes.ArtifactFingerprintPresent,
        ResearchReproducibilityCheckCodes.ArtifactFingerprintValid,
        ResearchReproducibilityCheckCodes.DatasetFingerprintMatch,
        ResearchReproducibilityCheckCodes.ArtifactReferenceVersionMatch,
        ResearchReproducibilityCheckCodes.ArtifactReferenceFingerprintMatch,
        ResearchReproducibilityCheckCodes.StrategyIdentityValid,
        ResearchReproducibilityCheckCodes.StrategyModeMatch,
        ResearchReproducibilityCheckCodes.StrategyVersionMatch,
        ResearchReproducibilityCheckCodes.StrategyFingerprintValid,
        ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintValid,
        ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintMatch,
        ResearchReproducibilityCheckCodes.AnalysisFingerprintValid,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingValid,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceExperimentMatch,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceParameterMatch,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceStrategyParameterMatch,
        ResearchReproducibilityCheckCodes.ExecutionProvenancePortfolioMatch,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceAnalysisMatch,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceDatasetMatch,
        ResearchReproducibilityCheckCodes.ExecutionProvenanceArtifactMatch
    };

    [Fact]
    public async Task VerifyAsync_ValidV2Artifact_ReturnsVerifiedAndAllChecksPass()
    {
        (PersistedResearchExperimentRecord record, SparrowPortfolioResearchArtifact artifact, string artifactJson) = await CreateMatchingPairAsync();
        SparrowResearchReproducibilityVerifier verifier = new();

        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, artifactJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, result.Status);
        Assert.Equal(27, result.CheckCount);
        Assert.Equal(0, result.FailedCheckCount);
        Assert.Equal(0, result.UnsupportedCheckCount);
        Assert.Equal(27, result.PassedCheckCount);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal(record.ExperimentId, result.ExperimentId);
        Assert.Equal(record.ExperimentFingerprint, result.ExperimentFingerprint);
        Assert.Equal(artifact.DatasetFingerprint, result.DatasetFingerprint);
        Assert.Equal(SparrowPortfolioResearchArtifact.CurrentArtifactVersion, result.ArtifactVersion);
        Assert.Equal(artifact.ArtifactFingerprint, result.ArtifactFingerprint);

        Assert.Equal(CanonicalCheckCodes, result.Checks.Select(c => c.Code));
        Assert.All(result.Checks, check => Assert.Equal(ResearchReproducibilityCheckStatus.Pass, check.Status));
    }

    [Fact]
    public async Task VerifyAsync_Determinism_ProducesIdenticalStatusAndOrderedChecks()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        SparrowResearchReproducibilityVerifier verifier = new();

        ResearchReproducibilityVerificationResult first = verifier.VerifyContent(record, artifactJson);
        ResearchReproducibilityVerificationResult second = verifier.VerifyContent(record, artifactJson);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.CheckCount, second.CheckCount);
        Assert.Equal(first.FailedCheckCount, second.FailedCheckCount);
        Assert.Equal(first.ArtifactFingerprint, second.ArtifactFingerprint);
        Assert.Equal(first.ExperimentFingerprint, second.ExperimentFingerprint);
        Assert.Equal(first.Checks.Select(c => c.Code), second.Checks.Select(c => c.Code));
        Assert.Equal(first.Checks.Select(c => c.Status), second.Checks.Select(c => c.Status));
        Assert.Equal(first.Checks.Select(c => c.Expected), second.Checks.Select(c => c.Expected));
        Assert.Equal(first.Checks.Select(c => c.Actual), second.Checks.Select(c => c.Actual));
    }

    [Fact]
    public async Task VerifyAsync_PathIndependence_YieldsIdenticalVerification()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        SparrowResearchReproducibilityVerifier verifier = new();

        string dirA = TemporaryDirectory();
        string dirB = TemporaryDirectory();
        try
        {
            string pathA = Path.Combine(dirA, "result.json");
            string pathB = Path.Combine(dirB, "renamed_experiment_artifact.json");
            await File.WriteAllTextAsync(pathA, artifactJson);
            await File.WriteAllTextAsync(pathB, artifactJson);

            ResearchReproducibilityVerificationResult resultA = await verifier.VerifyAsync(record, pathA);
            ResearchReproducibilityVerificationResult resultB = await verifier.VerifyAsync(record, pathB);

            Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, resultA.Status);
            Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, resultB.Status);
            Assert.Equal(resultA.ArtifactFingerprint, resultB.ArtifactFingerprint);
            Assert.Equal(resultA.Checks.Select(c => c.Status), resultB.Checks.Select(c => c.Status));
        }
        finally
        {
            DeleteDirectory(dirA);
            DeleteDirectory(dirB);
        }
    }

    [Fact]
    public async Task VerifyAsync_FormattingIndependence_IndentationWhitespaceLineEndingsDoNotAlterFingerprint()
    {
        (PersistedResearchExperimentRecord record, SparrowPortfolioResearchArtifact originalArtifact, string originalJson) = await CreateMatchingPairAsync();
        SparrowResearchReproducibilityVerifier verifier = new();

        // 1. Compact JSON (no indentation)
        using JsonDocument parsedDoc = JsonDocument.Parse(originalJson);
        string compactJson = JsonSerializer.Serialize(parsedDoc.RootElement, new JsonSerializerOptions { WriteIndented = false });

        // 2. Extra spaces and tabs
        string spaciousJson = compactJson.Replace(":", "  :  ", StringComparison.Ordinal).Replace(",", "  ,\n  ", StringComparison.Ordinal);

        // 3. Different line endings (\r\n vs \n)
        string crlfJson = originalJson.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        string lfOnlyJson = originalJson.Replace("\r\n", "\n", StringComparison.Ordinal);

        ResearchReproducibilityVerificationResult compactResult = verifier.VerifyContent(record, compactJson);
        ResearchReproducibilityVerificationResult spaciousResult = verifier.VerifyContent(record, spaciousJson);
        ResearchReproducibilityVerificationResult crlfResult = verifier.VerifyContent(record, crlfJson);
        ResearchReproducibilityVerificationResult lfResult = verifier.VerifyContent(record, lfOnlyJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, compactResult.Status);
        Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, spaciousResult.Status);
        Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, crlfResult.Status);
        Assert.Equal(ResearchReproducibilityVerificationStatus.Verified, lfResult.Status);

        Assert.Equal(originalArtifact.ArtifactFingerprint, compactResult.ArtifactFingerprint);
        Assert.Equal(originalArtifact.ArtifactFingerprint, spaciousResult.ArtifactFingerprint);
        Assert.Equal(originalArtifact.ArtifactFingerprint, crlfResult.ArtifactFingerprint);
        Assert.Equal(originalArtifact.ArtifactFingerprint, lfResult.ArtifactFingerprint);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactPayloadTamper_PerformanceChangeFailsArtifactFingerprint()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["performance"]!["finalEquity"] = 9999999;
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.ArtifactFingerprintValid, result.ReasonCodes);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactPayloadTamper_TradeChangeFailsArtifactFingerprint()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["trades"]!.AsArray()[0]!["price"] = 999;
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactPayloadTamper_WarningChangeFailsArtifactFingerprint()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["warnings"]!.AsArray().Add("tampered-warning");
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactPayloadTamper_LimitationChangeFailsArtifactFingerprint()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["limitations"]!.AsArray().Add("tampered-limitation");
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactPayloadTamper_CreatedByChangeFailsArtifactFingerprint()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["createdBy"] = "tampered-operator";
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactFingerprintTamper_DetectedAndFails()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["artifactFingerprint"] = "0000000000000000000000000000000000000000000000000000000000000000";
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck fpValidCheck = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, fpValidCheck.Status);
    }

    [Fact]
    public async Task VerifyAsync_DatasetMismatch_DetectedAndFails()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync(datasetFingerprint: "dataset-AAA");
        // Create an artifact with dataset-BBB
        (_, _, string artifactJsonB) = await CreateMatchingPairAsync(datasetFingerprint: "dataset-BBB");

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, artifactJsonB);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.DatasetFingerprintMatch);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.DatasetFingerprintMatch, result.ReasonCodes);
    }

    [Fact]
    public async Task VerifyAsync_ArtifactReferenceMismatch_DetectedAndFails()
    {
        (PersistedResearchExperimentRecord record, _, _) = await CreateMatchingPairAsync(experimentId: "EXP-001");
        // Another completely valid artifact with different buy price (different artifact fingerprint)
        (_, _, string otherArtifactJson) = await CreateMatchingPairAsync(experimentId: "EXP-002", buyPrice: 105);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, otherArtifactJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactReferenceFingerprintMatch);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
        Assert.Equal(ResearchReproducibilityCheckStatus.Pass, Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.DatasetFingerprintMatch).Status);
    }

    [Fact]
    public async Task VerifyAsync_StrategyMismatch_ModeMismatchFails()
    {
        (PersistedResearchExperimentRecord recordV2, _, string artifactJsonV2) = await CreateMatchingPairAsync();

        // Construct a record with Classic strategy mode
        ResearchExperimentDefinition definitionClassic = new(
            recordV2.Definition.Identity,
            recordV2.Definition.DatasetFingerprint,
            new ResearchExperimentStrategyIdentity("Classic", "classic-v1"),
            recordV2.Definition.Parameters,
            recordV2.Definition.PortfolioConfigurationFingerprint,
            recordV2.Definition.AnalysisConfigurationFingerprint);

        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(recordV2.Definition.Identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), recordV2.ExecutionSummary.ArtifactFingerprint);

        PersistedResearchExperimentRecord recordClassic = new(
            recordV2.ExperimentId,
            definitionClassic.SemanticFingerprint,
            definitionClassic,
            ResearchExperimentExecutionSummary.FromCompletedExecution(execution, recordV2.ExecutionSummary.Performance),
            recordV2.ArtifactReference,
            new ResearchArtifactLineage(definitionClassic.SemanticFingerprint, definitionClassic.DatasetFingerprint, recordV2.Definition.Parameters.Fingerprint, recordV2.ExecutionSummary.ArtifactFingerprint),
            recordV2.CreatedAt);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(recordClassic, artifactJsonV2);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.StrategyModeMatch);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_StrategyMismatch_VersionMismatchFails()
    {
        (PersistedResearchExperimentRecord recordV2, _, string artifactJsonV2) = await CreateMatchingPairAsync();

        // Construct a record with changed strategy version
        ResearchExperimentDefinition definitionOtherVer = new(
            recordV2.Definition.Identity,
            recordV2.Definition.DatasetFingerprint,
            new ResearchExperimentStrategyIdentity(recordV2.Definition.StrategyIdentity.Mode, "v2-different-version"),
            recordV2.Definition.Parameters,
            recordV2.Definition.PortfolioConfigurationFingerprint,
            recordV2.Definition.AnalysisConfigurationFingerprint);

        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(recordV2.Definition.Identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), recordV2.ExecutionSummary.ArtifactFingerprint);

        PersistedResearchExperimentRecord recordOtherVer = new(
            recordV2.ExperimentId,
            definitionOtherVer.SemanticFingerprint,
            definitionOtherVer,
            ResearchExperimentExecutionSummary.FromCompletedExecution(execution, recordV2.ExecutionSummary.Performance),
            recordV2.ArtifactReference,
            new ResearchArtifactLineage(definitionOtherVer.SemanticFingerprint, definitionOtherVer.DatasetFingerprint, recordV2.Definition.Parameters.Fingerprint, recordV2.ExecutionSummary.ArtifactFingerprint),
            recordV2.CreatedAt);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(recordOtherVer, artifactJsonV2);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.StrategyVersionMatch);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_PortfolioConfigurationTamper_TopNChangeFailsPortfolioCheck()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["portfolio"]!["topN"] = 10; // was 2
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_AnalysisFingerprintTamper_DetectedAndFails()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
        root["analysisFingerprint"] = "0000000000000000000000000000000000000000000000000000000000000000";
        string tampered = root.ToJsonString();

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, tampered);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.AnalysisFingerprintValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_V1Artifact_ReturnsUnsupportedWithLegacyReason()
    {
        (PersistedResearchExperimentRecord record, _, _) = await CreateMatchingPairAsync();
        string v1Json = "{\"artifactVersion\":\"portfolio-research-v1\",\"analysisFingerprint\":\"analysis-fp-v1\"}";

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, v1Json);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Unsupported, result.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.ArtifactIdentityUnavailableV1, result.ReasonCodes);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactVersionSupported);
        Assert.Equal(ResearchReproducibilityCheckStatus.Unsupported, check.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.ArtifactIdentityUnavailableV1, check.Message);
    }

    [Fact]
    public async Task VerifyAsync_UnknownFutureArtifactVersion_ReturnsUnsupported()
    {
        (PersistedResearchExperimentRecord record, _, _) = await CreateMatchingPairAsync();
        string futureJson = "{\"artifactVersion\":\"portfolio-research-v3\",\"analysisFingerprint\":\"analysis-fp-v3\"}";

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, futureJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Unsupported, result.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.ArtifactVersionUnsupported, result.ReasonCodes);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactVersionSupported);
        Assert.Equal(ResearchReproducibilityCheckStatus.Unsupported, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_MalformedJson_FailsWithArtifactJsonInvalid()
    {
        (PersistedResearchExperimentRecord record, _, _) = await CreateMatchingPairAsync();
        string malformed = "{ \"artifactVersion\": \"portfolio-research-v2\", ";

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(record, malformed);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.ArtifactJsonValid, result.ReasonCodes);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ArtifactJsonValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_MissingArtifactFile_ThrowsFileNotFoundException()
    {
        (PersistedResearchExperimentRecord record, _, _) = await CreateMatchingPairAsync();
        SparrowResearchReproducibilityVerifier verifier = new();

        await Assert.ThrowsAsync<FileNotFoundException>(() => verifier.VerifyAsync(record, "C:\\non_existent_directory_for_test\\missing.json"));
    }

    [Fact]
    public async Task HistoricalTool_CliVerifyReproducibility_SucceedsOfflineWithoutHistoricalGatewayUrl()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        string directory = TemporaryDirectory();
        string artifactPath = Path.Combine(directory, "EXP-001-artifact.json");
        try
        {
            await new JsonResearchExperimentRepository(directory).SaveAsync(record);
            await File.WriteAllTextAsync(artifactPath, artifactJson);

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
            start.ArgumentList.Add("--verify-reproducibility");
            start.ArgumentList.Add("EXP-001");
            start.ArgumentList.Add("--experiment-store");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("--artifact");
            start.ArgumentList.Add(artifactPath);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.True(process.ExitCode == 0, $"CLI failed (code {process.ExitCode}): {stdout}\n{stderr}");
            Assert.Contains("VERIFICATION_STATUS=Verified", stdout);
            Assert.Contains("EXPERIMENT_ID=EXP-001", stdout);
            Assert.Contains("ARTIFACT_VERSION=portfolio-research-v2", stdout);
            Assert.Contains($"EXPERIMENT_FINGERPRINT={record.ExperimentFingerprint}", stdout);
            Assert.Contains($"DATASET_FINGERPRINT={record.Definition.DatasetFingerprint}", stdout);
            Assert.Contains($"ARTIFACT_FINGERPRINT={record.ExecutionSummary.ArtifactFingerprint}", stdout);
            Assert.Contains("CHECK_COUNT=27", stdout);
            Assert.Contains("FAILED_CHECK_COUNT=0", stdout);
            Assert.Contains("CHECK=ARTIFACT_FINGERPRINT_VALID:Pass", stdout);
            Assert.Contains("CHECK=DATASET_FINGERPRINT_MATCH:Pass", stdout);
            Assert.Contains("CHECK=EXECUTION_PROVENANCE_BINDING_VALID:Pass", stdout);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task HistoricalTool_CliVerifyReproducibility_FailsWithNonZeroOnTamperedArtifact()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        string directory = TemporaryDirectory();
        string artifactPath = Path.Combine(directory, "EXP-001-artifact.json");
        try
        {
            await new JsonResearchExperimentRepository(directory).SaveAsync(record);
            JsonObject root = JsonNode.Parse(artifactJson)!.AsObject();
            root["performance"]!["finalEquity"] = 9999999;
            await File.WriteAllTextAsync(artifactPath, root.ToJsonString());

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
            start.ArgumentList.Add("--verify-reproducibility");
            start.ArgumentList.Add("EXP-001");
            start.ArgumentList.Add("--experiment-store");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("--artifact");
            start.ArgumentList.Add(artifactPath);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("VERIFICATION_STATUS=Failed", stdout);
            Assert.Contains("CHECK=ARTIFACT_FINGERPRINT_VALID:Fail", stdout);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task HistoricalTool_CliVerifyReproducibility_FailsWithNonZeroOnV1Artifact()
    {
        (PersistedResearchExperimentRecord record, _, _) = await CreateMatchingPairAsync();
        string directory = TemporaryDirectory();
        string artifactPath = Path.Combine(directory, "EXP-001-artifact.json");
        try
        {
            await new JsonResearchExperimentRepository(directory).SaveAsync(record);
            await File.WriteAllTextAsync(artifactPath, "{\"artifactVersion\":\"portfolio-research-v1\",\"analysisFingerprint\":\"fp\"}");

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
            start.ArgumentList.Add("--verify-reproducibility");
            start.ArgumentList.Add("EXP-001");
            start.ArgumentList.Add("--experiment-store");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("--artifact");
            start.ArgumentList.Add(artifactPath);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("VERIFICATION_STATUS=Unsupported", stdout);
            Assert.Contains("CHECK=ARTIFACT_VERSION_SUPPORTED:Unsupported", stdout);
            Assert.Contains("REASON=ARTIFACT_IDENTITY_UNAVAILABLE_V1", stdout);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task HistoricalTool_CliVerifyReproducibility_MissingArtifactExitsNonZero()
    {
        string directory = TemporaryDirectory();
        try
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
            start.ArgumentList.Add("--verify-reproducibility");
            start.ArgumentList.Add("EXP-001");
            start.ArgumentList.Add("--experiment-store");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("--artifact");
            start.ArgumentList.Add(Path.Combine(directory, "does_not_exist.json"));

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("ARTIFACT_NOT_FOUND", stderr);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task VerifyAsync_V1Record_ReturnsUnsupportedWithBindingUnavailableReason()
    {
        (PersistedResearchExperimentRecord boundRecord, _, string artifactJson) = await CreateMatchingPairAsync();
        // Construct an unbound V1 record
        PersistedResearchExperimentRecord v1Record = new(
            boundRecord.ExperimentId,
            boundRecord.ExperimentFingerprint,
            boundRecord.Definition,
            boundRecord.ExecutionSummary,
            boundRecord.ArtifactReference,
            boundRecord.Lineage,
            boundRecord.CreatedAt,
            PersistedResearchExperimentRecord.LegacySchemaVersion,
            executionProvenanceBinding: null);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(v1Record, artifactJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Unsupported, result.Status);
        Assert.Contains(ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingUnavailableV1, result.ReasonCodes);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent);
        Assert.Equal(ResearchReproducibilityCheckStatus.Unsupported, check.Status);
        Assert.Equal(18, result.PassedCheckCount);
        Assert.Equal(1, result.UnsupportedCheckCount);
        Assert.Equal(0, result.FailedCheckCount);
    }

    [Fact]
    public async Task VerifyAsync_ExecutionProvenanceBinding_TamperedStrategyParameter_Fails()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        ResearchExecutionProvenanceBinding originalBinding = record.ExecutionProvenanceBinding!;

        ResearchExecutionProvenanceBinding tamperedBinding = new(
            originalBinding.BindingVersion,
            originalBinding.ExperimentFingerprint,
            originalBinding.ParameterSnapshotFingerprint,
            originalBinding.ExperimentStrategyParameterFingerprint,
            "tampered-artifact-strategy-param-fp",
            originalBinding.ExperimentPortfolioConfigurationFingerprint,
            originalBinding.ArtifactPortfolioConfigurationFingerprint,
            originalBinding.ExperimentAnalysisConfigurationFingerprint,
            originalBinding.ArtifactAnalysisFingerprint,
            originalBinding.DatasetFingerprint,
            originalBinding.ArtifactVersion,
            originalBinding.ArtifactFingerprint,
            ResearchExecutionProvenanceBinding.ComputeFingerprint(
                originalBinding.BindingVersion,
                originalBinding.ExperimentFingerprint,
                originalBinding.ParameterSnapshotFingerprint,
                originalBinding.ExperimentStrategyParameterFingerprint,
                "tampered-artifact-strategy-param-fp",
                originalBinding.ExperimentPortfolioConfigurationFingerprint,
                originalBinding.ArtifactPortfolioConfigurationFingerprint,
                originalBinding.ExperimentAnalysisConfigurationFingerprint,
                originalBinding.ArtifactAnalysisFingerprint,
                originalBinding.DatasetFingerprint,
                originalBinding.ArtifactVersion,
                originalBinding.ArtifactFingerprint));

        PersistedResearchExperimentRecord tamperedRecord = new(
            record.ExperimentId,
            record.ExperimentFingerprint,
            record.Definition,
            record.ExecutionSummary,
            record.ArtifactReference,
            record.Lineage,
            record.CreatedAt,
            record.SchemaVersion,
            tamperedBinding);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(tamperedRecord, artifactJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ExecutionProvenanceStrategyParameterMatch);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_ExecutionProvenanceBinding_TamperedAnalysisFingerprint_Fails()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        ResearchExecutionProvenanceBinding originalBinding = record.ExecutionProvenanceBinding!;

        ResearchExecutionProvenanceBinding tamperedBinding = new(
            originalBinding.BindingVersion,
            originalBinding.ExperimentFingerprint,
            originalBinding.ParameterSnapshotFingerprint,
            originalBinding.ExperimentStrategyParameterFingerprint,
            originalBinding.ArtifactStrategyParameterFingerprint,
            originalBinding.ExperimentPortfolioConfigurationFingerprint,
            originalBinding.ArtifactPortfolioConfigurationFingerprint,
            originalBinding.ExperimentAnalysisConfigurationFingerprint,
            "tampered-analysis-fp",
            originalBinding.DatasetFingerprint,
            originalBinding.ArtifactVersion,
            originalBinding.ArtifactFingerprint,
            ResearchExecutionProvenanceBinding.ComputeFingerprint(
                originalBinding.BindingVersion,
                originalBinding.ExperimentFingerprint,
                originalBinding.ParameterSnapshotFingerprint,
                originalBinding.ExperimentStrategyParameterFingerprint,
                originalBinding.ArtifactStrategyParameterFingerprint,
                originalBinding.ExperimentPortfolioConfigurationFingerprint,
                originalBinding.ArtifactPortfolioConfigurationFingerprint,
                originalBinding.ExperimentAnalysisConfigurationFingerprint,
                "tampered-analysis-fp",
                originalBinding.DatasetFingerprint,
                originalBinding.ArtifactVersion,
                originalBinding.ArtifactFingerprint));

        PersistedResearchExperimentRecord tamperedRecord = new(
            record.ExperimentId,
            record.ExperimentFingerprint,
            record.Definition,
            record.ExecutionSummary,
            record.ArtifactReference,
            record.Lineage,
            record.CreatedAt,
            record.SchemaVersion,
            tamperedBinding);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(tamperedRecord, artifactJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ExecutionProvenanceAnalysisMatch);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task VerifyAsync_ExecutionProvenanceBinding_TamperedBindingFingerprint_Fails()
    {
        (PersistedResearchExperimentRecord record, _, string artifactJson) = await CreateMatchingPairAsync();
        ResearchExecutionProvenanceBinding originalBinding = record.ExecutionProvenanceBinding!;

        ResearchExecutionProvenanceBinding tamperedBinding = new(
            originalBinding.BindingVersion,
            originalBinding.ExperimentFingerprint,
            originalBinding.ParameterSnapshotFingerprint,
            originalBinding.ExperimentStrategyParameterFingerprint,
            originalBinding.ArtifactStrategyParameterFingerprint,
            originalBinding.ExperimentPortfolioConfigurationFingerprint,
            originalBinding.ArtifactPortfolioConfigurationFingerprint,
            originalBinding.ExperimentAnalysisConfigurationFingerprint,
            originalBinding.ArtifactAnalysisFingerprint,
            originalBinding.DatasetFingerprint,
            originalBinding.ArtifactVersion,
            originalBinding.ArtifactFingerprint,
            "0000000000000000000000000000000000000000000000000000000000000000");

        Assert.Throws<ArgumentException>(() => new PersistedResearchExperimentRecord(
            record.ExperimentId,
            record.ExperimentFingerprint,
            record.Definition,
            record.ExecutionSummary,
            record.ArtifactReference,
            record.Lineage,
            record.CreatedAt,
            PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion,
            tamperedBinding));

        PersistedResearchExperimentRecord bypassRecord = new(
            record.ExperimentId,
            record.ExperimentFingerprint,
            record.Definition,
            record.ExecutionSummary,
            record.ArtifactReference,
            record.Lineage,
            record.CreatedAt,
            PersistedResearchExperimentRecord.LegacySchemaVersion,
            tamperedBinding);

        SparrowResearchReproducibilityVerifier verifier = new();
        ResearchReproducibilityVerificationResult result = verifier.VerifyContent(bypassRecord, artifactJson);

        Assert.Equal(ResearchReproducibilityVerificationStatus.Failed, result.Status);
        ResearchReproducibilityCheck check = Assert.Single(result.Checks, c => c.Code == ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingValid);
        Assert.Equal(ResearchReproducibilityCheckStatus.Fail, check.Status);
    }

    [Fact]
    public async Task HistoricalTool_CliConflictingModes_ExitsNonZeroWithConflictingOperationModes()
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
        start.ArgumentList.Add("--verify-reproducibility");
        start.ArgumentList.Add("EXP-001");
        start.ArgumentList.Add("--report-experiment");
        start.ArgumentList.Add("EXP-001");

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("CONFLICTING_OPERATION_MODES", stderr);
    }

    [Fact]
    public async Task HistoricalTool_CliVerifyReproducibility_V1Record_ExitsNonZeroWithUnsupported()
    {
        (PersistedResearchExperimentRecord boundRecord, _, string artifactJson) = await CreateMatchingPairAsync();
        PersistedResearchExperimentRecord v1Record = new(
            boundRecord.ExperimentId,
            boundRecord.ExperimentFingerprint,
            boundRecord.Definition,
            boundRecord.ExecutionSummary,
            boundRecord.ArtifactReference,
            boundRecord.Lineage,
            boundRecord.CreatedAt,
            PersistedResearchExperimentRecord.LegacySchemaVersion,
            executionProvenanceBinding: null);

        string directory = TemporaryDirectory();
        string artifactPath = Path.Combine(directory, "EXP-001-artifact.json");
        try
        {
            await new JsonResearchExperimentRepository(directory).SaveAsync(v1Record);
            await File.WriteAllTextAsync(artifactPath, artifactJson);

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
            start.ArgumentList.Add("--verify-reproducibility");
            start.ArgumentList.Add("EXP-001");
            start.ArgumentList.Add("--experiment-store");
            start.ArgumentList.Add(directory);
            start.ArgumentList.Add("--artifact");
            start.ArgumentList.Add(artifactPath);

            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI.");
            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains("VERIFICATION_STATUS=Unsupported", stdout);
            Assert.Contains("CHECK=EXECUTION_PROVENANCE_BINDING_PRESENT:Unsupported", stdout);
            Assert.Contains("REASON=EXECUTION_PROVENANCE_BINDING_UNAVAILABLE_V1", stdout);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static async Task<(PersistedResearchExperimentRecord Record, SparrowPortfolioResearchArtifact Artifact, string ArtifactJson)> CreateMatchingPairAsync(
        string experimentId = "EXP-001",
        string datasetFingerprint = "dataset-001",
        decimal buyPrice = 100)
    {
        DateOnly first = new(2026, 1, 2);
        DateOnly second = new(2026, 1, 3);
        PortfolioSimulationRequest request = new(
            "fixture-dataset",
            datasetFingerprint,
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

        PortfolioTrade[] trades =
        {
            new("600000", first, PortfolioTradeSide.Buy, buyPrice, 1, buyPrice, 0),
            new("600001", second, PortfolioTradeSide.Sell, 110, 1, 110, 0)
        };
        PortfolioPosition[] positions =
        {
            new("600000", first, 100, 1, 100, 0, PortfolioPositionStatus.Closed, second, 110, 110, 0, 10, 10),
            new("600001", second, 100, 1, 100, 0, PortfolioPositionStatus.Open)
        };
        SparrowPortfolioSimulationResult simulation = new(request, datasetFingerprint, trades, positions, warnings: new[] { "warning-a" });
        PortfolioEquityCurve curve = new(new[]
        {
            new PortfolioEquityPoint(first, 900, 100, 1_000, null, 0),
            new PortfolioEquityPoint(second, 910, 100, 1_010, .01, .01)
        });
        PortfolioAttribution[] attribution =
        {
            new("600000", first, second, 1, 1, 10, 10, 1, true),
            new("600001", second, second, 0, 1, -5, -5, -.5, false)
        };
        PortfolioPerformanceMetrics metrics = new(1_000, 1_010, 1, -1, second, 2, 1, 1, .5);
        SparrowPortfolioPerformanceResult performanceResult = new(simulation, curve, metrics, attribution, warnings: new[] { "warning-a" });

        string tempPath = Path.Combine(Path.GetTempPath(), $"artifact-{Guid.NewGuid():N}.json");
        SparrowPortfolioResearchArtifact artifact = await new SparrowPortfolioResearchExporter().ExportAsync(performanceResult, tempPath);
        string artifactJson = await File.ReadAllTextAsync(tempPath);
        File.Delete(tempPath);

        ResearchExperimentIdentity identity = new(experimentId, ResearchExperimentIdentity.CurrentExperimentVersion, "operator-a", new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero));
        ExperimentParameterSnapshot parameters = new("v1",
            new Dictionary<string, string> { ["StrategyParam"] = "value-a" },
            new Dictionary<string, string> { ["TopN"] = "2" },
            new Dictionary<string, string> { ["Metric"] = "Return" });

        ResearchExperimentDefinition definition = new(
            identity,
            artifact.DatasetFingerprint,
            new ResearchExperimentStrategyIdentity(artifact.Strategy.Mode, artifact.Strategy.Version),
            parameters,
            artifact.PortfolioConfigurationFingerprint,
            "analysis-config-001",
            "benchmark:CSI300",
            "Reproducibility test experiment");

        ResearchExperimentExecution execution = ResearchExperimentExecution.Create(identity)
            .Start(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero))
            .Complete(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero), artifact.ArtifactFingerprint, new[] { "warning-a" });

        ResearchExperimentPerformanceSummary performanceSummary = new(1.0, -1.0, 2, 0.5);
        ResearchExperimentExecutionSummary executionSummary = ResearchExperimentExecutionSummary.FromCompletedExecution(execution, performanceSummary);

        ResearchArtifactLineage lineage = new(
            definition.SemanticFingerprint,
            definition.DatasetFingerprint,
            parameters.Fingerprint,
            artifact.ArtifactFingerprint);

        ResearchResultArtifactReference artifactRef = new(artifact.ArtifactFingerprint, artifact.ArtifactVersion);

        ResearchExecutionProvenanceBinding binding = ResearchExecutionProvenanceBinding.Create(definition, artifact);

        PersistedResearchExperimentRecord record = new(
            experimentId,
            definition.SemanticFingerprint,
            definition,
            executionSummary,
            artifactRef,
            lineage,
            new DateTimeOffset(2026, 1, 1, 11, 0, 0, TimeSpan.Zero),
            PersistedResearchExperimentRecord.ExecutionBindingSchemaVersion,
            binding);

        return (record, artifact, artifactJson);
    }

    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AIHelper-ReproducibilityTests", Guid.NewGuid().ToString("N"));
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
