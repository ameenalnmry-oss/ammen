using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class SiteEmWorkflowCases
{
    private sealed record VisiblePlate(int PlateId,int? TotalCount,string Remarks,int? EquipmentID);
    // Auth/plan/status/audit adapters deliberately isolate displayed-evidence
    // freshness. Native execution uses real snapshot SELECTs and actual method SQL.
    private class BoundaryBase
    {
        internal static string CurrentStatus="";
        internal static Func<SqlConnection,SqlTransaction,DataTable> ReadSnapshot=null!;
        internal static Action<Action<SqlConnection,SqlTransaction>> Transaction=null!;
        internal static Func<string,SqlParameter[],SqlConnection,SqlTransaction,int> Write=null!;
        internal static Action Audit=()=>{};
        protected static class AppConfig { public static bool DevelopmentAdminFullPermissions=>false; public static bool IsProduction=>true; public static int CommandTimeoutSeconds=>15; }
        protected static void EnsureEMWorkflowObjects(){}
        protected static string ResolveAuthenticatedSigner(string user)=>user;
        protected static string GetEMWorkflowStatus(int id)=>CurrentStatus;
        protected static string GetLockedEMWorkflowStatusInTransaction(SqlConnection c,SqlTransaction t,int id)=>CurrentStatus;
        protected static string EnsureUserPermissionInTransaction(SqlConnection c,SqlTransaction t,string user,string permission,string action)=>"Supervisor";
        protected static string EnsureQaApprovalAuthorizationInTransaction(SqlConnection c,SqlTransaction t,string user,string action)=>"QA";
        protected static int EnsureEmSourcePlanInTransaction(SqlConnection c,SqlTransaction t,int id,bool production)=>0;
        protected static bool IsAdministrativeRole(string role)=>false;
        protected static bool HasEmSignatureInTransaction(SqlConnection c,SqlTransaction t,int id,string user,params string[] actions)=>false;
        protected static void EnsureEmApprovalQualityEventGateInTransaction(SqlConnection c,SqlTransaction t,int id,string number){}
        protected static void ExecuteInTransaction(Action<SqlConnection,SqlTransaction> action)=>Transaction(action);
        protected static DataTable ReadEmPlateSnapshotInTransaction(SqlConnection c,SqlTransaction t,int id)=>ReadSnapshot(c,t);
        protected static int ExecuteNonQueryWithTransaction(string sql,SqlParameter[] p,SqlConnection c,SqlTransaction t)=>Write(sql,p,c,t);
        protected static void AddAuditTrailAdvanced(params object?[] args)=>Audit();
    }

    internal static void RunPortable(bool correctedRequired)
    {
        BoundaryBase.Transaction=action=>action(null!,null!);
        int writes=0;BoundaryBase.Write=(sql,p,c,t)=>{writes++;return 1;};
        var changes=new Action<DataTable>[] {
            t=>{t.Rows[0]["TotalCount"]=2m;t.Rows[0]["ResultCFU"]=2m;},
            t=>t.Rows[0]["ColoniesObserved"]="new remarks",
            t=>t.Rows[0]["AlertLimitSnapshot"]=3m,
            t=>t.Rows[0]["ResultUnitSnapshot"]="CFU/sample",
            t=>t.Rows[0]["PlateCode"]="new plate identity",
            t=>t.Rows[0]["EquipmentID"]=2,
            t=>t.Rows[0]["EquipmentHistoryID"]=2L,
            t=>t.Rows[0]["ResultRowVersion"]=new byte[]{0,0,0,0,0,0,0,2},
            t=>t.Rows[0]["EventRowVersion"]=new byte[]{0,0,0,0,0,0,0,3},
            t=>{var row=t.NewRow();row.ItemArray=t.Rows[0].ItemArray;row["Id"]=3;t.Rows.Add(row);},
            t=>t.Rows.RemoveAt(1)
        };
        foreach(bool corrected in new[]{false,true})
        {
            int accepted=0,rejected=0;
            foreach(string action in new[]{"ReviewEMEvent","ApproveEMEvent"})
            {
                BoundaryBase.CurrentStatus=action=="ReviewEMEvent"?"Under Review":"Reviewed";
                foreach(var change in changes)
                {
                    var loaded=MakeSnapshot();var current=loaded.Copy();change(current);
                    BoundaryBase.ReadSnapshot=(c,t)=>current;
                    writes=0;bool reject=Invoke(corrected,action,loaded);
                    if(reject){if(writes!=0)throw new Exception("Stale evidence reached a write.");rejected++;}else accepted++;
                }
                var unchanged=MakeSnapshot();BoundaryBase.ReadSnapshot=(c,t)=>unchanged;
                if(Invoke(corrected,action,unchanged)||writes==0)throw new Exception("Complete unchanged evidence was rejected.");
                writes=0;
                if(corrected && correctedRequired && (!Invoke(true,action,null)||writes!=0))throw new Exception("Missing loaded evidence was accepted.");
            }
            if(!corrected && accepted!=22)throw new Exception("Frozen acceptance gap did not reproduce.");
            if(corrected && correctedRequired && rejected!=22)throw new Exception($"Corrected freshness rejected {rejected}/22; accepted {accepted}.");
            Console.WriteLine($"SITE EM PORTABLE {(corrected?"candidate":"frozen")}: rejected {rejected}/22 stale cases; accepted {accepted}; unchanged controls accepted. Exact backend methods with read/write/auth/plan adapters.");
        }
        VerifyUiCapture();
    }

    private static void VerifyUiCapture()
    {
        UiCaptureAdapter Ui()=>new(){_loadedPlateSnapshot=MakeSnapshot(),plateItems=new(){new(1,1,"observed",1),new(2,1,"observed",1)}};
        int rejected=0;
        foreach(var corrupt in new Action<UiCaptureAdapter>[] {
            ui=>ui._loadedPlateSnapshot=null,
            ui=>ui.InvalidGrid=true,
            ui=>ui.plateItems[0]=new(1,2,"observed",1),
            ui=>ui.plateItems[0]=new(1,1,"unsaved remarks",1),
            ui=>ui.plateItems[0]=new(1,1,"observed",2),
            ui=>ui.plateItems.RemoveAt(1),
            ui=>ui.plateItems[1]=new(1,1,"observed",1)})
        {
            var ui=Ui();corrupt(ui);
            try{ui.Capture(1);}catch(Exception ex) when(ex is InvalidOperationException || ex is ArgumentException || ex is DBConcurrencyException){rejected++;}
        }
        var good=Ui();DataTable captured=good.Capture(1);
        if(ReferenceEquals(captured,good._loadedPlateSnapshot))throw new Exception("Expected independent pre-signature copy.");
        good._loadedPlateSnapshot!.Rows[0]["ColoniesObserved"]="changed after capture";
        if((string)captured.Rows[0]["ColoniesObserved"]!="observed")throw new Exception("Captured evidence rebased with UI data.");
        try{Ui().Capture(99);}catch(DBConcurrencyException){rejected++;}
        if(rejected!=8)throw new Exception($"UI capture rejected {rejected}/8 invalid states.");
        Console.WriteLine("PASS SITE EM CAPTURE: 8/8 missing/unsaved/invalid/wrong-event controls rejected; immutable snapshot copy retained. Exact capture method with grid adapter; not WPF.");
    }

    private static bool Invoke(bool corrected,string action,DataTable? loaded)
    {
        var method=(corrected?typeof(Corrected):typeof(Frozen)).GetMethod(action)!;
        var args=new List<object?>{1,"EM-1","independent-user","Controlled evidence review","Fixture verification"};
        if(method.GetParameters().Length==6)args.Add(loaded);
        try{method.Invoke(null,args.ToArray());return false;}
        catch(TargetInvocationException ex) when(ex.InnerException is DBConcurrencyException){return true;}
        catch(TargetInvocationException ex){throw ex.InnerException!;}
    }

    private static DataTable MakeSnapshot()
    {
        var t=new DataTable();
        foreach(var col in new[]{("Id",typeof(int)),("EventId",typeof(int)),("TotalCount",typeof(decimal)),("ColoniesObserved",typeof(string)),
            ("ResultCFU",typeof(decimal)),("Status",typeof(string)),("ResultCalculationVersion",typeof(short)),("EvidenceComplete",typeof(int)),
            ("Method",typeof(string)),("ResultUnitSnapshot",typeof(string)),("AlertLimitSnapshot",typeof(decimal)),("ActionLimitSnapshot",typeof(decimal)),
            ("AirVolumeLitersSnapshot",typeof(int)),("FinalResult",typeof(string)),("ResultRowVersion",typeof(byte[])),("EventRowVersion",typeof(byte[])),
            ("EquipmentID",typeof(int)),("EquipmentHistoryID",typeof(long)),("PlateCode",typeof(string))})t.Columns.Add(col.Item1,col.Item2);
        foreach(int id in new[]{1,2})t.Rows.Add(id,1,1m,"observed",1m,"Pass",(short)1,1,"Personnel Monitoring","CFU/plate",2m,5m,DBNull.Value,"Results Entered",new byte[]{0,0,0,0,0,0,0,1},new byte[]{0,0,0,0,0,0,0,1},1,1L,"PM-"+id);
        return t;
    }
}
