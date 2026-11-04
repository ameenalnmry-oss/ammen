# AMEEN PRM investigation concurrency — source qualification only

These complete uninstalled artifacts preserve the site source and the earlier
optional-failure coverage correction. Existing header/checklist comparisons and
attributed before/after history remain. No historical test timestamps are examined
or changed; the user confirmed those records are intentional development fixtures.

The local window formerly compared header/active checklist before Save, Submit and
Close; AddAction checked current status. Linked actions, affected results, CAPA,
root-cause, impact, retest/distribution, signatures, reconciliation and result data
could change without those comparisons noticing. The four transaction callbacks
now invoke the current GitHub full-evidence guard before their first write.
The supplied helper is byte-identical to the already-tested root guard, verified
by the test before executing corrected cases. It is intended for
Infrastructure/QualityEventInvestigationConcurrencyGuard.cs when the entire site
cohort is later qualified; no installer or deployment allowlist is changed here.

Loading clears the old token, captures versions before/after loading the persisted
view, rejects observed changes, and publishes the token after UI data binding.
Affected-result and action reads now propagate exceptions and reject null results;
a partial display cannot publish an evidence version. After own-action commit,
the old version is cleared and the saved version publishes only after a strict
action refresh and binding succeed. Failed refresh requires reloading.
AddAction captures its new version under retained locks and publishes it after
transaction success, preserving unsaved header/checklist edits.

Native RED evidence:7db8d9da4b4d041d722bd3677e01ad9d1122b72d,
https://github.com/ameenalnmry-oss/ammen/actions/runs/40964383471
The frozen comparison/first-write probes reproduced48 child-change acceptance
failures and accepted four unchanged controls. The existing current-runtime40/40,
native Windows122/122 and Water37/37 checks passed. Corrected execution is a
subsequent required CI gate; this document does not assert that gate has passed.

Test scope: real DataTable snapshot comparison, real full guard and actual extracted
first-write SQL in the existing uniquely named disposable SQL database. The full
raw header/all-answer comparison is a conservative unchanged witness for these
child-only changes; the complete old WPF callback/business validators are not run.
The source detector requires an unconditional loaded-token guard before writes and
rejects comment-only, nested, rebased and late guard mentions. Corrected52/52 probes,
two child phantom locks and own-action committed-token continuation are required.
Probe writes roll back; deliberate competing edits and the owned continuation
control commit only inside the disposable database, which is then dropped.

This does not instantiate WPF, authenticate signers, qualify all local dependencies
or prove complete approval/closure behavior. Exact whole-cohort Windows build and
local UAT remain separate. No AMEEN source/runtime, database schema/permissions,
Production, version, rollback receipt or merge is changed.

Complete window source SHA256 before this batch: `41ad393bfdf414842d7abe600948d52798d3d667f01661234cd0865f2e47e94a`; corrected: `42f172e3c53cb99c45554c2371217d6abc0cf9522a0792589e74c60854e0b004`.

Exact helper SHA256: `8ed302f1fdee7f51cf317e3753950d0401e311380d76a75ec5fb5c202053e181`.

Review follow-up: b87c3a797b94090af42997cf91aed9b3f3e4db06 passed both
Windows CI jobs in run 40966296070, including corrected 52/52 SQL probes, two
child phantom-lock checks and committed own-action continuation. Those tests did
not cover the child-read failure or unbraced conditional found in the subsequent
review. New RED tests reproduced both; corrected source passes the portable
fault-injection probes and 36 guard-wiring controls. Native CI on this final
follow-up remains the authoritative gate.

The load regression compiles the exact extracted child-loading/publication
segment, checks its bytes against the supplied full artifact at runtime, and
injects throwing/null affected-result and action readers under stable version
capture. The frozen b87c segment accepts all four incomplete reads; corrected
segment rejects all four without publishing a version and accepts successful
reads. These read adapters test propagation, not SQL loading, WPF binding or
authentication. The four-actor guard detector additionally rejects an unbraced
conditional and requires a statement boundary before the guard (28 controls).

The independent reviewer supplied two findings, addressed here; its session then
ended before a complete final review. This is not a clean full-cohort review signoff.
