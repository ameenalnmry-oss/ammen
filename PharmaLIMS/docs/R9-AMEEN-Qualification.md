# R9 / AMEEN source qualification

This branch is a qualification candidate. It does not authorize installation,
database migration, certificate changes, Production activation or GMP release.
PR #62 remains separate, Draft and unmerged.

The R9 source archive SHA256 is
`1461a2c8db8447236ed166117bfa27dea7fbca99cc3488edd93bcb8ffa5fcd9c`.
The AMEEN source snapshot SHA256 is
`6615f08ea6f8582b19a02d3338659eea96e94da7b25668d2b14defbf81877175`.
The qualification branch starts at PR head
`f6b5cc2af3794727cc02233ad2ce171d95ce7f7c` and preserves its test infrastructure,
frozen 306 site cohort and source-update safeguards.

The active source baseline is v309, with reviewed AMEEN source corrections and
local equipment recovery files. R9 certificate-store configuration, separate
activity-signing connection, canonical reauthentication, activity snapshots and
diagnostics remain present. Equipment Draft/Link/Confirm flags remain false by
default, and their SQL prototypes remain on SECURITY HOLD.

The existing PRM 20261028 migration source is retained with its original checksum,
alongside the R9 manifest entries. There are 107 controlled SQL inputs including
the baseline. No site SQL migration has been executed by this qualification work.
Site schema/ledger compatibility must be reviewed before any runtime refresh.

Local qualification has compiled WPF and the host to Windows targets with .NET 8,
with zero warnings/errors. Portable component and source tests are separate from
actual Windows pipe ownership, TLS, service identities, WPF and SQL execution.
The five package lockfiles are generated from the merged project graphs.

Independent review identified two important preservation gaps: EM investigation
evidence lifecycle calls and PRM append-only readiness checks. The first test
commit deliberately retains those production gaps while adding actual SQL
regressions and frozen permissive witnesses. It must fail on the hosted Windows
runner before the fixes are accepted. Neither this test commit nor a successful
source build is a deployable release.

The dedicated SQL matrix uses only runner LocalDB databases with generated test
names. It never targets the retained AMEEN UAT databases or Production. The schema
tests roll back trigger mutations and drop only their owned disposable database.
The EM tests compile the actual active approval gate and source-plan methods,
rather than substituting an empty investigation gate.

After those native regressions prove the fixes, complete the existing Windows
build/security/WPF/SQL gates, review the final diff and prepare a guarded original
project update with new rollback evidence. Preserve the original 97b34729 Water
receipt and all subsequent source/runtime records. Actual site signing, PRM
independent review/approval/audit, Runtime Preflight, Water/EM/PRM regression and
QA acceptance remain separate executed-evidence requirements.
