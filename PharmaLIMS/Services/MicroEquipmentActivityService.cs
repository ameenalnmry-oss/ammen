using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;

namespace PharmaLIMS.Services;

/// <summary>Captures real operator-confirmed activity; it does not change legacy equipment assignments.</summary>
internal static class MicroEquipmentActivityService
{
    internal static long CreateDraft(SqlConnection connection, SqlTransaction transaction,
        int equipmentId, string activityType, string actor, string methodReference)
    {
        if (equipmentId <= 0 || string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(activityType))
            throw new ArgumentException("Equipment, activity type and authenticated performer are required.");
        using var command = new SqlCommand(@"
INSERT dbo.MicroEquipmentActivities
(EquipmentID,ActivityType,PerformedBy,MethodReference,EquipmentCodeSnapshot,
 EquipmentNameSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot)
OUTPUT INSERTED.ActivityID
SELECT e.EquipmentID,@Type,@Actor,@Method,e.EquipmentCode,e.EquipmentName,
       e.CalibrationStatus,e.QualificationStatus
FROM dbo.LabEquipment e WITH (UPDLOCK,HOLDLOCK)
WHERE e.EquipmentID=@EquipmentID AND e.IsActive=1 AND e.EquipmentCode LIKE N'MIC-EQ-[0-9][0-9][0-9]'
 AND LEN(e.EquipmentCode)=10
 AND TRY_CONVERT(int,SUBSTRING(e.EquipmentCode,8,3)) BETWEEN 1 AND 34;
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

    internal static void AddLink(SqlConnection connection,SqlTransaction transaction,
        long activityId,string module,int parentId,int? resultId,string actor)
    {
        if(activityId<=0 || parentId<=0 || resultId<=0 || string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Valid activity, source record and actor are required.");
        string normalized=(module??string.Empty).Trim().ToUpperInvariant();
        if(normalized is not ("PRM" or "EM" or "WATER")) throw new ArgumentException("Unsupported laboratory module.");
        using var command=new SqlCommand(@"
IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND ActivityStatus=N'Draft')
 THROW 56510,'Activity is no longer editable.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityLinks WITH(UPDLOCK,HOLDLOCK)
 WHERE ActivityID=@Activity AND Module=@Module AND ParentRecordID=@Parent AND ResultRecordID=@Result)
 INSERT dbo.MicroEquipmentActivityLinks(ActivityID,Module,ParentRecordID,ResultRecordID,LinkedBy)
 VALUES(@Activity,@Module,@Parent,@Result,@Actor);",connection,transaction);
        command.Parameters.Add("@Activity",SqlDbType.BigInt).Value=activityId;
        command.Parameters.Add("@Module",SqlDbType.NVarChar,20).Value=normalized;
        command.Parameters.Add("@Parent",SqlDbType.Int).Value=parentId;
        command.Parameters.Add("@Result",SqlDbType.Int).Value=resultId!.Value;
        command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=actor;
        command.ExecuteNonQuery();
    }

    internal static void ConfirmUse(SqlConnection connection,SqlTransaction transaction,
        long activityId,byte[] expectedRowVersion,string actor,DateTimeOffset actualStart,DateTimeOffset actualEnd)
    {
        if(activityId<=0 || expectedRowVersion is null || expectedRowVersion.Length!=8 || string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("Activity, baseline and authenticated performer are required.");
        if(actualEnd<actualStart) throw new ArgumentException("Use end must not precede the start.");
        using var command=new SqlCommand(@"
UPDATE dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
SET ActivityStatus=N'Submitted', ActualStartAt=@Start, ActualEndAt=@End
WHERE ActivityID=@Activity AND ActivityStatus=N'Draft' AND VersionToken=@Version
 AND EXISTS(SELECT 1 FROM dbo.MicroEquipmentActivityLinks WHERE ActivityID=@Activity);
IF @@ROWCOUNT<>1 THROW 56511,'Stale or unlinked activity: reload and confirm actual use.',1;",connection,transaction);
        command.Parameters.Add("@Activity",SqlDbType.BigInt).Value=activityId;
        command.Parameters.Add("@Version",SqlDbType.Binary,8).Value=expectedRowVersion;
        command.Parameters.Add("@Start",SqlDbType.DateTimeOffset).Value=actualStart;
        command.Parameters.Add("@End",SqlDbType.DateTimeOffset).Value=actualEnd;
        command.ExecuteNonQuery();
        WriteAudit(connection,transaction,activityId,"USE CONFIRMED",actor,"Actual equipment use confirmed by technician.");
    }

    private static void WriteAudit(SqlConnection connection,SqlTransaction transaction,long activityId,
        string action,string actor,string reason)
    {
        using var command=new SqlCommand(@"
INSERT dbo.MicroEquipmentActivityAudit(ActivityID,ActionType,NewStateJson,ActionReason,ChangedBy)
SELECT ActivityID,@Action,
(SELECT ActivityID,EquipmentID,ActivityType,ActivityStatus,PerformedBy,ActualStartAt,ActualEndAt,
 EquipmentCodeSnapshot,CalibrationStatusSnapshot,QualificationStatusSnapshot
 FOR JSON PATH,WITHOUT_ARRAY_WRAPPER,INCLUDE_NULL_VALUES),@Reason,@Actor
FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK) WHERE ActivityID=@Activity;",connection,transaction);
        command.Parameters.Add("@Activity",SqlDbType.BigInt).Value=activityId;
        command.Parameters.Add("@Action",SqlDbType.NVarChar,60).Value=action;
        command.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=reason;
        command.Parameters.Add("@Actor",SqlDbType.NVarChar,100).Value=actor;
        if(command.ExecuteNonQuery()!=1) throw new DBConcurrencyException("Activity evidence no longer exists.");
    }
}
