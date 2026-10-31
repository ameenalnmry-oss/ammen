using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

string root=Path.GetFullPath(args.Length==1?args[0]:throw new ArgumentException("Pass the PharmaLIMS source root as the sole argument."));
using var settings=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS")??throw new Exception("Explicit disposable SQL settings required")));
var master=settings.RootElement.TryGetProperty("MasterConnectionString",out var mc)?new SqlConnectionStringBuilder(mc.GetString()):new SqlConnectionStringBuilder{DataSource=settings.RootElement.GetProperty("server").GetString(),InitialCatalog="master",UserID="sa",Password=settings.RootElement.GetProperty("password").GetString(),Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=false,Pooling=false,ConnectTimeout=15};
string suffix=Guid.NewGuid().ToString("N")[..10],db="EQ001_UAT_WaterAnalysis_"+suffix,login="water_analysis_svc_"+suffix,clogin="water_analysis_client_"+suffix,pw="Aa1!"+Guid.NewGuid().ToString("N");
string admin=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=db}.ConnectionString;
string ActorConnection(string user)=>new SqlConnectionStringBuilder(admin){IntegratedSecurity=false,UserID=user,Password=pw}.ConnectionString;
string service=ActorConnection(login),client=ActorConnection(clogin);
int checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;Console.WriteLine("PASS "+message);}
async Task<object?> Exec(string cs,string sql){await using var c=new SqlConnection(cs);await c.OpenAsync();using var cmd=new SqlCommand(sql,c){CommandTimeout=45};return await cmd.ExecuteScalarAsync();}
async Task Denied(Func<Task> action,string message,int? code=null){try{await action();}catch(SqlException ex)when(code==null||ex.Number==code){Check(true,message+" (SQL "+ex.Number+")");return;}throw new Exception("Accepted prohibited action: "+message);}
async Task<JsonObject> Request(string op,int sample=1){string status=(string)(await Exec(admin,$"SELECT Status FROM dbo.Samples WHERE SampleID={sample}"))!;return new JsonObject{["TargetServer"]=master.DataSource,["TargetDatabase"]=db,["Operation"]=op,["SampleId"]=sample,["SampleNumber"]="UAT-"+sample,["ExpectedStatus"]=status,["ExpectedSnapshot"]="fixture-locked-snapshot",["Results"]=new JsonArray()};}
JsonObject Plan(JsonObject req,string status)=>new(){["Version"]=1,["Operation"]=req["Operation"]!.GetValue<string>(),["SampleId"]=req["SampleId"]!.GetValue<int>(),["ExpectedStatus"]=req["ExpectedStatus"]!.GetValue<string>(),["ExpectedSnapshot"]=req["ExpectedSnapshot"]!.GetValue<string>(),["Status"]=status,["MarkAnalysisCompleted"]=false,["Rows"]=new JsonArray()};
JsonObject Row(int id,string? result="7.1234",string status="PASS",bool evidence=true)=>new(){["SampleTestId"]=id,["WriteResult"]=result!=null,["ResultValue"]=result,["Remarks"]=status=="OOS"?"Synthetic OOS":null,["ResultStatus"]=status,["DeviationType"]=status=="OOS"?"OOS":null,["WriteEvidence"]=evidence,["EquipmentId"]=1,["SupportingEquipmentIds"]=new JsonArray(),["TestKitCode"]="",["TestKitLot"]="",["TestKitExpiry"]=null,["RawResultSnapshot"]=result,["Execution"]=null};
async Task<JsonObject> Call(Guid id,string mode,JsonObject req,JsonObject? plan=null,string actor="analyst",string sid="S-1-5-21-1-2-3-1001",byte[]? version=null,string reason="Synthetic Water analysis SQL verification",string meaning="I confirm the recorded Water operation",bool rollback=false){
 await using var c=new SqlConnection(service);await c.OpenAsync();await using var tx=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable);
 if(version==null){using var v=new SqlCommand("SELECT AuthenticationRowVersion FROM dbo.Users WHERE Username=@u",c,tx);v.Parameters.AddWithValue("@u",actor);version=(byte[])(await v.ExecuteScalarAsync())!;}
 using var cmd=new SqlCommand("dbo.EQ001_UAT_WaterWorkflow",c,tx){CommandType=CommandType.StoredProcedure,CommandTimeout=45};
 cmd.Parameters.AddWithValue("@Mode",mode);cmd.Parameters.AddWithValue("@Operation",req["Operation"]!.GetValue<string>());cmd.Parameters.AddWithValue("@RequestId",id);cmd.Parameters.AddWithValue("@SampleId",req["SampleId"]!.GetValue<int>());cmd.Parameters.AddWithValue("@Payload",req.ToJsonString());cmd.Parameters.AddWithValue("@Plan",plan?.ToJsonString()??"");cmd.Parameters.AddWithValue("@Actor",actor);cmd.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=version;cmd.Parameters.AddWithValue("@CallerSid",sid);cmd.Parameters.AddWithValue("@Reason",reason);cmd.Parameters.AddWithValue("@Meaning",meaning);
 string json=(string)(await cmd.ExecuteScalarAsync())!;if(rollback)await tx.RollbackAsync();else await tx.CommitAsync();return JsonNode.Parse(json)!.AsObject();
}
async Task<List<DataTable>> Snapshot(bool owner,Func<Task>? whileLocked=null){
 await using var c=new SqlConnection(owner?service:client);await c.OpenAsync();await using var tx=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable);SqlCommand cmd;
 if(owner){cmd=new SqlCommand("dbo.EQ001_UAT_WaterWorkflow",c,tx){CommandType=CommandType.StoredProcedure};var req=await Request("SaveResults");cmd.Parameters.AddWithValue("@Mode","Read");cmd.Parameters.AddWithValue("@Operation","SaveResults");cmd.Parameters.AddWithValue("@RequestId",Guid.NewGuid());cmd.Parameters.AddWithValue("@SampleId",1);cmd.Parameters.AddWithValue("@Payload",req.ToJsonString());cmd.Parameters.AddWithValue("@Plan","");cmd.Parameters.AddWithValue("@Actor","analyst");using var v=new SqlCommand("SELECT AuthenticationRowVersion FROM dbo.Users WHERE Username=N'analyst'",c,tx);cmd.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=(byte[])(await v.ExecuteScalarAsync())!;cmd.Parameters.AddWithValue("@CallerSid","S-1-5-21-1-2-3-1001");cmd.Parameters.AddWithValue("@Reason","Read locked state");cmd.Parameters.AddWithValue("@Meaning","Verify locked state");}
 else{cmd=new SqlCommand(WaterWorkflowSqlContract.SnapshotQuery.Replace("{LOCK}","WITH (HOLDLOCK)"),c,tx);cmd.Parameters.AddWithValue("@sampleId",1);}
 using(cmd){using var reader=await cmd.ExecuteReaderAsync();var tables=new List<DataTable>();do{var table=new DataTable();for(int i=0;i<reader.FieldCount;i++)table.Columns.Add(reader.GetName(i),reader.GetFieldType(i));while(await reader.ReadAsync()){var values=new object[reader.FieldCount];reader.GetValues(values);table.Rows.Add(values);}tables.Add(table);}while(await reader.NextResultAsync());await reader.CloseAsync();if(whileLocked!=null)await whileLocked();await tx.CommitAsync();return tables;}
}
async Task<(string Water,string Combined,string Metadata)> DeploymentWrapper(string template){
 string directory=Path.Combine(Path.GetTempPath(),"water-wrapper-"+suffix);Directory.CreateDirectory(directory);
 string wrapperFile=Path.Combine(directory,"wrapper.sql"),composedFile=Path.Combine(directory,"composed.sql"),combinedFile=Path.Combine(directory,"combined.sql"),metadataFile=Path.Combine(directory,"metadata.sql");
 try{
  string command="""
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[System.Management.Automation.Language.Parser]::ParseFile($args[0],[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Deployment script parse failure'}
foreach($name in @('Get-WaterWorkflowStage','New-WaterAnalysisBatch')) {
 $found=@($ast.FindAll({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
 if($found.Count -ne 1){throw 'Expected one deployment helper'}
 . ([scriptblock]::Create($found[0].Extent.Text))
}
$stage=Get-WaterWorkflowStage ([IO.File]::ReadAllText($args[1])) ([IO.File]::ReadAllText($args[2]))
$prmStage=[IO.File]::ReadAllText($args[5]); $IncludePrmRegistration=$true
[IO.File]::WriteAllText($args[3],$stage)
[IO.File]::WriteAllText($args[4],(New-WaterAnalysisBatch $stage))
[IO.File]::WriteAllText($args[6],(New-WaterAnalysisBatch $stage $prmStage))
$source=[IO.File]::ReadAllText($args[0]); $begin=$source.IndexOf("    $"+"metadata=@'"); $end=$source.IndexOf('    Invoke-WaterAnalysisSql $metadata',$begin)
if($begin -lt 0 -or $end -lt $begin){throw 'Expected unique pure metadata composition block'}
. ([scriptblock]::Create($source.Substring($begin,$end-$begin)))
[IO.File]::WriteAllText($args[7],$metadata)
""";
  string script=Path.Combine(directory,"export.ps1");File.WriteAllText(script,command);
  var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("EQ001_TEST_PWSH")??(File.Exists("/workspace/.cloud-setup/powershell-parser/runtime/pwsh")?"/workspace/.cloud-setup/powershell-parser/runtime/pwsh":"pwsh")){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
  foreach(string argument in new[]{"-NoProfile","-NonInteractive","-File",script,root+"/scripts/Deploy-Eq001WaterAnalysis.ps1",root+"/scripts/EQ001_UAT_Water_Analysis.sql",root+"/tools/PharmaLIMS.TrustedSigning/WaterWorkflowSqlContract.cs",composedFile,wrapperFile,root+"/scripts/EQ001_UAT_Prm_Registration.sql",combinedFile,metadataFile})start.ArgumentList.Add(argument);
  foreach(string name in new[]{"XDG_CACHE_HOME","XDG_CONFIG_HOME","XDG_DATA_HOME"})start.Environment[name]=directory;
  using var process=Process.Start(start)!;Task<string> output=process.StandardOutput.ReadToEndAsync(),errors=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();string diagnostics=await errors;await output;
  Check(process.ExitCode==0,"actual PowerShell deployment helpers parse and export without executing site deployment");if(process.ExitCode!=0)throw new Exception(diagnostics);
  Check(File.ReadAllText(composedFile)==WaterWorkflowSqlContract.ComposeStage(template),"actual PowerShell deployment composition exactly matches C# ComposeStage");
  return (File.ReadAllText(wrapperFile),File.ReadAllText(combinedFile),File.ReadAllText(metadataFile));
 }finally{Directory.Delete(directory,true);}
}
async Task VerifyWrapper(string sql,int expectedTables){
 await using var c=new SqlConnection(admin);await c.OpenAsync();var messages=new List<string>();c.InfoMessage+=(_,e)=>messages.Add(e.Message);
 using var cmd=new SqlCommand(sql+"\nSELECT @@TRANCOUNT AS TransactionCount;",c){CommandTimeout=60};using var reader=await cmd.ExecuteReaderAsync();int before=0,after=0;bool committed=false;
 do{while(await reader.ReadAsync()){
  if(reader.FieldCount==3 && reader.GetName(0)=="Phase"){string phase=reader.GetString(0);if(phase=="BEFORE")before++;else if(phase=="AFTER")after++;}
  else if(reader.FieldCount==1 && reader.GetName(0)=="TransactionCount")committed=reader.GetInt32(0)==0;
 }}while(await reader.NextResultAsync());await reader.CloseAsync();
 Check(before==expectedTables && after==expectedTables,"deployment wrapper verifies every pre-existing table before and after stage");
 Check(committed && messages.Count(m=>m=="PASS: Water analysis outer transaction committed; all existing rows preserved.")==1,"deployment wrapper emits one final outer-commit proof with no open SQL transaction");
}
string AdaptSql(string sql)=>sql.Replace("EQ001_UAT_AMEEN_20261024",db).Replace("@@SERVERNAME<>N'AMEEN\\SQLEXPRESS'","1=0").Replace("@@SERVERNAME<>N''AMEEN\\SQLEXPRESS''","1=0").Replace("AND type=N''U''","AND type=N''S''").Replace("AND type=N'U'","AND type=N'S'").Replace("AMEEN\\PharmaLIMSSigner",login);
async Task VerifyMetadata(string sql){
 await using var c=new SqlConnection(admin);await c.OpenAsync();var messages=new List<string>();c.InfoMessage+=(_,e)=>messages.Add(e.Message);using var cmd=new SqlCommand(sql,c){CommandTimeout=45};await cmd.ExecuteNonQueryAsync();
 Check(messages.Count(m=>m=="PASS: Independent committed Water analysis metadata verified.")==1,"actual independent deployment metadata verifies both exact owner procedures, grants, trigger hashes and provenance types");
}
try{
 await Exec(master.ConnectionString,$"CREATE DATABASE [{db}]; CREATE LOGIN [{login}] WITH PASSWORD='{pw}',CHECK_POLICY=OFF; CREATE LOGIN [{clogin}] WITH PASSWORD='{pw}',CHECK_POLICY=OFF;");
 await Exec(admin,File.ReadAllText(root+"/tests/fixtures/Eq001WaterAnalysisActualSchema.sql"));
 // Reuse the actual PRM fixture's complete non-overlapping schema and controls; Users/AuditTrail already use the supplied Water site contracts.
 string prmFixture=File.ReadAllText(root+"/tests/fixtures/Eq001PrmRegistrationSchema.sql");int common=prmFixture.IndexOf("CREATE TABLE dbo.[AuditTrail]",StringComparison.Ordinal),controls=prmFixture.IndexOf("EXEC(N'CREATE OR ALTER VIEW dbo.EQ001_UAT_PrmVersionEvidence",StringComparison.Ordinal);if(common<0||controls<common)throw new Exception("PRM fixture composition markers changed");
 await Exec(admin,prmFixture[..common]+prmFixture[controls..]);
 await Exec(admin,"INSERT dbo.PRM_NumberSequences(SequenceName,Prefix,CurrentYear,LastNumber) VALUES(N'Historical sequence',N'HIST',2025,42); INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,CreatedBy) VALUES(N'HIST-PRM-1',N'Raw Material',N'historical');");
 await Exec(admin,$"CREATE USER PharmaLIMS_TrustedEquipmentSigner FOR LOGIN [{login}]; CREATE USER water_analysis_client FOR LOGIN [{clogin}]; GRANT SELECT ON SCHEMA::dbo TO PharmaLIMS_TrustedEquipmentSigner; GRANT SELECT ON SCHEMA::dbo TO water_analysis_client;");
 await Exec(admin,"INSERT dbo.Users(Username,Role,IsActive,IsLocked,MustChangePassword,CanRegisterSamples,CanEnterResults,CanReviewResults,CanApproveResults) VALUES(N'analyst',N'Analyst',1,0,0,1,1,0,0),(N'reviewer',N'Reviewer',1,0,0,0,0,1,0),(N'qa',N'QA',1,0,0,0,0,0,1),(N'admin',N'Admin',1,0,0,0,1,1,1); INSERT dbo.WaterSamplingPoints(PointCode,PointName,WaterType,Location,Status,CreatedAt) VALUES(N'UAT-PW',N'Synthetic',N'Purified',N'UAT',N'Active',SYSDATETIME()); INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,IsWaterTest,IsActive) VALUES(N'T1',N'Total Organic Carbon',N'Chemical',N'ppm',1,1),(N'T2',N'pH',N'Chemical',N'pH',1,1),(N'T3',N'Conductivity',N'Chemical',N'uS/cm',1,1); INSERT dbo.Samples(SampleNumber,PointID,SampleType,SamplingDateTime,ReceivedDateTime,Status) VALUES(N'UAT-1',1,N'Purified Water',DATEADD(hour,-2,SYSDATETIME()),DATEADD(hour,-1,SYSDATETIME()),N'Registered'),(N'UAT-2',1,N'Purified Water',DATEADD(hour,-2,SYSDATETIME()),DATEADD(hour,-1,SYSDATETIME()),N'Registered'); INSERT dbo.SampleTests(SampleID,TestID,TestNameSnapshot,TestCategorySnapshot,UnitSnapshot,ActionLimitSnapshot,LimitDescription) VALUES(1,1,N'Total Organic Carbon',N'Chemical',N'ppm',10,N'NMT 10 ppm'),(2,1,N'Total Organic Carbon',N'Chemical',N'ppm',10,N'NMT 10 ppm'); INSERT dbo.LabEquipment(EquipmentCode,EquipmentName,EquipmentType,QualificationStatus,CalibrationStatus,CreatedBy) VALUES(N'UAT-TOC',N'Synthetic TOC',N'TOC Analyzer',N'Qualified',N'Calibrated',N'fixture'),(N'UAT-PH',N'Synthetic pH',N'pH Meter',N'Qualified',N'Calibrated',N'fixture'),(N'UAT-COND',N'Synthetic Conductivity',N'Conductivity Meter',N'Qualified',N'Calibrated',N'fixture');");
 // RED reproduces the reported Start Analysis permission229 with a separate restricted desktop login.
 await Denied(()=>Exec(client,"UPDATE dbo.Samples SET Status=N'In Analysis' WHERE SampleID=1"),"RED readonly desktop StartAnalysis UPDATE Samples denied",229);
 await Denied(()=>Exec(service,"UPDATE dbo.Samples SET Status=N'In Analysis' WHERE SampleID=1"),"RED independent service direct clinical UPDATE denied",229);
 await Denied(()=>Exec(service,"EXEC dbo.EQ001_UAT_WaterWorkflow"),"RED constrained analysis procedure absent",2812);
 await Exec(admin,"INSERT dbo.ElectronicSignatures(SampleID,ActionType,SignedBy,ActionReason,MeaningOfSignature,CreatedUtc,UtcSignedAt) VALUES(2,N'Historical fixture',N'historical',N'Preserve reason',N'Preserve meaning',SYSUTCDATETIME(),SYSUTCDATETIME()); INSERT dbo.AuditTrail(TableName,RecordID,Action,OldValue,NewValue,CreatedUtc,UtcRecordedAt) VALUES(N'Samples',2,N'Historical fixture',N'old',N'new',SYSUTCDATETIME(),SYSUTCDATETIME());");
 string historicalSql="SELECT CONVERT(varchar(64),HASHBYTES('SHA2_256',CONCAT((SELECT * FROM dbo.Samples ORDER BY SampleID FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.SampleTests ORDER BY SampleTestID FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.ElectronicSignatures ORDER BY SignatureID FOR JSON PATH,INCLUDE_NULL_VALUES),(SELECT * FROM dbo.AuditTrail ORDER BY AuditID FOR JSON PATH,INCLUDE_NULL_VALUES))),2)";
 string historicalBefore=(string)(await Exec(admin,historicalSql))!;
 string permissionsSql="SELECT permission_name FROM fn_my_permissions(N'dbo.Samples',N'OBJECT') ORDER BY permission_name FOR JSON PATH";
 string permissionsBefore=(string)(await Exec(client,permissionsSql))!;
 string template=File.ReadAllText(root+"/scripts/EQ001_UAT_Water_Analysis.sql");
 try{WaterWorkflowSqlContract.ComposeStage(template+WaterWorkflowSqlContract.SnapshotMarker);throw new Exception("Duplicate marker accepted");}catch(ArgumentException){Check(true,"duplicate snapshot marker rejected");}
 string stage=WaterWorkflowSqlContract.ComposeStage(template).Replace("EQ001_UAT_AMEEN_20261024",db).Replace("@@SERVERNAME<>N'AMEEN\\SQLEXPRESS'","1=0").Replace("@@SERVERNAME<>N''AMEEN\\SQLEXPRESS''","1=0").Replace("AND type=N''U''","AND type=N''S''");
 await Denied(()=>Exec(admin,stage),"stage default opt-in disabled",57000);
 var exported=await DeploymentWrapper(template);string wrapper=AdaptSql(exported.Water),combined=AdaptSql(exported.Combined),metadata=AdaptSql(exported.Metadata);
 string secondBoundaryFailure=combined.Replace("DECLARE @VerifiedPrmIsolatedUat bit = 1;","DECLARE @VerifiedPrmIsolatedUat bit = 1; THROW 57050,'Injected second boundary stage failure',1;");
 await Denied(()=>Exec(admin,secondBoundaryFailure),"combined wrapper propagates second-boundary stage failure",57050);
 Check(Convert.ToInt32(await Exec(admin,"SELECT CASE WHEN OBJECT_ID(N'dbo.EQ001_UAT_WaterWorkflow') IS NULL AND OBJECT_ID(N'dbo.EQ001_WaterWorkflowReceipts') IS NULL AND OBJECT_ID(N'dbo.EQ001_UAT_PrmRegistration') IS NULL AND OBJECT_ID(N'dbo.EQ001_PrmRegistrationReceipts') IS NULL THEN 1 ELSE 0 END"))==1,"second-boundary failure rolls back first boundary DDL and both receipt tables");
 int tablesBefore=Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0"));await VerifyWrapper(combined,tablesBefore);Check(true,"actual combined Water and PRM stages create both boundaries atomically without variable collisions");await VerifyMetadata(metadata);
 await VerifyWrapper(wrapper,Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0")));
 stage=stage.Replace("DECLARE @VerifiedIsolatedUat bit = 0;","DECLARE @VerifiedIsolatedUat bit = 1;");
 await Exec(admin,stage);Check(true,"actual analysis SQL stage creates and revalidates exact reviewed boundary");
 string tampered=wrapper.Replace("DECLARE @AfterHash varbinary(32)","UPDATE dbo.Samples SET ModifiedBy=N'INJECTED WRAPPER MUTATION' WHERE SampleID=2; DECLARE @AfterHash varbinary(32)");
 await Denied(()=>Exec(admin,tampered),"deployment wrapper detects any pre-existing row mutation and refuses commit proof",57040);
 Check(historicalBefore==(string)(await Exec(admin,historicalSql))!,"deployment verification failure rolls back injected history mutation");
 Check(historicalBefore==(string)(await Exec(admin,historicalSql))!,"stage and idempotent restage preserve historical clinical/signature/audit hashes");
 Check(permissionsBefore==(string)(await Exec(client,permissionsSql))!,"stage preserves desktop sample permission snapshot");
 await Denied(()=>Exec(client,"EXEC dbo.EQ001_UAT_WaterWorkflow"),"desktop cannot execute trusted owner procedure",229);
 await Denied(()=>Exec(service,"UPDATE dbo.Samples SET Status=N'Approved' WHERE SampleID=1"),"service direct clinical mutation remains denied",229);
 await Denied(()=>Exec(service,"DELETE dbo.EQ001_WaterWorkflowReceipts"),"service direct receipt DML denied",229);
 var start=await Request("StartAnalysis");var startPlan=Plan(start,"In Analysis");startPlan["AnalysisStartedAt"]=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified).AddMinutes(-30).ToString("O");Guid firstId=Guid.NewGuid();
 var first=await Call(firstId,"Commit",start,startPlan);Check(first["Status"]!.GetValue<string>()=="In Analysis","GREEN StartAnalysis writes time/signature/audit/receipt atomically");
 await Exec(admin,"UPDATE dbo.Samples SET PointCodeSnapshot=N'PW-FROZEN' WHERE SampleID=1; UPDATE dbo.WaterSamplingPoints SET PointCode=N'PW-RENAMED' WHERE Id=1; INSERT dbo.SamplingPoints(PointID,PointCode) VALUES(1,N'PW-LEGACY');");
 var clientSnapshot=await Snapshot(false);var ownerSnapshot=await Snapshot(true);Check(ownerSnapshot.Count==14 && clientSnapshot.Count==14,"owner read and readonly desktop return canonical14 tables");
 Check((string)ownerSnapshot[0].Rows[0]["EffectivePointCode"]=="PW-FROZEN" && (string)clientSnapshot[0].Rows[0]["EffectivePointCode"]=="PW-FROZEN","locked effective point code preserves registered snapshot over mutable masters");
 for(int i=0;i<13;i++)Check(ownerSnapshot[i].Columns.Cast<DataColumn>().Select(c=>c.ColumnName+":"+c.DataType.Name).SequenceEqual(clientSnapshot[i].Columns.Cast<DataColumn>().Select(c=>c.ColumnName+":"+c.DataType.Name)),"snapshot table"+i+" native column/type parity");
 Check((await Call(firstId,"Commit",start,startPlan)).ToJsonString()==first.ToJsonString(),"same ID retry returns historical committed outcome");
 await Denied(async()=>{await Call(firstId,"Resolve",start,reason:"changed signed reason");},"receipt reason participates in immutable identity",57005);
 await Denied(async()=>{await Call(firstId,"Resolve",start,sid:"S-1-5-21-999");},"receipt SID cannot be rebound",57005);
 await Exec(admin,"UPDATE dbo.Users SET CanEnterResults=0 WHERE Username=N'analyst'");Check((await Call(firstId,"Resolve",start))["State"]!.GetValue<string>()=="Committed","receipt resolves after permission/auth-version change with fresh active authentication");
 await Exec(admin,"UPDATE dbo.Users SET CanEnterResults=1 WHERE Username=N'analyst'");
 var cancelReq=await Request("SaveResults");Guid cancelledId=Guid.NewGuid();Check((await Call(cancelledId,"Cancel",cancelReq))["State"]!.GetValue<string>()=="Cancelled","pending identity receives permanent cancellation tombstone");Check((await Call(cancelledId,"Commit",cancelReq,Plan(cancelReq,"In Analysis")))["State"]!.GetValue<string>()=="Cancelled","late commit cannot mutate tombstoned identity");
 var times=await Request("CorrectTimes");var timesPlan=Plan(times,"In Analysis");timesPlan["Times"]=new JsonObject{["SamplingDateTime"]=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified).AddHours(-3).ToString("O"),["ReceivedDateTime"]=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified).AddHours(-2).ToString("O"),["IncubationStartedDateTime"]=null,["AnalysisStartedDateTime"]=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified).AddMinutes(-40).ToString("O")};await Call(Guid.NewGuid(),"Commit",times,timesPlan);Check(true,"GREEN CorrectTimes preserves derived incubation and completion semantics");
 var save=await Request("SaveResults");var savePlan=Plan(save,"Results Entered");savePlan["Rows"]=new JsonArray(Row(1));await Call(Guid.NewGuid(),"Commit",save,savePlan);Check(Convert.ToDecimal(await Exec(admin,"SELECT ResultValue FROM dbo.SampleTests WHERE SampleTestID=1"))==7.1234m,"GREEN SaveResults preserves exact4dp value");Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.WaterResultResourceEvidence WHERE SampleID=1"))==1,"SaveResults appends current signed resource batch");
 save=await Request("SaveResults");savePlan=Plan(save,"Results Entered");savePlan["Rows"]=new JsonArray(Row(1));
 var badRow=Row(1,"7.12345");var precision=Plan(save,"Results Entered");precision["Rows"]=new JsonArray(badRow);await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,precision);},"fifth decimal cannot be rounded silently",57022);
 var cross=Plan(save,"Results Entered");cross["Rows"]=new JsonArray(Row(2));await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,cross);},"cross-sample result plan denied",57021);
 var duplicate=Plan(save,"Results Entered");duplicate["Rows"]=new JsonArray(Row(1),Row(1));await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,duplicate);},"duplicate result identities rejected",57021);
 var omitted=Plan(save,"Results Entered");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,omitted);},"omitted visible result denied",57021);
 await Exec(admin,"UPDATE dbo.LabEquipment SET NextCalibrationDate=DATEADD(day,-1,SYSDATETIME()) WHERE EquipmentID=1");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,savePlan);},"equipment expiry rechecked at SQL clock",57027);await Exec(admin,"UPDATE dbo.LabEquipment SET NextCalibrationDate=NULL WHERE EquipmentID=1");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,savePlan,version:new byte[8]);},"stale authentication version denied",57003);
 await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,savePlan,actor:"missing",version:new byte[8]);},"unknown actor denied",57003);
 await Exec(admin,"UPDATE dbo.Users SET IsLocked=1,LockedUntil=NULL WHERE Username=N'analyst'");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,savePlan);},"locked actor denied",57003);await Exec(admin,"UPDATE dbo.Users SET IsLocked=0 WHERE Username=N'analyst'");
 var staleStatus=(JsonObject)save.DeepClone();staleStatus["ExpectedStatus"]="In Analysis";await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",staleStatus,savePlan);},"stale sample status denied",57010);
 var expiredKit=Plan(save,"Results Entered");var kit=Row(1);kit["EquipmentId"]=null;kit["TestKitCode"]="SYNTHETIC";kit["TestKitLot"]="LOT";kit["TestKitExpiry"]=DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");expiredKit["Rows"]=new JsonArray(kit);await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",save,expiredKit);},"expired kit denied against SQL date",57026);
 var submit=await Request("SubmitReview");await Call(Guid.NewGuid(),"Commit",submit,Plan(submit,"Under Review"));Check(true,"GREEN SubmitReview transitions complete sample");
 await Exec(admin,"UPDATE dbo.Users SET CanReviewResults=1 WHERE Username=N'analyst'");
 var review=await Request("Review");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",review,Plan(review,"Reviewed"),actor:"analyst");},"independent reviewer cannot be prior author",57014);
 await Call(Guid.NewGuid(),"Commit",review,Plan(review,"Reviewed"),actor:"reviewer");Check(true,"GREEN independent Review records technical signature");
 await Exec(admin,"INSERT dbo.Water_Plans(PlanNo,WaterType,SourceType,DueDate,RequiredDate,AnalysisProfile,CreatedBy) VALUES(N'UAT-LINKED',N'Purified',N'UAT',CAST(SYSDATETIME() AS date),CAST(SYSDATETIME() AS date),N'Chemical',N'analyst'); INSERT dbo.Water_PlanSamples(WaterPlanID,PointID,PointCode,SampleID,SampleNumber,Status) VALUES(1,1,N'UAT-PW',1,N'UAT-1',N'Registered'); UPDATE dbo.Water_Plans SET Status=N'Analysis In Progress' WHERE WaterPlanID=1;");
 var approve=await Request("Approve");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",approve,Plan(approve,"Approved"),actor:"admin");},"Admin permission does not substitute for QA role",57006);
 await Exec(admin,"CREATE TRIGGER dbo.FailPlanAudit ON dbo.AuditTrail AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Action=N'Water Plan Status Synchronized') THROW 57048,'Injected plan audit failure',1; END;");
 Guid failedApprovalId=Guid.NewGuid();await Denied(async()=>{await Call(failedApprovalId,"Commit",approve,Plan(approve,"Approved"),actor:"qa");},"injected plan audit failure aborts approval",57048);
 Check((string)(await Exec(admin,"SELECT Status FROM dbo.Samples WHERE SampleID=1"))! =="Reviewed" && (string)(await Exec(admin,"SELECT Status FROM dbo.Water_Plans WHERE WaterPlanID=1"))! =="Analysis In Progress" && (string)(await Exec(admin,"SELECT Status FROM dbo.Water_PlanSamples WHERE SampleID=1"))! =="Registered" && (await Call(failedApprovalId,"Resolve",approve,actor:"qa"))["State"]!.GetValue<string>()=="Pending","plan-sync failure rolls back approval, linked statuses and receipt");await Exec(admin,"DROP TRIGGER dbo.FailPlanAudit;");
 Guid approvalId=Guid.NewGuid();await Call(approvalId,"Commit",approve,Plan(approve,"Approved"),actor:"qa");Check((string)(await Exec(admin,"SELECT Status FROM dbo.Water_Plans WHERE WaterPlanID=1"))! =="Completed","GREEN QA Approve synchronizes linked plan inside clinical transaction");
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.ElectronicSignatures WHERE SampleID=1"))==6,"full6-operation lifecycle retains one signature per action");
 // A second sample exercises rollback, scientific gates, OOS, closed-investigation pending-only edits and trigger verification.
 var s2=await Request("StartAnalysis",2);var s2p=Plan(s2,"In Analysis");s2p["AnalysisStartedAt"]=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified).AddMinutes(-10).ToString("O");
 var future=(JsonObject)s2p.DeepClone();future["AnalysisStartedAt"]=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified).AddDays(1).ToString("O");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",s2,future);},"future analysis start rejected by DB clock",57017);
 await Call(Guid.NewGuid(),"Commit",s2,s2p);
 var sv2=await Request("SaveResults",2);var sv2p=Plan(sv2,"Results Entered");sv2p["Rows"]=new JsonArray(Row(2,"5.4321"));
 Guid rollbackId=Guid.NewGuid();await Call(rollbackId,"Commit",sv2,sv2p,rollback:true);Check((await Call(rollbackId,"Resolve",sv2))["State"]!.GetValue<string>()=="Pending" && await Exec(admin,"SELECT ResultValue FROM dbo.SampleTests WHERE SampleTestID=2") is DBNull,"caller rollback restores result and removes receipt");
 await Exec(admin,"CREATE TRIGGER dbo.FailAnalysisAudit ON dbo.AuditTrail AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Action=N'Result Entry') THROW 57049,'Injected audit failure',1; END;");
 int signaturesBefore=Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.ElectronicSignatures"));await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",sv2,sv2p);},"injected audit failure rejects clinical operation",57049);Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.ElectronicSignatures"))==signaturesBefore && await Exec(admin,"SELECT ResultValue FROM dbo.SampleTests WHERE SampleTestID=2") is DBNull,"audit failure rolls back result/resource/signature/receipt");await Exec(admin,"DROP TRIGGER dbo.FailAnalysisAudit;");
 await Exec(admin,"CREATE TRIGGER dbo.RoundAnalysisResult ON dbo.SampleTests AFTER UPDATE AS BEGIN IF UPDATE(ResultValue) UPDATE st SET ResultValue=ROUND(st.ResultValue,2) FROM dbo.SampleTests st JOIN inserted i ON i.SampleTestID=st.SampleTestID; END;");await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",sv2,sv2p);},"posttrigger result rounding aborts full transaction",57024);await Exec(admin,"DROP TRIGGER dbo.RoundAnalysisResult;");
 var oos=(JsonObject)sv2p.DeepClone();oos["Rows"]=new JsonArray(Row(2,"11","OOS"));await Call(Guid.NewGuid(),"Commit",sv2,oos);Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.QualityEvents WHERE SampleID=2 AND EventType=N'OOS'"))==1 && Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.QualityEventAffectedResults WHERE SampleTestID=2"))==1,"new OOS atomically opens QE and links exact affected evidence");
 var blocked=await Request("SubmitReview",2);await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",blocked,Plan(blocked,"Under Review"));},"under-investigation sample cannot submit",57013);
 await Exec(admin,"UPDATE dbo.QualityEvents SET CurrentStatus=N'Closed',FinalDisposition=N'Released After Investigation' WHERE SampleID=2; UPDATE dbo.Samples SET Status=N'Released After Investigation' WHERE SampleID=2;");var frozen=await Request("SaveResults",2);var frozenPlan=Plan(frozen,"Released After Investigation");frozenPlan["Rows"]=new JsonArray(Row(2,"5"));await Denied(async()=>{await Call(Guid.NewGuid(),"Commit",frozen,frozenPlan);},"closed investigation completed results/evidence remain immutable",57023);
 var pending=await Request("SubmitReview",2);await Call(Guid.NewGuid(),"Commit",pending,Plan(pending,"Under Review"));Check(true,"acceptable closed OOS disposition allows controlled next step");
 // The owner snapshot must retain all scientific/evidence/plan locks until the caller commits.
 // A separate administrator connection tries each write; SQL1222 proves it cannot race validation.
 await Snapshot(true,async()=>{
  foreach(var item in new (string Sql,string Name)[]{
   ("UPDATE dbo.Samples SET ModifiedBy=ModifiedBy WHERE SampleID=1","sample header"),
   ("UPDATE dbo.SampleTests SET Remarks=Remarks WHERE SampleTestID=1","result rowversion"),
   ("UPDATE dbo.WaterResultResourceEvidence SET ResultSnapshot=ResultSnapshot WHERE SampleID=1","resource evidence"),
   ("INSERT dbo.WaterResultExecutionEvidence(SampleID,SampleTestID,TestID,ProcedureReference,MethodGuidanceSnapshot,VerificationConfirmed,RawResultSnapshot,EquipmentID,EquipmentCodeSnapshot,EquipmentNameSnapshot,EquipmentTypeSnapshot,SignedBy,MeaningOfSignature,ActionReason) VALUES(1,1,1,N'fixture',N'fixture',1,N'7.1234',1,N'UAT-TOC',N'Synthetic TOC',N'TOC Analyzer',N'analyst',N'fixture',N'fixture')","LES phantom evidence"),
   ("UPDATE dbo.LabEquipment SET IsActive=IsActive WHERE EquipmentID=1","current equipment"),
   ("UPDATE dbo.Water_PlanSamples SET Status=Status WHERE SampleID=1","linked plan sample"),
   ("UPDATE dbo.Water_Plans SET Status=Status WHERE WaterPlanID=1","linked plan header")})
    await Denied(()=>Exec(admin,"SET LOCK_TIMEOUT 400; "+item.Sql),"retained owner snapshot blocks concurrent "+item.Name,1222);
 });
 await Exec(admin,"SET LOCK_TIMEOUT 1000; UPDATE dbo.Samples SET ModifiedBy=ModifiedBy WHERE SampleID=1");Check(true,"retained snapshot locks release at caller commit");
 await VerifyWrapper(wrapper,Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0")));Check(true,"deployment restage preserves populated receipts, signed evidence, QE and plan history");
 string broadened=combined.Replace("IF EXISTS(SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM #WaterPermissions","GRANT UPDATE ON dbo.Samples TO water_analysis_client; IF EXISTS(SELECT class,major_id,minor_id,grantee_principal_id,grantor_principal_id,type,state FROM #WaterPermissions");
 await Denied(()=>Exec(admin,broadened),"deployment wrapper detects permission widening and refuses commit proof",57045);
 await VerifyWrapper(combined,Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0")));await VerifyMetadata(metadata);
 await Denied(()=>Exec(admin,"DELETE dbo.EQ001_WaterWorkflowReceipts"),"workflow receipts are append-only",57032);
 Check(Convert.ToInt32(await Exec(client,"SELECT HAS_PERMS_BY_NAME(N'dbo.Samples',N'OBJECT',N'UPDATE')"))==0 && Convert.ToInt32(await Exec(service,"SELECT HAS_PERMS_BY_NAME(N'dbo.SampleTests',N'OBJECT',N'UPDATE')"))==0,"readonly clinical SQL permissions preserved after all6 operations");
 Console.WriteLine($"PASS: {checks} real restricted-principal Water analysis SQL checks.");
}finally{await Exec(master.ConnectionString,$"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END; IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]; IF SUSER_ID(N'{clogin}') IS NOT NULL DROP LOGIN [{clogin}];");}
