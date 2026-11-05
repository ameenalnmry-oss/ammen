# PharmaLIMS latest-review remediation — v304

Reference: `MEDICA_PharmaLIMS_Latest_Code_Review_2026-10-09.pdf` (18 Major, 3 Minor findings), baseline `c70f6107ed82bfee456a41768518991445ad6cd8`. Candidate: `2026.10.10.304`.

Implementation is complete for the findings below. Final closure requires the candidate's Windows build, disposable SQL integration run and the targeted WPF acceptance cases. No production database was modified. An older successful CI run does not verify this candidate.

| Finding | Resulting control | Verification supplied |
|---|---|---|
| F01 | GPT start INSERT binds actual `PerformedBy`, database time and approved timing snapshot. | Shared INSERT/parameter SQL fixture; source wiring check. |
| F02 | Changed water results store signer/database entry time; remarks preserve prior attribution. Preflight detects signed but unattributed records. | Water preflight SQL fixtures; source branch check. |
| F03 | Loaded/locked Water and PRM snapshots include current equipment and immutable history/evidence IDs under the same owner. | Pure snapshot conflicts; actual SQL resource-only fixture; existing PRM snapshot harness now uses production loader. |
| F04 | Every LES entry binds historical instrument-use evidence; only latest LES must match current assignment. | Historical A → B reassignment SQL fixture. |
| F05 | Water preflight applies controlled no-instrument and kit eligibility/expiry policies. | All no-instrument policy names, valid/expired kit SQL fixtures. |
| F06 | Resource evidence is in append-only/schema preflight protection. | Generic protection/tamper harness and new source/schema checks. |
| F07 | Kit expiry uses transaction database date, also stored as execution date for later preflight. | UTC date-boundary SQL fixture; authoritative clock wiring. |
| F08 | Conductivity temperature rejects grouping/exponents and values that SQL would round. | Culture-independent actual parser regressions; stored temperature SQL fixture. |
| F09 | Specification signing compares all persisted rows/fields against the loaded version; immutable complete JSON/hash binds signature. Exact numeric storage is enforced. | Full-content conflict/hash/>8KB SQL fixtures; precision regressions. |
| F10 | Original creator is retained; complete draft editor history blocks self-review. Legacy drafts must first become a new controlled draft. | Actual author/editor independence SQL fixture. |
| F11 | Save/submit/close compare raw investigation header, questions and answers. Unchanged normalized answers retain exact bytes/author/time; changed/cleared answers record actual editor/history. | Actual service raw-snapshot/N/A/clearing/>8KB SQL fixtures. |
| F12 | Reissue requires fresh issue **and** cancel permissions before certificate mutation. | Actual transaction authorization SQL fixture; UI/handler/transaction wiring. |
| F13 | EM reports prefer event area/grade snapshots; historical current-master fallback is labeled explicitly. | Source/query inspection; WPF historical/native report acceptance required. |
| F14 | Shared plan lock and fresh plan/event status guard cover result operations. Cancellation affects linked events/samples and retains signatures; approved evidence prevents cancellation. | Actual cancellation/source-plan SQL fixtures including padded historical statuses; lock-order wiring. |
| F15 | Preparation amendments require current native rowversion and unsigned visual/sterility fields under transaction locks. | Pure signed/stale/missing-baseline guards; WPF two-session stock/SOP acceptance required. |
| F16 | Culture receipt/preparation writes refresh active/locked/password state and entry permission inside the transaction. | Actual permission-revocation SQL fixtures. |
| F17 | Referenced media/master and qualified/prepared lot identity cannot be rewritten. A new receipt supplier cannot mutate a shared manufacturer. Stock-only updates return before history queries. | Actual master/lot trigger and stock-update SQL fixtures. |
| F18 | Printing compares stored plate values/status/version and event interpretation against frozen evidence. Historical workflow aliases remain explicit. | Actual production guard regressions; WPF preview/print acceptance required. |
| F19 | Grid commit errors/unsaved results/remarks/equipment block submission; captured event and snapshot are checked inside submission transaction. | Visible equipment/result guard regressions and transaction callback wiring; WPF dirty-grid acceptance required. |
| F20 | Handoff groups by area + recorded employee identity, validates sizes/consistent names and snapshots employee fields. Historical plan identities remain visible without master backfill. | Actual handoff contract regressions and INSERT wiring; WPF personnel handoff required. |
| F21 | Numeric acceptance with growth-condition context uses numeric evaluation. | Actual interpretation regressions for in-limit/excursion values. |

## Local verification

- 354 C# behavior cases passed, 0 failed; compiled with warnings treated as errors.
- 651 Python source/release contract checks passed.
- The disposable SQL integration executable, including production authorization/evidence/source-plan services, compiled with warnings treated as errors. SQL execution has not run on this host.
- Application C# type checking succeeded for 139 actual source files against exact locked dependencies and .NET 8 WPF references. Generated XAML field declarations were used only for type checking; this does not compile markup/BAML or create a runnable application. Actual source had no compiler warnings; the generated declarations produced only unassigned-field warnings.
- Syntax checks: 163 C# files and 10 new T-SQL contracts/trigger bodies, 0 syntax errors.

SQL fixtures require SQL Server/LocalDB for execution. Direct Roslyn compilation is used because this Linux host cannot run the .NET CLI/MSBuild process-metadata path. C# syntax and SQL parsing are additional checks, not a WPF build or database execution.

## Required controlled acceptance

1. Run the repository Windows CI on this exact candidate: restore/build WPF and DB maintenance, C# regressions and GUID-named disposable SQL integration. The harness refuses non-disposable database names. Production artifact signing/smoke remain the established separate release gates.
2. Two sessions: open the same Water/PRM result; change only equipment or evidence in B; stale A must roll back without result/signature/audit writes.
3. Two sessions: change criteria/unit/reference/timing or checklist/narrative in B after A loads; A review/save/submit/close must reject stale content. Every draft author/editor must be rejected as reviewer.
4. Cancel a released EM plan while another session attempts save/QE/submit/approve/reconciliation; one transaction wins, and canceled evidence cannot resume. An already approved event must prevent plan cancellation. Same-user handoff/cancel must follow user → plan lock order.
5. Sign visual/sterility preparation evidence in B after A opens amendment; A must reject before stock/SOP/signature mutation.
6. Preview approved EM records with inconsistent saved plate or event interpretations; refuse printing. Verify frozen native area/employee context after master edits, labeled historical fallback and report-print signature recording. Dirty cell/equipment edits must reject submission.

Until these runtime checks pass, describe the findings as **implemented, awaiting final runtime closure**, not production-qualified or fully closed.
