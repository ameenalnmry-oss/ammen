using System.Data;
using System.Threading;

namespace PharmaLIMS.Infrastructure;

// Bounded compatibility for already-initialized signers in isolated Windows UAT.
// It does not grant credential replacement or general data-writing privileges.
internal static class WindowsSigningUatRuntime
{
    internal const string RoleName = "PharmaLIMS_EquipmentWindowsUat";

    internal static async Task<bool> IsEnabledAsync(
        DatabaseConnection database, CancellationToken cancellationToken = default)
    {
        DataTable result = await database.ExecuteQueryAsync(
            "SELECT CONVERT(bit,CASE WHEN IS_ROLEMEMBER(N'PharmaLIMS_EquipmentWindowsUat')=1 THEN 1 ELSE 0 END) AS Restricted, DB_NAME() AS DatabaseName;",
            commandTimeoutSeconds: 10, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Rows.Count != 1)
            throw new InvalidOperationException("Windows signing UAT runtime could not be verified.");
        if (!Convert.ToBoolean(result.Rows[0]["Restricted"])) return false;
        string name = Convert.ToString(result.Rows[0]["DatabaseName"]) ?? string.Empty;
        if (!AppConfig.IsDevelopment ||
            !(name == "PharmaLIMS_UAT_20261001" ||
              name.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal) ||
              name.StartsWith("PharmaLIMS_UAT_CLEAN_", StringComparison.Ordinal)))
            throw new InvalidOperationException("Windows signing test access is limited to isolated Development UAT.");
        return true;
    }
}
