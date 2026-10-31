# Isolated UAT controlled PRM supersession

Authorization: user requested preserving the approved PP-AL-001 profile and historical evidence, resolving its blocker, then explicitly instructed implementation. No Production activation or PR merge.

The observed v1 has both review and approval by admin. Its signatures must remain unmodified; they do not satisfy independence. v2 has independent native review by AMEEN\LimsReview and is not yet approved. QA user qa is enabled with CanApproveResults.

## Approach

Use an append-only supersession ledger, never alter old IsActive, content, or signatures. A narrow read-only effective-profile view projects a superseded row as inactive for UAT selection/preflight, while historical loads and all evidence continue reading original rows. Updating IsActive in the old version conflicts with the preservation requirement; ignoring its finding without signed replacement would bypass governance.

Explicit QA confirmation binds old version and complete evidence hash to the new reviewed snapshot. Server serializable locks verify that hash, scope, current unique active predecessor, live QA authorization, independent author/reviewer/approver, and new review evidence. Approval, supersession, immutable evidence, replay and audit commit atomically. Future-effective replacements are rejected to avoid a gap. No silent substitution for unrelated scope. Already superseded, stale, ambiguous or unknown metadata fails closed.

No new raw WPF writes/credential grants; service-only procedure execution. Production behavior and baseline business data untouched. AMEEN site overlay uses effective view only for configured EQ001_UAT_ database with trusted signing. Preflight verifies ledger/append-only controls before excluding superseded versions.

## Verification

Portable request validation; disposable SQL tests for signed atomic replacement, old rows/signature/history hashes identical, stale/forged metadata, same-actor, duplicate/replay, rollback and direct mutation denial. Windows service regression extends existing UAT. Cross-build actual site overlay and host; no repeated Windows deployment until a new patch is qualified. Site acceptance remains pending until actual new QA action and evidence readback.
