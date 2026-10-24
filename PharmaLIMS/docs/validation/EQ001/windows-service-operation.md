# EQ-001 Windows service boundary

Implementation remains on PR #62. Production activation and production SQL grants are outside this change's execution scope. The default application configuration disables trusted signing; equipment saves have no direct-SQL fallback.

## Protected deployment inputs

- Install the host as `PharmaLIMS.TrustedSigning` under a dedicated Windows service account. Use `PharmaLIMS.TrustedSigning.Host.exe --service --config <protected-path>`.
- Protect the service binaries, host configuration, host `appsettings.json`, PFX and their directories against desktop-user modification. Host configuration/PFX ownership and access are restricted to the service account, SYSTEM and Administrators; the loader checks these ACLs.
- Configure a server-authentication certificate with a chain trusted by the desktop, the expected DNS name `PharmaLIMS.TrustedSigning`, private-key access for the service, and its SHA256 certificate hash in the desktop's `TrustedSigning.ServerCertificateSha256`. TLS chain/name verification remains enabled alongside the pin.
- Map each allowed Windows caller SID explicitly to its approved PharmaLIMS username in protected `WindowsAccounts`. Do not map shared Windows logins to different application signers. Requests naming another signer, unmapped callers and callers running as the service account are refused.
- Give the service its own encrypted, server-validated SQL connection. Its application configuration must match its build configuration and use the same isolated UAT database during qualification. Its internal `TrustedSigning.Enabled` must be false to prevent authentication recursion.

## SQL authority separation

The desktop SQL principal must not be a database owner, sysadmin, or a member of a role that can defeat this boundary. It must have no credential-material reads, no INSERT/UPDATE/DELETE access to `dbo.Users`, no direct equipment/equipment-signature evidence writes, and no execution access to `dbo.EQ001_CommitEquipmentSignature`. Preserve the existing permissions required by unrelated application modules while qualifying this restriction.

When trusted signing is enabled, login, reauthentication, password changes and signed account administration use the service. This preserves these workflows without granting desktop users the ability to replace a signer's password, permissions or lockout state. Account administration consumes a fresh, single-use, 30-second proof bound to the actual Windows SID, verified application account and current authentication rowversion. The server supplies the audit actor and signer.

The host establishes an internal audit-actor scope only after independent credential verification or validation of that administration proof. This asynchronous scope lets existing audited operations run without a WPF login session. It rejects a different requested actor and restores the previous context on disposal; it never assigns the desktop's global `Login.CurrentUser`. Desktop operations retain their existing session-based audit behavior.

The isolated CI fixture applies these permissions only to disposable UAT principals. It gives the service Windows SQL login the Users read/write and audit/signature INSERT permissions required by existing guarded authentication/administration code, plus execution of the controlled equipment commit procedure. It does not apply a production grant template or modify production SQL.

## Qualification evidence

`Invoke-Eq001WindowsUat.ps1 -IsolatedGithubUat` refuses non-GitHub-hosted or non-Windows execution. It creates a temporary local service account, trusted test certificate, SQL Express configuration and a uniquely named `EQ001_UAT_*` database. It runs the actual SCM service, native caller identity, WPF equipment/signature controls, protected login and SQL persistence paths. It also exercises invalid PFX/occupied-pipe startup, signer impersonation, direct SQL permission attacks, replay, stale rowversion, lockout and service loss.

Only sanitized check results and execution output are uploaded in the `eq001-windows-wpf-sql-uat` CI artifact. The private fixture, credentials and PFX remain outside the checkout and are removed in cleanup. Historical UAT control rows are hashed before and after; these controls are synthetic records in the disposable database, not approved production records.

CI UAT proves the tested build on its isolated Windows runner. Site deployment still requires the site's approved Windows accounts, certificate chain, SQL authority separation and acceptance evidence. Do not label EQ-001 production-ready while required Windows UAT or site qualification remains incomplete.
