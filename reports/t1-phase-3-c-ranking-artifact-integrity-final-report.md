# T1-3-C - Ranking artifact integrity corrections

Verdict: NOT ACCEPTED pending final-SHA CI/size check; local gates green; final SHA and workflow/job IDs in delivery response. Forward-only commits on develop from baseline b21e54e (its CI 40921039545 + size 40921039521 verified); worktree clean.

1. Start b21e54e.
2. Reproduced: same RunId Top5 to Top3 reused one filename; JSON silently replaced; mixed CSV/JSON pairs possible; no verifier.
3. Causes: RunId-only name; overwrite moves; no atomicity; no pairing contract.
4. Changed: T1RankingArtifact.cs, SparrowT1Ranking.cs, T1RankingTests.cs, Services.csproj.
5. Identity: dir {RunId}-priceonlyv1-{full content fingerprint}; SHA-256 of compact canonical content, round-trip byte-stable, no volatile timestamps.
6. Publish: staging dir, flushed parts, cancel checks each boundary, one atomic rename; dirs immutable; identical re-export validates and reuses; staging cleaned; fault seam.
7. Verify(): schema, recomputed fingerprint, CSV SHA-256 pairing, identity, coverage math, rank sequence, dup symbols, exclusion validity, class, dir-name identity, expected equality, size bounds.
8. Legacy flats untouched, unverifiable by design; no migration; snapshots untouched.
9. Cancel at any boundary publishes nothing; failures preserve prior packages; close/new scan block late publication.
10. WPF unchanged; status shows dir, fingerprint, reuse; failure never shows success.
11. T01/T11 bytes+reuse; T02/T03 TopN identity; T04/T05 collision+attribution; T06-T08/T29 cancels; T09/T10 failures; T12-T16 tamper; T18 structural; T19/T20 legacy; T21-T23 WPF; T24-T27 suites+guard.
12. .NET 897/0; Python 13; build 0 errors.
13. Size 4,168,152 + 24,780 code + this report; guard green on final SHA.
14. Limits: no replay/returns (T1-4 unauthorized); scoring untouched.
15. STOP: no backtesting/labels/optimization/trading/brokerage.
