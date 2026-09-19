using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PharmaLIMS.Services
{
    internal readonly record struct EmCalculatedResult(decimal? Value, string Status);

    /// <summary>
    /// Version 1: store decimal(28,12), round only the stored/displayed quotient.
    /// NMT decisions compare count * 1000 against limit * frozen litres, not a
    /// rounded string. This preserves strict excursions arbitrarily near a limit.
    /// </summary>
    internal static class EmResultCalculator
    {
        internal const byte Precision = 28;
        internal const byte Scale = 12;
        internal const short CalculationVersion = 1;

        internal static bool IsActiveAirSampling(string? method) =>
            string.Equals(method?.Trim(), "Active Air Sampling", StringComparison.OrdinalIgnoreCase);

        internal static bool IsDirectCountMethod(string? method) =>
            new[] { "Settle Plate", "Contact Plate", "Surface Swab", "Personnel Monitoring" }
                .Any(value => string.Equals(method?.Trim(), value, StringComparison.OrdinalIgnoreCase));

        internal static bool TryReadStoredCount(object? raw, out int? count)
        {
            count = null;
            if (raw == null || raw == DBNull.Value) return true;
            try
            {
                decimal value = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                if (value < 0m || value > int.MaxValue || decimal.Truncate(value) != value)
                    return false;
                count = decimal.ToInt32(value);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        internal static int? ReadStoredCount(object? raw)
        {
            if (TryReadStoredCount(raw, out int? count)) return count;
            throw new InvalidOperationException(
                "The stored EM count is fractional, negative, invalid, or outside the supported range. Controlled review is required; it will not be rounded.");
        }

        internal static decimal? CalculateValue(int? count, string? method, int? frozenLitres)
        {
            if (!count.HasValue) return null;
            if (count.Value < 0) throw new ArgumentOutOfRangeException(nameof(count), "Total count cannot be negative.");
            if (IsDirectCountMethod(method)) return count.Value;
            if (!IsActiveAirSampling(method) || !frozenLitres.HasValue || frozenLitres.Value <= 0)
                return null;
            return decimal.Round(count.Value * 1000m / frozenLitres.Value, Scale, MidpointRounding.ToEven);
        }

        internal static EmCalculatedResult Calculate(
            int? count, string? method, int? frozenLitres, decimal? alert, decimal? action)
        {
            decimal? storedValue = CalculateValue(count, method, frozenLitres);
            if (!count.HasValue) return new EmCalculatedResult(null, "Pending");
            if (!storedValue.HasValue || !alert.HasValue || !action.HasValue ||
                alert.Value < 0 || action.Value < alert.Value)
                return new EmCalculatedResult(storedValue, "Not Assessed");

            decimal numerator = IsActiveAirSampling(method) ? count.Value * 1000m : count.Value;
            decimal denominator = IsActiveAirSampling(method) ? frozenLitres!.Value : 1m;
            string status = numerator > action.Value * denominator ? "OOS"
                : numerator > alert.Value * denominator ? "Alert" : "PASS";
            return new EmCalculatedResult(storedValue, status);
        }

        internal static string Format(decimal? value) =>
            value?.ToString("0.############", CultureInfo.InvariantCulture) ?? string.Empty;

        internal static string Aggregate(IEnumerable<string> statuses)
        {
            string[] values = statuses.ToArray();
            if (values.Any(s => s == "Not Assessed"))
                throw new InvalidOperationException("EM evidence is incomplete or invalid. No result changes were committed.");
            if (values.Contains("OOS")) return "OOS";
            if (values.Contains("Alert")) return "Alert";
            if (values.Length == 0 || values.All(s => s == "Pending")) return "Pending";
            if (values.Contains("Pending")) return "Partially Entered";
            return "Results Entered";
        }

        // The save path re-reads the complete set before updating event state.
        // Verify storage as well as recomputation, rather than masking a truncated
        // ResultCFU with a correctly recalculated summary.
        internal static bool StoredEvidenceMatches(
            EmCalculatedResult expected, object? storedValue, object? storedStatus, object? version)
        {
            bool isNull = storedValue == null || storedValue == DBNull.Value;
            if (expected.Value.HasValue)
            {
                if (isNull || !decimal.TryParse(Convert.ToString(storedValue, CultureInfo.InvariantCulture),
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out decimal actual) || actual != expected.Value.Value)
                    return false;
            }
            else if (!isNull) return false;

            return string.Equals(Convert.ToString(storedStatus, CultureInfo.InvariantCulture)?.Trim(),
                       expected.Status, StringComparison.OrdinalIgnoreCase) &&
                   version != null && version != DBNull.Value &&
                   short.TryParse(Convert.ToString(version, CultureInfo.InvariantCulture),
                       NumberStyles.Integer, CultureInfo.InvariantCulture, out short actualVersion) &&
                   actualVersion == CalculationVersion;
        }
    }
}
