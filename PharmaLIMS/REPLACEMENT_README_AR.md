# PharmaLIMS 2026.9.23.295 — Full Copy / Replace

هذه النسخة مبنية على آخر `main` بعد إغلاق مراجعة 23-Sep-2026، وتثبت هوية إصدار مستقلة عن v294. خط lineage المحكوم يشمل الإصدار السابق `2026.9.17.288` ضمن سلسلة التطوير التاريخية.

## حدود التغيير

- جميع **85 controlled migrations** الحالية محفوظة دون تعديل bytes التاريخية.
- تم تحديث release identity إلى `2026.9.23.295`.
- تم إغلاق فجوة `Clone Active Approved`: لا يتم اختراع `MinimumElapsedHours = 120` عند غياب القيمة التاريخية.
- أي required cloned row بدون controlled timing يبقى fail-closed عند `Save Draft` حتى إدخال قيمة صحيحة ثم `Review / Approve`.
- Production / In-Process يبقى مربوطًا بالـcontrolled profile `MQC-G-0021` وبصفوف مستقلة لكل اختبار.
- لم يتم إدخال تغيير وظيفي على Water أو EM أو Culture Media ضمن هذا الإغلاق.

## قبل اعتماد Production

لا تعتبر نسخة Production معتمدة بمجرد نجاح source CI. يجب أن ينجح المسار المحكوم على `main`: Production configuration validation، self-contained win-x64 publish، Authenticode signing، exact signed artifact smoke، publish manifest، resolved SBOM، provenance attestation، ثم controlled artifact upload.

راجع `RELEASE_NOTES_2026.9.23.295.md` و`CHANGESET_v295.txt`.
