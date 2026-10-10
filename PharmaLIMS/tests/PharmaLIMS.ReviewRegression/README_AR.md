# اختبارات منطق PharmaLIMS

يشير هذا المشروع مباشرة إلى خدمات التطبيق الفعلية. إصدار 304 يحتوي على 354 حالة للحدود العددية، سلامة التخزين، اللقطات المتزامنة، توقيعات PRM، LES، تعديلات التحضير، أدلة تقرير EM وهوية الموظف.

```powershell
dotnet run --project tests/PharmaLIMS.ReviewRegression/PharmaLIMS.ReviewRegression.csproj --configuration Release
```

نجاح هذه الحالات لا يغطي بناء WPF أو تنفيذ SQL أو الطباعة. توجد اختبارات SQL مستقلة في PharmaLIMS.DatabaseIntegration تنشئ قاعدة ذات اسم عشوائي ثم تحذفها؛ لا يجوز توجيهها إلى الإنتاج. راجع docs/REVIEW_CLOSURE_v304.md لأدلة الإغلاق وحالات قبول الواجهة.
