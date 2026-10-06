# PharmaLIMS 2026.10.6.302

## Release Scope

- Advances the controlled application identity from 2026.10.6.301 to 2026.10.6.302.
- Corrects Historical Water evidence cutover classification in Runtime System Preflight.
- Uses controlled Water catalog migration `20260908_000` as the immutable Water specification evidence cutover instead of the earlier generic SampleTests schema migration `20260714_001`.
- Preserves legacy Water evidence as warnings while continuing to block true post-control Water rows that lack immutable `LimitDescription` evidence.
- Adds regression coverage requiring the Historical Water preflight to use the Water-specific cutover.

## Data Integrity

- No database schema migration or data mutation is introduced.
- Historical Water rows are not backfilled or fabricated.
- Live read-only verification shows 401 pre-control Water rows without `LimitDescription` evidence and 6 post-control rows, all on sample `PW-2026-0084`, that remain genuine blockers requiring controlled QA disposition/reconciliation.

## Verification

Release validation requires source-manifest verification, Release build, source-contract tests, review regression, SQL integration, WPF RuntimeSmoke, Production artifact smoke, and live Production preflight confirmation.
