# PharmaLIMS 2026.9.17.290 — Release Validation

## الهدف
إغلاق ملاحظات المراجعة العميقة على v289 بدون تعديل منطق Water أو EM أو PRM وبدون تغيير migration تاريخية سبق اعتماد checksum لها.

## ما تم إغلاقه
- منع User Update بدون تغيير فعلي قبل تنفيذ SQL UPDATE أو AuditTrail أو Electronic Signature evidence.
- تحويل فحص `dbo.UserAdministrationSignatures` من مجرد وجود الأعمدة إلى عقد Schema دقيق Fail-Closed يشمل الأنواع والأطوال وNullability وIdentity وPK وFK والـCheck constraints والـDefaults والـIndex والـAppend-only trigger.
- تطبيق نفس العقد في Startup postcondition وFull System Preflight.
- إضافة DatabaseIntegration gate مخصص للعقد الجديد.
- الإبقاء على `20260917_001_User_Administration_Signature_Evidence.sql` بدون أي تغيير في البايتات أو SHA-256.

## بوابات التحقق المطلوبة قبل Production
- Python/unit/static tests: PASS.
- Source manifest and delivery-package validation: PASS.
- Windows Clean Release Build.
- SQL Server DatabaseIntegration.
- RuntimeSmoke + ProductionArtifactSmoke.
- Full System Preflight = 0 BLOCKER.
- User Management end-to-end UAT، بما في ذلك Create/Update/Reset/Unlock/Audit/Signature evidence.
- Authenticode verification وQA approval.
