using System.Data;
using System.IO;
using PharmaLIMS;
using PharmaLIMS.Services;
internal static class SampleRegisterSmoke
{
    internal static void Verify(string directory)
    {
        static void Require(bool condition,string message) { if (!condition) throw new InvalidOperationException("Sample register: "+message); }
        Require(SampleReceiptRegisterQuery.NormalizeType("pw")=="Purified Water","PW alias");
        Require(SampleReceiptRegisterQuery.NormalizeType(" PTW ")=="Potable Water","PTW alias");
        var parameters=SampleReceiptRegisterQuery.Parameters("All",new DateTime(2026,9,1),new DateTime(2026,9,30),false);
        Require((DateTime)parameters[2].Value==new DateTime(2026,10,1),"inclusive date end");
        try { SampleReceiptRegisterQuery.ValidateDates(new DateTime(2026,9,30),new DateTime(2026,9,1)); throw new Exception("reversed dates accepted"); } catch (InvalidOperationException) {}
        DataTable source=new();
        foreach (string name in new[] {"RecordKind","SampleID","SampleType","SampleNumber","Description","BatchOrLot","Quantity","SampledBy","ReceivedBy","RegisteredBy","ReceiptDecision","Tests","Status"}) source.Columns.Add(name);
        foreach (string name in new[] {"SamplingDateTime","ReceivedDateTime","RegisteredDateTime"}) source.Columns.Add(name,typeof(DateTime));
        int id=0;
        foreach (string type in SampleReceiptRegisterQuery.Types.Skip(1))
        for (int i=0;i<15;i++)
        {
            DataRow row=source.NewRow(); row["RecordKind"]=type.Contains("Water") ? "WATER" : type=="Environmental Monitoring" ? "EM" : "PRM";
            row["SampleID"]=++id; row["SampleType"]=type; row["SampleNumber"]="REGISTER-TEST-"+id.ToString("D3");
            row["Description"]="Fixture sample / source "+(i==14 ? new string('x',700) : "Room 01"); row["BatchOrLot"]="BATCH-001"; row["Quantity"]="100 g";
            row["SampledBy"]="Sampler"; row["RegisteredBy"]="Registrar"; row["Tests"]="TAMC; TYMC; Specified organisms"; row["Status"]=i==0 ? "Rejected" : "Registered";
            row["SamplingDateTime"]=new DateTime(2026,8,31,9,0,0); row["RegisteredDateTime"]=new DateTime(2026,9,1,12,0,0);
            if (type.Contains("Water")) {row["ReceivedDateTime"]=new DateTime(2026,9,1,10,0,0);row["ReceivedBy"]="Receiver";row["ReceiptDecision"]=i==0 ? "Rejected" : "Accepted";}
            source.Rows.Add(row);
        }
        var tables=SampleReceiptRegisterData.Tables(source);
        Require(tables.Count==8 && tables.Sum(t=>t.Data.Rows.Count)==120,"all eight types and complete row counts");
        Require(tables.Any(t=>t.Data.Rows.Cast<DataRow>().Any(r=>r["Dates"].ToString()!.Contains("R: Not recorded"))),"missing lab receipt remains explicit");
        Require(tables.All(t=>t.Data.Rows[0]["No"].ToString()=="1"),"per-type numbering");
        Require(tables.SelectMany(t=>t.Data.Rows.Cast<DataRow>()).Count(r=>r["Status"].ToString()=="Rejected")==8,"rejected records retained");
        var window=new SampleReceiptRegister(); // No Loaded query: constructor must remain read-only.
        window.Close();
        TrendPdfReportWriter.Write(Path.Combine(directory,"MEDICA_Sample_Register_Fixture_Preview.pdf"),"LABORATORY SAMPLE REGISTER","REGISTER-FIXTURE-ONLY","All types | September 2026 | Registration date","Fixture user",new DateTime(2026,9,30),
            Path.Combine(AppContext.BaseDirectory,"medica-logo.png"),new[] {"SYNTHETIC TEST DATA ONLY. 120 source records across eight types. Not laboratory results.","Dates legend: S = Sampled; R = Laboratory received; G = Registered."},Array.Empty<TrendReportChart>(),tables,sampleRegister:true);
        Console.WriteLine("PASS Sample register: eight types, all 120 rows, receipt evidence, date filter, per-type numbering and full PDF.");
    }
}
