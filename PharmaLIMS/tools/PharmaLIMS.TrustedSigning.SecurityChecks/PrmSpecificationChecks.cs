using System.Data;
using System.Reflection;
using System.Text.Json;
using PharmaLIMS.TrustedSigning;

internal static class PrmSpecificationChecks
{
    internal static void Run()
    {
        var assembly = typeof(TrustedApplicationRequest).Assembly;
        var dto = assembly.GetType("PharmaLIMS.TrustedSigning.PrmSpecificationRequest");
        if (dto is null) throw new Exception("FAIL: trusted PRM request boundary is missing.");
        var policy = assembly.GetType("PharmaLIMS.TrustedSigning.PrmSpecificationPolicy")!;
        var validate = policy.GetMethod("Validate")!;
        var hash = policy.GetMethod("SnapshotHash")!;
        string valid = """
        {"SpecificationNo":"EQ001-TEST","Category":"Primary Packaging","Version":0,"Reference":"Synthetic UAT only","ItemCode":"EQ001-SYNTHETIC","ProductionStage":"","EffectiveDate":"2026-10-25T00:00:00","ExpectedSnapshotHash":"","Tests":[{"TestCode":"TBC","TestName":"Synthetic count","SpecificationText":"NMT 10 CFU/25 cm2","SpecificationLimit":10,"IsDefaultForCategory":false,"Unit":"CFU/25 cm2","ResultType":"Numeric","RequiredTest":true,"MinimumElapsedHours":120,"SortOrder":1}]}
        """;
        void Check(string json, string action, bool rejected)
        {
            try {
                validate.Invoke(null,new[]{JsonSerializer.Deserialize(json,dto),action});
                if(rejected) throw new Exception("FAIL: unsafe PRM request was accepted: "+action);
            } catch(TargetInvocationException e) when(e.InnerException is ArgumentException) {
                if(!rejected) throw;
            }
        }
        Check(valid,"PrmSaveDraft",false);
        Check(valid,"PrmApprove",true);
        Check(valid.Replace("\"Version\":0","\"Version\":1"),"PrmReview",true);
        Check(valid.Replace("\"SpecificationLimit\":10","\"SpecificationLimit\":10.0001"),"PrmSaveDraft",true);
        Check(valid.Replace("\"MinimumElapsedHours\":120","\"MinimumElapsedHours\":0"),"PrmSaveDraft",true);
        Check(valid.Replace("\"ResultType\":\"Numeric\"","\"ResultType\":\"arbitrary\""),"PrmSaveDraft",true);
        Check(valid.Replace("\"Reference\":\"Synthetic UAT only\"","\"Reference\":null"),"PrmSaveDraft",true);
        Check(valid,"ExecuteSql",true);
        using var rows=new DataTable(); rows.Columns.Add("SpecificationTestID",typeof(int)); rows.Columns.Add("Value",typeof(decimal));rows.Rows.Add(1,10m);
        string original=(string)hash.Invoke(null,new object[]{rows})!;
        if(original!=(string)hash.Invoke(null,new object[]{rows.Copy()})!)throw new Exception("FAIL: snapshot hash is unstable.");
        rows.Rows[0]["Value"]=2m;
        if(original==(string)hash.Invoke(null,new object[]{rows})!)throw new Exception("FAIL: changed specification content has the same hash.");
        Console.WriteLine("PASS: PRM request validation and immutable snapshot checks (10 checks).");
    }
}
