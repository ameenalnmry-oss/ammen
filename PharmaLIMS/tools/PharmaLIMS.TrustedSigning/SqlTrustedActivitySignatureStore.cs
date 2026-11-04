using Microsoft.Data.SqlClient;
using System.Data;

namespace PharmaLIMS.TrustedSigning;

public sealed class SqlTrustedActivitySignatureStore : ITrustedActivitySignatureStore
{
    private readonly string _connectionString;
    private readonly string _databaseName;

    public SqlTrustedActivitySignatureStore(string connectionString)
    {
        var settings = new SqlConnectionStringBuilder(connectionString);
        if ((settings.Encrypt != SqlConnectionEncryptOption.Mandatory && settings.Encrypt != SqlConnectionEncryptOption.Strict) ||
            settings.TrustServerCertificate || !settings.IntegratedSecurity || settings.AttachDBFilename.Length != 0)
            throw new ArgumentException("Activity signing requires Windows SQL authentication and server-validated TLS.");
        // Activity qualification is UAT-only; there is no production activation switch.
        if (!(settings.InitialCatalog.StartsWith("EQ001_UAT_", StringComparison.Ordinal) ||
              settings.InitialCatalog.StartsWith("PharmaLIMS_EQ001_UAT_", StringComparison.Ordinal)))
            throw new UnauthorizedAccessException("Activity signing is restricted to explicitly isolated EQ001 UAT.");
        _connectionString = settings.ConnectionString;
        _databaseName = settings.InitialCatalog;
    }

    public async Task CommitOnceAsync(ActivitySigningRequest request, VerifiedActivitySigner signer,
        CancellationToken cancellationToken)
    {
        TrustedActivitySigningCoordinator.ValidateShape(request, DateTimeOffset.UtcNow);
        if (!request.DatabaseName.Equals(_databaseName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(signer.Username) || signer.AuthenticationRowVersion?.Length != 8 ||
            !signer.IsActive || signer.IsLocked || signer.MustChangePassword ||
            (request.Stage == "Reviewed" ? !signer.CanReviewResults : !signer.CanApproveResults))
            throw new UnauthorizedAccessException("Activity signer or configured database does not match.");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("dbo.EQ001_CommitActivitySignature", connection)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = 20 };
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = request.RequestId;
        command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = request.ActivityId;
        command.Parameters.Add("@Version", SqlDbType.Binary, 8).Value = request.ExpectedVersion;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = signer.Username;
        command.Parameters.Add("@Stage", SqlDbType.NVarChar, 30).Value = request.Stage;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = request.Reason.Trim();
        command.Parameters.Add("@AuthenticationVersion", SqlDbType.Binary, 8).Value = signer.AuthenticationRowVersion;
        command.Parameters.Add("@IssuedAtUtc", SqlDbType.DateTimeOffset).Value = request.IssuedAtUtc;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
