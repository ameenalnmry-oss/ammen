# EQ-001 site preflight follow-up — 2026-10-24

The actual AMEEN Release application (2026.10.19.306), running as AMEEN\LimsReview, is connected to EQ001_UAT_AMEEN_20261024 with trusted signing enabled. The service runs manually under the separate AMEEN\PharmaLIMSSigner identity. Fourteen database-context permission checks passed. Site signing and final acceptance remain outstanding.

## Confirmed failure and correction

A read-only site probe was denied access to Users.PasswordHashNew and PasswordSalt. Windows CI run [40109014539](https://github.com/ameenalnmry-oss/ammen/actions/runs/40109014539) independently reproduced the same failure when invoking the real identity preflight under the restricted desktop principal. The supplied local preflight additionally called a Development-only legacy Windows UAT gate in the Release configuration.

Trusted signing now selects a read-only readiness view instead of accessing verifier columns. The new isolated-UAT script exposes only Username, IsActive and a credential-initialized flag, preserving NULL and whitespace detection. It never updates users, signatures, audit history or existing approved data. Existing direct credential-column DENYs remain in force. Missing readiness deployment is a blocker, with no raw-credential fallback. The existing view definition must match exactly before a repeated deployment is accepted.

The complete AMEEN source overlay preserves all supplied Water, PRM and review-closure changes. Its prerequisite digest is computed from the actual uploaded local SystemPreflightService.cs, not from the older cloud version. The original guarded installer handles locking and durable rollback.

Five additional Windows/SQL checks exercise the actual preflight: raw credential reads remain denied after deployment; initialized credentials pass; NULL and whitespace fields remain blockers; missing deployment fails closed. Final CI qualification is separate from site verification.

## Controlled site continuation

1. Close the original application and stop only the EQ-001 UAT signing service before applying the guarded source installer from the qualified commit. Do not switch branches, overwrite an unknown source file or create another project checkout.
2. Apply scripts/EQ001_UAT_Credential_Readiness.sql only to EQ001_UAT_AMEEN_20261024. No production permission cutover is authorized.
3. Rebuild the original Release host/WPF dependency, refresh protected runtime artifacts with the service stopped, and preserve appsettings.json, hostsettings.json and all certificate/password files. Verify copied file hashes before restarting the same service.
4. Correct inherited profile environment in the reviewer process before further diagnostics. A GUI credential launch inherited Elite's LOCALAPPDATA despite the correct LimsReview Windows token; do not grant access to Elite's private folders.
5. Rerun the affected identity preflight and continue audited reviewer password recovery and site E2E. Do not infer site acceptance from CI or an administrative login.

## Unresolved independent gate

The site preflight reports an existing approved PRM profile without matching controlled review/approve evidence. This change does not repair, relabel, suppress or retrospectively sign that historical record. The blocker remains subject to its controlled PRM workflow. EQ-001 is not declared production-ready and PR #62 must remain unmerged pending full UAT evidence.
