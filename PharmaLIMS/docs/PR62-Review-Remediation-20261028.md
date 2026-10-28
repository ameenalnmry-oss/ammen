# PR62 source-review remediation — 2026-10-28

Comparison: `e4d66337e4f3a695b5028197659e77ee162fa6c1`. Implementation started at `b4b3b0c3638628cac53d0af5a7f4455984dca724`, whose additional change was the qualified Water deployment connection parser. This change does not merge PR62 or activate Production.

## Findings and final behavior

| Review finding | Implementation files | Reproduction and verification |
|---|---|---|
| 1. Generic stale investigation save/reopen | `QualityEventInvestigation.xaml.cs`, `DatabaseHelper.QualityEvents.cs`, `Infrastructure/QualityEventInvestigationConcurrencyGuard.cs` | Snapshot loaded persisted header and dependent evidence, compare under retained locks before saving. Submit and close also recheck the token after the signature callback. Generic closure signatures independently prevent modification. Investigation suite baseline 8/37 passes, 29 failures; fixed 40/40. |
| 2. PRM close overwrites concurrent investigation edits | `PRMQualityEventInvestigation.xaml.cs`, same concurrency guard | Save, submit and close compare header, checklist definitions/answers, affected evidence, sample results, signatures and reconciliations. Real competing SQL writers verify retained locks. Included in 40/40. |
| 3. EM approval uses old investigation decision | `DatabaseHelper.EnvironmentalMonitoring.cs`, new `.Evidence.cs`, `DatabaseHelper.QualityEvents.cs`, `EMResultsEntry.xaml.cs`, `.Part2.cs`, `.Part3.cs` | Capture result/limit/personnel/instrument evidence on investigation creation. Match current evidence at submit/close/approval/report. Edits append invalidation; reverting cannot revive an old decision. Existing closed records and actions remain unchanged. EM baseline 6/23 passes; fixed 24/24. Equipment-only gap separately reproduced 3 failures before fixing. |
| 4. Optional recorded PRM failures omitted | `ProductionRawMaterialResults.xaml.cs`, `PRMQualityEventInvestigation.xaml.cs` | Release and investigation closure cover all recorded failed/check-required results, required or optional; blank optional tests remain excluded. Real release and closure SQL regressions included in PRM 23 checks. |
| 5. Cancelled EM plan permits work | `EMPlanning.xaml.cs`, `.Part2.cs`, `Services/EmControlledLifecycleGuard.cs`, result/helper files above | Guard parent plan under transaction locks for save/submit/review/approve/report, recheck after the physical printer dialog (stale-preview RED reproduced); cancellation locks dependent events and rejects conflicting terminal work. Included in EM 24/24. |
| 6. Water certificate loses precision and lower bound | `ReportCertificate.xaml.cs` | Preserve decimal precision, use frozen pH/chlorine ranges, show both inclusive endpoints and reject incomplete/reversed ranges. Remove invented chlorine limits. Baseline 309 failures; fixed 416/416 actual extracted report-method checks across cultures. |
| 7. Media amendment races signed review | `CultureMediaPreparation.xaml.Part2.cs`, `.Part3.cs`, `Services/MediaPreparationAmendmentSnapshot.cs` | Recheck locked full preparation, SOP and signature snapshots after reauthentication; reject signed/concurrent mutations. Audit full scientific before/after JSON. Existing stock delta/source-lot paths retained. Baseline admits concurrent signature (RED); fixed 16 SQL test groups. |
| 8. Personnel identity lost/mixed | EM planning, evidence and lifecycle files above | Group release by area plus employee identity; copy locked saved identity into event used by report; compare event identity with plan samples at controlled boundaries. Included in EM 24/24. |
| 9. Production branch guard runs too late | Root and mirrored `.github/workflows/ci.yml` | Hosted PR validation is separated from a main-only dispatch job using the Production environment and self-hosted runner. Other dispatches fail on hosted runner without checkout. No Production secrets in hosted PR steps. Executable policy matrix tests included. |
| Pipe availability and caller admission | `tools/PharmaLIMS.TrustedSigning/{WindowsTrustedPipeServer,TrustedCallerMap,TrustedPipeConnectionPool}.cs` | ACL admits configured native SIDs plus service only; application tickets remain independent of native account mapping. Four retained listeners with 15-second connection deadlines. TLS and kernel SID checks preserved; first-instance ownership retained. Three portable checks pass; native ownership assertion runs in Windows CI. |
| Preflight date | `Infrastructure/{PreflightTimestamp,SystemPreflightService}.cs`, `SystemPreflight.xaml.cs`, AMEEN preflight overlay | One invariant Gregorian display for UI and copied report, including under Hijri workstation culture. No system clock change. |
| Preserve AMEEN PRM controls in base | `ProductionRawMaterialSamples.xaml.cs`, `Services/{SpecificationEvidenceService,SpecificationNumericStorage}.cs` | Port exact-content signing checks, all-editor review independence, draft history and numeric storage controls. Existing AMEEN PRM overlays left unchanged. Add checksum-controlled `Database/Migrations/20261028_001_PRM_Specification_Content_History.sql` and verification SQL; no fabricated history for legacy drafts. Preflight requires history table and enabled append-only trigger. |

## Additional Water rollout correction

The site's previous build/runtime copy succeeded, but SQL staging logged `Timeout expired` while the launcher printed PASS. Exit code alone is no longer accepted: `scripts/Deploy-Eq001WaterRegistration.ps1` requires the committed final marker and rejects timeout/error output. The PowerShell regression first reproduced the false success and now passes.

`scripts/Complete-Eq001WaterSql.ps1` completes **only SQL** for the already-installed immutable b4 runtime. It performs the original immutable preflight, verifies the protected rollback and 184 known runtime payload files, stages the unchanged b4 SQL inside a nine-table before/after hash transaction, then requires an independent metadata proof before starting the UAT service. It never rebuilds or recopies runtime files, changes settings/certificates, or reruns PRM staging. The actual pure completion batch builder is executed by `WaterSqlRegression`: 40 checks pass using disposable SQL. Native Windows completion and actual standard-user Water registration remain unexecuted here.

## Test entry points and limits

- `tests/PharmaLIMS.InvestigationConcurrencyRegression`: 40/40.
- `tests/PharmaLIMS.PrmReviewRegression`: 23 checks, including optional closure coverage and append-only history.
- `tests/PharmaLIMS.MediaAmendmentRegression`: 16 groups.
- `tests/PharmaLIMS.EmLifecycleRegression`: 24/24.
- `tests/PharmaLIMS.WaterCertificateRegression`: 416/416.
- `tests/PharmaLIMS.InfrastructureReviewRegression`: 3 portable groups; native first-instance check explicitly skips on Linux and runs on Windows.
- `tests/PharmaLIMS.WaterSqlRegression`: 40 actual-shape checks including production completion-batch builder.
- Python discovery: 652 tests.
- `tests/Test-Eq001WaterDeploymentSettings.ps1`: actual parser, fail-closed commit proof, pure completion builder.

Four new SQL suites run in PR CI through `scripts/Invoke-Eq001WindowsUat.ps1` → `scripts/Invoke-Pr62ReviewRegressions.ps1` using the disposable integrated-authentication/TLS fixture. Certificate tests run in `Invoke-ReleaseValidation.ps1`; infrastructure tests run on the hosted Windows job. CI results must be checked against the exact commit on PR62. The first run exposed a source-delivery contamination issue in the new infrastructure test step; its artifacts now build outside the checkout, with a RED/GREEN workflow regression.

The combined WPF application and signing host build succeeded with zero warnings/errors. App/MainWindow/PRM overlays are separately checked where their source dependencies are available. The complete old AMEEN preflight overlay also references site-only `ReviewClosureSchemaContract`, `WaterEvidencePreflightSql` and `WindowsSigningUatRuntime`, which are absent from the base delivery; its complete compilation requires those original site files. They were not replaced with stubs or removed.

These are executable SQL/helper/method tests and source wiring checks, not complete interactive WPF workflow or physical printing tests. Actual laboratory UAT, local overlay build, report layout/printing, role handoff and QA acceptance remain required. Historical signed evidence is preserved; previously closed EM decisions without a current evidence snapshot fail closed rather than receiving fabricated backdated evidence. A fresh investigation is required.

GitHub runner-group restrictions and Production environment branch rules must be enforced by repository administrators: branch-controlled YAML alone cannot defend against an attacker authorized to rewrite the workflow itself. No external runner or Production settings were changed.
