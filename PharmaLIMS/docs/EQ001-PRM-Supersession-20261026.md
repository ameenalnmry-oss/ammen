# EQ-001 isolated UAT controlled PRM supersession

## Confirmed site findings

Original AMEEN Release uses EQ001_UAT_AMEEN_20261024, not Production data. PP-AL-001 v1 contains Review and Approve signatures by the same account admin; missing independence causes the business blocker. Existing SQL229 helper permission was already corrected narrowly. v2 was created by admin, independently reviewed by AMEEN\LimsReview through the trusted service, and remains Reviewed. User qa is enabled, unlocked, QA and CanApproveResults=1. Attempted activation was refused deliberately by old SQL56806, not by authentication/SQL permissions.

## Behavior

The QA approval action first displays explicit predecessor/successor confirmation with No selected. Fresh electronic signature reason also becomes the immutable supersession decision reason. The request includes old version and complete old content/signature/history SHA256. The service/owner-executing SQL writer locks the reviewed snapshot and current predecessor, checks live QA permission and separation from every author/editor and reviewer, matches item/stage scope and stored effective date, and commits new approval, signature, content history, replay record, QA audit and supersession together.

Old specification rows, physical IsActive and signatures/history remain unchanged. A protected UAT-only effective-profile view projects superseded versions as inactive for new selection and Preflight. Historical loads/sample links still use original rows. The old signatures never become compliant retroactively. New record links the new QA signature, native Windows caller and request; update/delete is rejected. Missing or changed projection/trigger definitions block preflight and supersession. Ordinary approval without an explicit predecessor remains refused when an active predecessor exists.

Only EQ001_UAT_ + TrustedSigningEnabled uses this projection. Other runtimes use the prior table. No Production settings or existing SQL grants change. WPF receives no credential reads or direct signing writes.

## New staging and deployment

This is a new patch, not a rerun of completed b75 installation. Stage only scripts/EQ001_UAT_Prm_Controlled_Supersession.sql after exact-site guards and explicit UAT opt-in. It accepts only the pinned b75 writer definition or its own definition; refuses unknown definitions, validates metadata and is idempotent. Do NOT rerun the earlier boundary staging script after upgrading, because it correctly refuses this new writer.

Use guarded original-source installer at the new immutable revision. Preserve desktop/private settings and PFX bytes. Compile the new original project, stage the new SQL with before/after hashes of existing PRM tables, refresh only changed private binaries with rollback, verify service account/pipe. Never reset or clone the original worktree. No Windows operation has been performed by the cloud agent.

## Qualification and limits

- Portable request/snapshot suite: 15 checks, including invalid supersession metadata RED-to-GREEN.
- Disposable SQL business-rule checks: implicit/stale/scope/future refusals; atomic audit-failure rollback; old complete evidence hash and IsActive unchanged; exactly one effective successor; linked decision/audit; append-only deletion denial. Local positive SQL fixture bypasses the native-origin predicate only in disposable memory because Linux has no Windows principal; it is not native Windows acceptance.
- Existing Python suite: 634 passed. Existing .NET review regression: 296 passed.
- Windows UAT extended from 108 to 122 checks; native result must be read from the exact commit's workflow, not inferred from cross-building.
- Baseline Windows project and actual PRM WPF overlay can cross-build. The full supplied preflight overlay has pre-existing cloud-missing ReviewClosureSchemaContract/WaterEvidencePreflightSql helpers; no stubs or unrelated Water changes added. Its modified section is also compiled against baseline. The original Windows source includes those helpers and must build before installation acceptance.
- Site QA approval, actual ledger/audit readback, old-row hash comparison and new Preflight result are still pending. No Production activation, PR merge or release acceptance claimed.
