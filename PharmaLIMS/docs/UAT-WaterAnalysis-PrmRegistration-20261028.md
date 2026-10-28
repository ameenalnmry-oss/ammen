# Water analysis and PRM registration repair

The reported SQL 229 failures are confirmed integration defects: the restricted desktop attempted to update `Samples` when starting Water analysis (`9C2AA5FD`) and `PRM_NumberSequences` when registering PRM (`A6A69F1F`). An application `admin` role does not authorize those SQL writes.

The repair routes six Water analysis actions and new PRM registration through independent, default-disabled UAT boundaries in the existing signing service. The desktop retains its restricted SQL permissions. Owner procedures accept only the existing independent service identity and typed operations; they do not expose arbitrary SQL or grant service table DML. Numbering, clinical writes, test assignment/evidence, signatures where required, audit and request receipts commit atomically. Durable private DPAPI records retain the original request after an uncertain reply; retries and cancellation use that same identity.

## Preserved site behavior

The Water implementation uses the twenty supplied site files, including the complete legacy certificate-routing Part6. Frozen scientific limits, pending-only investigation completion, decimal precision, current equipment/kit/LES requirements, independent review/approval and linked-plan updates remain enforced. Independent review caught and corrected two discrepancies: the frozen sample point now takes precedence over a renamed point master, and frozen historical remarks preserve whitespace exactly. PRM keeps the complete AMEEN specification overlay and hash-pinned approved profile/supersession evidence. New registration also preserves local calendar dates without changing their wall-clock values.

Only new PRM registration is added here. Existing PRM sample editing/results, certificate issuance/printing and manual investigation writes are separate paths and are not qualified by this work. This is not a claim that the laboratory system is ready for live work.

## Verification

- Python suite: 677 tests, two platform-dependent skips.
- Portable Water domain: 28 groups; endpoint/settings: 30 checks; real-store input rejection: 14 checks.
- PRM typed policy/form mapping: 37 checks; actual restricted SQL behavior: 44 checks.
- Water SQL and combined deployment: 92 checks, including all six actions, retained locks, post-trigger precision, receipt recovery, atomic plan updates and injected audit failure.
- Actual combined deployment batches and their independent metadata queries run against disposable SQL. Failure in the second PRM stage rolls back the first Water stage too. Re-staging preserves populated historical tables and permissions; false timeout/commit evidence is rejected.
- WPF, host and native test project cross-builds pass with no warnings or errors. Linux cannot execute Windows desktop, DPAPI, SCM or native integrated-Windows SQL authentication; the dedicated Windows CI phase must execute those scenarios.

## Controlled delivery

`Install-Eq001AmeenSource.ps1 -WaterAnalysisOnly` has an exact, hash-guarded source allowlist covering these two authorized repairs. `Deploy-Eq001WaterAnalysis.ps1` builds once and can additionally stage PRM with `-IncludePrmRegistration`; analysis-only never enables PRM. It protects rollback bytes, desktop settings, private certificate, existing rows and permissions. It requires an outer-commit marker plus independent exact metadata verification before copying runtime outputs or enabling either flag. No Production activation or PR merge occurs.

The deployment build-input closure pins unchanged files to the installed b4 revision and known supplied AMEEN overlays. Unknown local sources fail closed. Two site preflight helper classes were not supplied (`ReviewClosureSchemaContract` and `WindowsSigningUatRuntime`); their contents and any additional local drift must be reviewed before the exact site build can be qualified. `Export-Eq001WorkflowBuildReview.ps1` exports only unmatched source files for that review. It does not change source, runtime, service, database, certificate or configuration.

Actual acceptance still requires the repaired runtime on `EQ001_UAT_AMEEN_20261024`, standard-user registration/analysis through the UI, distinct reviewer/QA accounts, and database evidence of atomic saves, unchanged historical evidence and retained restricted desktop permissions. CI and disposable SQL results do not replace that evidence.
