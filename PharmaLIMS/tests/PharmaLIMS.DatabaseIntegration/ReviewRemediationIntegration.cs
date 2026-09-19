using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Security.Cryptography;

internal static class ReviewRemediationIntegration
{
    // Called only by the harness that creates/drops a GUID-named disposable database.
    // These exercise production SQL contracts and schema, not WPF event handlers.
    internal static async Task VerifyAsync(string connectionString, string projectRoot, string masterConnectionString)
    {
        string database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!database.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Review tests refuse a non-disposable database.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await VerifyAuthenticationAsync(connection);
        await Merge262Integration.VerifyAsync(connectionString);
        await VerifyEvidenceAndVersionsAsync(connection);
        await VerifyLegacyUpgradeAsync(projectRoot, masterConnectionString);
        Console.WriteLine("Review remediation SQL contracts PASS (WPF multi-session acceptance remains separate).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("v260 integration: " + message);
    }
    private static async Task<DataTable> QueryAsync(SqlConnection c, string sql, SqlTransaction? tx = null, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql,c,tx) { CommandTimeout=60 };
        cmd.Parameters.AddRange(parameters);
        await using var reader = await cmd.ExecuteReaderAsync();
        var t = new DataTable(); t.Load(reader); return t;
    }
    private static async Task ExecAsync(SqlConnection c,string sql, SqlTransaction? tx = null, params SqlParameter[] parameters)
    {
        await using var cmd = new SqlCommand(sql,c,tx) { CommandTimeout=120 };
        cmd.Parameters.AddRange(parameters);
        await cmd.ExecuteNonQueryAsync();
    }
    private static SqlParameter Id(int id) => new("@id",SqlDbType.Int){Value=id};
    private static async Task<byte[]> UserVersionAsync(SqlConnection c,int id)
    {
        var t = await QueryAsync(c,"SELECT AuthenticationRowVersion FROM dbo.Users WHERE UserID=@id;",null,Id(id));
        Require(t.Rows.Count==1,"fixture identity missing");
        return (byte[])t.Rows[0][0];
    }
    private static Task<DataTable> CompleteAsync(SqlConnection c,int id,string username,byte[] expected,
        bool login=true,bool upgrade=false,string? hash=null,string? salt=null) =>
        QueryAsync(c,AuthenticationCommitContract.Sql,null,
            new SqlParameter("@UserID",SqlDbType.Int){Value=id},
            new SqlParameter("@Username",SqlDbType.NVarChar,256){Value=username},
            new SqlParameter("@ExpectedVersion",SqlDbType.Binary,8){Value=expected},
            new SqlParameter("@IsLogin",SqlDbType.Bit){Value=login},
            new SqlParameter("@Upgrade",SqlDbType.Bit){Value=upgrade},
            new SqlParameter("@PasswordHashNew",SqlDbType.NVarChar,512){Value=(object?)hash??DBNull.Value},
            new SqlParameter("@PasswordSalt",SqlDbType.NVarChar,256){Value=(object?)salt??DBNull.Value});

    private static async Task VerifyAuthenticationAsync(SqlConnection c)
    {
        string username="INTEGRATION260_"+Guid.NewGuid().ToString("N");
        // These random fixture strings are not valid application password encodings.
        string firstHash="TEST_ONLY_"+Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var inserted=await QueryAsync(c,@"
DECLARE @Ids TABLE(Id int);
INSERT dbo.Users(Username,PasswordHash,PasswordHashNew,PasswordSalt,FullName,Role,IsActive,CanEnterResults,CanAccessEM)
OUTPUT inserted.UserID INTO @Ids
VALUES(@name,N'[MIGRATED]',@hash,N'TEST_ONLY',N'Review disposable fixture',N'QA',1,1,1);
SELECT Id FROM @Ids;",null,
            new SqlParameter("@name",username),new SqlParameter("@hash",firstHash));
        int id=Convert.ToInt32(inserted.Rows[0][0]);

        byte[] before=await UserVersionAsync(c,id);
        var successful=await CompleteAsync(c,id,username,before);
        Require(successful.Rows.Count==1,"current unlocked identity must commit once");
        Require((await CompleteAsync(c,id,username,before)).Rows.Count==0,"used version must not commit twice");

        before=await UserVersionAsync(c,id);
        await ExecAsync(c,"UPDATE dbo.Users SET IsLocked=1,LockedUntil=NULL WHERE UserID=@id;",null,Id(id));
        Require((await CompleteAsync(c,id,username,before)).Rows.Count==0,"new administrative lock must defeat stale success");
        var state=await QueryAsync(c,"SELECT IsLocked,LockedUntil,PasswordHashNew FROM dbo.Users WHERE UserID=@id;",null,Id(id));
        Require(Convert.ToBoolean(state.Rows[0]["IsLocked"]) && state.Rows[0]["LockedUntil"]==DBNull.Value,
            "indefinite administrative lock was altered");
        byte[] lockedVersion=await UserVersionAsync(c,id);
        Require((await CompleteAsync(c,id,username,lockedVersion)).Rows.Count==0,"even current version cannot clear effective lock");

        await ExecAsync(c,"UPDATE dbo.Users SET IsLocked=0,LockedUntil=NULL WHERE UserID=@id;",null,Id(id));
        before=await UserVersionAsync(c,id);
        string newHash="TEST_ONLY_CHANGED_"+Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await ExecAsync(c,"UPDATE dbo.Users SET PasswordHashNew=@hash WHERE UserID=@id;",null,Id(id),new SqlParameter("@hash",newHash));
        Require((await CompleteAsync(c,id,username,before,upgrade:true,hash:firstHash,salt:"OLD_TEST_SALT")).Rows.Count==0,
            "stale verification must not replace a newer credential");
        state=await QueryAsync(c,"SELECT PasswordHashNew FROM dbo.Users WHERE UserID=@id;",null,Id(id));
        Require(Convert.ToString(state.Rows[0][0])==newHash,"new password hash was overwritten");

        before=await UserVersionAsync(c,id);
        await ExecAsync(c,"UPDATE dbo.Users SET CanEnterResults=0 WHERE UserID=@id;",null,Id(id));
        Require((await CompleteAsync(c,id,username,before,login:false)).Rows.Count==0,"permission change must invalidate in-flight signature verification");

        await ExecAsync(c,"UPDATE dbo.Users SET IsActive=0 WHERE UserID=@id;",null,Id(id));
        before=await UserVersionAsync(c,id);
        Require((await CompleteAsync(c,id,username,before)).Rows.Count==0,"inactive identity cannot commit");
        await ExecAsync(c,@"UPDATE dbo.Users SET IsActive=1,IsLocked=1,
LockedUntil=DATEADD(MINUTE,-1,SYSDATETIME()),FailedLoginAttempts=5,LastLogin=NULL WHERE UserID=@id;",null,Id(id));
        before=await UserVersionAsync(c,id);
        var signed=await CompleteAsync(c,id,username,before,login:false);
        Require(signed.Rows.Count==1,"expired lock may clear only on the verified current version");
        state=await QueryAsync(c,"SELECT IsLocked,LockedUntil,FailedLoginAttempts,LastLogin FROM dbo.Users WHERE UserID=@id;",null,Id(id));
        Require(!Convert.ToBoolean(state.Rows[0]["IsLocked"]) && state.Rows[0]["LockedUntil"]==DBNull.Value &&
            Convert.ToInt32(state.Rows[0]["FailedLoginAttempts"])==0 && state.Rows[0]["LastLogin"]==DBNull.Value,
            "signature accounting must clear expired failures without rewriting LastLogin");

        before=await UserVersionAsync(c,id);
        Require((await CompleteAsync(c,id,username,before,upgrade:true,hash:firstHash,salt:"NEW_TEST_SALT")).Rows.Count==1,
            "verified credential upgrade should commit atomically");
        state=await QueryAsync(c,"SELECT PasswordHashNew,PasswordSalt,PasswordHash FROM dbo.Users WHERE UserID=@id;",null,Id(id));
        Require(Convert.ToString(state.Rows[0]["PasswordHashNew"])==firstHash &&
            Convert.ToString(state.Rows[0]["PasswordSalt"])=="NEW_TEST_SALT" &&
            Convert.ToString(state.Rows[0]["PasswordHash"])=="[MIGRATED]","atomic credential upgrade fields disagree");
        Console.WriteLine("PASS F07/F08 guarded authentication SQL race scenarios.");
    }

    private static async Task VerifyEvidenceAndVersionsAsync(SqlConnection c)
    {
        // Use the append-only signed historical fixture made by the existing v194 test.
        var t=await QueryAsync(c,@"
SELECT P.Id,P.EventId,P.ResultRowVersion,EVID.*
FROM dbo.EM_EventPlates P
"+EmLimitEvidenceSql.Joins()+@"
WHERE P.PlateCode=N'V194-LEGACY';");
        Require(t.Rows.Count==1,"expected signed historical EM fixture");
        var r=t.Rows[0];
        Require(Convert.ToInt32(r["EvidenceComplete"])==1 && Convert.ToDecimal(r["ActionLimitSnapshot"])==20m &&
            Convert.ToInt32(r["ReconciliationID"])>0,"shared effective evidence ignored signed reconciliation");

        // Verify the shared query compiles against the final water schema as well.
        var water=await QueryAsync(c,WaterResultSnapshotSql.Build(false),null,new SqlParameter("@sampleId",-1));
        Require(water.Columns.Contains("ResultRowVersion"),"water snapshot must carry concurrency evidence");

        await using var tx=(SqlTransaction)await c.BeginTransactionAsync();
        try
        {
            var locked=await QueryAsync(c,@"
SELECT P.Id,EVID.EvidenceComplete FROM dbo.EM_EventPlates P WITH(UPDLOCK,HOLDLOCK)
"+EmLimitEvidenceSql.Joins(true)+@" WHERE P.Id=@id;",tx,Id(Convert.ToInt32(r["Id"])));
            Require(locked.Rows.Count==1 && Convert.ToInt32(locked.Rows[0]["EvidenceComplete"])==1,
                "locked shared evidence query differs from read-only query");

            int id=Convert.ToInt32(r["Id"]);
            byte[] original=(byte[])r["ResultRowVersion"];
            await ExecAsync(c,"UPDATE dbo.EM_EventPlates SET ColoniesObserved=ColoniesObserved WHERE Id=@id;",tx,Id(id));
            var stale=await QueryAsync(c,@"
DECLARE @Affected TABLE(Id int);
UPDATE dbo.EM_EventPlates SET ColoniesObserved=N'STALE TEST MUST NOT WRITE'
OUTPUT inserted.Id INTO @Affected
WHERE Id=@id AND ResultRowVersion=@version;
SELECT Id FROM @Affected;",tx,Id(id),new SqlParameter("@version",SqlDbType.Binary,8){Value=original});
            Require(stale.Rows.Count==0,"stale plate rowversion update was accepted");

            // Storage/type probe only; not a valid settle-plate measurement. Rolled back.
            await ExecAsync(c,"UPDATE dbo.EM_EventPlates SET ResultCFU=@value WHERE Id=@id;",tx,Id(id),
                new SqlParameter("@value",SqlDbType.Decimal){Precision=28,Scale=12,Value=1.25m});
            var stored=await QueryAsync(c,"SELECT ResultCFU FROM dbo.EM_EventPlates WHERE Id=@id;",tx,Id(id));
            Require(Convert.ToDecimal(stored.Rows[0][0])==1.25m,"ResultCFU lost fractional precision");
        }
        finally { await tx.RollbackAsync(); }
        Console.WriteLine("PASS F02/F03/F09 SQL precision, stale rowversion and signed evidence contracts.");
    }

    private static async Task VerifyLegacyUpgradeAsync(string projectRoot,string masterConnectionString)
    {
        string migration=await File.ReadAllTextAsync(Path.Combine(projectRoot,"Database","Migrations",
            "20260910_000_Review_Result_And_Authentication_Concurrency.sql"));
        string name="PharmaLIMS_Integration_260_"+Guid.NewGuid().ToString("N")[..12];
        var masterBuilder=new SqlConnectionStringBuilder(masterConnectionString){InitialCatalog="master"};
        await using var master=new SqlConnection(masterBuilder.ConnectionString);
        await master.OpenAsync();
        try
        {
            await ExecAsync(master,$"CREATE DATABASE [{name}];");
            var builder=new SqlConnectionStringBuilder(masterConnectionString){InitialCatalog=name};
            await using (var c=new SqlConnection(builder.ConnectionString))
            {
                await c.OpenAsync();
                // Minimal populated legacy schema isolates this additive migration.
                // The parent harness separately validates the complete baseline/chain.
                await ExecAsync(c,@"
CREATE TABLE dbo.Users(UserID int PRIMARY KEY,Username nvarchar(100));
CREATE TABLE dbo.EM_Events(Id int PRIMARY KEY,FinalResult nvarchar(50));
CREATE TABLE dbo.EM_EventPlates(Id int PRIMARY KEY,ResultCFU int NULL,Status nvarchar(50));
CREATE TABLE dbo.SampleTests(SampleTestID int PRIMARY KEY,ResultValue decimal(18,4) NULL);
INSERT dbo.Users VALUES(1,N'LEGACY_TEST');
INSERT dbo.EM_Events VALUES(1,N'OOS');
INSERT dbo.EM_EventPlates VALUES(1,2147483647,N'OOS'),(2,NULL,N'Pending');
INSERT dbo.SampleTests VALUES(1,1.2345);");
                await ExecAsync(c,migration);
                var before=await QueryAsync(c,"SELECT * FROM dbo.EM_EventPlates ORDER BY Id;");
                Require(Convert.ToDecimal(before.Rows[0]["ResultCFU"])==2147483647m &&
                    Convert.ToString(before.Rows[0]["Status"])=="OOS" &&
                    before.Rows[0]["ResultCalculationVersion"]==DBNull.Value &&
                    before.Rows[1]["ResultCFU"]==DBNull.Value,"migration rewrote historical evidence");
                await ExecAsync(c,migration);
                var after=await QueryAsync(c,"SELECT * FROM dbo.EM_EventPlates ORDER BY Id;");
                for(int i=0;i<before.Rows.Count;i++)
                    foreach(DataColumn column in before.Columns)
                        Require(ResultSnapshotGuard.Equivalent(before.Rows[i][column.ColumnName],after.Rows[i][column.ColumnName]),
                            "idempotent rerun changed existing data/version");

                await ExecAsync(c,@"ALTER TABLE dbo.EM_EventPlates ALTER COLUMN ResultCFU decimal(29,13) NULL;
UPDATE dbo.EM_EventPlates SET ResultCFU=1.0000000000001 WHERE Id=1;");
                bool lossBlocked=false;
                try { await ExecAsync(c,migration); }
                catch(SqlException ex) when(ex.Number==54907) { lossBlocked=true; }
                Require(lossBlocked,"migration must reject a lossy numeric conversion");
                var retained=await QueryAsync(c,"SELECT ResultCFU FROM dbo.EM_EventPlates WHERE Id=1;");
                Require(Convert.ToDecimal(retained.Rows[0][0])==1.0000000000001m,"failed migration damaged prior precision");
            }
        }
        finally
        {
            await ExecAsync(master,$@"IF DB_ID(N'{name}') IS NOT NULL BEGIN
ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [{name}]; END;");
        }
        Console.WriteLine("PASS additive v260 migration: populated legacy values, idempotence, lossy-conversion refusal.");
    }
}
