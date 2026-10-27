# EQ001 isolated UAT Water registration

## Problem and resulting behavior

The restricted desktop failed Water registration with SQL229 updating LIMS_NumberSequences. Samples, SampleTests, ElectronicSignatures and AuditTrail were also read-only, so a numbering-only grant would not restore the workflow. Trusted UAT Water registration now uses the existing TLS/pin-validated signing service and a narrow owner-executed SQL boundary. No direct client write permission is added.

PW/PTW numbers, sample receipt, controlled test/specification snapshots, optional Water Plan linkage, signature, audit and request receipt commit together. Existing frozen plan identity/assignments and effective specification precedence remain intact. Limits that cannot fit decimal(18,4) exactly are rejected rather than rounded. Legacy nontrusted registration and EM/PRM behavior remain unchanged.

The user sees a number only after confirmed commit. Cancelling the signature does not reserve a number. Before dispatch, current-user DPAPI protects the exact payload and stable RequestId in a private profile directory. A lost reply retains this record. Save Registration then offers recovery of the original saved request or terminal service cancellation. A pending lookup never authorizes a new ID; a late original request cannot create after terminal cancellation. Same-ID retries return the original result. Changed payload/caller replay is rejected.

Label preview/printing follows confirmed registration. A successful print needs a separate fresh confirmation to record its acknowledgement. Cancelling or failing this step does not undo registration or create another sample. The printed flag/audit are idempotent and bound to the original actor/caller/receipt.

## Scope and limitations

EnableWaterRegistrationUat defaults false. Enabling it requires exact effective server tcp:AMEEN,1433, database EQ001_UAT_AMEEN_20261024, UAT deployment mode and no Production activation. SQL independently checks the exact site and original independent Windows service login. This change does not implement Water result entry, review, approval or certificates through the service and does not claim laboratory/Production acceptance.

The SQL fixture adapts the literal site/origin-type checks only in an ephemeral test copy to exercise distinct SQL service/client logins. It preserves actual site column widths/precision and the original frozen-plan triggers. This is not proof of the enabled endpoint over native Windows pipe; standard-user site acceptance below is required.

## Controlled rollout

1. Fetch the exact reviewed PR62 commit; do not switch/reset the original site checkout. Run Install-Eq001AmeenSource.ps1 -SourceCommit <exact> -WaterRegistrationOnly without Apply. It checks all local source prerequisites and changes nothing.
2. Close PharmaLIMS and, from elevated AMEEN\Elite PowerShell, stop PharmaLIMS.TrustedSigning. Apply that same installer with -WaterRegistrationOnly -Apply. It saves durable rollback bytes before changing source. Keep source-drift guards; never adjust hashes merely to force an installation.
3. Invoke scripts/Deploy-Eq001WaterRegistration.ps1 from the exact fetched Git commit, passing -SourceCommit <exact> without Apply. It verifies installed source, desktop/service target/TLS, service executable identity and private directory protection. No configuration/secrets are printed.
4. Invoke the same deployment script with -Apply. It requires the desktop closed and service stopped. It saves and hashes full desktop/private-service runtime rollback copies before building the host and desktop, restores exact desktop settings bytes, stages only the new Water SQL boundary in a transaction that verifies historical business-table hashes, refreshes/verifies service runtime files without overwriting private config/PFX, and atomically enables only the independent Water flag. It starts the service and verifies its pipe. The delayed-auto startup mode is preserved.
5. A failure leaves the desktop closed and attempts to stop the service/restore pre-rollout config bytes. Preserve the printed protected rollback directory. SQL staging is additive; never drop receipt tables once business requests exist or silently retry the old supersession staging. Restoration of a prior runtime requires the saved source/runtime inventory and operator review of the completed rollout phase.

Do not execute DesktopSessionOnly for this change: host code and new SQL are involved. Do not rerun the older PRM boundary script. Never expose private hostsettings, connection credentials or PFX bytes.

## Actual site acceptance

Use only synthetic UAT entries, exact existing restricted Windows actors and fresh PharmaLIMS passwords; no GUI as Elite. Verify service running and the expected UAT pipe before opening the desktop.

- Register a deliberate synthetic PW sample with one currently approved test, actual timestamps/receipt fields and independent signed reason. Verify one number, one Samples row, exact SampleTests snapshots, registration signature actor/role/source and matching audit/receipt. Original client DML denials must remain unchanged.
- Cancel a fresh signature and verify no number/sample/signature/audit was created.
- Recover the same request after a simulated lost reply and verify no duplicate. Test terminal cancellation against delayed/repeated dispatch.
- Exercise PTW, microbiological incubation, rejected receipt and planned registration/recollection. Confirm frozen plan identities/test assignment stay unchanged.
- Verify actors without CanRegisterSamples, wrong/stale proofs, invalid current specification or future timestamps cannot register.
- Check label print cancellation/failure versus successful separately authenticated acknowledgement; registration remains committed in all post-commit print outcomes.

Do not treat Preflight PASS or a portable build as these actual site acceptance results. Existing equipment/history warnings remain separate and are not repaired by this change.
