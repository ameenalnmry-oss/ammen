# PharmaLIMS 2026.9.18.294 — Full Copy / Replace

هذه النسخة مبنية مباشرة على `2026.9.18.293` وتضيف قوالب المواصفات الميكروبيولوجية المبنية على USP/Ph. Eur. لمسار PRM بدون تجاوز نظام Review / Approve.
سلسلة المصدر الخاضعة للضبط ما زالت ترجع إلى أساس إدارة المستخدمين `2026.9.17.288`، مع الحفاظ على جميع migrations التاريخية دون تعديل.

## ما الجديد

- `Create Profile for This Scope` يحمّل تلقائيًا قالب الميكرو المناسب للفئة الحالية.
- أضيف زر `Load Pharmacopeial Template` داخل Specification Master.
- Finished Product وStability للأقراص/المستحضرات الفموية غير المائية: TAMC معيار 10^3 (الحد الأقصى المقبول 2000 CFU/g)، TYMC معيار 10^2 (الحد الأقصى المقبول 200 CFU/g)، وEscherichia coli غائب في 1 g.
- Raw Material: TAMC/TYMC حسب معيار substances for pharmaceutical use، بينما الكائنات المحددة تعتمد على monograph/risk assessment للمادة ولا تُفرض كمتطلب عام غير صحيح.
- In-Process: قالب رقابي داخلي aligned مع مواصفات المنتج النهائي الفموي غير المائي؛ لا يتم وصفه على أنه فئة دوائية مستقلة في الفارماكوبيا.
- القالب يبقى Draft حتى Save Draft -> Review -> Approve بالتوقيع الإلكتروني؛ لا يوجد Auto-Approval.

## قبل الاستخدام على Windows

1. استبدل المشروع كاملًا بهذه الحزمة واحذف `bin` و`obj` ثم نفّذ Clean/Rebuild.
2. شغّل Database Integration وRuntime Smoke وFull System Preflight وتأكد من `0 BLOCKER`.
3. راجع القالب مقابل مواصفة المنتج/المادة المسجلة؛ إذا كانت مواصفة Medica أو monograph الخاص بالمنتج أشد فهي المرجع المعتمد بعد QA approval.
4. نفّذ UAT والطباعة والحمل/التزامن وموافقة QA قبل Production.

## حدود التغيير

- جميع الـ82 migration التاريخية مطابقة للنسخة v293؛ لا توجد migration جديدة.
- Water وEM وCulture Media وAuthentication لم تتغير.
- التغيير محصور في PRM Specification Master وبيانات الإصدار/الاختبارات.

راجع `RELEASE_NOTES_2026.9.18.294.md` و`CHANGESET_v294.txt`.
