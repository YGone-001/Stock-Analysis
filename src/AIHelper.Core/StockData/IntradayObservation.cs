using System;
using System.Collections.Generic;
using AIHelper.Models;

namespace AIHelper.Core.StockData;

public enum IntradayQuality { Valid, Stale, FutureData, WrongTradingDate, MissingEventTime, Unavailable, Partial, SourceFailure, Unsupported }
public enum SessionEvidence { TradingDay, NonTradingDay, Unavailable, Inconsistent }

public static class ChinaExchangeTime
{
    // China exchange civil time has a fixed +08 offset; independent of host timezone/DST.
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);
    public static DateOnly Date(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);
}

public sealed record IntradayFreshnessPolicy(TimeSpan MaximumEventAge)
{
    public static IntradayFreshnessPolicy Default { get; } = new(TimeSpan.FromMinutes(5));
}

public sealed record MinuteObservation(string Symbol, DateTimeOffset? ExchangeEventTime,
    DateTimeOffset ObservedAtUtc, DateTimeOffset? AvailableAtUtc, decimal? PriceYuan,
    decimal? SourceNumber, decimal? SourceAmount, string VolumeUnit, string AmountUnit,
    MarketDataMetadata Provenance, string Interval = "source-labelled minute; interval closure unverified", DateTimeOffset? TransportReceivedAtUtc = null, string Provider = "unspecified /api/minute provider");

public static class IntradayCutoff
{
    public static IntradayQuality Validate(MinuteObservation row, DateOnly date, DateTimeOffset cutoff, IntradayFreshnessPolicy policy)
    {
        if (policy.MaximumEventAge <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(policy));
        if (row.ExchangeEventTime is not { } eventTime) return IntradayQuality.MissingEventTime;
        if (ChinaExchangeTime.Date(eventTime) != date || ChinaExchangeTime.Date(cutoff) != date) return IntradayQuality.WrongTradingDate;
        if (eventTime > cutoff || row.AvailableAtUtc > cutoff || row.ObservedAtUtc > cutoff) return IntradayQuality.FutureData;
        if (row.AvailableAtUtc is null) return IntradayQuality.Unavailable;
        if (row.Provenance.IsStale || row.Provenance.CacheFreshness == CacheFreshness.Stale || cutoff - eventTime > policy.MaximumEventAge) return IntradayQuality.Stale;
        if (row.PriceYuan is null or <= 0 || row.SourceNumber is null or < 0 || row.SourceAmount is null or < 0) return IntradayQuality.Partial;
        if (row.VolumeUnit == "unknown" || row.AmountUnit == "unknown" || row.Interval.Contains("unverified", StringComparison.Ordinal)) return IntradayQuality.Partial;
        return IntradayQuality.Valid;
    }
}

public sealed record MinuteObservationResult(IReadOnlyList<MinuteObservation> Rows, IntradayQuality Status, string Reason, MarketDataMetadata Provenance);
public interface IMinuteObservationService
{
    System.Threading.Tasks.Task<MinuteObservationResult> GetAsync(string symbol, DateOnly date, System.Threading.CancellationToken cancellationToken = default);
}
public sealed record IntradayScanContext(Guid RunId, string Strategy, string StrategyVersion, string ParameterFingerprint,
    int TopN, DateTimeOffset ScanStartedAtUtc, DateTimeOffset ScanCompletedAtUtc);
public sealed record SnapshotSymbol(string Symbol, string Name, int Rank, double Score, string RankingProfile,
    QuoteSnapshot? Quote, DateTimeOffset? QuoteExchangeEventTime, DateTimeOffset QuoteObservedAtUtc,
    MarketDataMetadata QuoteProvenance, IntradayQuality QuoteQuality, MinuteObservationResult Minutes,
    IntradayQuality Quality);
public sealed record IntradaySnapshot(int SchemaVersion, IntradayScanContext Scan, DateOnly TradingDate,
    DateTimeOffset AcquisitionStartedAtUtc, DateTimeOffset CapturedAtUtc, DateTimeOffset RequestedAsOf,
    SessionEvidence Calendar, MarketDataMetadata CalendarProvenance, IReadOnlyList<SnapshotSymbol> Symbols,
    bool Certified, string Coverage = "selected TopN only; not an all-market or unbiased training dataset",
    string Timezone = "Asia/Shanghai (UTC+08:00)");
