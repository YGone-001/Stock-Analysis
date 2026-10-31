using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AIHelper.Core.StockData;

public enum T1CandidateAssociationCategory
{
    SHORTLISTED, ELIGIBLE_TRUNCATED_DECLARED, EXCLUDED,
    ELIGIBLE_STATUS_NOT_VERIFIED, SOURCE_OR_RANKING_INVALID
}
public sealed record T1AssociationInput(T1EvidenceRequest Evidence,
    IReadOnlyDictionary<string, T1PriceBasis>? DecisionBases = null,
    IReadOnlyDictionary<string, T1CalendarEvidence>? Calendars = null);
public sealed record T1ExchangeCalendarInput(int SchemaVersion, IReadOnlyList<T1CalendarEvidence> Calendars);
/// <summary>Offline independent outcome file format; metadata is declared, never an authentication grant.</summary>
public sealed record T1AssociationOutcomeInput(IReadOnlyList<T1OutcomeEnvelope> Outcomes,
    IReadOnlyDictionary<string, T1PriceBasis>? DecisionBases = null);
public sealed record T1CandidateAssociation(T1CapturedIdentity Original, T1CandidateAssociationCategory Category,
    int? T1Rank, T1RankingExclusionCode? ExclusionCode, string? ExclusionReason,
    T1ReadinessStatus IndependentEligibility, T1OutcomeCheck Outcome, string DecisionBasisFingerprint,
    bool OutcomeAvailableDeclared, bool AssociationUsableDeclared, IReadOnlyList<string> Reasons,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Exchange = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T1CalendarMapping? Calendar = null);
/// <summary>Stable local declarations for schema-2 semantic replay; these are not external authentication.</summary>
public sealed record T1AssociationDerivation(T1EvidenceSample SourceFacts, string StrategyVersion,
    string SourceParameterFingerprint, DateTimeOffset? Cutoff, bool SnapshotFilePresent, string SourceFileKey,
    IReadOnlyDictionary<string, T1CalendarEvidence> Calendars, IReadOnlyList<T1OutcomeEnvelope> Outcomes,
    IReadOnlyDictionary<string, T1PriceBasis> DecisionBases, T1PriceBasis? FallbackBasis,
    T1RankingRequest? RankingRequest, IReadOnlyList<T1RankingCandidate> Ranked, IReadOnlyList<T1RankingExclusion> Excluded);
public sealed record T1AssociationRecord(T1EvidenceSample Evidence, string StrategyVersion,
    string SourceParameterFingerprint, DateTimeOffset? Cutoff, IReadOnlyList<T1CandidateAssociation> Candidates,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T1AssociationDerivation? Derivation = null);
public sealed record T1AssociationBreakdown(string DecisionDate, string Strategy, int Captured, int AssociatedDeclared, int Fixtures);
public sealed record T1AssociationCoverage(T1EvidenceCoverageReport SourceCoverage, int TotalCapturedCandidates, int CandidateAssociations,
    int DeclaredAvailableBars, int ValidatedFields, int BasisAligned, int BasisBlocked,
    int MissingBars, int UnavailableSources, int SupportedSuspensions, int LifecycleUnverified,
    int NoExecutionEvidence, int UsableDeclaredAssociations, string AssociationCoverage,
    IReadOnlyList<T1AssociationBreakdown> Breakdown);
public sealed record T1AssociationContent(int SchemaVersion, string PolicyVersion,
    string AuthenticationLimitation, IReadOnlyList<T1AssociationRecord> Records, T1AssociationCoverage Coverage);
public sealed record T1AssociationArtifact(int SchemaVersion, string Fingerprint, T1AssociationContent Content);
public sealed record T1AssociationPublication(string Fingerprint, string PackageDirectory, bool ReusedExisting);
