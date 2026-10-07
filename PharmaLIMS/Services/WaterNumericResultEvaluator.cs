namespace PharmaLIMS.Services;

/// <summary>Frozen numeric water rules shared by result entry and reporting.</summary>
internal static class WaterNumericResultEvaluator
{
    internal static bool TryParseNonnegative(string? input, out decimal value) =>
        ControlledNumericValue.TryParse(input, out value) && value >= 0m;

    internal static bool TryParseBinary(string? input, out decimal value) =>
        ControlledNumericValue.TryParse(input, out value) && (value == 0m || value == 1m);

    internal static bool IsRangeTest(string? testName)
    {
        string name = (testName ?? string.Empty).Trim().ToLowerInvariant();
        return name == "ph" || name == "p.h" || name.Contains("ph value") || name.Contains("ph test") ||
            name.StartsWith("ph ") || name.EndsWith(" ph") || name.Contains("residual chlorine") ||
            name.Contains("free chlorine") || name == "chlorine";
    }

    internal static bool HasCompleteLimits(string? testName, decimal? alert, decimal? action) =>
        action.HasValue && action >= 0m && (!alert.HasValue || (alert >= 0m && alert <= action)) &&
        (!IsRangeTest(testName) || alert.HasValue);

    internal static string Evaluate(string? testName, decimal value, decimal? alert, decimal? action)
    {
        if (value < 0m) return "Invalid";
        if (!HasCompleteLimits(testName, alert, action)) return "NOT ASSESSED";
        if (IsRangeTest(testName))
            return value < alert!.Value || value > action!.Value ? "OOS" : "PASS";
        if (value > action!.Value) return "OOS";
        return alert.HasValue && value > alert.Value ? "ALERT" : "PASS";
    }

    internal static bool RequiresNumericLimits(string? testName, string? unit, string? specification)
    {
        if (IsRangeTest(testName)) return true;
        string spec = (specification ?? string.Empty).Trim().ToLowerInvariant();
        // These are the existing controlled comparator/endpoint water rules.
        // They must not acquire fabricated numeric limits from a master unit label.
        if (spec.Contains("record complies/does not comply") || spec.Contains("not more intensely coloured") ||
            spec.Contains("not more intensely colored") || spec.Contains("comparator") ||
            spec.Contains("does not change") || spec.Contains("remains faintly pink") || spec.Contains("not red"))
            return false;
        string u = (unit ?? string.Empty).Replace("µ", "U").Replace("μ", "U").Replace(" ", "").ToUpperInvariant();
        string name = (testName ?? string.Empty).ToLowerInvariant();
        return u.Contains("CFU") || u.Contains("MG/L") || u.Contains("PPM") || u.Contains("PPB") ||
            u.Contains("UG/L") || u.Contains("NG/L") || u.Contains("US/CM") || u.Contains("NTU") ||
            u.Contains("MG/100ML") || name.Contains("conductivity") || name.Contains("organic carbon") ||
            name.Contains("hardness");
    }
}
