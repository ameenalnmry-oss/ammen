using Microsoft.Data.SqlClient;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
// Disposable SQL exercises linked production fingerprint/identity methods. Source-boundary checks
// make baseline runs execute the former permissive boundary when the new method is absent.
string root=Path.GetFullPath(args[0]);
using var settings=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS")!));
var data=settings.RootElement;
var master=data.TryGetProperty("MasterConnectionString",out var mc)?new SqlConnectionStringBuilder(mc.GetString()):new SqlConnectionStringBuilder { DataSource=data.GetProperty("server").GetString(),InitialCatalog="master",UserID="sa",Password=data.GetProperty("password").GetString(),Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=false,Pooling=false };
string db="EQ001_UAT_EM_"+Guid.NewGuid().ToString("N")[..12];
string cs=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=db}.ConnectionString;
void Sql(string connection,string sql) {using var c=new SqlConnection(connection);c.Open();using var q=new SqlCommand(sql,c);q.ExecuteNonQuery();}
Type helper=typeof(PharmaLIMS.DatabaseHelper);
object? Invoke(string name,params object[] arguments) {try{return helper.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)?.Invoke(null,arguments);}catch(TargetInvocationException e){throw e.InnerException!;}}
object? Call(string name, params object[] args) {using var c=new SqlConnection(cs);c.Open();using var t=c.BeginTransaction();object? result=Invoke(name,new object[]{c,t}.Concat(args).ToArray());t.Commit();return result;}
int failures=0,checks=0;
void Check(string name,Action action) {checks++;try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
void Reject(Action action){try{action();}catch(InvalidOperationException){return;}catch(SqlException){return;}throw new Exception("unsafe operation accepted");}
string em=File.ReadAllText(Path.Combine(root,"DatabaseHelper.EnvironmentalMonitoring.cs"));
string planning=File.ReadAllText(Path.Combine(root,"EMPlanning.xaml.cs"));
try {
Sql(master.ConnectionString,$"CREATE DATABASE [{db}]");
Sql(cs,@"
CREATE TABLE dbo.EM_Plans(PlanID int PRIMARY KEY,Status nvarchar(40));
CREATE TABLE dbo.EM_Events(Id int PRIMARY KEY,PlanID int,EventNo nvarchar(50),AreaId int,AreaCodeSnapshot nvarchar(100),AreaNameSnapshot nvarchar(200),GradeSnapshot nvarchar(50),EmployeeId nvarchar(100),EmployeeName nvarchar(200),EmployeeDepartment nvarchar(100),EmployeeShift nvarchar(100));
CREATE TABLE dbo.EM_PlanSamples(PlanSampleID int PRIMARY KEY,PlanID int,EventID int,EmployeeID nvarchar(100),EmployeeName nvarchar(200));
CREATE TABLE dbo.EM_EventPlates(Id int PRIMARY KEY,EventId int,Method nvarchar(100),PlateCode nvarchar(100),SequenceNo int,SampleSite nvarchar(200),PlanSampleID int,TotalCount decimal(18,3),FungalCount decimal(18,3),CorrectedCount decimal(18,3),ColoniesObserved nvarchar(500),ResultCFU decimal(18,3),Status nvarchar(50),ResultCalculationVersion smallint,AlertLimitSnapshot decimal(18,6),ActionLimitSnapshot decimal(18,6),ResultUnitSnapshot nvarchar(100),AirVolumeLitersSnapshot decimal(18,6));
CREATE TABLE dbo.EM_LimitSnapshotReconciliations(ReconciliationID int,PlateID int,AlertLimitSnapshot decimal(18,6),ActionLimitSnapshot decimal(18,6),ResultUnitSnapshot nvarchar(100),AirVolumeLitersSnapshot decimal(18,6),SignedBy nvarchar(100),SignedAt datetime2,MeaningOfSignature nvarchar(200),EvidenceReference nvarchar(200),ReconciliationSchemaVersion int);
CREATE TABLE dbo.LabEquipmentUsage(UsageID int IDENTITY PRIMARY KEY,Module nvarchar(20),ParentRecordID int,ResultRecordID int,EquipmentID int,AssignedBy nvarchar(100),AssignedAt datetime2(0),UNIQUE(Module,ResultRecordID));
CREATE TABLE dbo.LabEquipmentUsageHistory(HistoryID bigint IDENTITY PRIMARY KEY,Module nvarchar(20),ParentRecordID int,ResultRecordID int,PreviousEquipmentID int,EquipmentID int,EquipmentCodeSnapshot nvarchar(50),EquipmentNameSnapshot nvarchar(200),EquipmentTypeSnapshot nvarchar(100),EquipmentStatusSnapshot nvarchar(30),QualificationStatusSnapshot nvarchar(30),CalibrationStatusSnapshot nvarchar(30),NextQualificationDateSnapshot date,NextCalibrationDateSnapshot date,ChangeType nvarchar(20),MeaningOfSignature nvarchar(250),ActionReason nvarchar(500),SignedBy nvarchar(100),UserRole nvarchar(100),SignedAt datetime2(0),SourceWorkstation nvarchar(100));
CREATE TABLE dbo.QualityEvents(QualityEventID int PRIMARY KEY,SourceRecordID int,SampleNumber nvarchar(50),SourceModule nvarchar(100),DetectionSource nvarchar(100),CurrentStatus nvarchar(50));
CREATE TABLE dbo.QualityEventActions(QualityEventActionID int IDENTITY PRIMARY KEY,QualityEventID int,ActionType nvarchar(100),ActionDescription nvarchar(max),PerformedBy nvarchar(100),PerformedDate datetime2,FOREIGN KEY(QualityEventID) REFERENCES dbo.QualityEvents(QualityEventID));
INSERT dbo.LabEquipmentUsage VALUES(N'EM',1,1,1,N'analyst',SYSDATETIME());
INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,EquipmentID,EquipmentCodeSnapshot,ChangeType,MeaningOfSignature,ActionReason,SignedBy,SignedAt) VALUES(N'EM',1,1,1,N'INC-1',N'ASSIGN',N'Assign',N'Initial assignment',N'analyst',SYSDATETIME());
INSERT dbo.EM_Plans VALUES(1,N'Ready for Results');
INSERT dbo.EM_Events VALUES(1,1,N'EM-1',1,N'A',N'Area',N'B',N'E1',N'Alice',N'Lab',N'Day');
INSERT dbo.EM_PlanSamples VALUES(1,1,1,N'E1',N'Alice');
INSERT dbo.EM_EventPlates VALUES(1,1,N'Personnel Monitoring',N'PM-1',1,N'Glove',1,10,0,10,N'observed',10,N'OOS',1,2,5,N'CFU/plate',NULL);
INSERT dbo.QualityEvents VALUES(1,1,N'EM-1',N'EM',N'Environmental Monitoring',N'Closed');
INSERT dbo.QualityEventActions VALUES(1,N'Historical decision',N'Historical signed evidence',N'QA',SYSDATETIME());");
Call("CaptureEMInvestigationEvidenceInTransaction",1,1,"original");
bool Current()=> (bool?)Call("HasCurrentEMInvestigationEvidenceInTransaction",1,1) ?? true;
Check("unchanged result evidence retains closure",()=>{if(!Current())throw new Exception("valid closure rejected");});
Sql(cs,"UPDATE dbo.EM_EventPlates SET TotalCount=20,ResultCFU=N'20'");
Check("changed result cannot reuse closed investigation",()=>{if(Current())throw new Exception("stale closure reused");});
Call("InvalidateEMInvestigationEvidenceInTransaction",1,"EM-1","editor");
Sql(cs,"UPDATE dbo.EM_EventPlates SET TotalCount=10,ResultCFU=N'10'");
Check("reverted result cannot revive invalidated closure",()=>{if(Current())throw new Exception("invalidated closure revived");});
Check("original closure and snapshot retained",()=> {using var c=new SqlConnection(cs);c.Open();using var q=new SqlCommand("SELECT COUNT(*) FROM dbo.QualityEvents WHERE CurrentStatus=N'Closed' AND EXISTS(SELECT 1 FROM dbo.QualityEventActions WHERE ActionType=N'Historical decision')",c);if((int)q.ExecuteScalar()! != 1)throw new Exception("history missing");});
Sql(cs,"INSERT dbo.QualityEvents VALUES(2,1,N'EM-1',N'EM',N'Environmental Monitoring',N'Open')");
Call("CaptureEMInvestigationEvidenceInTransaction",1,2,"fresh");
Sql(cs,"UPDATE dbo.EM_EventPlates SET TotalCount=0,ResultCFU=N'0',Status=N'Pass'");
Check("changed passing result still invalidates earlier decision",()=>{if(Current())throw new Exception("passing result reused old decision");});
Check("approval gate retains investigation requirement after passing amendment",()=>{if(!em.Contains("FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)",StringComparison.Ordinal))throw new Exception("passing result bypasses historical investigation");});
Sql(cs,"UPDATE dbo.EM_EventPlates SET TotalCount=10,ResultCFU=N'10',Status=N'OOS'");
Check("new investigation covers changed evidence",()=>{if((bool?)Call("HasCurrentEMInvestigationEvidenceInTransaction",1,2)==false)throw new Exception("fresh evidence rejected");});
Sql(cs,"UPDATE dbo.EM_Plans SET Status=N'Cancelled'");
foreach(string boundary in new[]{"save","submit","review","approve","print"}) Check("cancelled plan blocks "+boundary,()=>Reject(()=>{
 string source=boundary=="save"?File.ReadAllText(Path.Combine(root,"EMResultsEntry.xaml.Part3.cs")):em;
 string method=boundary switch { "save"=>"private string PersistEmResultsInTransaction", "submit"=>"public static void SubmitEMEventForReview", "review"=>"public static void ReviewEMEvent", "approve"=>"public static void ApproveEMEvent", _=>"public static bool CanPrintEMResultReport" };
 int start=source.IndexOf(method,StringComparison.Ordinal); int end=source.IndexOf("\n        public static",start+method.Length,StringComparison.Ordinal);
 source=source[start..(end<0?source.Length:end)];
 bool wired=source.Contains("EnsureEMParentPlanActiveInTransaction",StringComparison.Ordinal) ||
   (source.Contains("GetLockedEMWorkflowStatusInTransaction",StringComparison.Ordinal) && em[..em.IndexOf("private static",em.IndexOf("GetLockedEMWorkflowStatusInTransaction",StringComparison.Ordinal)+10,StringComparison.Ordinal)].Contains("EnsureEMParentPlanActiveInTransaction",StringComparison.Ordinal));
 if(wired)Call("EnsureEMParentPlanActiveInTransaction",1);
}));
Check("stale report preview rechecks cancelled parent after print dialog",()=>Reject(()=>{
 string source=File.ReadAllText(Path.Combine(root,"EMResultsEntry.xaml.cs"));
 int preview=source.IndexOf("private void ShowEMReportPreview",StringComparison.Ordinal);
 int click=source.IndexOf("printButton.Click",preview,StringComparison.Ordinal);
 int dialog=source.IndexOf("printDialog.ShowDialog()",click,StringComparison.Ordinal);
 int build=source.IndexOf("FlowDocument printDocument = BuildEMResultReportDocument()",dialog,StringComparison.Ordinal);
 if(source[dialog..build].Contains("CanPrintApprovedEMReport",StringComparison.Ordinal))
     Call("EnsureEMParentPlanActiveInTransaction",1);
}));
Sql(cs,"UPDATE dbo.EM_Plans SET Status=N'Completed'");
Check("completed plan remains readable",()=>Call("EnsureEMParentPlanActiveInTransaction",1));
Sql(cs,"UPDATE dbo.EM_Events SET EmployeeName=N'Bob'");
Check("personnel event cannot differ from locked plan identity",()=>Reject(()=>Call("EnsureEMParentPlanActiveInTransaction",1)));
Sql(cs,"UPDATE dbo.EM_Events SET EmployeeName=N'Alice'");
Check("personnel release separates different people",()=>{
 if(!planning.Contains("EmControlledLifecycleGuard.ReleaseGroupKey",StringComparison.Ordinal))throw new Exception("area grouping mixes employees");
 var guard=helper.Assembly.GetType("PharmaLIMS.Services.EmControlledLifecycleGuard")!;
 var method=guard.GetMethod("ReleaseGroupKey",BindingFlags.Static|BindingFlags.NonPublic)!;
 if(Equals(method.Invoke(null,new object[]{1,"Personnel Monitoring","A","Alice"}),method.Invoke(null,new object[]{1,"Personnel Monitoring","B","Bob"})))throw new Exception("people grouped");
});
Check("personnel release copies identity into event used by report",()=>{if(!Regex.IsMatch(planning,@"WorkflowStatus,PlanID,EmployeeId,EmployeeName,CreatedAt"))throw new Exception("event identity omitted");});
foreach(string method in new[]{"SubmitQualityEventToQA","CloseQualityEvent"}) Check("generic "+method+" enforces current EM evidence",()=>{
 string source=File.ReadAllText(Path.Combine(root,"DatabaseHelper.QualityEvents.cs"));int start=source.IndexOf("public static void "+method,StringComparison.Ordinal);int end=source.IndexOf("\n        public static",start+10,StringComparison.Ordinal);source=source[start..(end<0?source.Length:end)];
 Reject(()=> {if(source.Contains("EnsureEMQualityEventEvidenceCurrentInTransaction",StringComparison.Ordinal))Call("EnsureEMQualityEventEvidenceCurrentInTransaction",1);});
});
Sql(cs,"INSERT dbo.QualityEvents VALUES(3,1,N'EM-1',N'EM',N'Environmental Monitoring',N'Closed')");
Call("CaptureEMInvestigationEvidenceInTransaction",1,3,"instrument-reviewer");
bool InstrumentCurrent()=> (bool?)Call("HasCurrentEMInvestigationEvidenceInTransaction",1,3) ?? true;
Check("unchanged instrument traceability retains closure",()=>{if(!InstrumentCurrent())throw new Exception("unchanged instrument rejected");});
Sql(cs,@"UPDATE dbo.LabEquipmentUsage SET EquipmentID=2,AssignedBy=N'editor',AssignedAt=SYSDATETIME() WHERE Module=N'EM' AND ResultRecordID=1;
INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,PreviousEquipmentID,EquipmentID,EquipmentCodeSnapshot,ChangeType,MeaningOfSignature,ActionReason,SignedBy,SignedAt) VALUES(N'EM',1,1,1,2,N'INC-2',N'REASSIGN',N'Reassign',N'Correct instrument',N'editor',SYSDATETIME());");
Check("instrument-only reassignment invalidates closed decision",()=>{if(InstrumentCurrent())throw new Exception("different instrument reused decision");});
Check("equipment-only save reaches append-only invalidation",()=>{string source=File.ReadAllText(Path.Combine(root,"EMResultsEntry.xaml.Part3.cs"));if(!source.Contains("evidenceBeforeSave",StringComparison.Ordinal))throw new Exception("equipment-only save skips invalidation");});
Sql(cs,@"UPDATE dbo.LabEquipmentUsage SET EquipmentID=1 WHERE Module=N'EM' AND ResultRecordID=1;
INSERT dbo.LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,PreviousEquipmentID,EquipmentID,EquipmentCodeSnapshot,ChangeType,MeaningOfSignature,ActionReason,SignedBy,SignedAt) VALUES(N'EM',1,1,2,1,N'INC-1',N'REASSIGN',N'Reassign',N'Restore instrument',N'editor',SYSDATETIME());");
Check("instrument revert cannot revive historical closed decision",()=>{if(InstrumentCurrent())throw new Exception("instrument revert revived closure");});
Call("InvalidateEMInvestigationEvidenceInTransaction",1,"EM-1","editor");
Check("original instrument traceability and decision retained",()=>{using var c=new SqlConnection(cs);c.Open();using var q=new SqlCommand("SELECT COUNT(*) FROM dbo.LabEquipmentUsageHistory WHERE EquipmentCodeSnapshot=N'INC-1' AND ChangeType=N'ASSIGN' AND EXISTS(SELECT 1 FROM dbo.QualityEvents WHERE QualityEventID=3 AND CurrentStatus=N'Closed')",c);if((int)q.ExecuteScalar()! != 1)throw new Exception("original equipment evidence missing");});
SiteEmWorkflowCases.RunNative(cs,root);
SiteEmMembershipCases.Run(cs,root,true);
}finally{Sql(master.ConnectionString,$"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]");}
Console.WriteLine($"{checks-failures}/{checks} passed");return failures==0?0:1;
