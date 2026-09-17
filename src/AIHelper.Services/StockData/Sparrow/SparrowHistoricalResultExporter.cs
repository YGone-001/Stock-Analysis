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
    public static async Task<string> ExportBenchmarkAnalysisJsonAsync(SparrowBenchmarkAnalysisResult result, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var payload = new BenchmarkAnalysisJson(
            result.Request.BacktestRequest.StrategyMode.ToString(), result.Request.BacktestRequest.StrategyVersion,
            result.DatasetId, result.DatasetFingerprint, result.AnalysisFingerprint, result.Request.BenchmarkId,
            result.BenchmarkProvenance, result.Request.BacktestRequest.StartDate, result.Request.BacktestRequest.EndDate,
            result.Request.BacktestRequest.Horizons.Distinct().Order().ToArray(), result.Support.ToString(),
            result.StockPriceAdjustmentMode.ToString(), SparrowBenchmarkAnalysisResult.StockReturnBasis,
            SparrowBenchmarkAnalysisResult.BenchmarkReturnBasis, SparrowBenchmarkAnalysisResult.ExcessReturnFormula,
            SparrowBenchmarkAnalysisResult.WeightingMethod,
            result.RelativeSelections.OrderBy(item => item.Selection.ReplayDate).ThenBy(item => item.Selection.Selection.RankedCandidate.Rank).ThenBy(item => item.Selection.Selection.Code, StringComparer.Ordinal).ToArray(),
            result.HorizonMetrics.OrderBy(item => item.HorizonTradingDays).ToArray(),
            result.UnavailableReasonCounts.OrderBy(item => item.Key, StringComparer.Ordinal).ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            result.Warnings.OrderBy(item => item, StringComparer.Ordinal).ToArray());
        await ExportAsync(payload, path, cancellationToken);
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
    private sealed record BenchmarkAnalysisJson(
        string Strategy,
        string StrategyVersion,
        string DatasetId,
        string DatasetFingerprint,
        string AnalysisFingerprint,
        string BenchmarkId,
        HistoricalBenchmarkProvenance? BenchmarkProvenance,
        DateOnly StartDate,
        DateOnly EndDate,
        IReadOnlyList<int> Horizons,
        string Support,
        string StockPriceAdjustmentMode,
        string StockReturnBasis,
        string BenchmarkReturnBasis,
        string ExcessReturnFormula,
        string WeightingMethod,
        IReadOnlyList<SparrowBenchmarkRelativeSelection> RelativeSelections,
        IReadOnlyList<SparrowBenchmarkHorizonMetrics> HorizonMetrics,
        IReadOnlyDictionary<string, int> UnavailableReasonCounts,
        IReadOnlyList<string> Warnings);
}
