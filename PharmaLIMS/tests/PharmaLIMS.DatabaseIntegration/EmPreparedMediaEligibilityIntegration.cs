using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.RegularExpressions;

internal static class EmPreparedMediaEligibilityIntegration
{
    internal static async Task VerifyAsync(SqlConnection connection, string projectRoot)
    {
        if (!connection.Database.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("EM eligibility tests refuse a non-disposable database.");

        string planning = File.ReadAllText(Path.Combine(projectRoot, "EMPlanning.xaml.cs"));
        string scheduler = File.ReadAllText(Path.Combine(projectRoot, "EMPlanning.xaml.Part2.cs"));
        string planSql = LiteralAfter(planning, "private static int EnsureReleasedMediaPreparation(");
        string collectionSql = LiteralAfter(planning, "using SqlCommand mediaGate =");
        string existsLine = scheduler.Split('\n').Single(line =>
            line.TrimStart().StartsWith("AND EXISTS(SELECT 1 FROM dbo.MediaPreparations", StringComparison.Ordinal));
        string scheduleSql = "SELECT 1 FROM dbo.EM_Schedules WHERE " + existsLine.Trim()[4..] + ";";

        // Execute the actual source SQL against connection-local temporary fixtures.
        // No application table or historical migration is changed by this test.
        foreach (string table in new[] { "MediaPreparations", "CultureMediaLots", "CultureMedia", "EM_PlanSamples", "EM_Schedules" })
        {
            planSql = planSql.Replace("dbo." + table + " ", "#" + table + " ", StringComparison.Ordinal);
            collectionSql = collectionSql.Replace("dbo." + table + " ", "#" + table + " ", StringComparison.Ordinal);
            scheduleSql = scheduleSql.Replace("dbo." + table, "#" + table, StringComparison.Ordinal);
        }

        await ExecuteAsync(connection, @"
CREATE TABLE #MediaPreparations(MediaPreparationID int, MediaPreparationNo nvarchar(100), MediaID int,
    MediaLotID int, ReleaseStatus nvarchar(50), SterilityReview nvarchar(50), ExpiryDate date);
CREATE TABLE #CultureMedia(MediaID int, MediaCode nvarchar(100), MediaName nvarchar(200));
CREATE TABLE #CultureMediaLots(MediaLotID int);
CREATE TABLE #EM_PlanSamples(PlanSampleID int, PlanID int, SampleCode nvarchar(100), MediaPreparationID int);
CREATE TABLE #EM_Schedules(MediaPreparationID int);
INSERT #CultureMedia VALUES(1,N'TSA',N'Tryptic Soy Agar');
INSERT #CultureMediaLots VALUES(1);
INSERT #MediaPreparations VALUES(1,N'MP-1',1,1,N'Released',NULL,DATEADD(day,1,CAST(GETDATE() AS date)));
INSERT #EM_PlanSamples VALUES(1,1,N'EM-1',1);
INSERT #EM_Schedules VALUES(1);");
        try
        {
            foreach (string? legacy in new string?[] { null, "", "Pending", "Passed", "GPT PASSED", "Rejected" })
            {
                await ExecuteAsync(connection, "UPDATE #MediaPreparations SET SterilityReview=@Legacy;",
                    new SqlParameter("@Legacy", SqlDbType.NVarChar, 50) { Value = (object?)legacy ?? DBNull.Value });
                await AssertEligibilityAsync(connection, planSql, scheduleSql, collectionSql, true);
            }
            foreach (string? status in new string?[] { null, "", "Under Release", "Rejected", "Quarantine" })
            {
                await ExecuteAsync(connection, "UPDATE #MediaPreparations SET ReleaseStatus=@Status,SterilityReview=N'Passed';",
                    new SqlParameter("@Status", SqlDbType.NVarChar, 50) { Value = (object?)status ?? DBNull.Value });
                await AssertEligibilityAsync(connection, planSql, scheduleSql, collectionSql, false);
            }
            await ExecuteAsync(connection, "UPDATE #MediaPreparations SET ReleaseStatus=N' released ',ExpiryDate=CAST(GETDATE() AS date);");
            await AssertEligibilityAsync(connection, planSql, scheduleSql, collectionSql, true);
            await ExecuteAsync(connection, "UPDATE #MediaPreparations SET ExpiryDate=DATEADD(day,-1,CAST(GETDATE() AS date));");
            await AssertEligibilityAsync(connection, planSql, scheduleSql, collectionSql, false);
            await ExecuteAsync(connection, "UPDATE #MediaPreparations SET ExpiryDate=NULL;");
            await AssertEligibilityAsync(connection, planSql, scheduleSql, collectionSql, false);
            await ExecuteAsync(connection, "DELETE #MediaPreparations;");
            await AssertEligibilityAsync(connection, planSql, scheduleSql, collectionSql, false);
            Console.WriteLine("PASS EM prepared-media eligibility: released/null legacy, unreleased, expiry boundary, missing expiry and missing preparation using actual source SQL.");
        }
        finally
        {
            await ExecuteAsync(connection, "DROP TABLE #EM_Schedules,#EM_PlanSamples,#CultureMediaLots,#CultureMedia,#MediaPreparations;");
        }
    }

    private static string LiteralAfter(string code, string marker)
    {
        int index = code.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("EM SQL source marker not found: " + marker);
        Match match = Regex.Match(code[(index + marker.Length)..], "@\"((?:\"\"|[^\"])*)\"");
        if (!match.Success) throw new InvalidOperationException("EM SQL source literal not found.");
        return match.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertEligibilityAsync(SqlConnection connection, string planSql, string scheduleSql, string collectionSql, bool eligible)
    {
        foreach ((string sql, bool expected) in new[] { (planSql, eligible), (scheduleSql, eligible), (collectionSql, !eligible) })
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
            command.Parameters.AddWithValue("@Reference", "MP-1");
            command.Parameters.AddWithValue("@Media", "TSA");
            command.Parameters.AddWithValue("@PlanID", 1);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync() != expected)
                throw new InvalidOperationException("EM prepared-media eligibility disagrees with the release/expiry contract.");
        }
    }
}
