#nullable disable
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows.Controls;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using PharmaLIMS.Services;

namespace PharmaLIMS;

public partial class ReportsTrends
{
    private readonly List<PlotModel> _reportTrendModels = new();

    private void InvalidateTrendData()
    {
        currentDataTable = null;
        _reportTrendModels.Clear();
        if (TrendChart != null) TrendChart.Model = null;
        if (dgTrends != null) dgTrends.ItemsSource = null;
        if (cboReportCurve != null) cboReportCurve.ItemsSource = null;
        ResetStatusCounters();
    }

    private bool BuildCurrentTrendModels()
    {
        _reportTrendModels.Clear();
        if (currentDataTable == null || currentDataTable.Rows.Count == 0) { InvalidateTrendData(); return false; }
        // Every report panel has one point, test, method, unit and controlled population.
        foreach (var group in currentDataTable.Rows.Cast<DataRow>().GroupBy(row => TrendReportData.Population(row) + " / " + TrendReportData.Text(row, "PointCode"), StringComparer.OrdinalIgnoreCase))
        {
            DataTable data = currentDataTable.Clone(); foreach (DataRow row in group) data.ImportRow(row);
            _reportTrendModels.Add(BuildSnapshotTrendModel(data, group.Key));
        }
        cboReportCurve.ItemsSource = _reportTrendModels.Select(model => model.Title).ToArray();
        cboReportCurve.SelectedIndex = 0;
        TrendChart.Model = _reportTrendModels[0];
        SetStatus($"{currentDataTable.Rows.Count} records; {_reportTrendModels.Count} curve panel(s). PW/PTW separated scales: separate panels. All panels are included in PDF.", false);
        return true;
    }

    private void ReportCurve_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = cboReportCurve.SelectedIndex;
        if (index >= 0 && index < _reportTrendModels.Count) TrendChart.Model = _reportTrendModels[index];
    }

    private static bool TryReportDate(DataRow row, out DateTime date)
    {
        string column = row.Table.Columns.Contains("SamplingDateTime") ? "SamplingDateTime" : "SamplingDate";
        object raw = row[column];
        if (raw is DateTimeOffset offset) { date = offset.DateTime; return true; }
        if (raw is DateTime native) { date = native; return true; }
        return DateTime.TryParse(TrendReportData.Text(row, column), CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private PlotModel BuildSnapshotTrendModel(DataTable data, string title)
    {
        var model = new PlotModel { Title = title, TitleFontSize = 13, Background = OxyColors.White };
        string unit = TrendReportData.Text(data.Rows[0], "Unit");
        model.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, Title = "Sampling date", StringFormat = "dd-MMM-yyyy", MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = OxyColors.LightGray });
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = unit, MinimumPadding = .08, MaximumPadding = .15, MajorGridlineStyle = LineStyle.Solid, MajorGridlineColor = OxyColors.LightGray });
        var results = new LineSeries { Title = "Exact result", MarkerType = MarkerType.Circle, MarkerSize = 4, Color = OxyColors.SteelBlue };
        var boundaries = new ScatterSeries { Title = "Qualified boundary (not exact)", MarkerType = MarkerType.Diamond, MarkerFill = OxyColors.Purple, TrackerFormatString = "{0}\nDate: {2:yyyy-MM-dd}\nBoundary: {4}\nQualifier: {Tag}" };
        bool ph = IsPhReportTest(TrendReportData.Text(data.Rows[0], "TestName"));
        var alerts = new StairStepSeries { Title = ph ? "Lower specification (historical)" : "Alert (historical)", Color = OxyColors.DarkOrange, LineStyle = LineStyle.Dash };
        var actions = new StairStepSeries { Title = ph ? "Upper specification (historical)" : "Action / specification (historical)", Color = OxyColors.Red, LineStyle = LineStyle.Dash };
        var dated = data.Rows.Cast<DataRow>().Select(row => (Row: row, Valid: TryReportDate(row, out DateTime date), Date: date)).Where(item => item.Valid).OrderBy(item => item.Date).ToArray();
        foreach (var item in dated)
        {
            double x = DateTimeAxis.ToDouble(item.Date);
            string value = TrendReportData.ResultText(item.Row);
            if (TryGetTrendNumericValue(value, out double numeric, out _) && TrendReportData.Text(item.Row, "ResultQualifier").Length == 0)
                results.Points.Add(new DataPoint(x, numeric));
            else
            {
                results.Points.Add(new DataPoint(x, double.NaN));
                string boundary = value.TrimStart('<', '>', '≤', '≥', '=', ' ');
                if (TrendReportData.ExactNumber(boundary, out double number)) boundaries.Points.Add(new ScatterPoint(x, number, 5, double.NaN, value));
            }
            double? alert = TryGetNullableDouble(item.Row.Table.Columns.Contains("AlertLimit") ? item.Row["AlertLimit"] : DBNull.Value);
            double? action = null;
            if (item.Row.Table.Columns.Contains("ActionLimit"))
            {
                action = TryGetNullableDouble(item.Row["ActionLimit"]);
                if (!action.HasValue && TryExtractSpecificationUpperLimit(item.Row["ActionLimit"], out double upper)) action = upper;
            }
            alerts.Points.Add(new DataPoint(x, alert ?? double.NaN));
            actions.Points.Add(new DataPoint(x, action ?? double.NaN));
        }
        model.Series.Add(results); if (boundaries.Points.Count > 0) model.Series.Add(boundaries);
        if (dated.Length == 1)
        {
            foreach (var limit in new[] { alerts, actions })
            {
                double value = limit.Points[0].Y;
                limit.Points.Clear();
                limit.Points.Add(new DataPoint(DateTimeAxis.ToDouble(dated[0].Date.AddHours(-12)), value));
                limit.Points.Add(new DataPoint(DateTimeAxis.ToDouble(dated[0].Date.AddHours(12)), value));
            }
        }
        if (chkShowLimits?.IsChecked == true) { model.Series.Add(alerts); model.Series.Add(actions); }
        if (!results.Points.Any(point => double.IsFinite(point.Y))) model.Subtitle = "No exact numeric observations; see qualified boundaries and individual results.";
        if (dated.Length > 0 && dated[0].Date == dated[^1].Date)
        {
            model.Axes[0].Minimum = DateTimeAxis.ToDouble(dated[0].Date.AddHours(-12));
            model.Axes[0].Maximum = DateTimeAxis.ToDouble(dated[0].Date.AddHours(12));
        }
        model.Legends.Add(new OxyPlot.Legends.Legend { LegendPosition = OxyPlot.Legends.LegendPosition.BottomCenter, LegendPlacement = OxyPlot.Legends.LegendPlacement.Outside });
        return model;
    }

    private static byte[] ExportTrendModel(PlotModel model)
    {
        using var stream = new MemoryStream();
        new PngExporter { Width = 1600, Height = 700 }.Export(model, stream);
        return stream.ToArray();
    }

    private string CurrentReportScope()
    {
        if (externalTrendBatchId.HasValue)
        {
            var dates = currentDataTable.Rows.Cast<DataRow>().Select(row => (Valid: TryReportDate(row, out DateTime date), Date: date)).Where(item => item.Valid).Select(item => item.Date).ToArray();
            return dates.Length == 0 ? $"Approved import {externalTrendBatchId}" : $"{dates.Min():yyyy-MM-dd} to {dates.Max():yyyy-MM-dd} | Approved import {externalTrendBatchId} | {externalTrendParameter}";
        }
        return GetReportDateText(dpDateFrom) + " to " + GetReportDateText(dpDateTo) + " | " + GetSelectedCategory() + " | " + GetSelectedPointText();
    }
}
