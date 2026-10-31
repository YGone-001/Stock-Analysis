using System;
using System.Collections.Generic;
using AIHelper.Core.Sparrow;

namespace AIHelper.Core.StockData;

public enum T1ReadinessStatus { PASS, PARTIAL, BLOCKED, NOT_VERIFIED, FAIL }
public enum T1EvidenceClass { LIVE_RECORDED_PIT, ARCHIVED_PIT_REPLAY, RETROSPECTIVE_DIAGNOSTIC, DAILY_ONLY, UNVERIFIED }
public enum T1EvidenceOrigin { UNVERIFIED, TEST_FIXTURE }
public enum T1OutcomeSourceState
{
    AVAILABLE, SOURCE_NOT_CONFIGURED, CREDENTIAL_REQUIRED, PERMISSION_DENIED, RATE_LIMITED,
    DATA_MISSING, PARTIAL_COVERAGE, SOURCE_ERROR, NOT_VERIFIED
}
public enum T1PriceBasisState
{
    ALIGNED_RAW, ALIGNED_ADJUSTED_WITH_PROVENANCE, INCOMPATIBLE_BASIS, UNKNOWN_BASIS, CORPORATE_ACTION_UNVERIFIED
}
public enum T1OutcomeSession { UNKNOWN, OPEN, SUSPENDED, DELISTED, NOT_YET_LISTED }

/// <summary>Declarations are not authentication. This version has no trusted archive/live attestation adapter.</summary>
public sealed record T1CalendarEvidence(string Exchange, HistoricalAcquisitionResult<HistoricalCalendarDay> Acquisition);
public sealed record T1CalendarMapping(T1ReadinessStatus Status, DateOnly? NextTradingDate, string Exchange, string Source,
    IReadOnlyList<string> Reasons, string EvidenceFingerprint = "");
public sealed record T1PriceBasis(HistoricalPriceAdjustmentMode AdjustmentMode, string SourceInstrumentId,
    string Currency, string PriceUnit, string SourceReference, string? FactorFingerprint = null,
    DateOnly? FactorAsOf = null, DateTimeOffset? FactorAvailableAtUtc = null);
public sealed record T1CorporateActionEvidence(DateOnly DecisionDate, DateOnly TargetDate, string SourceReference,
    DateTimeOffset RetrievedAtUtc, bool NoAction, string? FactorFingerprint = null);

/// <summary>Post-session outcome data only. Never accepted by the feature/ranking input API.</summary>
public sealed record T1OutcomeEnvelope(string Symbol, string Exchange, string SourceInstrumentId,
    DateOnly DecisionDate, DateOnly TargetDate, string Source, DateTimeOffset? RetrievedAtUtc,
    T1OutcomeSourceState State, HistoricalPriceAdjustmentMode AdjustmentMode, T1OutcomeSession Session,
    HistoricalDailyPrice? Daily = null, HistoricalSuspension? Suspension = null,
    HistoricalSourceSecurity? Security = null, T1PriceBasis? Basis = null,
    T1CorporateActionEvidence? CorporateAction = null, string SourceReference = "",
    string AuthenticationLimitation = "source declaration; not independently authenticated",
    HistoricalSecurity? Lifecycle = null, string LifecycleSourceReference = "");

/// <summary>Only explicit paths are read. Origin can restrict fixture accounting; it cannot grant trust.</summary>
public sealed record T1EvidenceRequest(string? SnapshotPath, string? PackageDirectory,
    T1CalendarEvidence? Calendar = null, IReadOnlyList<T1OutcomeEnvelope>? Outcomes = null,
    T1PriceBasis? DecisionBasis = null, T1EvidenceOrigin Origin = T1EvidenceOrigin.UNVERIFIED);
public sealed record T1SnapshotVerification(T1ReadinessStatus Structure, string Fingerprint,
    IntradaySnapshot? Snapshot, IReadOnlyList<string> Reasons);
public sealed record T1CapturedIdentity(string Symbol, string Name, int OriginalRank, double OriginalScore);
public sealed record T1OutcomeCheck(string Symbol, T1OutcomeSourceState SourceState, T1ReadinessStatus SourceAvailability,
    T1ReadinessStatus FieldValidity, T1ReadinessStatus Status, T1PriceBasisState PriceBasis,
    IReadOnlyList<string> Reasons, string EvidenceFingerprint = "");
public sealed record T1EvidenceSample(string PolicyVersion, Guid? RunId, DateOnly? DecisionDate,
    string Strategy, string SnapshotFingerprint, string PackageFingerprint, T1EvidenceOrigin Origin,
    T1ReadinessStatus SnapshotStructure, T1ReadinessStatus RankingIntegrity, T1ReadinessStatus Binding,
    T1ReadinessStatus CapturedPool, T1ReadinessStatus FullEligibleOrdering,
    T1EvidenceClass EvidenceClass, T1ReadinessStatus PitAvailability, T1CalendarMapping Calendar,
    T1ReadinessStatus ExecutionEvidence, T1ReadinessStatus Overall,
    IReadOnlyList<T1CapturedIdentity> Captured, T1RankingCoverage? Coverage,
    IReadOnlyList<T1OutcomeCheck> Outcomes, IReadOnlyList<string> Reasons, string DecisionBasisFingerprint = "");
public sealed record T1EvidenceCoverageReport(string PolicyVersion, string SampleState,
    T1ReadinessStatus Overall, int SourceSnapshotFiles, int VerifiedSnapshots, int VerifiedRankingPackages,
    int LinkedPairs, int MismatchedPairs, int DistinctDecisionDates, int DistinctRunIds,
    int CapturedCandidates, int EligibleCandidates, int ExcludedCandidates, int ActualShortlistSize,
    int TruncatedEligibleCandidates, int AvailableNextDates, int MissingCalendarCoverage,
    int AvailableOutcomeEnvelopes, int PriceBasisMismatches, int ExecutionBlockers,
    int FixtureSamples, int AuthenticatedProductionSamples, string FailureRate,
    IReadOnlyDictionary<string, int> Strategies, IReadOnlyDictionary<string, int> EvidenceClasses,
    IReadOnlyDictionary<string, int> MissingOutcomeReasons, IReadOnlyList<T1EvidenceSample> Samples);
