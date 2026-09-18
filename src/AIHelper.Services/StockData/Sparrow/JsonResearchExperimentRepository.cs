using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Local UTF-8 JSON repository. Each record is verified on both save and load before it is exposed.</summary>
public sealed class JsonResearchExperimentRepository : IResearchExperimentRepository
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _rootDirectory;
    private readonly Func<string, FileStream> _createTemporaryFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonResearchExperimentRepository(string rootDirectory, Func<string, FileStream>? createTemporaryFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _createTemporaryFile = createTemporaryFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough));
    }

    public async Task SaveAsync(PersistedResearchExperimentRecord experiment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(experiment);
        ValidateRecord(experiment);
        string path = PathFor(experiment.ExperimentId);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            if (File.Exists(path)) throw new InvalidOperationException($"Experiment '{experiment.ExperimentId}' already exists and cannot be overwritten.");
            try
            {
                await using (FileStream stream = _createTemporaryFile(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(stream, experiment, Json, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                PersistedResearchExperimentRecord verified = await LoadPathAsync(temporaryPath, cancellationToken, validateFileName: false).ConfigureAwait(false);
                if (!string.Equals(verified.ExperimentFingerprint, experiment.ExperimentFingerprint, StringComparison.Ordinal))
                    throw new InvalidDataException("Temporary experiment record fingerprint verification failed.");
                File.Move(temporaryPath, path, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<PersistedResearchExperimentRecord> GetAsync(string experimentId, CancellationToken cancellationToken = default) =>
        LoadPathAsync(PathFor(experimentId), cancellationToken);

    public async Task<ResearchExperimentHistory> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_rootDirectory)) return new ResearchExperimentHistory(Array.Empty<PersistedResearchExperimentRecord>());
        string[] paths = Directory.EnumerateFiles(_rootDirectory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        List<PersistedResearchExperimentRecord> records = new(paths.Length);
        foreach (string path in paths) records.Add(await LoadPathAsync(path, cancellationToken).ConfigureAwait(false));
        return new ResearchExperimentHistory(records);
    }

    public async Task DeleteAsync(string experimentId, CancellationToken cancellationToken = default)
    {
        string path = PathFor(experimentId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("Research experiment record was not found.", path);
            File.Delete(path);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PersistedResearchExperimentRecord> LoadPathAsync(string path, CancellationToken cancellationToken, bool validateFileName = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Research experiment record was not found.", path);
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan);
            PersistedResearchExperimentRecord? record = await JsonSerializer.DeserializeAsync<PersistedResearchExperimentRecord>(stream, Json, cancellationToken).ConfigureAwait(false);
            if (record is null) throw new InvalidDataException("Research experiment record is empty.");
            ValidateRecord(record);
            if (validateFileName && !string.Equals(Path.GetFileNameWithoutExtension(path), record.ExperimentId, StringComparison.Ordinal))
                throw new InvalidDataException("Research experiment filename does not match ExperimentId.");
            return record;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Research experiment record JSON is invalid.", exception);
        }
    }

    private static void ValidateRecord(PersistedResearchExperimentRecord record)
    {
        if (!string.Equals(record.SchemaVersion, PersistedResearchExperimentRecord.CurrentSchemaVersion, StringComparison.Ordinal))
            throw new NotSupportedException($"Research experiment schema '{record.SchemaVersion}' is unsupported.");
        if (!string.Equals(record.ExperimentFingerprint, record.Definition.SemanticFingerprint, StringComparison.Ordinal)
            || !string.Equals(record.ExperimentFingerprint, record.Lineage.ExperimentFingerprint, StringComparison.Ordinal)
            || !string.Equals(record.Definition.DatasetFingerprint, record.Lineage.DatasetFingerprint, StringComparison.Ordinal)
            || !string.Equals(record.Definition.Parameters.Fingerprint, record.Lineage.ParameterFingerprint, StringComparison.Ordinal)
            || !string.Equals(record.ExecutionSummary.ArtifactFingerprint, record.Lineage.ArtifactFingerprint, StringComparison.Ordinal)
            || !string.Equals(record.ArtifactReference.ArtifactFingerprint, record.Lineage.ArtifactFingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("Research experiment record fingerprint or lineage validation failed.");
    }

    private string PathFor(string experimentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);
        if (!string.Equals(Path.GetFileName(experimentId), experimentId, StringComparison.Ordinal)
            || experimentId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("ExperimentId must be a safe local filename.", nameof(experimentId));
        return Path.Combine(_rootDirectory, experimentId + ".json");
    }
}
