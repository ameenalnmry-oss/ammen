using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SitePrmCoverageCases
{
    private sealed record Queries(string Release, string Closure);
    private sealed record Case(string Name, string Arrange, int Release, int Closure);

    internal static async Task RunFrozen(string connectionString, string project)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(project,
            "tests/fixtures/AMEEN-PRM-coverage-20261029.json")));
        string ReadQuery(string name)
        {
            var entry = fixture.RootElement.GetProperty(name);
            string query = entry.GetProperty("Query").GetString()!;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
            if (hash != entry.GetProperty("QuerySha256").GetString())
                throw new Exception("Frozen site SQL hash mismatch: " + name);
            return query;
        }
        var frozen = new Queries(ReadQuery("Release"), ReadQuery("Closure"));
        var failures = await Evaluate(connectionString, frozen, "frozen");
        string[] expectedFailures = {
            "optional OOS/release", "optional OOS/closure",
            "optional Check Required/release", "optional Check Required/closure",
            "changed optional snapshot/closure"
        };
        if (!failures.Order().SequenceEqual(expectedFailures.Order()))
            throw new Exception("Frozen site defect did not reproduce exactly: " + string.Join(", ", failures));
        Console.WriteLine("PASS SITE PRM FROZEN: five acceptance failures reproduced in actual uploaded SQL; controls for blank, conforming, required and legacy evidence retained.");
    }

    private static async Task<List<string>> Evaluate(string connectionString, Queries queries, string variant)
    {
        var cases = new[] {
            new Case("optional OOS", "", 3, 1),
            new Case("optional Check Required", "UPDATE dbo.PRM_SampleTests SET Interpretation=N'Check Required' WHERE SampleTestID=2;", 3, 1),
            new Case("optional blank", "UPDATE dbo.PRM_SampleTests SET ResultValue=N' ',Interpretation=N'Check Required' WHERE SampleTestID=2;", 0, 0),
            new Case("optional NULL", "UPDATE dbo.PRM_SampleTests SET ResultValue=NULL,Interpretation=N'Check Required' WHERE SampleTestID=2;", 0, 0),
            new Case("optional conforms", "UPDATE dbo.PRM_SampleTests SET ResultValue=N'3',Interpretation=N'Conforms' WHERE SampleTestID=2;", 0, 0),
            new Case("optional covered", CoverOptional, 0, 0),
            new Case("changed optional snapshot", CoverOptional+"UPDATE dbo.PRM_SampleTests SET ResultValue=N'31' WHERE SampleTestID=2;", 3, 1),
            new Case("NULL required defaults required", "UPDATE dbo.PRM_SampleTests SET RequiredTest=NULL,ResultValue=N' ',Interpretation=N'Check Required' WHERE SampleTestID=2;", 3, 1),
            new Case("required uncovered", "DELETE dbo.QualityEventAffectedResults; UPDATE dbo.PRM_SampleTests SET ResultValue=NULL WHERE SampleTestID=2;", 3, 1),
            new Case("other sample excluded", "UPDATE dbo.PRM_SampleTests SET SampleID=2 WHERE SampleTestID=2;", 0, 0),
            new Case("traceable legacy evidence retained", """
UPDATE dbo.PRM_SampleTests SET ResultValue=NULL,Interpretation=N'Check Required' WHERE SampleTestID=2;
INSERT dbo.QualityEvents SELECT 0,SourceModule,SourceRecordID,CurrentStatus,ClosedBy,ClosedDate,RootCauseCategory,RootCauseDetails,ImpactAssessment,QAConclusion,FinalDisposition FROM dbo.QualityEvents WHERE QualityEventID=1;
INSERT dbo.QualityEventAffectedResults SELECT 0,N'PRM',SampleTestID,0,TestName,N'old evidence',SpecificationText,SpecificationLimit,Unit,Interpretation FROM dbo.PRM_SampleTests WHERE SampleTestID=2;
""", 3, 1)
        };
        var failures = new List<string>();
        foreach (var test in cases)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            try
            {
                using (var arrange = new SqlCommand(test.Arrange.Length == 0 ? "SELECT 1;" : test.Arrange, connection, transaction))
                    await arrange.ExecuteNonQueryAsync();
                async Task<int> Execute(string sql)
                {
                    using var command = new SqlCommand("DECLARE @SampleID int=1,@ReplacementQualityEventID int=1;" + sql, connection, transaction);
                    return Convert.ToInt32(await command.ExecuteScalarAsync());
                }
                foreach (var check in new[] {
                    (Name: test.Name+"/release", Actual: await Execute(queries.Release), Expected: test.Release),
                    (Name: test.Name+"/closure", Actual: await Execute(queries.Closure), Expected: test.Closure) })
                {
                    if (check.Actual == check.Expected) continue;
                    failures.Add(check.Name);
                    Console.WriteLine($"SITE PRM {variant}: {check.Name} expected {check.Expected}, actual {check.Actual}");
                }
            }
            finally { transaction.Rollback(); }
        }
        Console.WriteLine($"SITE PRM {variant}: {cases.Length*2-failures.Count}/{cases.Length*2} acceptance checks passed; {failures.Count} failed.");
        return failures;
    }

    private const string CoverOptional = "INSERT dbo.QualityEventAffectedResults SELECT 1,N'PRM',SampleTestID,1,TestName,ResultValue,SpecificationText,SpecificationLimit,Unit,Interpretation FROM dbo.PRM_SampleTests WHERE SampleTestID=2;";
}
