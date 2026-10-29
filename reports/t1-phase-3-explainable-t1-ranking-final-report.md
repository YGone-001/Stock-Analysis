# T1-3 — Explainable late-session T1-Rank / PriceOnlyV1 research ranking

Verdict at commit creation: **NOT ACCEPTED pending final-SHA remote CI and size verification**. Local gates pass. The final delivery response supplies the exact containing SHA, actual workflow/job IDs and the final verdict after successful completion. One forward-only development commit is intended; no future workflow results are invented here.

## Repository, baseline and isolation

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Starting HEAD and freshly fetched origin/develop both equalled `0d2e7cf490802f6963aed1db2ed293ddcb92edb4`; initial worktree was clean. Baseline CI run 40910376650 and size-guard run 40910376649 were completed/success for that SHA. Baseline: 853 Windows .NET 10 tests, 13 Python tests, 4,081,994 tracked bytes, 112,310 bytes remaining under 4,194,304.
The implementation was written after reading `IntradayObservation`, `IntradayFeatureAnalysis`, `IntradaySnapshotCapture`, `SparrowIntradayFeatures`, `SparrowViewModel`, `SparrowWindow.xaml`, the ranking/CSV-export services and the existing dispatcher tests. No Classic–V6 selection mathematics, `RankClassic`/`RankV2` scores, legacy result groups, scan schedule, snapshot schema/serializer, or ranking-profile constants were modified. There is no V7, no ML, no trading integration, and T1-4 was not started. Final SHA and forward-only proof are recorded in the delivery response after push.

## What was built (R1, R7)

A standalone, independent ranking layer `T1-Rank / PriceOnlyV1` in `AIHelper.Core.StockData.T1PriceOnlyRanking` consumes the real T1-2 feature results plus the retained snapshot and produces an explainable diagnostic research shortlist. Conceptual types: `T1RankingRequest` (model/version, snapshot fingerprint, scan RunId, trading date, cutoff, feature computation version, shortlist TopN, scoring parameter fingerprint, captured coverage), `T1RankingCandidate` + `T1RankingExplanation` (data time, original strategy identity/rank/score, profile, raw final score, component scores, weighted contributions, evidence status, snapshot fingerprint, data-quality warnings), `T1RankingExclusion` (machine-readable `T1RankingExclusionCode` + Chinese reason), `T1RankingCoverage`, `T1RankingResult`. Original strategy `Rank`/`TotalScore`/`RankingProfile` are never overwritten; the snapshot and feature records are byte-identical after ranking (T24).

## Frozen scoring formula (R3, Gate D)

Weights (frozen in `T1PriceOnlyRanking.WeightTable`): 5m momentum 20, 15m momentum 20, price persistence 25, observed-high proximity 20, chase-risk moderation 15 — fraction form sums to exactly 1.00 (100 points, T02). Components are decimal, normalized to [0,100], percent units for returns/pullback and fraction for persistence:

- M5 = clamp(100·(R5+1)/3); M15 = clamp(100·(R15+2)/5); Persistence = clamp(100·P); HighProximity = clamp(100·(PB+3)/3); ChaseModeration = clamp(100·(3−max(0,R5))/2)
- Final = 0.20·M5 + 0.20·M15 + 0.25·Persistence + 0.20·HighProximity + 0.15·ChaseModeration; contributions are exposed per component and sum exactly to the final score (T25).
- The standard fixture (R5=R15=+2%, persistence 1, pullback 0) yields M5=100, M15=80, Persistence=100, HighProximity=100, ChaseModeration=50 → **88.5** exactly (T01).

This is a documented research baseline, not a trained or optimized model; the mappings and the scoring-parameter fingerprint (SHA-256 over model/version/mapping/weights JSON) are pinned by tests and must not drift silently.

## Eligibility policy (R2, Gates C/E)

A symbol is scored only if it belongs to the verified source snapshot, the supplying feature result matches it (identity, name, trading date, RunId, snapshot fingerprint, cutoff, computation version `intraday-features-v1`), its effective endpoint is in `[14:30, 14:57)` China exchange time and not after the cutoff, all four required metrics (5m/15m returns, persistence with positive eligible-transition denominator, pullback) are available and finite within declared bounds (|R5|,|R15| ≤ 20; 0 ≤ P ≤ 1; −100 ≤ PB ≤ 0), no required contributing observation was acquired after the cutoff, no observation is missing from the retained provenance, and no required window mixes provider price bases. The snapshot fingerprint is re-derived inside the engine from the canonical serialization (same bytes as store and feature analysis), so ranking is impossible against an unverified snapshot. Any failure yields an explicit exclusion with a code such as `EndpointMissing`, `EndpointOutsideWindow`, `Return5mUnavailable`, `PersistenceDenominatorNonPositive`, `MetricOutOfRange`, `ObservationAcquiredAfterCutoff`, `ObservationProvenanceMissing`, `MixedSourceBasis`, `FingerprintMismatch`, `RunIdMismatch`, `TradingDateMismatch`, `CutoffMismatch`, `FeatureVersionUnsupported`, `FeatureResultMissing` — never a partial score (T07–T19). A valid run may rank zero symbols (T22).

## Missing-dimension governance (R4)

`EvidenceClass = Diagnostic` and `RankingProfile = PriceOnlyV1` are carried on every result. `NotIncluded` names RelativeStrength, Volume, Vwap and HistoricalRvol with evidence-aware reasons: with no aligned benchmark series in the snapshot the UI states that the snapshot does not contain the aligned sh000001 minute series (the answer to "why is RS absent"); unverified minute quantity units and absent same-clock historical baselines are named likewise. If a future snapshot ever carries such evidence, PriceOnlyV1 still excludes it and the reason says the frozen configuration does not include the dimension (T20, T21). No unavailable feature is normalized to zero and no weight is redistributed.

## Coverage and scope (R5, Gate E)

The ranking is a secondary ordering inside the captured TopN pool only. Coverage discloses captured candidates, eligible candidates, excluded candidates, requested shortlist size and actual shortlist size (T23); TopN truncation never changes captured or eligible counts. The UI and every artifact state that the shortlist is not the best of the market. Empty captures produce valid empty results.

## Deterministic ordering (R6, Gate G)

Sort: descending raw PriceOnlyV1 score, descending persistence, descending 15m return, ascending ordinal symbol code. All four levels are pinned by tests, including score ties broken by persistence and 15m return against code order (T05, T05b, T05c, T06). Display rounding is fixed four-decimal text and never feeds the ordering; repeated analysis produces byte-identical output (T26, T27).

## Real WPF workflow (R8, Gate H)

A new tab **T1 研究排序** sits next to 盘中特征诊断. The user runs an existing Classic/V2–V6 scan with snapshot capture enabled, clicks 分析已保存快照, then **PriceOnlyV1 排序（本地计算，不取数）**. The command reloads the saved snapshot, re-verifies the fingerprint, runs the pure engine on the retained feature results (captured at analysis time), and fills one grid: ranked rows (T1排名, 状态, 代码, 名称, 原策略, 原排名, PriceOnly评分, 5m/15m收益, 持续性, 回撤, 追高缓和, 证据) followed by excluded rows with a 排除原因 column. A selected-row pane exposes the full explanation (raw precision, component contributions, warnings, fingerprint). A visible disclaimer marks the output Diagnostic, never evaluated against next-day outcomes, not a trading signal, and limited to captured TopN coverage. The legacy result grid and double-click chart behavior are untouched. The shared-dispatcher WPF test drives the real scan → capture → feature → rank → export path through controlled fixture providers (T30, T31) and asserts the snapshot file bytes never change.

## Reproducible artifact (R9, Gate I)

`T1RankingArtifact.Write` produces a CSV (user inspection: ranks, components, contributions, exclusions) and an authoritative JSON sidecar (schema version, request identity, profile, evidence class, coverage, NotIncluded, disclaimer, shortlist, exclusions) under `DataSavePath/t1-ranking/`, named `{RunId}-priceonlyv1.{csv,json}`. Content carries no volatile export timestamp; the content fingerprint is SHA-256 over the canonical content bytes and is stored in the envelope. Writes are bounded (2 MiB cap), temp-file + atomic-move crash-safe, and the sidecar is written first so a cancelled export can never leave a bare CSV masquerading as a result (T28, T29). Source snapshots and schema-1 files are never mutated. No credentials, absolute developer paths or raw private requests are included.

## Runtime ownership and cancellation (R10, Gate I)

The ranking has its own generation counter, CTS, and busy flag; no overlapping ranking operations (rank and export share the gate). Window close, cancel, a newer scan, or a re-analysis invalidates pending work; late completions cannot publish (T32, T33). `CancelScan` covers the new operation; `CanExecute` gates require an existing verified snapshot and completed feature analysis. A corrupt or missing file fails the ranking status while `ScanOutcome` stays `Completed` and legacy rows remain (T34). The ranking is local and CPU-bound — no network calls, no new always-running service; the core is pure and the ViewModel is a narrow command wrapper.

## Test matrix and results (R11, Gate K)

`tests/AIHelper.Services.Tests/T1RankingTests.cs` — 28 tests, all deterministic and offline (fixed clock, fixture providers, temporary paths, real shared-dispatcher WPF window):
T01 exact formula + request identity · T02 weight sum · T03 [0,100] bounds + out-of-range exclusion · T04 percent/fraction interpretation · T05/T05b/T05c/T06 deterministic ties (score, persistence, 15m return, code) · T07–T10 missing-return/persistence/pullback exclusions · T11–T13 endpoint window boundaries (14:29 missing, 14:30 ranks, 14:56 ranks, 14:57 outside, auction-only cannot rank) · T14/T17/T18 date/RunId/version/cutoff mismatches fail closed · T15 post-cutoff acquisition and provenance-missing exclusion · T16 fingerprint mismatch fails closed · T19 mixed price basis (hand-built and real pipeline) · T20 available RS disclosed but never scored · T21 unknown volume units never enter score · T22 valid empty shortlist · T23 captured-only coverage + TopN truncation · T24 original strategy/snapshot untouched · T25 contributions sum exactly · T26/T27 repeat determinism (byte identity) · T28 export byte reproducibility + fingerprint · T29 cancelled export publishes nothing · T30 real WPF scan→snapshot→feature→rank→export workflow · T31 WPF shows score, limitations and exclusion reasons · T32 newer scan invalidates stale ranking · T33 window close prevents late publication · T34 missing/corrupt snapshot fails closed with legacy results intact. T35–T37 are the existing Classic–V6, historical replay and snapshot compatibility suites; T38 is the size guard — all executed below.

Executed validation (Release, .NET 10, Windows):

- `dotnet build AIHelper.sln -c Release` — 0 errors (12 pre-existing NU1701-style warnings only).
- `dotnet test AIHelper.sln -c Release --no-build` — **881 passed / 0 failed** (853 baseline + 28 new).
- `python -m compileall app` — OK; project `.venv` `python -m pytest -q` — **13 passed**.
- `tools/Test-LargeBlobs.ps1 -MaxSizeMiB 5 -MaxTreeMiB 4.00` — guard passed at baseline HEAD; projected committed tree ≈ 4,166,000 bytes (new sources/tests 70,103 bytes + 5,433 bytes modified-file deltas + this report), remaining ≈ 27,000+ bytes. The committed-SHA size check is re-verified remotely and in the delivery response.
- `git diff --check` clean; no whitespace errors.

## Known limitations (unchanged, not solved here)

Benchmark minute series are still not captured (production RS unavailable); minute volume/amount units remain unverified; true production VWAP and RVOL remain unavailable; snapshots cover selected TopN only; the Python minute cache still does not distinguish market-prefixed instruments; historical 14:30 certification is unsupported. PriceOnlyV1 scores are heuristic diagnostics with no demonstrated relationship to next-day returns. These limitations are disclosed in status text, details, and every artifact rather than papered over.

## STOP boundary

This phase stops after T1-3. No next-day-return backtesting, weight tuning against future outcomes, profit models, trading execution, or historical certification claims are included. T1-4 (historical point-in-time replay and next-trading-day return validation) requires separate authorization after independent review of this phase's sources, evidence, tests and CI.
