# Manual lock and Windows user switch

User delegated the implementation choice on 2026-10-27. Implement inline in the existing PR checkout, without another approval cycle.

## Intent and approach
Add `Lock Session` and `Switch User` in the main window. Both end the authenticated PharmaLIMS session and return to a fresh login window. Lock does not retain unsaved forms. Require closing workflow windows and completing outstanding main-window operations before a manual transition, so no work is silently discarded. Preserve automatic timeout behavior.

A login-only switch would leave the native Windows identity unchanged and cannot satisfy equipment signing's caller mapping. A Windows desktop sign-out would disrupt unrelated work. Instead, `Switch User` opens the standard Windows credentials dialog and launches the same application, with a loaded target profile, as the selected local standard Windows account. PharmaLIMS authentication remains required in the new process. The login window also exposes this action after cancellation/failure. No shell, PowerShell, password command line or saved credential is used. Do not add mappings, trust certificates, change SQL, restart services or weaken the native caller boundary.

## Failure and security behavior
Clear the auth service, static identity/permission flags and trusted reauthentication ticket before showing login. Reject reentry. On failure to clear/show login, fail closed. Windows credential cancellation or launch failure leaves the old process at login; never restore the old session. Validate target credentials using the OS, reject the current Windows SID and administrator-group accounts, and start without elevation. Zero/free native credential buffers and dispose the SecureString. Remove inherited PharmaLIMS connection/password/startup overrides and set the new process's own profile paths before configuration/logger initialization. Require an installed apphost executable; do not invoke arbitrary paths supplied by the user.

Keep the old locked login available until the child has an input-ready window; if startup does not become ready, leave the locked login available and do not kill the child. Existing target-account SQL access, native mapping, certificate trust and rights remain prerequisites. No promise that an unconfigured Windows account can use the application.

## Verification and deployment
Behavioral portable tests cover busy/reentry rejection, clear-before-login ordering, clear/login failures, and environment cleanup. Cross-compile actual WPF and run the repository suites. Native Windows credential UI, profile and switch cancellation need site verification. Supply full affected files and guarded source installation; never overwrite local drift. This is a new desktop feature and may require a new desktop build, but no SQL staging or signing-service binary refresh.

Review fixes: derive SQL identity from the effective SqlConnectionStringBuilder, not display labels; mark login ready only after startup verification/monitor initialization; track pending PRM workflow opening and discard closed-session continuations. Tests cover effective override drift and visible-but-unready login.
