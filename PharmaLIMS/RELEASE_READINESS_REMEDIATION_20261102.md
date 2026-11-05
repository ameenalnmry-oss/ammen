# PharmaLIMS v309 — Release Readiness Remediation Record
**Date:** 2026-11-02  
**Baseline:** `PharmaLIMS-v309-latest-consolidated-source-20261102.zip`  
**Scope:** Source/test remediation only. No runtime deployment, SQL write, password reset, ACL relaxation, certificate rotation, or Activity 10 change was performed in this workspace.

## H-01 — Security regression verification
**Source-side status: REMEDIATED / Windows execution confirmation still required.**

The stale EQ-001 and Windows-signing tests were updated to the current trusted-signing architecture without removing the control intent. They now target:
- `WindowsTrustedPipeServer.cs`
- `TrustedCallerMap.cs`
- `TrustedActivitySigning.cs`
- `SqlTrustedActivitySignatureStore.cs`
- canonical primary-identity reauthentication

The updated tests retain checks for SID binding, unauthorized actor rejection, segregation of duties, request freshness/rowversion, wrong-database rejection, no local SQL fallback and fail-closed behavior.

Historical release tests that hard-coded `.307` were aligned to the controlled `.309` release identity. Refactored async method signatures and moved controls were updated only where the implementation retained the same control.

A real CI defect was found during this cleanup: the site-specific Production configuration could remain in the published artifact. `.github/workflows/ci.yml` now removes `artifacts/publish/appsettings.json` after the signed smoke test and before manifest generation/attestation/upload, and fails if removal does not succeed.

## H-02 — Plaintext certificate secret
**Status: PARTIALLY REMEDIATED IN SOURCE / OPERATIONAL CUTOVER OPEN.**

`TrustedHostSettings` now supports an exclusive `LocalMachine\My` certificate source by exact thumbprint and requires:
- exactly one of file or store mode;
- 40-hex thumbprint;
- `CertificateStoreName=My`;
- empty `CertificatePassword` in store mode;
- private key present;
- certificate currently valid.

Legacy PFX loading remains only for controlled transition. See `CERTIFICATE_SECRET_CUTOVER_20261102.md`.

The exposed credential still requires controlled rotation/revocation assessment and Windows cutover evidence before H-02 can close.

## H-03 — Source/build/runtime provenance
**Status: TOOLING ADDED / WINDOWS RUNTIME ATTESTATION OPEN.**

Added `scripts/Invoke-V309RuntimeProvenance.ps1`. It:
- verifies `SOURCE_MANIFEST_SHA256.txt`;
- records application version, source-manifest hash, Git commit where available, SDK version and OS;
- performs clean Release builds unless explicitly skipped;
- compares SHA-256 for client and trusted-service code artifacts against the installed runtime;
- excludes site-specific configuration and certificate/key files from code-artifact equivalence;
- fails closed on missing, extra or hash-mismatched runtime code;
- emits JSON evidence and never modifies/stops/starts the installed runtime.

H-03 remains open until this is executed on the controlled Windows UAT/release host and the mismatch is reconciled or an exact `MATCH` evidence record is produced.

## M-02 — Trusted service diagnosability
**Source-side status: REMEDIATED / Windows build and smoke confirmation required.**

`WindowsTrustedPipeServer` now emits protected internal diagnostic categories through a host-side callback, correlated by request GUID. The service log records only correlation/category data; credentials, usernames and connection strings are not included by this diagnostic sink. Client-visible error codes remain generic (`Denied`, `Conflict`, `StoreUnavailable`, etc.), preserving fail-closed information disclosure behavior.


## Verification performed in this source workspace
- `python -B scripts/validate_project.py`: **PASS**.
- `python -B -m unittest discover -s tests -p "test_*.py" -v`: **761 passed, 1 skipped**.
- `python -B -m pytest -q`: **783 passed, 1 skipped, 23 subtests passed**.
- Focused trusted-signing/security suites are included in the full green suite.
- A Windows/.NET build of these newly modified files was **not** executed in this Linux workspace because the .NET SDK/Windows runtime is unavailable here. The prior clean v309 Windows build applies to the pre-remediation baseline, not automatically to these new source edits.


## Windows execution tooling added
To reduce manual release-validation drift, the following read-only/orchestrating tools were added:

- `scripts/Invoke-V309ReleaseReadinessGate.ps1` — authoritative wrapper around the existing source/build/test gates with optional disposable DB integration, WPF runtime smoke, certificate-store preflight and installed-runtime provenance. It emits `V309_RELEASE_READINESS_SUMMARY.json` evidence and does not deploy, stop/start services or write to UAT.
- `scripts/Test-V309CertificateStoreReadiness.ps1` — read-only `LocalMachine\My` preflight that requires an exact certificate thumbprint, current validity, private key, Server Authentication EKU and exact match to the desktop client's SHA-256 TLS pin.
- `EXECUTE_NEXT_ON_WINDOWS_20261102.md` — controlled execution runbook for the remaining Windows-only gates.

These tools do not close H-02/H-03 by themselves; they make the required Windows evidence deterministic and auditable.

## Remaining release gates
- Modified source must receive a clean Windows build with warnings-as-errors and ReviewRegression execution.
- Run the provenance script on the controlled installed runtime.
- Complete the controlled certificate-store cutover/rotation assessment.
- Run Database Integration only against an explicitly disposable SQL target.
- Complete documented functional UAT (Water, EM, PRM, Culture Media, Quality Events, COA/EMRR, cancellation/reissue, concurrency, A4/print outputs).
- QA validation approval remains required.

## Activity 10 preservation
Activity 10 was already verified as `Approved`; this remediation did not re-review, re-approve, alter signatures, alter audit data, or write to either UAT database.

## Final disposition
This source remediation improves release readiness but does **not** by itself authorize GMP validation sign-off or Production release. Open operational validation gates must still be completed.


## R5 operational hardening — 2026-11-03
- Added service-private-key ACL verification to certificate-store readiness.
- Added `Invoke-V309CertificateStoreCutover.ps1` with configuration backup, service restart verification, post-cutover validation and rollback.
- Updated UAT operational scripts to understand both the legacy PFX compatibility source and the preferred `LocalMachine\My` source.
- Added `Invoke-V309DisposableDatabaseIntegration.ps1`.
- External SQL integration now requires `PHARMALIMS_TEST_SQL_DISPOSABLE=YES`, a master/blank initial catalog, no AttachDbFilename, and explicit acknowledgment in the dedicated runner.
- Release DatabaseIntegration refuses retained clean-UAT and retained Windows runtime-probe modes.
- Default disposable integration path is Windows LocalDB and does not inherit an unrelated external test connection string.

These changes do not execute SQL, alter UAT, rotate a certificate, or authorize GMP release. H-02 and M-03 remain operationally open until the Windows evidence is executed and approved.
