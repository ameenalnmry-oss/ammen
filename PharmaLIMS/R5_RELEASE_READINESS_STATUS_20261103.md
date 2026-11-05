# PharmaLIMS v309 R5 — Release Readiness Status
**Date:** 2026-11-03  
**Disposition:** Source/tooling remediation complete for the scope below; GMP release still requires Windows operational evidence and QA sign-off.

## Source validation
- pytest: 795 passed, 1 skipped, 23 subtests passed.
- unittest discover: 761 passed, 1 skipped.
- delivery-package validator: PASS.
- source manifest: regenerated after R5 changes.

## Closed in source/tooling
- H-01 security regression drift: current architecture is covered without weakening fail-closed assertions.
- M-02 diagnostics: protected reason categories/correlation evidence remain service-side while user responses stay generic.
- H-03 tooling: isolated provenance and controlled five-DLL reconciliation are available.
- H-02 source support: exact LocalMachine\My thumbprint loading is implemented.
- H-02 operational tooling: service-private-key ACL readiness and controlled certificate-store cutover/rollback are available.
- M-03 safety tooling: disposable DatabaseIntegration defaults to LocalDB and blocks unacknowledged external SQL.

## Still requires Windows evidence
1. Reconcile H-03 runtime hashes and re-run provenance until blocking differences are zero.
2. Run certificate-store readiness with the approved thumbprint.
3. Execute the controlled certificate-store cutover only after the approved exposure/rotation assessment authorizes it.
4. Run disposable DatabaseIntegration (and Development WPF runtime smoke if selected).
5. Complete functional UAT, rendered report/A4 acceptance, certificate/secret rotation/revocation assessment, and QA validation sign-off.

## Preservation
No UAT SQL, Activity 10, password, certificate, ACL or installed runtime was changed while producing R5.
Activity 10 remains an already-Approved historical UAT record and must not be re-reviewed or re-approved.
