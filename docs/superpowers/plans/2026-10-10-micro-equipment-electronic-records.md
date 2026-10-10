# Microbiology Equipment Electronic Records Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generate controlled microbiology equipment usage, incubation, calibration, cleaning, maintenance and disinfectant-testing records directly from evidence captured inside PharmaLIMS.

**Architecture:** Extend the existing LabEquipment master with a separate multi-device activity ledger and immutable audit/e-signature evidence, while retaining existing WATER/EM/PRM result-to-equipment assignments. A single versioned document renderer creates MEDICA-branded records from structured activity snapshots; existing screens gain contextual activity capture, not duplicated equipment masters.

**Tech Stack:** C#/.NET 8 WPF, SQL Server Express, existing repository migrations, report/PDF infrastructure, C# regression and disposable SQL integration tests.

**Spec:** `docs/superpowers/specs/2026-10-10-micro-equipment-electronic-records-design.md`

## Global Constraints
- Scope restricted to registered microbiology equipment MIC-EQ-001 through MIC-EQ-034.
- Preserve existing `LabEquipment`, `LabEquipmentUsage`, associated signed history, and applied migration checksums; use additive migrations only.
- All records and samples are synthetic in Development/UAT; no production deployment, outbound telemetry, or factory-data upload.
- Never merge or modify PR #58. Branch work is separate, with fresh CI and review.
- A planned device is never reported as actually used without a technician-confirmed execution action.
- Generated reports use the approved real MEDICA logo asset, not a text substitute, and must show accurate signatures and revision history.
- Regulatory acceptability depends on validated behavior and approved SOPs, not formatting alone.

## Review Focus
1. One test uses three devices: show three executed links and no violation of the existing unique assignment constraint (Task 2).
2. Three samples share an incubation: one activity with three child links, and traceability in both directions (Task 3).
3. Repeated submit/cross-session edit: exactly one final transition, reject stale revision, preserve audit (Task 2).
4. Expired calibration discovered retroactively: flag affected activity links during a bounded interval without silently modifying approved sample results (Task 4).
5. Approved report reissue and missing official logo: do not silently overwrite an approved record or produce an unofficial-looking report (Task 6).

---

### Task 1: Add isolated activity schema and SQL contract tests

**Files:**
- Create: `PharmaLIMS/Database/Migrations/<next-controlled-id>_Micro_Equipment_Activity_Ledger.sql`
- Create: `PharmaLIMS/Services/MicroEquipmentActivityContract.cs`
- Test: `PharmaLIMS/tests/PharmaLIMS.DatabaseIntegration/MicroEquipmentActivityIntegration.cs`

**Interfaces:**
- Produces: `MicroEquipmentActivityContract.RequireScope(string equipmentCode)`, and database `dbo.MicroEquipmentActivities`, `dbo.MicroEquipmentActivityLinks`, `dbo.MicroEquipmentActivityAudit`, `dbo.MicroEquipmentActivitySignatures`.
- Activities have primary key, EquipmentID FK, type/status, performer/times, reason/method, state snapshot, rowversion; links have module + parent/test reference; audit/signatures append-only.

- [ ] Check actual migration ledger, then allocate unused ordered migration name; write failing disposable-SQL tests for schema, MIC-only lookup, FK constraints and append-only audit/signatures.
- [ ] Run integration test and confirm failure on the absent schema.
- [ ] Add non-destructive, idempotent migration and minimal contract. Do not change or drop existing usage uniqueness.
- [ ] Run SQL integration and migration checksum preflight; expect PASS with 0 blockers.
- [ ] Commit schema, contract and tests.

### Task 2: Capture confirmed activities and controlled revisions

**Files:**
- Create: `PharmaLIMS/Services/MicroEquipmentActivityService.cs`
- Create: `PharmaLIMS/Services/MicroEquipmentActivityAuthorization.cs`
- Test: `PharmaLIMS/tests/PharmaLIMS.DatabaseIntegration/MicroEquipmentActivityIntegration.cs`

**Interfaces:**
- Produces: `CreateDraft(connection, transaction, equipmentId, activityType, actor, methodReference)`; `ConfirmUse(connection,transaction,activityId,expectedRowVersion,actor,actualStart,actualEnd)`; `AddLink(connection,transaction,activityId,module,parentId,resultId,actor)`; `Transition(connection,transaction,activityId,expectedRowVersion,targetStatus,actor,reason,signature)`.
- Allowed transitions Draft → Submitted → Reviewed → Approved; Voided or corrected only through controlled revision process.

- [ ] Add failing tests for one result with three distinct activity/equipment references, duplicate idempotent submission, stale-rowversion rejection, denied role, and no phantom usage on sample creation.
- [ ] Confirm tests fail; implement atomic writes, authenticated actor from session, permissions under transaction, server timestamps, complete before/after audit and content-bound signatures.
- [ ] Run integration tests; expect PASS with prior WATER/EM/PRM assignment tests unchanged.
- [ ] Commit.

### Task 3: Incubation and usage capture within existing micro workflows

**Files:**
- Create: `PharmaLIMS/Services/MicroEquipmentIncubationService.cs`
- Create: `PharmaLIMS/MicroEquipmentActivityPanel.xaml`
- Create: `PharmaLIMS/MicroEquipmentActivityPanel.xaml.cs`
- Modify: `PharmaLIMS/ProductionRawMaterialResults.xaml.cs` and existing partials only at confirmed execution integration points
- Modify: `PharmaLIMS/EMResultsEntry.xaml.cs` and existing partials only at confirmed execution integration points
- Test: `PharmaLIMS/tests/PharmaLIMS.DatabaseIntegration/MicroEquipmentIncubationIntegration.cs`

**Interfaces:**
- Produces: `OpenActivityCapture(module,parentId,resultIds)`; `StartIncubation(equipmentId,plateRefs,actor,time,setpoint)`; `EndIncubation(activityId,actor,time,recordedTemperature)`.
- Preserve existing result-entry flow and its validation; do not auto-confirm devices when samples are registered.

- [ ] Write failing tests: multiple plates/samples share chamber, end precedes start rejected, missing technician confirmation not treated as use, relocation retains original trace, known incompatible equipment gives warning.
- [ ] Implement activity panel and incubation records with existing equipment choices and physical start/end.
- [ ] Run WPF Release build + C# and SQL regressions, check existing EM and PRM result-entry test flows.
- [ ] Commit.

### Task 4: Calibration, verification, sanitation, maintenance, and impact lookup

**Files:**
- Create: `PharmaLIMS/Services/MicroEquipmentLifecycleService.cs`
- Create: `PharmaLIMS/Services/MicroEquipmentImpactAssessment.cs`
- Modify: `PharmaLIMS/MicroEquipmentActivityPanel.xaml` and code-behind
- Test: `PharmaLIMS/tests/PharmaLIMS.DatabaseIntegration/MicroEquipmentLifecycleIntegration.cs`

**Interfaces:**
- Produces: `RecordLifecycleActivity(equipmentId,activityType,readings,materials,actor)`; `FindAffectedActivityLinks(equipmentId,affectedFrom,affectedTo)`.
- Reading metadata includes method, measurement unit, as-found/as-left, reference standard identity, acceptance criteria and any failure. Scope calibration only where applicable.

- [ ] Write failing tests for calibration pass/fail, missed due date, sanitation lot/concentration, maintenance downtime, and bounded out-of-calibration affected sample lookup without overwriting results.
- [ ] Implement validated typed readings and activity-specific panels; do not conflate calibration with periodic verification.
- [ ] Run SQL and WPF regression suites; expect PASS.
- [ ] Commit.

### Task 5: Disinfectant efficacy test evidence

**Files:**
- Create: `PharmaLIMS/Services/MicroDisinfectantEfficacyService.cs`
- Create: `PharmaLIMS/Database/Migrations/<next-controlled-id>_Micro_Disinfectant_Efficacy.sql`
- Extend: `PharmaLIMS/MicroEquipmentActivityPanel.xaml` and code-behind
- Test: `PharmaLIMS/tests/PharmaLIMS.DatabaseIntegration/MicroDisinfectantEfficacyIntegration.cs`

**Interfaces:**
- Produces: `CreateStudy(disinfectant,lot,concentration,method,actor)`; `AddChallenge(studyId,organism,isolateId,contactTime,neutralizerEvidence,replicateCounts)`; `LinkActualEquipment(studyId,activityId)`.
- A disinfectant is never added as LabEquipment; study links media, reference cultures, local isolates and actual used equipment.

- [ ] Write failing tests for missing neutralizer evidence, wrong contact time/units, control recovery, reproducible calculations, incomplete or unapproved method, multiple equipment/organism links.
- [ ] Implement controlled study entity, typed challenge evidence and links to activity ledger; preserve traceability.
- [ ] Run integration and numerical regression tests; expect PASS.
- [ ] Commit.

### Task 6: Versioned MEDICA records, instrument history and approval controls

**Files:**
- Create: `PharmaLIMS/Services/MicroEquipmentRecordRenderer.cs`
- Create: `PharmaLIMS/Services/MicroEquipmentRecordIssueService.cs`
- Create: `PharmaLIMS/MicroEquipmentManagement.xaml` and `.xaml.cs` (one consolidated entry area)
- Modify: existing application navigation at verified location
- Test: `PharmaLIMS/tests/PharmaLIMS.DatabaseIntegration/MicroEquipmentRecordIssueIntegration.cs`
- Test: add PDF/layout artifact verification to current UI smoke checks.

**Interfaces:**
- Produces: `PreviewDraft(activityId)`; `IssueApproved(activityId,expectedRevision,signer)`; `ExportEquipmentHistory(equipmentId,from,to)`.
- Versioned renderer uses approved project logo asset; output includes data/revision identifiers and performed/reviewed/approved attribution; issue keeps frozen content snapshot and digest.

- [ ] Write failing tests for absent official logo refusal, missing approval, forged/stale signature, amendment after approval, record export of only selected equipment, long table pagination, and replay of prior issue without silent changes.
- [ ] Reuse existing PDF/report infrastructure; implement deterministic issue snapshot, history query and export.
- [ ] Run WPF Release build, full regression, disposable SQL migration suite, synthetic multi-session workflow and PDF/print preview checks.
- [ ] Commit and request independent PR review. Keep PR #58 unchanged; do not merge or deploy.

## Final Acceptance Gate
- [ ] Clean Windows .NET 8 WPF Release build with all required CI checks green.
- [ ] All migrations applied on disposable synthetic SQL Server Express database, no checksum drift or destructive updates.
- [ ] Multi-session UAT for concurrency, authorization, signatures, activities and approved revisions.
- [ ] Report output checks official MEDICA logo, sample/device traceability, pagination and controlled approval.
- [ ] Produce evidence matrix and list residual issues by Critical/Major/Moderate/Minor. Development merge remains a separate explicit user decision; production adoption requires site CSV/QA approval.
