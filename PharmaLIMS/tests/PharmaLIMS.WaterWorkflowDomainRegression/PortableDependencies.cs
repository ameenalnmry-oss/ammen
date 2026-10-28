using System.Data;
using Microsoft.Data.SqlClient;
namespace PharmaLIMS.Infrastructure
{
    // SQL helpers are linked only to exercise their actual installed pure rules. A portable test must never call a database adapter.
    public sealed class DatabaseConnection { public DataTable ExecuteQuery(string sql,SqlParameter[]? parameters=null)=>throw new InvalidOperationException("Portable domain tests cannot access SQL."); }
}
namespace PharmaLIMS
{
    internal static class AppConfig {internal const int CommandTimeoutSeconds=30;}
}
namespace PharmaLIMS.Services
{
    public sealed class LabEquipmentChoice
    {
        public int EquipmentID {get;init;} public string EquipmentCode {get;init;}=""; public string EquipmentName {get;init;}="";
        public string EquipmentType {get;init;}=""; public bool IsAvailable {get;init;} public string ReadinessLabel {get;init;}="";
    }
}
