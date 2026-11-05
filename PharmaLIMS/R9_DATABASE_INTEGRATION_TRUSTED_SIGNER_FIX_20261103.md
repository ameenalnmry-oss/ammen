# R9 database integration trusted signer fix — 2026-11-03

## Finding

Fresh disposable DatabaseIntegration progressed through `20261030_002` batch parsing and then failed because the
environment-specific database principal `PharmaLIMS_TrustedEquipmentSigner` was not provisioned in the newly created
integration database.

The production/UAT security model intentionally provisions that principal separately from schema migrations because it
is mapped to the dedicated Windows trusted-signing service identity. A fresh disposable schema test therefore needs a
test-scoped equivalent before migrations that bind permissions to the principal.

## Remediation

`tests/PharmaLIMS.DatabaseIntegration/Program.cs` now provisions the principal only for generated
`PharmaLIMS_Integration_<12 hex>` databases used by ordinary disposable integration tests.

The runner first attempts to map the principal to the current Windows login. If SQL Server access was obtained through
a Windows group and no dedicated login exists, fallback to a no-login database user is allowed only when the current
connection is already `sysadmin`; this allows grant binding while preserving the existing trusted-origin checks.

The bootstrap is not run for retained clean-UAT mode or the Windows runtime probe mode. It creates no server login,
does not alter UAT databases, and is removed automatically when the disposable database is dropped.

## Release impact

This change is limited to the DatabaseIntegration test harness. Application/runtime code and deployed UAT state are
unchanged.
