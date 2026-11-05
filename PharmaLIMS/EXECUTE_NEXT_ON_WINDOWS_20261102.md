# PharmaLIMS v309 — Execute Next on Controlled Windows Host
**Date:** 2026-11-02

This runbook turns the remaining release-readiness work into auditable Windows commands. It does **not** authorize Production/GMP release by itself.

## 1. Automated clean validation
Run from an elevated Windows PowerShell in the extracted controlled v309 source:

```powershell
.\scripts\Invoke-V309ReleaseReadinessGate.ps1
```

This executes source validation, the full Python/unittest source-control suite, source manifest verification, code-signing guard, clean .NET Release builds, trusted-signer library/host builds and ReviewRegression.

Expected terminal status:

```text
PASS: automated release-readiness gates completed for the selected scope.
```

The JSON summary is written under `artifacts\release-readiness\<UTC timestamp>`.

## 2. Exact runtime provenance
On the controlled UAT host, after step 1 succeeds:

```powershell
.\scripts\Invoke-V309ReleaseReadinessGate.ps1 -RunProvenance
```

This is read-only against the installed client/service. Any `HASH_MISMATCH`, missing runtime code or unexpected runtime code fails closed and produces JSON evidence. Do not interpret a mismatch as tampering until the build/publish/deployment path is reconciled.

## 3. Disposable database integration
Only on LocalDB or an explicitly disposable SQL test target:

```powershell
.\scripts\Invoke-V309ReleaseReadinessGate.ps1 -RunDatabaseIntegration -RunRuntimeSmoke
```

Never point destructive integration tests at:
- `EQ001_UAT_AMEEN_20261024`
- `PharmaLIMS_UAT_20261001`

## 4. Certificate-store preflight
After the approved service certificate has been imported into `LocalMachine\My`, but **before** editing `hostsettings.json`:

```powershell
.\scripts\Test-V309CertificateStoreReadiness.ps1 `
  -Thumbprint '<40 HEX SHA1 THUMBPRINT>' `
  -ServiceName 'PharmaLIMS.TrustedSigning' `
  -ClientConfig 'C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309\appsettings.json'
```

This is read-only. It verifies unique thumbprint lookup, validity, private-key presence, Server Authentication EKU and exact client SHA-256 pin match. It does not prove the service identity can open the private key; that is proven only by the controlled cutover/startup validation described in `CERTIFICATE_SECRET_CUTOVER_20261102.md`.

## 5. Still manual / QA-controlled
Even after every automated gate passes, the following remain separate release evidence:
- controlled certificate cutover and exposure/rotation assessment;
- documented functional UAT for Water, EM, PRM, Culture Media, Quality Events, COA/EMRR, cancellation/reissue, concurrency and A4/print outputs;
- QA validation approval.

Activity 10 is already Approved and must not be re-reviewed or re-approved.
