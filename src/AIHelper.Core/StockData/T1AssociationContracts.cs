using System;
using System.Collections.Generic;

namespace AIHelper.Core.StockData;

public enum T1CandidateAssociationCategory
{
    SHORTLISTED, ELIGIBLE_TRUNCATED_DECLARED, EXCLUDED,
    ELIGIBLE_STATUS_NOT_VERIFIED, SOURCE_OR_RANKING_INVALID
}
public sealed record T1AssociationInput(T1EvidenceRequest Evidence,
    IReadOnlyDictionary<string, T1PriceBasis>? DecisionBases = null);
/// <summary>Offline independent outcome file format; metadata is declared, never an authentication grant.</summary>
public sealed record T1AssociationOutcomeInput(IReadOnlyList<T1OutcomeEnvelope> Outcomes,
    IReadOnlyDictionary<string, T1PriceBasis>? DecisionBases = null);
public sealed record T1CandidateAssociation(T1CapturedIdentity Original, T1CandidateAssociationCategory Category,
    int? T1Rank, T1RankingExclusionCode? ExclusionCode, string? ExclusionReason,
    T1ReadinessStatus IndependentEligibility, T1OutcomeCheck Outcome, string DecisionBasisFingerprint,
    bool OutcomeAvailableDeclared, bool AssociationUsableDeclared, IReadOnlyList<string> Reasons);
public sealed record T1AssociationRecord(T1EvidenceSample Evidence, string StrategyVersion,
    string SourceParameterFingerprint, DateTimeOffset? Cutoff, IReadOnlyList<T1CandidateAssociation> Candidates);
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
