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
    public static async Task<string> ExportLegacyReplayCapabilityJsonAsync(SparrowLegacyHistoricalReplayResult result, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        await ExportAsync(new LegacyReplayCapabilityJson(
            result.StrategyIdentity,
            result.StrategyVersion,
            result.TradingDate,
            result.DatasetId,
            result.DatasetFingerprint,
            result.Support.ToString(),
            result.BlockerReasonCodes,
            result.Selections,
            result.Warnings,
            result.Explanation), path, cancellationToken);
        return path;
    }
    private static async Task ExportAsync<T>(T result, string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); string? directory = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using FileStream stream = File.Create(path); await JsonSerializer.SerializeAsync(stream, result, Options, token);
    }

    private sealed record LegacyReplayCapabilityJson(
        string StrategyIdentity,
        string StrategyVersion,
        DateOnly TradingDate,
        string DatasetId,
        string DatasetFingerprint,
        string Support,
        IReadOnlyList<string> BlockerReasonCodes,
        IReadOnlyList<string> Selections,
        IReadOnlyList<string> Warnings,
        string Explanation);
}
