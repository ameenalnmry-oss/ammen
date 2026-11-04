using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;

internal sealed class TrustedEquipmentConfirmStore(string connectionString)
{
    internal async Task<EquipmentConfirmReceipt> ExecuteAsync(TrustedApplicationRequest envelope,
        byte[] authenticationVersion, string callerSid, CancellationToken cancellationToken)
    {
        EquipmentDraftContract.ValidateTarget(connectionString);
        if (envelope.Action != "EquipmentConfirm" || envelope.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(envelope.Username) || string.IsNullOrWhiteSpace(callerSid) ||
            callerSid.Length > 184 || authenticationVersion is null || authenticationVersion.Length != 8 ||
            string.IsNullOrWhiteSpace(envelope.Reason) || envelope.Reason.Length > 500 ||
            string.IsNullOrWhiteSpace(envelope.Meaning) || envelope.Meaning.Length > 200)
            throw new ArgumentException("Invalid authenticated equipment confirmation envelope.");
        var payload = EquipmentConfirmContract.Parse(envelope.UserJson);
        var settings = new SqlConnectionStringBuilder(connectionString);
        if (!settings.IntegratedSecurity ||
            settings.Encrypt == SqlConnectionEncryptOption.Optional || settings.TrustServerCertificate)
            throw new UnauthorizedAccessException("Equipment confirm requires verified Windows SQL identity and TLS.");
        if (settings.ConnectTimeout is <= 0 or > 15) settings.ConnectTimeout = 15;
        await using var connection = new SqlConnection(settings.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        using var cmd = new SqlCommand("dbo.EQ001_TrustedEquipmentConfirm", connection, tx)
        {
            CommandType = CommandType.StoredProcedure, CommandTimeout = 20
        };
        cmd.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = envelope.RequestId;
        cmd.Parameters.Add("@ActivityId", SqlDbType.BigInt).Value = payload.ActivityId;
        cmd.Parameters.Add("@ExpectedVersion", SqlDbType.Binary, 8).Value = payload.ExpectedVersion;
        cmd.Parameters.Add("@Started", SqlDbType.DateTimeOffset).Value = payload.Started;
        cmd.Parameters.Add("@Ended", SqlDbType.DateTimeOffset).Value = payload.Ended;
        cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = envelope.UserJson;
        cmd.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = envelope.Username;
        cmd.Parameters.Add("@AuthenticationVersion", SqlDbType.Binary, 8).Value = authenticationVersion;
        cmd.Parameters.Add("@CallerSid", SqlDbType.NVarChar, 184).Value = callerSid;
        cmd.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = envelope.Reason.Trim();
        cmd.Parameters.Add("@Meaning", SqlDbType.NVarChar, 200).Value = envelope.Meaning.Trim();
        long activity;
        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Missing trusted equipment confirmation receipt.");
            activity = reader.GetInt64(reader.GetOrdinal("ActivityId"));
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Multiple trusted equipment confirmation receipts.");
        }
        if (activity != payload.ActivityId)
            throw new InvalidOperationException("Invalid equipment confirmation receipt contents.");
        await tx.CommitAsync(cancellationToken);
        return new EquipmentConfirmReceipt(envelope.RequestId, activity, "Submitted");
    }
}
