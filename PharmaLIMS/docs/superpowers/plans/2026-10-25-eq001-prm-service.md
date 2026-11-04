# EQ001 PRM Service Boundary Implementation Plan

> Execution: native, under the user's standing instruction to continue safe work without repeated approval requests.

**Goal:** remove direct WPF PRM writes in trusted UAT mode while preserving the existing Windows and historical-record boundaries.

**Architecture:** fresh SID-bound single-use tickets, fixed SQL entry points, server-side permission/state checks and one transaction for data/history/signature/audit. Site-only WPF overlay preserves the original project.

**Tech Stack:** .NET 8 WPF, existing named-pipe TLS transport, SQL Server 2022.

**Spec:** ../specs/2026-10-25-eq001-prm-service-design.md

## Global Constraints
- No Production operations, merge, historical-content edits or desktop DML/credential grants.
- Preserve PP-AL-001 v1 and its evidence; no automatic supersession.
- Only the original project and PR62; no new project copy.

## Review Focus
- Actor spoofing/replayed or wrong-SID tickets: reject before mutation.
- Stale complete row sets, including replaced/deleted tests: reject before mutation.
- Numeric rounding, malformed JSON, excessive payloads: fail closed.
- Reviewer is an author/editor or approver is reviewer: reject transactionally.
- Existing active approved versions: reject activation without updating them.

## Tasks
- [x] Write/run failing SQL boundary tests and portable request/snapshot checks.
- [x] Add shared request validation/snapshot hashing, fixed UAT SQL procedures and host endpoint.
- [x] Route all trusted site PRM mutations through fresh authentication; preserve legacy code only when trusted mode is disabled.
- [x] Run targeted SQL/portable/build/regression checks, review changes and prepare guarded original-source installation. Keep Windows UAT and business blocker explicitly pending.

## Execution evidence and rulings

- Portable missing-boundary test observed RED, then 10 request/snapshot checks GREEN.
- 634 Python regressions and 296 .NET review regressions PASS.
- Actual uploaded-site overlay/host and baseline Windows test project cross-build separately with zero warnings/errors.
- Disposable SQL staging/default refusal/idempotence/Windows-origin rejection/desktop denial/unknown-definition preservation PASS.
- Independent review: one Important draft-scope regression; exact SQL predicate RED then GREEN, item/stage regression cases added. No unaddressed Critical/Important findings from that review.
- Ruling: freeze activation over any existing active approved version — preserves the user's historical-record requirement; cost is that the PP-AL-001 preflight blocker remains unresolved pending controlled supersession.
- Ruling: use separate cloud build-output directories for baseline and site overlays — avoids cached generated-XAML mixing; no extra project checkout.
- Ruling: legacy content history alone is not native trusted creation — requires a linked immutable service request; legacy drafts must be proposed as new controlled versions.
- Real Windows CI execution and actual AMEEN installation/acceptance are separate evidence, never inferred from these local checks.
