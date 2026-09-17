using AIHelper.Core.StockData;

namespace AIHelper.Core.Sparrow;

/// <summary>Source capability is an acquisition fact, never a strategy pass/fail value.</summary>
public enum HistoricalSourceCapabilityStatus { Available, Unavailable, PermissionDenied, RateLimited, Unknown }
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

    // Compatibility defaults keep existing in-memory fixtures source-agnostic. Production sources override these
    // envelopes, which are the only basis for a source-backed Full coverage claim.
    async Task<HistoricalAcquisitionResult<HistoricalSourceSecurity>> AcquireSecuritiesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetSecuritiesAsync(startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalCalendarDay>> AcquireCalendarAsync(string exchange, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetCalendarAsync(exchange, startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalDailyPrice>> AcquireDailyPricesAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetDailyPricesAsync(tsCode, startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalTurnover>> AcquireTurnoverAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetTurnoverAsync(tsCode, startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalIndexDaily>> AcquireIndexDailyAsync(string indexCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetIndexDailyAsync(indexCode, startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalSuspension>> AcquireSuspensionsAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetSuspensionsAsync(startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalStStatus>> AcquireStStatusesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetStStatusesAsync(startDate, endDate, cancellationToken).ConfigureAwait(false));
    async Task<HistoricalAcquisitionResult<HistoricalSourceAdjustmentFactor>> AcquireAdjustmentFactorsAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        new(await GetAdjustmentFactorsAsync(tsCode, startDate, endDate, cancellationToken).ConfigureAwait(false));
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
    string V2IndexCode = "000001.SH",
    HistoricalDatasetScope? Scope = null,
    IReadOnlyList<string>? ExplicitSymbols = null,
    IReadOnlyList<string>? BenchmarkIds = null)
{
    public IReadOnlyList<string> BenchmarkIds { get; init; } = Array.AsReadOnly(BenchmarkIds?.ToArray() ?? Array.Empty<string>());

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DatasetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputPath);
        if (StartDate > EndDate) throw new ArgumentException("StartDate must not be after EndDate.");
        if (PriceAdjustmentMode != HistoricalPriceAdjustmentMode.Raw)
            throw new NotSupportedException("Phase 3.1B supports Raw prices only.");
        if (ExplicitSymbols is { Count: > 0 } && Scope?.Kind == HistoricalDatasetScopeKind.MarketUniverse)
            throw new ArgumentException("An explicit symbol filter must not claim MarketUniverse scope.");
        if (BenchmarkIds.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Benchmark identifiers must be non-empty.");
        if (BenchmarkIds.Distinct(StringComparer.Ordinal).Count() != BenchmarkIds.Count) throw new ArgumentException("Benchmark identifiers must be unique.");
    }
}

public sealed record HistoricalDatasetBuildStatistics(
    int SecurityMasterCount, int UniverseSecurityCount, int SymbolsRequested, int SymbolsBuilt,
    int SymbolsSkipped, int TradingDays, int KlineBars, int QuotesBuilt, int SuspensionRecords,
    int StRecords, int IndexBars, IReadOnlyList<string> Warnings, TimeSpan Duration,
    int AdjustmentFactorRows = 0, int DelistedSecurityCount = 0, int MissingLifecycleCount = 0,
    int MissingHistoricalStCount = 0, int MissingSuspensionEvidenceCount = 0);

public sealed record HistoricalDatasetBuildResult(
    HistoricalMarketDataset Dataset, HistoricalDatasetBuildStatistics Statistics, bool IsPartial,
    IReadOnlyList<HistoricalCoverageEvidence>? CoverageEvidence = null,
    IReadOnlyList<HistoricalStrategyCapabilityExplanation>? StrategyCapabilities = null)
{
    public IReadOnlyList<HistoricalCoverageEvidence> CoverageEvidence { get; init; } = CoverageEvidence ?? Array.Empty<HistoricalCoverageEvidence>();
    public IReadOnlyList<HistoricalStrategyCapabilityExplanation> StrategyCapabilities { get; init; } = StrategyCapabilities ?? Array.Empty<HistoricalStrategyCapabilityExplanation>();
}
