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
}
