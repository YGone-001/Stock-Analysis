using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;

namespace AIHelper.Services;

public sealed class HistoricalResultExporter : IHistoricalResultExporter
{
    public Task<string> ExportReplayJsonAsync(SparrowReplayResult result, string path, CancellationToken cancellationToken = default) => SparrowHistoricalResultExporter.ExportReplayJsonAsync(result, path, cancellationToken);
    public Task<string> ExportBacktestJsonAsync(SparrowBacktestResult result, string path, CancellationToken cancellationToken = default) => SparrowHistoricalResultExporter.ExportBacktestJsonAsync(result, path, cancellationToken);
    public Task<string> ExportLegacyReplayCapabilityJsonAsync(SparrowLegacyHistoricalReplayResult result, string path, CancellationToken cancellationToken = default) => SparrowHistoricalResultExporter.ExportLegacyReplayCapabilityJsonAsync(result, path, cancellationToken);
    public Task<string> ExportBenchmarkAnalysisJsonAsync(SparrowBenchmarkAnalysisResult result, string path, CancellationToken cancellationToken = default) => SparrowHistoricalResultExporter.ExportBenchmarkAnalysisJsonAsync(result, path, cancellationToken);
}
