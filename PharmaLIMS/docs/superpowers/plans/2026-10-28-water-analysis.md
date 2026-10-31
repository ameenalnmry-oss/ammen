# Trusted Water Analysis Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair the confirmed readonly-client Water analysis SQL229 through all six signed analysis operations without broadening client SQL access.

**Architecture:** Typed requests enter the existing TLS/SID/fresh-ticket independent service. Shared read-only snapshot and scientific validation derive a server-only mutation plan; a constrained owner procedure commits all clinical changes, evidence, signatures, audit and idempotent receipt together.

**Tech Stack:** Existing WPF/.NET8, Microsoft.Data.SqlClient6.1.6, SQL Server, PowerShell, native Windows UAT harness.

**Spec:** `docs/superpowers/specs/2026-10-28-water-analysis-design.md`

## Global Constraints

- Preserve supplied 20-file site snapshot protections, EM/PRM local changes and historical evidence.
- No broad client/service DML grants, arbitrary SQL endpoint, Production activation, branch switch or PR62 merge.
- Existing trusted registration/PRM boundaries remain installed and unchanged.
- Request canonical JSON max32768chars; protocol frame remains65536bytes; reject rather than truncate.
- Use known isolated UAT targets and native TLS/SID/authentication checks; no client/server target mismatch.

## Review Focus

- A successful first Start must not reveal the same SQL229 on Save/Submit/Review/Approve; native whole-cycle test owns this.
- Supplied site pending-only and exact saved/display evidence controls must survive adapter merge; client/scientific tests own this.
- Instrument/kit/LES evidence can change while the signing dialog is open; store snapshot+locked readiness tests own this.
- Lost acknowledgement after commit must not create duplicate signatures/results; durable recovery and receipt tests own this.
- Plan synchronization or audit failure must roll back approval, not report failure after committed state; SQL tests own this.

### Task 1: Typed contract, scientific validator and locked snapshot

Files: new trusted-library `WaterWorkflowRequest.cs`; new `Services/WaterWorkflowSnapshot.cs`, `WaterWorkflowValidator.cs` + narrow scientific/plan types; preserve installed Water helper variants in base/site overlays. Tests: new portable policy/scientific regression.

Interfaces: records named in spec. `WaterWorkflowSnapshot.Load(connection,transaction,sampleId)` returns Header/Results/Assignments/Resources/Executions, DatabaseNow and Hash; `WaterWorkflowValidator.Build(state,request,connection,transaction)` returns server-only plan. Validator performs SELECT-only SQL; no writes.

- [ ] Write/observe RED against current missing trusted contract and actual site scientific policy parity.
- [ ] Implement bounded typed shape/canonicalization, deterministic snapshot, installed-policy-compatible scientific and evidence checks.
- [ ] GREEN cases include legacy precision/notes, pending-only freeze, raw qualitative codes, ranges, visual Turbidity, LES temperature and expiry/readiness.

### Task 2: Narrow SQL mutation boundary and behavioral fixture

Files: `scripts/EQ001_UAT_Water_Analysis.sql`, new actual-shape SQL fixture/project. Consumes original typed request and server-only plan JSON. Produces procedure `dbo.EQ001_UAT_WaterWorkflow`, append-only receipts, scoped signature/audit/outcome.

- [ ] Reproduce client UPDATE Samples229 and missing constrained procedure with real separate SQL users.
- [ ] Implement six explicit actions plus resolve/cancel; origin/target/actor/version/status guards and one transaction; no arbitrary SQL or client plan endpoint.
- [ ] Cover all writes, automatic OOS QE and approval plan sync; re-read result values after triggers before commit.
- [ ] GREEN permission denial, full cycle, stale/evidence/precision/clock/role tests, duplicate/tombstone and injected failure rollback. Preserve historical hashes and client permission snapshot on stage.

### Task 3: Host integration

Files: new `TrustedWaterWorkflowStore.cs`, host endpoint/Program/settings and source client session route as coordinated. Consumes typed contract/snapshot/validator/procedure. Entry actions exactly WaterWorkflow/WaterWorkflowResolve/WaterWorkflowCancel behind independent EnableWaterAnalysisUat.

- [ ] Observe RED routing missing/disabled and no fallback under readonly actor.
- [ ] Validate target, fresh actor/version/SID, strict JSON and payload shape; retain Serializable locks, build plan from trusted state, execute owner procedure, commit only after all verification.
- [ ] Receipt recovery precedes stale-snapshot retry and revalidates current active caller without repeating clinical writes.
- [ ] GREEN endpoint role/ticket/target/replay plus actual restricted SQL tests.

### Task 4: Preserve site UI and add trusted adapter/recovery

Files: actual ResultsEntry family + new TrustedWaterWorkflow partial/recovery; supplied site Water services/Part6 preserved. Source overlays carry raw/normalized supplied hashes; shared domain files owned Task1.

- [ ] RED proves all six current handlers route to local SQL despite trusted mode.
- [ ] Add trusted dispatch before legacy DDL/DML; capture before/after load snapshot, full grid edits/evidence checks, existing fresh signing dialog and DPAPI pending request before dispatch.
- [ ] Apply/recover exact committed outcome then reload; no fallback or automatic replacement request on uncertain outcome.
- [ ] GREEN trusted mode routing, exact signed payload stability, cancellation recovery, legacy branch unchanged, site overlay full compile and existing pending-only controls retained.

### Task 5: Native Windows full-cycle and integration CI

Files: native WindowsUat tests/fixture helpers, regression runner/CI registrations. Preserve existing122 checks and all PR62 regression suites.

- [ ] Native failure reproduction uses real service/native SID/TLS plus readonly desktop SQL identity.
- [ ] Exercise WPF start/save/submit/review/approve and correct-times, three authorized app roles, wrong actor/SQL tamper/stale snapshots/idempotency/rollback.
- [ ] Run full Python/source manifests, base/host/site-overlay builds, portable/SQL/native CI and independent review. Do not claim site UAT or certificate readiness.

### Task 6: Guarded actual-site handoff

Files: source prerequisite manifest/allowlist mode, isolated Water analysis deploy script and tests/docs. Root owns shared installer/manifest integration, commits/pushes and CI.

- [ ] Verify supplied source hashes; reject unknown drift; protected source/runtime rollback before mutation.
- [ ] New analysis procedure only; preserve existing registration/PRM service/database boundaries and all config/PFX bytes. Strict committed staging proof and independent metadata before starting service.
- [ ] Actual standard-user site UAT through approval and readback of scientific/signature/audit state. Certificate/QE manual paths remain explicitly unqualified pending their own evidence.

## Frozen implementation interfaces after builder check

Contracts are in trusted namespace `PharmaLIMS.TrustedSigning`; server-only state/validator/plan in `PharmaLIMS.Services`. `WaterWorkflowRequest` fields and six Operation strings follow the spec. RPC actions exactly WaterWorkflow/WaterWorkflowResolve/WaterWorkflowCancel. Owner procedure parameters: `@Mode nvarchar(16)`, `@Operation nvarchar(30)`, `@RequestId uniqueidentifier`, `@SampleId int`, `@Payload nvarchar(max)` (canonical original request), `@Plan nvarchar(max)` (server derived or empty), `@Actor nvarchar(100)`, `@ActorVersion binary(8)`, `@CallerSid nvarchar(184)`, `@Reason nvarchar(500)`, `@Meaning nvarchar(200)`. Receipt result JSON uses WaterWorkflowResult. Read mode returns canonical13table result sets plus last clock row. Resolve returns State Pending/Committed/Cancelled; host reads existing receipt before snapshot validation; cancelled/committed recovery never re-executes changes. Commit mode applies expected state/status/root operation, all clinical writes, actor/signature/audit and receipt; host verifies written values before tx.Commit. Owner procedure enforces permissions/roles/current-version; validator receives authoritative state and typed request and performs no SQL/DML itself.

Native fixture may use the existing protected DPAPI readonly SQL client; service connection remains Windows-authenticated. Workflow target server/catalog must match the host private connection and an isolated UAT prefix; deployed SQL script itself pins exact AMEEN/site target. Native fixture adapts only SQL target guard literals as existing fixtures do, retaining Windows origin/type checks, TLS and real independent service identity. No new public test-bypass host setting is needed.
