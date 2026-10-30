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
        return planId;
    }
    }
}
