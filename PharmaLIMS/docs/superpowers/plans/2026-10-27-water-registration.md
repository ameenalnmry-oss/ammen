# Trusted Water registration implementation plan

> Execute inline with superpowers:executing-plans; one fresh final reviewer. User approved the design by “استمر”.

**Goal:** Restore complete, atomic PW/PTW registration for restricted EQ001 UAT clients.
**Architecture:** Typed TLS service commands, fresh SID/version-bound tickets, service-only SQL procedure and durable idempotency receipts; current-user encrypted desktop recovery. Keep legacy nontrusted registration unchanged.
**Tech stack:** Existing WPF .NET8, Microsoft.Data.SqlClient, SQL Server2022, Windows DPAPI/pipe TLS.
**Spec:** ../specs/2026-10-27-water-registration-design.md

## Global constraints

Exact site database EQ001_UAT_AMEEN_20261024, host tcp:AMEEN,1433. Native CI may provision the same logical database on its ephemeral server, using a test-only SQL script substitution, never a production bypass. No client direct DML/signature/audit grants. Preserve histories, append-only triggers, standard users, certificate pin, original site overlays/settings/PFX, original checkout and PR62 unmerged. New feature disabled by default. No startup migrations.

## Review focus

Lost reply/restart cannot turn into duplicate creation; no receipt lookup absence authorizes a new ID. Changed payload/SID replay rejects. Numeric snapshots require exact decimal18,4 conversion. Printing failure never negates registration. Existing Water Plan frozen identity/test assignments stay enforced.

### Task1: Typed contracts and exact input policy

Files: tools/PharmaLIMS.TrustedSigning/WaterRegistrationRequest.cs; tests/PharmaLIMS.WaterRegistrationRegression/{csproj,Program.cs}. Consume existing envelope; produce WaterRegistrationRequest, WaterRegistrationResult, WaterRegistrationPolicy.Validate/CanonicalPayload/RequireTarget/IncubationDays/RequireExactLimit.
- [x] Write behavior tests for target gate, canonical ordered test IDs, receipt/required fields, duplicate IDs, dates, allowed incubation programs and exact precision. Observe RED, implement, then GREEN.

### Task2: Atomic SQL boundary and store

Files: scripts/EQ001_UAT_Water_Registration.sql; tools/PharmaLIMS.TrustedSigning.Host/TrustedWaterRegistrationStore.cs; tests/water_registration_sql.py and isolated schema fixture. Consume canonical payload, actor/version/SID, stable RequestId; produce result or explicit terminal cancellation. Service SQL calls exact procedure with action Register/Resolve/Cancel/Label and original request ID/digest. SERIALIZABLE/receipt-key applock held through transaction; no client-visible DML permission.
- [x] Write failing actual-shape SQL fixture checks: direct client denial, accepted/planned/rejected, rollback, precision, spec priority, duplicate/concurrent/stale-plan, cancelled/changed replay and label acknowledgement.
- [x] Implement guarded stage script plus host store, preserve original TOP1/numeric rules, test SQL on disposable Linux SQL (native origin checked in Windows). GREEN all behavior cases.

### Task3: Authenticated host dispatch and enablement

Files: TrustedApplicationEndpoint.cs, Program.cs, TrustedHostSettings.cs, hostsettings.example.json. Consume Task1/2; produce WaterRegister/WaterResolve/WaterCancel/WaterLabel actions, fresh proof for every call and version check. Add EnableWaterRegistrationUat=false independent of PRM. Both constructors enforce exact target; SQL rechecks actor/version at commit. Return typed serialized response, sanitize errors.
- [x] Add RED gate/dispatch regression checks; implement, cross-build host, rerun existing security checks.

### Task4: Desktop recovery, registration and printing

Files: Services/TrustedWaterRegistrationClient.cs, Services/WaterRegistrationRecovery.cs, TrustedApplicationSession.cs, NewSampleDialog.xaml.cs. Consume Task1/3; produce trusted branch before legacy numbering; durable DPAPI record containing exact payload+RequestId before dispatch, SID/target/payload validation, recovery same-ID replay or serialized cancel. Keep cancellation, confirmed success and uncertain outcome distinct. Label acknowledgement separately fresh-signs and cannot re-register.
- [x] Add failing behavior tests for durable-before-dispatch/response identity/error recovery; implement; new source integration guards ensure no local fallback in trusted mode. Build original desktop/host overlays.

### Task5: Qualification, installer and rollout artifact

Files: new Water source prerequisite manifest and installer mode, Water deployment/build/site script, tests/Test-Eq001SourceInstaller.ps1, native UAT integration, source manifest, docs. Preserve existing modes and exact f2c20c7 site bytes; new host source baselines from exact installed8036813/409 as appropriate. Capture rollback before mutation. Deployment explicitly stops service and preserves private configuration except explicit independent Water flag. Enable only after guarded SQL staged and binaries match.
- [x] Write failing installer/target/artifact guards, implement complete replacement source packaging. Run portable behavioral suites/Python/all relevant builds, native Windows CI and actual-shape SQL tests.
- [ ] Final fresh review, resolve Important findings RED/GREEN, recheck diff/manifest. Commit/push to authorized existing PR62, no merge. Provide site read-only/preflight step first and actual UAT retest instructions. No site success claim until user evidence.

## Execution record

Progress, evidence and decisions: /workspace/.cloud-setup/water-registration/progress.md. Do not redo completed site session installation, PRM SQL rollout or trust configuration.

Local evidence: policy25, SQL39, Python646, review296, session14 and security checks passed; original-site desktop and host builds zero warnings/errors. Fresh read-only reviewer water_code_review found no Critical/Important defect. Native Windows CI and actual site registration remain pending; Task5 is not site acceptance.
