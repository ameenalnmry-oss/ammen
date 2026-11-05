# PharmaLIMS v309 — Release Readiness Changeset
**Date:** 2026-11-02  
**Baseline ZIP SHA-256:** `ADB673DF408F80D4B60F3398F963A21CAD44E57152E5DF1E9715CD2503F8B71B`

This changeset records source/test changes made for release-readiness remediation. It does not represent a runtime deployment.

## Modified baseline files
- `.github/workflows/ci.yml`
- `tests/test_eq001_remediation.py`
- `tests/test_eq001_signed_status_security_contract.py`
- `tests/test_release_controls.py`
- `tests/test_v262_merge_controls.py`
- `tests/test_v268_certificate_json_transport.py`
- `tests/test_v273_prm_standard_profile_readiness.py`
- `tests/test_v274_governance_failclosed.py`
- `tests/test_v277_compile_fix.py`
- `tests/test_v278_review_clean_release.py`
- `tests/test_v279_user_management.py`
- `tests/test_v279r1_user_snapshot_compile_contract.py`
- `tests/test_v280_release_readiness.py`
- `tests/test_v282_runtime_release_hardening.py`
- `tests/test_v283_runtime_schema_gate_closure.py`
- `tests/test_v285_deep_review_closure.py`
- `tests/test_v287_auth_upgrade_path.py`
- `tests/test_v288_user_admin_write_compatibility.py`
- `tests/test_v289_user_administration_hardening.py`
- `tests/test_v294_prm_pharmacopoeial_templates.py`
- `tests/test_v294_release_hardening.py`
- `tests/test_v303_review_closure.py`
- `tests/test_v307_equipment_usage_reconciliation.py`
- `tests/test_water_workflow_findings.py`
- `tests/test_windows_signing_runtime.py`
- `tools/PharmaLIMS.TrustedSigning.Host/Program.cs`
- `tools/PharmaLIMS.TrustedSigning/TrustedHostSettings.cs`
- `tools/PharmaLIMS.TrustedSigning/WindowsTrustedPipeServer.cs`

## New controlled files
- `CERTIFICATE_SECRET_CUTOVER_20261102.md`
- `RELEASE_READINESS_REMEDIATION_20261102.md`
- `scripts/Invoke-V309RuntimeProvenance.ps1`
- `tests/test_v309_release_provenance.py`
- `tests/test_v309_trusted_host_certificate_store.py`
- `tests/test_v309_trusted_service_diagnostics.py`

`SOURCE_MANIFEST_SHA256.txt` is regenerated after this record and therefore is intentionally not listed as an ordinary source edit above.

## Data preservation
No UAT database write, password reset, Activity 10 change, runtime deployment, ACL relaxation, certificate import/rotation, or service restart was performed in this source workspace.


## Additional Windows release-gate tooling
- Added `scripts/Invoke-V309ReleaseReadinessGate.ps1`.
- Added `scripts/Test-V309CertificateStoreReadiness.ps1`.
- Added `tests/test_v309_release_readiness_gate.py`.
- Added `EXECUTE_NEXT_ON_WINDOWS_20261102.md`.
- Updated `scripts/generate_source_manifest.py` to exclude `.pytest_cache`, aligning the manifest generator with the validator's controlled-source definition.
- Full Python verification after these additions: `783 passed, 1 skipped, 23 subtests passed`; unittest discovery: `761 passed, 1 skipped`.


## R5 additions — 2026-11-03
Changed/added:
- `scripts/Test-V309CertificateStoreReadiness.ps1`
- `scripts/Invoke-V309CertificateStoreCutover.ps1` (new)
- `scripts/Invoke-V309DisposableDatabaseIntegration.ps1` (new)
- `scripts/Invoke-ReleaseValidation.ps1`
- `scripts/Configure-UatActivitySigning.ps1`
- `scripts/Deploy-Eq001WaterAnalysis.ps1`
- `tests/test_v309_operational_release_gates.py` (new)
- certificate/release-readiness documentation

Intent:
- remove the plaintext PFX password dependency by a controlled, fail-closed Windows certificate-store cutover;
- prevent accidental destructive integration execution against an inherited or non-designated SQL target;
- keep existing UAT deployment helpers compatible after certificate-store cutover.
