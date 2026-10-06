using System.Data;
using System.Globalization;

namespace PharmaLIMS.Services;

/// <summary>Read-only report calculations. Units, methods and source populations never share statistics.</summary>
internal static class TrendReportData
{
    internal static string Text(DataRow row, string column) => row.Table.Columns.Contains(column)
        ? Convert.ToString(row[column], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty : string.Empty;

    internal static bool ExactNumber(object? raw, out double value)
    {
        string text = Convert.ToString(raw, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        value = 0d;
        if (!ControlledNumericValue.TryParse(text, out decimal parsed)) return false;
        value = (double)parsed;
        return double.IsFinite(value);
    }

    internal static string Population(DataRow row) => string.Join(" / ", new[]
    { Text(row, "WaterProfile"), Text(row, "AreaClassification"), Text(row, "TestName"), Text(row, "MethodName"), Text(row, "Unit") }
        .Where(value => value.Length > 0));

    internal static string ResultText(DataRow row)
    {
        string raw = Text(row, "ResultValue");
        string qualifier = Text(row, "ResultQualifier");
        if (raw.Length == 0) return "NR";
        return qualifier.Length > 0 && !raw.StartsWith(qualifier, StringComparison.Ordinal)
            ? qualifier + raw : raw;
    }

    internal static string WaterStatus(string stored, string testName, double value, double? alert, double? action)
    {
        if (!double.IsFinite(value) || alert.HasValue && !double.IsFinite(alert.Value) ||
            action.HasValue && !double.IsFinite(action.Value)) return "NOT ASSESSED";
        try
        {
            return WaterStatusExact(stored, testName, Convert.ToDecimal(value),
                alert.HasValue ? Convert.ToDecimal(alert.Value) : null,
                action.HasValue ? Convert.ToDecimal(action.Value) : null);
        }
        catch (OverflowException) { return "NOT ASSESSED"; }
    }

    internal static string WaterStatusExact(string stored, string testName, decimal value, decimal? alert, decimal? action)
    {
        string status = WaterNumericResultEvaluator.Evaluate(testName, value, alert, action);
        if (status == "OOS") return "FAIL";
        if (status == "Invalid") return "NOT ASSESSED";
        if (status == "NOT ASSESSED" && (stored.Trim().ToUpperInvariant() is "FAIL" or "OOS" or "ACTION"))
            return "FAIL";
        return status;
    }

    internal static IReadOnlyList<string> Statistics(DataTable? data)
    {
        if (data == null || data.Rows.Count == 0) return new[] { "No observations available." };
        List<string> lines = new();
        foreach (var group in data.Rows.Cast<DataRow>().GroupBy(Population, StringComparer.OrdinalIgnoreCase))
        {
            List<double> values = new();
            int pending = 0, qualified = 0, unassessed = 0;
            foreach (DataRow row in group)
            {
                string status = Text(row, "Status");
                if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase)) { pending++; continue; }
                if (status.Equals("NOT ASSESSED", StringComparison.OrdinalIgnoreCase)) unassessed++;
                string result = ResultText(row);
                if (Text(row, "ResultQualifier").Length > 0 || result.StartsWith('<') || result.StartsWith('>') || result.StartsWith('≤') || result.StartsWith('≥'))
                { qualified++; continue; }
                if (ExactNumber(result, out double value)) values.Add(value);
            }
            string context = string.IsNullOrWhiteSpace(group.Key) ? "Unspecified population" : group.Key;
            string completeness = $"Records {group.Count()} | Pending {pending} | Not assessed {unassessed} (exact measurements included, conformity not implied) | Qualified {qualified} (excluded from exact statistics)";
            if (values.Count == 0) { lines.Add(context + ": " + completeness + " | No exact numeric observations."); continue; }
            double mean = values.Average();
            string sd = values.Count > 1 ? Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Count - 1)).ToString("0.###", CultureInfo.InvariantCulture) : "NR (n < 2)";
            lines.Add(FormattableString.Invariant($"{context}: {completeness} | Exact n {values.Count} | Mean {mean:0.###} | Min {values.Min():0.###} | Max {values.Max():0.###} | SD {sd}"));
        }
        return lines;
    }
}
