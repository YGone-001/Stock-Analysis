using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;

namespace AIHelper.Services.StockData;

/// <summary>One retained decision, at most 20 symbols; no minute, security-master or market-wide requests.</summary>
public static class T1OutcomePilot
{
    public static async Task<T1PilotEvidence> AcquireAsync(string sampleDirectory, T1PilotRequest request,
        IHistoricalMarketDataSource? source = null, TimeProvider? clock = null, CancellationToken token = default)
    {
        var sample = T1ResearchSamples.Verify(sampleDirectory);
        var c = sample.Content;
        if (request.CalendarDays is < 1 or > 31 || c.Candidates.Count is < 1 or > 20) throw new ArgumentException("PILOT_SCOPE_BOUND");
        if (request.Provider is not (null or "tushare")) throw new ArgumentException("PILOT_PROVIDER_UNSUPPORTED");
        var calendars = new List<T1CalendarEvidence>(); var daily = new List<T1PilotDailyEvidence>(); var reasons = new SortedSet<string>(StringComparer.Ordinal);
        var time = clock ?? TimeProvider.System;
        var state = !request.AllowNetwork ? T1OutcomeSourceState.NOT_VERIFIED
            : !request.Configured ? T1OutcomeSourceState.SOURCE_NOT_CONFIGURED
            : !request.CredentialAvailable ? T1OutcomeSourceState.CREDENTIAL_REQUIRED : T1OutcomeSourceState.AVAILABLE;
        if (!request.AllowNetwork) reasons.Add("OFFLINE_NO_PROVIDER_CALLS");
        if (state != T1OutcomeSourceState.AVAILABLE) reasons.Add(state.ToString());
        if (state == T1OutcomeSourceState.AVAILABLE && (source is null || request.Provider != "tushare")) throw new ArgumentException("PILOT_SOURCE_REQUIRED");
        var exchangeStates = new Dictionary<string, T1OutcomeSourceState>();
        foreach (string exchange in c.Candidates.Select(x => x.Exchange).Distinct().Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested(); exchangeStates[exchange] = state;
            if (state != T1OutcomeSourceState.AVAILABLE) continue;
            try
            {
                var acquired = await source!.AcquireCalendarAsync(exchange, c.TradingDate, c.TradingDate.AddDays(request.CalendarDays), token).ConfigureAwait(false);
                calendars.Add(new(exchange, acquired));
            }
            catch (Exception ex) when (SourceFailure(ex, token))
            { exchangeStates[exchange] = FailureState(ex); reasons.Add(exchange + "_CALENDAR_" + FailureState(ex)); }
        }
        foreach (var candidate in c.Candidates)
        {
            token.ThrowIfCancellationRequested();
            var map = T1EvidenceReadiness.MapNextTradingDay(c.TradingDate, calendars.SingleOrDefault(x => x.Exchange == candidate.Exchange));
            if (map.Status != T1ReadinessStatus.PASS)
            { daily.Add(new(candidate.Original.Symbol, null, null, exchangeStates[candidate.Exchange] == T1OutcomeSourceState.AVAILABLE ? T1OutcomeSourceState.NOT_VERIFIED : exchangeStates[candidate.Exchange], null)); continue; }
            var target = map.NextTradingDate!.Value;
            if (time.GetUtcNow() < new DateTimeOffset(target.ToDateTime(new TimeOnly(15, 0)), ChinaExchangeTime.Offset))
            { reasons.Add("TARGET_SESSION_NOT_COMPLETED"); daily.Add(new(candidate.Original.Symbol, target, null, T1OutcomeSourceState.NOT_VERIFIED, null)); continue; }
            try
            {
                var acquired = await source!.AcquireDailyPricesAsync(Instrument(candidate.Original.Symbol, candidate.Exchange), target, target, token).ConfigureAwait(false);
                daily.Add(new(candidate.Original.Symbol, target, time.GetUtcNow(), acquired.Data.Count == 0 ? T1OutcomeSourceState.DATA_MISSING : T1OutcomeSourceState.AVAILABLE, acquired));
            }
            catch (Exception ex) when (SourceFailure(ex, token))
            { reasons.Add("DAILY_" + FailureState(ex)); daily.Add(new(candidate.Original.Symbol, target, time.GetUtcNow(), FailureState(ex), null)); }
        }
        var content = new T1PilotContent(1, sample.Fingerprint, request.Provider ?? "", request.AllowNetwork, request.CalendarDays,
            calendars, daily, reasons.ToArray());
        var result = new T1PilotEvidence(1, T1ResearchStorage.Fingerprint(content), content);
        _ = Associate(sampleDirectory, result); // accepted verifier and strict request-scope checks before evidence can be published
        return result;
    }
    private static bool SourceFailure(Exception ex, CancellationToken token) => ex is HistoricalHttpMarketDataSource.HistoricalSourceAccessException
        or HttpRequestException or System.Text.Json.JsonException || ex is OperationCanceledException && !token.IsCancellationRequested;
    private static T1OutcomeSourceState FailureState(Exception ex) => ex is HistoricalHttpMarketDataSource.HistoricalSourceAccessException access
        ? T1EvidenceReadiness.DescribeSource(true, true, access.Status) : T1OutcomeSourceState.SOURCE_ERROR;
    private static string Instrument(string symbol, string exchange) => symbol + (exchange switch { "SSE" => ".SH", "SZSE" => ".SZ", "BSE" => ".BJ", _ => throw new InvalidDataException("EXCHANGE_INVALID") });

    public static T1AssociationArtifact Associate(string sampleDirectory, T1PilotEvidence evidence)
    {
        var sample = T1ResearchSamples.Verify(sampleDirectory); var c = evidence.Content; var s = sample.Content;
        if (evidence.SchemaVersion != 1 || c.SchemaVersion != 1 || evidence.Fingerprint != T1ResearchStorage.Fingerprint(c)
            || c.SampleFingerprint != sample.Fingerprint || c.CalendarDays is < 1 or > 31 || s.Candidates.Count is < 1 or > 20
            || c.Provider is not ("" or "tushare") || c.Calendars.Count > 3 || c.Calendars.Select(x => x.Exchange).Distinct().Count() != c.Calendars.Count
            || c.Daily.Count != s.Candidates.Count || c.Daily.Select(x => x.Symbol).Distinct().Count() != c.Daily.Count)
            throw new InvalidDataException("PILOT_EVIDENCE_INVALID");
        var maps = new SortedDictionary<string, T1CalendarEvidence>(StringComparer.Ordinal);
        foreach (var calendar in c.Calendars)
        {
            if (!s.Candidates.Any(x => x.Exchange == calendar.Exchange) || calendar.Acquisition.Data.Count > c.CalendarDays + 1
                || calendar.Acquisition.Data.Any(d => d.TradingDate < s.TradingDate || d.TradingDate > s.TradingDate.AddDays(c.CalendarDays))
                || calendar.Acquisition.CoverageEvidence.Count > 32) throw new InvalidDataException("PILOT_CALENDAR_SCOPE_INVALID");
            maps.Add(calendar.Exchange, calendar);
        }
        if ((!c.NetworkAuthorized || c.Provider != "tushare") && (maps.Count > 0 || c.Daily.Any(d => d.Acquisition is not null))) throw new InvalidDataException("PILOT_NETWORK_PROVENANCE_INVALID");
        var outcomes = new List<T1OutcomeEnvelope>();
        foreach (var row in c.Daily)
        {
            var candidate = s.Candidates.SingleOrDefault(x => x.Original.Symbol == row.Symbol) ?? throw new InvalidDataException("PILOT_FOREIGN_SYMBOL");
            var mapping = T1EvidenceReadiness.MapNextTradingDay(s.TradingDate, maps.GetValueOrDefault(candidate.Exchange));
            if (!Enum.IsDefined(row.State) || row.TargetDate != mapping.NextTradingDate || row.Acquisition?.Data.Count > 1
                || row.Acquisition?.CoverageEvidence.Count > 32 || row.Acquisition is not null && row.RetrievedAtUtc is null
                || row.State == T1OutcomeSourceState.AVAILABLE && row.Acquisition?.Data.Count != 1)
                throw new InvalidDataException("PILOT_DAILY_SCOPE_INVALID");
            if (row.TargetDate is null) { if (row.Acquisition is not null) throw new InvalidDataException("UNMAPPED_DAILY_EVIDENCE"); continue; }
            if (row.Acquisition is null && row.RetrievedAtUtc is null) continue; // no request was made; do not invent a source-retrieval timestamp
            string id = Instrument(row.Symbol, candidate.Exchange);
            var bar = row.Acquisition?.Data.SingleOrDefault();
            if (bar is not null && (bar.Symbol != row.Symbol || bar.TsCode != id || bar.TradingDate != row.TargetDate || bar.AdjustmentMode != HistoricalPriceAdjustmentMode.Raw))
                throw new InvalidDataException("PILOT_FOREIGN_DAILY_RECORD");
            if (row.Acquisition?.CoverageEvidence.Any(e => e.Endpoint != "daily" || e.Scope.Symbol != id
                || e.Scope.StartDate != row.TargetDate || e.Scope.EndDate != row.TargetDate) == true) throw new InvalidDataException("PILOT_DAILY_COVERAGE_SCOPE_INVALID");
            string reference = row.Acquisition is null ? "source status only" : "retained acquisition SHA256:" + T1ResearchStorage.Fingerprint(row.Acquisition);
            // A raw daily bar proves neither the original minute basis nor an OPEN session / absence of corporate actions.
            outcomes.Add(new(row.Symbol, candidate.Exchange, id, s.TradingDate, row.TargetDate.Value, bar?.Source ?? "tushare", row.RetrievedAtUtc,
                row.State, HistoricalPriceAdjustmentMode.Raw, T1OutcomeSession.UNKNOWN, bar,
                Basis: bar is null ? null : new(HistoricalPriceAdjustmentMode.Raw, id, "CNY", "per-share", reference), SourceReference: reference));
        }
        var artifact = T1EvidenceReadiness.Associate(new[] { new T1AssociationInput(new(T1ResearchSamples.SnapshotPath(sampleDirectory),
            T1ResearchSamples.RankingPath(sampleDirectory, sample), Outcomes: outcomes, Origin: s.Origin), Calendars: maps) });
        if (!T1EvidenceReadiness.VerifyAssociation(artifact)) throw new InvalidDataException("PILOT_ASSOCIATION_INVALID");
        return artifact;
    }
    public static T1PilotEvidence Load(string path)
    {
        var evidence = T1ResearchStorage.Load<T1PilotEvidence>(path);
        if (evidence.SchemaVersion != 1 || evidence.Content is null || evidence.Content.SchemaVersion != 1
            || evidence.Content.Daily is null || evidence.Content.Calendars is null || evidence.Content.Daily.Count > 20 || evidence.Content.Calendars.Count > 3
            || evidence.Fingerprint != T1ResearchStorage.Fingerprint(evidence.Content)) throw new InvalidDataException("PILOT_EVIDENCE_INVALID");
        return evidence;
    }
    public static string Publish(T1PilotEvidence evidence, string sampleDirectory, string outputRoot, CancellationToken token = default)
    {
        _ = Associate(sampleDirectory, evidence); string root = T1ResearchStorage.Root(outputRoot, requireIgnored: true);
        Directory.CreateDirectory(root); string final = Path.Combine(root, "t1-outcomes-" + evidence.Fingerprint);
        byte[] bytes = T1ResearchStorage.Bytes(evidence);
        string Reuse()
        {
            if (!T1ResearchStorage.Read(Path.Combine(final, "source.json")).SequenceEqual(bytes)) throw new InvalidDataException("PILOT_IMMUTABLE_COLLISION");
            return Path.Combine(final, "source.json");
        }
        if (Directory.Exists(final)) return Reuse();
        string stage = Path.Combine(root, ".t1-staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            token.ThrowIfCancellationRequested(); Directory.CreateDirectory(stage);
            T1ResearchStorage.Write(Path.Combine(stage, "source.json"), bytes); token.ThrowIfCancellationRequested();
            try { Directory.Move(stage, final); } catch (IOException) when (Directory.Exists(final)) { return Reuse(); }
            return Path.Combine(final, "source.json");
        }
        finally { T1ResearchStorage.RemoveStaging(root, stage); }
    }
    public static int EstablishedCoverage(T1PilotEvidence evidence, T1AssociationArtifact association) => evidence.Content.Daily.Count(d => d.Acquisition is { Data.Count: 1 } a
        && association.Content.Records.SelectMany(r => r.Candidates).Any(c => c.Original.Symbol == d.Symbol
            && c.Calendar!.Status == T1ReadinessStatus.PASS && c.Outcome.SourceAvailability == T1ReadinessStatus.PASS && c.Outcome.FieldValidity == T1ReadinessStatus.PASS)
        && a.CoverageEvidence.Count > 0 && a.CoverageEvidence.All(e => e.CoverageStatus == HistoricalCoverageAcquisitionStatus.Full
            && !e.ResponseCapHit && e.DuplicateRows == 0 && e.InvalidRows == 0 && e.MissingCount == 0
            && e.ReturnedRows == 1 && e.ObservedCount == 1 && e.ExpectedCount == 1 && !string.IsNullOrWhiteSpace(e.ProofMethod)));
}
