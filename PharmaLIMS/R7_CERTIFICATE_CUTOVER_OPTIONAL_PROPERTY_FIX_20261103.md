# v309 R7 — Certificate Store Cutover Optional-Property Fix

Date: 2026-11-03

## Trigger
`Invoke-V309CertificateStoreCutover.ps1` failed under `Set-StrictMode -Version Latest`
when a retained `hostsettings.json` did not contain the optional
`EnableWaterAnalysisUat` property.

The failure occurred after backup/read-only certificate readiness evidence,
but before `Stop-Service` and before any host configuration write.

## Correction
- Removed hard-coded reads of optional/non-certificate settings.
- Added `Get-ProtectedConfigSnapshot` to preserve and compare every
  non-certificate property dynamically.
- Added safe JSON property get/set helpers for certificate fields.
- Missing optional UAT flags no longer cause a StrictMode property failure.
- Scope remains limited to:
  - `CertificatePath`
  - `CertificatePassword`
  - `CertificateThumbprint`
  - `CertificateStoreName`
- Backup, fail-closed rollback, service restart verification, certificate
  readiness preflight, and client SHA-256 pin verification remain required.

## Validation
- Focused operational release-gate tests: PASS.
- Full pytest regression: 796 passed, 1 skipped, 23 subtests passed.
- unittest discover: 761 passed, 1 skipped.
