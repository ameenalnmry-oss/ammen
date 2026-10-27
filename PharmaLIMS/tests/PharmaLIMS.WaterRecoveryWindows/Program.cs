using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.IO;
using System.Security.Cryptography;
using System.Text;

if(!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS")!="true" ||
    Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT")!="github-hosted")
    throw new InvalidOperationException("This test writes only to a fresh GitHub-hosted Windows profile.");
string configPath=Path.Combine(AppContext.BaseDirectory,"appsettings.json");
byte[]? priorConfig=File.Exists(configPath)?File.ReadAllBytes(configPath):null;
File.Copy(Path.Combine(Path.GetFullPath(args[0]),"appsettings.Production.example.json"),configPath,true);
Environment.SetEnvironmentVariable("PHARMALIMS_CONNECTION_STRING","Server=tcp:AMEEN,1433;Database=EQ001_UAT_AMEEN_20261024;Integrated Security=true;Encrypt=true;TrustServerCertificate=false");
string path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PharmaLIMS","WaterRegistration","pending-registration.dat");
if(File.Exists(path))throw new Exception("Refusing to overwrite an existing pending recovery record");
var store=new WaterRegistrationRecovery();var now=DateTime.SpecifyKind(DateTime.Now,DateTimeKind.Unspecified);
var r=new WaterRegistrationRequest(1,"Purified Water",new[]{1},now.AddHours(-2),now.AddHours(-1),null,"SYNTHETIC PRIVATE SAMPLER","UAT",1,"100 mL","Intact","20 C","Accepted","","","",null);
int count=0;
void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;Console.WriteLine("PASS "+text);}
try{
 var p=store.SaveNew("admin",r);byte[] original=File.ReadAllBytes(path);
 Check(!Encoding.UTF8.GetString(original).Contains("SYNTHETIC PRIVATE SAMPLER"),"Recovery data encrypted before persistence");
 var restored=store.Load("admin")!; Check(restored.RequestId==p.RequestId && restored.Actor==p.Actor && WaterRegistrationPolicy.CanonicalPayload(restored.Payload)==WaterRegistrationPolicy.CanonicalPayload(r),"Actual current-user DPAPI restart roundtrip");
 try{store.Load("other");throw new Exception("Actor mismatch accepted");}catch(UnauthorizedAccessException){count++;}
 try{store.SaveNew("admin",r);throw new Exception("Unresolved identity overwritten");}catch(InvalidOperationException){count++;}
 Check(store.Load("admin")!.RequestId==p.RequestId,"Request identity retained across repeated save");
 var corrupt=(byte[])original.Clone();corrupt[^1]^=1;File.WriteAllBytes(path,corrupt);
 try{store.Load("admin");throw new Exception("Corrupt recovery accepted");}catch(CryptographicException){count++;}
 File.WriteAllBytes(path,original);store.Complete("admin",p.RequestId);Check(!File.Exists(path),"Resolved recovery removed only by matching identity");
 Console.WriteLine($"PASS: {count} native Windows DPAPI recovery checks.");
}finally{if(File.Exists(path))File.Delete(path);if(priorConfig is null)File.Delete(configPath);else File.WriteAllBytes(configPath,priorConfig);}
