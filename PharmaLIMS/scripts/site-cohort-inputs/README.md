# Exact AMEEN site-cohort compile evidence

This is a compile-only review input. It is not a deployment/source-installation
allowlist, acceptance receipt or instruction to replace the original project.

The immutable source archive SHA256 is
`b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a`.
The plan preserves its 220 declared build inputs and11 additional compile files,
applies the separately tested complete site artifacts, and adds the two necessary
complete evidence helpers. Version `2026.10.19.306` remains. Database definitions,
the site migration manifest and all102 controlled SQL checksums remain exact.
Nine unmatched database inputs are inert base64 .txt files so BOM/newlines can
round-trip without changing migration checksums. No SQL is executed by this gate.

The existing hosted Windows validation job finishes general checks first. It
restores only the two lockfiles changed by that job's restore, rechecks the
controlled source manifest and clears only six known build-output directories.
The complete site input set is validated and buffered before its first write.
Pinned files come from immutable Git objects; .txt artifacts have expected SHA256.
Extra application/shared/host C#/XAML inputs in that same ephemeral checkout are
excluded so current root helpers cannot silently fill a missing site dependency.
No new project checkout or original AMEEN project copy is created.

After locked restore, evaluated MSBuild Compile/Page/ApplicationDefinition lists
must exactly match the plan for the WPF project, shared signing contract and
signer host. Release builds use those exact inputs. Final checks retain source
hashes, all102 SQL checksums and actual WPF file version306. The uploaded JSON
contains source graphs, output hashes and explicit false local/deployment flags;
it is not a runnable deployment package.

Preparation rejects non-hosted/non-Windows execution, original AMEEN, a different
checkout, source drift, path escape, Windows path aliases, missing immutable Git
objects and an incomplete plan. Failure is a blocked compile gate; no permissive
fallback, host configuration read, SQL permission change, signing service action,
source-rollback receipt rewrite, Production activation or merge is included.

Native Culture CI41088593600 has already passed the complete-helper child SQL,
SQL-backed load/control adapters, seven retained release gates and own visual
continuation. Its controls do not establish actual local WPF authentication.
Even a successful full-cohort compile leaves independent review, original source
rollback provenance, authenticated site WPF and module/printing/QA acceptance open.


First exact-cohort CI41092443803 completed source buffering and both locked
restores, but its strict graph comparison stopped before compilation: the shared
project explicitly links Services/ControlledNumericValue.cs and
Services/WaterNumericResultEvaluator.cs. Both already belong to the reviewed site
inputs. BuildGraphs now records those exact includes from the pinned project;
all other unexpected sources remain rejected. This is a verifier correction,
not an application-dependency addition or a successful compile result.

Exact uploaded SystemPreflight.xaml.cs also retains current-calendar timestamp
interpolation. Its complete uninstalled artifact changes only the display and
copied-report timestamp expressions to invariant formatting. Actual report method
and label expression tests use original DTOs, four date boundaries, explicit
UmAlQura/ThaiBuddhist/Gregorian calendars and unchanged target/count/evidence
checks. Frozen source is wrong16/24; corrected source wrong0/24. The label control
is an adapter, not WPF. Explicit calendars are required because this Linux ICU
defaults ar-SA to Gregorian while AMEEN's supplied report uses the Hijri calendar.
No timestamp instant, machine clock, database row or earlier report is changed.
Native execution and full-cohort compilation remain required subsequent gates.
