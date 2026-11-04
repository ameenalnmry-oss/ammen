# EQ-001 Final Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Complete and demonstrate a trusted Windows service-to-WPF equipment signature flow on PR #62, without production activation or merge.

**Architecture:** Kernel-derived Windows pipe caller SID is mapped by protected host configuration to a PharmaLIMS account. TLS authenticates the service before a fresh password is transmitted; the existing SQL repository/coordinator/store retain independent authorization and atomic evidence persistence.

**Tech Stack:** .NET 8, Windows named pipes and TLS, WPF, Microsoft.Data.SqlClient 6.1.6, isolated SQL Server UAT, GitHub Actions Windows runner.

**Spec:** ../specs/2026-10-23-eq001-final-integration-design.md

## Global Constraints

- Same repository and PR #62; no duplicate checkout/project copy.
- No production data changes, production grants, historical evidence edits or merge.
- Do not repeat accepted tests without a changed dependency or covered path.
- No credential or private certificate material in Git/artifacts.

## Review Focus

- Forged payload username or untrusted/same-account Windows identity must not authorize signing.
- Untrusted server certificate or pipe squatting must not receive a password.
- Account permission/lockout changes must be checked by the service and SQL at commit.
- Cancellation/network/service failure must never fall back to local SQL signing.
- CI passing unit tests must not be represented as actual Windows/WPF/SQL UAT.

### Task 1: Preserve accepted SQL evidence

- [x] Save accepted report and execution log under docs/validation/EQ001; regenerate source manifest and push same PR.
- [x] Preserve distinction between synthetic test caller and real Windows transport.

### Task 2: Implement authenticated Windows transport and runnable host

Files: tools/PharmaLIMS.TrustedSigning/{Windows*,TrustedPipe*,TrustedHostSettings.cs}; tools/PharmaLIMS.TrustedSigning.Host/*; tools/PharmaLIMS.TrustedSigning.SecurityChecks/Program.cs.

Interfaces: `TrustedPipeClient.CommitAsync(EquipmentSigningRequest,string expectedUsername,string password,CancellationToken)` and `WindowsTrustedPipeServer.RunAsync(CancellationToken)`; configured certificate pin, caller-SID map and trusted SQL credentials.

- [x] Add failing transport/configuration security checks; run and observe failure.
- [x] Implement bounded protocol, authenticated server/client pipe boundary, protected configuration and host startup; preserve existing SQL interfaces.
- [x] Compile and run focused checks; commit and push same PR.

### Task 3: Integrate actual WPF equipment signing

Files: LabResources.xaml.cs; TrustedEquipmentSignature.xaml/.cs; Services/TrustedEquipmentSigningClient.cs; PharmaLIMS.csproj; tests/test_eq001_production_signing_guard.py.

- [x] Replace outdated guard-only tests with failing assertions for exclusive service routing and no local SQL write fallback.
- [x] Implement dialog and client configuration, async save and request serialization. Preserve unrelated login/review/signature behavior.
- [x] Cross-build, run impacted tests and SecurityChecks; regenerate source manifest and commit.

### Task 4: Windows/WPF/SQL UAT and regression

Files: tests/PharmaLIMS.TrustedSigning.WindowsUat/*; scripts/Invoke-Eq001WindowsUat.ps1; .github/workflows/ci.yml; docs/validation/EQ001/*.

- [x] Add isolated Windows integration runner that exercises the actual service, Windows caller SID and WPF controls against a disposable UAT database; no fake production identity implementation.
- [ ] Verify success and denial scenarios plus historical evidence unchanged. Provision no production permissions.
- [ ] Execute through Windows CI, inspect results and repair failures. Run impacted regression/build checks and save artifacts/results.
- [ ] Fresh whole-branch review; address material findings, report exact remaining blockers. Do not merge or activate production.

## Material review correction

The fresh review found that allowing desktop credential/counter writes could defeat independent reauthentication. Extend the same service boundary to authentication and signed user administration, deny desktop credential reads/writes and all Users mutations, and verify legitimate password/login/admin flows. Tighten negative tests to exact authorization/TLS/conflict failures with valid control payloads. Exercise the actual service-account caller and SCM lifecycle, including invalid PFX and occupied-pipe startup failures. These material findings require new Windows execution; the earlier provisioning failure is not UAT acceptance.
