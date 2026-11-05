using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
internal static class AppendOnlyPreflightCases
{
    private static void Require(bool value,string context) { if(!value)throw new InvalidOperationException(context); }
    private static async Task<DataTable> Query(SqlConnection c,string sql,SqlTransaction? t=null)
    { using var q=new SqlCommand(sql,c,t);using var reader=await q.ExecuteReaderAsync();var rows=new DataTable();rows.Load(reader);return rows; }
    private static async Task Exec(SqlConnection c,string sql,SqlTransaction? t=null)
    { using var q=new SqlCommand(sql,c,t);await q.ExecuteNonQueryAsync(); }
    internal static async Task RunAsync(SqlConnection c)
    {
        string frozen = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "R9-ReviewClosureSchema-before-trigger-guard.sql"));
        const string name = "dbo.TRG_PRM_SpecificationContentHistory_AppendOnly";
        const string body = " AS BEGIN SET NOCOUNT ON; THROW 56456,'PRM specification content history is append-only and cannot be updated or deleted.',1; END;";
        var mutations = new (string Name, string[] Sql)[] {
            ("disabled",new[]{"DISABLE TRIGGER "+name+" ON dbo.PRM_SpecificationContentHistory;"}),
            ("missing",new[]{"DROP TRIGGER "+name+";"}),
            ("no protection",new[]{"CREATE OR ALTER TRIGGER "+name+" ON dbo.PRM_SpecificationContentHistory AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; END;"}),
            ("one event",new[]{"CREATE OR ALTER TRIGGER "+name+" ON dbo.PRM_SpecificationContentHistory AFTER UPDATE"+body}),
            ("wrong table",new[]{"DROP TRIGGER "+name+";","CREATE TRIGGER "+name+" ON dbo.PRM_SpecificationSignatures AFTER UPDATE,DELETE"+body}),
            ("conditional rejection",new[]{"CREATE OR ALTER TRIGGER "+name+" ON dbo.PRM_SpecificationContentHistory AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; IF 1=0 THROW 56456,'PRM specification content history is append-only and cannot be updated or deleted.',1; END;"})
        };
        foreach (var mutation in mutations)
        {
            using var tx = c.BeginTransaction();
            try
            {
                foreach (string sql in mutation.Sql) await Exec(c, sql, tx);
                Require((await Query(c, frozen, tx)).Rows.Count == 0,
                    "frozen preflight RED witness: " + mutation.Name);
                Require((await Query(c, ReviewClosureSchemaContract.Sql, tx)).Rows.Count > 0,
                    "active preflight must reject PRM append-only " + mutation.Name);
            }
            finally { tx.Rollback(); }
        }
        Require((await Query(c, ReviewClosureSchemaContract.Sql)).Rows.Count == 0,
            "readiness mutation tests restore the original protected trigger");
        Console.WriteLine("PASS PRM append-only preflight differential: six unsafe trigger variants escaped frozen R9 and are blocked by active contract; disposable DDL rolled back.");
    }

}
