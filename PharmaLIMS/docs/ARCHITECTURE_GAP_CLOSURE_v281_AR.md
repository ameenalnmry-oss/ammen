# PharmaLIMS 2026.9.16.281 — Architecture Gap Closure

## Partial classes
`ClassName.cs` هو الجزء الأساسي (Part 1 ضمنيًا). الملفات `ClassName.Part2.cs` و`Part3.cs` امتدادات لنفس `partial class`.
يمنع `scripts/check_partial_classes.py` وجود ملف Part يتيم أو عدم تطابق اسم الـ class. لذلك غياب ملف اسمه حرفيًا `Part1.cs` ليس نقصًا.

## Dependency Injection
التطبيق يستخدم `Microsoft.Extensions.DependencyInjection` من `App.xaml.cs`. خدمات قاعدة البيانات والمصادقة وإدارة المستخدمين وواجهات العمل الأساسية مسجلة في container.
الـ parameterless constructors المتبقية مطلوبة لبعض مسارات WPF/XAML/designer ولا تعد إنشاءً يدويًا لخدمات البيانات.

## MVVM / code-behind
المشروع Legacy WPF ويحتوي code-behind منظمًا إلى partial classes وخدمات/Repositories. التحويل الكامل إلى MVVM إعادة تصميم واسعة وليس إصلاح سلامة مناسبًا لإصدار hardening.
قاعدة الإصدار الحالية: منطق الأمان/المعاملات/التقييم القابل لإعادة الاستخدام يجب أن يبقى في Services/Repositories/Infrastructure، ولا يُنشأ DatabaseConnection أو repositories مباشرة داخل screens.

## Database engine
لا يوجد SQLite. الإنتاج مبني على Microsoft SQL Server و`Microsoft.Data.SqlClient`. ملاحظات SQLite locking غير منطبقة على هذه الحزمة.

## Session inactivity
يوجد timeout مركزي في `App.xaml.cs` عبر `DispatcherTimer` و`InputManager.PreProcessInput`، ويغلق سياق المصادقة ويعيد المستخدم إلى Login.

## Secrets
الإعداد المفضل Production هو Windows Integrated Security. عند الحاجة إلى SQL Authentication، v281 يرفض password plaintext ويقبل `DpapiProtectedPassword` المحمي بـ Windows DPAPI CurrentUser.

## Testing
Python source-contract tests وC# runtime/integration tests لها أدوار مختلفة. بدلاً من حذف أحد النوعين، `scripts/run_release_validation.ps1` يجمعهما في pipeline واحدة. الاختبارات التشغيلية على Windows/SQL Server تبقى شرط اعتماد.
