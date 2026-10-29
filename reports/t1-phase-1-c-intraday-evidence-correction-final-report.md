# T1-1-C — Intraday evidence integrity corrections

Verdict at commit creation: NOT ACCEPTED while final remote verification is pending.
Local correction gates pass. The final delivery response records the exact final SHA, real workflow/job IDs and final acceptance verdict after both workflows complete. It is the receipt for the containing commit, whose future hash and workflow IDs cannot be embedded here without another commit. Only one forward correction commit is planned.

## Repository and baseline

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Starting HEAD and freshly fetched origin/develop both equal `cc8b463159db45efc6aac51652f9ab6b9dc0274f`; initial worktree clean. README and actual relevant contracts, scanner, window, transports, serializer and dispatcher tests were inspected; no applicable AGENTS.md was found.
Baseline [CI 40898844500](https://github.com/YGone-001/Stock-Analysis/actions/runs/40898844500) and [size guard 40898844808](https://github.com/YGone-001/Stock-Analysis/actions/runs/40898844808) were independently rechecked: completed/success for that exact SHA. Baseline totals: 809 .NET and 12 Python; tracked tree 3,979,169 bytes.

## Reproduction, root causes and corrections

1. Quality: source selected the first non-Valid row, so an old stale row masked a newer valid/partial row. A controlled old-stale/current fixture now asserts the latest quality independently of retained collection anomalies. The native minute adapter also dropped duplicate labels before capture; it now retains them for diagnosis.
2. Causality: validator checked cutoff and date but omitted receipt-before-event and transport-before-original-receipt contradictions. Explicit verified-event semantics and an appended InconsistentEvidence enum value now reject impossible sequences. Unknown interval labels are not promoted into verified event timestamps.
3. Native numerics: GetMinuteAsync called ParseDouble, which returned zero on bad input, and ToMilli also converted invalid/negative values to zero. A fixture HTTP handler exercises the real native provider: bad/NaN/Infinity/negative fields now serialize as null, while real zero remains zero. Checked minute-only price scaling preserves milli-CNY and rejects overflow. Unrelated endpoint helpers remain unchanged.
4. Lifecycle: successful strategy publication awaited network capture with IsScanning=true. It now publishes once, sets IsScanning=false, exposes IsCapturing=true, retains Completed/CompletedEmpty strategy outcome and uses a separate capture CTS with an injected-clock cancellation budget. Controlled pending providers demonstrate the corrected state, timeout/cancel, window close and next automatic run.
5. Artifact UI: LastSnapshotPath previously remained ambiguous on disabled later runs and was discarded when a new capture started. LastSnapshotRunId now accompanies the retained path; every new run displays its own identity and either capture state or not-captured status, plus the explicitly identified previous artifact. Cancellation/failure/timeout preserve completed strategy rows and previous-file access. Late responses cannot publish a new path/status.

## Latest-observation and anomaly policy

All retained minute records, original provenance and per-row values remain in Minutes.Rows. The adapter retains duplicate records and sorts by timestamp as before. MinuteSummary adds LatestRowIndex, LatestTime, LatestQuality, AggregateQuality, indexed anomalies and explanatory source/status reasons.
The latest usable timestamp is the maximum timestamp at/before the cutoff, on the expected exchange date and with no future or contradictory availability/receipt. Missing availability/numeric/interval/unit evidence remains Unavailable/Partial; it is not upgraded to Valid. Stale earlier rows remain indexed diagnostics but do not replace the selected newer row's freshness. Ties select the first retained row. Conflicting records sharing a timestamp are InconsistentEvidence and are excluded from current selection.
Aggregate severity is deterministic: source failure, inconsistent/conflicting evidence, future data, wrong date, missing event timestamp, then the selected latest row's own quality. Earlier malformed numeric or stale rows remain visible in the anomaly list even when a later row is valid. Empty collections are Unavailable; failed sources remain SourceFailure. Raw provider status/reason remains in the summary. Certified stays false regardless of valid rows.

## Temporal contract

Cutoff/date checks are retained. Transport receipt cannot precede original availability or original observation. Only EventTimeSemanticsVerified=true together with a nonempty verified interval description enables the completed-event ordering assertion: an event cannot be later than original availability/observation. The production adapters leave this flag false because interval closure is not verified. A possible period-start label remains an unverified label; no completed-event claim is inferred from prose, a recently received timestamp or unit values. All UTC versus China exchange-date semantics remain unchanged.

## Native numeric and provenance behavior

Only /api/minute uses the new null-preserving finite nonnegative parser and checked milli-price conversion. Actual zero and invalid/missing values are distinct. Number and optional Amount remain raw unknown-unit fields; native Amount is now retained when supplied rather than silently omitted. Native provider identity remains EastMoney/kline/klt=1; Python-through-gateway observations retain EastMoney/trends2/ndays=1, ExternalGateway transport, cache-use marker, original receipt and separate transport receipt. Existing Python source/cache fixtures still pass; no production Python code is changed.

## Scan versus capture ownership and bounded serialization

The existing AsyncRelayCommand still serializes a scan and its diagnostic acquisition; this is a deliberate small correction, not a background-service rewrite. While capture runs, IsScanning=false and IsCapturing=true. Start requests cannot queue/launch overlaps, and the independent cancel command can cancel capture idempotently. SnapshotStatus and a compact capturing indicator make the distinction visible.
CaptureTimeout defaults to 30 seconds and accepts positive values up to one minute. An overall CTS created with SnapshotClock bounds all source awaits, so capture adds at most the configured budget to the command's network wait, below the unchanged five-minute automatic interval. Controlled timer expiry proves the next 14:05 automatic scan runs after a slow 14:00 capture. A source request that ignores cancellation remains tracked; subsequent captures fail promptly instead of accumulating source requests. Its late faults are explicitly observed, and it cannot write a snapshot or update the VM. A single capture service is retained per window to enforce that bound across runs.
Closing cancels capture without disposing its CTS while active. The awaited operation handles cancellation, clears capture state, then its owning scope disposes the CTS. No detached acquisition task or process-wide service is introduced. A late provider task may complete outside the window but has no publication authority. Capture errors never change a completed strategy to failed/cancelled.

## Artifact state and compatible schema

LastSnapshotPath and LastSnapshotRunId retain the last successful artifact; disabled later runs say “本次未启用采集” and “上次文件所属运行 <id>”. Failure/cancellation/timeout identify the current run and retained previous evidence. The open button explicitly directs users to the run label. A failed/cancelled strategy says no capture for that run. Closed windows receive no late property updates.
Schema remains 1. New MinuteSummary defaults null and is omitted when null. EventTimeSemanticsVerified defaults false and is omitted when false. Prior enum numeric values are unchanged; InconsistentEvidence is appended. Thus old schema-1 objects reserialize without added default fields and retain their existing canonical SHA-256 fingerprints. A legacy-shaped persisted file is loaded and its unchanged fingerprint asserted. New records include summaries; neither schema nor summaries certify diagnostic records.
The compatibility-test investigation found JsonNode escaped '+00:00' differently from typed DateTimeOffset serialization when constructing its old-file fixture. The fixture now uses original typed canonical formatting; the production fingerprint algorithm was not changed or weakened.

## Changed files

- IntradayObservation.cs: explicit semantic flag, causality, independent latest/aggregate/anomaly summary.
- MinuteObservationService.cs: retain duplicate rows for integrity diagnostics.
- IntradaySnapshotCapture.cs: use summary and cancellation-aware bounded source awaits; retain/observe a non-cooperative pending source request.
- EastMoneyStockDataProvider.cs: scoped null-preserving minute numeric parsing, raw Amount and checked scaling.
- SparrowViewModel.cs: distinct bounded capture state/CTS, previous/current artifact identity, truthful failure/disabled status.
- SparrowWindow.xaml: capturing indicator and explicit artifact-owner wording.
- IntradaySnapshotTests.cs: existing fixtures declare verified event semantics explicitly and assert capture state separately; duplicate rows remain visible.
- IntradayEvidenceCorrectionTests.cs: deterministic correction regressions on the existing WPF dispatcher, fake timers/providers and local temp files.
- This report: root reports directory only; no docs directory or unrelated layout change.

## Tests T01–T23 and actual validation

| Tests | Behavioral evidence | Result |
|---|---|---|
| T01–T06 | LatestQualityAndEarlierIntegrityAreIndependent: old stale versus latest valid/partial/stale, future exclusion, earlier invalid numeric/date/time anomalies, conflicts, empty versus failed source | PASS |
| T07–T09 | ImpossibleCausalityAndAmbiguousLabels: event after receipt/availability, transport before original receipt/availability, unverified/period-start labels | PASS (6 cases) |
| T10–T12 | NativeMinuteParsingDoesNotZeroFill: real native HTTP fixture, invalid versus actual zero, negative/nonfinite values and correct price scaling | PASS |
| T13 | Native fixture plus GatewayMinuteRetainsPythonSourceCacheAndOriginalReceipt and existing Python minute evidence fixture | PASS |
| T14–T18 | BoundedCaptureIsSeparateAndCannotRelabelNextRun: completed strategy while pending, fake timeout, no stacked requests, late response, close cancellation, no late PropertyChanged, next automatic scan | PASS (2 cases) |
| T16 | Existing CancellingAcquisitionPreservesSuccessfulScanButWritesNoSnapshot asserts results and Completed remain | PASS |
| T19–T20 | PreviousArtifactRemainsClearlyOwnedAndLegacyFingerprintLoads: later disabled run, accessible prior path/run identity, schema-1 canonical fingerprint retained | PASS |
| T21–T22 | Full numerical strategy and historical replay suites; their production rule/replay files unchanged | PASS locally |
| T23 | Original committed-tree/blob guard, unchanged thresholds | Final committed measurement and remote receipt required |

Windows .NET 10: restore AIHelper.sln succeeded; Release build --no-restore succeeded with zero errors and six existing NU1701 dependency compatibility warnings. Full dotnet test AIHelper.sln -c Release --no-build: 821 passed, zero failed, zero skipped (21 seconds). Python 3.12 environment: compileall app succeeded; pytest -q: 12 passed (3.57 seconds). No test uses live markets, network availability, holiday APIs or personal directories.
git diff --check and scoped review pass before commit. No rule, ranking, historical dataset/fingerprint, replay engine or portfolio source is modified.

Repository tree before: 3,979,169 bytes. Final committed tree: 4016552 bytes. Budget stays 4,194,304 bytes; blob limit stays 5 MiB. No assets/tests removed. Test-LargeBlobs.ps1 and Measure-RepositorySize.ps1 are run against the final commit before pushing/delivery.

## Gates and final receipt

| Gate | Evidence/state at commit creation |
|---|---|
| A | PASS: exact requested baseline, clean start |
| B | PASS: latest row and indexed anomalies have independent semantics |
| C | PASS: verified causality enforced; unknown interval semantics remain unknown |
| D | PASS: native malformed values null, actual zero preserved |
| E | PASS locally: separate state and bounded serialization, controlled cancellation/timeout/late tasks |
| F | PASS locally: current and previous run identities are explicit |
| G | PASS locally: numerical strategies unchanged; full suite passes |
| H | PASS locally: historical behavior/source unchanged |
| I | PASS locally: 821 .NET, 12 Python |
| J | Required final committed guard/measurement; delivery response confirms exact bytes |
| K | PENDING final-SHA Windows/Python and size workflows; no acceptance claimed yet |
| L | PASS: correction-only, no signals/orders/scoring/strategy expansion |

After push the exact final SHA, workflow names/IDs/URLs/job IDs/conclusions, clean worktree and HEAD==origin/develop are independently verified and recorded in the delivery response. History is forward-only on develop, with no amend/reset/squash/force push or changes to main/xdevelop.

## Limits and STOP

Volume/amount units and source minute interval closure remain unverified; quote exchange-event timestamps remain unavailable. No diagnostic artifact is certified or advertised as an unbiased historical 14:30 dataset. Serialization is bounded for network-backed source awaits; an external provider ignoring cancellation may retain one source request until it finishes, while later scans remain usable and later captures fail promptly. This does not promise a latency bound on arbitrary operating-system disk failures or synchronous blocking third-party code. Snapshot flags and summaries do not establish trading profitability.
STOP after T1-1-C. No T1-2, T1-Rank, VWAP/RVOL, next-day return work, ranking changes, new recommendations or dashboard redesign.
