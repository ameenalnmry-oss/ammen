using System.Globalization;
using PharmaLIMS;

return new CertificateMethods().Run();

namespace PharmaLIMS
{
    internal partial class CertificateMethods
    {
        public int Run()
        {
            int checks = 0, failures = 0;
            void Equal(string expected, string actual, string name)
            {
                checks++;
                if (expected == actual) return;
                failures++;
                Console.Error.WriteLine($"FAIL {name}: expected '{expected}', got '{actual}'");
            }
            object Missing = DBNull.Value;
            foreach (string culture in new[] { "en-US", "de-DE", "fr-FR", "ar-EG" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (decimal value in new[] { 0.0002m, 0.000001m, 0.1234567890123456789012345678m, 123.456789m, 0m })
                {
                    string expected = value.ToString(CultureInfo.InvariantCulture);
                    Equal(expected, FormatResultForCertificate("Heavy metals", "mg/L", value, Missing, 1m, ""), $"{culture} exact result {expected}");
                }
                Equal("NMT 0.0002", FormatReportSpecification("Purified Water", "Heavy metals", "mg/L", Missing, 0.0002m, ""), $"{culture} exact upper limit");
                foreach (string name in new[] { "pH", "p.h", "Free Residual Chlorine", "Free chlorine", "chlorine" })
                {
                    Equal("0.0002 - 0.0004", FormatReportSpecification("Potable Water", name, "mg/L", 0.0002m, 0.0004m, "Frozen specification"), $"{culture} {name} exact range");
                    foreach (var item in new[] { (0.0001m, "Non-Conform"), (0.0002m, "Conform"), (0.0003m, "Conform"), (0.0004m, "Conform"), (0.0005m, "Non-Conform") })
                        Equal(item.Item2, GetConformity("Potable Water", name, "mg/L", item.Item1, 0.0002m, 0.0004m, ""), $"{culture} {name} inclusive range {item.Item1.ToString(CultureInfo.InvariantCulture)}");
                    foreach (var bounds in new[] { (Missing, (object)0.0004m), ((object)0.0002m, Missing), (Missing, Missing), ((object)0.0004m, (object)0.0002m), ((object)(-1m), (object)0.0002m) })
                    {
                        Equal("Check Required", GetConformity("Potable Water", name, "mg/L", 0.0003m, bounds.Item1, bounds.Item2, "Operational / trend parameter"), $"{culture} {name} invalid range conformity");
                        Equal("Check Required", FormatReportSpecification("Potable Water", name, "mg/L", bounds.Item1, bounds.Item2, "Operational / trend parameter"), $"{culture} {name} invalid range specification");
                    }
                    Equal("Conform", GetConformity("Potable Water", name, "mg/L", 0m, 0m, 0m, ""), $"{culture} {name} zero endpoints");
                    Equal("0 - 0", FormatReportSpecification("Potable Water", name, "mg/L", 0m, 0m, ""), $"{culture} {name} zero range");
                }
                Equal("Complies", FormatResultForCertificate("Ammonium", "", 0m, Missing, Missing, "Not more intensely coloured than comparator"), $"{culture} qualitative result");
                Equal("Not more intense than comparator", FormatReportSpecification("Purified Water", "Ammonium", "", Missing, Missing, "Not more intensely coloured than comparator"), $"{culture} qualitative specification");
                Equal("Conform", GetConformity("Purified Water", "Ammonium", "", 0m, Missing, Missing, "Not more intensely coloured than comparator"), $"{culture} qualitative conformity");
                Equal("Absence", FormatResultForCertificate("E. coli", "Absence", 0m, Missing, Missing, ""), $"{culture} absence");
                Equal("Clear and Colorless", FormatResultForCertificate("Appearance", "", 0m, Missing, Missing, ""), $"{culture} appearance");
                Equal("Pending", FormatResultForCertificate("pH", "", Missing, 6m, 8m, ""), $"{culture} pending");
                Equal("Approved specification snapshot missing", FormatReportSpecification("Purified Water", "Custom test", "", Missing, Missing, ""), $"{culture} no invented snapshot");
                Equal("Frozen custom criterion", FormatReportSpecification("Purified Water", "Custom test", "", Missing, Missing, "Frozen custom criterion"), $"{culture} frozen criterion preserved");
            }
            Console.WriteLine($"{checks - failures}/{checks} actual certificate method checks passed; {failures} failed.");
            return failures == 0 ? 0 : 1;
        }
    }
}
