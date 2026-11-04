using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PharmaLIMS;
using PharmaLIMS.Infrastructure;

internal static class SitePreflightTimestampCases
{
    internal static void Run(string? root=null)
    {
        if(root!=null)VerifySources(root);
        int frozenWrong=0,correctedWrong=0,total=0;
        var original=CultureInfo.CurrentCulture;
        try
        {
            foreach(string culture in new[]{"ar-SA","th-TH","en-US"})
            foreach(DateTime date in new[]{new DateTime(2024,2,29,0,0,0),new DateTime(2026,10,27,16,20,31),new DateTime(2026,12,31,23,59,59),new DateTime(2027,1,1,0,0,0)})
            {
                var selectedCulture=new CultureInfo(culture,false);
                if(culture=="ar-SA")selectedCulture.DateTimeFormat.Calendar=new UmAlQuraCalendar();
                if(culture=="th-TH")selectedCulture.DateTimeFormat.Calendar=new ThaiBuddhistCalendar();
                CultureInfo.CurrentCulture=selectedCulture;
                var report=new SystemPreflightReport{ExecutedAt=date,Server=@"AMEEN\SQLEXPRESS",Database="EQ001_UAT_AMEEN_20261024",
                    Checks=new[]{new SystemPreflightCheck{Area="Database",Status="BLOCKER",Check="blocked",Details="retained"},new SystemPreflightCheck{Area="Audit",Status="WARNING",Check="warning"},new SystemPreflightCheck{Area="Source",Status="PASS",Check="verified"}}};
                string stamp=date.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture);
                string expectedLabel=$"Executed {stamp} | {report.Server} / {report.Database}";
                string oldText=FrozenSitePreflight.Export(report),newText=CorrectedSitePreflight.Export(report);
                string Executed(string text)=>text.Split('\n').Single(x=>x.StartsWith("Executed:",StringComparison.Ordinal)).TrimEnd('\r');
                if(Executed(oldText)!="Executed: "+stamp)frozenWrong++;
                if(FrozenSitePreflight.Label(report)!=expectedLabel)frozenWrong++;
                if(Executed(newText)!="Executed: "+stamp)correctedWrong++;
                if(CorrectedSitePreflight.Label(report)!=expectedLabel)correctedWrong++;
                total+=2;
                if(report.ExecutedAt.Ticks!=date.Ticks||report.BlockerCount!=1||report.WarningCount!=1||report.PassCount!=1||report.CanProceed)
                    throw new Exception("Timestamp formatting changed the recorded time or acceptance counts.");
                string OtherLines(string text)=>string.Join("\n",text.Split('\n').Where(x=>!x.StartsWith("Executed:",StringComparison.Ordinal)));
                if(OtherLines(oldText)!=OtherLines(newText))throw new Exception("Unrelated report evidence changed.");
            }
        }
        finally {CultureInfo.CurrentCulture=original;}
        Console.WriteLine($"SITE PREFLIGHT TIMESTAMP: frozen wrong{frozenWrong}/{total}; corrected wrong{correctedWrong}/{total}; actual copied report and label expression, no WPF instantiation.");
        if(frozenWrong!=16||correctedWrong!=0)throw new Exception("RED: exact site timestamp formatting is calendar-dependent.");
    }

    private static string Code(string source)=>Regex.Replace(source,"@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",m=>new string(' ',m.Length));
    private static string Method(string source,string signature)
    {
        string code=Code(source);int start=code.IndexOf(signature,StringComparison.Ordinal),begin=code.IndexOf('{',start),end=begin+1,depth=1;
        while(depth!=0&&end<code.Length){depth+=code[end]=='{'?1:code[end]=='}'?-1:0;end++;}
        if(start<0||depth!=0)throw new Exception("Source method missing.");return source[start..end];
    }
    private static string Label(string source)=>source.Split('\n').Select(x=>x.Trim()).Single(x=>x.StartsWith("lblExecutedAt.Text = $\"Executed ",StringComparison.Ordinal));
    private static void VerifySources(string root)
    {
        string site=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-SystemPreflight.xaml.cs.txt")).Replace("\r\n","\n");
        string compiled=File.ReadAllText(Path.Combine(root,"tests/PharmaLIMS.InfrastructureReviewRegression/SitePreflightTimestampMethods.cs")).Replace("\r\n","\n");
        string fixedClass=Method(compiled,"internal static class CorrectedSitePreflight"),frozenClass=Method(compiled,"internal static class FrozenSitePreflight");
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-preflight-timestamp-20261030.json")));
        var fixture=json.RootElement;
        if(Method(site,"private static string BuildTextReport(")!=Method(fixedClass,"private static string BuildTextReport(")||Label(site)!=Label(fixedClass)||
            Method(frozenClass,"private static string BuildTextReport(")!=fixture.GetProperty("FrozenMethod").GetString()||Label(frozenClass)!=fixture.GetProperty("FrozenLabel").GetString()||
            Method(compiled,"public sealed class SystemPreflightCheck")!=fixture.GetProperty("CheckDto").GetString()||Method(compiled,"public sealed class SystemPreflightReport")!=fixture.GetProperty("ReportDto").GetString())
            throw new Exception("Compiled site preflight source differs from full artifact/immutable fixture.");
    }
}
