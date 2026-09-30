using System.Data;
using System.Globalization;
using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PharmaLIMS.Services;

internal sealed record TrendReportChart(string Title, byte[] Image);
internal sealed record TrendReportTable(string Title, DataTable Data, string[] Columns);

/// <summary>Paginated Medica review draft: all charts, complete wrapped results, repeated headings.</summary>
internal sealed class TrendPdfReportWriter : IDisposable
{
    private readonly PdfDocument document = new();
    private XGraphics? graphics;
    private readonly XFont regular;
    private readonly XFont bold;
    private readonly XFont small;
    private readonly XBrush navy = new XSolidBrush(XColor.FromArgb(20, 55, 82));
    private readonly string title, number, period, user, logo;
    private readonly DateTime generated;
    private readonly bool sampleRegister;
    private double y;
    private const double Left = 32, Width = 778, Bottom = 537;

    internal TrendPdfReportWriter(string title, string number, string period, string user, DateTime generated, string logo, bool sampleRegister = false)
    {
        this.sampleRegister = sampleRegister; this.title = title; this.number = number; this.period = period; this.user = user; this.generated = generated; this.logo = logo;
        if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new WindowsFontResolver();
        regular = new XFont("Arial", 8, XFontStyleEx.Regular);
        bold = new XFont("Arial", 8, XFontStyleEx.Bold);
        small = new XFont("Arial", 7, XFontStyleEx.Regular);
        document.Info.Title = title; document.Info.Author = user;
        document.Info.Subject = sampleRegister ? "Medica sample registration register; source-record export." : "Medica system-generated trend review draft; requires documented review and QA approval.";
        document.Info.CreationDate = generated;
    }

    internal static void Write(string path, string title, string number, string period, string user, DateTime generated,
        string logo, IReadOnlyList<string> summary, IReadOnlyList<TrendReportChart> charts, IReadOnlyList<TrendReportTable> tables,
        string narrative = "", bool sampleRegister = false)
    {
        using var writer = new TrendPdfReportWriter(title, number, period, user, generated, logo, sampleRegister);
        writer.NewPage(sampleRegister ? "Register overview" : "Results overview");
        foreach (string line in summary) writer.Paragraph(line);
        foreach (TrendReportChart chart in charts)
        {
            if (writer.y + 305 > Bottom) writer.NewPage("Trend curves");
            writer.Paragraph(chart.Title, true);
            using XImage image = XImage.FromStream(new MemoryStream(chart.Image));
            double scale = Math.Min(Width / image.PixelWidth, 260d / image.PixelHeight);
            writer.graphics!.DrawImage(image, Left + (Width - image.PixelWidth * scale) / 2, writer.y + 8,
                image.PixelWidth * scale, image.PixelHeight * scale);
            writer.y += image.PixelHeight * scale + 24;
            writer.Paragraph("Results are separated by sampling point. Historical limits use each observation's frozen evidence. Qualified values are boundary markers, excluded from exact-value statistics. Missing evidence is not a passing result.");
        }
        foreach (TrendReportTable table in tables) writer.Table(table);
        if (!string.IsNullOrWhiteSpace(narrative))
        {
            writer.EnsureSpace(80, "Trend assessment");
            writer.Paragraph("Trend assessment", true);
            foreach (string block in narrative.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)) writer.Paragraph(block);
        }
        writer.EnsureSpace(90, "Review and approval");
        writer.Paragraph("Prepared by (Microbiology): __________________    Checked by (Head of Microbiology): __________________", true);
        if (!sampleRegister) writer.Paragraph("Approved by (Quality Assurance): __________________    Date: __________________", true);
        writer.Paragraph(sampleRegister ? "SYSTEM-GENERATED REGISTER EXPORT. Dates and names are shown as recorded. Not recorded means the source has no documented value. This register is not a certificate of analysis or a release decision." : "SYSTEM-GENERATED REVIEW DRAFT. This report requires documented review and QA approval under the applicable approved procedure. Source records, historical specifications and audit evidence remain unchanged.");
        writer.graphics?.Dispose(); writer.graphics = null;
        for (int i = 0; i < writer.document.PageCount; i++)
        {
            using XGraphics footer = XGraphics.FromPdfPage(writer.document.Pages[i], XGraphicsPdfPageOptions.Append);
            footer.DrawLine(XPens.LightGray, Left, 548, Left + Width, 548);
            footer.DrawString(sampleRegister ? $"MEDICA | SAMPLE REGISTER | SYSTEM EXPORT | {number}" : $"MEDICA | MQC-R-TREND-001 | REVIEW DRAFT | {number}", writer.small, XBrushes.Gray, new XRect(Left, 554, Width - 130, 12), XStringFormats.TopLeft);
            footer.DrawString($"Page {i + 1} of {writer.document.PageCount}", writer.small, XBrushes.Gray, new XRect(Left, 554, Width, 12), XStringFormats.TopRight);
            footer.DrawString("Generated " + generated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (sampleRegister ? " | Source record export; uncontrolled copy when printed" : " | Uncontrolled when printed until reviewed and approved"), writer.small, XBrushes.Gray, new XRect(Left, 568, Width, 12), XStringFormats.TopLeft);
        }
        writer.document.Save(path);
    }

    private void NewPage(string section)
    {
        graphics?.Dispose();
        PdfPage page = document.AddPage(); page.Width = XUnit.FromPoint(842); page.Height = XUnit.FromPoint(595);
        graphics = XGraphics.FromPdfPage(page);
        graphics.DrawRectangle(navy, Left, 22, Width, 46);
        if (File.Exists(logo))
        {
            using XImage image = XImage.FromFile(logo);
            graphics.DrawRectangle(XBrushes.White, Left + 7, 27, 110, 35);
            double scale = Math.Min(100d / image.PixelWidth, 29d / image.PixelHeight);
            graphics.DrawImage(image, Left + 12 + (100 - image.PixelWidth * scale) / 2, 30 + (29 - image.PixelHeight * scale) / 2, image.PixelWidth * scale, image.PixelHeight * scale);
        }
        else graphics.DrawString("MEDICA", new XFont("Arial", 17, XFontStyleEx.Bold), XBrushes.White, new XRect(Left + 12, 34, 105, 24), XStringFormats.TopLeft);
        graphics.DrawString("MEDICA PHARMACEUTICAL INDUSTRY", new XFont("Arial", 15, XFontStyleEx.Bold), XBrushes.White, new XRect(Left + 126, 29, Width - 145, 20), XStringFormats.TopLeft);
        graphics.DrawString(sampleRegister ? "Microbiology Department | Sample Registration Register" : "Microbiology Department | Reports and Trends", regular, XBrushes.White, new XRect(Left + 126, 50, Width - 145, 12), XStringFormats.TopLeft);
        y = 79; Paragraph(title, true); Paragraph("Period / scope: " + period);
        Paragraph("Report: " + number + " | Generated by: " + user + (sampleRegister ? " | SYSTEM-GENERATED REGISTER EXPORT" : " | SYSTEM-GENERATED REVIEW DRAFT"));
        graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(230, 239, 246)), Left, y, Width, 23);
        graphics.DrawString(section, bold, navy, new XRect(Left + 8, y + 6, Width - 16, 14), XStringFormats.TopLeft); y += 32;
    }

    private List<string> Wrap(string text, XFont font, double width)
    {
        List<string> lines = new();
        foreach (string paragraph in text.Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (string word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && graphics!.MeasureString(line + " " + word, font).Width > width) { lines.Add(line); line = ""; }
                string rest = word;
                while (graphics!.MeasureString(rest, font).Width > width)
                {
                    int count = rest.Length - 1;
                    while (count > 1 && graphics.MeasureString(rest[..count], font).Width > width) count--;
                    if (line.Length > 0) { lines.Add(line); line = ""; }
                    lines.Add(rest[..count]); rest = rest[count..];
                }
                line = line.Length == 0 ? rest : line + " " + rest;
            }
            lines.Add(line);
        }
        return lines;
    }

    private void EnsureSpace(double height, string section) { if (y + height > Bottom) NewPage(section + " - continued"); }

    private void Paragraph(string text, bool heading = false)
    {
        XFont font = heading ? bold : regular;
        foreach (string line in Wrap(text, font, Width - 12))
        {
            // NewPage calls Paragraph only for bounded metadata before y reaches the content area.
            EnsureSpace(13, sampleRegister ? "Register notes" : "Trend assessment");
            graphics!.DrawString(line, font, heading ? navy : XBrushes.Black, new XRect(Left + 6, y, Width - 12, 12), XStringFormats.TopLeft); y += 12;
        }
        y += 6;
    }

    private void Table(TrendReportTable table)
    {
        string[] columns = table.Columns.Where(table.Data.Columns.Contains).ToArray();
        if (columns.Length == 0) return;
        NewPage(table.Title);
        double[] weights = columns.Select(c => c is "Location" or "AreaName" or "TestName" or "ValueIntegrity" or "EvidenceSource" ? 2d : c.Contains("Date") || c == "SampleNumber" || c == "EventNo" ? 1.6d : 1d).ToArray();
        if (sampleRegister) weights = columns.Select(c => c switch { "No" => .35d, "Description" or "Dates" => 1.8d, "Tests" => 1.5d, "SampleNumber" => 1.6d, "Quantity" => .8d, "SampledBy" or "ReceivedBy" or "RegisteredBy" => .9d, "Status" => 1.1d, _ => 1d }).ToArray();
        double[] widths = weights.Select(w => Width * w / weights.Sum()).ToArray();
        static bool IsContinuationContentColumn(string column) =>
            column is "Description" or "Location" or "EvidenceSource" or "ValueIntegrity" or "HistoricalContext";

        void DrawCells(List<string>[] cells, int fromLine, int lineCount, bool header, bool alternate, string status)
        {
            double height = lineCount * 11 + 9, x = Left;
            for (int i = 0; i < columns.Length; i++)
            {
                XBrush background = header ? navy : alternate ? new XSolidBrush(XColor.FromArgb(246, 249, 252)) : XBrushes.White;
                if (!header && columns[i] is "Status" or "Assessment") background = status.ToUpperInvariant() switch
                { "FAIL" or "ACTION" or "OOS" => new XSolidBrush(XColor.FromArgb(255, 225, 225)), "ALERT" => new XSolidBrush(XColor.FromArgb(255, 239, 209)), "PASS" => new XSolidBrush(XColor.FromArgb(224, 244, 231)), _ => background };
                graphics!.DrawRectangle(new XPen(XColors.LightGray, .4), background, x, y, widths[i], height);

                int cellOffset = !header && fromLine > 0 && !IsContinuationContentColumn(columns[i]) ? 0 : fromLine;
                XBrush foreground = !header && fromLine > 0 && !IsContinuationContentColumn(columns[i]) ? XBrushes.Gray : header ? XBrushes.White : XBrushes.Black;
                for (int l = 0; l < lineCount && cellOffset + l < cells[i].Count; l++)
                    graphics.DrawString(cells[i][cellOffset + l], header ? bold : regular, foreground, new XRect(x + 4, y + 4 + l * 11, widths[i] - 8, 11), XStringFormats.TopLeft);
                x += widths[i];
            }
            y += height;
        }
        void Header()
        {
            List<string>[] cells = columns.Select((c, i) => Wrap(RegexLabel(c), bold, widths[i] - 8)).ToArray();
            DrawCells(cells, 0, cells.Max(c => c.Count), true, false, "");
        }
        Header();
        int rowNumber = 0;
        foreach (DataRow row in table.Data.Rows)
        {
            List<string>[] cells = columns.Select((c, i) => Wrap(c == "ResultValue" ? TrendReportData.ResultText(row) : FormatValue(row[c]), regular, widths[i] - 8)).ToArray();
            int total = cells.Max(c => c.Count), offset = 0;
            string status = TrendReportData.Text(row, "Status"); if (status.Length == 0) status = TrendReportData.Text(row, "Assessment");
            while (offset < total)
            {
                int available = (int)((Bottom - y - 9) / 11);
                if (available < 1 || (offset == 0 && total <= 25 && available < total)) { NewPage(table.Title + " - continued"); Header(); available = (int)((Bottom - y - 9) / 11); }
                int consume = Math.Min(available, total - offset);
                int repeatedContextLines = offset > 0
                    ? cells.Where((cell, index) => !IsContinuationContentColumn(columns[index])).Select(cell => cell.Count).DefaultIfEmpty(1).Max()
                    : 1;
                int drawLines = Math.Min(available, Math.Max(consume, repeatedContextLines));
                DrawCells(cells, offset, drawLines, false, rowNumber % 2 == 1, status);
                offset += consume;
            }
            rowNumber++;
        }
        Paragraph($"End of {table.Title}: {table.Data.Rows.Count} record(s).");
    }

    private static string FormatValue(object raw) => raw == DBNull.Value ? "NR" : raw is DateTimeOffset offset ? offset.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : raw is DateTime date ? date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
    private static string RegexLabel(string text) => System.Text.RegularExpressions.Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
    public void Dispose() { graphics?.Dispose(); document.Dispose(); }
}
