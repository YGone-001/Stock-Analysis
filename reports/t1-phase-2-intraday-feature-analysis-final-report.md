# T1-2 — Intraday feature analysis

Verdict at commit creation: **NOT ACCEPTED pending final-SHA remote CI and size verification**. Local gates pass. The final delivery response supplies the exact containing SHA, actual workflow/job IDs and final verdict after successful completion. One forward-only development commit is intended; no future workflow results are invented here.

## Repository and baseline

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Starting HEAD and freshly fetched origin/develop both equalled `f86bca245e8bc554fd4d4169e54aeac4419edb4e`; initial worktree was clean. No applicable AGENTS.md was found. README, observation/cache/market-data contracts, both adapters, snapshot storage/fingerprints, scanner ownership, ranking records, window and dispatcher tests were reviewed before changes.
Baseline [CI 40903016861](https://github.com/YGone-001/Stock-Analysis/actions/runs/40903016861) and [size guard 40903016648](https://github.com/YGone-001/Stock-Analysis/actions/runs/40903016648) were rechecked: completed/success against that SHA. Baseline: 821 Windows .NET tests, 12 Python tests, 4,016,552 tracked bytes; 177,752 bytes remained under 4,194,304.
Final SHA and forward-only proof are recorded in the delivery response after push. No amendment, history rewrite or change to main/xdevelop is authorized or performed.

## Source capability and unit audit

Evidence consists of reviewed repository mappings and realistic deterministic HTTP/gateway fixtures. No new guarantee about upstream units or interval closure is asserted.

| Capability | Native C# /api/minute | Python /api/minute |
|---|---|---|
| Identity | EastMoney/kline/klt=1, kline/get | EastMoney/trends2/ndays=1, trends2/get |
| Source positions | Time parts[0]/f51; close price parts[2]/f53; Number parts[5]/f56; optional Amount parts[6]/f57 | Time parts[0], Price parts[2], Number parts[5], optional Amount parts[6], requested fields f51–f58 |
| Price conversion | Existing CNY contract -> checked milli-CNY -> typed /1000 | Existing CNY contract -> to_milli -> typed /1000 for identified source |
| Price basis | fqt=1 requested | trends source-labelled price; cross-family adjusted-price equivalence unverified |
| Volume/amount | Raw nullable nonnegative quantities; units and incremental/cumulative meaning unverified | Raw nullable quantities; units and incremental/cumulative meaning unverified |
| Timestamp/interval | One-minute request does not verify start/end label or completed-event semantics | Labelled trend does not verify interval closure or incremental semantics |
| Date coverage | Requested beg/end filtering, availability not guaranteed | Latest source day only, ndays=1; filtering does not establish arbitrary historical coverage |
| Cache | Transport/cache metadata retained; missing original availability fails closed | Ten-second memory cache retains original receipt and reports cache use |
| Instruments | Normalized six digits with explicit market prefix; captured universe remains selected A-share TopN | Explicit sh/sz routing; index availability not guaranteed |

Enabled price scaling is backed by the existing typed PriceYuan contract and conversion fixtures, not a new feed-quality promise. Unknown quantity units remain unknown; production capture supplies no quantity capability proof, so production volume acceleration and trade VWAP remain Unavailable.
The configured comparison identity is **sh000001 (SSE Composite)**. Native HTTP fixture verifies secid=1.000001; Python test verifies sh000001 -> 1.000001 and sz000001 -> 0.000001. Bare 000001 is rejected. Routing proof is not proof of live index coverage. Python minute cache keys currently omit market prefix, so no production benchmark request is performed through that cache. Existing snapshots contain no index series: UI reports missing benchmark evidence instead of retrospectively acquiring it.

## Feature definitions and availability

The pure engine returns values with status, units, actual window/effective time, contributing labels, source and missing-data reason. Decimal arithmetic is used. No metric becomes a filter, score, recommendation or predicted return.

- **5m/15m returns:** `(P(T)/P(reference)-1)*100`, displayed as percent. Default timestamp tolerance is zero. An explicitly configured earlier-only tolerance may be 0–30 seconds; actual endpoints and policy are retained. No future-nearest reference or interpolation. Internal gaps exceeding one minute plus tolerance, or mixed provider price bases, make returns unavailable.
- **Persistence:** nonnegative changes divided by eligible exactly-one-minute adjacent transitions within the preceding 15 minutes. The denominator is exposed. Missing/bad/conflicting transitions are excluded, not counted as flat; cross-provider transitions are excluded.
- **Pullback:** `(latest / maximum eligible observed afternoon price - 1)*100`, normally zero/negative. Window starts at the first eligible observed afternoon label, not an asserted full-day high. Quote/daily OHLC high is never an input. Mixed provider bases are unavailable.
- **RS5m/RS15m:** stock return minus sh000001 return, independently validated with matching trading date and exact actual endpoints. Controlled stock +2% versus index +1% gives +1 percentage point. Missing, shifted, wrong-date or acquired-later index data is Unavailable while stock price results remain visible.
- **Volume acceleration:** sum in `(T-5,T]` / sum in `(T-10,T-5]`, using ten exact consecutive minute-end labels and verified incremental units. Zero previous volume is Undefined. Capability proof identifies provider/symbol, unit, interval duration/end interpretation, incremental semantics and evidence reference. A bad quantity outside the ten-minute window does not erase valid recent volume evidence.
- **True trade VWAP:** verified actual traded amount converted to CNY / verified actual shares, over the disclosed contiguous nonoverlapping observed window. Requires verified amount semantics, compatible price/amount basis, declared conversions and positive shares. Never computed by weighting minute closes. A fixture whose amount/share average is 11 while closes are near 10 returns 11. Separate declared 100-shares/unit and 1000-CNY/amount-unit fixture converts to 10 CNY; neither conversion is claimed for EastMoney production fields.
- **Historical same-clock RVOL:** complete current afternoon volume through T / mean volume through the same clock on at least three distinct earlier known trading days. Policy permits 3–20 samples; inputs are capped at 20 sessions. Window is `(13:00,T]`, with equal clock, complete minute-close coverage, compatible volume basis and original availability by each historical cutoff. Missing/non-trading/duplicate-date/shifted/late-acquired samples are excluded. Three retained one-share/minute sessions versus current two shares/minute produce ratio 2, sample count 3. No full-day volume substitution or ingestion subsystem exists. Production lacks this dataset and shows Unavailable.

All computed values remain **Diagnostic**. Arithmetic success never promotes original snapshot certification. Quantity, benchmark and historical fixture capabilities are distinct from production availability.

## Point-in-time and session selection

Analysis takes an explicit DateTimeOffset cutoff. UI uses the saved cutoff. A later boundary than the snapshot supports is rejected; earlier boundaries still require original receipt/availability by that time. Existing cutoff validation checks China trading date, future and contradictory evidence, stale provenance and unknown availability. Receipt time never replaces an event label.
Price labels are limited to the afternoon domain [13:00,14:57), with effective endpoints at/after 14:30 and no more than one minute behind cutoff. The exact lag boundary and one second beyond are tested. 14:57 and 15:00 closing auction labels are excluded. Legacy automatic scan scheduling is unchanged.
Invalid prices, wrong day, future/post-cutoff acquisitions, causal contradictions, failed sources and missing original availability cannot contribute. Identical duplicates count once; conflicting duplicate labels are excluded and explained. Raw records remain attributable, and exclusions remain warnings. Normal older reference labels are not rejected merely for being 15 minutes before T; latest-endpoint freshness and reference eligibility are separate checks.

## Real application workflow, provenance and safety

Enable the existing TopN snapshot toggle, run Classic or V2–V6, then open **盘中特征诊断** and click **分析已保存快照（不补取行情）**. The command validates the actual saved artifact, runs pure CPU analysis and displays code/name/time, 5m/15m returns, persistence, pullback, RS, volume acceleration, trade VWAP, RVOL and evidence status. Selected-row details expose every metric's source/window/reason, original availability/receipt/cache/fallback evidence, policy and conversion proof.
The actual shared-dispatcher WPF test captures fixture minute data through the service, invokes the command, and asserts the real DataGrid displays +2% plus missing-metric reasons. It asserts unchanged snapshot bytes and exactly one minute acquisition: analysis does not collect the same data again.
Results identify canonical snapshot fingerprint (independently checked), scan GUID/strategy/version/parameter identity, symbol/date/cutoff, computation version, policy, raw source records and optional benchmark/quantity/historical evidence. Results are in memory and reproducible by pure functions. Original schema-1 files, serializer, loader and fingerprints are unchanged.
Analysis has a separate CTS/generation and awaited bounded CPU task. Cancel, window close or a newer scan prevents late publication; a new scan clears old analysis and identifies its new run. Previous saved artifacts remain explicitly owned by their original run. Corrupt files/feature errors do not change Completed legacy strategy state or rows. Missing benchmark/quantity metrics do not remove price-only results. No network acquisition, polling, unobserved task or capture overlap is added. Bounds: 20 captured symbols, 500 minute rows/symbol and at most 20 optional historical sessions.

## Files changed

- Core/StockData/IntradayFeatureAnalysis.cs: small typed feature/evidence/policy records and pure computations, independent of ranking.
- ViewModels/SparrowIntradayFeatures.cs: local artifact command, ownership/cancellation, in-memory table/details.
- ViewModels/SparrowViewModel.cs: command-state and invalidation hooks only; scan mathematics unchanged.
- Views/SparrowWindow.xaml: separate diagnostic tab; existing result grid unchanged.
- IntradayFeatureTests.cs: arithmetic/time/provenance fixtures and actual WPF workflow.
- Python test_minute_evidence.py: explicit instrument market-prefix routing test; production Python unchanged.
- This root reports file. No docs directory, screenshots, binaries, dependencies, removed assets or historical dataset changes.

## T01–T38 evidence

| Tests | Actual offline evidence | Outcome |
|---|---|---|
| T01–T04 | Positive/negative/flat 5m and positive 15m return with exact endpoints | PASS |
| T05–T07 | Missing references, internal gap, earlier-only configured tolerance and bounds | PASS |
| T08–T10 | Wrong date/future labels, later acquisitions and unsupported snapshot boundary excluded | PASS |
| T11–T12 | Zero/negative/null prices and conflicting duplicates excluded; identical duplicate counted once | PASS |
| T13–T14 | Persistence 14/15 and 12/13; gaps not counted as flat | PASS |
| T15–T16 | Observed pullback -10% despite quote daily high 1000 | PASS |
| T17–T21 | Aligned RS +1pp; ambiguous identity, wrong header/row date, late receipt, missing/shifted benchmark refused | PASS (7 cases) |
| T22–T28 | Unknown units/end semantics disabled, verified ratio 2, zero denominator Undefined, actual amount/share VWAP 11 rather than closing approximation, missing history explicit | PASS |
| T29 | Three equal-clock retained sessions produce RVOL 2; shifted clocks/non-trading/late-acquired samples excluded | PASS |
| T30–T31 | Fingerprint preserved and checked, unverified labels yield price Diagnostic only, auctions excluded, no certification promotion | PASS |
| T32–T35 | Actual scan/capture/WPF grid values and reasons; cancellation, new scan, close and corrupt-file failure retain legacy results | PASS (5 cases) |
| T36–T37 | Full original numerical strategy and historical suites; production rules/replay/fingerprint files unchanged | PASS locally |
| T38 | Unchanged committed tree/blob guards | Final commit and remote receipt required |

Additional assertions cover endpoint lag, mixed provider bases, native/Python market routing and explicit verified conversion factors. No live network, market-open conditions or personal screenshot paths are required.

## Actual local validation and size

Windows .NET SDK 10.0.401: dotnet --info and restore AIHelper.sln succeeded; Release build --no-restore succeeded with zero errors and six existing NU1701 dependency warnings. Full dotnet test AIHelper.sln -c Release --no-build: **853 passed, 0 failed, 0 skipped** (19 seconds), including 32 added cases. Python 3.12 environment from repository requirements: compileall app succeeded, pytest -q: **13 passed** (3.32 seconds).
An intermediate cancellation test caught a terminal status left at “analyzing” for a late non-cooperative computation; it was fixed and the complete suite rerun. No test was suppressed. git diff --check and scoped review pass before commit.
Tree before: **4,016,552 bytes**. Final tracked tree: **4081994 bytes**. Budget remains **4,194,304 bytes**, individual blob limit 5 MiB. No deletion or guard change is required. Original Test-LargeBlobs.ps1 and Measure-RepositorySize.ps1 are run against committed final HEAD before delivery.

## Gates and final receipt

| Gate | State at commit creation |
|---|---|
| A baseline | PASS: exact SHA and clean start |
| B point-in-time | PASS: cutoff, availability, causal/session validation |
| C prices | PASS: arithmetic and actual diagnostic UI |
| D RS | PASS with aligned controlled benchmark; production explicitly missing benchmark |
| E volume | PASS: production unknown units not used; verified fixtures tested |
| F RVOL | PASS: no fabricated production history; same-clock fixture tested |
| G user feature | PASS: actual saved artifact to real WPF table/details |
| H provenance | PASS: fingerprint/run/time/source/policy/conversion evidence |
| I compatibility | PASS locally: old snapshots, strategy and research suites |
| J runtime | PASS: independent CPU task/late-result guards, zero new network work |
| K tests | PASS locally: 853 .NET, 13 Python |
| L size | Final committed guard/measurement required; thresholds unchanged |
| M remote CI | PENDING final-SHA Windows/Python and size workflows |
| N scope | PASS: features only, no ranking/trading/returns simulation |

After push, exact final SHA, workflow URLs/IDs/jobs/status/conclusions, clean worktree and HEAD==origin/develop are independently verified and recorded in the delivery response. No acceptance is claimed while workflows are queued/running. No report-only follow-up commit is planned.

## Limitations and STOP

Production snapshots have no benchmark series, no verified quantity/interval capability and no complete historical same-clock dataset. Those metrics remain explained Unavailable; supported price diagnostics remain visible. This is not certified historical decision data, a trading signal or proof of predictive value. Afternoon-only windows, strict default alignment and one-minute lag policy can leave sparse/old data unavailable. Python market-prefix cache separation must be addressed before future benchmark acquisition through that cache.
STOP after T1-2. No T1-3/T1-Rank, weight tuning, next-day return optimization, prediction, recommendations or automated orders.
