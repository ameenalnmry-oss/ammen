using Microsoft.Data.SqlClient;
using System.Data;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

// Runs the production UPDATE text and (when wired at its WPF call site) the linked production guard.
// This portable SQL harness does not instantiate WPF or authenticate an electronic signature.
string project = Path.GetFullPath(args[0]);
using var settings = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS")!));
var master = settings.RootElement.TryGetProperty("MasterConnectionString", out var m)
    ? new SqlConnectionStringBuilder(m.GetString())
    : new SqlConnectionStringBuilder { DataSource=settings.RootElement.GetProperty("server").GetString(), InitialCatalog="master", UserID="sa", Password=settings.RootElement.GetProperty("password").GetString(), Encrypt=SqlConnectionEncryptOption.Mandatory, TrustServerCertificate=false, Pooling=false };
string db = "EQ001_UAT_Investigation_" + Guid.NewGuid().ToString("N")[..12];
string cs = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog=db }.ConnectionString;
void Sql(string connection, string sql) { using var c=new SqlConnection(connection); c.Open(); using var q=new SqlCommand(sql,c); q.ExecuteNonQuery(); }
Type? guard=Assembly.GetExecutingAssembly().GetType("PharmaLIMS.Infrastructure.QualityEventInvestigationConcurrencyGuard");
object? Invoke(string name, params object?[] p) { try { return guard?.GetMethod(name,BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(null,p); } catch(TargetInvocationException e) { throw e.InnerException!; } }
object? Capture(bool prm) { using var c=new SqlConnection(cs); c.Open(); using var t=c.BeginTransaction(); return Invoke("Capture",c,t,1,prm); }
string Method(string file,string method) { string s=File.ReadAllText(Path.Combine(project,file)); int start=s.IndexOf(method,StringComparison.Ordinal); int end=s.IndexOf("\n        private ",start+method.Length,StringComparison.Ordinal); return s[start..(end<0?s.Length:end)]; }
string generic=Method("QualityEventInvestigation.xaml.cs","private bool SaveInvestigation(string status)");
string genericHeader=Method("QualityEventInvestigation.xaml.cs","private void SaveInvestigationHeaderInTransaction");
string prmSave=Method("PRMQualityEventInvestigation.xaml.cs","private bool SaveInvestigation(string status)");
string prmSubmit=Method("PRMQualityEventInvestigation.xaml.cs","private void SubmitInvestigationToQa");
string prmClose=Method("PRMQualityEventInvestigation.xaml.cs","private void BtnCloseInvestigation_Click");
string helper=File.ReadAllText(Path.Combine(project,"DatabaseHelper.QualityEvents.cs"));
int h=helper.IndexOf("public static void SaveQualityEventInvestigation(\n            SqlConnection",StringComparison.Ordinal);
string updatePrm=Regex.Match(helper[h..],"ExecuteNonQueryWithTransaction\\(@\"([\\s\\S]*?)\",").Groups[1].Value;
string PublicMethod(string signature) {
 int start=helper.IndexOf(signature,StringComparison.Ordinal);
 int end=helper.IndexOf("\n        public static ",start+signature.Length,StringComparison.Ordinal);
 return helper[start..(end<0?helper.Length:end)];
}
string genericSubmitHelper=PublicMethod("public static void SubmitQualityEventToQA(");
string genericCloseHelper=PublicMethod("public static void CloseQualityEvent(");
string genericSubmitWindow=Method("QualityEventInvestigation.xaml.cs","private async void BtnSubmitQA_Click");
string genericCloseWindow=Method("QualityEventInvestigation.xaml.cs","private async void BtnCloseEvent_Click");
string FinalUpdate(string method) => Regex.Matches(method,"ExecuteNonQueryWithTransaction\\(@\"([\\s\\S]*?)\",").Select(x=>x.Groups[1].Value).First(sql=>sql.Contains("UPDATE dbo.QualityEvents",StringComparison.Ordinal));
string updateGeneric=Regex.Match(genericHeader,"ExecuteNonQueryWithTransaction\\(@\"([\\s\\S]*?)\",").Groups[1].Value;
var failures=new List<string>(); int checks=0;
void Check(bool condition,string label) { checks++; Console.WriteLine((condition?"PASS ":"FAIL ")+label); if(!condition)failures.Add(label); }
try {
 Sql(master.ConnectionString,$"CREATE DATABASE [{db}]");
 Sql(cs,@"CREATE TABLE dbo.QualityEvents(QualityEventID int PRIMARY KEY,SampleID int,SourceRecordID int,SourceModule nvarchar(20),CurrentStatus nvarchar(60),Severity nvarchar(60),InitialDescription nvarchar(max),ImmediateAction nvarchar(max),RootCauseCategory nvarchar(120),RootCauseDetails nvarchar(max),ImpactAssessment nvarchar(max),CAPARequired bit,QAConclusion nvarchar(max),FinalDisposition nvarchar(120),ModifiedBy nvarchar(120),ModifiedDate datetime2,ClosedBy nvarchar(120),ClosedDate datetime2);
CREATE TABLE dbo.QualityEventChecklistAnswers(QualityEventID int,QuestionID int,Comments nvarchar(max));
CREATE TABLE dbo.QualityEventAffectedResults(QualityEventID int,ResultID int,ResultText nvarchar(max));
CREATE TABLE dbo.QualityEventActions(QualityEventID int,ActionDescription nvarchar(max));
CREATE TABLE dbo.QualityEventSignatures(QualityEventID int,SignatureID int,SignedBy nvarchar(120));
CREATE TABLE dbo.PRM_QualityEventEvidenceReconciliations(ReplacementQualityEventID int,LegacyQualityEventID int,ElectronicSignatureID int);
CREATE TABLE dbo.PRM_SampleTests(SampleID int,TestID int,ResultValue nvarchar(max));
CREATE TABLE dbo.PRM_Samples(SampleID int PRIMARY KEY,SampleNumber nvarchar(80));
CREATE TABLE dbo.PRM_ElectronicSignatures(SampleID int,ActionType nvarchar(100),SignedBy nvarchar(120));
INSERT dbo.QualityEvents(QualityEventID,SampleID,SourceRecordID,SourceModule,CurrentStatus,InitialDescription,ModifiedDate) VALUES(1,7,7,N'PRM',N'QA Review',N'loaded description',SYSDATETIME());
INSERT dbo.QualityEventChecklistAnswers VALUES(1,1,N'loaded checklist'); INSERT dbo.QualityEventAffectedResults VALUES(1,10,N'loaded evidence'); INSERT dbo.QualityEventActions VALUES(1,N'loaded action');
INSERT dbo.PRM_QualityEventEvidenceReconciliations VALUES(1,2,10);
INSERT dbo.PRM_Samples VALUES(7,N'S-7'); INSERT dbo.PRM_SampleTests VALUES(7,10,N'loaded result'); INSERT dbo.PRM_ElectronicSignatures VALUES(7,N'Historical',N'original signer');");
 void Scenario(string label,bool prm,string method,string mutation,bool expectReject=true,string? actualUpdate=null,string initialStatus="QA Review") {
  Sql(cs,"UPDATE dbo.QualityEvents SET CurrentStatus=N'"+initialStatus+"',ClosedBy=NULL,ClosedDate=NULL");
  object? loaded=Capture(prm);
  Sql(cs,mutation);
  bool rejected=false;
  using(var c=new SqlConnection(cs)) { c.Open(); using var t=c.BeginTransaction();
   try {
    if(method.Contains("QualityEventInvestigationConcurrencyGuard.EnsureUnchanged",StringComparison.Ordinal)) Invoke("EnsureUnchanged",c,t,1,prm,loaded);
    using var cmd=new SqlCommand(actualUpdate ?? (prm?updatePrm:updateGeneric),c,t);
    foreach(string p in Regex.Matches(cmd.CommandText,"@[A-Za-z0-9_]+").Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase)) {
     object value=p.Equals("@QualityEventID",StringComparison.OrdinalIgnoreCase)?1:p.Equals("@CAPARequired",StringComparison.OrdinalIgnoreCase)?false:p.Equals("@CurrentStatus",StringComparison.OrdinalIgnoreCase)?"QA Review":"stale window value";
     cmd.Parameters.AddWithValue(p,value);
    }
    cmd.ExecuteNonQuery();
   } catch(DBConcurrencyException) { rejected=true; } catch(InvalidOperationException) { rejected=true; }
   t.Rollback();
  }
  Check(rejected==expectReject,label);
 }
 Scenario("generic stale Save cannot reopen closed signed event",false,generic,"UPDATE dbo.QualityEvents SET CurrentStatus=N'Closed',ClosedBy=N'QA signer',ClosedDate=SYSDATETIME()");
 Scenario("generic Save rejects changed content at unchanged status",false,generic,"UPDATE dbo.QualityEvents SET InitialDescription=N'new author content'");
 foreach(var action in new[]{("Save",prmSave),("Submit",prmSubmit),("Close",prmClose)}) {
  Scenario("PRM "+action.Item1+" rejects stale header",true,action.Item2,"UPDATE dbo.QualityEvents SET QAConclusion=CONVERT(nvarchar(36),NEWID())");
  Scenario("PRM "+action.Item1+" rejects stale checklist",true,action.Item2,"UPDATE dbo.QualityEventChecklistAnswers SET Comments=CONVERT(nvarchar(36),NEWID())");
  Scenario("PRM "+action.Item1+" rejects changed affected evidence",true,action.Item2,"UPDATE dbo.QualityEventAffectedResults SET ResultText=CONVERT(nvarchar(36),NEWID())");
  Scenario("PRM "+action.Item1+" rejects changed current result",true,action.Item2,"UPDATE dbo.PRM_SampleTests SET ResultValue=CONVERT(nvarchar(36),NEWID())");
 }
 // Capture is the draft persisted before a dialog/validation callback; mutation models a
 // second editor during that callback. The final helper must compare that original token.
 foreach(var action in new[]{("Submit",genericSubmitHelper,genericSubmitWindow,"Open"),("Close",genericCloseHelper,genericCloseWindow,"QA Review")}) {
  bool forwards=action.Item3.Contains("loadedInvestigationVersion",StringComparison.Ordinal)
    && action.Item2.Contains("expectedInvestigationVersion",StringComparison.Ordinal);
  string gate=forwards?action.Item2:"";
  Scenario("generic "+action.Item1+" callback rejects concurrent header edit",false,gate,"UPDATE dbo.QualityEvents SET RootCauseDetails=CONVERT(nvarchar(36),NEWID())",true,FinalUpdate(action.Item2),action.Item4);
  Scenario("generic "+action.Item1+" callback rejects concurrent checklist edit",false,gate,"UPDATE dbo.QualityEventChecklistAnswers SET Comments=CONVERT(nvarchar(36),NEWID())",true,FinalUpdate(action.Item2),action.Item4);
  Scenario("generic "+action.Item1+" callback rejects concurrent affected-evidence edit",false,gate,"UPDATE dbo.QualityEventAffectedResults SET ResultText=CONVERT(nvarchar(36),NEWID())",true,FinalUpdate(action.Item2),action.Item4);
  Scenario("generic "+action.Item1+" callback accepts unchanged content",false,gate,"SELECT 1",false,FinalUpdate(action.Item2),action.Item4);
 }
 Scenario("generic Save rejects changed closure signature evidence",false,generic,"INSERT dbo.QualityEventSignatures VALUES(1,99,N'QA signer')");
 Scenario("generic Save rejects existing closure signature despite stale open status",false,generic,"SELECT 1");
 Sql(cs,"DELETE dbo.QualityEventSignatures WHERE SignatureID=99");
 foreach(var action in new[]{("Save",prmSave),("Submit",prmSubmit),("Close",prmClose)}) {
  Scenario("PRM "+action.Item1+" rejects changed signatures",true,action.Item2,"INSERT dbo.PRM_ElectronicSignatures VALUES(7,N'Historical',CONVERT(nvarchar(36),NEWID()))");
  Scenario("PRM "+action.Item1+" rejects changed reconciliation evidence",true,action.Item2,"UPDATE dbo.PRM_QualityEventEvidenceReconciliations SET ElectronicSignatureID=ElectronicSignatureID+1");
  Scenario("PRM "+action.Item1+" concurrency gate accepts unchanged content",true,action.Item2,"SELECT 1",false);
 }
 Scenario("fresh generic draft remains saveable",false,generic,"SELECT 1",false);
 Scenario("fresh PRM draft remains saveable",true,prmSave,"SELECT 1",false);
 using(var c=new SqlConnection(cs)) { c.Open(); using var q=new SqlCommand("SELECT COUNT(*) FROM dbo.PRM_ElectronicSignatures WHERE SignedBy=N'original signer'",c); Check((int)q.ExecuteScalar()! == 1,"historical signature preserved"); }
 if(guard != null) {
  using var owner=new SqlConnection(cs); owner.Open(); using var tx=owner.BeginTransaction();
  var loaded=Invoke("Capture",owner,tx,1,true);
  Invoke("EnsureUnchanged",owner,tx,1,true,loaded);
  foreach(var mutation in new[]{"UPDATE dbo.QualityEvents SET QAConclusion=N'competing write' WHERE QualityEventID=1", "UPDATE dbo.QualityEventChecklistAnswers SET Comments=N'competing write' WHERE QualityEventID=1", "INSERT dbo.QualityEventAffectedResults VALUES(1,11,N'phantom evidence')", "UPDATE dbo.PRM_SampleTests SET ResultValue=N'competing write' WHERE SampleID=7"}) {
   bool blocked=false;
   try { Sql(cs,"SET LOCK_TIMEOUT 500; "+mutation); } catch(SqlException e) when(e.Number==1222) { blocked=true; }
   Check(blocked,"retained locks block "+mutation.Split(' ')[0]+" on "+mutation.Split(' ')[1]);
  }
  tx.Rollback();
 } else { Check(false,"retained locks protect full evidence until transaction ends"); }
 SitePrmInvestigationCases.RunFrozen(cs,project);
 SitePrmInvestigationCases.RunCorrected(cs,project);
 Console.WriteLine($"{checks-failures.Count}/{checks} checks passed");
 if(failures.Count>0) Environment.ExitCode=1;
} finally { Sql(master.ConnectionString,$"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END"); }
