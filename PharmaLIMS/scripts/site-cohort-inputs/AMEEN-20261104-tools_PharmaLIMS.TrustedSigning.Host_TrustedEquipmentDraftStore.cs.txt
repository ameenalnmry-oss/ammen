using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Text.Json;

internal sealed class TrustedEquipmentDraftStore(string connectionString)
{
    internal async Task<EquipmentDraftReceipt> ExecuteAsync(
        TrustedApplicationRequest envelope, byte[] authenticationVersion,
        string callerSid, CancellationToken cancellationToken)
    {
        EquipmentDraftContract.ValidateTarget(connectionString);
        var payload = EquipmentDraftContract.Parse(envelope.UserJson);
        if (envelope.Action != "EquipmentDraft" ||
            envelope.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(envelope.Username) ||
            string.IsNullOrWhiteSpace(callerSid) || callerSid.Length > 184 ||
            authenticationVersion?.Length != 8 ||
            string.IsNullOrWhiteSpace(envelope.Reason) ||
            string.IsNullOrWhiteSpace(envelope.Meaning) ||
            envelope.Reason.Length > 500 || envelope.Meaning.Length > 200)
            throw new ArgumentException("Invalid authenticated equipment draft envelope.");

        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.IntegratedSecurity ||
            settings.Encrypt == SqlConnectionEncryptOption.Optional ||
            settings.TrustServerCertificate)
            throw new UnauthorizedAccessException("Equipment draft service requires verified Windows SQL identity and TLS.");
        if (settings.ConnectTimeout <= 0 || settings.ConnectTimeout > 15)
            settings.ConnectTimeout = 15;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        using var command = new SqlCommand("dbo.EQ001_TrustedEquipmentDraft", connection, transaction)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 15
        };
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = envelope.RequestId;
        command.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = payload.EquipmentId;
        command.Parameters.Add("@ActivityType", SqlDbType.NVarChar, 60).Value = payload.ActivityType;
        command.Parameters.Add("@MethodReference", SqlDbType.NVarChar, 200).Value = payload.MethodReference;
        command.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = envelope.UserJson;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = envelope.Username;
        command.Parameters.Add("@AuthenticationVersion", SqlDbType.Binary, 8).Value = authenticationVersion;
        command.Parameters.Add("@CallerSid", SqlDbType.NVarChar, 184).Value = callerSid;
        command.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = envelope.Reason.Trim();
        command.Parameters.Add("@Meaning", SqlDbType.NVarChar, 200).Value = envelope.Meaning.Trim();
        long activityId;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Trusted equipment draft returned no receipt.");
            activityId = reader.GetInt64(reader.GetOrdinal("ActivityID"));
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Trusted equipment draft returned multiple receipts.");
        }
        if (activityId <= 0) throw new InvalidOperationException("Invalid equipment draft receipt.");
        await transaction.CommitAsync(cancellationToken);
        return new EquipmentDraftReceipt(envelope.RequestId, activityId, "Committed");
    }
}
