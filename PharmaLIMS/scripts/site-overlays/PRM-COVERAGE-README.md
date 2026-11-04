# AMEEN PRM investigation coverage — uninstalled source correction

These complete source artifacts retain the uploaded local cohort. They are not
compiled or installed by the application, deployment scripts or source installer.
The exact mixed site cohort still requires separate qualification.

The source ZIP SHA256 is
`b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a`.
The frozen query fixture records the original raw source hashes and exact extracted
SQL hashes. Complete artifacts use UTF-8 without BOM and LF line endings.

Exactly one predicate changes in each source file. Both investigation coverage
queries include an optional test once it has a nonblank result, retaining the
existing inclusion of required tests (including NULL RequiredTest defaults).
Unperformed optional tests remain excluded. Snapshot matching, legacy evidence,
authorization, signatures and all other local changes are retained.

The test executes actual SQL extracted from these full source files in the existing
uniquely named disposable PRM SQL fixture; every case rolls back its transaction.
It rejects SQL/build/parser errors as defect evidence. Frozen source must reproduce
exactly five acceptance failures across22 checks; corrected source must pass all22.
The current GitHub runtime's independent PRM regressions remain unchanged.

Native RED evidence at b78c5fc527bfbe3629b7627a16672c47be706fa3:
https://github.com/ameenalnmry-oss/ammen/actions/runs/40952928951
Frozen SQL passed17/22 and reproduced five expected failures. Corrected execution
is a required subsequent CI gate; this document does not claim it has passed.

The changed-snapshot case tests the coverage query specifically. The complete
closure method also contains other snapshot checks; this result alone does not
establish that stale evidence bypasses the entire closure workflow.

No schema, SQL permission, deployment allowlist, configuration, rollback receipt,
application version, AMEEN runtime or Production change is included. Other reported
site concurrency gaps and actual independent-user WPF UAT remain unresolved.

Normalized complete source SHA256 values:

- ProductionRawMaterialResults.xaml.cs: original `0294a46fb84cf6bea44beb186a32f4eaf7be636933857046ae4c47cd996e2421`; corrected `4cdd114b4bca2accf5e073437c5ea586b263d5262e7b6f19134063dbfd0611ee`.

- PRMQualityEventInvestigation.xaml.cs: original `366b49c092fd4340d5dc07f3a09b8971bfa6fee4d93ca5287b464d62ebc335b5`; corrected `41ad393bfdf414842d7abe600948d52798d3d667f01661234cd0865f2e47e94a`.
