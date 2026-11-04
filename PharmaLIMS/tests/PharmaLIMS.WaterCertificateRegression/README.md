Run with .NET 8 and Python 3:

```sh
dotnet run --project PharmaLIMS/tests/PharmaLIMS.WaterCertificateRegression/PharmaLIMS.WaterCertificateRegression.csproj
```

This portable harness extracts `FormatResultForCertificate`, `FormatReportSpecification`,
`GetConformity`, and their report helper methods directly from the current production
`ReportCertificate.xaml.cs` during every build. It compiles the actual shared numeric
evaluator and parser. It does not reproduce report logic in a test-only implementation.
The sole UI-state stub is the point-code field used by the existing hardness branch.

Checks cover exact small numeric results and limits, inclusive frozen pH/chlorine
range endpoints, both out-of-range directions, missing/negative/reversed bounds,
zero bounds, four workstation cultures, and preservation of approved qualitative,
appearance, absence, pending, and custom frozen specifications. It does not run WPF
or verify rendered certificate layout or database integration.
