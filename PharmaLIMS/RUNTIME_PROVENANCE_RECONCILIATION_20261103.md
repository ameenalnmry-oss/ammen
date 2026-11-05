# PharmaLIMS v309 — Runtime Provenance Reconciliation

Date: 2026-11-03

## Why R2 produced a very large mismatch list

The R2 provenance tool compared the entire default `bin\Release` trees after the release-validation workflow had also produced RID-specific `win-x64` output under those same trees. Those nested artifacts were not part of the historical client deployment layout, so they appeared as a large set of `MISSING_RUNTIME` rows even when the corresponding top-level deployed artifacts were present.

That was a tooling defect in the attestation step, not evidence that all listed migrations or satellite assemblies were absent from the controlled application source.

## R3 correction

`Invoke-V309RuntimeProvenance.ps1` now:

1. Builds the WPF client and trusted host into unique isolated staging directories under the evidence directory.
2. Compares only those isolated controlled outputs to the installed client/service runtimes.
3. Excludes site-local configuration and separately provisioned certificate/private-key material.
4. Records PDB/debug-symbol differences as non-blocking by default because they are not executable runtime identity; `-FailOnDebugSymbolDifference` can make them blocking for a stricter symbol-provenance exercise.
5. Keeps DLL/EXE/deps/runtimeconfig missing files, unexpected runtime code, and executable hash mismatches blocking.
6. Emits schemaVersion 2 evidence with separate blocking and non-blocking difference counts.

## Interpretation

If R3 still reports `HASH_MISMATCH` for `PharmaLIMS.dll`, `PharmaLIMS.TrustedSigning.dll`, or `PharmaLIMS.TrustedSigning.Host.dll`, that is a real release-provenance gap for the currently installed runtime. It must not be relabeled as tampering without further evidence; the installed UAT runtime has received controlled hotfix deployments and may legitimately predate the current remediated source package.

The release gate remains fail-closed until the installed executable code matches an exact controlled staging/package or the runtime is replaced through an approved deployment of that controlled package.

No UAT database writes, runtime changes, password operations, or Activity 10 changes are performed by this provenance tool.
