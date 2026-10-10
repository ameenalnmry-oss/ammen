using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;
namespace PharmaLIMS;
public static partial class DatabaseHelper
{
    internal static string EnsureCultureMediaEntryAuthorizationInTransaction(SqlConnection connection, SqlTransaction transaction, string signedBy, string action)
    {
        return Services.ReviewWorkflowAuthorization.EnsureCultureEntry(connection, transaction, signedBy, action,
            AppConfig.IsDevelopment && AppConfig.DevelopmentAdminFullPermissions);
    }
}
