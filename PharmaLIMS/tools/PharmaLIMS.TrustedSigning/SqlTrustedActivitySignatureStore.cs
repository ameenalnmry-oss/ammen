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
        if (!ActivitySigningDatabasePolicy.IsAllowedUatDatabase(settings.InitialCatalog))
            throw new UnauthorizedAccessException("Activity signing is restricted to an explicitly isolated UAT database.");
        _connectionString = settings.ConnectionString;
        _databaseName = settings.InitialCatalog;
    }

    public async Task<ActivitySnapshot> ReadSnapshotAsync(ActivitySnapshotRequest request,
        CancellationToken cancellationToken)
    {
        TrustedActivitySigningCoordinator.ValidateSnapshotShape(request);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand("dbo.MicroEquipment_ReadActivitySnapshot", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 10
        };
        command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = request.ActivityId;
        command.Parameters.Add("@Equipment", SqlDbType.Int).Value = request.EquipmentId;
        command.Parameters.Add("@Stage", SqlDbType.NVarChar, 30).Value = request.Stage;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Trusted activity snapshot was not returned.");
        long activityId = reader.GetInt64(0);
        int equipmentId = reader.GetInt32(1);
        string activityStatus = reader.GetString(2);
        if (reader.GetValue(3) is not byte[] version || version.Length != 8)
            throw new InvalidOperationException("Trusted activity snapshot rowversion is invalid.");
        return new ActivitySnapshot(activityId, equipmentId, activityStatus, version, _databaseName);
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
        // The controlled migration exposes this exact stage-transition procedure.
        // Replay is rejected by the procedure's prior-status + ROWVERSION gate, and
        // current signer eligibility is re-checked inside the same SQL transaction.
        await using var command = new SqlCommand("dbo.MicroEquipment_SignActivityStage", connection)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = 20 };
        command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = request.ActivityId;
        command.Parameters.Add("@Version", SqlDbType.Binary, 8).Value = request.ExpectedVersion;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = signer.Username;
        command.Parameters.Add("@Stage", SqlDbType.NVarChar, 30).Value = request.Stage;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = request.Reason.Trim();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
