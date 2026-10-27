using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Text.Json;

string project=Path.GetFullPath(args[0]);
using var settings=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS")??throw new Exception("Explicit isolated SQL settings required")));
var master=settings.RootElement.TryGetProperty("MasterConnectionString",out var existingMaster) ? new SqlConnectionStringBuilder(existingMaster.GetString()) : new SqlConnectionStringBuilder {DataSource=settings.RootElement.GetProperty("server").GetString(),InitialCatalog="master",UserID="sa",Password=settings.RootElement.GetProperty("password").GetString(),Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=false,Pooling=false,ConnectTimeout=10};
string suffix=Guid.NewGuid().ToString("N")[..10],db="EQ001_UAT_Water_"+suffix,login="water_svc_"+suffix,clientLogin="water_client_"+suffix,pw="Aa1!"+Guid.NewGuid().ToString("N");
string admin=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=db}.ConnectionString;
string service=new SqlConnectionStringBuilder(admin){UserID=login,Password=pw}.ConnectionString;
string client=new SqlConnectionStringBuilder(admin){UserID=clientLogin,Password=pw}.ConnectionString;
int count=0;
async Task<object?> Exec(string cs,string sql){await using var c=new SqlConnection(cs);await c.OpenAsync();using var cmd=new SqlCommand(sql,c){CommandTimeout=30};return await cmd.ExecuteScalarAsync();}
void Check(bool ok,string msg){if(!ok)throw new Exception(msg);count++;Console.WriteLine("PASS "+msg);}
async Task Denied(Func<Task> action,string msg){try{await action();}catch(SqlException){Check(true,msg);return;}throw new Exception("Accepted: "+msg);}
var now=DateTime.SpecifyKind(DateTime.Now.AddMinutes(-10),DateTimeKind.Unspecified);
var r=new WaterRegistrationRequest(1,"Purified Water",new[]{1},now.AddHours(-1),now,null,"UAT Sampler","UAT Receiver",1,"100 mL","Intact","20 C","Accepted","","","",null);
Guid id=Guid.NewGuid();
async Task<WaterRegistrationResult> Call(Guid rid,string action,WaterRegistrationRequest input,string actor="admin",string sid="S-1-5-21-1-2-3-1001",bool fail=false,byte[]? staleVersion=null){
 await using var con=new SqlConnection(service);await con.OpenAsync();await using var tx=(SqlTransaction)await con.BeginTransactionAsync(IsolationLevel.Serializable);
 using var vcmd=new SqlCommand("SELECT AuthenticationRowVersion FROM dbo.Users WHERE Username=@u",con,tx);vcmd.Parameters.AddWithValue("@u",actor);var version=(byte[])(await vcmd.ExecuteScalarAsync())!;
 using var cmd=new SqlCommand("dbo.EQ001_UAT_WaterRegistration",con,tx){CommandType=CommandType.StoredProcedure,CommandTimeout=30};
 cmd.Parameters.AddWithValue("@Action",action);cmd.Parameters.AddWithValue("@RequestId",rid);cmd.Parameters.AddWithValue("@Payload",WaterRegistrationPolicy.CanonicalPayload(input));cmd.Parameters.AddWithValue("@Actor",actor);cmd.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=staleVersion??version;cmd.Parameters.AddWithValue("@CallerSid",sid);cmd.Parameters.AddWithValue("@Reason","Synthetic UAT registration verification");cmd.Parameters.AddWithValue("@Meaning","I confirm this UAT registration");
 var json=(string)(await cmd.ExecuteScalarAsync())!;if(fail){await tx.RollbackAsync();throw new InvalidOperationException("Simulated lost precommit execution");}await tx.CommitAsync();return JsonSerializer.Deserialize<WaterRegistrationResult>(json)!;
}
try{
 await Exec(master.ConnectionString,$"CREATE DATABASE [{db}]; CREATE LOGIN [{login}] WITH PASSWORD='{pw}',CHECK_POLICY=OFF; CREATE LOGIN [{clientLogin}] WITH PASSWORD='{pw}',CHECK_POLICY=OFF;");
 await Exec(admin,File.ReadAllText(project+"/tests/Fixtures/Eq001WaterActualSchema.sql"));
 await Exec(admin,$"CREATE USER PharmaLIMS_TrustedEquipmentSigner FOR LOGIN [{login}]; CREATE USER water_client FOR LOGIN [{clientLogin}]; GRANT SELECT ON SCHEMA::dbo TO PharmaLIMS_TrustedEquipmentSigner; GRANT SELECT ON SCHEMA::dbo TO water_client;");
 string stage=File.ReadAllText(project+"/scripts/EQ001_UAT_Water_Registration.sql");
 // Local disposable qualification adapts only target/origin type, preserving real distinct SQL service/client logins.
 stage=stage.Replace("EQ001_UAT_AMEEN_20261024",db).Replace("@@SERVERNAME<>N'AMEEN\\SQLEXPRESS'","1=0").Replace("@@SERVERNAME<>N''AMEEN\\SQLEXPRESS''","1=0").Replace("AND type=N''U''","AND type=N''S''");
 await Denied(()=>Exec(admin,stage),"default staging opt-in rejected");
 stage=stage.Replace("DECLARE @VerifiedIsolatedUat bit = 0;","DECLARE @VerifiedIsolatedUat bit = 1;");
 await Exec(admin,stage);await Exec(admin,stage);Check(true,"staging idempotent against exact site column/trigger shape");
 await Exec(admin,"INSERT dbo.Users(Username,Role,IsActive,IsLocked,MustChangePassword,CanRegisterSamples) VALUES(N'admin',N'Admin',1,0,0,1),(N'reviewer',N'Supervisor',1,0,0,0); INSERT dbo.WaterSamplingPoints(PointCode,PointName,WaterType,Location,Status,CreatedAt) VALUES(N'UAT-PW',N'Synthetic UAT point',N'Purified',N'UAT',N'Active',SYSDATETIME()),(N'UAT-PTW',N'Synthetic potable point',N'Potable',N'UAT',N'Active',SYSDATETIME()); INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,IsWaterTest,IsActive) VALUES(N'COND',N'Conductivity',N'Chemical',N'uS/cm',1,1),(N'TAMC',N'Total Bacterial Count',N'Microbiology',N'CFU/mL',1,1); INSERT dbo.WaterTestProfiles(ProfileCode,ProfileName,VersionNo,ApprovalStatus,IsActive,ApprovedBy,ApprovedAt,CreatedBy,ControlledReference) VALUES(N'PW',N'UAT profile',1,N'Approved',1,N'qa',SYSDATETIME(),N'admin',N'UAT SOP'),(N'PTW',N'UAT potable profile',1,N'Approved',1,N'qa',SYSDATETIME(),N'admin',N'UAT SOP'); INSERT dbo.WaterSpecifications(ProfileCode,ProfileID,TestID,ActionLimit,SpecificationText,ApprovalStatus,IsActive,CreatedBy) VALUES(N'PW',1,1,10,N'NMT 10 uS/cm',N'Approved',1,N'admin'),(N'PW',1,2,100,N'NMT 100 CFU/mL',N'Approved',1,N'admin'),(N'PTW',2,1,20,N'NMT 20 uS/cm',N'Approved',1,N'admin');");
 await Denied(()=>Exec(client,"UPDATE dbo.LIMS_NumberSequences SET LastNumber=0"),"client direct numbering denied");
 await Denied(()=>Exec(client,"EXEC dbo.EQ001_UAT_WaterRegistration"),"client procedure execute denied");
 await Denied(()=>Exec(service,"INSERT dbo.EQ001_WaterRegistrationReceipts DEFAULT VALUES"),"service direct receipt writes denied");
 var first=await Call(id,"Register",r);Check(first.State=="Committed" && first.SampleId>0 && first.TestCount==1,"accepted Water registration committed");
 var replay=await Call(id,"Register",r);Check(replay==first,"same-ID retry returns identical committed result");
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.Samples"))==1,"replay did not create duplicate sample");
 await Denied(async()=>{await Call(id,"Register",r with{SampleVolume="200 mL"});},"changed-payload replay denied");
 await Denied(async()=>{await Call(id,"Register",r,sid:"S-1-5-21-1-2-3-9999");},"changed-SID replay denied");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r,actor:"reviewer");},"actor without registration permission denied");
 var cancelId=Guid.NewGuid();Check((await Call(cancelId,"Cancel",r)).State=="Cancelled","terminal cancellation recorded");Check((await Call(cancelId,"Register",r)).State=="Cancelled","late original dispatch cannot create after cancellation");
 var pendingId=Guid.NewGuid();Check((await Call(pendingId,"Resolve",r)).State=="Pending","unsubmitted request resolves pending");Check((await Call(pendingId,"Cancel",r)).State=="Cancelled","pending lookup can be safely terminally cancelled");
 var label=await Call(id,"Label",r);Check(label.LabelPrinted,"label acknowledgement recorded");await Call(id,"Label",r);Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.AuditTrail WHERE Action=N'Water Sample Label Printed'"))==1,"label acknowledgement idempotent");
 var failId=Guid.NewGuid();int before=Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.LIMS_NumberSequences WHERE NumberKey=N'PW'"));try{await Call(failId,"Register",r,fail:true);}catch(InvalidOperationException){}Check(Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.LIMS_NumberSequences WHERE NumberKey=N'PW'"))==before,"precommit failure rolls back number");Check((await Call(failId,"Resolve",r)).State=="Pending","precommit failure leaves no receipt");
 await Exec(admin,"UPDATE dbo.WaterSpecifications SET ActionLimit=0.000001 WHERE ProfileID=1 AND TestID=1");await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"inexact specification snapshot rejected");await Exec(admin,"UPDATE dbo.WaterSpecifications SET ActionLimit=10 WHERE ProfileID=1 AND TestID=1");
 var concurrentId=Guid.NewGuid();var same=await Task.WhenAll(Call(concurrentId,"Register",r),Call(concurrentId,"Register",r));Check(same[0].SampleId==same[1].SampleId,"concurrent identical requests produce one sample");
 var different=await Task.WhenAll(Call(Guid.NewGuid(),"Register",r),Call(Guid.NewGuid(),"Register",r));Check(different[0].SampleNumber!=different[1].SampleNumber,"concurrent distinct requests have unique numbers");
 var micro=r with{TestIds=new[]{2},IncubationAt=now,IncubationProgram="Water TAMC — 30–35 °C / 5 days",IncubatorId="UAT-NOT-FOR-USE"};Check((await Call(Guid.NewGuid(),"Register",micro)).Status=="Incubation","microbiological registration derives incubation status");
 Check((await Call(Guid.NewGuid(),"Register",r with{ReceiptDecision="Rejected",ReceiptReason="Synthetic rejection test"})).Status=="Rejected","rejected receipt preserved");
 Check((await Call(Guid.NewGuid(),"Register",r with{PointId=2,WaterType="Potable Water"})).SampleNumber.StartsWith("PTW-"),"potable numbering separated");
 await Exec(admin,"INSERT dbo.Water_Plans(PlanNo,WaterType,SourceType,DueDate,RequiredDate,AnalysisProfile,CreatedBy) VALUES(N'UAT-PLAN',N'Purified',N'UAT',CAST(GETDATE() AS date),CAST(GETDATE() AS date),N'Chemical',N'admin'); INSERT dbo.Water_PlanSamples(WaterPlanID,PointID,PointCode,Status) VALUES(1,1,N'UAT-PW',N'Distributed'); INSERT dbo.Water_PlanSampleTests(WaterPlanSampleID,TestID) VALUES(1,1); UPDATE dbo.Water_Plans SET Status=N'Distributed' WHERE WaterPlanID=1;");
 var plan=r with{WaterPlanSampleId=1};
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",micro with{WaterPlanSampleId=1});},"distributed plan requires exact test assignment");
 var planResult=await Call(Guid.NewGuid(),"Register",plan);Check(planResult.State=="Committed","planned registration commits linkage");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",plan);},"already linked plan cannot create a second registration");
 await Denied(()=>Exec(admin,"UPDATE dbo.Water_PlanSamples SET PointCode=N'forged' WHERE WaterPlanSampleID=1"),"frozen plan point identity preserved");
 await Denied(()=>Exec(admin,"INSERT dbo.Water_PlanSampleTests(WaterPlanSampleID,TestID) VALUES(1,2)"),"distributed tests stay frozen");
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.Water_PlanSampleAttempts WHERE WaterPlanSampleID=1"))==1,"one planned registration attempt recorded");
 await Exec(admin,"UPDATE dbo.Water_Plans SET Status=N'Recollection Required' WHERE WaterPlanID=1; UPDATE dbo.Water_PlanSamples SET Status=N'Rejected' WHERE WaterPlanSampleID=1;");
 Check((await Call(Guid.NewGuid(),"Register",plan)).State=="Committed","rejected plan attempt allows controlled recollection");
 await Exec(admin,"INSERT dbo.WaterSpecifications(ProfileCode,ProfileID,TestID,PointCode,ActionLimit,SpecificationText,ApprovalStatus,IsActive,CreatedBy) VALUES(N'PW',1,1,N'UAT-PW',7,N'NMT 7 point-specific',N'Approved',1,N'admin');");
 var pointResult=await Call(Guid.NewGuid(),"Register",r);Check(Convert.ToDecimal(await Exec(admin,$"SELECT ActionLimitSnapshot FROM dbo.SampleTests WHERE SampleID={pointResult.SampleId}"))==7m,"point-specific controlled specification precedence preserved");
 await Exec(admin,"CREATE TRIGGER dbo.FailWaterAudit ON dbo.AuditTrail AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Action=N'Water Sample Registered') THROW 56998,'Injected audit write failure',1; END;");
 int sampleBefore=Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.Samples"));int sequenceBefore=Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.LIMS_NumberSequences WHERE NumberKey=N'PW'"));
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"audit failure rejects entire registration");
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.Samples"))==sampleBefore && Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.LIMS_NumberSequences WHERE NumberKey=N'PW'"))==sequenceBefore,"audit failure leaves no sample or allocated number");
 await Exec(admin,"DROP TRIGGER dbo.FailWaterAudit;");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r,staleVersion:new byte[8]);},"stale authentication version rejected at commit");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r with{SamplingAt=now.AddDays(1),ReceivedAt=now.AddDays(1).AddHours(1)});},"future timestamps rejected by database clock");
 await Denied(()=>Exec(admin,"DELETE dbo.EQ001_WaterRegistrationReceipts"),"registration receipts remain append-only");
 await Denied(()=>Exec(admin,"UPDATE dbo.ElectronicSignatures SET SignedBy=N'forged'"),"signature append-only protection preserved");
 Console.WriteLine($"PASS: {count} actual-shape Water SQL behavior checks.");
}finally{await Exec(master.ConnectionString,$"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END; IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]; IF SUSER_ID(N'{clientLogin}') IS NOT NULL DROP LOGIN [{clientLogin}];");}
