# Session Switch Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline; user delegated implementation.

**Goal:** Lock PharmaLIMS and switch native Windows actors without forced shutdown or shell commands.
**Architecture:** A small session transition coordinator clears authentication before a new login is shown. Native credential UI launches the existing apphost under a standard target identity; startup normalizes that actor's profile before configuration is read.
**Tech Stack:** .NET 8 WPF, Windows CredUI/LogonUser, ProcessStartInfo, existing DI/auth.
**Spec:** ../specs/2026-10-27-session-switch-design.md

## Global Constraints
No SQL, grants, service settings, certificate trust or Production activation. Preserve local custom source and fail on drift. No secret in logs/arguments. No handoff with pending workflow work.

## Review Focus
Busy and reentrant transitions preserve active work; cancellation never restores old authority; launch failure keeps login; target profile does not inherit prior user's secrets; old main-window timers stop on close.

### Task 1: Session transition and profile policy
- [x] Add behavioral tests in tests/PharmaLIMS.SessionRegression for transition order, busy, reentry, failure and environment cleanup; run RED.
- [x] Add Services/SessionTransitionCoordinator.cs and Services/SwitchedProcessEnvironment.cs; run GREEN.

### Task 2: WPF and Windows handoff
- [x] Add main-window lock/switch actions and a fresh-login transition in App; stop old clock on close. Preserve timeout callers.
- [x] Add native WindowsUserSwitchService and login switch action with cancellation/failure recovery, no saved credential, no elevation and target profile bootstrap before AppConfig.
- [x] Build actual WPF source and inspect XAML linkage. Add focused native/manual acceptance steps and complete replacement files.

### Task 3: Qualification and handoff
- [x] Run portable and repository regression suites plus source validation; inspect final diff and risk boundaries.
- [x] Prepare guarded installation against known source versions; local drift is a hard stop. Update source manifest.
- [x] Report what passed and what still requires Windows/site execution; do not claim deployed.

Local verification: 10 behavioral checks; 639 Python checks; 296 ReviewRegression checks; WPF cross-build 0 warnings/errors; installer helper checks PASS (native Windows locks skipped here). Actual Windows credential UI and site deployment remain pending. The source-install prerequisite check, rather than an overwrite, decides whether original MainWindow/Login files match the known revision.
