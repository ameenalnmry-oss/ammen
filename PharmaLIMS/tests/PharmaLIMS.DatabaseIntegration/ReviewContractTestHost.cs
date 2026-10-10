using System.Data;
using Microsoft.Data.SqlClient;
namespace PharmaLIMS;
// Transaction services below receive the real disposable connection. The host-only
// wrappers are deliberately unusable: tests must not silently invoke WPF/global DB routing.
internal static class AppConfig { internal static int CommandTimeoutSeconds => 60; }
public static partial class DatabaseHelper
{
    public static DataTable ExecuteQuery(string sql,SqlParameter[] parameters)=>throw new InvalidOperationException("Use explicit disposable transaction services in integration tests.");
    public static void ExecuteInTransaction(Action<SqlConnection,SqlTransaction> action)=>throw new InvalidOperationException("Use an explicit disposable test transaction.");
    public static void ExecuteNonQuery(string sql,SqlParameter[] parameters)=>throw new InvalidOperationException("Use an explicit disposable test transaction.");
    private static void ValidateSignatureMetadata(int id,string action,string meaning)=>throw new InvalidOperationException("Use explicit SQL contracts, not host signature routing.");
    private static string ResolveAuthenticatedSigner(string user)=>throw new InvalidOperationException("Use explicit transaction authorization services.");
    internal static string EnsureUserPermissionInTransaction(SqlConnection c,SqlTransaction t,string user,string permission,string action)=>throw new InvalidOperationException("Use explicit transaction authorization services.");
    private static void AddAuditTrailAdvanced(SqlConnection c,SqlTransaction t,string table,int id,string action,string oldValue,string newValue,string reason,string user,string field,object? relatedId,string number,string module)=>throw new InvalidOperationException("Use explicit disposable audit fixtures.");
}
