# PRM Controlled Supersession Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans. Implement inline; preserve completed site installation.

**Goal:** Enable explicit signed UAT replacement without changing any historical predecessor rows.
**Architecture:** optional predecessor identity/hash on the existing fresh-proof request; append-only ledger and service SQL boundary; effective read view for UAT profile selection/preflight.
**Tech Stack:** .NET 8 WPF, SqlClient, SQL Server 2022.
**Spec:** ../specs/2026-10-26-prm-controlled-supersession-design.md

## Global Constraints
- No Production changes, PR merge, retrospective signatures, raw desktop writes or credential access.
- Preserve PP-AL-001 v1 bytes and signature/history rows.
- No repeated site Build/Staging/Refresh; new patch qualification must be distinguished.

## Review Focus
- Replacements changing item/stage/reference scope must be rejected.
- Concurrent approvals must yield one effective successor and immutable prior evidence.
- Missing or altered ledger/view/trigger must fail closed.
- Future-effective proposals cannot remove the current selection.
- Native independent review and QA actor are mandatory; a stale ticket cannot authorize supersession.

### Task 1: Request and atomic SQL boundary
- [ ] Add failing portable validation cases for optional predecessor metadata, restricted to approval with lower positive predecessor and SHA256.
- [ ] Extend existing DTO/policy and host/SQL checks; add append-only ledger, effective view, signed evidence links. Preserve old procedure staging refusal; provide separately guarded upgrade from pinned b75 definitions.
- [ ] Exercise disposable SQL RED/GREEN including immutable predecessor and rollback.

### Task 2: Actual site WPF selection and preflight
- [ ] Explicit confirmation before fresh electronic signature; bind predecessor evidence hash.
- [ ] Update only active UAT selection and site preflight to effective view, preserving original historical loads. Missing controls block the check.
- [ ] Update guarded source prerequisites and manifests with accepted predecessor revision.

### Task 3: Qualification and handoff
- [ ] Extend Windows UAT scenarios and exact expected count; focused portable and SQL tests; site-overlay/host cross-build.
- [ ] Independent review of final patch; resolve important findings with regression.
- [ ] Commit and update existing draft PR only within standing authorization; do not merge. Report exact qualification and remaining site steps.

## Execution evidence

- Portable validation RED: prior code accepted incomplete supersession metadata. GREEN: 15 request/snapshot checks.
- Disposable SQL: implicit activation, stale evidence, different scope and stored future date rejected; audit failure rolled back all effects; approval selected only successor; predecessor content/signature/history hash and original IsActive unchanged; ledger DELETE rejected. Linux behavior probe removed only the native Windows-origin predicate in its disposable in-memory writer, never in source. Native-origin enforcement requires Windows UAT.
- 634 Python tests and 296 .NET review regressions passed.
- Independent reviewer found two Important native test-fixture defects (mismatching successor item scope and wrong expected InvalidRequest code); both corrected, with a distinct wrong-scope negative case. Native job now requires 122 checks.
- Ruling: successor reference/criteria may change through independent review; only predecessor item/stage scope must match. This preserves the purpose of versioning.
- Complete AMEEN preflight overlay compilation cannot be reproduced in cloud without existing ReviewClosureSchemaContract/WaterEvidencePreflightSql helpers. No fake helper added; the changed check is also compiled in baseline service, while full PRM site overlay is built against that service. Actual original-project build remains a new-patch site acceptance step.
