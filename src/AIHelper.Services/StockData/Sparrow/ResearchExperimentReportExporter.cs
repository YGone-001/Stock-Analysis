using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Writes deterministic Markdown or JSON report presentation artifacts using create-new atomic promotion.</summary>
public sealed class ResearchExperimentReportExporter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() }
    };
    private readonly IResearchExperimentReportRenderer _renderer;
    private readonly Func<string, FileStream> _createTemporaryFile;

    public ResearchExperimentReportExporter(IResearchExperimentReportRenderer renderer, Func<string, FileStream>? createTemporaryFile = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _createTemporaryFile = createTemporaryFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough));
    }

    public string SerializeJson(IResearchExperimentReportDocument report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, report.GetType(), Json);
    }

    public Task ExportMarkdownAsync(ResearchExperimentReport report, string outputPath, CancellationToken cancellationToken = default) => ExportAsync(_renderer.Render(report), outputPath, ".md", cancellationToken);
    public Task ExportMarkdownAsync(ResearchExperimentLineageReport report, string outputPath, CancellationToken cancellationToken = default) => ExportAsync(_renderer.Render(report), outputPath, ".md", cancellationToken);
    public Task ExportMarkdownAsync(ResearchExperimentComparisonReport report, string outputPath, CancellationToken cancellationToken = default) => ExportAsync(_renderer.Render(report), outputPath, ".md", cancellationToken);
    public Task ExportJsonAsync(IResearchExperimentReportDocument report, string outputPath, CancellationToken cancellationToken = default) => ExportAsync(SerializeJson(report), outputPath, ".json", cancellationToken);

    private async Task ExportAsync(string content, string outputPath, string extension, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string path = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"Report output must use the '{extension}' extension.", nameof(outputPath));
        if (File.Exists(path)) throw new InvalidOperationException($"Report output '{path}' already exists and cannot be overwritten.");
        string? directory = Path.GetDirectoryName(path); if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Report output directory is invalid.", nameof(outputPath));
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await using (FileStream stream = _createTemporaryFile(temporaryPath))
            await using (StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 8192, leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
