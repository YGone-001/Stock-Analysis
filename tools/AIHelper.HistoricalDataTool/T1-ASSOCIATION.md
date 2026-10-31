# Offline T1 outcome association

This explicitly invoked operation reuses T1-4-B validation. It has no network,
credential lookup, directory discovery, recapture, strategy evaluation, return
calculation or trading path. A valid local declaration is not authenticated PIT.

Build with `dotnet build AIHelper.sln -c Release`. Run from the repository root:

```powershell
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --associate-t1 --help
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --associate-t1
```

The second command is the no-real-data example: exit 2, NO_AUTHORITATIVE_SAMPLE,
CALENDAR_NOT_VERIFIED and OUTCOME_SOURCE_NOT_VERIFIED. It fetches and writes nothing.
Exit 0 means help, 2 means expected research blockers, and 1 means invalid evidence,
operation conflict or unsafe publication. A declared available bar does not make
the research result PASS; production authentication and execution remain unverified.

For explicitly authorized retained files:

```powershell
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --associate-t1 --snapshot C:\authorized\snapshot.json --ranking-package C:\authorized\immutable-ranking-package --calendar C:\authorized\calendar.json --outcomes C:\authorized\outcomes.json --output .\local-research-data\associations
```

Paths above are placeholders, not discovered production samples. Omit `--output`
for read-only diagnostics. Inputs are read-only, bounded to 2 MiB per file, and
must not be placed in tracked source paths. Output must be inside an actual
Git-ignored directory; the tool checks the existing ignore policy using Git.
Output overlapping an input is rejected; linked output ancestors are unsupported.

Supported evidence formats (case-sensitive property names, string enum names):

- `--snapshot`: unchanged schema-1 SnapshotEnvelope from IntradaySnapshotStore.
- `--ranking-package`: unchanged immutable directory containing provenance.json
  and ranking.csv. Legacy flat files cannot certify the source pool.
- `--calendar`: serialized T1CalendarEvidence: Exchange plus Acquisition containing
  HistoricalCalendarDay Data and HistoricalCoverageEvidence CoverageEvidence.
  Full matching trade_cal scope, counts, source, intervening dates and open-day
  chain are required. A source name or IsOpen flag alone is insufficient.
- `--outcomes`: serialized T1AssociationOutcomeInput with Outcomes (existing
  T1OutcomeEnvelope records) and optional DecisionBases dictionary keyed by source
  symbol. Each outcome includes exact decision/target dates, instrument/exchange,
  source/retrieval/reference, state, session and optional HistoricalDailyPrice,
  status, basis and corporate-action declarations. Source authentication limitations
  remain in force. Per-symbol bases prevent one instrument's metadata being reused
  for the entire pool. Unsupported/malformed formats fail closed.
- Independent metadata belongs in these existing typed basis/action/status fields;
  no caller-provided authentication override is supported. Historical daily dataset
  JSON is not an intraday snapshot or an outcome envelope and is not silently converted.
- `--test-fixture` marks fixture accounting only; it cannot grant source trust.

The exact constructor/property contracts live in
`src/AIHelper.Core/StockData/T1EvidenceContracts.cs` and
`T1AssociationContracts.cs`. No new market provider or daily dataset engine exists.

TEST_FIXTURE operational example, separate from production:

```powershell
dotnet test tests/AIHelper.Services.Tests/AIHelper.Services.Tests.csproj -c Release --filter FullyQualifiedName~OperationalFixtureFilesUseTheDocumentedOfflineCommandAndIgnoredOutput
```

That deterministic test creates schema-1 snapshot, real pure-function ranking,
typed calendar/outcome JSON files and a separate ignored temporary output repo;
invokes this same command API, verifies immutable publication/reuse, then removes
its fixtures. It does not supply real market-source readiness.

Output is `t1-association-{contentSHA256}/association.json`, atomically renamed
from staging. Repeating identical content reuses the same verified bytes;
divergent/tampered content is never overwritten. The envelope hashes deterministic
typed content without a generation timestamp. Explanation is console text, outside
the canonical content. Verify with T1EvidenceReadiness.VerifyAssociationPackage.
Self-hashes prove content integrity, not external authenticity.

Every retained source candidate appears, including exclusions and unshortlisted
members. Eligible-but-truncated is declared by the complete source/package
partition, with IndependentEligibility=NOT_VERIFIED and FULL_ORDERING_NOT_VERIFIED;
the service does not reconstruct missing ordering or regenerate rankings.

Coverage separates total observed captured candidates, linked-pair counts, declared
bars, validated fields, basis alignment, PIT class/authentication and execution.
AssociationCoverage is an exact usable-declared/observed-captured fraction, never
an investment return or a production success rate; no denominator gives
NOT_APPLICABLE. Existing B source coverage counters retain their linked-pair scope.
Duplicate pairs/envelopes do not inflate counts. Invalid sources can prevent pool
enumeration entirely; zero observed records never proves an empty market universe.

No real input paths were supplied for this delivery. The production pilot is
NO_AUTHORITATIVE_SAMPLE/BLOCKED; fixture success does not remove that blocker.
STOP at T1-4-C-R: no profitability claims, live collection, optimization or orders.

## Exchange calendars (T1-4-C-R)

The single-exchange `--calendar` command above remains supported. It applies only
to candidates whose actual exchange matches that declaration. For a mixed SSE/SZSE
pool, replace it with the explicit collection option:

```powershell
dotnet run --project tools/AIHelper.HistoricalDataTool -c Release --no-build -- --associate-t1 --snapshot C:\authorized\snapshot.json --ranking-package C:\authorized\immutable-ranking-package --calendars C:\authorized\calendars.json --outcomes C:\authorized\outcomes.json --output .\local-research-data\associations
```

`--calendars` is a serialized `T1ExchangeCalendarInput` with this structure:

```json
{
  "SchemaVersion": 1,
  "Calendars": [
    { "Exchange": "SSE", "Acquisition": { "Data": [], "CoverageEvidence": [] } },
    { "Exchange": "SZSE", "Acquisition": { "Data": [], "CoverageEvidence": [] } }
  ]
}
```

The empty arrays illustrate the shape only and cannot prove a next trading date.
Supply each exchange's retained `HistoricalCalendarDay` records and full matching
`HistoricalCoverageEvidence`, as required by the existing `MapNextTradingDay` verifier.
The collection has at most three entries, exactly one per declared exchange.
BSE is optional and supports existing 4/8/9-prefix identities such as 430001.BJ,
with its own `BSE` calendar and independent outcome envelope. Duplicate exchanges,
unsupported identifiers, unknown collection versions, or supplying both calendar
options are rejected. The API accepts the equivalent bounded exchange-keyed mapping;
each key must match its evidence's Exchange.

Each candidate records its actual Exchange and Calendar (Status, NextTradingDate,
EvidenceFingerprint, Reasons). Missing or insufficient matching evidence retains
the candidate with CALENDAR_NOT_VERIFIED and blocks its usable-declared flag.
There is no cross-exchange fallback. A missing daily bar never moves the calendar
target. Mixed records expose a MULTIPLE summary with no universal next trading
date; candidate-specific dates are authoritative even when markets differ.

## Association schema 2 and semantic verification

New association envelopes and content explicitly use schema 2. Calendar input
collection schema 1 is a separate contract; snapshot schema 1 and ranking package
formats remain unchanged. No existing association.json is modified or migrated.
The content-addressed immutable directory format and atomic publication remain.

Schema 2 stores a Derivation containing stable source-verifier facts, captured
identities, validated ranking/exclusion projections, calendar declarations, outcome
envelopes, and decision-basis declarations. Its SourceFileKey is an opaque hash of
the explicit absolute input path, used only to recompute distinct-file accounting.
Identical inputs at the same paths are deterministic; relocating an input changes
this local identity. This field does not authenticate a file or market source.

`VerifyAssociation` accepts schema 2 only. It replays accepted calendar, outcome
and price-basis checks, derives candidate categories/rank attribution/readiness/
reasons, and compares the entire regenerated records and coverage. Every source
counter, candidate counter, date/strategy breakdown and ratio is recomputed.
The schema-2 nested SourceCoverage calendar counters count candidate mappings
(plus one missing-calendar blocker for an unenumerable record); source pool totals
retain their linked-pair scope. Its outcome-reason map counts candidate check
reasons. This does not change the separate T1-4-B readiness API's counter semantics.

The explicitly named `VerifyLegacyAssociationIntegrity` retains schema-1 structural
diagnostics only, with its original limitations. It does not certify new semantic
readiness, grant publication, or migrate old content. The default package verifier
and publisher reject schema 1. Old bytes can still be inspected separately and
remain untouched when a new schema-2 package is published.

Self-rehashed contradictions in derived fields fail verification. Consistently
replacing all source declarations is outside an unsigned artifact's authenticity
boundary: this is internal semantic verification against stored local facts, not
re-authentication of original source files or independent market observations.
Production PIT, execution and market authentication remain NOT_VERIFIED/BLOCKED;
all correction fixtures are synthetic and authenticated production samples stay zero.

Run the mixed CLI fixture and correction matrix offline with:

```powershell
dotnet test tests/AIHelper.Services.Tests/AIHelper.Services.Tests.csproj -c Release --filter FullyQualifiedName~T1AssociationCorrectionTests
```
