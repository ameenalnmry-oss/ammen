# PharmaLIMS 2026.10.4.298

## Corrective UAT release

- Advances the controlled application identity from `2026.10.2.297` to `2026.10.4.298` after confirmed site UAT defects in Water reporting and Sample Details.
- Includes PR #42 from the frozen v297 baseline.
- Fixes Sample Details so test-grid query failures no longer fail silently and the displayed test rows must reconcile with the sample test count.
- Makes Sample Details safe for mixed numeric and qualitative Water result evidence.
- Keeps numeric-unit Water chemistry results numeric rather than forcing `Complies / Does Not Comply` display.
- Aligns the PW conductivity controlled limit/fallback to `1.3 µS/cm` and removes the unwanted `at 25°C` phrase from the certificate display.
- Replaces long method/procedure prose in the certificate specification column with concise approved acceptance criteria.
- Gives Non-Conform/OOS priority over Pending in the final conclusion and explicitly holds the sample under investigation pending QA disposition.
- Does not display Analysis Completed while required tests remain pending.
- Blocks controlled printing until an issued certificate/report exists.
- Allows wrapped report rows to expand instead of clipping specification text.
- No EM, PRM, historical migration SQL, or approved historical record is rewritten by this corrective release.

## Verification state before identity closure

- Corrective source branch: `fix/v298-uat-water-report`.
- PR #42 merged to `main` as `4ca5890f8e8b0005231c5e1a037a8983101b1e22`.
- PR CI run #329 completed successfully before merge.
- Historical migration SQL/checksums remain unchanged.

## Production release gate

- This identity closure does not by itself authorize Production use.
- Final Production release requires the controlled `workflow_dispatch` path from `main`: Production configuration validation, self-contained `win-x64` publish, exact-artifact smoke, manifest/SBOM/provenance verification, hosted attestation, and final package upload.
- Site UAT must then re-check the same Water/Sample Details defects in the v298 Production package before final QA acceptance.
