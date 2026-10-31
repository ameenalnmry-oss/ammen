#nullable enable
using System;
namespace PharmaLIMS.Services
{
    // Shared by transaction boundaries and portable state regressions.
    internal static class EmControlledLifecycleGuard
    {
        internal static void EnsureInvestigationEvidence(string? captured, string current, bool invalidated)
        {
            if (invalidated || string.IsNullOrWhiteSpace(captured) ||
                !string.Equals(captured, current, StringComparison.Ordinal))
                throw new InvalidOperationException("The EM investigation evidence no longer matches the saved results. A new investigation of the current evidence is required; historical decisions are retained.");
        }

        internal static void EnsurePlanActive(int? planId, string? status)
        {
            if (!planId.HasValue) return; // Historical standalone events have no plan.
            if (string.IsNullOrWhiteSpace(status) || string.Equals(status.Trim(), "Cancelled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The parent EM plan is missing or cancelled. EM result processing and approved reporting are blocked.");
        }

        internal static (int Area, string EmployeeId, string EmployeeName) ReleaseGroupKey(
            int areaId, string method, string employeeId, string employeeName)
        {
            string id = employeeId ?? "";
            string name = employeeName ?? "";
            if (((method ?? "").IndexOf("personnel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (method ?? "").IndexOf("glove", StringComparison.OrdinalIgnoreCase) >= 0) &&
                (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)))
                throw new InvalidOperationException("Employee ID and name are required before personnel samples can be released.");
            return (areaId, id, name);
        }
    }
}
