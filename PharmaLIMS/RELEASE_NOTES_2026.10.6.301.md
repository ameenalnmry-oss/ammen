# PharmaLIMS 2026.10.6.301

## Release Scope

- Advances the controlled application identity from 2026.10.6.300 to 2026.10.6.301.
- Corrects the Production Runtime System Preflight SQL failure in the Historical Water evidence check.
- Aligns each Historical Water evidence GROUP BY expression with the exact severity expression selected, eliminating SQL Server error 8120.
- Adds a regression test that requires all three Historical Water evidence aggregations to group by the same BLOCKER/WARNING expression they select.

## Database and Workflow Control

- No database schema migration or data mutation is introduced by this corrective release.
- The Historical Water evidence preflight remains read-only.
- Existing Water records are not rewritten, backfilled, cancelled, or otherwise altered by this fix.
- Existing v300 Water runtime corrections, Reports preflight gating, evidence checks, and timestamp hardening are preserved.

## Verification

Release validation requires source-manifest verification, Release build, source-contract tests, review regression, SQL integration, WPF RuntimeSmoke, and live Production preflight confirmation before controlled publication.
