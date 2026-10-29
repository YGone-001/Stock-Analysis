# Phase T1-1 — Intraday time and snapshot foundation

Verdict at commit creation: NOT ACCEPTED until final-SHA remote CI completes.
The final delivery response supplies the exact committed SHA, actual workflow URLs/job IDs and verified acceptance verdict; queued/running workflows do not pass Gate K. This report deliberately does not invent future workflow results or embed its own containing hash. One forward-only development commit is intended.

## Baseline and archival closure

Repository https://github.com/YGone-001/Stock-Analysis.git, branch develop.
Exact starting HEAD and freshly fetched origin/develop: `3c849922aea251d48bc1897928d91e35e6c1db96`.
Working tree was clean. No applicable AGENTS.md was found. README, project files, typed transport/calendar/quote contracts, both minute providers, scanner ownership, cache policies, historical fingerprint code, WPF dispatcher tests and workflows were inspected before implementation.
Baseline [CI 40881830783](https://github.com/YGone-001/Stock-Analysis/actions/runs/40881830783) and [size guard 40881830810](https://github.com/YGone-001/Stock-Analysis/actions/runs/40881830810) were independently rechecked: completed/success against that SHA, Windows 781 tests and Python 11 tests. HEAD tree 3,923,514 bytes; budget 4,194,304; remaining 270,790.
The T1-0 report receives an append-only verification addendum. StartButtonText already says scanning while running, with cancellation on its separate command, so no extra text change was needed.

## Architecture and changed files

- Core/StockData/IntradayObservation.cs: companion records for exchange event/receipt/availability/transport times, China exchange date, policy, pure cutoff validation, session evidence and schema.
- Services/StockData/MinuteObservationService.cs: /api/minute adapter through existing IStockDataProvider, source/time/unit evidence, bounded parsing and explicit failure results.
- Services/StockData/IntradaySnapshotCapture.cs: sequential TopN-only acquisition, calendar/quote diagnostics, immutable scan context, bounded atomic UTF-8 store and fingerprint loader.
- MarketDataContracts.cs: optional authoritative-calendar flag, retaining the old TradingDayResult call contract.
- TypedMarketDataServices.cs: weekday-heuristic or explicitly unavailable calendar evidence cannot authorize a trading session.
- EastMoneyStockDataProvider.cs: empty index-calendar response marked unavailable, retaining legacy is_workday behavior.
- SparrowViewModel.cs: opt-in captured at dispatch, actual run GUID and UTC start/completion, strategy/version/parameter fingerprint/TopN; capture outcome separate from completed strategy outcome; open-artifact command.
- SparrowWindow.xaml: small default-off toggle, selectable diagnostic status/path and open-file button. Existing charts/results/controls remain.
- Python eastmoney.py: source receipt timestamp retained across memory-cache reads, explicit internal-cache evidence, declared latest-day-only coverage, missing/invalid numeric values retained as null in minute responses.
- IntradaySnapshotTests.cs and Python test_minute_evidence.py: deterministic fixture coverage. Reports remain under root reports; no docs directory is introduced.

## Time and source semantics

Asia/Shanghai exchange civil time is explicit UTC+08:00, using DateTimeOffset.ToOffset independently of the host timezone. TradingDate is DateOnly. Offset-free source minute labels are parsed only with explicit formats and assigned the China offset; explicit supported offset timestamps are parsed as such. Malformed/missing labels remain absent; receipt time never replaces event time.
ScanStartedAtUtc comes from an injected TimeProvider independently of the legacy local automatic-scan policy clock. ScanCompletedAtUtc is captured after successful legacy execution/export/publication. AcquisitionStartedAtUtc and CapturedAtUtc describe the additional fetch. For live capture RequestedAsOf equals the actual completed acquisition boundary, never scan start. Historical requested cutoffs remain explicit and fail when availability/event evidence is later.
MarketDataMetadata retains source kind, legacy source ID, cache/staleness/background-refresh/fallback/freshness. C# direct minute receipt is known at the transport boundary. Python adds its original UTC receipt and internal cache-use marker, which the adapter preserves separately from the new transport receipt. Cached observations without original availability evidence remain unavailable for certification. Quote receipt is known but quote exchange-event time remains null: its old contract provides none.

## Minute response audit and limits

The two existing transports are different:
- C# /api/minute uses EastMoney kline/get with klt=1, fqt=1, requested beg/end; emits Time=parts[0], Price=ToMilli(parts[2]), Number=parts[5]. It does not emit Amount.
- Python /api/minute uses trends2/get with ndays=1 and fields f51–f58, filters returned source labels to the requested date, emits Price=to_milli(parts[2]), Number=parts[5], Amount=parts[6]. It cannot establish arbitrary-date historical coverage.
Both repository mappings demonstrably scale Price to milli-CNY; the adapter divides by 1000 only for identified EastMoney paths. Unidentified gateway price normalization remains unavailable.
No authoritative declaration of Number's hands/shares unit, Amount's currency scale or the minute label's interval closure was established. They remain raw SourceNumber/SourceAmount with explicit unknown unit labels and unverified interval semantics. No silent volume conversion or invented amount is performed. Consequently these responses remain partial even when HTTP succeeds. This is an explicit limitation, not a passing assertion of normalized volume/amount correctness.
At most 500 source rows per symbol are parsed. Duplicates retain the first occurrence and mark partial; missing timestamps and invalid/negative numeric evidence fail closed. Normalized rows sort by timestamp. Wrong dates are explicit. Empty results mean unavailable requested-date evidence, never substituted daily or 30/60-minute bars. Transport exceptions/malformed payloads remain SourceFailure.

## Calendar and eligibility

Existing IMarketCalendarService is reused. Python's network/calendar fallback still serves legacy weekday-heuristic behavior, but the new typed trust flag rejects it. C# calendar infers dates from index daily bars; an empty response now carries unavailable evidence. No network failure is called a holiday. Typed failures, malformed payloads, stale calendar evidence and untrusted heuristics are Unavailable. Explicit non-trading dates are NonTradingDay; inconsistent requested dates are Inconsistent. Calendar fixtures cover weekday, weekend and a configured holiday; no holiday API is used by tests. Index-bar calendar evidence remains a source limitation, not an official exchange-calendar guarantee.

The pure validator distinguishes Valid, Stale, FutureData, WrongTradingDate, MissingEventTime, Unavailable and Partial; retrieval additionally distinguishes SourceFailure. Default maximum event age is five minutes, a configurable research policy choice, not a feed latency guarantee. It checks event date and cutoff date, event/receipt/original availability after the cutoff, missing availability, stale metadata/age, numeric availability and declared units/intervals. UsedCache=false or HTTP success alone never establishes eligibility.
Examples: a 14:45 event cannot certify 14:30; an event downloaded at 14:45 cannot prove availability at 14:30; a retained observation originally received by T may retain that evidence when loaded later; a cached row with no original receipt remains unsupported; an unavailable historical 14:30 record remains missing.

## Actual user workflow and schema

Launch Sparrow; enable “采集 TopN 分钟诊断快照”; run Classic or V2–V6. The successful scan's captured TopN candidates are fetched sequentially through the existing transport, independently of mutable post-start UI settings. Compare/PairCompare retain their normal behavior and do not pretend to supply a unified capture ranking.
The status shows saved/failed/cancelled capture, actual UTC acquisition time, exchange date and timezone, calendar/data-quality summary and full path. “打开已保存快照” opens the JSON artifact; the status is selectable for copying. This path is exercised through the real VM on the existing WPF dispatcher with controlled scan/provider/clock and temporary storage.
Output: ConfigManager.DataSavePath/intraday-snapshots; the existing application-base fallback is retained when DataSavePath is empty. No personal path or configuration migration is introduced.
Schema 1 stores run GUID, strategy/version, SHA-256 parameter identity, TopN, scan/acquisition/cutoff times, calendar evidence/provenance, selected symbols with rank/score/profile, quote receipt and missing event time, minute source records/units, quality and coverage. Data is selected TopN only, not an all-market or unbiased historical training dataset.
The schema-1 envelope hashes deterministic System.Text.Json UTF-8 bytes with SHA-256. Loader verifies schema, bound and fingerprint. Files use run GUID names, exclusive same-directory temporary writes, durable flush and atomic move. Identical duplicate runs reuse a verified file; changed content under the same run GUID is rejected. Maximum artifact size 2 MiB; retain at most 100 store-owned GUID JSON files. The newest returned file is retained. Old store-owned temporary files are cleaned; unrelated files and existing research datasets are untouched.

Complete versus partial: the pure validator accepts fully evidenced declared-unit fixture observations. The production legacy quote contract lacks event time, and available minute units/intervals remain unverified. Current capture therefore deliberately sets Certified=false and writes diagnostic evidence only. Empty, partial, source-failed and non-trading/unknown-calendar records may be retained diagnostically. A successful legacy scan never becomes a false snapshot certification. Cancellation during acquisition writes no snapshot and preserves completed strategy results; cancellation/failure before strategy completion never enters capture. Capture/storage errors have their own visible outcome.
No existing historical dataset, fingerprint, replay or return contract is changed. Actual live observation, persisted observation and point-in-time reconstruction remain distinct; unsupported reconstruction is never fabricated.

## Offline tests T01–T28

| Tests | Evidence |
|---|---|
| T01–T02 | ExchangeDateDoesNotUseHostZone: non-China offset, UTC rollover and +08 conversion |
| T03–T05, T11–T13 | CutoffFailsClosed: null event, future event/receipt, unknown cache age, stale event, invalid numbers and unknown units; 14:45 versus 14:30 |
| T06–T10, T14, T17 | AdapterAuditsPriceAndUnknownUnitsAndCache and WrongDateEmptyAndFailureAreExplicit: price/1000 verified, raw units unknown, duplicate/invalid dates/numbers, malformed payload, failure and unavailable older dates |
| T15–T16 | CalendarKnownAndUnknownAreSeparate and FixtureCalendarControlsEligibilityOnWeekendsAndHolidays: explicit holiday/weekend/weekday, untrusted fallback, unavailable/malformed calendar |
| T18–T21, T27 | ExistingScanCaptureWorkflowIsOptInAndCapturesStartSettings: default off, real JSON file, real run times and captured V2/Top1 after UI changes to V6/Top2; visible timestamp/path/status and open command |
| T22 | CancellingAcquisitionPreservesSuccessfulScanButWritesNoSnapshot; cancelled/failed strategy runs never add an artifact |
| T23–T25 | PersistedObservationRoundTripsAndRejectsCorruption: partial quote evidence, fingerprints, duplicate collision, tamper/truncation/schema refusal; CancellationAndRetentionAreBounded |
| T26 | Full existing strategy and historical suites; rule, ranking and historical engine sources unchanged |
| T28 | Original committed HEAD tree/blob guards, unchanged thresholds; final results in delivery receipt |

Local Windows .NET SDK 10.0.401: dotnet --info; restore AIHelper.sln; build -c Release --no-restore succeeded with zero errors and six pre-existing NU1701 compatibility warnings. Final dotnet test -c Release --no-build: 809 passed, zero failed, zero skipped. Python 3.12.10 temporary environment installed from requirements.txt: compileall app succeeded, pytest -q: 12 passed in 2.27s. All tests use controlled providers/calendars, clocks and temporary directories. No network or personal directories are required.
Repository size before: 3,923,514 bytes. Final staged/committed tree: 3979169 bytes; budget remains 4,194,304 bytes. No assets/tests deleted and no check weakened. Measure-RepositorySize and Test-LargeBlobs are run against the committed final HEAD before delivery. git diff --check and scope review precede commit.

## Gates and delivery evidence

| Gate | Local state / remote boundary |
|---|---|
| A baseline | PASS: exact SHA and clean worktree |
| B time | PASS: distinct event/receipt/availability/scan/acquisition fields |
| C provenance | PASS: available provider/cache/fallback evidence retained; unknown explicitly absent |
| D calendar | PASS: known/unknown/non-trading distinct; heuristics never certify |
| E minute adapter | PASS for normalized verified price and explicit unknown units/intervals; no unsupported unit claim |
| F snapshot | PASS: opt-in real artifact and readable application workflow; current evidence is diagnostic |
| G no false history | PASS: missing historical evidence remains unavailable |
| H compatibility | PASS locally: all existing tests, no numerical or research semantics change |
| I tests | PASS locally: 809 .NET, 12 Python; final remote receipt required |
| J size | Final committed guard required; unchanged budget, exact count above |
| K remote CI | NOT ACCEPTED while pending; delivery response must record exact final SHA, workflow/job IDs and success |
| L scope | PASS: no factors/scoring/returns/orders/dashboard redesign |

Final remote SHA, workflow URLs/jobs/conclusions, clean worktree and HEAD==origin/develop are independently checked after push and recorded in the delivery response. No report-only follow-up commit is planned; no amend or force push is used.

## Known limits and STOP

No trustworthy quote event timestamp is currently supplied, minute volume/amount units and interval closure are unverified, direct C# minute Amount is absent, and upstream arbitrary historical minute availability is not guaranteed. Current snapshots are diagnostic and not T+1 certified. The UI does not implement an all-market collector or final dashboard. Five-minute freshness is a research policy choice. Existing local automatic-scan weekday/time rules and daily K-line cache semantics remain unchanged and are not certification evidence. No data or profitability claim is made beyond available provenance.
STOP at T1-1. No VWAP/RVOL, intraday strength/Alpha scoring, T1-Rank, buy/sell advice, brokerage API, next-day returns, profitability optimization or T1-2 work.
