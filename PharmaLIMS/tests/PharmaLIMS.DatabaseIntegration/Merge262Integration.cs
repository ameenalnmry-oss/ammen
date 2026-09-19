using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Security.Cryptography;

// Executes the SAME failure SQL and stored-value verifier used by production.
// The caller owns a freshly-created GUID-named disposable test database.
internal static class Merge262Integration
{
    internal static async Task VerifyAsync(string connectionString)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.InitialCatalog.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Merged tests refuse a non-disposable database.");
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        string username = "MERGE262_" + Guid.NewGuid().ToString("N");
        string hash = "TEST_ONLY_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string salt = "TEST_ONLY_SALT";
        var ids = await QueryAsync(c, @"
DECLARE @Ids TABLE(Id int);
INSERT dbo.Users(Username,PasswordHash,PasswordHashNew,PasswordSalt,FullName,Role,IsActive)
OUTPUT inserted.UserID INTO @Ids
VALUES(@name,N'[MIGRATED]',@hash,@salt,N'Merge disposable fixture',N'Technician',1);
SELECT Id FROM @Ids;", new SqlParameter("@name",username), new SqlParameter("@hash",hash), new SqlParameter("@salt",salt));
        int id = Convert.ToInt32(ids.Rows[0][0]);
        var checkedCredential = new Credential(id, username, "[MIGRATED]", hash, salt);

        // A stale failure after a password reset must not be charged to the new credential.
        await ExecAsync(c,"UPDATE dbo.Users SET PasswordHashNew=@hash WHERE UserID=@id;", Id(id), new SqlParameter("@hash",hash+"_NEW"));
        await FailAsync(c,checkedCredential);
        await RequireStateAsync(c,id,0,false,"stale credential failure was charged");
        await ExecAsync(c,"UPDATE dbo.Users SET PasswordHashNew=@hash WHERE UserID=@id;", Id(id), new SqlParameter("@hash",hash));

        // Binary comparison is required regardless of the SQL database collation.
        await FailAsync(c,checkedCredential with { Hash=hash.ToLowerInvariant() });
        await RequireStateAsync(c,id,0,false,"case-different credential was accepted");
        await FailAsync(c,checkedCredential with { Salt=salt+"_WRONG" });
        await RequireStateAsync(c,id,0,false,"different salt was accepted");
        await FailAsync(c,checkedCredential with { UserId=-1 });
        await RequireStateAsync(c,id,0,false,"wrong account identity was charged");

        // Five independent genuine failures all count, despite changing rowversion.
        await Task.WhenAll(Enumerable.Range(0,5).Select(async _ =>
        {
            await using var other = new SqlConnection(connectionString);
            await other.OpenAsync();
            await FailAsync(other,checkedCredential);
        }));
        await RequireStateAsync(c,id,5,true,"concurrent genuine failures did not accumulate to the threshold");
        var before = await StateAsync(c,id);
        await FailAsync(c,checkedCredential);
        var after = await StateAsync(c,id);
        Require(Equals(before.Rows[0]["LockedUntil"],after.Rows[0]["LockedUntil"]) &&
            Convert.ToInt32(after.Rows[0]["FailedLoginAttempts"])==5,"in-flight failure extended the timed lock");

        // An administrative indefinite lock must remain indefinite.
        await ExecAsync(c,"UPDATE dbo.Users SET IsLocked=1,LockedUntil=NULL,FailedLoginAttempts=2 WHERE UserID=@id;",Id(id));
        await FailAsync(c,checkedCredential);
        await RequireStateAsync(c,id,2,true,"administrative lock was weakened");
        Require((await StateAsync(c,id)).Rows[0]["LockedUntil"]==DBNull.Value,"indefinite lock became timed");

        // Retain the candidate's expired-lock behavior without accepting stale credentials.
        await ExecAsync(c,@"UPDATE dbo.Users SET IsLocked=1,FailedLoginAttempts=5,
LockedUntil=DATEADD(MINUTE,-1,SYSDATETIME()) WHERE UserID=@id;",Id(id));
        await FailAsync(c,checkedCredential);
        await RequireStateAsync(c,id,1,false,"expired lock did not restart counting at one");
        Require((await StateAsync(c,id)).Rows[0]["LockedUntil"]==DBNull.Value,"expired timestamp was retained");

        await ExecAsync(c,"UPDATE dbo.Users SET IsActive=0 WHERE UserID=@id;",Id(id));
        await FailAsync(c,checkedCredential);
        await RequireStateAsync(c,id,1,false,"inactive account accepted a failure write");

        // Verify helper response to SQL's actual conversion; not a WPF save test.
        var precision = await QueryAsync(c,@"SELECT CONVERT(decimal(18,4),2.0001) AS ExactValue,
CONVERT(decimal(18,3),2.0001) AS NarrowValue, CONVERT(nvarchar(50),CONVERT(decimal(18,4),2.0001)) AS TextValue;");
        Require(WaterResultValueContract.StoredValueMatches(2.0001m,precision.Rows[0]["ExactValue"]),"exact numeric storage rejected");
        Require(WaterResultValueContract.StoredValueMatches(2.0001m,precision.Rows[0]["TextValue"]),"exact text storage rejected");
        Require(!WaterResultValueContract.StoredValueMatches(2.0001m,precision.Rows[0]["NarrowValue"]),"narrow SQL conversion was not detected");
        Console.WriteLine("PASS v262 credential-failure accounting and SQL stored-value contracts. WPF saves/lock guards remain separate acceptance tests.");
    }

    private sealed record Credential(int UserId,string Username,string LegacyHash,string Hash,string Salt);
    private static Task FailAsync(SqlConnection c,Credential checkedCredential) =>
        ExecAsync(c,AuthenticationCommitContract.FailureSql,
            new SqlParameter("@VerifiedUserID",SqlDbType.Int){Value=checkedCredential.UserId},
            new SqlParameter("@Username",SqlDbType.NVarChar,256){Value=checkedCredential.Username},
            new SqlParameter("@VerifiedLegacyHash",SqlDbType.NVarChar,-1){Value=checkedCredential.LegacyHash},
            new SqlParameter("@VerifiedHash",SqlDbType.NVarChar,-1){Value=checkedCredential.Hash},
            new SqlParameter("@VerifiedSalt",SqlDbType.NVarChar,-1){Value=checkedCredential.Salt});
    private static SqlParameter Id(int id) => new("@id",SqlDbType.Int){Value=id};
    private static Task<DataTable> StateAsync(SqlConnection c,int id) => QueryAsync(c,
        "SELECT FailedLoginAttempts,IsLocked,LockedUntil FROM dbo.Users WHERE UserID=@id;",Id(id));
    private static async Task RequireStateAsync(SqlConnection c,int id,int failures,bool locked,string message)
    {
        var rows=await StateAsync(c,id);
        Require(rows.Rows.Count==1 && Convert.ToInt32(rows.Rows[0]["FailedLoginAttempts"])==failures &&
            Convert.ToBoolean(rows.Rows[0]["IsLocked"])==locked,message);
    }
    private static void Require(bool condition,string message)
    {
        if (!condition) throw new InvalidOperationException("v262 integration: "+message);
    }
    private static async Task ExecAsync(SqlConnection c,string sql,params SqlParameter[] parameters)
    {
        await using var command=new SqlCommand(sql,c){CommandTimeout=30};
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<DataTable> QueryAsync(SqlConnection c,string sql,params SqlParameter[] parameters)
    {
        await using var command=new SqlCommand(sql,c){CommandTimeout=30};
        command.Parameters.AddRange(parameters);
        await using var reader=await command.ExecuteReaderAsync();
        var rows=new DataTable(); rows.Load(reader); return rows;
    }
}
