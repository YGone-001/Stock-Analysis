# T1-5-A — Real Evidence Retention and Bounded Source Coverage

## 1. Engineering verdict

ENGINEERING_READY. The existing WPF scan → capture → features → PriceOnlyV1 →
immutable export workflow now has an explicit opt-in retention action. The existing
historical tool can inspect archives, run an explicitly authorized bounded source
acquisition, import its retained evidence offline, and publish/verify associations.
All engineering gates are locally satisfied; exact delivery-SHA remote gates are
pending at authoring and must succeed before final delivery is declared complete.

## 2. Separate production pilot verdict

REAL_SAMPLE_NOT_YET_CAPTURED. No operator-selected real-market scan, original real
sample path or production source request was provided/executed in this delivery.
The actual explicit default archive inventory reported zero samples. All new
acquisition demonstrations are deterministic TEST_FIXTURE inputs or in-memory HTTP
responses. They are not real observations. Production evaluation remains BLOCKED.

## 3. Repository, starting SHA and final SHA

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch: develop.
Initial remote/branch/status/fetch checks confirmed clean HEAD == origin/develop ==
`05282f16a387adcf73b1cfd3dbf046f3031478d2`.
No applicable AGENTS.md was present in the inspected repository/ancestor locations.

The final delivery SHA is the forward-only commit containing this report, pending
commit at authoring. Resolve it with
`git log -1 --format=%H -- reports/t1-phase-5-a-real-evidence-retention-final-report.md`.
The final response supplies the literal full SHA and the actual exact-SHA workflow outcomes.
No amend, reset, squash, force push or accepted-history rewrite was used.

## 4. Exact changed-file inventory

New implementation:

- src/AIHelper.Core/StockData/T1ResearchContracts.cs
- src/AIHelper.Services/StockData/T1ResearchStorage.cs
- src/AIHelper.Services/StockData/T1ResearchArchive.cs
- src/AIHelper.Services/StockData/T1OutcomePilot.cs
- src/AIHelper.Services/StockData/T1ResearchReport.cs
- src/AIHelper.Services/StockData/T1ResearchCommand.cs
- src/AIHelper.ViewModels/SparrowResearchRetention.cs

Narrow integration changes:

- src/AIHelper.ViewModels/SparrowIntradayFeatures.cs: actual completion timing and retention invalidation.
- src/AIHelper.ViewModels/SparrowT1Ranking.cs: retained export identity/timing and matching-feature ownership guards.
- src/AIHelper.ViewModels/SparrowViewModel.cs: retention cancellation/closure/command availability.
- src/AIHelper.Views/SparrowWindow.xaml: archive-root field, retain button and status in existing T1 tab.
- tools/AIHelper.HistoricalDataTool/Program.cs: explicit early operation routing and mode-conflict detection.
- tests/AIHelper.Services.Tests/T1RankingTests.cs: one existing Configure helper made internal; no assertion removed/changed.

New validation/documentation:

- tests/AIHelper.Services.Tests/T1ResearchRetentionTests.cs
- tests/AIHelper.Services.Tests/T1OutcomePilotTests.cs
- tests/AIHelper.Services.Tests/T1PilotGatewayTests.cs
- tools/AIHelper.HistoricalDataTool/T1-PILOT.md
- reports/t1-phase-5-a-real-evidence-retention-final-report.md

18 directly scoped files. No strategy score, ranking algorithm, snapshot schema,
immutable ranking format, association schema-2 implementation/verifier, historical
replay semantics, provider subsystem, dependency, ignore rule or workflow was changed.

## 5. Reused scan and snapshot workflow

The original completed RunId, scan parameters, strategy/version and captured pool
come from the accepted saved snapshot. Existing completed feature results and the
already exported immutable ranking package are retained. Source structure, ranking
CSV/JSON integrity and source/package binding are checked by the accepted verifiers.
Retention does not call a quote/minute source, analyze replacement observations,
re-run a scan, or construct historical rankings from daily prices.

## 6. Real-scan retention integration

The existing T1 research tab exposes “研究归档目录” and “留存 T1 研究样本”. Root is
empty by default; operator action and an explicit storage root are required.
The action requires the selected completed run, matching saved snapshot, completed
features, matching ranked-feature ownership and completed export. Starting a new
scan invalidates previous retention ownership even if a previous snapshot remains
available for diagnostics. Re-analysis/ranking/export also invalidates pending work.

Preparation/copying can run in the background. The dispatcher checks generation,
CTS, run, source path, export identity and selected root before synchronous atomic
publication. New scan, cancellation and window-close tests prove a late prepared
sample is disposed without publication. Closed sessions emit no late property updates.
An advanced existing-file CLI is available, but normal operators do not author JSON.

## 7. Immutable archive implementation

Archive schema 1 is independent of frozen source schemas. Layout:

```text
<root>/<original-RunId>-<manifest-content-SHA256>/
  original-snapshot.json
  features.json
  <original-ranking-package-name>/ranking.csv
  <original-ranking-package-name>/provenance.json
  manifest.json
```

Snapshot and ranking files are copied byte-identically. Features are the existing
completed results, with full candidate coverage required; missing results are never
silently recomputed. A versioned manifest binds original identity, parameters,
source/ranking fingerprints, exact file hashes, all captured identities, counts,
quality summaries, observed timing and source/evidence limitations.

Reads/writes are bounded to 2 MiB per file. Independent post-copy verification
rechecks original source/package semantics, feature identity and all manifest
content. Directory identity must match RunId/fingerprint; unexpected components
are rejected. A single-run publication lock and atomic directory rename prevent
overwrite. Identical retention reuses existing valid bytes; divergent same-run
content fails closed. Partial staging is cleaned only within its owned root.
Archive creation time is separate provenance outside deterministic content identity.
Neither source/file hashes nor manifest self-hashes establish market authenticity.

## 8. 100-file operational retention isolation

IntradaySnapshotStore.MaximumFiles remains 100, unchanged. A deterministic test
archives an original sample, saves 105 additional operational snapshots, confirms
the original operational file was removed and exactly 100 operational snapshots
remain, then independently verifies the archived copy. Archive storage never
silently prunes old research samples and has no 100-sample retention cutoff.

## 9. Capture timestamp accuracy

Original scan start/completion, acquisition start, capture completion, requested
cutoff and every observation's event/available/observed/transport timestamps remain
in original snapshot bytes. The manifest records their independent roles and the
actual scan-completion-to-capture delay. The explicit 14:30–14:55 CST study-window
check includes scan start, cutoff and capture completion; out-of-window capture is
labelled, never backdated. A 14:30 start does not prove 14:30 source availability.

WPF now records actual observed completion of feature analysis, ranking and first
successful export. Existing-file imports leave unavailable timing fields null.
Repeated export of the same content retains the first observed export completion.
Archive time does not replace any decision-time or source-receipt timestamp.

## 10. Source-quality limitations

Each archived candidate retains symbol/exchange, original rank/score, minute count,
latest usable feature observation time, existing 5m/15m window metrics and reasons,
freshness/anomalies, cutoff consistency, source identity, unit declarations and T1
exclusion. Existing diagnostic intraday feature values are preserved; no new outcome
investment-return calculation is introduced.

Unknown units stay unknown; unverified interval closure stays unverified. Missing
timestamps produce NOT_VERIFIED cutoff consistency; contradictory/post-cutoff
receipts produce FAIL. Native minute adjustment remains UNKNOWN_BASIS. All original
failures/exclusions and eligible-but-truncated identities remain attributable.
Local timestamp consistency is not authenticated PIT availability.

## 11. Operator commands and UI procedure

Complete operator instructions are in tools/AIHelper.HistoricalDataTool/T1-PILOT.md.
Sequence: select authorized existing source → one actual late-session original
strategy scan with capture enabled → analyze retained snapshot → PriceOnlyV1 →
existing export → fill authorized archive root → click retain → copy sample directory.

From repository root, with the actual printed directory assigned to `$sample`:

```powershell
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --report-t1 --sample "$sample"
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --probe-t1 --sample "$sample" --output .\local-research-data\t1-outcomes
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --probe-t1 --sample "$sample" --output .\local-research-data\t1-outcomes --allow-network --provider tushare --calendar-days 14
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --report-t1 --archive-root .\local-research-data\t1-samples --outcome-root .\local-research-data\t1-outcomes --limit 25
```

The third command is a separate explicit operator authorization; it was not run
against a production source in this delivery. Guide includes existing gateway
setup references, file-backed offline import and the advanced original-file retain
command. The actual executable help/no-sample/inventory/conflicting-mode paths ran.

## 12. Explicit network authorization

OFFLINE is the default. No gateway/environment configuration is read on that path.
Network acquisition requires --allow-network, explicit --provider tushare and an
existing configured gateway selected through HISTORICAL_GATEWAY_URL only. No
credential discovery, personal directory scan or client token lookup exists.
Only the configured gateway owns its existing service-side credential setup.

Scope is printed before any request. One existing /health request observes whether
the service-side Tushare provider is configured; a missing credential causes no
market-provider request. The broad historical capability probe is deliberately not
called because it queries unrelated dates/market data. Actual 403/429 statuses are
preserved as permission/rate-limit failures; inaccessible gateway configuration is
SOURCE_ERROR, not an invented credential diagnosis. URLs with user info/query/
fragment are rejected; URLs/secrets are not persisted in provenance or error text.
No new retries, autonomous server or polling loop exists. Existing gateway foreground
retry evidence is retained. Client budget: 90 seconds total, 20 seconds/request,
2 MiB response buffer; redirects disabled.

## 13. Calendar and daily-source pilot API

T1OutcomePilot reuses IHistoricalMarketDataSource and the existing
HistoricalHttpMarketDataSource acquisition envelopes. It verifies one immutable
sample and rejects invalid bounds before source access: at most 20 captured symbols,
at most three represented exchanges, calendar horizon 1–31 days (default 14).
At most one calendar call per represented exchange and one exact-target daily call
per captured symbol follow the initial gateway configuration check.

MapNextTradingDay alone determines each exchange's target. No D+1 target assumption
or missing-bar date advancement exists. A target session not yet completed is not
polled; no retrieval timestamp is invented for an unmade request. Calendar failures
remain exchange-specific. Original daily acquisition/CoverageEvidence and actual
receipt times are retained. Foreign symbol/date, non-Raw data and oversized scope
fail closed. No historical-minute or market-wide request is made.

Existing suspension/security endpoints lack the required captured-pool filtering.
They are not called. Session/lifecycle metadata therefore remains unknown, and the
converter never fabricates OPEN, suspension or corporate-action NoAction declarations.

## 14. Actual provider access status

Production provider selection: NOT_SELECTED for this delivery.
Production network acquisition: NOT_RUN.
Gateway/credential availability: NOT_INSPECTED in production; no missing-credential
or access-permission state was guessed. Those conditions are reported only when
an explicitly authorized operation actually observes them.

Offline tests exercise configured/unconfigured gateway health, actual adapter
403/429 mapping, unavailable source classifications and oversized HTTP responses
through memory handlers. They make no external market request and establish no
real entitlement. Real OUTCOME_SOURCE_AVAILABLE remains NOT_VERIFIED.

## 15. Result input and accepted association behavior

Source pilot schema 1 stores the retained sample fingerprint, bounded request scope,
calendars, per-symbol target/receipt/source state and original daily acquisition.
It is immutable and content-addressed. Offline --import reads the generated file;
hand-authored large nested outcome JSON is not needed for the normal workflow.
Hash/version/bounds/identity/scope checks run before accepted schema-2 association.

The converter builds existing T1OutcomeEnvelope values and invokes the frozen
T1EvidenceReadiness.Associate, VerifyAssociation and PublishAssociation APIs.
Original source/ranking fingerprints and captured attribution stay unchanged.
Raw daily metadata cannot prove minute basis compatibility, independently establish
an OPEN session, or justify NoAction. Unknown basis/session/action metadata blocks
usable-declared comparison readiness. Daily outcomes never enter features/ranking.
Association output is content-addressed, immutable and stored under Git-ignored storage.

## 16. Available and missing data counts

Actual production: retained real samples 0; acquired production daily records 0;
observed production denominator NOT_APPLICABLE; production market coverage NOT_VERIFIED.
These are counts of delivery observations, not a claim that the market has zero data.

Representative offline mixed fixture: 5 captured, 4 eligible, 1 excluded, Top2 →
2 shortlisted + 2 eligible-truncated; independent SSE/SZSE/BSE calendars; 5 declared
daily records with full scope/valid fields → 5/5 source-declared coverage. Usable
associations remain 0/5 because minute basis/session/action evidence is unverified.
Separate fixtures retain missing calendar, one missing bar, partial coverage, invalid
fields, permission failure and rate limitation without shifting dates or inventing suspension.

Selected-sample reporting prints all required source, capture, calendar, candidate,
basis, missing-input, authentication, execution and association states. Date/strategy
inventory inspects at most 100 samples (default 25) and optionally 100 explicitly
selected outcome packages; it labels uninspected/unknown portions and missing source
inputs. It does not substitute zero for an unobserved coverage denominator.

## 17. Source authentication limitations

ENGINEERING_READY, operator-declared REAL_SAMPLE_RETAINED, declared source access
and local OUTCOME_COVERAGE_ESTABLISHED are distinct from external authentication.
TEST_FIXTURE records are labelled and excluded from real-sample claims.
Authenticated production sample count remains zero. SHA-256, local timestamps,
provider labels and internal semantic consistency cannot certify market-source
authenticity. Production PIT, execution feasibility and performance evaluation
remain NOT_VERIFIED/BLOCKED. No attestation or profitability mechanism was added.

## 18. Engineering gates A–Y

| Gate | Status at authoring | Evidence |
|---|---|---|
| A | PASS | Exact clean accepted baseline and fetched origin/develop |
| B | PASS | Accepted scan/capture/features/PriceOnlyV1/export reused |
| C | PASS | Visible opt-in retain action plus file-based CLI |
| D | PASS | Snapshot bytes and original envelope fingerprint preserved |
| E | PASS | Both ranking files copied and independently verified |
| F | PASS | Content identity, same-run collisions, atomic publication and component checks |
| G | PASS | Archive survives 105 subsequent saves under unchanged 100-file store policy |
| H | PASS | New scan/cancel/closed-window late preparation never publishes |
| I | PASS | Source timestamps preserved; completion timing separately observed |
| J | PASS | Unknown units/interval/basis and failed candidate evidence retained |
| K | PASS | Default offline operation makes no gateway/provider request |
| L | PASS | One saved decision, ≤20 symbols, bounded calendars and exact-target daily calls |
| M | PASS | Only documented gateway configuration; no credential/location discovery |
| N | PASS | Independent SSE/SZSE/BSE source mapping fixtures |
| O | PASS | Accepted next-open verifier; holiday differences retained |
| P | PASS | Missing bars retained; no target advancement or invented suspension |
| Q | PASS | Raw daily versus unknown minute basis cannot become usable |
| R | PASS | Frozen association schema-2 verifier/publisher reused unchanged |
| S | PASS | Selected-sample and bounded date/strategy inventories work |
| T | PASS | No investment-return, win-rate, performance or trading calculation |
| U | PASS | 1123 .NET tests; 13 Python tests; required build/compile commands |
| V | PASS | Unchanged 8 MiB/5 MiB guards and isolated capacity/ignore checks |
| W | PENDING REMOTE | Exact delivery-SHA CI must complete/success |
| X | PENDING REMOTE | Exact delivery-SHA size guard must complete/success |
| Y | PASS | Only listed retention/source/report integration files; frozen engines unchanged |

Pending W/X are mandatory final delivery gates, not authoring-time success claims.

## 19. Independent production gates P1–P10

| Gate | Actual status | Reason |
|---|---|---|
| P1 | NOT_RUN | No actual operator-selected late-session market scan executed |
| P2 | NOT_RUN | No genuine original production snapshot supplied/retained |
| P3 | NOT_RUN | No matching genuine production ranking package supplied/retained |
| P4 | NOT_RUN | No genuine sample population on which to certify attribution |
| P5 | NOT_RUN | No genuine original observation-time evidence retained this delivery |
| P6 | NOT_RUN | No approved production calendar request executed |
| P7 | NOT_RUN | No approved production daily request executed |
| P8 | NOT_RUN | No production outcome observation; limitations implemented and explicitly disclosed |
| P9 | NOT_RUN | No genuine source/outcome association available to verify |
| P10 | PASS | No false production/PIT/execution claim was made; this verdict does not use fixture success |

Complete the missing pilot using the guide at the next actual available trading
session, then explicitly obtain each exchange's target-session data after its close.
Do not recapture later prices and backfill them as original 14:30 availability.

## 20. New and existing .NET tests

Windows .NET SDK 10.0.401/runtime 10.0.12. Required --info, restore, Release build
--no-restore and solution test --no-build commands executed. Final local result:
1123 passed, 0 failed, 0 skipped = 1072 accepted + 51 new deterministic cases.
Build: 0 errors; existing package compatibility and two existing T1RankingTests
nullable warnings remain; no new warning introduced.

New case groups: archive/WPF 24; bounded acquisition/report/CLI 22; real HTTP adapter
with memory gateway handler 5. Coverage mapping: T01–16 retention/WPF/timing/quality;
T17–30 source bounds, exchange calendars, correct targets, access states, basis and
association isolation; T31–34 no-sample/fixture/report semantics; T35–37 all accepted
Classic–V6/PriceOnlyV1/B/C/C-R suites; T38 Python; T39–40 required governance checks.
Additional direct cases cover component tampering, renamed archive identity, extra
components, junctions, traversal, oversized files/HTTP responses and conflicting CLI modes.
All tests are offline; no network-dependent CI test was added.

## 21. Python regression

python -m compileall app: exit 0. python -m pytest -q: 13 passed.
No Python source/dependency or gateway API was changed.

## 22. Exact tracked-tree size and delta

- Baseline Git tracked-tree bytes: 4,413,874.
- Delivery Git tracked-tree bytes: 4548232.
- Exact growth: 134358 bytes.
- Tree maximum: 8,388,608 bytes; remaining: 3840376 bytes.
- Independent introduced-blob maximum: 5,242,880 bytes, unchanged.

Staged Git blobs are summed after LF normalization, excluding generated/untracked
data; HEAD guard and Top-30 measurement run again after commit. Required local guard,
measurement and diff checks pass. Isolated temporary-repository tests verify exactly
8 MiB passes; +1 byte fails; a 5 MiB+1 introduced blob fails even below tree budget,
including introduced-then-deleted history; invalid-base fallback; LF/CRLF equivalence
despite a 9 MiB untracked file. Thirteen ignored generated/credential/cache paths
and three visible source/fixture paths preserve existing policy. Enforcement scripts,
workflows/triggers and ignore rules are unchanged; no market artifacts are committed.

## 23. Final-SHA CI URL

Pending push/run ID at authoring. Workflow:
https://github.com/YGone-001/Stock-Analysis/actions/workflows/ci.yml
Final response reports the actual exact-SHA run URL and outcome; predecessor success
is not used as delivery proof.

## 24. Final-SHA size-guard URL

Pending push/run ID at authoring. Workflow:
https://github.com/YGone-001/Stock-Analysis/actions/workflows/repository-size.yml
Final response reports the actual exact-SHA run URL and outcome, including committed bytes.

## 25. Real retained sample identifiers

None available. No real identifiers are invented or taken from synthetic fixtures.
Generated test archives and outcome/association JSON remain temporary local data;
none are committed or treated as verified production observations.

## 26. Missing source permissions or paths

No genuine retained sample path or explicitly selected production acquisition was
supplied in this delivery. The documented default archive-root inventory was empty.
Production source credentials and entitlements were not inspected; absence/permission
denial was not guessed. Operator must select their authorized existing source and
storage root, retain one actual run, then explicitly authorize the bounded gateway
operation. No credential, raw market record or private local filesystem path appears here.

## 27. Clean forward-only synchronization

Pending delivery commit/push at authoring. Final completion requires develop HEAD ==
origin/develop, a clean worktree, the accepted starting commit as parent, and both
workflows completed/success on that same literal SHA. The final response records
those checks after completion. No history rewrite is authorized or used.

## 28. Explicit STOP boundary

STOP after T1-5-A. Engineering delivery does not begin T1-5-B sample expansion,
historical profitability backtesting, Original-versus-PriceOnlyV1 outcome-return
comparison, weight tuning, signal generation, brokerage integration or orders.
