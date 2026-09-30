using System.Data;
using PharmaLIMS.Services;

internal static class TrendReportDataRegression
{
    internal static void Verify()
    {
        static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Trend regression: " + message); }
        DataTable data = new();
        foreach (string column in new[] { "ResultValue", "ResultQualifier", "Status", "TestName", "MethodName", "Unit", "WaterProfile", "AreaClassification" }) data.Columns.Add(column);
        DataRow a = data.Rows.Add("10", "", "PASS", "TAMC", "Settle", "CFU/plate", "", "ISO 8");
        data.Rows.Add("100", "", "PASS", "TAMC", "Active Air", "CFU/m3", "", "ISO 8");
        DataRow qualified = data.Rows.Add("10", "<", "PASS", "TAMC", "Settle", "CFU/plate", "", "ISO 8");
        data.Rows.Add("", "", "Pending", "TAMC", "Settle", "CFU/plate", "", "ISO 8");
        var lines = TrendReportData.Statistics(data);
        Require(lines.Count == 2 && !lines.Any(line => line.Contains("Mean 55")), "incompatible methods/units must not share statistics");
        Require(lines.Any(line => line.Contains("Qualified 1") && line.Contains("Pending 1") && line.Contains("Exact n 1")), "qualified/pending observations remain visible and excluded from exact statistics");
        Require(TrendReportData.ResultText(qualified) == "<10", "qualifier must remain visible in the results table");
        a["ResultValue"] = ">25"; Require(TrendReportData.ResultText(a) == ">25", "embedded qualifier must remain visible");
        Require(!TrendReportData.ExactNumber("<10", out _) && !TrendReportData.ExactNumber("Absent/100 mL", out _) && !TrendReportData.ExactNumber("NaN", out _), "qualitative, qualified and nonfinite values are not exact results");
        Require(TrendReportData.ExactNumber("1.25", out double exact) && exact == 1.25, "exact decimal parsing");
        Require(TrendReportData.WaterStatus("PASS", "pH", 9, 5, null) == "NOT ASSESSED", "incomplete pH range cannot pass");
        Require(TrendReportData.WaterStatus("PASS", "pH", 9, 5, 7) == "FAIL", "upper-range failure");
        Require(TrendReportData.WaterStatus("", "pH", 5, 5, 7) == "PASS", "range equality remains inclusive");
        Require(TrendReportData.WaterStatus("PASS", "TAMC", 10, null, null) == "NOT ASSESSED", "missing controlled limits cannot produce PASS");
        Require(TrendReportData.WaterStatus("", "TAMC", 20, 10, 20) == "ALERT", "exact NMT action equality is not failure");
        Require(TrendReportData.WaterStatus("", "TAMC", 21, 10, 20) == "FAIL", "action excursion");
        Console.WriteLine("PASS Trend report unit/method separation, qualifiers, pending evidence and pH completeness.");
    }
}
