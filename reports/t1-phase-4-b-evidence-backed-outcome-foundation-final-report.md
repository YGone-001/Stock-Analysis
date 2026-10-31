# T1-4-B Evidence-Backed Next-Trading-Day Outcome Foundation Final Report

## Verdict and identity

Implementation PASS locally: an offline evidence foundation, not a profitability backtester. Production research readiness remains BLOCKED/NOT_VERIFIED. No production PIT sample or market outcome was evaluated. Only deterministic TEST_FIXTURE data was generated in temporary test directories.

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Exact starting HEAD = fetched origin/develop: f4912b08504f51ed37b174e600449075c0fb97f8. Initial worktree clean; no applicable AGENTS.md found. Final delivery SHA, synchronized clean state, exact-SHA CI/size workflow run/job IDs: pending commit/push verification at authoring; the delivery response records actual results. No future workflow results are asserted here.

## Inspected infrastructure and reuse

Inspected definitions and compatibility boundaries:

- Core/StockData: IntradayObservation.cs, IntradayFeatureAnalysis.cs, T1Ranking.cs, MarketDataContracts.cs.
- Core/Sparrow: HistoricalDatasetAcquisitionContracts.cs, SparrowHistoricalReplayModels.cs, SparrowHistoricalDataIntegrityModels.cs, SparrowHistoricalFingerprint.cs, SparrowStrategyVersions.cs.
- Services/StockData: IntradaySnapshotCapture.cs, T1RankingArtifact.cs; existing MinuteObservationService/provider boundaries were established in T1-4-A and remain unchanged.
- Services/StockData/Sparrow: HistoricalDatasetBuilder.cs, HistoricalDatasetJsonLoader.cs, HistoricalHttpMarketDataSource.cs, SparrowHistoricalReplayEngine.cs, ManagedResearchExperimentRunner.cs.
- Existing T1RankingTests.cs, project settings, ignore rules and repository size governance.

The snapshot envelope and canonical IntradaySnapshotStore.Fingerprint are reused. Source reads are bounded before allocation (2 MiB) and deserialize the existing schema-1 envelope; this avoids the existing loader's complete-file read while preserving its schema/hash contract. Package inspection invokes the unchanged T1RankingArtifact.Verify under read-only component leases before projecting typed content. IntradayCutoff and pure IntradayFeatureAnalysis are reused for temporal/required-feature findings. The service never generates rankings; only tests call the existing pure Rank to create fixtures.

HistoricalCalendarDay, HistoricalAcquisitionResult, HistoricalCoverageEvidence, HistoricalDailyPrice, HistoricalSuspension, HistoricalSourceSecurity and HistoricalSecurity are reused. Historical source failure states are mapped without a provider call or inferred permission failure. Existing datasets/loaders/fingerprints and managed research engines retain their own daily research contracts; none is repurposed as a 14:30 replay or duplicated. No new dataset repository, calendar service, market client, adjustment engine or hash implementation replaces an established one.

## New source/API inventory

All application code changes are additive:

- src/AIHelper.Core/StockData/T1EvidenceContracts.cs: readiness/evidence/source/basis/session enums, typed request, calendar declaration/mapping, basis/corporate-action declarations, independent outcome envelope, snapshot result, captured identity, outcome checks, sample and coverage report.
- src/AIHelper.Services/StockData/T1EvidenceReadiness.cs: VerifySnapshot and Evaluate, exact source/package binding, bounded read-only input and conservative classification.
- src/AIHelper.Services/StockData/T1OutcomeEvidence.cs: MapNextTradingDay, DescribeSource, CheckPriceBasis and outcome validation.
- src/AIHelper.Services/StockData/T1EvidenceCoverage.cs: EvaluateCoverage, CanonicalReport and separate Explain presentation.
- tests/AIHelper.Services.Tests/T1EvidenceReadinessTests.cs: 72 offline cases.
- This authorized final report.

New types are necessary because neither the existing daily dataset nor Diagnostic ranking package expresses an independently sourced outcome, a verified source/package relationship, or separate readiness gates. Declarations are never authentication. SHA-256 over canonical typed new declaration content supplies calendar/outcome/basis identity fingerprints; existing snapshot/package canonical hashes are unchanged. Declared retrieval timestamps are stable input evidence, not volatile report-generation timestamps.

Service-level use (explicit authorized paths only):

```csharp
var request = new T1EvidenceRequest(snapshotPath, packageDirectory,
    Calendar: calendarEvidence, Outcomes: outcomeDeclarations,
    DecisionBasis: decisionBasis);
var sample = T1EvidenceReadiness.Evaluate(request);
var report = T1EvidenceReadiness.EvaluateCoverage(new[] { request });
string canonicalJson = T1EvidenceReadiness.CanonicalReport(report);
string explanation = T1EvidenceReadiness.Explain(report);
```

The API performs no discovery, credential reads, polling or network I/O and writes no source/artifact/report files. Consumers may persist report content only in ignored local research storage or outside Git. Machine content contains no filesystem paths or source exception messages; raw source references are represented by fingerprints. Calendar provider labels are constrained to safe identifiers. No new UI/CLI/service is needed to call the public service API.

## Verification and binding

Snapshot structural checks cover schema/envelope fingerprint, supported Classic/V2-V6 identity/version, nonempty frozen parameter identity, nonempty unique normalized A-share symbols, unique positive original ranks, finite scores, TopN/count bounds, minute row bounds/identity/provenance, chronological scan/acquisition/capture order, China date/cutoff consistency, timezone and captured-TopN coverage declaration. Original ranks may contain gaps; the service never renumbers them. Structural PASS is separate from PIT suitability. Missing files produce NO_AUTHORITATIVE_SAMPLE; malformed, oversized, unsupported or inconsistent snapshots fail with stable findings and are never repaired.

Package verification preserves the existing content-addressed directory, schema/model/version/parameter and feature identities, canonical JSON/CSV hashes, coverage equations, rank sequence, exclusion validation and legacy rejection. Subsequent typed projection is not an alternative verifier.

Binding compares RunId, actual canonical snapshot hash, decision date, cutoff, captured count, ranked/excluded source membership, source strategy/version/name/rank and ranked original score; explanation snapshot hashes must agree too. The original scan parameter identity is transitively bound by the exact snapshot hash, not confused with the distinct PriceOnlyV1 parameter hash. Exclusions do not serialize an original score; that value is preserved from the complete source pool rather than falsely claimed to have been compared.

Captured identities always come from the source snapshot. A 5 captured / 4 eligible / 1 excluded / requested2 / actual2 package binds successfully and retains all 5 source identities. Its two omitted eligible symbols are not exclusions. FullEligibleOrdering is NOT_VERIFIED for a truncated package; this phase does not reconstruct full ordering. For an untruncated package PASS means the published declared eligible ordering is present, not independently authenticated or recomputed. A missing or mismatched source cannot certify the linked captured pool.

## PIT and time boundaries

Supported classes: LIVE_RECORDED_PIT, ARCHIVED_PIT_REPLAY, RETROSPECTIVE_DIAGNOSTIC, DAILY_ONLY, UNVERIFIED. This policy has no independent trusted live/archive authentication adapter and deliberately never emits the first two classes. Neither Certified, a hash, origin declarations nor a caller assertion can promote trust. TEST_FIXTURE accounting stays separate; AuthenticatedProductionSamples is always zero in v1.

Readable diagnostic source data stays RETROSPECTIVE_DIAGNOSTIC; corrupt/binding-conflicting or contradictory/missing required observation timestamps are UNVERIFIED. A daily-only declaration without an intraday source is DAILY_ONLY and remains blocked. The stored ranking EvidenceClass remains Diagnostic.

IntradayCutoff checks event/available/original observed times independently; original timestamps are retained. Transport receipt after cutoff is disclosed separately and is not rewritten as original receipt. Scan/capture after cutoff, unknown interval closure, missing original availability/receipt, missing required features, absent decision-session evidence, independent source authentication, ranking completion timing and executable-action evidence remain explicit findings. Source hash integrity cannot establish original parameter authenticity or historical publication versions. No signal-completion timestamp is invented. Current schema-1 minute data has no established price-basis envelope; any supplied basis remains a declaration, not retrospective proof of the source's true basis.

## Calendar and independent outcomes

D_next is the first declared verified-open date strictly after D. Require a supported explicit SSE/SZSE/BSE calendar, identified consistent source, no duplicate dates, D open, next.PreviousOpenDate == D, every natural date through the target, and Full matching trade_cal coverage evidence with exact retained date-set counts. Missing/partial evidence yields CALENDAR_NOT_VERIFIED and no target; contradictory records fail. Calendar PASS is declared-envelope structural completeness, not source authentication. Fixture calendars never become production evidence. No next-calendar-day shortcut or stock-bar-based day skipping exists.

Outcome envelopes carry symbol/exchange/source instrument ID, decision/target dates, provider/reference/retrieval time, source state, adjustment mode/session, independent HistoricalDailyPrice and optional volume/amount/status/lifecycle/basis/corporate-action evidence. Supported states: AVAILABLE, SOURCE_NOT_CONFIGURED, CREDENTIAL_REQUIRED, PERMISSION_DENIED, RATE_LIMITED, DATA_MISSING, PARTIAL_COVERAGE, SOURCE_ERROR, NOT_VERIFIED. Missing credentials/configuration and uncalled providers are not labelled permission failures.

Validate instrument/exchange, exact mapped target, envelope/bar identity, source metadata, post-session retrieval, positive finite consistent OHLC, nonnegative finite optional volume/amount and basis agreement. SourceAvailability, FieldValidity, price alignment and overall outcome status are distinct. A declared AVAILABLE state with invalid or missing fields is not counted as a usable available envelope. Daily fields never enter Analyze/Rank input and no return arithmetic is performed.

Basis states: ALIGNED_RAW, ALIGNED_ADJUSTED_WITH_PROVENANCE, INCOMPATIBLE_BASIS, UNKNOWN_BASIS, CORPORATE_ACTION_UNVERIFIED. Require consistent instrument, CNY/per-share units and adjustment mode; raw comparison also needs declared no-action evidence over the exact date pair. Adjusted alignment needs a common factor fingerprint/as-of and cutoff-safe factor availability plus action provenance. Missing/future factors block; no conversion is performed. All alignment conclusions are metadata checks; PIT/source authentication blockers still prevent a production comparison claim.

Missing bars stay missing, not zero returns or presumed suspensions. SUSPENDED requires exact instrument/date S evidence; NOT_YET_LISTED requires supporting listing data. Raw stock_basic delist_date is not treated as an exclusive effective boundary: DELISTED needs the separate complete HistoricalSecurity lifecycle/effective-date contract and source reference. Unknown status, source partial/unavailable, identity/date mismatch, corporate actions, calendar/PIT gaps, limit restrictions and no execution evidence remain explicit reasons. No fills are assumed.

## Deterministic reporting and no-real-data behavior

Reports disclose source files/verified snapshots/packages, linked/mismatched pairs, distinct dates/RunIds, strategy counts, captured/eligible/excluded/actual/truncated counts, evidence classes, mapped dates/calendar gaps, valid available outcome envelopes, reason counts, basis mismatches and execution blockers. Pair-weighted candidate totals are per distinct supplied snapshot/package pair; dates/RunIds remain distinct. Repeated path or content-addressed pairs are rejected rather than inflating coverage. No sample yields NO_SAMPLE, BLOCKED and NOT_APPLICABLE for rates; no performance statistics exist.

Deterministic test assertions demonstrate these excerpts (not production examples):

- Empty/missing input: SampleState=NO_SAMPLE, Overall=BLOCKED, VerifiedSnapshots=0, AuthenticatedProductionSamples=0, FailureRate=NOT_APPLICABLE.
- TEST_FIXTURE captured5/eligible4/excluded1/Top2: LinkedPairs=1, CapturedCandidates=5, TruncatedEligibleCandidates=2, FullEligibleOrdering=NOT_VERIFIED, Overall=BLOCKED.
- Same fixture with complete declared calendar and one valid daily envelope: AvailableOutcomeEnvelopes=1, all 5 candidates retained, SOURCE_UNAVAILABLE=4, NO_EXECUTION_EVIDENCE=5. Canonical content is byte-identical on repeat evaluation.

No new production storage scan was performed. The earlier audit's absence of authoritative samples remains an unresolved blocker, not a claim that all possible storage is empty. No live provider, account or market data access was attempted in this phase.

## Tests and executed validation

T01-T44 mapping:

- T01-T04: valid canonical fingerprint, corrupt JSON/hash, unsupported schema, duplicate symbol; additional rank/date/time/provenance/coverage/size negatives.
- T05-T09: RunId/hash/cutoff/strategy/version/original rank-score mismatches rejected even when package integrity independently passes.
- T10-T11: JSON/CSV tamper and legacy rejection through existing verifier.
- T12-T17: valid truncated pair, no fabricated exclusions, outside ranked/excluded membership rejection, missing source pool blocker, retained timestamps.
- T18-T21: post-cutoff availability, unknown closure, no hash/Certified trust promotion, fixtures not authenticated production.
- T22-T25: Friday-to-Monday, holiday gap, missing/partial/duplicate/contradictory/source/exchange/chain/calendar coverage failures.
- T26-T27: missing/wrong-day bar never moves target or changes decision identities/input path.
- T28-T31: raw alignment, adjusted/raw mismatch, unknown basis, missing and future factor/action evidence.
- T32-T34: missing bar is not suspension; exact supporting suspension and lifecycle evidence; actual source-state meanings preserved.
- T35-T39: no credentials/path/sample blocked, no unobserved rates or returns, deterministic reports and source/package byte preservation.
- T40-T41: all existing ranking/formula/Classic-V6 and historical replay tests remain byte-identical and pass in the full suite.
- T42: final tracked inventory restricted to new source/tests/this report; runtime data protections unchanged; ignored-path checks pass.
- T43-T44: unchanged 8/5 guard, plus isolated temporary repositories: exactly8MiB passes; 8MiB+1 fails; introduced5MiB+1 fails below tree budget and even after deletion; invalid-base fallback and LF/CRLF/untracked-file invariance pass. 13 runtime paths ignored, 3 legitimate source/JSON/CSV fixture paths visible. Fixtures never enter project history.

Executed: dotnet --info (SDK 10.0.401), restore, Release build --no-restore (0 errors, 8 existing warnings), solution test --no-build: 995 passed, 0 failed, 0 skipped (923 existing +72 new). Python 3.12.10 compileall app and pytest: 13 passed. Dependencies unchanged. git diff --check passed. Guard and Top30 size diagnostics run again on final committed HEAD before delivery. Local logs are in the OS temporary directory, not tracked research output.

## Growth and acceptance

Planning target: approximately 64-128 KiB added source/tests/report, without deleting accepted content.
Starting tracked tree: 4201383 bytes. Proposed/final staged tree: 4287503 bytes; exact delta +86120 bytes; remaining 4101105 bytes under 8388608. The same values are rechecked against committed HEAD. Both guard thresholds, workflows, allowlist, ignore rules and Measure-RepositorySize.ps1 are unchanged.

Gate | Status at commit authoring
--- | ---
A exact baseline/clean start | PASS
B existing infrastructure reused | PASS
C snapshot verification | PASS
D immutable package verifier preserved | PASS
E complete captured-pool binding | PASS
F truncated TopN semantics | PASS
G conservative classification | PASS
H no future decision input | PASS
I next actual trading date | PASS
J bad/missing calendar fails closed | PASS
K independent outcome source | PASS
L price basis blocks comparison | PASS
M explicit missing/untradable reasons | PASS
N deterministic readiness/coverage | PASS
O missing production evidence blocked | PASS
P no performance/historical signals | PASS
Q original behavior unchanged | PASS
R .NET/Python suites | PASS
S unchanged 8/5 governance | PASS isolated; committed-tree check pending
T exact-SHA remote CI/size | pending commit/push
U no unauthorized market/network access | PASS

## Remaining boundary

Independent trusted live/archive source attestation, real retained PIT decisions, historical minute publication/version semantics, original full parameter provenance, true minute adjustment basis, corporate-action provenance, real daily/calendar access and execution evidence remain unresolved. Schema-1 hashes and Diagnostic packages do not solve these gaps. Even valid declared fixtures remain BLOCKED for production performance evaluation.

One forward-only develop commit is intended after local acceptance; parent must equal the authoritative starting SHA. Final SHA, remote workflow run/job IDs and clean synchronization are verified in the delivery response. No amend, force push, branch change or history rewrite.

STOP after T1-4-B. No T1-4-C outcome association work, historical returns, profitability comparison, significance testing, weight optimization, backtester, live execution or brokerage integration is authorized by this delivery.
