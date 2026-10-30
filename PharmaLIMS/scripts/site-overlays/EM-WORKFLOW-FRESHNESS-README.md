# AMEEN EM displayed-evidence freshness — uninstalled source artifacts

The uploaded site Submit handler already checked loaded and visible evidence in
its transaction. Review and Approval checked status, permissions and independent
actors; Approval also validated current scientific consistency. They did not check
whether those current results were the evidence the signer had loaded.

The complete backend artifact now requires an expected DataTable for Review and
Approval and compares it against the complete locked snapshot before writes.
Both WPF handlers commit pending grid edits, copy the loaded snapshot, reject
unsaved count/remark/equipment changes, capture event identity/user, and pass the
copy created before reauthentication. No null/default bypass parameter is added.
Existing permissions, actor separation, source-plan handling, Quality Event gate,
current-result consistency, transaction signatures/audits, equipment histories,
grouped personnel labels and post-commit refresh remain.

The freshness correction supplies three complete site files:
DatabaseHelper.EnvironmentalMonitoring.cs, EMResultsEntry.xaml.cs and
EMResultsEntry.xaml.Part3.cs. DatabaseHelper.EmSourcePlan.cs now additionally
contains the separate correction described in EM-MEMBERSHIP-README.md; its snapshot
SELECT/reader remains unchanged. EmReportEvidenceGuard.cs,
EmQualityEventEvidenceGuard.cs and EmResultCalculator.cs remain unchanged site
dependencies. These form a preserved cohort for later qualification; do not replace
unrelated current GitHub files with them. No deployment allowlist is expanded.

Tests compile exact frozen/corrected Review and Approval methods and the exact site
snapshot SELECT/reader. Runtime comparisons reject drift between compiled excerpts
and full artifacts. Explicit test adapters supply authentication, permission,
source-plan, status and audit boundaries. These are not tests of signer identity,
source-plan completion or the real audit implementation. Probe signatures and
controlled transitions execute actual extracted backend SQL, then roll back in the
existing uniquely named disposable EM fixture. Competing fixture mutations commit
only there; the existing harness drops its database in finally. It never connects
to AMEEN or the original UAT database.

Portable RED: the original methods accepted22 changed-evidence cases and missing
loaded evidence. Corrected methods reject22/22 before writes, accept unchanged
controls, and reject missing snapshots. The exact capture helper rejects8 invalid
or unsaved states and retains an independent snapshot. Native SQL evidence is a
required subsequent CI gate; this document does not claim it already passed.
The native matrix uses real multi-plate snapshot SELECTs and method SQL, checks
rollback and four competing child locks, and validates compiled/artifact identity.
The linked result calculator is the complete actual site artifact, preserving its
additional manual/direct-count equipment semantics. The limit SQL matches both
site and current GitHub after LF/BOM normalization. No root calculator is substituted.

No AMEEN source/runtime installation, SQL schema/permission change, Production,
historical record change, rollback-receipt relabel, source qualification bypass or
merge. Whole site build/WPF UAT remains incomplete. EM personnel-plan membership
and Culture complete child-evidence concurrency remain separate pending scopes.
Independent whole-cohort review remains incomplete after the earlier reviewer
session ended; this batch does not assert review signoff.

`DatabaseHelper.EnvironmentalMonitoring.cs` original SHA256 `aa537e6d8e5b4fca0dd7cebac264dd901db655072a035ee9fe83a6c8ced69a52`, corrected `60d060b85c11d2dc5e158d478b1ef20ff21edfd31ad2359b53ccc1d6df45cfc8`.

`EMResultsEntry.xaml.cs` original SHA256 `866672e7be6e5ffdc0ba4dfa0b68a4627e35094a1905ee4cd81afbdb19f84882`, corrected `4065c7543f2e0017126fd7341d78f7b8d374ab731def50daeea55f34d1c2cb06`.

`EMResultsEntry.xaml.Part3.cs` original SHA256 `9600f07af2fb98aede325c4eebb8aa345d59a1f5213d2fabe212e2386f5c91c5`, corrected `0efdf677f8af5785427d631953fd153bb8f87b2f868ce38277c662778b81e765`.
