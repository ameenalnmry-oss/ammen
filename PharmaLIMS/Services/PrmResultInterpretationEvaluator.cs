using System;

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

            if (IsQualitativeAbsenceTest(normalizedType, normalizedSpecification))
            {
                if (IsNegativeQualitativeResult(normalizedResult))
                    return "Conforms";

                if (IsPositiveQualitativeResult(normalizedResult))
                    return "Does Not Conform";

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

        private static bool IsQualitativeAbsenceTest(string resultType, string specification)
        {
            return resultType.Contains("Presence", StringComparison.OrdinalIgnoreCase) ||
                   resultType.Contains("Qualitative", StringComparison.OrdinalIgnoreCase) ||
                   specification.Contains("Absent", StringComparison.OrdinalIgnoreCase) ||
                   specification.Contains("Absence", StringComparison.OrdinalIgnoreCase) ||
                   specification.Contains("Not Detected", StringComparison.OrdinalIgnoreCase) ||
                   specification.Contains("Negative", StringComparison.OrdinalIgnoreCase);
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
