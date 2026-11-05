# PharmaLIMS v309 — Trusted Admin diagnostics hotfix

Purpose:
- Keep existing authorization, electronic-signature, rowversion, audit and password controls.
- Validate non-secret Reset Password request fields before crossing the trusted pipe.
- Preserve server-side reauthentication and authorization.
- Replace opaque `Denied` for User Management with safe deterministic rejection categories.

No database schema or data migration is included.
No password hash, activity record, signature or audit evidence is modified by deployment.

After deployment, retry Reset Password once. If rejected, the client message/log will state
whether the cause is target rowversion, password policy, reason, permission, signer binding,
audit actor binding, or another controlled administration state.
