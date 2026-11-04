using Microsoft.Data.SqlClient;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Text.Json;
string project=Path.GetFullPath(args[0]);
using var settings=JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("EQ001_SQL_TEST_SETTINGS")??throw new Exception("Explicit isolated SQL settings required")));
var master=settings.RootElement.TryGetProperty("MasterConnectionString",out var existingMaster) ? new SqlConnectionStringBuilder(existingMaster.GetString()) : new SqlConnectionStringBuilder {DataSource=settings.RootElement.GetProperty("server").GetString(),InitialCatalog="master",UserID="sa",Password=settings.RootElement.GetProperty("password").GetString(),Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=false,Pooling=false,ConnectTimeout=10};
string suffix=Guid.NewGuid().ToString("N")[..10],db="EQ001_UAT_Prm_"+suffix,login="prm_svc_"+suffix,clientLogin="prm_client_"+suffix,pw="Aa1!"+Guid.NewGuid().ToString("N");
string admin=new SqlConnectionStringBuilder(master.ConnectionString){InitialCatalog=db}.ConnectionString;
string service=new SqlConnectionStringBuilder(admin){IntegratedSecurity=false,UserID=login,Password=pw}.ConnectionString;
string client=new SqlConnectionStringBuilder(admin){IntegratedSecurity=false,UserID=clientLogin,Password=pw}.ConnectionString;
int checks=0;
async Task<object?> Exec(string cs,string sql){await using var c=new SqlConnection(cs);await c.OpenAsync();using var cmd=new SqlCommand(sql,c){CommandTimeout=60};return await cmd.ExecuteScalarAsync();}
void Check(bool b,string m){if(!b)throw new Exception(m);Console.WriteLine("PASS "+m);checks++;}
async Task Denied(Func<Task> a,string m,int? code=null){try{await a();}catch(SqlException ex) when(code is null || ex.Number==code){Check(true,m+" (SQL "+ex.Number+")");return;}throw new Exception("Accepted: "+m);}
var sampled=DateTime.Now.AddMinutes(-10);
var r=new PrmRegistrationRequest {SampleCategory="Raw Material",MaterialCode="RM-01",MaterialName="Synthetic material",ManufacturerLotNo="LOT-1",SamplePurpose="Release",SpecificationNo="SPEC-01",SampledBy="Synthetic sampler",SampleDateTime=new DateTime(sampled.Year,sampled.Month,sampled.Day,sampled.Hour,sampled.Minute,0)};
async Task<PrmRegistrationResult> Call(Guid id,string action,PrmRegistrationRequest p,string actor="admin",string sid="S-1-5-21-1-2-3-1001",bool rollback=false,byte[]? stale=null,string reason="Synthetic UAT registration verification")
{
 byte[] version=(byte[])(await Exec(service,"SELECT AuthenticationRowVersion FROM dbo.Users WHERE Username=N'"+actor+"'"))!;
 await using var c=new SqlConnection(service);await c.OpenAsync();await using var tx=(SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Serializable);
 using var cmd=new SqlCommand("dbo.EQ001_UAT_PrmRegistration",c,tx){CommandType=CommandType.StoredProcedure,CommandTimeout=30};
 cmd.Parameters.AddWithValue("@Action",action);cmd.Parameters.AddWithValue("@RequestId",id);cmd.Parameters.AddWithValue("@Payload",PrmRegistrationPolicy.CanonicalPayload(p));
 cmd.Parameters.AddWithValue("@Actor",actor);cmd.Parameters.Add("@ActorVersion",SqlDbType.Binary,8).Value=stale??version;cmd.Parameters.AddWithValue("@CallerSid",sid);
 cmd.Parameters.AddWithValue("@Reason",reason);cmd.Parameters.AddWithValue("@Meaning","I confirm this synthetic registration");
 string json=(string)(await cmd.ExecuteScalarAsync())!;if(rollback){await tx.RollbackAsync();throw new InvalidOperationException("Simulated failure before commit");}await tx.CommitAsync();return JsonSerializer.Deserialize<PrmRegistrationResult>(json)!;
}
try
{
 await Exec(master.ConnectionString,$"CREATE DATABASE [{db}]; CREATE LOGIN [{login}] WITH PASSWORD='{pw}',CHECK_POLICY=OFF; CREATE LOGIN [{clientLogin}] WITH PASSWORD='{pw}',CHECK_POLICY=OFF;");
 await Exec(admin,File.ReadAllText(project+"/tests/fixtures/Eq001PrmRegistrationSchema.sql"));
 await Exec(admin,$"CREATE USER PharmaLIMS_TrustedEquipmentSigner FOR LOGIN [{login}]; CREATE USER prm_client FOR LOGIN [{clientLogin}]; GRANT SELECT ON dbo.Users TO PharmaLIMS_TrustedEquipmentSigner;");
 string stage=File.ReadAllText(project+"/scripts/EQ001_UAT_Prm_Registration.sql");
 // Disposable qualification adapts only database/server/origin Windows type; distinct restricted SQL logins remain real.
 stage=stage.Replace("EQ001_UAT_AMEEN_20261024",db).Replace("@@SERVERNAME<>N'AMEEN\\SQLEXPRESS'","1=0").Replace("@@SERVERNAME<>N''AMEEN\\SQLEXPRESS''","1=0").Replace("AND type=N''U''","AND type=N''S''");
 await Denied(()=>Exec(admin,stage),"default-off SQL staging rejected",56930);
 stage=stage.Replace("DECLARE @VerifiedIsolatedUat bit = 0;","DECLARE @VerifiedIsolatedUat bit = 1;");
 await Exec(admin,stage);await Exec(admin,stage);Check(true,"exact procedure stages idempotently with hash validation");
 await Exec(admin,"INSERT dbo.Users(Username,Role,IsActive,IsLocked,MustChangePassword,CanRegisterSamples) VALUES(N'admin',N'Admin',1,0,0,1),(N'reviewer',N'Supervisor',1,0,0,0); INSERT dbo.PRM_NumberSequences(SequenceName,Prefix,CurrentYear,LastNumber) VALUES(N'RM_SAMPLE',N'RM',YEAR(GETDATE()),0),(N'PP_SAMPLE',N'PP',YEAR(GETDATE()),0); INSERT dbo.PRM_SpecificationTests(SpecificationNo,SampleCategory,VersionNo,TestCode,TestName,SpecificationText,ResultType,RequiredTest,MinimumElapsedHours,ApprovalStatus,IsActive,CreatedBy,ReviewedBy,ReviewedDate,ApprovedBy,ApprovedDate,EffectiveDate,ItemCode) VALUES(N'SPEC-01',N'Raw Material',1,N'TAMC',N'Total aerobic microbial count',N'NMT 1000 CFU/g',N'Numeric',1,120,N'Approved',1,N'author',N'reviewer',DATEADD(day,-2,GETDATE()),N'qa',DATEADD(day,-1,GETDATE()),CAST(GETDATE() AS date),N'RM-01'); INSERT dbo.PRM_SpecificationSignatures(SpecificationNo,SampleCategory,VersionNo,ActionType,ActionReason,SignedBy,MeaningOfSignature,SignedAt) VALUES(N'SPEC-01',N'Raw Material',1,N'Review Specification',N'Synthetic review',N'reviewer',N'I reviewed',DATEADD(day,-2,GETDATE())),(N'SPEC-01',N'Raw Material',1,N'Approve Specification',N'Synthetic approval',N'qa',N'I approve',DATEADD(day,-1,GETDATE()));");
 foreach(string cs in new[]{client,service}) foreach(string table in new[]{"PRM_NumberSequences","PRM_Samples","PRM_SampleTests","AuditTrail","EQ001_PrmRegistrationReceipts"})
  await Denied(()=>Exec(cs,"UPDATE dbo."+table+" SET "+(table=="PRM_NumberSequences"?"LastNumber=0":table=="PRM_Samples"?"CreatedBy=N'forged'":table=="PRM_SampleTests"?"TestName=N'forged'":table=="AuditTrail"?"ChangedBy=N'forged'":"Actor=N'forged'")),"direct "+table+" DML denied to "+(cs==client?"desktop":"service"),229);
 await Denied(()=>Exec(client,"EXEC dbo.EQ001_UAT_PrmRegistration @Action=NULL,@RequestId=NULL,@Payload=NULL,@Actor=NULL,@ActorVersion=NULL,@CallerSid=NULL,@Reason=NULL,@Meaning=NULL"),"desktop cannot execute trusted procedure",229);
 Guid id=Guid.NewGuid();var first=await Call(id,"Register",r);Check(first.State=="Committed" && first.TestCount==1 && first.SampleId>0,"new registration commits sample and assigned tests");
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.AuditTrail WHERE Action=N'PRM Sample Registered' AND ChangedBy=N'admin'"))==1,"audit actor derives from authenticated service identity");
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.PRM_SampleTests WHERE SpecificationVersionNo=1 AND MinimumElapsedHours=120 AND SourceSpecificationTestID=1"))==1,"approved version and timing snapshot preserved");
 var replay=await Call(id,"Register",r);Check(replay==first,"lost-response replay returns original immutable receipt");Check(await Call(id,"Resolve",r)==first,"resolve returns original committed sample");Check(await Call(id,"Cancel",r)==first,"cancel after commit preserves committed sample");
 await Denied(async()=>{await Call(id,"Register",r with {ManufacturerLotNo="CHANGED"});},"changed payload replay denied",56935);
 await Denied(async()=>{await Call(id,"Resolve",r,sid:"S-1-5-21-9-9-9-9999");},"changed caller SID replay denied",56935);
 await Denied(async()=>{await Call(id,"Register",r,reason:"Changed recovery reason");},"original recovery reason remains bound to request identity",56935);
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r,actor:"reviewer");},"actor without registration permission denied",56933);
 var cancel=Guid.NewGuid();Check((await Call(cancel,"Resolve",r)).State=="Pending","unsubmitted request resolves pending");Check((await Call(cancel,"Cancel",r)).State=="Cancelled","pending request cancellation records tombstone");Check((await Call(cancel,"Register",r)).State=="Cancelled","late registration after cancellation remains cancelled");
 int before=Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.PRM_NumberSequences WHERE SequenceName=N'RM_SAMPLE'"));var failed=Guid.NewGuid();try{await Call(failed,"Register",r,rollback:true);}catch(InvalidOperationException){}
 Check(Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.PRM_NumberSequences WHERE SequenceName=N'RM_SAMPLE'"))==before,"precommit failure rolls back numbering");Check((await Call(failed,"Resolve",r)).State=="Pending","precommit failure leaves no receipt");
 Guid concurrent=Guid.NewGuid();var same=await Task.WhenAll(Call(concurrent,"Register",r),Call(concurrent,"Register",r));Check(same[0]==same[1],"concurrent identical requests create one sample");
 var different=await Task.WhenAll(Call(Guid.NewGuid(),"Register",r),Call(Guid.NewGuid(),"Register",r));Check(different[0].SampleNumber!=different[1].SampleNumber,"concurrent distinct registrations have unique numbers");
 await Exec(admin,"CREATE TRIGGER dbo.FailPrmAudit ON dbo.AuditTrail AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE Action=N'PRM Sample Registered') THROW 56998,'Injected audit write failure',1; END;");
 int samples=Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.PRM_Samples"));before=Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.PRM_NumberSequences WHERE SequenceName=N'RM_SAMPLE'"));
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"audit failure rolls back entire operation",56998);
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.PRM_Samples"))==samples && Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.PRM_NumberSequences WHERE SequenceName=N'RM_SAMPLE'"))==before,"audit failure creates no sample and consumes no number");await Exec(admin,"DROP TRIGGER dbo.FailPrmAudit");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r,stale:new byte[8]);},"stale authentication rowversion denied",56933);
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r with {SampleDateTime=r.SampleDateTime.AddDays(1)});},"future laboratory timestamp denied",56936);
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r with {MaterialCode="WRONG"});},"approved specification item scope enforced",56938);
 await Exec(admin,"UPDATE dbo.PRM_SpecificationTests SET IsActive=0");await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"inactive specification denied",56938);await Exec(admin,"UPDATE dbo.PRM_SpecificationTests SET IsActive=1");
 await Exec(admin,"UPDATE dbo.PRM_NumberSequences SET LastNumber=9999 WHERE SequenceName=N'RM_SAMPLE'");
 Check((await Call(Guid.NewGuid(),"Register",r)).SampleNumber.EndsWith("-10000"),"numbering above four digits never wraps or duplicates");
 await Exec(admin,"UPDATE dbo.PRM_NumberSequences SET CurrentYear=YEAR(GETDATE())-1,LastNumber=15000 WHERE SequenceName=N'RM_SAMPLE'");
 Check((await Call(Guid.NewGuid(),"Register",r)).SampleNumber.EndsWith("-10001"),"year rollover reconciles existing current-year numbers without duplication");
 await Exec(admin,"UPDATE dbo.PRM_NumberSequences SET LastNumber=2147483647 WHERE SequenceName=N'RM_SAMPLE'");
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"exhausted sequence fails without integer overflow",56939);
 await Exec(admin,"UPDATE dbo.PRM_NumberSequences SET LastNumber=10001 WHERE SequenceName=N'RM_SAMPLE'");
 await Exec(admin,"CREATE TRIGGER dbo.FailPrmTests ON dbo.PRM_SampleTests AFTER INSERT AS BEGIN THROW 56997,'Injected test snapshot failure',1; END;");
 before=Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.PRM_NumberSequences WHERE SequenceName=N'RM_SAMPLE'"));samples=Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.PRM_Samples"));
 await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"test assignment failure rolls back registration",56997);
 Check(Convert.ToInt32(await Exec(admin,"SELECT COUNT(*) FROM dbo.PRM_Samples"))==samples && Convert.ToInt32(await Exec(admin,"SELECT LastNumber FROM dbo.PRM_NumberSequences WHERE SequenceName=N'RM_SAMPLE'"))==before,"test failure creates no sample and consumes no number");
 await Exec(admin,"DROP TRIGGER dbo.FailPrmTests");
 await Exec(admin,"DELETE dbo.PRM_SpecificationSignatures WHERE ActionType=N'Review Specification'");await Denied(async()=>{await Call(Guid.NewGuid(),"Register",r);},"missing attributable specification review signature denied",56938);
 await Denied(()=>Exec(admin,"UPDATE dbo.EQ001_PrmRegistrationReceipts SET Actor=N'forged'"),"registration receipts are append-only",56940);
 await Exec(admin,"ALTER TRIGGER dbo.TRG_EQ001_PrmRegistrationReceipts_AppendOnly ON dbo.EQ001_PrmRegistrationReceipts AFTER UPDATE,DELETE AS BEGIN SET NOCOUNT ON; END;");
 await Denied(()=>Exec(admin,stage),"unknown modified receipt trigger is rejected by staging",56940);
 Console.WriteLine($"PASS: {checks} PRM SQL registration behavior checks.");
}
finally {await Exec(master.ConnectionString,$"IF DB_ID(N'{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END; IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]; IF SUSER_ID(N'{clientLogin}') IS NOT NULL DROP LOGIN [{clientLogin}];");}
