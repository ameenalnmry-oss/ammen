#nullable disable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Models;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS
{
    public partial class QualityEventInvestigation
    {
        private void SaveCAPAItemsTable(SqlConnection connection, SqlTransaction transaction, string changeReason)
        {
            if (!V15TableExists("QualityEventCAPAItems"))
                return;

            string oldRowsJson = CaptureStructuredEvidenceSnapshot(connection, transaction, "QualityEventCAPAItems");

            DatabaseHelper.ExecuteNonQueryWithTransaction("DELETE FROM dbo.QualityEventCAPAItems WHERE QualityEventID = @QualityEventID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } }, connection, transaction);

            foreach (DataRow row in capaItemsTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus"))
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventCAPAItems (QualityEventID, ActionDescription, ActionType, Responsible, DueDate, EffectivenessCheck, CAPAStatus, CreatedBy, ModifiedBy)
                    VALUES (@QualityEventID, @ActionDescription, @ActionType, @Responsible, @DueDate, @EffectivenessCheck, @CAPAStatus, @User, @User)",
                    new[] {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@ActionDescription", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("ActionDescription") ?? DBNull.Value },
                        new SqlParameter("@ActionType", SqlDbType.NVarChar, 50) { Value = (object)row.GetSafeString("ActionType") ?? DBNull.Value },
                        new SqlParameter("@Responsible", SqlDbType.NVarChar, 150) { Value = (object)row.GetSafeString("Responsible") ?? DBNull.Value },
                        new SqlParameter("@DueDate", SqlDbType.Date) { Value = ParseDatabaseDateOrDBNull(row, "DueDate", "CAPA Due Date") },
                        new SqlParameter("@EffectivenessCheck", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("EffectivenessCheck") ?? DBNull.Value },
                        new SqlParameter("@CAPAStatus", SqlDbType.NVarChar, 50) { Value = (object)row.GetSafeString("CAPAStatus") ?? DBNull.Value },
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = currentUser }
                    }, connection, transaction);
            }
            RecordStructuredEvidenceHistory(connection, transaction, "QualityEventCAPAItems", oldRowsJson, changeReason);
        }

        private void SaveRetestingTable(SqlConnection connection, SqlTransaction transaction, string changeReason)
        {
            if (!V15TableExists("QualityEventRetesting"))
                return;

            string oldRowsJson = CaptureStructuredEvidenceSnapshot(connection, transaction, "QualityEventRetesting");

            DatabaseHelper.ExecuteNonQueryWithTransaction("DELETE FROM dbo.QualityEventRetesting WHERE QualityEventID = @QualityEventID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } }, connection, transaction);

            foreach (DataRow row in retestingTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "RetestPerformed", "SampleAliquot", "Analyst", "RetestResult", "Justification", "ScientificBasis", "DispositionUse"))
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventRetesting (QualityEventID, RetestPerformed, SampleAliquot, Analyst, RetestResult, Justification, ScientificBasis, DispositionUse, CreatedBy, ModifiedBy)
                    VALUES (@QualityEventID, @RetestPerformed, @SampleAliquot, @Analyst, @RetestResult, @Justification, @ScientificBasis, @DispositionUse, @User, @User)",
                    new[] {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@RetestPerformed", SqlDbType.NVarChar, 20) { Value = (object)row.GetSafeString("RetestPerformed") ?? DBNull.Value },
                        new SqlParameter("@SampleAliquot", SqlDbType.NVarChar, 200) { Value = (object)row.GetSafeString("SampleAliquot") ?? DBNull.Value },
                        new SqlParameter("@Analyst", SqlDbType.NVarChar, 150) { Value = (object)row.GetSafeString("Analyst") ?? DBNull.Value },
                        new SqlParameter("@RetestResult", SqlDbType.NVarChar, 200) { Value = (object)row.GetSafeString("RetestResult") ?? DBNull.Value },
                        new SqlParameter("@Justification", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("Justification") ?? DBNull.Value },
                        new SqlParameter("@ScientificBasis", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("ScientificBasis") ?? DBNull.Value },
                        new SqlParameter("@DispositionUse", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("DispositionUse") ?? DBNull.Value },
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = currentUser }
                    }, connection, transaction);
            }
            RecordStructuredEvidenceHistory(connection, transaction, "QualityEventRetesting", oldRowsJson, changeReason);
        }

        private void SaveDistributionTable(SqlConnection connection, SqlTransaction transaction, string changeReason)
        {
            if (!V15TableExists("QualityEventDistribution"))
                return;

            string oldRowsJson = CaptureStructuredEvidenceSnapshot(connection, transaction, "QualityEventDistribution");

            DatabaseHelper.ExecuteNonQueryWithTransaction("DELETE FROM dbo.QualityEventDistribution WHERE QualityEventID = @QualityEventID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } }, connection, transaction);

            foreach (DataRow row in distributionTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "Department", "Recipient", "DateReceived"))
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventDistribution (QualityEventID, Department, Recipient, DateReceived, CreatedBy, ModifiedBy)
                    VALUES (@QualityEventID, @Department, @Recipient, @DateReceived, @User, @User)",
                    new[] {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@Department", SqlDbType.NVarChar, 150) { Value = (object)row.GetSafeString("Department") ?? DBNull.Value },
                        new SqlParameter("@Recipient", SqlDbType.NVarChar, 150) { Value = (object)row.GetSafeString("Recipient") ?? DBNull.Value },
                        new SqlParameter("@DateReceived", SqlDbType.Date) { Value = ParseDatabaseDateOrDBNull(row, "DateReceived", "Distribution Date Received") },
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = currentUser }
                    }, connection, transaction);
            }
            RecordStructuredEvidenceHistory(connection, transaction, "QualityEventDistribution", oldRowsJson, changeReason);
        }

        private void FocusChecklistRow(DataRow row)
        {
            if (dgChecklist == null || row == null)
                return;

            try
            {
                object itemToSelect = null;

                foreach (object item in dgChecklist.Items)
                {
                    if (item is DataRowView dataRowView && ReferenceEquals(dataRowView.Row, row))
                    {
                        itemToSelect = item;
                        break;
                    }
                }

                if (itemToSelect == null && checklistTable != null)
                {
                    foreach (DataRowView dataRowView in checklistTable.DefaultView)
                    {
                        if (ReferenceEquals(dataRowView.Row, row))
                        {
                            itemToSelect = dataRowView;
                            break;
                        }
                    }
                }

                if (itemToSelect != null)
                {
                    dgChecklist.SelectedItem = itemToSelect;
                    dgChecklist.ScrollIntoView(itemToSelect);
                    dgChecklist.Focus();
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to focus the selected quality-event checklist row.", ex);
            }
        }

        private static Brush ReportNavyBrush() => new SolidColorBrush(Color.FromRgb(20, 66, 99));
        private static Brush ReportBlueGrayBrush() => new SolidColorBrush(Color.FromRgb(226, 238, 247));
        private static Brush ReportHeaderBrush() => new SolidColorBrush(Color.FromRgb(31, 45, 64));
        private static Brush ReportLightBrush() => new SolidColorBrush(Color.FromRgb(248, 250, 252));
        private static Brush ReportBorderBrush() => new SolidColorBrush(Color.FromRgb(203, 213, 225));
        private static Brush ReportMutedBrush() => new SolidColorBrush(Color.FromRgb(71, 85, 105));

        private Paragraph MakeParagraph(string text, bool bold = false, double fontSize = 10.2)
        {
            Paragraph paragraph = new Paragraph(new Run(text ?? string.Empty));
            paragraph.Margin = new Thickness(0, 3, 0, 5);
            paragraph.FontSize = fontSize;
            paragraph.LineHeight = fontSize + 3.2;
            paragraph.Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59));
            if (bold)
                paragraph.FontWeight = FontWeights.SemiBold;
            return paragraph;
        }

        private TableCell MakeCell(string text, bool bold = false)
        {
            return MakeReportTableCell(
                text,
                bold,
                bold ? 8.7 : 8.5,
                TextAlignment.Left,
                bold ? ReportBlueGrayBrush() : Brushes.White,
                bold ? ReportHeaderBrush() : Brushes.Black);
        }

        private static TableColumn ReportStarColumn(double weight)
        {
            return new TableColumn { Width = new GridLength(Math.Max(0.1, weight), GridUnitType.Star) };
        }

        private bool IsReportClosed()
        {
            return string.Equals(lblHeaderStatus.Text?.Trim(), "Closed", StringComparison.OrdinalIgnoreCase);
        }

        private Table MakeTwoColumnTable(params string[] values)
        {
            Table table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 3, 0, 8)
            };
            table.Columns.Add(ReportStarColumn(1.45));
            table.Columns.Add(ReportStarColumn(5.55));
            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                TableRow row = new TableRow();
                row.Cells.Add(MakeReportTableCell(values[i], true, 8.7, TextAlignment.Left, ReportBlueGrayBrush(), ReportHeaderBrush()));
                row.Cells.Add(MakeReportTableCell(values[i + 1], false, 8.7, TextAlignment.Left, Brushes.White, Brushes.Black));
                group.Rows.Add(row);
            }

            return table;
        }

        private double GetReportColumnWeight(string columnName)
        {
            switch (columnName ?? string.Empty)
            {
                case "SectionName": return 1.3;
                case "QuestionText": return 3.6;
                case "AnswerValue": return 0.75;
                case "Comments": return 2.35;
                case "TestName": return 2.0;
                case "ResultValue": return 1.0;
                case "SpecificationLimit": return 2.4;
                case "Unit": return 1.0;
                case "FailureType": return 0.9;
                case "WhyLevel": return 0.55;
                case "Question": return 3.4;
                case "Answer": return 3.0;
                case "AssessmentArea": return 2.2;
                case "Finding": return 3.2;
                case "ImpactStatus": return 1.2;
                case "ActionDescription": return 2.3;
                case "ActionType": return 1.0;
                case "Responsible": return 1.25;
                case "DueDate": return 1.15;
                case "EffectivenessCheck": return 2.5;
                case "CAPAStatus": return 1.35;
                case "Department": return 1.7;
                case "Recipient": return 2.1;
                case "DateReceived": return 1.4;
                case "RetestPerformed": return 1.0;
                case "SampleAliquot": return 1.4;
                case "PerformedBy": return 1.25;
                case "RetestResult": return 1.35;
                case "Justification": return 2.15;
                case "ScientificBasis": return 2.3;
                case "DispositionUse": return 1.9;
                default: return 1.5;
            }
        }

        private void AddDataTableSection(FlowDocument document, string title, DataTable table, params string[] columns)
        {
            if (!string.IsNullOrWhiteSpace(title))
                AddReportSubsectionTitle(document, title);

            if (table == null || table.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No records documented.", false, 9.2));
                return;
            }

            Table reportTable = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 3, 0, 8)
            };

            foreach (string column in columns)
                reportTable.Columns.Add(ReportStarColumn(GetReportColumnWeight(column)));

            TableRowGroup group = new TableRowGroup();
            reportTable.RowGroups.Add(group);

            TableRow header = new TableRow();
            foreach (string column in columns)
                header.Cells.Add(MakeReportTableCell(GetReportColumnHeader(column), true, 8.7, TextAlignment.Left, ReportHeaderBrush(), Brushes.White));
            group.Rows.Add(header);

            bool alternate = false;
            foreach (DataRow dataRow in table.Rows)
            {
                TableRow row = new TableRow();
                Brush background = alternate ? ReportLightBrush() : Brushes.White;
                foreach (string column in columns)
                {
                    TextAlignment alignment = column == "AnswerValue" || column == "FailureType" || column == "ImpactStatus" || column == "CAPAStatus"
                        ? TextAlignment.Center
                        : TextAlignment.Left;
                    row.Cells.Add(MakeReportTableCell(
                        dataRow.Table.Columns.Contains(column) ? dataRow.GetSafeString(column) : string.Empty,
                        false,
                        8.45,
                        alignment,
                        background,
                        Brushes.Black));
                }
                group.Rows.Add(row);
                alternate = !alternate;
            }

            document.Blocks.Add(reportTable);
        }

        private string GetReportColumnHeader(string columnName)
        {
            switch (columnName)
            {
                case "WhyLevel": return "Why";
                case "AssessmentArea": return "Assessment Area";
                case "ImpactStatus": return "Impact Status";
                case "RetestPerformed": return "Follow-up Performed";
                case "SampleAliquot": return currentEventIsEnvironmentalMonitoring ? "Monitoring Point / Plate / Sample" : "Sample / Aliquot";
                case "RetestResult": return "Follow-up Result";
                case "DispositionUse": return "Use in QA Disposition";
                case "ActionDescription": return "Action Description";
                case "ActionType": return "Type";
                case "DueDate": return "Due Date";
                case "EffectivenessCheck": return "Effectiveness Check / Acceptance Criterion";
                case "CAPAStatus": return "Status";
                case "DateReceived": return "Date Received";
                case "SectionName": return "Section";
                case "QuestionText": return "Checklist Question";
                case "AnswerValue": return "Answer";
                case "Comments": return "Comments / Evidence";
                default: return columnName;
            }
        }

        private TableCell MakeReportTableCell(
            string text,
            bool bold = false,
            double fontSize = 8.6,
            TextAlignment alignment = TextAlignment.Left,
            Brush background = null,
            Brush foreground = null)
        {
            Paragraph paragraph = new Paragraph(new Run(text ?? string.Empty));
            paragraph.Margin = new Thickness(0);
            paragraph.FontSize = fontSize;
            paragraph.TextAlignment = alignment;
            paragraph.LineHeight = fontSize + 3.0;
            paragraph.Foreground = foreground ?? Brushes.Black;
            if (bold)
                paragraph.FontWeight = FontWeights.SemiBold;

            TableCell cell = new TableCell(paragraph)
            {
                Padding = new Thickness(6, 4.5, 6, 4.5),
                BorderBrush = ReportBorderBrush(),
                BorderThickness = new Thickness(0.55),
                Background = background ?? Brushes.White
            };
            return cell;
        }

        private void AddInvestigationChecklistSection(FlowDocument document, DataTable table)
        {
            document.Blocks.Add(MakeParagraph("Investigation Checklist", true, 14));

            if (table == null || table.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No checklist records found."));
                return;
            }

            Table reportTable = new Table();
            reportTable.CellSpacing = 0;
            reportTable.Columns.Add(new TableColumn { Width = new GridLength(125) });
            reportTable.Columns.Add(new TableColumn { Width = new GridLength(335) });
            reportTable.Columns.Add(new TableColumn { Width = new GridLength(70) });
            reportTable.Columns.Add(new TableColumn { Width = new GridLength(190) });

            TableRowGroup group = new TableRowGroup();
            reportTable.RowGroups.Add(group);

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportTableCell("Section", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Checklist Question", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Answer", true, 9.5, TextAlignment.Center));
            header.Cells.Add(MakeReportTableCell("Comments / Evidence", true, 9.5));
            group.Rows.Add(header);

            foreach (DataRow dataRow in table.Rows)
            {
                string section = table.Columns.Contains("SectionName") ? dataRow.GetSafeString("SectionName") : "";
                string question = table.Columns.Contains("QuestionText") ? dataRow.GetSafeString("QuestionText") : "";
                string answer = table.Columns.Contains("AnswerValue") ? dataRow.GetSafeString("AnswerValue") : "";
                string comments = table.Columns.Contains("Comments") ? dataRow.GetSafeString("Comments") : "";

                TableRow row = new TableRow();
                row.Cells.Add(MakeReportTableCell(section, false, 8.8));
                row.Cells.Add(MakeReportTableCell(question, false, 8.8));
                row.Cells.Add(MakeReportTableCell(answer, false, 8.8, TextAlignment.Center));
                row.Cells.Add(MakeReportTableCell(comments, false, 8.8));
                group.Rows.Add(row);
            }

            document.Blocks.Add(reportTable);
        }

        private string GenerateInvestigationReportNumber()
        {
            if (!loadedEventCreatedDate.HasValue)
                throw new InvalidOperationException("Quality Event Created Date is missing. A controlled investigation report number cannot be generated.");

            return "QEIR-" + loadedEventCreatedDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                   "-" + qualityEventId.ToString("0000", CultureInfo.InvariantCulture);
        }

        private TableCell MakeHeaderCell(string text, bool bold = false, double fontSize = 10, TextAlignment alignment = TextAlignment.Left)
        {
            TableCell cell = new TableCell();
            Paragraph paragraph = new Paragraph(new Run(text ?? ""));
            paragraph.Margin = new Thickness(0);
            paragraph.TextAlignment = alignment;
            paragraph.FontSize = fontSize;
            if (bold)
                paragraph.FontWeight = FontWeights.Bold;

            cell.Blocks.Add(paragraph);
            cell.Padding = new Thickness(6);
            cell.BorderBrush = Brushes.LightGray;
            cell.BorderThickness = new Thickness(0.5);
            return cell;
        }

        private void AddOfficialReportHeader(FlowDocument document, string reportNumber)
        {
            Table headerTable = new Table();
            headerTable.CellSpacing = 0;
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(110) });
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(360) });
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(210) });

            TableRowGroup group = new TableRowGroup();
            headerTable.RowGroups.Add(group);

            TableRow row = new TableRow();

            TableCell logoCell = MakeHeaderCell("M", true, 26, TextAlignment.Center);
            logoCell.Padding = new Thickness(10);
            row.Cells.Add(logoCell);

            TableCell companyCell = new TableCell();
            companyCell.Padding = new Thickness(8);
            companyCell.BorderBrush = Brushes.LightGray;
            companyCell.BorderThickness = new Thickness(0.5);
            Paragraph company = new Paragraph(new Run("MEDICA PHARMACEUTICAL INDUSTRY"));
            company.TextAlignment = TextAlignment.Center;
            company.FontSize = 18;
            company.FontWeight = FontWeights.Bold;
            company.Margin = new Thickness(0, 0, 0, 3);
            Paragraph department = new Paragraph(new Run("Microbiology Department"));
            department.TextAlignment = TextAlignment.Center;
            department.FontSize = 11;
            department.Margin = new Thickness(0);
            companyCell.Blocks.Add(company);
            companyCell.Blocks.Add(department);
            row.Cells.Add(companyCell);

            TableCell formCell = MakeHeaderCell(
                "Form No: MQC-F-QE-001\nVersion: 01\nPage: Generated Report",
                false,
                10,
                TextAlignment.Right);
            row.Cells.Add(formCell);

            group.Rows.Add(row);
            document.Blocks.Add(headerTable);

            string reportTitleText = lblHeaderStatus.Text.Equals("Closed", StringComparison.OrdinalIgnoreCase) ? "QUALITY EVENT INVESTIGATION REPORT" : "DRAFT QUALITY EVENT INVESTIGATION REPORT";
            Paragraph title = new Paragraph(new Run(reportTitleText));
            title.TextAlignment = TextAlignment.Center;
            title.FontSize = 16;
            title.FontWeight = FontWeights.Bold;
            title.Margin = new Thickness(0, 12, 0, 2);
            document.Blocks.Add(title);

            Paragraph subtitle = new Paragraph(new Run("OOS / OOT / Deviation Investigation and QA Disposition"));
            subtitle.TextAlignment = TextAlignment.Center;
            subtitle.FontSize = 11;
            subtitle.Foreground = Brushes.DimGray;
            subtitle.Margin = new Thickness(0, 0, 0, 10);
            document.Blocks.Add(subtitle);

            if (!lblHeaderStatus.Text.Equals("Closed", StringComparison.OrdinalIgnoreCase))
            {
                Paragraph draftNotice = new Paragraph(new Run("DRAFT - Event is not QA closed. This report is for investigation review only."));
                draftNotice.TextAlignment = TextAlignment.Center;
                draftNotice.FontSize = 11;
                draftNotice.FontWeight = FontWeights.Bold;
                draftNotice.Foreground = Brushes.Firebrick;
                draftNotice.Margin = new Thickness(0, 0, 0, 10);
                document.Blocks.Add(draftNotice);
            }

            Table meta = new Table();
            meta.CellSpacing = 0;
            meta.Columns.Add(new TableColumn { Width = new GridLength(160) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(210) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(150) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(160) });
            TableRowGroup metaGroup = new TableRowGroup();
            meta.RowGroups.Add(metaGroup);

            TableRow metaRow1 = new TableRow();
            metaRow1.Cells.Add(MakeHeaderCell("Report No", true));
            metaRow1.Cells.Add(MakeHeaderCell(reportNumber));
            metaRow1.Cells.Add(MakeHeaderCell("Event No", true));
            metaRow1.Cells.Add(MakeHeaderCell(lblEventNumber.Text));
            metaGroup.Rows.Add(metaRow1);

            TableRow metaRow2 = new TableRow();
            metaRow2.Cells.Add(MakeHeaderCell("Printed Date", true));
            metaRow2.Cells.Add(MakeHeaderCell(DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            metaRow2.Cells.Add(MakeHeaderCell("Printed By", true));
            metaRow2.Cells.Add(MakeHeaderCell(currentUser));
            metaGroup.Rows.Add(metaRow2);

            TableRow metaRow3 = new TableRow();
            metaRow3.Cells.Add(MakeHeaderCell("Event Status", true));
            metaRow3.Cells.Add(MakeHeaderCell(lblHeaderStatus.Text));
            metaRow3.Cells.Add(MakeHeaderCell("Final Disposition", true));
            metaRow3.Cells.Add(MakeHeaderCell(GetComboText(cboFinalDisposition)));
            metaGroup.Rows.Add(metaRow3);

            document.Blocks.Add(meta);
        }

        private void AddOfficialReportFooter(FlowDocument document)
        {
            Paragraph separator = new Paragraph(new Run(""));
            separator.BorderBrush = Brushes.LightGray;
            separator.BorderThickness = new Thickness(0, 0.5, 0, 0);
            separator.Margin = new Thickness(0, 12, 0, 4);
            document.Blocks.Add(separator);

            string verificationCode = BuildVerificationCode();
            Paragraph footer = new Paragraph(new Run(
                "This report is electronically generated from PharmaLIMS. " +
                "Approval, closure, and print activities are controlled by electronic records and audit trail.\n" +
                "Verification Code: " + verificationCode));
            footer.FontSize = 9;
            footer.Foreground = Brushes.DimGray;
            footer.TextAlignment = TextAlignment.Center;
            footer.Margin = new Thickness(0, 0, 0, 2);
            document.Blocks.Add(footer);
        }

        private string BuildVerificationCode()
        {
            string eventNumber = string.IsNullOrWhiteSpace(lblEventNumber.Text) ? "QE" + qualityEventId.ToString("0000") : lblEventNumber.Text.Trim();
            return eventNumber + "-QEIR-V15.7";
        }

        private void AddReportSectionTitle(FlowDocument document, string title)
        {
            Paragraph paragraph = new Paragraph(new Run(title ?? string.Empty));
            paragraph.FontSize = 12.5;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Foreground = Brushes.White;
            paragraph.Background = ReportHeaderBrush();
            paragraph.Padding = new Thickness(7, 5, 7, 5);
            paragraph.Margin = new Thickness(0, 11, 0, 6);
            document.Blocks.Add(paragraph);
        }

        private void AddReportSubsectionTitle(FlowDocument document, string title)
        {
            Paragraph paragraph = new Paragraph(new Run(title ?? string.Empty));
            paragraph.FontSize = 10.4;
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Foreground = ReportNavyBrush();
            paragraph.Background = ReportBlueGrayBrush();
            paragraph.Padding = new Thickness(6, 3, 6, 3);
            paragraph.Margin = new Thickness(0, 6, 0, 3);
            document.Blocks.Add(paragraph);
        }

        private string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "Not documented";
        }

        private string FindChecklistAnswer(DataTable table, params string[] keywords)
        {
            if (table == null || table.Rows.Count == 0)
                return "Not documented";

            foreach (DataRow row in table.Rows)
            {
                string question = table.Columns.Contains("QuestionText") ? row.GetSafeString("QuestionText") : "";
                string section = table.Columns.Contains("SectionName") ? row.GetSafeString("SectionName") : "";
                string combined = (section + " " + question).ToLowerInvariant();

                bool match = true;
                foreach (string keyword in keywords)
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                        continue;
                    if (!combined.Contains(keyword.ToLowerInvariant()))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    string answer = table.Columns.Contains("AnswerValue") ? row.GetSafeString("AnswerValue") : "";
                    string comments = table.Columns.Contains("Comments") ? row.GetSafeString("Comments") : "";
                    if (string.IsNullOrWhiteSpace(answer) && string.IsNullOrWhiteSpace(comments))
                        return "Not documented";
                    if (string.IsNullOrWhiteSpace(comments))
                        return answer;
                    if (string.IsNullOrWhiteSpace(answer))
                        return comments;
                    return answer + " - " + comments;
                }
            }

            return "Not documented";
        }

        private string BuildPhaseOneConclusion(DataTable checklist)
        {
            if (checklist == null || checklist.Rows.Count == 0)
                return "Phase I is incomplete. No checklist records are available; laboratory/sampling error cannot be excluded.";

            string phaseSection = NormalizeChecklistIdentity("Phase I - Laboratory Review");
            List<DataRow> phaseRows = new List<DataRow>();
            foreach (DataRow row in checklist.Rows)
            {
                if (row.RowState != DataRowState.Deleted &&
                    NormalizeChecklistIdentity(row.GetSafeString("SectionName")) == phaseSection)
                    phaseRows.Add(row);
            }

            if (phaseRows.Count < 9)
                return "Phase I is incomplete. Required laboratory/sampling review controls are missing; laboratory error cannot be excluded.";

            List<string> incomplete = new List<string>();
            List<string> adverse = new List<string>();
            foreach (DataRow row in phaseRows)
            {
                string answer = row.GetSafeString("AnswerValue").Trim();
                string comments = row.GetSafeString("Comments").Trim();
                if (string.IsNullOrWhiteSpace(answer) || comments.Length < 12)
                {
                    incomplete.Add(row.GetSafeString("QuestionText"));
                    continue;
                }
                if (answer.Equals("No", StringComparison.OrdinalIgnoreCase))
                    adverse.Add(row.GetSafeString("QuestionText"));
            }

            if (incomplete.Count > 0)
                return "Phase I is incomplete. Laboratory/sampling error cannot be excluded until all required review controls and evidence are documented.";

            if (adverse.Count > 0)
                return "Phase I identified a potential laboratory, sampling, method, equipment, or data-integrity contribution. The finding must be resolved or incorporated into root-cause and impact assessment before final disposition. First adverse item: " + adverse[0];

            return "Phase I laboratory/sampling review is complete. No attributable laboratory or sampling error was identified from the documented controls and evidence; proceed to broader environmental/process root-cause and impact assessment.";
        }

        private bool IsAffirmativeAnswer(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
                return false;

            string normalized = answer.Trim().ToLowerInvariant();
            return normalized == "yes" || normalized == "y" || normalized == "true" || normalized == "1";
        }

        private void AddPhaseOneLaboratoryInvestigation(FlowDocument document, DataTable checklist)
        {
            AddReportSectionTitle(document, "Phase I: Laboratory Investigation");
            document.Blocks.Add(MakeParagraph("Objective: Determine whether the event can be attributed to a laboratory, method, instrument, sampling, or data handling issue before moving to broader impact/root-cause assessment."));

            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(ReportStarColumn(1.75));
            table.Columns.Add(ReportStarColumn(1.0));
            table.Columns.Add(ReportStarColumn(3.85));
            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportTableCell("Review Area", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Status", true, 9.5, TextAlignment.Center));
            header.Cells.Add(MakeReportTableCell("Evidence / Comment", true, 9.5));
            group.Rows.Add(header);

            AddPhaseRow(group, "Analyst training / authorization", FindChecklistAnswer(checklist, "training", "authorization"));
            AddPhaseRow(group, "Analyst / sampler interview", FindChecklistAnswer(checklist, "interview", "sampler"));
            AddPhaseRow(group, "Calculation / transcription / result entry", FindChecklistAnswer(checklist, "calculation", "transcription"));
            AddPhaseRow(group, "Approved method / SOP", FindChecklistAnswer(checklist, "approved", "method/sop"));
            AddPhaseRow(group, "Acceptance criteria / limit interpretation", FindChecklistAnswer(checklist, "acceptance criteria", "alert/action"));
            AddPhaseRow(group, "Sample / plate identity and chain of custody", FindChecklistAnswer(checklist, "chain of custody"));
            AddPhaseRow(group, "Sampling / transport / handling", FindChecklistAnswer(checklist, "transport", "holding time"));
            AddPhaseRow(group, "Raw data and audit trail", FindChecklistAnswer(checklist, "raw data", "audit trail"));
            AddPhaseRow(group, "Equipment / calibration / incubator / controls", FindChecklistAnswer(checklist, "equipment/instrument", "calibration"));

            document.Blocks.Add(table);
            document.Blocks.Add(MakeParagraph("Phase I Conclusion: " + BuildPhaseOneConclusion(checklist)));
        }

        private void AddPhaseRow(TableRowGroup group, string area, string evidence)
        {
            string status = "Reviewed";
            if (string.IsNullOrWhiteSpace(evidence) || evidence == "Not documented")
                status = "Not documented";

            TableRow row = new TableRow();
            row.Cells.Add(MakeReportTableCell(area, false, 8.8));
            row.Cells.Add(MakeReportTableCell(status, false, 8.8, TextAlignment.Center));
            row.Cells.Add(MakeReportTableCell(evidence, false, 8.8));
            group.Rows.Add(row);
        }

        private void AddPhaseTwoRootCauseAndImpact(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase II: Full-Scale Investigation / Root Cause and Impact Assessment");
            document.Blocks.Add(MakeParagraph("Objective: Document the confirmed or most probable root cause, assess impact, and define QA disposition based on structured investigation records."));
            document.Blocks.Add(MakeTwoColumnTable(
                "Root Cause Category", FirstNonEmpty(GetComboText(cboRootCauseCategory)),
                "Root Cause Details", FirstNonEmpty(txtRootCauseDetails.Text),
                "CAPA Required", chkCAPARequired.IsChecked == true ? "Yes" : "No",
                "QA Conclusion", FirstNonEmpty(txtQAConclusion.Text),
                "Final Disposition", FirstNonEmpty(GetComboText(cboFinalDisposition))));

            AddRootCauseWhysReportSection(document);
            AddImpactAssessmentReportSection(document);
            AddRetestingReportSection(document);
        }

        private void AddRootCauseWhysReportSection(FlowDocument document)
        {
            AddReportSectionTitle(document, "Root Cause Analysis - 5 Whys");
            DataTable rows = FilterMeaningfulRows(rootCauseWhysTable, "Answer");
            if (rows.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No structured 5 Whys answers documented."));
                return;
            }

            AddDataTableSection(document, "", rows, "WhyLevel", "Question", "Answer");
        }

        private void AddImpactAssessmentReportSection(FlowDocument document)
        {
            AddReportSectionTitle(document, "Impact Assessment");
            DataTable rows = FilterMeaningfulRows(impactAssessmentTable, "Finding", "ImpactStatus");
            if (rows.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No structured impact assessment rows documented."));
            }
            else
            {
                AddDataTableSection(document, "", rows, "AssessmentArea", "Finding", "ImpactStatus");
            }

            if (!string.IsNullOrWhiteSpace(txtImpactAssessment.Text))
                document.Blocks.Add(MakeParagraph("Overall impact assessment: " + txtImpactAssessment.Text.Trim()));
        }

        private void AddRetestingReportSection(FlowDocument document)
        {
            AddReportSectionTitle(document, currentEventIsEnvironmentalMonitoring
                ? "Follow-up Environmental Monitoring / Resampling Strategy"
                : "Re-evaluation and Retesting Strategy");
            DataTable rows = FilterMeaningfulRows(retestingTable, "RetestPerformed");
            if (rows.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No structured retesting strategy documented."));
                return;
            }

            int recordNumber = 1;
            foreach (DataRow row in rows.Rows)
            {
                if (rows.Rows.Count > 1)
                    document.Blocks.Add(MakeParagraph("Retesting Record " + recordNumber, true, 11));

                document.Blocks.Add(MakeTwoColumnTable(
                    currentEventIsEnvironmentalMonitoring ? "Follow-up Performed" : "Retest Performed", FirstNonEmpty(row.GetSafeString("RetestPerformed")),
                    currentEventIsEnvironmentalMonitoring ? "Monitoring Point / Plate / Sample" : "Sample / Aliquot", FirstNonEmpty(row.GetSafeString("SampleAliquot")),
                    currentEventIsEnvironmentalMonitoring ? "Performed By" : "Analyst", FirstNonEmpty(row.GetSafeString("Analyst")),
                    currentEventIsEnvironmentalMonitoring ? "Follow-up Result" : "Retest Result", FirstNonEmpty(row.GetSafeString("RetestResult")),
                    "Justification", FirstNonEmpty(row.GetSafeString("Justification")),
                    "Scientific Basis", FirstNonEmpty(row.GetSafeString("ScientificBasis")),
                    "Use in QA Disposition (Narrative)", FirstNonEmpty(row.GetSafeString("DispositionUse"))));
                recordNumber++;
            }
        }

        private void AddCAPASection(FlowDocument document)
        {
            AddReportSectionTitle(document, "CAPA / Corrective and Preventive Action Plan");
            document.Blocks.Add(MakeParagraph("CAPA Required: " + (chkCAPARequired.IsChecked == true ? "Yes" : "No")));

            DataTable rows = FilterMeaningfulRows(capaItemsTable, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus");
            if (rows.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No structured CAPA action items documented."));
                return;
            }

            string[] columns = GetMeaningfulColumns(rows,
                new string[] { "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus" },
                new string[] { "ActionDescription" });
            AddDataTableSection(document, "", rows, columns);

            bool hasEffectiveness = false;
            bool hasDueDate = false;
            foreach (DataRow row in rows.Rows)
            {
                if (!string.IsNullOrWhiteSpace(row.GetSafeString("EffectivenessCheck")))
                    hasEffectiveness = true;
                if (!string.IsNullOrWhiteSpace(row.GetSafeString("DueDate")))
                    hasDueDate = true;
            }

            if (hasEffectiveness)
            {
                document.Blocks.Add(MakeParagraph("CAPA effectiveness verification: Documented in the CAPA action item table above."));
            }
            else if (chkCAPARequired.IsChecked == true)
            {
                document.Blocks.Add(MakeParagraph("CAPA effectiveness verification: Not separately documented in this record. Follow-up should be controlled through the CAPA follow-up process."));
            }

            if (!hasDueDate && chkCAPARequired.IsChecked == true)
            {
                document.Blocks.Add(MakeParagraph("CAPA due date status: Not separately documented for the listed action item(s)."));
            }
        }

        private void AddDistributionSection(FlowDocument document)
        {
            DataTable rows = FilterMeaningfulRows(distributionTable, "Recipient", "DateReceived");
            if (rows.Rows.Count == 0)
                return;

            AddReportSectionTitle(document, "Distribution List");
            AddDataTableSection(document, "", rows, "Department", "Recipient", "DateReceived");
        }

        private DataTable FilterMeaningfulRows(DataTable source, params string[] meaningfulColumns)
        {
            if (source == null)
                return new DataTable();

            DataTable filtered = source.Clone();
            foreach (DataRow row in source.Rows)
            {
                bool hasMeaningfulValue = false;
                foreach (string column in meaningfulColumns)
                {
                    if (row.Table.Columns.Contains(column) && !string.IsNullOrWhiteSpace(row.GetSafeString(column)))
                    {
                        hasMeaningfulValue = true;
                        break;
                    }
                }

                if (hasMeaningfulValue)
                    filtered.ImportRow(row);
            }

            return filtered;
        }

        private string[] GetMeaningfulColumns(DataTable source, string[] candidateColumns, string[] requiredColumns)
        {
            var result = new System.Collections.Generic.List<string>();

            foreach (string column in candidateColumns)
            {
                bool include = false;

                foreach (string required in requiredColumns)
                {
                    if (string.Equals(required, column, StringComparison.OrdinalIgnoreCase))
                    {
                        include = true;
                        break;
                    }
                }

                if (!include && source != null && source.Columns.Contains(column))
                {
                    foreach (DataRow row in source.Rows)
                    {
                        if (!string.IsNullOrWhiteSpace(row.GetSafeString(column)))
                        {
                            include = true;
                            break;
                        }
                    }
                }

                if (include)
                    result.Add(column);
            }

            return result.ToArray();
        }

        private void AddDataIntegritySection(FlowDocument document, DataTable checklist, DataTable actions)
        {
            AddReportSectionTitle(document, "Data Integrity Verification (ALCOA+)");
            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(new TableColumn { Width = new GridLength(160) });
            table.Columns.Add(new TableColumn { Width = new GridLength(90) });
            table.Columns.Add(new TableColumn { Width = new GridLength(440) });
            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportTableCell("ALCOA+ Principle", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Status", true, 9.5, TextAlignment.Center));
            header.Cells.Add(MakeReportTableCell("Evidence / Comment", true, 9.5));
            group.Rows.Add(header);

            string rawAuditEvidence = FindChecklistAnswer(checklist, "raw data", "audit trail");
            string unexplainedActivityEvidence = FindChecklistAnswer(checklist, "unexplained", "data activity");
            string actionHistoryEvidence = actions != null && actions.Rows.Count > 0 ? actions.Rows.Count + " recorded action(s)." : "No action history records found.";

            AddDataIntegrityRow(group, "Attributable", "Recorded", "User attribution captured through PharmaLIMS user/action records and electronic workflow.");
            AddDataIntegrityRow(group, "Legible", "Recorded", "Electronic records are generated in a readable report format.");
            AddDataIntegrityRow(group, "Contemporaneous", "Recorded", "System timestamps are retained in the event action history. " + actionHistoryEvidence);
            AddDataIntegrityRow(group, "Original", EvidenceStatus(rawAuditEvidence), rawAuditEvidence);
            AddDataIntegrityRow(group, "Accurate", EvidenceStatus(unexplainedActivityEvidence), "Unexplained data activity: " + unexplainedActivityEvidence);
            AddDataIntegrityRow(group, "Complete", EvidenceStatus(rawAuditEvidence), "Required checklist, action history, and investigation sections are controlled by the event workflow.");
            AddDataIntegrityRow(group, "Consistent", "Recorded", "Chronological sequence is maintained in the action history. " + actionHistoryEvidence);
            AddDataIntegrityRow(group, "Enduring", "Recorded", "Records are retained in PharmaLIMS database tables.");
            AddDataIntegrityRow(group, "Available", "Recorded", "Report and event records are available from the Quality Event Investigation screen.");

            document.Blocks.Add(table);
        }

        private string EvidenceStatus(string evidence)
        {
            if (string.IsNullOrWhiteSpace(evidence) || evidence == "Not documented")
                return "Not documented";

            return "Recorded";
        }

        private void AddDataIntegrityRow(TableRowGroup group, string aspect, string status, string evidence)
        {
            if (string.IsNullOrWhiteSpace(evidence))
                evidence = "Not documented";

            TableRow row = new TableRow();
            row.Cells.Add(MakeReportTableCell(aspect, false, 8.8));
            row.Cells.Add(MakeReportTableCell(status, false, 8.8, TextAlignment.Center));
            row.Cells.Add(MakeReportTableCell(evidence, false, 8.8));
            group.Rows.Add(row);
        }

        private void AddAmendmentAndApprovalSummary(FlowDocument document, DataTable actions)
        {
            AddReportSectionTitle(document, "Amendment / Revision Log");
            if (actions == null || actions.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No amendment, review, or approval activity records found."));
                return;
            }

            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(new TableColumn { Width = new GridLength(70) });
            table.Columns.Add(new TableColumn { Width = new GridLength(130) });
            table.Columns.Add(new TableColumn { Width = new GridLength(310) });
            table.Columns.Add(new TableColumn { Width = new GridLength(100) });
            table.Columns.Add(new TableColumn { Width = new GridLength(110) });
            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportTableCell("#", true, 9.5, TextAlignment.Center));
            header.Cells.Add(MakeReportTableCell("Date", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Description of Change / Activity", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Modified By", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Reason", true, 9.5));
            group.Rows.Add(header);

            int number = 1;
            foreach (DataRow action in actions.Rows)
            {
                string actionType = action.Table.Columns.Contains("ActionType") ? action.GetSafeString("ActionType") : "";
                string lowerAction = actionType.ToLowerInvariant();
                if (!lowerAction.Contains("opened") &&
                    !lowerAction.Contains("investigation") &&
                    !lowerAction.Contains("submit") &&
                    !lowerAction.Contains("qa") &&
                    !lowerAction.Contains("closure"))
                    continue;

                string description = action.Table.Columns.Contains("ActionDescription") ? action.GetSafeString("ActionDescription") : "";
                string reason = BuildAmendmentReason(actionType);

                TableRow row = new TableRow();
                row.Cells.Add(MakeReportTableCell(number.ToString("000"), false, 8.8, TextAlignment.Center));
                row.Cells.Add(MakeReportTableCell(action.Table.Columns.Contains("PerformedDate") ? action.GetSafeString("PerformedDate") : "", false, 8.8));
                row.Cells.Add(MakeReportTableCell(FirstNonEmpty(actionType + (string.IsNullOrWhiteSpace(description) ? "" : " - " + description)), false, 8.8));
                row.Cells.Add(MakeReportTableCell(action.Table.Columns.Contains("PerformedBy") ? action.GetSafeString("PerformedBy") : "", false, 8.8));
                row.Cells.Add(MakeReportTableCell(reason, false, 8.8));
                group.Rows.Add(row);
                number++;
            }

            if (number == 1)
            {
                document.Blocks.Add(MakeParagraph("No key amendment, review, or approval activity records found."));
                return;
            }

            document.Blocks.Add(table);
        }

        private string BuildAmendmentReason(string actionType)
        {
            string action = (actionType ?? "").ToLowerInvariant();
            if (action.Contains("opened")) return "Event opened";
            if (action.Contains("submit")) return "QA review";
            if (action.Contains("closure")) return "Final disposition";
            if (action.Contains("qa")) return "QA decision";
            if (action.Contains("investigation")) return "Investigation update";
            return "Controlled record";
        }

        private ImageSource TryLoadQualityEventReportLogoSource()
        {
            try
            {
                string localLogo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medica-logo.png");
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = File.Exists(localLogo)
                    ? new Uri(localLogo, UriKind.Absolute)
                    : new Uri("pack://siteoforigin:,,,/medica-logo.png", UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to load the MEDICA logo for the Quality Event report.", ex);
                return null;
            }
        }

        private Image TryCreateQualityEventReportLogo(double width, double height)
        {
            ImageSource source = TryLoadQualityEventReportLogoSource();
            if (source == null)
                return null;

            return new Image
            {
                Source = source,
                Width = width,
                Height = height,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private IDocumentPaginatorSource CreateQualityEventReportPaginatorSource(FlowDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            IDocumentPaginatorSource source = document;
            source.DocumentPaginator.ComputePageCount();
            return new QualityEventPaginatorSource(
                new QualityEventReportPaginator(
                    source.DocumentPaginator,
                    BuildVerificationCode(),
                    "MQC-F-QE-002 | Version 05",
                    TryLoadQualityEventReportLogoSource()));
        }

        private FlowDocument BuildGlobalInvestigationReportDocument()
        {
            FlowDocument document = new FlowDocument
            {
                PagePadding = new Thickness(34, 42, 34, 48),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 9.8,
                ColumnWidth = double.PositiveInfinity,
                ColumnGap = 0,
                TextAlignment = TextAlignment.Left
            };

            AddGlobalReportHeader(document);
            AddPhaseOneLaboratoryInvestigation(document, checklistTable);

            if (IsCurrentWaterEvent())
                AddPhaseIIWaterSystemInvestigation(document);
            else
                AddPhaseIIEnvironmentalInvestigation(document);

            AddPhaseIIIRootCauseAnalysis(document);
            AddPhaseIVImpactAssessment(document);
            AddRetestingReportSection(document);
            AddPhaseVCAPAPlan(document);
            AddPhaseVIDataIntegrity(document);
            AddPhaseVIIQADisposition(document);
            AddPhaseVIIIDistribution(document);
            AddGlobalReportFooter(document);

            return document;
        }

        private void AddGlobalReportHeader(FlowDocument document)
        {
            Table headerTable = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 0, 0, 7)
            };
            headerTable.Columns.Add(ReportStarColumn(1.35));
            headerTable.Columns.Add(ReportStarColumn(4.25));
            headerTable.Columns.Add(ReportStarColumn(2.40));

            TableRowGroup group = new TableRowGroup();
            headerTable.RowGroups.Add(group);
            TableRow row = new TableRow();

            TableCell logoCell = new TableCell
            {
                Background = Brushes.White,
                BorderBrush = ReportBorderBrush(),
                BorderThickness = new Thickness(0.55),
                Padding = new Thickness(7)
            };
            Image logoImage = TryCreateQualityEventReportLogo(112, 50);
            if (logoImage != null)
            {
                BlockUIContainer logoBlock = new BlockUIContainer(logoImage)
                {
                    Margin = new Thickness(0)
                };
                logoCell.Blocks.Add(logoBlock);
            }
            else
            {
                Paragraph fallbackLogo = new Paragraph(new Run("MEDICA"))
                {
                    TextAlignment = TextAlignment.Center,
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = ReportHeaderBrush(),
                    Margin = new Thickness(0)
                };
                logoCell.Blocks.Add(fallbackLogo);
            }
            row.Cells.Add(logoCell);

            TableCell companyCell = new TableCell
            {
                Background = ReportHeaderBrush(),
                Padding = new Thickness(10, 9, 10, 9),
                BorderBrush = ReportHeaderBrush(),
                BorderThickness = new Thickness(0.55)
            };
            Paragraph company = new Paragraph(new Run("MEDICA PHARMACEUTICAL INDUSTRY"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 14.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 3)
            };
            Paragraph dept = new Paragraph(new Run("Microbiology Department"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 9.6,
                Foreground = Brushes.White,
                Margin = new Thickness(0)
            };
            companyCell.Blocks.Add(company);
            companyCell.Blocks.Add(dept);
            row.Cells.Add(companyCell);

            TableCell infoCell = MakeReportTableCell(
                "Form No: MQC-F-QE-002\nVersion: 05 (Balanced GMP Report)\nDocument: Controlled Investigation Report",
                false,
                8.35,
                TextAlignment.Left,
                ReportHeaderBrush(),
                Brushes.White);
            row.Cells.Add(infoCell);

            group.Rows.Add(row);
            document.Blocks.Add(headerTable);

            Paragraph title = new Paragraph(new Run(currentEventIsEnvironmentalMonitoring
                ? "ENVIRONMENTAL MONITORING QUALITY EVENT INVESTIGATION REPORT"
                : "QUALITY EVENT INVESTIGATION REPORT"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 16.5,
                FontWeight = FontWeights.Bold,
                Foreground = ReportHeaderBrush(),
                Margin = new Thickness(0, 8, 0, 1)
            };
            document.Blocks.Add(title);

            Paragraph subtitle = new Paragraph(new Run(currentEventIsEnvironmentalMonitoring
                ? "Controlled Environmental Monitoring investigation, impact assessment, CAPA and QA disposition"
                : "Controlled Quality Event investigation, impact assessment, CAPA and QA disposition"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 9.5,
                Foreground = ReportMutedBrush(),
                Margin = new Thickness(0, 0, 0, 8)
            };
            document.Blocks.Add(subtitle);

            if (!IsReportClosed())
            {
                Paragraph draftBanner = new Paragraph(new Run("DRAFT - INVESTIGATION OPEN - NOT FOR FINAL GMP RELEASE"))
                {
                    TextAlignment = TextAlignment.Center,
                    FontSize = 9.2,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)),
                    Background = new SolidColorBrush(Color.FromRgb(255, 247, 237)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(251, 146, 60)),
                    BorderThickness = new Thickness(0.7),
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                document.Blocks.Add(draftBanner);
            }

            Table meta = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 0, 0, 7)
            };
            meta.Columns.Add(ReportStarColumn(1.15));
            meta.Columns.Add(ReportStarColumn(2.85));
            meta.Columns.Add(ReportStarColumn(1.15));
            meta.Columns.Add(ReportStarColumn(3.15));
            TableRowGroup metaGroup = new TableRowGroup();
            meta.RowGroups.Add(metaGroup);

            string reportNo = GenerateInvestigationReportNumber();
            void AddMetaRow(string label1, string value1, string label2, string value2)
            {
                TableRow metaRow = new TableRow();
                metaRow.Cells.Add(MakeReportTableCell(label1, true, 8.2, TextAlignment.Left, ReportBlueGrayBrush(), ReportHeaderBrush()));
                metaRow.Cells.Add(MakeReportTableCell(value1, false, 8.2));
                metaRow.Cells.Add(MakeReportTableCell(label2, true, 8.2, TextAlignment.Left, ReportBlueGrayBrush(), ReportHeaderBrush()));
                metaRow.Cells.Add(MakeReportTableCell(value2, false, 8.2));
                metaGroup.Rows.Add(metaRow);
            }

            AddMetaRow("Report No", reportNo, "Event No", lblEventNumber.Text);
            AddMetaRow("Event Type", lblEventType.Text, "Severity", GetComboText(cboSeverity));
            AddMetaRow("Status", lblHeaderStatus.Text, "Detection Source", lblDetectionSource.Text);
            AddMetaRow("Detected By", lblDetectedBy.Text, "Detected Date", lblDetectedDate.Text);
            AddMetaRow("Sample Number", lblSampleNumber.Text, "Final Disposition", GetComboText(cboFinalDisposition));

            document.Blocks.Add(meta);
        }

        private void AddPhaseIIWaterSystemInvestigation(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase II: Water System Investigation");

            document.Blocks.Add(MakeParagraph("Objective: Review the water sampling, testing, method execution, water system status, trend history, and potential impact related to the affected sampling point.", false, 10));

            DataTable affectedResults = DatabaseHelper.GetQualityEventAffectedResults(qualityEventId);
            if (affectedResults != null && affectedResults.Rows.Count > 0)
            {
                AddReportSectionTitle(document, "Affected Water Result(s)");
                AddDataTableSection(document, "", affectedResults, "TestName", "ResultValue", "SpecificationLimit", "Unit", "FailureType");
            }

            if (checklistTable != null && checklistTable.Rows.Count > 0)
            {
                AddReportSectionTitle(document, "Water Investigation Checklist Findings");
                AddDataTableSection(document, "", checklistTable, "SectionName", "QuestionText", "AnswerValue", "Comments");
            }
            else
            {
                document.Blocks.Add(MakeParagraph("No water investigation checklist records found.", false, 10));
            }

            document.Blocks.Add(MakeParagraph("Water system review should include sampling point condition, recent sanitization, maintenance/interventions, media/reagent or instrument verification, incubation/testing conditions, analyst technique, and trend impact as applicable to the affected test.", false, 10));
        }

        private void AddPhaseIIEnvironmentalInvestigation(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase II: Environmental Monitoring Technical Investigation");

            document.Blocks.Add(MakeParagraph(
                "Objective: establish whether the excursion reflects sampling/laboratory error or a true environmental/facility/process condition, assess recurrence and contamination risk, and document evidence supporting area and product/material disposition.",
                false, 10));

            AddReportSectionTitle(document, "Environmental Monitoring Event Context");
            document.Blocks.Add(MakeTwoColumnTable(
                "Area / Grade", FirstNonEmpty(lblEmAreaGrade.Text),
                "Plan / Category", FirstNonEmpty(lblEmPlanCategory.Text),
                "Sampling / Activity", FirstNonEmpty(lblEmSamplingActivity.Text),
                "Media / Controls", FirstNonEmpty(lblEmMediaControls.Text),
                "Cleaning / Facility", FirstNonEmpty(lblEmCleaningFacility.Text),
                "Incubation", FirstNonEmpty(lblEmIncubation.Text),
                "Product / Personnel", FirstNonEmpty(lblEmProductPersonnel.Text)));

            DataTable affectedResults = DatabaseHelper.GetQualityEventAffectedResults(qualityEventId);
            if (affectedResults != null && affectedResults.Rows.Count > 0)
            {
                AddReportSectionTitle(document, "Affected Environmental Monitoring Result(s)");
                AddDataTableSection(document, "", affectedResults, "TestName", "ResultValue", "SpecificationLimit", "Unit", "FailureType");
            }

            AddReportSectionTitle(document, "Environmental Monitoring Technical Checklist");
            DataTable technicalChecklist = FilterEnvironmentalTechnicalChecklist(checklistTable);
            if (technicalChecklist.Rows.Count > 0)
            {
                AddChecklistInChunks(document, technicalChecklist, 11);
            }
            else
            {
                document.Blocks.Add(MakeParagraph("No Environmental Monitoring technical checklist records found.", false, 9.2));
            }
        }

        private DataTable FilterEnvironmentalTechnicalChecklist(DataTable source)
        {
            DataTable result = source != null ? source.Clone() : new DataTable();
            if (source == null)
                return result;

            foreach (DataRow row in source.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string section = row.GetSafeString("SectionName").Trim();
                string normalized = NormalizeChecklistIdentity(section);

                bool dedicatedElsewhere =
                    normalized == NormalizeChecklistIdentity("Phase I - Laboratory Review") ||
                    normalized == NormalizeChecklistIdentity("Root Cause / Fishbone") ||
                    normalized == NormalizeChecklistIdentity("Impact Assessment") ||
                    normalized == NormalizeChecklistIdentity("Follow-up Monitoring") ||
                    normalized == NormalizeChecklistIdentity("CAPA") ||
                    normalized == NormalizeChecklistIdentity("QA Closure") ||
                    normalized == NormalizeChecklistIdentity("QA Disposition");

                if (!dedicatedElsewhere)
                    result.ImportRow(row);
            }

            return result;
        }

        private void AddChecklistInChunks(FlowDocument document, DataTable table, int rowsPerChunk)
        {
            if (table == null || table.Rows.Count == 0)
                return;

            int chunkSize = Math.Max(6, rowsPerChunk);
            for (int startIndex = 0; startIndex < table.Rows.Count; startIndex += chunkSize)
            {
                DataTable chunk = table.Clone();
                for (int i = startIndex; i < Math.Min(startIndex + chunkSize, table.Rows.Count); i++)
                    chunk.ImportRow(table.Rows[i]);

                if (startIndex > 0)
                {
                    AddReportSubsectionTitle(document, "Phase II - Environmental Monitoring Technical Checklist (continued)");
                    Block continuationTitle = document.Blocks.LastBlock;
                    if (continuationTitle != null)
                        continuationTitle.BreakPageBefore = true;
                }

                AddDataTableSection(document, string.Empty, chunk, "SectionName", "QuestionText", "AnswerValue", "Comments");
            }
        }

        private void AddPhaseIIIRootCauseAnalysis(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase III: Root Cause Analysis (ICH Q9)");

            document.Blocks.Add(MakeTwoColumnTable(
                "Root Cause Category", FirstNonEmpty(GetComboText(cboRootCauseCategory)),
                "Root Cause Details", FirstNonEmpty(txtRootCauseDetails.Text)));

            document.Blocks.Add(MakeParagraph("5 Whys Analysis:", true, 11));
            DataTable whys = FilterMeaningfulRows(rootCauseWhysTable, "Answer");
            if (whys.Rows.Count > 0)
            {
                AddDataTableSection(document, "", whys, "WhyLevel", "Question", "Answer");
            }
            else
            {
                document.Blocks.Add(MakeParagraph("No 5 Whys analysis documented.", false, 10));
            }

            document.Blocks.Add(MakeParagraph("Root Cause Categories Assessed (Fishbone):", true, 11));
            Table fishbone = new Table();
            fishbone.CellSpacing = 0;
            fishbone.Columns.Add(ReportStarColumn(1.45));
            fishbone.Columns.Add(ReportStarColumn(5.55));

            TableRowGroup fishGroup = new TableRowGroup();
            fishbone.RowGroups.Add(fishGroup);

            AddFishboneRow(fishGroup, "Personnel / Training", FindChecklistAnswer(checklistTable, "Personnel / Training", "assessed"));
            AddFishboneRow(fishGroup, "Method / Procedure", FindChecklistAnswer(checklistTable, "Method / Procedure", "assessed"));
            AddFishboneRow(fishGroup, "Equipment / Instrument", FindChecklistAnswer(checklistTable, "Equipment / Instrument", "assessed"));
            AddFishboneRow(fishGroup, "Material / Media", FindChecklistAnswer(checklistTable, "Material / Media", "assessed"));
            AddFishboneRow(fishGroup, "Environment / Facility", FindChecklistAnswer(checklistTable, "Environment / Facility", "assessed"));
            AddFishboneRow(fishGroup, "Measurement / Data", FindChecklistAnswer(checklistTable, "Measurement / Data", "assessed"));

            document.Blocks.Add(fishbone);
        }

        private void AddFishboneRow(TableRowGroup group, string category, string evidence)
        {
            string status = evidence == "Not documented" ? "Not Assessed" : "Assessed";
            TableRow row = new TableRow();
            row.Cells.Add(MakeReportTableCell(category, false, 9));
            row.Cells.Add(MakeReportTableCell(status + ": " + evidence, false, 9));
            group.Rows.Add(row);
        }

        private void AddPhaseIVImpactAssessment(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase IV: Impact Assessment (FDA, WHO)");

            document.Blocks.Add(MakeParagraph("Overall Impact Assessment:", true, 11));
            document.Blocks.Add(MakeParagraph(FirstNonEmpty(txtImpactAssessment.Text), false, 10));

            DataTable impacts = FilterMeaningfulRows(impactAssessmentTable, "Finding", "ImpactStatus");
            if (impacts.Rows.Count > 0)
            {
                AddDataTableSection(document, "", impacts, "AssessmentArea", "Finding", "ImpactStatus");
            }
            else
            {
                document.Blocks.Add(MakeParagraph("No structured impact assessment rows documented.", false, 10));
            }
        }

        private void AddPhaseVCAPAPlan(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase V: Corrective and Preventive Action Plan (ICH Q10)");

            document.Blocks.Add(MakeParagraph("CAPA Required: " + (chkCAPARequired.IsChecked == true ? "Yes" : "No"), true, 11));

            DataTable rows = FilterMeaningfulRows(capaItemsTable, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus");
            if (rows.Rows.Count > 0)
            {
                AddDataTableSection(document, "", rows, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus");
            }
            else
            {
                document.Blocks.Add(MakeParagraph("No structured CAPA action items documented.", false, 10));
            }
        }

        private void AddPhaseVIDataIntegrity(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase VI: Data Integrity - ALCOA+ (FDA 21 CFR Part 11, WHO)");

            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(ReportStarColumn(1.10));
            table.Columns.Add(ReportStarColumn(0.95));
            table.Columns.Add(ReportStarColumn(4.95));

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportTableCell("Principle", true, 9.5));
            header.Cells.Add(MakeReportTableCell("Status", true, 9.5, TextAlignment.Center));
            header.Cells.Add(MakeReportTableCell("Evidence", true, 9.5));
            group.Rows.Add(header);

            string rawAuditEvidence = FindChecklistAnswer(checklistTable, "raw data", "audit trail");
            string actionHistoryEvidence = dgActions != null && dgActions.Items.Count > 0 ? dgActions.Items.Count + " recorded action(s)." : "No action history records found.";

            string rawAuditStatus = EvidenceStatus(rawAuditEvidence);
            bool requiredChecklistComplete = checklistTable != null && checklistTable.Rows.Cast<DataRow>().All(row =>
                row.RowState == DataRowState.Deleted ||
                !row.Table.Columns.Contains("IsRequired") ||
                !Convert.ToBoolean(row["IsRequired"]) ||
                !string.IsNullOrWhiteSpace(row.GetSafeString("AnswerValue")));
            bool completeVerified = requiredChecklistComplete && rawAuditStatus.Equals("Verified", StringComparison.OrdinalIgnoreCase);

            AddDataIntegrityRow(group, "Attributable", "Verified", "User attribution captured through PharmaLIMS user/action records.");
            AddDataIntegrityRow(group, "Legible", "Verified", "Electronic records generated in readable report format.");
            AddDataIntegrityRow(group, "Contemporaneous", "Verified", "System timestamps retained in event action history.");
            AddDataIntegrityRow(group, "Original", rawAuditStatus, rawAuditEvidence);
            AddDataIntegrityRow(group, "Accurate", rawAuditStatus, rawAuditStatus == "Verified" ? "Accuracy supported by documented raw-data/audit-trail review." : rawAuditEvidence);
            AddDataIntegrityRow(group, "Complete", completeVerified ? "Verified" : "Not verified", completeVerified ? "All required checklist and investigation sections are documented." : "Completeness cannot be verified while required checklist or raw-data/audit-trail evidence is incomplete.");
            AddDataIntegrityRow(group, "Consistent", "Verified", "Chronological sequence maintained in action history.");
            AddDataIntegrityRow(group, "Enduring", "Verified", "Records retained in PharmaLIMS database tables.");
            AddDataIntegrityRow(group, "Available", "Verified", "Records available from Quality Event Investigation screen.");

            document.Blocks.Add(table);
        }

        private void AddPhaseVIIQADisposition(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase VII: QA Disposition (FDA OOS Guidance)");

            if (IsReportClosed())
            {
                string closedBy = string.IsNullOrWhiteSpace(loadedClosedBy) ? "Not documented" : loadedClosedBy.Trim();
                string closedDate = loadedClosedDate.HasValue
                    ? loadedClosedDate.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "Not documented";

                document.Blocks.Add(MakeTwoColumnTable(
                    "Final Disposition", FirstNonEmpty(GetComboText(cboFinalDisposition)),
                    "QA Conclusion", FirstNonEmpty(txtQAConclusion.Text),
                    "Investigation Status", "Closed",
                    "Closed By", closedBy,
                    "Closed Date", closedDate));
            }
            else
            {
                document.Blocks.Add(MakeTwoColumnTable(
                    "Final Disposition", string.IsNullOrWhiteSpace(GetComboText(cboFinalDisposition)) ? "Pending QA disposition" : GetComboText(cboFinalDisposition),
                    "QA Conclusion", string.IsNullOrWhiteSpace(txtQAConclusion.Text) ? "Pending QA conclusion" : txtQAConclusion.Text.Trim(),
                    "Investigation Status", FirstNonEmpty(lblHeaderStatus.Text),
                    "Closure Status", "Not closed - final QA closure has not been completed."));
            }
        }

        private void AddPhaseVIIIDistribution(FlowDocument document)
        {
            AddReportSectionTitle(document, "Phase VIII: Distribution & Reporting");

            DataTable rows = FilterMeaningfulRows(distributionTable, "Department", "Recipient", "DateReceived");
            if (rows.Rows.Count > 0)
            {
                AddDataTableSection(document, "", rows, "Department", "Recipient", "DateReceived");
            }
            else
            {
                document.Blocks.Add(MakeParagraph("No distribution list documented.", false, 10));
            }
        }

        private void AddGlobalReportFooter(FlowDocument document)
        {
            Table control = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 12, 0, 0)
            };
            control.Columns.Add(ReportStarColumn(1.0));
            TableRowGroup group = new TableRowGroup();
            control.RowGroups.Add(group);
            TableRow row = new TableRow();
            row.Cells.Add(MakeReportTableCell(
                "Controlled electronic record generated by PharmaLIMS. Approval, closure and print activity remain traceable in the system audit trail. Site procedures and the validated system configuration remain authoritative.",
                false,
                7.8,
                TextAlignment.Center,
                ReportBlueGrayBrush(),
                ReportMutedBrush()));
            group.Rows.Add(row);
            document.Blocks.Add(control);
        }

        private void BtnPrintReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (qualityEventId <= 0)
                {
                    MessageBox.Show("No Quality Event is loaded.", "Print Report", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                PrintDialog printDialog = new PrintDialog();
                if (printDialog.ShowDialog() != true)
                    return;

                DatabaseHelper.AddAuditTrailAdvanced(
                    "QualityEvents",
                    qualityEventId,
                    "Quality Event Report Print Requested",
                    "",
                    lblEventNumber.Text,
                    "A controlled print request was accepted before the document was sent to the printer.",
                    currentUser);

                FlowDocument document = BuildGlobalInvestigationReportDocument();
                document.PageHeight = printDialog.PrintableAreaHeight;
                document.PageWidth = printDialog.PrintableAreaWidth;

                IDocumentPaginatorSource paginatorSource = CreateQualityEventReportPaginatorSource(document);
                printDialog.PrintDocument(paginatorSource.DocumentPaginator, "Quality Event Investigation Report - " + lblEventNumber.Text);

                DatabaseHelper.AddQualityEventPrintHistory(qualityEventId, currentUser);
                DatabaseHelper.AddAuditTrailAdvanced(
                    "QualityEvents",
                    qualityEventId,
                    "Quality Event Report Print Completed",
                    "Requested",
                    "Completed",
                    "Quality Event report was sent to the selected printer.",
                    currentUser);
            }
            catch (Exception ex)
            {
                try
                {
                    if (qualityEventId > 0)
                    {
                        DatabaseHelper.AddAuditTrailAdvanced(
                            "QualityEvents",
                            qualityEventId,
                            "Quality Event Report Print Failed",
                            "Requested",
                            "Failed",
                            Infrastructure.UserFacingError.SafeMessage(ex),
                            currentUser);
                    }
                }
                catch (Exception auditException)
                {
                    ApplicationLogger.Warning("Unable to record the failed print attempt.", auditException);
                }

                MessageBox.Show("Error printing Quality Event report: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Print Report", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCloseWindow_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private sealed class QualityEventLoadData
        {
            public DataTable Header { get; init; } = new DataTable();
            public DataTable AffectedResults { get; init; } = new DataTable();
            public DataTable Actions { get; init; } = new DataTable();
            public DataTable Checklist { get; init; } = new DataTable();
            public DataTable RootCauseWhys { get; init; } = new DataTable();
            public DataTable ImpactAssessment { get; init; } = new DataTable();
            public DataTable CapaItems { get; init; } = new DataTable();
            public DataTable Retesting { get; init; } = new DataTable();
            public DataTable Distribution { get; init; } = new DataTable();
            public DataTable EnvironmentalMonitoringContext { get; init; } = new DataTable();
        }
    }
}
