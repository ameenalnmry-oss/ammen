# EQ-001 Site Activity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement task-by-task in the current PR checkout.

**Goal:** Preserve the AMEEN equipment activity workflow while requiring independent service-authenticated review/approval.

**Architecture:** Add an activity envelope to the existing kernel-SID-bound TLS pipe. Reuse guarded service authentication; delegate atomic signature persistence to a restricted SQL adapter and existing site procedure. Produce hash-guarded complete replacement files for the original site, preserving its unrelated source.

**Tech Stack:** .NET 8, WPF, Windows named pipes/TLS, SQL Server.

**Spec:** `../specs/2026-10-24-eq001-site-activity-design.md`.

## Global Constraints

- Same PharmaLIMS project and PR #62; no new project copy.
- No Production changes, historical record edits or PR merge.
- `AMEEN\\LimsReview` is separate from `AMEEN\\PharmaLIMSSigner`.
- Runtime destination `PharmaLIMS_UAT_20261001` is identified but not accepted as the new host's disposable UAT scope.
- Preserve Water and existing activity/history/print behavior.

## Review Focus

- Forged payload names must fail before authentication; native caller mapping remains authoritative.
- Revoked permissions/password changes between authentication and commit must fail atomically.
- Repeated request IDs and stale activity versions must not append signatures or advance stages.
- Transport/certificate failure must not fall back to desktop SQL or the old HTTP service.
- Local source changes after evidence collection must stop replacement rather than be overwritten.

### Task 1: Protected activity boundary

Files: new `tools/PharmaLIMS.TrustedSigning/TrustedActivitySigning.cs`, SQL adapter, host authentication adapter; modify pipe envelope/client/server and host composition. Tests: `tools/PharmaLIMS.TrustedSigning.SecurityChecks/ActivitySigningChecks.cs`.

- [ ] Write and run failing tests for request validation, identity mismatch, fresh credential, current stage permission and cancellation.
- [ ] Implement `TrustedActivitySigningCoordinator.CommitAsync(ActivitySigningRequest, string boundUsername, string secret, CancellationToken)` and explicit authenticator/store interfaces.
- [ ] Add a distinct activity envelope routed only after kernel SID resolution; verify accepted and rejected messages.
- [ ] Add a TLS-enforced SQL store with database/origin checks, credential-version recheck, unique request ledger and transactional call to the hardened site procedure.
- [ ] Build library/host and run the focused boundary checks.

### Task 2: Compatible WPF and controlled UAT schema

- [ ] Preserve supplied activity XAML, async loading, entry, links and printing; route master and activity signatures exclusively through the protected service.
- [ ] Preserve newer local password-change audit SQL and verify-only startup in the site replacement variant.
- [ ] Supply a complete idempotent, isolated-UAT-only request-ledger script; never apply it to existing site databases from cloud.
- [ ] Build and run affected regressions; do not repeat unrelated accepted SQL tests without a code dependency.
- [ ] Generate complete changed files plus SHA-256 prerequisites and a replacement manifest; stop on local drift.

### Task 3: Windows qualification and site handoff

- [ ] Extend Windows CI for activity success/failure, caller binding, lockout, replay, stage segregation, audit persistence and historical synthetic row immutability.
- [ ] Publish source/evidence to the same PR with exact commit and CI references.
- [ ] Execute installation in the original Windows directory only after source reconciliation, certificate trust and disposable SQL UAT prerequisites pass.
- [ ] Report each pending site gate honestly; no production-ready claim until complete site evidence exists.
