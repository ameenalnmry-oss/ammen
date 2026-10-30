using Microsoft.Data.SqlClient;
using System.Data;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static partial class SiteEmWorkflowCases
{
    internal static void RunNative(string cs,string root)
    {
        if(!Regex.IsMatch(new SqlConnectionStringBuilder(cs).InitialCatalog,@"^EQ001_UAT_EM_[0-9a-f]{12}$"))
            throw new Exception("Site EM probe requires the existing owned disposable database.");
        VerifySources(root);
        RunPortable(true);
        void Sql(string sql){using var c=new SqlConnection(cs);c.Open();using var cmd=new SqlCommand(sql,c){CommandTimeout=15};cmd.ExecuteNonQuery();}
        Sql(@"
ALTER TABLE dbo.EM_Events ADD ResultRowVersion rowversion,WorkflowStatus nvarchar(50),FinalResult nvarchar(50),ReviewedBy nvarchar(100),ReviewedDate datetime2,ApprovedBy nvarchar(100),ApprovedDate datetime2;
ALTER TABLE dbo.EM_EventPlates ADD ResultRowVersion rowversion;
CREATE TABLE dbo.EM_Areas(Id int PRIMARY KEY,Grade nvarchar(50)); INSERT dbo.EM_Areas VALUES(1,N'B');
CREATE TABLE dbo.EM_EventSignatures(EventID int,EventNo nvarchar(50),ActionType nvarchar(100),ActionReason nvarchar(500),SignedBy nvarchar(100),UserRole nvarchar(100),MeaningOfSignature nvarchar(250),SignedAt datetime2);
CREATE TABLE dbo.SiteEmTestAudit(ActionText nvarchar(50));
ALTER TABLE dbo.EM_Plans ADD CompletedBy nvarchar(100),CompletedAt datetime2;
ALTER TABLE dbo.EM_PlanSamples ADD Status nvarchar(50);");
        void Reset(string status)
        {
            BoundaryBase.CurrentStatus=status;
            Sql(@"DELETE dbo.EM_EventPlates WHERE Id<>1;
UPDATE dbo.EM_Events SET WorkflowStatus=N'"+status+@"',FinalResult=N'Results Entered',ReviewedBy=NULL,ReviewedDate=NULL,ApprovedBy=NULL,ApprovedDate=NULL,EmployeeId=N'E1',EmployeeName=N'Alice';
UPDATE dbo.EM_EventPlates SET TotalCount=1,ResultCFU=1,Status=N'Pass',ColoniesObserved=N'observed',PlateCode=N'PM-1',AlertLimitSnapshot=2,ActionLimitSnapshot=5,ResultUnitSnapshot=N'CFU/plate' WHERE Id=1;
INSERT dbo.EM_EventPlates(Id,EventId,Method,PlateCode,SequenceNo,SampleSite,PlanSampleID,TotalCount,FungalCount,CorrectedCount,ColoniesObserved,ResultCFU,Status,ResultCalculationVersion,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot)
 SELECT 2,EventId,Method,N'PM-2',2,SampleSite,PlanSampleID,TotalCount,FungalCount,CorrectedCount,ColoniesObserved,ResultCFU,Status,ResultCalculationVersion,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot FROM dbo.EM_EventPlates WHERE Id=1;
UPDATE dbo.LabEquipmentUsage SET EquipmentID=1 WHERE Module=N'EM';
DELETE dbo.LabEquipmentUsageHistory;
INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,EquipmentID,EquipmentCodeSnapshot,ChangeType,MeaningOfSignature,ActionReason,SignedBy,SignedAt) VALUES(N'EM',1,1,1,N'INC-1',N'ASSIGN',N'Assign',N'Initial',N'analyst',SYSDATETIME());
DELETE dbo.EM_EventSignatures;DELETE dbo.SiteEmTestAudit;");
        }
        BoundaryBase.ReadSnapshot=(c,t)=>SiteSnapshot.ReadEmPlateSnapshotInTransaction(c,t,1);
        int writes=0,locks=0;
        bool testLocks=false;
        BoundaryBase.Write=(sql,p,c,t)=>{using var cmd=new SqlCommand(sql,c,t){CommandTimeout=15};cmd.Parameters.AddRange(p);writes++;return cmd.ExecuteNonQuery();};
        BoundaryBase.Transaction=action=>
        {
            using var c=new SqlConnection(cs);c.Open();using var tx=c.BeginTransaction();
            BoundaryBase.Audit=()=>{using var cmd=new SqlCommand("INSERT dbo.SiteEmTestAudit VALUES(N'test adapter')",c,tx);cmd.ExecuteNonQuery();};
            try
            {
                action(c,tx);
                if(testLocks)foreach(string change in new[]{"UPDATE dbo.EM_EventPlates SET ColoniesObserved=N'race' WHERE Id=1", "INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,EquipmentID) VALUES(N'EM',1,1,9)"})
                {
                    using var other=new SqlConnection(cs);other.Open();using var cmd=new SqlCommand("SET LOCK_TIMEOUT 300; "+change,other){CommandTimeout=5};
                    try{cmd.ExecuteNonQuery();throw new Exception("Competing child writer escaped retained snapshot locks.");}
                    catch(SqlException e) when(e.Number==1222){locks++;}
                }
            }
            finally{tx.Rollback();}
        };
        DataTable Capture(){using var c=new SqlConnection(cs);c.Open();using var tx=c.BeginTransaction();return SiteSnapshot.ReadEmPlateSnapshotInTransaction(c,tx,1);}
        var mutations=new[]{
            "UPDATE dbo.EM_EventPlates SET TotalCount=2,ResultCFU=2 WHERE Id=1",
            "UPDATE dbo.EM_EventPlates SET ColoniesObserved=N'changed' WHERE Id=1",
            "UPDATE dbo.EM_EventPlates SET AlertLimitSnapshot=3 WHERE Id=1",
            "UPDATE dbo.EM_EventPlates SET ResultUnitSnapshot=N'CFU/sample' WHERE Id=1",
            "UPDATE dbo.EM_EventPlates SET PlateCode=N'new identity' WHERE Id=1",
            "UPDATE dbo.LabEquipmentUsage SET EquipmentID=2 WHERE Module=N'EM' AND ResultRecordID=1",
            "INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,EquipmentID) VALUES(N'EM',1,1,2)",
            "UPDATE dbo.EM_EventPlates SET Status=Status WHERE Id=1",
            "UPDATE dbo.EM_Events SET FinalResult=FinalResult WHERE Id=1",
            "INSERT dbo.EM_EventPlates(Id,EventId,Method,PlateCode,SequenceNo,SampleSite,PlanSampleID,TotalCount,FungalCount,CorrectedCount,ColoniesObserved,ResultCFU,Status,ResultCalculationVersion,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot) SELECT 3,EventId,Method,N'PM-3',3,SampleSite,PlanSampleID,TotalCount,FungalCount,CorrectedCount,ColoniesObserved,ResultCFU,Status,ResultCalculationVersion,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot FROM dbo.EM_EventPlates WHERE Id=1",
            "DELETE dbo.EM_EventPlates WHERE Id=2"
        };
        int oldAccepted=0,newRejected=0;
        foreach(bool corrected in new[]{false,true})foreach(string action in new[]{"ReviewEMEvent","ApproveEMEvent"})
        {
            string status=action=="ReviewEMEvent"?"Under Review":"Reviewed";
            foreach(string mutation in mutations)
            {
                Reset(status);DataTable loaded=Capture();if(loaded.Rows.Count!=2)throw new Exception("Expected complete multi-plate snapshot.");
                Sql(mutation);writes=0;bool rejected=Invoke(corrected,action,loaded);
                if(corrected){if(!rejected||writes!=0)throw new Exception("Stale site EM evidence reached native SQL writes: "+action);newRejected++;}
                else{if(rejected||writes!=2)throw new Exception("Frozen site method did not reproduce exact SQL gap: "+action);oldAccepted++;}
                VerifyRollback();
            }
            Reset(status);writes=0;testLocks=corrected;
            if(Invoke(corrected,action,Capture())||writes!=2)throw new Exception("Unchanged site EM SQL probe rejected.");
            testLocks=false;VerifyRollback();
            if(corrected){Reset(status);writes=0;if(!Invoke(true,action,null)||writes!=0)throw new Exception("Missing site EM snapshot accepted.");VerifyRollback();}
        }
        if(oldAccepted!=22||newRejected!=22||locks!=4)throw new Exception("Incomplete native site EM matrix.");
        Console.WriteLine("PASS SITE EM SQL: frozen accepted 22/22 changed-evidence cases; corrected rejected 22/22 before writes; four unchanged and two missing-snapshot controls; four competing-child lock checks. Exact backend methods and site SELECT; authentication, source-plan, status and audit adapters are not site UAT.");

        void VerifyRollback()
        {
            using var c=new SqlConnection(cs);c.Open();using var cmd=new SqlCommand("SELECT (SELECT COUNT(*) FROM dbo.EM_EventSignatures)+(SELECT COUNT(*) FROM dbo.SiteEmTestAudit)",c);
            if((int)cmd.ExecuteScalar()! !=0)throw new Exception("Probe signature/audit adapter writes were not rolled back.");
            using var state=new SqlCommand("SELECT WorkflowStatus FROM dbo.EM_Events WHERE Id=1",c);
            if(!Equals(state.ExecuteScalar(),BoundaryBase.CurrentStatus))throw new Exception("Probe transition was not rolled back.");
        }
    }

    private static string Method(string source,string signature)
    {
        string code=Regex.Replace(source,"@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",m=>new string(' ',m.Length));
        int start=code.IndexOf(signature,StringComparison.Ordinal);if(start<0)throw new Exception("Missing method: "+signature);
        int begin=code.IndexOf('{',start),depth=1,end=begin+1;
        while(depth!=0&&end<code.Length){depth+=code[end]=='{'?1:code[end]=='}'?-1:0;end++;}
        if(depth!=0)throw new Exception("Unbalanced source method.");return source[start..end];
    }

    private static void VerifySources(string root)
    {
        string Site(string name)=>File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-"+name+".txt")).Replace("\r\n","\n");
        string backend=Site("DatabaseHelper.EnvironmentalMonitoring.cs"),window=Site("EMResultsEntry.xaml.cs"),part=Site("EMResultsEntry.xaml.Part3.cs");
        foreach(var action in new[]{("ReviewEMEvent",Corrected.ReviewEMEventSource,Frozen.ReviewEMEventSource,"BtnReview_Click"),("ApproveEMEvent",Corrected.ApproveEMEventSource,Frozen.ApproveEMEventSource,"BtnApprove_Click")})
        {
            if(Method(backend,"public static void "+action.Item1+"(")!=action.Item2)throw new Exception("Compiled method differs from corrected site artifact.");
            using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-EM-workflow-freshness-20261030.json")));
            if(fixture.RootElement.GetProperty("Methods").GetProperty(action.Item1).GetString()!=action.Item3)throw new Exception("Frozen method drift.");
            string ui=Method(window,"private async void "+action.Item4+"(");
            int capture=ui.IndexOf("DataTable original = CaptureEmWorkflowEvidence(signedEventId)",StringComparison.Ordinal);
            if(capture<0||capture>ui.IndexOf("await PerformEMWorkflowActionAsync",StringComparison.Ordinal)||
                !ui.Contains("DatabaseHelper."+action.Item1+"(signedEventId, signedEventNo, signedBy, meaning, reason, original)",StringComparison.Ordinal))
                throw new Exception("Site WPF handler does not pass its pre-signature snapshot and captured identity.");
        }
        string compiled=UiCaptureSource;
        if(Method(part,"private DataTable CaptureEmWorkflowEvidence(")!=compiled)throw new Exception("Compiled UI capture differs from corrected artifact.");
        using var frozen=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-EM-workflow-freshness-20261030.json")));
        string snapshot=Site("DatabaseHelper.EmSourcePlan.cs");
        string methods=Method(snapshot,"internal static string GetEmPlateSnapshotSql(")+"\n"+Method(snapshot,"internal static DataTable ReadEmPlateSnapshotInTransaction(");
        if(methods!=frozen.RootElement.GetProperty("SnapshotMethods").GetString())throw new Exception("Tested site snapshot SQL differs.");
    }
}
