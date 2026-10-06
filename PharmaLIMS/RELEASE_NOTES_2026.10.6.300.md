# PharmaLIMS 2026.10.6.300

## Release Scope

- Advances the controlled application identity from 2026.10.5.299 to 2026.10.6.300.
- Corrects a confirmed Water Results Entry StackOverflow caused by recursive qualitative-result fallback when immutable specification text is absent.
- Makes Water result-entry values explicitly readable in the Results grid.
- Adds System Preflight coverage for historical/post-control Water result evidence gaps and active certificates with open Quality Events.
- Routes Reports through runtime preflight before opening regulated trend/report workflows.
- Standardizes Water workflow database timestamps on SYSDATETIME().
- Adds regression controls for the Water qualitative fallback, result visibility, Reports preflight, Water evidence preflight, and timestamp contract.

## Database and Workflow Control

- No new database migration or schema change is introduced by this corrective release.
- Existing historical Water records are not rewritten, backfilled, or fabricated. Pre-control gaps are surfaced as warnings; post-control evidence gaps are surfaced as blockers for QA-controlled disposition.
- Certificate records are not automatically cancelled when a post-issuance Quality Event is detected. Preflight surfaces the condition for QA disposition while preserving issued evidence and lifecycle traceability.

## Verification

Release validation requires source-manifest verification, Release build, source-contract tests, review regression, SQL integration, and WPF RuntimeSmoke before controlled publication.
