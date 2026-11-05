# PharmaLIMS v309 R5 — Windows execution order
**Status:** source/tooling preparation only. This document is not GMP release approval.

## 1. Close H-03 first
Run the release-readiness gate with runtime provenance.
If exactly the five controlled executable DLL hashes differ, use `Deploy-V309ControlledReleaseArtifacts.ps1`, then re-run provenance until blocking differences are zero.

Do not proceed if unexpected runtime differences appear.

## 2. H-02 certificate-store preflight
Identify the approved certificate thumbprint in `LocalMachine\My`.
Run:
`Test-V309CertificateStoreReadiness.ps1 -Thumbprint <thumbprint> ...`

Required evidence:
- selected certificate valid;
- Server Authentication EKU present;
- desktop SHA-256 pin matches;
- trusted-signing service SID resolved;
- service SID has read access to the machine private key.

Do not grant broad ACLs to make the check pass.

## 3. Controlled certificate-store cutover
Run `Invoke-V309CertificateStoreCutover.ps1 -Thumbprint <thumbprint>` only after the approved certificate/secret exposure plan is authorized.

The script changes only certificate-source fields in protected `hostsettings.json`, backs up and rolls back on failure, and verifies the service returns to Running.

The legacy PFX is deliberately not deleted by the script. Removal/rotation/revocation is a separate approved security action.

## 4. M-03 disposable database integration
Preferred:
`Invoke-V309DisposableDatabaseIntegration.ps1 -RunRuntimeSmoke`

This uses LocalDB by default and refuses inherited retained-UAT/runtime-probe modes.

An external SQL Server may be used only with explicit disposable acknowledgment and a master connection. Never use:
- `EQ001_UAT_AMEEN_20261024`
- `PharmaLIMS_UAT_20261001`
as destructive integration databases.

## 5. Remaining validation
Complete functional UAT, reports/A4 rendering, negative trusted-signing cases, certificate/secret rotation assessment and QA validation sign-off.

Activity 10 is already Approved and must not be re-reviewed or re-approved.
