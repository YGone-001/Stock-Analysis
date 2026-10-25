using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Services.StockData.Sparrow;

public interface IResearchEvidenceBundleExporter
{
    Task<ResearchEvidenceBundleExportResult> ExportAsync(ResearchEvidenceBundleExportRequest request, CancellationToken cancellationToken = default);
}

public sealed class ResearchEvidenceBundleExporter : IResearchEvidenceBundleExporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions CanonicalJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Func<string, IResearchExperimentRepository> _repositoryFactory;
    private readonly SparrowResearchReproducibilityVerifier _readinessVerifier;
    private readonly ISparrowResearchReexecutionValidator _reexecutionValidator;
    private readonly IHistoricalDatasetLoader _datasetLoader;
    private readonly IResearchEvidenceBundleVerifier _bundleVerifier;

    public ResearchEvidenceBundleExporter(
        Func<string, IResearchExperimentRepository>? repositoryFactory = null,
        SparrowResearchReproducibilityVerifier? readinessVerifier = null,
        ISparrowResearchReexecutionValidator? reexecutionValidator = null,
        IHistoricalDatasetLoader? datasetLoader = null,
        IResearchEvidenceBundleVerifier? bundleVerifier = null)
    {
        _repositoryFactory = repositoryFactory ?? (store => new JsonResearchExperimentRepository(store));
        _readinessVerifier = readinessVerifier ?? new SparrowResearchReproducibilityVerifier();
        _reexecutionValidator = reexecutionValidator ?? new SparrowResearchReexecutionValidator();
        _datasetLoader = datasetLoader ?? new HistoricalDatasetJsonLoader();
        _bundleVerifier = bundleVerifier ?? new ResearchEvidenceBundleVerifier();
    }

    public async Task<ResearchEvidenceBundleExportResult> ExportAsync(ResearchEvidenceBundleExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string fullOutputPath = Path.GetFullPath(request.OutputPath);
        if (File.Exists(fullOutputPath))
            throw new IOException($"Output bundle '{fullOutputPath}' already exists; authoritative research evidence is never overwritten.");

        string? outDir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        if (!File.Exists(request.ArtifactPath))
            throw new FileNotFoundException($"Portfolio research artifact file was not found: '{request.ArtifactPath}'.", request.ArtifactPath);
        if (!File.Exists(request.DatasetPath))
            throw new FileNotFoundException($"Historical dataset file was not found: '{request.DatasetPath}'.", request.DatasetPath);
        if (!File.Exists(request.ParametersPath))
            throw new FileNotFoundException($"Strategy parameters file was not found: '{request.ParametersPath}'.", request.ParametersPath);

        // 1. Load experiment record V4 from repository
        IResearchExperimentRepository repository = _repositoryFactory(request.ExperimentStore);
        PersistedResearchExperimentRecord record = await repository.GetAsync(request.ExperimentId, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(record.SchemaVersion, PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Experiment record schema '{record.SchemaVersion}' is not the current V4 schema.");
        if (record.ExecutionSummary.Status != ResearchExperimentExecutionStatus.Completed)
            throw new InvalidOperationException("Only completed experiment records can be exported into an evidence bundle.");
        if (record.ExecutionProvenanceBinding is null || record.SourceBuildProvenance is null || record.ExecutionEnvironmentProvenance is null)
            throw new InvalidOperationException("Experiment record is missing required provenance components.");

        // 2. Load artifact V2
        string artifactJson = await File.ReadAllTextAsync(request.ArtifactPath, cancellationToken).ConfigureAwait(false);
        SparrowPortfolioResearchArtifact artifact = JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(artifactJson, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize portfolio research artifact.");

        if (!string.Equals(artifact.ArtifactVersion, SparrowPortfolioResearchArtifact.CurrentArtifactVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"Artifact version '{artifact.ArtifactVersion}' is not current V2.");

        // 3. Pre-export Phase 3.9 readiness verification
        ResearchReproducibilityVerificationResult readiness = _readinessVerifier.VerifyContent(record, artifactJson);
        if (readiness.Status != ResearchReproducibilityVerificationStatus.Verified)
            throw new InvalidOperationException($"Pre-export readiness verification failed: {string.Join(", ", readiness.ReasonCodes)}");

        // 4. Load dataset
        HistoricalDatasetLoadResult datasetResult = await _datasetLoader.LoadAsync(request.DatasetPath, cancellationToken).ConfigureAwait(false);
        if (!datasetResult.Success || datasetResult.Dataset is null)
            throw new InvalidOperationException($"Dataset load failed: {string.Join("; ", datasetResult.Errors)}");
        HistoricalMarketDataset dataset = datasetResult.Dataset;

        if (!string.Equals(dataset.Fingerprint, record.Definition.DatasetFingerprint, StringComparison.Ordinal)
            || !string.Equals(dataset.Fingerprint, artifact.DatasetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Dataset fingerprint does not match experiment record and artifact.");

        // 5. Load typed parameter snapshot
        string parametersJson = await File.ReadAllTextAsync(request.ParametersPath, cancellationToken).ConfigureAwait(false);
        SparrowClassicParameterSnapshot? classicParameters = null;
        SparrowV2ParameterSnapshot? v2Parameters = null;
        SparrowV3ParameterSnapshot? v3Parameters = null;
        if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.Classic)
            classicParameters = JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("Classic snapshot was null.");
        else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V2)
            v2Parameters = JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V2 snapshot was null.");
        else if (artifact.PortfolioRequest.StrategyMode == SparrowStrategyMode.V3)
            v3Parameters = JsonSerializer.Deserialize<SparrowV3ParameterSnapshot>(parametersJson, JsonOpts) ?? throw new InvalidOperationException("V3 snapshot was null.");
        else
            throw new NotSupportedException($"Unsupported strategy mode: {artifact.PortfolioRequest.StrategyMode}");

        // 6. Validate parameter identity relationships
        var pr = artifact.PortfolioRequest;
        SparrowBacktestRequest backtestRequest = new(pr.StrategyMode, pr.StrategyVersion, pr.StartDate, pr.EndDate, pr.TopN,
            new[] { pr.HorizonTradingDays }, request.BacktestRoundTripCostRate, request.BacktestSlippageRate,
            ClassicParameters: classicParameters, V2Parameters: v2Parameters, V3Parameters: v3Parameters);

        string candidateParamFp = SparrowHistoricalFingerprint.Parameters(backtestRequest);
        if (!string.Equals(candidateParamFp, pr.StrategyParameterFingerprint, StringComparison.Ordinal)
            || !string.Equals(candidateParamFp, record.ExecutionProvenanceBinding.ArtifactStrategyParameterFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Executable strategy parameter fingerprint does not match artifact and execution binding.");

        ExperimentParameterSnapshot genericParams = ResearchExperimentParameterSnapshotFactory.Create(
            pr.StrategyMode, pr.StrategyVersion, pr.StartDate, pr.EndDate, pr.TopN, pr.HorizonTradingDays,
            request.BacktestRoundTripCostRate, request.BacktestSlippageRate, classicParameters, v2Parameters,
            pr.InitialCapital, pr.PositionSizingMethod, pr.CommissionRate, pr.SlippageRate, pr.ExecutionModel,
            v3Parameters);

        if (!string.Equals(genericParams.Fingerprint, record.Definition.Parameters.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(genericParams.Fingerprint, record.ExecutionProvenanceBinding.ParameterSnapshotFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Generic experiment parameter snapshot fingerprint does not match record definition and execution binding.");

        if (!string.Equals(genericParams.StrategyFingerprint, record.Definition.Parameters.StrategyFingerprint, StringComparison.Ordinal)
            || !string.Equals(genericParams.StrategyFingerprint, record.ExecutionProvenanceBinding.ExperimentStrategyParameterFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Generic experiment strategy fingerprint does not match execution binding.");
        if (!string.Equals(genericParams.PortfolioFingerprint, record.Definition.Parameters.PortfolioFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Generic experiment portfolio fingerprint does not match parameter snapshot.");
        if (!string.Equals(genericParams.AnalysisFingerprint, record.Definition.Parameters.AnalysisFingerprint, StringComparison.Ordinal)
            || !string.Equals(genericParams.AnalysisFingerprint, record.ExecutionProvenanceBinding.ExperimentAnalysisConfigurationFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Generic experiment analysis fingerprint does not match execution binding.");
        if (!string.Equals(record.Definition.PortfolioConfigurationFingerprint, record.ExecutionProvenanceBinding.ExperimentPortfolioConfigurationFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Experiment definition portfolio configuration fingerprint does not match execution binding.");

        // 7. Pre-export Phase 3.10 re-execution validation (must return Equivalent)
        ResearchReexecutionValidationResult reexecResult = await _reexecutionValidator.ValidateReexecutionAsync(
            record, request.ArtifactPath, request.DatasetPath, request.ParametersPath,
            request.BacktestRoundTripCostRate, request.BacktestSlippageRate, cancellationToken).ConfigureAwait(false);

        if (reexecResult.Status != ResearchReexecutionStatus.Equivalent)
            throw new InvalidOperationException($"Pre-export re-execution validation returned '{reexecResult.Status}': {string.Join(", ", reexecResult.ReasonCodes)}");

        // 8. Prepare entry bytes
        string recordStoreFilePath = Path.Combine(request.ExperimentStore, request.ExperimentId + ".json");
        byte[] recordBytes = await File.ReadAllBytesAsync(recordStoreFilePath, cancellationToken).ConfigureAwait(false);
        byte[] datasetBytes = await File.ReadAllBytesAsync(request.DatasetPath, cancellationToken).ConfigureAwait(false);
        byte[] artifactBytes = await File.ReadAllBytesAsync(request.ArtifactPath, cancellationToken).ConfigureAwait(false);
        byte[] parameterBytes = await File.ReadAllBytesAsync(request.ParametersPath, cancellationToken).ConfigureAwait(false);

        ResearchEvidenceReexecutionInputs reexecInputs = new(request.BacktestRoundTripCostRate, request.BacktestSlippageRate);
        byte[] reexecInputsBytes = JsonSerializer.SerializeToUtf8Bytes(reexecInputs, CanonicalJsonOpts);

        ResearchEvidenceBundleEntry CreateEntry(string logicalPath, byte[] bytes) =>
            new(logicalPath, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));

        List<ResearchEvidenceBundleEntry> entries = new()
        {
            CreateEntry(ResearchEvidenceBundleConstants.ExperimentRecordEntry, recordBytes),
            CreateEntry(ResearchEvidenceBundleConstants.HistoricalDatasetEntry, datasetBytes),
            CreateEntry(ResearchEvidenceBundleConstants.PortfolioArtifactEntry, artifactBytes),
            CreateEntry(ResearchEvidenceBundleConstants.StrategyParametersEntry, parameterBytes),
            CreateEntry(ResearchEvidenceBundleConstants.ReexecutionInputsEntry, reexecInputsBytes)
        };

        ResearchEvidenceBundleManifest manifest = ResearchEvidenceBundleManifest.Create(
            ResearchEvidenceBundleConstants.BundleVersionV1,
            record.ExperimentId,
            record.ExperimentFingerprint,
            dataset.Fingerprint,
            artifact.ArtifactFingerprint,
            record.ExecutionProvenanceBinding.BindingFingerprint,
            record.SourceBuildProvenance.ProvenanceFingerprint,
            record.ExecutionEnvironmentProvenance.ProvenanceFingerprint,
            entries);

        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, CanonicalJsonOpts);

        // 9. Atomic temporary zip creation and promotion
        string tempZipPath = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        DateTimeOffset fixedTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        try
        {
            using (FileStream fs = new(tempZipPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create, leaveOpen: true))
            {
                void WriteZipEntry(string name, byte[] content)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    entry.LastWriteTime = fixedTimestamp;
                    using Stream entryStream = entry.Open();
                    entryStream.Write(content, 0, content.Length);
                }

                // Write manifest
                WriteZipEntry(ResearchEvidenceBundleConstants.ManifestEntry, manifestBytes);

                // Write payload entries
                WriteZipEntry(ResearchEvidenceBundleConstants.ExperimentRecordEntry, recordBytes);
                WriteZipEntry(ResearchEvidenceBundleConstants.HistoricalDatasetEntry, datasetBytes);
                WriteZipEntry(ResearchEvidenceBundleConstants.PortfolioArtifactEntry, artifactBytes);
                WriteZipEntry(ResearchEvidenceBundleConstants.StrategyParametersEntry, parameterBytes);
                WriteZipEntry(ResearchEvidenceBundleConstants.ReexecutionInputsEntry, reexecInputsBytes);
            }

            // Verify temporary bundle before promotion
            ResearchEvidenceBundleVerificationResult verifyTemp = await _bundleVerifier.VerifyAsync(tempZipPath, reexecute: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (verifyTemp.Status != ResearchEvidenceBundleVerificationStatus.Verified)
                throw new InvalidDataException($"Temporary bundle verification failed: {string.Join(", ", verifyTemp.ReasonCodes)}");

            File.Move(tempZipPath, fullOutputPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                try { File.Delete(tempZipPath); }
                catch { /* Best effort cleanup of temporary file */ }
            }
        }

        return new ResearchEvidenceBundleExportResult(
            manifest.ExperimentId,
            manifest.BundleVersion,
            manifest.ExperimentFingerprint,
            manifest.DatasetFingerprint,
            manifest.ArtifactFingerprint,
            manifest.ExecutionBindingFingerprint,
            manifest.SourceBuildProvenanceFingerprint,
            manifest.ExecutionEnvironmentProvenanceFingerprint,
            manifest.BundleManifestFingerprint,
            fullOutputPath);
    }
}
