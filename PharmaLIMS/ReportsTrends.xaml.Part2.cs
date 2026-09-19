#nullable disable
using Microsoft.Data.SqlClient;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using OxyPlot.Wpf;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PharmaLIMS
{
    public partial class ReportsTrends
    {
        #region PDF Drawing Helper Methods

        private string FindReportLogoPath()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string currentDir = Environment.CurrentDirectory;

                List<string> roots = new List<string>();

                if (!string.IsNullOrWhiteSpace(baseDir))
                    roots.Add(baseDir);

                if (!string.IsNullOrWhiteSpace(currentDir))
                    roots.Add(currentDir);

                try
                {
                    DirectoryInfo dir = new DirectoryInfo(baseDir);

                    for (int i = 0; i < 6 && dir != null; i++)
                    {
                        roots.Add(dir.FullName);
                        dir = dir.Parent;
                    }
                }
                catch
                {
                    // Keep searching in the known roots.
                }

                string[] folders =
                {
                    "",
                    "Assets",
                    Path.Combine("Assets", "Images"),
                    "Images",
                    "Resources",
                    Path.Combine("Resources", "Images"),
                    "Properties"
                };

                string[] fixedNames =
                {
                    "logo.png",
                    "Logo.png",
                    "medica.png",
                    "Medica.png",
                    "medica_logo.png",
                    "MedicaLogo.png",
                    "company_logo.png",
                    "CompanyLogo.png",
                    "header_logo.png",
                    "HeaderLogo.png"
                };

                foreach (string root in roots.Distinct())
                {
                    foreach (string folder in folders)
                    {
                        foreach (string name in fixedNames)
                        {
                            string path = string.IsNullOrWhiteSpace(folder)
                                ? Path.Combine(root, name)
                                : Path.Combine(root, folder, name);

                            if (File.Exists(path))
                                return path;
                        }
                    }
                }

                foreach (string root in roots.Distinct())
                {
                    if (!Directory.Exists(root))
                        continue;

                    string[] imageFiles = Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                        .Where(f =>
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    foreach (string file in imageFiles)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();

                        if (fileName.Contains("logo") ||
                            fileName.Contains("medica") ||
                            fileName.Contains("company") ||
                            fileName.Contains("header"))
                        {
                            return file;
                        }
                    }
                }
            }
            catch
            {
                // Keep PDF export working even if logo lookup fails.
            }

            return "";
        }

        private void DrawPdfInfoCell(
            XGraphics gfx,
            string label,
            string value,
            XFont labelFont,
            XFont valueFont,
            double x,
            double y,
            double width,
            double height)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = "All";

            gfx.DrawRectangle(XPens.LightGray, XBrushes.White, x, y, width, height);

            string labelText = (label ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(labelText) && !labelText.EndsWith(":"))
                labelText += ":";

            double textY = y + Math.Max(3, (height - 8) / 2);
            double labelX = x + 5;
            double labelWidth = gfx.MeasureString(labelText, labelFont).Width;
            double valueX = labelX + labelWidth + 4;
            double valueWidth = Math.Max(10, width - (valueX - x) - 5);

            gfx.DrawString(labelText,
                labelFont,
                XBrushes.Gray,
                new XRect(labelX, textY, labelWidth + 2, height - 4),
                XStringFormats.TopLeft);

            gfx.DrawString(value,
                valueFont,
                XBrushes.Black,
                new XRect(valueX, textY, valueWidth, height - 4),
                XStringFormats.TopLeft);
        }

        private void DrawReportPdfHeader(
            XGraphics gfx,
            PdfPage page,
            string reportNumber,
            string generatedBy,
            DateTime generatedOn,
            string reportTitle,
            int pageNumber = 1,
            int totalPages = 1)
        {
            double pageWidth = page.Width.Point;

            XFont companyFont = new XFont("Arial", 14, XFontStyleEx.Bold);
            XFont departmentFont = new XFont("Arial", 8, XFontStyleEx.Regular);
            XFont reportTitleFont = new XFont("Arial", 12, XFontStyleEx.Bold);
            XFont smallFont = new XFont("Arial", 7, XFontStyleEx.Regular);
            XFont smallBoldFont = new XFont("Arial", 7, XFontStyleEx.Bold);
            XFont filterLabelFont = new XFont("Arial", 6, XFontStyleEx.Bold);
            XFont filterValueFont = new XFont("Arial", 7, XFontStyleEx.Regular);

            double left = 30;
            double top = 18;
            double width = pageWidth - 60;
            double headerHeight = 134;

            gfx.DrawRectangle(XPens.DarkSlateGray, XBrushes.White, left, top, width, headerHeight);
            gfx.DrawRectangle(XBrushes.DarkSlateGray, left, top, width, 42);

            // Logo: draw the real logo inside a safe box without cropping or stretching.
            double logoBoxX = left + 8;
            double logoBoxY = top + 6;
            double logoBoxWidth = 118;
            double logoBoxHeight = 30;

            gfx.DrawRoundedRectangle(XPens.White, XBrushes.White, logoBoxX, logoBoxY, logoBoxWidth, logoBoxHeight, 4, 4);

            string logoPath = FindReportLogoPath();
            if (!string.IsNullOrWhiteSpace(logoPath))
            {
                try
                {
                    using (XImage logo = XImage.FromFile(logoPath))
                    {
                        double padding = 3;
                        double maxLogoWidth = logoBoxWidth - (padding * 2);
                        double maxLogoHeight = logoBoxHeight - (padding * 2);
                        double logoRatio = Math.Min(maxLogoWidth / logo.PixelWidth, maxLogoHeight / logo.PixelHeight);

                        double logoWidth = logo.PixelWidth * logoRatio;
                        double logoHeight = logo.PixelHeight * logoRatio;
                        double logoX = logoBoxX + ((logoBoxWidth - logoWidth) / 2);
                        double logoY = logoBoxY + ((logoBoxHeight - logoHeight) / 2);

                        gfx.DrawImage(logo, logoX, logoY, logoWidth, logoHeight);
                    }
                }
                catch
                {
                    gfx.DrawString("medica", companyFont, XBrushes.DarkSlateGray,
                        new XRect(logoBoxX + 8, logoBoxY + 7, logoBoxWidth - 16, 16), XStringFormats.TopLeft);
                }
            }
            else
            {
                gfx.DrawString("medica", companyFont, XBrushes.DarkSlateGray,
                    new XRect(logoBoxX + 8, logoBoxY + 7, logoBoxWidth - 16, 16), XStringFormats.TopLeft);
            }

            gfx.DrawString("MEDICA PHARMACEUTICAL INDUSTRY",
                companyFont,
                XBrushes.White,
                new XRect(left + 110, top + 6, width - 320, 18),
                XStringFormats.TopCenter);

            gfx.DrawString("Microbiology Department",
                departmentFont,
                XBrushes.White,
                new XRect(left + 110, top + 26, width - 320, 12),
                XStringFormats.TopCenter);

            gfx.DrawString("Form No: MQC-R-TREND-001",
                smallFont,
                XBrushes.White,
                new XRect(pageWidth - 185, top + 6, 145, 10),
                XStringFormats.TopRight);

            gfx.DrawString("Version: 01",
                smallFont,
                XBrushes.White,
                new XRect(pageWidth - 185, top + 19, 145, 10),
                XStringFormats.TopRight);

            gfx.DrawString("Page " + pageNumber.ToString(CultureInfo.InvariantCulture) + " of " + totalPages.ToString(CultureInfo.InvariantCulture),
                smallFont,
                XBrushes.White,
                new XRect(pageWidth - 185, top + 32, 145, 10),
                XStringFormats.TopRight);

            gfx.DrawString("REPORTS AND TRENDS ANALYSIS",
                reportTitleFont,
                XBrushes.Black,
                new XRect(left, top + 50, width, 16),
                XStringFormats.TopCenter);

            gfx.DrawString(reportTitle,
                smallBoldFont,
                XBrushes.Black,
                new XRect(left, top + 68, width, 12),
                XStringFormats.TopCenter);

            double metaY = top + 84;
            double metaH = 18;
            DrawPdfInfoCell(gfx, "Report No", reportNumber, filterLabelFont, filterValueFont, left + 10, metaY, 210, metaH);
            DrawPdfInfoCell(gfx, "Generated By", generatedBy, filterLabelFont, filterValueFont, left + 230, metaY, 160, metaH);
            DrawPdfInfoCell(gfx, "Generated On", generatedOn.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), filterLabelFont, filterValueFont, left + 400, metaY, width - 410, metaH);

            double filterY = top + 106;
            double filterH = 20;
            double cellGap = 4;
            double pointWidth = 160;
            double categoryWidth = 115;
            double testWidth = 190;
            double fromWidth = 115;
            double toWidth = width - 20 - pointWidth - categoryWidth - testWidth - fromWidth - (cellGap * 4);
            double x = left + 10;

            DrawPdfInfoCell(gfx, "Sampling Point", GetSelectedPointText(), filterLabelFont, filterValueFont, x, filterY, pointWidth, filterH);
            x += pointWidth + cellGap;
            DrawPdfInfoCell(gfx, "Category", GetSelectedCategory(), filterLabelFont, filterValueFont, x, filterY, categoryWidth, filterH);
            x += categoryWidth + cellGap;
            DrawPdfInfoCell(gfx, "Test", GetSelectedTestText(), filterLabelFont, filterValueFont, x, filterY, testWidth, filterH);
            x += testWidth + cellGap;
            DrawPdfInfoCell(gfx, "From", GetReportDateText(dpDateFrom), filterLabelFont, filterValueFont, x, filterY, fromWidth, filterH);
            x += fromWidth + cellGap;
            DrawPdfInfoCell(gfx, "To", GetReportDateText(dpDateTo), filterLabelFont, filterValueFont, x, filterY, toWidth, filterH);
        }

        private string GetPdfTableValue(DataRow row, string columnName)
        {
            if (row == null || string.IsNullOrWhiteSpace(columnName))
                return "";

            if (!row.Table.Columns.Contains(columnName))
                return "";

            object rawValue = row[columnName];
            string status = row.Table.Columns.Contains("Status") && row["Status"] != DBNull.Value
                ? (row["Status"]?.ToString() ?? "").Trim()
                : "";

            if (columnName.Equals("ResultValue", StringComparison.OrdinalIgnoreCase))
            {
                if (rawValue == null || rawValue == DBNull.Value || string.IsNullOrWhiteSpace(rawValue.ToString()))
                    return "-";

                if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    return "-";

                if (TryGetDouble(rawValue, out double numericResult))
                    return numericResult.ToString("0.00", CultureInfo.InvariantCulture);

                return rawValue.ToString() ?? "";
            }

            if (columnName.Equals("AlertLimit", StringComparison.OrdinalIgnoreCase) ||
                columnName.Equals("ActionLimit", StringComparison.OrdinalIgnoreCase))
            {
                if (rawValue == null || rawValue == DBNull.Value || string.IsNullOrWhiteSpace(rawValue.ToString()))
                    return "";

                if (TryGetDouble(rawValue, out double numericLimit))
                    return numericLimit.ToString("0.00", CultureInfo.InvariantCulture);
            }

            return rawValue == null || rawValue == DBNull.Value
                ? ""
                : rawValue.ToString() ?? "";
        }

        private string BuildInterpretation()
        {
            if (currentDataTable == null || currentDataTable.Rows.Count == 0)
                return "Interpretation: No data available for interpretation.";

            List<string> profiles = currentDataTable.Columns.Contains("WaterProfile")
                ? currentDataTable.Rows.Cast<DataRow>()
                    .Select(row => Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture) ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            if (profiles.Count > 1)
            {
                List<string> parts = new();
                foreach (string profile in profiles)
                {
                    DataRow[] rows = currentDataTable.Rows.Cast<DataRow>()
                        .Where(row => string.Equals(Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture), profile, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    int pass = rows.Count(row => string.Equals(Convert.ToString(row["Status"], CultureInfo.InvariantCulture), "PASS", StringComparison.OrdinalIgnoreCase));
                    int alert = rows.Count(row => string.Equals(Convert.ToString(row["Status"], CultureInfo.InvariantCulture), "ALERT", StringComparison.OrdinalIgnoreCase));
                    int fail = rows.Count(row => string.Equals(Convert.ToString(row["Status"], CultureInfo.InvariantCulture), "FAIL", StringComparison.OrdinalIgnoreCase));
                    int pending = rows.Length - pass - alert - fail;
                    parts.Add($"{profile}: PASS {pass}, ALERT {alert}, FAIL {fail}, PENDING {pending}");
                }
                return "Interpretation by water profile: " + string.Join(" | ", parts) + ". PW and PTW are assessed independently using their own controlled limits.";
            }

            int totalPass = 0, totalAlert = 0, totalFail = 0, totalPending = 0;
            foreach (DataRow row in currentDataTable.Rows)
            {
                string status = row["Status"] == DBNull.Value ? "Pending" : (row["Status"]?.ToString() ?? "Pending");
                if (status.Equals("PASS", StringComparison.OrdinalIgnoreCase)) totalPass++;
                else if (status.Equals("ALERT", StringComparison.OrdinalIgnoreCase)) totalAlert++;
                else if (status.Equals("FAIL", StringComparison.OrdinalIgnoreCase)) totalFail++;
                else totalPending++;
            }

            string pendingNote = totalPending > 0
                ? $" Pending result(s): {totalPending}. Pending results are excluded from final trend interpretation until completion."
                : string.Empty;

            if (totalFail > 0)
                return $"Interpretation: {totalFail} result(s) exceeded the applicable action/specification limit. Immediate investigation is required." + pendingNote;
            if (totalAlert > 0)
                return $"Interpretation: {totalAlert} result(s) exceeded the applicable alert limit. Monitoring and review are recommended." + pendingNote;
            if (totalPass > 0 && totalPending == 0)
                return "Interpretation: All available results are within the applicable controlled limits. Overall status: PASS.";
            if (totalPass > 0 && totalPending > 0)
                return $"Interpretation: Available completed results are within the applicable controlled limits. Pending result(s): {totalPending}. Complete pending results before final conclusion.";
            if (totalPending > 0)
                return $"Interpretation: No completed PASS/ALERT/FAIL results are available. Pending result(s): {totalPending}.";
            return "Interpretation: Results are available, but no PASS/ALERT/FAIL status could be determined.";
        }

        private IReadOnlyList<string> BuildStatisticsLines()
        {
            if (currentDataTable == null || currentDataTable.Rows.Count == 0)
                return new[] { "Statistics: No data available." };

            List<string> profiles = currentDataTable.Columns.Contains("WaterProfile")
                ? currentDataTable.Rows.Cast<DataRow>()
                    .Select(row => Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture) ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            IEnumerable<(string Label, IEnumerable<DataRow> Rows)> groups = profiles.Count > 1
                ? profiles.Select(profile => (profile, currentDataTable.Rows.Cast<DataRow>().Where(row => string.Equals(Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture), profile, StringComparison.OrdinalIgnoreCase))))
                : new[] { (string.Empty, currentDataTable.Rows.Cast<DataRow>().AsEnumerable()) };

            List<string> lines = new();
            foreach ((string label, IEnumerable<DataRow> rows) in groups)
            {
                List<double> values = new();
                foreach (DataRow row in rows)
                {
                    string status = row.Table.Columns.Contains("Status") && row["Status"] != DBNull.Value
                        ? (row["Status"]?.ToString() ?? string.Empty).Trim()
                        : string.Empty;
                    if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string qualifier = row.Table.Columns.Contains("ResultQualifier")
                        ? (Convert.ToString(row["ResultQualifier"], CultureInfo.InvariantCulture) ?? string.Empty).Trim()
                        : string.Empty;
                    if (!string.IsNullOrWhiteSpace(qualifier))
                        continue;
                    if (row.Table.Columns.Contains("ResultValue") &&
                        TryGetTrendNumericValue(row["ResultValue"], out double value, out _))
                        values.Add(value);
                }

                if (values.Count == 0)
                {
                    lines.Add((string.IsNullOrWhiteSpace(label) ? string.Empty : label + ": ") + "No completed numeric results available; pending results are excluded.");
                    continue;
                }

                double average = values.Average();
                double minimum = values.Min();
                double maximum = values.Max();
                double stdDev = 0;
                if (values.Count > 1)
                {
                    double variance = values.Sum(value => Math.Pow(value - average, 2)) / (values.Count - 1);
                    stdDev = Math.Sqrt(variance);
                }

                string prefix = string.IsNullOrWhiteSpace(label) ? "Statistics" : $"{label} statistics";
                lines.Add(prefix + " (completed numeric results only): " +
                          "Count " + values.Count.ToString(CultureInfo.InvariantCulture) + " | " +
                          "Average " + average.ToString("0.00", CultureInfo.InvariantCulture) + " | " +
                          "Min " + minimum.ToString("0.00", CultureInfo.InvariantCulture) + " | " +
                          "Max " + maximum.ToString("0.00", CultureInfo.InvariantCulture) + " | " +
                          "Std Dev " + stdDev.ToString("0.00", CultureInfo.InvariantCulture));
            }

            return lines;
        }

        private string BuildStatisticsText() => string.Join("  ||  ", BuildStatisticsLines());

        private XBrush GetPdfStatusBackgroundBrush(string status)
        {
            status = status == null ? "" : status.Trim().ToUpperInvariant();

            if (status == "PASS")
                return new XSolidBrush(XColor.FromArgb(255, 231, 246, 236));

            if (status == "ALERT")
                return new XSolidBrush(XColor.FromArgb(255, 255, 243, 205));

            if (status == "FAIL" || status == "OOS" || status == "ACTION")
                return new XSolidBrush(XColor.FromArgb(255, 253, 226, 225));

            return new XSolidBrush(XColor.FromArgb(255, 238, 238, 238));
        }

        private XBrush GetPdfStatusTextBrush(string status)
        {
            status = status == null ? "" : status.Trim().ToUpperInvariant();

            if (status == "PASS")
                return new XSolidBrush(XColor.FromArgb(255, 16, 128, 80));

            if (status == "ALERT")
                return new XSolidBrush(XColor.FromArgb(255, 180, 90, 0));

            if (status == "FAIL" || status == "OOS" || status == "ACTION")
                return new XSolidBrush(XColor.FromArgb(255, 190, 40, 40));

            return XBrushes.Gray;
        }

        private string TrimForPdfCell(string value, int maxLength)
        {
            value = value == null ? "" : value.Trim();

            if (maxLength <= 0 || value.Length <= maxLength)
                return value;

            return value.Substring(0, maxLength) + "...";
        }

        private int GetPdfMaxCharsForColumn(string columnName)
        {
            switch (columnName)
            {
                case "SampleNumber": return 16;
                case "PointCode": return 12;
                case "AreaClassification": return 14;
                case "SamplingDate": return 18;
                case "TestName": return 28;
                case "ResultValue": return 12;
                case "Unit": return 10;
                case "AlertLimit": return 10;
                case "ActionLimit": return 10;
                case "Status": return 14;
                default: return 18;
            }
        }

        private void DrawReportTableHeader(XGraphics gfx, string[] cols, double[] widths, double x, double y, double rowHeight, XFont boldFont)
        {
            double currentX = x;

            for (int i = 0; i < cols.Length; i++)
            {
                gfx.DrawRectangle(XPens.Gray, XBrushes.LightGray, currentX, y, widths[i], rowHeight);
                string header = cols[i] switch
                {
                    "WaterProfile" => "Water Profile",
                    "SampleType" => "Sample Type",
                    "SampleNumber" => "Sample No.",
                    "PointCode" => "Point",
                    "SamplingDate" => "Sampling Date",
                    "ResultValue" => "Result",
                    "AlertLimit" => "Alert / Lower",
                    "ActionLimit" => "Action / Upper",
                    "Status" => "Trend Status",
                    _ => cols[i]
                };
                gfx.DrawString(header, boldFont, XBrushes.Black,
                    new XRect(currentX + 2, y + 3, widths[i] - 4, rowHeight), XStringFormats.TopLeft);
                currentX += widths[i];
            }
        }

        private void DrawReportTableRow(XGraphics gfx, DataRow row, string[] cols, double[] widths, double x, double y, double rowHeight, XFont normalFont)
        {
            double currentX = x;
            string rowStatus = row != null && row.Table.Columns.Contains("Status") && row["Status"] != DBNull.Value
                ? (row["Status"]?.ToString() ?? "")
                : "";

            for (int c = 0; c < cols.Length; c++)
            {
                string col = cols[c];
                string value = TrimForPdfCell(GetPdfTableValue(row, col), GetPdfMaxCharsForColumn(col));

                XBrush cellBackground = col.Equals("Status", StringComparison.OrdinalIgnoreCase)
                    ? GetPdfStatusBackgroundBrush(rowStatus)
                    : XBrushes.White;

                XBrush textBrush = col.Equals("Status", StringComparison.OrdinalIgnoreCase)
                    ? GetPdfStatusTextBrush(rowStatus)
                    : XBrushes.Black;

                gfx.DrawRectangle(XPens.LightGray, cellBackground, currentX, y, widths[c], rowHeight);
                gfx.DrawString(value, normalFont, textBrush,
                    new XRect(currentX + 2, y + 3, widths[c] - 4, rowHeight), XStringFormats.TopLeft);
                currentX += widths[c];
            }
        }

        private void DrawReportControlNote(XGraphics gfx, double pageWidth, double y, XFont footerFont)
        {
            gfx.DrawLine(XPens.LightGray, 40, y - 6, pageWidth - 40, y - 6);
            gfx.DrawString("This report is electronically generated and controlled by PharmaLIMS.", footerFont, XBrushes.Gray,
                new XRect(40, y, pageWidth - 80, 12), XStringFormats.TopCenter);
            gfx.DrawString("Uncontrolled when printed unless reviewed, approved, and signed according to the approved procedure.", footerFont, XBrushes.Gray,
                new XRect(40, y + 12, pageWidth - 80, 12), XStringFormats.TopCenter);
        }

        private void DrawPdfPageBorder(XGraphics gfx, double pageWidth, double pageHeight)
        {
            gfx.DrawRectangle(XPens.DarkSlateGray, 18, 12, pageWidth - 36, pageHeight - 24);
        }

        private void DrawSectionBox(XGraphics gfx, double x, double y, double width, double height)
        {
            gfx.DrawRoundedRectangle(XPens.LightGray, XBrushes.White, x, y, width, height, 4, 4);
        }

        private void DrawGeneratedFooter(XGraphics gfx, double pageWidth, double pageHeight, DateTime generatedOn, XFont footerFont)
        {
            gfx.DrawString("Generated by PharmaLIMS | " + generatedOn.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                footerFont, XBrushes.Gray, new XRect(40, pageHeight - 16, pageWidth - 80, 12), XStringFormats.TopCenter);
        }

        private void DrawReportGenerationRecord(XGraphics gfx, double pageWidth, double pageHeight, string generatedBy, DateTime generatedOn, XFont normalFont, XFont boldFont, XFont footerFont)
        {
            double recordY = pageHeight - 82;
            double recordHeight = 48;
            double recordWidth = pageWidth - 80;
            string generatedAt = generatedOn.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string role = string.IsNullOrWhiteSpace(Login.CurrentUserRole) ? "Role not recorded" : "Role: " + Login.CurrentUserRole.Trim();
            string user = string.IsNullOrWhiteSpace(generatedBy) ? "Authenticated user not recorded" : generatedBy.Trim();

            gfx.DrawString("Report Generation Record", boldFont, XBrushes.Black,
                new XRect(40, recordY - 16, recordWidth, 14), XStringFormats.TopLeft);

            gfx.DrawRoundedRectangle(XPens.LightGray, XBrushes.White, 40, recordY, recordWidth, recordHeight, 5, 5);
            gfx.DrawString("Generated by authenticated PharmaLIMS account", footerFont, XBrushes.Gray,
                new XRect(44, recordY + 4, recordWidth - 8, 10), XStringFormats.TopCenter);
            gfx.DrawLine(XPens.DarkSlateGray, 60, recordY + 16, pageWidth - 60, recordY + 16);
            gfx.DrawString(user, boldFont, XBrushes.Black,
                new XRect(44, recordY + 20, recordWidth - 8, 10), XStringFormats.TopCenter);
            gfx.DrawString("Generated: " + generatedAt + " | " + role, normalFont, XBrushes.Black,
                new XRect(44, recordY + 32, recordWidth - 8, 10), XStringFormats.TopCenter);
            gfx.DrawString("This record documents report generation only; it is not a technical review or QA approval signature.", footerFont, XBrushes.Gray,
                new XRect(44, recordY + 41, recordWidth - 8, 9), XStringFormats.TopCenter);
        }

        private double DrawPdfChart(XGraphics gfx, PdfPage page, string imagePath)
        {
            double chartBoxX = 40;
            double chartBoxY = 154;
            double chartBoxWidth = page.Width.Point - 80;
            double chartBoxHeight = 176;

            DrawSectionBox(gfx, chartBoxX, chartBoxY, chartBoxWidth, chartBoxHeight);

            using (XImage image = XImage.FromFile(imagePath))
            {
                double maxWidth = chartBoxWidth - 24;
                double maxHeight = chartBoxHeight - 20;
                double ratio = Math.Min(maxWidth / image.PixelWidth, maxHeight / image.PixelHeight);
                double imgWidth = image.PixelWidth * ratio;
                double imgHeight = image.PixelHeight * ratio;
                double x = chartBoxX + ((chartBoxWidth - imgWidth) / 2);
                double y = chartBoxY + ((chartBoxHeight - imgHeight) / 2);
                gfx.DrawImage(image, x, y, imgWidth, imgHeight);
            }

            return chartBoxY + chartBoxHeight + 10;
        }

        private static List<string> WrapPdfText(XGraphics gfx, string value, XFont font, double width)
        {
            List<string> lines = new();
            foreach (string sourceLine in (value ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (string.IsNullOrWhiteSpace(sourceLine))
                {
                    lines.Add(string.Empty);
                    continue;
                }

                StringBuilder line = new();
                foreach (string word in sourceLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (gfx.MeasureString(candidate, font).Width > width && line.Length > 0)
                    {
                        lines.Add(line.ToString());
                        line.Clear();
                        line.Append(word);
                    }
                    else
                    {
                        line.Clear();
                        line.Append(candidate);
                    }
                }
                if (line.Length > 0)
                    lines.Add(line.ToString());
            }
            return lines.Count == 0 ? new List<string> { string.Empty } : lines;
        }

        private static double DrawPdfWrappedLines(XGraphics gfx, IEnumerable<string> lines, XFont font, XBrush brush, double x, double y, double width, double lineHeight)
        {
            foreach (string line in lines)
            {
                gfx.DrawString(line, font, brush, new XRect(x, y, width, lineHeight), XStringFormats.TopLeft);
                y += lineHeight;
            }
            return y;
        }

        private double DrawPdfSummary(XGraphics gfx, PdfPage page, double yPos, XFont normalFont, XFont boldFont)
        {
            IReadOnlyList<string> statisticsLines = BuildStatisticsLines();
            bool multipleWaterProfiles = currentDataTable != null && currentDataTable.Columns.Contains("WaterProfile") &&
                currentDataTable.Rows.Cast<DataRow>()
                    .Select(row => Convert.ToString(row["WaterProfile"], CultureInfo.InvariantCulture) ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Any();

            double textWidth = page.Width.Point - 96;
            string profileNote = "PW and PTW are displayed and evaluated as separate water profiles; combined descriptive statistics are not calculated.";
            List<string> profileNoteLines = multipleWaterProfiles ? WrapPdfText(gfx, profileNote, normalFont, textWidth) : new List<string>();
            List<string> interpretationLines = WrapPdfText(gfx, BuildInterpretation(), normalFont, textWidth);
            List<string> statisticWrappedLines = statisticsLines.SelectMany(line => WrapPdfText(gfx, line, normalFont, textWidth)).ToList();

            double boxHeight = 52 + (profileNoteLines.Count + interpretationLines.Count + statisticWrappedLines.Count) * 13;
            DrawSectionBox(gfx, 40, yPos - 6, page.Width.Point - 80, boxHeight);
            gfx.DrawString("Summary", boldFont, XBrushes.Black, new XRect(48, yPos, 200, 15), XStringFormats.TopLeft);
            yPos += 16;

            string summaryLine = $"{lblPassCount.Text}     {lblAlertCount.Text}     {lblFailCount.Text}     {lblTotalSamples.Text}     {lblTotalResults.Text}     {lblTestTypes.Text}";
            gfx.DrawString(summaryLine, normalFont, XBrushes.Black, new XRect(48, yPos, textWidth, 18), XStringFormats.TopLeft);
            yPos += 20;

            if (profileNoteLines.Count > 0)
            {
                yPos = DrawPdfWrappedLines(gfx, profileNoteLines, normalFont, XBrushes.Black, 48, yPos, textWidth, 13);
                yPos += 2;
            }

            yPos = DrawPdfWrappedLines(gfx, interpretationLines, normalFont, XBrushes.Black, 48, yPos, textWidth, 13);
            yPos += 2;
            yPos = DrawPdfWrappedLines(gfx, statisticWrappedLines, normalFont, XBrushes.Black, 48, yPos, textWidth, 13);
            yPos += 8;
            return yPos;
        }

        private double DrawPdfTableStart(XGraphics gfx, PdfPage page, double yPos, string[] cols, double[] widths, int firstPageRows, XFont normalFont, XFont boldFont, XFont footerFont)
        {
            if (currentDataTable != null && currentDataTable.Rows.Count > 0)
            {
                gfx.DrawString("Full Data Table", boldFont, XBrushes.Black, new XRect(40, yPos, 200, 15), XStringFormats.TopLeft);
                yPos += 16;

                double tableX = 40;
                double rowHeight = 13;
                DrawReportTableHeader(gfx, cols, widths, tableX, yPos, rowHeight, boldFont);
                yPos += rowHeight;

                int rowsToDraw = Math.Min(firstPageRows, currentDataTable.Rows.Count);
                for (int r = 0; r < rowsToDraw; r++)
                {
                    DrawReportTableRow(gfx, currentDataTable.Rows[r], cols, widths, tableX, yPos, rowHeight, normalFont);
                    yPos += rowHeight;
                }

                if (currentDataTable.Rows.Count > rowsToDraw)
                {
                    yPos += 4;
                    gfx.DrawString("Table continues on the following page(s). Full dataset is included in this PDF.",
                        footerFont, XBrushes.Gray, new XRect(40, yPos, page.Width.Point - 80, 12), XStringFormats.TopLeft);
                }
            }

            return yPos;
        }

        #endregion
    }

    /// <summary>
    /// PDF Font Resolver for cross-platform font support
    /// </summary>
    [SupportedOSPlatform("windows7.0")]
    public class WindowsFontResolver : IFontResolver
    {
        public byte[] GetFont(string faceName)
        {
            string fontPath;

            switch (faceName)
            {
                case "Arial#Regular":
                    fontPath = @"C:\Windows\Fonts\arial.ttf";
                    break;

                case "Arial#Bold":
                    fontPath = @"C:\Windows\Fonts\arialbd.ttf";
                    break;

                case "Arial#Italic":
                    fontPath = @"C:\Windows\Fonts\ariali.ttf";
                    break;

                case "Arial#BoldItalic":
                    fontPath = @"C:\Windows\Fonts\arialbi.ttf";
                    break;

                default:
                    fontPath = @"C:\Windows\Fonts\arial.ttf";
                    break;
            }

            if (!File.Exists(fontPath))
                fontPath = @"C:\Windows\Fonts\segoeui.ttf";

            return File.ReadAllBytes(fontPath);
        }

        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (isBold && isItalic)
                return new FontResolverInfo("Arial#BoldItalic");

            if (isBold)
                return new FontResolverInfo("Arial#Bold");

            if (isItalic)
                return new FontResolverInfo("Arial#Italic");

            return new FontResolverInfo("Arial#Regular");
        }
    }
}
