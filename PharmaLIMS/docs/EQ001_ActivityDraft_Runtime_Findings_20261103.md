# EQ-001 Activity Draft — Verified Runtime Findings
Date: 2026-11-03
Status: BLOCKED / NOT DEPLOYED
Target: EQ001_UAT_AMEEN_20261024
Installed executable: C:\ProgramData\PharmaLIMS\EQ001-UAT-20261024\client-v309\PharmaLIMS.exe
Observed Windows process owner: AMEEN\LimsUatAdmin

## Evidence
1. User observed Create Activity Draft failure (user-facing reference 289121A8). Underlying logged exception has not yet been recovered, so do not claim it is the exact SQL error code.
2. Reviewed R9-M03 v309 source Services/MicroEquipmentActivityService.cs: CreateDraft uses direct INSERT dbo.MicroEquipmentActivities, via normal WPF SQL transaction. AddLink / ConfirmUse / SubmitNonUseExecution likewise use direct database writes.
3. Read-only database metadata in EQ001_UAT_AMEEN_20261024: role PharmaLIMS_EquipmentWindowsUat explicitly DENY INSERT, UPDATE and DELETE ON SCHEMA::dbo, GRANT SELECT. No EQ001_UAT_CreateMicroEquipmentDraft / Link / ConfirmUse / SubmitExecution prototype procedures are installed.
4. Trusted signing is implemented for *signed stages*: EQ001_CommitActivitySignature, EQ001_CommitEquipmentSignature, MicroEquipment_SignActivityStage and MicroEquipment_ValidateActivitySignatures are present in EQ001 UAT. Dedicated PharmaLIMS_TrustedEquipmentSigner has scoped EXECUTE grants. Service PharmaLIMS.TrustedSigning was Running.
5. Canonical source attempts to route unsigned activity mutations to four uninstalled prototype stored procedures. All four corresponding SQL templates intentionally contain unconditional THROW 57198 SECURITY HOLD, because Windows-only actor-name checks do not prove an active authenticated application session.
6. Previous development and testing of signed activity stages therefore must not be confused with completion of Draft/Link/Confirm/Submit under EQ001 security boundary.

## Diagnosis
**Confirmed contract mismatch**: installed client uses direct DML for unsigned equipment activity mutations, while current UAT security policy forbids client-side schema DML.
**Still unverified**: exact SQL exception associated with user-facing reference 289121A8, and complete native end-to-end UAT with separate Windows/app identities.

## Safe implementation sequence
- Preserve all UAT records, roles, privileges, signer credentials and historical signatures. No GRANT schema-level DML; no removal of security holds.
- Reconcile canonical source identity with installed v309 signed-stage code before deployment.
- Extend existing TrustedApplicationEndpoint reauthentication-ticket protocol to bounded EquipmentDraft / EquipmentLink / EquipmentConfirmUse / EquipmentSubmitExecution operations, binding authenticated username, Windows SID, authentication rowversion and one-time ticket.
- Implement signer-only SQL procedures with atomic receipts, idempotency, append-only audit, ownership, role and rowversion checks, independently validated numeric evidence, and no unreviewed permission widening.
- Test wrong SID / actor / ticket replay, password change, stale version, direct SQL client, concurrent update, signed immutability, successful draft->link->confirm->review->approve and print.
- Require full Release build, isolated database integration, separately authorized Windows UAT acceptance, controlled deployment, QA release authorization.

## Controls held
The installed v309 executable, live trusted service, EQ001 UAT database and client/SQL permissions were not modified during this diagnosis. Do not interpret prior regression/test PASS as an equipment workflow release approval.
