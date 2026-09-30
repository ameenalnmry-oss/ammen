using System.Data;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using OxyPlot;
using OxyPlot.Series;
using OxyPlot.Wpf;
using PharmaLIMS;
using PharmaLIMS.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            TrendReportDataRegression.Verify();
            DataTable data = new();
            foreach (string column in new[] { "SampleNumber", "PointCode", "SamplingDate", "TestName", "MethodName", "ResultValue", "ResultQualifier", "Unit", "AlertLimit", "ActionLimit", "Status", "WaterProfile", "Location" }) data.Columns.Add(column);
            data.Rows.Add("MQC-PW-260001", "PWS-01", "2026-09-01 09:00", "TAMC", "", "5", "", "CFU/mL", "10", "20", "PASS", "PW", "Media preparation");
            data.Rows.Add("MQC-PW-260002", "PWS-01", "2026-09-02 09:00", "TAMC", "", "25", "", "CFU/mL", "30", "50", "PASS", "PW", "Media preparation");
            data.Rows.Add("MQC-PW-260003", "PWS-01", "2026-09-03 09:00", "TAMC", "", "10", "<", "CFU/mL", "30", "50", "PASS", "PW", "Media preparation");
            data.Rows.Add("MQC-PW-260004", "PWS-01", "2026-09-04 09:00", "TAMC", "", "", "", "CFU/mL", "", "", "Pending", "PW", "Media preparation");
            data.Rows.Add("MQC-PTW-260001", "PTWS-01", "2026-09-01 10:00", "TAMC", "", "100", "", "CFU/mL", "250", "500", "PASS", "PTW", "Bore well");
            data.Rows.Add("EXT-EM-001", "ROOM-01", "2026-09-01 11:00", "TAMC", "Settle", ">25", "", "CFU/plate", "10", "20", "FAIL", "", "Granulation");
            for (int i = 0; i < 75; i++) data.Rows.Add("ROW-" + i.ToString("D3"), "PWS-01", "2026-09-05 09:00", "TAMC", "", i.ToString(), "", "CFU/mL", "30", "50", i > 50 ? "FAIL" : "PASS", "PW", "Fixture");
            data.Rows.Add("LAST-RESULT-VERIFIED", "PWS-01", "2026-09-06 09:00", "TAMC", "", "7", "", "CFU/mL", "30", "50", "PASS", "PW", new string('x', 2000));

            var window = new ReportsTrends(); // Not shown: no Loaded handler, database query or workflow mutation.
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(ReportsTrends).GetField("currentDataTable", flags)!.SetValue(window, data);
            ((CheckBox)typeof(ReportsTrends).GetField("chkShowLimits", flags)!.GetValue(window)!).IsChecked = true;
            Require((bool)typeof(ReportsTrends).GetMethod("BuildCurrentTrendModels", flags)!.Invoke(window, null)!, "curve generation");
            var models = (List<PlotModel>)typeof(ReportsTrends).GetField("_reportTrendModels", flags)!.GetValue(window)!;
            Require(models.Count == 3, "separate PW/PTW/method/unit panels");
            PlotModel pw = models.Single(model => model.Title.Contains("PWS-01") && !model.Title.Contains("PTWS"));
            var historical = pw.Series.OfType<StairStepSeries>().First();
            Require(historical.Points[0].Y == 10 && historical.Points[1].Y == 30, "each frozen historical limit must be plotted");
            Require(historical.Points.Any(point => double.IsNaN(point.Y)), "missing evidence breaks the limit line");
            Require(pw.Series.OfType<ScatterSeries>().Single().Points.Count == 1, "qualified value is a boundary marker");
            string display = (string)typeof(ReportsTrends).GetMethod("GetPdfTableValue", flags)!.Invoke(window, new object[] { data.Rows[2], "ResultValue" })!;
            Require(display == "<10", "actual WPF PDF value helper preserves qualifier");
            string directory = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "PharmaLIMS-TrendReport-Smoke");
            Directory.CreateDirectory(directory);
            var charts = models.Select(model =>
            {
                using var stream = new MemoryStream(); new PngExporter { Width = 1600, Height = 700 }.Export(model, stream);
                return new TrendReportChart(model.Title, stream.ToArray());
            }).ToArray();
            TrendPdfReportWriter.Write(Path.Combine(directory, "MEDICA_Trend_Regression_Preview.pdf"), "REPORTS AND TRENDS ANALYSIS - VALIDATION FIXTURE", "TREND-TEST-001", "2026-09-01 to 2026-09-06", "Validation fixture", new DateTime(2026, 9, 30, 12, 0, 0),
                Path.Combine(AppContext.BaseDirectory, "medica-logo.png"), TrendReportData.Statistics(data), charts,
                new[] { new TrendReportTable("Full individual results", data, new[] { "SampleNumber", "PointCode", "WaterProfile", "SamplingDate", "TestName", "MethodName", "ResultValue", "Unit", "AlertLimit", "ActionLimit", "Status", "Location" }) }, "Validation fixture only. All source rows and curve panels must appear. This report contains no live laboratory records.");
            window.Close();
            Console.WriteLine($"PASS Trend WPF grouping / frozen limits / qualifiers / complete PDF smoke; {data.Rows.Count} fixture rows, {charts.Length} charts. PDF={directory}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Trend report smoke: " + message); }
}
