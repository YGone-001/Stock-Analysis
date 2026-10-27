using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleCheckCodes;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleReasonCodes;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleCheckStatus;

namespace AIHelper.Services.StockData.Sparrow;

public interface IResearchEvidenceBundleVerifier
{
    Task<ResearchEvidenceBundleVerificationResult> VerifyAsync(string bundleZipPath, bool reexecute = false, CancellationToken cancellationToken = default);
}

public sealed class ResearchEvidenceBundleVerifier : IResearchEvidenceBundleVerifier
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SparrowResearchReproducibilityVerifier _readinessVerifier;
    private readonly ISparrowResearchReexecutionValidator _reexecutionValidator;
    private readonly HistoricalDatasetJsonLoader _datasetLoader;

    public ResearchEvidenceBundleVerifier(
        SparrowResearchReproducibilityVerifier? readinessVerifier = null,
        ISparrowResearchReexecutionValidator? reexecutionValidator = null,
        HistoricalDatasetJsonLoader? datasetLoader = null)
    {
        _readinessVerifier = readinessVerifier ?? new SparrowResearchReproducibilityVerifier();
        _reexecutionValidator = reexecutionValidator ?? new SparrowResearchReexecutionValidator();
        _datasetLoader = datasetLoader ?? new HistoricalDatasetJsonLoader();
    }

    public async Task<ResearchEvidenceBundleVerificationResult> VerifyAsync(string bundleZipPath, bool reexecute = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleZipPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(bundleZipPath))
            throw new FileNotFoundException($"Evidence bundle archive was not found: '{bundleZipPath}'.", bundleZipPath);

        List<ResearchEvidenceBundleCheck> checks = new();
        List<string> reasonCodes = new();

        void Pass(string code, string? exp, string? act, string msg) =>
            checks.Add(new(code, ResearchEvidenceBundleCheckStatus.Pass, exp, act, msg));
        void Fail(string code, string? exp, string? act, string msg, string reason)
        {
            checks.Add(new(code, ResearchEvidenceBundleCheckStatus.Fail, exp, act, msg));
            if (!reasonCodes.Contains(reason, StringComparer.Ordinal)) reasonCodes.Add(reason);
        }
        void Unsup(string code, string? exp, string? act, string msg, string reason)
        {
            checks.Add(new(code, ResearchEvidenceBundleCheckStatus.Unsupported, exp, act, msg));
            if (!reasonCodes.Contains(reason, StringComparer.Ordinal)) reasonCodes.Add(reason);
        }

        // Stage progression state: every reported component status starts as NotChecked and is promoted only
        // when the corresponding structural stage actually completes. No stage is assumed to have passed.
        StageProgress progress = new();

        ResearchEvidenceBundleVerificationResult Result(
            ResearchEvidenceBundleVerificationStatus status,
            ResearchEvidenceBundleManifest? manifest = null,
            ResearchEvidenceBundleReexecutionStatus reexecStatus = ResearchEvidenceBundleReexecutionStatus.NotRequested)
        {
            return new ResearchEvidenceBundleVerificationResult(
                status,
                reexecStatus,
                manifest?.ExperimentId,
                manifest?.BundleVersion,
                manifest?.BundleManifestFingerprint,
                checks,
                reasonCodes,
                progress.EntryIntegrity,
                progress.ExperimentRecord,
                progress.ArtifactLineage,
                progress.Dataset,
                progress.Parameters);
        }

        // ==============================================================================================
        // Stage A: Archive Structure, Entry Names & Limits
        // ==============================================================================================
        ZipArchive zip;
        FileStream zipFs;
        try
        {
            zipFs = new FileStream(bundleZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            zip = new ZipArchive(zipFs, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (Exception ex)
        {
            Fail(ArchiveStructure, "ValidZip", "MalformedZip", $"Failed to open ZIP archive: {ex.Message}", BundleArchiveInvalid);
            return Result(ResearchEvidenceBundleVerificationStatus.Failed);
        }

        using (zip)
        {
            if (zip.Entries.Count != ResearchEvidenceBundleConstants.ExactEntryCount)
            {
                Fail(ArchiveStructure, $"{ResearchEvidenceBundleConstants.ExactEntryCount} entries", $"{zip.Entries.Count} entries",
                    $"Archive must contain exactly {ResearchEvidenceBundleConstants.ExactEntryCount} entries, found {zip.Entries.Count}.", BundleArchiveInvalid);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed);
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;

                if (name.Contains('\\'))
                {
                    Fail(ArchiveStructure, "ForwardSlashPathsOnly", name, "Archive entry contains backslash path separator.", BundleArchiveInvalid);
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed);
                }
                if (name.StartsWith('/') || name.Contains("..") || name.Contains(':'))
                {
                    Fail(ArchiveStructure, "RelativeSafePathsOnly", name, "Archive entry contains illegal path traversal characters.", BundleArchiveInvalid);
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed);
                }
                if (!seen.Add(name))
                {
                    Fail(ArchiveStructure, "UniqueCaseSensitiveEntries", name, $"Duplicate or case-colliding entry '{name}' detected.", BundleArchiveInvalid);
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed);
                }
                if (!ResearchEvidenceBundleConstants.AllAllowedEntries.Contains(name, StringComparer.Ordinal))
                {
                    Fail(ArchiveStructure, "AllowedEntriesOnly", name, $"Unexpected entry '{name}' in evidence bundle.", BundleArchiveInvalid);
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed);
                }
            }

            foreach (string required in ResearchEvidenceBundleConstants.AllAllowedEntries)
            {
                if (zip.GetEntry(required) is null)
                {
                    Fail(ArchiveStructure, required, "Missing", $"Required archive entry '{required}' was not found.", BundleArchiveInvalid);
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed);
                }
            }

            Pass(ArchiveStructure, "Valid", "Valid", "Archive structure, entry names and fixed entry set verified.");

            // ==============================================================================================
            // Stage B: Streaming Read with Decompression Limits & Entry Integrity
            // ==============================================================================================
            ZipArchiveEntry manifestEntry = zip.GetEntry(ResearchEvidenceBundleConstants.ManifestEntry)!;
            byte[] manifestBytes;
            try
            {
                manifestBytes = ReadEntryBytes(manifestEntry, ResearchEvidenceBundleConstants.MaxManifestDecompressedBytes);
            }
            catch (InvalidOperationException limitEx)
            {
                Fail(ArchiveStructure, $"<= {ResearchEvidenceBundleConstants.MaxManifestDecompressedBytes} bytes", "Exceeded", limitEx.Message, BundleSecurityLimitExceeded);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed);
            }

            ResearchEvidenceBundleManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<ResearchEvidenceBundleManifest>(manifestBytes, JsonOpts);
            }
            catch (Exception ex)
            {
                Fail(ManifestFingerprint, "ValidManifestJson", "DeserializationFailed", $"Manifest deserialization failed: {ex.Message}", BundleArchiveInvalid);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed);
            }

            if (manifest is null)
            {
                Fail(ManifestFingerprint, "ValidManifest", "Null", "Manifest is empty.", BundleArchiveInvalid);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed);
            }

            if (!string.Equals(manifest.BundleVersion, ResearchEvidenceBundleConstants.BundleVersionV1, StringComparison.Ordinal))
            {
                Unsup(ArchiveStructure, ResearchEvidenceBundleConstants.BundleVersionV1, manifest.BundleVersion,
                    $"Unsupported bundle version '{manifest.BundleVersion}'.", BundleUnsupportedVersion);
                return Result(ResearchEvidenceBundleVerificationStatus.Unsupported, manifest);
            }

            if (manifest.Entries.Count != ResearchEvidenceBundleConstants.FixedPayloadEntries.Count)
            {
                Fail(EntryIntegrity, $"{ResearchEvidenceBundleConstants.FixedPayloadEntries.Count} entries", $"{manifest.Entries.Count} entries",
                    "Manifest declared entry count mismatch.", BundleArchiveInvalid);
                progress.EntryIntegrity = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            foreach (string path in ResearchEvidenceBundleConstants.FixedPayloadEntries)
            {
                if (manifest.Entries.All(e => !string.Equals(e.LogicalPath, path, StringComparison.Ordinal)))
                {
                    Fail(EntryIntegrity, path, "Missing", $"Manifest does not declare entry '{path}'.", BundleArchiveInvalid);
                    progress.EntryIntegrity = ResearchEvidenceBundleStageStatus.Failed;
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
                }
            }

            Dictionary<string, byte[]> payloadBytes = new(StringComparer.Ordinal);
            long totalDecompressedBytes = manifestBytes.Length;

            foreach (ResearchEvidenceBundleEntry entryDecl in manifest.Entries)
            {
                ZipArchiveEntry zipEntry = zip.GetEntry(entryDecl.LogicalPath)!;
                byte[] entryContent;
                try
                {
                    long remainingAllowed = ResearchEvidenceBundleConstants.MaxTotalDecompressedBytes - totalDecompressedBytes;
                    long singleLimit = Math.Min(ResearchEvidenceBundleConstants.MaxSingleEntryDecompressedBytes, remainingAllowed);
                    entryContent = ReadEntryBytes(zipEntry, singleLimit);
                    totalDecompressedBytes += entryContent.Length;
                }
                catch (InvalidOperationException limitEx)
                {
                    Fail(EntryIntegrity, "WithinLimits", "Exceeded", limitEx.Message, BundleSecurityLimitExceeded);
                    progress.EntryIntegrity = ResearchEvidenceBundleStageStatus.Failed;
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
                }

                if (entryContent.LongLength != entryDecl.ByteLength)
                {
                    Fail(EntryIntegrity, $"{entryDecl.ByteLength} bytes", $"{entryContent.LongLength} bytes",
                        $"Byte length mismatch for entry '{entryDecl.LogicalPath}'.", BundleEntryIntegrityFailed);
                    progress.EntryIntegrity = ResearchEvidenceBundleStageStatus.Failed;
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
                }

                string actualHash = Convert.ToHexString(SHA256.HashData(entryContent));
                if (!string.Equals(actualHash, entryDecl.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Fail(EntryIntegrity, entryDecl.Sha256, actualHash,
                        $"SHA-256 hash mismatch for entry '{entryDecl.LogicalPath}'.", BundleEntryIntegrityFailed);
                    progress.EntryIntegrity = ResearchEvidenceBundleStageStatus.Failed;
                    return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
                }

                payloadBytes[entryDecl.LogicalPath] = entryContent;
            }

            Pass(EntryIntegrity, "Verified", "Verified", "All entry byte lengths and SHA-256 hashes match manifest.");
            progress.EntryIntegrity = ResearchEvidenceBundleStageStatus.Verified;

            // ==============================================================================================
            // Stage C: Manifest Fingerprint
            // ==============================================================================================
            string recomputedManifestFp = manifest.ComputeFingerprint();
            if (!string.Equals(recomputedManifestFp, manifest.BundleManifestFingerprint, StringComparison.Ordinal))
            {
                Fail(ManifestFingerprint, manifest.BundleManifestFingerprint, recomputedManifestFp,
                    "Bundle manifest fingerprint does not match canonical preimage.", BundleManifestFingerprintMismatch);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(ManifestFingerprint, manifest.BundleManifestFingerprint, recomputedManifestFp, "Bundle manifest fingerprint verified.");

            // ==============================================================================================
            // Stage D: Experiment Record V4 Integrity
            // ==============================================================================================
            PersistedResearchExperimentRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<PersistedResearchExperimentRecord>(payloadBytes[ResearchEvidenceBundleConstants.ExperimentRecordEntry], JsonOpts);
            }
            catch (Exception ex)
            {
                Fail(ExperimentRecord, "ValidRecordJson", "DeserializationFailed", $"Record deserialization failed: {ex.Message}", BundleExperimentRecordInvalid);
                progress.ExperimentRecord = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            if (record is null
                || !string.Equals(record.SchemaVersion, PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal)
                || record.ExecutionSummary.Status != ResearchExperimentExecutionStatus.Completed
                || !string.Equals(record.ExperimentId, manifest.ExperimentId, StringComparison.Ordinal)
                || !string.Equals(record.ExperimentFingerprint, manifest.ExperimentFingerprint, StringComparison.Ordinal)
                || record.ExecutionProvenanceBinding is null
                || !string.Equals(record.ExecutionProvenanceBinding.BindingFingerprint, manifest.ExecutionBindingFingerprint, StringComparison.Ordinal)
                || record.SourceBuildProvenance is null
                || !string.Equals(record.SourceBuildProvenance.ProvenanceFingerprint, manifest.SourceBuildProvenanceFingerprint, StringComparison.Ordinal)
                || record.ExecutionEnvironmentProvenance is null
                || !string.Equals(record.ExecutionEnvironmentProvenance.ProvenanceFingerprint, manifest.ExecutionEnvironmentProvenanceFingerprint, StringComparison.Ordinal))
            {
                Fail(ExperimentRecord, "ValidCompletedRecordV4", "Invalid", "Experiment record V4 verification failed.", BundleExperimentRecordInvalid);
                progress.ExperimentRecord = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(ExperimentRecord, "Verified", "Verified", "Experiment record V4 schema, status and manifest identities verified.");
            progress.ExperimentRecord = ResearchEvidenceBundleStageStatus.Verified;

            // ==============================================================================================
            // Stage E & F: Portfolio Artifact V2 Integrity & Phase 3.9 Lineage
            // ==============================================================================================
            SparrowPortfolioResearchArtifact? artifact;
            string artifactJson = Encoding.UTF8.GetString(payloadBytes[ResearchEvidenceBundleConstants.PortfolioArtifactEntry]);
            try
            {
                artifact = JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(artifactJson, JsonOpts);
            }
            catch (Exception ex)
            {
                Fail(PortfolioArtifact, "ValidArtifactJson", "DeserializationFailed", $"Artifact deserialization failed: {ex.Message}", BundlePortfolioArtifactInvalid);
                progress.ArtifactLineage = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            if (artifact is null
                || !string.Equals(artifact.ArtifactVersion, SparrowPortfolioResearchArtifact.CurrentArtifactVersion, StringComparison.Ordinal)
                || !string.Equals(artifact.ArtifactFingerprint, manifest.ArtifactFingerprint, StringComparison.Ordinal)
                || !string.Equals(record.ExecutionSummary.ArtifactFingerprint, manifest.ArtifactFingerprint, StringComparison.Ordinal)
                || !string.Equals(artifact.DatasetFingerprint, manifest.DatasetFingerprint, StringComparison.Ordinal))
            {
                Fail(PortfolioArtifact, manifest.ArtifactFingerprint, artifact?.ArtifactFingerprint ?? "<null>",
                    "Portfolio artifact V2 identity or dataset fingerprint does not match manifest.", BundlePortfolioArtifactInvalid);
                progress.ArtifactLineage = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(PortfolioArtifact, manifest.ArtifactFingerprint, artifact.ArtifactFingerprint, "Portfolio artifact V2 identity verified.");

            ResearchReproducibilityVerificationResult readiness = _readinessVerifier.VerifyContent(record, artifactJson);
            if (readiness.Status != ResearchReproducibilityVerificationStatus.Verified)
            {
                Fail(ArtifactLineage, "Verified", readiness.Status.ToString(),
                    $"Artifact lineage and reproducibility readiness failed: {string.Join(", ", readiness.ReasonCodes)}", BundleLineageFailed);
                progress.ArtifactLineage = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(ArtifactLineage, "Verified", "Verified", "Artifact lineage, execution binding V3 and reproducibility readiness verified.");
            progress.ArtifactLineage = ResearchEvidenceBundleStageStatus.Verified;

            // ==============================================================================================
            // Stage G: Historical Dataset Identity
            // ==============================================================================================
            HistoricalDatasetLoadResult datasetResult;
            using (MemoryStream dsStream = new(payloadBytes[ResearchEvidenceBundleConstants.HistoricalDatasetEntry]))
            {
                datasetResult = await _datasetLoader.LoadStreamAsync(dsStream, cancellationToken).ConfigureAwait(false);
            }

            if (!datasetResult.Success || datasetResult.Dataset is null)
            {
                Fail(DatasetIdentity, "Success", "Failed", $"Dataset load failed: {string.Join("; ", datasetResult.Errors)}", BundleDatasetInvalid);
                progress.Dataset = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            HistoricalMarketDataset dataset = datasetResult.Dataset;
            if (!string.Equals(dataset.Fingerprint, manifest.DatasetFingerprint, StringComparison.Ordinal))
            {
                Fail(DatasetIdentity, manifest.DatasetFingerprint, dataset.Fingerprint,
                    "Recomputed historical dataset fingerprint does not match manifest.", BundleDatasetInvalid);
                progress.Dataset = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(DatasetIdentity, manifest.DatasetFingerprint, dataset.Fingerprint, "Historical dataset fingerprint verified.");
            progress.Dataset = ResearchEvidenceBundleStageStatus.Verified;

            // ==============================================================================================
            // Stage H: Typed Parameter & Executable Strategy Parameter Identities
            // ==============================================================================================
            ResearchEvidenceReexecutionInputs? reexecInputs;
            try
            {
                reexecInputs = JsonSerializer.Deserialize<ResearchEvidenceReexecutionInputs>(
                    payloadBytes[ResearchEvidenceBundleConstants.ReexecutionInputsEntry], JsonOpts);
            }
            catch (Exception ex)
            {
                Fail(ReexecutionInputs, "ValidInputsJson", "DeserializationFailed", $"Reexecution inputs deserialization failed: {ex.Message}", BundleReexecutionInputsInvalid);
                progress.Parameters = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            if (reexecInputs is null
                || !double.IsFinite(reexecInputs.BacktestRoundTripCostRate) || reexecInputs.BacktestRoundTripCostRate < 0
                || !double.IsFinite(reexecInputs.BacktestSlippageRate) || reexecInputs.BacktestSlippageRate < 0)
            {
                Fail(ReexecutionInputs, "ValidFiniteNonNegative", "Invalid", "Explicit re-execution costs are invalid or missing.", BundleReexecutionInputsInvalid);
                progress.Parameters = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(ReexecutionInputs, "Valid", "Valid", "Explicit re-execution backtest costs verified.");

            string parametersJson = Encoding.UTF8.GetString(payloadBytes[ResearchEvidenceBundleConstants.StrategyParametersEntry]);
            SparrowClassicParameterSnapshot? classicParameters = null;
            SparrowV2ParameterSnapshot? v2Parameters = null;
            SparrowV3ParameterSnapshot? v3Parameters = null;
            SparrowV4ParameterSnapshot? v4Parameters = null;
            SparrowV5ParameterSnapshot? v5Parameters = null;
            SparrowV6ParameterSnapshot? v6Parameters = null;
            try
            {
                if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.Classic)
                    classicParameters = JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("Classic snapshot was null.");
                else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V2)
                    v2Parameters = JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V2 snapshot was null.");
                else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V3)
                    v3Parameters = JsonSerializer.Deserialize<SparrowV3ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V3 snapshot was null.");
                else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V4)
                    v4Parameters = JsonSerializer.Deserialize<SparrowV4ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V4 snapshot was null.");
                else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V5)
                    v5Parameters = JsonSerializer.Deserialize<SparrowV5ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V5 snapshot was null.");
                else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V6)
                    v6Parameters = JsonSerializer.Deserialize<SparrowV6ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V6 snapshot was null.");
                else
                {
                    Unsup(ParameterIdentity, "Classic, V2, V3, V4, V5 or V6", artifact.PortfolioRequest.StrategyMode.ToString(),
                        $"Unsupported strategy mode: {artifact.PortfolioRequest.StrategyMode}", BundleParametersInvalid);
                    progress.Parameters = ResearchEvidenceBundleStageStatus.Unsupported;
                    return Result(ResearchEvidenceBundleVerificationStatus.Unsupported, manifest);
                }
            }
            catch (Exception ex)
            {
                Fail(ParameterIdentity, "ValidTypedParameters", "DeserializationFailed", $"Typed parameter deserialization failed: {ex.Message}", BundleParametersInvalid);
                progress.Parameters = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            var pr = artifact.PortfolioRequest;
            SparrowBacktestRequest backtestRequest = new(pr.StrategyMode, pr.StrategyVersion, pr.StartDate, pr.EndDate, pr.TopN,
                new[] { pr.HorizonTradingDays }, reexecInputs.BacktestRoundTripCostRate, reexecInputs.BacktestSlippageRate,
                ClassicParameters: classicParameters, V2Parameters: v2Parameters, V3Parameters: v3Parameters, V4Parameters: v4Parameters, V5Parameters: v5Parameters, V6Parameters: v6Parameters);

            string candidateParamFp = SparrowHistoricalFingerprint.Parameters(backtestRequest);
            if (!string.Equals(candidateParamFp, pr.StrategyParameterFingerprint, StringComparison.Ordinal)
                || !string.Equals(candidateParamFp, record.ExecutionProvenanceBinding.ArtifactStrategyParameterFingerprint, StringComparison.Ordinal))
            {
                Fail(ParameterIdentity, pr.StrategyParameterFingerprint, candidateParamFp,
                    "Reconstructed executable parameter fingerprint does not match artifact and binding.", BundleParametersInvalid);
                progress.Parameters = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            ExperimentParameterSnapshot genericParams = ResearchExperimentParameterSnapshotFactory.Create(
                pr.StrategyMode, pr.StrategyVersion, pr.StartDate, pr.EndDate, pr.TopN, pr.HorizonTradingDays,
                reexecInputs.BacktestRoundTripCostRate, reexecInputs.BacktestSlippageRate, classicParameters, v2Parameters,
                pr.InitialCapital, pr.PositionSizingMethod, pr.CommissionRate, pr.SlippageRate, pr.ExecutionModel,
                v3Parameters, v4Parameters, v5Parameters, v6Parameters);

            if (!string.Equals(genericParams.Fingerprint, record.Definition.Parameters.Fingerprint, StringComparison.Ordinal)
                || !string.Equals(genericParams.Fingerprint, record.ExecutionProvenanceBinding.ParameterSnapshotFingerprint, StringComparison.Ordinal))
            {
                Fail(ParameterIdentity, record.Definition.Parameters.Fingerprint, genericParams.Fingerprint,
                    "Reconstructed generic experiment parameter snapshot fingerprint does not match record and binding.", BundleParametersInvalid);
                progress.Parameters = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            if (!string.Equals(genericParams.StrategyFingerprint, record.Definition.Parameters.StrategyFingerprint, StringComparison.Ordinal)
                || !string.Equals(genericParams.StrategyFingerprint, record.ExecutionProvenanceBinding.ExperimentStrategyParameterFingerprint, StringComparison.Ordinal)
                || !string.Equals(genericParams.PortfolioFingerprint, record.Definition.Parameters.PortfolioFingerprint, StringComparison.Ordinal)
                || !string.Equals(genericParams.AnalysisFingerprint, record.Definition.Parameters.AnalysisFingerprint, StringComparison.Ordinal)
                || !string.Equals(genericParams.AnalysisFingerprint, record.ExecutionProvenanceBinding.ExperimentAnalysisConfigurationFingerprint, StringComparison.Ordinal)
                || !string.Equals(record.Definition.PortfolioConfigurationFingerprint, record.ExecutionProvenanceBinding.ExperimentPortfolioConfigurationFingerprint, StringComparison.Ordinal))
            {
                Fail(ParameterIdentity, "MatchingSections", "Mismatch",
                    "Reconstructed generic experiment parameter section fingerprints do not match record and execution binding.", BundleParametersInvalid);
                progress.Parameters = ResearchEvidenceBundleStageStatus.Failed;
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            Pass(ParameterIdentity, "Verified", "Verified", "Typed parameter, executable strategy parameter and generic experiment parameter identities verified.");
            progress.Parameters = ResearchEvidenceBundleStageStatus.Verified;

            // ==============================================================================================
            // Stage I: Stored Provenance Internal Consistency
            // ==============================================================================================
            if (!string.Equals(record.SourceBuildProvenance.ProvenanceFingerprint, record.ExecutionProvenanceBinding.SourceBuildProvenanceFingerprint, StringComparison.Ordinal)
                || !string.Equals(record.ExecutionEnvironmentProvenance.ProvenanceFingerprint, record.ExecutionProvenanceBinding.ExecutionEnvironmentProvenanceFingerprint, StringComparison.Ordinal))
            {
                Fail(StoredProvenance, "MatchingProvenanceFingerprints", "Mismatch",
                    "Stored source/build or execution environment provenance fingerprint does not match execution binding.", BundleStoredProvenanceInvalid);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }

            string recomputedBindingFp = record.ExecutionProvenanceBinding.ComputeFingerprint();
            if (!string.Equals(recomputedBindingFp, record.ExecutionProvenanceBinding.BindingFingerprint, StringComparison.Ordinal))
            {
                Fail(StoredProvenance, record.ExecutionProvenanceBinding.BindingFingerprint, recomputedBindingFp,
                    "Recomputed execution binding fingerprint does not match stored binding.", BundleStoredProvenanceInvalid);
                return Result(ResearchEvidenceBundleVerificationStatus.Failed, manifest);
            }
            Pass(StoredProvenance, "Verified", "Verified", "Stored source/build and execution environment provenance internal consistency verified.");

            // ==============================================================================================
            // Stage J: Optional Full Re-execution
            // Structural verification is already complete at this point. Re-execution is an independent
            // domain: its outcome never rewrites the structural bundle status. Callers that requested full
            // verification decide their own process exit code from ReexecutionStatus.
            // ==============================================================================================
            if (!reexecute)
            {
                return Result(ResearchEvidenceBundleVerificationStatus.Verified, manifest, ResearchEvidenceBundleReexecutionStatus.NotRequested);
            }

            string stagingDir = Path.Combine(Path.GetTempPath(), "sparrow-bundle-stage-" + Guid.NewGuid().ToString("N"));
            try
            {
                ResearchReexecutionValidationResult reexec;
                try
                {
                    Directory.CreateDirectory(stagingDir);
                    string stagedArtifact = Path.Combine(stagingDir, "portfolio-artifact.json");
                    string stagedDataset = Path.Combine(stagingDir, "historical-dataset.json");
                    string stagedParameters = Path.Combine(stagingDir, "strategy-parameters.json");

                    await File.WriteAllBytesAsync(stagedArtifact, payloadBytes[ResearchEvidenceBundleConstants.PortfolioArtifactEntry], cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(stagedDataset, payloadBytes[ResearchEvidenceBundleConstants.HistoricalDatasetEntry], cancellationToken).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(stagedParameters, payloadBytes[ResearchEvidenceBundleConstants.StrategyParametersEntry], cancellationToken).ConfigureAwait(false);

                    reexec = await _reexecutionValidator.ValidateReexecutionAsync(
                        record, stagedArtifact, stagedDataset, stagedParameters,
                        reexecInputs.BacktestRoundTripCostRate, reexecInputs.BacktestSlippageRate, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is never downgraded to a structured verification outcome.
                    throw;
                }
                catch (Exception ex)
                {
                    // The optional execution process must not retroactively invalidate completed structural checks.
                    Fail(Reexecution, "Equivalent", "ExecutionException",
                        $"Optional re-execution could not complete: {ex.Message}", BundleReexecutionFailed);
                    return Result(ResearchEvidenceBundleVerificationStatus.Verified, manifest, ResearchEvidenceBundleReexecutionStatus.Failed);
                }

                switch (reexec.Status)
                {
                    case ResearchReexecutionStatus.Equivalent:
                        Pass(Reexecution, "Equivalent", "Equivalent", "Deterministic research re-execution reproduced equivalent results.");
                        return Result(ResearchEvidenceBundleVerificationStatus.Verified, manifest, ResearchEvidenceBundleReexecutionStatus.Equivalent);
                    case ResearchReexecutionStatus.Diverged:
                        Fail(Reexecution, "Equivalent", reexec.Status.ToString(),
                            $"Re-execution diverged: {string.Join(", ", reexec.ReasonCodes)}", BundleReexecutionDiverged);
                        return Result(ResearchEvidenceBundleVerificationStatus.Verified, manifest, ResearchEvidenceBundleReexecutionStatus.Diverged);
                    case ResearchReexecutionStatus.Unsupported:
                        Unsup(Reexecution, "Equivalent", reexec.Status.ToString(),
                            $"Re-execution unsupported: {string.Join(", ", reexec.ReasonCodes)}", BundleReexecutionUnsupported);
                        return Result(ResearchEvidenceBundleVerificationStatus.Verified, manifest, ResearchEvidenceBundleReexecutionStatus.Unsupported);
                    default:
                        Fail(Reexecution, "Equivalent", reexec.Status.ToString(),
                            $"Re-execution failed: {string.Join(", ", reexec.ReasonCodes)}", BundleReexecutionFailed);
                        return Result(ResearchEvidenceBundleVerificationStatus.Verified, manifest, ResearchEvidenceBundleReexecutionStatus.Failed);
                }
            }
            finally
            {
                if (Directory.Exists(stagingDir))
                {
                    try { Directory.Delete(stagingDir, recursive: true); }
                    catch { /* Cleanup private staging directory */ }
                }
            }
        }
    }

    /// <summary>
    /// Narrow progression state used to report only the structural stages that actually completed.
    /// Every component starts as NotChecked and is promoted at the moment its stage completes.
    /// </summary>
    private sealed class StageProgress
    {
        public string EntryIntegrity { get; set; } = ResearchEvidenceBundleStageStatus.NotChecked;
        public string ExperimentRecord { get; set; } = ResearchEvidenceBundleStageStatus.NotChecked;
        public string ArtifactLineage { get; set; } = ResearchEvidenceBundleStageStatus.NotChecked;
        public string Dataset { get; set; } = ResearchEvidenceBundleStageStatus.NotChecked;
        public string Parameters { get; set; } = ResearchEvidenceBundleStageStatus.NotChecked;
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, long byteLimit)
    {
        using Stream stream = entry.Open();
        using MemoryStream ms = new();
        byte[] buffer = new byte[8192];
        long totalRead = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            totalRead += read;
            if (totalRead > byteLimit)
                throw new InvalidOperationException($"Decompressed size exceeded maximum limit of {byteLimit} bytes.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
