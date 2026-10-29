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
            Expect(!HasFullGuard(source.Replace(line, "loadedInvestigationVersion=\"unreviewed token\";"+line)), "A rebased token was accepted.");
            string removed = source.Replace(line, ""), code = Code(removed);
            int begin = code.IndexOf('{', code.IndexOf("DatabaseHelper.ExecuteInTransaction", StringComparison.Ordinal));
            int depth = 1, end = begin+1;
            while (depth != 0) { depth += code[end]=='{' ? 1 : code[end]=='}' ? -1 : 0; end++; }
            Expect(!HasFullGuard(removed.Insert(end-1, line)), "A guard after controlled writes was accepted.");
        }
        if (checks != 24) throw new Exception("Incomplete source guard detector controls.");
        Console.WriteLine("PASS SITE PRM CAS detector: 24 actual/reference and comment, nested, rebased, late-source controls.");
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
        if (Regex.IsMatch(before, @"loadedInvestigationVersion\s*=")) return false;
        var write = Regex.Match(body, @"(?:DatabaseHelper\.(?:SaveQualityEventInvestigation|ExecuteNonQueryWithTransaction|AddAuditTrailAdvanced)|PRMQualityEventInvestigationService\.SavePRMQualityEventChecklistAnswers)\s*\(");
        return write.Success && guard.Index < write.Index;
    }
}
