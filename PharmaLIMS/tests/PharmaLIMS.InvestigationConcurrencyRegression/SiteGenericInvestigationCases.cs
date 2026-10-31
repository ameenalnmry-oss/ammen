using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SiteGenericInvestigationCases
{
    internal static void Run(string cs,string root)
    {
        if(!Regex.IsMatch(new SqlConnectionStringBuilder(cs).InitialCatalog,@"^EQ001_UAT_Investigation_[0-9a-f]{12}$"))throw new Exception("Generic probes require the existing owned SQL fixture.");
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"tests/fixtures/AMEEN-generic-investigation-20261030.json")));
        string frozen=fixture.RootElement.GetProperty("Window").GetString()!,oldHelper=fixture.RootElement.GetProperty("Helper").GetString()!;
        string site=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-QualityEventInvestigation.xaml.cs.txt")).Replace("\r\n","\n");
        string helper=File.ReadAllText(Path.Combine(root,"scripts/site-overlays/AMEEN-DatabaseHelper.QualityEvents.cs.txt")).Replace("\r\n","\n");
        VerifySources(site,helper);
        int oldUnsafe=Evaluate(cs,frozen,oldHelper,false),fixedUnsafe=Evaluate(cs,site,helper,true);
        if(oldUnsafe!=39||fixedUnsafe!=0)throw new Exception($"Generic actual first-write matrix: frozen unsafe{oldUnsafe}/39; corrected unsafe{fixedUnsafe}/39.");
        SiteGenericReloadProbe.Run(root);
        RetainedLocks(cs);
        Console.WriteLine("PASS SITE GENERIC QE SQL: actual frozen Save/Submit/Close allowed39 changed/missing evidence cases; corrected rejected39/39 and accepted3 unchanged controls; all probe writes rolled back. Callbacks/authentication/WPF not executed.");
    }

    private static int Evaluate(string cs,string window,string helper,bool corrected)
    {
        string header=Method(window,"private void SaveInvestigationHeaderInTransaction(");
        var paths=new[]{("Save",Method(window,"private bool SaveInvestigation("),header,"QA Review"),
            ("Submit",Method(helper,"public static void SubmitQualityEventToQA("),Method(helper,"public static void SubmitQualityEventToQA("),"Open"),
            ("Close",Method(helper,"public static void CloseQualityEvent("),Method(helper,"public static void CloseQualityEvent("),"QA Review")};
        string[] changes={
            "UPDATE dbo.QualityEventAffectedResults SET ResultText=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventActions SET ActionDescription=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventRootCauseWhys SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventImpactAssessments SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventCAPAItems SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventRetesting SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventDistribution SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "INSERT dbo.QualityEventInvestigationEvidenceHistory VALUES(1,N'new evidence')",
            "INSERT dbo.QualityEventSignatures VALUES(1,202,N'competing signer')",
            "UPDATE dbo.QualityEventChecklistAnswers SET Comments=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEvents SET RootCauseDetails=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "INSERT dbo.QualityEventRootCauseWhys VALUES(1,N'new child')"};
        int unsafeCount=0;
        foreach(var path in paths)
        foreach(string change in changes.Concat(new[]{"MISSING","UNCHANGED"}))
        {
            using var c=new SqlConnection(cs);c.Open();using var tx=c.BeginTransaction();
            Sql(c,tx,"UPDATE dbo.QualityEvents SET CurrentStatus=N'"+path.Item4+"',ClosedBy=NULL,ClosedDate=NULL,SourceModule=N'WATER' WHERE QualityEventID=1");
            string? loaded=change=="MISSING"?null:QualityEventInvestigationConcurrencyGuard.Capture(c,tx,1,false);
            if(change!="MISSING"&&change!="UNCHANGED")Sql(c,tx,change);
            bool rejected=false;
            try
            {
                if(HasGuard(path.Item2))QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(c,tx,1,false,loaded!);
                string query=Regex.Matches(path.Item3,"ExecuteNonQueryWithTransaction\\(@\"([\\s\\S]*?)\",").Select(m=>m.Groups[1].Value).First(x=>x.Contains("UPDATE dbo.QualityEvents",StringComparison.Ordinal));
                using var write=new SqlCommand(query,c,tx);
                foreach(string parameter in Regex.Matches(query,"@[A-Za-z0-9_]+").Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                    write.Parameters.AddWithValue(parameter,parameter.Equals("@QualityEventID",StringComparison.OrdinalIgnoreCase)?1:parameter.Equals("@CAPARequired",StringComparison.OrdinalIgnoreCase)?false:parameter.Equals("@CurrentStatus",StringComparison.OrdinalIgnoreCase)?path.Item4:"synthetic first-write probe");
                if(write.ExecuteNonQuery()<1)throw new Exception("Actual generic first-write SQL did not execute.");
            }
            catch(DBConcurrencyException){rejected=true;}
            finally{tx.Rollback();}
            if(change=="UNCHANGED"&&rejected)throw new Exception("Unchanged generic investigation was rejected.");
            if(change!="UNCHANGED"&&!rejected)unsafeCount++;
        }
        Console.WriteLine($"SITE GENERIC QE {(corrected?"corrected":"frozen")}: unsafe{unsafeCount}/39;3 unchanged controls accepted.");return unsafeCount;
    }

    private static void RetainedLocks(string cs)
    {
        using var owner=new SqlConnection(cs);owner.Open();using var tx=owner.BeginTransaction();
        string version=QualityEventInvestigationConcurrencyGuard.Capture(owner,tx,1,false);
        QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(owner,tx,1,false,version);
        foreach(string sql in new[]{"UPDATE dbo.QualityEventActions SET ActionDescription=N'race' WHERE QualityEventID=1","INSERT dbo.QualityEventRootCauseWhys VALUES(1,N'phantom')"})
        {
            using var other=new SqlConnection(cs);other.Open();
            try{Sql(other,null,"SET LOCK_TIMEOUT 300; "+sql);throw new Exception("Generic evidence lock admitted a writer.");}
            catch(SqlException ex)when(ex.Number==1222){}
        }
        tx.Rollback();Console.WriteLine("PASS SITE GENERIC QE retained child/update and phantom locks.");
    }
    private static void Sql(SqlConnection c,SqlTransaction? tx,string sql){using var cmd=new SqlCommand(sql,c,tx){CommandTimeout=15};cmd.ExecuteNonQuery();}
    private static string Code(string source)=>Regex.Replace(source,"@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",m=>new string(' ',m.Length));
    internal static string Method(string source,string signature)
    {
        string code=Code(source);int start=code.IndexOf(signature,StringComparison.Ordinal);if(start<0)throw new Exception("Source method missing: "+signature);
        int begin=code.IndexOf('{',start),end=begin+1,depth=1;while(depth!=0&&end<code.Length){depth+=code[end]=='{'?1:code[end]=='}'?-1:0;end++;}
        if(depth!=0)throw new Exception("Unbalanced source method.");return source[start..end];
    }
    private static bool HasGuard(string source)
    {
        string code=Code(source);int tx=code.IndexOf("ExecuteInTransaction",StringComparison.Ordinal);if(tx<0)return false;
        int begin=code.IndexOf('{',tx);
        var guard=Regex.Match(code,@"QualityEventInvestigationConcurrencyGuard\.EnsureUnchanged\(\s*(?:conn|connection)\s*,\s*(?:tx|transaction)\s*,\s*qualityEventId\s*,\s*false\s*,\s*(?:loadedInvestigationVersion|expectedInvestigationVersion)\s*\)\s*;");
        if(!guard.Success||guard.Index<begin)return false;
        int depth=0;for(int i=begin;i<guard.Index;i++)depth+=(code[i]=='{'?1:code[i]=='}'?-1:0);
        string before=code[begin..guard.Index].TrimEnd();
        if(depth!=1||before.Length==0||!"{;}".Contains(before[^1])||Regex.IsMatch(before,@"(?:loadedInvestigationVersion|expectedInvestigationVersion)\s*="))return false;
        var write=Regex.Match(code[begin..],@"SaveInvestigationHeaderInTransaction\s*\(|ExecuteNonQueryWithTransaction\s*\(");
        return write.Success&&guard.Index<begin+write.Index;
    }
    internal static void VerifySources(string window,string helper)
    {
        foreach(string source in new[]{Method(window,"private bool SaveInvestigation("),Method(helper,"public static void SubmitQualityEventToQA("),Method(helper,"public static void CloseQualityEvent(")})
        {
            if(!HasGuard(source))throw new Exception("Generic site evidence guard is missing, conditional, rebased or after a write.");
            string line=Regex.Match(source,@"(?m)^.*QualityEventInvestigationConcurrencyGuard\.EnsureUnchanged[^\r\n]*;").Value;
            string token=source.Contains("string expectedInvestigationVersion",StringComparison.Ordinal)?"expectedInvestigationVersion":"loadedInvestigationVersion";
            foreach(string replacement in new[]{"/*"+line+"*/","if(false){"+line+"}","if(false) "+line,token+"=\"unreviewed\";"+line})
                if(HasGuard(source.Replace(line,replacement)))throw new Exception("Unsafe generic guard wiring accepted.");
            string removed=source.Replace(line,"");
            if(HasGuard(removed.Insert(removed.LastIndexOf('}'),line)))throw new Exception("A late generic guard was accepted.");
        }
        string load=Method(window,"private async Task LoadEventAsync(");
        if(!load.Contains("loadedInvestigationVersion = null",StringComparison.Ordinal)||!load.Contains("data.EvidenceVersion = versionAfterLoad",StringComparison.Ordinal)||
            load.IndexOf("loadedInvestigationVersion = loaded.EvidenceVersion",StringComparison.Ordinal)<load.IndexOf("UpdateButtons",StringComparison.Ordinal))throw new Exception("Complete loaded evidence publishes before UI binding.");
        string close=Method(window,"private async void BtnCloseEvent_Click(");
        int capture=close.IndexOf("string expectedVersion = loadedInvestigationVersion",StringComparison.Ordinal);
        if(capture<0||capture>close.IndexOf("ShowDialog",StringComparison.Ordinal)||!close.Contains("currentRole,\n                    expectedVersion",StringComparison.Ordinal))throw new Exception("Closure signature target evidence not frozen.");
        if(!Method(window,"private async void BtnSubmitQA_Click(").Contains("currentUser, loadedInvestigationVersion",StringComparison.Ordinal))throw new Exception("Submit did not forward loaded evidence.");
        Console.WriteLine("PASS SITE GENERIC QE source boundaries: three unconditional pre-write guards, no conditional/comment bypass, complete load publication and frozen closure token.");
    }
}
