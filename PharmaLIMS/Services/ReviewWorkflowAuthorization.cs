using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;

namespace PharmaLIMS.Services;

// Authorization is read again under the same transaction as the controlled write.
internal static class ReviewWorkflowAuthorization
{
    internal static string EnsureCultureEntry(SqlConnection connection, SqlTransaction transaction,
        string username, string action, bool developmentAdministratorOverride) =>
        Ensure(connection, transaction, username, action,
            "CASE WHEN ISNULL(CanEnterResults,0)=1 OR ISNULL(CanRegisterSamples,0)=1 THEN 1 ELSE 0 END",
            developmentAdministratorOverride);

    internal static string EnsurePrmReissue(SqlConnection connection, SqlTransaction transaction,
        string username, bool developmentAdministratorOverride) =>
        Ensure(connection, transaction, username, "cancel/reissue a PRM certificate/report",
            "CASE WHEN ISNULL(CanIssueCOA,0)=1 AND ISNULL(CanCancelCOA,0)=1 THEN 1 ELSE 0 END",
            developmentAdministratorOverride);

    private static string Ensure(SqlConnection connection, SqlTransaction transaction, string username,
        string action, string permissionExpression, bool developmentAdministratorOverride)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new UnauthorizedAccessException("An authenticated user is required for " + action + ".");
        using var command = new SqlCommand(@"SELECT ISNULL(Role,N''), ISNULL(IsActive,1),
ISNULL(MustChangePassword,0), CASE WHEN ISNULL(IsLocked,0)=1 AND
(LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END, " + permissionExpression + @"
FROM dbo.Users WITH (UPDLOCK, HOLDLOCK) WHERE Username=@User;", connection, transaction)
            { CommandTimeout = AppConfig.CommandTimeoutSeconds };
        command.Parameters.Add("@User", SqlDbType.NVarChar, 256).Value = username.Trim();
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new UnauthorizedAccessException("The authenticated account was not found.");
        string role = reader.GetString(0).Trim();
        bool active = Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture);
        bool mustChange = Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture);
        bool locked = Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
        bool allowed = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);
        if (reader.Read()) throw new UnauthorizedAccessException("Duplicate user identity was detected.");
        bool admin = developmentAdministratorOverride &&
            (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) || role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        if (!active || mustChange || locked || (!allowed && !admin))
            throw new UnauthorizedAccessException("The authenticated user no longer has permission to " + action + ".");
        return role;
    }
}
