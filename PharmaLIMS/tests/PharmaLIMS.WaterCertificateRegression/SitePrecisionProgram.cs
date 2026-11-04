using System.Globalization;
using PharmaLIMS;

return new CertificateMethods().RunSitePrecision();

namespace PharmaLIMS
{
    internal partial class CertificateMethods
    {
        public int RunSitePrecision()
        {
            int checks = 0, failures = 0;
            void Equal(string expected, string actual, string name)
            {
                checks++;
                if (expected == actual) return;
                failures++;
                Console.Error.WriteLine($"FAIL {name}: expected '{expected}', got '{actual}'");
            }
            object missing = DBNull.Value;
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                foreach (string culture in new[] { "en-US", "de-DE", "fr-FR", "ar-EG" })
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                    foreach (var item in new[] {
                        (0.0002m, "0.0002"), (0.000001m, "0.000001"),
                        (0.1234567890123456789012345678m, "0.1234567890123456789012345678"),
                        (123.456789m, "123.456789"), (0m, "0"), (-0.0002m, "-0.0002") })
                        Equal(item.Item2, FormatResultForCertificate("Heavy metals", "mg/L", item.Item1, missing, 1m, ""), culture + " exact result " + item.Item2);
                    Equal("NMT 0.0002", FormatReportSpecification("Purified Water", "Heavy metals", "mg/L", missing, 0.0002m, ""), culture + " exact frozen upper limit");
                    Equal("NMT 0", FormatReportSpecification("Purified Water", "Custom numeric test", "mg/L", missing, 0m, ""), culture + " retained site zero upper limit");
                    foreach (string name in new[] { "pH", "p.h", "Free Residual Chlorine", "Free chlorine", "chlorine" })
                    {
                        Equal("0.0002 - 0.0004", FormatReportSpecification("Potable Water", name, "mg/L", 0.0002m, 0.0004m, "Frozen specification"), culture + " exact range " + name);
                        Equal("Conform", GetConformity("Potable Water", name, "mg/L", 0.0003m, 0.0002m, 0.0004m, ""), culture + " range conformity " + name);
                        Equal("Non-Conform", GetConformity("Potable Water", name, "mg/L", 0.0005m, 0.0002m, 0.0004m, ""), culture + " range failure " + name);
                    }
                    Equal("Clear and Colorless", FormatResultForCertificate("Appearance", "", 0m, missing, missing, ""), culture + " retained appearance");
                    Equal("Absence", FormatResultForCertificate("E. coli", "Absence", 0m, missing, missing, ""), culture + " retained absence");
                    Equal("Pending", FormatResultForCertificate("pH", "", missing, 6m, 8m, ""), culture + " retained pending");
                    Equal("Operational / trend parameter", FormatReportSpecification("Potable Water", "pH", "", missing, missing, "Operational / trend parameter"), culture + " retained site trend specification");
                    Equal("Trend", GetConformity("Potable Water", "pH", "", 7m, missing, missing, "Operational / trend parameter"), culture + " retained site trend classification");
                }
            }
            finally { CultureInfo.CurrentCulture = previous; }
            Console.WriteLine($"Site certificate precision: {checks - failures}/{checks} checks passed; {failures} failed.");
            return failures == 0 ? 0 : 1;
        }
    }
}
