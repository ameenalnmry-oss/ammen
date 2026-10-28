using Microsoft.Data.SqlClient;
namespace PharmaLIMS {
internal static class AppConfig { public static int CommandTimeoutSeconds => 15; }
public static partial class DatabaseHelper {
internal static int ExecuteNonQueryWithTransaction(string sql, SqlParameter[] parameters, SqlConnection connection, SqlTransaction transaction) { using var command=new SqlCommand(sql,connection,transaction); command.Parameters.AddRange(parameters); return command.ExecuteNonQuery(); }
}}
