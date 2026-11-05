# 2026-10-31 UAT hotfix — split-database identity authority

- Activity Review/Approve reauthentication now verifies the caller-bound PharmaLIMS credential against the primary `EQ001_UAT_*` database, which is the same authority used by interactive login and account administration.
- The isolated `PharmaLIMS_UAT_*` activity database remains authoritative for activity state, local stage authorization, segregation of duties, rowversion and immutable signed evidence; it no longer treats its duplicated password-readiness fields as the credential authority.
- New ActivitySigning migration `20261031_001_Activity_Signing_Primary_Identity_Authority.sql` keeps the protected Windows-service origin gate and local Review/Approve permission gate while removing duplicated credential/lock/password-state checks from the isolated database.
- Interactive login now requires self-service password finalization when `PasswordChangedAt` is missing, even if historical data incorrectly has `MustChangePassword=0`.
- Self-service Change Password validates the password policy before crossing the trusted pipe, so policy failures are shown as specific validation errors instead of an opaque `Denied`.
- No activity is recreated and no password hash/salt is copied between databases.

# PharmaLIMS 2026.10.19.309 — trusted activity reauthentication correction

This candidate corrects the equipment-activity signing failure observed in v308 after Windows identity, TLS pinning,
database targeting, signer eligibility, segregation of duties and Activity 10 evidence were all verified.

## Root cause fixed
- v308 `TrustedActivityAuthenticator` reused the desktop `AuthService/UserRepository` path inside the independent
  signing service. That path requires direct credential-table access and did not match the dedicated signer's
  least-privilege SQL boundary.
- v309 adds migration `20261030_001_Trusted_Activity_Authentication_Bridge.sql` with three owner-executed procedures
  that are callable only from the mapped non-sysadmin `PharmaLIMS_TrustedEquipmentSigner` Windows service origin.
- The service loads only the caller-bound signer credential through the controlled bridge, verifies the PBKDF2
  password inside the trusted host, records failed attempts with the existing lockout semantics, and atomically
  confirms a successful reauthentication before the existing signed-stage transaction runs.
- v309 removes the temporary broad `dbo.Users` SELECT/UPDATE grants used by the v308 UAT configuration script.
  Password hashes/salts remain inaccessible by direct table SELECT to the service principal.
- Existing `MicroEquipment_SignActivityStage`, immutable signature evidence, segregation-of-duties checks,
  activity rowversion checks and signed-content integrity protections remain unchanged.
- Activity 10 does not need to be recreated; after migration and service update it can continue from `Submitted`.

## Required Windows/UAT sequence
1. Build v309 Release on Windows.
2. Run controlled Development database maintenance against the existing UAT database so migration `20261030_001`
   is recorded and applied.
3. Run `scripts/Configure-UatActivitySigning.ps1` as Administrator. It preserves the primary EQ001 database,
   targets only equipment activity signing at the configured UAT database, refreshes the trusted host binaries,
   and keeps a rollback directory.
4. Run System Preflight and require `Blockers: 0`.
5. Run the WPF application under the Windows SID mapped to `AMEEN\LimsReview`, sign Activity 10 Review,
   then use the independently mapped QA Windows account for Approval.
6. Record the actual Windows/SQL/UAT evidence before any Production release decision.

This source package does not claim Production acceptance. Runtime build, trusted signing, UAT and laboratory
equipment readiness must be demonstrated on the target Windows/SQL environment.
