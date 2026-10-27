using PharmaLIMS.TrustedSigning;
using System.Text.Json;

int count=0;
void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;}
void Reject(Action action,string message){try{action();}catch(ArgumentException){count++;return;}throw new Exception(message);}
var now=new DateTime(2026,10,27,10,30,0);
var r=new WaterRegistrationRequest(1,"Purified Water",new[]{2,1},now.AddHours(-2),now.AddHours(-1),null,
    "Sampler","Receiver",1,"100 mL","Intact","20 C","Accepted","","","",null);
WaterRegistrationPolicy.Validate(r);
Check(WaterRegistrationPolicy.CanonicalPayload(r)==WaterRegistrationPolicy.CanonicalPayload(r with {TestIds=new[]{1,2}}),"Test ordering must not change request identity");
Check(WaterRegistrationPolicy.CanonicalPayload(r)!=WaterRegistrationPolicy.CanonicalPayload(r with {SampleVolume="200 mL"}),"Changed payload must change request identity");
Reject(()=>WaterRegistrationPolicy.Validate(r with {TestIds=new[]{1,1}}),"Duplicate tests accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {TestIds=Array.Empty<int>()}),"Empty tests accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {ReceiptDecision="Override"}),"Unknown disposition accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {ReceiptDecision="Rejected",ReceiptReason=""}),"Rejection reason missing");
Reject(()=>WaterRegistrationPolicy.Validate(r with {ReceivedAt=now.AddHours(-3)}),"Backwards receipt accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {SampledBy=new string('X',101)}),"Oversize sampler accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {WaterType="EM"}),"Wrong module accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {ContainerCount=0}),"Zero containers accepted");
Reject(()=>WaterRegistrationPolicy.Validate(r with {WaterPlanSampleId=0}),"Invalid plan identity accepted");
Check(WaterRegistrationPolicy.IncubationDays("TYMC — 20–25 °C / 5–7 days")==7,"TYMC duration wrong");
Check(WaterRegistrationPolicy.IncubationDays("Water TAMC — 30–35 °C / 5 days")==5,"TAMC duration wrong");
Reject(()=>WaterRegistrationPolicy.IncubationDays("99 days"),"Uncontrolled incubation accepted");
Check(WaterRegistrationPolicy.RequireExactLimit(12.3456m)==12.3456m,"Exact limit changed");
Reject(()=>WaterRegistrationPolicy.RequireExactLimit(0.000001m),"Snapshot precision rounded");
Reject(()=>WaterRegistrationPolicy.RequireExactLimit(100000000000000m),"Snapshot overflow accepted");
Reject(()=>WaterRegistrationPolicy.ValidateSnapshot("Conductivity","uS/cm","NMT 10",null,null),"Incomplete numeric limits accepted");
Reject(()=>WaterRegistrationPolicy.ValidateSnapshot("pH","","6.5–8.5",null,8.5m),"Incomplete range accepted");
WaterRegistrationPolicy.ValidateSnapshot("Conductivity","uS/cm","NMT 10",null,10m);count++;
WaterRegistrationPolicy.ValidateSnapshot("Colour","","Not more intensely coloured than comparator",null,null);count++;
WaterRegistrationPolicy.RequireTarget("Server=tcp:AMEEN,1433;Database=EQ001_UAT_AMEEN_20261024;Integrated Security=true;Encrypt=true;TrustServerCertificate=false");count++;
Reject(()=>WaterRegistrationPolicy.RequireTarget("Server=tcp:OTHER,1433;Database=EQ001_UAT_AMEEN_20261024;Integrated Security=true;Encrypt=true"),"Wrong host accepted");
Reject(()=>WaterRegistrationPolicy.RequireTarget("Server=tcp:AMEEN,1433;Database=Other;Integrated Security=true;Encrypt=true"),"Wrong DB accepted");
Reject(()=>WaterRegistrationPolicy.RequireTarget("Server=tcp:AMEEN,1433;Database=EQ001_UAT_AMEEN_20261024;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"),"Unvalidated TLS accepted");
Console.WriteLine($"PASS: {count} Water registration policy checks.");
