using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

// This is a separate process/phase: AppConfig must not retain the original 122-check fixture.
internal sealed partial class UatRun
{
    private readonly List<string> _waterPasses = new();
    private void WaterCheck(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Water native assertion: " + label);
        _waterPasses.Add(label);
        Console.WriteLine("PASS WATER: " + label);
        Console.WriteLine("::notice title=Water native milestone::" + _waterPasses.Count + ": " + label);
    }
    private async Task WaterSqlDenied(string client, string sql, string label)
    {
        try { await Exec(client, sql); }
        catch (SqlException ex) when (ex.Number == 229) { WaterCheck(true, label + " (SQL229)"); return; }
        throw new InvalidOperationException("Expected exact SQL229: " + label);
    }
    private async Task WaterRejected(Func<Task> action, string label, string code)
    {
        try { await action(); }
        catch (TrustedSigningRejectedException ex) when (ex.ErrorCode == code) { WaterCheck(true, label + " (" + code + ")"); return; }
        throw new InvalidOperationException("Expected exact trusted rejection " + code + ": " + label);
    }

    public async Task ExecuteWaterWorkflowAsync()
    {
        const string author = "uat-water-author", reviewer = "uat-water-reviewer", qa = "uat-water-qa";
        if (WindowsIdentity.GetCurrent().User?.Value != settings.CallerSid || settings.CallerSid == settings.ServiceSid)
            throw new UnauthorizedAccessException("A real independent native caller is required.");
        var master = new SqlConnectionStringBuilder(settings.MasterConnectionString);
        if (master.DataSource != "localhost,14335" || !master.IntegratedSecurity || master.TrustServerCertificate || master.Encrypt == SqlConnectionEncryptOption.Optional)
            throw new InvalidOperationException("The exact isolated validated-TLS SQL fixture is required.");
        _db = "EQ001_UAT_WaterNative_" + Guid.NewGuid().ToString("N");
        _serviceLogin = Environment.MachineName + "\\" + settings.ServiceAccount;
        byte[]? oldHost = File.Exists(_hostConfigPath) ? File.ReadAllBytes(_hostConfigPath) : null;
        FileSecurity? oldHostAcl = File.Exists(_hostConfigPath) ? new FileInfo(_hostConfigPath).GetAccessControl() : null;
        _oldSettings = File.Exists(_appSettingsPath) ? File.ReadAllBytes(_appSettingsPath) : null;
        Directory.CreateDirectory(settings.ResultsDirectory);
        string stage = "create fresh migrated database";
        try
        {
            await Exec(settings.MasterConnectionString, $"CREATE DATABASE [{_db}];CREATE LOGIN [{_serviceLogin}] FROM WINDOWS;CREATE LOGIN [{_clientLogin}] WITH PASSWORD='{_sqlPassword}',CHECK_POLICY=OFF;");
            _admin = new SqlConnectionStringBuilder(settings.MasterConnectionString) { InitialCatalog = _db }.ConnectionString;
            await PrepareSchema();
            await PrepareNativeWaterSiteContract();
            // Retain the existing host's authentication rights; grant no direct clinical DML.
            await Exec(_admin, $"CREATE USER PharmaLIMS_TrustedEquipmentSigner FOR LOGIN [{_serviceLogin}];GRANT SELECT ON SCHEMA::dbo TO PharmaLIMS_TrustedEquipmentSigner;GRANT UPDATE ON dbo.Users TO PharmaLIMS_TrustedEquipmentSigner;GRANT INSERT ON dbo.AuditTrail TO PharmaLIMS_TrustedEquipmentSigner;CREATE USER [{_clientLogin}] FOR LOGIN [{_clientLogin}];GRANT SELECT ON SCHEMA::dbo TO [{_clientLogin}];DENY SELECT ON dbo.Users(PasswordHash,PasswordHashNew,PasswordSalt) TO [{_clientLogin}];");
            await PrepareActivitySchema();
            foreach (string actor in new[] { "uat-signer", author, reviewer, qa }) await SeedUser(actor, false);
            await Exec(_admin, "UPDATE dbo.Users SET Role=CASE Username WHEN N'uat-water-qa' THEN N'QA' WHEN N'uat-water-reviewer' THEN N'Supervisor' ELSE N'Analyst' END,CanRegisterSamples=1,CanEnterResults=1,CanReviewResults=1,CanApproveResults=1,PasswordChangedAt=SYSUTCDATETIME(),MustChangePassword=0,IsActive=1,IsLocked=0;");
            string template = File.ReadAllText(Path.Combine(settings.RepositoryRoot, "scripts/EQ001_UAT_Water_Analysis.sql"));
            string sql = WaterWorkflowSqlContract.ComposeStage(template)
                .Replace("DECLARE @VerifiedIsolatedUat bit = 0;", "DECLARE @VerifiedIsolatedUat bit = 1;", StringComparison.Ordinal)
                .Replace("EQ001_UAT_AMEEN_20261024", _db, StringComparison.Ordinal)
                .Replace("@@SERVERNAME<>N'AMEEN\\SQLEXPRESS'", "1=0", StringComparison.Ordinal)
                .Replace("@@SERVERNAME<>N''AMEEN\\SQLEXPRESS''", "1=0", StringComparison.Ordinal);
            await Exec(_admin, sql);
            await Exec(_admin, sql);
            WaterCheck(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.database_principals WHERE name=N'PharmaLIMS_TrustedEquipmentSigner' AND type=N'U'")) == 1, "owner boundary retains the real Windows SQL principal");
            await SeedNativeWater();
            string client = new SqlConnectionStringBuilder(_admin) { IntegratedSecurity = false, UserID = _clientLogin, Password = _sqlPassword }.ConnectionString;
            await WaterSqlDenied(client, "UPDATE dbo.Samples SET Status=N'In Analysis' WHERE SampleNumber=N'WATER-NATIVE-1'", "reproduce the standard desktop StartAnalysis permission failure");
            await WaterSqlDenied(client, "UPDATE dbo.SampleTests SET ResultValue=1", "desktop cannot write results directly");
            await WaterSqlDenied(client, "EXEC dbo.EQ001_UAT_WaterWorkflow", "desktop cannot execute owner workflow procedure");
            WaterCheck(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM sys.database_permissions WHERE grantee_principal_id=DATABASE_PRINCIPAL_ID(N'PharmaLIMS_TrustedEquipmentSigner') AND permission_name IN(N'INSERT',N'UPDATE',N'DELETE') AND major_id IN(OBJECT_ID(N'dbo.Samples'),OBJECT_ID(N'dbo.SampleTests'),OBJECT_ID(N'dbo.Water_PlanSamples'),OBJECT_ID(N'dbo.WaterResultResourceEvidence')) AND state IN(N'G',N'W')")) == 0, "service has no direct clinical DML grants");
            var hostSettings = new TrustedHostSettings { PipeName = settings.PipeName, ConnectionString = new SqlConnectionStringBuilder(_admin) { IntegratedSecurity = true }.ConnectionString, CertificatePath = settings.CertificatePath, CertificatePassword = settings.CertificatePassword, EnableWaterAnalysisUat = false, EnableWaterRegistrationUat = false, WindowsAccounts = new() { [settings.CallerSid] = "uat-signer", [settings.ServiceSid] = "uat-signer" } };
            File.WriteAllText(_hostConfigPath, JsonSerializer.Serialize(hostSettings)); ProtectHostConfig();
            string protectedPassword = (string)typeof(DatabaseConnection).Assembly.GetType("PharmaLIMS.Infrastructure.ProtectedSecretStore")!.GetMethod("ProtectCurrentUserBase64", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { _sqlPassword })!;
            foreach (string name in new[] { "PHARMALIMS_CONNECTION_STRING", "PHARMALIMS_DPAPI_PROTECTED_PASSWORD", "PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES" }) Environment.SetEnvironmentVariable(name, null);
            File.WriteAllText(_appSettingsPath, JsonSerializer.Serialize(new { Environment = "Production", Database = new { Server = master.DataSource, Database = _db, IntegratedSecurity = false, UserId = _clientLogin, DpapiProtectedPassword = protectedPassword, Encrypt = true, TrustServerCertificate = false }, Runtime = new { ApplyStartupDatabaseUpdates = false, DevelopmentAdminFullPermissions = false }, TrustedSigning = new { Enabled = true, PipeName = settings.PipeName, ServerCertificateSha256 = settings.CertificateSha256 } }));
            var actual = new SqlConnectionStringBuilder(AppConfig.ConnectionString);
            WaterCheck(AppConfig.IsProduction && actual.InitialCatalog == _db && actual.UserID == _clientLogin && !actual.IntegratedSecurity && !actual.TrustServerCertificate && actual.Encrypt != SqlConnectionEncryptOption.Optional, "real WPF uses account-bound readonly SQL credentials and validated TLS");
            await StartHost();
            var pipe = new WindowsTrustedPipeClient(new(settings.PipeName, settings.CertificateSha256));
            WaterCheck(WindowsIdentity.GetCurrent().User!.Value != settings.ServiceSid, "desktop native SID differs from the SCM service SID");
            stage = "real TLS and native caller identity";
            try { await new WindowsTrustedPipeClient(new(settings.PipeName, new string('0',64))).ApplicationAsync(new(Guid.NewGuid(), "Reauthenticate", author, _password)); throw new Exception("Bad TLS pin accepted"); }
            catch (System.Security.Authentication.AuthenticationException) { WaterCheck(true, "Water application authentication rejects a false TLS certificate pin"); }
            var input = await NativeRequest("StartAnalysis");
            await WaterRejected(async () => { await NativeCall(pipe, input, author); }, "independent analysis activation flag refuses a signed native request", "WaterWorkflowDisabled");
            StopHost(); hostSettings.EnableWaterAnalysisUat = true;
            File.WriteAllText(_hostConfigPath, JsonSerializer.Serialize(hostSettings)); ProtectHostConfig(); await StartHost();
            await WaterRejected(() => pipe.ApplicationAsync(new(Guid.NewGuid(), "WaterWorkflow", author, UserJson: WaterWorkflowPolicy.CanonicalPayload(input), Reason: WaterReason, Meaning: WaterMeaning)), "missing ticket cannot mutate Water", "Denied");
            var forged = await NativeEnvelope(pipe, input, author);
            await WaterRejected(() => pipe.ApplicationAsync(forged with { Username = qa }), "ticket cannot select another app actor", "Denied");
            var revoked = await NativeEnvelope(pipe, input, author);
            await Exec(_admin, "UPDATE dbo.Users SET CanEnterResults=0 WHERE Username=N'uat-water-author';");
            await WaterRejected(() => pipe.ApplicationAsync(revoked), "permission rowversion invalidates a fresh ticket", "Denied");
            await Exec(_admin, "UPDATE dbo.Users SET CanEnterResults=1 WHERE Username=N'uat-water-author';");
            var stale = await NativeEnvelope(pipe, await NativeRequest("StartAnalysis"), author);
            await Exec(_admin, "UPDATE dbo.Samples SET ModifiedBy=N'fixture-concurrent-change' WHERE SampleNumber=N'WATER-NATIVE-1';");
            await WaterRejected(() => pipe.ApplicationAsync(stale), "real service rejects stale sample snapshots", "WaterWorkflowSnapshotChanged");
            var cancelInput = await NativeRequest("StartAnalysis"); Guid cancelledId = Guid.NewGuid();
            var pending = await NativeCall(pipe, cancelInput, author, cancelledId, "WaterWorkflowResolve");
            WaterCheck(pending.State == "Pending", "unsubmitted native request resolves pending");
            var cancelled = await NativeCall(pipe, cancelInput, author, cancelledId, "WaterWorkflowCancel");
            WaterCheck(cancelled.State == "Cancelled", "native cancellation records a permanent tombstone");
            WaterCheck((await NativeCall(pipe, cancelInput, author, cancelledId)).State == "Cancelled", "late dispatch cannot mutate a cancelled request");
            stage = "six signed real ResultsEntry WPF operations";
            await NativeUiOperation(author, "CorrectTimes", "Registered");
            await NativeUiOperation(author, "StartAnalysis", "In Analysis");
            var duplicateInput = await NativeRequest("SaveResults");
            // The visible complete test set comes from the same production formatter.
            duplicateInput = duplicateInput with { Results = duplicateInput.Results.Select(r => r with { ResultValue = "Clear/Colorless" }).ToArray() };
            var rollback = await NativeEnvelope(pipe, duplicateInput, author);
            long signaturesBefore = Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM dbo.ElectronicSignatures WHERE SampleID=1"));
            await Exec(_admin, "CREATE TRIGGER dbo.NativeWaterAuditFailure ON dbo.AuditTrail AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE TableName=N'Samples' AND SampleNumber=N'WATER-NATIVE-1') THROW 57999,'Synthetic native audit failure',1; END;");
            try { await WaterRejected(() => pipe.ApplicationAsync(rollback), "audit failure rejects service save", "StoreUnavailable"); }
            finally { await Exec(_admin, "DROP TRIGGER dbo.NativeWaterAuditFailure;"); }
            WaterCheck(Convert.ToString(await Scalar("SELECT Status FROM dbo.Samples WHERE SampleID=1")) == "In Analysis" && Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM dbo.ElectronicSignatures WHERE SampleID=1")) == signaturesBefore && Convert.ToInt32(await Scalar($"SELECT COUNT(*) FROM dbo.EQ001_WaterWorkflowReceipts WHERE RequestId='{rollback.RequestId}'")) == 0 && await Scalar("SELECT ResultValue FROM dbo.SampleTests WHERE SampleID=1") is DBNull, "failed audit atomically rolls back result status signature and receipt");
            await NativeUiOperation(author, "SaveResults", "Results Entered");
            WaterCheck(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.WaterResultResourceEvidence WHERE SampleID=1 AND ResourceKind=N'NO_INSTRUMENT'")) == 1, "actual WPF save appends NO_INSTRUMENT signed evidence");
            await NativeUiOperation(author, "SubmitReview", "Under Review");
            Guid submitId = (Guid)(await Scalar("SELECT RequestId FROM dbo.EQ001_WaterWorkflowReceipts WHERE SampleId=1 AND Operation=N'SubmitReview'"))!;
            // Recover exactly the UI's reason/meaning and canonical input, rather than inventing a second identity.
            var historical = await NativeReceiptRequest(submitId);
            var replay = await NativeCall(pipe, historical.Payload, author, submitId, "WaterWorkflowResolve", historical.Reason, historical.Meaning);
            var duplicate = await NativeCall(pipe, historical.Payload, author, submitId, "WaterWorkflow", historical.Reason, historical.Meaning);
            WaterCheck(replay == duplicate && replay.Status == "Under Review", "new-ticket duplicate and resolve return the identical historical UI result");
            WaterCheck(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.ElectronicSignatures WHERE SampleID=1 AND ActionType=N'Submit Review'")) == 1, "UI request retry never adds a second signature");
            await WaterRejected(async () => { await NativeCall(pipe, await NativeRequest("Review"), author); }, "author cannot review their own Water result", "WaterWorkflowStateRejected");
            await NativeUiOperation(reviewer, "Review", "Reviewed");
            await WaterRejected(async () => { await NativeCall(pipe, await NativeRequest("Approve"), reviewer); }, "reviewer cannot approve the same Water result", "WaterWorkflowStateRejected");
            await WaterRejected(async () => { await NativeCall(pipe, await NativeRequest("Approve"), author); }, "author cannot approve despite valid approval permission", "WaterWorkflowStateRejected");
            await NativeUiOperation(qa, "Approve", "Approved");
            WaterCheck(Convert.ToString(await Scalar("SELECT Status FROM dbo.Water_Plans WHERE PlanNo=N'WATER-NATIVE-PLAN'")) == "Completed" && Convert.ToString(await Scalar("SELECT Status FROM dbo.Water_PlanSamples WHERE SampleID=1")) == "Approved", "QA approval atomically synchronizes the linked Water plan");
            WaterCheck(Convert.ToInt32(await Scalar($"SELECT COUNT(*) FROM dbo.EQ001_WaterWorkflowReceipts WHERE SampleId=1 AND State=N'Committed' AND CallerSid=N'{settings.CallerSid}'")) == 6, "six operations have immutable receipts bound to the real kernel SID");
            WaterCheck(Convert.ToInt32(await Scalar("SELECT COUNT(DISTINCT SignedBy) FROM dbo.ElectronicSignatures WHERE SampleID=1 AND SourceApplication=N'PharmaLIMS.TrustedSigning'")) == 3, "all six operations have attributable three-actor service signatures");
            WaterCheck(Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.AuditTrail WHERE TableName=N'Samples' AND SampleNumber=N'WATER-NATIVE-1' AND SourceApplication=N'PharmaLIMS.TrustedSigning'")) == 6, "each signed clinical mutation has committed service audit evidence");
            await WaterSqlDenied(client, "UPDATE dbo.Samples SET Status=N'Registered' WHERE SampleID=1", "desktop remains readonly after complete approval");
            stage = "WPF transport failure never falls back to SQL DML";
            await NativeUiOperation(author, "StartAnalysis", "Registered", sampleNumber: "WATER-NATIVE-2", expectedCommit: false, stopAfterSignature: true);
            WaterCheck(Convert.ToString(await Scalar("SELECT Status FROM dbo.Samples WHERE SampleID=2")) == "Registered" && Convert.ToInt32(await Scalar("SELECT COUNT(*) FROM dbo.ElectronicSignatures WHERE SampleID=2")) == 0, "unavailable service leaves the real WPF sample and signature unchanged");
            WaterCheck(_passes.Count == 0, "Water phase does not alter the original 122-check counter");
            File.WriteAllText(Path.Combine(settings.ResultsDirectory, "water-workflow-results.json"), JsonSerializer.Serialize(new { Commit = Environment.GetEnvironmentVariable("GITHUB_SHA"), Phase = "WaterWorkflow", BuildConfiguration = "Release", DesktopRuntime = AppConfig.EnvironmentName, WindowsCallerSid = settings.CallerSid, ServiceSid = settings.ServiceSid, Passed = _waterPasses.Count, ExpectedOperations = 6, Operations = new[] { "CorrectTimes", "StartAnalysis", "SaveResults", "SubmitReview", "Review", "Approve" }, Checks = _waterPasses, ProductionActivated = false, SiteUatQualified = false, CertificateLifecycleQualified = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"WATER NATIVE WINDOWS/WPF/SQL: {_waterPasses.Count} checks passed; six operations; isolated CI only.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("::error title=Water native phase::stage=" + stage + "; passed=" + _waterPasses.Count + "; exception=" + ex.GetType().Name);
            throw;
        }
        finally
        {
            StopHost(); TrustedApplicationSession.Clear();
            if (_oldSettings is not null) File.WriteAllBytes(_appSettingsPath, _oldSettings); else if (File.Exists(_appSettingsPath)) File.Delete(_appSettingsPath);
            if (oldHost is not null) { File.WriteAllBytes(_hostConfigPath, oldHost); if (oldHostAcl is not null) new FileInfo(_hostConfigPath).SetAccessControl(oldHostAcl); }
            else if (File.Exists(_hostConfigPath)) File.Delete(_hostConfigPath);
            await Exec(settings.MasterConnectionString, $"IF DB_ID(N'{_db}') IS NOT NULL BEGIN ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;DROP DATABASE [{_db}];END;IF SUSER_ID(N'{_serviceLogin}') IS NOT NULL DROP LOGIN [{_serviceLogin}];IF SUSER_ID(N'{_clientLogin}') IS NOT NULL DROP LOGIN [{_clientLogin}];");
        }
    }

    private const string WaterReason = "Isolated native Water workflow qualification";
    private const string WaterMeaning = "I performed this controlled Water UAT action";
    private async Task<TrustedApplicationRequest> NativeEnvelope(WindowsTrustedPipeClient pipe, WaterWorkflowRequest payload, string actor, Guid? id = null, string action = "WaterWorkflow", string reason = WaterReason, string meaning = WaterMeaning)
    {
        var proof = await pipe.ApplicationAsync(new(Guid.NewGuid(), "Reauthenticate", actor, _password));
        if (string.IsNullOrWhiteSpace(proof.Ticket)) throw new InvalidOperationException("Missing real native authentication ticket.");
        return new(id ?? Guid.NewGuid(), action, actor, Ticket: proof.Ticket, UserJson: WaterWorkflowPolicy.CanonicalPayload(payload), Reason: reason, Meaning: meaning);
    }
    private async Task<WaterWorkflowResult> NativeCall(WindowsTrustedPipeClient pipe, WaterWorkflowRequest payload, string actor, Guid? id = null, string action = "WaterWorkflow", string reason = WaterReason, string meaning = WaterMeaning)
    {
        var response = await pipe.ApplicationAsync(await NativeEnvelope(pipe, payload, actor, id, action, reason, meaning));
        return JsonSerializer.Deserialize<WaterWorkflowResult>(response.UserJson!) ?? throw new InvalidOperationException("Missing Water result.");
    }
    private async Task<WaterWorkflowRequest> NativeRequest(string operation)
    {
        await using var connection = new SqlConnection(AppConfig.ConnectionString); await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        WaterWorkflowSnapshot snapshot = WaterWorkflowSnapshot.Load(connection, transaction, 1); await transaction.CommitAsync();
        DataRow header = snapshot.Header.Rows[0];
        return new("localhost,14335", _db, operation, 1, "WATER-NATIVE-1", (string)header["Status"], snapshot.Hash,
            operation == "StartAnalysis" ? DateTime.SpecifyKind(snapshot.DatabaseNow.AddMinutes(-1), DateTimeKind.Unspecified) : null, null,
            operation is "StartAnalysis" or "CorrectTimes" ? Array.Empty<WaterWorkflowRow>() : WaterWorkflowValidator.DisplayRows(snapshot));
    }
    private async Task<(WaterWorkflowRequest Payload, string Reason, string Meaning)> NativeReceiptRequest(Guid id)
    {
        await using var connection = new SqlConnection(_admin); await connection.OpenAsync();
        using var command = new SqlCommand("SELECT Reason,Meaning FROM dbo.EQ001_WaterWorkflowReceipts WHERE RequestId=@id", connection); command.Parameters.AddWithValue("@id",id);
        using var reader = await command.ExecuteReaderAsync(); await reader.ReadAsync();
        // The submitted clinical input is retained by the WPF capture below.
        return (_nativeSubmittedPayload ?? throw new InvalidOperationException("Missing captured WPF payload"), reader.GetString(0), reader.GetString(1));
    }
    private WaterWorkflowRequest? _nativeSubmittedPayload;
    private async Task PrepareNativeWaterSiteContract()
    {
        // The supplied installed source has these additions beyond the migration ledger.
        // Reproduce them only in this fresh disposable native fixture; no product DDL bypass.
        if (!_db.StartsWith("EQ001_UAT_WaterNative_", StringComparison.Ordinal))
            throw new InvalidOperationException("Native Water fixture target guard failed.");
        await Exec(_admin, """
IF COL_LENGTH(N'dbo.Samples',N'IsLocked') IS NULL
 ALTER TABLE dbo.Samples ADD IsLocked bit NULL;
IF COL_LENGTH(N'dbo.Samples',N'ModifiedBy') IS NULL
 ALTER TABLE dbo.Samples ADD ModifiedBy nvarchar(100) NULL;
IF COL_LENGTH(N'dbo.WaterResultResourceEvidence',N'ExecutionDate') IS NULL
 ALTER TABLE dbo.WaterResultResourceEvidence ADD ExecutionDate date NULL;
IF COL_LENGTH(N'dbo.ElectronicSignatures',N'CreatedUtc') IS NULL
 ALTER TABLE dbo.ElectronicSignatures ADD CreatedUtc datetime2(0) NOT NULL DEFAULT SYSUTCDATETIME();
IF COL_LENGTH(N'dbo.ElectronicSignatures',N'SourceWorkstation') IS NULL
 ALTER TABLE dbo.ElectronicSignatures ADD SourceWorkstation nvarchar(128) NULL;
IF COL_LENGTH(N'dbo.ElectronicSignatures',N'SourceApplication') IS NULL
 ALTER TABLE dbo.ElectronicSignatures ADD SourceApplication nvarchar(128) NULL;
IF COL_LENGTH(N'dbo.ElectronicSignatures',N'UtcSignedAt') IS NULL
 ALTER TABLE dbo.ElectronicSignatures ADD UtcSignedAt datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME();
IF OBJECT_ID(N'dbo.TRG_AuditTrail_AppendOnly',N'TR') IS NULL
 EXEC(N'CREATE TRIGGER dbo.TRG_AuditTrail_AppendOnly ON dbo.AuditTrail AFTER UPDATE,DELETE AS BEGIN THROW 56999,''Append-only fixture protection'',1; END');
IF OBJECT_ID(N'dbo.TRG_ElectronicSignatures_AppendOnly',N'TR') IS NULL
 EXEC(N'CREATE TRIGGER dbo.TRG_ElectronicSignatures_AppendOnly ON dbo.ElectronicSignatures AFTER UPDATE,DELETE AS BEGIN THROW 56999,''Append-only fixture protection'',1; END');
""");
    }
    private async Task SeedNativeWater()
    {
        await Exec(_admin, """
DECLARE @test int=(SELECT TOP(1) TestID FROM dbo.Tests WHERE TestName LIKE N'Appearance%' ORDER BY TestID);
IF @test IS NULL BEGIN INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,IsActive) VALUES(N'NATIVE-APPEAR',N'Appearance (Color & Clarity)',N'Physical',N'Result',1);SET @test=SCOPE_IDENTITY();END;
-- LES uses hard-coded IDs 2/3. A full migration may allocate Appearance elsewhere; explicitly use ID1 for this synthetic fixture.
IF @test<>1 BEGIN UPDATE dbo.Tests SET TestName=N'Appearance (Color & Clarity)',TestCategory=N'Physical',Unit=N'Result' WHERE TestID=1;SET @test=1;END;
INSERT dbo.WaterSamplingPoints(PointCode,PointName,WaterType,Location,Status,CreatedAt) VALUES(N'NATIVE-PW',N'Native synthetic point',N'Purified',N'CI',N'Active',SYSDATETIME());
DECLARE @point int=SCOPE_IDENTITY();
INSERT dbo.Samples(SampleNumber,PointID,SampleType,SamplingDateTime,ReceivedDateTime,SampledBy,ReceivedBy,ReceiptDecision,Status)
VALUES(N'WATER-NATIVE-1',@point,N'Purified Water',DATEADD(hour,-3,SYSDATETIME()),DATEADD(hour,-2,SYSDATETIME()),N'uat-water-author',N'uat-water-author',N'Accepted',N'Registered'),
(N'WATER-NATIVE-2',@point,N'Purified Water',DATEADD(hour,-3,SYSDATETIME()),DATEADD(hour,-2,SYSDATETIME()),N'uat-water-author',N'uat-water-author',N'Accepted',N'Registered');
INSERT dbo.SampleTests(SampleID,TestID,TestNameSnapshot,TestCategorySnapshot,UnitSnapshot,LimitDescription) SELECT SampleID,@test,N'Appearance (Color & Clarity)',N'Physical',N'Result',N'Clear and colorless' FROM dbo.Samples WHERE SampleNumber LIKE N'WATER-NATIVE-%';
INSERT dbo.Water_Plans(PlanNo,WaterType,SourceType,DueDate,RequiredDate,AnalysisProfile,CreatedBy) VALUES(N'WATER-NATIVE-PLAN',N'Purified',N'UAT',CAST(SYSDATETIME() AS date),CAST(SYSDATETIME() AS date),N'Chemical',N'uat-water-author');
DECLARE @plan int=SCOPE_IDENTITY();
INSERT dbo.Water_PlanSamples(WaterPlanID,PointID,PointCode,SampleID,SampleNumber,Status) VALUES(@plan,@point,N'NATIVE-PW',1,N'WATER-NATIVE-1',N'Registered');
UPDATE dbo.Water_Plans SET Status=N'Analysis In Progress' WHERE WaterPlanID=@plan;
""");
    }

    private static readonly BindingFlags NativePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private async Task NativeUiOperation(string actor, string operation, string expectedStatus, string sampleNumber = "WATER-NATIVE-1", bool expectedCommit = true, bool stopAfterSignature = false)
    {
        var auth = new AuthService(new UserRepository(new DatabaseConnection()));
        if (await auth.AuthenticateAsync(actor, _password) is null) throw new InvalidOperationException("Native WPF login failed for " + actor);
        var services = new ServiceCollection().AddTransient<ElectronicSignature>(_ => new ElectronicSignature(auth)).BuildServiceProvider();
        var window = new ResultsEntry(auth, services); window.Show();
        await (Task)typeof(ResultsEntry).GetMethod("LoadSampleAsync", NativePrivate)!.Invoke(window, new object[] { sampleNumber })!;
        if (operation == "SaveResults")
        {
            var items = (System.Collections.IEnumerable)typeof(ResultsEntry).GetField("resultItems", NativePrivate)!.GetValue(window)!;
            foreach (object item in items) item.GetType().GetProperty("ResultValue")!.SetValue(item, "Clear/Colorless");
        }
        if (operation == "SubmitReview")
            _nativeSubmittedPayload = (WaterWorkflowRequest)typeof(ResultsEntry).GetMethod("CaptureTrustedWaterRequest", NativePrivate)!.Invoke(window, new object?[] { operation, null, null })!;
        long before = Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM dbo.ElectronicSignatures"));
        bool submitted = false, confirmed = false, completed = false;
        string? failure = null; ElectronicSignature? signature = null;
        var handled = new HashSet<Window>(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(w => w != window).ToArray())
                {
                    if (dialog is ElectronicSignature electronic)
                    {
                        signature = electronic;
                        if (!submitted)
                        {
                            submitted = true;
                            if (stopAfterSignature) electronic.Closed += (_, _) => { if (electronic.IsConfirmed) StopHost(); };
                            ((PasswordBox)electronic.FindName("txtPassword")).Password = _password;
                            ((ComboBox)electronic.FindName("cboMeaning")).SelectedIndex = 0;
                            ((ComboBox)electronic.FindName("cboReason")).SelectedIndex = operation == "CorrectTimes" ? 6 : 1;
                            ((Button)electronic.FindName("btnConfirm")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                        confirmed |= electronic.IsConfirmed;
                        var error = (TextBlock)electronic.FindName("lblError");
                        if (error.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(error.Text)) { failure = "Electronic signature rejected"; electronic.Close(); }
                    }
                    else if (dialog.Title == "Record Analysis Start" && handled.Add(dialog))
                        Descendants<Button>(dialog).Single(b => Convert.ToString(b.Content) == "Continue").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (dialog.Title == "Correct Water Sample Times" && handled.Add(dialog))
                    {
                        var fields = ((StackPanel)dialog.Content).Children.OfType<Grid>().Single();
                        var pickers = fields.Children.OfType<DatePicker>().ToArray(); var times = fields.Children.OfType<TextBox>().ToArray();
                        if (pickers.Length != 4 || times.Length != 4) throw new InvalidOperationException("Correction dialog no longer exposes exactly four editable times");
                        DateTime correction = pickers[0].SelectedDate!.Value.Date.Add(TimeSpan.Parse(times[0].Text, CultureInfo.InvariantCulture)).AddMinutes(-1);
                        pickers[0].SelectedDate = correction.Date; times[0].Text = correction.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                        Descendants<Button>(dialog).Single(b => Convert.ToString(b.Content) == "Continue to Signature").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                NativeActivityDialogs.CloseUnexpected(title =>
                {
                    if (title == "Water Workflow") completed = true;
                    else if (!expectedCommit && title == "Water Workflow Needs Attention") completed = true;
                    else failure = "Unexpected native dialog: " + title;
                });
                if (clock.Elapsed > TimeSpan.FromSeconds(55)) { failure = "WPF operation timed out"; signature?.Close(); timer.Stop(); }
            }
            catch (Exception ex) { failure = "WPF automation: " + ex.GetType().Name; signature?.Close(); timer.Stop(); }
        };
        timer.Start();
        try
        {
            string handler = "Btn" + operation + "_Click";
            typeof(ResultsEntry).GetMethod(handler, NativePrivate)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
            while (!completed && failure is null && clock.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(25);
            confirmed |= signature?.IsConfirmed == true;
            if (failure is not null || !completed || !submitted || expectedCommit && !confirmed)
                throw new InvalidOperationException("Actual ResultsEntry " + operation + " failed: " + (failure ?? "missing signature/outcome"));
            if (expectedCommit)
            {
                WaterCheck(Convert.ToString(await Scalar($"SELECT Status FROM dbo.Samples WHERE SampleNumber=N'{sampleNumber}'")) == expectedStatus && Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM dbo.ElectronicSignatures")) == before + 1,
                    "actual ResultsEntry " + operation + " handler and fresh signature commit " + expectedStatus + " as " + actor);
            }
        }
        finally { timer.Stop(); window.Close(); services.Dispose(); }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
