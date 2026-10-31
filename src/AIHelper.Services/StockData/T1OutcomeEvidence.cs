using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData;

public static partial class T1EvidenceReadiness
{
    private static T1CalendarMapping CalendarMissing() => new(T1ReadinessStatus.NOT_VERIFIED, null, "", "", new[] { "CALENDAR_NOT_VERIFIED", "MISSING_NEXT_TRADING_DATE" });

    /// <summary>Validates the declared acquisition envelope; no implicit provider calls or fixture-to-production promotion.</summary>
    public static T1CalendarMapping MapNextTradingDay(DateOnly decision, T1CalendarEvidence? calendar)
    {
        if (calendar?.Acquisition?.Data is null || decision == default) return CalendarMissing();
        var rows = calendar.Acquisition.Data;
        T1CalendarMapping Invalid(string code) => new(T1ReadinessStatus.FAIL, null, calendar.Exchange, "", new[] { code, "CALENDAR_NOT_VERIFIED" });
        if (calendar.Exchange is not ("SSE" or "SZSE" or "BSE")) return Invalid("CALENDAR_EXCHANGE_UNSUPPORTED");
        if (rows.Count > 3660 || rows.Any(r => r is null || Missing(r.Source) || r.Source.Length > 80
                || !r.Source.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            || rows.Select(r => r.TradingDate).Distinct().Count() != rows.Count)
            return Invalid("CALENDAR_DUPLICATE_OR_INVALID_RECORD");
        if (rows.Select(r => r.Source).Distinct(StringComparer.Ordinal).Count() != 1) return Invalid("CALENDAR_SOURCE_INCONSISTENT");
        var ordered = rows.OrderBy(r => r.TradingDate).ToArray();
        var current = ordered.SingleOrDefault(r => r.TradingDate == decision);
        var next = ordered.FirstOrDefault(r => r.TradingDate > decision && r.IsOpen);
        if (current is null || next is null) return CalendarMissing();
        if (!current.IsOpen || next.PreviousOpenDate != decision) return Invalid("CALENDAR_OPEN_CHAIN_INVALID");
        // Require every natural date through the first declared open date, not just two open rows.
        var required = ordered.Where(r => r.TradingDate >= decision && r.TradingDate <= next.TradingDate).ToArray();
        if (required.Length != next.TradingDate.DayNumber - decision.DayNumber + 1) return CalendarMissing();
        var evidence = calendar.Acquisition.CoverageEvidence;
        if (evidence is null) return CalendarMissing();
        if (evidence.Count > 3660) return Invalid("CALENDAR_COVERAGE_BOUND_EXCEEDED");
        foreach (var row in required)
        {
            var covering = evidence.Where(e => e is not null && e.Scope is not null
                && e.Scope.StartDate <= row.TradingDate && e.Scope.EndDate >= row.TradingDate).ToArray();
            if (covering.Length == 0 || covering.Any(e => e.Endpoint != "trade_cal" || e.Source != row.Source
                || e.Scope.Exchange != calendar.Exchange || e.CoverageStatus != HistoricalCoverageAcquisitionStatus.Full
                || e.ResponseCapHit || e.DuplicateRows != 0 || e.InvalidRows != 0 || e.MissingCount != 0
                || e.ExpectedCount != e.Scope.EndDate.DayNumber - e.Scope.StartDate.DayNumber + 1
                || e.ObservedCount != e.ExpectedCount || e.ReturnedRows != e.ObservedCount || Missing(e.ProofMethod)))
                return CalendarMissing();
            // A Full claim must also match the actual retained records over its entire declared range.
            if (covering.Any(e => ordered.Count(r => r.TradingDate >= e.Scope.StartDate && r.TradingDate <= e.Scope.EndDate) != e.ExpectedCount))
                return CalendarMissing();
        }
        return new(T1ReadinessStatus.PASS, next.TradingDate, calendar.Exchange, current.Source, Array.Empty<string>(),
            DeclarationFingerprint(calendar with { Acquisition = calendar.Acquisition with
                { Data = ordered, CoverageEvidence = evidence.OrderBy(e => e.Scope.StartDate).ThenBy(e => e.Scope.EndDate).ToArray() } }));
    }

    public static T1OutcomeSourceState DescribeSource(bool configured, bool credentialRequired, HistoricalSourceCapabilityStatus? observedStatus)
    {
        if (!configured) return credentialRequired ? T1OutcomeSourceState.CREDENTIAL_REQUIRED : T1OutcomeSourceState.SOURCE_NOT_CONFIGURED;
        return observedStatus switch
        {
            HistoricalSourceCapabilityStatus.Available => T1OutcomeSourceState.AVAILABLE,
            HistoricalSourceCapabilityStatus.PermissionDenied => T1OutcomeSourceState.PERMISSION_DENIED,
            HistoricalSourceCapabilityStatus.RateLimited => T1OutcomeSourceState.RATE_LIMITED,
            HistoricalSourceCapabilityStatus.Unavailable => T1OutcomeSourceState.SOURCE_ERROR,
            _ => T1OutcomeSourceState.NOT_VERIFIED
        };
    }

    /// <summary>Declared metadata alignment only, not authentication or an adjustment/conversion engine.</summary>
    public static T1PriceBasisState CheckPriceBasis(T1PriceBasis? decision, T1PriceBasis? outcome,
        T1CorporateActionEvidence? action, DateOnly date, DateOnly target, DateTimeOffset cutoff)
    {
        if (decision is null || outcome is null || Missing(decision.SourceReference) || Missing(outcome.SourceReference)
            || Missing(decision.SourceInstrumentId) || Missing(outcome.SourceInstrumentId)
            || decision.AdjustmentMode == HistoricalPriceAdjustmentMode.Unknown || outcome.AdjustmentMode == HistoricalPriceAdjustmentMode.Unknown)
            return T1PriceBasisState.UNKNOWN_BASIS;
        if (!Enum.IsDefined(decision.AdjustmentMode) || !Enum.IsDefined(outcome.AdjustmentMode)
            || decision.SourceInstrumentId != outcome.SourceInstrumentId || decision.Currency != "CNY" || outcome.Currency != "CNY"
            || decision.PriceUnit != "per-share" || outcome.PriceUnit != "per-share" || decision.AdjustmentMode != outcome.AdjustmentMode)
            return T1PriceBasisState.INCOMPATIBLE_BASIS;
        if (action is null || Missing(action.SourceReference) || action.DecisionDate != date || action.TargetDate != target
            || ChinaExchangeTime.Date(action.RetrievedAtUtc) < target)
            return T1PriceBasisState.CORPORATE_ACTION_UNVERIFIED;
        if (decision.AdjustmentMode == HistoricalPriceAdjustmentMode.Raw)
            return action.NoAction ? T1PriceBasisState.ALIGNED_RAW : T1PriceBasisState.CORPORATE_ACTION_UNVERIFIED;
        if (Missing(decision.FactorFingerprint) || decision.FactorFingerprint != outcome.FactorFingerprint
            || decision.FactorFingerprint != action.FactorFingerprint || decision.FactorAsOf is null
            || decision.FactorAsOf > date || outcome.FactorAsOf != decision.FactorAsOf
            || decision.FactorAvailableAtUtc is null || decision.FactorAvailableAtUtc > cutoff
            || outcome.FactorAvailableAtUtc != decision.FactorAvailableAtUtc)
            return T1PriceBasisState.CORPORATE_ACTION_UNVERIFIED;
        return T1PriceBasisState.ALIGNED_ADJUSTED_WITH_PROVENANCE;
    }

    private static T1OutcomeCheck CheckOutcome(string symbol, DateOnly date, DateTimeOffset cutoff,
        T1CalendarMapping calendar, T1OutcomeEnvelope? envelope, T1PriceBasis? decisionBasis)
    {
        var reasons = new List<string>();
        var state = envelope?.State ?? T1OutcomeSourceState.SOURCE_NOT_CONFIGURED;
        var basis = T1PriceBasisState.UNKNOWN_BASIS;
        void Add(bool condition, string code) { if (condition) reasons.Add(code); }
        Add(calendar.Status != T1ReadinessStatus.PASS, "CALENDAR_NOT_VERIFIED");
        Add(calendar.NextTradingDate is null, "MISSING_NEXT_TRADING_DATE");
        if (envelope is null) reasons.Add("SOURCE_UNAVAILABLE");
        else
        {
            Add(!Enum.IsDefined(state), "SOURCE_STATE_INVALID");
            Add(state != T1OutcomeSourceState.AVAILABLE, state == T1OutcomeSourceState.PARTIAL_COVERAGE ? "SOURCE_PARTIAL" : "SOURCE_UNAVAILABLE");
            string expectedId = symbol + (ExchangeOf(symbol) switch { "SSE" => ".SH", "SZSE" => ".SZ", "BSE" => ".BJ", _ => "" });
            Add(envelope.Symbol != symbol || envelope.Exchange != ExchangeOf(symbol) || envelope.SourceInstrumentId != expectedId
                || (calendar.Status == T1ReadinessStatus.PASS && calendar.Exchange != envelope.Exchange), "SYMBOL_IDENTITY_MISMATCH");
            Add(envelope.DecisionDate != date || calendar.NextTradingDate is { } next && envelope.TargetDate != next, "OUTCOME_TARGET_DATE_MISMATCH");
            Add(Missing(envelope.Source) || Missing(envelope.SourceReference), "OUTCOME_PROVENANCE_MISSING");
            var sessionEnd = new DateTimeOffset(envelope.TargetDate.ToDateTime(new TimeOnly(15, 0)), ChinaExchangeTime.Offset);
            Add(envelope.RetrievedAtUtc is null || envelope.RetrievedAtUtc < sessionEnd, "OUTCOME_PUBLICATION_NOT_ESTABLISHED");
            var bar = envelope.Daily;
            Add(bar is null, "MISSING_TARGET_DAILY_BAR");
            if (bar is not null)
            {
                Add(bar.Symbol != symbol || bar.TsCode != expectedId || bar.Source != envelope.Source, "SYMBOL_IDENTITY_MISMATCH");
                Add(bar.TradingDate != envelope.TargetDate, "OUTCOME_TARGET_DATE_MISMATCH");
                Add(bar.AdjustmentMode != envelope.AdjustmentMode || envelope.Basis?.AdjustmentMode != envelope.AdjustmentMode
                    || envelope.Basis?.SourceInstrumentId != expectedId || decisionBasis?.SourceInstrumentId != expectedId, "PRICE_BASIS_MISMATCH");
                double?[] prices = { bar.Open, bar.High, bar.Low, bar.Close };
                Add(prices.Any(p => p is null || !double.IsFinite(p.Value) || p <= 0)
                    || bar.Low > bar.High || bar.Open < bar.Low || bar.Open > bar.High || bar.Close < bar.Low || bar.Close > bar.High
                    || bar.Volume is { } volume && (!double.IsFinite(volume) || volume < 0)
                    || bar.Amount is { } amount && (!double.IsFinite(amount) || amount < 0), "OUTCOME_FIELDS_INVALID");
            }
            basis = CheckPriceBasis(decisionBasis, envelope.Basis, envelope.CorporateAction, date, envelope.TargetDate, cutoff);
            Add(basis == T1PriceBasisState.UNKNOWN_BASIS, "PRICE_BASIS_UNKNOWN");
            Add(basis == T1PriceBasisState.INCOMPATIBLE_BASIS, "PRICE_BASIS_MISMATCH");
            Add(basis == T1PriceBasisState.CORPORATE_ACTION_UNVERIFIED, "CORPORATE_ACTION_UNVERIFIED");
            if (envelope.Session == T1OutcomeSession.SUSPENDED)
            {
                var s = envelope.Suspension;
                Add(s is not null && s.Symbol == symbol && s.TsCode == expectedId && s.TradingDate == envelope.TargetDate
                    && s.Action == "S" && !Missing(s.Source), "SUSPENDED");
                if (!reasons.Contains("SUSPENDED")) reasons.Add("SESSION_STATUS_UNVERIFIED");
            }
            else if (envelope.Session is T1OutcomeSession.DELISTED or T1OutcomeSession.NOT_YET_LISTED)
            {
                // stock_basic delist_date does not establish an exclusive effective date; no automatic delisting certification.
                var s = envelope.Security;
                var lifecycle = envelope.Lifecycle;
                if (envelope.Session == T1OutcomeSession.DELISTED && lifecycle is not null && lifecycle.Symbol == symbol
                    && lifecycle.LifecycleQuality == HistoricalLifecycleQuality.Complete && !Missing(envelope.LifecycleSourceReference)
                    && lifecycle.DelistingEffectiveDate <= envelope.TargetDate) reasons.Add("DELISTED");
                else if (envelope.Session == T1OutcomeSession.NOT_YET_LISTED && s is not null && s.Symbol == symbol
                    && s.TsCode == expectedId && !Missing(s.Source) && s.ListingDate > envelope.TargetDate) reasons.Add("NOT_YET_LISTED");
                else reasons.Add("LIFECYCLE_EFFECTIVE_DATE_UNVERIFIED");
            }
            else Add(envelope.Session != T1OutcomeSession.OPEN, "SESSION_STATUS_UNVERIFIED");
            reasons.Add("OUTCOME_SOURCE_AUTHENTICATION_UNVERIFIED");
        }
        reasons.AddRange(new[] { "PIT_EVIDENCE_UNVERIFIED", "NO_EXECUTION_EVIDENCE", "LIMIT_RESTRICTION_UNVERIFIED" });
        bool invalid = reasons.Any(r => r is "OUTCOME_FIELDS_INVALID" or "SYMBOL_IDENTITY_MISMATCH" or "OUTCOME_TARGET_DATE_MISMATCH"
            or "OUTCOME_PROVENANCE_MISSING" or "OUTCOME_PUBLICATION_NOT_ESTABLISHED" or "SOURCE_STATE_INVALID");
        return new(symbol, state, invalid ? T1ReadinessStatus.FAIL
                : state == T1OutcomeSourceState.AVAILABLE ? T1ReadinessStatus.PASS : T1ReadinessStatus.NOT_VERIFIED,
            invalid ? T1ReadinessStatus.FAIL : envelope?.Daily is not null ? T1ReadinessStatus.PASS : T1ReadinessStatus.BLOCKED,
            invalid ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED, basis, Ordered(reasons),
            envelope is null ? "" : DeclarationFingerprint(envelope));
    }
}
