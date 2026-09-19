# PharmaLIMS 2026.9.18.294

## PRM pharmacopoeial microbiology templates

- Added a controlled **Load Pharmacopeial Template** action to PRM Specification Master.
- `Create Profile for This Scope` now preloads the applicable microbiology template for Raw Material, Production / In-Process, Finished Product, and Stability.
- Finished Product and Stability oral-solid templates follow harmonized non-aqueous oral acceptance criteria: TAMC criterion 10^3 CFU/g (implemented as maximum acceptable count 2000 CFU/g), TYMC criterion 10^2 CFU/g (maximum acceptable count 200 CFU/g), and *Escherichia coli* absent in 1 g.
- Raw Material template follows the harmonized pharmaceutical-substance TAMC/TYMC criteria; specified microorganisms remain material-monograph/risk-assessment dependent and are not falsely imposed as universal compendial requirements.
- In-Process uses an internal site-control template aligned to the final non-aqueous oral product criteria and is explicitly labelled as an internal control, not a standalone pharmacopoeial dosage-form category.
- Templates remain Draft master data until the normal independent Review -> Approve electronic-signature workflow is completed. No automatic approval or GMP bypass was introduced.
- Existing approved item/product specifications are not overwritten. Stricter registered/product/site specifications remain authoritative when approved.

## Scope protection

- No Water, EM, Culture Media, authentication, certificate, or historical migration logic was modified for this change.
- The existing 82 checksum-controlled historical migrations remain unchanged.
