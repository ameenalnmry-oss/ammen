# PharmaLIMS 2026.10.19.306 — source remediation candidate

F01–F07 from the 17 October source audit are merged onto the newer source review supplied on 19 October.
The 12 newer source files are preserved byte-for-byte, including the five-table print snapshot with linked samples, multi-sample equipment linking, optional PRM equipment documentation, CIPLA TBC/TFC numeric rules and development certificate marker.
The prior v305 installer aborted its full preflight before writing any source; this package uses the newer baseline hashes.
This is not a Windows build, UAT acceptance, production release, or SQL permission cutover.

- New additive `20261017_001` migration rejects untrusted SQL signing origins, checks current signer eligibility, wraps signature/status/audit in one transaction, protects signed activity/link content and verifies historical stage hashes before printing. It does not change the shared-role EXECUTE permission or rewrite historical rows/hashes.
- The signing service reads a readiness-only view. Provisioning verifies the dedicated Windows identity SID and grants the narrow view/boundary operations without password hashes or salts.
- An explicit isolated UAT rehearsal mode can run with permanent origin/integrity guards before the final permission DENY. Normal operation still requires DENY. The final cutover requires recorded administrative acceptance of real functional/security/regression UAT and does not replace integrity guards.
- Water permits only pending-result completion during/after investigation. Saved completed evidence stays frozen. Submission/review/approval reject unsaved or concurrently changed visible evidence.
- Water reports share frozen numeric range rules for pH/chlorine; no report layout changes.
- Candidate assembly, manifest and inherited dependency inventory identity advanced together. Dependencies were not changed; the inherited SBOM is not a newly resolved restore result.

Required next gates on Windows: WPF and signer builds, portable C# regressions, disposable SQL integration, Windows-authenticated positive review/approval, negative SQL-role and content-integrity tests, two-session Water tests, report acceptance. Record actual UAT evidence before executing the separate cutover. A source-only Git-history skip does not verify the old commit.

Installer/source merge revision R3 incorporates the actual three-file conflict review supplied at 18:04 on 19 October. It preserves the additional CIPLA source-contract test byte-for-byte and the existing mapped Admin/Administrator stage permission at both service authorization checks and the SQL stage check. Account readiness, Windows mapping/groups, non-sysadmin restrictions, trusted SQL origin, segregation of duties, historical content hashes and post-UAT cutover gates remain required. The service continues using the readiness-only view, now including the application role. The actual security-contract assertions are combined with the stronger readiness/origin assertions; no raw credential grant is restored. This candidate migration has not been executed here.
