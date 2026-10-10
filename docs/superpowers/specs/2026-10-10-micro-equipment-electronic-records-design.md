# MEDICA PharmaLIMS — Microbiology Equipment Electronic Records Design
Status: PROPOSED / awaiting design approval
Date: 2026-10-10
Reference: PR #58 head afb6b6d337050a271bafc650a0c191ba4b434bac (not merged)
Scope: microbiology equipment already registered as MIC-EQ-001 through MIC-EQ-034 only.

## Purpose and boundaries
Create a low-friction inspection-ready electronic execution and logbook module inside PharmaLIMS; do not duplicate Equipment Master or create independent software. Actual activity evidence is captured once and linked to PRM, EM, and applicable microbiology water work. The system generates readable, controlled MEDICA-branded records from structured evidence. No automatic claim of GMP compliance: validation, approved SOPs, and applicable e-record/e-signature controls are prerequisites for operational adoption.

## Baseline and compatibility
Existing tables: dbo.LabEquipment (unique code, qualification/calibration state and rowversion); dbo.LabEquipmentUsage (currently unique Module+ResultRecordID, hence only one equipment per result); dbo.LabEquipmentUsageHistory and dbo.LabEquipmentSignatures (append-only); dbo.LabEquipmentOperationalUses (MIC-EQ-001..034). Existing service LabEquipmentUsageService links WATER/EM/PRM. Preserve all current constraints, data, workflow, IDs, and permissions. The new implementation must not reinterpret LabEquipmentUsage as a full proof of actual use: it presently records equipment assignment.

## Architecture
Introduce a separate multi-equipment *execution evidence* aggregate without replacing existing assignments:
- EquipmentActivity: immutable activity ID, equipment ID, activity type (Use, Incubation, Calibration, Periodic Verification, Cleaning, Sanitization, Maintenance, Breakdown, Repair), start/end and timezone, actual performer, status Draft/Submitted/Reviewed/Approved/Voided, reason, SOP/method reference, equipment state snapshot, version token.
- EquipmentActivityLinks: zero-to-many sample/test/result references (module PRM/EM/WATER-MICRO), batch/product snapshots where applicable, and source-record identity; one activity may involve multiple samples, one test may use multiple activities/equipment; no fictitious link from sample registration alone.
- EquipmentActivityReadings: typed parameter, unit, value, acceptance bound, source, timestamp and recorded-by; preserve original value and any corrections.
- EquipmentActivityEvidence: media lot, disinfectant lot/concentration/contact time, reference culture, cycle IDs and supporting attachments where appropriate.
- EquipmentActivityAudit and EquipmentActivitySignatures: append-only before/after context, authenticated actor/server timestamps and meaningful signed state. Amendments create a new revision with full trace; approved evidence never overwritten or deleted.
- EquipmentCalibration/Verification: activity details per instrument capability; metrological traceability fields, standards/reference IDs, as-found/as-left readings, uncertainty/decision-rule when relevant, due-date logic and impact assessment. Not every equipment type requires calibration.
- Disinfectant Efficacy Test is a separate controlled laboratory test object, linked to equipment activities/media/isolate/reference strain, preparation batch, neutralizer effectiveness/toxicity, inoculum, contact time, counts, calculations, acceptance criteria and approved method; never registered as equipment.
- Incubation session: chamber, set-point/range, physical start/end, samples/plates, relocation events and excursion linkage. A suggested device is not a used device until confirmed during execution.

## Workflow / integrity
Draft activities are editable with durable change records and optimistic concurrency checks. Submission validates performer, effective method, applicable readiness, sample linkage and required results. Review and approval must use authenticated roles, separation of duties where required, and content-bound electronic signatures with server timestamps. Any change after approval follows controlled amendment or cancellation/reissue; preserve previous approved revision and original evidence. Development Admin broad access remains gated to Development, not Production.

Equipment readiness shown alongside device: In Service, Out of Service, Calibration Due/Expired, Verification Due, Under Maintenance. A warning must not silently allow uncontrolled use in regulated operations; blocking/exception policies need approved SOP and role permissions. When out-of-calibration/breakdown is found, create a traceable impact-assessment query for linked sample/test activities covering a specified affected period, not automatic invalidation.

## Operator experience
One Equipment Management area embedded in current PharmaLIMS navigation with tabs/panels Equipment Master, Activities, Due Tasks, History, Reports. Reuse existing PRM/EM/WATER result-entry screens: contextual "Record Actual Equipment Use" opens an inline compact capture step, offering compatible devices by operational use and allowing all listed equipment; no forced default equipment. Reuse a single activity across samples (e.g., common incubation session); support independent cleaning, maintenance, and calibration activities. Do not create a new window per equipment or copy fixed paper forms.

## Records and reports
Dynamic rendering from approved structured evidence via one versioned report engine: equipment header, identity/code, activity, sample/product/test and batch links, instrument readiness at activity time, timestamp/UTC offset, readings/limits/units, performed/reviewed/approved signatures, exceptions, audit/revision, pagination, PDF and print. Visible MEDICA official logo must be sourced from the currently approved project asset, never recreated from text or guessed. A missing logo must fail preview/issue rather than silently ship an incorrect one. Exports for individual activity, instrument timeline, calibration, sanitation, and disinfectant efficacy. Distinguish Draft/Uncontrolled preview from Approved/Controlled issue, unique document/revision and checksum; never invent entries or signatures.

## Migration and acceptance
Use additive SQL scripts only, no destructive rewrite of existing tables and no changes to existing migrations/checksums. New migration id allocated only after verifying current migration ledger. Fail closed if key append-only, identity, permission or concurrency schema absent. Rollback tested on disposable development SQL DB. Preserve older EquipmentUsage mapping. Test with synthetic records: three products sharing one incubation; one product uses cabinet + incubator + colony counter; unsaved assignment not recorded as actual use; dual-session conflicting edits; calibration due/expired; OOC impact analysis; media/sanitizer lots; disinfectant controls; signature independence; approved-report amendment and exact replay; official logo PDF and multi-page audit report. Verify WPF build + C# regression + Python source checks + SQL integration + manual UI and real printer/PDF tests.

## Explicit non-goals
No chemical-analysis department equipment; no auto-import of production plant records; no silent cloud upload or outbound telemetry; no modification of PR #58; no merge or production deployment. Existing Medica equipment numbering must be retained.
