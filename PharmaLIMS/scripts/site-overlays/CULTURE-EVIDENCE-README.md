# AMEEN Culture preparation evidence — complete uninstalled site artifacts

The site's byte-rowversion guard notices parent changes/signed header columns but
cannot compare child SOP/signature changes. Portable tests execute that exact
guard over explicit DataSets: it accepts6 child-only changes. The complete
reference snapshot rejects6/6 and12/12 review/release comparisons. These portable
tests are not SQL or WPF execution. The new native fixture runs the actual site's
parent lock SELECT/byte guard against real child mutations, then the complete
supplied helper. Every case rolls back in the existing owned MediaAmendment DB.

Four complete source files form this correction: CultureMediaPreparation.xaml.cs,
its Part2/Part3 and the necessary MediaPreparationAmendmentSnapshot helper. The
original CultureMediaPreparationGuard is supplied unchanged as a test dependency
and remains a redundant parent-version/signed-column gate in the site code.

The helper starts from the exact current reference snapshot and adds typed visible
input comparisons, signing scope/mode checks and visual/release stage controls.
Snapshot Read/AuditValue/EnsureCanAmend/EnsureUnchanged retain the complete parent,
all SOP fields and all preparation signatures. Six changed child cases,32 changed
or missing visible fields and14 modal-scope/input/mode states have portable
coverage; precise API execution is distinguished from WPF authentication.

Selection now reads header/SOP together under retained locks, binds from that same
snapshot and invalidates all usable evidence on failure. Explicit New recovers
from failed loading. A loaded released/rejected record cannot silently become a
new preparation when Save is clicked; existing UI instructions already require
New Preparation. The initial blank form and explicit New remain supported.

All three signing actions capture target ID, displayed number, loaded evidence
and typed form inputs before modal reauthentication. Afterward they reject target,
grid/form, scientific input or SOP changes. Review/release also require displayed
scientific values to match saved evidence; unsigned visual-entry A7 inputs remain
editable. Inside the original authorized transaction, complete current evidence
is compared before writes. Existing actor independence, parent-rowversion SQL,
immutable source lot, stock delta, authoritative expiry/source-lot/visual gates,
signatures and history remain. Full before/after scientific/SOP/signature JSON is
retained in all three audits. Own post-write snapshots publish only after commit;
post-commit refresh failures require reload and do not claim rollback.

Tests compile exact original/corrected selection/SOP/New handlers with control
adapters and real SQL snapshot reads, and compare actual compiled source bodies
against full artifacts. They reproduce a stale-grid/fresh-SOP mixed display,
verify coherent corrected loading, inject binding failure, test explicit-New
recovery and lock released-record creation mode. These control adapters do not
instantiate WPF. Actual unchanged release-gate SQL is compiled/tested for seven
expiry/source/visual/status cases, and a synthetic committed visual transition
checks old-token rejection, continuation, duplicate review and signed freeze.
Role/password/modal WPF execution and real site permissions remain unverified.

Native SQL and full cohort build are required subsequent gates; no local PASS or
deployment acceptance is asserted. No original AMEEN files, Production, permissions,
schema, source qualification allowlists, rollback receipts, versions or historical
records are changed. These full files are review inputs for later exact-cohort
qualification, not an instruction to overwrite the original site project.

`CultureMediaPreparation.xaml.cs` corrected SHA256 `baa95606eda6f2ed922377d947d152bdd7dee39bda18a2fac991833c282bf95b`.

`CultureMediaPreparation.xaml.Part2.cs` corrected SHA256 `fac667ad722d5cbd98b4cea2a374e1677fd94aa0e2a034e63b9f7f2ebcf15dde`.

`CultureMediaPreparation.xaml.Part3.cs` corrected SHA256 `f52b86e8290ce1aa385767b5376fc06ed6e8d7faa1f794c2f97133e50ba809b6`.

`Services/MediaPreparationAmendmentSnapshot.cs` corrected SHA256 `f7cc9ae8e4efc88c5481b4252355f6ad0ff839a4f82f240270515a9617ddbcc7`.
