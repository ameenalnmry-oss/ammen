# PharmaLIMS 2026.9.16.281 — تقرير إغلاق الفجوات

## النتيجة
تمت معالجة العيوب البرمجية المؤكدة من مراجعة v280، مع الحفاظ على الضوابط الموجودة وعدم إجراء إعادة هيكلة واسعة غير لازمة في إصدار hardening.

## الفجوات المؤكدة التي أُغلقت
1. **Production startup DDL**: أصبح startup في Production تحققًا فقط عبر `VerifyControlledMigrationAsync`. لا يتم تنفيذ migration تلقائيًا.
2. **صلاحية Database Maintenance**: المسار التفاعلي يستدعي `ApplyRequiredUpdatesAsUserAsync` ويعيد التحقق من `CanManageSettings` بعد حيازة قفل الصيانة. `UserAdministrationService` يأخذ Shared app-lock لمنع تغيير صلاحيات المستخدم بالتزامن مع الصيانة.
3. **UserRepository bypass**: `AddAsync/UpdateAsync/DeleteAsync` المباشرة أصبحت `[Obsolete(..., error: true)]` وترمي `NotSupportedException`. جميع تعديلات الحسابات تمر عبر `UserAdministrationService`.
4. **Migration package integrity**: System Preflight يحسب SHA-256 لملف SQL المثبت نفسه ويقارنه بالـ manifest، ثم يقارن ledger.
5. **حماية سر SQL Authentication**: دعم DPAPI CurrentUser عبر `DpapiProtectedPassword`، ومنع plaintext password/SQL-auth connection-string override في Production.
6. **Partial-class governance**: أضيف `scripts/check_partial_classes.py` لمنع الملفات اليتيمة أو عدم تطابق `partial class`.
7. **ازدواجية test runners**: أضيف `scripts/run_release_validation.ps1` كمدخل CI/Release واحد لتشغيل Python controls و.NET build/regression/database integration.
8. **UI/load evidence**: أضيف Release Acceptance Gate صريح وبروتوكول `docs/UI_LOAD_VALIDATION_v281_AR.md`.

## ملاحظات تبيّن أنها ليست عيوبًا في v280
- **SQLite**: غير مستخدم. التطبيق يستخدم Microsoft SQL Server و`Microsoft.Data.SqlClient`.
- **غياب DI**: غير صحيح؛ `App.xaml.cs` يبني `ServiceCollection` ويسجل خدمات المصادقة/قاعدة البيانات/الواجهات الأساسية.
- **غياب Session Timeout**: غير صحيح؛ timeout مركزي موجود في `App.xaml.cs` ويستخدم inactivity monitor.
- **غياب Part1**: اسم الملف الأساسي مثل `EMPlanning.xaml.cs` هو الجزء الأول ضمنيًا؛ لا يلزم وجود ملف اسمه `Part1.cs`.

## ملاحظة MVVM
التحويل الكامل لكل شاشات WPF إلى MVVM ليس إصلاح defect صغيرًا بل إعادة تصميم واسعة قد تضيف مخاطر regression. لم يتم الادعاء بإتمام Full-MVVM. بدلاً من ذلك، أبقينا منطق الأمان/المعاملات في Services/Repositories/Infrastructure، وأضفنا ضوابط بنيوية واختبارات تمنع إنشاء طبقة بيانات جديدة عشوائيًا. يمكن تنفيذ MVVM migration لاحقًا كبرنامج refactoring مستقل مع UAT كامل.

## التحقق المنفذ في بيئة المراجعة
- Python/source/control tests: **472 PASS**.
- `scripts/validate_project.py --delivery-package`: **PASS**.
- Migration SQL manifest: **80/80 hashes match**.
- Partial-class validator: **PASS، 8 families**.
- لا توجد ملفات `bin/obj/.vs/__pycache__/.pytest_cache` في حزمة التسليم النهائية.

## ما لم يمكن إثباته هنا
بيئة المراجعة لا تحتوي Windows WPF build toolchain أو SQL Server runtime. لذلك يبقى مطلوبًا قبل Production:
- Clean Release Build.
- ReviewRegression.
- DatabaseIntegration على SQL Server ممثل.
- Production startup verify-only test بحساب runtime بلا DDL.
- UAT Water/EM/PRM/User Management/Audit/Printing.
- UI/load/concurrency evidence.
- QA approval.

## أوامر Windows المقترحة
```powershell
# المصدر/البناء/الاختبارات (مع PHARMALIMS_TEST_CONNECTION_STRING مضبوط لاختبارات DB)
powershell -ExecutionPolicy Bypass -File .\scripts\run_release_validation.ps1

# عند استخدام SQL Authentication فقط: أنشئ DPAPI secret تحت نفس Windows account الذي سيشغل PharmaLIMS
$secret = Read-Host "SQL password" -AsSecureString
.\scripts\protect_db_password.ps1 -Password $secret
```
ضع الناتج في `Database:DpapiProtectedPassword` واترك `Database:Password` فارغًا.
