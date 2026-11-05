# EQ-001 Equipment Activities — Controlled Source Review
Date: 2026-11-03
Status: SOURCE STAGED / RELEASE BLOCKED
Single canonical source: E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS
Single established EQ001 UAT: EQ001_UAT_AMEEN_20261024

## Implemented in the source
- EquipmentDraft: trusted WPF signature, native SID/user/rowversion/one-use ticket; bounded trusted service store; request receipt and append-only audit installer; DPAPI recovery.
- EquipmentLink: supports ordered PRM/EM/WATER batch associations (up to 100), optional single-test association, validates source membership, signer ownership, duplicate/idempotent receipts; DPAPI recovery.
- EquipmentConfirm: requires current eight-byte VersionToken, bounded use timestamps, registered links, technician ownership, signed request, receipt and append-only audit; DPAPI recovery.
- Service host: EnableEquipmentDraftUat, EnableEquipmentLinkUat, EnableEquipmentConfirmUat all default false. Link depends on Draft; Confirm depends on Link. Verified Windows identity, native service permissions and encrypted SQL prerequisites retained.
- Existing trusted Review/Approve stage signing is left intact.
- SubmitNonUseExecution: currently NOT implemented over trusted boundary; isolated EQ001 UAT UI now rejects it with an explicit controlled message rather than silently attempting unavailable SQL writes. Existing non-EQ001 behavior preserved.

## SQL source reviewed but NOT activated
- scripts/EQ001_TrustedEquipmentDraft_Installer_REVIEW_ONLY.sql
- scripts/EQ001_TrustedEquipmentLink_Installer_REVIEW_ONLY.sql
- scripts/EQ001_TrustedEquipmentConfirm_Installer_REVIEW_ONLY.sql
Each contains an unconditional THROW 57198 SECURITY HOLD. None were run.
The four older Windows-only prototype SQL installer scripts remain security-held.
The signer-only SQL definitions are review candidates, NOT validated database migrations; do not remove their holds without independent security and QA review.

## Verification observed
- WPF Release build: PASS, 0 warnings, 0 errors.
- TrustedSigning.Host Release build: PASS, 0 warnings, 0 errors.
- PharmaLIMS.ReviewRegression: PASS, 423 passed / 0 failed (11 equipment-contract cases added).
- Read-only Assert-EQ001-EquipmentSecurityGate.ps1: PASS; 0 failures.
- EQ001 UAT existing equipment activities: 3, unchanged.
- Original insecure prototype procedures installed: 0.
- New signer-only Draft/Link/Confirm procedures installed: 0.
- Earlier unrelated integration suite: SQL schema integration PASS; no new native EQ001 execution tests have been run for these staged procedures.
- Installed client at C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309\PharmaLIMS.exe NOT replaced; trusted signing Windows service NOT redeployed.

## Critical before UAT release
1. Perform independent SQL permission, atomicity and idempotency review of all three held installer scripts. Test on a disposable isolated SQL test target and record proof without modifying established UAT.
2. Design and implement structured/calibration/cleaning SubmitNonUseExecution trusted boundary with complete numeric/annex and audit verification, preserving historic records and original approval locks.
3. Cover unauthorized Windows SID, expired/replayed ticket, wrong app username, stale rowversion, unknown transport outcome, duplicated activity-link IDs, disabled role, password changes and transaction rollback.
4. Reconcile the source with the installed v309 release provenance; deploy only an approved, versioned build with retained UAT configuration.
5. Verify on the actual Windows desktop: Draft -> Link -> Confirm / Submit -> Review -> Approve -> Print with separate Technician/Reviewer/QA credentials; QA-approved Freeze/Release only then.

## Safety
No new production claim; all new equipment write paths are disabled. No changes to established UAT roles, schema, data, service credentials, operational certificates or approved signatures.
