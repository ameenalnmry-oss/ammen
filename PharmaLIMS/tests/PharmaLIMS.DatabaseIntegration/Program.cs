using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Data;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private static async Task<int> Main()
    {
        string? projectRoot = FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("PharmaLIMS project root was not found.");
            return 2;
        }

        string manifestPath = Path.Combine(projectRoot, "Database", "MigrationManifest.json");
        string masterConnectionString = Environment.GetEnvironmentVariable("PHARMALIMS_TEST_MASTER_CONNECTION_STRING")
            ?? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Encrypt=false;TrustServerCertificate=true;Connection Timeout=15;";
        string databaseName = "PharmaLIMS_Integration_" + Guid.NewGuid().ToString("N")[..12];
        string databaseConnectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created temporary SQL integration database {databaseName}.");

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            JsonElement root = document.RootElement;
            JsonElement baseline = root.GetProperty("freshInstallBaseline");
            string applicationVersion = root.GetProperty("applicationVersion").GetString() ?? string.Empty;

            await ApplyControlledFileAsync(projectRoot, databaseConnectionString, baseline.GetProperty("file").GetString()!, baseline.GetProperty("sha256").GetString()!, baseline.GetProperty("versionKey").GetString()!);
            await RecordMigrationLedgerAsync(
                databaseConnectionString,
                baseline.GetProperty("versionKey").GetString()!,
                baseline.GetProperty("description").GetString() ?? baseline.GetProperty("versionKey").GetString()!,
                baseline.GetProperty("sha256").GetString()!,
                applicationVersion);

            foreach (JsonElement migration in root.GetProperty("migrations").EnumerateArray())
            {
                string versionKey = migration.GetProperty("versionKey").GetString() ?? string.Empty;
                if (migration.TryGetProperty("supersededBy", out JsonElement supersededBy) &&
                    !string.IsNullOrWhiteSpace(supersededBy.GetString()))
                {
                    Console.WriteLine($"SKIP retired migration {versionKey}; superseded by {supersededBy.GetString()}.");
                    continue;
                }

                await ApplyControlledFileAsync(
                    projectRoot,
                    databaseConnectionString,
                    migration.GetProperty("file").GetString()!,
                    migration.GetProperty("sha256").GetString()!,
                    versionKey);

                await RecordMigrationLedgerAsync(
                    databaseConnectionString,
                    versionKey,
                    migration.GetProperty("description").GetString() ?? versionKey,
                    migration.GetProperty("sha256").GetString()!,
                    applicationVersion);
            }

            await VerifySchemaAsync(databaseConnectionString);
            await VerifyUserAdministrationSignatureEvidenceSchemaAsync(databaseConnectionString);
            await VerifyComplianceProtectionTamperDetectionAsync(databaseConnectionString);
            VerifyPrmNumericInterpretationEngine();
            await VerifyPrmResultOptimisticConcurrencyAsync(databaseConnectionString);
            await VerifyPrmSampleWideStateConcurrencyAsync(databaseConnectionString);
            await VerifyV224WaterControlledMasterSchemaBackfillAsync(projectRoot, masterConnectionString, root);
            await VerifyV225LegacyTestsActiveContractAsync(projectRoot, masterConnectionString, root);
            await VerifyV216EmAreaSnapshotCompileSafeRepairAsync(projectRoot, masterConnectionString, root);
            await VerifyV222TimingDispositionWidthRepairAsync(projectRoot, masterConnectionString, root);
            await VerifyV223PrmReportStatusContractRepairAsync(projectRoot, masterConnectionString, root);
            await VerifyV221TimingSchemaContractHardeningAsync(projectRoot, masterConnectionString, root);
            await VerifyLifecycleLeaseRoundTripAsync(databaseConnectionString);
            await VerifyLegacyQualityEventUpgradeAsync(projectRoot, masterConnectionString, root);
            await VerifyPrmQualityEventNumberReconciliationAsync(databaseConnectionString);
            await VerifyPrmInvestigationEvidenceBindingAsync(databaseConnectionString);
            await VerifyPrmLegacyEvidenceReconciliationAsync(databaseConnectionString);
            await VerifyV212TimingGovernanceUpgradeAsync(projectRoot, masterConnectionString, root);
            await VerifyV213FinalReleaseHardeningAsync(databaseConnectionString);
            await VerifyEmLimitSnapshotReconciliationAsync(databaseConnectionString);
            await ReviewRemediationIntegration.VerifyAsync(databaseConnectionString, projectRoot, masterConnectionString);
            Console.WriteLine("Database migration/schema integration PASS.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Database migration/schema integration FAILED.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("Temporary database cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task RecordMigrationLedgerAsync(
        string connectionString,
        string versionKey,
        string description,
        string checksum,
        string applicationVersion)
    {
        const string sql = @"
MERGE dbo.LIMS_SchemaVersions WITH (HOLDLOCK) AS target
USING (SELECT @VersionKey AS VersionKey) AS source
ON target.VersionKey=source.VersionKey
WHEN MATCHED THEN UPDATE SET
    Description=@Description,
    MigrationChecksum=@Checksum,
    ApplicationVersion=@ApplicationVersion
WHEN NOT MATCHED THEN INSERT
    (VersionKey,Description,MigrationChecksum,ApplicationVersion)
    VALUES(@VersionKey,@Description,@Checksum,@ApplicationVersion);";

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = 60 };
        command.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
        command.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value = description;
        command.Parameters.Add("@Checksum", SqlDbType.NVarChar, 128).Value = checksum;
        command.Parameters.Add("@ApplicationVersion", SqlDbType.NVarChar, 50).Value = applicationVersion;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ApplyControlledFileAsync(
        string projectRoot,
        string connectionString,
        string relativePath,
        string expectedSha256,
        string versionKey)
    {
        string fullPath = Path.GetFullPath(Path.Combine(projectRoot, "Database", relativePath));
        string databaseRoot = Path.GetFullPath(Path.Combine(projectRoot, "Database")) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(databaseRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new InvalidOperationException($"Controlled SQL file is invalid or missing: {relativePath}");

        byte[] bytes = await File.ReadAllBytesAsync(fullPath);
        string actualSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Checksum mismatch for {versionKey}: {relativePath}");

        Console.WriteLine("APPLY " + versionKey);

        if (versionKey.StartsWith("20260811_001", StringComparison.Ordinal))
        {
            const string areaClassificationCompatibilitySql = @"
IF OBJECT_ID(N'dbo.ExternalTrendImportRows', N'U') IS NULL
    THROW 51090, 'Apply migration 20260810_001 before 20260811_001 compatibility preparation.', 1;

IF COL_LENGTH(N'dbo.ExternalTrendImportRows', N'AreaClassification') IS NULL
BEGIN
    ALTER TABLE dbo.ExternalTrendImportRows
        ADD AreaClassification NVARCHAR(30) NOT NULL
            CONSTRAINT DF_ExternalTrendImportRows_AreaClassification
            DEFAULT (N'Unspecified');
END;";

            await ExecuteAsync(connectionString, areaClassificationCompatibilitySql, timeoutSeconds: 120);
        }

        if (versionKey.StartsWith("20260823_002", StringComparison.Ordinal))
        {
            const string cultureMediaApprovalCompatibilitySql = @"
IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
    THROW 53100, 'Required table dbo.CultureMediaQualificationRequirements is missing before 20260823_002 compatibility preparation.', 1;

IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'ApprovalStatus') IS NULL
    ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ApprovalStatus NVARCHAR(30) NOT NULL
        CONSTRAINT DF_CultureMediaQualificationRequirements_ApprovalStatus DEFAULT (N'Draft');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'ReviewedBy') IS NULL
    ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ReviewedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements', N'ReviewedAt') IS NULL
    ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ReviewedAt DATETIME2(0) NULL;
";

            await ExecuteAsync(connectionString, cultureMediaApprovalCompatibilitySql, timeoutSeconds: 120);
        }

        if (versionKey.StartsWith("20260824_001", StringComparison.Ordinal))
        {
            const string prmItemStageCompatibilitySql = @"
IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
    THROW 53201, 'Required table dbo.PRM_SpecificationTests is missing before 20260824_001 compatibility preparation.', 1;
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
    THROW 53202, 'Required table dbo.PRM_Samples is missing before 20260824_001 compatibility preparation.', 1;
IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
    THROW 53203, 'Required table dbo.PRM_SampleTests is missing before 20260824_001 compatibility preparation.', 1;

IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'ItemCode') IS NULL
    ALTER TABLE dbo.PRM_SpecificationTests ADD ItemCode NVARCHAR(80) NULL;
IF COL_LENGTH(N'dbo.PRM_SpecificationTests', N'ProductionStage') IS NULL
    ALTER TABLE dbo.PRM_SpecificationTests ADD ProductionStage NVARCHAR(80) NULL;

IF COL_LENGTH(N'dbo.PRM_Samples', N'SpecificationVersionNo') IS NULL
    ALTER TABLE dbo.PRM_Samples ADD SpecificationVersionNo INT NULL;
IF COL_LENGTH(N'dbo.PRM_Samples', N'StabilityChamberNo') IS NULL
    ALTER TABLE dbo.PRM_Samples ADD StabilityChamberNo NVARCHAR(120) NULL;
IF COL_LENGTH(N'dbo.PRM_Samples', N'StabilityProtocolNo') IS NULL
    ALTER TABLE dbo.PRM_Samples ADD StabilityProtocolNo NVARCHAR(120) NULL;

IF COL_LENGTH(N'dbo.PRM_SampleTests', N'SourceSpecificationTestID') IS NULL
    ALTER TABLE dbo.PRM_SampleTests ADD SourceSpecificationTestID INT NULL;
IF COL_LENGTH(N'dbo.PRM_SampleTests', N'SpecificationVersionNo') IS NULL
    ALTER TABLE dbo.PRM_SampleTests ADD SpecificationVersionNo INT NULL;
IF COL_LENGTH(N'dbo.PRM_SampleTests', N'SpecificationItemCode') IS NULL
    ALTER TABLE dbo.PRM_SampleTests ADD SpecificationItemCode NVARCHAR(80) NULL;
IF COL_LENGTH(N'dbo.PRM_SampleTests', N'SpecificationProductionStage') IS NULL
    ALTER TABLE dbo.PRM_SampleTests ADD SpecificationProductionStage NVARCHAR(80) NULL;
";

            await ExecuteAsync(connectionString, prmItemStageCompatibilitySql, timeoutSeconds: 120);
        }

        if (versionKey.StartsWith("20260827_001", StringComparison.Ordinal))
        {
            const string prmEvidenceBindingCompatibilitySql = @"
IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
    THROW 53710, 'Required table dbo.QualityEventAffectedResults is missing before 20260827_001 compatibility preparation.', 1;

IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationNumericLimit') IS NULL
    ALTER TABLE dbo.QualityEventAffectedResults
        ADD SpecificationNumericLimit DECIMAL(18,3) NULL;

IF COL_LENGTH(N'dbo.QualityEventAffectedResults', N'EvidenceSchemaVersion') IS NULL
    ALTER TABLE dbo.QualityEventAffectedResults
        ADD EvidenceSchemaVersion TINYINT NOT NULL
            CONSTRAINT DF_QualityEventAffectedResults_EvidenceSchemaVersion_20260827_001
            DEFAULT (0) WITH VALUES;
";

            await ExecuteAsync(connectionString, prmEvidenceBindingCompatibilitySql, timeoutSeconds: 120);
        }

        string sql = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        await ExecuteAsync(connectionString, sql, timeoutSeconds: 300);
    }

    private static void VerifyPrmNumericInterpretationEngine()
    {
        AssertPrmInterpretation(90m, "95-105", null, "Does Not Conform");
        AssertPrmInterpretation(100m, "95-105", null, "Conforms");
        AssertPrmInterpretation(100m, "LESS THAN 100", null, "Does Not Conform");
        AssertPrmInterpretation(99m, "LESS THAN 100", null, "Conforms");
        AssertPrmInterpretation(100m, "NMT 100", null, "Conforms");
        AssertPrmInterpretation(101m, "NMT 100", null, "Does Not Conform");
        AssertPrmInterpretation(100m, "NMT 100 CFU/g; incubation 30-35 C", null, "Conforms");
        AssertPrmInterpretation(100m, "NMT 1,000", null, "Check Required");
        AssertPrmInterpretation(106m, "NLT 95 AND NMT 105", null, "Does Not Conform");
        AssertPrmInterpretation(100m, "NLT 95 AND NMT 105", null, "Conforms");
        AssertPrmInterpretation(106m, ">=95 AND <=105", null, "Does Not Conform");
        AssertPrmInterpretation(32m, "NMT 10 CFU/g; incubation RANGE 30-35 C", null, "Does Not Conform");
        AssertPrmInterpretation(50m, "NMT 100 CFU/g; incubation RANGE 30-35 C", null, "Conforms");
        AssertPrmInterpretation(0.5m, "NMT 1e-3 CFU/g", null, "Does Not Conform");
        AssertPrmInterpretation(999m, "NMT 10^3 CFU/g (1000 CFU/g)", "1000", "Conforms");
        AssertPrmInterpretation(101m, "NMT 10^3 CFU/g; TYMC NMT 10^2 CFU/g", "100", "Does Not Conform", "TYMC", "Total Yeast and Mold Count");

        if (PrmNumericSpecificationEvaluator.TryParseControlledDecimal("1,000", out _))
            throw new InvalidOperationException("Ambiguous comma-formatted PRM numeric result must be rejected.");
        if (!PrmNumericSpecificationEvaluator.TryParseControlledDecimal("1000", out decimal parsed) || parsed != 1000m)
            throw new InvalidOperationException("Invariant PRM numeric result parsing failed for 1000.");

        Console.WriteLine("PRM numeric interpretation engine integration PASS.");
    }

    private static void AssertPrmInterpretation(
        decimal value,
        string specification,
        string? structuredLimit,
        string expected,
        string testCode = "TAMC",
        string testName = "Total Aerobic Microbial Count")
    {
        string actual = PrmNumericSpecificationEvaluator.Evaluate(value, specification, structuredLimit, testCode, testName);
        if (!actual.Equals(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"PRM interpretation mismatch for value={value}, specification='{specification}'. Expected {expected}, got {actual}.");
        }
    }

    private static async Task VerifyPrmResultOptimisticConcurrencyAsync(string connectionString)
    {
        const string sampleNumber = "RM-INTEGRATION-CONCURRENCY-001";
        int sampleId;
        int sampleTestId;

        await using (SqlConnection seedConnection = new(connectionString))
        {
            await seedConnection.OpenAsync();
            await using SqlCommand seed = new(@"
INSERT dbo.PRM_Samples(SampleNumber, SampleCategory, SampleStatus, CreatedBy)
VALUES(@SampleNumber, N'Raw Material', N'Results Entered', N'integration');
DECLARE @SampleID INT = CONVERT(INT, SCOPE_IDENTITY());
INSERT dbo.PRM_SampleTests
(
    SampleID, TestCode, TestName, SpecificationText, Unit, ResultValue, ResultType,
    SpecificationLimit, Interpretation, Remarks, RequiredTest, EnteredBy, EnteredDate
)
VALUES
(
    @SampleID, N'TAMC', N'Total Aerobic Microbial Count', N'NMT 100', N'CFU/g', N'5', N'Numeric',
    100, N'Conforms', N'initial', 1, N'analyst-1', SYSDATETIME()
);
SELECT @SampleID, CONVERT(INT, SCOPE_IDENTITY());", seedConnection);
            seed.CommandTimeout = 60;
            seed.Parameters.Add("@SampleNumber", SqlDbType.NVarChar, 50).Value = sampleNumber;
            await using SqlDataReader reader = await seed.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("PRM concurrency integration seed did not return a row.");
            sampleId = reader.GetInt32(0);
            sampleTestId = reader.GetInt32(1);
        }

        await using SqlConnection firstConnection = new(connectionString);
        await using SqlConnection staleConnection = new(connectionString);
        await firstConnection.OpenAsync();
        await staleConnection.OpenAsync();

        string staleExpectedResult;
        string staleExpectedInterpretation;
        string staleExpectedRemarks;
        string staleExpectedEnteredBy;
        DateTime staleExpectedEnteredDate;
        await using (SqlCommand staleLoad = new(@"
SELECT ResultValue, Interpretation, Remarks, EnteredBy, EnteredDate
FROM dbo.PRM_SampleTests
WHERE SampleID=@SampleID AND SampleTestID=@SampleTestID;", staleConnection))
        {
            staleLoad.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            staleLoad.Parameters.Add("@SampleTestID", SqlDbType.Int).Value = sampleTestId;
            await using SqlDataReader staleReader = await staleLoad.ExecuteReaderAsync();
            if (!await staleReader.ReadAsync())
                throw new InvalidOperationException("The second PRM test session could not load its initial result snapshot.");
            staleExpectedResult = staleReader.GetString(0);
            staleExpectedInterpretation = staleReader.GetString(1);
            staleExpectedRemarks = staleReader.GetString(2);
            staleExpectedEnteredBy = staleReader.GetString(3);
            staleExpectedEnteredDate = staleReader.GetDateTime(4);
        }

        int firstRows = await ExecuteGuardedPrmResultUpdateAsync(
            firstConnection,
            sampleId,
            sampleTestId,
            expectedResult: staleExpectedResult,
            expectedInterpretation: staleExpectedInterpretation,
            expectedRemarks: staleExpectedRemarks,
            expectedEnteredBy: staleExpectedEnteredBy,
            expectedEnteredDate: staleExpectedEnteredDate,
            newResult: "6",
            newInterpretation: "Conforms",
            newRemarks: "initial",
            enteredBy: "analyst-2",
            resultChanged: true,
            interpretationChanged: false,
            remarksChanged: false);
        if (firstRows != 1)
            throw new InvalidOperationException("First PRM guarded result update did not persist exactly one row.");

        int staleRows = await ExecuteGuardedPrmResultUpdateAsync(
            staleConnection,
            sampleId,
            sampleTestId,
            expectedResult: staleExpectedResult,
            expectedInterpretation: staleExpectedInterpretation,
            expectedRemarks: staleExpectedRemarks,
            expectedEnteredBy: staleExpectedEnteredBy,
            expectedEnteredDate: staleExpectedEnteredDate,
            newResult: "7",
            newInterpretation: "Conforms",
            newRemarks: "stale-window",
            enteredBy: "analyst-3",
            resultChanged: true,
            interpretationChanged: false,
            remarksChanged: true);
        if (staleRows != 0)
            throw new InvalidOperationException("A stale PRM result window overwrote a newer database value.");

        DataTable afterFirst = await QueryAsync(connectionString, $@"
SELECT ResultValue, EnteredBy, EnteredDate
FROM dbo.PRM_SampleTests
WHERE SampleID={sampleId} AND SampleTestID={sampleTestId};");
        if (afterFirst.Rows.Count != 1 || Convert.ToString(afterFirst.Rows[0]["ResultValue"]) != "6")
            throw new InvalidOperationException("PRM guarded result update did not preserve the current database value after stale-save rejection.");

        string currentEnteredBy = Convert.ToString(afterFirst.Rows[0]["EnteredBy"]) ?? string.Empty;
        DateTime currentEnteredDate = Convert.ToDateTime(afterFirst.Rows[0]["EnteredDate"]);

        int remarksRows = await ExecuteGuardedPrmResultUpdateAsync(
            firstConnection,
            sampleId,
            sampleTestId,
            expectedResult: "6",
            expectedInterpretation: "Conforms",
            expectedRemarks: "initial",
            expectedEnteredBy: currentEnteredBy,
            expectedEnteredDate: currentEnteredDate,
            newResult: "6",
            newInterpretation: "Conforms",
            newRemarks: "remark-only-change",
            enteredBy: "reviewer-1",
            resultChanged: false,
            interpretationChanged: false,
            remarksChanged: true);
        if (remarksRows != 1)
            throw new InvalidOperationException("PRM remarks-only guarded update did not persist exactly one row.");

        DataTable afterRemarks = await QueryAsync(connectionString, $@"
SELECT ResultValue, Remarks, EnteredBy, EnteredDate
FROM dbo.PRM_SampleTests
WHERE SampleID={sampleId} AND SampleTestID={sampleTestId};");
        DataRow finalRow = afterRemarks.Rows[0];
        if (Convert.ToString(finalRow["ResultValue"]) != "6" ||
            Convert.ToString(finalRow["Remarks"]) != "remark-only-change" ||
            !string.Equals(Convert.ToString(finalRow["EnteredBy"]), currentEnteredBy, StringComparison.Ordinal) ||
            Convert.ToDateTime(finalRow["EnteredDate"]) != currentEnteredDate)
        {
            throw new InvalidOperationException("Remarks-only PRM update changed result attribution or entry time.");
        }

        Console.WriteLine("PRM optimistic-concurrency and remarks-attribution integration PASS.");
    }

    private static async Task VerifyPrmSampleWideStateConcurrencyAsync(string connectionString)
    {
        const string sampleNumber = "RM-INTEGRATION-SAMPLE-WIDE-001";
        int sampleId;
        int tamcId;
        int tymcId;

        await using (SqlConnection seedConnection = new(connectionString))
        {
            await seedConnection.OpenAsync();
            await using SqlCommand seed = new(@"
INSERT dbo.PRM_Samples(SampleNumber, SampleCategory, SampleStatus, ResultInterpretation, CreatedBy)
VALUES(@SampleNumber, N'Raw Material', N'Results Entered', N'Conforms', N'integration');
DECLARE @SampleID INT = CONVERT(INT, SCOPE_IDENTITY());

INSERT dbo.PRM_SampleTests
(
    SampleID, TestCode, TestName, SpecificationText, Unit, ResultValue, ResultType,
    SpecificationLimit, Interpretation, Remarks, RequiredTest, SortOrder, EnteredBy, EnteredDate
)
VALUES
(@SampleID, N'TAMC', N'Total Aerobic Microbial Count', N'NMT 100 CFU/g', N'CFU/g', N'5', N'Numeric', 100, N'Conforms', N'tamc-initial', 1, 10, N'analyst-a', SYSDATETIME()),
(@SampleID, N'TYMC', N'Total Yeast and Mold Count', N'NMT 10 CFU/g', N'CFU/g', N'1', N'Numeric', 10, N'Conforms', N'tymc-initial', 1, 20, N'analyst-a', SYSDATETIME());

SELECT @SampleID,
       MIN(CASE WHEN TestCode=N'TAMC' THEN SampleTestID END),
       MIN(CASE WHEN TestCode=N'TYMC' THEN SampleTestID END)
FROM dbo.PRM_SampleTests
WHERE SampleID=@SampleID;", seedConnection);
            seed.CommandTimeout = 60;
            seed.Parameters.Add("@SampleNumber", SqlDbType.NVarChar, 60).Value = sampleNumber;
            await using SqlDataReader reader = await seed.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("Sample-wide PRM integration seed did not return identifiers.");
            sampleId = reader.GetInt32(0);
            tamcId = reader.GetInt32(1);
            tymcId = reader.GetInt32(2);
        }

        await using SqlConnection writerConnection = new(connectionString);
        await using SqlConnection staleConnection = new(connectionString);
        await writerConnection.OpenAsync();
        await staleConnection.OpenAsync();

        DataTable writerSnapshot = await LoadPrmResultSnapshotAsync(writerConnection, sampleId);
        DataTable staleSnapshot = await LoadPrmResultSnapshotAsync(staleConnection, sampleId);
        DataRow writerTamc = writerSnapshot.Select("SampleTestID=" + tamcId)[0];

        await using (SqlTransaction writerTransaction = writerConnection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                writerConnection, writerTransaction, sampleId, writerSnapshot);

            int updated = await ExecuteGuardedPrmResultUpdateAsync(
                writerConnection,
                sampleId,
                tamcId,
                expectedResult: Convert.ToString(writerTamc["ResultValue"]),
                expectedInterpretation: Convert.ToString(writerTamc["Interpretation"]),
                expectedRemarks: Convert.ToString(writerTamc["Remarks"]),
                expectedEnteredBy: Convert.ToString(writerTamc["EnteredBy"]),
                expectedEnteredDate: Convert.ToDateTime(writerTamc["EnteredDate"]),
                newResult: "150",
                newInterpretation: "Does Not Conform",
                newRemarks: Convert.ToString(writerTamc["Remarks"]),
                enteredBy: "analyst-b",
                resultChanged: true,
                interpretationChanged: true,
                remarksChanged: false,
                transaction: writerTransaction);
            if (updated != 1)
                throw new InvalidOperationException("Writer PRM result update did not persist exactly one TAMC row.");

            PrmAuthoritativeSampleResultState state =
                PrmSampleResultStateService.ReadAuthoritativeStateForUpdate(writerConnection, writerTransaction, sampleId);
            if (!state.AllRequiredResultsEntered ||
                !state.PersistedInterpretationsMatchEvidence ||
                !state.OverallInterpretation.Equals("Does Not Conform", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Authoritative sample-wide PRM state was not derived from the committed TAMC/TYMC evidence.");
            }

            await using SqlCommand summary = new(@"
UPDATE dbo.PRM_Samples
SET ResultInterpretation=@Interpretation, SampleStatus=N'Results Entered'
WHERE SampleID=@SampleID;", writerConnection, writerTransaction);
            summary.Parameters.Add("@Interpretation", SqlDbType.NVarChar, 60).Value = state.OverallInterpretation;
            summary.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            if (await summary.ExecuteNonQueryAsync() != 1)
                throw new InvalidOperationException("Authoritative PRM sample summary update failed in integration rehearsal.");

            writerTransaction.Commit();
        }

        bool staleRejected = false;
        await using (SqlTransaction staleTransaction = staleConnection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            try
            {
                PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                    staleConnection, staleTransaction, sampleId, staleSnapshot);
            }
            catch (DBConcurrencyException)
            {
                staleRejected = true;
            }
            finally
            {
                staleTransaction.Rollback();
            }
        }
        if (!staleRejected)
            throw new InvalidOperationException("A stale second PRM window was not rejected after another result row changed.");

        DataTable afterStale = await QueryAsync(connectionString, $@"
SELECT s.ResultInterpretation,t.ResultValue
FROM dbo.PRM_Samples s
INNER JOIN dbo.PRM_SampleTests t ON t.SampleID=s.SampleID AND t.SampleTestID={tamcId}
WHERE s.SampleID={sampleId};");
        if (afterStale.Rows.Count != 1 ||
            !string.Equals(Convert.ToString(afterStale.Rows[0]["ResultInterpretation"]), "Does Not Conform", StringComparison.Ordinal) ||
            !string.Equals(Convert.ToString(afterStale.Rows[0]["ResultValue"]), "150", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stale PRM window rehearsal corrupted the authoritative sample summary or TAMC result.");
        }

        DataTable freshSnapshot = await LoadPrmResultSnapshotAsync(writerConnection, sampleId);
        DataRow freshTymc = freshSnapshot.Select("SampleTestID=" + tymcId)[0];
        string tymcEnteredBy = Convert.ToString(freshTymc["EnteredBy"]) ?? string.Empty;
        DateTime tymcEnteredDate = Convert.ToDateTime(freshTymc["EnteredDate"]);

        await using (SqlTransaction remarksTransaction = writerConnection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                writerConnection, remarksTransaction, sampleId, freshSnapshot);

            int remarksRows = await ExecuteGuardedPrmResultUpdateAsync(
                writerConnection,
                sampleId,
                tymcId,
                expectedResult: Convert.ToString(freshTymc["ResultValue"]),
                expectedInterpretation: Convert.ToString(freshTymc["Interpretation"]),
                expectedRemarks: Convert.ToString(freshTymc["Remarks"]),
                expectedEnteredBy: tymcEnteredBy,
                expectedEnteredDate: tymcEnteredDate,
                newResult: Convert.ToString(freshTymc["ResultValue"]),
                newInterpretation: Convert.ToString(freshTymc["Interpretation"]) ?? string.Empty,
                newRemarks: "fresh-remarks-only",
                enteredBy: "reviewer-b",
                resultChanged: false,
                interpretationChanged: false,
                remarksChanged: true,
                transaction: remarksTransaction);
            if (remarksRows != 1)
                throw new InvalidOperationException("Fresh remarks-only PRM update did not persist exactly one row.");

            PrmAuthoritativeSampleResultState state =
                PrmSampleResultStateService.ReadAuthoritativeStateForUpdate(writerConnection, remarksTransaction, sampleId);
            if (!state.OverallInterpretation.Equals("Does Not Conform", StringComparison.Ordinal))
                throw new InvalidOperationException("Remarks-only save changed the authoritative sample-wide PRM interpretation.");

            await using SqlCommand summary = new(@"
UPDATE dbo.PRM_Samples SET ResultInterpretation=@Interpretation WHERE SampleID=@SampleID;",
                writerConnection, remarksTransaction);
            summary.Parameters.Add("@Interpretation", SqlDbType.NVarChar, 60).Value = state.OverallInterpretation;
            summary.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            await summary.ExecuteNonQueryAsync();
            remarksTransaction.Commit();
        }

        DataTable final = await QueryAsync(connectionString, $@"
SELECT s.ResultInterpretation,t.Remarks,t.EnteredBy,t.EnteredDate
FROM dbo.PRM_Samples s
INNER JOIN dbo.PRM_SampleTests t ON t.SampleID=s.SampleID AND t.SampleTestID={tymcId}
WHERE s.SampleID={sampleId};");
        if (final.Rows.Count != 1 ||
            !string.Equals(Convert.ToString(final.Rows[0]["ResultInterpretation"]), "Does Not Conform", StringComparison.Ordinal) ||
            !string.Equals(Convert.ToString(final.Rows[0]["Remarks"]), "fresh-remarks-only", StringComparison.Ordinal) ||
            !string.Equals(Convert.ToString(final.Rows[0]["EnteredBy"]), tymcEnteredBy, StringComparison.Ordinal) ||
            Convert.ToDateTime(final.Rows[0]["EnteredDate"]) != tymcEnteredDate)
        {
            throw new InvalidOperationException("PRM sample-wide remarks-only integration rehearsal failed traceability or summary consistency.");
        }

        const string partialSampleNumber = "RM-INTEGRATION-PARTIAL-001";
        int partialSampleId;
        await using (SqlCommand partialSeed = new(@"
INSERT dbo.PRM_Samples(SampleNumber, SampleCategory, SampleStatus, ResultInterpretation, CreatedBy)
VALUES(@SampleNumber, N'Raw Material', N'In Progress', N'In Progress', N'integration');
DECLARE @SampleID INT=CONVERT(INT,SCOPE_IDENTITY());
INSERT dbo.PRM_SampleTests
(SampleID,TestCode,TestName,SpecificationText,Unit,ResultValue,ResultType,SpecificationLimit,Interpretation,RequiredTest,SortOrder)
VALUES
(@SampleID,N'TAMC',N'Total Aerobic Microbial Count',N'NMT 100 CFU/g',N'CFU/g',N'5',N'Numeric',100,N'Conforms',1,10),
(@SampleID,N'TYMC',N'Total Yeast and Mold Count',N'NMT 10 CFU/g',N'CFU/g',NULL,N'Numeric',10,N'Not Tested',1,20);
SELECT @SampleID;", writerConnection))
        {
            partialSeed.Parameters.Add("@SampleNumber", SqlDbType.NVarChar, 60).Value = partialSampleNumber;
            partialSampleId = Convert.ToInt32(await partialSeed.ExecuteScalarAsync());
        }

        await using (SqlTransaction partialTransaction = writerConnection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            PrmAuthoritativeSampleResultState partial =
                PrmSampleResultStateService.ReadAuthoritativeStateForUpdate(writerConnection, partialTransaction, partialSampleId);
            if (partial.AllRequiredResultsEntered || !partial.OverallInterpretation.Equals("In Progress", StringComparison.Ordinal))
                throw new InvalidOperationException("Partial PRM required-result state did not remain In Progress.");
            partialTransaction.Rollback();
        }

        Console.WriteLine("PRM sample-wide stale-window, aggregate, and remarks-only integration PASS.");
    }

    private static async Task<DataTable> LoadPrmResultSnapshotAsync(SqlConnection connection, int sampleId)
    {
        await using SqlCommand command = new(@"
SELECT
    SampleTestID, SampleID, TestName, SpecificationText, Unit, ResultValue, ResultType, TestCode,
    SpecificationLimit, ISNULL(RequiredTest,1) AS RequiredTest, MinimumElapsedHours, Interpretation,
    Remarks, EnteredBy, EnteredDate, ISNULL(SortOrder,SampleTestID) AS SortOrder
FROM dbo.PRM_SampleTests
WHERE SampleID=@SampleID
ORDER BY ISNULL(SortOrder,SampleTestID),SampleTestID;", connection) { CommandTimeout = 60 };
        command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        DataTable table = new();
        table.Load(reader);
        return table;
    }

    private static async Task<int> ExecuteGuardedPrmResultUpdateAsync(
        SqlConnection connection,
        int sampleId,
        int sampleTestId,
        string? expectedResult,
        string? expectedInterpretation,
        string? expectedRemarks,
        string? expectedEnteredBy,
        DateTime? expectedEnteredDate,
        string? newResult,
        string newInterpretation,
        string? newRemarks,
        string enteredBy,
        bool resultChanged,
        bool interpretationChanged,
        bool remarksChanged,
        SqlTransaction? transaction = null)
    {
        await using SqlCommand command = new(PrmResultPersistenceContract.GuardedUpdateSql, connection, transaction) { CommandTimeout = 60 };
        command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
        command.Parameters.Add("@SampleTestID", SqlDbType.Int).Value = sampleTestId;
        command.Parameters.Add("@ResultChanged", SqlDbType.Bit).Value = resultChanged;
        command.Parameters.Add("@InterpretationChanged", SqlDbType.Bit).Value = interpretationChanged;
        command.Parameters.Add("@RemarksChanged", SqlDbType.Bit).Value = remarksChanged;
        command.Parameters.Add("@ResultValue", SqlDbType.NVarChar, 200).Value = newResult == null ? DBNull.Value : newResult;
        command.Parameters.Add("@Interpretation", SqlDbType.NVarChar, 60).Value = newInterpretation;
        command.Parameters.Add("@Remarks", SqlDbType.NVarChar, 500).Value = newRemarks == null ? DBNull.Value : newRemarks;
        command.Parameters.Add("@EnteredBy", SqlDbType.NVarChar, 120).Value = enteredBy;
        command.Parameters.Add("@ExpectedResultValue", SqlDbType.NVarChar, 200).Value = expectedResult == null ? DBNull.Value : expectedResult;
        command.Parameters.Add("@ExpectedInterpretation", SqlDbType.NVarChar, 60).Value = expectedInterpretation == null ? DBNull.Value : expectedInterpretation;
        command.Parameters.Add("@ExpectedRemarks", SqlDbType.NVarChar, 500).Value = expectedRemarks == null ? DBNull.Value : expectedRemarks;
        command.Parameters.Add("@ExpectedEnteredBy", SqlDbType.NVarChar, 120).Value = expectedEnteredBy == null ? DBNull.Value : expectedEnteredBy;
        command.Parameters.Add("@ExpectedEnteredDate", SqlDbType.DateTime2).Value = expectedEnteredDate.HasValue ? expectedEnteredDate.Value : DBNull.Value;

        int rows = 0;
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows++;
        return rows;
    }

    private static async Task VerifySchemaAsync(string connectionString)
    {
        const string verificationSql = @"
DECLARE @Missing TABLE(ObjectName nvarchar(260) NOT NULL);

IF OBJECT_ID(N'dbo.Users',N'U') IS NULL INSERT @Missing VALUES(N'dbo.Users');
IF OBJECT_ID(N'dbo.Samples',N'U') IS NULL INSERT @Missing VALUES(N'dbo.Samples');
IF OBJECT_ID(N'dbo.SampleTests',N'U') IS NULL INSERT @Missing VALUES(N'dbo.SampleTests');
IF OBJECT_ID(N'dbo.WaterTestProfiles',N'U') IS NULL INSERT @Missing VALUES(N'dbo.WaterTestProfiles');
IF OBJECT_ID(N'dbo.WaterTestProfileTests',N'U') IS NULL INSERT @Missing VALUES(N'dbo.WaterTestProfileTests');
IF OBJECT_ID(N'dbo.WaterSpecifications',N'U') IS NULL INSERT @Missing VALUES(N'dbo.WaterSpecifications');
IF OBJECT_ID(N'dbo.WaterTestProfileSignatures',N'U') IS NULL INSERT @Missing VALUES(N'dbo.WaterTestProfileSignatures');
IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL INSERT @Missing VALUES(N'dbo.QualityEvents');
IF OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NULL INSERT @Missing VALUES(N'dbo.QualityEventAffectedResults');
IF OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NULL INSERT @Missing VALUES(N'dbo.QualityEventActions');
IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NULL INSERT @Missing VALUES(N'dbo.QualityEventChecklistQuestions');
IF OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory',N'U') IS NULL INSERT @Missing VALUES(N'dbo.QualityEventInvestigationEvidenceHistory');
IF OBJECT_ID(N'dbo.TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001',N'TR') IS NULL INSERT @Missing VALUES(N'TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001');
IF OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL INSERT @Missing VALUES(N'dbo.EM_Events');
IF OBJECT_ID(N'dbo.EM_EventPlates',N'U') IS NULL INSERT @Missing VALUES(N'dbo.EM_EventPlates');
IF OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NULL INSERT @Missing VALUES(N'dbo.EM_LimitSnapshotReconciliations');
IF OBJECT_ID(N'dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828',N'TR') IS NULL INSERT @Missing VALUES(N'TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828');
IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NULL INSERT @Missing VALUES(N'dbo.LegacyCertificateEvidenceReconciliations');
IF OBJECT_ID(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909',N'FN') IS NULL INSERT @Missing VALUES(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909');
IF OBJECT_ID(N'dbo.TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908',N'TR') IS NULL INSERT @Missing VALUES(N'TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908');
IF OBJECT_ID(N'dbo.EM_GradeLimitSignatures',N'U') IS NULL INSERT @Missing VALUES(N'dbo.EM_GradeLimitSignatures');
IF OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_Samples');
IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_SpecificationTests');
IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_TimingMigrationHistory');
IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_TimingMigrationTestEvidence');
IF OBJECT_ID(N'dbo.PRM_SpecificationTimingReapprovalHistory',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_SpecificationTimingReapprovalHistory');
IF OBJECT_ID(N'dbo.PRM_TimingGovernanceMigrationState',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_TimingGovernanceMigrationState');
IF OBJECT_ID(N'dbo.PRM_TimingQELegacyLinkCorrections',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_TimingQELegacyLinkCorrections');
IF OBJECT_ID(N'dbo.PRM_SampleTests',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_SampleTests');
IF OBJECT_ID(N'dbo.PRM_Certificates',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_Certificates');
IF OBJECT_ID(N'dbo.PRM_CertificateHistory',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_CertificateHistory');
IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_CertificateSnapshots');
IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_ElectronicSignatures');
IF OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NULL INSERT @Missing VALUES(N'dbo.PRM_QualityEventEvidenceReconciliations');
IF OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002',N'TR') IS NULL INSERT @Missing VALUES(N'TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002');
IF OBJECT_ID(N'dbo.EMTrendReviewSnapshots',N'U') IS NULL INSERT @Missing VALUES(N'dbo.EMTrendReviewSnapshots');
IF OBJECT_ID(N'dbo.CultureMediaLots',N'U') IS NULL INSERT @Missing VALUES(N'dbo.CultureMediaLots');
IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements',N'U') IS NULL INSERT @Missing VALUES(N'dbo.CultureMediaQualificationRequirements');
IF OBJECT_ID(N'dbo.MediaQualifications',N'U') IS NULL INSERT @Missing VALUES(N'dbo.MediaQualifications');
IF OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots',N'U') IS NULL INSERT @Missing VALUES(N'dbo.MediaQualificationRequirementSnapshots');

IF COL_LENGTH(N'dbo.SampleTests',N'ResultStatus') IS NULL INSERT @Missing VALUES(N'SampleTests.ResultStatus');
IF COL_LENGTH(N'dbo.WaterTestProfiles',N'ControlledReference') IS NULL INSERT @Missing VALUES(N'WaterTestProfiles.ControlledReference');
IF COL_LENGTH(N'dbo.WaterSpecifications',N'ProfileID') IS NULL INSERT @Missing VALUES(N'WaterSpecifications.ProfileID');
IF OBJECT_ID(N'dbo.TRG_WaterTestProfileSignatures_AppendOnly',N'TR') IS NULL INSERT @Missing VALUES(N'TRG_WaterTestProfileSignatures_AppendOnly');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.WaterTestProfiles') AND name=N'UX_WaterTestProfiles_OneActiveCode_20260907_000') INSERT @Missing VALUES(N'UX_WaterTestProfiles_OneActiveCode_20260907_000');
IF COL_LENGTH(N'dbo.SampleTests',N'LimitDescription') IS NULL INSERT @Missing VALUES(N'SampleTests.LimitDescription');
IF COL_LENGTH(N'dbo.EM_EventPlates',N'AlertLimitSnapshot') IS NULL INSERT @Missing VALUES(N'EM_EventPlates.AlertLimitSnapshot');
IF COL_LENGTH(N'dbo.EM_EventPlates',N'ActionLimitSnapshot') IS NULL INSERT @Missing VALUES(N'EM_EventPlates.ActionLimitSnapshot');
IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'EvidenceReference') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.EvidenceReference');
IF COL_LENGTH(N'dbo.EM_LimitSnapshotReconciliations',N'ReconciliationSchemaVersion') IS NULL INSERT @Missing VALUES(N'EM_LimitSnapshotReconciliations.ReconciliationSchemaVersion');
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL INSERT @Missing VALUES(N'QualityEvents.SourceModule');
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL INSERT @Missing VALUES(N'QualityEvents.SourceRecordID');
IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationNumericLimit') IS NULL INSERT @Missing VALUES(N'QualityEventAffectedResults.SpecificationNumericLimit');
IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion') IS NULL INSERT @Missing VALUES(N'QualityEventAffectedResults.EvidenceSchemaVersion');
IF COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'OldRowsJson') IS NULL INSERT @Missing VALUES(N'QualityEventInvestigationEvidenceHistory.OldRowsJson');
IF COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'NewRowsJson') IS NULL INSERT @Missing VALUES(N'QualityEventInvestigationEvidenceHistory.NewRowsJson');
IF COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'ChangeReason') IS NULL INSERT @Missing VALUES(N'QualityEventInvestigationEvidenceHistory.ChangeReason');
IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NULL INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.LegacyQualityEventID');
IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NULL INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.ReplacementQualityEventID');
IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NULL INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.ElectronicSignatureID');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SampleStatus') IS NULL INSERT @Missing VALUES(N'PRM_Samples.SampleStatus');
IF COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisStartedDate') IS NULL INSERT @Missing VALUES(N'PRM_Samples.AnalysisStartedDate');
IF COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisCompletedDate') IS NULL INSERT @Missing VALUES(N'PRM_Samples.AnalysisCompletedDate');
IF COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationStatus') IS NULL INSERT @Missing VALUES(N'PRM_Samples.TimingReconciliationStatus');
IF COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledBy') IS NULL INSERT @Missing VALUES(N'PRM_Samples.TimingReconciledBy');
IF COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledAt') IS NULL INSERT @Missing VALUES(N'PRM_Samples.TimingReconciledAt');
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NULL INSERT @Missing VALUES(N'PRM_SpecificationTests.MinimumElapsedHours');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NULL INSERT @Missing VALUES(N'PRM_SampleTests.MinimumElapsedHours');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'HasControlledQualityEventEvidence') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationHistory.HasControlledQualityEventEvidence');
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
      AND name=N'ReconciliationDisposition'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND is_nullable=0
      AND (max_length=-1 OR max_length>=160)
) INSERT @Missing VALUES(N'PRM_TimingMigrationHistory.ReconciliationDisposition>=NVARCHAR(80)');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartSignatureAt') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationHistory.AnalysisStartSignatureAt');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartProvenanceIssue') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationHistory.AnalysisStartProvenanceIssue');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'OriginalEnteredDate') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationTestEvidence.OriginalEnteredDate');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'EligibleAt') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationTestEvidence.EligibleAt');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartSignatureAt') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationTestEvidence.AnalysisStartSignatureAt');
IF COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartProvenanceIssue') IS NULL INSERT @Missing VALUES(N'PRM_TimingMigrationTestEvidence.AnalysisStartProvenanceIssue');
IF COL_LENGTH(N'dbo.PRM_TimingGovernanceMigrationState',N'CompletedAt') IS NULL INSERT @Missing VALUES(N'PRM_TimingGovernanceMigrationState.CompletedAt');
IF COL_LENGTH(N'dbo.PRM_TimingQELegacyLinkCorrections',N'SampleID') IS NULL INSERT @Missing VALUES(N'PRM_TimingQELegacyLinkCorrections.SampleID');
IF COL_LENGTH(N'dbo.PRM_TimingQELegacyLinkCorrections',N'TimingMigrationHistoryID') IS NULL INSERT @Missing VALUES(N'PRM_TimingQELegacyLinkCorrections.TimingMigrationHistoryID');
IF COL_LENGTH(N'dbo.CultureMediaLots',N'ExpiryDate') IS NULL INSERT @Missing VALUES(N'CultureMediaLots.ExpiryDate');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'MinimumIncubationHours') IS NULL INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.MinimumIncubationHours');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours') IS NULL INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.TimingConfirmedMinimumIncubationHours');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedBy') IS NULL INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.TimingConfirmedBy');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedAt') IS NULL INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.TimingConfirmedAt');
IF COL_LENGTH(N'dbo.MediaQualifications',N'QualificationStartedAt') IS NULL INSERT @Missing VALUES(N'MediaQualifications.QualificationStartedAt');
IF COL_LENGTH(N'dbo.MediaQualifications',N'MinimumIncubationHoursSnapshot') IS NULL INSERT @Missing VALUES(N'MediaQualifications.MinimumIncubationHoursSnapshot');
IF COL_LENGTH(N'dbo.MediaQualifications',N'IncubationCompletedAt') IS NULL INSERT @Missing VALUES(N'MediaQualifications.IncubationCompletedAt');
IF COL_LENGTH(N'dbo.MediaQualificationRequirementSnapshots',N'MinimumIncubationHoursSnapshot') IS NULL INSERT @Missing VALUES(N'MediaQualificationRequirementSnapshots.MinimumIncubationHoursSnapshot');
IF OBJECT_ID(N'dbo.CK_PRM_Samples_TimingReconciliation_20260906',N'C') IS NULL INSERT @Missing VALUES(N'CK_PRM_Samples_TimingReconciliation_20260906');
IF OBJECT_ID(N'dbo.TR_PRM_TimingMigrationHistory_AppendOnly_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_PRM_TimingMigrationHistory_AppendOnly_20260906');
IF OBJECT_ID(N'dbo.TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906');
IF OBJECT_ID(N'dbo.TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906');
IF OBJECT_ID(N'dbo.TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906');
IF OBJECT_ID(N'dbo.TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906');
IF OBJECT_ID(N'dbo.TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906');
IF OBJECT_ID(N'dbo.TR_CultureMediaLots_FinalReleaseExpiryGate_20260906',N'TR') IS NULL INSERT @Missing VALUES(N'TR_CultureMediaLots_FinalReleaseExpiryGate_20260906');
IF OBJECT_ID(N'dbo.CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906',N'C') IS NULL INSERT @Missing VALUES(N'CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906');
IF OBJECT_ID(N'dbo.CK_MediaQualificationReqSnapshots_MinHours',N'C') IS NULL INSERT @Missing VALUES(N'CK_MediaQualificationReqSnapshots_MinHours');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'ReportHash') IS NULL INSERT @Missing VALUES(N'PRM_Certificates.ReportHash');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'HtmlContent') IS NULL INSERT @Missing VALUES(N'PRM_CertificateSnapshots.HtmlContent');
IF COL_LENGTH(N'dbo.EMTrendReviewSnapshots',N'SnapshotHashSha256') IS NULL INSERT @Missing VALUES(N'EMTrendReviewSnapshots.SnapshotHashSha256');

SELECT ObjectName FROM @Missing ORDER BY ObjectName;";

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(verificationSql, connection) { CommandTimeout = 120 };
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        List<string> missing = new();
        while (await reader.ReadAsync())
            missing.Add(reader.GetString(0));

        if (missing.Count > 0)
            throw new InvalidOperationException("Fresh database schema is incomplete: " + string.Join(", ", missing));
    }

    private static async Task VerifyV225LegacyTestsActiveContractAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_Water225_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created v225 legacy Tests active-contract rehearsal database {databaseName}.");

            // Reproduce the user's v224 preflight shape: the historical dbo.Tests
            // contract has no IsActive column, while the Water controlled-master
            // tables are then created by 20260906_006.
            await ExecuteAsync(connectionString, @"
CREATE TABLE dbo.Tests
(
    TestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tests_V225 PRIMARY KEY,
    TestName NVARCHAR(200) NOT NULL,
    TestCategory NVARCHAR(100) NOT NULL,
    SortOrder INT NULL
);
INSERT dbo.Tests(TestName,TestCategory,SortOrder)
VALUES(N'Conductivity',N'Chemical',10);", timeoutSeconds: 120);

            JsonElement waterSchema = FindMigration(manifestRoot, "20260906_006");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                waterSchema.GetProperty("file").GetString()!,
                waterSchema.GetProperty("sha256").GetString()!,
                "20260906_006-v225-legacy-shape");

            JsonElement activeContract = FindMigration(manifestRoot, "20260906_007");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                activeContract.GetProperty("file").GetString()!,
                activeContract.GetProperty("sha256").GetString()!,
                "20260906_007-v225-tests-active");

            DataTable contract = await QueryAsync(connectionString, @"
SELECT
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Tests')
          AND name=N'IsActive'
          AND system_type_id=TYPE_ID(N'bit')
          AND is_nullable=0
    ) THEN 1 ELSE 0 END AS HasRequiredColumn,
    CASE WHEN EXISTS
    (
        SELECT 1 FROM dbo.Tests WHERE TestName=N'Conductivity' AND IsActive=1
    ) THEN 1 ELSE 0 END AS ExistingRowsPreservedActive;", timeoutSeconds: 120);

            if (contract.Rows.Count != 1 ||
                Convert.ToInt32(contract.Rows[0]["HasRequiredColumn"]) != 1 ||
                Convert.ToInt32(contract.Rows[0]["ExistingRowsPreservedActive"]) != 1)
            {
                throw new InvalidOperationException(
                    "v225 did not restore dbo.Tests.IsActive BIT NOT NULL while preserving legacy test rows.");
            }

            // This is the exact master-data query shape that v224 could not compile
            // on a legacy Tests table. It must now execute without SQL Server 207.
            DataTable missingProfiles = await QueryAsync(connectionString, @"
DECLARE @ExpectedWaterProfiles TABLE(ProfileCode nvarchar(20) NOT NULL PRIMARY KEY);
INSERT @ExpectedWaterProfiles(ProfileCode) VALUES(N'PW'),(N'PTW');

SELECT expected.ProfileCode
FROM @ExpectedWaterProfiles expected
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.WaterTestProfiles profile
    INNER JOIN dbo.WaterTestProfileTests profileTest
        ON profileTest.ProfileID=profile.ProfileID
       AND ISNULL(profileTest.IsActive,1)=1
    INNER JOIN dbo.Tests test
        ON test.TestID=profileTest.TestID
       AND ISNULL(test.IsActive,0)=1
    WHERE UPPER(LTRIM(RTRIM(profile.ProfileCode)))=expected.ProfileCode
      AND ISNULL(profile.IsActive,0)=1
)
ORDER BY expected.ProfileCode;", timeoutSeconds: 120);

            if (missingProfiles.Rows.Count != 2)
                throw new InvalidOperationException(
                    "v225 Water profile preflight rehearsal returned an unexpected empty-master result.");

            // Controlled replay must remain idempotent.
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                activeContract.GetProperty("file").GetString()!,
                activeContract.GetProperty("sha256").GetString()!,
                "20260906_007-v225-replay");

            Console.WriteLine("v225 legacy dbo.Tests.IsActive + Water preflight SQL 207 regression rehearsal PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v225 Tests active-contract rehearsal cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyV216EmAreaSnapshotCompileSafeRepairAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_EmRepair_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created drifted EM rehearsal database {databaseName}.");

            await ExecuteAsync(connectionString, @"
CREATE TABLE dbo.EM_Areas
(
    Id INT NOT NULL CONSTRAINT PK_EM_Areas_V216 PRIMARY KEY,
    AreaCode NVARCHAR(100) NULL,
    AreaName NVARCHAR(200) NULL,
    Grade NVARCHAR(100) NULL
);
CREATE TABLE dbo.EM_Events
(
    Id INT NOT NULL CONSTRAINT PK_EM_Events_V216 PRIMARY KEY,
    AreaId INT NULL
);
INSERT dbo.EM_Areas(Id,AreaCode,AreaName,Grade)
VALUES(1,N'GRAN-I',N'Granulation I',N'Grade D');
INSERT dbo.EM_Events(Id,AreaId) VALUES(1001,1);", timeoutSeconds: 120);

            JsonElement migration = FindMigration(manifestRoot, "20260906_004");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_004");

            await using (SqlConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                await using SqlCommand command = new(@"
SELECT AreaCodeSnapshot,AreaNameSnapshot,GradeSnapshot,AreaSnapshotSource,
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.triggers
           WHERE parent_id=OBJECT_ID(N'dbo.EM_Events')
             AND name=N'TRG_EM_Events_ProtectAreaSnapshot_20260830'
             AND is_disabled=0
       ) THEN 1 ELSE 0 END AS GuardEnabled
FROM dbo.EM_Events
WHERE Id=1001;", connection);
                await using SqlDataReader reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    throw new InvalidOperationException("v216 EM repair rehearsal did not retain the seeded event.");
                if (!reader.GetString(0).Equals("GRAN-I", StringComparison.Ordinal) ||
                    !reader.GetString(1).Equals("Granulation I", StringComparison.Ordinal) ||
                    !reader.GetString(2).Equals("Grade D", StringComparison.Ordinal) ||
                    !reader.GetString(3).Contains("Legacy current-master reconciliation", StringComparison.Ordinal) ||
                    reader.GetInt32(4) != 1)
                {
                    throw new InvalidOperationException("v216 EM repair rehearsal did not produce the expected frozen area snapshot and enabled guard.");
                }
            }

            try
            {
                await ExecuteAsync(connectionString,
                    "UPDATE dbo.EM_Events SET AreaNameSnapshot=N'TAMPER' WHERE Id=1001;",
                    timeoutSeconds: 120);
                throw new InvalidOperationException("v216 EM repair rehearsal allowed an immutable area snapshot to be changed.");
            }
            catch (SqlException ex) when (ex.Number == 54231)
            {
                // Expected immutable-snapshot protection.
            }

            // The replacement migration must also be idempotent on a repaired database.
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_004-replay");

            Console.WriteLine("v216 compile-safe EM area snapshot repair rehearsal PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v216 EM repair rehearsal cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyV222TimingDispositionWidthRepairAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_Timing222_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created SQL 2628 timing-disposition rehearsal database {databaseName}.");

            // Reproduce a database where the historical migration-owned table already
            // exists with the original NVARCHAR(40) contract.
            await ExecuteAsync(connectionString, @"
CREATE TABLE dbo.PRM_Samples
(
    SampleID INT NOT NULL CONSTRAINT PK_PRM_Samples_V222 PRIMARY KEY
);
INSERT dbo.PRM_Samples(SampleID) VALUES(1);

CREATE TABLE dbo.PRM_TimingMigrationHistory
(
    TimingMigrationHistoryID INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_PRM_TimingMigrationHistory_V222 PRIMARY KEY,
    SampleID INT NOT NULL,
    HasIssuedCertificate BIT NOT NULL,
    HasControlledQualityEventEvidence BIT NOT NULL,
    ReconciliationDisposition NVARCHAR(40) NOT NULL,
    EvidenceSummary NVARCHAR(1000) NOT NULL
);", timeoutSeconds: 120);

            JsonElement migration = FindMigration(manifestRoot, "20260906_000A");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_000A-v222-width-repair");

            await ExecuteAsync(connectionString, @"
INSERT dbo.PRM_TimingMigrationHistory
(
    SampleID,HasIssuedCertificate,HasControlledQualityEventEvidence,
    ReconciliationDisposition,EvidenceSummary
)
VALUES
(
    1,0,1,N'Historical Closed - Quality Event Evidence',
    N'v222 SQL 2628 regression rehearsal'
);", timeoutSeconds: 120);

            DataTable state = await QueryAsync(connectionString, @"
SELECT
    c.max_length AS MaxLengthBytes,
    h.ReconciliationDisposition,
    LEN(h.ReconciliationDisposition) AS DispositionLength
FROM sys.columns c
CROSS JOIN dbo.PRM_TimingMigrationHistory h
WHERE c.object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
  AND c.name=N'ReconciliationDisposition'
  AND h.SampleID=1;", timeoutSeconds: 120);

            if (state.Rows.Count != 1 ||
                Convert.ToInt32(state.Rows[0]["MaxLengthBytes"]) < 120 ||
                !string.Equals(
                    Convert.ToString(state.Rows[0]["ReconciliationDisposition"]),
                    "Historical Closed - Quality Event Evidence",
                    StringComparison.Ordinal) ||
                Convert.ToInt32(state.Rows[0]["DispositionLength"]) != 42)
            {
                throw new InvalidOperationException(
                    "v222 disposition-width repair did not preserve the 42-character controlled Quality Event disposition.");
            }

            // The prerequisite is intentionally idempotent for ledger post-condition replay.
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_000A-v222-replay");

            Console.WriteLine("v222 SQL 2628 timing-disposition regression rehearsal PASS.");

            // v223 keeps 000A immutable and strengthens the pre-001 contract with 000B.
            JsonElement widthContract = FindMigration(manifestRoot, "20260906_000B");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                widthContract.GetProperty("file").GetString()!,
                widthContract.GetProperty("sha256").GetString()!,
                "20260906_000B-v223-width-contract");

            DataTable v223State = await QueryAsync(connectionString, @"
SELECT c.max_length AS MaxLengthBytes,h.ReconciliationDisposition
FROM sys.columns c
CROSS JOIN dbo.PRM_TimingMigrationHistory h
WHERE c.object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
  AND c.name=N'ReconciliationDisposition'
  AND h.SampleID=1;", timeoutSeconds: 120);

            if (v223State.Rows.Count != 1 ||
                Convert.ToInt32(v223State.Rows[0]["MaxLengthBytes"]) < 160 ||
                !string.Equals(
                    Convert.ToString(v223State.Rows[0]["ReconciliationDisposition"]),
                    "Historical Closed - Quality Event Evidence",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "v223 disposition-width contract did not widen to NVARCHAR(80) while preserving controlled Quality Event evidence.");
            }

            Console.WriteLine("v223 pre-001 NVARCHAR(80) timing-disposition contract PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v222 timing-disposition rehearsal cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyV224WaterControlledMasterSchemaBackfillAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_Water224_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created v224 legacy Water-schema rehearsal database {databaseName}.");

            // Reproduce the user's legacy-upgrade shape: dbo.Tests exists, while the
            // three controlled Water master tables were baseline-only and are absent.
            await ExecuteAsync(connectionString, @"
CREATE TABLE dbo.Tests
(
    TestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tests_V224 PRIMARY KEY
);", timeoutSeconds: 120);

            JsonElement migration = FindMigration(manifestRoot, "20260906_006");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_006-v224-water-schema");

            DataTable state = await QueryAsync(connectionString, @"
SELECT
    CASE WHEN OBJECT_ID(N'dbo.WaterTestProfiles',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasProfiles,
    CASE WHEN OBJECT_ID(N'dbo.WaterTestProfileTests',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasProfileTests,
    CASE WHEN OBJECT_ID(N'dbo.WaterSpecifications',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasSpecifications,
    CASE WHEN COL_LENGTH(N'dbo.WaterTestProfiles',N'ProfileID') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfiles',N'ProfileCode') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfiles',N'IsActive') IS NOT NULL
         THEN 1 ELSE 0 END AS HasProfileColumns,
    CASE WHEN COL_LENGTH(N'dbo.WaterTestProfileTests',N'ProfileID') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfileTests',N'TestID') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfileTests',N'IsActive') IS NOT NULL
         THEN 1 ELSE 0 END AS HasProfileTestColumns,
    CASE WHEN EXISTS
         (
             SELECT 1
             FROM sys.foreign_keys
             WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
               AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
               AND is_disabled=0 AND is_not_trusted=0
         ) THEN 1 ELSE 0 END AS HasProfileFk,
    CASE WHEN EXISTS
         (
             SELECT 1
             FROM sys.foreign_keys
             WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
               AND referenced_object_id=OBJECT_ID(N'dbo.Tests')
               AND is_disabled=0 AND is_not_trusted=0
         ) THEN 1 ELSE 0 END AS HasTestFk;", timeoutSeconds: 120);

            if (state.Rows.Count != 1 ||
                Enumerable.Range(0, state.Columns.Count)
                    .Any(index => Convert.ToInt32(state.Rows[0][index]) != 1))
            {
                throw new InvalidOperationException(
                    "v224 Water controlled-master backfill did not create the required schema and trusted relationships.");
            }

            // Controlled replay must be idempotent and preserve the already-created objects.
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_006-v224-replay");

            Console.WriteLine("v224 legacy Water controlled-master schema backfill rehearsal PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v224 Water-schema rehearsal cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyV223PrmReportStatusContractRepairAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_Prm223_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created v223 PRM report-status repair rehearsal database {databaseName}.");

            await ExecuteAsync(connectionString, @"
CREATE TABLE dbo.PRM_Samples
(
    SampleID INT NOT NULL CONSTRAINT PK_PRM_Samples_V223 PRIMARY KEY,
    SampleStatus NVARCHAR(60) NULL,
    ReportStatus NVARCHAR(60) NULL,
    ReviewedBy NVARCHAR(120) NULL,
    ApprovedBy NVARCHAR(120) NULL,
    ModifiedBy NVARCHAR(120) NULL,
    ModifiedDate DATETIME2(0) NULL
);
CREATE TABLE dbo.PRM_TimingQELegacyLinkCorrections
(
    CorrectionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_QECorr_V223 PRIMARY KEY,
    SampleID INT NOT NULL
);
CREATE TABLE dbo.PRM_Certificates
(
    CertificateID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_Cert_V223 PRIMARY KEY,
    SampleID INT NOT NULL
);

INSERT dbo.PRM_Samples
    (SampleID,SampleStatus,ReportStatus,ReviewedBy,ApprovedBy,ModifiedBy,ModifiedDate)
VALUES
    (1,N'In Progress',N'Results Entered',NULL,NULL,N'System Migration 20260906_002',SYSDATETIME()),
    (2,N'Results Entered',N'Not Issued',NULL,NULL,N'IntegrationTest',SYSDATETIME()),
    (3,N'In Progress',N'Results Entered',N'Reviewer',NULL,N'IntegrationTest',SYSDATETIME());

INSERT dbo.PRM_TimingQELegacyLinkCorrections(SampleID) VALUES(1),(2),(3);
", timeoutSeconds: 120);

            JsonElement migration = FindMigration(manifestRoot, "20260906_002A");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_002A-v223-report-status-repair");

            DataTable state = await QueryAsync(connectionString, @"
SELECT SampleID,SampleStatus,ReportStatus,ReviewedBy,ModifiedBy
FROM dbo.PRM_Samples
ORDER BY SampleID;", timeoutSeconds: 120);

            if (state.Rows.Count != 3 ||
                !string.Equals(Convert.ToString(state.Rows[0]["SampleStatus"]), "In Progress", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(state.Rows[0]["ReportStatus"]), "Not Issued", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(state.Rows[0]["ModifiedBy"]), "System Migration 20260906_002A", StringComparison.Ordinal) ||
                !string.Equals(Convert.ToString(state.Rows[1]["ReportStatus"]), "Not Issued", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(state.Rows[2]["ReportStatus"]), "Results Entered", StringComparison.OrdinalIgnoreCase) ||
                state.Rows[2]["ReviewedBy"] == DBNull.Value)
            {
                throw new InvalidOperationException(
                    "v223 PRM report-status repair did not correct only the exact unprogressed 20260906_002 state.");
            }

            // Migration is idempotent and must preserve already-progressed rows.
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_002A-v223-replay");

            Console.WriteLine("v223 PRM ReportStatus Not Issued correction rehearsal PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v223 PRM report-status rehearsal cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyV221TimingSchemaContractHardeningAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_Timing221_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created drifted timing-contract rehearsal database {databaseName}.");

            // Rehearse the v220 shape that is type-compatible but lost the PRM default.
            await ExecuteAsync(connectionString, @"
CREATE TABLE dbo.PRM_SpecificationTests
(
    MinimumElapsedHours DECIMAL(9,2) NULL
);
CREATE TABLE dbo.PRM_SampleTests
(
    MinimumElapsedHours DECIMAL(9,2) NULL
);
CREATE TABLE dbo.CultureMediaQualificationRequirements
(
    MinimumIncubationHours DECIMAL(9,2) NULL,
    TimingConfirmedMinimumIncubationHours DECIMAL(9,2) NULL,
    TimingConfirmedBy NVARCHAR(100) NULL,
    TimingConfirmedAt DATETIME2(0) NULL
);
CREATE TABLE dbo.MediaQualifications
(
    QualificationStartedAt DATETIME2(0) NULL,
    MinimumIncubationHoursSnapshot DECIMAL(9,2) NULL,
    IncubationCompletedAt DATETIME2(0) NULL
);
CREATE TABLE dbo.PRM_Samples
(
    SampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_Samples_V221 PRIMARY KEY,
    TimingReconciliationStatus NVARCHAR(30) NOT NULL,
    TimingReconciledBy NVARCHAR(120) NULL,
    TimingReconciledAt DATETIME2(0) NULL,
    TimingReconciliationReason NVARCHAR(1000) NULL
);", timeoutSeconds: 120);

            JsonElement migration = FindMigration(manifestRoot, "20260906_005");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_005");

            await using (SqlConnection connection = new(connectionString))
            {
                await connection.OpenAsync();
                await using SqlCommand command = new(@"
DECLARE @DefaultDefinition NVARCHAR(MAX) =
(
    SELECT dc.definition
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id=dc.parent_object_id
       AND c.column_id=dc.parent_column_id
    WHERE dc.parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND c.name=N'TimingReconciliationStatus'
);

INSERT dbo.PRM_Samples DEFAULT VALUES;

SELECT
    CASE WHEN UPPER(REPLACE(REPLACE(REPLACE(ISNULL(@DefaultDefinition,N''),N'(',N''),N')',N''),N' ',N''))
                   IN (N'N''NOTREQUIRED''', N'''NOTREQUIRED''') THEN 1 ELSE 0 END,
    TimingReconciliationStatus
FROM dbo.PRM_Samples
WHERE SampleID=SCOPE_IDENTITY();", connection);
                await using SqlDataReader reader = await command.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    throw new InvalidOperationException("v221 timing schema rehearsal did not insert a defaulted PRM sample.");
                if (reader.GetInt32(0) != 1 ||
                    !reader.GetString(1).Equals("Not Required", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("v221 timing schema hardening did not restore the controlled PRM default.");
                }
            }

            // The hardening migration is intentionally idempotent for controlled replay.
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                migration.GetProperty("file").GetString()!,
                migration.GetProperty("sha256").GetString()!,
                "20260906_005-replay");

            Console.WriteLine("v221 timing schema contract hardening rehearsal PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v221 timing schema rehearsal cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyLegacyQualityEventUpgradeAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_LegacyQE_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created legacy Quality Event rehearsal database {databaseName}.");
            await ExecuteAsync(connectionString, LegacyQualityEventSchemaSql, timeoutSeconds: 120);

            foreach (string versionKey in new[] { "20260825_007", "20260826_001", "20260826_002" })
            {
                JsonElement migration = FindMigration(manifestRoot, versionKey);
                await ApplyControlledFileAsync(
                    projectRoot,
                    connectionString,
                    migration.GetProperty("file").GetString()!,
                    migration.GetProperty("sha256").GetString()!,
                    versionKey);
            }

            await VerifyLegacyQualityEventUpgradeStateAsync(connectionString);
            Console.WriteLine("Legacy PRM Quality Event migration rehearsal PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("Legacy rehearsal database cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyPrmQualityEventNumberReconciliationAsync(string connectionString)
    {
        const int year = 2026;
        const string seedSql = @"
DELETE FROM dbo.QualityEvents WHERE EventNumber=N'PRM-QE-2026-0003';
DELETE FROM dbo.PRM_NumberSequences WHERE SequenceName=N'PRM_QE';
INSERT dbo.QualityEvents
(
    EventNumber,EventType,Severity,SampleID,SampleNumber,SourceModule,SourceRecordID,
    CurrentStatus,DetectedBy,DetectedDate,DetectionSource,InitialDescription,
    ImmediateAction,CAPARequired,CreatedBy,CreatedDate
)
VALUES
(
    N'PRM-QE-2026-0003',N'PRM Microbiology Result Deviation',N'Major',NULL,
    N'IP-LEGACY-0003',N'PRM',900003,N'Closed',N'IntegrationTest',SYSDATETIME(),
    N'Integration rehearsal',N'Legacy numbering evidence.',N'Closed legacy event.',0,
    N'IntegrationTest',SYSDATETIME()
);";
        await ExecuteAsync(connectionString, seedSql, timeoutSeconds: 120);

        const string allocationSql = @"
DECLARE @Prefix NVARCHAR(20)=N'PRM-QE';
DECLARE @Stem NVARCHAR(40)=@Prefix+N'-'+CONVERT(NVARCHAR(4),@Year)+N'-';
DECLARE @ExistingEventMax INT=0;
SELECT @ExistingEventMax=ISNULL(MAX(TRY_CONVERT(INT, SUBSTRING(EventNumber,LEN(@Stem)+1,20))),0)
FROM dbo.QualityEvents WITH (UPDLOCK,HOLDLOCK)
WHERE EventNumber LIKE @Stem+N'%'
  AND TRY_CONVERT(INT, SUBSTRING(EventNumber,LEN(@Stem)+1,20)) IS NOT NULL;
MERGE dbo.PRM_NumberSequences WITH (HOLDLOCK) AS target
USING (SELECT N'PRM_QE' AS SequenceName) AS source
ON target.SequenceName=source.SequenceName
WHEN MATCHED THEN UPDATE SET
    Prefix=@Prefix,
    LastNumber=CASE WHEN target.CurrentYear=@Year
        THEN CASE WHEN ISNULL(target.LastNumber,0)>@ExistingEventMax THEN target.LastNumber+1 ELSE @ExistingEventMax+1 END
        ELSE @ExistingEventMax+1 END,
    CurrentYear=@Year,
    LastUpdated=SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT(SequenceName,Prefix,CurrentYear,LastNumber,LastUpdated)
    VALUES(N'PRM_QE',@Prefix,@Year,@ExistingEventMax+1,SYSDATETIME())
OUTPUT inserted.Prefix+N'-'+CONVERT(NVARCHAR(4),inserted.CurrentYear)+N'-'
       +RIGHT(N'0000'+CONVERT(NVARCHAR(20),inserted.LastNumber),
          CASE WHEN LEN(CONVERT(NVARCHAR(20),inserted.LastNumber))>4
               THEN LEN(CONVERT(NVARCHAR(20),inserted.LastNumber)) ELSE 4 END);";

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using SqlCommand command = new(allocationSql, connection, transaction) { CommandTimeout = 120 };
        command.Parameters.Add("@Year", SqlDbType.Int).Value = year;
        object? result = await command.ExecuteScalarAsync();
        string allocated = Convert.ToString(result) ?? string.Empty;
        if (!string.Equals(allocated, "PRM-QE-2026-0004", StringComparison.Ordinal))
            throw new InvalidOperationException("PRM Quality Event sequence did not reconcile with legacy EventNumber evidence. Allocated=" + allocated);
        await transaction.RollbackAsync();
        Console.WriteLine("Legacy PRM Quality Event number reconciliation PASS.");
    }

    private static async Task VerifyPrmInvestigationEvidenceBindingAsync(string connectionString)
    {
        const string seedSql = @"
DECLARE @SampleID INT;
DECLARE @SampleTestID INT;
DECLARE @QualityEventID INT;

INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,SampleStatus,ResultInterpretation,ReportStatus,CreatedBy)
VALUES(N'IP-EVIDENCE-0001',N'In-Process',N'Results Entered',N'Does Not Conform',N'Not Issued',N'IntegrationTest');
SET @SampleID = SCOPE_IDENTITY();

INSERT dbo.PRM_SampleTests
(
    SampleID,TestCode,TestName,SpecificationText,Unit,ResultValue,ResultType,
    SpecificationLimit,Interpretation,RequiredTest,SortOrder,EnteredBy,EnteredDate
)
VALUES
(
    @SampleID,N'TAMC',N'TAMC',N'NMT 100 CFU/g',N'CFU/g',N'200',N'Numeric',
    100.000,N'Does Not Conform',1,1,N'IntegrationTest',SYSDATETIME()
);
SET @SampleTestID = SCOPE_IDENTITY();

INSERT dbo.QualityEvents
(
    EventNumber,EventType,Severity,SampleNumber,SourceModule,SourceRecordID,CurrentStatus,
    DetectedBy,DetectedDate,DetectionSource,InitialDescription,ImmediateAction,
    RootCauseCategory,RootCauseDetails,ImpactAssessment,QAConclusion,FinalDisposition,
    ClosedBy,ClosedDate,CreatedBy,CreatedDate
)
VALUES
(
    N'PRM-QE-EVIDENCE-0001',N'PRM Microbiology Result Deviation',N'Major',N'IP-EVIDENCE-0001',
    N'PRM',@SampleID,N'Closed',N'IntegrationTest',SYSDATETIME(),N'Integration rehearsal',
    N'Evidence binding test',N'No immediate action',N'Laboratory',N'Controlled test',
    N'No additional impact',N'QA reviewed',N'Confirmed OOS - Original Result Retained',
    N'QAIntegration',SYSDATETIME(),N'IntegrationTest',SYSDATETIME()
);
SET @QualityEventID = SCOPE_IDENTITY();

INSERT dbo.QualityEventAffectedResults
(
    QualityEventID,SampleTestID,SourceModule,SourceResultID,TestID,TestName,ResultValue,
    SpecificationLimit,SpecificationNumericLimit,Unit,FailureType,EvidenceSchemaVersion,CreatedDate
)
VALUES
(
    @QualityEventID,NULL,N'PRM',@SampleTestID,NULL,N'TAMC',N'200',
    N'NMT 100 CFU/g',100.000,N'CFU/g',N'Does Not Conform',1,SYSDATETIME()
);

SELECT @SampleID AS SampleID, @SampleTestID AS SampleTestID, @QualityEventID AS QualityEventID;";

        int sampleId;
        int sampleTestId;
        int qualityEventId;
        await using (SqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand seed = new(seedSql, connection) { CommandTimeout = 120 };
            await using SqlDataReader reader = await seed.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("PRM evidence-binding rehearsal could not create test data.");
            sampleId = reader.GetInt32(0);
            sampleTestId = reader.GetInt32(1);
            qualityEventId = reader.GetInt32(2);
        }

        const string mismatchSql = @"
SELECT COUNT(1)
FROM dbo.QualityEventAffectedResults affected
LEFT JOIN dbo.PRM_SampleTests currentResult
  ON currentResult.SampleTestID = affected.SourceResultID
 AND currentResult.SampleID = @SampleID
WHERE affected.QualityEventID = @QualityEventID
  AND affected.SourceModule = N'PRM'
  AND
  (
      currentResult.SampleTestID IS NULL
      OR ISNULL(affected.EvidenceSchemaVersion,0) <> 1
      OR CONVERT(VARBINARY(MAX),ISNULL(affected.TestName,N'')) <> CONVERT(VARBINARY(MAX),ISNULL(currentResult.TestName,N''))
      OR CONVERT(VARBINARY(MAX),ISNULL(affected.ResultValue,N'')) <> CONVERT(VARBINARY(MAX),ISNULL(currentResult.ResultValue,N''))
      OR CONVERT(VARBINARY(MAX),ISNULL(affected.SpecificationLimit,N'')) <> CONVERT(VARBINARY(MAX),ISNULL(currentResult.SpecificationText,N''))
      OR NOT (affected.SpecificationNumericLimit=currentResult.SpecificationLimit OR (affected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
      OR CONVERT(VARBINARY(MAX),ISNULL(affected.Unit,N'')) <> CONVERT(VARBINARY(MAX),ISNULL(currentResult.Unit,N''))
      OR CONVERT(VARBINARY(MAX),ISNULL(affected.FailureType,N'')) <> CONVERT(VARBINARY(MAX),ISNULL(currentResult.Interpretation,N''))
  );";

        async Task<int> MismatchCountAsync()
        {
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqlCommand command = new(mismatchSql, connection) { CommandTimeout = 120 };
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            command.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;
            object? value = await command.ExecuteScalarAsync();
            return Convert.ToInt32(value ?? 0);
        }

        if (await MismatchCountAsync() != 0)
            throw new InvalidOperationException("Fresh v183 PRM investigation evidence did not match its source result.");

        await ExecuteAsync(connectionString,
            $"UPDATE dbo.PRM_SampleTests SET ResultValue=N'150' WHERE SampleTestID={sampleTestId};");
        if (await MismatchCountAsync() == 0)
            throw new InvalidOperationException("PRM evidence binding failed to detect a changed result value on the same SampleTestID.");

        await ExecuteAsync(connectionString,
            $"UPDATE dbo.PRM_SampleTests SET ResultValue=N'200', SpecificationLimit=90.000 WHERE SampleTestID={sampleTestId};");
        if (await MismatchCountAsync() == 0)
            throw new InvalidOperationException("PRM evidence binding failed to detect a changed numeric specification on the same SampleTestID.");

        await ExecuteAsync(connectionString,
            $"UPDATE dbo.PRM_SampleTests SET SpecificationLimit=100.000 WHERE SampleTestID={sampleTestId}; " +
            $"UPDATE dbo.QualityEventAffectedResults SET EvidenceSchemaVersion=0 WHERE QualityEventID={qualityEventId} AND SourceResultID={sampleTestId};");
        if (await MismatchCountAsync() == 0)
            throw new InvalidOperationException("Legacy/unversioned PRM evidence did not fail closed.");

        Console.WriteLine("PRM investigation evidence version-binding rehearsal PASS.");
    }

    private static async Task VerifyPrmLegacyEvidenceReconciliationAsync(string connectionString)
    {
        const string seedSql = @"
DECLARE @SampleID INT;
DECLARE @SampleTestID INT;
DECLARE @LegacyEventID INT;
DECLARE @ReplacementEventID INT;
DECLARE @SignatureID INT;

INSERT dbo.PRM_Samples(SampleNumber,SampleCategory,SampleStatus,ResultInterpretation,ReportStatus,CreatedBy)
VALUES(N'IP-RECON-0001',N'In-Process',N'Reviewed',N'Does Not Conform',N'Not Issued',N'IntegrationTest');
SET @SampleID=SCOPE_IDENTITY();

INSERT dbo.PRM_SampleTests
(
    SampleID,TestCode,TestName,SpecificationText,Unit,ResultValue,ResultType,
    SpecificationLimit,Interpretation,RequiredTest,SortOrder,EnteredBy,EnteredDate
)
VALUES
(
    @SampleID,N'TAMC',N'TAMC',N'NMT 100 CFU/g',N'CFU/g',N'200',N'Numeric',
    100.000,N'Does Not Conform',1,1,N'IntegrationTest',SYSDATETIME()
);
SET @SampleTestID=SCOPE_IDENTITY();

INSERT dbo.QualityEvents
(
    EventNumber,EventType,Severity,SampleNumber,SourceModule,SourceRecordID,CurrentStatus,
    DetectedBy,DetectedDate,DetectionSource,InitialDescription,ImmediateAction,
    RootCauseCategory,RootCauseDetails,ImpactAssessment,QAConclusion,FinalDisposition,
    ClosedBy,ClosedDate,CreatedBy,CreatedDate
)
VALUES
(
    N'PRM-QE-RECON-LEGACY',N'PRM Microbiology Result Deviation',N'Major',N'IP-RECON-0001',
    N'PRM',@SampleID,N'Closed',N'IntegrationTest',DATEADD(MINUTE,-5,SYSDATETIME()),N'Legacy rehearsal',
    N'Historical event',N'Historical action',N'Laboratory',N'Historical root cause',
    N'Historical impact',N'Historical QA conclusion',N'Confirmed OOS - Original Result Retained',
    N'QAIntegration',DATEADD(MINUTE,-4,SYSDATETIME()),N'IntegrationTest',DATEADD(MINUTE,-5,SYSDATETIME())
);
SET @LegacyEventID=SCOPE_IDENTITY();

INSERT dbo.QualityEventAffectedResults
(
    QualityEventID,SampleTestID,SourceModule,SourceResultID,TestID,TestName,ResultValue,
    SpecificationLimit,SpecificationNumericLimit,Unit,FailureType,EvidenceSchemaVersion,CreatedDate
)
VALUES
(
    @LegacyEventID,NULL,N'PRM',@SampleTestID,NULL,N'TAMC',N'200',
    N'NMT 100 CFU/g',NULL,N'CFU/g',N'Does Not Conform',0,DATEADD(MINUTE,-5,SYSDATETIME())
);

INSERT dbo.QualityEvents
(
    EventNumber,EventType,Severity,SampleNumber,SourceModule,SourceRecordID,CurrentStatus,
    DetectedBy,DetectedDate,DetectionSource,InitialDescription,ImmediateAction,
    RootCauseCategory,RootCauseDetails,ImpactAssessment,QAConclusion,FinalDisposition,
    ClosedBy,ClosedDate,CreatedBy,CreatedDate
)
VALUES
(
    N'PRM-QE-RECON-V1',N'PRM Microbiology Result Deviation',N'Major',N'IP-RECON-0001',
    N'PRM',@SampleID,N'Closed',N'IntegrationTest',DATEADD(MINUTE,-2,SYSDATETIME()),N'v184 reconciliation rehearsal',
    N'Controlled replacement investigation',N'Controlled action',N'Laboratory',N'Controlled root cause',
    N'Controlled impact',N'QA reviewed replacement',N'Confirmed OOS - Original Result Retained',
    N'QAIntegration',DATEADD(MINUTE,-1,SYSDATETIME()),N'IntegrationTest',DATEADD(MINUTE,-2,SYSDATETIME())
);
SET @ReplacementEventID=SCOPE_IDENTITY();

INSERT dbo.QualityEventAffectedResults
(
    QualityEventID,SampleTestID,SourceModule,SourceResultID,TestID,TestName,ResultValue,
    SpecificationLimit,SpecificationNumericLimit,Unit,FailureType,EvidenceSchemaVersion,CreatedDate
)
VALUES
(
    @ReplacementEventID,NULL,N'PRM',@SampleTestID,NULL,N'TAMC',N'200',
    N'NMT 100 CFU/g',100.000,N'CFU/g',N'Does Not Conform',1,SYSDATETIME()
);

INSERT dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,UserRole,SignedAt)
VALUES
(@SampleID,N'Quality Event Closure',N'QAIntegration',N'PRM Quality Event Closure and Legacy Evidence Reconciliation',N'Integration rehearsal',N'QA',SYSDATETIME());
SET @SignatureID=SCOPE_IDENTITY();

SELECT @SampleID,@SampleTestID,@LegacyEventID,@ReplacementEventID,@SignatureID;";

        int sampleId;
        int sampleTestId;
        int legacyEventId;
        int replacementEventId;
        int signatureId;
        await using (SqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand seed = new(seedSql, connection) { CommandTimeout = 120 };
            await using SqlDataReader reader = await seed.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidOperationException("PRM legacy reconciliation rehearsal could not create test data.");
            sampleId = reader.GetInt32(0);
            sampleTestId = reader.GetInt32(1);
            legacyEventId = reader.GetInt32(2);
            replacementEventId = reader.GetInt32(3);
            signatureId = reader.GetInt32(4);
        }

        const string unresolvedSql = @"
SELECT COUNT(1)
FROM dbo.QualityEvents legacyEvent
WHERE legacyEvent.QualityEventID=@LegacyEventID
  AND legacyEvent.SourceModule=N'PRM'
  AND legacyEvent.SourceRecordID=@SampleID
  AND legacyEvent.CurrentStatus=N'Closed'
  AND EXISTS
  (
      SELECT 1 FROM dbo.QualityEventAffectedResults legacyAffected
      WHERE legacyAffected.QualityEventID=legacyEvent.QualityEventID
        AND legacyAffected.SourceModule=N'PRM'
        AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
  )
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.PRM_QualityEventEvidenceReconciliations rec
      INNER JOIN dbo.QualityEvents replacementEvent
        ON replacementEvent.QualityEventID=rec.ReplacementQualityEventID
       AND replacementEvent.CurrentStatus=N'Closed'
       AND replacementEvent.SourceModule=N'PRM'
       AND replacementEvent.SourceRecordID=@SampleID
      INNER JOIN dbo.PRM_ElectronicSignatures sig
        ON sig.SignatureID=rec.ElectronicSignatureID
       AND sig.SampleID=@SampleID
       AND sig.SignedBy=rec.ReconciledBy
      WHERE rec.LegacyQualityEventID=@LegacyEventID
        AND rec.SampleID=@SampleID
        AND rec.ReconciliationSchemaVersion=1
        AND rec.ReplacementQualityEventID>rec.LegacyQualityEventID
        AND NOT EXISTS
        (
            SELECT 1
            FROM dbo.QualityEventAffectedResults oldAffected
            WHERE oldAffected.QualityEventID=@LegacyEventID
              AND oldAffected.SourceModule=N'PRM'
              AND ISNULL(oldAffected.EvidenceSchemaVersion,0)<>1
              AND
              (
                  oldAffected.SourceResultID IS NULL
                  OR NOT EXISTS
                  (
                      SELECT 1
                      FROM dbo.QualityEventAffectedResults newAffected
                      INNER JOIN dbo.PRM_SampleTests currentResult
                        ON currentResult.SampleTestID=newAffected.SourceResultID
                       AND currentResult.SampleID=@SampleID
                      WHERE newAffected.QualityEventID=rec.ReplacementQualityEventID
                        AND newAffected.SourceModule=N'PRM'
                        AND newAffected.SourceResultID=oldAffected.SourceResultID
                        AND ISNULL(newAffected.EvidenceSchemaVersion,0)=1
                        AND ISNULL(newAffected.TestName,N'')=ISNULL(currentResult.TestName,N'')
                        AND ISNULL(newAffected.ResultValue,N'')=ISNULL(currentResult.ResultValue,N'')
                        AND ISNULL(newAffected.SpecificationLimit,N'')=ISNULL(currentResult.SpecificationText,N'')
                        AND (newAffected.SpecificationNumericLimit=currentResult.SpecificationLimit OR (newAffected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
                        AND ISNULL(newAffected.Unit,N'')=ISNULL(currentResult.Unit,N'')
                        AND ISNULL(newAffected.FailureType,N'')=ISNULL(currentResult.Interpretation,N'')
                  )
              )
        )
  );";

        async Task<int> UnresolvedCountAsync()
        {
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqlCommand command = new(unresolvedSql, connection) { CommandTimeout = 120 };
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            command.Parameters.Add("@LegacyEventID", SqlDbType.Int).Value = legacyEventId;
            object? value = await command.ExecuteScalarAsync();
            return Convert.ToInt32(value ?? 0);
        }

        if (await UnresolvedCountAsync() != 1)
            throw new InvalidOperationException("Legacy v0 PRM evidence was not fail-closed before explicit reconciliation.");

        await ExecuteAsync(connectionString, $@"
INSERT dbo.PRM_QualityEventEvidenceReconciliations
(LegacyQualityEventID,ReplacementQualityEventID,SampleID,ReconciledBy,ReconciliationReason,ElectronicSignatureID,ReconciliationSchemaVersion)
VALUES({legacyEventId},{replacementEventId},{sampleId},N'QAIntegration',N'Controlled replacement integration rehearsal',{signatureId},1);");

        if (await UnresolvedCountAsync() != 0)
            throw new InvalidOperationException("A signed valid v1 replacement did not reconcile the historical v0 PRM evidence.");

        await ExecuteAsync(connectionString, $"UPDATE dbo.PRM_SampleTests SET ResultValue=N'150' WHERE SampleTestID={sampleTestId};");
        if (await UnresolvedCountAsync() != 1)
            throw new InvalidOperationException("Changing the current result did not invalidate the prior PRM evidence reconciliation.");

        Console.WriteLine("PRM legacy v0 -> signed v1 evidence reconciliation rehearsal PASS.");
    }

    private static async Task VerifyEmLimitSnapshotReconciliationAsync(string connectionString)
    {
        const string seedSql = @"
DECLARE @AreaId int,@EventId int,@PlateId int;
INSERT dbo.EM_Areas(AreaCode,AreaName,AreaGroup,Grade,IsActive)
VALUES(N'V194-AREA',N'V194 integration area',N'Integration',N'V194-GRADE',1);
SET @AreaId=CONVERT(int,SCOPE_IDENTITY());

INSERT dbo.EM_Events(EventNo,EventDate,AreaId,WorkflowStatus,FinalResult)
VALUES(N'EM-V194-INTEGRATION',CAST(GETDATE() AS date),@AreaId,N'Results Pending',N'Pending');
SET @EventId=CONVERT(int,SCOPE_IDENTITY());

BEGIN TRY
    DISABLE TRIGGER dbo.TRG_EM_EventPlates_FreezeLimits_20260828 ON dbo.EM_EventPlates;
    INSERT dbo.EM_EventPlates(EventId,Method,PlateCode,SequenceNo,Status)
    VALUES(@EventId,N'Settle Plate',N'V194-LEGACY',1,N'Pending');
    SET @PlateId=CONVERT(int,SCOPE_IDENTITY());
    ENABLE TRIGGER dbo.TRG_EM_EventPlates_FreezeLimits_20260828 ON dbo.EM_EventPlates;
END TRY
BEGIN CATCH
    IF EXISTS
    (
        SELECT 1 FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.EM_EventPlates')
          AND name=N'TRG_EM_EventPlates_FreezeLimits_20260828'
          AND is_disabled=1
    )
        ENABLE TRIGGER dbo.TRG_EM_EventPlates_FreezeLimits_20260828 ON dbo.EM_EventPlates;
    THROW;
END CATCH;

DECLARE @InsertedReconciliation TABLE(ReconciliationID int NOT NULL);

INSERT dbo.EM_LimitSnapshotReconciliations
(PlateID,SupersedesReconciliationID,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot,
 EvidenceReference,Reason,SignedBy,UserRole,MeaningOfSignature,SourceWorkstation,ReconciliationSchemaVersion)
OUTPUT INSERTED.ReconciliationID INTO @InsertedReconciliation(ReconciliationID)
VALUES(@PlateId,NULL,10,20,N'CFU/plate',NULL,N'INT-V194-EVIDENCE',N'Integration reconciliation',N'IntegrationTest',N'QA',N'Approval of historical evidence',N'CI',1);

SELECT
    @PlateId AS PlateId,
    (SELECT TOP(1) ReconciliationID FROM @InsertedReconciliation) AS ReconciliationID;";

        DataTable seeded = await QueryAsync(connectionString, seedSql, timeoutSeconds: 120);
        if (seeded.Rows.Count != 1)
            throw new InvalidOperationException("EM reconciliation integration seed did not return the legacy plate id.");
        int plateId = Convert.ToInt32(seeded.Rows[0]["PlateId"]);
        int reconciliationId = Convert.ToInt32(seeded.Rows[0]["ReconciliationID"]);
        if (reconciliationId <= 0)
            throw new InvalidOperationException("Trigger-safe EM reconciliation identity capture did not return a reconciliation id.");

        DataTable effective = await QueryAsync(connectionString, $@"
SELECT TOP(1) AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,ReconciliationID
FROM dbo.EM_LimitSnapshotReconciliations
WHERE PlateID={plateId}
ORDER BY ReconciliationID DESC;", timeoutSeconds: 120);
        if (effective.Rows.Count != 1 || Convert.ToDecimal(effective.Rows[0]["AlertLimitSnapshot"]) != 10m ||
            Convert.ToDecimal(effective.Rows[0]["ActionLimitSnapshot"]) != 20m ||
            !string.Equals(Convert.ToString(effective.Rows[0]["ResultUnitSnapshot"]), "CFU/plate", StringComparison.Ordinal))
            throw new InvalidOperationException("Signed EM reconciliation evidence was not preserved correctly.");

        bool appendOnlyBlocked = false;
        try
        {
            await ExecuteAsync(connectionString, $"UPDATE dbo.EM_LimitSnapshotReconciliations SET Reason=N'Changed' WHERE PlateID={plateId};", timeoutSeconds: 120);
        }
        catch (SqlException)
        {
            appendOnlyBlocked = true;
        }
        if (!appendOnlyBlocked)
            throw new InvalidOperationException("EM reconciliation append-only trigger did not block evidence update.");

        bool missingLimitBlocked = false;
        try
        {
            await ExecuteAsync(connectionString, @"
DECLARE @AreaId int,@EventId int;
INSERT dbo.EM_Areas(AreaCode,AreaName,AreaGroup,Grade,IsActive)
VALUES(N'V194-NOLIMIT',N'V194 no-limit area',N'Integration',N'V194-NO-LIMIT',1);
SET @AreaId=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.EM_Events(EventNo,EventDate,AreaId,WorkflowStatus,FinalResult)
VALUES(N'EM-V194-NOLIMIT',CAST(GETDATE() AS date),@AreaId,N'Results Pending',N'Pending');
SET @EventId=CONVERT(int,SCOPE_IDENTITY());
INSERT dbo.EM_EventPlates(EventId,Method,PlateCode,SequenceNo,Status,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot)
VALUES(@EventId,N'Settle Plate',N'V194-NOLIMIT-PLATE',1,N'Pending',1,2,N'CFU/plate');", timeoutSeconds: 120);
        }
        catch (SqlException)
        {
            missingLimitBlocked = true;
        }
        if (!missingLimitBlocked)
            throw new InvalidOperationException("Strict EM plate creation did not fail closed when an approved Grade/Method limit was missing.");

        Console.WriteLine("EM historical snapshot reconciliation and strict creation PASS.");
    }

    private static async Task VerifyV212TimingGovernanceUpgradeAsync(
        string projectRoot,
        string masterConnectionString,
        JsonElement manifestRoot)
    {
        string databaseName = "PharmaLIMS_V212Upgrade_" + Guid.NewGuid().ToString("N")[..12];
        string connectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];");
            Console.WriteLine($"Created v212 timing-governance upgrade rehearsal database {databaseName}.");

            JsonElement baseline = manifestRoot.GetProperty("freshInstallBaseline");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                baseline.GetProperty("file").GetString()!,
                baseline.GetProperty("sha256").GetString()!,
                baseline.GetProperty("versionKey").GetString()! + "-v212-rehearsal");

            foreach (JsonElement migration in manifestRoot.GetProperty("migrations").EnumerateArray())
            {
                string versionKey = migration.GetProperty("versionKey").GetString() ?? string.Empty;
                if (string.Equals(versionKey, "20260906_001", StringComparison.Ordinal))
                    break;

                if (migration.TryGetProperty("supersededBy", out JsonElement supersededBy) &&
                    !string.IsNullOrWhiteSpace(supersededBy.GetString()))
                {
                    continue;
                }

                await ApplyControlledFileAsync(
                    projectRoot,
                    connectionString,
                    migration.GetProperty("file").GetString()!,
                    migration.GetProperty("sha256").GetString()!,
                    versionKey + "-v212-rehearsal");
            }

            DataTable seeded = await QueryAsync(connectionString, @"
DECLARE @SampleID int,@NoResultSampleID int,@QualityEventSampleID int,@QualityEventSampleTestID int,@QualityEventID int;
INSERT INTO dbo.PRM_Samples
(
    SampleNumber,SampleCategory,SampleDateTime,SampleStatus,ResultInterpretation,ReportStatus,
    AnalysisStartedDate,CreatedBy
)
VALUES
(
    N'PRM-V212-BACKDATE',N'Raw Material',DATEADD(DAY,-6,SYSDATETIME()),N'Results Entered',N'Pass',N'Not Issued',
    DATEADD(DAY,-5,SYSDATETIME()),N'IntegrationTest'
);
SET @SampleID=CONVERT(int,SCOPE_IDENTITY());

INSERT INTO dbo.PRM_SampleTests
(SampleID,TestCode,TestName,ResultValue,Interpretation,RequiredTest,EnteredBy,EnteredDate,MinimumElapsedHours)
VALUES
(@SampleID,N'TAMC',N'TAMC',N'25',N'Pass',1,N'IntegrationTest',SYSDATETIME(),72.00);

INSERT INTO dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,SignedAt)
VALUES
(@SampleID,N'Analysis Start',N'IntegrationTest',N'Analysis Start',N'Integration backdate provenance rehearsal',SYSDATETIME());

INSERT INTO dbo.PRM_Samples
(
    SampleNumber,SampleCategory,SampleDateTime,SampleStatus,ResultInterpretation,ReportStatus,
    AnalysisStartedDate,CreatedBy
)
VALUES
(
    N'PRM-V212-BACKDATE-NORESULT',N'Raw Material',DATEADD(DAY,-6,SYSDATETIME()),N'In Progress',N'Not Tested',N'Not Issued',
    DATEADD(DAY,-5,SYSDATETIME()),N'IntegrationTest'
);
SET @NoResultSampleID=CONVERT(int,SCOPE_IDENTITY());

INSERT INTO dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,SignedAt)
VALUES
(@NoResultSampleID,N'Analysis Start',N'IntegrationTest',N'Analysis Start',N'Integration no-result backdate provenance rehearsal',SYSDATETIME());

/* Exact v223 SQL 2628 regression path: affected legacy PRM result with controlled QE evidence. */
INSERT INTO dbo.PRM_Samples
(
    SampleNumber,SampleCategory,SampleDateTime,SampleStatus,ResultInterpretation,ReportStatus,
    AnalysisStartedDate,CreatedBy
)
VALUES
(
    N'PRM-V223-QE-DISPOSITION',N'Raw Material',DATEADD(DAY,-6,SYSDATETIME()),N'Results Entered',N'Pass',N'Not Issued',
    DATEADD(DAY,-5,SYSDATETIME()),N'IntegrationTest'
);
SET @QualityEventSampleID=CONVERT(int,SCOPE_IDENTITY());

INSERT INTO dbo.PRM_SampleTests
(SampleID,TestCode,TestName,ResultValue,Interpretation,RequiredTest,EnteredBy,EnteredDate,MinimumElapsedHours)
VALUES
(@QualityEventSampleID,N'TYMC',N'TYMC',N'12',N'Pass',1,N'IntegrationTest',SYSDATETIME(),72.00);
SET @QualityEventSampleTestID=CONVERT(int,SCOPE_IDENTITY());

INSERT INTO dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,SignedAt)
VALUES
(@QualityEventSampleID,N'Analysis Start',N'IntegrationTest',N'Analysis Start',N'v223 Quality Event disposition regression rehearsal',SYSDATETIME());

INSERT INTO dbo.QualityEvents
(
    EventNumber,EventType,Severity,SampleNumber,SourceModule,SourceRecordID,CurrentStatus,
    DetectedBy,DetectedDate,DetectionSource,InitialDescription,ImmediateAction,CreatedBy,CreatedDate
)
VALUES
(
    N'PRM-QE-V223-2628',N'PRM Microbiology Result Deviation',N'Major',N'PRM-V223-QE-DISPOSITION',
    N'PRM',@QualityEventSampleID,N'Closed',N'IntegrationTest',SYSDATETIME(),N'Integration rehearsal',
    N'Exercise historical timing evidence linked to a Quality Event.',N'Controlled migration regression test.',
    N'IntegrationTest',SYSDATETIME()
);
SET @QualityEventID=CONVERT(int,SCOPE_IDENTITY());

INSERT dbo.QualityEventAffectedResults
(
    QualityEventID,SampleTestID,SourceModule,SourceResultID,TestName,ResultValue,FailureType,CreatedDate
)
VALUES
(
    @QualityEventID,NULL,N'PRM',@QualityEventSampleTestID,N'TYMC',N'12',N'Pass',SYSDATETIME()
);

SELECT
    @SampleID AS SampleID,
    @NoResultSampleID AS NoResultSampleID,
    @QualityEventSampleID AS QualityEventSampleID;", timeoutSeconds: 120);
            if (seeded.Rows.Count != 1)
                throw new InvalidOperationException("v212 upgrade rehearsal could not seed legacy PRM timing records.");
            int backdatedSampleId = Convert.ToInt32(seeded.Rows[0]["SampleID"]);
            int noResultBackdatedSampleId = Convert.ToInt32(seeded.Rows[0]["NoResultSampleID"]);
            int qualityEventSampleId = Convert.ToInt32(seeded.Rows[0]["QualityEventSampleID"]);

            JsonElement v212 = FindMigration(manifestRoot, "20260906_001");
            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                v212.GetProperty("file").GetString()!,
                v212.GetProperty("sha256").GetString()!,
                "20260906_001-upgrade-rehearsal");

            DataTable reconciledBackdate = await QueryAsync(connectionString, $@"
SELECT
    s.TimingReconciliationStatus,
    s.AnalysisStartedDate,
    t.ResultValue,
    h.AnalysisStartSignatureAt,
    h.AnalysisStartProvenanceIssue,
    h.ReconciliationDisposition,
    e.OriginalResultValue,
    e.AnalysisStartProvenanceIssue AS TestEvidenceIssue
FROM dbo.PRM_Samples s
INNER JOIN dbo.PRM_SampleTests t ON t.SampleID=s.SampleID
INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
INNER JOIN dbo.PRM_TimingMigrationTestEvidence e ON e.SampleTestID=t.SampleTestID
WHERE s.SampleID={backdatedSampleId};", timeoutSeconds: 120);
            if (reconciledBackdate.Rows.Count != 1 ||
                !string.Equals(Convert.ToString(reconciledBackdate.Rows[0]["TimingReconciliationStatus"]), "Required", StringComparison.OrdinalIgnoreCase) ||
                reconciledBackdate.Rows[0]["AnalysisStartedDate"] != DBNull.Value ||
                reconciledBackdate.Rows[0]["ResultValue"] != DBNull.Value ||
                reconciledBackdate.Rows[0]["AnalysisStartSignatureAt"] == DBNull.Value ||
                string.IsNullOrWhiteSpace(Convert.ToString(reconciledBackdate.Rows[0]["AnalysisStartProvenanceIssue"])) ||
                !string.Equals(Convert.ToString(reconciledBackdate.Rows[0]["ReconciliationDisposition"]), "Requires Controlled Re-entry", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(reconciledBackdate.Rows[0]["OriginalResultValue"]), "25", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(Convert.ToString(reconciledBackdate.Rows[0]["TestEvidenceIssue"])))
            {
                throw new InvalidOperationException("v212 upgrade did not preserve and quarantine legacy backdated PRM result evidence fail-closed.");
            }

            DataTable controlledRestart = await QueryAsync(connectionString, $@"
SELECT s.TimingReconciliationStatus,s.AnalysisStartedDate,h.ReconciliationDisposition,h.AnalysisStartProvenanceIssue
FROM dbo.PRM_Samples s
INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
WHERE s.SampleID={noResultBackdatedSampleId};", timeoutSeconds: 120);
            if (controlledRestart.Rows.Count != 1 ||
                !string.Equals(Convert.ToString(controlledRestart.Rows[0]["TimingReconciliationStatus"]), "Not Required", StringComparison.OrdinalIgnoreCase) ||
                controlledRestart.Rows[0]["AnalysisStartedDate"] != DBNull.Value ||
                !string.Equals(Convert.ToString(controlledRestart.Rows[0]["ReconciliationDisposition"]), "Controlled Restart Required", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(Convert.ToString(controlledRestart.Rows[0]["AnalysisStartProvenanceIssue"])))
            {
                throw new InvalidOperationException("v212 upgrade did not reset an untrusted legacy Analysis Start that had no result evidence.");
            }

            DataTable qualityEventDisposition = await QueryAsync(connectionString, $@"
SELECT
    s.TimingReconciliationStatus,
    h.ReconciliationDisposition,
    LEN(h.ReconciliationDisposition) AS DispositionLength,
    COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'ReconciliationDisposition') AS DispositionBytes
FROM dbo.PRM_Samples s
INNER JOIN dbo.PRM_TimingMigrationHistory h ON h.SampleID=s.SampleID
WHERE s.SampleID={qualityEventSampleId};", timeoutSeconds: 120);
            if (qualityEventDisposition.Rows.Count != 1 ||
                !string.Equals(
                    Convert.ToString(qualityEventDisposition.Rows[0]["TimingReconciliationStatus"]),
                    "Historical Closed",
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    Convert.ToString(qualityEventDisposition.Rows[0]["ReconciliationDisposition"]),
                    "Historical Closed - Quality Event Evidence",
                    StringComparison.Ordinal) ||
                Convert.ToInt32(qualityEventDisposition.Rows[0]["DispositionLength"]) != 42 ||
                Convert.ToInt32(qualityEventDisposition.Rows[0]["DispositionBytes"]) < 160)
            {
                throw new InvalidOperationException(
                    "v223 SQL 2628 regression path did not preserve the controlled 42-character Quality Event disposition in the NVARCHAR(80) contract.");
            }
            Console.WriteLine("v223 SQL 2628 historical 20260906_001 Quality Event disposition PASS.");

            DataTable firstPassState = await QueryAsync(connectionString, @"
SELECT
    (SELECT COUNT(1) FROM dbo.PRM_TimingGovernanceMigrationState WHERE StateKey=N'InitialLegacyPrmTimingEvidenceCaptured') AS LegacyStateCount,
    (SELECT COUNT(1) FROM dbo.PRM_TimingGovernanceMigrationState WHERE StateKey=N'InitialSpecificationTimingReapprovalCaptured') AS SpecStateCount,
    (SELECT COUNT(1) FROM dbo.PRM_TimingMigrationHistory) AS MigrationHistoryCount,
    (SELECT COUNT(1) FROM dbo.PRM_TimingMigrationTestEvidence) AS TestEvidenceCount;", timeoutSeconds: 120);
            if (firstPassState.Rows.Count != 1 ||
                Convert.ToInt32(firstPassState.Rows[0]["LegacyStateCount"]) != 1 ||
                Convert.ToInt32(firstPassState.Rows[0]["SpecStateCount"]) != 1)
            {
                throw new InvalidOperationException("v212 one-time timing-governance migration state was not captured exactly once.");
            }
            int firstHistoryCount = Convert.ToInt32(firstPassState.Rows[0]["MigrationHistoryCount"]);
            int firstEvidenceCount = Convert.ToInt32(firstPassState.Rows[0]["TestEvidenceCount"]);

            await ExecuteAsync(connectionString, $@"
UPDATE dbo.PRM_Samples
SET AnalysisStartedDate=SYSDATETIME(),ModifiedBy=N'IntegrationTest'
WHERE SampleID={noResultBackdatedSampleId};
INSERT INTO dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,SignedAt)
VALUES
({noResultBackdatedSampleId},N'Analysis Start',N'IntegrationTest',N'Analysis Start',N'Controlled restart after v212 reconciliation',SYSDATETIME());", timeoutSeconds: 120);

            DataTable restartBeforeReplay = await QueryAsync(connectionString, $@"
SELECT AnalysisStartedDate FROM dbo.PRM_Samples WHERE SampleID={noResultBackdatedSampleId};", timeoutSeconds: 120);
            if (restartBeforeReplay.Rows.Count != 1 || restartBeforeReplay.Rows[0]["AnalysisStartedDate"] == DBNull.Value)
                throw new InvalidOperationException("Controlled restart rehearsal could not establish a new authoritative Analysis Start.");
            DateTime controlledRestartTime = Convert.ToDateTime(restartBeforeReplay.Rows[0]["AnalysisStartedDate"]);

            await ApplyControlledFileAsync(
                projectRoot,
                connectionString,
                v212.GetProperty("file").GetString()!,
                v212.GetProperty("sha256").GetString()!,
                "20260906_001-idempotency-replay");

            DataTable replayState = await QueryAsync(connectionString, $@"
SELECT
    (SELECT COUNT(1) FROM dbo.PRM_TimingGovernanceMigrationState WHERE StateKey=N'InitialLegacyPrmTimingEvidenceCaptured') AS LegacyStateCount,
    (SELECT COUNT(1) FROM dbo.PRM_TimingGovernanceMigrationState WHERE StateKey=N'InitialSpecificationTimingReapprovalCaptured') AS SpecStateCount,
    (SELECT COUNT(1) FROM dbo.PRM_TimingMigrationHistory) AS MigrationHistoryCount,
    (SELECT COUNT(1) FROM dbo.PRM_TimingMigrationTestEvidence) AS TestEvidenceCount,
    (SELECT AnalysisStartedDate FROM dbo.PRM_Samples WHERE SampleID={noResultBackdatedSampleId}) AS RestartedAt;", timeoutSeconds: 120);
            if (replayState.Rows.Count != 1 ||
                Convert.ToInt32(replayState.Rows[0]["LegacyStateCount"]) != 1 ||
                Convert.ToInt32(replayState.Rows[0]["SpecStateCount"]) != 1 ||
                Convert.ToInt32(replayState.Rows[0]["MigrationHistoryCount"]) != firstHistoryCount ||
                Convert.ToInt32(replayState.Rows[0]["TestEvidenceCount"]) != firstEvidenceCount ||
                replayState.Rows[0]["RestartedAt"] == DBNull.Value ||
                Convert.ToDateTime(replayState.Rows[0]["RestartedAt"]) != controlledRestartTime)
            {
                throw new InvalidOperationException("v212 replay reprocessed legacy evidence or mutated a controlled post-upgrade Analysis Start.");
            }

            DataTable trustedControls = await QueryAsync(connectionString, @"
SELECT
    CASE WHEN EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'CK_PRM_Samples_TimingReconciliation_20260906'
          AND is_disabled=0 AND is_not_trusted=0
    ) THEN 1 ELSE 0 END AS PrmConstraintTrusted,
    CASE WHEN EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906'
          AND is_disabled=0 AND is_not_trusted=0
    ) THEN 1 ELSE 0 END AS MediaConstraintTrusted;", timeoutSeconds: 120);
            if (trustedControls.Rows.Count != 1 ||
                Convert.ToInt32(trustedControls.Rows[0]["PrmConstraintTrusted"]) != 1 ||
                Convert.ToInt32(trustedControls.Rows[0]["MediaConstraintTrusted"]) != 1)
            {
                throw new InvalidOperationException("v212 replay did not preserve trusted timing-governance constraints.");
            }

            bool stateAppendOnlyBlocked = false;
            try
            {
                await ExecuteAsync(connectionString, @"
UPDATE dbo.PRM_TimingGovernanceMigrationState
SET Notes=N'Integration mutation attempt'
WHERE StateKey=N'InitialLegacyPrmTimingEvidenceCaptured';", timeoutSeconds: 120);
            }
            catch (SqlException)
            {
                stateAppendOnlyBlocked = true;
            }
            if (!stateAppendOnlyBlocked)
                throw new InvalidOperationException("v212 timing-governance migration state was not append-only.");

            DataTable historyCount = await QueryAsync(connectionString,
                "SELECT COUNT(1) AS HistoryCount FROM dbo.PRM_SpecificationTimingReapprovalHistory;",
                timeoutSeconds: 120);
            if (historyCount.Rows.Count == 1 && Convert.ToInt32(historyCount.Rows[0]["HistoryCount"]) > 0)
            {
                bool historyAppendOnlyBlocked = false;
                try
                {
                    await ExecuteAsync(connectionString, @"
UPDATE dbo.PRM_SpecificationTimingReapprovalHistory
SET TestCode=N'Integration mutation attempt'
WHERE ReapprovalHistoryID=(SELECT MIN(ReapprovalHistoryID) FROM dbo.PRM_SpecificationTimingReapprovalHistory);", timeoutSeconds: 120);
                }
                catch (SqlException)
                {
                    historyAppendOnlyBlocked = true;
                }
                if (!historyAppendOnlyBlocked)
                    throw new InvalidOperationException("PRM timing master reapproval history was not append-only.");
            }

            Console.WriteLine("v212 pre-upgrade timing evidence / one-time replay / append-only integration PASS.");
        }
        finally
        {
            try
            {
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;");
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("v212 upgrade rehearsal database cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task VerifyV213FinalReleaseHardeningAsync(string connectionString)
    {
        DataTable seeded = await QueryAsync(connectionString, @"
DECLARE @MediaID int,@ExpiredLotID int,@ValidLotID int;
INSERT dbo.CultureMedia(MediaCode,MediaName,MediaType,Manufacturer,StorageCondition,DefaultExpiryDays,IsActive)
VALUES(N'V213-EXPIRY',N'v213 expiry gate',N'Dehydrated',N'Integration',N'Controlled',14,1);
SET @MediaID=CONVERT(int,SCOPE_IDENTITY());
DECLARE @InsertedLot TABLE(MediaLotID int NOT NULL, LotNumber nvarchar(100) NOT NULL);

INSERT dbo.CultureMediaLots(MediaID,LotNumber,ReceivedDate,ExpiryDate,ReceiptStatus,CurrentStockG)
OUTPUT INSERTED.MediaLotID,INSERTED.LotNumber INTO @InsertedLot(MediaLotID,LotNumber)
VALUES(@MediaID,N'V213-EXPIRED',DATEADD(DAY,-10,CAST(SYSDATETIME() AS date)),DATEADD(DAY,-1,CAST(SYSDATETIME() AS date)),N'Quarantine',100),
      (@MediaID,N'V213-VALID',CAST(SYSDATETIME() AS date),DATEADD(DAY,30,CAST(SYSDATETIME() AS date)),N'Quarantine',100);
SELECT
    MAX(CASE WHEN LotNumber=N'V213-EXPIRED' THEN MediaLotID END) AS ExpiredLotID,
    MAX(CASE WHEN LotNumber=N'V213-VALID' THEN MediaLotID END) AS ValidLotID
FROM @InsertedLot;", timeoutSeconds: 120);
        if (seeded.Rows.Count != 1)
            throw new InvalidOperationException("v213 final-release expiry rehearsal could not seed Culture Media lots.");
        int expiredLotId = Convert.ToInt32(seeded.Rows[0]["ExpiredLotID"]);
        int validLotId = Convert.ToInt32(seeded.Rows[0]["ValidLotID"]);

        bool expiredReleaseBlocked = false;
        try
        {
            await ExecuteAsync(connectionString,
                $"UPDATE dbo.CultureMediaLots SET ReceiptStatus=N'Released' WHERE MediaLotID={expiredLotId};",
                timeoutSeconds: 120);
        }
        catch (SqlException ex) when (ex.Number == 54122)
        {
            expiredReleaseBlocked = true;
        }
        if (!expiredReleaseBlocked)
            throw new InvalidOperationException("v213 database expiry trigger did not block release of an expired Culture Media lot.");

        await ExecuteAsync(connectionString,
            $"UPDATE dbo.CultureMediaLots SET ReceiptStatus=N'Released' WHERE MediaLotID={validLotId};",
            timeoutSeconds: 120);
        DataTable validState = await QueryAsync(connectionString,
            $"SELECT ReceiptStatus FROM dbo.CultureMediaLots WHERE MediaLotID={validLotId};",
            timeoutSeconds: 120);
        if (validState.Rows.Count != 1 ||
            !string.Equals(Convert.ToString(validState.Rows[0]["ReceiptStatus"]), "Released", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("v213 database expiry trigger blocked a valid within-expiry Culture Media release.");
        }

        Console.WriteLine("v213 Culture Media final-release expiry database gate PASS.");
    }

    private static JsonElement FindMigration(JsonElement manifestRoot, string versionKey)
    {
        foreach (JsonElement migration in manifestRoot.GetProperty("migrations").EnumerateArray())
        {
            if (string.Equals(migration.GetProperty("versionKey").GetString(), versionKey, StringComparison.Ordinal))
                return migration;
        }

        throw new InvalidOperationException("Controlled migration is missing from the manifest: " + versionKey);
    }

    private static async Task VerifyLegacyQualityEventUpgradeStateAsync(string connectionString)
    {
        // Validate the legacy physical shape first, then verify that new PRM evidence can be inserted.
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        const string structuralSql = @"
DECLARE @Problems TABLE(Problem nvarchar(400) NOT NULL);
IF EXISTS
(
    SELECT object_id FROM sys.identity_columns
    WHERE object_id IN
    (
        OBJECT_ID(N'dbo.QualityEvents'),OBJECT_ID(N'dbo.QualityEventAffectedResults'),
        OBJECT_ID(N'dbo.QualityEventActions'),OBJECT_ID(N'dbo.QualityEventChecklistAnswers'),
        OBJECT_ID(N'dbo.QualityEventPrintHistory')
    )
    GROUP BY object_id HAVING COUNT(*)>1
) INSERT @Problems VALUES(N'Multiple IDENTITY columns detected.');
IF (SELECT COUNT_BIG(*) FROM dbo.QualityEvents)<>1 INSERT @Problems VALUES(N'QualityEvents row count changed.');
IF (SELECT COUNT_BIG(*) FROM dbo.QualityEventAffectedResults)<>1 INSERT @Problems VALUES(N'Affected-result row count changed.');
IF (SELECT COUNT_BIG(*) FROM dbo.QualityEventActions)<>1 INSERT @Problems VALUES(N'Action row count changed.');
IF (SELECT COUNT_BIG(*) FROM dbo.QualityEventChecklistAnswers)<>1 INSERT @Problems VALUES(N'Checklist-answer row count changed.');
IF (SELECT COUNT_BIG(*) FROM dbo.QualityEventPrintHistory)<>1 INSERT @Problems VALUES(N'Print-history row count changed.');
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'ResultValue'
      AND system_type_id=TYPE_ID(N'nvarchar') AND (max_length=-1 OR max_length>=400) AND is_computed=0
) INSERT @Problems VALUES(N'ResultValue is not NVARCHAR(200+).');
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'SourceResultID'
      AND system_type_id=TYPE_ID(N'int') AND is_nullable=1 AND is_computed=0
) INSERT @Problems VALUES(N'SourceResultID is not nullable INT.');
SELECT Problem FROM @Problems ORDER BY Problem;";

        await using (SqlCommand structural = new(structuralSql, connection) { CommandTimeout = 120 })
        await using (SqlDataReader reader = await structural.ExecuteReaderAsync())
        {
            List<string> problems = new();
            while (await reader.ReadAsync())
                problems.Add(reader.GetString(0));
            if (problems.Count > 0)
                throw new InvalidOperationException("Legacy Quality Event reconciliation failed: " + string.Join("; ", problems));
        }

        const string insertSql = @"
DECLARE @QualityEventID int=(SELECT TOP 1 QualityEventID FROM dbo.QualityEvents ORDER BY QualityEventID);
DECLARE @Affected TABLE(ID int NOT NULL);
DECLARE @Action TABLE(ID int NOT NULL);
DECLARE @Print TABLE(ID bigint NOT NULL);

INSERT dbo.QualityEventAffectedResults
(
    QualityEventID,SampleTestID,SourceModule,SourceResultID,TestID,TestName,
    ResultValue,SpecificationLimit,Unit,FailureType,CreatedDate
)
OUTPUT inserted.AffectedResultID INTO @Affected(ID)
VALUES(@QualityEventID,NULL,N'PRM',900001,NULL,N'Salmonella spp.',N'Absence',N'Absent in 10 g',NULL,N'Does Not Conform',SYSDATETIME());

INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate,ElectronicSignatureID)
OUTPUT inserted.QualityEventActionID INTO @Action(ID)
VALUES(@QualityEventID,N'Integration Test',N'Legacy rehearsal generated action.',N'IntegrationTest',SYSDATETIME(),NULL);

INSERT dbo.QualityEventPrintHistory(QualityEventID,PrintedBy,PrintedDate)
OUTPUT inserted.QualityEventPrintID INTO @Print(ID)
VALUES(@QualityEventID,N'IntegrationTest',SYSUTCDATETIME());

SELECT
    (SELECT TOP 1 ID FROM @Affected) AS AffectedID,
    (SELECT TOP 1 ID FROM @Action) AS ActionID,
    (SELECT TOP 1 ID FROM @Print) AS PrintID,
    (SELECT TOP 1 ResultValue FROM dbo.QualityEventAffectedResults WHERE SourceModule=N'PRM' AND SourceResultID=900001) AS QualitativeResult;";

        await using SqlCommand insert = new(insertSql, connection) { CommandTimeout = 120 };
        await using SqlDataReader inserted = await insert.ExecuteReaderAsync();
        if (!await inserted.ReadAsync())
            throw new InvalidOperationException("Legacy Quality Event insert rehearsal returned no verification row.");
        if (inserted.GetInt32(0) <= 0 || inserted.GetInt32(1) <= 0 || inserted.GetInt64(2) <= 0)
            throw new InvalidOperationException("A reconciled Quality Event surrogate key did not auto-generate.");
        if (!string.Equals(inserted.GetString(3), "Absence", StringComparison.Ordinal))
            throw new InvalidOperationException("Qualitative PRM affected-result evidence was not preserved.");
    }

    private const string LegacyQualityEventSchemaSql = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;

CREATE TABLE dbo.PRM_NumberSequences
(
    SequenceName nvarchar(50) NOT NULL PRIMARY KEY,
    CurrentYear int NOT NULL,
    LastNumber int NOT NULL
);
INSERT dbo.PRM_NumberSequences(SequenceName,CurrentYear,LastNumber) VALUES(N'QE',2026,12);
CREATE TABLE dbo.PRM_Samples(SampleID int IDENTITY(1,1) NOT NULL PRIMARY KEY);
CREATE TABLE dbo.PRM_SampleTests(SampleTestID int IDENTITY(1,1) NOT NULL PRIMARY KEY);
CREATE TABLE dbo.ElectronicSignatures(SignatureID int IDENTITY(1,1) NOT NULL PRIMARY KEY);

CREATE TABLE dbo.QualityEvents
(
    QualityEventID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EventNumber nvarchar(50) NOT NULL,
    EventType nvarchar(50) NOT NULL,
    Severity nvarchar(50) NOT NULL,
    SampleID int NULL,
    SampleNumber nvarchar(100) NULL,
    CurrentStatus nvarchar(50) NOT NULL,
    DetectedBy nvarchar(100) NULL,
    DetectedDate datetime NOT NULL,
    DetectionSource nvarchar(100) NULL,
    InitialDescription nvarchar(max) NULL,
    ImmediateAction nvarchar(max) NULL,
    RootCauseCategory nvarchar(100) NULL,
    RootCauseDetails nvarchar(max) NULL,
    ImpactAssessment nvarchar(max) NULL,
    CAPARequired bit NOT NULL CONSTRAINT DF_LegacyQE_CAPA DEFAULT(0),
    QAConclusion nvarchar(max) NULL,
    FinalDisposition nvarchar(100) NULL,
    ClosedBy nvarchar(100) NULL,
    ClosedDate datetime NULL,
    CreatedDate datetime NOT NULL CONSTRAINT DF_LegacyQE_Created DEFAULT(GETDATE()),
    ModifiedBy nvarchar(100) NULL,
    ModifiedDate datetime NULL,
    SourceModule nvarchar(100) NULL,
    SourceRecordID int NULL
);

CREATE TABLE dbo.QualityEventChecklistQuestions
(
    QuestionID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    SectionName nvarchar(100) NOT NULL,
    QuestionText nvarchar(700) NOT NULL,
    AppliesToEventType nvarchar(50) NULL,
    AppliesToSampleType nvarchar(100) NULL,
    AppliesToTestCategory nvarchar(100) NULL,
    AppliesToTestNameKeyword nvarchar(100) NULL,
    AnswerType nvarchar(50) NOT NULL CONSTRAINT DF_LegacyQQ_AnswerType DEFAULT(N'YesNoNA'),
    ExpectedAnswer nvarchar(20) NULL,
    QuestionLogic nvarchar(50) NULL,
    IsRequired bit NOT NULL CONSTRAINT DF_LegacyQQ_Required DEFAULT(1),
    SortOrder int NOT NULL CONSTRAINT DF_LegacyQQ_Sort DEFAULT(0),
    IsActive bit NOT NULL CONSTRAINT DF_LegacyQQ_Active DEFAULT(1),
    CreatedDate datetime NOT NULL CONSTRAINT DF_LegacyQQ_Created DEFAULT(GETDATE())
);

CREATE TABLE dbo.QualityEventAffectedResults
(
    AffectedResultID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    QualityEventID int NOT NULL,
    SampleTestID int NULL,
    TestID int NULL,
    TestName nvarchar(200) NULL,
    ResultValue decimal(18,4) NULL,
    SpecificationLimit nvarchar(200) NULL,
    Unit nvarchar(50) NULL,
    FailureType nvarchar(50) NOT NULL,
    CreatedDate datetime NOT NULL CONSTRAINT DF_LegacyQEAR_Created DEFAULT(GETDATE())
);

CREATE TABLE dbo.QualityEventActions
(
    QualityEventActionID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    QualityEventID int NOT NULL,
    ActionType nvarchar(100) NOT NULL,
    ActionDescription nvarchar(max) NULL,
    PerformedBy nvarchar(100) NULL,
    PerformedDate datetime NOT NULL CONSTRAINT DF_LegacyQEA_Created DEFAULT(GETDATE()),
    ElectronicSignatureID int NULL
);

CREATE TABLE dbo.QualityEventChecklistAnswers
(
    AnswerID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    QualityEventID int NOT NULL,
    QuestionID int NOT NULL,
    AnswerValue nvarchar(250) NULL,
    Comments nvarchar(max) NULL,
    AnsweredBy nvarchar(100) NULL,
    AnsweredDate datetime NULL
);

/* Actual legacy drift case: the table already owns another IDENTITY column. */
CREATE TABLE dbo.QualityEventPrintHistory
(
    PrintHistoryID int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    QualityEventID int NOT NULL,
    PrintedBy nvarchar(100) NOT NULL,
    PrintedDate datetime NOT NULL CONSTRAINT DF_LegacyQEPrint_Created DEFAULT(GETDATE())
);

INSERT dbo.QualityEvents(EventNumber,EventType,Severity,CurrentStatus,DetectedBy,DetectedDate,SourceModule,SourceRecordID)
VALUES(N'QE-LEGACY-0001',N'OOS',N'Major',N'Open',N'LegacyUser',GETDATE(),N'PRM',1);
DECLARE @QEID int=SCOPE_IDENTITY();
INSERT dbo.QualityEventChecklistQuestions(SectionName,QuestionText,ExpectedAnswer,QuestionLogic,SortOrder)
VALUES(N'General',N'Legacy question',N'Yes',N'PositiveCheck',1);
DECLARE @QuestionID int=SCOPE_IDENTITY();
INSERT dbo.QualityEventAffectedResults(QualityEventID,SampleTestID,TestID,TestName,ResultValue,SpecificationLimit,Unit,FailureType)
VALUES(@QEID,NULL,NULL,N'TAMC',200,N'NMT 100 CFU/g',N'CFU/g',N'Does Not Conform');
INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy)
VALUES(@QEID,N'Legacy Action',N'Preserve this action.',N'LegacyUser');
INSERT dbo.QualityEventChecklistAnswers(QualityEventID,QuestionID,AnswerValue,Comments,AnsweredBy,AnsweredDate)
VALUES(@QEID,@QuestionID,N'Yes',N'Preserve this answer.',N'LegacyUser',GETDATE());
INSERT dbo.QualityEventPrintHistory(QualityEventID,PrintedBy) VALUES(@QEID,N'LegacyUser');
";

    private static async Task VerifyLifecycleLeaseRoundTripAsync(string connectionString)
    {
        await using SqlConnection preflightConnection = new(connectionString);
        await preflightConnection.OpenAsync();

        int initialCompatibility = await ExecuteLockScalarAsync(preflightConnection, @"
SELECT APPLOCK_TEST(N'public', N'PharmaLIMS.SchemaMigration', N'Shared', N'Session');");
        if (initialCompatibility != 1)
            throw new InvalidOperationException($"Preflight maintenance-compatibility test should be grantable before maintenance, but returned {initialCompatibility}.");

        await using SqlTransaction preflightTransaction = preflightConnection.BeginTransaction(IsolationLevel.ReadCommitted);
        int sharedResult;
        await using (SqlCommand shared = new(
            DatabaseLifecycleCoordinationContract.AcquirePreflightSharedTransactionLockSql,
            preflightConnection,
            preflightTransaction) { CommandTimeout = 15 })
        {
            object? value = await shared.ExecuteScalarAsync();
            sharedResult = value == null || value == DBNull.Value ? -999 : Convert.ToInt32(value);
        }
        if (sharedResult < 0)
            throw new InvalidOperationException($"Transaction-owned shared preflight lifecycle lease could not be acquired: {sharedResult}.");

        await using SqlConnection maintenanceConnection = new(connectionString);
        await maintenanceConnection.OpenAsync();
        int blockedExclusive = await ExecuteLockScalarAsync(maintenanceConnection, @"
DECLARE @r INT;
EXEC @r=sys.sp_getapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockMode=N'Exclusive',
    @LockOwner=N'Session',
    @LockTimeout=0;
SELECT @r;");
        if (blockedExclusive >= 0)
            throw new InvalidOperationException("Exclusive maintenance lifecycle lease was incorrectly granted while full preflight held its Shared Transaction lease.");

        // Transaction rollback is the release mechanism. There is no session-owned
        // preflight lock to leak into the connection pool.
        preflightTransaction.Rollback();

        int exclusiveResult = await ExecuteLockScalarAsync(maintenanceConnection, @"
DECLARE @r INT;
EXEC @r=sys.sp_getapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockMode=N'Exclusive',
    @LockOwner=N'Session',
    @LockTimeout=0;
SELECT @r;");
        if (exclusiveResult < 0)
            throw new InvalidOperationException($"Exclusive maintenance lifecycle lease could not be acquired after preflight cleanup: {exclusiveResult}.");

        await using SqlConnection blockedPreflightConnection = new(connectionString);
        await blockedPreflightConnection.OpenAsync();
        await using SqlTransaction blockedPreflightTransaction = blockedPreflightConnection.BeginTransaction(IsolationLevel.ReadCommitted);
        int blockedShared;
        await using (SqlCommand shared = new(
            DatabaseLifecycleCoordinationContract.AcquirePreflightSharedTransactionLockSql,
            blockedPreflightConnection,
            blockedPreflightTransaction) { CommandTimeout = 15 })
        {
            object? value = await shared.ExecuteScalarAsync();
            blockedShared = value == null || value == DBNull.Value ? -999 : Convert.ToInt32(value);
        }
        if (blockedShared != -1)
            throw new InvalidOperationException($"Full preflight should be denied while Database Maintenance holds the Exclusive lease, but returned {blockedShared}.");
        blockedPreflightTransaction.Rollback();

        int exclusiveRelease = await ExecuteLockScalarAsync(maintenanceConnection, @"
DECLARE @r INT;
EXEC @r=sys.sp_releaseapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockOwner=N'Session';
SELECT @r;");
        if (exclusiveRelease < 0)
            throw new InvalidOperationException($"Maintenance lifecycle lease release failed: {exclusiveRelease}.");

        await using SqlConnection retryConnection = new(connectionString);
        await retryConnection.OpenAsync();
        await using SqlTransaction retryTransaction = retryConnection.BeginTransaction(IsolationLevel.ReadCommitted);
        int retryShared;
        await using (SqlCommand shared = new(
            DatabaseLifecycleCoordinationContract.AcquirePreflightSharedTransactionLockSql,
            retryConnection,
            retryTransaction) { CommandTimeout = 15 })
        {
            object? value = await shared.ExecuteScalarAsync();
            retryShared = value == null || value == DBNull.Value ? -999 : Convert.ToInt32(value);
        }
        if (retryShared < 0)
            throw new InvalidOperationException($"Full preflight shared lease did not recover after maintenance release: {retryShared}.");
        retryTransaction.Rollback();

        int finalCompatibility = await ExecuteLockScalarAsync(preflightConnection, @"
SELECT APPLOCK_TEST(N'public', N'PharmaLIMS.SchemaMigration', N'Shared', N'Session');");
        if (finalCompatibility != 1)
            throw new InvalidOperationException($"Preflight maintenance-compatibility test should recover after maintenance release, but returned {finalCompatibility}.");

        Console.WriteLine("Database lifecycle Shared Transaction preflight / Exclusive maintenance coordination integration PASS.");
    }

    private static async Task VerifyComplianceProtectionTamperDetectionAsync(string connectionString)
    {
        const string expectedTrigger = "TRG_AuditTrail_AppendOnly";
        const string decoyTrigger = "TRG_v285_AuditTrail_Decoy";

        DataTable baseline = await QueryAsync(connectionString, ComplianceRecordProtectionContract.QuerySql);
        DataRow? baselineAudit = baseline.Rows.Cast<DataRow>()
            .FirstOrDefault(row => string.Equals(Convert.ToString(row["TableName"]), "AuditTrail", StringComparison.Ordinal));
        if (baselineAudit == null ||
            Convert.ToInt32(baselineAudit["TableExists"]) != 1 ||
            Convert.ToInt32(baselineAudit["HasExpectedEnabledTrigger"]) != 1 ||
            Convert.ToInt32(baselineAudit["ProtectsUpdate"]) != 1 ||
            Convert.ToInt32(baselineAudit["ProtectsDelete"]) != 1 ||
            Convert.ToInt32(baselineAudit["HasCanonicalAppendOnlyBody"]) != 1)
        {
            throw new InvalidOperationException("AuditTrail append-only protection did not satisfy the authoritative contract before tamper testing.");
        }

        try
        {
            await ExecuteAsync(connectionString, $@"
DISABLE TRIGGER dbo.[{expectedTrigger}] ON dbo.AuditTrail;
EXEC sys.sp_executesql N'
CREATE OR ALTER TRIGGER dbo.[{decoyTrigger}]
ON dbo.AuditTrail
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
END;';");

            DataTable tampered = await QueryAsync(connectionString, ComplianceRecordProtectionContract.QuerySql);
            DataRow? audit = tampered.Rows.Cast<DataRow>()
                .FirstOrDefault(row => string.Equals(Convert.ToString(row["TableName"]), "AuditTrail", StringComparison.Ordinal));
            if (audit == null)
                throw new InvalidOperationException("AuditTrail protection row disappeared during tamper test.");

            bool incorrectlyPassed =
                Convert.ToInt32(audit["HasExpectedEnabledTrigger"]) == 1 &&
                Convert.ToInt32(audit["ProtectsUpdate"]) == 1 &&
                Convert.ToInt32(audit["ProtectsDelete"]) == 1 &&
                Convert.ToInt32(audit["HasCanonicalAppendOnlyBody"]) == 1;
            if (incorrectlyPassed)
                throw new InvalidOperationException("A decoy trigger incorrectly satisfied the authoritative append-only protection contract.");
        }
        finally
        {
            await ExecuteAsync(connectionString, $@"
IF OBJECT_ID(N'dbo.{decoyTrigger}', N'TR') IS NOT NULL
    DROP TRIGGER dbo.[{decoyTrigger}];
ENABLE TRIGGER dbo.[{expectedTrigger}] ON dbo.AuditTrail;");
        }

        // F01: a protected table must remain represented by the contract even when the table name disappears.
        const string protectedTable = "CultureMediaPrintHistory";
        const string temporaryTable = "CultureMediaPrintHistory_v285_missing_test";
        try
        {
            await ExecuteAsync(connectionString, $"EXEC sys.sp_rename N'dbo.{protectedTable}', N'{temporaryTable}';");
            DataTable missingTableState = await QueryAsync(connectionString, ComplianceRecordProtectionContract.QuerySql);
            DataRow? missingTableRow = missingTableState.Rows.Cast<DataRow>()
                .FirstOrDefault(row => string.Equals(Convert.ToString(row["TableName"]), protectedTable, StringComparison.Ordinal));
            if (missingTableRow == null || Convert.ToInt32(missingTableRow["TableExists"]) != 0)
                throw new InvalidOperationException("A missing protected table was not reported as TableExists=0 by the authoritative protection contract.");
        }
        finally
        {
            await ExecuteAsync(connectionString, $@"
IF OBJECT_ID(N'dbo.{temporaryTable}', N'U') IS NOT NULL
    EXEC sys.sp_rename N'dbo.{temporaryTable}', N'{protectedTable}';");
        }

        // F02: structural tokens alone are not accepted as proof; behavior must reject real UPDATE and DELETE.
        string originalTriggerDefinition = Convert.ToString(await ScalarAsync(connectionString,
            "SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.TRG_AuditTrail_AppendOnly'));")) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(originalTriggerDefinition))
            throw new InvalidOperationException("Could not capture the controlled AuditTrail append-only trigger definition.");
        try
        {
            await ExecuteAsync(connectionString, @"
CREATE OR ALTER TRIGGER dbo.TRG_AuditTrail_AppendOnly
ON dbo.AuditTrail
AFTER UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    -- append-only and cannot be updated or deleted
    IF 1 = 0 THROW 59999, 'deceptive non-protective body', 1;
END;");

            DataTable deceptiveState = await QueryAsync(connectionString, ComplianceRecordProtectionContract.QuerySql);
            DataRow deceptiveRow = deceptiveState.Rows.Cast<DataRow>()
                .Single(row => string.Equals(Convert.ToString(row["TableName"]), "AuditTrail", StringComparison.Ordinal));
            if (Convert.ToInt32(deceptiveRow["HasCanonicalAppendOnlyBody"]) != 1)
                throw new InvalidOperationException("The deceptive-body regression fixture no longer exercises the structural false-positive case.");

            bool updateBlocked = false;
            bool deleteBlocked = false;
            await using SqlConnection behaviorConnection = new(connectionString);
            await behaviorConnection.OpenAsync();
            await using SqlTransaction tx = behaviorConnection.BeginTransaction();
            long auditId;
            await using (SqlCommand insert = new(@"
INSERT dbo.AuditTrail(TableName,RecordID,Action,ChangedBy,PerformedBy)
VALUES(N'v285-protection-test',-285,N'BehaviorTest',N'v285',N'v285');
SELECT CONVERT(bigint,SCOPE_IDENTITY());", behaviorConnection, tx))
                auditId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            try
            {
                await using SqlCommand update = new("UPDATE dbo.AuditTrail SET Action=N'BehaviorTestUpdated' WHERE AuditID=@id;", behaviorConnection, tx);
                update.Parameters.AddWithValue("@id", auditId);
                await update.ExecuteNonQueryAsync();
            }
            catch (SqlException) { updateBlocked = true; }
            try
            {
                await using SqlCommand delete = new("DELETE dbo.AuditTrail WHERE AuditID=@id;", behaviorConnection, tx);
                delete.Parameters.AddWithValue("@id", auditId);
                await delete.ExecuteNonQueryAsync();
            }
            catch (SqlException) { deleteBlocked = true; }
            tx.Rollback();
            if (updateBlocked || deleteBlocked)
                throw new InvalidOperationException("Deceptive trigger unexpectedly blocked DML; regression fixture is invalid.");
        }
        finally
        {
            await ExecuteAsync(
                connectionString,
                "DROP TRIGGER IF EXISTS dbo.TRG_AuditTrail_AppendOnly;");

            await ExecuteAsync(
                connectionString,
                originalTriggerDefinition);
        }

        bool protectedUpdateBlocked = false;
        bool protectedDeleteBlocked = false;

        await using (SqlConnection updateConnection = new(connectionString))
        {
            await updateConnection.OpenAsync();
            await using SqlTransaction tx = updateConnection.BeginTransaction();
            long auditId;
            await using (SqlCommand insert = new(@"
INSERT dbo.AuditTrail(TableName,RecordID,Action,ChangedBy,PerformedBy)
VALUES(N'v285-protection-test',-285,N'BehaviorTest',N'v285',N'v285');
SELECT CONVERT(bigint,SCOPE_IDENTITY());", updateConnection, tx))
                auditId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            try
            {
                await using SqlCommand update = new("UPDATE dbo.AuditTrail SET Action=N'BlockedUpdate' WHERE AuditID=@id;", updateConnection, tx);
                update.Parameters.AddWithValue("@id", auditId);
                await update.ExecuteNonQueryAsync();
            }
            catch (SqlException) { protectedUpdateBlocked = true; }
            try { tx.Rollback(); } catch (InvalidOperationException) { }
        }

        await using (SqlConnection deleteConnection = new(connectionString))
        {
            await deleteConnection.OpenAsync();
            await using SqlTransaction tx = deleteConnection.BeginTransaction();
            long auditId;
            await using (SqlCommand insert = new(@"
INSERT dbo.AuditTrail(TableName,RecordID,Action,ChangedBy,PerformedBy)
VALUES(N'v285-protection-test',-285,N'BehaviorTest',N'v285',N'v285');
SELECT CONVERT(bigint,SCOPE_IDENTITY());", deleteConnection, tx))
                auditId = Convert.ToInt64(await insert.ExecuteScalarAsync());
            try
            {
                await using SqlCommand delete = new("DELETE dbo.AuditTrail WHERE AuditID=@id;", deleteConnection, tx);
                delete.Parameters.AddWithValue("@id", auditId);
                await delete.ExecuteNonQueryAsync();
            }
            catch (SqlException) { protectedDeleteBlocked = true; }
            try { tx.Rollback(); } catch (InvalidOperationException) { }
        }

        if (!protectedUpdateBlocked || !protectedDeleteBlocked)
            throw new InvalidOperationException("Controlled AuditTrail append-only trigger did not behaviorally reject both UPDATE and DELETE.");

        // v286 F01: behaviorally prove every protected table rejects UPDATE and DELETE.
        // WHERE 1=0 deliberately avoids mutating data while still firing statement-level DML triggers.
        // The UPDATE column is selected dynamically and excludes identity/computed/rowversion columns.
        await VerifyAllProtectedTablesRejectDmlAsync(connectionString, baseline);

        DataTable restored = await QueryAsync(connectionString, ComplianceRecordProtectionContract.QuerySql);
        DataRow? restoredAudit = restored.Rows.Cast<DataRow>()
            .FirstOrDefault(row => string.Equals(Convert.ToString(row["TableName"]), "AuditTrail", StringComparison.Ordinal));
        if (restoredAudit == null || Convert.ToInt32(restoredAudit["HasExpectedEnabledTrigger"]) != 1)
            throw new InvalidOperationException("AuditTrail append-only trigger was not restored after tamper testing.");

        Console.WriteLine("Compliance append-only exact-trigger tamper/decoy integration PASS.");
    }

    private static async Task VerifyAllProtectedTablesRejectDmlAsync(string connectionString, DataTable protectionContract)
    {
        foreach (DataRow row in protectionContract.Rows)
        {
            string tableName = Convert.ToString(row["TableName"]) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tableName))
                throw new InvalidOperationException("Compliance protection contract returned a blank protected table name.");
            if (Convert.ToInt32(row["TableExists"]) != 1)
                throw new InvalidOperationException($"Protected table dbo.{tableName} is missing before behavioral protection verification.");

            string columnName = Convert.ToString(await ScalarAsync(connectionString, $@"
SELECT TOP (1) c.name
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(N'dbo.{tableName}', N'U')
  AND c.is_identity = 0
  AND c.is_computed = 0
  AND c.system_type_id <> 189
ORDER BY c.column_id;")) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(columnName))
                throw new InvalidOperationException($"Protected table dbo.{tableName} has no safe column available for behavioral UPDATE verification.");

            bool updateBlocked = false;
            bool deleteBlocked = false;
            await using SqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await using SqlTransaction tx = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            try
            {
                await using SqlCommand update = new(
                    $"UPDATE dbo.[{tableName}] SET [{columnName}]=[{columnName}] WHERE 1=0;",
                    connection,
                    tx);
                await update.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
                updateBlocked = true;
            }

            try
            {
                await using SqlCommand delete = new(
                    $"DELETE FROM dbo.[{tableName}] WHERE 1=0;",
                    connection,
                    tx);
                await delete.ExecuteNonQueryAsync();
            }
            catch (SqlException)
            {
                deleteBlocked = true;
            }
            finally
            {
                try { tx.Rollback(); } catch (InvalidOperationException) { }
            }

            if (!updateBlocked || !deleteBlocked)
                throw new InvalidOperationException(
                    $"Append-only behavioral gate failed for dbo.{tableName}: UPDATE blocked={updateBlocked}, DELETE blocked={deleteBlocked}.");
        }

        Console.WriteLine($"Compliance append-only behavioral verification PASS for {protectionContract.Rows.Count} protected tables.");
    }


    private static async Task VerifyUserAdministrationSignatureEvidenceSchemaAsync(string connectionString)
    {
        object? contract = await ScalarAsync(connectionString, @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.UserAdministrationSignatures', N'U') IS NOT NULL
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'SignatureID' AND system_type_id=TYPE_ID(N'bigint') AND max_length=8 AND is_nullable=0 AND is_identity=1)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'TargetUserID' AND system_type_id=TYPE_ID(N'int') AND max_length=4 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'TargetUsername' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=200 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'ActionType' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=200 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'ActionReason' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=2000 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'MeaningOfSignature' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=510 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'SignedBy' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=200 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'UserRole' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=200 AND is_nullable=1)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'SignedAt' AND system_type_id=TYPE_ID(N'datetime2') AND max_length=6 AND scale=0 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'SourceWorkstation' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=400 AND is_nullable=1)
    AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'SourceApplication' AND system_type_id=TYPE_ID(N'nvarchar') AND max_length=200 AND is_nullable=0)
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'PK_UserAdministrationSignatures' AND is_primary_key=1 AND is_unique=1 AND is_disabled=0)
    AND EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys fk
        INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
        INNER JOIN sys.columns pc ON pc.object_id=fkc.parent_object_id AND pc.column_id=fkc.parent_column_id
        INNER JOIN sys.columns rc ON rc.object_id=fkc.referenced_object_id AND rc.column_id=fkc.referenced_column_id
        WHERE fk.parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
          AND fk.name=N'FK_UserAdministrationSignatures_TargetUser'
          AND fk.referenced_object_id=OBJECT_ID(N'dbo.Users')
          AND pc.name=N'TargetUserID' AND rc.name=N'UserID'
          AND fk.is_disabled=0 AND fk.is_not_trusted=0
    )
    AND 3=(SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name IN (N'CK_UserAdministrationSignatures_ActionReason_Specific',N'CK_UserAdministrationSignatures_Meaning_NotBlank',N'CK_UserAdministrationSignatures_SignedBy_NotBlank') AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND c.name=N'SignedAt' AND UPPER(dc.definition) LIKE N'%SYSUTCDATETIME%')
    AND EXISTS (SELECT 1 FROM sys.default_constraints dc INNER JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id WHERE dc.parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND c.name=N'SourceApplication' AND UPPER(dc.definition) LIKE N'%PHARMALIMS%')
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'IX_UserAdministrationSignatures_Target_SignedAt' AND is_disabled=0)
    AND EXISTS (SELECT 1 FROM sys.triggers WHERE parent_id=OBJECT_ID(N'dbo.UserAdministrationSignatures') AND name=N'TRG_UserAdministrationSignatures_AppendOnly' AND is_disabled=0)
THEN 1 ELSE 0 END;");

        if (contract == null || contract == DBNull.Value || Convert.ToInt32(contract) != 1)
            throw new InvalidOperationException("UserAdministrationSignatures exact schema contract failed after controlled migrations.");

        Console.WriteLine("User administration signature exact-schema integration PASS.");
    }

    private static async Task<int> ExecuteLockScalarAsync(SqlConnection connection, string sql)
    {
        await using SqlCommand command = new(sql, connection) { CommandTimeout = 15 };
        object? result = await command.ExecuteScalarAsync();
        return result == null || result == DBNull.Value ? -999 : Convert.ToInt32(result);
    }

    private static async Task<DataTable> QueryAsync(string connectionString, string sql, int timeoutSeconds = 60)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = timeoutSeconds };
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, int timeoutSeconds = 60)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = timeoutSeconds };
        await command.ExecuteNonQueryAsync();
    }

    private static string? FindProjectRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            DirectoryInfo? directory = new(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Database", "MigrationManifest.json")) &&
                    File.Exists(Path.Combine(directory.FullName, "PharmaLIMS.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return null;
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = 60 };
        return await command.ExecuteScalarAsync();
    }

}
