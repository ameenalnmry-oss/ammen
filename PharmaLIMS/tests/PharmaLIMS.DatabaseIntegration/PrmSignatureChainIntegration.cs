using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;

internal static class PrmSignatureChainIntegration
{
    public static async Task VerifyAsync(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        using var seed = new SqlCommand(@"
INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,SampleStatus,CreatedBy)
VALUES(N'PRM-SIGNATURE-CYCLE-001',N'Primary Packaging',N'Approved',N'integration');
SELECT CAST(SCOPE_IDENTITY() AS int);", connection);
        int sampleId = Convert.ToInt32(await seed.ExecuteScalarAsync());

        using (SqlTransaction transaction = connection.BeginTransaction())
        {
            foreach (string action in new[] { "Result Entry", "Review", "Approval", "Certificate Issuance" })
                await AddSignatureAsync(connection, transaction, sampleId, action);
            DataTable rows = await ReadAsync(connection, transaction, sampleId);
            if (!PrmCertificateSignatureChain.IsCompleteForLatestResultCycle(rows, sampleId, "Certificate Issuance"))
                throw new InvalidOperationException("The persisted complete PRM signature cycle was rejected.");
            transaction.Commit();
        }

        // Rehearse drift in an Approved record without touching an operational database.
        // The issue signature is visible in the same transaction, but old approval must fail.
        using (SqlTransaction transaction = connection.BeginTransaction())
        {
            await AddSignatureAsync(connection, transaction, sampleId, "Result Entry");
            await AddSignatureAsync(connection, transaction, sampleId, "Certificate Reissue");
            DataTable rows = await ReadAsync(connection, transaction, sampleId);
            if (PrmCertificateSignatureChain.IsCompleteForLatestResultCycle(rows, sampleId, "Certificate Reissue"))
                throw new InvalidOperationException("Old PRM approval authorized newer result evidence.");
            transaction.Rollback();
        }

        DataTable retained = await ReadAsync(connection, null, sampleId);
        if (retained.Rows.Count != 4 ||
            !PrmCertificateSignatureChain.IsCompleteForLatestResultCycle(retained, sampleId, "Certificate Issuance"))
            throw new InvalidOperationException("Rejected PRM signature rehearsal did not roll back cleanly.");

        using (SqlTransaction transaction = connection.BeginTransaction())
        {
            await AddSignatureAsync(connection, transaction, sampleId, "Certificate Reissue");
            DataTable rows = await ReadAsync(connection, transaction, sampleId);
            if (!PrmCertificateSignatureChain.IsCompleteForLatestResultCycle(rows, sampleId, "Certificate Reissue"))
                throw new InvalidOperationException("A valid PRM reissue lost its approved signature cycle.");
            transaction.Rollback();
        }

        Console.WriteLine("PRM persisted signature cycle, stale approval rejection and rollback PASS.");
    }

    private static async Task AddSignatureAsync(
        SqlConnection connection, SqlTransaction transaction, int sampleId, string action)
    {
        using var command = new SqlCommand(@"
INSERT dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,UserRole,SignedAt)
VALUES(@SampleID,@Action,N'integration',N'Integration verification',N'Disposable database rehearsal',N'QA',SYSDATETIME());",
            connection, transaction);
        command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
        command.Parameters.Add("@Action", SqlDbType.NVarChar, 80).Value = action;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<DataTable> ReadAsync(
        SqlConnection connection, SqlTransaction? transaction, int sampleId)
    {
        using var command = new SqlCommand(@"
SELECT SignatureID,SampleID,ActionType,SignedBy,SignedAt
FROM dbo.PRM_ElectronicSignatures WITH(HOLDLOCK)
WHERE SampleID=@SampleID ORDER BY SignatureID;", connection, transaction);
        command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
        using SqlDataReader reader = await command.ExecuteReaderAsync();
        var rows = new DataTable();
        rows.Load(reader);
        return rows;
    }
}
