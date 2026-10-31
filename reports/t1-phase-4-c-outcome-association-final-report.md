# T1-4-C Outcome Association Final Report

## Verdict and repository identity

Implementation and local contract validation PASS. Production validation remains NO_AUTHORITATIVE_SAMPLE/BLOCKED. No production evidence paths were supplied; no discovery, real-market pilot, credential reads or market-data requests occurred. Synthetic TEST_FIXTURE success is not production source/PIT readiness.

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Exact authoritative starting HEAD = fetched origin/develop = 3625f462c59ab945294e4fa9b1d61c4c7f86edf5. Initial worktree clean; no applicable AGENTS.md found. Final delivery SHA and exact-SHA CI/size run/job IDs are pending commit/push verification at authoring; actual results are supplied in the delivery response. No future results asserted here.

## Reuse and source inventory

Inspected accepted T1-4-B contracts and tests: T1EvidenceContracts.cs, T1EvidenceReadiness.cs, T1OutcomeEvidence.cs, T1EvidenceCoverage.cs and T1EvidenceReadinessTests.cs. Also inspected existing tool dispatch/parser/project, historical acquisition contracts/IHistoricalMarketDataSource, HistoricalHttpMarketDataSource, HistoricalDatasetBuilder, HistoricalDatasetJsonLoader, SparrowHistoricalFingerprint and ManagedResearchExperimentRunner compatibility boundaries. Existing IntradaySnapshotStore, IntradayFeatureAnalysis, immutable T1RankingArtifact and historical daily/date/security contracts remain authoritative and unchanged.

New implementation:

- Core/StockData/T1AssociationContracts.cs: association input/outcome-file wrapper, candidate category, record, aggregate coverage and immutable artifact contracts.
- Services/StockData/T1OutcomeAssociation.cs: Associate, canonical content and association integrity checks; extends the existing partial T1EvidenceReadiness class to reuse its accepted private readers and outcome checker rather than adding parallel verifiers.
- Services/StockData/T1AssociationPublication.cs: explicit Git-ignored output validation, read-only artifact verification and immutable staged publication/reuse.
- Services/StockData/T1AssociationCommand.cs: bounded explicit-file offline command API.
- tools/AIHelper.HistoricalDataTool/Program.cs: association operation mode, existing mode-conflict detection and early dispatch before any provider initialization.
- tools/AIHelper.HistoricalDataTool/T1-ASSOCIATION.md: operational documentation/formats/examples.
- tests/AIHelper.Services.Tests/T1OutcomeAssociationTests.cs: 29 deterministic offline cases.
- Existing B test fixture and three helper methods change only private -> internal to reuse fixtures; all accepted assertions remain unchanged.
- This authorized report. No other accepted source, tests, reports, limits, workflow or dependencies changed.

There is no parallel historical dataset/provider/replay service. Typed independent outcome declarations are accepted directly; a daily HistoricalMarketDataset is not silently converted into a decision snapshot or an authenticated outcome. HistoricalDatasetJsonLoader and SparrowHistoricalFingerprint retain their existing daily-research semantics and are not rerun to reconstruct signals.

## Operational path and input formats

From the repository root after Release build:

```powershell
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --associate-t1 --help
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --associate-t1
```

Actual second-command result: exit 2; SAMPLE=NO_SAMPLE, NO_AUTHORITATIVE_SAMPLE, CALENDAR_NOT_VERIFIED, OUTCOME_SOURCE_NOT_VERIFIED, READINESS=BLOCKED, CANDIDATES=0, AUTHENTICATION=NOT_VERIFIED. Its deterministic fingerprint is 21E159E8CB0D708D8A664B44AE31C9D7FE312BEE37ABB863FB35D8D587EC76CE. No output is written without --output. Help exits 0; invalid input/publication exits 1. Combining association with --source tushare was tested: operation conflict, exit 1, no provider creation/request.

Real file invocation is documented with placeholder authorized paths in T1-ASSOCIATION.md. Required evidence for a usable declared association is the original schema-1 snapshot, immutable ranking directory, T1CalendarEvidence JSON, and independent T1AssociationOutcomeInput JSON. The outcome wrapper contains existing T1OutcomeEnvelope records and optional per-symbol DecisionBases. String enum names and actual typed property names are used. Metadata/action/status evidence uses existing B contracts and cannot grant trust. Each file is bounded to 2 MiB; batch input is bounded to 100 pairs and per-pair outcomes to 100 declarations. Only source-pool candidates are associated.

--test-fixture restricts accounting; it cannot authenticate evidence. --output must be explicitly supplied and actually Git-ignored. Publication requires local Git to verify ignore status, refuses linked output ancestors and overlapping source/package/input paths, and never changes ignore rules. No autonomous server, background polling, new executable framework, WPF changes or source discovery exists.

## Binding, candidate categories and ordering

Association calls the accepted VerifySnapshot and T1-4-B Evaluate/ReadPackage paths, including T1RankingArtifact.Verify. Actual canonical source/package fingerprints, RunId, date, cutoff, model/version, parameter identities, captured counts, source membership and original strategy/rank/score attribution are checked before a candidate can become usable-declared. Source/package identity is rechecked across reads; changes abort rather than yielding a mixed record.

All original captured identities are retained when the source is valid, even if package binding fails. Missing/corrupt source means the full pool cannot be enumerated or certified; no symbols are invented from the shortlist alone.

Categories:

- SHORTLISTED: actual saved T1 rank present.
- ELIGIBLE_TRUNCATED_DECLARED: complement of saved shortlist/exclusions in the fully bound captured pool; eligible by the package declaration, not independently reconstructed. It has no fabricated T1 rank or exclusion code.
- EXCLUDED: actual original machine-readable exclusion code and reason retained.
- ELIGIBLE_STATUS_NOT_VERIFIED: source present but ranking relation unavailable.
- SOURCE_OR_RANKING_INVALID: invalid source/ranking identity relationship; no usable association.

IndependentEligibility stays NOT_VERIFIED. FULL_ORDERING_NOT_VERIFIED is disclosed for truncated or unavailable complete ordering. No optional reranking/reconstruction is performed. Captured5/eligible4/excluded1/Top2 produces 5 associations: 2 saved shortlist, 2 declared eligible-truncated and 1 original exclusion. Original ranks/scores remain unchanged; captured count is never equated with shortlist length.

## Calendar, source validation and decision isolation

The accepted MapNextTradingDay checks explicit exchange, complete intervening natural-date coverage, open-day chain, source/coverage identity and duplicate/contradictory records. Missing/partial coverage leaves the target unknown; a missing bar never moves the verified target date. No weekday fallback or D.AddDays(1) mapping is introduced.

Accepted CheckOutcome/CheckPriceBasis provide independent source state, field/identity/provenance/session/date/basis checks. States retain AVAILABLE, SOURCE_NOT_CONFIGURED, CREDENTIAL_REQUIRED, PERMISSION_DENIED, RATE_LIMITED, DATA_MISSING, PARTIAL_COVERAGE, SOURCE_ERROR and NOT_VERIFIED meanings. Missing bar, supported suspension, lifecycle-unverified, identity/target/basis mismatch, corporate-action unknown, PIT and execution reasons remain visible. Absent local bars additionally receive MISSING_TARGET_DAILY_BAR; this does not assert remote unavailability or suspension.

OutcomeAvailableDeclared distinguishes a claimed available bar from validated fields. AssociationUsableDeclared additionally requires binding, verified declared calendar shape, source/field checks, aligned basis and an OPEN session. It still does not imply authentication, PIT validity or executable fills. Session restrictions and NO_EXECUTION_EVIDENCE remain explicit. Raw/adjusted mismatch and unknown or temporally incompatible corporate actions block usable declaration; no adjustment factors or conversions are invented.

Decision isolation is explicit in data flow: source Evaluate/EvaluateCoverage receives Outcomes=null; outcome checks happen afterward against the independent envelopes and per-symbol basis. No outcome is supplied to Analyze, Rank or a legacy selector. Tests change only next-day OHLC: association/outcome declaration hashes change, while snapshot/package hashes, original ranks/scores, T1 ranks, captured identities and PIT class stay identical. No returns, win rates, alpha, Sharpe or P&L are calculated or serialized.

## Artifact identity and safe publication

New association envelope/content schema 1 (not a change to snapshot/package schema): policy version, source fingerprints/RunId, original strategy/version/parameter identity, decision/cutoff, target and calendar fingerprint, per-candidate outcome/basis fingerprints and category/attribution/status/reasons, PIT limitations and readiness plus aggregate coverage. Canonical typed content uses the existing B declaration SHA-256 helper and serializer settings. No volatile generation time, private input path, invented rank, fill or return is included. Console explanation stays outside canonical content.

Publication: t1-association-{fingerprint}/association.json. Bounded canonical bytes are flushed to an owned staging directory, independently parsed/verified, then atomically renamed on the same output root. Reuse verifies existing content and exact bytes; tampered/divergent preexisting content fails rather than being overwritten. A racing publisher can only reuse the matching valid package. Tests cover repeat byte identity, file tamper, rehashed duplicate candidate rejection, source/unignored output rejection, injected written/publish failures and cancellation at both boundaries; previous bytes survive and staging is removed. Self-hash verification is internal consistency, never external market authentication.

## Coverage and denominator semantics

Report includes accepted B source coverage: source files/verified snapshots/packages, linked/mismatched pairs, distinct dates/RunIds, strategy/evidence distributions, linked captured/eligible/excluded/shortlisted/truncated totals, mapped dates/calendar gaps, available envelopes, reason counts, fixture and authenticated production counts. C adds TotalCapturedCandidates and CandidateAssociations across every observed source pool (including unlinked invalid pairs), declared bars, validated fields, aligned/blocked basis, missing bars, unavailable source states, supported suspensions, lifecycle uncertainty, no execution evidence, usable-declared associations and date/strategy breakdown.

Existing B captured/eligible totals retain linked-pair scope. TotalCapturedCandidates/CandidateAssociations are observed-pool counts, not a claim about an unknown missing universe. AssociationCoverage is exact usable-declared/observed-captured (e.g. 5/5 in a fully declared fixture), with NOT_APPLICABLE when denominator is absent. It is not an authenticated source success rate or investment performance. Duplicate pair/content identities and duplicate/foreign envelopes cannot inflate counts. Source fields, source authentication, PIT availability and execution remain separate conclusions.

## Production pilot versus TEST_FIXTURE

Production: NO_AUTHORITATIVE_SAMPLE. No explicit real files were supplied. No additional local source discovery or external market call was authorized or attempted. No real pilot passed and no credentials/permission status was fabricated.

TEST_FIXTURE: operational test creates temporary snapshot/package/calendar/outcome files and an isolated Git-ignored output repository. Through the same command API, 5 candidates are associated, 5 declared bars/field sets/bases pass local checks, 2 truncated eligible members and 1 exclusion remain attributable; production authentication stays zero and overall readiness BLOCKED. Identical invocation reuses the same verified package. Fixtures are removed and never tracked as research data. Missing-source invocation with existing outcome declarations still reports NO_AUTHORITATIVE_SAMPLE.

## Tests and validation

T01-T42 mapping:

- T01-T07: complete declared association, missing/corrupt source/package, foreign RunId/hash and original-score mismatch; accepted B tests additionally cover original rank/version/cutoff attribution.
- T08-T13: full truncated pool, declared eligibility, actual exclusion reason, unknown ordering, duplicate/foreign candidate rejection.
- T14-T23: weekend/holiday evidence, partial/contradictory calendar, missing bar with unchanged target, no inferred suspension/delisting, supported suspension, incompatible basis and missing corporate actions.
- T24-T27: OHLC changes cannot affect decision identities/PIT; fixtures and declarations never become authenticated production.
- T28-T32: NO_SAMPLE and NOT_APPLICABLE, duplicate-pair rejection, distinct dates/RunIds/strategies and order-independent canonical records.
- T33-T38: deterministic bytes/reuse, tamper and self-rehashed invalid structure rejection, staged write/publish failure and cancellation, preserved source bytes, no profitability fields; real command API/JSON format/output path test included.
- T39-T41: accepted B, ranking, Classic-V6, historical and Python suites all pass; existing assertions unchanged.
- T42: unchanged 8/5 rules and isolated guard negatives pass (8 MiB boundary accepted, +1 byte rejected; >5 MiB introduced blob rejected below tree limit and after deletion; invalid-base fallback, LF/CRLF/untracked size invariance, ignored runtime data and visible source fixtures).

Executed on Windows: dotnet --info (SDK 10.0.401), restore, Release build --no-restore: 0 errors, 8 existing compatibility/nullability warnings. Full solution test --no-build: 1024 passed, 0 failed, 0 skipped (995 accepted +29 new). Python 3.12.10 compileall app and pytest: 13 passed. Actual tool --help/no-data/mode-conflict invocations executed. git diff --check passed. Size guard and Top30 diagnostics rechecked against final committed HEAD before delivery. Dependencies, limits, allowlist and workflows unchanged.

## Exact growth and acceptance

Starting tracked tree: 4287503 bytes. Proposed/final staged tree: 4355489 bytes; delta +67986 bytes; remaining 4033119 under 8388608. These include this report and are rechecked against committed HEAD. No source asset, prior test/report, market dataset, runtime output or credentials are removed/introduced to meet budget.

Gate | Status at authoring
--- | ---
A exact baseline | PASS
B accepted contracts reused | PASS
C binding preserved | PASS
D full observed captured pool | PASS
E truncated TopN | PASS
F declared calendar validation | PASS
G independent outcome isolation | PASS
H no future ranking inputs | PASS
I unavailable outcomes explicit | PASS
J basis mismatch blockers | PASS
K deterministic association identity | PASS
L immutable publication | PASS
M coverage/denominators | PASS
N production/fixture separation | PASS
O absent production samples truthful | PASS
P original algorithms unchanged | PASS
Q no performance arithmetic | PASS
R existing/new suites | PASS
S unchanged 8/5 governance | PASS isolated; committed-tree check pending
T exact-SHA remote workflows | pending commit/push
U no unauthorized data access/trades | PASS

## Remaining boundary

Source authentication, real retained decision/PIT samples, source timing/publication versions, true minute price basis, corporate-action proof, actual calendar/outcome access and execution remain unresolved. Association schema is an integrity/declaration record, not an authenticity certificate. Complete eligible ordering is not independently reconstructed. Successful local source metadata cannot promote Diagnostic to LIVE_RECORDED_PIT or ARCHIVED_PIT_REPLAY.

Forward-only develop delivery, no amend/reset/rebase/force push/history rewrite. Final SHA, exact-SHA workflow run/job IDs and clean synchronized HEAD are verified in the delivery response. STOP after T1-4-C: no profitability backtesting, ranking optimization, outperformance claim or trades. Authorized real PIT sample retention and an independently authorized coverage pilot remain the next prospective stage.
