using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PharmaLIMS
{
    public partial class EMTrendReport : Window
    {
        private bool _isLoadingTrend;
        private DataTable _details = new();
        private DataTable _summary = new();
        private DateTime _periodStart;
        private DateTime _periodEndExclusive;
        private bool _approvedOnly = true;

        public EMTrendReport()
        {
            InitializeComponent();
            try
            {
                DateTime databaseToday = DatabaseHelper.GetAuthoritativeDatabaseTime().Date;
                dpFrom.SelectedDate = databaseToday.AddMonths(-6).AddDays(1);
                dpTo.SelectedDate = databaseToday;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning(
                    "Authoritative database time was unavailable while initializing the EM trend date filters. Workstation time was not substituted.",
                    ex);
                dpFrom.SelectedDate = null;
                dpTo.SelectedDate = null;
                lblStatus.Text = "Database clock unavailable. Restore database connectivity before loading the EM trend.";
            }

            Loaded += async (_, _) =>
            {
                if (dpFrom.SelectedDate.HasValue && dpTo.SelectedDate.HasValue)
                    await LoadTrendAsync();
            };
        }

        private async void Load_Click(object sender, RoutedEventArgs e) => await LoadTrendAsync();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private async Task LoadTrendAsync()
        {
            if (_isLoadingTrend) return;
            _isLoadingTrend = true;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                if (dpFrom.SelectedDate is not DateTime selectedFrom || dpTo.SelectedDate is not DateTime selectedTo)
                    throw new InvalidOperationException("Select both From and To dates.");
                DateTime start = selectedFrom.Date;
                DateTime end = selectedTo.Date.AddDays(1);
                if (end <= start)
                    throw new InvalidOperationException("The To date cannot be earlier than the From date.");
                bool approvedOnly = chkApprovedOnly.IsChecked == true;
                lblStatus.Text = "Checking EM trend schema readiness...";
                await EnsureTrendSnapshotSchemaReadyAsync();
                lblStatus.Text = "Loading controlled environmental-monitoring observations...";

                string detailsSql = @"
SELECT
    e.EventNo,
    CAST(e.EventDate AS date) AS EventDate,
    COALESCE(NULLIF(LTRIM(RTRIM(e.AreaCodeSnapshot)),N''),a.AreaCode,N'AREA-'+CONVERT(nvarchar(20),e.AreaId)) AS AreaCode,
    COALESCE(NULLIF(LTRIM(RTRIM(e.AreaNameSnapshot)),N''),a.AreaName,N'Historical area') AS AreaName,
    COALESCE(NULLIF(LTRIM(RTRIM(e.GradeSnapshot)),N''),a.Grade,N'Unspecified') AS Grade,
    ISNULL(NULLIF(LTRIM(RTRIM(e.AreaSnapshotSource)),N''),N'Legacy context unavailable') AS AreaContextSource,
    P.Method, P.PlateCode, P.TotalCount,
    TRY_CONVERT(decimal(28,12),P.ResultCFU) AS StoredResult,
    P.Status AS StoredStatus, P.ResultCalculationVersion,
    TRY_CONVERT(decimal(18,3),P.FungalCount) AS FungalCount,
    EVID.ResultUnitSnapshot AS Unit,
    EVID.AlertLimitSnapshot AS AlertLimit,
    EVID.ActionLimitSnapshot AS ActionLimit,
    EVID.AirVolumeLitersSnapshot AS AirVolumeLiters,
    EVID.ReconciliationID, EVID.EvidenceSource, EVID.EvidenceComplete,
    ISNULL(e.WorkflowStatus,N'') AS WorkflowStatus,
    ISNULL(e.FinalResult,N'') AS FinalResult,
    ISNULL(P.ColoniesObserved,N'') AS Remarks
FROM dbo.EM_EventPlates P
INNER JOIN dbo.EM_Events e ON e.Id=P.EventId
LEFT JOIN dbo.EM_Areas a ON a.Id=e.AreaId
" + EmLimitEvidenceSql.Joins() + @"
WHERE e.EventDate>=@StartDate AND e.EventDate<@EndDate
  AND (@ApprovedOnly=0 OR UPPER(LTRIM(RTRIM(ISNULL(e.WorkflowStatus,N''))))=N'APPROVED')
ORDER BY AreaCode,P.Method,e.EventDate,P.SequenceNo,P.PlateCode;";

                (DataTable details, DataTable summary, DataTable previousPeriod, DataTable previousYear) = await Task.Run(() =>
                {
                    TimeSpan selectedDuration = end - start;
                    DateTime previousPeriodStart = start - selectedDuration;
                    DateTime historyStart = new[] { previousPeriodStart, start.AddYears(-1) }.Min();
                    DataTable loaded = DatabaseHelper.ExecuteQuery(detailsSql, new[]
                    {
                        new SqlParameter("@StartDate", SqlDbType.DateTime2) { Value = historyStart },
                        new SqlParameter("@EndDate", SqlDbType.DateTime2) { Value = end },
                        new SqlParameter("@ApprovedOnly", SqlDbType.Bit) { Value = approvedOnly }
                    }, commandTimeoutSeconds: 20);
                    EmTrendAssessmentService.Apply(loaded);
                    DataTable current = SliceDetails(loaded, start, end);
                    DataTable prior = SliceDetails(loaded, previousPeriodStart, start);
                    DataTable yearAgo = SliceDetails(loaded, start.AddYears(-1), end.AddYears(-1));
                    return (current, BuildSummary(current), prior, yearAgo);
                });

                _periodStart = start;
                _periodEndExclusive = end;
                _approvedOnly = approvedOnly;
                _details = details;
                _summary = summary;
                gridDetails.ItemsSource = details.DefaultView;
                gridSummary.ItemsSource = summary.DefaultView;
                txtNarrative.Text = BuildNarrative(details, summary, previousPeriod, previousYear, start, end) +
                    "\n\nEvidence integrity: Result is included in numeric summaries only when the stored value and decision agree with the original count and effective frozen limits. Inconsistent legacy rows remain visible as Not Assessed, with StoredResult, RecalculatedResult, EvidenceSource and ReconciliationID; no historical record is rewritten.";
                if (summary.Rows.Count > 0)
                    gridSummary.SelectedIndex = 0;
                else
                    TrendChart.Model = null;

                int numeric = details.Rows.Cast<DataRow>().Count(r => r["Result"] != DBNull.Value);
                int pending = details.Rows.Cast<DataRow>().Count(r => IsAssessment(r, "Pending"));
                int unassessed = details.Rows.Cast<DataRow>().Count(r => IsAssessment(r, "Not Assessed"));
                lblStatus.Text = $"{start:yyyy-MM-dd} to {end.AddDays(-1):yyyy-MM-dd} | {details.Rows.Count} records | {numeric} numeric | {pending} pending | {unassessed} not assessed | {summary.Rows.Count} area/method groups";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to load the six-month EM trend.", ex);
                MessageBox.Show("Unable to load the EM trend: " + UserFacingError.SafeMessage(ex), "EM Trend", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _isLoadingTrend = false;
            }
        }

        private static async Task EnsureTrendSnapshotSchemaReadyAsync()
        {
            DataTable readiness = await Task.Run(() => DatabaseHelper.ExecuteQuery(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.EM_Events',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_EventPlates',N'ResultCalculationVersion') IS NOT NULL
    AND OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NOT NULL
    AND EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
               AND name=N'ResultCFU' AND precision=28 AND scale=12)
THEN 1 ELSE 0 END AS IsReady;", commandTimeoutSeconds: 5));

            bool ready = readiness.Rows.Count > 0
                && Convert.ToInt32(readiness.Rows[0]["IsReady"], CultureInfo.InvariantCulture) == 1;
            if (ready)
                return;

            throw new InvalidOperationException(
                "EM trend historical-context columns are missing from the configured database. " +
                "In Development, close workflow windows and run Database Maintenance, then reopen EM Trend. " +
                "For Production, apply the controlled deployment migrations before enabling workflows.");
        }

        private static DataTable SliceDetails(DataTable source, DateTime fromInclusive, DateTime toExclusive)
        {
            DataTable result = source.Clone();
            foreach (DataRow row in source.Rows)
            {
                DateTime eventDate = Convert.ToDateTime(row["EventDate"], CultureInfo.InvariantCulture);
                if (eventDate >= fromInclusive.Date && eventDate < toExclusive.Date)
                    result.ImportRow(row);
            }
            return result;
        }

        private void GridSummary_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (gridSummary.SelectedItem is DataRowView selected)
                BuildTrendChart(selected.Row);
        }

        private void BuildTrendChart(DataRow selectedSummary)
        {
            string areaCode = Convert.ToString(selectedSummary["AreaCode"], CultureInfo.InvariantCulture) ?? string.Empty;
            string method = Convert.ToString(selectedSummary["Method"], CultureInfo.InvariantCulture) ?? string.Empty;
            string unit = Convert.ToString(selectedSummary["Unit"], CultureInfo.InvariantCulture) ?? string.Empty;
            List<DataRow> rows = _details.Rows.Cast<DataRow>()
                .Where(row => string.Equals(Convert.ToString(row["AreaCode"], CultureInfo.InvariantCulture), areaCode, StringComparison.OrdinalIgnoreCase))
                .Where(row => string.Equals(Convert.ToString(row["Method"], CultureInfo.InvariantCulture), method, StringComparison.OrdinalIgnoreCase))
                .Where(row => string.Equals(Convert.ToString(row["Unit"], CultureInfo.InvariantCulture), unit, StringComparison.OrdinalIgnoreCase))
                .Where(row => row["Result"] != DBNull.Value)
                .OrderBy(row => Convert.ToDateTime(row["EventDate"], CultureInfo.InvariantCulture))
                .ToList();

            var model = new PlotModel { Title = $"{areaCode} — {method} ({unit})", Background = OxyColors.White };
            model.Axes.Add(new DateTimeAxis { Position = AxisPosition.Bottom, Title = "Monitoring date", StringFormat = "dd-MMM-yyyy" });
            model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = unit, MinimumPadding = 0.08, MaximumPadding = 0.15 });
            var total = new LineSeries { Title = "Total count", MarkerType = MarkerType.Circle, StrokeThickness = 2 };
            var fungal = new LineSeries { Title = "Fungal count", MarkerType = MarkerType.Square, StrokeThickness = 1.5 };
            var alert = new LineSeries { Title = "Alert limit (historical)", LineStyle = LineStyle.Dash, StrokeThickness = 1.4 };
            var action = new LineSeries { Title = "Action limit (historical)", LineStyle = LineStyle.Dot, StrokeThickness = 1.4 };
            foreach (DataRow row in rows)
            {
                DateTime date = Convert.ToDateTime(row["EventDate"], CultureInfo.InvariantCulture);
                double x = DateTimeAxis.ToDouble(date);
                total.Points.Add(new DataPoint(x, Convert.ToDouble(row["Result"], CultureInfo.InvariantCulture)));
                if (row["FungalCount"] != DBNull.Value)
                    fungal.Points.Add(new DataPoint(x, Convert.ToDouble(row["FungalCount"], CultureInfo.InvariantCulture)));
                if (row["AlertLimit"] != DBNull.Value)
                    alert.Points.Add(new DataPoint(x, Convert.ToDouble(row["AlertLimit"], CultureInfo.InvariantCulture)));
                if (row["ActionLimit"] != DBNull.Value)
                    action.Points.Add(new DataPoint(x, Convert.ToDouble(row["ActionLimit"], CultureInfo.InvariantCulture)));
            }
            model.Series.Add(total);
            if (fungal.Points.Count > 0) model.Series.Add(fungal);
            if (alert.Points.Count > 0) model.Series.Add(alert);
            if (action.Points.Count > 0) model.Series.Add(action);
            TrendChart.Model = model;
        }

        private static bool IsAssessment(DataRow row, string expected) =>
            string.Equals(Convert.ToString(row["Assessment"], CultureInfo.InvariantCulture), expected, StringComparison.OrdinalIgnoreCase);

        private static DataTable BuildSummary(DataTable details)
        {
            DataTable summary = new();
            foreach ((string Name, Type Type) col in new[]
            {
                ("AreaCode", typeof(string)), ("AreaName", typeof(string)), ("Grade", typeof(string)), ("Method", typeof(string)),
                ("Unit", typeof(string)), ("Records", typeof(int)), ("NumericObservations", typeof(int)), ("Pending", typeof(int)),
                ("Minimum", typeof(decimal)), ("Mean", typeof(decimal)), ("Maximum", typeof(decimal)),
                ("FungalObservations", typeof(int)), ("FungalMinimum", typeof(decimal)), ("FungalMaximum", typeof(decimal)),
                ("AlertLimit", typeof(decimal)), ("ActionLimit", typeof(decimal)), ("LimitState", typeof(string)),
                ("Alerts", typeof(int)), ("Actions", typeof(int)), ("NotAssessed", typeof(int)), ("HistoricalContext", typeof(string))
            }) summary.Columns.Add(col.Name, col.Type);

            var groups = details.Rows.Cast<DataRow>().GroupBy(r => string.Join("|", r["AreaCode"], r["Method"], r["Unit"]), StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                List<DataRow> rows = group.ToList();
                List<decimal> numeric = rows.Where(r => r["Result"] != DBNull.Value).Select(r => Convert.ToDecimal(r["Result"], CultureInfo.InvariantCulture)).ToList();
                List<decimal> fungal = rows.Where(r => r["FungalCount"] != DBNull.Value).Select(r => Convert.ToDecimal(r["FungalCount"], CultureInfo.InvariantCulture)).ToList();
                List<decimal> alerts = rows.Where(r => r["AlertLimit"] != DBNull.Value).Select(r => Convert.ToDecimal(r["AlertLimit"], CultureInfo.InvariantCulture)).Distinct().ToList();
                List<decimal> actions = rows.Where(r => r["ActionLimit"] != DBNull.Value).Select(r => Convert.ToDecimal(r["ActionLimit"], CultureInfo.InvariantCulture)).Distinct().ToList();
                bool missingLimit = rows.Any(r => r["AlertLimit"] == DBNull.Value || r["ActionLimit"] == DBNull.Value);
                string limitState = missingLimit ? (alerts.Count > 1 || actions.Count > 1 ? "Mixed / missing" : "Missing snapshot") : (alerts.Count == 1 && actions.Count == 1 ? "Complete" : "Varied within period");
                string context = rows.Any(r => (Convert.ToString(r["AreaContextSource"], CultureInfo.InvariantCulture) ?? "").Contains("Legacy", StringComparison.OrdinalIgnoreCase))
                    ? "Legacy context - verify historical identity" : "Native frozen context";
                DataRow first = rows[0];
                DataRow output = summary.NewRow();
                output["AreaCode"] = first["AreaCode"]; output["AreaName"] = first["AreaName"]; output["Grade"] = first["Grade"];
                output["Method"] = first["Method"]; output["Unit"] = first["Unit"]; output["Records"] = rows.Count; output["NumericObservations"] = numeric.Count;
                output["Pending"] = rows.Count(r => IsAssessment(r, "Pending"));
                output["Minimum"] = numeric.Count > 0 ? numeric.Min() : DBNull.Value; output["Mean"] = numeric.Count > 0 ? Math.Round(numeric.Average(), 2) : DBNull.Value; output["Maximum"] = numeric.Count > 0 ? numeric.Max() : DBNull.Value;
                output["FungalObservations"] = fungal.Count; output["FungalMinimum"] = fungal.Count > 0 ? fungal.Min() : DBNull.Value; output["FungalMaximum"] = fungal.Count > 0 ? fungal.Max() : DBNull.Value;
                output["AlertLimit"] = alerts.Count == 1 && !missingLimit ? alerts[0] : DBNull.Value; output["ActionLimit"] = actions.Count == 1 && !missingLimit ? actions[0] : DBNull.Value;
                output["LimitState"] = limitState; output["Alerts"] = rows.Count(r => IsAssessment(r, "Alert")); output["Actions"] = rows.Count(r => IsAssessment(r, "Action")); output["NotAssessed"] = rows.Count(r => IsAssessment(r, "Not Assessed")); output["HistoricalContext"] = context;
                summary.Rows.Add(output);
            }
            return summary;
        }

        private static string BuildNarrative(DataTable details, DataTable summary, DataTable previousPeriod, DataTable previousYear, DateTime start, DateTime end)
        {
            int alertCount = details.Rows.Cast<DataRow>().Count(r => IsAssessment(r, "Alert"));
            int actionCount = details.Rows.Cast<DataRow>().Count(r => IsAssessment(r, "Action"));
            int pending = details.Rows.Cast<DataRow>().Count(r => IsAssessment(r, "Pending"));
            int notAssessed = details.Rows.Cast<DataRow>().Count(r => IsAssessment(r, "Not Assessed"));
            int legacyGroups = summary.Rows.Cast<DataRow>().Count(r => (Convert.ToString(r["HistoricalContext"]) ?? "").StartsWith("Legacy", StringComparison.OrdinalIgnoreCase));
            int missingGroups = summary.Rows.Cast<DataRow>().Count(r => !(Convert.ToString(r["LimitState"]) ?? "").Equals("Complete", StringComparison.OrdinalIgnoreCase));

            static IEnumerable<IGrouping<string, DataRow>> MethodUnitGroups(DataTable table) => table.Rows.Cast<DataRow>()
                .Where(r => r["Result"] != DBNull.Value)
                .GroupBy(r => $"{Convert.ToString(r["Method"], CultureInfo.InvariantCulture)}|{Convert.ToString(r["Unit"], CultureInfo.InvariantCulture)}", StringComparer.OrdinalIgnoreCase);

            static string MethodUnitLabel(IGrouping<string, DataRow> group)
            {
                DataRow first = group.First();
                return $"{Convert.ToString(first["Method"], CultureInfo.InvariantCulture)} ({Convert.ToString(first["Unit"], CultureInfo.InvariantCulture)})";
            }

            string overview = !MethodUnitGroups(details).Any()
                ? "No completed numeric observations are available."
                : "Method/unit-specific statistics (different units are never pooled): " + string.Join("; ", MethodUnitGroups(details).Select(g =>
                {
                    List<decimal> v = g.Select(r => Convert.ToDecimal(r["Result"], CultureInfo.InvariantCulture)).ToList();
                    return $"{MethodUnitLabel(g)} n={v.Count}, mean {v.Average():0.##}, min {v.Min():0.##}, max {v.Max():0.##}";
                })) + ".";

            string monthlyText = !MethodUnitGroups(details).Any()
                ? "No completed numeric observations were available for monthly comparison."
                : "Monthly method/unit trends: " + string.Join(" | ", MethodUnitGroups(details).Select(g =>
                {
                    string months = string.Join("; ", g.GroupBy(r => Convert.ToDateTime(r["EventDate"], CultureInfo.InvariantCulture).ToString("yyyy-MM", CultureInfo.InvariantCulture))
                        .OrderBy(m => m.Key)
                        .Select(m => $"{m.Key} mean {m.Average(r => Convert.ToDecimal(r["Result"], CultureInfo.InvariantCulture)):0.##}, max {m.Max(r => Convert.ToDecimal(r["Result"], CultureInfo.InvariantCulture)):0.##}, n={m.Count()}"));
                    return $"{MethodUnitLabel(g)}: {months}";
                })) + ".";

            static Dictionary<string, (int Count, decimal Mean, decimal Max)> Aggregate(DataTable table) => MethodUnitGroups(table)
                .ToDictionary(g => g.Key, g =>
                {
                    List<decimal> v = g.Select(r => Convert.ToDecimal(r["Result"], CultureInfo.InvariantCulture)).ToList();
                    return (v.Count, v.Average(), v.Max());
                }, StringComparer.OrdinalIgnoreCase);

            Dictionary<string, (int Count, decimal Mean, decimal Max)> current = Aggregate(details);
            Dictionary<string, (int Count, decimal Mean, decimal Max)> prior = Aggregate(previousPeriod);
            Dictionary<string, (int Count, decimal Mean, decimal Max)> yearAgo = Aggregate(previousYear);
            string comparison = current.Count == 0 ? "No numeric data are available for period comparison." : string.Join("; ", current.Select(kvp =>
            {
                string label = kvp.Key.Replace("|", " (", StringComparison.Ordinal) + ")";
                string p = prior.TryGetValue(kvp.Key, out var pv) ? $"preceding equal-duration mean {pv.Mean:0.##} (n={pv.Count})" : "preceding equal-duration data NR";
                string y = yearAgo.TryGetValue(kvp.Key, out var yv) ? $"same period previous year mean {yv.Mean:0.##} (n={yv.Count})" : "same period previous year NR";
                return $"{label}: current mean {kvp.Value.Mean:0.##} (n={kvp.Value.Count}); {p}; {y}";
            })) + ".";

            List<decimal> fungal = details.Rows.Cast<DataRow>().Where(r => r["FungalCount"] != DBNull.Value).Select(r => Convert.ToDecimal(r["FungalCount"], CultureInfo.InvariantCulture)).ToList();
            string excursion = actionCount > 0 ? $"{actionCount} Action excursion(s) and {alertCount} Alert excursion(s) were identified using result > approved NMT limit. Equality with an NMT limit is not an excursion."
                : alertCount > 0 ? $"No Action excursions; {alertCount} Alert excursion(s) were identified using result > approved NMT limit."
                : "No Alert or Action excursions were identified among observations that had complete approved limit snapshots.";
            string flora = fungal.Count == 0 ? "No separate fungal-count values were available in the selected dataset. Microbial-flora shift cannot be concluded without organism-identification records."
                : $"Separate fungal counts were available for {fungal.Count} observation(s); fungal range {fungal.Min():0.##} to {fungal.Max():0.##}. Species/flora-shift assessment still requires the isolate-identification records.";
            string conclusion = actionCount > 0 ? "Action excursions are present; reconcile each excursion with the applicable investigation/CAPA before QA concludes state of control."
                : alertCount > 0 ? "Alert excursions are present; perform documented review of recurrence, location/method pattern and associated operating conditions before approval."
                : notAssessed > 0 || pending > 0 || missingGroups > 0 ? "No Action excursion was identified in assessed completed results, but the report contains incomplete/pending or unassessed evidence; final state-of-control conclusion remains conditional until those gaps are resolved."
                : "All completed assessed observations in the selected period are within the frozen approved Alert/Action limits. Continue routine monitoring and periodic trend review.";

            return
                $"ENVIRONMENTAL MONITORING TREND REVIEW\r\nPeriod: {start:dd-MMM-yyyy} to {end.AddDays(-1):dd-MMM-yyyy}\r\n\r\n" +
                "1. Guidance / Alert / Action Levels\r\nEach completed observation is evaluated against the frozen limit snapshot stored with that EM plate. NMT logic is applied: excursion only when Result > Limit. The report does not reconstruct missing historical limits from today's master data. " + (missingGroups > 0 ? $"{missingGroups} area/method group(s) contain missing or varying limit evidence and are explicitly flagged." : "All summarized groups have complete consistent limit evidence.") + "\r\n\r\n" +
                "2. Impact of Seasonal Changes\r\n" + monthlyText + " Causal seasonal conclusions require matching temperature/RH, HVAC, production-activity and cleaning records.\r\n\r\n" +
                "3. Analysis of Change in Microbial Flora\r\n" + flora + "\r\n\r\n" +
                "4. Analysis of Excursions\r\n" + excursion + $" Pending: {pending}; Not assessed because controlled limits are incomplete: {notAssessed}.\r\n\r\n" +
                "5. Probable Reasons for Peak Observed (if any)\r\nWhere a peak or excursion is present, review documented production activity, personnel/material movement, cleaning/disinfection, HVAC condition, maintenance/door events, sampling technique, media condition and related quality-event records. These are review factors, not assumed causes.\r\n\r\n" +
                "6. Overview of Trend Analysis in All Areas\r\n" + overview + "\r\n\r\n" +
                "7. Comparison with Previous Period / Previous Year\r\n" + comparison + "\r\n\r\n" +
                "8. Review of Alert and Action Levels\r\nThis report does not automatically reset operational Alert/Action limits. Any change to approved levels requires SOP MQC-G-0011, documented statistical/risk assessment, QA approval and change control.\r\n\r\n" +
                "9. Historical Data Context\r\n" + (legacyGroups > 0 ? $"{legacyGroups} group(s) contain legacy area context frozen during migration. Verify historical area identity before final approval if master data may have changed before migration." : "All selected groups use native frozen area context captured at event creation.") + "\r\n\r\n" +
                "10. Conclusion\r\n" + conclusion + "\r\n\r\n" +
                "11. Action Plan\r\n" + (actionCount > 0 ? "Complete/reconcile investigation and CAPA records for all Action excursions; assess recurrence and affected areas before approval." : alertCount > 0 ? "Document review of Alert excursions, recurrence and operating conditions; escalate according to the approved procedure when warranted." : pending > 0 || notAssessed > 0 ? "Complete pending results and resolve/reconcile missing controlled limit evidence before final trend approval." : "No additional corrective action is generated by the trend report; continue the approved EM schedule and routine review.");
        }

        private void Print_Click(object sender, RoutedEventArgs e)
        {
            if (!DatabaseHelper.CanAccessReports(Login.CurrentUser))
            {
                MessageBox.Show("Reports permission is required to print the Environmental Monitoring trend.", "EM Trend", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_summary.Rows.Count == 0 && _details.Rows.Count == 0)
            {
                MessageBox.Show("Load trend data before printing.", "EM Trend", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            PrintDialog dialog = new();
            if (dialog.ShowDialog() != true) return;
            DateTime printedAt = DatabaseHelper.GetAuthoritativeDatabaseTime();
            FlowDocument document = new()
            {
                PageWidth = dialog.PrintableAreaWidth, PageHeight = dialog.PrintableAreaHeight, PagePadding = new Thickness(28), ColumnGap = 0,
                ColumnWidth = double.PositiveInfinity, FontFamily = new FontFamily("Arial"), FontSize = 8.2
            };
            Brush navy = new SolidColorBrush(Color.FromRgb(30, 58, 95));
            Table headerTable = new() { CellSpacing = 0 };
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(120) }); headerTable.Columns.Add(new TableColumn { Width = new GridLength(390) }); headerTable.Columns.Add(new TableColumn { Width = new GridLength(180) });
            TableRowGroup hg = new(); headerTable.RowGroups.Add(hg); TableRow hr = new();
            hr.Cells.Add(new TableCell(new Paragraph(new Run("MEDICA")) { TextAlignment = TextAlignment.Center, FontWeight = System.Windows.FontWeights.Bold, FontSize = 15, Foreground = navy }) { BorderBrush = navy, BorderThickness = new Thickness(.7), Padding = new Thickness(7) });
            hr.Cells.Add(new TableCell(new Paragraph(new Run("MEDICA PHARMACEUTICAL INDUSTRY\nMicrobiology Department")) { TextAlignment = TextAlignment.Center, FontWeight = System.Windows.FontWeights.Bold, FontSize = 12.5, Foreground = Brushes.White }) { Background = navy, BorderBrush = navy, BorderThickness = new Thickness(.7), Padding = new Thickness(7) });
            hr.Cells.Add(new TableCell(new Paragraph(new Run("Procedure Ref: MQC-G-0009\nA11: MQC-G-0009/G1/1 | A12: MQC-G-0009/F4/1\nPrinted: " + printedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))) { TextAlignment = TextAlignment.Right, FontSize = 7.8, Foreground = Brushes.White }) { Background = navy, BorderBrush = navy, BorderThickness = new Thickness(.7), Padding = new Thickness(7) });
            hg.Rows.Add(hr); document.Blocks.Add(headerTable);
            string controlState = _approvedOnly ? "SYSTEM-GENERATED REVIEW DRAFT - APPROVED SOURCE EVENTS" : "DRAFT - INCLUDES NON-APPROVED EVENTS";
            document.Blocks.Add(new Paragraph(new Run(controlState)) { FontSize = 9.5, FontWeight = System.Windows.FontWeights.Bold, Foreground = _approvedOnly ? navy : Brushes.DarkRed, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 7, 0, 2) });
            document.Blocks.Add(new Paragraph(new Run("ENVIRONMENTAL MONITORING TREND REVIEW")) { FontSize = 15, FontWeight = System.Windows.FontWeights.Bold, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 3) });
            document.Blocks.Add(new Paragraph(new Run(lblStatus.Text)) { TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });

            string[] printColumns = { "AreaCode", "AreaName", "Grade", "Method", "Unit", "NumericObservations", "Minimum", "Mean", "Maximum", "FungalMinimum", "FungalMaximum", "AlertLimit", "ActionLimit", "Alerts", "Actions", "NotAssessed" };
            Table table = new() { CellSpacing = 0 }; foreach (string _ in printColumns) table.Columns.Add(new TableColumn()); TableRowGroup group = new(); table.RowGroups.Add(group);
            TableRow th = new(); foreach (string column in printColumns) th.Cells.Add(Cell(column, true)); group.Rows.Add(th);
            foreach (DataRow row in _summary.Rows) { TableRow tr = new(); foreach (string column in printColumns) tr.Cells.Add(Cell(row[column] == DBNull.Value ? "NR" : Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? "", false)); group.Rows.Add(tr); }
            document.Blocks.Add(table);

            document.Blocks.Add(new Paragraph(new Run("TREND REVIEW NARRATIVE")) { FontWeight = System.Windows.FontWeights.Bold, FontSize = 10.5, Margin = new Thickness(0, 9, 0, 4) });
            foreach (string block in (txtNarrative.Text ?? string.Empty).Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
                document.Blocks.Add(new Paragraph(new Run(block.Trim())) { FontSize = 8, Margin = new Thickness(0, 2, 0, 4) });
            document.Blocks.Add(new Paragraph(new Run("System-generated review draft from PharmaLIMS electronic records. It is not a QA-approved trend summary until prepared by Microbiology, checked by Head of Microbiology and approved by Quality Assurance according to MQC-G-0009. Individual observations remain available in the audit trail and EM event records.")) { FontSize = 7.3, Foreground = Brushes.Gray, Margin = new Thickness(0, 7, 0, 0) });
            document.Blocks.Add(new Paragraph(new Run("PREPARED BY (Microbiologist): ____________________    CHECKED BY (Head of Microbiology): ____________________    APPROVED BY (Head of Quality Assurance): ____________________")) { FontSize = 8, FontWeight = System.Windows.FontWeights.Bold, Margin = new Thickness(0, 9, 0, 0) });

            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "EM Trend Review");
            string user = string.IsNullOrWhiteSpace(Login.CurrentUser) ? "Unknown" : Login.CurrentUser.Trim();
            DatabaseHelper.AddAuditTrailAdvanced("EM_Trend", 0, "EM Trend Review Printed", "", "Printed", lblStatus.Text, user);
        }

        private static TableCell Cell(string text, bool header) => new(new Paragraph(new Run(text)))
        { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.35), Padding = new Thickness(2.3), FontWeight = header ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal };
    }
}
