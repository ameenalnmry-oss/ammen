using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;

// Executes the exact production password-update and audit SQL. This is not a
// substitute for the real first-sign-in/password-verification WPF acceptance.
internal static class PasswordChangeAuditIntegration
{
    internal static async Task VerifyAsync(string connectionString)
    {
        string database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        if (!database.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Password audit regression refuses a non-disposable database.");

        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        string username = "PW_AUDIT_" + Guid.NewGuid().ToString("N");
        int id = Convert.ToInt32(await ScalarAsync(c, @"
DECLARE @Id TABLE (Id int);
INSERT dbo.Users(Username,PasswordHash,PasswordHashNew,PasswordSalt,FullName,Role,IsActive,MustChangePassword)
OUTPUT inserted.UserID INTO @Id
VALUES(@name,N'[MIGRATED]',N'TEST_ONLY_OLD_HASH',N'TEST_ONLY_OLD_SALT',N'Password regression',N'Supervisor',1,1);
SELECT Id FROM @Id;", new SqlParameter("@name", username)));

        byte[] initialVersion = await VersionAsync(c, id);
        Require(await ChangeAsync(c, id, username, initialVersion), "first change was rejected");
        Require(Convert.ToInt32(await ScalarAsync(c,
            "SELECT MustChangePassword FROM dbo.Users WHERE UserID=@id;", Id(id))) == 0,
            "mandatory-change flag was not committed");
        Require(Convert.ToInt32(await ScalarAsync(c, @"
SELECT COUNT(*) FROM dbo.AuditTrail
WHERE TableName=N'Users' AND RecordID=@id AND Action=N'Password Changed'
 AND ChangedBy=@name AND PerformedBy=@name AND ModuleName=N'Authentication'
 AND OldValue=N'Credential retained securely; value not exposed'
 AND NewValue=N'Personal credential replaced securely; value not exposed'
 AND CreatedUtc IS NOT NULL AND UtcRecordedAt IS NOT NULL;",
            Id(id), new SqlParameter("@name", username))) == 1, "audit actor/metadata was not bound to the changed account");

        // Ordinary self-service changes still work after first sign-in.
        Require(await ChangeAsync(c, id, username, await VersionAsync(c, id)), "ordinary change was rejected");
        await RequireRejectedUnchangedAsync(c, id, username, initialVersion, "stale rowversion");
        await RequireRejectedUnchangedAsync(c, id, username + "_OTHER", await VersionAsync(c, id), "wrong username");
        await ExecAsync(c, "UPDATE dbo.Users SET IsActive=0 WHERE UserID=@id;", Id(id));
        await RequireRejectedUnchangedAsync(c, id, username, await VersionAsync(c, id), "inactive account");
        await ExecAsync(c, "UPDATE dbo.Users SET IsActive=1,IsLocked=1,LockedUntil=NULL WHERE UserID=@id;", Id(id));
        await RequireRejectedUnchangedAsync(c, id, username, await VersionAsync(c, id), "locked account");
        await ExecAsync(c, "UPDATE dbo.Users SET IsLocked=0,LockedUntil=NULL WHERE UserID=@id;", Id(id));

        // An audit that cannot bind the just-updated row must roll back the credential.
        string before = await SnapshotAsync(c, id);
        try
        {
            await ChangeAsync(c, id, username, await VersionAsync(c, id), invalidAuditVersion: true);
            throw new InvalidOperationException("Mismatched audit rowversion was accepted.");
        }
        catch (SqlException ex) when (ex.Number == 55221) { }
        Require(before == await SnapshotAsync(c, id), "audit identity failure did not roll back the credential and audit");

        // Simulate a real audit INSERT failure without changing any controlled
        // trigger. This additional trigger exists only in the disposable database.
        await ExecAsync(c, @"
CREATE TRIGGER dbo.TRG_PasswordAuditFailure_Test ON dbo.AuditTrail AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted WHERE Action=N'Password Changed')
  THROW 57091, 'Deliberate disposable password-audit failure.', 1;
END;");
        try
        {
            before = await SnapshotAsync(c, id);
            try
            {
                await ChangeAsync(c, id, username, await VersionAsync(c, id));
                throw new InvalidOperationException("Audit INSERT failure was ignored.");
            }
            catch (SqlException ex) when (ex.Number == 57091) { }
            Require(before == await SnapshotAsync(c, id), "audit INSERT failure did not roll back the credential and audit");
        }
        finally
        {
            await ExecAsync(c, "DROP TRIGGER IF EXISTS dbo.TRG_PasswordAuditFailure_Test;");
        }
        Console.WriteLine("PASS password-change SQL: first/ordinary change, exact audit actor, stale identity/version, inactive/locked rejection, audit binding and INSERT-failure rollback. WPF first-login acceptance remains separate.");
    }

    private static async Task<bool> ChangeAsync(SqlConnection c, int id, string name, byte[] version, bool invalidAuditVersion = false)
    {
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();
        try
        {
            await using var update = new SqlCommand(AuthenticationCommitContract.PasswordChangeSql, c, tx);
            update.Parameters.Add("@UserID", SqlDbType.Int).Value = id;
            update.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = name;
            update.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = version;
            update.Parameters.Add("@PasswordHashNew", SqlDbType.NVarChar, 512).Value = "TEST_ONLY_" + Guid.NewGuid().ToString("N");
            update.Parameters.Add("@PasswordSalt", SqlDbType.NVarChar, 256).Value = "TEST_ONLY_" + Guid.NewGuid().ToString("N");
            byte[]? committed = null;
            await using (var reader = await update.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync()) committed = (byte[])reader[0];
                Require(!await reader.ReadAsync(), "duplicate changed account");
            }
            if (committed == null)
            {
                await tx.RollbackAsync();
                return false;
            }
            await using var audit = new SqlCommand(AuthenticationCommitContract.PasswordChangeAuditSql, c, tx);
            audit.Parameters.Add("@UserID", SqlDbType.Int).Value = id;
            audit.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = name;
            audit.Parameters.Add("@CommittedVersion", SqlDbType.Binary, 8).Value = invalidAuditVersion ? version : committed;
            audit.Parameters.Add("@Workstation", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
            await audit.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return true;
        }
        catch
        {
            if (tx.Connection != null) await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task RequireRejectedUnchangedAsync(SqlConnection c, int id, string name, byte[] version, string scenario)
    {
        string before = await SnapshotAsync(c, id);
        Require(!await ChangeAsync(c, id, name, version), scenario + " was accepted");
        Require(before == await SnapshotAsync(c, id), scenario + " changed credentials or audit evidence");
    }

    private static async Task<string> SnapshotAsync(SqlConnection c, int id) =>
        Convert.ToString(await ScalarAsync(c, @"
SELECT u.UserID,u.Username,u.PasswordHash,u.PasswordHashNew,u.PasswordSalt,
 u.MustChangePassword,u.PasswordChangedAt,u.IsActive,u.IsLocked,u.LockedUntil,
 CONVERT(varchar(18),u.AuthenticationRowVersion,1) AS RowVersion,
 (SELECT COUNT(*) FROM dbo.AuditTrail a WHERE a.TableName=N'Users' AND a.RecordID=u.UserID) AS AuditRows
FROM dbo.Users u WHERE u.UserID=@id FOR JSON PATH;", Id(id))) ?? string.Empty;

    private static async Task<byte[]> VersionAsync(SqlConnection c, int id) =>
        (byte[])(await ScalarAsync(c, "SELECT AuthenticationRowVersion FROM dbo.Users WHERE UserID=@id;", Id(id)))!;
    private static SqlParameter Id(int id) => new("@id", SqlDbType.Int) { Value = id };
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Password-change audit regression: " + message);
    }
    private static async Task<object?> ScalarAsync(SqlConnection c, string sql, params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, c) { CommandTimeout = 30 };
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync();
    }
    private static async Task ExecAsync(SqlConnection c, string sql, params SqlParameter[] parameters)
    {
        await using var command = new SqlCommand(sql, c) { CommandTimeout = 30 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}
