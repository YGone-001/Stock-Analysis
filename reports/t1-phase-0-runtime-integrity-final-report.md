# Phase T1-0 runtime integrity final report

Verdict: NOT ACCEPTED — remote verification pending. This is a checkpoint, not a green baseline claim.

## Provenance

Repository: https://github.com/YGone-001/Stock-Analysis.git; branch: develop.
Starting HEAD and freshly fetched origin/develop both equalled
`04681b5761abdb60c97e6afe763d22a516794a4e`; worktree was clean before edits.
No AGENTS.md or additional development instruction file was found in the repository or its parent directories. README development commands, project files, existing dispatcher tests and workflow conventions were inspected.
Final implementation SHA: pending first commit. Final delivery SHA: resolve `git rev-parse HEAD`; the completion response records the exact immutable delivery SHA and its workflow results. A report cannot embed its own containing commit hash without changing that hash.
History is forward-only on develop; no amend, squash, reset, force push, main or xdevelop changes.

## Baseline findings A–F

- A confirmed: Start was a default AsyncRelayCommand and the sole button attempted to cancel by reentering it. `BaselineDualPurposeAsyncCommandCannotReenterToCancel` reproduces the disabled CanExecute behavior using the same toolkit command and a controlled task.
- B confirmed by source: every Loaded attached an anonymous PropertyChanged handler; anonymous timer callback had no close cleanup. Regression tests now raise repeated Loaded, inspect retained subscriptions/timer and close during controlled execution.
- C confirmed by source: CompleteAutoScanCycle ran after catch/finally, read cached prior rankings and mutable mode/TopN, returned on empty before resolving pending state, and lacked date/parameter identity. Controlled success, exception, cancellation, validation, mode/TopN mutation and pure snapshot tests cover the corrections.
- D confirmed by source and reproduced with seeded prior rows: comparison application updated groups and summary without replacing Results. Both comparison projections now replace the grid.
- E confirmed by source and reproduced with seeded generated groups: standard modes skipped SetResultGroup on empty rankings. All six standard modes now call the existing empty-aware group helper.
- F reproduced with the unchanged guard: HEAD 4,259,095 bytes, budget 4,194,304, excess 64,791. Baseline [CI 40547911400](https://github.com/YGone-001/Stock-Analysis/actions/runs/40547911400) succeeded; [size guard 40547911538](https://github.com/YGone-001/Stock-Analysis/actions/runs/40547911538), job reject-large-blobs, failed. Baseline was not fully green.

## Corrections and ownership

StartCommand retains its public name and refuses overlapping work even when ExecuteAsync is invoked directly. CancelScanCommand is independent and enabled while scanning. Cancellation is idempotent; ScanOutcome exposes Idle, Running, Cancelling, Completed, CompletedEmpty, Cancelled, Failed and Skipped. Start/cancel CanExecute changes follow IsScanning.

Each dispatched invocation owns a GUID, local CTS/token, captured trigger/start time, mode, pair and TopN. Mapper outputs and gate records are private to that invocation and resolved before its first await. The serialized compatibility identity captures these execution parameters. No scanner service or ranking algorithm was rewritten. Local candidate/ranking/comparison variables remain unpublished until scanning and CSV export succeed. Ownership/token checks gate progress and authoritative publication. An old run never disposes a new run's CTS; the executing method disposes its own CTS after unwinding.

Automatic requests are consumed before validation; only a started automatic invocation advances the attempt interval. Failed/cancelled/skipped attempts never call snapshot observation. Successful observations are scoped by captured execution identity and start date. The first observation is silent; completed-empty stores an empty set, so later real entrants are detectable. Notifications use captured mode/TopN and name at most five entrants. Manual successful scans may establish the observation baseline, preserving the prior manual-baseline behavior. Comparison scans do not claim a unified ranking snapshot. The existing weekday 14:00–14:50, five-minute policy is unchanged.

Results always represent the last completed mode, identified by ResultSummary. While running or following failure/cancellation, the summary explicitly identifies retained previous completion evidence. Standard rows retain numerical ranking/features. Compare rows retain Both/ClassicOnly/V2Only; PairCompare retains Both/LeftOnly/RightOnly and captured actual version labels. Nullable display score/rank avoid inventing comparison scores; the original ranked-row public numerical properties remain compatible. Double-click charts still use Code/Name.

Successful empty standard or comparison results remove only tracked generated groups via StockVM.RemoveGeneratedResultGroup. Cancelled/failed runs retain prior valid groups and cannot publish incomplete candidates. No manually maintained group is removed.

Loaded attaches a named handler once and starts the window-owned timer. Unloaded stops/detaches; reload can attach once again. Closed stops/detaches timer and window/VM handlers, closes the VM and requests cancellation without premature CTS disposal. Late progress/completion cannot publish or raise late property updates. No process-wide timer was added.

## Changed files

- SparrowViewModel.cs: independent cancellation, invocation ownership, local publication staging, snapshot integration, empty/group and comparison-row corrections.
- SparrowAutoScan.cs: small pure successful-observation state helper; original time rules retained.
- SparrowWindow.xaml: cancel control and category/left/right result columns; nullable score/rank display.
- SparrowWindowDC.cs: deterministic timer/event cleanup.
- AIHelper.ViewModels.csproj: friend access for offline execution and notification test seams.
- AIHelper.csproj and src/app.ico: duplicate icon consolidation preserving output/resource contracts.
- SparrowRuntimeIntegrityTests.cs: controlled offline behavior tests on the existing shared WPF dispatcher.
- SparrowWindowRealUiTests.cs: reusable dispatcher harness, portable temporary screenshot location and assertions for comparison columns.
- This report: audit, verification and stop boundary.

## Conservative size recovery

Only removed asset: `src/app.ico`, 370,070 bytes. Git tree metadata proves it was byte-identical to `src/assets/app.ico`: both blob `127ffce781354d244b8a6ebbdeb360dc040a0111`.
`rg -n 'app.ico' src tests README.md` located ApplicationIcon, the assets resource entries and AssemblyAssociatedContentFile("app.ico"). ApplicationIcon now uses assets/app.ico; the resource remains assets/app.ico and linked Content preserves output app.ico. Release output and retained source SHA-256 both equal `4EE74D066439D8AF2335A66EA54B1F16FEA6C93DB36E049B30D25C14E314FDDF`. Build and full WPF tests validate packaging/UI compatibility. Necessary JS, legacy BAML, runtime assets, source and all tests are retained.
Before HEAD bytes: 4,259,095. After HEAD bytes: pending commit measurement.
The original 5 MiB blob limit and exact 4 MiB tree limit, scripts and workflow are unchanged.

## Local validation

Windows .NET SDK 10.0.401; Python 3.12.10.
- dotnet --info: successful.
- dotnet restore AIHelper.sln: successful.
- dotnet build AIHelper.sln -c Release --no-restore: successful, zero errors; six existing NU1701 compatibility warnings.
- dotnet test AIHelper.sln -c Release --no-build: 781 passed, 0 failed, 0 skipped (20s).
- python -m compileall app: successful.
- python -m pytest -q: 11 passed in 5.61s in a temporary Python 3.12 venv installed from requirements.txt. Initial default interpreter lacked httpx/pydantic; dependency installation resolved collection errors without source changes.
- An intermediate full WPF run found the old 11-column assertion; it was updated to assert 14 columns and category/left/right headers. No test was suppressed.
- pwsh -NoProfile -File tools/Test-LargeBlobs.ps1 -MaxSizeMiB 5 -MaxTreeMiB 4.00: pending committed-tree check.
- pwsh -NoProfile -File tools/Measure-RepositorySize.ps1 -Top 20 -MaxTrackedMiB 0 -MaxGitMiB 0: pending committed-tree check.
- git diff --check: passed; final status and remote synchronization pending push.

## T01–T24 evidence map

| Scenarios | Behavioral evidence | Outcome |
|---|---|---|
| T01–T06 | CommandsCancelRestartAndRejectOldProgress: idle/start/cancel CanExecute, direct overlapping start, twice-cancel, restart, old queued progress | PASS |
| T07–T09 | WindowCleanupIsIdempotentAndSuppressesLatePublication: repeated Loaded subscription counts, stopped timer, detached target, close mid-scan and zero late PropertyChanged/notification | PASS |
| T10–T12 | AutoOutcomesAndCapturedIdentity: invalid automatic request never executes, exception and cancellation never alert | PASS |
| T13–T18 | AutoOutcomesAndCapturedIdentity; SuccessfulSnapshotsIncludeEmptyAndAreScoped: first silent, real entrant, empty resets, mode/TopN/parameter/date keys; EntrantNotificationNamesAtMostFiveAndUsesCapturedMode | PASS |
| T19–T20 | EmptyRemovesGroupsButFailureAndCancellationKeepThem parameterized across Classic/V2–V6 | PASS |
| T21–T23 | ComparisonsReplaceRowsAndPreserveCategories parameterized for both comparison modes, including empty clear and actual side identities | PASS |
| T24 | Unchanged guard on committed HEAD, exact byte measurement | PENDING |

## Remote CI and acceptance gates

Final implementation workflows pending push; no dispatch/running state is treated as success.

| Gate | State | Evidence |
|---|---|---|
| A baseline | PASS | Exact requested SHA and clean start |
| B lifecycle | PASS locally | Controlled command/window tests |
| C notifications | PASS locally | Compatible successful snapshots only |
| D results | PASS locally | Eight modes and empty/group coverage |
| E compatibility | PASS locally (781/781) | No numerical/filter/replay changes |
| F size | PENDING | Committed-tree guard required |
| G CI | PENDING | Exact final-SHA completed workflows required |
| H cleanliness | PENDING | Push and synchronization required |

## Limitations and STOP boundary

Offline fake execution exercises lifecycle without live providers; this phase does not certify live market-data quality or trading profitability. Existing NU1701 warnings remain. No holiday calendar or timezone redesign. No V7, VWAP/RVOL, T1-Rank, prediction, order API, replay/return methodology change, minute snapshot store, dashboard redesign or strategy tuning. STOP after Phase T1-0; Phase T1-1 requires separate authorization.
