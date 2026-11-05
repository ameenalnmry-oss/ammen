# PharmaLIMS source audit remediation implementation plan

> **For agentic workers:** Use test-driven changes and one final independent review. The user explicitly authorized immediate implementation; no additional planning approval is needed.

**Goal:** Prepare replacement source for F01–F07 without modifying historical records or applying a database cutover.

**Architecture:** Add a checksum-controlled SQL hardening migration; preserve the old migration and its signature JSON format. Keep approved cutover separate. Repair Water state/evidence guards and use frozen range criteria in reporting.

**Tech Stack:** Existing WPF/.NET 8, SQL Server, portable C# behavior cases, Python source contracts.

**Spec:** Source audit delivered 2026-10-17, F01–F07; original ZIP SHA256 04e5a76dcca92829e187ca3cd7459d05c87d466eeef67170e9e3433f53c5e8f6.

## Global Constraints
- Work in the existing extracted source; do not create another project checkout.
- No historical approved record modification, signature rehash/backfill, SQL execution, or permission cutover on the user's device.
- Runtime security verification is pending Windows/SQL UAT; source checks cannot close that requirement.
- Keep Medica layouts and existing workflows except the explicitly diagnosed defects.

## Review Focus
- Direct SQL mutation with unchanged status, signed INSERT, delete, reversion, and moving links.
- Exact historical signature JSON ordering/UTF-16 and both stage literals.
- Legitimate draft creation and submitted structured evidence must remain possible.
- Pending water completion may not overwrite previously saved results/equipment.
- Dirty or concurrent Water values must reject submission without writes.

## Task 1: Security boundary and print integrity (root)
- [x] Add failing source contracts plus SQL integration rejection cases.
- [x] Add 20261017_001 hardening migration: trusted SQL origin, account readiness view, atomic signing, immutable signed activity/link data, and hash verification.
- [x] Update least-privilege provisioning and trusted-service authorization; keep final DENY cutover explicitly separate from UAT rehearsal.
- [x] Load and validate activity print evidence in one transaction.
- [x] Run focused contracts; retain unexecuted SQL/Windows acceptance tests.

## Task 2: Water and report defects (workflow agent)
- [x] Record RED contracts and portable behavior cases.
- [x] Permit pending-only completion during investigation without changing frozen completed evidence.
- [x] Reject dirty/stale Water submission against locked saved evidence.
- [x] Share range classification for chlorine/pH reports.
- [x] Run focused Python contracts; C# execution remains pending SDK.

## Task 3: Identity, verification, and delivery (root)
- [x] Advance candidate identity coherently; preserve every original historical migration hash.
- [x] Regenerate source manifest after review; run all Python and structural checks.
- [x] Obtain fresh read-only patch review and address confirmed regressions.
- [x] Package only changed/new files plus a guarded in-place installer and validation instructions; save deliverable.

## Execution decisions
- Source archive has no Git repository or SDK. No artificial Git history/worktree is created. Original ZIP is the byte-level base for diff and installer guards.
- Existing historical Git test will skip only when its required original commit is unavailable; it must still execute when Git history exists. This skip is reported, not treated as proof of historical identity.
- Security status remains verification-blocked until Windows/SQL positive and negative UAT pass, even when source contracts pass.

## Verification boundary
Source tasks and independent source review completed. SDK/Windows/SQL UAT gates are NOT executed here and remain required before runtime/security closure. No SQL changes have been applied.
