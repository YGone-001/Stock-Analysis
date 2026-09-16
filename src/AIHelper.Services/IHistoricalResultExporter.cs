using AIHelper.Core.Sparrow;

namespace AIHelper.Services;

public interface IHistoricalResultExporter
{
    Task<string> ExportReplayJsonAsync(SparrowReplayResult result, string path, CancellationToken cancellationToken = default);
    Task<string> ExportBacktestJsonAsync(SparrowBacktestResult result, string path, CancellationToken cancellationToken = default);
    Task<string> ExportLegacyReplayCapabilityJsonAsync(SparrowLegacyHistoricalReplayResult result, string path, CancellationToken cancellationToken = default);
}
