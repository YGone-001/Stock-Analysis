using AIHelper.Core.Sparrow;

namespace AIHelper.Core.StockData;

public sealed record T1ResearchTimings(DateTimeOffset? FeatureCompletedAtUtc = null,
    DateTimeOffset? RankingCompletedAtUtc = null, DateTimeOffset? ExportCompletedAtUtc = null);
public sealed record T1ResearchCandidate(T1CapturedIdentity Original, string Exchange, int MinuteObservations,
    DateTimeOffset? LatestUsableObservation, IntradayMetric Window5m, IntradayMetric Window15m,
    MinuteQualitySummary Freshness, T1ReadinessStatus CutoffConsistency, IReadOnlyList<string> VolumeUnits,
    IReadOnlyList<string> AmountUnits, bool IntervalClosureVerified, string PriceBasis,
    IReadOnlyList<string> Sources, T1RankingExclusion? Exclusion);
public sealed record T1ResearchManifestContent(int SchemaVersion, Guid RunId, string Strategy, string StrategyVersion,
    string ParameterFingerprint, DateOnly TradingDate, DateTimeOffset ScanStartedAtUtc, DateTimeOffset ScanCompletedAtUtc,
    DateTimeOffset RequestedCutoff, DateTimeOffset AcquisitionStartedAtUtc, DateTimeOffset CapturedAtUtc,
    long ScanToCaptureMilliseconds, TimeOnly WindowStart, TimeOnly WindowEnd, bool OutsideStudyWindow,
    string SnapshotFingerprint, string RankingFingerprint, string RankingDirectoryName, T1RankingCoverage Coverage,
    T1EvidenceOrigin Origin, T1EvidenceClass EvidenceClass, T1ResearchTimings Timings,
    IReadOnlyList<T1ResearchCandidate> Candidates, IReadOnlyDictionary<string, string> FileHashes,
    IReadOnlyList<string> Limitations, IReadOnlyList<string> ProvenanceReferences);
public sealed record T1ResearchManifest(int SchemaVersion, string Fingerprint, DateTimeOffset ArchivedAtUtc, T1ResearchManifestContent Content);
public sealed record T1ResearchArchive(string Directory, T1ResearchManifest Manifest, bool ReusedExisting);
public sealed record T1PilotRequest(bool AllowNetwork = false, string? Provider = null,
    bool Configured = false, bool CredentialAvailable = false, int CalendarDays = 14);
public sealed record T1PilotDailyEvidence(string Symbol, DateOnly? TargetDate, DateTimeOffset? RetrievedAtUtc,
    T1OutcomeSourceState State, HistoricalAcquisitionResult<HistoricalDailyPrice>? Acquisition);
public sealed record T1PilotContent(int SchemaVersion, string SampleFingerprint, string Provider,
    bool NetworkAuthorized, int CalendarDays, IReadOnlyList<T1CalendarEvidence> Calendars,
    IReadOnlyList<T1PilotDailyEvidence> Daily, IReadOnlyList<string> Reasons);
public sealed record T1PilotEvidence(int SchemaVersion, string Fingerprint, T1PilotContent Content);
