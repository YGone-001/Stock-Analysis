using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHelper.Core.StockData;

public enum FeatureAvailability { Diagnostic, Unavailable, Undefined, Unsupported }
public sealed record IntradayMetric(decimal? Value, FeatureAvailability Availability, string Unit,
    DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd, DateTimeOffset? EffectiveObservationTime,
    IReadOnlyList<DateTimeOffset> ObservationTimes, string Source, string Reason, int EligibleTransitions = 0, int BaselineSamples = 0)
{
    public string Display => Value.HasValue ? $"{Value.Value.ToString("0.####", CultureInfo.InvariantCulture)} {Unit} [Diagnostic]" : $"{Availability}: {Reason}";
}
public sealed record IntradayFeaturePolicy(TimeSpan ReferenceTolerance, int MinimumHistoricalSamples = 3)
{
    public static IntradayFeaturePolicy Default { get; } = new(TimeSpan.Zero);
    public static readonly TimeSpan SessionStart = new(13, 0, 0);
    public static readonly TimeSpan AnalysisStart = new(14, 30, 0);
    public static readonly TimeSpan ContinuousEnd = new(14, 57, 0);
}
/// <summary>Explicit separately reviewed capability evidence. Production adapters currently supply none.</summary>
public sealed record IntradayQuantityEvidence(string Provider, string Symbol, string VolumeUnit, string AmountUnit,
    decimal SharesPerUnit, decimal CnyPerAmountUnit, bool Incremental, bool TradeAmountVerified,
    bool PriceAmountBasisCompatible, string VolumeBasis, string EvidenceReference, TimeSpan IntervalLength, bool LabelsAreIntervalEnds);
public sealed record IntradayBenchmarkEvidence(string InstrumentId, DateOnly TradingDate, MinuteObservationResult Minutes);
public sealed record HistoricalClockSession(string Symbol, DateOnly TradingDate, DateTimeOffset Cutoff,
    SessionEvidence Calendar, MinuteObservationResult Minutes, IntradayQuantityEvidence Quantity);
public sealed record IntradayFeatureResult(int SchemaVersion, string ComputationVersion, IntradayFeaturePolicy Policy, string SnapshotFingerprint,
    IntradayScanContext Scan, string Symbol, string Name, DateOnly TradingDate, DateTimeOffset RequestedAsOf,
    DateTimeOffset? EffectiveObservationTime, bool SourceSnapshotCertified, string EvidenceStatus,
    IntradayMetric LatestPrice, IntradayMetric Return5m, IntradayMetric Return15m, IntradayMetric PricePersistence,
    IntradayMetric PullbackFromObservedHigh, IntradayMetric RelativeStrength5m, IntradayMetric RelativeStrength15m,
    IntradayMetric VolumeAcceleration, IntradayMetric TradeVwap, IntradayMetric HistoricalSameClockRvol,
    IReadOnlyList<MinuteObservation> SourceProvenance, IntradayBenchmarkEvidence? Benchmark, IntradayQuantityEvidence? QuantityEvidence, IReadOnlyList<HistoricalClockSession> HistoricalEvidence, IReadOnlyList<string> Warnings);

/// <summary>Pure snapshot analysis. No data acquisition, recommendations, ranking or snapshot mutation.</summary>
public static class IntradayFeatureAnalysis
{
    public const string Version = "intraday-features-v1";
    public const string BenchmarkId = "sh000001"; // SSE Composite; explicit Shanghai market, never bare 000001.
    private static DateTimeOffset Time(MinuteObservation row) => row.ExchangeEventTime!.Value;
    private static string Sources(IEnumerable<MinuteObservation> rows) => string.Join("; ", rows.Select(r => $"{r.Provider}/{r.Provenance.SourceKind}/{r.Provenance.Source}").Distinct());
    private static IntradayMetric Missing(string reason, string unit, DateTimeOffset? start, DateTimeOffset? end, string source = "none", FeatureAvailability status = FeatureAvailability.Unavailable) => new(null, status, unit, start, end, end, Array.Empty<DateTimeOffset>(), source, reason);
    private static IntradayMetric Metric(decimal value, string unit, IReadOnlyList<MinuteObservation> rows, string reason = "source-labelled observations; not certified minute-close evidence", int transitions = 0, int samples = 0) => new(value, FeatureAvailability.Diagnostic, unit, Time(rows[0]), Time(rows[^1]), Time(rows[^1]), rows.Select(Time).ToArray(), Sources(rows), reason, transitions, samples);
    private static List<MinuteObservation> Eligible(MinuteObservationResult series, string symbol, DateOnly date, DateTimeOffset cutoff, List<string> warnings)
    {
        if (series.Rows.Count > 500 || series.Status == IntradayQuality.SourceFailure) { warnings.Add("source failure or row bound exceeded: " + series.Reason); return new(); }
        var rows = new List<MinuteObservation>();
        foreach (var group in series.Rows.GroupBy(r => r.ExchangeEventTime))
        {
            if (group.Key is null) { warnings.Add("missing timestamp excluded"); continue; }
            // Equal duplicates count once. Different values/provenance at the same label fail closed.
            if (group.Distinct().Count() > 1) { warnings.Add($"conflicting duplicate {group.Key:O} excluded"); continue; }
            var row = group.First();
            var q = IntradayCutoff.Validate(row, date, cutoff, new(TimeSpan.FromDays(1)));
            var local = Time(row).ToOffset(ChinaExchangeTime.Offset).TimeOfDay;
            if (row.Symbol != symbol || row.PriceYuan is null or <= 0 || q is not (IntradayQuality.Valid or IntradayQuality.Partial) || local < IntradayFeaturePolicy.SessionStart || local >= IntradayFeaturePolicy.ContinuousEnd)
            { warnings.Add($"excluded {group.Key:O}: {q}, invalid price/symbol or outside afternoon continuous auction"); continue; }
            rows.Add(row);
        }
        return rows.OrderBy(Time).ToList();
    }
    private static bool Contiguous(IReadOnlyList<MinuteObservation> rows, TimeSpan tolerance) => rows.Count > 0 && rows.Zip(rows.Skip(1), (a, b) => Time(b) - Time(a)).All(gap => gap > TimeSpan.Zero && gap <= TimeSpan.FromMinutes(1) + tolerance);
    private static IntradayMetric Return(IReadOnlyList<MinuteObservation> rows, DateTimeOffset end, int minutes, IntradayFeaturePolicy policy)
    {
        var target = end.AddMinutes(-minutes);
        var reference = rows.LastOrDefault(r => Time(r) <= target && target - Time(r) <= policy.ReferenceTolerance);
        var latest = rows.FirstOrDefault(r => Time(r) == end);
        if (reference == null || latest == null) return Missing("exact or permitted earlier reference missing", "%", target, end, Sources(rows));
        var window = rows.Where(r => Time(r) >= Time(reference) && Time(r) <= end).ToArray();
        if (window.Select(r => r.Provider).Distinct().Count() != 1) return Missing("mixed provider price bases in return window", "%", Time(reference), end, Sources(window));
        if (!Contiguous(window, policy.ReferenceTolerance)) return Missing("gap inside return window; no interpolation", "%", Time(reference), end, Sources(window));
        try { return Metric((latest.PriceYuan!.Value / reference.PriceYuan!.Value - 1) * 100, "%", window); }
        catch (OverflowException) { return Missing("decimal return range exceeded", "%", Time(reference), end, Sources(window)); }
    }
    private static bool QuantityValid(IReadOnlyList<MinuteObservation> rows, IntradayQuantityEvidence? evidence, string symbol, bool amount)
        => evidence != null && evidence.Symbol == symbol && evidence.Incremental && evidence.IntervalLength == TimeSpan.FromMinutes(1) && evidence.LabelsAreIntervalEnds && evidence.SharesPerUnit > 0
        && !string.IsNullOrWhiteSpace(evidence.EvidenceReference) && !string.IsNullOrWhiteSpace(evidence.VolumeBasis)
        && evidence.VolumeUnit is not ("unknown" or "") && rows.Count > 0
        && (!amount || evidence.TradeAmountVerified && evidence.PriceAmountBasisCompatible && evidence.CnyPerAmountUnit > 0 && evidence.AmountUnit is not ("unknown" or ""))
        && rows.All(r => r.Provider == evidence.Provider && r.VolumeUnit == evidence.VolumeUnit && r.EventTimeSemanticsVerified
            && !string.IsNullOrWhiteSpace(r.Interval) && !r.Interval.Contains("unverified", StringComparison.OrdinalIgnoreCase)
            && r.SourceNumber is >= 0 && (!amount || r.SourceAmount is >= 0 && r.AmountUnit == evidence.AmountUnit));
    private static MinuteObservation[] ClosingWindow(IReadOnlyList<MinuteObservation> rows, DateTimeOffset end, int minutes)
        => rows.Where(r => Time(r) > end.AddMinutes(-minutes) && Time(r) <= end).ToArray();
    private static bool CompleteClosing(IReadOnlyList<MinuteObservation> rows, DateTimeOffset end, int minutes)
        => rows.Count == minutes && Enumerable.Range(0, minutes).All(i => Time(rows[i]) == end.AddMinutes(i - minutes + 1));

    public static IReadOnlyList<IntradayFeatureResult> Analyze(IntradaySnapshot snapshot, string fingerprint, DateTimeOffset asOf,
        IntradayBenchmarkEvidence? benchmark = null, IntradayQuantityEvidence? quantity = null,
        IReadOnlyList<HistoricalClockSession>? history = null, IntradayFeaturePolicy? policy = null, CancellationToken token = default)
    {
        policy ??= IntradayFeaturePolicy.Default;
        if (policy.ReferenceTolerance < TimeSpan.Zero || policy.ReferenceTolerance > TimeSpan.FromSeconds(30) || policy.MinimumHistoricalSamples is < 3 or > 20) throw new ArgumentOutOfRangeException(nameof(policy));
        if (snapshot.SchemaVersion != 1 || snapshot.Symbols.Count > 20 || snapshot.Symbols.Any(s => s.Minutes.Rows.Count > 500) || string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Invalid snapshot analysis input");
        token.ThrowIfCancellationRequested();
        if (fingerprint != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)))) throw new ArgumentException("Source snapshot fingerprint differs");
        var results = new List<IntradayFeatureResult>();
        foreach (var symbol in snapshot.Symbols)
        {
            token.ThrowIfCancellationRequested();
            var warnings = new List<string> { "Diagnostic only; source labels are not promoted to verified minute-close events", "selected TopN coverage; no predictive or unbiased historical claim", $"snapshot calendar: {snapshot.Calendar}; source certification: {snapshot.Certified}" };
            var rows = Eligible(symbol.Minutes, symbol.Symbol, snapshot.TradingDate, asOf, warnings);
            if (asOf > snapshot.RequestedAsOf || ChinaExchangeTime.Date(asOf) != snapshot.TradingDate) { rows.Clear(); warnings.Add("requested cutoff outside original snapshot boundary/date"); }
            var latest = rows.LastOrDefault();
            if (latest != null && (Time(latest).ToOffset(ChinaExchangeTime.Offset).TimeOfDay < IntradayFeaturePolicy.AnalysisStart || asOf - Time(latest) > TimeSpan.FromMinutes(1))) { warnings.Add("no current post-14:30 continuous-auction endpoint (maximum lag one minute)"); latest = null; }
            DateTimeOffset? end = latest?.ExchangeEventTime;
            IntradayMetric Unavailable(string reason, string unit = "%") => Missing(reason, unit, null, end, Sources(rows));
            var lastPrice = latest == null ? Unavailable("eligible current price missing", "CNY") : Metric(latest.PriceYuan!.Value, "CNY", new[] { latest });
            var r5 = latest == null ? Unavailable("current endpoint missing") : Return(rows, end!.Value, 5, policy);
            var r15 = latest == null ? Unavailable("current endpoint missing") : Return(rows, end!.Value, 15, policy);
            var recent = end == null ? Array.Empty<MinuteObservation>() : rows.Where(r => Time(r) >= end.Value.AddMinutes(-15)).ToArray();
            var transitions = recent.Zip(recent.Skip(1), (a, b) => (a, b)).Where(p => Time(p.b) - Time(p.a) == TimeSpan.FromMinutes(1) && p.a.Provider == p.b.Provider).ToArray();
            var persistence = latest == null || transitions.Length == 0 ? Unavailable("no eligible adjacent one-minute transitions", "fraction")
                : Metric((decimal)transitions.Count(p => p.b.PriceYuan >= p.a.PriceYuan) / transitions.Length, "fraction", recent, $"nonnegative transitions / eligible one-minute transitions = {transitions.Count(p => p.b.PriceYuan >= p.a.PriceYuan)}/{transitions.Length}; gaps excluded", transitions.Length);
            var pullback = latest == null || rows.Select(r => r.Provider).Distinct().Count() != 1 ? Unavailable("eligible comparable-provider afternoon prices missing") : Metric((latest.PriceYuan!.Value / rows.Max(r => r.PriceYuan!.Value) - 1) * 100, "%", rows, "pullback from observed eligible afternoon high; no daily OHLC input");
            IntradayMetric Relative(IntradayMetric stock, int minutes)
            {
                if (benchmark == null) return Unavailable("benchmark minute evidence absent; no spot/full-day substitution");
                if (benchmark.InstrumentId != BenchmarkId) return Unavailable("benchmark identity must be explicit sh000001 (SSE Composite)");
                if (benchmark.TradingDate != snapshot.TradingDate) return Unavailable("benchmark trading date differs");
                if (stock.Value == null || end == null) return Unavailable("stock return unavailable");
                var indexRows = Eligible(benchmark.Minutes, BenchmarkId, snapshot.TradingDate, asOf, warnings);
                var comparison = Return(indexRows, end.Value, minutes, policy);
                if (comparison.Value == null || comparison.WindowStart != stock.WindowStart || comparison.WindowEnd != stock.WindowEnd) return Unavailable("benchmark missing or windows do not align exactly");
                return stock with { Value = stock.Value - comparison.Value, Source = stock.Source + " versus " + comparison.Source, Reason = $"stock return minus {BenchmarkId} return; same actual endpoints", ObservationTimes = stock.ObservationTimes.Concat(comparison.ObservationTimes).Distinct().Order().ToArray() };
            }
            var acceleration = Unavailable("verified incremental volume units/intervals or valid quantities absent", "ratio");
            var vwap = Unavailable("verified incremental traded amount/share units and compatible basis absent; weighted closes are not trade VWAP", "CNY");
            var rvol = Unavailable("genuine comparable historical same-clock observations absent", "ratio");
            try {
            if (end is { } t && quantity != null)
            {
                var ten = ClosingWindow(rows, t, 10);
                if (QuantityValid(ten, quantity, symbol.Symbol, false) && CompleteClosing(ten, t, 10)) {
                    decimal previous = ten.Take(5).Sum(r => r.SourceNumber!.Value);
                    acceleration = previous == 0 ? Missing("previous five-minute volume is zero", "ratio", t.AddMinutes(-10), t, Sources(ten), FeatureAvailability.Undefined)
                        : Metric(ten.Skip(5).Sum(r => r.SourceNumber!.Value) / previous, "ratio", ten, "incremental last5 / previous5; two adjacent left-open/right-closed windows") with { WindowStart = t.AddMinutes(-10) };
                } else acceleration = Unavailable("incomplete or unverified ten-minute incremental volume window", "ratio");
                var traded = rows.Where(r => Time(r).ToOffset(ChinaExchangeTime.Offset).TimeOfDay > IntradayFeaturePolicy.SessionStart).ToArray();
                if (QuantityValid(traded, quantity, symbol.Symbol, true) && Contiguous(traded, TimeSpan.Zero)) {
                    decimal shares = traded.Sum(r => r.SourceNumber!.Value) * quantity!.SharesPerUnit;
                    vwap = shares == 0 ? Missing("aggregate traded shares are zero", "CNY", Time(traded[0]).AddMinutes(-1), t, Sources(traded), FeatureAvailability.Undefined)
                        : Metric(traded.Sum(r => r.SourceAmount!.Value) * quantity.CnyPerAmountUnit / shares, "CNY", traded, "actual traded amount / actual traded shares; not weighted closing prices") with { WindowStart = Time(traded[0]).AddMinutes(-1) };
                }
                int sessionMinutes = (int)(t.ToOffset(ChinaExchangeTime.Offset).TimeOfDay - IntradayFeaturePolicy.SessionStart).TotalMinutes;
                var current = ClosingWindow(rows, t, sessionMinutes);
                if (history != null && sessionMinutes > 0 && CompleteClosing(current, t, sessionMinutes) && QuantityValid(current, quantity, symbol.Symbol, false)) {
                    var baselines = new List<decimal>();
                    foreach (var sample in history.Take(20).GroupBy(h => h.TradingDate).Where(g => g.Count() == 1).Select(g => g.Single())) {
                        token.ThrowIfCancellationRequested();
                        if (sample.Symbol != symbol.Symbol || sample.TradingDate >= snapshot.TradingDate || sample.Calendar != SessionEvidence.TradingDay || sample.Cutoff.ToOffset(ChinaExchangeTime.Offset).TimeOfDay != t.ToOffset(ChinaExchangeTime.Offset).TimeOfDay || sample.Quantity.VolumeBasis != quantity!.VolumeBasis) continue;
                        var past = Eligible(sample.Minutes, sample.Symbol, sample.TradingDate, sample.Cutoff, warnings);
                        var session = ClosingWindow(past, sample.Cutoff, sessionMinutes);
                        if (CompleteClosing(session, sample.Cutoff, sessionMinutes) && QuantityValid(session, sample.Quantity, sample.Symbol, false)) baselines.Add(session.Sum(r => r.SourceNumber!.Value) * sample.Quantity.SharesPerUnit);
                    }
                    if (baselines.Count >= policy.MinimumHistoricalSamples && baselines.Average() > 0)
                        rvol = Metric(current.Sum(r => r.SourceNumber!.Value) * quantity!.SharesPerUnit / baselines.Average(), "ratio", current, "afternoon through same clock / historical mean; distinct known trading days, retained original availability", samples: baselines.Count) with { WindowStart = new DateTimeOffset(t.ToOffset(ChinaExchangeTime.Offset).Date + IntradayFeaturePolicy.SessionStart, ChinaExchangeTime.Offset) };
                    else if (baselines.Count >= policy.MinimumHistoricalSamples) rvol = Missing("historical same-clock mean volume is zero", "ratio", Time(current[0]), t, Sources(current), FeatureAvailability.Undefined) with { BaselineSamples = baselines.Count };
                    else rvol = Unavailable($"insufficient comparable same-clock sessions: {baselines.Count}/{policy.MinimumHistoricalSamples}", "ratio") with { BaselineSamples = baselines.Count };
                } else if (history != null) rvol = Unavailable("current afternoon through-clock volume coverage incomplete", "ratio");
            }
            } catch (OverflowException) {
                acceleration = Unavailable("decimal quantity range exceeded", "ratio"); vwap = Unavailable("decimal quantity range exceeded", "CNY"); rvol = Unavailable("decimal quantity range exceeded", "ratio");
            }
            token.ThrowIfCancellationRequested();
            results.Add(new(1, Version, policy, fingerprint, snapshot.Scan, symbol.Symbol, symbol.Name, snapshot.TradingDate, asOf, end, snapshot.Certified, "Diagnostic",
                lastPrice, r5, r15, persistence, pullback, Relative(r5, 5), Relative(r15, 15), acceleration, vwap, rvol, symbol.Minutes.Rows, benchmark, quantity, history?.Take(20).ToArray() ?? Array.Empty<HistoricalClockSession>(), warnings.Distinct().ToArray()));
        }
        return results;
    }
}
