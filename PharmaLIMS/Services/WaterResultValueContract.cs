using System;
using System.Globalization;

namespace PharmaLIMS.Services
{
    /// <summary>Lossless numeric display and the existing water write-parameter contract.</summary>
    internal static class WaterResultValueContract
    {
        // The baseline has a text ResultValue column, and some existing databases
        // have a decimal column. The writer uses decimal(18,4) in either case.
        private const decimal MaximumStoredMagnitude = 99999999999999.9999m;

        internal static string FormatNumeric(decimal value) =>
            value.ToString("0.############################", CultureInfo.InvariantCulture);

        // Do not round a newly entered measurement silently. Unchanged historical
        // values are not passed to the writer, so remarks-only edits preserve them.
        internal static bool IsExactlyRepresentable(decimal value) =>
            value >= -MaximumStoredMagnitude && value <= MaximumStoredMagnitude &&
            decimal.Round(value, 4, MidpointRounding.AwayFromZero) == value;

        internal static bool StoredValueMatches(decimal expected, object? stored) =>
            stored != null && stored != DBNull.Value &&
            decimal.TryParse(Convert.ToString(stored, CultureInfo.InvariantCulture),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal actual) && actual == expected;
    }
}
