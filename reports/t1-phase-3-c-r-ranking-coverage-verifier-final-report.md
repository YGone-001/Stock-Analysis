# T1-3-C-R final report

Verdict: local PASS; remote pending at authoring.
Repository: https://github.com/YGone-001/Stock-Analysis.git; branch develop.
Start: 785e623fd7610ec34f6a5ac968df6c41e69b5549 (clean; fetched origin/develop equal).
Final SHA, clean sync, exact-SHA workflow URLs/job IDs: pending; see delivery response.

Reproduction: real Rank(): 5 captured/4 eligible/1 excluded/Top2/actual2.
Before: Write succeeded, Verify failed `shortlist/coverage inconsistent` (1 fail).
After: verification/reuse pass; same directory/hash; Top4 bytes preserved.

Coverage: captured, eligible, excluded >=0; captured=eligible+excluded (long sum);
excluded=Excluded.Length; requested in [1,20]; actual=Shortlist.Length
=min(eligible,requested); captured=Request.CapturedCandidateCount;
requested=Request.ShortlistTopN.
TopN-omitted eligible candidates are not exclusions.

Rules preserved: schema/model/version/run identity, content/CSV SHA, directory,
ranks, unique/disjoint symbols, exclusion codes/reasons, expected comparison,
legacy rejection, 2 MiB bound. Open-file length checked before allocation;
missing/locked/oversized/malformed/incomplete files fail explicitly. Same semantic
verifier checks staging before atomic rename; final name checked on reuse.
Fault/cancel seams preserved.

Tests: 923 .NET passed (897 baseline +26); 70 ranking tests. Python 3.12.10: 13 passed; compileall passed.
Restore/build passed.
Tests: truncation/reuse/coexistence, empty/underfilled, 20 rehashed mutations,
missing/locked/oversized files; existing tamper/cancel/fault/arithmetic/sort,
strategy/history, real WPF capture5/Top2 export/reuse pass.

Source: T1RankingArtifact.cs; T1RankingTests.cs; scoring unchanged.
Savings: shared WPF/scan/temp fixtures, type aliases, tab indentation.
Assertions retained; regression passed; guard unchanged.
Tracked bytes: before 4194100; after 4194196 (budget 4194304).

Gate | Result
--- | ---
A baseline | PASS
B reproduction | PASS
C equations | PASS
D truncation | PASS
E invalid coverage | PASS
F publish/reuse | PASS
G immutable | PASS
H atomic/cancel | PASS
I pairing/tamper | PASS
J bounded reads | PASS
K scoring | PASS
L history | PASS
M suites | PASS
N size | pending committed-tree check
O remote | pending exact-SHA runs
P scope | PASS
Limitations: self-hashes establish integrity, not external market authenticity.
STOP at T1-3-C-R. No T1-4/backtesting/optimization/trading or profit claims.
