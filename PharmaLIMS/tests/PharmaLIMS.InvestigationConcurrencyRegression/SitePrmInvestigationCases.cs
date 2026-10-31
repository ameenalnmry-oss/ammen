using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

// Executes the real snapshot guards and extracted first-write SQL, not WPF or authentication.
// Unchanged raw header/all-answer rows are a conservative witness that the old
// narrower header/active-checklist comparison cannot detect these child-only changes.
internal static class SitePrmInvestigationCases
{
    private sealed record Probe(string Name, string Source, string WriteSql, string Status, bool HeaderChecklist);

    internal static void RunFrozen(string connectionString, string project)
    {
        RequireDisposable(connectionString);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(project,
            "tests/fixtures/AMEEN-PRM-investigation-concurrency-20261030.json")));
        var probes = fixture.RootElement.GetProperty("Probes").EnumerateArray().Select(p => new Probe(
            p.GetProperty("Name").GetString()!, p.GetProperty("Source").GetString()!,
            p.GetProperty("WriteSql").GetString()!, p.GetProperty("Status").GetString()!,
            p.GetProperty("HeaderChecklist").GetBoolean())).ToArray();
        VerifyDetector(fixture.RootElement.GetProperty("GuardedReferences"));
        if (Regex.Matches(Code(fixture.RootElement.GetProperty("PartialComparisonSource").GetString()!),
            @"ResultSnapshotGuard\.EnsureMatches\s*\(").Count != 2)
            throw new Exception("Frozen partial comparison no longer has the reviewed header/checklist contract.");
        foreach (var p in probes)
        {
            if (HasFullGuard(p.Source)) throw new Exception("Frozen site unexpectedly contains the complete guard: " + p.Name);
            if (Code(p.Source).Contains("CaptureInvestigationEvidence(", StringComparison.Ordinal) != p.HeaderChecklist)
                throw new Exception("Frozen partial-comparison call changed: " + p.Name);
        }
        Prepare(connectionString);
        var result = Evaluate(connectionString, probes, "frozen");
        if (result.Failures != 48 || result.Checks != 52)
            throw new Exception($"Frozen child-evidence gap did not reproduce: {result.Failures}/{result.Checks}");
        Console.WriteLine("PASS SITE PRM CAS FROZEN: 48 child-change acceptance failures reproduced; four unchanged first-write probes accepted. This is not execution of the complete WPF callbacks.");
    }

    internal static void RunCorrected(string connectionString, string project)
    {
        RequireDisposable(connectionString);
        string source = File.ReadAllText(Path.Combine(project,
            "scripts/site-overlays/AMEEN-PRMQualityEventInvestigation.xaml.cs.txt"));
        byte[] siteGuard = File.ReadAllBytes(Path.Combine(project,
            "scripts/site-overlays/AMEEN-QualityEventInvestigationConcurrencyGuard.cs.txt"));
        byte[] linkedGuard = File.ReadAllBytes(Path.Combine(project,
            "Infrastructure/QualityEventInvestigationConcurrencyGuard.cs"));
        if (!siteGuard.SequenceEqual(linkedGuard))
            throw new Exception("The tested full guard differs from the supplied site helper.");
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(project,
            "tests/fixtures/AMEEN-PRM-investigation-concurrency-20261030.json")));
        var probes = fixture.RootElement.GetProperty("Probes").EnumerateArray().Select(p => new Probe(
            p.GetProperty("Name").GetString()!, ExtractMethod(source,p.GetProperty("Signature").GetString()!),
            p.GetProperty("WriteSql").GetString()!, p.GetProperty("Status").GetString()!,
            p.GetProperty("HeaderChecklist").GetBoolean())).ToArray();
        foreach (var probe in probes)
        {
            if (!HasFullGuard(probe.Source))
                throw new Exception("The complete guard is not unconditionally before the first write: " + probe.Name);
            bool partial = Code(probe.Source).Contains("CaptureInvestigationEvidence(", StringComparison.Ordinal);
            if (partial != probe.HeaderChecklist)
                throw new Exception("The existing site header/checklist comparison was not preserved: " + probe.Name);
        }
        VerifyLoadAndOwnActionToken(source, probes.Single(p=>p.Name=="AddAction").Source);
        var result = Evaluate(connectionString, probes, "corrected");
        if (result.Checks != 52 || result.Failures != 0)
            throw new Exception($"Corrected site concurrency probes failed: {result.Failures}/{result.Checks}");
        VerifyRetainedLocks(connectionString);
        VerifyOwnActionContinuation(connectionString, probes.Single(p=>p.Name=="AddAction"));
        Console.WriteLine("PASS SITE PRM CAS CORRECTED: 52/52 comparison/first-write probes; two child phantom-lock checks; own-action old-token rejection and committed-token continuation. Full WPF/authentication and site deployment remain unqualified.");
    }

    private static string ExtractMethod(string source, string signature)
    {
        string code=Code(source);
        var matches=Regex.Matches(code,Regex.Escape(signature));
        if(matches.Count!=1) throw new Exception("Expected one source method: "+signature);
        int start=matches[0].Index,begin=code.IndexOf('{',start),depth=1,end=begin+1;
        if(begin<0) throw new Exception("Method body missing.");
        while(end<code.Length && depth!=0) { depth+=code[end]=='{'?1:code[end]=='}'?-1:0; end++; }
        if(depth!=0) throw new Exception("Unbalanced source method.");
        return source[start..end];
    }

    private static void VerifyLoadAndOwnActionToken(string source, string addAction)
    {
        string load=Code(ExtractMethod(source,"private async Task LoadEventAsync()"));
        int clear=load.IndexOf("loadedInvestigationVersion = null",StringComparison.Ordinal);
        int before=load.IndexOf("string versionBeforeLoad = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion",StringComparison.Ordinal);
        int after=load.IndexOf("string versionAfterLoad = QualityEventInvestigationConcurrencyGuard.CaptureLoadedVersion",StringComparison.Ordinal);
        int compare=load.IndexOf("string.Equals(versionBeforeLoad, versionAfterLoad, StringComparison.Ordinal)",StringComparison.Ordinal);
        int reject=load.IndexOf("throw new DBConcurrencyException",compare<0?0:compare,StringComparison.Ordinal);
        int saved=load.IndexOf("data.EvidenceVersion = versionAfterLoad",StringComparison.Ordinal);
        int actionsBound=load.IndexOf("dgActions.ItemsSource = actionsTable.DefaultView",StringComparison.Ordinal);
        int publish=load.IndexOf("loadedInvestigationVersion = loaded.EvidenceVersion",StringComparison.Ordinal);
        if(!(clear>=0 && clear<before && before<after && after<compare && compare<reject && reject<saved && saved<actionsBound && actionsBound<publish))
            throw new Exception("Load must clear the token, compare before/after data, and publish only after all UI data are bound.");
        if(Regex.Matches(load,@"QualityEventInvestigationConcurrencyGuard\.CaptureLoadedVersion\s*\(").Count!=2 ||
            Regex.Matches(load,@"loadedInvestigationVersion\s*=").Count!=2 ||
            !load.Contains("string.IsNullOrEmpty(loaded.EvidenceVersion)",StringComparison.Ordinal))
            throw new Exception("Unexpected load token publication.");
        if(load.Contains("SafeLoadAffectedResults(",StringComparison.Ordinal) || load.Contains("SafeLoadActions(",StringComparison.Ordinal) ||
            !load.Contains("PRMQualityEventInvestigationService.GetPRMAffectedResults(qualityEventId)",StringComparison.Ordinal) ||
            !load.Contains("PRMQualityEventInvestigationService.GetPRMActions(qualityEventId)",StringComparison.Ordinal) ||
            Regex.Matches(load,@"\?\?\s*throw new InvalidOperationException").Count!=2)
            throw new Exception("The qualified load must propagate failed/null child reads.");
        string action=Code(addAction);
        int capture=action.IndexOf("savedVersion = QualityEventInvestigationConcurrencyGuard.Capture",StringComparison.Ordinal);
        int callback=action.IndexOf("DatabaseHelper.ExecuteInTransaction",StringComparison.Ordinal);
        int begin=action.IndexOf('{',callback),depth=1,end=begin+1;
        while(depth!=0) {depth+=action[end]=='{'?1:action[end]=='}'?-1:0;end++;}
        int publishAction=action.IndexOf("loadedInvestigationVersion = savedVersion",StringComparison.Ordinal);
        int clearAction=action.IndexOf("loadedInvestigationVersion = null",StringComparison.Ordinal);
        int actionRead=action.IndexOf("actionsTable = PRMQualityEventInvestigationService.GetPRMActions",StringComparison.Ordinal);
        int actionBound=action.IndexOf("dgActions.ItemsSource = actionsTable.DefaultView",StringComparison.Ordinal);
        var writes=Regex.Matches(action[..end],@"DatabaseHelper\.(?:ExecuteNonQueryWithTransaction|AddAuditTrailAdvanced)\s*\(");
        if(writes.Count!=2 || capture<writes[^1].Index || capture>=end || publishAction<=end ||
            !(end<clearAction && clearAction<actionRead && actionRead<actionBound && actionBound<publishAction) ||
            action.Contains("SafeLoadActions(",StringComparison.Ordinal) ||
            !action.Contains("?? throw new InvalidOperationException",StringComparison.Ordinal) ||
            Regex.Matches(action,@"loadedInvestigationVersion\s*=").Count!=2)
            throw new Exception("Own-action token must describe post-write content and publish after transaction success.");
        Console.WriteLine("PASS SITE PRM CAS wiring: four pre-write guards, preserved partial/history calls, load publication, and post-commit own-action token.");
    }

    private static void VerifyRetainedLocks(string cs)
    {
        using var owner=new SqlConnection(cs);owner.Open();using var transaction=owner.BeginTransaction();
        string loaded=QualityEventInvestigationConcurrencyGuard.Capture(owner,transaction,1,true);
        QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(owner,transaction,1,true,loaded);
        foreach(string mutation in new[]{
            "INSERT dbo.QualityEventCAPAItems VALUES(1,N'competing phantom')",
            "INSERT dbo.PRM_ElectronicSignatures VALUES(7,N'Historical',N'competing phantom')"})
        {
            bool blocked=false;
            try
            {
                using var competitor=new SqlConnection(cs);competitor.Open();
                using var write=new SqlCommand("SET LOCK_TIMEOUT 500; "+mutation,competitor);write.ExecuteNonQuery();
            }
            catch(SqlException exception) when(exception.Number==1222){blocked=true;}
            if(!blocked) throw new Exception("The complete guard did not retain child phantom protection.");
        }
        transaction.Rollback();
    }

    private static void VerifyOwnActionContinuation(string cs,Probe addAction)
    {
        using var connection=new SqlConnection(cs);connection.Open();
        string oldVersion,savedVersion;
        using(var transaction=connection.BeginTransaction())
        {
            oldVersion=QualityEventInvestigationConcurrencyGuard.Capture(connection,transaction,1,true);
            QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(connection,transaction,1,true,oldVersion);
            using var write=new SqlCommand(addAction.WriteSql,connection,transaction);
            foreach(string parameter in Regex.Matches(addAction.WriteSql,"@[A-Za-z0-9_]+").Select(m=>m.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                write.Parameters.AddWithValue(parameter,parameter.Equals("@QualityEventID",StringComparison.OrdinalIgnoreCase)?1:"owned continuation probe");
            if(write.ExecuteNonQuery()!=1)throw new Exception("The owned fixture action did not execute.");
            savedVersion=QualityEventInvestigationConcurrencyGuard.Capture(connection,transaction,1,true);
            transaction.Commit(); // Deliberate owned-fixture control; the surrounding fixture is dropped.
        }
        using var verify=connection.BeginTransaction();
        bool stale=false;
        try{QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(connection,verify,1,true,oldVersion);}
        catch(DBConcurrencyException){stale=true;}
        if(!stale)throw new Exception("The previous action token remained valid after the committed action.");
        QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(connection,verify,1,true,savedVersion);
        verify.Rollback();
    }

    private static void RequireDisposable(string cs)
    {
        if (!Regex.IsMatch(new SqlConnectionStringBuilder(cs).InitialCatalog, @"^EQ001_UAT_Investigation_[0-9a-f]{12}$"))
            throw new Exception("Site concurrency probes require the owned disposable investigation fixture.");
    }

    private static void Prepare(string cs)
    {
        using var connection = new SqlConnection(cs); connection.Open();
        using var command = new SqlCommand("""
ALTER TABLE dbo.QualityEventActions ADD ActionType nvarchar(80),PerformedBy nvarchar(100),PerformedDate datetime2;
CREATE TABLE dbo.QualityEventRootCauseWhys(QualityEventID int,Payload nvarchar(100));
CREATE TABLE dbo.QualityEventImpactAssessments(QualityEventID int,Payload nvarchar(100));
CREATE TABLE dbo.QualityEventCAPAItems(QualityEventID int,Payload nvarchar(100));
CREATE TABLE dbo.QualityEventRetesting(QualityEventID int,Payload nvarchar(100));
CREATE TABLE dbo.QualityEventDistribution(QualityEventID int,Payload nvarchar(100));
CREATE TABLE dbo.QualityEventInvestigationEvidenceHistory(QualityEventID int,Payload nvarchar(100));
INSERT dbo.QualityEventRootCauseWhys VALUES(1,N'loaded');
INSERT dbo.QualityEventImpactAssessments VALUES(1,N'loaded');
INSERT dbo.QualityEventCAPAItems VALUES(1,N'loaded');
INSERT dbo.QualityEventRetesting VALUES(1,N'loaded');
INSERT dbo.QualityEventDistribution VALUES(1,N'loaded');
INSERT dbo.QualityEventInvestigationEvidenceHistory VALUES(1,N'loaded');
""", connection);
        command.ExecuteNonQuery();
    }

    private static (int Checks, int Failures) Evaluate(string cs, Probe[] probes, string variant)
    {
        string[] mutations = {
            "UPDATE dbo.QualityEventAffectedResults SET ResultText=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventActions SET ActionDescription=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventRootCauseWhys SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventImpactAssessments SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventCAPAItems SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventRetesting SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "UPDATE dbo.QualityEventDistribution SET Payload=CONVERT(nvarchar(36),NEWID()) WHERE QualityEventID=1",
            "INSERT dbo.QualityEventInvestigationEvidenceHistory VALUES(1,CONVERT(nvarchar(36),NEWID()))",
            "INSERT dbo.QualityEventSignatures VALUES(1,101,N'synthetic competing editor')",
            "INSERT dbo.PRM_ElectronicSignatures VALUES(7,N'Historical',CONVERT(nvarchar(36),NEWID()))",
            "UPDATE dbo.PRM_QualityEventEvidenceReconciliations SET ElectronicSignatureID=ElectronicSignatureID+1",
            "UPDATE dbo.PRM_SampleTests SET ResultValue=CONVERT(nvarchar(36),NEWID()) WHERE SampleID=7"
        };
        int checks = 0, failures = 0;
        foreach (var probe in probes)
        foreach (string mutation in mutations.Append("SELECT 1"))
        {
            using var connection = new SqlConnection(cs); connection.Open();
            using (var reset = new SqlCommand("UPDATE dbo.QualityEvents SET CurrentStatus=@Status,ClosedBy=NULL,ClosedDate=NULL WHERE QualityEventID=1", connection))
            { reset.Parameters.AddWithValue("@Status", probe.Status); reset.ExecuteNonQuery(); }
            string loaded;
            DataTable header, answers;
            using (var snapshot = connection.BeginTransaction())
            {
                loaded = QualityEventInvestigationConcurrencyGuard.Capture(connection, snapshot, 1, true);
                header = Read(connection, snapshot, "SELECT * FROM dbo.QualityEvents WHERE QualityEventID=1");
                answers = Read(connection, snapshot, "SELECT * FROM dbo.QualityEventChecklistAnswers WHERE QualityEventID=1");
                snapshot.Commit();
            }
            using (var edit = new SqlCommand(mutation, connection)) edit.ExecuteNonQuery();
            bool rejected = false;
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    // These remain identical for every child-only mutation. This executes
                    // the production DataTable comparer and never invents a stale header.
                    ResultSnapshotGuard.EnsureMatches(header, Read(connection, transaction,
                        "SELECT * FROM dbo.QualityEvents WHERE QualityEventID=1"), "QualityEventID", "QualityEventID", 1);
                    ResultSnapshotGuard.EnsureMatches(answers, Read(connection, transaction,
                        "SELECT * FROM dbo.QualityEventChecklistAnswers WHERE QualityEventID=1"), "QuestionID", "QualityEventID", 1);
                    if (HasFullGuard(probe.Source))
                        QualityEventInvestigationConcurrencyGuard.EnsureUnchanged(connection, transaction, 1, true, loaded);
                    using var write = new SqlCommand(probe.WriteSql, connection, transaction);
                    foreach (string parameter in Regex.Matches(probe.WriteSql, "@[A-Za-z0-9_]+").Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase))
                        write.Parameters.AddWithValue(parameter, parameter.Equals("@QualityEventID", StringComparison.OrdinalIgnoreCase) ? 1 :
                            parameter.Equals("@CAPARequired", StringComparison.OrdinalIgnoreCase) ? false :
                            parameter.Equals("@CurrentStatus", StringComparison.OrdinalIgnoreCase) ? probe.Status : "synthetic probe");
                    if (write.ExecuteNonQuery() < 1) throw new Exception("The extracted first write did not execute.");
                }
                catch (DBConcurrencyException) { rejected = true; }
                finally { transaction.Rollback(); }
            }
            bool expectedReject = mutation != "SELECT 1";
            checks++;
            if (rejected != expectedReject)
            {
                failures++;
                Console.WriteLine($"SITE PRM CAS {variant}: {probe.Name} / {mutation.Split(' ')[1]} expected reject={expectedReject}, actual={rejected}");
            }
            // Every probe write rolled back; only deliberate competing fixture edits persist.
            using var verify = connection.BeginTransaction();
            ResultSnapshotGuard.EnsureMatches(header, Read(connection, verify,
                "SELECT * FROM dbo.QualityEvents WHERE QualityEventID=1"), "QualityEventID", "QualityEventID", 1);
            verify.Rollback();
        }
        Console.WriteLine($"SITE PRM CAS {variant}: {checks-failures}/{checks} comparison/first-write acceptance checks passed; {failures} failed.");
        return (checks, failures);
    }

    private static DataTable Read(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        using var command = new SqlCommand(sql, connection, transaction);
        using var reader = command.ExecuteReader(); var table = new DataTable(); table.Load(reader); return table;
    }

    private static void VerifyDetector(JsonElement references)
    {
        int checks = 0;
        void Expect(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
        foreach (var item in references.EnumerateArray())
        {
            string source = item.GetProperty("Source").GetString()!;
            Expect(HasFullGuard(source), "The real guarded reference was not detected.");
            string line = Regex.Match(source, @"(?m)^.*QualityEventInvestigationConcurrencyGuard\.EnsureUnchanged[^\r\n]*;").Value;
            Expect(line.Length != 0, "The reference guard line was missing.");
            Expect(!HasFullGuard(source.Replace(line, "/*"+line+"*/")), "A comment was accepted as a guard.");
            Expect(!HasFullGuard(source.Replace(line, "if(false){"+line+"}")), "A conditional was accepted as an unconditional guard.");
            Expect(!HasFullGuard(source.Replace(line, "if(false) "+line)), "An unbraced conditional was accepted as an unconditional guard.");
            Expect(!HasFullGuard(source.Replace(line, "loadedInvestigationVersion=\"unreviewed token\";"+line)), "A rebased token was accepted.");
            string removed = source.Replace(line, ""), code = Code(removed);
            int begin = code.IndexOf('{', code.IndexOf("DatabaseHelper.ExecuteInTransaction", StringComparison.Ordinal));
            int depth = 1, end = begin+1;
            while (depth != 0) { depth += code[end]=='{' ? 1 : code[end]=='}' ? -1 : 0; end++; }
            Expect(!HasFullGuard(removed.Insert(end-1, line)), "A guard after controlled writes was accepted.");
        }
        if (checks != 28) throw new Exception("Incomplete source guard detector controls.");
        Console.WriteLine("PASS SITE PRM CAS detector: 28 actual/reference and comment, nested, unbraced conditional, rebased, late-source controls.");
    }

    // Ignore comments, SQL/string literals and nested conditionals. A token merely
    // mentioned in source must never cause the actual backend guard to be applied.
    private static string Code(string source) => Regex.Replace(source,
        "@\"(?:\"\"|[^\"])*\"|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/",
        m => new string(' ', m.Length));

    private static bool HasFullGuard(string source)
    {
        string code = Code(source);
        int call = code.IndexOf("DatabaseHelper.ExecuteInTransaction", StringComparison.Ordinal);
        if (call < 0) throw new Exception("Expected actual SQL transaction callback.");
        int begin = code.IndexOf('{', call), depth = 1, end = begin+1;
        if (begin < 0) throw new Exception("Transaction callback body not found.");
        while (end < code.Length && depth != 0) { depth += code[end]=='{' ? 1 : code[end]=='}' ? -1 : 0; end++; }
        if (depth != 0) throw new Exception("Unbalanced source callback.");
        string body = code[(begin+1)..(end-1)];
        var guard = Regex.Match(body, @"QualityEventInvestigationConcurrencyGuard\.EnsureUnchanged\(\s*(?:conn|connection)\s*,\s*(?:tx|transaction)\s*,\s*qualityEventId\s*,\s*true\s*,\s*loadedInvestigationVersion\s*\)\s*;");
        if (!guard.Success) return false;
        string before = body[..guard.Index];
        if (before.Count(c => c=='{') != before.Count(c => c=='}')) return false;
        string prefix = before.TrimEnd();
        if (prefix.Length != 0 && prefix[^1] != ';' && prefix[^1] != '}') return false;
        if (Regex.IsMatch(before, @"loadedInvestigationVersion\s*=")) return false;
        var write = Regex.Match(body, @"(?:DatabaseHelper\.(?:SaveQualityEventInvestigation|ExecuteNonQueryWithTransaction|AddAuditTrailAdvanced)|PRMQualityEventInvestigationService\.SavePRMQualityEventChecklistAnswers)\s*\(");
        return write.Success && guard.Index < write.Index;
    }
}
