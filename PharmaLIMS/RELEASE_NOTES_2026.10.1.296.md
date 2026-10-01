# PharmaLIMS 2026.10.1.296

## Controlled release identity closure

- Advances the controlled application identity from `2026.9.23.295` to `2026.10.1.296` without changing historical database migration version keys or migration SQL.
- Establishes a distinct release identity for all source corrections merged after v295, including Culture Media release logic, EM prepared-media eligibility, trend/report integrity, the complete Laboratory Sample Register, Primary Packaging, Sample Details evidence fixes, PRM signature-chain hardening, and signed laboratory receipt evidence.
- No new functional feature is introduced by this release-identity change.

## Release state

- Current source baseline before this identity closure: `9183e52e071ca32104d9405d97badb410198c000` on `main`.
- Controlled migration `20260930_001` is present in the migration manifest for signed append-only laboratory receipt evidence.
- Production behavior remains fail-closed: no Development administrator bypass, no early microbiology result override, no legacy PRM specification fallback, and no automatic startup migration in Production.

## Production release gate

- Source validation and CI success do not by themselves approve this release for Production use.
- Final Production release requires the controlled `workflow_dispatch` path from `main`: materialize and validate the protected Production configuration, publish self-contained `win-x64`, Authenticode-sign first-party binaries, smoke-test the exact signed artifact against the controlled SQL Server target, generate and verify the publish hash manifest and CycloneDX SBOM, attest provenance, and upload the signed package.
- Site UAT, actual printing verification, operational load evidence, and QA approval remain release-acceptance activities.
