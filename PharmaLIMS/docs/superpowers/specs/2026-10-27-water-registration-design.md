# EQ001 UAT trusted Water registration design

Status: proposed; implementation and site deployment not performed.

## Problem and intent

Actual AMEEN UAT Water registration fails with SQL229 on UPDATE dbo.LIMS_NumberSequences (AB6158EB). AMEEN\LimsUatAdmin has SELECT but no INSERT/UPDATE/DELETE on the sequence, Samples, SampleTests, ElectronicSignatures, and AuditTrail. NewSampleDialog.SaveWaterSample still directly writes all five. Extending number-table permissions alone cannot restore registration. Login and Preflight PASS do not prove the registration workflow works.

Restore Purified Water and Potable Water registration for authorized standard Windows users through the existing trusted signing service. Preserve current receipt, timing, specification, planning, and signature controls; leave client direct signature/audit writes unavailable. Scope is exact isolated UAT EQ001_UAT_AMEEN_20261024 on AMEEN, not Production readiness or acceptance of other workflow stages.

## Verified inputs

- Site raw DatabaseHelper.cs SHA256: 84EDEDAB0DF1D219DDE8B38C005B46A62BD277004EA0A18B923660EF53796465.
- Site raw NewSampleDialog.xaml.cs SHA256: 30FCF8DE611B8A3870E050E0729667F9B1AEEB3D2B5EF6AE0F7C5A0660CB7D07.
- Both exactly match the current repository source. Preserve unmodified DatabaseHelper behavior outside this feature.
- Actual core schema received: Samples.SampleNumber nvarchar(20); LIMS_NumberSequences identity column Id; SampleTests limits decimal(18,4); Samples timestamps datetime. Never assume the baseline identity name SequenceID or silently truncate specification precision/strings.
- Actual related schema received: Tests, WaterTestProfiles, WaterSpecifications, Water_Plans, Water_PlanSamples, Water_PlanSampleTests, Water_PlanSampleAttempts all present.
- Audit and electronic-signature append-only triggers remain enabled. Water Plan test-freeze and point-identity protection triggers are enabled; point identity/assignment cannot be rewritten after Distribution. Registration status/sample linkage updates are allowed.
- Existing TLS public certificate and pin, Windows caller mapping, fresh authentication/version tickets, UAT SQL security, and native service are established. No new account or trust changes are part of this fix.

## Approaches

1. Grant direct client writes. Smallest change but permits bypassing the trusted signature/audit boundary; ruled out by the accepted deployment constraints. Broad db_owner/db_datawriter or launching the desktop as Elite are also ruled out.
2. Move the entire Water lifecycle to the service immediately. Addresses future stages, but unnecessarily couples this confirmed registration failure to result/review/approval/certificate migration. Those paths require separate evidence and acceptance.
3. Extend the existing service for complete Water registration, planned registration, and registration label acknowledgement. Recommended: restores the failing operation and its directly coupled writes while preserving controls and making the change independently testable. User requested repair; this document proposes the concrete implementation before crossing the existing service/SQL boundary.

## Request and authorization

Add a typed Water registration payload carried by the existing trusted TLS pipe application envelope. Keep protocol action/JSON bounds explicit. Include point ID, water type, test IDs, actual sampling/receipt/incubation timestamps, sampled/received personnel, container count/condition, sample volume, receipt temperature/decision/reason, incubation program/instrument, and optional WaterPlanSampleID. Do not accept client-created sample numbers, status, limit snapshots, actor role, signature time, or precomputed database identity as authority.

Require a fresh electronic-signature reason and meaning; use the existing one-use reauthentication ticket bound to kernel caller SID, canonical application username, authentication row version and expiry. SQL rechecks actor active/unlocked/password-change state and CanRegisterSamples at commit, and rejects account/version changes after reauthentication. Follow the existing authenticated UAT application-ticket model rather than introducing a weaker username claim. Tests must cover an application actor/native caller combination that violates the configured ticket/caller rules.

Add host setting EnableWaterRegistrationUat, default false, independent of the existing PRM enable flag. Require DeploymentMode UAT, Production activation false, effective server tcp:AMEEN,1433 and exact database EQ001_UAT_AMEEN_20261024 in host configuration validation; SQL independently requires exact DB_NAME and the established AMEEN signer origin. Both service-host entry points must pass the explicit flag. Fail closed for a non-exact UAT target and for missing procedures. Never fall back to desktop direct SQL when trusted mode is active, including when Water is disabled.

## Service SQL boundary and atomic commit

Add a narrow service-only stored procedure using the existing EXECUTE AS OWNER pattern. Validate original Windows service SQL origin against the existing signer principal SID and reject sysadmin/client origins. Grant only the named procedure EXECUTE to the existing independent service principal; do not grant new table DML or client EXECUTE. Preserve all existing roles, grants, triggers, and historical rows.

One serializable transaction must:

1. Lock/revalidate the actor and the request receipt key.
2. Load the current active Water sampling point and verify requested Water type matches it; derive point snapshots from database values.
3. Load current active Water tests and approved/effective controlled profile/specification rows. Preserve the existing deterministic TOP 1 ordering: point-specific before global, EffectiveFrom descending, then SpecificationID descending. Multiple eligible historical/global rows alone are not an error. Preserve effective dates, controlled-reference requirement, fallback from Alert/Action to Lower/Upper limits, and WaterNumericResultEvaluator.RequiresNumericLimits/HasCompleteLimits checks. Reject missing controlled evidence or incomplete required numeric limits; never accept a client-supplied limit. Preserve selected tests and controlled snapshot text.
4. Validate required receipt fields and lengths against the actual schema; validate the receipt-decision vocabulary and reason for rejection. Use the authoritative SQL clock with the existing one-minute future tolerance. Preserve sampling <= receipt <= incubation sequencing, accepted/rejected behavior and microbiological incubation requirements. Derive Registered/Incubation/Rejected status and incubation end from validated policy instead of trusting client status/duration.
5. For planned registration, lock plan/sample/test assignment; allow the existing Distributed, In Collection, Recollection Required statuses; enforce point/type and exact selected-test set; permit existing rejected-attempt recollection while preventing duplicate linked registration. Do not modify protected identity/assignment fields.
6. Allocate the existing PW/PTW database-year number under locks, respecting existing maxima and the actual 20-character SampleNumber capacity. Concurrent registrations cannot duplicate a number. No number is reserved before fresh signature acceptance.
7. Insert Samples, exact SampleTests snapshots, optional plan attempt/link/status changes, registration signature and audit entries, and a durable request receipt together. Bind evidence to authenticated actor, native caller SID, request ID and database-generated time; use explicit existing SourceApplication semantics. Do not invent fields or rewrite past evidence.
8. Commit and return authoritative SampleID, SampleNumber, Status, inserted-test count, and a registration receipt identity.

Require each captured limit to round-trip exactly into actual decimal(18,4). Reject overflow and nonzero digits beyond four decimal places with an explicit validation result. Silently rounding a controlled limit from decimal(18,6) is unacceptable. No snapshot-column precision migration is part of this repair.

Any pre-commit failure rolls back all changes including numbering, plan linkage, signatures/audit and receipt. Avoid the old second standalone void-number write after a rolled-back registration; the new operation has no committed number to void. Existing historical void records remain untouched.

## Duplicate, cancellation, and uncertain outcome

Use a stable RequestId and canonical payload digest. Persist actor, caller SID, digest and authoritative committed response in a service-only append-only receipt table. An exact retry by the same authenticated caller returns the committed response without another sample/signature/audit/number. Reuse with changed payload/actor/SID rejects. Every retry still needs valid fresh authorization; possession of a RequestId alone grants nothing.

Cancelling the signature before dispatch writes nothing. Disable simultaneous Save and lock/switch while dispatch is active using existing session guards. A transport loss after dispatch may occur after commit: display outcome unknown and preserve the request identity and form; do not claim rollback or create a fresh request automatically. Before first dispatch, durably save RequestId, canonical payload and target in a current-user DPAPI-protected recovery record with caller-only ACL; the form contains personnel/sample information even though it contains no password. Recovery verifies SID and effective UAT target and obtains fresh reauthentication, then retries the identical payload with the identical RequestId. Do not persist passwords, reauth tickets or service credentials. A receipt lookup returning not found is never permission to generate another RequestId: the original request may still arrive/commit. Keep the pending operation until same-ID retry establishes a committed outcome or an explicit service-side terminal rejection/cancellation receipt serializes against late dispatch. Terminal cancellation under the same receipt-key lock must either return the already committed response or durably reject later creation using that RequestId before allowing edited/new input. Delete or mark the local recovery record complete only after durable server resolution. Test a crash between dispatch and reply and a delayed original dispatch racing with recovery/cancellation.

## Label printing and UI

After confirmed commit, show the authoritative number and existing success summary. Set RegisteredSampleId/Number/Status and clear dirty state from the committed result independently of printing. Preserve planned-dialog result behavior.

Keep the existing label preview/print action. A successful print acknowledgement is another bounded service operation tied to the committed registration receipt and the same authenticated actor/caller. Policy choice proposed here: require a fresh one-use reauthentication ticket, current CanRegisterSamples, and an acknowledgement reason after successful print; this adds one confirmation to the old printing flow. No direct client UPDATE Samples or INSERT AuditTrail. The service changes only LabelPrinted and appends the matching audit, idempotently. A printer, reauthentication, or acknowledgement failure must state that registration succeeded and printing/print-recording failed; it must not reopen registration or retry creation. A cancelled print writes no acknowledgement. This policy is explicitly part of the design approval, not represented as an existing user requirement.

Preserve legacy registration outside trusted UAT mode. EM registration and PRM behavior remain unchanged. No unrelated UI redesign, migrations at startup, credential bypass, or suppression of Preflight warnings.

## Implementation boundaries

- Shared trusted library: typed request/result/policy and serialization limits.
- Desktop: NewSampleDialog trusted registration branch; TrustedApplicationSession ticket consumption and response checking; isolated service client/recovery helper if necessary. Existing untrusted mode retains its path.
- Host: explicit Water registration/receipt/label actions, independent UAT gate, authenticated dispatch and store.
- SQL: separate guarded, idempotent UAT staging artifact, exact-origin procedure(s), receipt table and only required service EXECUTE permissions. Reject unknown preexisting definitions.
- Packaging: guarded source prerequisite manifest/installer with source rollback; controlled service binary rollout and schema verification. DesktopSessionOnly mode is not suitable because this fix adds host and SQL behavior.

## Verification and acceptance

Use actual site schema widths/precision, existing append-only/plan triggers and client denial permissions in the SQL test fixture. Required behavioral checks:

- PW and PTW accepted/rejected, physical/chemical-only versus microbiological incubation, manual-time required/sequence/future validation, valid reason and actor evidence.
- Active point/type/test validation, controlled-spec point precedence/effectivity, missing or changed spec, precision overflow or unrepresentable limits.
- Planned exact test assignment, frozen identity protection, cancelled/stale plans, repeat registration, rejected recollection and concurrent plan claims.
- Cancellation before dispatch creates nothing; forced failure at each write leaves no partial rows/number/link/evidence.
- Missing/expired/replayed ticket, wrong SID, inactive/locked/permission/version-changed actor, direct client procedure calls and service origin spoofing are rejected.
- Concurrent registrations have unique numbers; identical RequestId returns one result; changed replay rejects; post-commit transport loss/restart recovers the same registration.
- Successful/cancelled/failed print and repeated acknowledgement do not duplicate registration or misstate its outcome.
- Original signature/audit/history evidence and direct client denials remain unchanged.

Run existing regressions and original-site overlay desktop/host builds. Run real Windows pipe/SQL acceptance as standard actors, with existing TLS and service identity, before claiming this fixed at AMEEN. Independent review must cover auth, transaction scope, idempotency, specification snapshots, rollback, lost replies and printing after commit. A green portable build alone is insufficient.

## Controlled site rollout

Prepare exact immutable source and reviewed SQL artifacts first; do not repeat the old supersession deployment. Close desktop, stop signing service for the new coordinated rollout, save verified source/runtime/schema-definition/grant rollback evidence and untouched private configuration/PFX hashes. Build, stage only the new Water boundary with pre/postcondition checks and no history changes, refresh only changed service binaries with hash verification, restore protected settings bytes, then start service and verify its UAT pipe. Preserve the site's delayed-auto start configuration. Do not switch/reset the local original branch or merge PR62.

Final actual acceptance: one deliberate synthetic UAT Water registration as a standard authorized actor, verify committed number/test snapshots/signature/audit/receipt and expected role, then cancellation/denial/concurrency evidence. Use explicit UAT labeling; no physical equipment readiness or official specimen disposition is inferred. Result entry, review/approval, certificates, EM, backups/reboot and final laboratory release remain separate acceptance items.

