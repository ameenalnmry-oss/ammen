using System.Data;
using System.Globalization;
namespace PharmaLIMS.Services;
internal static class EmReportEvidenceGuard
{
    internal static void EnsureConsistent(DataTable rows)
    {
        if(rows.Rows.Count==0) throw new InvalidOperationException("No EM plates exist for this report.");
        if(!rows.Columns.Contains("FinalResult")) throw new InvalidOperationException("The stored EM event interpretation is missing.");
        var statuses = new System.Collections.Generic.List<string>();
        foreach(DataRow row in rows.Rows)
        {
            int? count=EmResultCalculator.ReadStoredCount(row["TotalCount"]);string method=Convert.ToString(row["Method"],CultureInfo.InvariantCulture) ?? "";
            if(!count.HasValue || Convert.ToInt32(row["EvidenceComplete"],CultureInfo.InvariantCulture)!=1 || string.IsNullOrWhiteSpace(Convert.ToString(row["ResultUnitSnapshot"],CultureInfo.InvariantCulture)))
                throw new InvalidOperationException("Approved EM report evidence is incomplete. Controlled QA correction is required.");
            decimal? alert=row["AlertLimitSnapshot"]==DBNull.Value?null:Convert.ToDecimal(row["AlertLimitSnapshot"],CultureInfo.InvariantCulture);
            decimal? action=row["ActionLimitSnapshot"]==DBNull.Value?null:Convert.ToDecimal(row["ActionLimitSnapshot"],CultureInfo.InvariantCulture);
            int? volume=row["AirVolumeLitersSnapshot"]==DBNull.Value?null:Convert.ToInt32(row["AirVolumeLitersSnapshot"],CultureInfo.InvariantCulture);
            var expected=EmResultCalculator.Calculate(count,method,volume,alert,action);
            object version=row["ResultCalculationVersion"]==DBNull.Value?EmResultCalculator.CalculationVersion:row["ResultCalculationVersion"];
            if(!expected.Value.HasValue || expected.Status is "Pending" or "Not Assessed" || !EmResultCalculator.StoredEvidenceMatches(expected,row["ResultCFU"],row["Status"],version))
                throw new InvalidOperationException("Stored EM result/interpretation disagrees with its frozen count, limits or volume. Printing is blocked pending controlled QA correction.");
            statuses.Add(expected.Status);
        }
        string aggregate = EmResultCalculator.Aggregate(statuses);
        foreach(DataRow row in rows.Rows)
        {
            string stored = (Convert.ToString(row["FinalResult"],CultureInfo.InvariantCulture) ?? "").Trim();
            if(stored.Equals("Approved",StringComparison.OrdinalIgnoreCase) || stored.Equals("Completed",StringComparison.OrdinalIgnoreCase)) continue;
            if(stored.Equals("PASS",StringComparison.OrdinalIgnoreCase)) stored = "Results Entered";
            if(!stored.Equals(aggregate,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The stored EM event interpretation disagrees with its plate evidence. Printing is blocked pending controlled QA correction.");
        }
    }
}
