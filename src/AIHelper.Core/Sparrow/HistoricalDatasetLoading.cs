namespace AIHelper.Core.Sparrow;

/// <summary>Application boundary for immutable historical research datasets.</summary>
public interface IHistoricalDatasetLoader
{
    Task<HistoricalDatasetLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed record HistoricalDatasetLoadResult(
    bool Success,
    HistoricalMarketDataset? Dataset,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static HistoricalDatasetLoadResult Failed(params string[] errors) => new(false, null, errors, Array.Empty<string>());
    public static HistoricalDatasetLoadResult Loaded(HistoricalMarketDataset dataset, IReadOnlyList<string>? warnings = null) => new(true, dataset, Array.Empty<string>(), warnings ?? Array.Empty<string>());
}

/// <summary>Explicit UI state; a null dataset alone cannot explain an in-flight or invalid load.</summary>
public enum HistoricalDatasetLoadState
{
    NoDataset,
    Loading,
    Ready,
    Invalid
}
