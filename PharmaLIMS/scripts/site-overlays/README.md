# AMEEN certificate precision correction — source review only

This complete replacement artifact is based on the uploaded AMEEN source ZIP,
not on the current GitHub ReportCertificate.xaml.cs. It is deliberately outside
the application's compiled source and is not installed by the deployment scripts.
Do not activate it until the entire mixed site source cohort is qualified.

Input: PharmaLIMS-Source-648ba40b.zip, SHA256
`b9244b38b1d4cb72dbd0bacf913fb7e92e07802626057e5169a5c58d71b6309a`.

The source fixture uses UTF-8 without BOM and LF line endings. Its SHA256 is
`b461b2025947277f412e94b22c11782609279f1571164882a9f1da466c91e282`.
The corrected complete source SHA256 is
`65d28d48cf0f96a759de49b6678214e45d5e994c6aed5400435787777ca62200`.

Exactly three source lines change: parse numeric objects with invariant culture,
and preserve all decimal digits in the numeric result and NMT upper limit.
Local range, frozen specification, zero upper limit and trend behavior remain.
No SQL, service configuration, version or deployment allowlist changes are included.

Run `./scripts/Invoke-Eq001SiteCertificateRegression.ps1` from an existing checkout.
It extracts the actual certificate methods and requires the frozen source to fail
the demonstrated precision assertion before requiring the corrected source to pass.
The unchanged general certificate suite also remains a separate release gate.

Portable .NET 8 execution on 2026-10-29 reproduced 39 failures out of 112 checks
in the supplied source; the corrected source passed 112/112. The current GitHub
certificate methods passed their existing 416/416 checks. These results cover
formatting and conformity methods; they do not establish WPF printing or site UAT.

The source-export encoding regression extracts helpers by AST and never executes
the exporter's site body. Its frozen fixture is the unchanged exporter at commit
97b347290360d8f8f90f6d29c56029af59298b24. It must reproduce the false Unicode
mismatch with an ASCII console, then verify UTF-8, BOM and line-ending handling
in the corrected helper while continuing to reject actual source changes.
