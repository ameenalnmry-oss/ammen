using Microsoft.Data.SqlClient;
using PharmaLIMS.Services;
using PharmaLIMS.Services.Investigations;
using System.Data;
using System.Security.Cryptography;
using System.Text;

internal static class ReviewGapClosureIntegration
{
    internal static async Task VerifyAsync(SqlConnection connection)
    {
        if (!connection.Database.StartsWith("PharmaLIMS_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("v304 tests refuse a non-disposable database.");
        Require((await Query(connection, ReviewClosureSchemaContract.Sql)).Rows.Count == 0, "closure schema contract");
        await WaterEvidence(connection);
        await Specifications(connection);
        await Investigations(connection);
        await Authorizations(connection);
        await CultureIdentity(connection);
        await EmCancellation(connection);
        Console.WriteLine("PASS v304 review closure SQL fixtures (WPF acceptance is separate).");
    }

    private static void Require(bool value, string context)
    { if (!value) throw new InvalidOperationException("v304 integration: " + context); }
    private static async Task<DataTable> Query(SqlConnection c, string sql, SqlTransaction? t = null, params SqlParameter[] args)
    {
        using var command = new SqlCommand(sql, c, t) { CommandTimeout = 60 };
        command.Parameters.AddRange(args);
        using var reader = await command.ExecuteReaderAsync();
        var table = new DataTable(); table.Load(reader); return table;
    }
    private static async Task Exec(SqlConnection c, string sql, SqlTransaction? t = null, params SqlParameter[] args)
    { using var command = new SqlCommand(sql, c, t) { CommandTimeout = 60 }; command.Parameters.AddRange(args); await command.ExecuteNonQueryAsync(); }
    private static SqlParameter Arg(string name, object value) => new(name, value);
    private static void Denied(Action action, string context)
    { try { action(); } catch (UnauthorizedAccessException) { return; } throw new InvalidOperationException("v304 authorization accepted: " + context); }
    private static void Stale(Action action, string context)
    { try { action(); } catch (DBConcurrencyException) { return; } throw new InvalidOperationException("v304 stale evidence accepted: " + context); }
    private static async Task SqlDenied(Func<Task> action, string context, int errorNumber)
    { try { await action(); } catch (SqlException ex) when (ex.Number == errorNumber) { return; } throw new InvalidOperationException("v304 SQL control accepted: " + context); }

    private static string WaterSql(string sql)
    {
        foreach (string table in new[] { "LIMS_SchemaVersions", "SampleTests", "Tests", "LabEquipmentUsageHistory", "LabEquipmentUsage",
            "WaterResultResourceEvidence", "WaterResultExecutionEvidence", "EM_EventPlates", "EM_Events", "PRM_SampleTests" })
            sql = sql.Replace("dbo." + table, "#Review_" + table, StringComparison.Ordinal);
        return sql;
    }

    private static async Task WaterEvidence(SqlConnection c)
    {
        await Exec(c, @"CREATE TABLE #Review_LIMS_SchemaVersions(VersionKey nvarchar(40),AppliedAt datetime2(0));
INSERT #Review_LIMS_SchemaVersions VALUES(N'20261008_002','20000101'),(N'20261009_001','20000101'),(N'20261009_002','20000101');
CREATE TABLE #Review_Tests(TestID int,TestName nvarchar(200),Unit nvarchar(50),AlertLimit decimal(18,4),ActionLimit decimal(18,4));
CREATE TABLE #Review_SampleTests(SampleTestID int PRIMARY KEY,SampleID int,TestID int,TestNameSnapshot nvarchar(200),
UnitSnapshot nvarchar(50),AlertLimitSnapshot decimal(18,4),ActionLimitSnapshot decimal(18,4),ResultValue nvarchar(200),
ResultEnteredDate datetime2(0),EnteredBy nvarchar(100),Remarks nvarchar(max),ResultStatus nvarchar(60),DeviationType nvarchar(60),LimitDescription nvarchar(500),ResultRowVersion rowversion);
CREATE TABLE #Review_LabEquipmentUsage(Module nvarchar(30),ParentRecordID int,ResultRecordID int,EquipmentID int);
CREATE TABLE #Review_LabEquipmentUsageHistory(HistoryID bigint IDENTITY,Module nvarchar(30),ParentRecordID int,ResultRecordID int,EquipmentID int,
ChangeType nvarchar(30),SignedBy nvarchar(100),MeaningOfSignature nvarchar(255),ActionReason nvarchar(max),SignedAt datetime2(0));
CREATE TABLE #Review_WaterResultResourceEvidence(EvidenceID bigint IDENTITY,BatchID uniqueidentifier,SampleID int,SampleTestID int,TestID int,ResourceKind nvarchar(30),
EquipmentID int,KitCode nvarchar(100),KitLot nvarchar(120),KitExpiry date,ExecutionDate date,ResultSnapshot nvarchar(200),SignedBy nvarchar(100),SignedAt datetime2(0));
CREATE TABLE #Review_WaterResultExecutionEvidence(EvidenceID bigint IDENTITY,SampleID int,SampleTestID int,TestID int,SampleTemperatureC decimal(5,2),ProcedureReference nvarchar(200),
VerificationReference nvarchar(200),VerificationConfirmed bit,SignedBy nvarchar(100),MeaningOfSignature nvarchar(255),ActionReason nvarchar(max),RawResultSnapshot nvarchar(200),EquipmentID int,SignedAt datetime2(0));
CREATE TABLE #Review_EM_EventPlates(Id int,EventId int,TotalCount int);
CREATE TABLE #Review_EM_Events(Id int,ResultsEnteredDate datetime2(0));
CREATE TABLE #Review_PRM_SampleTests(SampleTestID int,EnteredDate datetime2(0),ResultValue nvarchar(200));");
        int id = 100;
        foreach (string name in new[] { "Appearance", "Appearance (Color & Clarity)", "Color & Clarity", "Colour & Clarity", "Description", "Odor", "Odour", "Taste" })
        {
            await Exec(c, @"INSERT #Review_Tests(TestID,TestName) VALUES(@id,@name);
INSERT #Review_SampleTests(SampleTestID,SampleID,TestID,TestNameSnapshot,ResultValue,ResultEnteredDate,EnteredBy)
VALUES(@id,1,@id,@name,N'0','20261010',N'fixture');
INSERT #Review_WaterResultResourceEvidence(BatchID,SampleID,SampleTestID,TestID,ResourceKind,ExecutionDate,ResultSnapshot,SignedBy,SignedAt)
VALUES(NEWID(),1,@id,@id,N'NO_INSTRUMENT','20261010',N'0',N'fixture','20261010');", null, Arg("@id", id++), Arg("@name", name));
        }
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.EquipmentUsage))).Rows.Count == 0, "all controlled no-instrument policies pass without fabricated equipment");
        await Exec(c, @"INSERT #Review_Tests(TestID,TestName) VALUES(31,N'Residual Chlorine');
INSERT #Review_SampleTests(SampleTestID,SampleID,TestID,TestNameSnapshot,ResultValue,ResultEnteredDate,EnteredBy)
VALUES(31,1,31,N'Residual Chlorine',N'0.1','20261010',N'fixture');
INSERT #Review_WaterResultResourceEvidence(BatchID,SampleID,SampleTestID,TestID,ResourceKind,KitCode,KitLot,KitExpiry,ExecutionDate,ResultSnapshot,SignedBy,SignedAt)
VALUES(NEWID(),1,31,31,N'TEST_KIT',N'KIT',N'LOT','20261010','20261010',N'0.1',N'fixture','20261011');");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.EquipmentUsage))).Rows.Count == 0, "kit expiry uses authoritative execution date across UTC date boundary");
        await Exec(c, "UPDATE #Review_WaterResultResourceEvidence SET KitExpiry='20261009' WHERE SampleTestID=31;");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.EquipmentUsage))).Rows.Count > 0, "expired kit blocked");
        await Exec(c, "UPDATE #Review_WaterResultResourceEvidence SET KitExpiry='20261010',ResultSnapshot=N'bad' WHERE SampleTestID=31;");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.EquipmentUsage))).Rows.Count > 0, "invalid numeric evidence cannot compare as SQL UNKNOWN/pass");
        await Exec(c, "UPDATE #Review_WaterResultResourceEvidence SET ResultSnapshot=N'0.1' WHERE SampleTestID=31; UPDATE #Review_SampleTests SET EnteredBy=NULL,ResultEnteredDate=NULL WHERE SampleTestID=31;");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.EquipmentUsage))).Rows.Count > 0, "signed results missing entry attribution are detected");
        await Exec(c, "UPDATE #Review_SampleTests SET EnteredBy=N'fixture',ResultEnteredDate='20261010' WHERE SampleTestID=31;");
        DataTable original = await Query(c, WaterSql(WaterResultSnapshotSql.Build(false)), null, Arg("@sampleId", 1));
        await Exec(c, @"INSERT #Review_WaterResultResourceEvidence(BatchID,SampleID,SampleTestID,TestID,ResourceKind,KitCode,KitLot,KitExpiry,ExecutionDate,ResultSnapshot,SignedBy,SignedAt)
VALUES(NEWID(),1,31,31,N'TEST_KIT',N'KIT',N'NEW-LOT','20261010','20261010',N'0.1',N'fixture','20261011');");
        DataTable current = await Query(c, WaterSql(WaterResultSnapshotSql.Build(true)), null, Arg("@sampleId", 1));
        Require(((byte[])original.Select("SampleTestID=31")[0]["ResultRowVersion"]).SequenceEqual((byte[])current.Select("SampleTestID=31")[0]["ResultRowVersion"]), "resource-only edit must not require a result-row edit");
        Stale(() => ResultSnapshotGuard.EnsureMatches(original, current, "SampleTestID", "SampleID", 1), "resource-only SQL snapshot conflict");
        await Exec(c, @"INSERT #Review_Tests(TestID,TestName) VALUES(3,N'Conductivity');
INSERT #Review_SampleTests(SampleTestID,SampleID,TestID,TestNameSnapshot,ResultValue,ResultEnteredDate,EnteredBy)
VALUES(3,2,3,N'Conductivity',N'5','20261010',N'fixture');
INSERT #Review_LabEquipmentUsage VALUES(N'WATER',2,3,20);
INSERT #Review_LabEquipmentUsageHistory(Module,ParentRecordID,ResultRecordID,EquipmentID,ChangeType,SignedBy,MeaningOfSignature,ActionReason,SignedAt)
VALUES(N'WATER',2,3,10,N'ASSIGN',N'fixture',N'Instrument use',N'Original','20261009'),(N'WATER',2,3,20,N'REASSIGN',N'fixture',N'Instrument use',N'Correction','20261010');
INSERT #Review_WaterResultExecutionEvidence(SampleID,SampleTestID,TestID,SampleTemperatureC,ProcedureReference,VerificationReference,VerificationConfirmed,SignedBy,MeaningOfSignature,ActionReason,RawResultSnapshot,EquipmentID,SignedAt)
VALUES(2,3,3,25,N'SOP',N'VER',1,N'fixture',N'LES',N'Original',N'5',10,'20261010'),(2,3,3,25,N'SOP',N'VER',1,N'fixture',N'LES',N'Correction',N'5',20,'20261011');");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.Les))).Rows.Count == 0, "old LES instrument remains historical while latest instrument matches current assignment");
        await Exec(c, "UPDATE #Review_WaterResultExecutionEvidence SET RawResultSnapshot=N'bad' WHERE EquipmentID=20;");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.Les))).Rows.Count > 0, "invalid latest LES result blocked");
        await Exec(c, "UPDATE #Review_WaterResultExecutionEvidence SET RawResultSnapshot=N'5',SampleTemperatureC=27 WHERE EquipmentID=20;");
        Require((await Query(c, WaterSql(WaterEvidencePreflightSql.Les))).Rows.Count > 0, "out-of-range stored LES temperature blocked");
        Console.WriteLine("PASS F02–F08 actual water snapshot/preflight SQL contracts.");
    }

    private static async Task Specifications(SqlConnection c)
    {
        using var t = c.BeginTransaction();
        try
        {
            string no = "SPEC304-" + Guid.NewGuid().ToString("N"); const string category = "RawMaterial";
            await Exec(c, @"INSERT dbo.PRM_SpecificationTests(SpecificationNo,SampleCategory,VersionNo,TestCode,TestName,SpecificationText,Unit,ResultType,SpecificationLimit,RequiredTest,SortOrder,ApprovalStatus,IsActive,CreatedBy,MinimumElapsedHours)
VALUES(@no,@category,1,N'TAMC',N'TAMC',N'NMT 100 CFU/g',N'CFU/g',N'Numeric',100,1,1,N'Draft',0,N'author-a',120);", t, Arg("@no", no), Arg("@category", category));
            SpecificationEvidenceService.Record(c, t, no, category, 1, "Draft Created", "[]", "author-a", "Create controlled fixture");
            DataTable original = SpecificationEvidenceService.Load(c, t, no, category, 1);
            string before = SpecificationEvidenceService.Json(c, t, no, category, 1);
            await Exec(c, "UPDATE dbo.PRM_SpecificationTests SET SpecificationText=N'NMT 1000 CFU/g',SpecificationLimit=1000 WHERE SpecificationNo=@no;", t, Arg("@no", no));
            SpecificationEvidenceService.Record(c, t, no, category, 1, "Draft Updated", before, "editor-b", "Change acceptance limit");
            DataTable current = SpecificationEvidenceService.Load(c, t, no, category, 1);
            Stale(() => ResultSnapshotGuard.EnsureMatches(original, current, "SpecificationTestID", "VersionNo", 1), "full specification content");
            foreach (string editor in new[] { "author-a", "editor-b" })
                await SqlDenied(() => Task.Run(() => SpecificationEvidenceService.EnsureReviewIndependent(c, t, no, category, 1, editor)), "draft editor reviews own changes", 56462);
            SpecificationEvidenceService.EnsureReviewIndependent(c, t, no, category, 1, "reviewer-c");
            Require(Convert.ToString(current.Rows[0]["CreatedBy"]) == "author-a", "editing must retain the original creator");
            for (int i = 0; i < 20; i++) await Exec(c, @"INSERT dbo.PRM_SpecificationTests(SpecificationNo,SampleCategory,VersionNo,TestCode,TestName,SpecificationText,ResultType,RequiredTest,SortOrder,ApprovalStatus,IsActive,CreatedBy,MinimumElapsedHours)
VALUES(@no,@category,1,@code,@code,@text,N'Qualitative',1,10,N'Draft',0,N'author-a',120);", t,
                Arg("@no", no), Arg("@category", category), Arg("@code", "Q" + i), Arg("@text", "Absent " + new string(' ', 490)));
            string complete = SpecificationEvidenceService.Json(c, t, no, category, 1);
            Require(complete.Length > 8000, "content JSON must retain more than 8 KB");
            DataTable signature = await Query(c, @"INSERT dbo.PRM_SpecificationSignatures(SpecificationNo,SampleCategory,VersionNo,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole)
OUTPUT INSERTED.SignatureID VALUES(@no,@category,1,N'Reviewed',N'Independent review',N'reviewer-c',N'Review full content',N'Reviewer');", t, Arg("@no", no), Arg("@category", category));
            long signatureId = Convert.ToInt64(signature.Rows[0][0]);
            SpecificationEvidenceService.Record(c, t, no, category, 1, "Reviewed", before, "reviewer-c", "Independent review", signatureId);
            DataTable history = await Query(c, "SELECT NewRowsJson,ContentHash,SignatureID FROM dbo.PRM_SpecificationContentHistory WHERE SignatureID=@id;", t, Arg("@id", signatureId));
            string evidence = Convert.ToString(history.Rows[0]["NewRowsJson"])!;
            Require(evidence == complete && ((byte[])history.Rows[0]["ContentHash"]).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))), "signature binds complete UTF-8 SHA-256 content");
            await SqlDenied(() => Exec(c, "UPDATE dbo.PRM_SpecificationContentHistory SET ChangeReason=N'tamper' WHERE SignatureID=@id;", t, Arg("@id", signatureId)), "signed content history update", 56456);
        }
        finally { if (t.Connection != null) t.Rollback(); }
        Console.WriteLine("PASS F09/F10 complete specification content, independent editors and signature hash SQL.");
    }

    private static async Task Investigations(SqlConnection c)
    {
        using var t = c.BeginTransaction();
        try
        {
            string no = "QE304-" + Guid.NewGuid().ToString("N");
            int id = Convert.ToInt32((await Query(c, @"INSERT dbo.QualityEvents(EventNumber,EventType,Severity,SampleNumber,SourceModule,CurrentStatus,DetectedBy,DetectedDate,DetectionSource,InitialDescription,CreatedBy,CreatedDate)
OUTPUT INSERTED.QualityEventID VALUES(@no,N'PRM Microbiology Result Deviation',N'Major',@no,N'PRM',N'Open',N'author-a',SYSDATETIME(),N'Fixture',N'Original narrative',N'author-a',SYSDATETIME());", t, Arg("@no", no))).Rows[0][0]);
            int question = Convert.ToInt32((await Query(c, @"INSERT dbo.QualityEventChecklistQuestions(SectionName,QuestionText,AppliesToSampleType,AppliesToTestCategory,AnswerType,IsRequired,ExpectedAnswer,QuestionLogic,SortOrder,IsActive)
OUTPUT INSERTED.QuestionID VALUES(N'Fixture',N'Controlled question',N'PRM',N'PRM General',N'YesNoNA',1,N'Yes',N'PositiveCheck',900000,1);", t)).Rows[0][0]);
            const string originalComment = "[N/A: Not applicable to product/material/sample/test type] Stable note   ";
            await Exec(c, "INSERT dbo.QualityEventChecklistAnswers(QualityEventID,QuestionID,AnswerValue,Comments,AnsweredBy,AnsweredDate) VALUES(@id,@question,N'N/A',@comment,N'author-a','20260101');", t,
                Arg("@id", id), Arg("@question", question), Arg("@comment", originalComment));
            DataTable raw = PRMQualityEventInvestigationService.LoadChecklist(c, t, id, "");
            DataTable visible = raw.Copy(); DataRow visibleRow = visible.Select("QuestionID=" + question)[0];
            visibleRow["Comments"] = "Stable note"; visibleRow["NAJustification"] = "Not applicable to product/material/sample/test type";
            PRMQualityEventInvestigationService.SavePRMQualityEventChecklistAnswers(c, t, id, visible, "editor-b", visible.Copy());
            DataTable unchanged = await Query(c, "SELECT * FROM dbo.QualityEventChecklistAnswers WHERE QualityEventID=@id AND QuestionID=@question;", t, Arg("@id", id), Arg("@question", question));
            Require(Convert.ToString(unchanged.Rows[0]["Comments"]) == originalComment && Convert.ToString(unchanged.Rows[0]["AnsweredBy"]) == "author-a" && Convert.ToDateTime(unchanged.Rows[0]["AnsweredDate"]).Year == 2026,
                "unchanged normalized N/A answer retains original bytes, author and timestamp");
            string before = PRMQualityEventInvestigationService.CaptureHistoryJson(c, t, id, "", "QualityEventChecklistAnswers");
            DataTable baseline = visible.Copy(); visibleRow["AnswerValue"] = ""; visibleRow["Comments"] = ""; visibleRow["NAJustification"] = "";
            PRMQualityEventInvestigationService.SavePRMQualityEventChecklistAnswers(c, t, id, visible, "editor-b", baseline);
            PRMQualityEventInvestigationService.RecordHistory(c, t, id, "", "QualityEventChecklistAnswers", before, "editor-b", "Clear answer with controlled rationale");
            DataTable changed = await Query(c, "SELECT * FROM dbo.QualityEventChecklistAnswers WHERE QualityEventID=@id AND QuestionID=@question;", t, Arg("@id", id), Arg("@question", question));
            Require(changed.Rows[0]["AnswerValue"] == DBNull.Value && changed.Rows[0]["Comments"] == DBNull.Value && Convert.ToString(changed.Rows[0]["AnsweredBy"]) == "editor-b", "cleared answer persists and records actual editor");
            DataTable current = PRMQualityEventInvestigationService.LoadChecklist(c, t, id, "");
            Stale(() => ResultSnapshotGuard.EnsureMatches(raw, current, "QuestionID", "QualityEventID", id), "checklist answer/attribution changes");
            DataTable header = PRMQualityEventInvestigationService.LoadHeader(c, t, id);
            string oldHeader = PRMQualityEventInvestigationService.CaptureHistoryJson(c, t, id, "", "QualityEvents");
            await Exec(c, "UPDATE dbo.QualityEvents SET RootCauseDetails=@details WHERE QualityEventID=@id;", t, Arg("@id", id), Arg("@details", new string('X', 9000)));
            DataTable newHeader = PRMQualityEventInvestigationService.LoadHeader(c, t, id);
            Stale(() => ResultSnapshotGuard.EnsureMatches(header, newHeader, "QualityEventID", "QualityEventID", id), "investigation narrative");
            PRMQualityEventInvestigationService.RecordHistory(c, t, id, "", "QualityEvents", oldHeader, "editor-b", "Update full narrative");
            DataTable history = await Query(c, "SELECT NewRowsJson FROM dbo.QualityEventInvestigationEvidenceHistory WHERE QualityEventID=@id AND EvidenceTable=N'QualityEvents';", t, Arg("@id", id));
            Require(Convert.ToString(history.Rows[0][0])!.Length > 9000, "narrative history must not truncate to 8 KB");
        }
        finally { if (t.Connection != null) t.Rollback(); }
        Console.WriteLine("PASS F11 raw investigation snapshots and unchanged/cleared answer attribution SQL.");
    }

    private static async Task Authorizations(SqlConnection c)
    {
        using var t = c.BeginTransaction();
        try
        {
            string user = "AUTH304-" + Guid.NewGuid().ToString("N");
            await Exec(c, @"INSERT dbo.Users(Username,PasswordHash,FullName,Role,IsActive,MustChangePassword,CanIssueCOA,CanCancelCOA,CanEnterResults,CanRegisterSamples)
VALUES(@user,N'TEST_ONLY_INVALID_ENCODING',N'Disposable fixture',N'Technician',1,0,1,0,0,1);", t, Arg("@user", user));
            Denied(() => ReviewWorkflowAuthorization.EnsurePrmReissue(c, t, user, false), "issue-only account must not reissue");
            ReviewWorkflowAuthorization.EnsureCultureEntry(c, t, user, "receive culture media", false);
            await Exec(c, "UPDATE dbo.Users SET CanCancelCOA=1,CanRegisterSamples=0,CanEnterResults=1 WHERE Username=@user;", t, Arg("@user", user));
            ReviewWorkflowAuthorization.EnsurePrmReissue(c, t, user, false);
            ReviewWorkflowAuthorization.EnsureCultureEntry(c, t, user, "prepare culture media", false);
            await Exec(c, "UPDATE dbo.Users SET CanCancelCOA=0,CanEnterResults=0 WHERE Username=@user;", t, Arg("@user", user));
            Denied(() => ReviewWorkflowAuthorization.EnsurePrmReissue(c, t, user, false), "revoked cancellation permission");
            Denied(() => ReviewWorkflowAuthorization.EnsureCultureEntry(c, t, user, "receive culture media", false), "both entry permissions revoked");
            await Exec(c, "UPDATE dbo.Users SET CanCancelCOA=1,CanEnterResults=1,IsActive=0 WHERE Username=@user;", t, Arg("@user", user));
            Denied(() => ReviewWorkflowAuthorization.EnsureCultureEntry(c, t, user, "prepare culture media", false), "inactive culture signer");
            Denied(() => ReviewWorkflowAuthorization.EnsurePrmReissue(c, t, user, false), "inactive reissue signer");
        }
        finally { if (t.Connection != null) t.Rollback(); }
        Console.WriteLine("PASS F12/F16 transaction-scoped authorization and permission revocation SQL.");
    }

    private static async Task CultureIdentity(SqlConnection c)
    {
        using var t = c.BeginTransaction();
        try
        {
            string code = "C304-" + Guid.NewGuid().ToString("N")[..20];
            SqlParameter[] MasterArgs(string name = "Recorded media", string supplier = "Manufacturer A") => new[] {
                Arg("@MediaCode",code),Arg("@MediaName",name),Arg("@MediaType","Bottle"),Arg("@StorageCondition","Controlled storage"),Arg("@Manufacturer",supplier),Arg("@UserName","fixture") };
            int media = Convert.ToInt32((await Query(c, CultureMediaWriteContract.EnsureMaster, t, MasterArgs())).Rows[0][0]);
            int same = Convert.ToInt32((await Query(c, CultureMediaWriteContract.EnsureMaster, t, MasterArgs(supplier: "Supplier B"))).Rows[0][0]);
            Require(media == same && Convert.ToString((await Query(c, "SELECT Manufacturer FROM dbo.CultureMedia WHERE MediaID=@id;", t, Arg("@id", media))).Rows[0][0]) == "Manufacturer A", "a receipt supplier must not rewrite master manufacturer");
            await SqlDenied(() => Query(c, CultureMediaWriteContract.EnsureMaster, t, MasterArgs("Renamed media")), "shared master identity rewrite", 56461);
            int lot = Convert.ToInt32((await Query(c, @"INSERT dbo.CultureMediaLots(MediaID,LotNumber,ManufacturerLot,Supplier,ReceivedDate,ExpiryDate,QuantityReceived,InitialStockG,CurrentStockG,ReceivedBy)
OUTPUT INSERTED.MediaLotID VALUES(@media,@lot,N'MLOT',N'Supplier B','20261010','20301010',100,100,100,N'fixture');", t, Arg("@media", media), Arg("@lot", code))).Rows[0][0]);
            int qualification = Convert.ToInt32((await Query(c, CultureQualificationStartContract.Sql, t,
                CultureQualificationStartContract.Parameters("GPT304-" + Guid.NewGuid().ToString("N")[..20], lot, "actual-signer", 48m))).Rows[0][0]);
            DataTable started = await Query(c, "SELECT PerformedBy,QualificationStatus,QualificationStartedAt,MinimumIncubationHoursSnapshot FROM dbo.MediaQualifications WHERE MediaQualificationID=@id;", t, Arg("@id", qualification));
            Require(Convert.ToString(started.Rows[0]["PerformedBy"]) == "actual-signer" && Convert.ToString(started.Rows[0]["QualificationStatus"]) == "In Progress" && started.Rows[0]["QualificationStartedAt"] != DBNull.Value,
                "GPT start binds the actual performer and database start time");
            await SqlDenied(() => Exec(c, CultureMediaWriteContract.GuardLotIdentity, t, Arg("@MediaLotID", lot)), "qualification history freezes lot amendment", 56459);
            await Exec(c, "UPDATE dbo.CultureMediaLots SET CurrentStockG=90 WHERE MediaLotID=@id;", t, Arg("@id", lot));
            Require(Convert.ToDecimal((await Query(c, "SELECT CurrentStockG FROM dbo.CultureMediaLots WHERE MediaLotID=@id;", t, Arg("@id", lot))).Rows[0][0]) == 90m, "legitimate stock updates remain permitted");
            t.Commit();
            await SqlDenied(() => Exec(c, "UPDATE dbo.CultureMedia SET MediaName=N'Changed history' WHERE MediaID=@id;", null, Arg("@id", media)), "referenced master trigger", 56457);
            await SqlDenied(() => Exec(c, "UPDATE dbo.CultureMediaLots SET LotNumber=N'Changed history' WHERE MediaLotID=@id;", null, Arg("@id", lot)), "referenced lot trigger", 56458);
        }
        finally { if (t.Connection != null) t.Rollback(); }
        Console.WriteLine("PASS F01/F17 GPT parameter binding and referenced culture identity protection SQL.");
    }

    private static async Task EmCancellation(SqlConnection c)
    {
        using var t = c.BeginTransaction();
        try
        {
            string no = "EM304-" + Guid.NewGuid().ToString("N")[..20];
            int plan = Convert.ToInt32((await Query(c, @"INSERT dbo.EM_Plans(PlanNo,PlanType,SourceType,LoginDate,SampleDueDate,RequiredDate,Status,CreatedBy)
OUTPUT INSERTED.PlanID VALUES(@no,N'Routine',N'Manual','20261010','20261010','20261010',N'Ready for Results',N'fixture');", t, Arg("@no", no))).Rows[0][0]);
            int area = Convert.ToInt32((await Query(c, "SELECT TOP(1) Id FROM dbo.EM_Areas ORDER BY Id;", t)).Rows[0][0]);
            int eventId = Convert.ToInt32((await Query(c, @"INSERT dbo.EM_Events(EventNo,AreaId,EventDate,PlanID,WorkflowStatus,FinalResult)
OUTPUT INSERTED.Id VALUES(@no,@area,'20261010',@plan,N'Pending',N'Pending');", t, Arg("@no", no), Arg("@area", area), Arg("@plan", plan))).Rows[0][0]);
            SqlParameter[] CancelArgs(string status) => new[] { Arg("@Plan",plan),Arg("@Expected",status),Arg("@User","fixture"),Arg("@Role","QA"),Arg("@Meaning","Cancel plan and events"),Arg("@Reason","Controlled cancellation") };
            Require(PharmaLIMS.DatabaseHelper.EnsureEmSourcePlanInTransaction(c,t,eventId,true)==plan,"actual event source-plan guard accepts released plan");
            await Exec(c,"UPDATE dbo.EM_Events SET WorkflowStatus=NULL,FinalResult=N' Cancelled' WHERE Id=@id;",t,Arg("@id",eventId));
            Stale(()=>PharmaLIMS.DatabaseHelper.EnsureEmSourcePlanInTransaction(c,t,eventId,true),"normalized legacy cancellation");
            await Exec(c, "UPDATE dbo.EM_Events SET WorkflowStatus=N'Approved',ApprovedBy=N'qa',ApprovedDate=SYSDATETIME() WHERE Id=@id;", t, Arg("@id", eventId));
            await SqlDenied(() => Query(c, EmSourcePlanSql.Cancel, t, CancelArgs("Ready for Results")), "plan containing approved evidence", 55516);
            await Exec(c, "UPDATE dbo.EM_Events SET WorkflowStatus=NULL,FinalResult=N' Approved ',ApprovedBy=NULL,ApprovedDate=NULL WHERE Id=@id;", t, Arg("@id", eventId));
            await SqlDenied(() => Query(c, EmSourcePlanSql.Cancel, t, CancelArgs("Ready for Results")), "legacy approved event fallback", 55516);
            await Exec(c, "UPDATE dbo.EM_Events SET WorkflowStatus=N'Pending',FinalResult=N'Pending',ApprovedBy=NULL,ApprovedDate=NULL WHERE Id=@id;", t, Arg("@id", eventId));
            DataTable changes = await Query(c, EmSourcePlanSql.Cancel, t, CancelArgs("Ready for Results"));
            Require(changes.Rows.Count == 2, "cancellation returns plan and event audit targets");
            DataTable status = await Query(c, "SELECT E.WorkflowStatus,P.Status FROM dbo.EM_Events E JOIN dbo.EM_Plans P ON P.PlanID=E.PlanID WHERE E.Id=@id;", t, Arg("@id", eventId));
            Require(Convert.ToString(status.Rows[0][0]) == "Cancelled" && Convert.ToString(status.Rows[0][1]) == "Cancelled", "plan cancellation disables linked result events atomically");
            Require(Convert.ToInt32((await Query(c, "SELECT COUNT(*) FROM dbo.EM_EventSignatures WHERE EventID=@id AND ActionType=N'EM Plan Cancellation';", t, Arg("@id", eventId))).Rows[0][0]) == 1, "event cancellation signature is retained");
        }
        finally { if (t.Connection != null) t.Rollback(); }
        Console.WriteLine("PASS F14 actual plan/event cancellation SQL and approved-evidence refusal.");
    }
}
