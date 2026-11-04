using System.Globalization;

namespace PharmaLIMS.Services;

internal static class WaterReportNumericContract
{
    internal static string FormatRange(string testName, decimal? lower, decimal? upper) =>
        WaterNumericResultEvaluator.IsRangeTest(testName) && WaterNumericResultEvaluator.HasCompleteLimits(testName, lower, upper)
            ? lower!.Value.ToString("0.############################", CultureInfo.InvariantCulture) + " - " +
              upper!.Value.ToString("0.############################", CultureInfo.InvariantCulture)
            : "Approved specification snapshot missing";

    internal static string Conformity(string testName, decimal? value, decimal? alert, decimal? action) =>
        !value.HasValue ? "Check Required" : WaterNumericResultEvaluator.Evaluate(testName, value.Value, alert, action) switch
        {
            "PASS" => "Conform",
            "ALERT" => "Alert",
            "OOS" => "Non-Conform",
            _ => "Check Required"
        };
}
