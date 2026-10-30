# AMEEN EM plan/personnel membership — uninstalled source correction

The site's source-plan guard formerly validated the parent plan/event but never
checked result plates against their assigned plan samples. Native RED at
d516342913548ddb54da066fd8a9a990a5ae5cc8 (CI41060276008) reproduced23 unsafe
acceptances using the exact original guard and real SQL. Nine valid histories and
six existing plan/cancel gates remained accepted/rejected as intended.

The complete DatabaseHelper.EmSourcePlan.cs artifact now closes its parent reader
and validates the locked plate/sample set before returning. It checks plan, event,
area, method, non-negative-control status, released sample state and personnel
identity. Employee IDs follow .NET ordinal case-insensitive trimmed comparison;
names use exact trimmed comparison. No loose SQL collation is used for identities.
Historical aggregate events may have both event employee fields null/ASCII-blank,
matching the existing per-plate SQL label fallback; each linked sample must still
have complete identity. Partial or contradictory identities fail closed.
The existing Development standalone/Production native-plan policy remains.

Every assigned non-control sample must also have a result plate, and a planned
event cannot contain zero result plates. Multiple plates per sample remain valid;
no unverified one-to-one requirement is introduced. The final matrix adds these
two completeness cases to the already-reproduced23, running the unchanged old
guard first and then the corrected guard. It requires25 corrected rejections,
nine valid histories, six existing gates and four retained row/phantom locks.

The tests compile the actual guard/plan-lock bodies, verify them against fixture
and complete-artifact source, and invoke them on real SqlConnections with real
transaction-owned sp_getapplock and retained read locks. There are no auth/plan
adapters in these membership probes. The only configuration adapter supplies a
15-second command timeout. Per-case fixture mutations roll back; JSON fingerprints
assert the guards are read-only. Setup runs only in the existing GUID-named owned
EM database and the existing finally drops it. Earlier fixture setup compilation
failed because ALTER and new-column use shared a batch; that test-only failure was
corrected before any RED acceptance result was recorded.

Native execution of the corrected artifact is a required subsequent gate; this
document does not claim that gate has already passed. The source guard is called
by the site's save, submit, review, approval and print paths. This unit/native SQL
evidence does not establish authenticated local WPF execution, actual caller SQL
permissions or complete investigation closure/physical sampling traceability.

Only this complete source-plan helper is corrected in this sub-batch. It preserves
the previous snapshot SELECT, grouped personnel labels, equipment/history fields,
Add-based multi-plate reader and cancellation method byte-for-byte. The previous
EM review/approval freshness correction remains in its three full site artifacts.

No AMEEN installation, original project replacement/copy, Production change,
permission/schema change, historical data rewrite, source qualification expansion,
receipt/version relabel or PR merge. Culture complete child-evidence concurrency,
whole cohort build/rollback provenance, independent review and local UAT remain.
