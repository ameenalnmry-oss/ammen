using System;
using System.Collections.Generic;
using System.Linq;

namespace PharmaLIMS.Services;
internal static class EmPersonnelHandoffContract
{
    internal static string EmployeeKey(string method, string employeeId)
    {
        if (!string.Equals(method?.Trim(), "Personnel Monitoring", StringComparison.OrdinalIgnoreCase)) return "";
        if (string.IsNullOrWhiteSpace(employeeId)) throw new InvalidOperationException("Personnel samples require a recorded Employee ID before release.");
        return employeeId.Trim().ToUpperInvariant();
    }

    internal static void Validate(IEnumerable<(string Id, string Name)> people)
    {
        var rows = people.ToArray();
        if (rows.Length == 0) throw new InvalidOperationException("No personnel evidence was supplied.");
        var first = rows[0];
        if (string.IsNullOrWhiteSpace(first.Id) || string.IsNullOrWhiteSpace(first.Name) || first.Id.Length > 50 || first.Name.Length > 150 ||
            rows.Any(row => !row.Id.Trim().Equals(first.Id.Trim(), StringComparison.OrdinalIgnoreCase) || !row.Name.Equals(first.Name, StringComparison.Ordinal)))
            throw new InvalidOperationException("Personnel samples require a consistent recorded Employee ID (up to 50 characters) and name (up to 150) before release.");
    }
}
