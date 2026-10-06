namespace PharmaLIMS.Services;

/// <summary>The existing external import model supports upper limits only.</summary>
internal static class ExternalTrendNumericContract
{
    private const decimal ExclusiveMaximumMagnitude = 10000000000000000000000000000m;

    internal static bool IsExactlyRepresentable(decimal value) =>
        value > -ExclusiveMaximumMagnitude && value < ExclusiveMaximumMagnitude && decimal.Round(value, 10) == value;

    internal static bool TryParseQualified(string? text, out decimal value, out string? qualifier)
    {
        value = 0m; qualifier = null;
        string normalized = (text ?? string.Empty).Trim().Replace("≤", "<=").Replace("≥", ">=");
        foreach (string candidate in new[] { "<=", ">=", "<", ">" })
        {
            if (!normalized.StartsWith(candidate, StringComparison.Ordinal)) continue;
            qualifier = candidate; normalized = normalized[candidate.Length..].Trim(); break;
        }
        return ControlledNumericValue.TryParse(normalized, out value) && value >= 0m && IsExactlyRepresentable(value);
    }

    internal static string EvaluateUpper(decimal result, string? qualifier, decimal? alert, decimal? action)
    {
        if (result < 0m || !IsExactlyRepresentable(result) ||
            alert.HasValue && (alert < 0m || !IsExactlyRepresentable(alert.Value)) ||
            action.HasValue && (action < 0m || !IsExactlyRepresentable(action.Value)) ||
            alert.HasValue && action.HasValue && alert > action)
            return "UNASSESSED";
        if (!alert.HasValue && !action.HasValue) return "UNASSESSED";
        string q = qualifier ?? string.Empty;
        if (q is "<" or "<=")
        {
            decimal? lowestLimit = alert ?? action;
            return lowestLimit.HasValue && result <= lowestLimit.Value ? "PASS" : "UNASSESSED";
        }
        if (q == ">")
        {
            if (action.HasValue && result >= action.Value) return "FAIL";
            if (alert.HasValue && result >= alert.Value) return action.HasValue ? "UNASSESSED" : "ALERT";
            return "UNASSESSED";
        }
        if (q == ">=")
        {
            if (action.HasValue && result > action.Value) return "FAIL";
            if (alert.HasValue && result > alert.Value) return action.HasValue ? "UNASSESSED" : "ALERT";
            return "UNASSESSED";
        }
        if (q.Length != 0) return "UNASSESSED";
        return action.HasValue && result > action.Value ? "FAIL" :
            alert.HasValue && result > alert.Value ? "ALERT" : "PASS";
    }
}
