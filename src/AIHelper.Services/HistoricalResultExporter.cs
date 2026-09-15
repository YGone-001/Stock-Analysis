using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;

namespace AIHelper.Services;

public sealed class HistoricalResultExporter : IHistoricalResultExporter
{
    public Task<string> ExportReplayJsonAsync(SparrowReplayResult result, string path, CancellationToken cancellationToken = default) => SparrowHistoricalResultExporter.ExportReplayJsonAsync(result, path, cancellationToken);
    public Task<string> ExportBacktestJsonAsync(SparrowBacktestResult result, string path, CancellationToken cancellationToken = default) => SparrowHistoricalResultExporter.ExportBacktestJsonAsync(result, path, cancellationToken);
}
