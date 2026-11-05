# EQ-001 Equipment Operations: Trusted Service Boundary Contract
Status: DESIGN REVIEW / NOT DEPLOYED
Date: 2026-11-03
Environment: EQ001_UAT_AMEEN_20261024
Canonical source: E:\PharmaLIMS-MicroEquipment-SINGLE-VERIFY-20261012\PharmaLIMS

## Security-critical finding
The four earlier prototype SQL procedures used ORIGINAL_LOGIN() and a mapped PharmaLIMS username without independently validating a current authenticated PharmaLIMS session. A process running as an authorized Windows user could call them outside the WPF application. The scripts now contain an unconditional SECURITY HOLD; do not remove it for deployment.

## Required service boundary
- Use the v309 TrustedApplicationEndpoint reauthentication-ticket pattern. A ticket must be issued following successful application password authentication and bound to authenticated username, caller Windows SID, current AuthenticationRowVersion and a short expiry; consume it exactly once.
- Every Equipment request must carry a unique RequestId, action, actor, structured payload, reason and meaning. Do not trust actor supplied by the client; bind it to the consumed authenticated ticket.
- Allow only operations: EquipmentDraft, EquipmentLink, EquipmentConfirmUse, EquipmentSubmitExecution; add read/resolve/cancel operations where required for recovery after ambiguous transport failure.
- Require CanEnterResults for creation, linking and execution. Protect Review/Approve through the existing dedicated trusted signing path and role-based permission checks.
- The trusted service, not the Windows client, must connect as the dedicated AMEEN\PharmaLIMSSigner principal and invoke constrained stored procedures. SQL must validate ORIGINAL_LOGIN() against the signer database principal SID and reject sysadmin/elevated origin.
- Revoke/deny any equipment write grants from Windows user clients and equipment app roles. Grant EXECUTE on individually reviewed procedures only to PharmaLIMS_TrustedEquipmentSigner. Retain existing client-side schema DML DENY.
- SQL transaction must atomically update activity state, links, structured evidence, calibration points, append-only audit and any required signatures. Never update/decrement already signed records.
- Preserve the original activity/performed-by, approved status, timestamp/time zone, equipment calibration snapshot, and immutable audit chain. Do not set CalibrationStatus=Calibrated unless an independently reviewed calibration record supports it.
- All test outcomes must be independently derived in the trusted service/database from numeric evidence rather than blindly accepted from the UI.
- Add per-request receipts/idempotency: duplicate RequestId with same authenticated actor, SID, operation and payload returns the prior result; mismatched duplicates are rejected.

## Required verification before controlled UAT deployment
1. Source reconciliation: compare SHA-256 manifest of 26 differing files. Ten tracked modified, twelve untracked, four tracked clean. Review all differences, preserve existing work; do not copy v309 over the canonical project blindly.
2. Build: WPF app and service host Release with 0 errors, review regression, database integration and signer negative tests.
3. Authentication negatives: wrong Windows SID, wrong actor, missing/expired/replayed ticket, password changed between request/commit, inactive/locked account, role denied, foreign service principal and direct SQL caller all fail with zero data mutation.
4. Workflow positives: draft -> linked sample -> confirmed use; cleaning/calibration with structured rows and 1..100 points; 2-stage review/approval signed; print/re-read record and timestamps.
5. Data integrity: atomic rollback on invalid point, duplicate links and resubmission, concurrent edits, stale rowversion, signed record tampering, append-only triggers, correct numeric PASS/FAIL.
6. Controlled UAT release: verified copy-only DB backup, source/build hash identity, install service and client under one approved package, post-install SQL permission inventory, Windows user acceptance, then freeze.
7. Production release is separate and requires QA validation and traceable approval.

## Current status
Only an earlier SQL-compilation/rollback test was passed; the new trusted service routing and real-account positive UAT acceptance have NOT been implemented or qualified. The four prototype scripts are explicitly security held.
