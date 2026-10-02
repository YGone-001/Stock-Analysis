using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Strict, deterministic, read-only reproducibility readiness verifier.</summary>
public sealed class SparrowResearchReproducibilityVerifier : IResearchReproducibilityVerifier
{
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ResearchReproducibilityVerificationResult> VerifyAsync(
        PersistedResearchExperimentRecord experiment,
        string artifactPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactPath);

        if (!File.Exists(artifactPath))
            throw new FileNotFoundException($"Portfolio research artifact file was not found: '{artifactPath}'.", artifactPath);

        string jsonContent = await File.ReadAllTextAsync(artifactPath, cancellationToken).ConfigureAwait(false);
        return VerifyContent(experiment, jsonContent);
    }

    public ResearchReproducibilityVerificationResult VerifyContent(
        PersistedResearchExperimentRecord experiment,
        string artifactJson)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ArgumentNullException.ThrowIfNull(artifactJson);

        List<ResearchReproducibilityCheck> checks = new();
        List<string> reasonCodes = new();

        // 1. EXPERIMENT_RECORD_VALID
        if (experiment.Definition is null || experiment.ExecutionSummary is null || experiment.ArtifactReference is null || experiment.Lineage is null)
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ExperimentRecordValid,
                ResearchReproducibilityCheckStatus.Fail,
                PersistedResearchExperimentRecord.CurrentSchemaVersion,
                experiment.SchemaVersion ?? "<null>",
                "Persisted experiment record components are null."));
            reasonCodes.Add(ResearchReproducibilityCheckCodes.ExperimentRecordValid);
            return new ResearchReproducibilityVerificationResult(
                ResearchReproducibilityVerificationStatus.Failed,
                checks,
                experiment.ExperimentId,
                experiment.ExperimentFingerprint,
                null,
                null,
                null,
                reasonCodes);
        }

        bool recordValid = (string.Equals(experiment.SchemaVersion, PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal)
            || string.Equals(experiment.SchemaVersion, PersistedResearchExperimentRecord.LegacySchemaVersion, StringComparison.Ordinal))
            && experiment.ExecutionSummary.Status == ResearchExperimentExecutionStatus.Completed
            && string.Equals(experiment.ExperimentId, experiment.Definition.Identity.ExperimentId, StringComparison.Ordinal);

        if (recordValid)
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ExperimentRecordValid,
                ResearchReproducibilityCheckStatus.Pass,
                $"{PersistedResearchExperimentRecord.CurrentSchemaVersion} or {PersistedResearchExperimentRecord.LegacySchemaVersion}",
                experiment.SchemaVersion,
                "Persisted experiment record schema and structure are valid."));
        }
        else
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ExperimentRecordValid,
                ResearchReproducibilityCheckStatus.Fail,
                PersistedResearchExperimentRecord.CurrentSchemaVersion,
                experiment.SchemaVersion,
                "Persisted experiment record schema or structure is invalid."));
            reasonCodes.Add(ResearchReproducibilityCheckCodes.ExperimentRecordValid);
        }

        // 2. EXPERIMENT_FINGERPRINT_VALID
        string expectedIdentity = ResearchExperimentFingerprint.Identity(
            experiment.Definition.Identity.ExperimentId,
            experiment.Definition.Identity.ExperimentVersion);
        string expectedDefinition = ResearchExperimentFingerprint.Definition(experiment.Definition);

        bool fingerprintValid = string.Equals(experiment.Definition.Identity.SemanticFingerprint, expectedIdentity, StringComparison.Ordinal)
            && string.Equals(experiment.Definition.SemanticFingerprint, expectedDefinition, StringComparison.Ordinal)
            && string.Equals(experiment.ExperimentFingerprint, experiment.Definition.SemanticFingerprint, StringComparison.Ordinal);

        if (fingerprintValid)
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ExperimentFingerprintValid,
                ResearchReproducibilityCheckStatus.Pass,
                expectedDefinition,
                experiment.ExperimentFingerprint,
                "Experiment fingerprint matches recomputed definition semantic fingerprint."));
        }
        else
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ExperimentFingerprintValid,
                ResearchReproducibilityCheckStatus.Fail,
                expectedDefinition,
                experiment.ExperimentFingerprint,
                "Experiment fingerprint does not match recomputed definition semantic fingerprint."));
            reasonCodes.Add(ResearchReproducibilityCheckCodes.ExperimentFingerprintValid);
        }

        // 3. PARAMETER_SNAPSHOT_VALID
        ExperimentParameterSnapshot snapshot = experiment.Definition.Parameters;
        string expectedStrategyParam = ResearchExperimentFingerprint.Parameters(snapshot.ParameterVersion, "strategy", snapshot.StrategyParameters);
        string expectedPortfolioParam = ResearchExperimentFingerprint.Parameters(snapshot.ParameterVersion, "portfolio", snapshot.PortfolioParameters);
        string expectedAnalysisParam = ResearchExperimentFingerprint.Parameters(snapshot.ParameterVersion, "analysis", snapshot.AnalysisParameters);
        string expectedSnapshotParam = ResearchExperimentFingerprint.ParameterSnapshot(
            snapshot.ParameterVersion, snapshot.StrategyParameters, snapshot.PortfolioParameters, snapshot.AnalysisParameters);

        bool paramValid = string.Equals(snapshot.StrategyFingerprint, expectedStrategyParam, StringComparison.Ordinal)
            && string.Equals(snapshot.PortfolioFingerprint, expectedPortfolioParam, StringComparison.Ordinal)
            && string.Equals(snapshot.AnalysisFingerprint, expectedAnalysisParam, StringComparison.Ordinal)
            && string.Equals(snapshot.Fingerprint, expectedSnapshotParam, StringComparison.Ordinal)
            && string.Equals(experiment.Definition.StrategyParameterFingerprint, snapshot.StrategyFingerprint, StringComparison.Ordinal);

        if (paramValid)
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ParameterSnapshotValid,
                ResearchReproducibilityCheckStatus.Pass,
                expectedSnapshotParam,
                snapshot.Fingerprint,
                "Experiment parameter snapshot fingerprints match frozen canonical parameter data."));
        }
        else
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ParameterSnapshotValid,
                ResearchReproducibilityCheckStatus.Fail,
                expectedSnapshotParam,
                snapshot.Fingerprint,
                "Experiment parameter snapshot fingerprints do not match frozen canonical parameter data."));
            reasonCodes.Add(ResearchReproducibilityCheckCodes.ParameterSnapshotValid);
        }

        // 4. LINEAGE_VALID
        ResearchArtifactLineage lineage = experiment.Lineage;
        bool lineageValid = string.Equals(experiment.ExperimentFingerprint, lineage.ExperimentFingerprint, StringComparison.Ordinal)
            && string.Equals(experiment.Definition.DatasetFingerprint, lineage.DatasetFingerprint, StringComparison.Ordinal)
            && string.Equals(experiment.Definition.Parameters.Fingerprint, lineage.ParameterFingerprint, StringComparison.Ordinal)
            && string.Equals(experiment.ExecutionSummary.ArtifactFingerprint, lineage.ArtifactFingerprint, StringComparison.Ordinal)
            && string.Equals(experiment.ArtifactReference.ArtifactFingerprint, lineage.ArtifactFingerprint, StringComparison.Ordinal);

        if (lineageValid)
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.LineageValid,
                ResearchReproducibilityCheckStatus.Pass,
                lineage.ArtifactFingerprint,
                lineage.ArtifactFingerprint,
                "Experiment lineage internally links definition, execution, and artifact reference."));
        }
        else
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.LineageValid,
                ResearchReproducibilityCheckStatus.Fail,
                lineage.ArtifactFingerprint,
                "<inconsistent>",
                "Experiment lineage is inconsistent with experiment definition or execution."));
            reasonCodes.Add(ResearchReproducibilityCheckCodes.LineageValid);
        }

        // 5. ARTIFACT_JSON_VALID
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(artifactJson);
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ArtifactJsonValid,
                ResearchReproducibilityCheckStatus.Pass,
                "Valid JSON document",
                "Valid JSON document",
                "Artifact payload is well-formed JSON."));
        }
        catch (JsonException ex)
        {
            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ArtifactJsonValid,
                ResearchReproducibilityCheckStatus.Fail,
                "Valid JSON document",
                "Invalid JSON document",
                $"Artifact JSON is invalid: {ex.Message}"));
            reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactJsonValid);
            return new ResearchReproducibilityVerificationResult(
                ResearchReproducibilityVerificationStatus.Failed,
                checks,
                experiment.ExperimentId,
                experiment.ExperimentFingerprint,
                experiment.Definition.DatasetFingerprint,
                null,
                null,
                reasonCodes);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            string? rawArtifactVersion = root.TryGetProperty("artifactVersion", out JsonElement verProp) && verProp.ValueKind == JsonValueKind.String
                ? verProp.GetString() : null;

            // 6. ARTIFACT_VERSION_SUPPORTED
            if (string.Equals(rawArtifactVersion, SparrowPortfolioResearchArtifact.LegacyArtifactVersion, StringComparison.Ordinal))
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactVersionSupported,
                    ResearchReproducibilityCheckStatus.Unsupported,
                    SparrowPortfolioResearchArtifact.CurrentArtifactVersion,
                    rawArtifactVersion,
                    $"{ResearchReproducibilityCheckCodes.ArtifactIdentityUnavailableV1}: Legacy artifact version 'portfolio-research-v1' has no authoritative artifact fingerprint."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactIdentityUnavailableV1);
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactVersionSupported);
                return new ResearchReproducibilityVerificationResult(
                    ResearchReproducibilityVerificationStatus.Unsupported,
                    checks,
                    experiment.ExperimentId,
                    experiment.ExperimentFingerprint,
                    experiment.Definition.DatasetFingerprint,
                    rawArtifactVersion,
                    null,
                    reasonCodes);
            }
            else if (!string.Equals(rawArtifactVersion, SparrowPortfolioResearchArtifact.CurrentArtifactVersion, StringComparison.Ordinal))
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactVersionSupported,
                    ResearchReproducibilityCheckStatus.Unsupported,
                    SparrowPortfolioResearchArtifact.CurrentArtifactVersion,
                    rawArtifactVersion ?? "<null>",
                    $"{ResearchReproducibilityCheckCodes.ArtifactVersionUnsupported}: Unsupported artifact schema version '{rawArtifactVersion ?? "<null>"}'."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactVersionUnsupported);
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactVersionSupported);
                return new ResearchReproducibilityVerificationResult(
                    ResearchReproducibilityVerificationStatus.Unsupported,
                    checks,
                    experiment.ExperimentId,
                    experiment.ExperimentFingerprint,
                    experiment.Definition.DatasetFingerprint,
                    rawArtifactVersion,
                    null,
                    reasonCodes);
            }

            checks.Add(new ResearchReproducibilityCheck(
                ResearchReproducibilityCheckCodes.ArtifactVersionSupported,
                ResearchReproducibilityCheckStatus.Pass,
                SparrowPortfolioResearchArtifact.CurrentArtifactVersion,
                rawArtifactVersion,
                "Artifact version 'portfolio-research-v2' is supported."));

            // 7. ARTIFACT_FINGERPRINT_PRESENT
            string? rawArtifactFingerprint = root.TryGetProperty("artifactFingerprint", out JsonElement fpProp) && fpProp.ValueKind == JsonValueKind.String
                ? fpProp.GetString() : null;
            bool fpPresent = !string.IsNullOrWhiteSpace(rawArtifactFingerprint);

            if (fpPresent)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactFingerprintPresent,
                    ResearchReproducibilityCheckStatus.Pass,
                    "Present",
                    rawArtifactFingerprint,
                    "Artifact fingerprint is present in artifact payload."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactFingerprintPresent,
                    ResearchReproducibilityCheckStatus.Fail,
                    "Present",
                    "<empty>",
                    "Artifact fingerprint is missing or empty in artifact payload."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactFingerprintPresent);
            }

            // Deserialize V2 artifact
            SparrowPortfolioResearchArtifact? artifact;
            try
            {
                artifact = JsonSerializer.Deserialize<SparrowPortfolioResearchArtifact>(artifactJson, ArtifactJsonOptions);
            }
            catch (Exception ex)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactFingerprintValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    "Valid portfolio-research-v2 payload",
                    "Deserialization failed",
                    $"Artifact payload structure cannot be parsed as portfolio-research-v2: {ex.Message}"));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
                return new ResearchReproducibilityVerificationResult(
                    ResearchReproducibilityVerificationStatus.Failed,
                    checks,
                    experiment.ExperimentId,
                    experiment.ExperimentFingerprint,
                    experiment.Definition.DatasetFingerprint,
                    rawArtifactVersion,
                    rawArtifactFingerprint,
                    reasonCodes);
            }

            if (artifact is null)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactFingerprintValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    "Non-null artifact payload",
                    "<null>",
                    "Artifact payload deserialized to null."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
                return new ResearchReproducibilityVerificationResult(
                    ResearchReproducibilityVerificationStatus.Failed,
                    checks,
                    experiment.ExperimentId,
                    experiment.ExperimentFingerprint,
                    experiment.Definition.DatasetFingerprint,
                    rawArtifactVersion,
                    rawArtifactFingerprint,
                    reasonCodes);
            }

            // 8. ARTIFACT_FINGERPRINT_VALID
            string recomputedArtifactFingerprint = SparrowPortfolioResearchFingerprint.Artifact(
                artifact.ArtifactVersion,
                artifact.CreatedBy,
                artifact.DatasetFingerprint,
                artifact.StrategyFingerprint,
                artifact.PortfolioConfigurationFingerprint,
                artifact.AnalysisFingerprint,
                artifact.Strategy,
                artifact.PortfolioRequest,
                artifact.SimulationSummary,
                artifact.PerformanceSummary,
                artifact.BenchmarkSummary,
                artifact.Trades,
                artifact.Positions,
                artifact.EquityCurve,
                artifact.Attribution,
                artifact.Warnings,
                artifact.Limitations);

            bool artifactFpValid = fpPresent && string.Equals(artifact.ArtifactFingerprint, recomputedArtifactFingerprint, StringComparison.Ordinal);
            if (artifactFpValid)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactFingerprintValid,
                    ResearchReproducibilityCheckStatus.Pass,
                    artifact.ArtifactFingerprint,
                    recomputedArtifactFingerprint,
                    "Artifact fingerprint matches authoritative recomputed payload identity."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactFingerprintValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    artifact.ArtifactFingerprint ?? "<missing>",
                    recomputedArtifactFingerprint,
                    "Artifact payload fingerprint mismatch (tampering detected)."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactFingerprintValid);
            }

            // 9. DATASET_FINGERPRINT_MATCH
            bool datasetMatch = string.Equals(experiment.Definition.DatasetFingerprint, artifact.DatasetFingerprint, StringComparison.Ordinal)
                && string.Equals(experiment.Lineage.DatasetFingerprint, artifact.DatasetFingerprint, StringComparison.Ordinal);

            if (datasetMatch)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.DatasetFingerprintMatch,
                    ResearchReproducibilityCheckStatus.Pass,
                    experiment.Definition.DatasetFingerprint,
                    artifact.DatasetFingerprint,
                    "Experiment record and research artifact share the same dataset fingerprint."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.DatasetFingerprintMatch,
                    ResearchReproducibilityCheckStatus.Fail,
                    experiment.Definition.DatasetFingerprint,
                    artifact.DatasetFingerprint,
                    "Dataset fingerprint mismatch between experiment record and artifact."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.DatasetFingerprintMatch);
            }

            // 10. ARTIFACT_REFERENCE_VERSION_MATCH
            bool versionMatch = string.Equals(experiment.ArtifactReference.ArtifactVersion, artifact.ArtifactVersion, StringComparison.Ordinal);
            if (versionMatch)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactReferenceVersionMatch,
                    ResearchReproducibilityCheckStatus.Pass,
                    experiment.ArtifactReference.ArtifactVersion,
                    artifact.ArtifactVersion,
                    "Experiment artifact reference version matches artifact version."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactReferenceVersionMatch,
                    ResearchReproducibilityCheckStatus.Fail,
                    experiment.ArtifactReference.ArtifactVersion,
                    artifact.ArtifactVersion,
                    "Artifact reference version mismatch between experiment record and artifact."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactReferenceVersionMatch);
            }

            // 11. ARTIFACT_REFERENCE_FINGERPRINT_MATCH
            bool referenceFpMatch = string.Equals(experiment.ArtifactReference.ArtifactFingerprint, artifact.ArtifactFingerprint, StringComparison.Ordinal)
                && string.Equals(experiment.ExecutionSummary.ArtifactFingerprint, artifact.ArtifactFingerprint, StringComparison.Ordinal)
                && string.Equals(experiment.Lineage.ArtifactFingerprint, artifact.ArtifactFingerprint, StringComparison.Ordinal);

            if (referenceFpMatch)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactReferenceFingerprintMatch,
                    ResearchReproducibilityCheckStatus.Pass,
                    experiment.ArtifactReference.ArtifactFingerprint,
                    artifact.ArtifactFingerprint,
                    "Experiment execution, reference, and lineage artifact fingerprints match artifact."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ArtifactReferenceFingerprintMatch,
                    ResearchReproducibilityCheckStatus.Fail,
                    experiment.ArtifactReference.ArtifactFingerprint,
                    artifact.ArtifactFingerprint,
                    "Artifact fingerprint mismatch between experiment record and artifact."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.ArtifactReferenceFingerprintMatch);
            }

            // 12. STRATEGY_IDENTITY_VALID
            bool strategyIdValid = artifact.Strategy != null
                && artifact.PortfolioRequest != null
                && string.Equals(artifact.Strategy.StrategyFingerprint, artifact.StrategyFingerprint, StringComparison.Ordinal)
                && string.Equals(artifact.Strategy.Mode, artifact.PortfolioRequest.StrategyMode.ToString(), StringComparison.Ordinal)
                && string.Equals(artifact.Strategy.Version, artifact.PortfolioRequest.StrategyVersion, StringComparison.Ordinal);

            if (strategyIdValid)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyIdentityValid,
                    ResearchReproducibilityCheckStatus.Pass,
                    artifact.StrategyFingerprint,
                    artifact.Strategy!.StrategyFingerprint,
                    "Artifact strategy structure and request identity are consistent."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyIdentityValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    artifact.StrategyFingerprint,
                    artifact.Strategy?.StrategyFingerprint ?? "<null>",
                    "Artifact internal strategy identity is inconsistent."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.StrategyIdentityValid);
            }

            // 13. STRATEGY_MODE_MATCH
            bool modeMatch = artifact.Strategy != null && string.Equals(experiment.Definition.StrategyIdentity.Mode, artifact.Strategy.Mode, StringComparison.Ordinal);
            if (modeMatch)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyModeMatch,
                    ResearchReproducibilityCheckStatus.Pass,
                    experiment.Definition.StrategyIdentity.Mode,
                    artifact.Strategy!.Mode,
                    "Strategy mode matches between experiment record and artifact."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyModeMatch,
                    ResearchReproducibilityCheckStatus.Fail,
                    experiment.Definition.StrategyIdentity.Mode,
                    artifact.Strategy?.Mode ?? "<null>",
                    "Strategy mode mismatch between experiment record and artifact."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.StrategyModeMatch);
            }

            // 14. STRATEGY_VERSION_MATCH
            bool stratVerMatch = artifact.Strategy != null && string.Equals(experiment.Definition.StrategyIdentity.Version, artifact.Strategy.Version, StringComparison.Ordinal);
            if (stratVerMatch)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyVersionMatch,
                    ResearchReproducibilityCheckStatus.Pass,
                    experiment.Definition.StrategyIdentity.Version,
                    artifact.Strategy!.Version,
                    "Strategy version matches between experiment record and artifact."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyVersionMatch,
                    ResearchReproducibilityCheckStatus.Fail,
                    experiment.Definition.StrategyIdentity.Version,
                    artifact.Strategy?.Version ?? "<null>",
                    "Strategy version mismatch between experiment record and artifact."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.StrategyVersionMatch);
            }

            // 15. STRATEGY_FINGERPRINT_VALID
            string recomputedStrategyFingerprint = artifact.PortfolioRequest != null
                ? SparrowPortfolioResearchFingerprint.Strategy(artifact.PortfolioRequest)
                : "<null>";
            bool strategyFpValid = string.Equals(artifact.StrategyFingerprint, recomputedStrategyFingerprint, StringComparison.Ordinal);

            if (strategyFpValid)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyFingerprintValid,
                    ResearchReproducibilityCheckStatus.Pass,
                    artifact.StrategyFingerprint,
                    recomputedStrategyFingerprint,
                    "Authoritative strategy fingerprint recomputation verified."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.StrategyFingerprintValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    artifact.StrategyFingerprint,
                    recomputedStrategyFingerprint,
                    "Strategy fingerprint mismatch upon recomputation."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.StrategyFingerprintValid);
            }

            // 16. PORTFOLIO_CONFIGURATION_FINGERPRINT_VALID
            string recomputedPortfolioFingerprint = artifact.PortfolioRequest != null
                ? SparrowPortfolioResearchFingerprint.Portfolio(artifact.PortfolioRequest)
                : "<null>";
            bool portfolioFpValid = string.Equals(artifact.PortfolioConfigurationFingerprint, recomputedPortfolioFingerprint, StringComparison.Ordinal);

            if (portfolioFpValid)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintValid,
                    ResearchReproducibilityCheckStatus.Pass,
                    artifact.PortfolioConfigurationFingerprint,
                    recomputedPortfolioFingerprint,
                    "Authoritative portfolio configuration fingerprint recomputation verified."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    artifact.PortfolioConfigurationFingerprint,
                    recomputedPortfolioFingerprint,
                    "Portfolio configuration fingerprint mismatch upon recomputation."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintValid);
            }

            // 17. PORTFOLIO_CONFIGURATION_FINGERPRINT_MATCH
            bool portfolioMatch = string.Equals(experiment.Definition.PortfolioConfigurationFingerprint, artifact.PortfolioConfigurationFingerprint, StringComparison.Ordinal);
            if (portfolioMatch)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintMatch,
                    ResearchReproducibilityCheckStatus.Pass,
                    experiment.Definition.PortfolioConfigurationFingerprint,
                    artifact.PortfolioConfigurationFingerprint,
                    "Portfolio configuration fingerprint matches between experiment record and artifact."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintMatch,
                    ResearchReproducibilityCheckStatus.Fail,
                    experiment.Definition.PortfolioConfigurationFingerprint,
                    artifact.PortfolioConfigurationFingerprint,
                    "Portfolio configuration fingerprint mismatch between experiment record and artifact."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.PortfolioConfigurationFingerprintMatch);
            }

            // 18. ANALYSIS_FINGERPRINT_VALID
            string recomputedAnalysisFingerprint = SparrowPortfolioResearchFingerprint.Analysis(
                artifact.DatasetFingerprint,
                artifact.StrategyFingerprint,
                artifact.PortfolioConfigurationFingerprint);
            bool analysisFpValid = string.Equals(artifact.AnalysisFingerprint, recomputedAnalysisFingerprint, StringComparison.Ordinal);

            if (analysisFpValid)
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.AnalysisFingerprintValid,
                    ResearchReproducibilityCheckStatus.Pass,
                    artifact.AnalysisFingerprint,
                    recomputedAnalysisFingerprint,
                    "Authoritative analysis fingerprint recomputation verified."));
            }
            else
            {
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.AnalysisFingerprintValid,
                    ResearchReproducibilityCheckStatus.Fail,
                    artifact.AnalysisFingerprint,
                    recomputedAnalysisFingerprint,
                    "Analysis fingerprint mismatch upon recomputation."));
                reasonCodes.Add(ResearchReproducibilityCheckCodes.AnalysisFingerprintValid);
            }

            // 19. EXECUTION_PROVENANCE_BINDING_PRESENT
            if (experiment.ExecutionProvenanceBinding is null)
            {
                if (string.Equals(experiment.SchemaVersion, PersistedResearchExperimentRecord.LegacySchemaVersion, StringComparison.Ordinal))
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent,
                        ResearchReproducibilityCheckStatus.Unsupported,
                        ResearchExecutionProvenanceBinding.CurrentBindingVersion,
                        "<null>",
                        $"{ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingUnavailableV1}: Legacy experiment record version 'research-experiment-record-v1' has no authoritative execution provenance binding."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingUnavailableV1);
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent);
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent,
                        ResearchReproducibilityCheckStatus.Fail,
                        ResearchExecutionProvenanceBinding.CurrentBindingVersion,
                        "<null>",
                        "Execution provenance binding is missing in V2 experiment record."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent);
                }
            }
            else
            {
                ResearchExecutionProvenanceBinding binding = experiment.ExecutionProvenanceBinding;
                checks.Add(new ResearchReproducibilityCheck(
                    ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingPresent,
                    ResearchReproducibilityCheckStatus.Pass,
                    ResearchExecutionProvenanceBinding.CurrentBindingVersion,
                    binding.BindingVersion,
                    "Execution provenance binding is present in experiment record."));

                // 20. EXECUTION_PROVENANCE_BINDING_VALID
                string recomputedBindingFp = binding.ComputeFingerprint();
                bool bindingValid = string.Equals(binding.BindingVersion, ResearchExecutionProvenanceBinding.CurrentBindingVersion, StringComparison.Ordinal)
                    && string.Equals(binding.BindingFingerprint, recomputedBindingFp, StringComparison.Ordinal);

                if (bindingValid)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingValid,
                        ResearchReproducibilityCheckStatus.Pass,
                        binding.BindingFingerprint,
                        recomputedBindingFp,
                        "Authoritative execution provenance binding fingerprint recomputation verified."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingValid,
                        ResearchReproducibilityCheckStatus.Fail,
                        binding.BindingFingerprint,
                        recomputedBindingFp,
                        "Execution provenance binding fingerprint mismatch or unsupported version (tampering detected)."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceBindingValid);
                }

                // 21. EXECUTION_PROVENANCE_EXPERIMENT_MATCH
                bool expMatch = string.Equals(binding.ExperimentFingerprint, experiment.ExperimentFingerprint, StringComparison.Ordinal)
                    && string.Equals(binding.ExperimentFingerprint, experiment.Definition.SemanticFingerprint, StringComparison.Ordinal);
                if (expMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceExperimentMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        experiment.ExperimentFingerprint,
                        binding.ExperimentFingerprint,
                        "Binding experiment fingerprint matches experiment record."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceExperimentMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        experiment.ExperimentFingerprint,
                        binding.ExperimentFingerprint,
                        "Binding experiment fingerprint mismatch."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceExperimentMatch);
                }

                // 22. EXECUTION_PROVENANCE_PARAMETER_MATCH
                bool paramMatch = string.Equals(binding.ParameterSnapshotFingerprint, experiment.Definition.Parameters.Fingerprint, StringComparison.Ordinal);
                if (paramMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceParameterMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        experiment.Definition.Parameters.Fingerprint,
                        binding.ParameterSnapshotFingerprint,
                        "Binding parameter snapshot fingerprint matches experiment definition."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceParameterMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        experiment.Definition.Parameters.Fingerprint,
                        binding.ParameterSnapshotFingerprint,
                        "Binding parameter snapshot fingerprint mismatch."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceParameterMatch);
                }

                // 23. EXECUTION_PROVENANCE_STRATEGY_PARAMETER_MATCH
                bool stratParamMatch = string.Equals(binding.ExperimentStrategyParameterFingerprint, experiment.Definition.StrategyParameterFingerprint, StringComparison.Ordinal)
                    && artifact.PortfolioRequest is not null
                    && string.Equals(binding.ArtifactStrategyParameterFingerprint, artifact.PortfolioRequest.StrategyParameterFingerprint, StringComparison.Ordinal);
                if (stratParamMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceStrategyParameterMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        $"{experiment.Definition.StrategyParameterFingerprint}:{artifact.PortfolioRequest?.StrategyParameterFingerprint}",
                        $"{binding.ExperimentStrategyParameterFingerprint}:{binding.ArtifactStrategyParameterFingerprint}",
                        "Binding strategy parameter fingerprints match experiment definition and artifact request."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceStrategyParameterMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        $"{experiment.Definition.StrategyParameterFingerprint}:{artifact.PortfolioRequest?.StrategyParameterFingerprint ?? "<null>"}",
                        $"{binding.ExperimentStrategyParameterFingerprint}:{binding.ArtifactStrategyParameterFingerprint}",
                        "Binding strategy parameter fingerprint mismatch between experiment and artifact."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceStrategyParameterMatch);
                }

                // 24. EXECUTION_PROVENANCE_PORTFOLIO_MATCH
                bool portMatch = string.Equals(binding.ExperimentPortfolioConfigurationFingerprint, experiment.Definition.PortfolioConfigurationFingerprint, StringComparison.Ordinal)
                    && string.Equals(binding.ArtifactPortfolioConfigurationFingerprint, artifact.PortfolioConfigurationFingerprint, StringComparison.Ordinal);
                if (portMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenancePortfolioMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        experiment.Definition.PortfolioConfigurationFingerprint,
                        binding.ExperimentPortfolioConfigurationFingerprint,
                        "Binding portfolio configuration fingerprints match experiment definition and artifact."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenancePortfolioMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        experiment.Definition.PortfolioConfigurationFingerprint,
                        $"{binding.ExperimentPortfolioConfigurationFingerprint}:{binding.ArtifactPortfolioConfigurationFingerprint}",
                        "Binding portfolio configuration fingerprint mismatch."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenancePortfolioMatch);
                }

                // 25. EXECUTION_PROVENANCE_ANALYSIS_MATCH
                bool analysisMatch = string.Equals(binding.ExperimentAnalysisConfigurationFingerprint, experiment.Definition.AnalysisConfigurationFingerprint, StringComparison.Ordinal)
                    && string.Equals(binding.ArtifactAnalysisFingerprint, artifact.AnalysisFingerprint, StringComparison.Ordinal);
                if (analysisMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceAnalysisMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        $"{experiment.Definition.AnalysisConfigurationFingerprint}:{artifact.AnalysisFingerprint}",
                        $"{binding.ExperimentAnalysisConfigurationFingerprint}:{binding.ArtifactAnalysisFingerprint}",
                        "Binding analysis fingerprints match experiment definition and artifact analysis."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceAnalysisMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        $"{experiment.Definition.AnalysisConfigurationFingerprint}:{artifact.AnalysisFingerprint}",
                        $"{binding.ExperimentAnalysisConfigurationFingerprint}:{binding.ArtifactAnalysisFingerprint}",
                        "Binding analysis fingerprint mismatch between experiment definition and artifact."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceAnalysisMatch);
                }

                // 26. EXECUTION_PROVENANCE_DATASET_MATCH
                bool dsMatch = string.Equals(binding.DatasetFingerprint, experiment.Definition.DatasetFingerprint, StringComparison.Ordinal)
                    && string.Equals(binding.DatasetFingerprint, artifact.DatasetFingerprint, StringComparison.Ordinal);
                if (dsMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceDatasetMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        experiment.Definition.DatasetFingerprint,
                        binding.DatasetFingerprint,
                        "Binding dataset fingerprint matches experiment definition and artifact."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceDatasetMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        experiment.Definition.DatasetFingerprint,
                        binding.DatasetFingerprint,
                        "Binding dataset fingerprint mismatch."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceDatasetMatch);
                }

                // 27. EXECUTION_PROVENANCE_ARTIFACT_MATCH
                bool artMatch = string.Equals(binding.ArtifactVersion, artifact.ArtifactVersion, StringComparison.Ordinal)
                    && string.Equals(binding.ArtifactFingerprint, artifact.ArtifactFingerprint, StringComparison.Ordinal)
                    && string.Equals(binding.ArtifactFingerprint, experiment.ExecutionSummary.ArtifactFingerprint, StringComparison.Ordinal)
                    && string.Equals(binding.ArtifactFingerprint, experiment.ArtifactReference.ArtifactFingerprint, StringComparison.Ordinal);
                if (artMatch)
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceArtifactMatch,
                        ResearchReproducibilityCheckStatus.Pass,
                        artifact.ArtifactFingerprint,
                        binding.ArtifactFingerprint,
                        "Binding artifact identity and version match artifact and experiment record."));
                }
                else
                {
                    checks.Add(new ResearchReproducibilityCheck(
                        ResearchReproducibilityCheckCodes.ExecutionProvenanceArtifactMatch,
                        ResearchReproducibilityCheckStatus.Fail,
                        artifact.ArtifactFingerprint,
                        binding.ArtifactFingerprint,
                        "Binding artifact fingerprint or version mismatch."));
                    reasonCodes.Add(ResearchReproducibilityCheckCodes.ExecutionProvenanceArtifactMatch);
                }
            }

            ResearchReproducibilityVerificationStatus status = checks.Any(c => c.Status == ResearchReproducibilityCheckStatus.Fail)
                ? ResearchReproducibilityVerificationStatus.Failed
                : checks.Any(c => c.Status == ResearchReproducibilityCheckStatus.Unsupported)
                    ? ResearchReproducibilityVerificationStatus.Unsupported
                    : ResearchReproducibilityVerificationStatus.Verified;

            return new ResearchReproducibilityVerificationResult(
                status,
                checks,
                experiment.ExperimentId,
                experiment.ExperimentFingerprint,
                artifact.DatasetFingerprint,
                artifact.ArtifactVersion,
                artifact.ArtifactFingerprint,
                reasonCodes);
        }
    }
}
