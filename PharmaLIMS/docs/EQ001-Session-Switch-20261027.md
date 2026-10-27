# EQ001 desktop lock / Windows user switch

New desktop feature; not yet accepted on AMEEN. Does not change SQL, service runtime, native SID mappings, certificate trust or Production activation. Existing equipment evidence 42/43 and PRM supersession remain untouched.

## Behavior
- Sidebar footer `Lock Session`: close/save workflow windows first, finish system checks, then return to a fresh PharmaLIMS login. Clears auth-service user, static identity/permissions and trusted reauthentication ticket.
- `Switch User`: same logout, then standard Windows credentials prompt. Use a **local standard Windows account** on the workstation, such as AMEEN\LimsReview, AMEEN\LimsUatAdmin or AMEEN\LimsUatQA. Windows administrator identities and the current Windows identity are rejected. Enter the Windows password here; the new process still requires a PharmaLIMS login.
- Login also has `Switch Windows User...`, and displays the current Windows identity. Cancelling/failing a switch leaves the old application signed out. No credential is saved, printed or passed on the command line.
- New process loads its own profile before configuration/logging. It drops inherited PHARMALIMS connection/password/startup overrides and refuses a SQL target change before opening a connection. Both targets are parsed from the effective connection strings, including any overrides.
- The old logged-out window closes only when the new application's login window has completed startup verification and is ready for sign-in. After 30 seconds without that window, the old login remains available; inspect the new window before retrying. No child process is killed.
- Each target account must already have the required workstation/SQL access, native service mapping and trusted public certificate. The feature does not provision accounts or bypass missing prerequisites.

## Qualification
Portable behavioral tests: busy/reentry, clear-before-login, clear/open failures, target profile paths, override cleanup and SQL target continuity. Actual WPF cross-build and full existing regression suites are run before handoff. Windows credential UI cancellation, target-profile launch and visible lock remain site UAT items; Linux compilation cannot establish those.

## Original project installation
Use the immutable reviewed commit's `scripts/Install-Eq001AmeenSource.ps1 -SourceCommit <exact> -DesktopSessionOnly` for a read-only prerequisite check. `-Apply` writes only the eight desktop source files and saves durable original bytes first; it requires closing desktop instances but does **not** require stopping the signing service. Never apply on local source drift. Site App overlay preserves its verify-only Development startup; baseline release/dev policy is unchanged in the generic repository App. Full MainWindow overlays preserve the supplied local controlled COA cancellation navigation, permission checks and signed workflow; it stays hidden outside Development. Prerequisite hashes were reviewed against the 2026-10-27 user-supplied MainWindow pair, not substituted blindly.

A new **desktop** Release build is required for this feature. Back up original desktop output and exact appsettings bytes before building; restore and byte-verify settings after build. Do not rebuild or refresh service binaries, re-stage SQL, rerun grants or change certificates. Keep a rollback copy of old desktop runtime files before the build. Do not consider the feature installed until the actual guarded source check, build and Windows tests succeed.

## Site tests
1. With open Lab Resources, a manual lock/switch must ask to save/discard and close workflow windows; no data is discarded automatically.
2. Close workflows, `Lock Session`: old dashboard closes, login has empty password, no old permissions survive. Fresh sign-in succeeds normally.
3. `Switch User`, cancel Windows credentials: login remains and old session does not return.
4. Supply a different configured standard Windows actor: new login displays that actor, uses its own profile/log folder and same UAT database. Authenticate with its corresponding PharmaLIMS account; verify role permissions and native equipment signing boundary.
5. Wrong Windows credentials show a Windows-specific message and keep login; no SQL credential attempts occur from this OS rejection.
6. An administrator/current identity is refused without changing the host mapping. Missing target trust/access fails without local fallback.
7. Confirm the old desktop process exits after a ready new login, and that ordinary idle timeout still returns to login.

The full site App/MainWindow overlays are compiled by `scripts/Test-Eq001SessionSiteBuild.ps1` into a temporary artifact directory, both locally with Windows targeting and in the native Windows UAT job. This is compile evidence, not a real certificate cancellation test or permission to change historical data.
