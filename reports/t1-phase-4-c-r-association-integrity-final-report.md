# T1-4-C-R — Association Integrity Corrections Final Report

## 1. Executive verdict

Local correction gates pass. The mixed-exchange identity defect and self-rehashed
readiness contradiction were reproduced before corrections. Association schema 2
now selects calendars per candidate and independently replays declared evidence
before accepting derived records or counters. This is internal semantic consistency,
not market-source authentication. Production remains BLOCKED. No real pilot ran.

## 2. Repository identity

- Repository: https://github.com/YGone-001/Stock-Analysis.git
- Branch: develop; origin fetch/push URLs agree with the repository above.
- No applicable AGENTS.md was present in the repository or inspected ancestors.
- No amend, reset, squash, force push, or accepted-history rewrite was used.

## 3. Authoritative starting SHA

`dc38cc069fc4a55734c91ffaad16b707ccea150a`.
Initial remote/branch/status/fetch/revision gate confirmed a clean worktree and
HEAD == origin/develop == that exact SHA. Baseline: 1024 .NET tests, 13 Python tests,
4,355,489 Git tracked-tree bytes, 8,388,608-byte tree maximum, independent 5 MiB blob maximum.

## 4. Final delivery SHA

Pending the forward-only delivery commit at report authoring. The delivery SHA is
the commit containing this report, resolvable with
`git log -1 --format=%H -- reports/t1-phase-4-c-r-association-integrity-final-report.md`.
The final delivery response supplies its literal full SHA and exact-SHA remote results.
This avoids a self-referential embedded commit hash or an inaccurate prior-head claim.

## 5. Original failure reproduction

Two deterministic tests were added and executed against the accepted implementation
before correction (2 tests, both failed). The mixed pool was created through the
accepted snapshot store, feature analysis, pure ranking and immutable ranking package.
Its identities include 600001.SH, 000001.SZ, 300001.SZ, 430001.BJ and 600005.SH.

- T01: the 000001.SZ outcome had correct symbol, source instrument and exchange;
  supplying only SSE calendar evidence caused SYMBOL_IDENTITY_MISMATCH. The failing
  assertion found that reason. After correction it has CALENDAR_NOT_VERIFIED, without
  the false identity mismatch; its matching calendar was never supplied.
- Basis contradiction: a valid artifact was modified to declare incompatible basis
  and PRICE_BASIS_MISMATCH while retaining AssociationUsableDeclared=true. Outcome
  sample copies and basis counters were synchronized and the canonical outer hash
  recomputed. The old verifier returned true; the rejection assertion failed.
  The schema-2 verifier rejects the same contradictory claim.

No market requests or credential reads were needed for either reproduction.

## 6. Multi-exchange calendar contract

T1AssociationInput adds an optional bounded exchange-keyed Calendars mapping.
Only SSE, SZSE and BSE are accepted, at most three declarations; each key must equal
its evidence Exchange. Existing Evidence.Calendar remains a backward-compatible
single-exchange option. Supplying both is invalid. Duplicate CLI exchange entries,
unsupported keys/versions and impersonated keys fail closed.

The offline CLI adds --calendars with T1ExchangeCalendarInput schema 1:
SchemaVersion=1 and Calendars=[T1CalendarEvidence, ...]. This collection version is
independent of association schema 2. Existing --calendar commands still work.

## 7. Candidate-specific calendar mapping

Each captured symbol is resolved through the existing instrument identity contract.
Only its matching declaration is passed to MapNextTradingDay; the resulting mapping
is passed unchanged to CheckOutcome. Candidate Exchange and Calendar retain status,
target date, declaration fingerprint and reasons. No cross-exchange fallback exists.

Fixtures prove SSE/BSE mapping to 2026-11-02 and SZSE independently mapping through
closed dates to 2026-11-06. The mixed summary uses MULTIPLE and a null target;
candidate dates remain distinct. Missing bars do not advance targets. Missing SZSE
calendar evidence blocks only the two SZSE candidates and retains all five identities.

## 8. Association schema compatibility

New envelope/content versions are explicitly 2. Stable Derivation facts are stored
alongside the derived candidate and coverage claims. Null new fields are omitted
from legacy record serialization, preserving legacy canonical content.

VerifyAssociation and default package verification/publication accept schema 2 only.
VerifyLegacyAssociationIntegrity explicitly retains the weaker schema-1 structural
diagnostic, rejects schema-2 fields under a schema-1 header, and grants no new readiness.
No in-place conversion or automatic migration exists. A schema-1 immutable empty
package fixture retains identical bytes before and after new mixed schema-2 publication.
Historical snapshot schema 1 and ranking package formats are unchanged.

## 9. Independent verifier corrections

The outer content hash is necessary but insufficient. Schema-2 verification:

1. Checks bounded source facts, unique captured identities and original ranks,
   finite original scores, source gates, source/pool relationships and blocked trust.
2. Checks linked package request run/date/cutoff/snapshot/count attribution and
   retained shortlist/exclusion projections. Shortlist ranks must be contiguous;
   source names, ranks, scores, strategies and versions must agree.
3. Replays matching MapNextTradingDay, CheckOutcome and CheckPriceBasis against
   stored raw declarations. Re-derives category, outcome symbol/state/availability,
   field validity, basis status, flags, calendar, fingerprints and all reasons.
4. Compares the complete regenerated association record against the stored record.
5. Recomputes every coverage field: source files/snapshots/packages/pairs, source
   dates/runs/pools, candidate totals, availability, fields, basis aligned/blocked,
   missing bars, unavailable sources, suspensions, lifecycle, execution blockers,
   usable declarations, ratio, reason/class/strategy maps, and date/strategy breakdowns.

SourceFileKey is a digest of the explicit absolute input path, sufficient for local
distinct-file accounting without persisting raw paths. Relocating an input changes
this identity. Source-verifier facts/projections are stored local declarations, not
independent re-authentication of original source bytes. Coherently replacing all
declarations cannot be detected as an external forgery by an unsigned self-hash.

Schema-2 nested SourceCoverage calendar counters count candidate mappings, plus a
missing-calendar blocker for records with no enumerable pool. Linked source pool
totals retain linked-pair scope. The separate accepted T1-4-B API is unchanged.

## 10. Self-rehashed invalid-content tests

Required A–J examples are covered: incompatible basis while usable; unverified
calendar while usable; failed fields while usable; invalid shortlist rank;
incorrect strategy breakdown; incorrect missing-bar totals; incorrect alignment
totals; incorrect usable totals/ratio; wrong outcome symbol; conflicting calendar
target date. Every forged content hash is recomputed.

Additional cases cover invalid categories, altered original score, duplicate source
identity, availability/source flags, reason deletion, all coverage families and
source counter maps. Eight cases change raw stored basis/calendar/OHLC/symbol/
target/source/session/request declarations while leaving derived claims unchanged,
proving that verification replays the accepted checks rather than trusting flags.

## 11. Source/ranking isolation tests

Mixed-calendar/holiday and OHLC changes leave source fingerprint, package fingerprint,
captured original identities/ranks/scores, stored T1 ranking explanation/score content,
and PIT classification unchanged. Input snapshot and ranking package byte comparisons
pass. No outcomes are supplied to feature analysis or pure ranking by association.
No Classic–V6 or PriceOnlyV1 algorithm source was changed.

## 12. Full captured-pool validation

The accepted fixture retains captured=5, eligible=4, actual excluded=1, requested
TopN=2, actual shortlist=2. All five source identities remain: two shortlisted,
two eligible-truncated-declared and one excluded. Truncated identities have neither
invented rank nor exclusion. FullEligibleOrdering and IndependentEligibility remain
NOT_VERIFIED when the retained package cannot prove the full ordering.

## 13. Immutable publication behavior

The accepted staging/verification/atomic rename and ignored-output checks are reused.
Repeated identical mixed CLI inputs reuse identical schema-2 bytes. Invalid legacy
publication and tampered packages are rejected, never repaired or overwritten.
Existing failure/cancellation boundary tests still prove previous package survival.
Association outputs remain ignored temporary fixtures and are not committed.

## 14. CLI and documentation changes

The accepted --associate-t1 operation reads only explicit bounded local inputs.
--calendar and --calendars are mutually exclusive and both are protected from output
overlap. Typed collection validation rejects duplicate/unsupported declarations.
The guide includes single-exchange and mixed SSE/SZSE commands, BSE compatibility,
collection shape, missing calendars, schema versions, trust boundaries and fixture tests.
Actual executable help succeeds (0); no-input execution reports NO_AUTHORITATIVE_SAMPLE,
CALENDAR_NOT_VERIFIED, OUTCOME_SOURCE_NOT_VERIFIED, AUTHENTICATION=NOT_VERIFIED and
READINESS=BLOCKED (2). Mixed file-backed CLI publication/reuse passes in offline tests.

## 15. Files changed

- src/AIHelper.Core/StockData/T1AssociationContracts.cs: optional calendars and explicit schema-2 facts.
- src/AIHelper.Services/StockData/T1OutcomeAssociation.cs: downstream per-exchange construction and legacy boundary.
- src/AIHelper.Services/StockData/T1AssociationVerification.cs: semantic derivation and complete coverage verification.
- src/AIHelper.Services/StockData/T1AssociationCommand.cs: explicit typed collection option.
- tests/AIHelper.Services.Tests/T1AssociationCorrectionTests.cs: 48 offline correction cases.
- tests/AIHelper.Services.Tests/T1OutcomeAssociationTests.cs: two helpers made internal for fixture reuse; assertions preserved.
- tools/AIHelper.HistoricalDataTool/T1-ASSOCIATION.md: corrected operation and compatibility documentation.
- reports/t1-phase-4-c-r-association-integrity-final-report.md: this report.

No source snapshot, ranking algorithm, replay implementation, market provider,
ignore file, workflow, governance script, accepted report or existing test was removed.

## 16. Existing and new test totals

Windows .NET SDK 10.0.401, runtime 10.0.12:
dotnet --info; restore AIHelper.sln; build Release --no-restore; test Release --no-build.
Final local result: 1072 passed, 0 failed, 0 skipped (1024 accepted + 48 correction).
All accepted B readiness, C association, Classic–V6 and PriceOnlyV1 tests pass.
Build: zero errors; eight existing warnings (six package compatibility NU1701 and
two nullable CS8602 warnings in the accepted T1RankingTests). No new warning remains.

Matrix mapping: T01 red/green reproduction; T02–03 single-exchange theory; T04–05,
08,12–13,23 complete mixed fixture; T06 missing SZSE; T07,11 invalid calendars;
T09–10,14 holiday/OHLC isolation; T15–17 deterministic/legacy CLI publication;
T18–22 self-rehashed candidate/raw/counter cases; T24–25 blocked trust;
T26–28 full accepted .NET suite; T29 Python; T30 governance fixtures and guard.

## 17. Python regression results

python -m compileall app: exit 0.
python -m pytest -q: 13 passed. No Python source/dependency changes.

## 18. Exact tracked-tree size and delta

Git staged/committed blob bytes (LF normalization; generated/untracked data excluded):

- Baseline: 4,355,489 bytes.
- Final: 4413874 bytes.
- Growth: 58385 bytes.
- Maximum: 8,388,608 bytes.
- Remaining: 3974734 bytes.
- Independent introduced-blob maximum: 5,242,880 bytes (5 MiB), unchanged.

Required guard and Top-30 measurement commands pass. Before commit they correctly
measure the accepted HEAD; the staged tree was independently summed with git ls-tree.
Final HEAD is checked again after commit, including introduced blobs against baseline.

Isolated temporary Git-repository checks passed: exactly 8 MiB accepted; 8 MiB+1
rejected; introduced 5 MiB+1 blob rejected even below tree limit; introduced then
deleted oversized blob still rejected across BaseRef; invalid BaseRef fallback;
LF/CRLF and a 9 MiB untracked file do not alter the 24-byte Git-tree fixture metric.
Thirteen generated/credential/cache paths remain ignored, three source/fixture paths
remain visible; guard/workflow enforcement logic is unchanged. git diff --check passes.

## 19. Remote CI and size workflow URLs

Exact delivery-SHA run IDs/outcomes are pending at authoring and will be reported
in the final delivery response after push. Existing earlier-phase successful runs
are not evidence for this delivery.

- CI workflow: https://github.com/YGone-001/Stock-Analysis/actions/workflows/ci.yml
- Size workflow: https://github.com/YGone-001/Stock-Analysis/actions/workflows/repository-size.yml

## 20. Acceptance gate A–T matrix

| Gate | Status at authoring | Evidence |
|---|---|---|
| A | PASS | Exact clean dc38cc0 baseline, matching origin/develop |
| B | PASS | Both pre-correction tests failed for the specified defects |
| C | PASS | Matching SSE/SZSE/BSE maps; impersonation/ambiguity rejected |
| D | PASS | Independent holiday dates, no universal mixed target |
| E | PASS | Missing SZSE blocks only affected candidates |
| F | PASS | SSE/SZSE single-calendar fixtures and original CLI tests |
| G | PASS | Byte-identical source/package inputs, schemas unchanged |
| H | PASS | 5 captured / 4 eligible / 1 excluded / 2 shortlisted / 2 truncated |
| I | PASS | OHLC/calendar changes preserve source and ranking attribution |
| J | PASS | Explicit schema 2, separate legacy diagnostics, immutable publication |
| K | PASS | Regenerated candidate records from stable declarations |
| L | PASS | Complete source/candidate coverage and breakdown regeneration |
| M | PASS | Rehashed A–J contradictions plus raw-declaration/counter cases rejected |
| N | PASS | Authentication/production sample counts cannot be promoted |
| O | PASS | No profitability, execution or trading functionality |
| P | PASS | 1072 .NET and 13 Python cases; compile/build succeed |
| Q | PASS | Unchanged 8 MiB/5 MiB guards and isolated boundary checks |
| R | PENDING REMOTE | Exact final-SHA CI must complete successfully |
| S | PENDING REMOTE | Exact final-SHA size guard must complete successfully |
| T | PASS | Eight directly scoped files, no unrelated modifications |

R/S are mandatory delivery gates; pending authoring is not a success claim.

## 21. Remaining source/PIT limitations

All new market examples are synthetic TEST_FIXTURE declarations. Actual usable
production observations and authenticated production samples are zero. There is no
external attestation adapter, authenticated 14:30 receipt, certified original source,
execution evidence, fill model or performance calculation. Locally consistent outcome
association does not establish PIT availability or profitability. Production stays BLOCKED.

## 22. Clean synchronized branch

Pending delivery commit/push at authoring. Completion requires HEAD == origin/develop,
clean git status, accepted baseline as parent, and both workflows completed/success
on that same final SHA. The final response records actual results after verification.

## 23. Explicit STOP boundary

STOP after T1-4-C-R. T1-5-A is not started. No live collection, credential discovery,
real-data pilot, historical returns, weight optimization, execution or brokerage work.
