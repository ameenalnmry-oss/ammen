using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Printing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace PharmaLIMS
{
    public partial class EMPlanning
    {
        private void GenerateDuePlans()
        {
            var signature = ConfirmSignature("DUE EM SCHEDULES", "Generate Due EM Plans");
            if (signature == null) return;
            var generated = new List<string>();
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                using SqlCommand databaseDateCommand = new SqlCommand("SELECT CAST(SYSDATETIME() AS date);", connection, transaction);
                DateTime databaseToday = Convert.ToDateTime(databaseDateCommand.ExecuteScalar(), CultureInfo.InvariantCulture).Date;

                using SqlCommand dueCommand = new SqlCommand(@"
SELECT ScheduleID,ScheduleName,PlanType,Frequency,NextDueDate,ISNULL(DaysAhead,0) DaysAhead,
       Method,SamplingLocation,MediaUsed,MediaLotNo,MediaPreparationID,IncludeNegativeControl,EmployeeID,EmployeeName,
       ISNULL(ApprovedPointCount,0) ApprovedPointCount
FROM dbo.EM_Schedules WITH(UPDLOCK,HOLDLOCK)
WHERE IsActive=1
  AND ISNULL(ApprovalStatus,N'Draft')=N'Approved'
  AND (EffectiveFrom IS NULL OR EffectiveFrom<=CAST(GETDATE() AS DATE))
  AND ScheduleID=(SELECT TOP(1) S2.ScheduleID FROM dbo.EM_Schedules S2
                  WHERE S2.IsActive=1
                    AND ISNULL(S2.ApprovalStatus,N'Draft')=N'Approved'
                    AND (S2.EffectiveFrom IS NULL OR S2.EffectiveFrom<=CAST(GETDATE() AS DATE))
                    AND UPPER(LTRIM(RTRIM(S2.ScheduleName)))=UPPER(LTRIM(RTRIM(dbo.EM_Schedules.ScheduleName)))
                  ORDER BY ISNULL(S2.VersionNo,1) DESC,S2.ScheduleID DESC)
  AND NULLIF(LTRIM(RTRIM(Method)),N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(MediaUsed)),N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(MediaLotNo)),N'') IS NOT NULL
  AND MediaPreparationID IS NOT NULL
  AND EXISTS(SELECT 1 FROM dbo.MediaPreparations p WHERE p.MediaPreparationID=dbo.EM_Schedules.MediaPreparationID AND UPPER(LTRIM(RTRIM(ISNULL(p.ReleaseStatus,N''))))=N'RELEASED' AND UPPER(LTRIM(RTRIM(ISNULL(p.SterilityReview,N'')))) IN(N'PASSED',N'RELEASED',N'GPT PASSED') AND p.ExpiryDate IS NOT NULL AND p.ExpiryDate>=CAST(GETDATE() AS DATE))
  AND NextDueDate<=DATEADD(DAY,ISNULL(DaysAhead,0),CAST(GETDATE() AS DATE))
ORDER BY NextDueDate,ScheduleID;", connection, transaction);
                var configs = new List<ScheduleConfig>();
                using (SqlDataReader reader = dueCommand.ExecuteReader())
                    while (reader.Read()) configs.Add(new ScheduleConfig(reader));

                foreach (ScheduleConfig schedule in configs)
                {
                    using SqlCommand existsCommand = new SqlCommand(
                        "SELECT COUNT(1) FROM dbo.EM_Plans WHERE ScheduleID=@Schedule AND SampleDueDate=@Due;", connection, transaction);
                    existsCommand.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                    existsCommand.Parameters.AddExplicit("@Due", SqlDbType.Date, schedule.NextDueDate.Date);
                    bool exists = Convert.ToInt32(existsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
                    if (!exists)
                    {
                        string planNo = GetNextPlanNo(connection, transaction);
                        int planId;
                        using (SqlCommand planCommand = new SqlCommand(@"
INSERT dbo.EM_Plans(PlanNo,PlanType,SourceType,LoginDate,SampleDueDate,RequiredDate,Status,PlanNotes,CreatedBy,ScheduleID)
OUTPUT INSERTED.PlanID
VALUES(@No,@Type,N'Scheduled',CAST(GETDATE() AS DATE),@Due,DATEADD(DAY,1,@Due),N'Planned',@Notes,@User,@Schedule);", connection, transaction))
                        {
                            planCommand.Parameters.AddExplicit("@No", SqlDbType.NVarChar, planNo, size: 50);
                            planCommand.Parameters.AddExplicit("@Type", SqlDbType.NVarChar, schedule.PlanType, size: 50);
                            planCommand.Parameters.AddExplicit("@Due", SqlDbType.Date, schedule.NextDueDate.Date);
                            planCommand.Parameters.AddExplicit("@Notes", SqlDbType.NVarChar, "Generated from schedule: " + schedule.ScheduleName, size: -1);
                            planCommand.Parameters.AddExplicit("@User", SqlDbType.NVarChar, signature.SignedBy, size: 100);
                            planCommand.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                            planId = Convert.ToInt32(planCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                        }

                        List<ApprovedSchedulePointSnapshot> approvedSeeds = LoadApprovedSchedulePointSnapshots(connection, transaction, schedule.ScheduleID);
                        if (schedule.ApprovedPointCount <= 0 || approvedSeeds.Count != schedule.ApprovedPointCount)
                        {
                            throw new InvalidOperationException(
                                $"Schedule '{schedule.ScheduleName}' approved snapshot is incomplete ({approvedSeeds.Count}/{schedule.ApprovedPointCount}). Run System Preflight / Database Maintenance and do not regenerate from mutable master data.");
                        }

                        int sequence = 0;
                        foreach (ApprovedSchedulePointSnapshot item in approvedSeeds)
                        {
                            sequence++;
                            InsertScheduledSample(connection, transaction, planId, item.AreaID, item.Method, item.Location,
                                MakeCode(planNo, item.AreaCode, item.Method, sequence), false, schedule);
                        }
                        InsertScheduledSample(connection, transaction, planId, null, "Negative Control", "Transport / Media Negative Control",
                            "NC-" + planNo, true, schedule);
                        InsertSignature(connection, transaction, planId, "Generated from Schedule", signature);
                        InsertAudit(connection, transaction, "EM_Plans", planId,
                            "EM Plan Generated from Schedule", string.Empty, "Planned", signature.Reason,
                            signature.SignedBy, "Status", planNo + " | " + schedule.ScheduleName);
                        generated.Add(planNo + " | " + schedule.ScheduleName);
                    }

                    DateTime next = AdvanceToFuture(schedule.NextDueDate, schedule.Frequency, databaseToday);
                    using SqlCommand update = new SqlCommand(@"
UPDATE dbo.EM_Schedules SET LastGeneratedDate=@Generated,NextDueDate=@Next WHERE ScheduleID=@Schedule;", connection, transaction);
                    update.Parameters.AddExplicit("@Generated", SqlDbType.Date, schedule.NextDueDate.Date);
                    update.Parameters.AddExplicit("@Next", SqlDbType.Date, next.Date);
                    update.Parameters.AddExplicit("@Schedule", SqlDbType.Int, schedule.ScheduleID);
                    update.ExecuteNonQuery();
                }
            });


            if (generated.Count == 0)
                MessageBox.Show("No due schedules were found.", "EM Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("Generated controlled EM plans:\n\n" + string.Join("\n", generated), "EM Scheduler", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadSchedules();
            LoadPlans();
        }

        private static List<AreaChoice> LoadScheduleAreas(SqlConnection connection, SqlTransaction transaction, int scheduleId)
        {
            var result = new List<AreaChoice>();
            using SqlCommand command = new SqlCommand(@"
SELECT A.Id,A.AreaCode,A.AreaName,ISNULL(A.AreaGroup,N'') AreaGroup,ISNULL(A.Grade,N'Unclassified') Grade
FROM dbo.EM_ScheduleAreas S INNER JOIN dbo.EM_Areas A ON A.Id=S.AreaID
WHERE S.ScheduleID=@Schedule AND ISNULL(A.IsActive,1)=1 ORDER BY A.AreaCode;", connection, transaction);
            command.Parameters.AddExplicit("@Schedule", SqlDbType.Int, scheduleId);
            using SqlDataReader reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new AreaChoice
            {
                Id = Convert.ToInt32(reader["Id"], CultureInfo.InvariantCulture),
                AreaCode = reader["AreaCode"].ToString() ?? "",
                AreaName = reader["AreaName"].ToString() ?? "",
                AreaGroup = reader["AreaGroup"].ToString() ?? "",
                Grade = reader["Grade"].ToString() ?? ""
            });
            return result;
        }

        private static IEnumerable<SampleSeed> BuildScheduleSeeds(SqlConnection connection, SqlTransaction transaction,
            AreaChoice area, string method, string location)
        {
            var result = new List<SampleSeed>();
            using SqlCommand command = new SqlCommand(@"
SELECT Id,SequenceNo,Method,PlateCode FROM dbo.EM_AreaTemplates
WHERE AreaId=@Area AND ISNULL(IsActive,1)=1
AND (@Method=N'Both Methods' AND Method IN(N'Settle Plate',N'Active Air Sampling') OR UPPER(LTRIM(RTRIM(Method)))=UPPER(@Method))
ORDER BY SequenceNo,Id;", connection, transaction);
            command.Parameters.AddExplicit("@Area", SqlDbType.Int, area.Id);
            command.Parameters.AddExplicit("@Method", SqlDbType.NVarChar, method, size: 100);
            using SqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(new SampleSeed(
                    Convert.ToInt32(reader["Id"], CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader["SequenceNo"], CultureInfo.InvariantCulture),
                    reader["Method"].ToString() ?? method,
                    ComposeSamplingLocation(location, reader["PlateCode"].ToString(), area.AreaName)));
            if (result.Count == 0)
                throw new InvalidOperationException($"No approved active {method} templates exist for {area.AreaCode} - {area.AreaName}.");
            return result;
        }

        private static List<ApprovedSchedulePointSnapshot> LoadApprovedSchedulePointSnapshots(SqlConnection connection, SqlTransaction transaction, int scheduleId)
        {
            var result = new List<ApprovedSchedulePointSnapshot>();
            using SqlCommand command = new SqlCommand(@"
SELECT SnapshotSequence,AreaID,AreaCodeSnapshot,AreaNameSnapshot,ISNULL(GradeSnapshot,N'') GradeSnapshot,
       TemplateID,TemplateSequenceNo,MethodSnapshot,LocationSnapshot
FROM dbo.EM_SchedulePointSnapshots WITH(HOLDLOCK)
WHERE ScheduleID=@Schedule
ORDER BY SnapshotSequence;", connection, transaction);
            command.Parameters.Add("@Schedule", SqlDbType.Int).Value = scheduleId;
            using SqlDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ApprovedSchedulePointSnapshot(
                    Convert.ToInt32(reader["SnapshotSequence"], CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader["AreaID"], CultureInfo.InvariantCulture),
                    reader["AreaCodeSnapshot"].ToString() ?? string.Empty,
                    reader["AreaNameSnapshot"].ToString() ?? string.Empty,
                    reader["GradeSnapshot"].ToString() ?? string.Empty,
                    reader["MethodSnapshot"].ToString() ?? string.Empty,
                    reader["LocationSnapshot"].ToString() ?? string.Empty));
            }
            return result;
        }

        private static void InsertScheduledSample(SqlConnection connection, SqlTransaction transaction, int planId, int? areaId,
            string method, string location, string code, bool negative, ScheduleConfig schedule)
        {
            using SqlCommand command = new SqlCommand(@"
INSERT dbo.EM_PlanSamples(PlanID,AreaID,Method,SamplingLocation,SampleCode,IsNegativeControl,EmployeeID,EmployeeName,MediaUsed,MediaLotNo,MediaPreparationID,Status)
VALUES(@Plan,@Area,@Method,@Location,@Code,@Negative,@EmployeeID,@EmployeeName,@Media,@Lot,@MediaPreparationID,N'Planned');", connection, transaction);
            command.Parameters.AddExplicit("@Plan", SqlDbType.Int, planId); command.Parameters.AddExplicit("@Area", SqlDbType.Int, areaId.HasValue ? areaId.Value : DBNull.Value);
            command.Parameters.AddExplicit("@Method", SqlDbType.NVarChar, method, size: 100); command.Parameters.AddExplicit("@Location", SqlDbType.NVarChar, Db(location), size: 200);
            command.Parameters.AddExplicit("@Code", SqlDbType.NVarChar, code, size: 100); command.Parameters.AddExplicit("@Negative", SqlDbType.Bit, negative);
            command.Parameters.AddExplicit("@EmployeeID", SqlDbType.NVarChar, Db(schedule.EmployeeID), size: 100); command.Parameters.AddExplicit("@EmployeeName", SqlDbType.NVarChar, Db(schedule.EmployeeName), size: 200);
            command.Parameters.AddExplicit("@Media", SqlDbType.NVarChar, Db(schedule.MediaUsed), size: 200); command.Parameters.AddExplicit("@Lot", SqlDbType.NVarChar, Db(schedule.MediaLotNo), size: 100);
            command.Parameters.AddExplicit("@MediaPreparationID", SqlDbType.Int, schedule.MediaPreparationID);
            command.ExecuteNonQuery();
        }

        private static DateTime AdvanceToFuture(DateTime date, string frequency, DateTime today)
        {
            DateTime next = date;
            do
            {
                next = frequency switch
                {
                    "Daily" => next.AddDays(1), "Weekly" => next.AddDays(7), "Fortnightly" => next.AddDays(14),
                    "Monthly" => next.AddMonths(1), "Quarterly" => next.AddMonths(3), _ => throw new InvalidOperationException("Unsupported frequency: " + frequency)
                };
            } while (next <= today);
            return next;
        }

        private void PrintSheet_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (gridPlans.SelectedItem is not PlanRow plan)
                    throw new InvalidOperationException("Select a plan first.");
                if (_samples.Count == 0)
                    throw new InvalidOperationException("The selected plan has no sampling records to print.");

                DateTime printedAt = DatabaseHelper.GetAuthoritativeDatabaseTime();
                var document = new FlowDocument
                {
                    PageWidth = 793,
                    PageHeight = 1122,
                    PagePadding = new Thickness(38),
                    ColumnWidth = double.PositiveInfinity,
                    ColumnGap = 0,
                    IsColumnWidthFlexible = false,
                    FontFamily = new FontFamily("Arial"),
                    FontSize = 9
                };

                const int rowsPerPage = 17;
                int totalPages = Math.Max(1, (int)Math.Ceiling(_samples.Count / (double)rowsPerPage));
                for (int pageIndex = 0; pageIndex < totalPages; pageIndex++)
                {
                    var page = new Section { BreakPageBefore = pageIndex > 0, Margin = new Thickness(0) };
                    AddEmPlanPrintHeader(page, plan, printedAt, pageIndex + 1, totalPages);

                    Table table = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 0) };
                    foreach (double width in new[] { 28d, 92d, 70d, 112d, 88d, 116d, 108d, 82d })
                        table.Columns.Add(new TableColumn { Width = new GridLength(width) });
                    var group = new TableRowGroup();
                    table.RowGroups.Add(group);
                    AddEmPrintRow(group, true, "#", "Sample Code", "Grade", "Area", "Method", "Location / Surface", "Media / Prep. No.", "Condition / Time");

                    int rowIndex = pageIndex * rowsPerPage;
                    foreach (PlanSampleRow sample in _samples.Skip(rowIndex).Take(rowsPerPage))
                    {
                        rowIndex++;
                        string area = sample.IsNegativeControl ? "NEGATIVE CONTROL" : sample.AreaCode + " - " + sample.AreaName;
                        string media = string.Join(" / ", new[] { sample.MediaUsed, sample.MediaLotNo }.Where(value => !string.IsNullOrWhiteSpace(value)));
                        AddEmPrintRow(group, false,
                            rowIndex.ToString(CultureInfo.InvariantCulture),
                            sample.SampleCode,
                            sample.Grade,
                            area,
                            sample.Method,
                            sample.SamplingLocation,
                            media,
                            "________________");
                    }
                    page.Blocks.Add(table);

                    page.Blocks.Add(new Paragraph(new Run(
                        "Collection checks: Plate/Swab Condition __________   Kit/Media Condition __________   " +
                        "Transport Min/Max ______ / ______ °C"))
                    { Margin = new Thickness(0, 12, 0, 0), FontSize = 8.5 });
                    page.Blocks.Add(new Paragraph(new Run(
                        "Collected By __________________________   Sampling Start __________________   Sampling End __________________"))
                    { Margin = new Thickness(0, 6, 0, 0), FontSize = 8.5 });
                    page.Blocks.Add(new Paragraph(new Run(
                        "Negative Control Final Read:  No Growth / Growth Detected     Read By __________________     Date/Time __________________"))
                    { Margin = new Thickness(0, 6, 0, 0), FontSize = 8.5 });
                    document.Blocks.Add(page);
                }

                PrintDialog dialog = new PrintDialog();
                if (dialog.ShowDialog() == true)
                {
                    dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "EM Sampling Data Sheet " + plan.PlanNo);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        "EM_Plans", plan.PlanID, "EM Sampling Data Sheet Printed", "", "Printed",
                        $"Printed controlled EM sampling data sheet ({_samples.Count} sample records; {totalPages} page(s)).",
                        CurrentUser());
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private void PrintLabels_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (gridPlans.SelectedItem is not PlanRow plan)
                    throw new InvalidOperationException("Select a plan first.");
                if (_samples.Count == 0)
                    throw new InvalidOperationException("The selected plan has no labels to print.");

                DateTime printedAt = DatabaseHelper.GetAuthoritativeDatabaseTime();
                var document = new FlowDocument
                {
                    PageWidth = 793,
                    PageHeight = 1122,
                    PagePadding = new Thickness(28),
                    ColumnGap = 12,
                    ColumnWidth = 350,
                    FontFamily = new FontFamily("Arial")
                };
                foreach (PlanSampleRow sample in _samples)
                {
                    var block = new Section
                    {
                        BorderBrush = Brushes.Black,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10),
                        Margin = new Thickness(0, 0, 0, 10),
                        BreakPageBefore = false
                    };
                    block.Blocks.Add(new Paragraph(new Run("MEDICA | MICROBIOLOGY | EM SAMPLE"))
                    { FontWeight = FontWeights.Bold, FontSize = 11, Margin = new Thickness(0) });
                    block.Blocks.Add(new Paragraph(new Run(sample.SampleCode))
                    { FontFamily = new FontFamily("Consolas"), FontWeight = FontWeights.Bold, FontSize = 17, Margin = new Thickness(0, 5, 0, 3) });

                    string areaLine = sample.IsNegativeControl
                        ? "Area: NEGATIVE CONTROL"
                        : $"Area: {sample.AreaCode} - {sample.AreaName} | Grade: {sample.Grade}";
                    block.Blocks.Add(new Paragraph(new Run(
                        $"Plan: {plan.PlanNo}\n{areaLine}\nMethod: {sample.Method}\nLocation: {sample.SamplingLocation}\n" +
                        $"Media: {sample.MediaUsed} | Prep. No.: {sample.MediaLotNo}\nDue: {plan.SampleDueDate:yyyy-MM-dd} | Required: {plan.RequiredDate:yyyy-MM-dd}"))
                    { FontSize = 8.7, Margin = new Thickness(0) });
                    document.Blocks.Add(block);
                }
                document.Blocks.Add(new Paragraph(new Run(
                    "Labels generated from controlled PharmaLIMS EM plan at " + printedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                { FontSize = 7.5, Foreground = Brushes.Gray });

                PrintDialog dialog = new PrintDialog();
                if (dialog.ShowDialog() == true)
                {
                    dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "EM Sample Labels " + plan.PlanNo);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        "EM_Plans", plan.PlanID, "EM Sample Labels Printed", "", "Printed",
                        $"Printed {_samples.Count} controlled EM sample label(s).", CurrentUser());
                }
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private static void AddEmPlanPrintHeader(Section page, PlanRow plan, DateTime printedAt, int pageNumber, int totalPages)
        {
            Brush navy = new SolidColorBrush(Color.FromRgb(30, 58, 95));
            Brush light = new SolidColorBrush(Color.FromRgb(234, 240, 247));

            Table header = new Table { CellSpacing = 0 };
            header.Columns.Add(new TableColumn { Width = new GridLength(135) });
            header.Columns.Add(new TableColumn { Width = new GridLength(395) });
            header.Columns.Add(new TableColumn { Width = new GridLength(165) });
            TableRowGroup headerGroup = new TableRowGroup();
            header.RowGroups.Add(headerGroup);
            TableRow headerRow = new TableRow();
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run("MEDICA"))
            { TextAlignment = TextAlignment.Center, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = navy })
            { BorderBrush = navy, BorderThickness = new Thickness(.8), Padding = new Thickness(8) });
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run("MEDICA PHARMACEUTICAL INDUSTRY\nMicrobiology Department"))
            { TextAlignment = TextAlignment.Center, FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White })
            { Background = navy, BorderBrush = navy, BorderThickness = new Thickness(.8), Padding = new Thickness(8) });
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run(
                "Form No: MQC-F-EM-001\nProcedure Ref: MQC-I-0018\nPage: " + pageNumber.ToString(CultureInfo.InvariantCulture) + " of " + totalPages.ToString(CultureInfo.InvariantCulture)))
            { TextAlignment = TextAlignment.Right, FontSize = 8.2, Foreground = Brushes.White })
            { Background = navy, BorderBrush = navy, BorderThickness = new Thickness(.8), Padding = new Thickness(7) });
            headerGroup.Rows.Add(headerRow);
            page.Blocks.Add(header);

            page.Blocks.Add(new Paragraph(new Run("ENVIRONMENTAL MONITORING SAMPLING DATA SHEET"))
            {
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 7)
            });

            Table meta = new Table { CellSpacing = 0 };
            meta.Columns.Add(new TableColumn { Width = new GridLength(115) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(235) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(115) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(230) });
            TableRowGroup metaGroup = new TableRowGroup();
            meta.RowGroups.Add(metaGroup);
            AddPlanMetaRow(metaGroup, light, "Plan No.", plan.PlanNo, "Plan Type", plan.PlanType);
            AddPlanMetaRow(metaGroup, light, "Source / Status", plan.SourceType + " / " + plan.Status, "Login Date", plan.LoginDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            AddPlanMetaRow(metaGroup, light, "Sampling Due", plan.SampleDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "Required Date", plan.RequiredDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            string schedule = string.IsNullOrWhiteSpace(plan.ScheduleName)
                ? "Ad-hoc / manually created"
                : plan.ScheduleName + " / Rev. " + plan.ScheduleVersion.ToString(CultureInfo.InvariantCulture);
            AddPlanMetaRow(metaGroup, light, "Schedule", schedule, "Created By", plan.CreatedBy);
            AddPlanMetaRow(metaGroup, light, "Printed At", printedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "Plan Notes", string.IsNullOrWhiteSpace(plan.PlanNotes) ? "N/A" : plan.PlanNotes);
            page.Blocks.Add(meta);
        }

        private static void AddPlanMetaRow(TableRowGroup group, Brush labelBackground, string label1, string value1, string label2, string value2)
        {
            TableRow row = new TableRow();
            row.Cells.Add(new TableCell(new Paragraph(new Run(label1))) { Background = labelBackground, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.45), Padding = new Thickness(4), FontWeight = FontWeights.Bold });
            row.Cells.Add(new TableCell(new Paragraph(new Run(value1 ?? ""))) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.45), Padding = new Thickness(4) });
            row.Cells.Add(new TableCell(new Paragraph(new Run(label2))) { Background = labelBackground, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.45), Padding = new Thickness(4), FontWeight = FontWeights.Bold });
            row.Cells.Add(new TableCell(new Paragraph(new Run(value2 ?? ""))) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(.45), Padding = new Thickness(4) });
            group.Rows.Add(row);
        }

        private static void AddPrintRow(TableRowGroup group, bool header, params string[] values)
        {
            var row = new TableRow { Background = header ? Brushes.LightSteelBlue : Brushes.White };
            foreach (string value in values)
            {
                row.Cells.Add(new TableCell(new Paragraph(new Run(value ?? "")))
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(.5),
                    Padding = new Thickness(4),
                    FontWeight = header ? FontWeights.Bold : FontWeights.Normal
                });
            }
            group.Rows.Add(row);
        }

        private static void AddEmPrintRow(TableRowGroup group, bool header, params string[] values)
        {
            var row = new TableRow { Background = header ? new SolidColorBrush(Color.FromRgb(30, 58, 95)) : Brushes.White };
            foreach (string value in values)
            {
                row.Cells.Add(new TableCell(new Paragraph(new Run(value ?? "")))
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(.5),
                    Padding = new Thickness(3),
                    FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = header ? Brushes.White : Brushes.Black
                });
            }
            group.Rows.Add(row);
        }

        private static object Db(object? value) => value == null || string.IsNullOrWhiteSpace(value.ToString()) ? DBNull.Value : value;
        private static DateTime? ParseDate(string? text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime value) ? value : null;
        private static decimal? ParseDecimal(string? text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value) ? value : null;
        private static void ShowWaterError(Exception ex, string operation)
        {
            if (ex is OperationCanceledException)
                return;

            string safeOperation = string.IsNullOrWhiteSpace(operation) ? "Water planning operation" : operation.Trim();
            MessageBox.Show(
                Infrastructure.UserFacingError.SafeMessage(ex, safeOperation),
                "Water Planning",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        private static void ExecuteWaterUi(Action action, string operation)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                ShowWaterError(ex, operation);
            }
        }

        private static void ShowError(Exception ex)
        {
            if (ex is OperationCanceledException) return;
            MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex, "Environmental Monitoring operation"), "Environmental Monitoring", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private static void ExecuteUi(Action action)
        {
            try { action(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private sealed class InitialPlanningData
        {
            public DataTable Methods { get; init; } = new();
            public DataTable Areas { get; init; } = new();
            public DataTable Plans { get; init; } = new();
            public DataTable Schedules { get; init; } = new();
            public DataTable WaterPoints { get; init; } = new();
            public DataTable WaterPlans { get; init; } = new();
        }

        private sealed record SampleSeed(int TemplateID, int TemplateSequenceNo, string Method, string Location);
        private sealed record ApprovedSchedulePointSnapshot(int SnapshotSequence, int AreaID, string AreaCode, string AreaName, string Grade, string Method, string Location);
        public sealed class AreaChoice { public bool IsSelected { get; set; } public int Id { get; set; } public string AreaCode { get; set; } = ""; public string AreaName { get; set; } = ""; public string AreaGroup { get; set; } = ""; public string Grade { get; set; } = ""; }
        public sealed class PlanRow
        {
            public PlanRow(DataRow row)
            {
                PlanID = Convert.ToInt32(row["PlanID"], CultureInfo.InvariantCulture);
                PlanNo = row["PlanNo"].ToString() ?? "";
                PlanType = row["PlanType"].ToString() ?? "";
                SourceType = row["SourceType"].ToString() ?? "";
                LoginDate = row.Table.Columns.Contains("LoginDate") && row["LoginDate"] != DBNull.Value ? Convert.ToDateTime(row["LoginDate"], CultureInfo.InvariantCulture) : Convert.ToDateTime(row["SampleDueDate"], CultureInfo.InvariantCulture);
                SampleDueDate = Convert.ToDateTime(row["SampleDueDate"], CultureInfo.InvariantCulture);
                RequiredDate = Convert.ToDateTime(row["RequiredDate"], CultureInfo.InvariantCulture);
                Status = row["Status"].ToString() ?? "";
                CreatedBy = row["CreatedBy"].ToString() ?? "";
                PlanNotes = row["PlanNotes"].ToString() ?? "";
                SampleCount = Convert.ToInt32(row["SampleCount"], CultureInfo.InvariantCulture);
                ScheduleID = row.Table.Columns.Contains("ScheduleID") && row["ScheduleID"] != DBNull.Value ? Convert.ToInt32(row["ScheduleID"], CultureInfo.InvariantCulture) : null;
                ScheduleName = row.Table.Columns.Contains("ScheduleName") ? row["ScheduleName"].ToString() ?? "" : "";
                ScheduleVersion = row.Table.Columns.Contains("ScheduleVersion") && row["ScheduleVersion"] != DBNull.Value ? Convert.ToInt32(row["ScheduleVersion"], CultureInfo.InvariantCulture) : 0;
            }

            public int PlanID { get; }
            public string PlanNo { get; }
            public string PlanType { get; }
            public string SourceType { get; }
            public DateTime LoginDate { get; }
            public DateTime SampleDueDate { get; }
            public DateTime RequiredDate { get; }
            public string Status { get; }
            public string CreatedBy { get; }
            public string PlanNotes { get; }
            public int SampleCount { get; }
            public int? ScheduleID { get; }
            public string ScheduleName { get; }
            public int ScheduleVersion { get; }
        }

        public sealed class PlanSampleRow
        {
            public PlanSampleRow(DataRow r)
            {
                PlanSampleID = Convert.ToInt32(r["PlanSampleID"], CultureInfo.InvariantCulture);
                AreaID = r["AreaID"] == DBNull.Value ? null : Convert.ToInt32(r["AreaID"], CultureInfo.InvariantCulture);
                AreaCode = r.Table.Columns.Contains("AreaCode") ? r["AreaCode"].ToString() ?? "" : "";
                AreaName = r["AreaName"].ToString() ?? "";
                Grade = r.Table.Columns.Contains("Grade") ? r["Grade"].ToString() ?? "" : "";
                Method = r["Method"].ToString() ?? "";
                SamplingLocation = r["SamplingLocation"].ToString() ?? "";
                SampleCode = r["SampleCode"].ToString() ?? "";
                IsNegativeControl = Convert.ToBoolean(r["IsNegativeControl"], CultureInfo.InvariantCulture);
                MediaUsed = r["MediaUsed"].ToString() ?? "";
                MediaLotNo = r["MediaLotNo"].ToString() ?? "";
                MediaPreparationID = r.Table.Columns.Contains("MediaPreparationID") && r["MediaPreparationID"] != DBNull.Value ? Convert.ToInt32(r["MediaPreparationID"], CultureInfo.InvariantCulture) : null;
                Status = r["Status"].ToString() ?? "";
                PlateCondition = r["PlateCondition"].ToString() ?? "";
                KitCondition = r["KitCondition"].ToString() ?? "";
                SamplingStart = r["SamplingStart"] == DBNull.Value ? null : Convert.ToDateTime(r["SamplingStart"], CultureInfo.InvariantCulture);
                SamplingEnd = r["SamplingEnd"] == DBNull.Value ? null : Convert.ToDateTime(r["SamplingEnd"], CultureInfo.InvariantCulture);
                IncubationStart = r["IncubationStart"] == DBNull.Value ? null : Convert.ToDateTime(r["IncubationStart"], CultureInfo.InvariantCulture);
                IncubationEnd = r["IncubationEnd"] == DBNull.Value ? null : Convert.ToDateTime(r["IncubationEnd"], CultureInfo.InvariantCulture);
                PlannedIncubationEnd = r.Table.Columns.Contains("PlannedIncubationEnd") && r["PlannedIncubationEnd"] != DBNull.Value ? Convert.ToDateTime(r["PlannedIncubationEnd"], CultureInfo.InvariantCulture) : null;
                IncubationPhase1End = r.Table.Columns.Contains("IncubationPhase1End") && r["IncubationPhase1End"] != DBNull.Value ? Convert.ToDateTime(r["IncubationPhase1End"], CultureInfo.InvariantCulture) : null;
                IncubationPhase2End = r.Table.Columns.Contains("IncubationPhase2End") && r["IncubationPhase2End"] != DBNull.Value ? Convert.ToDateTime(r["IncubationPhase2End"], CultureInfo.InvariantCulture) : null;
                IncubationPhase1Temp = r.Table.Columns.Contains("IncubationPhase1Temp") ? r["IncubationPhase1Temp"].ToString() ?? "" : "";
                IncubationPhase2Temp = r.Table.Columns.Contains("IncubationPhase2Temp") ? r["IncubationPhase2Temp"].ToString() ?? "" : "";
                IncubationPhase1ActualTempC = r.Table.Columns.Contains("IncubationPhase1ActualTempC") && r["IncubationPhase1ActualTempC"] != DBNull.Value ? Convert.ToDecimal(r["IncubationPhase1ActualTempC"], CultureInfo.InvariantCulture) : null;
                IncubationPhase2ActualTempC = r.Table.Columns.Contains("IncubationPhase2ActualTempC") && r["IncubationPhase2ActualTempC"] != DBNull.Value ? Convert.ToDecimal(r["IncubationPhase2ActualTempC"], CultureInfo.InvariantCulture) : null;
                EventID = r["EventID"] == DBNull.Value ? null : Convert.ToInt32(r["EventID"], CultureInfo.InvariantCulture);
                BacteriaIncubatorID = r["BacteriaIncubatorID"].ToString() ?? "";
                FungiIncubatorID = r["FungiIncubatorID"].ToString() ?? "";
                SamplingStartText = SamplingStart?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
                SamplingEndText = SamplingEnd?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
                TransportMinText = r["TransportMinC"] == DBNull.Value ? "" : Convert.ToDecimal(r["TransportMinC"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                TransportMaxText = r["TransportMaxC"] == DBNull.Value ? "" : Convert.ToDecimal(r["TransportMaxC"], CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                NegativeControlResult = r["NegativeControlResult"].ToString() ?? "";
            }

            public string BuildIncubationTemperatureSummary()
            {
                string phase1 = IncubationPhase1ActualTempC.HasValue
                    ? $"Phase 1: {IncubationPhase1ActualTempC.Value:0.0} °C ({NormalizeTemperatureRange(IncubationPhase1Temp)})"
                    : string.Empty;
                string phase2 = IncubationPhase2ActualTempC.HasValue
                    ? $"Phase 2: {IncubationPhase2ActualTempC.Value:0.0} °C ({NormalizeTemperatureRange(IncubationPhase2Temp)})"
                    : string.Empty;
                return string.Join("; ", new[] { phase1, phase2 }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            private static string NormalizeTemperatureRange(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return "target range recorded in plan";
                return value.Replace(" C", " °C", StringComparison.OrdinalIgnoreCase);
            }

            public int PlanSampleID { get; }
            public int? AreaID { get; }
            public string AreaCode { get; }
            public string AreaName { get; }
            public string Grade { get; }
            public string Method { get; }
            public string SamplingLocation { get; }
            public string SampleCode { get; }
            public bool IsNegativeControl { get; }
            public string MediaUsed { get; }
            public string MediaLotNo { get; }
            public int? MediaPreparationID { get; }
            public string Status { get; }
            public string PlateCondition { get; set; }
            public string KitCondition { get; set; }
            public DateTime? SamplingStart { get; }
            public DateTime? SamplingEnd { get; }
            public DateTime? IncubationStart { get; }
            public DateTime? IncubationEnd { get; }
            public DateTime? PlannedIncubationEnd { get; }
            public DateTime? IncubationPhase1End { get; }
            public DateTime? IncubationPhase2End { get; }
            public string IncubationPhase1Temp { get; }
            public string IncubationPhase2Temp { get; }
            public decimal? IncubationPhase1ActualTempC { get; }
            public decimal? IncubationPhase2ActualTempC { get; }
            public int? EventID { get; }
            public string BacteriaIncubatorID { get; }
            public string FungiIncubatorID { get; }
            public string SamplingStartText { get; set; }
            public string SamplingEndText { get; set; }
            public string TransportMinText { get; set; }
            public string TransportMaxText { get; set; }
            public string NegativeControlResult { get; set; }
        }

        public sealed class ScheduleRow
        {
            public ScheduleRow(DataRow row) { ScheduleID=Convert.ToInt32(row["ScheduleID"]); ScheduleName=row["ScheduleName"].ToString()??""; Frequency=row["Frequency"].ToString()??""; NextDueDate=Convert.ToDateTime(row["NextDueDate"]); Method=row["Method"].ToString()??""; SamplingLocation=row["SamplingLocation"].ToString()??""; IsActive=Convert.ToBoolean(row["IsActive"]); ApprovalStatus=row["ApprovalStatus"].ToString()??"Draft"; VersionNo=Convert.ToInt32(row["VersionNo"],CultureInfo.InvariantCulture); CreatedBy=row["CreatedBy"].ToString()??""; ReviewedBy=row["ReviewedBy"].ToString()??""; ApprovedBy=row["ApprovedBy"].ToString()??""; ApprovedPointCount=Convert.ToInt32(row["ApprovedPointCount"],CultureInfo.InvariantCulture); }
            public int ScheduleID { get; } public string ScheduleName { get; } public string Frequency { get; } public DateTime NextDueDate { get; } public string Method { get; } public string SamplingLocation { get; } public bool IsActive { get; } public string ApprovalStatus { get; } public int VersionNo { get; } public string CreatedBy { get; } public string ReviewedBy { get; } public string ApprovedBy { get; } public int ApprovedPointCount { get; }
        }
        public sealed class WaterPointChoice
        {
            public WaterPointChoice(DataRow row) { Id=Convert.ToInt32(row["Id"]); PointCode=row["PointCode"].ToString()??""; PointName=row["PointName"].ToString()??""; Location=row["Location"].ToString()??""; }
            public bool IsSelected { get; set; } public int Id { get; } public string PointCode { get; } public string PointName { get; } public string Location { get; }
        }
        public sealed class WaterPlanRow
        {
            public WaterPlanRow(DataRow row) { WaterPlanID=Convert.ToInt32(row["WaterPlanID"]); PlanNo=row["PlanNo"].ToString()??""; WaterType=row["WaterType"].ToString()??""; SourceType=row["SourceType"].ToString()??""; Frequency=row["Frequency"].ToString()??""; DueDate=Convert.ToDateTime(row["DueDate"]); RequiredDate=Convert.ToDateTime(row["RequiredDate"]); Status=row["Status"].ToString()??""; Notes=row["Notes"].ToString()??""; PointCount=Convert.ToInt32(row["PointCount"]); RegisteredCount=row["RegisteredCount"]==DBNull.Value?0:Convert.ToInt32(row["RegisteredCount"]); }
            public int WaterPlanID { get; } public string PlanNo { get; } public string WaterType { get; } public string SourceType { get; } public string Frequency { get; } public DateTime DueDate { get; } public DateTime RequiredDate { get; } public string Status { get; } public string Notes { get; } public int PointCount { get; } public int RegisteredCount { get; }
        }
        public sealed class WaterPlanSampleRow
        {
            public WaterPlanSampleRow(DataRow row) { WaterPlanSampleID=Convert.ToInt32(row["WaterPlanSampleID"]); WaterPlanID=Convert.ToInt32(row["WaterPlanID"]); PointID=Convert.ToInt32(row["PointID"]); PointCode=row["PointCode"].ToString()??""; PointName=row["PointName"].ToString()??""; Location=row["Location"].ToString()??""; AnalysisProfile=row["AnalysisProfile"].ToString()??"Full"; TestNames=row["TestNames"].ToString()??""; Status=row["Status"].ToString()??""; SampleID=row["SampleID"]==DBNull.Value?null:Convert.ToInt32(row["SampleID"]); SampleNumber=row["SampleNumber"].ToString()??""; }
            public bool IsSelected { get; set; } public int WaterPlanSampleID { get; } public int WaterPlanID { get; } public int PointID { get; } public string PointCode { get; } public string PointName { get; } public string Location { get; } public string AnalysisProfile { get; } public string TestNames { get; } public string Status { get; } public int? SampleID { get; } public string SampleNumber { get; }
        }
        public sealed class WaterTestChoice
        {
            public bool IsSelected { get; set; } public int TestID { get; set; } public string TestName { get; set; } = ""; public string Category { get; set; } = "";
        }
        private sealed class WaterTestSelectionDialog : Window
        {
            private readonly List<WaterTestChoice> _choices;
            public IReadOnlyList<int> SelectedTestIds => _choices.Where(x => x.IsSelected).Select(x => x.TestID).ToList();
            public WaterTestSelectionDialog(List<WaterTestChoice> choices)
            {
                _choices=choices; Title="Select Approved Water Tests"; Width=680; Height=620; MinWidth=560; MinHeight=440; WindowStartupLocation=WindowStartupLocation.CenterOwner;
                var root=new Grid{Margin=new Thickness(16)}; root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto}); root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)}); root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
                var title=new TextBlock{Text="Select the tests to apply to every checked plan sample",FontSize=17,FontWeight=FontWeights.Bold,Margin=new Thickness(0,0,0,12)}; root.Children.Add(title);
                var grid=new DataGrid{ItemsSource=_choices,AutoGenerateColumns=false,CanUserAddRows=false,HeadersVisibility=DataGridHeadersVisibility.Column,SelectionMode=DataGridSelectionMode.Extended};
                grid.Columns.Add(new DataGridCheckBoxColumn{Header="Use",Binding=new System.Windows.Data.Binding("IsSelected"){UpdateSourceTrigger=System.Windows.Data.UpdateSourceTrigger.PropertyChanged},Width=60});
                grid.Columns.Add(new DataGridTextColumn{Header="Test",Binding=new System.Windows.Data.Binding("TestName"),Width=new DataGridLength(1,DataGridLengthUnitType.Star),IsReadOnly=true});
                grid.Columns.Add(new DataGridTextColumn{Header="Category",Binding=new System.Windows.Data.Binding("Category"),Width=180,IsReadOnly=true}); Grid.SetRow(grid,1); root.Children.Add(grid);
                var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
                var all=new Button{Content="Select All",Width=100,Height=34,Margin=new Thickness(0,0,8,0)}; all.Click+=(_,__)=>{foreach(var x in _choices)x.IsSelected=true;grid.Items.Refresh();};
                var clear=new Button{Content="Clear",Width=85,Height=34,Margin=new Thickness(0,0,8,0)}; clear.Click+=(_,__)=>{foreach(var x in _choices)x.IsSelected=false;grid.Items.Refresh();};
                var apply=new Button{Content="Apply",Width=100,Height=34,IsDefault=true}; apply.Click+=(_,__)=>{grid.CommitEdit(DataGridEditingUnit.Cell,true);grid.CommitEdit(DataGridEditingUnit.Row,true);if(!_choices.Any(x=>x.IsSelected)){MessageBox.Show("Select at least one approved test.","Water Tests",MessageBoxButton.OK,MessageBoxImage.Warning);return;}DialogResult=true;};
                var cancel=new Button{Content="Cancel",Width=90,Height=34,Margin=new Thickness(8,0,0,0),IsCancel=true}; actions.Children.Add(all);actions.Children.Add(clear);actions.Children.Add(apply);actions.Children.Add(cancel);Grid.SetRow(actions,2);root.Children.Add(actions);Content=root;
            }
        }
        private sealed class ScheduleConfig
        {
            public ScheduleConfig(SqlDataReader reader) { ScheduleID=Convert.ToInt32(reader["ScheduleID"]); ScheduleName=reader["ScheduleName"].ToString()??""; PlanType=reader["PlanType"].ToString()??""; Frequency=reader["Frequency"].ToString()??""; NextDueDate=Convert.ToDateTime(reader["NextDueDate"]); DaysAhead=Convert.ToInt32(reader["DaysAhead"]); Method=reader["Method"].ToString()??""; SamplingLocation=reader["SamplingLocation"].ToString()??""; MediaUsed=reader["MediaUsed"].ToString()??""; MediaLotNo=reader["MediaLotNo"].ToString()??""; MediaPreparationID=Convert.ToInt32(reader["MediaPreparationID"], CultureInfo.InvariantCulture); IncludeNegativeControl=reader["IncludeNegativeControl"]!=DBNull.Value&&Convert.ToBoolean(reader["IncludeNegativeControl"]); EmployeeID=reader["EmployeeID"].ToString()??""; EmployeeName=reader["EmployeeName"].ToString()??""; ApprovedPointCount=Convert.ToInt32(reader["ApprovedPointCount"],CultureInfo.InvariantCulture); }
            public int ScheduleID { get; } public int MediaPreparationID { get; } public int ApprovedPointCount { get; } public string ScheduleName { get; } public string PlanType { get; } public string Frequency { get; } public DateTime NextDueDate { get; } public int DaysAhead { get; } public string Method { get; } public string SamplingLocation { get; } public string MediaUsed { get; } public string MediaLotNo { get; } public bool IncludeNegativeControl { get; } public string EmployeeID { get; } public string EmployeeName { get; }
        }
    }
}
