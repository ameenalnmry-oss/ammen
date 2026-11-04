using System;
using System.Globalization;
using System.Text.RegularExpressions;
namespace PharmaLIMS.Services;
// Scientific rules extracted without changes from installed site 9C2AA5FD.
internal sealed class WaterWorkflowScientificItem
{
 public int SampleTestID {get;set;} public int TestID {get;set;}
 public string TestName {get;set;}=""; public string Unit {get;set;}="";
 public decimal? AlertLimit {get;set;} public decimal? ActionLimit {get;set;}
 public string SpecificationText {get;set;}=""; public string ResultValue {get;set;}=""; public string Remarks {get;set;}="";
}
internal sealed class WaterWorkflowScientific
{
 private readonly string currentSampleType; private readonly string currentPointCode;
 internal WaterWorkflowScientific(string type,string point){currentSampleType=type;currentPointCode=point;}
        internal bool IsPurifiedWaterSample()
        {
            string sampleType = currentSampleType == null ? "" : currentSampleType.Trim().ToLowerInvariant();
            return sampleType == "purified water" ||
                   sampleType == "purified" ||
                   sampleType.Contains("pws") ||
                   sampleType.Contains("purified");
        }

        internal bool IsPotableWaterSample()
        {
            string sampleType = currentSampleType == null ? "" : currentSampleType.Trim().ToLowerInvariant();
            return sampleType == "potable water" ||
                   sampleType == "potable" ||
                   sampleType.Contains("ptws") ||
                   sampleType.Contains("potable") ||
                   sampleType.Contains("drinking");
        }

        internal int ExtractPointNumber(string pointCode)
        {
            pointCode = pointCode == null ? "" : pointCode.Trim();
            if (string.IsNullOrWhiteSpace(pointCode))
                return 0;

            Match match = Regex.Match(pointCode, @"(\d+)");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int pointNumber))
                return pointNumber;

            return 0;
        }

        internal bool IsSoftWaterPoint()
        {
            if (!IsPotableWaterSample())
                return false;
            int pointNumber = ExtractPointNumber(currentPointCode);
            return pointNumber >= 5;
        }

        internal bool IsConductivityTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("conductivity");
        }

        internal bool IsHardnessTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("hardness") ||
                   (testName.Contains("calcium") && testName.Contains("magnesium"));
        }

        internal bool IsResidualChlorineTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("residual chlorine") ||
                   testName.Contains("free chlorine") ||
                   testName == "chlorine";
        }

        internal bool IsRemovedTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName == "taste" ||
                   testName == "odor" ||
                   testName == "odour" ||
                   testName == "taste & odor" ||
                   testName == "taste and odor" ||
                   testName == "taste & odour" ||
                   testName == "taste and odour";
        }

        internal bool IsAppearanceTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("appearance") ||
                   testName.Contains("color") ||
                   testName.Contains("colour") ||
                   testName.Contains("clarity");
        }

        internal bool IsPhTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName == "ph" ||
                   testName == "p.h" ||
                   testName.Contains("ph value") ||
                   testName.Contains("ph test") ||
                   testName.StartsWith("ph ") ||
                   testName.EndsWith(" ph");
        }

        internal bool IsAbsencePresenceTest(string unit, string testName)
        {
            unit = unit == null ? "" : unit.Trim().ToLowerInvariant();
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            if (unit == "absence")
                return true;

            return testName.Contains("e. coli") ||
                   testName.Contains("salmonella") ||
                   testName.Contains("staphylococcus") ||
                   testName.Contains("pseudomonas") ||
                   testName.Contains("burkholderia") ||
                   testName.Contains("candida") ||
                   testName.Contains("clostridia") ||
                   testName.Contains("bile tolerant") ||
                   testName.Contains("gram-negative") ||
                   testName.Contains("aureus");
        }

        internal bool IsComplianceQualitativeTest(string testName)
        {
            string normalized = (testName ?? string.Empty).Trim().ToLowerInvariant();

            if (normalized.Contains("residual chlorine") ||
                normalized.Contains("free chlorine") ||
                normalized.Contains("nitrates (as n)") ||
                normalized.Contains("nitrate (as n)"))
            {
                return false;
            }

            return normalized == "acidity" ||
                   normalized == "ammonium" ||
                   normalized == "ammonia" ||
                   normalized == "chloride" ||
                   normalized == "chlorides" ||
                   normalized == "nitrate" ||
                   normalized == "nitrates" ||
                   normalized == "sulphate" ||
                   normalized == "sulphates" ||
                   normalized == "sulfate" ||
                   normalized == "sulfates" ||
                   normalized.Contains("heavy metals") ||
                   normalized.Contains("oxidisable substances") ||
                   normalized.Contains("oxidizable substances");
        }

        internal bool IsComplianceQualitativeTest(string testName, string unit)
        {
            string normalizedUnit = (unit ?? string.Empty).Trim().ToUpperInvariant()
                .Replace("µ", "U", StringComparison.Ordinal)
                .Replace("μ", "U", StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);

            bool numericUnit =
                normalizedUnit.Contains("MG/L", StringComparison.Ordinal) ||
                normalizedUnit.Contains("PPM", StringComparison.Ordinal) ||
                normalizedUnit.Contains("PPB", StringComparison.Ordinal) ||
                normalizedUnit.Contains("UG/L", StringComparison.Ordinal) ||
                normalizedUnit.Contains("NG/L", StringComparison.Ordinal) ||
                normalizedUnit.Contains("US/CM", StringComparison.Ordinal) ||
                normalizedUnit.Contains("NTU", StringComparison.Ordinal) ||
                normalizedUnit.Contains("MG/100ML", StringComparison.Ordinal);

            return !numericUnit && IsComplianceQualitativeTest(testName);
        }

        internal bool IsApprovedComplianceQualitative(WaterWorkflowScientificItem item)
        {
            if (item == null || !IsComplianceQualitativeTest(item.TestName))
                return false;

            // Numeric specifications remain numeric. A legacy numeric master-unit
            // label must not override an immutable approved comparator/endpoint snapshot.
            if (item.AlertLimit.HasValue || item.ActionLimit.HasValue)
                return false;

            string specification = (item.SpecificationText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(specification))
                return IsComplianceQualitativeTest(item.TestName);

            string normalized = specification.ToLowerInvariant();
            return normalized.Contains("record complies/does not comply") ||
                   normalized.Contains("not more intensely coloured") ||
                   normalized.Contains("not more intensely colored") ||
                   normalized.Contains("comparator") ||
                   normalized.Contains("does not change") ||
                   normalized.Contains("remains faintly pink") ||
                   normalized.Contains("not red");
        }

        internal void ApplyEffectiveSpecification(WaterWorkflowScientificItem item)
        {
            if (item == null)
                return;

            if (IsPhTest(item.TestName))
            {
                if (IsPurifiedWaterSample())
                {
                    item.AlertLimit = 5.00m;
                    item.ActionLimit = 7.00m;
                    return;
                }

                if (IsPotableWaterSample())
                {
                    item.AlertLimit = 6.50m;
                    item.ActionLimit = 8.50m;
                    return;
                }
            }

            if (IsConductivityTest(item.TestName))
            {
                if (IsPurifiedWaterSample())
                {
                    item.AlertLimit = null;
                    item.ActionLimit = 1.30m;
                    return;
                }

                if (IsPotableWaterSample())
                {
                    item.AlertLimit = null;
                    item.ActionLimit = 500.00m;
                    return;
                }
            }

            if (IsHardnessTest(item.TestName) && IsPotableWaterSample())
            {
                item.AlertLimit = null;
                item.ActionLimit = IsSoftWaterPoint() ? 5.00m : 300.00m;
                return;
            }
        }

        internal bool TryNormalizeResultForSave(WaterWorkflowScientificItem item, out decimal result)
        {
            result = 0;

            if (item == null)
                return false;

            string raw = item.ResultValue == null ? "" : item.ResultValue.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            if (raw.Contains(','))
                return false;

            if (IsAppearanceTest(item.TestName))
            {
                if (raw.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Clear and Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }

                if (raw.Equals("Not Clear / Colored", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Turbid", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colored", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Coloured", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Non-Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }
            }

            if (IsApprovedComplianceQualitative(item))
            {
                if (raw.Equals("Complies", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Comply", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Acceptable", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }

                if (raw.Equals("Does Not Comply", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Does not comply", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Non-Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Nonconform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Fail", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Acceptable", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not OK", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }

                if (WaterNumericResultEvaluator.TryParseBinary(raw, out decimal qualitativeValue))
                {
                    result = qualitativeValue;
                    return true;
                }

                return false;
            }

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
            {
                if (raw.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Absent", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Negative", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("No", StringComparison.OrdinalIgnoreCase))
                {
                    result = 0;
                    return true;
                }

                if (raw.Equals("Presence", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Present", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Positive", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                {
                    result = 1;
                    return true;
                }

                if (WaterNumericResultEvaluator.TryParseBinary(raw, out decimal qualitativeValue))
                {
                    result = qualitativeValue;
                    return true;
                }

                return false;
            }

            return WaterNumericResultEvaluator.TryParseNonnegative(raw, out result);
        }

        internal string FormatResultForDisplay(WaterWorkflowScientificItem item)
        {
            if (item == null)
                return "";

            string raw = item.ResultValue == null ? "" : item.ResultValue.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "";

            if (IsAppearanceTest(item.TestName))
            {
                if (ControlledNumericValue.TryParse(raw, out decimal value) && (value == 0m || value == 1m))
                    return value <= 0 ? "Clear and Colorless" : "Not Clear / Colored";

                return raw;
            }

            if (IsApprovedComplianceQualitative(item))
            {
                if (ControlledNumericValue.TryParse(raw, out decimal value) && (value == 0m || value == 1m))
                    return value <= 0 ? "Complies" : "Does Not Comply";

                return raw;
            }

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
            {
                if (ControlledNumericValue.TryParse(raw, out decimal value) && (value == 0m || value == 1m))
                    return value <= 0 ? "Absence" : "Presence";

                return raw;
            }

            if (ControlledNumericValue.TryParse(raw, out decimal numeric))
                return WaterResultValueContract.FormatNumeric(numeric);

            return raw;
        }

        internal string CalculatePassFail(WaterWorkflowScientificItem item)
        {
            if (item == null)
                return "Pending";

            string raw = item.ResultValue == null ? "" : item.ResultValue.Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "Pending";

            if (!TryNormalizeResultForSave(item, out decimal resultValue))
                return "Invalid";

            if (IsAppearanceTest(item.TestName))
                return resultValue <= 0 ? "PASS" : "OOS";

            if (IsApprovedComplianceQualitative(item))
                return resultValue <= 0 ? "PASS" : "OOS";

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
                return resultValue <= 0 ? "PASS" : "OOS";

            return WaterNumericResultEvaluator.Evaluate(item.TestName, resultValue, item.AlertLimit, item.ActionLimit);
        }

        internal string BuildDeviationType(WaterWorkflowScientificItem item, string status)
        {
            if (item == null || string.IsNullOrWhiteSpace(status))
                return "";

            if (status.Equals("PASS", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                return "";

            if (IsAbsencePresenceTest(item.Unit, item.TestName))
                return "Microbiological OOS";

            if (item.TestName != null &&
                (item.TestName.Contains("TAMC", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("TYMC", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Microbial", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Aerobic", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Yeast", StringComparison.OrdinalIgnoreCase) ||
                 item.TestName.Contains("Mold", StringComparison.OrdinalIgnoreCase)))
            {
                return status.Equals("ALERT", StringComparison.OrdinalIgnoreCase)
                    ? "Microbiological Alert"
                    : "Microbiological OOS";
            }

            if (status.Equals("ALERT", StringComparison.OrdinalIgnoreCase))
                return "Alert Limit Exceeded";

            if (status.Equals("OOS", StringComparison.OrdinalIgnoreCase))
                return "Action Limit / Specification Exceeded";

            return status;
        }
}
