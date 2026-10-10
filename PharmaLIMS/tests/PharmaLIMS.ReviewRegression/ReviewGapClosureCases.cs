using System.Data;
using System.Globalization;
using PharmaLIMS.Services;
internal static partial class Program
{
    private static void RunReviewGapClosureCases()
    {
        foreach(string value in new[]{"24","25","26","25.01","25.000"})
            Run("F08 exact LES temperature "+value,()=>Equal(true,WaterLesTemperatureContract.TryParse(value,out _)));
        foreach(string value in new[]{"2,5","0,025","25,000","25.004","23.99","26.01","2.5e1","NaN","","25-"})
            Run("F08 ambiguous/lossy LES temperature "+value,()=>Equal(false,WaterLesTemperatureContract.TryParse(value,out _)));
        foreach(string culture in new[]{"en-US","de-DE","ar-YE"}) Run("F08 temperature locale "+culture,()=>{
            var original=CultureInfo.CurrentCulture;try{CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
            Equal(false,WaterLesTemperatureContract.TryParse("2,5",out _));Equal(true,WaterLesTemperatureContract.TryParse("25.01",out decimal value));Equal(25.01m,value);
            }finally{CultureInfo.CurrentCulture=original;}});
        foreach(string field in new[]{"EquipmentID","EquipmentHistoryID","ResourceEvidenceID","ExecutionEvidenceID"}) Run("F03 resource-only conflict "+field,()=>{
            DataTable original=new();original.Columns.Add("SampleTestID",typeof(int));original.Columns.Add("SampleID",typeof(int));
            foreach(string col in new[]{"EquipmentID","EquipmentHistoryID","ResourceEvidenceID","ExecutionEvidenceID"})original.Columns.Add(col,typeof(long));
            original.Rows.Add(1,7,3L,10L,20L,30L);var current=original.Copy();current.Rows[0][field]=99L;
            Throws<DBConcurrencyException>(()=>ResultSnapshotGuard.EnsureMatches(original,current,"SampleTestID","SampleID",7));});
        foreach(string context in new[]{"GROWTH CONDITIONS 30-35","GROWTH CONDITIONS 30-35 C FOR 48 HOURS"}){
            Run("F21 numeric growth context pass "+context,()=>Equal("Conforms",PrmResultInterpretationEvaluator.Evaluate("Numeric","NMT 10 CFU/g; "+context,"5","","TAMC","Total Aerobic Microbial Count")));
            Run("F21 numeric growth context excursion "+context,()=>Equal("Does Not Conform",PrmResultInterpretationEvaluator.Evaluate("Numeric","NMT 10 CFU/g; "+context,"11","","TAMC","Total Aerobic Microbial Count")));}
        Run("F20 personnel key retains controlled identity",()=>Equal("EMP-1",EmPersonnelHandoffContract.EmployeeKey("Personnel Monitoring"," emp-1 ")));
        Run("F20 non-personnel has no employee grouping",()=>Equal("",EmPersonnelHandoffContract.EmployeeKey("Active Air Sampling","emp-1")));
        Run("F20 distinct personnel form distinct events",()=>Equal(false,EmPersonnelHandoffContract.EmployeeKey("Personnel Monitoring","EMP-1")==EmPersonnelHandoffContract.EmployeeKey("Personnel Monitoring","EMP-2")));
        Run("F20 absent identity blocked",()=>Throws<InvalidOperationException>(()=>EmPersonnelHandoffContract.EmployeeKey("Personnel Monitoring","")));
        Run("F20 consistent personnel release",()=>EmPersonnelHandoffContract.Validate(new[]{("EMP-1","Recorded Name"),("emp-1","Recorded Name")}));
        Run("F20 conflicting names blocked",()=>Throws<InvalidOperationException>(()=>EmPersonnelHandoffContract.Validate(new[]{("EMP-1","First"),("EMP-1","Second")})));
        Run("F20 identity overflow blocked",()=>Throws<InvalidOperationException>(()=>EmPersonnelHandoffContract.Validate(new[]{(new string('A',51),"Name")})));
        Run("F20 name overflow blocked",()=>Throws<InvalidOperationException>(()=>EmPersonnelHandoffContract.Validate(new[]{("EMP-1",new string('A',151))})));
        Run("F09 numeric limit exact schema value",()=>SpecificationNumericStorage.EnsureExact(1.001m));
        Run("F09 numeric limit loses precision blocked",()=>Throws<InvalidOperationException>(()=>SpecificationNumericStorage.EnsureExact(1.0001m)));
        Run("F09 numeric limit schema overflow blocked",()=>Throws<InvalidOperationException>(()=>SpecificationNumericStorage.EnsureExact(1000000000000000m)));
        Run("F09 negative numeric limit blocked",()=>Throws<InvalidOperationException>(()=>SpecificationNumericStorage.EnsureExact(-1m)));
        byte[] version={1,2,3,4,5,6,7,8};
        Run("F15 unsigned current preparation amendable",()=>CultureMediaPreparationGuard.EnsureAmendable(version,(byte[])version.Clone(),false));
        Run("F15 signed preparation immutable",()=>Throws<InvalidOperationException>(()=>CultureMediaPreparationGuard.EnsureAmendable(version,version,true)));
        Run("F15 concurrent preparation edit rejected",()=>Throws<DBConcurrencyException>(()=>CultureMediaPreparationGuard.EnsureAmendable(version,new byte[8],false)));
        Run("F15 absent preparation baseline rejected",()=>Throws<DBConcurrencyException>(()=>CultureMediaPreparationGuard.EnsureAmendable(null,version,false)));
        Run("F18 approved exact air result printable",()=>EmReportEvidenceGuard.EnsureConsistent(ApprovedAirReport()));
        foreach(string column in new[]{"ResultCFU","Status","ResultCalculationVersion","TotalCount","EvidenceComplete"}) Run("F18 inconsistent approved evidence "+column,()=>{
            var table=ApprovedAirReport();table.Rows[0][column]=column switch{"Status"=>"OOS","ResultCalculationVersion"=>2,"TotalCount"=>1.5m,"EvidenceComplete"=>0,_=>1m};
            Throws<InvalidOperationException>(()=>EmReportEvidenceGuard.EnsureConsistent(table));});
        Run("F18 legacy matching value no fabricated version",()=>{var table=ApprovedAirReport();table.Rows[0]["ResultCalculationVersion"]=DBNull.Value;
            EmReportEvidenceGuard.EnsureConsistent(table);Equal(DBNull.Value,table.Rows[0]["ResultCalculationVersion"]);});
        foreach(string summary in new[]{"OOS","Alert","Pending"}) Run("F18 inconsistent stored event summary "+summary,()=>{var table=ApprovedAirReport();table.Rows[0]["FinalResult"]=summary;
            Throws<InvalidOperationException>(()=>EmReportEvidenceGuard.EnsureConsistent(table));});
        Run("F18 legacy workflow summary retained",()=>{var table=ApprovedAirReport();table.Rows[0]["FinalResult"]="Approved";EmReportEvidenceGuard.EnsureConsistent(table);});
        Run("F18 event pass cannot mask OOS plates",()=>{var table=ApprovedAirReport();table.Rows[0]["TotalCount"]=6m;table.Rows[0]["ResultCFU"]=24m;table.Rows[0]["Status"]="OOS";
            Throws<InvalidOperationException>(()=>EmReportEvidenceGuard.EnsureConsistent(table));});
        Run("F20 whitespace method preserves employee",()=>Equal("EMP-1",EmPersonnelHandoffContract.EmployeeKey(" Personnel Monitoring ","EMP-1")));
        Run("F20 whitespace method requires employee",()=>Throws<InvalidOperationException>(()=>EmPersonnelHandoffContract.EmployeeKey(" Personnel Monitoring ","")));
        Run("F18 no approved plates rejected",()=>Throws<InvalidOperationException>(()=>EmReportEvidenceGuard.EnsureConsistent(ApprovedAirReport().Clone())));
        Run("F19 changed visible equipment rejected",()=>{var t=ApprovedAirReport();t.Columns.Add("Id",typeof(int));t.Columns.Add("EquipmentID",typeof(int));t.Rows[0]["Id"]=1;t.Rows[0]["EquipmentID"]=3;
            Throws<DBConcurrencyException>(()=>EmQualityEventEvidenceGuard.EnsureEquipmentSaved(t,new Dictionary<int,int?>{{1,4}}));});
    }
    private static DataTable ApprovedAirReport()
    {
        DataTable t=new();t.Columns.Add("Method",typeof(string));t.Columns.Add("FinalResult",typeof(string));t.Columns.Add("TotalCount",typeof(decimal));t.Columns.Add("ResultCFU",typeof(decimal));t.Columns.Add("Status",typeof(string));
        t.Columns.Add("ResultCalculationVersion",typeof(short));t.Columns.Add("EvidenceComplete",typeof(int));t.Columns.Add("AlertLimitSnapshot",typeof(decimal));t.Columns.Add("ActionLimitSnapshot",typeof(decimal));
        t.Columns.Add("ResultUnitSnapshot",typeof(string));t.Columns.Add("AirVolumeLitersSnapshot",typeof(int));t.Rows.Add("Active Air Sampling","Results Entered",1m,4m,"PASS",(short)1,1,10m,20m,"CFU/m3",250);return t;
    }
}
