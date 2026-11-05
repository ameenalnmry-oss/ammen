using Microsoft.Data.SqlClient;

// Synthetic database tests: fixture records roll back, and the no-login probe
// is explicitly removed after reverting its context. These do not prove Windows service
// authentication or functional review/approval UAT and cannot record acceptance.
internal static class EquipmentSigningIntegrityIntegration
{
    private const string Setup = @"
SET NOCOUNT ON;
IF DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
 THROW 56850,'Signing integrity tests require a disposable integration database.',1;
-- The probe must survive a trigger-initiated rollback until EXECUTE AS is reverted.
-- Create only this no-login fixture principal outside the record transaction.
CREATE USER eq001_shared_probe WITHOUT LOGIN;
ALTER ROLE PharmaLIMS_EquipmentApp ADD MEMBER eq001_shared_probe;
GRANT SELECT,INSERT,UPDATE,DELETE ON dbo.MicroEquipmentActivities TO eq001_shared_probe;
GRANT SELECT,INSERT,UPDATE,DELETE ON dbo.MicroEquipmentActivityLinks TO eq001_shared_probe;
BEGIN TRANSACTION;
DECLARE @Equipment int,@Approved bigint,@Draft bigint,@Json nvarchar(max);
INSERT dbo.LabEquipment(EquipmentCode,EquipmentName,EquipmentType,Department,CreatedBy)
VALUES(N'MIC-EQ-998',N'Isolated signing integrity fixture',N'Incubator',N'Microbiology',N'fixture');
SET @Equipment=CONVERT(int,SCOPE_IDENTITY());
-- Seed historical signed data only in this disposable rolled-back transaction.
DISABLE TRIGGER dbo.TRG_MicroEquipmentActivities_ImmutableSigned ON dbo.MicroEquipmentActivities;
DISABLE TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin ON dbo.MicroEquipmentActivitySignatures;
DISABLE TRIGGER dbo.TRG_MicroEquipmentActivityLinks_SignedGuard ON dbo.MicroEquipmentActivityLinks;
INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,
 ActualStartAt,ActualEndAt,MethodReference,EquipmentCodeSnapshot,EquipmentNameSnapshot)
VALUES(@Equipment,N'Use',N'Approved',N'fixture-performer',SYSDATETIMEOFFSET(),
 SYSDATETIMEOFFSET(),N'Original SOP',N'MIC-EQ-998',N'Isolated signing integrity fixture');
SET @Approved=CONVERT(bigint,SCOPE_IDENTITY());
DECLARE @Stage nvarchar(30)=N'Reviewed';
SET @Json=(SELECT ActivityID,EquipmentID,ActivityType,@Stage AS ActivityStatus,PerformedBy,
         ActualStartAt,ActualEndAt,MethodReference,ActivityReason,
         EquipmentCodeSnapshot,EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot,
         JSON_QUERY((SELECT EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,
                            AbsoluteError,NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy,RecordedAt
                     FROM dbo.MicroEquipmentStructuredEvidence
                     WHERE ActivityID=@Approved FOR JSON PATH,INCLUDE_NULL_VALUES)) AS StructuredEvidence,
         JSON_QUERY((SELECT PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum,AbsoluteError,Outcome
                     FROM dbo.MicroEquipmentCalibrationPoints WHERE ActivityID=@Approved
                     ORDER BY PointNo FOR JSON PATH,INCLUDE_NULL_VALUES)) AS CalibrationPoints
  FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Approved
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
INSERT dbo.MicroEquipmentActivitySignatures(ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
VALUES(@Approved,N'Reviewed',N'Historical serializer fixture',HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Json)),N'fixture-reviewer');
SET @Stage=N'Approved';
SET @Json=(SELECT ActivityID,EquipmentID,ActivityType,@Stage AS ActivityStatus,PerformedBy,
         ActualStartAt,ActualEndAt,MethodReference,ActivityReason,
         EquipmentCodeSnapshot,EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot,
         JSON_QUERY((SELECT EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,
                            AbsoluteError,NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy,RecordedAt
                     FROM dbo.MicroEquipmentStructuredEvidence
                     WHERE ActivityID=@Approved FOR JSON PATH,INCLUDE_NULL_VALUES)) AS StructuredEvidence,
         JSON_QUERY((SELECT PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum,AbsoluteError,Outcome
                     FROM dbo.MicroEquipmentCalibrationPoints WHERE ActivityID=@Approved
                     ORDER BY PointNo FOR JSON PATH,INCLUDE_NULL_VALUES)) AS CalibrationPoints
  FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Approved
  FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES);
INSERT dbo.MicroEquipmentActivitySignatures(ActivityID,SignatureMeaning,SignatureReason,ContentHash,SignedBy)
VALUES(@Approved,N'Approved',N'Historical serializer fixture',HASHBYTES('SHA2_256',CONVERT(varbinary(max),@Json)),N'fixture-approver');
INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,
 ActualStartAt,ActualEndAt,MethodReference,EquipmentCodeSnapshot,EquipmentNameSnapshot)
VALUES(@Equipment,N'Use',N'Submitted',N'fixture-performer',SYSDATETIMEOFFSET(),
 SYSDATETIMEOFFSET(),N'Original SOP',N'MIC-EQ-998',N'Isolated signing integrity fixture');
SET @Draft=CONVERT(bigint,SCOPE_IDENTITY());
INSERT dbo.MicroEquipmentActivityLinks(ActivityID,Module,ParentRecordID,ResultRecordID,LinkedBy)
VALUES(@Approved,N'WATER',1,1,N'fixture');
ENABLE TRIGGER dbo.TRG_MicroEquipmentActivities_ImmutableSigned ON dbo.MicroEquipmentActivities;
ENABLE TRIGGER dbo.TRG_MicroEquipmentActivitySignatures_TrustedOrigin ON dbo.MicroEquipmentActivitySignatures;
ENABLE TRIGGER dbo.TRG_MicroEquipmentActivityLinks_SignedGuard ON dbo.MicroEquipmentActivityLinks;
SELECT @Approved AS Approved,@Draft AS Draft;
";

    internal static async Task VerifyAsync(string connectionString)
    {
        var attacks = new (string Name, string Sql, int[] Expected)[]
        {
            ("signed SOP content", "UPDATE dbo.MicroEquipmentActivities SET MethodReference=N'Changed SOP' WHERE ActivityID=@Approved;", new[]{56758}),
            ("signed trailing-space content", "UPDATE dbo.MicroEquipmentActivities SET MethodReference=MethodReference+N' ' WHERE ActivityID=@Approved;", new[]{56758}),
            ("signed workflow reversion", "UPDATE dbo.MicroEquipmentActivities SET ActivityStatus=N'Draft' WHERE ActivityID=@Approved;", new[]{56758}),
            ("signed insert", @"INSERT dbo.MicroEquipmentActivities(EquipmentID,ActivityType,ActivityStatus,PerformedBy,EquipmentCodeSnapshot,EquipmentNameSnapshot)
 SELECT EquipmentID,N'Use',N'Approved',N'forged',EquipmentCodeSnapshot,EquipmentNameSnapshot FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Draft;", new[]{56757}),
            ("signed link source move", "UPDATE dbo.MicroEquipmentActivityLinks SET ActivityID=@Draft WHERE ActivityID=@Approved;", new[]{56760}),
            ("signed link destination insert", "INSERT dbo.MicroEquipmentActivityLinks(ActivityID,Module,ParentRecordID,ResultRecordID,LinkedBy) VALUES(@Approved,N'WATER',2,2,N'forged');", new[]{56760}),
            ("forged signing actor", @"DECLARE @Version binary(8); SELECT @Version=VersionToken FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Draft;
 EXEC dbo.MicroEquipment_SignActivityStage @Draft,@Version,N'forged-reviewer',N'Reviewed',N'forged reason';", new[]{56750}),
            ("signed deletion", "DELETE dbo.MicroEquipmentActivities WHERE ActivityID=@Approved;", new[]{547,56758})
        };
        foreach (var attack in attacks)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            Exception? originalFailure = null;
            try
            {
                Console.WriteLine("CHECK equipment signing integrity: " + attack.Name);
                var ids = await SeedAsync(connection);
                using var command = new SqlCommand("EXECUTE AS USER=N'eq001_shared_probe';\n" + attack.Sql, connection);
                command.Parameters.AddWithValue("@Approved", ids.Approved);
                command.Parameters.AddWithValue("@Draft", ids.Draft);
                bool rejected = false;
                try { await command.ExecuteNonQueryAsync(); }
                catch (SqlException ex) when (attack.Expected.Contains(ex.Number)) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Security trigger succeeded unexpectedly: " + attack.Name);
            }
            catch (Exception ex)
            {
                originalFailure = ex;
                throw;
            }
            finally { await CleanupAfterCaseAsync(connection, originalFailure); }
        }
        // Legitimate read: unchanged historical stage hashes must still verify.
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            Exception? originalFailure = null;
            try
            {
                Console.WriteLine("CHECK equipment signing integrity: historical hashes and tamper rejection");
                var ids = await SeedAsync(connection);
                using var command = new SqlCommand("EXECUTE AS USER=N'eq001_shared_probe'; EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity; REVERT;", connection);
                command.Parameters.AddWithValue("@Activity", ids.Approved);
                await command.ExecuteNonQueryAsync();
                using var tamper = new SqlCommand(@"
DISABLE TRIGGER dbo.TRG_MicroEquipmentActivities_ImmutableSigned ON dbo.MicroEquipmentActivities;
UPDATE dbo.MicroEquipmentActivities SET MethodReference=N'Altered historical fixture' WHERE ActivityID=@Activity;
ENABLE TRIGGER dbo.TRG_MicroEquipmentActivities_ImmutableSigned ON dbo.MicroEquipmentActivities;
EXEC dbo.MicroEquipment_ValidateActivitySignatures @Activity;", connection);
                tamper.Parameters.AddWithValue("@Activity", ids.Approved);
                bool mismatch = false;
                try { await tamper.ExecuteNonQueryAsync(); }
                catch (SqlException ex) when (ex.Number == 56755) { mismatch = true; }
                if (!mismatch) throw new InvalidOperationException("Tampered historical signature unexpectedly verified.");
            }
            catch (Exception ex)
            {
                originalFailure = ex;
                throw;
            }
            finally { await CleanupAfterCaseAsync(connection, originalFailure); }
        }
        Console.WriteLine("Equipment SQL-origin/content/link/print hash integration PASS; Windows functional signing UAT remains separate.");
    }

    private static async Task<(long Approved, long Draft)> SeedAsync(SqlConnection connection)
    {
        using var command = new SqlCommand(Setup, connection);
        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Signing fixture IDs missing.");
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
    private static async Task CleanupAfterCaseAsync(SqlConnection connection, Exception? originalFailure)
    {
        try { await CleanupAsync(connection); }
        catch (Exception cleanupError) when (originalFailure != null)
        {
            // Keep the actual security assertion/SQL failure visible to the runner.
            Console.Error.WriteLine("Equipment signing fixture cleanup also failed: " + cleanupError);
        }
    }

    private static async Task CleanupAsync(SqlConnection connection)
    {
        using var command = new SqlCommand(@"
IF DB_NAME() NOT LIKE N'PharmaLIMS_Integration[_]%'
 THROW 56850,'Signing fixture cleanup requires a disposable integration database.',1;
IF USER_NAME()=N'eq001_shared_probe' REVERT;
IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
IF DATABASE_PRINCIPAL_ID(N'eq001_shared_probe') IS NOT NULL
 DROP USER eq001_shared_probe;", connection);
        await command.ExecuteNonQueryAsync();
    }
}
