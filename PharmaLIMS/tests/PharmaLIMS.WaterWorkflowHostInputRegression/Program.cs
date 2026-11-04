using System.Text.Json;
using PharmaLIMS.TrustedSigning;

const string connection="Server=sql.example;Database=EQ001_UAT_host;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";
var input=new WaterWorkflowRequest("sql.example","EQ001_UAT_host","StartAnalysis",1,"WT-1","Registered",new string('A',64),new DateTime(2026,10,28,9,0,0),null,Array.Empty<WaterWorkflowRow>());
var request=new TrustedApplicationRequest(Guid.NewGuid(),"WaterWorkflow","analyst",UserJson:JsonSerializer.Serialize(input),Reason:"Analysis start",Meaning:"Performed");
var proof=new byte[8];const string sid="S-1-5-21-101-102-103-1001";
async Task Reject(string name,TrustedApplicationRequest? envelope=null,string service=connection,byte[]? version=null)
{
    try { await new TrustedWaterWorkflowStore(service).ExecuteAsync(envelope??request,version??proof,sid,default); }
    catch(Exception e)when(e is ArgumentException or JsonException)
    {
        Console.WriteLine("PASS: "+name);return;
    }
    throw new Exception("FAIL: "+name+" reached SQL");
}
await Reject("empty reason",request with{Reason=""});
await Reject("meaning cannot exceed SQL boundary",request with{Meaning=new string('M',201)});
await Reject("authentication version must be binary8",version:new byte[7]);
await Reject("unknown transport action",request with{Action="WaterWorkflowCommit"});
await Reject("unknown root metadata/plan",request with{UserJson=request.UserJson[..^1]+",\"Plan\":{}}"});
await Reject("duplicate root properties",request with{UserJson=request.UserJson[..^1]+",\"SampleId\":2}"});
await Reject("target server must equal private connection",request with{UserJson=JsonSerializer.Serialize(input with{TargetServer="other.example"})});
await Reject("target catalog must equal private connection",request with{UserJson=JsonSerializer.Serialize(input with{TargetDatabase="EQ001_UAT_other"})});
await Reject("SQL service authentication forbidden",service:"Server=sql.example;Database=EQ001_UAT_host;User ID=service;Password=placeholder;Encrypt=true;TrustServerCertificate=false");
await Reject("unencrypted service forbidden",service:"Server=sql.example;Database=EQ001_UAT_host;Integrated Security=true;Encrypt=false;TrustServerCertificate=false");
await Reject("unvalidated service certificate forbidden",service:"Server=sql.example;Database=EQ001_UAT_host;Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
var row=new WaterWorkflowRow(7,"1.0","",null,"","","",null,"","",false,"");
var save=input with{Operation="SaveResults",AnalysisStartedAt=null,Results=new[]{row,row}};
await Reject("duplicate result IDs",request with{UserJson=JsonSerializer.Serialize(save)});
save=save with{Results=new[]{row}};
string saveJson=JsonSerializer.Serialize(save);
await Reject("caller scientific metadata forbidden",request with{UserJson=saveJson.Replace("\"SampleTestId\":7","\"SampleTestId\":7,\"TestID\":2",StringComparison.Ordinal)});
await Reject("duplicate nested result properties",request with{UserJson=saveJson.Replace("\"SampleTestId\":7","\"SampleTestId\":7,\"SampleTestId\":8",StringComparison.Ordinal)});
