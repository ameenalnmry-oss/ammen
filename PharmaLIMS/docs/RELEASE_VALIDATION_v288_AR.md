# PharmaLIMS 2026.9.17.288 — Release Validation

## الهدف
إغلاق عطل User Management في قواعد البيانات القديمة التي تسمح بتسجيل الدخول وتحميل المستخدمين ولكن تفشل عند عمليات الكتابة بسبب غياب `dbo.Users.CreatedAt` أو `dbo.Users.UpdatedAt`.

## بوابات التحقق المطلوبة

- Python/unit/static tests: PASS required.
- Migration manifest hash validation: PASS required.
- Historical migrations 20260911_000 and 20260915_000 bytes unchanged.
- Development: migration 20260917_000 may apply automatically before Login.
- Production: startup remains verify-only; controlled deployment must apply migration first.
- SQL Server integration on the validation workstation must exercise: Create User, Update Permissions, Reset Password, Unlock, forced Change Password, reload, and AuditTrail evidence.
- Clean Release Build, RuntimeSmoke, ProductionArtifactSmoke, Authenticode, System Preflight 0 BLOCKER and UAT remain mandatory before production approval.
