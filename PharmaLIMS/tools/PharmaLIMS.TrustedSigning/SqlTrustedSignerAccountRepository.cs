using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace PharmaLIMS.TrustedSigning;

/// <summary>
/// Runs ONLY inside the independent signing host with its private SQL credential.
/// Never instantiate with the shared WPF connection string.
/// The connection must use encryption and least-privilege grants provisioned by QA/DBA.
/// </summary>
public sealed class SqlTrustedSignerAccountRepository : ITrustedSignerAccountRepository
{
    private readonly string _trustedConnectionString;

    public SqlTrustedSignerAccountRepository(string trustedConnectionString)
    {
        if (string.IsNullOrWhiteSpace(trustedConnectionString))
            throw new ArgumentException("Trusted SQL connection is required.", nameof(trustedConnectionString));
        var builder = new SqlConnectionStringBuilder(trustedConnectionString);
        if ((builder.Encrypt != SqlConnectionEncryptOption.Mandatory &&
             builder.Encrypt != SqlConnectionEncryptOption.Strict) || builder.TrustServerCertificate)
            throw new ArgumentException("EQ-001 requires encrypted, server-validated SQL transport.");
        _trustedConnectionString = builder.ConnectionString;
    }

    private SqlConnection Connect() => new(_trustedConnectionString);

    public async Task<TrustedSignerAccount?> LoadByUsernameAsync(
        string username, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new UnauthorizedAccessException("Trusted signer identity is missing.");
        const string query = @"
SELECT UserID,Username,ISNULL(PasswordHashNew,N''),ISNULL(PasswordSalt,N''),
       CONVERT(bit,ISNULL(IsActive,1)),
       CONVERT(bit,CASE WHEN ISNULL(IsLocked,0)=1
           AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()) THEN 1 ELSE 0 END),
       CONVERT(bit,ISNULL(MustChangePassword,0)),
       CONVERT(bit,ISNULL(CanManageSettings,0)),
       AuthenticationRowVersion,ISNULL(PasswordHash,N'')
FROM dbo.Users
WHERE Username=@Username;";
        await using var connection = Connect();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 10 };
        command.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = username;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var user = new TrustedSignerAccount(
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6),
            reader.GetBoolean(7), reader.GetInt32(0), (byte[])reader[8],
            reader.GetString(9));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Duplicate EQ-001 signer identity.");
        return user;
    }

    public async Task RecordFailedAttemptAsync(
        TrustedSignerAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        // Match the existing PharmaLIMS AuthenticationCommitContract.FailureSql:
        // count failed attempts only when the exact checked credentials are still current;
        // five consecutive failures lock the account for 15 minutes.
        const string query = @"
UPDATE dbo.Users SET
 FailedLoginAttempts=CASE
   WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 1
   ELSE ISNULL(FailedLoginAttempts,0)+1 END,
 IsLocked=CASE
   WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN 0
   WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN 1 ELSE 0 END,
 LockedUntil=CASE
   WHEN ISNULL(IsLocked,0)=1 AND LockedUntil<=SYSDATETIME() THEN NULL
   WHEN ISNULL(FailedLoginAttempts,0)+1>=5 THEN DATEADD(MINUTE,15,SYSDATETIME())
   ELSE LockedUntil END
WHERE UserID=@UserID AND Username=@Username AND ISNULL(IsActive,1)=1
 AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
 AND CONVERT(varbinary(max),ISNULL(PasswordHash,N''))=CONVERT(varbinary(max),@Legacy)
 AND CONVERT(varbinary(max),ISNULL(PasswordHashNew,N''))=CONVERT(varbinary(max),@Hash)
 AND CONVERT(varbinary(max),ISNULL(PasswordSalt,N''))=CONVERT(varbinary(max),@Salt);";
        await using var connection = Connect();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 10 };
        AddAccountParameters(command, account);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmSuccessIfStillAuthorizedAsync(
        TrustedSignerAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.AuthenticationRowVersion?.Length != 8)
            return false;

        // Compare-and-swap the exact account state checked before accepting the
        // credential. Any concurrent account, password or permission edit rejects.
        const string query = @"
UPDATE dbo.Users
SET FailedLoginAttempts=0,IsLocked=0,LockedUntil=NULL
OUTPUT INSERTED.AuthenticationRowVersion
WHERE UserID=@UserID AND Username=@Username AND AuthenticationRowVersion=@Version
 AND ISNULL(IsActive,1)=1 AND ISNULL(MustChangePassword,0)=0
 AND ISNULL(CanManageSettings,0)=1
 AND NOT (ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil>SYSDATETIME()))
 AND CONVERT(varbinary(max),ISNULL(PasswordHashNew,N''))=CONVERT(varbinary(max),@Hash)
 AND CONVERT(varbinary(max),ISNULL(PasswordSalt,N''))=CONVERT(varbinary(max),@Salt);";
        await using var connection = Connect();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(query, connection) { CommandTimeout = 10 };
        AddAccountParameters(command, account);
        command.Parameters.Add("@Version", SqlDbType.Binary, 8).Value = account.AuthenticationRowVersion;
        object? version = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return version is byte[] bytes && bytes.Length == 8;
    }

    private static void AddAccountParameters(SqlCommand command, TrustedSignerAccount account)
    {
        command.Parameters.Add("@UserID", SqlDbType.Int).Value = account.UserId;
        command.Parameters.Add("@Username", SqlDbType.NVarChar, 256).Value = account.Username;
        command.Parameters.Add("@Hash", SqlDbType.NVarChar, -1).Value = account.PasswordHashNew;
        command.Parameters.Add("@Salt", SqlDbType.NVarChar, -1).Value = account.PasswordSalt;
        command.Parameters.Add("@Legacy", SqlDbType.NVarChar, -1).Value = account.LegacyPasswordHash;
    }
}
