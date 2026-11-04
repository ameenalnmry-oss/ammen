using PharmaLIMS.Models;
using PharmaLIMS.TrustedSigning;

// Portable boundary tests link the real endpoint. Database/authentication dependencies are
// deterministic substitutes here; native/SQL suites test those dependencies independently.
var sid="S-1-5-21-101-102-103-1001";
var request=new TrustedApplicationRequest(Guid.NewGuid(),"WaterWorkflow","qa",Reason:"Verified",Meaning:"Approval");
TrustedApplicationEndpoint Endpoint(bool enabled)
{
    var ctor=typeof(TrustedApplicationEndpoint).GetConstructors().Single();
    return (TrustedApplicationEndpoint)ctor.Invoke(ctor.GetParameters().Length==5
        ? new object[]{"unused",false,false,enabled,false} : new object[]{"unused",false,false,enabled});
}
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);Console.WriteLine("PASS: "+name);}
var disabled=Endpoint(false);
Check((await disabled.HandleAsync(sid,request,default)).ErrorCode=="WaterWorkflowDisabled","analysis is independently disabled before ticket/database access");
var endpoint=Endpoint(true);
Check((await endpoint.HandleAsync(sid,request,default)).ErrorCode=="Denied","enabled workflow requires a fresh ticket");
foreach(string action in new[]{"WaterWorkflowResolve","WaterWorkflowCancel"})
    Check((await endpoint.HandleAsync(sid,request with{Action=action},default)).ErrorCode=="Denied",action+" requires a fresh ticket");
Check((await endpoint.HandleAsync(sid,request with{Action="WaterWorkflowCommit"},default)).ErrorCode=="InvalidRequest","unknown workflow action is rejected");
async Task<string> Ticket()
{
    var reply=await endpoint.HandleAsync(sid,request with{Action="Reauthenticate",Password="fresh"},default);
    return reply.Ticket??throw new Exception("Missing ticket");
}
string proof=await Ticket();
Check(!(await endpoint.HandleAsync("S-1-5-21-101-102-103-9999",request with{Ticket=proof},default)).Success,"ticket is bound to native SID");
Check(!(await endpoint.HandleAsync(sid,request with{Ticket=proof},default)).Success,"rejected proof is consumed once");
proof=await Ticket();
Check(!(await endpoint.HandleAsync(sid,request with{Ticket=proof,Username="other"},default)).Success,"ticket is bound to application user");
proof=await Ticket();
var proofs=(System.Collections.IDictionary)typeof(TrustedApplicationEndpoint).GetField("_tickets",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(endpoint)!;
var expired=proofs[proof]!;
expired.GetType().GetProperty("Expires")!.SetValue(expired,DateTimeOffset.UtcNow.AddSeconds(-1));
Check(!(await endpoint.HandleAsync(sid,request with{Ticket=proof},default)).Success,"expired proof is rejected");
proof=await Ticket();
PharmaLIMS.Repositories.UserRepository.Actor.AuthenticationRowVersion=new byte[8]{1,0,0,0,0,0,0,0};
Check(!(await endpoint.HandleAsync(sid,request with{Ticket=proof},default)).Success,"changed authentication version invalidates proof");
proof=await Ticket();
var result=await endpoint.HandleAsync(sid,request with{Ticket=proof},default);
Check(result.Success && TrustedWaterWorkflowStore.Calls==1,"QA reaches workflow without registration or user-administration permission");
Check(!(await endpoint.HandleAsync(sid,request with{Ticket=proof},default)).Success,"successful signing proof cannot be replayed");
Check((await endpoint.HandleAsync(sid,request with{Action="WaterRegister",Ticket=await Ticket()},default)).ErrorCode=="WaterDisabled","analysis flag does not activate registration");
var settings=new TrustedHostSettings();
Check(settings.GetType().GetProperty("EnableWaterAnalysisUat")?.GetValue(settings) is false,"analysis flag defaults to false");
settings.ConnectionString="Server=sql.example;Database=EQ001_UAT_host;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";
settings.CertificatePath="protected.pfx";
settings.WindowsAccounts.Add(sid,"qa");
settings.EnableWaterAnalysisUat=true;
settings.Validate();
Check(true,"analysis configuration permits isolated Windows and validated TLS target");
void Reject(Action action,string name)
{
    try{action();}catch(Exception e)when(e is ArgumentException or UnauthorizedAccessException){Check(true,name);return;}
    throw new Exception("FAIL: "+name);
}
settings.ProductionActivationApproved=true;
Reject(settings.Validate,"UAT analysis refuses production activation");
settings.ProductionActivationApproved=false;
settings.DeploymentMode="Production";
Reject(settings.Validate,"analysis refuses Production deployment");
settings.DeploymentMode="UAT";
foreach(string invalid in new[]{
    "Server=sql.example;Database=EQ001_UAT_host;User ID=service;Password=placeholder;Encrypt=true;TrustServerCertificate=false",
    "Server=sql.example;Database=EQ001_UAT_host;Integrated Security=true;Encrypt=false;TrustServerCertificate=false",
    "Server=sql.example;Database=EQ001_UAT_host;Integrated Security=true;Encrypt=true;TrustServerCertificate=true",
    "Server=sql.example;Database=Production;Integrated Security=true;Encrypt=true;TrustServerCertificate=false"})
{
    settings.ConnectionString=invalid;
    Reject(settings.Validate,"analysis refuses SQL authentication, weak TLS or nonisolated target");
}

var prmRequest=request with{Action="PrmRegister"};
Check((await endpoint.HandleAsync(sid,prmRequest,default)).ErrorCode=="PrmRegistrationDisabled","Water analysis does not enable PRM registration");
foreach(string prmAction in new[]{"PrmResolve","PrmCancel"})
 Check((await endpoint.HandleAsync(sid,prmRequest with{Action=prmAction},default)).ErrorCode=="PrmRegistrationDisabled","PRM recovery independently disabled");
var prmEndpoint=new TrustedApplicationEndpoint("unused",enablePrmRegistrationUat:true);
Check((await prmEndpoint.HandleAsync(sid,prmRequest,default)).ErrorCode=="Denied","PRM registration needs fresh authentication");
async Task<string> PrmTicket()=> (await prmEndpoint.HandleAsync(sid,request with{Action="Reauthenticate",Password="fresh"},default)).Ticket!;
Check((await prmEndpoint.HandleAsync(sid,prmRequest with{Ticket=await PrmTicket()},default)).ErrorCode=="Denied","PRM sample registration requires CanRegisterSamples");
PharmaLIMS.Repositories.UserRepository.Actor.CanRegisterSamples=true;
var prmProof=await PrmTicket();
Check((await prmEndpoint.HandleAsync(sid,prmRequest with{Ticket=prmProof},default)).Success,"PRM sample registration reaches scoped store after independent fresh authorization");
Check(!(await prmEndpoint.HandleAsync(sid,prmRequest with{Ticket=prmProof},default)).Success,"PRM proof cannot be reused");
Check(!new TrustedHostSettings().EnablePrmRegistrationUat,"PRM registration flag defaults false");
var prmSettings=new TrustedHostSettings { ConnectionString="Server=tcp:AMEEN,1433;Database=EQ001_UAT_AMEEN_20261024;Integrated Security=true;Encrypt=true;TrustServerCertificate=false", CertificatePath="protected.pfx",EnablePrmRegistrationUat=true};
prmSettings.WindowsAccounts.Add(sid,"qa");prmSettings.Validate();
prmSettings.ProductionActivationApproved=true;Reject(prmSettings.Validate,"PRM registration refuses Production activation");
