using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System;
using System.Data;
using System.Globalization;
using System.Collections.Generic;
namespace PharmaLIMS;
public static partial class DatabaseHelper
{
    internal static void LockEmSourcePlanInTransaction(SqlConnection connection,SqlTransaction transaction,int planId)
    {
        using var cmd=new SqlCommand(@"DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@Resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
IF @r<0 THROW 55514,'The EM source plan is busy. Reload and retry.',1;",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        cmd.Parameters.Add("@Resource",SqlDbType.NVarChar,255).Value="PharmaLIMS-EM-SourcePlan-"+planId.ToString(CultureInfo.InvariantCulture);cmd.ExecuteNonQuery();
    }
    internal static int EnsureEmSourcePlanInTransaction(SqlConnection connection,SqlTransaction transaction,int eventId,bool requireNativePlan)
    {
        int planId;
        using(var find=new SqlCommand("SELECT ISNULL(PlanID,0) FROM dbo.EM_Events WHERE Id=@ID;",connection,transaction))
        { find.CommandTimeout=AppConfig.CommandTimeoutSeconds;find.Parameters.Add("@ID",SqlDbType.Int).Value=eventId;object? value=find.ExecuteScalar();
          if(value==null || value==DBNull.Value) throw new InvalidOperationException("The EM event no longer exists.");planId=Convert.ToInt32(value,CultureInfo.InvariantCulture); }
        if(planId<0 || (requireNativePlan && planId==0)) throw new InvalidOperationException("A released controlled source plan is required for this EM operation.");
        if(planId>0)
        {
            LockEmSourcePlanInTransaction(connection,transaction,planId);
            using var plan=new SqlCommand("SELECT Status FROM dbo.EM_Plans WITH(UPDLOCK,HOLDLOCK) WHERE PlanID=@ID;",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
            plan.Parameters.Add("@ID",SqlDbType.Int).Value=planId;string status=Convert.ToString(plan.ExecuteScalar(),CultureInfo.InvariantCulture) ?? "";
            if(!status.Equals("Ready for Results",StringComparison.OrdinalIgnoreCase) && !status.Equals("Completed",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The EM source plan is missing, cancelled or not released to results.");
        }
        using var verify=new SqlCommand("SELECT ISNULL(PlanID,0),COALESCE(NULLIF(LTRIM(RTRIM(WorkflowStatus)),N''),LTRIM(RTRIM(FinalResult))),LTRIM(RTRIM(FinalResult)) FROM dbo.EM_Events WITH(UPDLOCK,HOLDLOCK) WHERE Id=@ID;",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        verify.Parameters.Add("@ID",SqlDbType.Int).Value=eventId;using var reader=verify.ExecuteReader();
        if(!reader.Read() || reader.GetInt32(0)!=planId || string.Equals(Convert.ToString(reader.GetValue(1),CultureInfo.InvariantCulture),"Cancelled",StringComparison.OrdinalIgnoreCase) || string.Equals(Convert.ToString(reader.GetValue(2),CultureInfo.InvariantCulture),"Cancelled",StringComparison.OrdinalIgnoreCase))
            throw new DBConcurrencyException("The EM event/source plan changed or was cancelled. No operation was committed.");
        return planId;
    }
    internal static string GetEmPlateSnapshotSql(bool forUpdate)
    {
        string hint=forUpdate?" WITH(UPDLOCK,HOLDLOCK)":"";
        return @"SELECT P.Id,P.EventId,P.Method,
CASE WHEN P.Method=N'Personnel Monitoring' AND NULLIF(LTRIM(RTRIM(E.EmployeeId)),N'') IS NULL AND ps.EmployeeID IS NOT NULL
THEN CONCAT(P.PlateCode,N' [',ps.EmployeeID,N' - ',ps.EmployeeName,N']') ELSE P.PlateCode END AS PlateCode,P.SequenceNo,P.TotalCount,P.ColoniesObserved,P.ResultCFU,P.Status,P.FungalCount,P.CorrectedCount,
P.ResultRowVersion,P.ResultCalculationVersion,E.ResultRowVersion AS EventRowVersion,E.WorkflowStatus,E.FinalResult,
EVID.AlertLimitSnapshot,EVID.ActionLimitSnapshot,EVID.ResultUnitSnapshot,EVID.AirVolumeLitersSnapshot,EVID.NativeSnapshotComplete,EVID.ReconciliationID,EVID.EvidenceSource,EVID.EvidenceComplete,
COALESCE(NULLIF(LTRIM(RTRIM(E.GradeSnapshot)),N''),A.Grade) AS Grade,
(SELECT u.EquipmentID FROM dbo.LabEquipmentUsage u"+hint+@" WHERE u.Module=N'EM' AND u.ParentRecordID=P.EventId AND u.ResultRecordID=P.Id) AS EquipmentID,
(SELECT MAX(h.HistoryID) FROM dbo.LabEquipmentUsageHistory h"+hint+@" WHERE h.Module=N'EM' AND h.ParentRecordID=P.EventId AND h.ResultRecordID=P.Id) AS EquipmentHistoryID
FROM dbo.EM_EventPlates P"+hint+@" JOIN dbo.EM_Events E"+hint+@" ON E.Id=P.EventId JOIN dbo.EM_Areas A ON A.Id=E.AreaId LEFT JOIN dbo.EM_PlanSamples ps ON ps.PlanSampleID=P.PlanSampleID
"+EmLimitEvidenceSql.Joins(forUpdate)+@" WHERE P.EventId=@eventId ORDER BY P.Id;";
    }
    internal static DataTable ReadEmPlateSnapshotInTransaction(SqlConnection c,SqlTransaction t,int eventId)
    { using var cmd=new SqlCommand(GetEmPlateSnapshotSql(true),c,t){CommandTimeout=AppConfig.CommandTimeoutSeconds};cmd.Parameters.Add("@eventId",SqlDbType.Int).Value=eventId;
      using var r=cmd.ExecuteReader();var table=new DataTable();table.Load(r);return table; }
    internal static void CancelEmPlanInTransaction(SqlConnection c,SqlTransaction t,int planId,string planNo,string expectedStatus,string signedBy,string meaning,string reason)
    {
        ValidateSignatureMetadata(planId,"CancelEMPlan",meaning);
        if(string.IsNullOrWhiteSpace(reason) || reason.Length>500) throw new InvalidOperationException("A cancellation reason of 1–500 characters is required.");
        string signer=ResolveAuthenticatedSigner(signedBy);string role=EnsureUserPermissionInTransaction(c,t,signer,"CanApproveResults","cancel an EM plan");
        LockEmSourcePlanInTransaction(c,t,planId);
        using var cmd=new SqlCommand(EmSourcePlanSql.Cancel,c,t){CommandTimeout=AppConfig.CommandTimeoutSeconds};
        cmd.Parameters.Add("@Plan",SqlDbType.Int).Value=planId;cmd.Parameters.Add("@Expected",SqlDbType.NVarChar,100).Value=expectedStatus;
        cmd.Parameters.Add("@User",SqlDbType.NVarChar,100).Value=signer;cmd.Parameters.Add("@Role",SqlDbType.NVarChar,100).Value=role;
        cmd.Parameters.Add("@Meaning",SqlDbType.NVarChar,255).Value=meaning;cmd.Parameters.Add("@Reason",SqlDbType.NVarChar,500).Value=reason;
        var changes=new List<(string Table,int Id,string No,string Old)>();
        using(var reader=cmd.ExecuteReader()) while(reader.Read()) changes.Add((reader.GetString(0),reader.GetInt32(1),reader.GetString(2),reader.IsDBNull(3)?"":reader.GetString(3)));
        foreach(var change in changes) AddAuditTrailAdvanced(c,t,change.Table,change.Id,"Cancel EM Plan",change.Old,"Cancelled",reason,signer,change.Table=="EM_Plans"?"Status":"WorkflowStatus",null,change.No,"Environmental Monitoring");
    }
}
