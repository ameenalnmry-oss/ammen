# PharmaLIMS v309 — R6 Release Readiness Gate Re-run Fix
Date: 2026-11-03

## Problem observed on Windows
After a successful controlled runtime deployment, re-running `Invoke-V309ReleaseReadinessGate.ps1`
failed at delivery-package validation because the previous gate run had generated `bin/`, `obj/`,
and test-cache directories inside the extracted controlled source workspace.

This was a gate re-runnability defect. It did not indicate a runtime, SQL, Activity 10, or GMP
workflow failure.

## Fix
`Invoke-V309ReleaseReadinessGate.ps1` now runs a source-workspace cleanup gate before delivery
validation. The cleanup is restricted to generated directories under the extracted source root:

- `bin`
- `obj`
- `__pycache__`
- `.pytest_cache`

The cleanup explicitly excludes the `artifacts` evidence tree and does not touch the configured
client runtime, trusted-signing service runtime, database, configuration, or Activity state.

## Regression coverage
`tests/test_v309_release_readiness_gate.py` now asserts that:
- generated source-workspace artifacts are cleaned before delivery-package validation;
- the evidence tree is excluded from cleanup;
- client/service runtime paths are not removed.

## Source-side verification
- Targeted release-gate tests: 6 passed.
- Full pytest suite: 796 passed, 1 skipped, 23 subtests passed.
- unittest discovery: 761 passed, 1 skipped.

Additional source-side verification:
- Delivery-package validation: PASS.
- Source manifest regenerated and verified after the R6 changes.

Windows build/provenance evidence must still be produced by running the R6 gate on the controlled
Windows workstation.
