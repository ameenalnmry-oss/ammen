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
        return double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
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
        string name = testName.Trim().ToLowerInvariant();
        bool ph = name == "ph" || name == "p.h" || name.Contains("ph value") || name.Contains("ph test") || name.StartsWith("ph ") || name.EndsWith(" ph");
        if (ph)
        {
            if (alert.HasValue && value < alert.Value || action.HasValue && value > action.Value) return "FAIL";
            return alert.HasValue && action.HasValue ? "PASS" : "NOT ASSESSED";
        }
        if (action.HasValue && value > action.Value) return "FAIL";
        if (alert.HasValue && value > alert.Value) return "ALERT";
        if (action.HasValue) return "PASS";
        // A historical failure stays visible even if the numeric specification is absent.
        return stored.Trim().ToUpperInvariant() is "FAIL" or "OOS" or "ACTION" ? "FAIL" : "NOT ASSESSED";
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
            string completeness = $"Records {group.Count()} | Pending {pending} | Not assessed {unassessed} | Qualified {qualified} (excluded from exact statistics)";
            if (values.Count == 0) { lines.Add(context + ": " + completeness + " | No exact numeric observations."); continue; }
            double mean = values.Average();
            string sd = values.Count > 1 ? Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / (values.Count - 1)).ToString("0.###", CultureInfo.InvariantCulture) : "NR (n < 2)";
            lines.Add(FormattableString.Invariant($"{context}: {completeness} | Exact n {values.Count} | Mean {mean:0.###} | Min {values.Min():0.###} | Max {values.Max():0.###} | SD {sd}"));
        }
        return lines;
    }
}
