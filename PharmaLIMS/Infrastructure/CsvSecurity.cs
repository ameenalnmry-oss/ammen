using System.Globalization;

namespace PharmaLIMS.Infrastructure
{
    internal static class CsvSecurity
    {
        internal static string Escape(object? value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (value is string && IsFormulaLike(text))
                text = "'" + text;

            if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
                return "\"" + text.Replace("\"", "\"\"") + "\"";

            return text;
        }

        private static bool IsFormulaLike(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string trimmed = value.TrimStart(' ', '\t', '\r', '\n');
            if (trimmed.Length == 0)
                return false;

            char first = trimmed[0];
            if (first is '=' or '@')
                return true;

            if (first is '+' or '-')
                return !decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

            return value[0] is '\t' or '\r' or '\n';
        }
    }
}
