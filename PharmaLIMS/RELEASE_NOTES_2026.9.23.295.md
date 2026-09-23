# PharmaLIMS 2026.9.23.295

## Final review gap closure

- Advanced the controlled release identity from 2026.9.18.294 to 2026.9.23.295.
- Preserved all 85 controlled database migration entries without rewriting historical migration SQL.
- Hardened `Clone Active Approved`: absent historical `MinimumElapsedHours` is no longer silently replaced with 120 hours.
- Required cloned rows with missing timing remain fail-closed at `Save Draft` until a valid controlled timing is entered, reviewed and approved.
- Retained the MEDICA `MQC-G-0021` Production / In-Process profile with separate microbiology result rows.
- Retained PRM controlled reissue, immutable certificate evidence, QA separation, Quality Event gates and production release controls.
- No functional change was introduced to Water, EM or Culture Media in this closure.

## Production release gate

Source CI success does not itself approve a Production artifact. The exact Production package remains subject to the controlled main-branch workflow dispatch path: production configuration validation, self-contained win-x64 publish, Authenticode signing, exact signed-binary smoke, publish hash manifest verification, resolved CycloneDX SBOM generation, provenance attestation and controlled artifact upload.
