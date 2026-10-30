# T1-4-B0 Repository Capacity Rebaseline Final Report

Verdict: local governance validation PASS; exact final-SHA remote verification pending at commit time.

Authorization: repository owner explicitly approved in this chat: “将 tracked-tree 上限从 4 MiB 调整为 8 MiB，同时保留 5 MiB 单 blob 检查。” This is authorization for B0 only.
Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Starting HEAD = fetched origin/develop = 5462b9a7097141dd99744c586c8538fe3519405b; initial worktree clean. No applicable AGENTS.md found.

Changes: tools/Test-LargeBlobs.ps1 default and .github/workflows/repository-size.yml explicit invocation change MaxTreeMiB 4.00 -> 8.00. README.md and REPOSITORY_SLIMMING_PLAN.md document current policy; historical reports retain their original facts. .gitignore adds only intraday-snapshots/, t1-ranking/, and root /local-research-data/. This report is the sixth changed file.

Old tree ceiling: 4,194,304 bytes (4 MiB). New: 8,388,608 bytes (8 MiB).
Independent blob ceiling: unchanged 5,242,880 bytes (5 MiB).
Baseline tree: 4194196 bytes, old headroom 108; new-budget baseline headroom 4194412.
Final tree: 4201383 bytes; delta +7187 bytes; remaining 4187225 bytes.
Final bytes are calculated from the staged Git tree including this report and rechecked against committed HEAD before delivery.

Original behavior audited: committed HEAD blob sizes (not filesystem lengths or Git history size); valid BaseRef scans introduced objects, invalid/missing BaseRef scans HEAD; zero base handling in workflow unchanged. Empty allowlist policy, strict greater-than failure, separate blob limit, 90% warning, push main/develop/xdevelop, all PRs, weekly schedule and manual dispatch preserved. Measure-RepositorySize.ps1 remains byte-identical, including tracked/worktree/history/largest-file diagnostics. No exception or allowlist expansion.

Controlled tests used fresh temporary Git repositories outside the project; oversized fixtures never entered project history. Harness: %TEMP%/t1-b0-capacity-check.py; summary: %TEMP%/t1-b0-capacity.log.

Scenario | Observed result
--- | ---
Baseline under new default | PASS, HEAD 4194196 bytes
Exactly 8 MiB, individual blobs below 5 MiB | exit 0, 8388608 bytes
8 MiB + 1, individual blobs below 5 MiB | exit 1, over by 1 byte
Introduced 5 MiB + 1 blob; total tree 5242882 | exit 1, blob violation independently of tree budget
Same oversized introduced blob then deleted; HEAD 1 byte | exit 1 with original BaseRef
Invalid BaseRef after deletion | exit 0 using HEAD fallback
LF vs CRLF checkout plus untracked 9 MiB file | both exit 0, same 24-byte Git tree; text blob 4 bytes
Ignore checks | 13 runtime/data paths ignored; 3 source/JSON/CSV fixture paths visible
Source comparison | guard and workflow differ only in their tree-limit values

Protection: existing GPSJ, build, cache, .env, temporary and CI-artifact ignores remain. Downloaded vendor/daily/minute data, databases and generated research belong outside Git or in designated ignored local storage. No global JSON/CSV/database-extension ignore added. No source/test/asset/report deleted. Ignore rules are not a security boundary against git add -f; review remains required.

Executed on Windows .NET SDK 10.0.401: dotnet --info, restore, Release build --no-restore, solution test --no-build. Build: 0 errors (8 existing compatibility/nullability warnings); tests: 923 passed, 0 failed, 0 skipped. Python 3.12.10: compileall app passed; pytest: 13 passed. No dependency changes.
Git diff --check passed. Explicit 5/8 guard and Top30 size diagnostics are run against final committed HEAD before delivery.

Gate | Status at authoring
--- | ---
A exact baseline/authorization | PASS
B enforcement locations audited | PASS
C exact 8 MiB threshold | PASS
D independent 5 MiB guard | PASS
E no bypass expansion | PASS
F Git object bytes authoritative | PASS
G oversized tree rejection | PASS
H oversized introduced blob rejection | PASS
I push/PR/schedule enforcement | PASS
J runtime data protection | PASS
K application untouched | PASS
L .NET/Python suites | PASS
M final committed tree | pending post-commit check
N exact-SHA remote workflows | pending push/CI
O no T1-4-B functionality | PASS

Delivery SHA, CI/size workflow run and job IDs, final clean worktree and HEAD == origin/develop: pending commit/push; verified results belong in the final delivery response. Planned single forward-only commit has the exact starting SHA as parent; no amend/reset/squash/force push/history rewrite.

STOP at B0. No historical rankings, outcome labels, next-day returns, backtester, weights, trading, or WPF changes. T1-4-B requires separate authorization; missing PIT evidence and source access remain unresolved.
