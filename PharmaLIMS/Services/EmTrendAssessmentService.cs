using System;
using System.Data;
using System.Globalization;

namespace PharmaLIMS.Services
{
    /// <summary>
    /// Read-only historical validation. Does not rewrite legacy rows or silently
    /// promote inconsistent stored evidence to a pass. Defects remain visible,
    /// with the original stored value and a separate recomputation for review.
    /// </summary>
    internal static class EmTrendAssessmentService
    {
        internal static void Apply(DataTable rows)
        {
            rows.Columns.Add("Result", typeof(decimal));
            rows.Columns.Add("RecalculatedResult", typeof(decimal));
            rows.Columns.Add("Assessment", typeof(string));
            rows.Columns.Add("ValueIntegrity", typeof(string));
            foreach (DataRow row in rows.Rows)
            {
                row["Result"] = DBNull.Value;
                row["RecalculatedResult"] = DBNull.Value;
                row["Assessment"] = "Not Assessed";
                row["ValueIntegrity"] = "Controlled review required";
                bool validCount = EmResultCalculator.TryReadStoredCount(row["TotalCount"], out int? count);
                decimal? stored = row["StoredResult"] == DBNull.Value ? null : Convert.ToDecimal(row["StoredResult"], CultureInfo.InvariantCulture);
                if (validCount && !count.HasValue && !stored.HasValue)
                {
                    row["Assessment"] = "Pending";
                    row["ValueIntegrity"] = "No result entered";
                    continue;
                }
                if (!validCount || !count.HasValue)
                {
                    row["ValueIntegrity"] = "Missing or invalid original count; excluded from numeric summaries";
                    continue;
                }
                if (Convert.ToInt32(row["EvidenceComplete"], CultureInfo.InvariantCulture) != 1)
                {
                    row["ValueIntegrity"] = "Missing or invalid frozen evidence; excluded from numeric summaries";
                    continue;
                }

                EmCalculatedResult computed = EmResultCalculator.Calculate(
                    count, Convert.ToString(row["Method"], CultureInfo.InvariantCulture),
                    row["AirVolumeLiters"] == DBNull.Value ? null : Convert.ToInt32(row["AirVolumeLiters"], CultureInfo.InvariantCulture),
                    row["AlertLimit"] == DBNull.Value ? null : Convert.ToDecimal(row["AlertLimit"], CultureInfo.InvariantCulture),
                    row["ActionLimit"] == DBNull.Value ? null : Convert.ToDecimal(row["ActionLimit"], CultureInfo.InvariantCulture));
                row["RecalculatedResult"] = (object?)computed.Value ?? DBNull.Value;
                if (!stored.HasValue || !computed.Value.HasValue || stored.Value != computed.Value.Value)
                {
                    row["ValueIntegrity"] = "Stored result differs from frozen-source calculation; controlled review required";
                    continue;
                }
                if (!string.Equals(Convert.ToString(row["StoredStatus"], CultureInfo.InvariantCulture)?.Trim(),
                    computed.Status, StringComparison.OrdinalIgnoreCase))
                {
                    row["ValueIntegrity"] = "Stored decision differs from frozen-source calculation; controlled review required";
                    continue;
                }
                if (row["ResultCalculationVersion"] != DBNull.Value &&
                    Convert.ToInt16(row["ResultCalculationVersion"]) != EmResultCalculator.CalculationVersion)
                {
                    row["ValueIntegrity"] = "Unsupported calculation version; controlled review required";
                    continue;
                }
                if (computed.Status == "Not Assessed") continue;

                row["Result"] = stored.Value;
                row["Assessment"] = computed.Status == "OOS" ? "Action" : computed.Status == "PASS" ? "Pass" : computed.Status;
                row["ValueIntegrity"] = row["ResultCalculationVersion"] == DBNull.Value
                    ? "Legacy stored evidence agrees with frozen-source calculation"
                    : "Verified calculation v1";
            }
        }
    }
}
