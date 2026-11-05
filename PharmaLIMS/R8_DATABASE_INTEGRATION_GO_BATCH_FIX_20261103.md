# R8 Database Integration GO batch fix — 2026-11-03

## Scope
This change is limited to the disposable DatabaseIntegration test runner. No PharmaLIMS runtime application code, UAT database, certificate configuration, or Activity 10 state is changed.

## Trigger
The disposable SQL integration run reached migration `20261030_002_Trusted_Activity_Snapshot_Bridge.sql` and SQL Server rejected the file because `GO` is a client-side batch separator and `CREATE OR ALTER PROCEDURE` must start its batch.

## Correction
`tests/PharmaLIMS.DatabaseIntegration/Program.cs` now:
- verifies the controlled SQL file checksum exactly as before;
- splits controlled SQL scripts only on standalone `GO` lines;
- executes all batches sequentially on the same open SQL connection;
- preserves session-scoped settings such as `SET XACT_ABORT ON`;
- leaves ordinary SQL execution helpers unchanged.

## Safety
The disposable database lifecycle remains unchanged: a random `PharmaLIMS_Integration_<12 hex>` database is created on the explicitly acknowledged disposable instance and is dropped in `finally` after success or failure.
