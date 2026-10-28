using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
string project=Path.GetFullPath(args[0]);
using var settings=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS") ?? throw new Exception("Explicit disposable SQL settings required")));
var master=settings.RootElement.TryGetProperty("MasterConnectionString",out var m) ? new SqlConnectionStringBuilder(m.GetString()) : new SqlConnectionStringBuilder {DataSource=settings.RootElement.GetProperty("server").GetString(),InitialCatalog="master",UserID="sa",Password=settings.RootElement.GetProperty("password").GetString(),Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=false,Pooling=false};
string db="EQ001_UAT_PrmReview_"+Guid.NewGuid().ToString("N")[..10];
async Task<object?> Exec(string cs,string sql) {await using var c=new SqlConnection(cs);await c.OpenAsync();using var cmd=new SqlCommand(sql,c);return await cmd.ExecuteScalarAsync();}
var connection=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=db};
int failures=0;
void Check(bool ok,string message) { Console.WriteLine((ok?"PASS ":"FAIL ")+message); if(!ok)failures++; }
try {
 await Exec(master.ConnectionString,$"CREATE DATABASE [{db}]");
 await Exec(connection.ConnectionString,"""
CREATE TABLE dbo.QualityEvents(QualityEventID int PRIMARY KEY,SourceModule nvarchar(40),SourceRecordID int,CurrentStatus nvarchar(40),ClosedBy nvarchar(120),ClosedDate datetime2,RootCauseCategory nvarchar(120),RootCauseDetails nvarchar(max),ImpactAssessment nvarchar(max),QAConclusion nvarchar(max),FinalDisposition nvarchar(120));
CREATE TABLE dbo.PRM_QualityEventEvidenceReconciliations(LegacyQualityEventID int,ReplacementQualityEventID int,ElectronicSignatureID int,SampleID int,ReconciliationSchemaVersion int,ReconciledBy nvarchar(120),ReconciledAt datetime2);
CREATE TABLE dbo.PRM_ElectronicSignatures(SignatureID int,SampleID int,SignedBy nvarchar(120),ActionType nvarchar(120));
CREATE TABLE dbo.PRM_SampleTests(SampleTestID int PRIMARY KEY,SampleID int,RequiredTest bit,TestName nvarchar(120),ResultValue nvarchar(120),SpecificationText nvarchar(120),SpecificationLimit decimal(18,3),Unit nvarchar(50),Interpretation nvarchar(120));
CREATE TABLE dbo.QualityEventAffectedResults(QualityEventID int,SourceModule nvarchar(40),SourceResultID int,EvidenceSchemaVersion int,TestName nvarchar(120),ResultValue nvarchar(120),SpecificationLimit nvarchar(120),SpecificationNumericLimit decimal(18,3),Unit nvarchar(50),FailureType nvarchar(120));
INSERT dbo.QualityEvents VALUES(1,N'PRM',1,N'Closed',N'qa',SYSDATETIME(),N'Laboratory',N'Cause',N'Impact',N'Conclusion',N'Confirmed OOS - Original Result Retained');
INSERT dbo.PRM_SampleTests VALUES(1,1,1,N'Required',N'20',N'NMT 10',10,N'CFU',N'Does Not Conform'),(2,1,0,N'Optional',N'30',N'NMT 10',10,N'CFU',N'Does Not Conform');
INSERT dbo.QualityEventAffectedResults SELECT 1,N'PRM',SampleTestID,1,TestName,ResultValue,SpecificationText,SpecificationLimit,Unit,Interpretation FROM dbo.PRM_SampleTests WHERE SampleTestID=1;
""");
 string source=File.ReadAllText(Path.Combine(project,"ProductionRawMaterialResults.xaml.cs"));
 string gate=Regex.Match(source,"private const string PrmQualityEventStateSql = @\"(.*?)\";",RegexOptions.Singleline).Groups[1].Value.Replace("\"\"","\"");
 async Task<int> State() => Convert.ToInt32(await Exec(connection.ConnectionString,"DECLARE @SampleID int=1;"+gate));
 Check(await State()==3,"High4: recorded optional failure without investigation coverage blocks release");
 await Exec(connection.ConnectionString,"INSERT dbo.QualityEventAffectedResults SELECT 1,N'PRM',SampleTestID,1,TestName,ResultValue,SpecificationText,SpecificationLimit,Unit,Interpretation FROM dbo.PRM_SampleTests WHERE SampleTestID=2;");
 Check(await State()==0,"High4: complete required and optional failure evidence allows release");
 await Exec(connection.ConnectionString,"DELETE dbo.QualityEventAffectedResults WHERE SourceResultID=2; UPDATE dbo.PRM_SampleTests SET ResultValue=N' ',Interpretation=N'Check Required' WHERE SampleTestID=2;");
 Check(await State()==0,"High4: optional blank/unperformed test does not require investigation");
 await Exec(connection.ConnectionString,"UPDATE dbo.PRM_SampleTests SET ResultValue=N'30',Interpretation=N'Check Required' WHERE SampleTestID=2;");
 Check(await State()==3,"High4: performed optional Check Required test requires investigation");
 string investigation=File.ReadAllText(Path.Combine(project,"PRMQualityEventInvestigation.xaml.cs"));
 string closureGate=Regex.Match(investigation,"object missingCoverage = ExecuteScalarInTransaction\\(connection, transaction, @\"(.*?)\",",RegexOptions.Singleline).Groups[1].Value.Replace("\"\"","\"");
 if(string.IsNullOrWhiteSpace(closureGate)) throw new Exception("PRM closure coverage SQL was not found");
 async Task<int> MissingCoverage(string sql) => Convert.ToInt32(await Exec(connection.ConnectionString,"DECLARE @SampleID int=1,@ReplacementQualityEventID int=1;"+sql));
 string oldClosureGate=closureGate.Replace("(ISNULL(st.RequiredTest,1)=1 OR NULLIF(LTRIM(RTRIM(ISNULL(st.ResultValue,N''))),N'') IS NOT NULL)","ISNULL(st.RequiredTest,1)=1");
 Check(await MissingCoverage(oldClosureGate)==0 && await MissingCoverage(closureGate)==1,"Closure RED/GREEN: old predicate omitted performed optional Check Required; current predicate blocks closure");
 await Exec(connection.ConnectionString,"UPDATE dbo.PRM_SampleTests SET Interpretation=N'Does Not Conform' WHERE SampleTestID=2;");
 Check(await MissingCoverage(oldClosureGate)==0 && await MissingCoverage(closureGate)==1,"Closure RED/GREEN: old predicate omitted performed optional failure; current predicate blocks closure");
 await Exec(connection.ConnectionString,"INSERT dbo.QualityEventAffectedResults SELECT 1,N'PRM',SampleTestID,1,TestName,ResultValue,SpecificationText,SpecificationLimit,Unit,Interpretation FROM dbo.PRM_SampleTests WHERE SampleTestID=2;");
 Check(await MissingCoverage(closureGate)==0,"Closure accepts complete required and optional failure evidence");
 await Exec(connection.ConnectionString,"DELETE dbo.QualityEventAffectedResults WHERE SourceResultID=2; UPDATE dbo.PRM_SampleTests SET ResultValue=N' ',Interpretation=N'Check Required' WHERE SampleTestID=2;");
 Check(await MissingCoverage(closureGate)==0,"Closure excludes blank/unperformed optional tests from investigation coverage");
 string sample=File.ReadAllText(Path.Combine(project,"ProductionRawMaterialSamples.xaml.cs"));
 Check(sample.Contains("SpecificationEvidenceService.Record(") && sample.Contains("SpecificationEvidenceService.EnsureReviewIndependent("),"Base specification: draft history and all-editor independence are wired");
 Check(sample.Contains("EnsureSpecificationViewSaved();") && sample.Contains("ResultSnapshotGuard.EnsureMatches(_loadedSpecificationSnapshot, lockedContent"),"Base specification: exact visible and persisted content checked before signing");
 await Exec(connection.ConnectionString,"""
CREATE TABLE dbo.PRM_SpecificationTests(SpecificationTestID int PRIMARY KEY,SpecificationNo nvarchar(120),SampleCategory nvarchar(40),VersionNo int,CreatedBy nvarchar(120),SpecificationLimit decimal(18,3),SpecificationText nvarchar(500),ApprovalStatus nvarchar(40));
CREATE TABLE dbo.PRM_SpecificationSignatures(SignatureID bigint PRIMARY KEY);
INSERT dbo.PRM_SpecificationTests VALUES(1,N'SPEC',N'Raw Material',1,N'author',10,N'NMT 10',N'Draft');
""");
 string migration=File.ReadAllText(Path.Combine(project,"Database/Migrations/20261028_001_PRM_Specification_Content_History.sql"));
 await Exec(connection.ConnectionString,migration);
 await Exec(connection.ConnectionString,migration);
 await Exec(connection.ConnectionString,File.ReadAllText(Path.Combine(project,"Database/Verification/20261028_PRM_Specification_Content_History_Verification.sql")));
 Check(Convert.ToInt32(await Exec(connection.ConnectionString,"SELECT COUNT(*) FROM dbo.PRM_SpecificationTests"))==1,"History migration is repeatable and preserves existing specification rows");
 await using(var c=new SqlConnection(connection.ConnectionString)) {
  await c.OpenAsync(); using var t=c.BeginTransaction();
  Check(!SpecificationEvidenceService.HasControlledCreation(c,t,"SPEC","Raw Material",1),"Legacy draft has no fabricated content attribution");
  SpecificationEvidenceService.Record(c,t,"SPEC","Raw Material",1,"Draft Created","[]","author","Regression creation");
  string before=SpecificationEvidenceService.Json(c,t,"SPEC","Raw Material",1);
  using(var update=new SqlCommand("UPDATE dbo.PRM_SpecificationTests SET SpecificationText=N'NMT 9',SpecificationLimit=9",c,t)) update.ExecuteNonQuery();
  SpecificationEvidenceService.Record(c,t,"SPEC","Raw Material",1,"Draft Updated",before,"editor","Regression edit");
  foreach(string signer in new[]{" AUTHOR "," EDITOR "}) {
   bool rejected=false; try { SpecificationEvidenceService.EnsureReviewIndependent(c,t,"SPEC","Raw Material",1,signer); }
   catch(SqlException ex) when(ex.Number==56462) {rejected=true;}
   Check(rejected,"Every draft author/editor is excluded from review: "+signer.Trim());
  }
  SpecificationEvidenceService.EnsureReviewIndependent(c,t,"SPEC","Raw Material",1,"reviewer");
  Check(true,"Independent reviewer is accepted");
  DataTable snapshot=SpecificationEvidenceService.Load(c,t,"SPEC","Raw Material",1);
  ResultSnapshotGuard.EnsureMatches(snapshot,SpecificationEvidenceService.Load(c,t,"SPEC","Raw Material",1),"SpecificationTestID","VersionNo",1);
  using(var update=new SqlCommand("UPDATE dbo.PRM_SpecificationTests SET SpecificationLimit=8",c,t)) update.ExecuteNonQuery();
  bool stale=false; try {ResultSnapshotGuard.EnsureMatches(snapshot,SpecificationEvidenceService.Load(c,t,"SPEC","Raw Material",1),"SpecificationTestID","VersionNo",1);} catch(DBConcurrencyException) {stale=true;}
  Check(stale,"Changed persisted numeric limit invalidates the loaded signing snapshot");
  t.Commit();
 }
 await using(var c=new SqlConnection(connection.ConnectionString)) {
  await c.OpenAsync();using var cmd=new SqlCommand("SELECT NewRowsJson,ContentHash,OldRowsJson FROM dbo.PRM_SpecificationContentHistory ORDER BY HistoryID",c);
  using var reader=await cmd.ExecuteReaderAsync(); await reader.ReadAsync(); string created=reader.GetString(0);
  Check(SHA256.HashData(Encoding.UTF8.GetBytes(created)).SequenceEqual((byte[])reader[1]),"History content hash binds the exact stored JSON");
  await reader.ReadAsync(); Check(reader.GetString(2)==created && reader.GetString(0).Contains("NMT 9"),"Draft update retains exact before and after content");
 }
 foreach(string mutation in new[]{"UPDATE dbo.PRM_SpecificationContentHistory SET ChangeReason=N'forged'","DELETE dbo.PRM_SpecificationContentHistory"}) {
  bool denied=false;try {await Exec(connection.ConnectionString,mutation);} catch(SqlException ex) when(ex.Number==56456) {denied=true;}
  Check(denied,"Content history rejects "+mutation.Split(' ')[0]);
 }
 SpecificationNumericStorage.EnsureExact(999999999999999.999m); SpecificationNumericStorage.EnsureExact(null);
 foreach(decimal invalid in new[]{0.0001m,1000000000000000m,-1m}) {
  bool denied=false;try {SpecificationNumericStorage.EnsureExact(invalid);}catch(InvalidOperationException){denied=true;}
  Check(denied,"Numeric limits reject unrepresentable or negative value "+invalid);
 }

} finally {await Exec(master.ConnectionString,$"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END");}
if(failures>0)throw new Exception($"{failures} PRM regressions failed");
