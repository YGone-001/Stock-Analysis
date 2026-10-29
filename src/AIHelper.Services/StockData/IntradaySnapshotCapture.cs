using System.Security.Cryptography;
using System.Text.Json;
using AIHelper.Models;

namespace AIHelper.Services.StockData;

public sealed record SnapshotEnvelope(int SchemaVersion, string Fingerprint, IntradaySnapshot Snapshot);
public sealed class IntradaySnapshotStore(string directory)
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public const int MaximumFiles = 100;
    public static string Fingerprint(IntradaySnapshot snapshot) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
    public string Save(IntradaySnapshot snapshot, CancellationToken token = default)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new SnapshotEnvelope(1, Fingerprint(snapshot), snapshot));
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Snapshot exceeds size bound");
        Directory.CreateDirectory(directory);
        foreach (var orphan in new DirectoryInfo(directory).EnumerateFiles(".*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1) && Guid.TryParseExact(f.Name[1..^4], "N", out _))) orphan.Delete();
        string path = Path.Combine(directory, $"{snapshot.Scan.RunId:N}.json");
        if (File.Exists(path))
        {
            if (Fingerprint(Load(path)) != Fingerprint(snapshot)) throw new InvalidDataException("Run identity collision");
            return path;
        }
        string temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // Only this store's GUID-named artifacts are subject to retention; unrelated user files are untouched.
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.json").Where(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f.Name), "N", out _)).Where(f => !string.Equals(f.FullName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).Skip(MaximumFiles - 1)) file.Delete();
        return path;
    }
    public IntradaySnapshot Load(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("Snapshot exceeds size bound");
        SnapshotEnvelope envelope;
        try { envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty snapshot"); }
        catch (JsonException ex) { throw new InvalidDataException("Corrupted snapshot", ex); }
        if (envelope.Snapshot is null || envelope.SchemaVersion != 1 || envelope.Snapshot.SchemaVersion != 1) throw new InvalidDataException("Unsupported snapshot schema");
        if (envelope.Fingerprint != Fingerprint(envelope.Snapshot)) throw new InvalidDataException("Snapshot fingerprint mismatch");
        return envelope.Snapshot;
    }
}

public sealed class IntradaySnapshotCapture(IMinuteObservationService minutes, IQuoteService quotes,
    IMarketCalendarService calendar, IntradaySnapshotStore store, TimeProvider? clock = null,
    IntradayFreshnessPolicy? freshness = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<(IntradaySnapshot Snapshot, string Path)> CaptureAsync(IntradayScanContext scan,
        IReadOnlyList<SparrowRankedCandidate> ranking, CancellationToken token, DateTimeOffset? requestedAsOf = null)
    {
        if (scan.TopN is < 1 or > 20 || scan.ScanCompletedAtUtc < scan.ScanStartedAtUtc) throw new ArgumentException("Invalid scan context");
        var started = _clock.GetUtcNow();
        var date = ChinaExchangeTime.Date(requestedAsOf ?? started);
        MarketDataMetadata unknown = new("unavailable", false, false, false);
        var calendarMetadata = unknown;
        var session = SessionEvidence.Unavailable;
        try
        {
            var day = await calendar.GetTradingDayAsync(date.ToDateTime(TimeOnly.MinValue), token);
            calendarMetadata = day.Metadata;
            if (day.Success && day.Data is { } d && d.HasAuthoritativeEvidence && !day.Metadata.IsStale && day.Metadata.CacheFreshness != CacheFreshness.Stale)
                session = DateOnly.FromDateTime(d.RequestedDate) != date ? SessionEvidence.Inconsistent : d.IsTradingDay ? SessionEvidence.TradingDay : SessionEvidence.NonTradingDay;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* Unknown is never a holiday. */ }
        var symbols = new List<SnapshotSymbol>();
        foreach (var candidate in ranking.Take(scan.TopN))
        {
            token.ThrowIfCancellationRequested();
            MinuteObservationResult minute;
            try { minute = await minutes.GetAsync(candidate.Code, date, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { minute = new(Array.Empty<MinuteObservation>(), IntradayQuality.SourceFailure, "minute source failure", unknown); }
            QuoteSnapshot? quote = null; var quoteMetadata = unknown; var quoteQuality = IntradayQuality.SourceFailure;
            try
            {
                var q = await quotes.GetQuoteAsync(candidate.Code, token);
                quote = q.Data; quoteMetadata = q.Metadata;
                quoteQuality = q.Success && quote != null ? IntradayQuality.MissingEventTime : IntradayQuality.SourceFailure;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { }
            symbols.Add(new(candidate.Code, candidate.Name, candidate.Rank, candidate.TotalScore, candidate.RankingProfile,
                quote, null, _clock.GetUtcNow(), quoteMetadata, quoteQuality, minute, IntradayQuality.Partial));
        }
        var captured = _clock.GetUtcNow();
        var cutoff = requestedAsOf ?? captured; // Newly acquired data is NEVER labelled as scan-start evidence.
        var policy = freshness ?? IntradayFreshnessPolicy.Default;
        symbols = symbols.Select(s => s with { Quality = s.Minutes.Status == IntradayQuality.SourceFailure ? IntradayQuality.SourceFailure
            : s.Minutes.Rows.Count == 0 ? IntradayQuality.Unavailable
            : s.Minutes.Rows.Select(r => IntradayCutoff.Validate(r, date, cutoff, policy)).FirstOrDefault(q => q != IntradayQuality.Valid, IntradayQuality.Partial) }).ToList();
        // The legacy quote contract supplies no event time. Persist useful diagnostics, never certify it.
        var snapshot = new IntradaySnapshot(1, scan, date, started, captured, cutoff, session, calendarMetadata, symbols, false);
        token.ThrowIfCancellationRequested();
        return (snapshot, store.Save(snapshot, token));
    }
}
