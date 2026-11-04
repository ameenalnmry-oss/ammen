# PR62 review remediation plan

User request 2026-10-27 explicitly authorizes repair of the nine High findings, pipe availability, fixed Preflight date presentation, and preservation/porting of AMEEN PRM controls. Comparison baseline e4d66337e4f3a695b5028197659e77ee162fa6c1; starting implementation HEAD b4b3b0c3638628cac53d0af5a7f4455984dca724 adds only the previously tested deployment-parser correction.

## Goal and constraints

Correct confirmed runtime integrity/access defects with RED/GREEN behavioral regressions. Preserve existing Water/EM/PRM local changes, historical evidence, least privilege, Production configuration and unmerged PR62. No new SQL grants, rewriting evidence, deployment to Production or claims of actual site UAT from CI.

Water rollout status remains an independent blocker: original-source patch and184runtimefiles installed, build0warnings/errors, new SQL objects absent after Timeoutexpired. Service now stopped, desktop closed. Original rollback water-registration-rollback-142c0b87-417e-45fd-832b-081538e0841e. Only SQL completion is authorized for next local repair; no rebuild/runtime recopy. Root owns that unfinished repair and the Water certificate formatting finding.

## Work ownership

1. Investigations worker: QualityEventInvestigation.xaml.cs, PRMQualityEventInvestigation.xaml.cs and narrowly named new investigation guards/tests. Reject stale state/content/evidence within the transaction before any update and retain closure evidence. No EM helper/planning edits.
2. EM worker: DatabaseHelper.EnvironmentalMonitoring source family, EMPlanning, EM result/report paths and narrowly named EM guards/tests. Match current results to closed-investigation evidence; reject cancelled parent plan at save/review/approval/print; carry employee identity from plan to event/plate/report without mixing personnel. Coordinate any shared generic investigation interfaces with worker1.
3. PRM worker: ProductionRawMaterialResults source family, base ProductionRawMaterialSamples/Services SpecificationEvidenceService and corresponding AMEEN overlays where necessary. Cover optional recorded failed results; port existing overlay independence/content/draft-history controls to base without regressing qualified site variant.
4. Media worker: CultureMediaPreparation source family and narrowly named guards/tests. Recheck review/approval signatures and full scientific content under locks after reauth, prevent stale mutation, capture scientific old/new audit values.
5. Infrastructure worker: root and mirrored CI YAML, WindowsTrustedPipeServer/TrustedCallerMap as needed, Preflight report date output and narrowly named tests. Block untrusted dispatch before runner allocation/secrets; bounded/authorized pipe connections preserving TLS/SID; explicit invariant Gregorian display without clock alteration.
6. Root: ReportCertificate.xaml.cs/shared precision/range formatter/tests; SQL-only Water recovery and fail-closed rollout proof; shared manifest/project/CI test registration after coordinating owners; integration and independent whole-patch review.

All workers read skill instructions and actual source before changes. Shared checkout on the authorized existing non-main branch; no worker commits, pushes, regenerates source manifest or changes project-wide CI/test registration without root coordination. Unique test files and external build artifact directories under /workspace/.cloud-setup/pr62-review-fixes/<worker>. PYTHONDONTWRITEBYTECODE=1. Do not touch other owners' files; communicate dependencies before shared changes.

## Per-domain steps

- [ ] Verify finding against actual current/base/overlay source and record evidence/counterevidence.
- [ ] Write focused failing reproduction and observe RED before implementation; prefer executable state/SQL behavior over source-text mirrors.
- [ ] Implement minimal atomic control; fail closed on unknown/stale evidence; preserve old signed history.
- [ ] Observe GREEN, run relevant existing regressions and build affected code. Record exact commands/results/limitations and changed files.
- [ ] Root reviews interface consistency and integrates tests/manifest; no site rollout at this stage.

## Integration and verification

- [ ] Validate source/XAML linkage, packaging/case/path invariants and full Python suite.
- [ ] Run portable review/security/session/new behavioral suites and appropriate actual-shape SQL tests. Ensure tests model stale concurrent edits, changed result evidence, optional failures, cancellation and scientific-value precision/identity.
- [ ] Build root WPF, host and complete AMEEN site overlays without replacing private site configuration or changing dependencies unnecessarily.
- [ ] Fresh independent review of combined patch. Fix Important findings RED/GREEN; retain explicit native/site limits.
- [ ] Commit/push coherent changes to existing PR62, never merge. Run CI and report each finding with affected files/test evidence.
- [ ] Offer guarded site-specific follow-up only after evidence; actual Windows UAT remains separate. No broad grants/Production activation.

Progress ledger: /workspace/.cloud-setup/pr62-review-fixes/progress.md.
