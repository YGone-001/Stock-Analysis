using AIHelper.Core.StockData;

namespace AIHelper.Core.Sparrow;

/// <summary>Source capability is an acquisition fact, never a strategy pass/fail value.</summary>
public enum HistoricalSourceCapabilityStatus { Available, Unavailable, PermissionDenied, Unknown }
public sealed record HistoricalSourceCapability(string Capability, HistoricalSourceCapabilityStatus Status, string Detail = "");

public sealed record HistoricalSourceSecurity(
    string Symbol, string TsCode, string Name, string? Market, string? Exchange,
    string? ListStatus, DateOnly? ListingDate, DateOnly? DelistingDate, string Source);
public sealed record HistoricalCalendarDay(DateOnly TradingDate, bool IsOpen, DateOnly? PreviousOpenDate, string Source);
public sealed record HistoricalDailyPrice(
    string Symbol, string TsCode, DateOnly TradingDate, double? Open, double? High, double? Low,
    double? Close, double? PreviousClose, double? Change, double? Percent, double? Volume,
    double? Amount, string Source, HistoricalPriceAdjustmentMode AdjustmentMode);
public sealed record HistoricalTurnover(string Symbol, string TsCode, DateOnly TradingDate, double? TurnoverRate, string Source);
public sealed record HistoricalIndexDaily(string IndexCode, DateOnly TradingDate, double? Close, double? PreviousClose, double? Percent, string Source);
public sealed record HistoricalSuspension(string Symbol, string TsCode, DateOnly TradingDate, string Action, string? Timing, string Source);
public sealed record HistoricalStStatus(string Symbol, string TsCode, DateOnly TradingDate, string Type, string Source);
public sealed record HistoricalSourceAdjustmentFactor(string Symbol, string TsCode, DateOnly TradingDate, double Factor, string Source, DateTimeOffset? RetrievedAtUtc = null);

/// <summary>Historical acquisition is isolated from live IQuoteService/IKlineService by design.</summary>
public interface IHistoricalMarketDataSource
{
    Task<IReadOnlyList<HistoricalSourceCapability>> ProbeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalSourceSecurity>> GetSecuritiesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalCalendarDay>> GetCalendarAsync(string exchange, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalDailyPrice>> GetDailyPricesAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalTurnover>> GetTurnoverAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalIndexDaily>> GetIndexDailyAsync(string indexCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalSuspension>> GetSuspensionsAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalStStatus>> GetStStatusesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HistoricalSourceAdjustmentFactor>> GetAdjustmentFactorsAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
}

public sealed record HistoricalDatasetBuildRequest(
    string DatasetId,
    DateOnly StartDate,
    DateOnly EndDate,
    string OutputPath,
    HistoricalPriceAdjustmentMode PriceAdjustmentMode = HistoricalPriceAdjustmentMode.Raw,
    bool IncludeTurnover = true,
    bool IncludeSuspension = false,
    bool IncludeHistoricalSt = false,
    bool IncludeAdjustmentFactors = false,
    bool IncludeV2IndexContext = true,
    string Exchange = "SSE",
    string V2IndexCode = "000001.SH")
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        if (StartDate > EndDate) throw new ArgumentException("StartDate must not be after EndDate.");
        if (PriceAdjustmentMode != HistoricalPriceAdjustmentMode.Raw)
            throw new NotSupportedException("Phase 3.1B supports Raw prices only.");
    }
}

public sealed record HistoricalDatasetBuildStatistics(
    int SecurityMasterCount, int UniverseSecurityCount, int SymbolsRequested, int SymbolsBuilt,
    int SymbolsSkipped, int TradingDays, int KlineBars, int QuotesBuilt, int SuspensionRecords,
    int StRecords, int IndexBars, IReadOnlyList<string> Warnings, TimeSpan Duration,
    int AdjustmentFactorRows = 0, int DelistedSecurityCount = 0, int MissingLifecycleCount = 0,
    int MissingHistoricalStCount = 0, int MissingSuspensionEvidenceCount = 0);

public sealed record HistoricalDatasetBuildResult(
    HistoricalMarketDataset Dataset, HistoricalDatasetBuildStatistics Statistics, bool IsPartial);
