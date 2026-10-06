# v302 Comprehensive Review — v303 Corrective Change

Baseline: `e816de8c394c207136273f87c28d9924083ccc59` / 2026.10.6.302. Corrective identity: 2026.10.6.303.

“Implemented” below means the corrective source and regression are present. Technical closure requires a successful Windows/SQL/WPF run on the submitted commit. Site validation and QA release are separate, and the two external dependencies remain explicit.

| ID | Corrective behavior | Evidence / disposition |
|---|---|---|
| C01 | Reject negative numeric measurements; only controlled 0/1 qualitative codes; reject negative external counts. | Implemented; water and external behavioral cases and commit-path guards. |
| C02 | Incomplete frozen numeric limits cannot PASS or be committed; complete limits required at master review/approval and registration. | Implemented; TOC/heavy metals/pH/chlorine missing-limit and range boundary cases. Controlled comparator endpoints remain qualitative. |
| M01 | Native chlorine/pH trend uses the same inclusive range decision as water entry and exact decimal evidence. | Implemented; lower violation, midpoint, upper violation and large-decimal boundary cases. |
| M02 | Upper-limit-only external Water template rejects pH/chlorine range parameters at preview and write. | Implemented as explicit scope restriction. Supporting range imports needs a future controlled template/schema change. |
| M03 | EM creation reads saved evidence under parent/result locks, fresh permission and snapshot checks; rechecks duplicate investigation and records source plate ID. | Implemented; unsaved count/notes, stale/removed plate regression and transaction wiring checks. |
| M04 | Numeric parser accepts CFU/Bottle without changing approved 100/10 master limits. | Implemented; case variants and below/equal/above boundaries. |
| M05 | Qualitative decision respects the approved Present/Absent criterion; unclear/contradictory criteria require review. | Implemented; Present/Absent/Not Detected/Not present and ambiguous-rule cases. |
| M06 | Water profile/specification and test identity are read and held in the registration transaction. | Implemented; SQL replacement writer must time out while capture holds its lock, then inactive replacement is not selected. |
| M07 | Accepted external values/bounds must be exactly representable in SQL decimal(38,10); no silent rounding at write. | Implemented; excess precision rejected and accepted values checked through SQL storage. |
| M08 | Unsigned Production output fails the signing gate; SBOM reports observed per-binary signature status. | Guard implemented. OPEN until genuine institutional certificate configuration, signed artifact verification and exact signed artifact smoke. |
| M09 | Invariant ungrouped numbers with decimal point; comma input rejected consistently in native/external paths and trend parsing. | Implemented; en-US/de-DE/fr-FR/ar-YE and comma/grouping cases. No historical value is rewritten. |
| O01 | New PRM analysis cannot start before accepted signed receipt; existing start evidence is preserved. | PRM correction implemented with SQL blocked/success cases. OPEN site decision: approved EM in-site receipt policy and QA disposition of legacy missing receipts. |
| O02 | Exact NOT ASSESSED values remain in descriptive statistics with explicit count and “conformity not implied” disclosure. | Implemented; retained mean 500.5 for the 1/1000 fixture with policy disclosure. QA may select a different future analysis policy. |
| O03 | GPT original counts decide against approved limits before display rounding; final release verifies count/recovery consistency. | Implemented; 49.999% and 200.001% fail, exact 50/200 boundaries pass. |

## Validation and limits

- Local headless regression: 288/288; source checks: 624/624 after controlled identity updates.
- SQL integration compilation checked locally; actual SQL execution and WPF UI/runtime checks run in Windows CI.
- PowerShell absent/partial signing cases run in the mandatory release validator.
- No new migration and no SQL/history mutation. Site-approved acceptance limits and Medica presentation remain intact.
- v302 Water evidence cutover remains `20260908_000`; prior live observations (401 legacy warnings and six post-control blockers on `PW-2026-0084`) are historical review context, not a new site verification.
- An unsigned v303 build is a validation candidate. It is not an approved Production replacement.

## Site acceptance before Production

QA should execute the corrected water negative/missing-limit cases, chlorine lower/mid/upper boundaries, PRM bottle and qualitative rules, EM save/reload/create investigation with two sessions and revoked permission, receipt-before-analysis, representative external import rejection and GPT boundary/release cases against controlled disposable or validated test data. Record the real artifact identity, signed-binary verification, site preflight output, expected/actual results and authorized QA disposition. Do not fabricate receipt dates, historical specifications or signing evidence.
