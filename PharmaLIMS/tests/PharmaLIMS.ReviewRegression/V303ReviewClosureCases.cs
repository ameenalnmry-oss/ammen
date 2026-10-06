using System.Data;
using System.Globalization;
using PharmaLIMS.Services;

internal static partial class Program
{
    private static void RunV303ReviewClosureCases()
    {
        foreach (string input in new[] { "-1", "-0.1", "1,3", "1,300", "1,234.5", "1.234,5", "NaN", "Infinity", "", "1e-29", "0.00000000000000000000000000001", "100000000000000000000000000000" })
            Run("v303 water invalid input " + input, () => Equal(false, WaterNumericResultEvaluator.TryParseNonnegative(input, out _)));
        foreach (string input in new[] { "-2", "2", "0.1", "1,0" })
            Run("v303 pathogen binary input rejected " + input, () => Equal(false, WaterNumericResultEvaluator.TryParseBinary(input, out _)));
        foreach (string input in new[] { "0", "1", "1.0" })
            Run("v303 binary controlled value " + input, () => Equal(true, WaterNumericResultEvaluator.TryParseBinary(input, out _)));
        Run("v303 scientific input preserves exact value", () => { Equal(true, WaterNumericResultEvaluator.TryParseNonnegative("1e-1", out decimal v)); Equal(0.1m, v); });
        Run("v303 decimal with redundant zeros preserves value", () => { Equal(true, ControlledNumericValue.TryParse("0.10000000000000000000000000000", out decimal v)); Equal(0.1m, v); });
        Run("v303 parser preserves decimal maximum", () => { Equal(true, ControlledNumericValue.TryParse(decimal.MaxValue.ToString(CultureInfo.InvariantCulture), out decimal v)); Equal(decimal.MaxValue,v); });
        Run("v303 parser preserves minimum exact magnitude", () => { Equal(true, ControlledNumericValue.TryParse("1e-28",out decimal v)); Equal(0.0000000000000000000000000001m,v); });
        foreach (string culture in new[] { "en-US", "de-DE", "fr-FR", "ar-YE" })
            Run("v303 parsing independent of locale " + culture, () => {
                CultureInfo previous=CultureInfo.CurrentCulture;
                try { CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(culture);
                    Equal(false,ControlledNumericValue.TryParse("1,3",out _));
                    Equal(true,ControlledNumericValue.TryParse("1.3",out decimal v));Equal(1.3m,v);
                } finally {CultureInfo.CurrentCulture=previous;}
            });
        foreach (string name in new[] { "Total Organic Carbon (TOC)", "Heavy Metals (as Pb)", "pH Value", "Residual Chlorine" })
            Run("v303 incomplete numeric water cannot pass " + name, () => Equal("NOT ASSESSED",WaterNumericResultEvaluator.Evaluate(name,9000m,null,null)));
        Run("v303 negative numeric cannot pass", () => Equal("Invalid",WaterNumericResultEvaluator.Evaluate("TAMC",-1m,null,100m)));
        foreach (string name in new[] { "pH", "pH Value", "Residual Chlorine", "Free Chlorine", "Chlorine" })
        {
            Run("v303 range lower violation " + name,()=>Equal("OOS",WaterNumericResultEvaluator.Evaluate(name,0.1999m,0.2m,0.5m)));
            Run("v303 range lower equality " + name,()=>Equal("PASS",WaterNumericResultEvaluator.Evaluate(name,0.2m,0.2m,0.5m)));
            Run("v303 range midpoint " + name,()=>Equal("PASS",WaterNumericResultEvaluator.Evaluate(name,0.3m,0.2m,0.5m)));
            Run("v303 range upper equality " + name,()=>Equal("PASS",WaterNumericResultEvaluator.Evaluate(name,0.5m,0.2m,0.5m)));
            Run("v303 range upper violation " + name,()=>Equal("OOS",WaterNumericResultEvaluator.Evaluate(name,0.5001m,0.2m,0.5m)));
            Run("v303 range missing lower " + name,()=>Equal("NOT ASSESSED",WaterNumericResultEvaluator.Evaluate(name,0.3m,null,0.5m)));
        }
        Run("v303 action equality is NMT",()=>Equal("ALERT",WaterNumericResultEvaluator.Evaluate("TAMC",100m,25m,100m)));
        Run("v303 upper numeric missing action is unassessed",()=>Equal("NOT ASSESSED",WaterNumericResultEvaluator.Evaluate("TAMC",1m,25m,null)));
        Run("v303 water master recognizes TOC numeric",()=>Equal(true,WaterNumericResultEvaluator.RequiresNumericLimits("TOC","ppb","NMT 500 ppb")));
        Run("v303 water controlled comparator needs no fabricated limit",()=>Equal(false,WaterNumericResultEvaluator.RequiresNumericLimits("Heavy Metals (as Pb)","ppm","Record Complies/Does Not Comply against the approved comparator")));
        Run("v303 chlorine trend preserves lower failure",()=>Equal("FAIL",TrendReportData.WaterStatus("OOS","Residual Chlorine",0.05,0.2,0.5)));
        Run("v303 chlorine trend within range",()=>Equal("PASS",TrendReportData.WaterStatus("PASS","Residual Chlorine",0.3,0.2,0.5)));
        Run("v303 historical failure missing limits remains visible",()=>Equal("FAIL",TrendReportData.WaterStatus("OOS","TAMC",1,null,null)));
        Run("v303 water trend compares exact large decimals",()=>Equal("FAIL",TrendReportData.WaterStatusExact("OOS","TAMC",9999999999999.0001m,null,9999999999999m)));
        Run("v303 GPT out-of-schema limits rejected",()=>Equal(false,MediaGrowthPromotionEvaluator.Passes("100","50",0m,decimal.MaxValue)));
        foreach (string unit in new[]{"Bottle","bottle","BOTTLE"})
        {
            Run("v303 bottle below boundary " + unit,()=>Equal("Conforms",PrmResultInterpretationEvaluator.Evaluate("Numeric","NMT 100 CFU/"+unit,"99.9999","100","TAMC","Total Aerobic Microbial Count")));
            Run("v303 bottle at boundary " + unit,()=>Equal("Conforms",PrmResultInterpretationEvaluator.Evaluate("Numeric","NMT 100 CFU/"+unit,"100","100","TAMC","Total Aerobic Microbial Count")));
            Run("v303 bottle above boundary " + unit,()=>Equal("Does Not Conform",PrmResultInterpretationEvaluator.Evaluate("Numeric","NMT 100 CFU/"+unit,"100.0001","100","TAMC","Total Aerobic Microbial Count")));
        }
        foreach (string required in new[]{"Present","Absent","Not Detected","Not present"})
        {
            bool wantsPresent=required=="Present";
            Run("v303 qualitative expected presence " + required,()=>Equal(wantsPresent?"Conforms":"Does Not Conform",PrmResultInterpretationEvaluator.Evaluate("Presence/Absence",required,"Present")));
            Run("v303 qualitative expected absence " + required,()=>Equal(wantsPresent?"Does Not Conform":"Conforms",PrmResultInterpretationEvaluator.Evaluate("Presence/Absence",required,"Absent")));
        }
        foreach(string spec in new[]{"", "Per SOP", "Absent or Present", "Present not allowed", "Present except..."})
            Run("v303 ambiguous qualitative rule " + spec,()=>Equal("Check Required",PrmResultInterpretationEvaluator.Evaluate("Qualitative",spec,"Absent")));
        foreach(string input in new[]{"-1","< -1","1,3","100.000000000001","1e-11","10000000000000000000000000000"})
            Run("v303 import invalid or unrepresentable " + input,()=>Equal(false,ExternalTrendNumericContract.TryParseQualified(input,out _,out _)));
        Run("v303 import 10 places exact",()=>{Equal(true,ExternalTrendNumericContract.TryParseQualified("100.0000000001",out decimal v,out _));Equal(100.0000000001m,v);});
        Run("v303 import zero tail can be preserved",()=>{Equal(true,ExternalTrendNumericContract.TryParseQualified("100.000000000000",out decimal v,out _));Equal(100m,v);});
        Run("v303 import qualifier remains separate",()=>{Equal(true,ExternalTrendNumericContract.TryParseQualified("≤10",out decimal v,out string? q));Equal(10m,v);Equal("<=",q);});
        Run("v303 negative count never assessed PASS",()=>Equal("UNASSESSED",ExternalTrendNumericContract.EvaluateUpper(-1m,null,10m,20m)));
        Run("v303 upper classifier below action",()=>Equal("PASS",ExternalTrendNumericContract.EvaluateUpper(1m,null,10m,20m)));
        Run("v303 greater-than action equality is a failure",()=>Equal("FAIL",ExternalTrendNumericContract.EvaluateUpper(20m,">",10m,20m)));
        Run("v303 greater-or-equal boundary is unresolved",()=>Equal("UNASSESSED",ExternalTrendNumericContract.EvaluateUpper(20m,">=",10m,20m)));
        Run("v303 GPT below minimum cannot pass after rounding",()=>Equal(false,MediaGrowthPromotionEvaluator.Passes("100000","49999",50m,200m)));
        Run("v303 GPT above maximum cannot pass after rounding",()=>Equal(false,MediaGrowthPromotionEvaluator.Passes("100000","200001",50m,200m)));
        Run("v303 GPT minimum equality passes",()=>Equal(true,MediaGrowthPromotionEvaluator.Passes("100","50",50m,200m)));
        Run("v303 GPT maximum equality passes",()=>Equal(true,MediaGrowthPromotionEvaluator.Passes("100","200",50m,200m)));
        Run("v303 GPT excess count precision rejected",()=>Equal(false,MediaGrowthPromotionEvaluator.TryCalculate("100","49.999",out _)));
        Run("v303 GPT missing approved limits rejected",()=>Equal(false,MediaGrowthPromotionEvaluator.Passes("100","50",null,200m)));
        Run("v303 saved EM evidence accepted",()=> {DataTable t=SavedEm();EmQualityEventEvidenceGuard.EnsureSaved(t.Copy(),t,7,new[]{(1,(int?)21,"Saved remark")});});
        Run("v303 unsaved EM count rejected",()=> {DataTable t=SavedEm();Throws<DBConcurrencyException>(()=>EmQualityEventEvidenceGuard.EnsureSaved(t.Copy(),t,7,new[]{(1,(int?)22,"Saved remark")}));});
        Run("v303 unsaved EM notes rejected",()=> {DataTable t=SavedEm();Throws<DBConcurrencyException>(()=>EmQualityEventEvidenceGuard.EnsureSaved(t.Copy(),t,7,new[]{(1,(int?)21,"Unsaved")}));});
        Run("v303 stale EM snapshot rejected",()=> {DataTable t=SavedEm(),loaded=t.Copy();t.Rows[0]["ResultRowVersion"]=new byte[]{2};Throws<DBConcurrencyException>(()=>EmQualityEventEvidenceGuard.EnsureSaved(loaded,t,7,new[]{(1,(int?)21,"Saved remark")}));});
        Run("v303 missing EM plate rejected",()=> {DataTable t=SavedEm();Throws<DBConcurrencyException>(()=>EmQualityEventEvidenceGuard.EnsureSaved(t.Copy(),t,7,Array.Empty<(int,int?,string)>()));});
        Run("v303 statistics inclusion policy explicit",()=> {
            DataTable t=new();foreach(string c in new[]{"Status","ResultValue","ResultQualifier","TestName","Unit"})t.Columns.Add(c);
            t.Rows.Add("PASS","1","","Conductivity","µS/cm");t.Rows.Add("NOT ASSESSED","1000","","Conductivity","µS/cm");
            Equal(true,TrendReportData.Statistics(t)[0].Contains("conformity not implied"));
            Equal(true,TrendReportData.Statistics(t)[0].Contains("Mean 500.5"));
        });
    }

    private static DataTable SavedEm()
    {
        DataTable t=new();t.Columns.Add("Id",typeof(int));t.Columns.Add("EventId",typeof(int));
        t.Columns.Add("TotalCount",typeof(decimal));t.Columns.Add("ColoniesObserved");t.Columns.Add("ResultRowVersion",typeof(byte[]));
        t.Rows.Add(1,7,21m,"Saved remark",new byte[]{1});return t;
    }
}
