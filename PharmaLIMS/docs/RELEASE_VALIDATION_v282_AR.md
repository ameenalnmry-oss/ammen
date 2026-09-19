# PharmaLIMS 2026.9.16.282 — Release Validation

## المسار الآلي المعتمد
على Windows مع .NET 8 وSQL Server LocalDB (أو `PHARMALIMS_TEST_MASTER_CONNECTION_STRING`) شغّل:

```powershell
./scripts/Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke
```

هذا المسار ينفذ: source validator، اختبارات Python regression، source manifest، Release WPF build، ReviewRegression، SQL DatabaseIntegration، ثم WPF RuntimeSmoke.

`RuntimeSmoke` يعمل كتجربة Development/Debug على قاعدة مؤقتة لأنه لا يجوز إضعاف سياسة TLS الخاصة بـProduction لأجل LocalDB. في نفس الجولة يتم بناء Release binary بشكل منفصل.

## التحقق البنيوي عند Production startup
قبل السماح بتسجيل الدخول، migration verification للمصادقة يتحقق Read-only من:
- package SQL SHA-256
- `LIMS_SchemaVersions.MigrationChecksum`
- physical schema postconditions

إذا كان ledger صحيحًا لكن schema الفعلي تالفًا أو ناقصًا، يتوقف startup برسالة `Migration Required` بدون تنفيذ DDL/DML.

## ما يبقى يدويًا
- Site-specific Production configuration/TLS
- Water / EM / PRM representative UAT
- User Management / e-signature scenarios
- Actual printer/report verification
- Operational load/concurrency evidence on the site environment
- QA approval
