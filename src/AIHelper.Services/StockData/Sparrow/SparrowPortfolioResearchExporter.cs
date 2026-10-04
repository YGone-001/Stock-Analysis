using System.Text;
using System.Text.Json;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Writes canonical portfolio research evidence without recalculating simulation or performance facts.</summary>
public sealed class SparrowPortfolioResearchExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<SparrowPortfolioResearchArtifact> ExportAsync(
        SparrowPortfolioPerformanceResult result,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        SparrowPortfolioResearchArtifact artifact = new("AIHelper.HistoricalDataTool", result);
        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using FileStream stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, artifact, Options, cancellationToken);
        return artifact;
    }

    /// <summary>Canonical artifact JSON, used for in-memory validation before any file is published.</summary>
    public static string Serialize(SparrowPortfolioResearchArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return JsonSerializer.Serialize(artifact, Options);
    }

    /// <summary>
    /// Publishes a new artifact through a temp file, flush and non-overwriting promotion. The destination must not exist;
    /// authoritative managed evidence is never replaced.
    /// </summary>
    public async Task ExportNewAtomicAsync(string artifactJson, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (File.Exists(outputPath))
            throw new IOException($"Artifact output '{outputPath}' already exists; authoritative managed evidence is never overwritten.");

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(artifactJson), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
