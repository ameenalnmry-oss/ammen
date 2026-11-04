using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace PharmaLIMS.TrustedSigning;

/// <summary>
/// Trusted-host-only SQL persistence adapter. Does not issue direct INSERT/UPDATE.
/// The controlled dbo.EQ001_CommitEquipmentSignature procedure must be reviewed,
/// UAT-qualified and granted only to the trusted service SQL principal.
/// A missing procedure or permission always fails closed.
/// </summary>
public sealed class SqlTrustedEquipmentSignatureStore : ITrustedEquipmentSignatureStore
{
    private readonly string _trustedConnectionString;

    public SqlTrustedEquipmentSignatureStore(string trustedConnectionString)
    {
        if (string.IsNullOrWhiteSpace(trustedConnectionString))
            throw new ArgumentException("Trusted SQL connection is required.", nameof(trustedConnectionString));
        var builder = new SqlConnectionStringBuilder(trustedConnectionString);
        if ((builder.Encrypt != SqlConnectionEncryptOption.Mandatory &&
             builder.Encrypt != SqlConnectionEncryptOption.Strict) ||
            builder.TrustServerCertificate)
            throw new ArgumentException("EQ-001 requires server-validated TLS encryption.");
        _trustedConnectionString = builder.ConnectionString;
    }

    public async Task CommitOnceAsync(
        EquipmentSigningRequest request,
        VerifiedSigner signer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signer);
        if (string.IsNullOrWhiteSpace(signer.Username) || !signer.IsActive ||
            signer.IsLocked || signer.MustChangePassword || !signer.CanManageSettings)
            throw new UnauthorizedAccessException("EQ-001 signer authorization is missing.");

        await using var connection = new SqlConnection(_trustedConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand(
            "dbo.EQ001_CommitEquipmentSignature", connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 20
        };

        command.Parameters.Add("@TrustedRequestId", SqlDbType.UniqueIdentifier).Value = request.RequestId;
        command.Parameters.Add("@ActionType", SqlDbType.NVarChar, 50).Value = request.Action;
        command.Parameters.Add("@EquipmentID", SqlDbType.Int).Value =
            request.EquipmentId.HasValue ? request.EquipmentId.Value : DBNull.Value;
        command.Parameters.Add("@EquipmentCode", SqlDbType.NVarChar, 50).Value = request.EquipmentCode;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = request.Reason;
        command.Parameters.Add("@RequestedStateJson", SqlDbType.NVarChar, -1).Value =
            request.RequestedEquipmentStateJson;
        command.Parameters.Add("@ExpectedRowVersion", SqlDbType.Binary, 8).Value =
            request.ExpectedRowVersion is { Length: 8 }
                ? request.ExpectedRowVersion
                : DBNull.Value;
        command.Parameters.Add("@VerifiedSigner", SqlDbType.NVarChar, 100).Value = signer.Username;
        command.Parameters.Add("@SourceWorkstation", SqlDbType.NVarChar, 100).Value = Environment.MachineName;

        // Security and audit invariants are enforced in the single SQL transaction.
        // This service never falls back to WPF's LabResources.SaveEquipment path.
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
