# PharmaLIMS 2026.9.16.287 — Release Validation

## 1. بوابة المصدر وWindows Release
```powershell
./scripts/Invoke-ReleaseValidation.ps1 -RunDatabaseIntegration -RunRuntimeSmoke
```
يجب أن تنجح بالكامل. SQL integration في v285 يتضمن:
- اكتشاف جدول compliance محمي مفقود.
- decoy/exact-trigger tamper test.
- deceptive-body test يثبت أن وجود كلمات THROW/body token وحده لا يكفي.
- محاولة UPDATE وDELETE فعلية على AuditTrail والتأكد أن trigger المعتمد يمنعهما.

## 2. Production artifact gate
بعد النشر والتوقيع:
```powershell
./scripts/Invoke-ProductionArtifactSmoke.ps1 -AppPath ./artifacts/publish/PharmaLIMS.exe
```
يتحقق من trusted TLS، migration ledger، readiness/Login، ومن عدم تغير قاعدة smoke أثناء startup باستخدام SHA-256 row-multiset fingerprint بدل CHECKSUM_AGG/BINARY_CHECKSUM.

## 3. SBOM وSupply Chain
المسار المعتمد في CI:
1. restore/build/validation.
2. publish Production.
3. Authenticode signing.
4. Production Artifact Smoke.
5. publish SHA-256 manifest.
6. `New-ResolvedSbom.ps1` لتوليد CycloneDX من resolved NuGet graph الحالي.
7. إعادة توليد/التحقق من publish manifest بحيث يشمل SBOM.
8. provenance attestation.
9. upload artifact.

GitHub Actions الرسمية في workflow مثبتة إلى commit SHA وليست major tags متحركة.

## 4. Full System Preflight
في Production يلزم `0 BLOCKER`. عقد حماية compliance المركزي يعيد كل الجداول الخاضعة للحماية حتى عند فقدان جدول، ويعرض `TableExists`, exact trigger state, UPDATE/DELETE events وbody structural evidence. الاختبار السلوكي الإلزامي موجود في SQL integration gate.

## 5. ما يبقى قبل Production Approval
- Windows Release build فعلي.
- SQL Server integration ناجح على قاعدة disposable/upgrade ممثلة.
- Production Artifact Smoke على الملف المنشور والموقّع.
- Authenticode verification.
- Full System Preflight = 0 BLOCKER.
- UAT تمثيلي Water / EM / PRM / User Management / e-signature.
- الطباعة الفعلية.
- load/concurrency evidence.
- QA approval موثق.

## NuGet lock gate (v287)
- نفذ `./scripts/Generate-NuGetLocks.ps1` مرة واحدة فقط عند تحديث dependency graph وتحت change control.
- راجع ملفات `packages.lock.json` وأدخلها إلى source control.
- CI يرفض غياب lock الرئيسي ويستخدم `dotnet restore ./PharmaLIMS.csproj --locked-mode`.
- لا يجوز إنشاء lock يدويًا أو نسخ hashes من مصدر غير resolved restore.
