using System;
using System.Text.RegularExpressions;

namespace PharmaLIMS.Services
{
    internal static class PrmResultInterpretationEvaluator
    {
        internal static string Evaluate(
            string? resultType,
            string? specification,
            string? result,
            string? specificationLimit = "",
            string? testCode = "",
            string? testName = "")
        {
            if (string.IsNullOrWhiteSpace(result))
                return "Not Tested";

            string normalizedType = resultType ?? string.Empty;
            string normalizedSpecification = specification ?? string.Empty;
            string normalizedResult = result.Trim();

            bool hasRequiredPresence = TryGetRequiredPresence(normalizedSpecification, out bool requiredPresence);
            if (normalizedType.Contains("Presence", StringComparison.OrdinalIgnoreCase) ||
                normalizedType.Contains("Qualitative", StringComparison.OrdinalIgnoreCase) || hasRequiredPresence)
            {
                if (!hasRequiredPresence) return "Check Required";
                if (IsNegativeQualitativeResult(normalizedResult))
                    return requiredPresence ? "Does Not Conform" : "Conforms";

                if (IsPositiveQualitativeResult(normalizedResult))
                    return requiredPresence ? "Conforms" : "Does Not Conform";

                return "Check Required";
            }

            if (normalizedType.Contains("Numeric", StringComparison.OrdinalIgnoreCase))
            {
                if (!PrmNumericSpecificationEvaluator.TryParseControlledDecimal(normalizedResult, out decimal value) || value < 0m)
                    return "Check Required";

                return PrmNumericSpecificationEvaluator.Evaluate(
                    value,
                    normalizedSpecification,
                    specificationLimit,
                    testCode,
                    testName);
            }

            if (normalizedResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                normalizedResult.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
            {
                return "Conforms";
            }

            if (normalizedResult.Equals("Fail", StringComparison.OrdinalIgnoreCase) ||
                normalizedResult.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
            {
                return "Does Not Conform";
            }

            return "Check Required";
        }

        internal static bool TryGetRequiredPresence(string? specification, out bool requiredPresence)
        {
            requiredPresence = false;
            string spec = (specification ?? string.Empty).Trim();
            if (spec.Length == 0 || spec.Length > 4096) return false;
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            const string absence = @"\b(?:absent|absence|not\s+detected|not\s+(?:be\s+)?present|negative|no\s+growth|nil)\b";
            bool requiresAbsence = Regex.IsMatch(spec, absence, options);
            string remaining = Regex.Replace(spec, absence, string.Empty, options);
            if (Regex.IsMatch(remaining, @"\b(?:not|unless|except)\b", options)) return false;
            bool requiresPresence = Regex.IsMatch(remaining, @"\b(?:present|presence|detected|positive|growth)\b", options);
            if (requiresAbsence == requiresPresence) return false;
            requiredPresence = requiresPresence;
            return true;
        }

        private static bool IsNegativeQualitativeResult(string result)
        {
            return result.Equals("Absent", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Negative", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Not Detected", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("No Growth", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Nil", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPositiveQualitativeResult(string result)
        {
            return result.Equals("Present", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Presence", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Positive", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Detected", StringComparison.OrdinalIgnoreCase) ||
                   result.Equals("Growth", StringComparison.OrdinalIgnoreCase);
        }
    }
}
