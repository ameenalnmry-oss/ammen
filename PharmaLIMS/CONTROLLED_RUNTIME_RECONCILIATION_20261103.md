# PharmaLIMS v309 — Controlled Runtime Reconciliation

Date: 2026-11-03

## R3 evidence interpreted

The R3 provenance evidence reported seven blocking differences:

1. `client/PharmaLIMS.dll` — `HASH_MISMATCH`
2. `client/PharmaLIMS.TrustedSigning.dll` — `HASH_MISMATCH`
3. `service/PharmaLIMS.dll` — `HASH_MISMATCH`
4. `service/PharmaLIMS.TrustedSigning.dll` — `HASH_MISMATCH`
5. `service/PharmaLIMS.TrustedSigning.Host.dll` — `HASH_MISMATCH`
6. `client/PharmaLIMS.TrustedSigning.deps.json` — `MISSING_RUNTIME`
7. `service/PharmaLIMS.TrustedSigning.deps.json` — `MISSING_RUNTIME`

The two `PharmaLIMS.TrustedSigning.deps.json` rows are build-output noise for a
class-library dependency. The consuming executables (`PharmaLIMS` and
`PharmaLIMS.TrustedSigning.Host`) own the runtime dependency graphs used by the
installed applications. R4 therefore excludes this class-library `.deps.json`
from byte-for-byte runtime attestation.

The five executable DLL mismatches remain real H-03 provenance blockers.

## Controlled deployment guard

`scripts/Deploy-V309ControlledReleaseArtifacts.ps1` may be used only after a
fresh R4 provenance run. It intentionally refuses to proceed unless the selected
provenance evidence contains exactly the five known `HASH_MISMATCH` rows listed
above and no other blocking difference.

Before any replacement it verifies:

- administrator execution;
- no running `PharmaLIMS.exe`;
- current source manifest matches the provenance evidence;
- controlled staging files still match their recorded build SHA-256;
- installed runtime files still match their recorded pre-deployment SHA-256.

The script backs up all five installed DLLs, stops the trusted-signing service,
copies only the five controlled DLLs, verifies post-copy hashes, restarts the
service, and emits JSON deployment evidence. Any failure after deployment begins
triggers rollback from the backup.

The script does **not** modify:

- SQL Server or database state;
- Activity 10;
- user passwords;
- `appsettings.json`;
- `hostsettings.json`;
- certificate/PFX configuration;
- ACLs.

## Required post-deployment evidence

Re-run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
".\scripts\Invoke-V309ReleaseReadinessGate.ps1" -RunProvenance
```

Executable provenance is reconciled only when the resulting evidence reports:

- `status = MATCH`
- `blockingDifferenceCount = 0`

Debug-symbol/PDB differences may remain non-blocking unless the organization
explicitly requires symbol parity.

This closes only H-03 executable provenance. Certificate secret cutover,
disposable database integration, functional UAT, and QA validation sign-off
remain separate release gates.
