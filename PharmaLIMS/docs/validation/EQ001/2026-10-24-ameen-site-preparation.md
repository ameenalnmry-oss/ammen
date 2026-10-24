# AMEEN EQ-001 site preparation and compatible activity integration

## Evidence and scope

The original site project is `E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS`. User-executed PowerShell establishes that the directory exists, the local branch is `micro-equipment-dev` at `1fd5405` with substantial uncommitted changes, and PR #62 was fetched at `9d7c8b7`. The local source supplied for review contains activity entry, links, execution, review/approval, history and printing absent from the previously qualified PR baseline.

The site currently configures `.\SQLEXPRESS` / `PharmaLIMS_UAT_20261001` with Development settings, integrated authentication and startup updates disabled. This identifies the existing destination; it does not establish isolation or qualify the database for the new host. Do not rename it or weaken the UAT allowlist.

The independent account `AMEEN\PharmaLIMSSigner` is enabled and now has an explicit service-logon right. It remains separate from `AMEEN\LimsReview`. Direct SQL grants on the existing UAT database were inspected, not changed. Effective service-token SQL access still needs testing.

The user successfully opened an encrypted, certificate-validated System.Data.SqlClient connection to `master` at `AMEEN:1433` with `Encrypt=True;TrustServerCertificate=False`. This is interactive-account transport evidence, not a service-account test.

A separate UAT certificate `CN=PharmaLIMS.TrustedSigning` was created and exported to `C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\trusted-signing.pfx`. The user reopened the PFX successfully and matched its certificate. The protected directory/file ACLs allow only the service account RX and Administrators/SYSTEM F; their owner is Administrators. The certificate pin is `C891077332E7F06F1E2570A8A8093ADDD09D7083FC1DD36897D4E0BB887D8EE4`, with displayed validity ending 2027-01-22. No password or private key was uploaded. Reviewer trust, service loading and the TLS service handshake are pending.

## Source integration

- Preserve the supplied activity controls, async equipment loading, sample picker, history and printing. Master saves and activity review/approval use the pinned native pipe; there is no HTTP or desktop SQL signing fallback.
- Bind activity signatures to the existing kernel SID account map and exclude the service SID. Independently verify the password in the service using guarded authentication and lockout commits; do not accept WPF's password-check result as proof.
- Recheck the exact authentication rowversion, state and current stage permission inside the same SQL transaction as the request ledger and hardened activity signing procedure. Failed/stale/repeated requests must not append signature or audit evidence.
- Preserve the site's newer password-change audit binding and transactional `CanSubmitForReview` permission normalization. Do not change Water workflows.
- Activity support is deliberately UAT-only, including when the equipment-master host has another deployment mode.

`tests/eq001_activity_service_boundary_uat.sql` is an explicit disposable-UAT deployment script, not an automatic startup migration. It requires existing reviewed activity SQL and the dedicated Windows database principal. It adds the request ledger/wrapper without changing existing signatures or historical rows.

## Verification and delivery gates

The activity boundary checks were observed failing before implementation and passing afterward. Release WPF, host and Windows-UAT cross-builds succeeded with zero warnings/errors. The 634 Python regressions passed after their two inline-password-SQL location checks were updated for the preserved SQL contract.

The Windows qualification fixture adds 18 activity cases and one transactional permission compatibility case to the existing 46, requiring exactly 65 checks for this commit. Its tables/defaults are synthetic fixture setup based on supplied column metadata, with actual supplied procedure/trigger definitions. Calibration computed-column expressions and full site constraints were not exported: the new activity cases cover submitted Use activities, not calibration qualification or complete site schema equivalence. Inspect the CI run/artifact for the exact commit; these cross-build results alone are not a Windows runtime pass.

The source installer `scripts/Install-Eq001AmeenSource.ps1` targets only the original directory. It checks normalized source hashes before any writes, retains the site project version, preserves the existing checkout/branch, saves only touched source bytes for rollback and refuses source drift. Its default mode is read-only. The installer never installs a service, changes certificate trust, executes SQL or activates Production. Local verify-only startup source is left unchanged.

Remaining site gates: exact-commit Windows CI acceptance, source checks/application in the original directory, local build and relevant local regressions, qualified disposable database provisioning, independent service SQL permissions, reviewer certificate trust, protected configuration and service startup, then complete site Windows/WPF/SQL UAT. No Production changes, historical record edits, PR merge or production-readiness claim have occurred.
