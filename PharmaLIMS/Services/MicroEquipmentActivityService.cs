using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Data;
using System.Globalization;
using PharmaLIMS;

namespace PharmaLIMS.Services;

/// <summary>Captures real operator-confirmed activity; it does not change legacy equipment assignments.</summary>
internal static class MicroEquipmentActivityService
{
    internal static long CreateDraft(SqlConnection connection, SqlTransaction transaction,
        int equipmentId, string activityType, string actor, string methodReference)
    {
        if (equipmentId <= 0 || string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(activityType))
            throw new ArgumentException("Equipment, activity type and authenticated performer are required.");
        DatabaseHelper.EnsureUserPermissionInTransaction(connection,transaction,actor,"CanEnterResults","create microbiology equipment activity");
        using var command = new SqlCommand(@"
DECLARE @CreatedActivity TABLE (ActivityID bigint NOT NULL);
INSERT dbo.MicroEquipmentActivities
(EquipmentID,ActivityType,PerformedBy,MethodReference,EquipmentCodeSnapshot,
 EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot)
OUTPUT INSERTED.ActivityID INTO @CreatedActivity(ActivityID)
SELECT e.EquipmentID,@Type,@Actor,@Method,e.EquipmentCode,e.EquipmentName,
       e.CalibrationStatus,e.QualificationStatus
FROM dbo.LabEquipment e WITH (UPDLOCK,HOLDLOCK)
WHERE e.EquipmentID=@EquipmentID AND e.IsActive=1 AND e.EquipmentCode LIKE N'MIC-EQ-[0-9][0-9][0-9]'
 AND LEN(e.EquipmentCode)=10
 AND TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3)) >= 1
 AND UPPER(LTRIM(RTRIM(e.Department)))=N'MICROBIOLOGY';
SELECT ActivityID FROM @CreatedActivity;
",connection,transaction);
        command.Parameters.Add("@EquipmentID",SqlDbType.Int).Value=equipmentId;
        command.Parameters.Add("@Type",SqlDbType.NVarChar,60).Value=activityType;
        command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=actor;
        command.Parameters.Add("@Method",SqlDbType.NVarChar,200).Value=string.IsNullOrWhiteSpace(methodReference)?DBNull.Value:methodReference;
        object? id=command.ExecuteScalar();
        if(id is null || id==DBNull.Value) throw new InvalidOperationException("Selected microbiology equipment is not available.");
        long activityId=Convert.ToInt64(id,CultureInfo.InvariantCulture);
        WriteAudit(connection,transaction,activityId,"DRAFT CREATED",actor,"Created pending execution confirmation.");
        return activityId;
    }

    // Link a real operator-confirmed equipment-use draft to one or more registered
    // samples/events. A test/plate identifier is OPTIONAL; selecting a sample does
    // not claim any equipment has actually been used (ConfirmUse is a later step).
    internal static void AddLink(SqlConnection connection, SqlTransaction transaction,
        long activityId, string module, int parentId, int? resultId, string actor)
    {
        if (activityId <= 0 || parentId <= 0 || (resultId.HasValue && resultId.Value <= 0) ||
            string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Valid draft, registered sample/event and authenticated performer are required.");
        DatabaseHelper.EnsureUserPermissionInTransaction(connection, transaction, actor,
            "CanEnterResults", "link microbiology equipment activity");
        string normalized = (module ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized is not ("PRM" or "EM" or "WATER"))
            throw new ArgumentException("Unsupported laboratory module.");

        // Verify the parent independently, even when no test identifier was selected.
        // When a test is supplied, its membership in that same parent is mandatory.
        // The snapshot identifies the sampled material on the printed equipment log.
        string sourceSql = normalized switch
        {
            "PRM" => @"SELECT TOP(1) p.SampleNumber, t.TestCode
FROM dbo.PRM_Samples p WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN dbo.PRM_SampleTests t WITH(UPDLOCK,HOLDLOCK)
  ON t.SampleID=p.SampleID AND t.SampleTestID=@Result
WHERE p.SampleID=@Parent AND (@Result IS NULL OR t.SampleTestID IS NOT NULL)",
            "EM" => @"SELECT TOP(1) p.EventNo, t.PlateCode
FROM dbo.EM_Events p WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN dbo.EM_EventPlates t WITH(UPDLOCK,HOLDLOCK)
  ON t.EventId=p.Id AND t.Id=@Result
WHERE p.Id=@Parent AND (@Result IS NULL OR t.Id IS NOT NULL)",
            "WATER" => @"SELECT TOP(1) p.SampleNumber,
  CASE WHEN t.SampleTestID IS NULL THEN NULL ELSE CONVERT(nvarchar(100),t.SampleTestID) END
FROM dbo.Samples p WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN dbo.SampleTests t WITH(UPDLOCK,HOLDLOCK)
  ON t.SampleID=p.SampleID AND t.SampleTestID=@Result
WHERE p.SampleID=@Parent AND (@Result IS NULL OR t.SampleTestID IS NOT NULL)",
            _ => throw new ArgumentException("Unsupported laboratory module.")
        };
        string sampleNumber;
        string? testCode;
        using (var source = new SqlCommand(sourceSql, connection, transaction))
        {
            source.Parameters.Add("@Parent", SqlDbType.Int).Value = parentId;
            source.Parameters.Add("@Result", SqlDbType.Int).Value =
                resultId.HasValue ? (object)resultId.Value : DBNull.Value;
            using SqlDataReader reader = source.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The sample/event does not exist or the selected test does not belong to it.");
            sampleNumber = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
            testCode = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
        }
        if (string.IsNullOrWhiteSpace(sampleNumber))
            throw new InvalidOperationException("The registered sample/event has no identifying number.");

        using var command = new SqlCommand(@"
IF NOT EXISTS (SELECT 1 FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND ActivityStatus=N'Draft' AND PerformedBy=@Actor
 AND ActivityType IN(N'Use',N'Incubation'))
 THROW 56510,'Draft equipment use belongs to another performer or is no longer editable.',1;
IF NOT EXISTS (SELECT 1 FROM dbo.MicroEquipmentActivityLinks WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND Module=@Module AND ParentRecordID=@Parent
   AND ((@Result IS NULL AND ResultRecordID IS NULL) OR ResultRecordID=@Result))
 INSERT dbo.MicroEquipmentActivityLinks
 (ActivityID,Module,ParentRecordID,ResultRecordID,SampleNumberSnapshot,TestCodeSnapshot,LinkedBy)
 VALUES(@Activity,@Module,@Parent,@Result,@SampleNo,@TestCode,@Actor);",
            connection, transaction);
        command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = activityId;
        command.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = normalized;
        command.Parameters.Add("@Parent", SqlDbType.Int).Value = parentId;
        command.Parameters.Add("@Result", SqlDbType.Int).Value =
            resultId.HasValue ? (object)resultId.Value : DBNull.Value;
        command.Parameters.Add("@SampleNo", SqlDbType.NVarChar, 100).Value = sampleNumber;
        command.Parameters.Add("@TestCode", SqlDbType.NVarChar, 100).Value =
            string.IsNullOrWhiteSpace(testCode) ? DBNull.Value : testCode;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = actor;
        command.ExecuteNonQuery();
    }

    internal static void ConfirmUse(SqlConnection connection,SqlTransaction transaction,
        long activityId,byte[] expectedRowVersion,string actor,DateTimeOffset actualStart,DateTimeOffset actualEnd)
    {
        if(activityId<=0 || expectedRowVersion is null || expectedRowVersion.Length!=8 || string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Activity, baseline and authenticated performer are required.");
        if(actualEnd<actualStart) throw new ArgumentException("Use end must not precede the start.");
        DatabaseHelper.EnsureUserPermissionInTransaction(connection,transaction,actor,"CanEnterResults","confirm microbiology equipment use");
        using var command=new SqlCommand(@"
UPDATE dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
SET ActivityStatus=N'Submitted', ActualStartAt=@Start, ActualEndAt=@End
WHERE ActivityID=@Activity AND ActivityStatus=N'Draft' AND PerformedBy=@Actor AND VersionToken=@Version
 AND EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityLinks WHERE ActivityID=@Activity);
IF @@ROWCOUNT<>1 THROW 56511,'Stale or unlinked activity: reload and confirm actual use.',1;",connection,transaction);
        command.Parameters.Add("@Activity",SqlDbType.BigInt).Value=activityId;
        command.Parameters.Add("@Version",SqlDbType.Binary,8).Value=expectedRowVersion;
        command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=actor;
        command.Parameters.Add("@Start",SqlDbType.DateTimeOffset).Value=actualStart;
        command.Parameters.Add("@End",SqlDbType.DateTimeOffset).Value=actualEnd;
        command.ExecuteNonQuery();
        WriteAudit(connection,transaction,activityId,"USE CONFIRMED",actor,"Actual equipment use confirmed by technician.");
    }

    internal static void SubmitNonUseExecution(SqlConnection connection, SqlTransaction transaction,
        long activityId, byte[] expectedRowVersion, string actor, DateTimeOffset actualStart,
        DateTimeOffset actualEnd, string executionDetails)
    {
        if (activityId <= 0 || expectedRowVersion is null || expectedRowVersion.Length != 8 ||
            string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(executionDetails) ||
            executionDetails.Trim().Length > 500 || actualEnd < actualStart)
            throw new ArgumentException("Valid activity, performer, execution details and timestamps are required.");
        DatabaseHelper.EnsureUserPermissionInTransaction(connection, transaction, actor,
            "CanEnterResults", "submit microbiology equipment execution");
        using var command = new SqlCommand(@"
UPDATE dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
SET ActivityStatus=N'Submitted',ActualStartAt=@Start,ActualEndAt=@End,ActivityReason=@Details
WHERE ActivityID=@Activity AND ActivityStatus=N'Draft' AND ActivityType<>N'Use'
  AND PerformedBy=@Actor AND VersionToken=@Version;
IF @@ROWCOUNT<>1 THROW 56512,'Stale, unauthorized or unsuitable activity: reload before submission.',1;",
            connection, transaction);
        command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = activityId;
        command.Parameters.Add("@Version", SqlDbType.Binary, 8).Value = expectedRowVersion;
        command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = actor;
        command.Parameters.Add("@Start", SqlDbType.DateTimeOffset).Value = actualStart;
        command.Parameters.Add("@End", SqlDbType.DateTimeOffset).Value = actualEnd;
        command.Parameters.Add("@Details", SqlDbType.NVarChar, 500).Value = executionDetails.Trim();
        command.ExecuteNonQuery();
        WriteAudit(connection,transaction,activityId,"EXECUTION SUBMITTED",actor,
            "Actual non-use equipment activity recorded. Review and approval remain pending.");
    }

    private static void WriteAudit(SqlConnection connection,SqlTransaction transaction,long activityId,
        string action,string actor,string reason)
    {
        using var command=new SqlCommand(@"
INSERT dbo.MicroEquipmentActivityAudit(ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
SELECT ActivityID,@Action,
(SELECT ActivityID,EquipmentID,ActivityType,ActivityStatus,PerformedBy,ActualStartAt,ActualEndAt,
 EquipmentCodeSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot,ActivityReason,MethodReference
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),@Reason,@Actor
FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK) WHERE ActivityID=@Activity;",connection,transaction);
        command.Parameters.Add("@Activity",SqlDbType.BigInt).Value=activityId;
        command.Parameters.Add("@Action",SqlDbType.NVarChar,60).Value=action;
        command.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=reason;
        command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=actor;
        if(command.ExecuteNonQuery()!=1) throw new DBConcurrencyException("Activity evidence no longer exists.");
    }
}
