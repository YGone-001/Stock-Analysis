# Repository Operating Guide

## Scope and safety

- Work only on the requested concern; preserve unrelated staged or unstaged changes.
- Begin substantive work by checking `git status --short`, current branch, and `HEAD`.
- Never reset, rebase, force-push, discard user work, or rewrite history unless the user explicitly requests it.
- Do not add generated binaries, publish output, credentials, tokens, local data, or machine-specific paths to Git.
- Keep Python changes out of .NET-only work unless the request specifically requires them.

## Architecture boundaries

- Keep research reporting downstream of persisted/queryable experiment facts. It may not run strategies, replay, ranking, portfolio simulation, performance analysis, benchmark calculation, market-data access, network requests, or repository mutation.
- Preserve existing semantic behavior for Classic, V2, ranking, replay, benchmarks, portfolio simulation, performance, artifacts, persistence, query semantics, and historical datasets unless the user explicitly expands scope.
- Prefer small immutable contracts, deterministic ordering, invariant formatting, and explicit unavailable states. Do not infer missing research facts.
- Keep local reporting limited to structured JSON and Markdown unless the user authorizes another output type.

## Implementation and validation

- Follow existing project organization and avoid broad refactors.
- Use `apply_patch` for source edits. Add focused tests with each behavior change.
- For .NET changes, run restore, Debug build/tests, and Release build/tests when practical. Run `git diff --check` before committing.
- For Python changes, run `python -m compileall app` and `python -m pytest -q` from `market-data-server` when the required tools are installed.
- Run the repository-size guard for material additions; do not change its threshold without explicit user authorization and evidence.

## Commit and content naming restriction

- Commit messages and all tracked project-file content must not use stage-style labels or abbreviations.
- The prohibited family includes `phasexxx`, `Phasexxx`, `Phase xxx`, `Px`, and `Px-x`, plus any case variation, whitespace variation, punctuation variation, hyphenated form, or equivalent numbered form of those labels.
- The only exception is this policy declaration's literal deny-list, which is required to state the restriction unambiguously. Do not reproduce these terms elsewhere in repository content.
- Use concise Conventional Commit messages that describe the affected capability, for example `feat(research): add deterministic experiment reporting`.

## Git workflow

- Stage only intended files, inspect the staged diff and stat, then commit atomically.
- Push only after local validation succeeds and a configured repository author identity is present.
- Report unavailable external tooling or failed environmental gates accurately; do not mask them by changing project policy or source behavior.
