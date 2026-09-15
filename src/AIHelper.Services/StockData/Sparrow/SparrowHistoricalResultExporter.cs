using System.Text.Json;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

public static class SparrowHistoricalResultExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static async Task<string> ExportReplayJsonAsync(SparrowReplayResult result, string path, CancellationToken cancellationToken = default)
    { await ExportAsync(result, path, cancellationToken); return path; }
    public static async Task<string> ExportBacktestJsonAsync(SparrowBacktestResult result, string path, CancellationToken cancellationToken = default)
    { await ExportAsync(result, path, cancellationToken); return path; }
    private static async Task ExportAsync<T>(T result, string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); string? directory = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using FileStream stream = File.Create(path); await JsonSerializer.SerializeAsync(stream, result, Options, token);
    }
}
