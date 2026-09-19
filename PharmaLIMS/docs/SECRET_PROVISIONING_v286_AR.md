# SQL Authentication Secret Provisioning — v286

الخيار المفضل Production هو Windows Integrated Security. عند اعتماد SQL Authentication فقط:

1. سجّل الدخول إلى Windows بنفس الحساب الذي سيشغل PharmaLIMS.
2. شغّل `dotnet run --project tools/PharmaLIMS.SecretProvisioning/PharmaLIMS.SecretProvisioning.csproj -c Release`.
3. أدخل SQL password من لوحة المفاتيح؛ الإدخال مخفي ولا تتم كتابته إلى ملف أو log.
4. انسخ الناتج Base64 إلى `Database:DpapiProtectedPassword`.
5. اترك `Database:Password` فارغًا.
6. أعد provisioning عند تغيير حساب Windows أو SQL password.

DPAPI يستخدم `CurrentUser`، لذلك blob الناتج مرتبط بحساب Windows الذي أنشأه.
