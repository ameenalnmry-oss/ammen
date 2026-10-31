using System.Data;
using System.Text.Json;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;

int passed=0;
void Test(string name,Action test) {test();passed++;Console.WriteLine("PASS "+name);}
void Check(bool value) {if(!value)throw new Exception("Assertion failed");}
void Reject(Action action) {try {action();}catch(ArgumentException){return;}catch(InvalidOperationException){return;}catch(DBConcurrencyException){return;}catch(JsonException){return;}throw new Exception("Expected rejection");}
WaterWorkflowMutationPlan Build(WaterWorkflowSnapshot s,WaterWorkflowRequest? r=null)=>WaterWorkflowValidator.Build(s,r??Fixture.Request(s));
Test("typed policy rejects unknown request and nested plan authority",()=> {
 var s=Fixture.Create();string json=WaterWorkflowPolicy.CanonicalPayload(Fixture.Request(s));
 Reject(()=>JsonSerializer.Deserialize<WaterWorkflowRequest>(json[..^1]+",\"Role\":\"QA\"}"));
 json=json.Replace("\"SampleTestId\":1","\"SampleTestId\":1,\"ResultStatus\":\"PASS\"");
 Reject(()=>JsonSerializer.Deserialize<WaterWorkflowRequest>(json));
});
Test("bounded operation shapes targets and local timestamps",()=> {
 var r=Fixture.Request(Fixture.Create());
 Reject(()=>WaterWorkflowPolicy.Validate(r with{Operation="ExecuteSql"}));
 Reject(()=>WaterWorkflowPolicy.Validate(r with{TargetDatabase="Production"}));
 Reject(()=>WaterWorkflowPolicy.Validate(r with{Results=null!}));
 Reject(()=>WaterWorkflowPolicy.Validate(r with{Times=new(null,null,null,null,null)}));
 Reject(()=>WaterWorkflowPolicy.Validate(r with{Results=new[]{r.Results[0] with{Remarks=new string('x',4001)}}}));
 Reject(()=>WaterWorkflowPolicy.Validate(r with{Operation="StartAnalysis",Results=Array.Empty<WaterWorkflowRow>(),AnalysisStartedAt=DateTime.UtcNow}));
});
Test("canonical ordering and exact target TLS binding",()=> {
 var s=Fixture.Create();Fixture.AddResult(s,2,"Turbidity",null);var r=Fixture.Request(s);
 Check(WaterWorkflowPolicy.CanonicalPayload(r)==WaterWorkflowPolicy.CanonicalPayload(r with{Results=r.Results.Reverse().ToArray()}));
 const string connection="Server=server;Database=EQ001_UAT_test;Integrated Security=true;Encrypt=true;TrustServerCertificate=false";
 WaterWorkflowPolicy.RequireTarget(connection,r);
 Reject(()=>WaterWorkflowPolicy.RequireTarget(connection.Replace("Encrypt=true","Encrypt=false"),r));
 Reject(()=>WaterWorkflowPolicy.RequireTarget(connection.Replace("server","other"),r));
 Reject(()=>WaterWorkflowPolicy.RequireTarget(connection.Replace("Integrated Security=true","User Id=readonly;Password=unused"),r));
 WaterWorkflowPolicy.RequireTarget(connection.Replace("Integrated Security=true","User Id=readonly;Password=unused"),r,false);
});
Test("snapshot excludes clock and unrelated catalogs but includes clinical changes",()=> {
 var s=Fixture.Create();string hash=s.Hash;s.Clock.Rows[0]["DatabaseNow"]=Fixture.Now.AddSeconds(10);Check(s.Hash==hash);
 Fixture.AddEquipment(s);Check(s.Hash==hash);s.Compatibility.Rows.Add("WATER","TESTID:2",true,"Meter");Check(s.Hash==hash);
 s.PlanSamples.Rows.Add(11,7,999,"Pending");Check(s.Hash==hash);
 s.PlanSamples.Rows.Add(12,7,1,"Pending");Check(s.Hash!=hash);hash=s.Hash;
 s.Plans.Rows.Add(7,"Registered",99);Check(s.Hash!=hash);hash=s.Hash;s.Plans.Rows[0]["UnrelatedCount"]=100;Check(s.Hash==hash);
 s.Plans.Rows[0]["Status"]="Completed";Check(s.Hash!=hash);hash=s.Hash;
 s.Results.Rows[0]["Remarks"]="changed";Check(s.Hash!=hash);
});
Test("snapshot changes bind result attribution and resource LES certificate quality state",()=> {
 foreach(string table in new[]{"Results","Resources","Executions","Assignments","QualityEvents","Certificates","Signatures"}) {
  var s=Fixture.Create();string hash=s.Hash;
  switch(table) {
   case "Results":s.Results.Rows[0]["ResultRowVersion"]=new byte[]{1,2,3,4,5,6,7,8};break;
   case "Resources":s.Resources.Rows.Add(1,"NO_INSTRUMENT",DBNull.Value,"","","",DBNull.Value);break;
   case "Executions":s.Executions.Rows.Add(1,25m,"reference",true,"remark");break;
   case "Assignments":s.Assignments.Rows.Add(1,7,1);break;
   case "QualityEvents":s.QualityEvents.Rows.Add(1,"Open","");break;
   case "Certificates":s.Certificates.Rows.Add(1,false);break;
   case "Signatures":s.Signatures.Rows.Add(1,"tester","Review");break;
  }
  Check(s.Hash!=hash);
 }
});
Test("stale or foreign sample cannot build",()=> {
 var s=Fixture.Create();var r=Fixture.Request(s);s.Results.Rows[0]["Remarks"]="concurrent";Reject(()=>Build(s,r));
 r=Fixture.Request(s);Reject(()=>Build(s,r with{SampleNumber="other"}));s.Header.Rows[0]["SampleType"]="Environmental Monitoring";Reject(()=>Build(s,Fixture.Request(s)));
});
Test("exact visible keys reject omissions duplicates extras and cross sample",()=> {
 var s=Fixture.Create();var r=Fixture.Request(s);
 Reject(()=>Build(s,r with{Results=Array.Empty<WaterWorkflowRow>()}));
 Reject(()=>Build(s,r with{Results=new[]{r.Results[0],r.Results[0]}}));
 Reject(()=>Build(s,r with{Results=new[]{r.Results[0] with{SampleTestId=9}}}));
 s.Results.Rows[0]["SampleID"]=99;Reject(()=>Build(s));
});
Test("installed removed tests omitted but hidden NULL still prevents completion",()=> {
 var s=Fixture.Create();Fixture.AddResult(s,2,"Taste & Odour",null);var r=Fixture.Request(s);
 Check(r.Results.Length==1);var p=Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="Clear"}));Check(p.Status=="Partially Entered" && !p.MarkAnalysisCompleted);
 s.Header.Rows[0]["Status"]="Results Entered";s.Results.Rows[0]["ResultValue"]="0";
 Reject(()=>Build(s,Fixture.Request(s,"SubmitReview")));
});
Test("legacy precision and status survive notes only saves",()=> {
 var s=Fixture.Create("Turbidity","1.234567");s.Results.Rows[0]["ResultStatus"]="Historical PASS";
 var p=Build(s,Fixture.Edit(Fixture.Request(s),0,x=>x with{Remarks="retained measurement"}));
 Check(!p.Rows[0].WriteResult && p.Rows[0].ResultValue=="1.234567" && p.Rows[0].ResultStatus=="Historical PASS");
 Check(!p.MarkAnalysisCompleted);
});
Test("changed measurements cannot round or silently clear",()=> {
 var s=Fixture.Create("Turbidity","1");var r=Fixture.Request(s);
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="1.23456"})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue=""})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="1,2"})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="0.5"})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="26"})));
 Check(Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="1.2345"})).Rows[0].ResultValue=="1.2345");
});
Test("qualitative displays store raw numeric codes",()=> {
 var s=Fixture.Create();var r=Fixture.Request(s);var p=Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="Clear and Colorless"}));
 Check(p.Rows[0].ResultValue=="0" && p.Rows[0].RawResultSnapshot=="0" && p.Status=="Results Entered");
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="Colored"})));
 p=Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="Colored",Remarks="investigate"}));Check(p.Status=="Under Investigation" && p.Rows[0].ResultValue=="1");
});
Test("frozen legacy profile uses actual sample type and point",()=> {
 var s=Fixture.Create("pH Value",null);Fixture.ConfigurePh(s);s.Results.Rows[0]["HasSpecSnapshot"]=0;s.Results.Rows[0]["AlertLimit"]=DBNull.Value;s.Results.Rows[0]["ActionLimit"]=DBNull.Value;
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="6",EquipmentId=7,LesVerificationConfirmed=true,LesVerificationReference="verified"});Check(Build(s,r).Rows[0].ResultStatus=="PASS");
 s.Header.Rows[0]["SampleType"]="Potable Water";r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="6",EquipmentId=7,LesVerificationConfirmed=true,LesVerificationReference="verified",Remarks="below lower bound"});Check(Build(s,r).Rows[0].ResultStatus=="OOS");
 s.Results.Rows[0]["HasSpecSnapshot"]=1;s.Results.Rows[0]["AlertLimit"]=5m;s.Results.Rows[0]["ActionLimit"]=7m;r=r with{ExpectedSnapshot=s.Hash};Check(Build(s,r).Rows[0].ResultStatus=="PASS");
});
Test("numeric inclusive range and alert remarks",()=> {
 var s=Fixture.Create("pH Value",null);Fixture.ConfigurePh(s);
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="7",EquipmentId=7,LesVerificationConfirmed=true,LesVerificationReference="verified"});Check(Build(s,r).Rows[0].ResultStatus=="PASS");
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="7.1"})));
 s.Results.Rows[0]["TestName"]="Turbidity";s.Results.Rows[0]["TestID"]=9;s.Results.Rows[0]["AlertLimit"]=5m;s.Results.Rows[0]["ActionLimit"]=10m;
 r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="6"});Reject(()=>Build(s,r));
 Check(Build(s,Fixture.Edit(r,0,x=>x with{Remarks="alert"})).Status=="Alert");
});
Test("approved comparator and absence inputs retain controlled binary codes",()=> {
 var s=Fixture.Create("Nitrate",null);s.Results.Rows[0]["ActionLimit"]=DBNull.Value;s.Results.Rows[0]["LimitDescription"]="Record Complies/Does Not Comply against approved comparator";
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="Complies",TestKitCode="K",TestKitLot="L",TestKitExpiry=Fixture.Now});
 Check(Build(s,r).Rows[0].ResultValue=="0");Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="2"})));
 s=Fixture.Create("E. coli",null);s.Results.Rows[0]["Unit"]="Absence";Fixture.AddEquipment(s);
 r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="Absence",EquipmentId=7});Check(Build(s,r).Rows[0].ResultValue=="0");
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{ResultValue="2"})));
});
Test("legacy potable hardness fallback binds actual sampling point",()=> {
 var s=Fixture.Create("Hardness",null);s.Header.Rows[0]["SampleType"]="Potable Water";s.Header.Rows[0]["EffectivePointCode"]="PTWS-05";s.Results.Rows[0]["HasSpecSnapshot"]=0;
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="10",TestKitCode="K",TestKitLot="L",TestKitExpiry=Fixture.Now,Remarks="soft point limit exceeded"});Check(Build(s,r).Rows[0].ResultStatus=="OOS");
 s.Header.Rows[0]["EffectivePointCode"]="PTWS-04";r=r with{ExpectedSnapshot=s.Hash};Check(Build(s,r).Rows[0].ResultStatus=="PASS");
});
Test("support resources reject duplicate primary foreign and expired selections",()=> {
 var s=Fixture.Create("Residue on Evaporation",null);Fixture.AddEquipment(s);s.Equipment.Rows[0]["EquipmentName"]="Balance";s.Equipment.Rows[0]["EquipmentType"]="Balance";
 s.Equipment.Rows.Add(8,"E8","Oven","Oven",true,"Active","Qualified",Fixture.Now.Date,true,"Calibrated",Fixture.Now.Date);
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="0.5",EquipmentId=7,SupportingEquipmentCodes="E8"});Check(Build(s,r).Rows[0].SupportingEquipmentIds.SequenceEqual(new[]{8}));
 foreach(string support in new[]{"E8,E8","E7","foreign"})Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{SupportingEquipmentCodes=support})));
 s.Equipment.Rows[1]["NextQualificationDate"]=Fixture.Now.AddDays(-1);Reject(()=>Build(s,r));
});
Test("pending only freezes all completed evidence",()=> {
 var s=Fixture.Create("Appearance","0");s.Header.Rows[0]["Status"]="Under Investigation";
 var r=Fixture.Request(s);var mutations=new Func<WaterWorkflowRow,WaterWorkflowRow>[] {x=>x with{ResultValue="Colored"},x=>x with{Remarks="alter"},x=>x with{EquipmentId=7},x=>x with{SupportingEquipmentCodes="E7"},x=>x with{TestKitCode="kit"},x=>x with{TestKitLot="lot"},x=>x with{TestKitExpiry=Fixture.Now},x=>x with{LesSampleTemperatureC="25"},x=>x with{LesVerificationReference="ref"},x=>x with{LesVerificationConfirmed=true},x=>x with{LesExecutionRemarks="alter"}};
 foreach(var mutation in mutations)Reject(()=>Build(s,Fixture.Edit(r,0,mutation)));
 Check(!Build(s,r).Rows[0].WriteEvidence);
});
Test("released investigation remains released unless new pending OOS",()=> {
 var s=Fixture.Create("Appearance","0");s.Header.Rows[0]["Status"]="Released After Investigation";Fixture.AddResult(s,2,"Appearance",null);
 var r=Fixture.Edit(Fixture.Request(s),1,x=>x with{ResultValue="Clear"});Check(Build(s,r).Status=="Released After Investigation");
 r=Fixture.Edit(r,1,x=>x with{ResultValue="Colored",Remarks="new finding"});Check(Build(s,r).Status=="Under Investigation");
});
Test("kit evidence uses database clock and no unrelated instruments",()=> {
 var s=Fixture.Create("Residual Chlorine",null);s.Results.Rows[0]["AlertLimit"]=0.2m;s.Results.Rows[0]["ActionLimit"]=0.5m;
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="0.3",TestKitCode="K",TestKitLot="L",TestKitExpiry=Fixture.Now.Date});Check(Build(s,r).Rows[0].TestKitCode=="K");
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{TestKitExpiry=Fixture.Now.Date.AddDays(-1)})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{EquipmentId=7})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{TestKitLot=""})));
});
Test("fresh locked equipment readiness and compatibility",()=> {
 var s=Fixture.Create("pH Value",null);Fixture.ConfigurePh(s);
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="6",EquipmentId=7,LesVerificationConfirmed=true,LesVerificationReference="V"});Check(Build(s,r).Rows[0].Execution is not null);
 s.Equipment.Rows[0]["NextCalibrationDate"]=Fixture.Now.Date.AddDays(-1);Reject(()=>Build(s,r));
 s.Equipment.Rows[0]["NextCalibrationDate"]=Fixture.Now.Date;s.Compatibility.Rows.Add("WATER","TESTID:2",true,"Balance");Reject(()=>Build(s,r));
});
Test("conductivity LES bounds verification precision and expiry",()=> {
 var s=Fixture.Create("Conductivity",null);Fixture.ConfigurePh(s);s.Results.Rows[0]["TestID"]=3;s.Results.Rows[0]["AlertLimit"]=DBNull.Value;s.Results.Rows[0]["ActionLimit"]=1.3m;s.Equipment.Rows[0]["EquipmentName"]="Conductivity meter";
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="1.1",EquipmentId=7,LesSampleTemperatureC="25.01",LesVerificationConfirmed=true,LesVerificationReference="V"});Check(Build(s,r).Rows[0].Execution!.SampleTemperatureC=="25.01");
 foreach(string t in new[]{"23.99","26.01","25.001","25,00","2.5e1"})Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{LesSampleTemperatureC=t})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{LesVerificationConfirmed=false})));
 Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{LesVerificationReference=""})));
});
Test("microbiology cannot bypass locked incubation",()=> {
 var s=Fixture.Create();s.Results.Rows[0]["Category"]="Microbiology";
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="Clear"});Reject(()=>Build(s,r));
 s.Header.Rows[0]["IncubationEndDate"]=Fixture.Now.AddDays(1);Reject(()=>Build(s,Fixture.Request(s) with{Results=r.Results}));
 s.Header.Rows[0]["IncubationEndDate"]=Fixture.Now.AddDays(-1);Check(Build(s,Fixture.Request(s) with{Results=r.Results}).MarkAnalysisCompleted);
});
Test("submit review approve reject unsaved evidence and enforce source states",()=> {
 foreach(var pair in new[]{("SubmitReview","Results Entered","Under Review"),("Review","Under Review","Reviewed"),("Approve","Reviewed","Approved")}) {
  var s=Fixture.Create("Appearance","0");s.Header.Rows[0]["Status"]=pair.Item2;var r=Fixture.Request(s,pair.Item1);Check(Build(s,r).Status==pair.Item3);
  Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{Remarks="unsaved"})));
  Reject(()=>Build(s,Fixture.Edit(r,0,x=>x with{LesVerificationReference="unsaved"})));
 }
 var wrong=Fixture.Create("Appearance","0");Reject(()=>Build(wrong,Fixture.Request(wrong,"Approve")));
});
Test("quality gate open and unlinked OOS fail closed",()=> {
 var s=Fixture.Create("Appearance","1");s.Results.Rows[0]["Remarks"]="investigated";s.Results.Rows[0]["ResultStatus"]="OOS";s.Header.Rows[0]["Status"]="Results Entered";
 Reject(()=>Build(s,Fixture.Request(s,"SubmitReview")));s.QualityEvents.Rows.Add(1,"Open","Accept with Justification");s.AffectedResults.Rows.Add(1,1);Reject(()=>Build(s,Fixture.Request(s,"SubmitReview")));
 s.QualityEvents.Rows[0]["CurrentStatus"]="QA Closed";Reject(()=>Build(s,Fixture.Request(s,"SubmitReview")));
 s.QualityEvents.Rows[0]["CurrentStatus"]="Closed";Check(Build(s,Fixture.Request(s,"SubmitReview")).Status=="Under Review");
 s.QualityEvents.Rows[0]["FinalDisposition"]="Unapproved";Reject(()=>Build(s,Fixture.Request(s,"SubmitReview")));
});
Test("start chronology future boundary and existing start retained",()=> {
 var s=Fixture.Create();s.Header.Rows[0]["Status"]="Registered";s.Header.Rows[0]["AnalysisStartedDateTime"]=DBNull.Value;
 var r=Fixture.Request(s,"StartAnalysis") with{AnalysisStartedAt=Fixture.Now};Check(Build(s,r).Status=="In Analysis");
 Reject(()=>Build(s,r with{AnalysisStartedAt=Fixture.Now.AddMinutes(2)}));Reject(()=>Build(s,r with{AnalysisStartedAt=Fixture.Now.AddDays(-5)}));
 s.Header.Rows[0]["AnalysisStartedDateTime"]=Fixture.Now.AddHours(-2);r=r with{ExpectedSnapshot=s.Hash};Check(Build(s,r).AnalysisStartedAt==Fixture.Now.AddHours(-2));
});
Test("correct four editable times derives duration and protects completion certificates",()=> {
 var s=Fixture.Create();s.Header.Rows[0]["IncubationStartedDateTime"]=Fixture.Now.AddDays(-3);s.Header.Rows[0]["IncubationEndDate"]=Fixture.Now.AddDays(-1);
 var times=new WaterWorkflowTimes(Fixture.Now.AddDays(-5),Fixture.Now.AddDays(-4),Fixture.Now.AddDays(-2),Fixture.Now,Fixture.Now.AddHours(-1));
 var r=Fixture.Request(s,"CorrectTimes") with{Times=times};Check(Build(s,r).Times!.IncubationEndDate==Fixture.Now);
 Reject(()=>Build(s,r with{Times=times with{IncubationEndDate=Fixture.Now.AddMinutes(1)}}));
 Reject(()=>Build(s,r with{Times=times with{ReceivedDateTime=Fixture.Now.AddDays(-6)}}));
 s.Header.Rows[0]["AnalysisCompletedDateTime"]=Fixture.Now.AddHours(-2);Reject(()=>Build(s,r with{ExpectedSnapshot=s.Hash}));
 s.Header.Rows[0]["AnalysisCompletedDateTime"]=DBNull.Value;s.Certificates.Rows.Add(1,false);Reject(()=>Build(s,r with{ExpectedSnapshot=s.Hash}));
});
Test("frozen registered point beats renamed master for legacy hardness",()=> {
 var s=Fixture.Create("Hardness",null);s.Header.Columns.Add("PointCode",typeof(string));
 s.Header.Rows[0]["SampleType"]="Potable Water";s.Header.Rows[0]["EffectivePointCode"]="PTWS-05";
 s.Header.Rows[0]["PointCode"]="PTWS-04";s.Results.Rows[0]["HasSpecSnapshot"]=0;
 var r=Fixture.Edit(Fixture.Request(s),0,x=>x with{ResultValue="10",Remarks="soft water investigation",TestKitCode="K",TestKitLot="L",TestKitExpiry=Fixture.Now.AddDays(1)});
 Check(Build(s,r).Rows[0].ResultStatus=="OOS");
});
Test("pending completion preserves frozen remarks whitespace",()=> {
 var s=Fixture.Create("Appearance","0");s.Header.Rows[0]["Status"]="Under Investigation";
 s.Results.Rows[0]["Remarks"]=" historical remark ";Fixture.AddResult(s,2,"Appearance",null);
 var r=Fixture.Edit(Fixture.Request(s),1,x=>x with{ResultValue="Clear"});
 var p=Build(s,r);Check(p.Rows[0].Remarks==" historical remark " && !p.Rows[0].WriteResult && !p.Rows[0].WriteEvidence);
});
Console.WriteLine($"{passed} Water workflow domain regression groups passed.");

static class Fixture
{
 public static readonly DateTime Now=new(2026,10,28,12,0,0,DateTimeKind.Unspecified);
 private static DataTable Table(params (string,Type)[] cols) {var t=new DataTable();foreach(var c in cols)t.Columns.Add(c.Item1,c.Item2);return t;}
 public static WaterWorkflowSnapshot Create(string name="Appearance",string? value=null)
 {
  var h=Table(("SampleID",typeof(int)),("SampleNumber",typeof(string)),("SampleType",typeof(string)),("EffectivePointCode",typeof(string)),("Status",typeof(string)),("SamplingDateTime",typeof(DateTime)),("ReceivedDateTime",typeof(DateTime)),("IncubationStartedDateTime",typeof(DateTime)),("IncubationEndDate",typeof(DateTime)),("AnalysisStartedDateTime",typeof(DateTime)),("AnalysisCompletedDateTime",typeof(DateTime)));
  h.Rows.Add(1,"W1","Purified Water","PWS-01","In Analysis",Now.AddDays(-4),Now.AddDays(-3),DBNull.Value,DBNull.Value,Now.AddHours(-1),DBNull.Value);
  var results=Table(("SampleTestID",typeof(int)),("SampleID",typeof(int)),("TestID",typeof(int)),("ResultRowVersion",typeof(byte[])),("TestName",typeof(string)),("Unit",typeof(string)),("AlertLimit",typeof(decimal)),("ActionLimit",typeof(decimal)),("HasSpecSnapshot",typeof(int)),("ResultValue",typeof(string)),("Remarks",typeof(string)),("ResultStatus",typeof(string)),("DeviationType",typeof(string)),("LimitDescription",typeof(string)),("EquipmentID",typeof(int)),("Category",typeof(string)));
  var assignment=Table(("ResultRecordID",typeof(int)),("EquipmentID",typeof(int)),("HistoryID",typeof(int)));
  var resource=Table(("SampleTestID",typeof(int)),("ResourceKind",typeof(string)),("EquipmentID",typeof(int)),("EquipmentCodeSnapshot",typeof(string)),("KitCode",typeof(string)),("KitLot",typeof(string)),("KitExpiry",typeof(DateTime)));
  var execution=Table(("SampleTestID",typeof(int)),("SampleTemperatureC",typeof(decimal)),("VerificationReference",typeof(string)),("VerificationConfirmed",typeof(bool)),("ExecutionRemarks",typeof(string)));
  var equipment=Table(("EquipmentID",typeof(int)),("EquipmentCode",typeof(string)),("EquipmentName",typeof(string)),("EquipmentType",typeof(string)),("IsActive",typeof(bool)),("EquipmentStatus",typeof(string)),("QualificationStatus",typeof(string)),("NextQualificationDate",typeof(DateTime)),("CalibrationRequired",typeof(bool)),("CalibrationStatus",typeof(string)),("NextCalibrationDate",typeof(DateTime)));
  var compat=Table(("Module",typeof(string)),("MatchKey",typeof(string)),("IsActive",typeof(bool)),("EquipmentType",typeof(string)));
  var quality=Table(("QualityEventID",typeof(int)),("CurrentStatus",typeof(string)),("FinalDisposition",typeof(string)));
  var affected=Table(("QualityEventID",typeof(int)),("SampleTestID",typeof(int)));
  var signatures=Table(("SignatureID",typeof(int)),("SignedBy",typeof(string)),("Meaning",typeof(string)));
  var certificates=Table(("CertificateID",typeof(int)),("IsCancelled",typeof(bool)));
  var planSamples=Table(("WaterPlanSampleID",typeof(int)),("WaterPlanID",typeof(int)),("SampleID",typeof(int)),("Status",typeof(string)));
  var plans=Table(("WaterPlanID",typeof(int)),("Status",typeof(string)),("UnrelatedCount",typeof(int)));
  var clock=Table(("DatabaseNow",typeof(DateTime)));clock.Rows.Add(Now);
  var s=new WaterWorkflowSnapshot(new[]{h,results,assignment,resource,execution,equipment,compat,quality,affected,signatures,certificates,planSamples,plans,clock});AddResult(s,1,name,value);return s;
 }
 public static void AddResult(WaterWorkflowSnapshot s,int id,string name,string? value)=>s.Results.Rows.Add(id,1,9,new byte[8],name,"",DBNull.Value,name=="Turbidity"?25m:1m,1,value is null?DBNull.Value:value,"",value is null?"Pending":"PASS","","Approved specification",DBNull.Value,"Chemical");
 public static WaterWorkflowRequest Request(WaterWorkflowSnapshot s,string op="SaveResults")=>new("server","EQ001_UAT_test",op,1,"W1",Convert.ToString(s.Header.Rows[0]["Status"])!,s.Hash,null,null,op is "StartAnalysis" or "CorrectTimes"?Array.Empty<WaterWorkflowRow>():WaterWorkflowValidator.DisplayRows(s));
 public static WaterWorkflowRequest Edit(WaterWorkflowRequest r,int index,Func<WaterWorkflowRow,WaterWorkflowRow> edit)=>r with{Results=r.Results.Select((row,i)=>i==index?edit(row):row).ToArray()};
 public static void AddEquipment(WaterWorkflowSnapshot s)=>s.Equipment.Rows.Add(7,"E7","pH Meter","Meter",true,"Active","Qualified",Now.Date,true,"Calibrated",Now.Date);
 public static void ConfigurePh(WaterWorkflowSnapshot s) {s.Results.Rows[0]["TestID"]=2;s.Results.Rows[0]["AlertLimit"]=5m;s.Results.Rows[0]["ActionLimit"]=7m;AddEquipment(s);}
}
