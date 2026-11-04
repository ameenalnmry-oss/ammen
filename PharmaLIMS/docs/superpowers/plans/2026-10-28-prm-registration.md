# PRM registration implementation plan

1. Read actual AMEEN save/numbering and approved-test assignment plus Water registration/recovery reference (done).
2. Add failing portable and source-boundary tests for typed PRM registration validation and no direct restricted-mode writes.
3. Add typed request/result and policy, trusted host store, immutable receipt SQL procedure and local recovery.
4. Add shared UI partial and guarded async new-save routes in base and AMEEN overlay, preserving trusted specification controls.
5. Wire shared endpoint/settings/session and build manifest through parent integration; do not edit those shared files here.
6. Verify portable tests, disposable SQL replay/rollback/concurrency and restricted execution; document native Windows/UAT prerequisites and remaining checks.
