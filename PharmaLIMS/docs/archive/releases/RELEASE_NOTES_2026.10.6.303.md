# PharmaLIMS 2026.10.6.303

## Release Scope

Corrects the v302 comprehensive-review cases that could turn invalid numeric input, incomplete water limits or rounded GPT recovery into a passing decision. Numeric water entry rejects negative and comma-formatted measurements; qualitative codes accept only 0/1. Review/approval and registration require complete approved structured numeric limits. Native water trends share the frozen range decision for pH and residual chlorine and compare exact decimal evidence.

External trend imports reject negative, ambiguous or unrepresentable decimal(38,10) values before preview and again before committing. The existing upper-limit-only Water import template rejects pH/chlorine range parameters; it does not invent a lower bound. PRM accepts CFU/Bottle and respects the approved Present/Absent requirement, with ambiguous qualitative specifications held for review.

Water registration captures active approved profiles, specifications and test identity under update/serializable locks in the registration transaction. Manual EM Quality Event creation reauthorizes the user, locks the parent and result snapshot, rejects stale or unsaved edits, verifies persisted calculation evidence and rechecks an existing investigation before inserting. Affected results carry EM source module and plate ID.

New PRM analysis starts require an actual accepted signed laboratory receipt. Existing analysis timestamps are preserved. GPT decisions compare original counts before rounding; final release also verifies that the stored display recovery matches those counts. Trend statistics explicitly disclose inclusion of exact NOT ASSESSED measurements without implying conformity.

Production signing fails when its certificate configuration is absent or partial. The resolved SBOM records actual Authenticode status and signer identity for each application binary. This change does not supply an institutional signing certificate or grant Production approval.

## Data Integrity and Deployment

- No SQL migration or historical SQL byte change; no historical result, receipt or timestamp backfill.
- No changes to approved site limits, compendial master values or Medica layout/branding.
- Preserves v302 Historical Water cutover `20260908_000`. The prior report's 401 warning rows and six post-control blockers on `PW-2026-0084` have not been reverified against the live site in this change.
- Existing numeric water masters without complete bounds need controlled master review. Existing PRM analyses without receipt evidence need site QA disposition; no receipt may be invented.
- Formal receipt policy for in-site EM remains a site URS/SOP decision.

## Verification

Includes 118 new behavioral regression cases (288 total), 12 new source contract checks and SQL integration cases for receipt/start gating, exact decimal storage and the water specification replacement race. PowerShell tests prove that absent/partial signing configuration blocks release and parse the signing/SBOM scripts.

The unified release gate runs source integrity, Python checks, Release build, regression execution, disposable SQL integration and WPF RuntimeSmoke. Production deployment additionally requires the genuine signed self-contained artifact, exact artifact smoke, resolved SBOM, publish/provenance evidence and site preflight/UAT approval. See `docs/REVIEW_CLOSURE_v303.md` for the observation disposition.
