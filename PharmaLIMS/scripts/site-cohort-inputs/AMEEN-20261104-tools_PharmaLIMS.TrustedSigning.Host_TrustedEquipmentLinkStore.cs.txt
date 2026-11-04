using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;

internal sealed class TrustedEquipmentLinkStore(string connectionString)
{
    internal async Task<EquipmentLinkReceipt> ExecuteAsync(TrustedApplicationRequest envelope,
        byte[] authenticationVersion, string callerSid, CancellationToken cancellationToken)
    {
        EquipmentDraftContract.ValidateTarget(connectionString);
        if (envelope.Action != "EquipmentLink" || envelope.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(envelope.Username) || string.IsNullOrWhiteSpace(callerSid) ||
            callerSid.Length > 184 || authenticationVersion is null || authenticationVersion.Length != 8 ||
            string.IsNullOrWhiteSpace(envelope.Reason) || envelope.Reason.Length > 500 ||
            string.IsNullOrWhiteSpace(envelope.Meaning) || envelope.Meaning.Length > 200)
            throw new ArgumentException("Invalid authenticated equipment-link envelope.");
        var payload = EquipmentLinkContract.Parse(envelope.UserJson);
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.IntegratedSecurity ||
            settings.Encrypt == SqlConnectionEncryptOption.Optional || settings.TrustServerCertificate)
            throw new UnauthorizedAccessException("Equipment link requires verified Windows SQL identity and TLS.");
        if (settings.ConnectTimeout is <= 0 or > 15) settings.ConnectTimeout = 15;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        using var cmd = new SqlCommand("dbo.EQ001_TrustedEquipmentLink", connection, tx)
        {
            CommandType = CommandType.StoredProcedure, CommandTimeout = 20
        };
        cmd.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = envelope.RequestId;
        cmd.Parameters.Add("@ActivityId", SqlDbType.BigInt).Value = payload.ActivityId;
        cmd.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = payload.Module;
        cmd.Parameters.Add("@ParentIds", SqlDbType.NVarChar, -1).Value =
            System.Text.Json.JsonSerializer.Serialize(payload.ParentIds);
        cmd.Parameters.Add("@ResultId", SqlDbType.Int).Value =
            payload.ResultId.HasValue ? payload.ResultId.Value : DBNull.Value;
        cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = envelope.UserJson;
        cmd.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = envelope.Username;
        cmd.Parameters.Add("@AuthenticationVersion", SqlDbType.Binary, 8).Value = authenticationVersion;
        cmd.Parameters.Add("@CallerSid", SqlDbType.NVarChar, 184).Value = callerSid;
        cmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = envelope.Reason.Trim();
        cmd.Parameters.Add("@Meaning", SqlDbType.NVarChar, 200).Value = envelope.Meaning.Trim();
        long activity;
        int links;
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Missing trusted equipment-link receipt.");
            activity = reader.GetInt64(reader.GetOrdinal("ActivityId"));
            links = reader.GetInt32(reader.GetOrdinal("LinkCount"));
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Multiple trusted equipment-link receipts.");
        }
        if (activity != payload.ActivityId || links != payload.ParentIds.Length)
            throw new InvalidOperationException("Invalid trusted equipment-link receipt contents.");
        await tx.CommitAsync(cancellationToken);
        return new EquipmentLinkReceipt(envelope.RequestId, activity, links, "Committed");
    }
}
