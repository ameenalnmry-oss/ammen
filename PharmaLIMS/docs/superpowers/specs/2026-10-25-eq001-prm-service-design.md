# AMEEN UAT PRM service boundary

Goal: correct SQL 229 A29F44E7 without granting the desktop schema/table DML or access to credentials. Use the uploaded AMEEN WPF source and XAML as compatibility evidence, preserving local numeric limits, content history, concurrency checks and all other modules.

Approach: extend the existing SID-bound, single-use fresh-reauthentication ticket protocol with PRM draft/review/approval actions. The trusted UAT host calls two fixed owner-executing SQL procedures inside one serializable transaction. A load procedure locks and returns the complete specification set; the host compares its canonical hash with the UI's immutable load snapshot. A write procedure rechecks the live account rowversion/permission and performs only the selected state transition. Audit, append-only content history and applicable signature evidence commit atomically. No arbitrary SQL RPC, desktop DML grant, credential read, production entry point or implicit activation.

Alternatives rejected: desktop INSERT/UPDATE grants violate the Windows boundary and still leave signatures/history exposed; running WPF as SQL administrator invalidates least-privilege UAT. Rewriting PRM globally would lose site-only functionality.

Scope: UAT database names starting EQ001_UAT_; host explicitly enables the endpoint only in DeploymentMode UAT. An existing active approved version must not be deactivated or rewritten by this patch. Approval that would supersede such a version fails closed pending a separately controlled disposition. The old PP-AL-001 blocker therefore cannot be claimed resolved by this patch alone. User administration/global preflight gates remain unchanged.

Evidence: actual AMEEN table columns and enabled append-only trigger definitions supplied 2026-10-25. No installation is accepted until Windows site tests and data-preservation checks pass.
