using System;using System.Collections.Generic;using System.Linq;using System.Text;using System.Globalization;using PharmaLIMS.Infrastructure;
namespace PharmaLIMS.Infrastructure {
public sealed class SystemPreflightCheck
    {
        public string Area { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Check { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
    }
public sealed class SystemPreflightReport
    {
        public DateTime ExecutedAt { get; init; }
        public string Server { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;
        public IReadOnlyList<SystemPreflightCheck> Checks { get; init; } = Array.Empty<SystemPreflightCheck>();

        public int BlockerCount => Checks.Count(item => item.Status == "BLOCKER");
        public int WarningCount => Checks.Count(item => item.Status == "WARNING");
        public int PassCount => Checks.Count(item => item.Status == "PASS");
        public bool CanProceed => BlockerCount == 0;
    }
}
namespace PharmaLIMS { internal sealed class SystemPreflight {}
internal static class FrozenSitePreflight {
private static string BuildTextReport(SystemPreflightReport report)
        {
            var text = new StringBuilder();
            text.AppendLine("PharmaLIMS Runtime System Preflight");
            text.AppendLine($"Executed: {report.ExecutedAt:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Build: {typeof(SystemPreflight).Assembly.GetName().Version?.ToString() ?? "unknown"}");
            text.AppendLine($"Target: {report.Server} / {report.Database}");
            text.AppendLine($"Blockers: {report.BlockerCount}; Warnings: {report.WarningCount}; Passed: {report.PassCount}");
            text.AppendLine(new string('-', 88));

            foreach (SystemPreflightCheck check in report.Checks)
                text.AppendLine($"[{check.Status}] {check.Area} | {check.Check} | {check.Details}");

            return text.ToString();
        }
internal static string Export(SystemPreflightReport report)=>BuildTextReport(report);
private sealed class LabelControl { public string Text {get;set;}=""; }
internal static string Label(SystemPreflightReport report) { var _lastReport=report;var lblExecutedAt=new LabelControl();
lblExecutedAt.Text = $"Executed {_lastReport.ExecutedAt:yyyy-MM-dd HH:mm:ss} | {_lastReport.Server} / {_lastReport.Database}";
return lblExecutedAt.Text; }
}
internal static class CorrectedSitePreflight {
private static string BuildTextReport(SystemPreflightReport report)
        {
            var text = new StringBuilder();
            text.AppendLine("PharmaLIMS Runtime System Preflight");
            text.AppendLine($"Executed: {report.ExecutedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
            text.AppendLine($"Build: {typeof(SystemPreflight).Assembly.GetName().Version?.ToString() ?? "unknown"}");
            text.AppendLine($"Target: {report.Server} / {report.Database}");
            text.AppendLine($"Blockers: {report.BlockerCount}; Warnings: {report.WarningCount}; Passed: {report.PassCount}");
            text.AppendLine(new string('-', 88));

            foreach (SystemPreflightCheck check in report.Checks)
                text.AppendLine($"[{check.Status}] {check.Area} | {check.Check} | {check.Details}");

            return text.ToString();
        }
internal static string Export(SystemPreflightReport report)=>BuildTextReport(report);
private sealed class LabelControl { public string Text {get;set;}=""; }
internal static string Label(SystemPreflightReport report) { var _lastReport=report;var lblExecutedAt=new LabelControl();
lblExecutedAt.Text = $"Executed {_lastReport.ExecutedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} | {_lastReport.Server} / {_lastReport.Database}";
return lblExecutedAt.Text; }
}
}
