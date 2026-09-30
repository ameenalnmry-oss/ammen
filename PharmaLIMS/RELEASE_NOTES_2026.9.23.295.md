# PharmaLIMS 2026.9.23.295

## Final review gap closure

- Advanced the controlled release identity from 2026.9.18.294 to 2026.9.23.295.
- Preserved all 85 controlled database migration entries without rewriting historical migration SQL.
- Hardened `Clone Active Approved`: absent historical `MinimumElapsedHours` is no longer silently replaced with 120 hours.
- Required cloned rows with missing timing remain fail-closed at `Save Draft` until a valid controlled timing is entered, reviewed and approved.
- Retained the MEDICA `MQC-G-0021` Production / In-Process profile with separate microbiology result rows.
- Retained PRM controlled reissue, immutable certificate evidence, QA separation, Quality Event gates and production release controls.
- The original v295 closure did not change Water, EM or Culture Media. Subsequent source corrections include PR #18's Culture Media workflow change and the EM prepared-media eligibility correction documented below.

## EM prepared-media eligibility correction — 2026-09-30

- Align ad-hoc planning, due-schedule generation, collection revalidation and Development direct registration with the prepared-media release workflow: require `Released` and a present, unexpired use-before date.
- Remove the historical `SterilityReview` dependency from these active EM paths and their messages. Historical Culture Media evidence remains intact.
- Use SQL Server's current date for the direct-registration expiry gate.
- Add portable regression tests and disposable SQL Server integration tests using the application's actual planning and collection SQL.
- This is a source correction on baseline commit `121284020bb5b6401d62fde747a89d63ff188e99`, retaining the existing 295 assembly identity. It is not a newly approved Production artifact. A Production release must receive a distinct controlled identity and pass the existing release gates.

## Production release gate

Source CI success does not itself approve a Production artifact. The exact Production package remains subject to the controlled main-branch workflow dispatch path: production configuration validation, self-contained win-x64 publish, Authenticode signing, exact signed-binary smoke, publish hash manifest verification, resolved CycloneDX SBOM generation, provenance attestation and controlled artifact upload.
