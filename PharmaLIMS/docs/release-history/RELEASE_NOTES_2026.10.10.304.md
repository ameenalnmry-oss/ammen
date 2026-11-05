# PharmaLIMS 2026.10.10.304 — review remediation candidate

This candidate addresses F01–F21 from `MEDICA_PharmaLIMS_Latest_Code_Review_2026-10-09.pdf`, reviewed against `main@c70f6107ed82bfee456a41768518991445ad6cd8`.

- Water writes now record the actual result signer/database timestamp, protect concurrent equipment/resource/LES edits, evaluate approved instrument/kit/no-instrument policies consistently, and validate exact LES temperature and server-date kit expiry.
- PRM specification signatures bind complete, version-specific content history and SHA-256. Review excludes every draft author/editor. Numeric limits must fit SQL storage without rounding. Investigation saves compare raw headers/checklists, preserve unchanged answer attribution, record changed evidence and require both cancellation/issuance permissions for reissue.
- EM events honor their source plan's cancellation/release state. Plan cancellation includes linked events and signatures, refuses approved evidence, and shares a transaction plan lock. Handoff retains recorded personnel identity. Reports use frozen area context and reject inconsistent stored plate/event interpretations or unsaved visible results/equipment.
- Culture receipt/preparation transactions refresh authorization. Preparation amendments reject stale versions and signed visual/sterility evidence. Referenced media/master and lot identity are protected while stock updates remain possible. GPT start binds its actual performer.
- Numeric PRM growth-condition context stays on the numeric evaluation path.

The additive migration `20261010_001` adds specification content history, water resource execution date and native preparation rowversion, protects referenced culture identity, and expands existing investigation-history table coverage. Historical migration files/checksums are unchanged. Historical execution dates and result calculation versions are not backfilled.

Validation details and remaining Windows/SQL/WPF acceptance are recorded in `docs/REVIEW_CLOSURE_v304.md`. This is a review candidate, not evidence that MEDICA's production installation has been upgraded or qualified.
