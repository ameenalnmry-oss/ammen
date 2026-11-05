# PharmaLIMS v309 — Release Readiness Gate Fix
Date: 2026-11-03

## Scope
This change corrects validation tooling only. It does not modify application runtime logic, UAT databases, Activity 10, Windows accounts, certificates, ACLs, or installed service/client binaries.

## Defects corrected
1. `Invoke-ReleaseValidation.ps1` attempted `dotnet run` against `PharmaLIMS.TrustedSigning.csproj`, which is a class library (`OutputType=Library`). The invalid run step was removed. The gate now builds the actual executable `PharmaLIMS.TrustedSigning.Host` after RID-specific builds are complete.
2. `Invoke-V309ReleaseReadinessGate.ps1` serialized a generic `List[object]` with `@($steps)`, which can raise `Argument types do not match` under Windows PowerShell 5.1. Summary serialization now enumerates the list explicitly.
3. `Invoke-V309RuntimeProvenance.ps1` returned a generic list via `@($rows)`. It now returns `ToArray()` for Windows PowerShell 5.1 compatibility.

## Regression coverage
`tests/test_v309_release_readiness_gate.py` now asserts:
- the trusted-signing library is never executed with `dotnet run`;
- the executable trusted-signing host is built;
- the gate/provenance scripts avoid the PowerShell 5.1 generic-list conversion patterns that caused the observed failure.

## Validation in the review workspace
- Full Python suite: 785 passed, 1 skipped, 23 subtests passed.
- Windows/.NET execution remains to be rerun on the controlled Windows workstation.
