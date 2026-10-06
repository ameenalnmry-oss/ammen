using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Text.RegularExpressions;

internal static class V303ReviewClosureIntegration
{
    internal static async Task VerifyAsync(SqlConnection connection, string projectRoot)
    {
        if (!connection.Database.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Disposable integration database required.");
        await VerifyReceiptStartAsync(connection);
        await VerifyExternalPrecisionAsync(connection);
        await VerifyWaterSpecificationLocksAsync(connection, projectRoot);
        Console.WriteLine("PASS v303 signed receipt before PRM start, exact external decimal storage and water specification lock/replacement race.");
    }

    private static async Task VerifyReceiptStartAsync(SqlConnection connection)
    {
        using var tx = connection.BeginTransaction();
        string number = "V303-RECEIPT-" + Guid.NewGuid().ToString("N");
        try
        {
            using var seed = new SqlCommand(@"
INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,SampleDateTime,CreatedDate,CreatedBy,SampleStatus)
OUTPUT INSERTED.SampleID VALUES(@number,N'Primary Packaging','20260101','20260103',N'V303 fixture',N'Registered');", connection, tx);
            seed.Parameters.Add("@number", SqlDbType.NVarChar, 100).Value = number;
            int id = Convert.ToInt32(await seed.ExecuteScalarAsync());
            async Task Start()
            {
                using var command = new SqlCommand(LaboratoryReceiptSql.GuardPrmAnalysisStart + @"
UPDATE dbo.PRM_Samples SET AnalysisStartedDate=SYSDATETIME(),SampleStatus=N'In Progress' WHERE SampleID=@SampleID;", connection, tx);
                command.Parameters.Add("@SampleID", SqlDbType.Int).Value = id;
                await command.ExecuteNonQueryAsync();
            }
            bool blocked = false;
            try { await Start(); }
            catch (SqlException ex) when (ex.Number == 55309) { blocked = true; }
            if (!blocked) throw new InvalidOperationException("PRM analysis started without a signed receipt.");
            using (var query = new SqlCommand("SELECT AnalysisStartedDate,SampleStatus FROM dbo.PRM_Samples WHERE SampleID=@id;", connection, tx))
            {
                query.Parameters.Add("@id", SqlDbType.Int).Value = id;
                using var reader = await query.ExecuteReaderAsync();
                if (!await reader.ReadAsync() || !reader.IsDBNull(0) || reader.GetString(1) != "Registered")
                    throw new InvalidOperationException("Blocked analysis start changed its timestamp or status.");
            }
            using (var receipt = new SqlCommand(LaboratoryReceiptSql.Insert, connection, tx))
            {
                receipt.Parameters.AddRange(LaboratoryReceiptSql.Parameters("PRM", id, number,
                    new DateTime(2026, 1, 2, 10, 15, 0), "V303 receiver", "Received intact against approved procedure",
                    "Acceptance for laboratory testing", "Technician", "V303 integration"));
                await receipt.ExecuteScalarAsync();
            }
            await Start();
            using var started = new SqlCommand("SELECT COUNT(*) FROM dbo.PRM_Samples WHERE SampleID=@id AND AnalysisStartedDate IS NOT NULL AND SampleStatus=N'In Progress';", connection, tx);
            started.Parameters.Add("@id", SqlDbType.Int).Value = id;
            if (Convert.ToInt32(await started.ExecuteScalarAsync()) != 1)
                throw new InvalidOperationException("Actual accepted signed receipt did not permit PRM analysis.");
        }
        finally { try { tx.Rollback(); } catch (InvalidOperationException) { } }
    }

    private static async Task VerifyExternalPrecisionAsync(SqlConnection connection)
    {
        foreach (string text in new[] { "100.0000000001", "0.0000000001", "100.000000000000", "9999999999999999999999999999" })
        {
            if (!ExternalTrendNumericContract.TryParseQualified(text, out decimal value, out _))
                throw new InvalidOperationException("Accepted exact external fixture was rejected.");
            using var command = new SqlCommand("SELECT CAST(@value AS decimal(38,10));", connection);
            command.Parameters.Add(new SqlParameter("@value", SqlDbType.Decimal) { Precision = 38, Scale = 10, Value = value });
            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync() || !ControlledNumericValue.TryParse(reader.GetSqlDecimal(0).ToString(), out decimal stored) || stored != value)
                throw new InvalidOperationException("An accepted external measurement changed during SQL storage.");
        }
        foreach (string text in new[] { "100.000000000001", "1e-11", "1,3", "-1" })
            if (ExternalTrendNumericContract.TryParseQualified(text, out _, out _))
                throw new InvalidOperationException("Unrepresentable or ambiguous external fixture was accepted.");
    }

    private static async Task VerifyWaterSpecificationLocksAsync(SqlConnection connection, string projectRoot)
    {
        // Use the actual selection statement with its production lock substitutions.
        // Global temporary tables allow a second connection to attempt replacement.
        string source = File.ReadAllText(Path.Combine(projectRoot, "DatabaseHelper.cs"));
        string method = source[source.IndexOf("public static DataTable GetEffectiveWaterTestSpecification", StringComparison.Ordinal)..];
        Match literal = Regex.Match(method, "string query = @\"((?:\"\"|[^\"])*)\"");
        if (!literal.Success) throw new InvalidOperationException("Water specification source SQL missing.");
        string query = literal.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal)
            .Replace("FROM dbo.WaterTestProfiles profile", "FROM dbo.WaterTestProfiles profile WITH(UPDLOCK,HOLDLOCK)", StringComparison.Ordinal)
            .Replace("INNER JOIN dbo.WaterSpecifications specification", "INNER JOIN dbo.WaterSpecifications specification WITH(UPDLOCK,HOLDLOCK)", StringComparison.Ordinal);
        string suffix = Guid.NewGuid().ToString("N");
        string profiles = "##V303Profiles_" + suffix, specs = "##V303Specs_" + suffix;
        query = query.Replace("dbo.WaterTestProfiles", profiles, StringComparison.Ordinal)
            .Replace("dbo.WaterSpecifications", specs, StringComparison.Ordinal);
        using (var setup = new SqlCommand($@"
CREATE TABLE {profiles}(ProfileID int PRIMARY KEY,ProfileCode nvarchar(20),IsActive bit,ApprovalStatus nvarchar(30),
 ApprovedBy nvarchar(100),ApprovedAt datetime2,ControlledReference nvarchar(100),EffectiveFrom date,EffectiveTo date);
CREATE TABLE {specs}(SpecificationID int PRIMARY KEY,ProfileID int,TestID int,IsActive bit,ApprovalStatus nvarchar(30),
 SpecificationText nvarchar(1000),PointCode nvarchar(50),EffectiveFrom date,EffectiveTo date,
 LowerLimit decimal(18,4),UpperLimit decimal(18,4),AlertLimit decimal(18,4),ActionLimit decimal(18,4));
INSERT {profiles} VALUES(1,N'PW',1,N'Approved',N'QA',SYSDATETIME(),N'Approved fixture',NULL,NULL);
INSERT {specs} VALUES(1,1,1,1,N'Approved',N'NMT 100',NULL,NULL,NULL,NULL,NULL,25,100);", connection))
            await setup.ExecuteNonQueryAsync();
        try
        {
            await using var writer = new SqlConnection(connection.ConnectionString);
            await writer.OpenAsync();
            using (var tx = connection.BeginTransaction())
            {
                using (var capture = new SqlCommand(query, connection, tx))
                {
                    capture.Parameters.AddWithValue("@profileCode", "PW");
                    capture.Parameters.AddWithValue("@testId", 1);
                    capture.Parameters.AddWithValue("@pointCode", "PWS-1");
                    using var reader = await capture.ExecuteReaderAsync();
                    if (!await reader.ReadAsync() || reader.GetDecimal(3) != 100m)
                        throw new InvalidOperationException("The approved water specification was not captured.");
                }
                bool blocked = false;
                using var replacement = new SqlCommand($"SET LOCK_TIMEOUT 500; UPDATE {specs} SET IsActive=0 WHERE SpecificationID=1;", writer) { CommandTimeout = 5 };
                try { await replacement.ExecuteNonQueryAsync(); }
                catch (SqlException ex) when (ex.Number == 1222) { blocked = true; }
                if (!blocked) throw new InvalidOperationException("A water specification was replaced during registration capture.");
                tx.Rollback();
            }
            using (var replace = new SqlCommand($"UPDATE {specs} SET IsActive=0 WHERE SpecificationID=1;", writer))
                await replace.ExecuteNonQueryAsync();
            using var fresh = new SqlCommand(query, connection);
            fresh.Parameters.AddWithValue("@profileCode", "PW"); fresh.Parameters.AddWithValue("@testId", 1); fresh.Parameters.AddWithValue("@pointCode", "PWS-1");
            using var freshReader = await fresh.ExecuteReaderAsync();
            if (await freshReader.ReadAsync()) throw new InvalidOperationException("Registration reused an inactive water specification.");
        }
        finally
        {
            using var cleanup = new SqlCommand($"DROP TABLE {specs}; DROP TABLE {profiles};", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
