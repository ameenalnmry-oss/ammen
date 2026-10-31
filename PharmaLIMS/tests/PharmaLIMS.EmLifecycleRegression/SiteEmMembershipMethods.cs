using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
// Exact readonly site guard methods; real SqlConnection/transaction and sp_getapplock.
internal static partial class SiteEmMembershipCases
{
    private static class Frozen
    {
        private static class AppConfig { internal static int CommandTimeoutSeconds=>15; }
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
    }
    private static class Corrected
    {
        private static class AppConfig { internal static int CommandTimeoutSeconds=>15; }
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
        reader.Close();
        if(planId>0)
        {
            // Retain parent -> plate -> source-sample locks through the caller's
            // transaction. Never accept a plausible employee label from a wrong link.
            using var membership=new SqlCommand(@"
SELECT P.Method,S.PlanSampleID,S.PlanID,S.EventID,S.AreaID,S.Method,
       S.IsNegativeControl,S.Status,S.EmployeeID,S.EmployeeName,
       E.EmployeeId,E.EmployeeName,E.AreaId
FROM dbo.EM_EventPlates P WITH(UPDLOCK,HOLDLOCK)
JOIN dbo.EM_Events E WITH(UPDLOCK,HOLDLOCK) ON E.Id=P.EventId
LEFT JOIN dbo.EM_PlanSamples S WITH(UPDLOCK,HOLDLOCK) ON S.PlanSampleID=P.PlanSampleID
WHERE P.EventId=@ID ORDER BY P.Id;",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
            membership.Parameters.Add("@ID",SqlDbType.Int).Value=eventId;
            using var membershipReader=membership.ExecuteReader();
            bool anyPlate=false;
            while(membershipReader.Read())
            {
                anyPlate=true;
                string Text(int index)=>Convert.ToString(membershipReader.GetValue(index),CultureInfo.InvariantCulture) ?? string.Empty;
                string plateMethod=Text(0).Trim(),sampleMethod=Text(5).Trim(),sampleStatus=Text(7).Trim();
                if(membershipReader.IsDBNull(1) || membershipReader.IsDBNull(2) || membershipReader.GetInt32(2)!=planId ||
                    membershipReader.IsDBNull(3) || membershipReader.GetInt32(3)!=eventId ||
                    membershipReader.IsDBNull(4) || membershipReader.IsDBNull(12) || membershipReader.GetInt32(4)!=membershipReader.GetInt32(12) ||
                    string.IsNullOrWhiteSpace(plateMethod) || !string.Equals(plateMethod,sampleMethod,StringComparison.OrdinalIgnoreCase) ||
                    membershipReader.IsDBNull(6) || membershipReader.GetBoolean(6) ||
                    (!sampleStatus.Equals("Ready for Results",StringComparison.OrdinalIgnoreCase) && !sampleStatus.Equals("Completed",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("EM plate membership differs from the released source plan. Reload and obtain controlled QA correction before continuing.");

                string sampleEmployeeId=Text(8),sampleEmployeeName=Text(9),eventEmployeeId=Text(10),eventEmployeeName=Text(11);
                // Match the existing SQL label fallback: both event identity
                // fields may be null/ASCII-blank for historical aggregate events.
                bool eventIdAbsent=eventEmployeeId.Trim(' ').Length==0;
                bool eventNameAbsent=eventEmployeeName.Trim(' ').Length==0;
                bool personnel=plateMethod.IndexOf("personnel",StringComparison.OrdinalIgnoreCase)>=0 ||
                    plateMethod.IndexOf("glove",StringComparison.OrdinalIgnoreCase)>=0 || !eventIdAbsent || !eventNameAbsent;
                if(personnel && (string.IsNullOrWhiteSpace(sampleEmployeeId) || string.IsNullOrWhiteSpace(sampleEmployeeName) ||
                    eventIdAbsent!=eventNameAbsent || (!eventIdAbsent &&
                    (!string.Equals(eventEmployeeId.Trim(),sampleEmployeeId.Trim(),StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(eventEmployeeName.Trim(),sampleEmployeeName.Trim(),StringComparison.Ordinal)))))
                    throw new InvalidOperationException("Personnel identity is missing or contradicts the released plan sample. Reload and obtain controlled QA correction before continuing.");
            }
            membershipReader.Close();
            if(!anyPlate)
                throw new InvalidOperationException("The planned EM event has no result plates. Controlled QA correction is required before processing.");
            // Validate coverage in the reverse direction too. Multiple plates
            // per sample remain allowed; every assigned result sample needs one.
            using var orphan=new SqlCommand(@"
SELECT TOP(1) 1 FROM dbo.EM_PlanSamples S WITH(UPDLOCK,HOLDLOCK)
WHERE S.EventID=@ID AND ISNULL(S.IsNegativeControl,0)=0
  AND NOT EXISTS(SELECT 1 FROM dbo.EM_EventPlates P WITH(UPDLOCK,HOLDLOCK)
                 WHERE P.EventId=@ID AND P.PlanSampleID=S.PlanSampleID);",connection,transaction){CommandTimeout=AppConfig.CommandTimeoutSeconds};
            orphan.Parameters.Add("@ID",SqlDbType.Int).Value=eventId;
            if(orphan.ExecuteScalar()!=null)
                throw new InvalidOperationException("A plan sample assigned to this EM event has no result plate. Controlled QA correction is required before processing.");
        }
        return planId;
    }
    }
}
