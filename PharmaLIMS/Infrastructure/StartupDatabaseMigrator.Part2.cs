using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PharmaLIMS.Infrastructure
{
    public sealed partial class StartupDatabaseMigrator
    {
        private async Task<bool> ProvisionFreshDatabaseBaselineAsync()
        {
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "Database", "MigrationManifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("The controlled database migration manifest is missing.", manifestPath);

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("freshInstallBaseline", out JsonElement baseline) || baseline.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The controlled fresh-install baseline entry is missing from MigrationManifest.json.");

            string baselineVersionKey = baseline.GetProperty("versionKey").GetString() ?? string.Empty;
            string baselineDescription = baseline.GetProperty("description").GetString() ?? baselineVersionKey;
            string relativeFile = baseline.GetProperty("file").GetString() ?? string.Empty;
            string expectedHash = (baseline.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant();
            string applicationVersion = root.TryGetProperty("applicationVersion", out JsonElement versionElement)
                ? versionElement.GetString() ?? string.Empty
                : string.Empty;

            string baselinePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", relativeFile));
            string baselineRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", "Baseline")) + Path.DirectorySeparatorChar;
            if (string.IsNullOrWhiteSpace(baselineVersionKey) ||
                string.IsNullOrWhiteSpace(relativeFile) ||
                string.IsNullOrWhiteSpace(expectedHash) ||
                !baselinePath.StartsWith(baselineRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(baselinePath))
            {
                throw new InvalidOperationException("The fresh-install baseline manifest entry is invalid or its SQL file is missing.");
            }

            byte[] baselineBytes = await File.ReadAllBytesAsync(baselinePath).ConfigureAwait(false);
            string actualHash = Convert.ToHexString(SHA256.HashData(baselineBytes)).ToLowerInvariant();
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fresh-install baseline checksum mismatch: " + relativeFile);

            bool controlledFreshBaselinePresent = false;
            await _database.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                await ConfigureMigrationLockTimeoutAsync(connection, transaction).ConfigureAwait(false);
                await EnsureConnectedDatabaseAsync(connection, transaction).ConfigureAwait(false);

                const string stateSql = @"
SELECT
    (CASE WHEN OBJECT_ID(N'dbo.Users',N'U') IS NULL THEN 0 ELSE 1 END
     + CASE WHEN OBJECT_ID(N'dbo.Samples',N'U') IS NULL THEN 0 ELSE 1 END
     + CASE WHEN OBJECT_ID(N'dbo.Tests',N'U') IS NULL THEN 0 ELSE 1 END
     + CASE WHEN OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL THEN 0 ELSE 1 END
     + CASE WHEN OBJECT_ID(N'dbo.CultureMediaLots',N'U') IS NULL THEN 0 ELSE 1 END) AS OperationalObjectCount,
    (SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped=0) AS UserTableCount,
    CASE WHEN OBJECT_ID(N'dbo.LIMS_SchemaVersions',N'U') IS NULL THEN 0 ELSE 1 END AS HasLedger;";

                int operationalObjectCount;
                int userTableCount;
                bool hasLedger;
                await using (SqlCommand state = new SqlCommand(stateSql, connection, transaction))
                {
                    state.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    await using SqlDataReader reader = await state.ExecuteReaderAsync().ConfigureAwait(false);
                    await reader.ReadAsync().ConfigureAwait(false);
                    operationalObjectCount = reader.GetInt32(0);
                    userTableCount = reader.GetInt32(1);
                    hasLedger = reader.GetInt32(2) == 1;
                }

                if (userTableCount == 0)
                {
                    ApplicationLogger.Information($"Applying controlled fresh-install baseline '{baselineVersionKey}'.");
                    string baselineSql = System.Text.Encoding.UTF8.GetString(baselineBytes).TrimStart('\uFEFF');
                    await using (SqlCommand apply = new SqlCommand(baselineSql, connection, transaction))
                    {
                        apply.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 300);
                        await apply.ExecuteNonQueryAsync().ConfigureAwait(false);
                    }

                    await using SqlCommand record = new SqlCommand(@"
INSERT dbo.LIMS_SchemaVersions
    (VersionKey,Description,MigrationChecksum,ApplicationVersion)
VALUES
    (@VersionKey,@Description,@Checksum,@ApplicationVersion);", connection, transaction);
                    record.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    record.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = baselineVersionKey;
                    record.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value = baselineDescription;
                    record.Parameters.Add("@Checksum", SqlDbType.NVarChar, 128).Value = expectedHash;
                    record.Parameters.Add("@ApplicationVersion", SqlDbType.NVarChar, 50).Value = applicationVersion;
                    await record.ExecuteNonQueryAsync().ConfigureAwait(false);

                    await using SqlCommand verifyRecordedBaseline = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.LIMS_SchemaVersions WITH (UPDLOCK,HOLDLOCK)
WHERE VersionKey=@VersionKey
  AND MigrationChecksum=@Checksum;", connection, transaction);
                    verifyRecordedBaseline.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    verifyRecordedBaseline.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = baselineVersionKey;
                    verifyRecordedBaseline.Parameters.Add("@Checksum", SqlDbType.NVarChar, 128).Value = expectedHash;
                    if (Convert.ToInt32(await verifyRecordedBaseline.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("The fresh-install baseline ledger record was not created with the expected checksum.");

                    controlledFreshBaselinePresent = true;
                    return;
                }

                if (operationalObjectCount != 5 || !hasLedger)
                {
                    throw new InvalidOperationException(
                        "The target database contains a partial or uncontrolled PharmaLIMS schema. " +
                        "Fresh baseline provisioning is allowed only for an empty database; legacy databases require controlled reconciliation.");
                }

                bool baselineRowExists;
                string? recordedBaselineHash;
                await using (SqlCommand verifyBaseline = new SqlCommand(@"
SELECT COUNT(1),MAX(MigrationChecksum)
FROM dbo.LIMS_SchemaVersions WITH (UPDLOCK,HOLDLOCK)
WHERE VersionKey=@VersionKey;", connection, transaction))
                {
                    verifyBaseline.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    verifyBaseline.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = baselineVersionKey;
                    await using SqlDataReader reader = await verifyBaseline.ExecuteReaderAsync().ConfigureAwait(false);
                    await reader.ReadAsync().ConfigureAwait(false);
                    baselineRowExists = reader.GetInt32(0) > 0;
                    recordedBaselineHash = reader.IsDBNull(1) ? null : reader.GetString(1);
                }

                if (!baselineRowExists)
                    return;

                if (string.IsNullOrWhiteSpace(recordedBaselineHash) ||
                    !recordedBaselineHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Recorded fresh-install baseline checksum does not match the controlled manifest. " +
                        "Do not continue without controlled reconciliation.");
                }

                // A prior first-run may have committed the baseline and then
                // rolled back the all-migrations transaction. Treat the exact
                // baseline record as a safe, resumable fresh-install state.
                controlledFreshBaselinePresent = true;
            }).ConfigureAwait(false);

            return controlledFreshBaselinePresent;
        }


        private static async Task EnsureWaterPlanningAsync(SqlConnection connection, SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.Water_Plans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Water_Plans
    (
        WaterPlanID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_Plans PRIMARY KEY,
        PlanNo NVARCHAR(50) NOT NULL,
        WaterType NVARCHAR(30) NOT NULL,
        SourceType NVARCHAR(20) NOT NULL,
        Frequency NVARCHAR(30) NULL,
        DueDate DATE NOT NULL,
        RequiredDate DATE NOT NULL,
        AnalysisProfile NVARCHAR(40) NOT NULL,
        Status NVARCHAR(40) NOT NULL CONSTRAINT DF_Water_Plans_Status DEFAULT N'Planned',
        Notes NVARCHAR(MAX) NULL,
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_Plans_CreatedAt DEFAULT SYSDATETIME(),
        DistributedBy NVARCHAR(100) NULL,
        DistributedAt DATETIME2(0) NULL,
        CancelledBy NVARCHAR(100) NULL,
        CancelledAt DATETIME2(0) NULL,
        CancellationReason NVARCHAR(MAX) NULL
    );
    CREATE UNIQUE INDEX UX_Water_Plans_PlanNo ON dbo.Water_Plans(PlanNo);
END;

IF OBJECT_ID(N'dbo.Water_PlanSamples', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Water_PlanSamples
    (
        WaterPlanSampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_PlanSamples PRIMARY KEY,
        WaterPlanID INT NOT NULL,
        PointID INT NOT NULL,
        PointCode NVARCHAR(50) NOT NULL,
        PointName NVARCHAR(200) NULL,
        Location NVARCHAR(300) NULL,
        Status NVARCHAR(40) NOT NULL CONSTRAINT DF_Water_PlanSamples_Status DEFAULT N'Planned',
        SampleID INT NULL,
        SampleNumber NVARCHAR(50) NULL,
        RegisteredAt DATETIME2(0) NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSamples_CreatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_Water_PlanSamples_Plans FOREIGN KEY(WaterPlanID) REFERENCES dbo.Water_Plans(WaterPlanID),
        CONSTRAINT UQ_Water_PlanSamples_PlanPoint UNIQUE(WaterPlanID, PointID)
    );
    CREATE INDEX IX_Water_PlanSamples_Status ON dbo.Water_PlanSamples(WaterPlanID,Status);
END;

IF COL_LENGTH(N'dbo.Water_PlanSamples', N'AnalysisProfile') IS NULL
    ALTER TABLE dbo.Water_PlanSamples ADD AnalysisProfile NVARCHAR(40) NULL;

IF OBJECT_ID(N'dbo.Water_PlanSampleTests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Water_PlanSampleTests
    (
        WaterPlanSampleID INT NOT NULL,
        TestID INT NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSampleTests_CreatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT PK_Water_PlanSampleTests PRIMARY KEY(WaterPlanSampleID,TestID),
        CONSTRAINT FK_Water_PlanSampleTests_Samples FOREIGN KEY(WaterPlanSampleID) REFERENCES dbo.Water_PlanSamples(WaterPlanSampleID) ON DELETE CASCADE
    );
END;

IF OBJECT_ID(N'dbo.Water_PlanSampleAttempts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Water_PlanSampleAttempts
    (
        AttemptID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_PlanSampleAttempts PRIMARY KEY,
        WaterPlanSampleID INT NOT NULL,
        SampleID INT NOT NULL,
        SampleNumber NVARCHAR(50) NOT NULL,
        Outcome NVARCHAR(40) NOT NULL,
        LinkedBy NVARCHAR(100) NOT NULL,
        LinkedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSampleAttempts_LinkedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_Water_PlanSampleAttempts_PlanSample FOREIGN KEY(WaterPlanSampleID) REFERENCES dbo.Water_PlanSamples(WaterPlanSampleID)
    );
END;

IF OBJECT_ID(N'dbo.Water_PlanSignatures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Water_PlanSignatures
    (
        SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Water_PlanSignatures PRIMARY KEY,
        WaterPlanID INT NOT NULL,
        ActionType NVARCHAR(100) NOT NULL,
        ActionReason NVARCHAR(MAX) NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        MeaningOfSignature NVARCHAR(255) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_Water_PlanSignatures_SignedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_Water_PlanSignatures_Plans FOREIGN KEY(WaterPlanID) REFERENCES dbo.Water_Plans(WaterPlanID)
    );
END;

IF COL_LENGTH(N'dbo.Samples',N'PointCodeSnapshot') IS NULL
    ALTER TABLE dbo.Samples ADD PointCodeSnapshot NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.Samples',N'PointNameSnapshot') IS NULL
    ALTER TABLE dbo.Samples ADD PointNameSnapshot NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.Samples',N'PointLocationSnapshot') IS NULL
    ALTER TABLE dbo.Samples ADD PointLocationSnapshot NVARCHAR(300) NULL;

IF COL_LENGTH(N'dbo.Samples',N'ReceivedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD ReceivedDateTime DATETIME NULL;
IF COL_LENGTH(N'dbo.Samples',N'AnalysisStartedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD AnalysisStartedDateTime DATETIME NULL;
IF COL_LENGTH(N'dbo.Samples',N'AnalysisCompletedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD AnalysisCompletedDateTime DATETIME NULL;
IF COL_LENGTH(N'dbo.Samples',N'IncubationStartedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD IncubationStartedDateTime DATETIME NULL;
IF COL_LENGTH(N'dbo.Samples',N'IncubationCompletedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD IncubationCompletedDateTime DATETIME NULL;
IF COL_LENGTH(N'dbo.Samples',N'ReviewedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD ReviewedDateTime DATETIME NULL;
IF COL_LENGTH(N'dbo.Samples',N'ApprovedDateTime') IS NULL
    ALTER TABLE dbo.Samples ADD ApprovedDateTime DATETIME NULL;

IF COL_LENGTH(N'dbo.SampleTests',N'TestNameSnapshot') IS NULL
    ALTER TABLE dbo.SampleTests ADD TestNameSnapshot NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'TestCategorySnapshot') IS NULL
    ALTER TABLE dbo.SampleTests ADD TestCategorySnapshot NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'UnitSnapshot') IS NULL
    ALTER TABLE dbo.SampleTests ADD UnitSnapshot NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'AlertLimitSnapshot') IS NULL
    ALTER TABLE dbo.SampleTests ADD AlertLimitSnapshot DECIMAL(18,4) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'ActionLimitSnapshot') IS NULL
    ALTER TABLE dbo.SampleTests ADD ActionLimitSnapshot DECIMAL(18,4) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'ResultStatus') IS NULL
    ALTER TABLE dbo.SampleTests ADD ResultStatus NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'DeviationType') IS NULL
    ALTER TABLE dbo.SampleTests ADD DeviationType NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.SampleTests',N'LimitDescription') IS NULL
    ALTER TABLE dbo.SampleTests ADD LimitDescription NVARCHAR(300) NULL;

EXEC sys.sp_executesql N'
UPDATE st
SET TestNameSnapshot=COALESCE(st.TestNameSnapshot,t.TestName),
    TestCategorySnapshot=COALESCE(st.TestCategorySnapshot,t.TestCategory),
    UnitSnapshot=COALESCE(st.UnitSnapshot,t.Unit),
    AlertLimitSnapshot=COALESCE(st.AlertLimitSnapshot,t.AlertLimit),
    ActionLimitSnapshot=COALESCE(st.ActionLimitSnapshot,t.ActionLimit)
FROM dbo.SampleTests st INNER JOIN dbo.Tests t ON t.TestID=st.TestID
WHERE st.TestNameSnapshot IS NULL OR st.TestCategorySnapshot IS NULL OR st.UnitSnapshot IS NULL;';

EXEC sys.sp_executesql N'
UPDATE s
SET PointCodeSnapshot=COALESCE(s.PointCodeSnapshot,w.PointCode),
    PointNameSnapshot=COALESCE(s.PointNameSnapshot,w.PointName),
    PointLocationSnapshot=COALESCE(s.PointLocationSnapshot,w.Location)
FROM dbo.Samples s INNER JOIN dbo.WaterSamplingPoints w ON w.Id=s.PointID
WHERE s.PointCodeSnapshot IS NULL OR s.PointNameSnapshot IS NULL OR s.PointLocationSnapshot IS NULL;';";
            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureSchemaVersioningAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LIMS_SchemaVersions
    (
        VersionKey NVARCHAR(100) NOT NULL CONSTRAINT PK_LIMS_SchemaVersions PRIMARY KEY,
        Description NVARCHAR(500) NOT NULL,
        MigrationChecksum NVARCHAR(128) NULL,
        ApplicationVersion NVARCHAR(50) NULL,
        AppliedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedAt DEFAULT (SYSDATETIME()),
        AppliedBy NVARCHAR(128) NOT NULL CONSTRAINT DF_LIMS_SchemaVersions_AppliedBy DEFAULT (SUSER_SNAME())
    );
END;
IF COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'MigrationChecksum') IS NULL ALTER TABLE dbo.LIMS_SchemaVersions ADD MigrationChecksum NVARCHAR(128) NULL;
IF COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'ApplicationVersion') IS NULL ALTER TABLE dbo.LIMS_SchemaVersions ADD ApplicationVersion NVARCHAR(50) NULL;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task RecordCurrentSchemaVersionAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260718_001')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260718_001', N'Release hardening: global transactional EM numbering, legacy booth-template cleanup, and schema version tracking.');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260720_001')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260720_001', N'Water workflow timestamps, immutable result disposition fields, and snapshot migration ordering.');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260720_002')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260720_002', N'Culture media gram-based inventory balances and preparation stock transaction ledger.');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260721_001')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260721_001', N'Global EM compliance controls and prepared-media traceability.');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260721_002')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260721_002', N'Culture media global compliance, stock reconciliation, release identity and print history.');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260722_002')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description, ApplicationVersion) VALUES (N'20260722_002', N'Full compliance hardening', N'2026.7.22.40');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260722_001')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260722_001', N'Startup migration compile-safety repair for newly added EM and culture-media columns.');
END;

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260722_003')
BEGIN
    INSERT INTO dbo.LIMS_SchemaVersions (VersionKey, Description, ApplicationVersion)
    VALUES (N'20260722_003', N'Project complexity resolution: controlled EM schedules and incubation, atomic certificate snapshots, water timestamps, and workflow hardening.', N'2026.7.22.45');
END;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureCertificateComplianceAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.Certificates',N'U') IS NULL
    THROW 51070, 'Required table dbo.Certificates does not exist.', 1;

IF COL_LENGTH(N'dbo.Certificates',N'RevisionNo') IS NULL
    ALTER TABLE dbo.Certificates ADD RevisionNo INT NOT NULL CONSTRAINT DF_Certificates_RevisionNo DEFAULT(0);
IF COL_LENGTH(N'dbo.Certificates',N'IsCancelled') IS NULL
    ALTER TABLE dbo.Certificates ADD IsCancelled BIT NOT NULL CONSTRAINT DF_Certificates_IsCancelled DEFAULT(0);
IF COL_LENGTH(N'dbo.Certificates',N'VerificationCode') IS NULL
    ALTER TABLE dbo.Certificates ADD VerificationCode NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.Certificates',N'ReportHash') IS NULL
    ALTER TABLE dbo.Certificates ADD ReportHash NVARCHAR(128) NULL;
IF COL_LENGTH(N'dbo.Certificates',N'CertificateStatus') IS NULL
    ALTER TABLE dbo.Certificates ADD CertificateStatus NVARCHAR(40) NULL;
IF COL_LENGTH(N'dbo.Certificates',N'ReissuedFromCertificateID') IS NULL
    ALTER TABLE dbo.Certificates ADD ReissuedFromCertificateID INT NULL;

IF OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CertificateDocumentSnapshots
    (
        SnapshotID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CertificateDocumentSnapshots PRIMARY KEY,
        CertificateID INT NOT NULL,
        SampleID INT NOT NULL,
        CertificateNumber NVARCHAR(50) NOT NULL,
        SnapshotContent NVARCHAR(MAX) NOT NULL,
        SnapshotHash NVARCHAR(128) NOT NULL,
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL,
        CONSTRAINT FK_CertificateDocumentSnapshots_Certificate
            FOREIGN KEY(CertificateID) REFERENCES dbo.Certificates(CertificateID),
        CONSTRAINT UQ_CertificateDocumentSnapshots_Certificate UNIQUE(CertificateID)
    );
    CREATE INDEX IX_CertificateDocumentSnapshots_Sample
        ON dbo.CertificateDocumentSnapshots(SampleID,CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.CertificateLifecycleAudit',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CertificateLifecycleAudit
    (
        AuditID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CertificateLifecycleAudit PRIMARY KEY,
        CertificateID INT NOT NULL,
        SampleID INT NULL,
        CertificateNumber NVARCHAR(50) NOT NULL,
        ActionType NVARCHAR(100) NOT NULL,
        ActionName NVARCHAR(100) NULL,
        OldStatus NVARCHAR(40) NULL,
        NewStatus NVARCHAR(40) NULL,
        Reason NVARCHAR(MAX) NULL,
        PerformedBy NVARCHAR(100) NOT NULL,
        PerformedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CertificateLifecycleAudit_PerformedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_CertificateLifecycleAudit_Certificate FOREIGN KEY(CertificateID) REFERENCES dbo.Certificates(CertificateID)
    );
END
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'SampleID') IS NULL
        ALTER TABLE dbo.CertificateLifecycleAudit ADD SampleID INT NULL;
    IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'ActionType') IS NULL
        ALTER TABLE dbo.CertificateLifecycleAudit ADD ActionType NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'ActionName') IS NULL
        ALTER TABLE dbo.CertificateLifecycleAudit ADD ActionName NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.CertificateLifecycleAudit',N'PerformedAt') IS NULL
        ALTER TABLE dbo.CertificateLifecycleAudit ADD PerformedAt DATETIME2(0) NULL;
END;

EXEC sys.sp_executesql N'
UPDATE dbo.Certificates
SET CertificateStatus = COALESCE(NULLIF(LTRIM(RTRIM(CertificateStatus)),N''''),
                                CASE WHEN ISNULL(IsCancelled,0)=1 THEN N''Cancelled'' ELSE N''Active'' END),
    VerificationCode = COALESCE(NULLIF(LTRIM(RTRIM(VerificationCode)),N''''), CertificateNumber)
WHERE CertificateStatus IS NULL OR LTRIM(RTRIM(CertificateStatus))=N''''
   OR VerificationCode IS NULL OR LTRIM(RTRIM(VerificationCode))=N'''';';

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.Certificates') AND name=N'UX_Certificates_ActiveSample'
)
AND NOT EXISTS
(
    SELECT SampleID
    FROM dbo.Certificates
    WHERE SampleID IS NOT NULL AND IsCancelled=0 AND CertificateStatus=N'Active'
    GROUP BY SampleID
    HAVING COUNT(*)>1
)
BEGIN
    EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_Certificates_ActiveSample ON dbo.Certificates(SampleID) WHERE IsCancelled=0 AND CertificateStatus=N''Active'';';
END;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task VerifyGlobalComplianceSchemaAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.CultureMediaStockTransactions', N'U') IS NULL
    THROW 51060, 'Required table dbo.CultureMediaStockTransactions was not created.', 1;
IF COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'BalanceBeforeG') IS NULL
    THROW 51061, 'Required column dbo.CultureMediaStockTransactions.BalanceBeforeG was not created.', 1;
IF OBJECT_ID(N'dbo.EM_PlanSamples', N'U') IS NULL
    THROW 51062, 'Required table dbo.EM_PlanSamples does not exist.', 1;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ReviewedBy') IS NULL
    THROW 51063, 'Required column dbo.EM_Schedules.ReviewedBy was not created.', 1;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ApprovedBy') IS NULL
    THROW 51064, 'Required column dbo.EM_Schedules.ApprovedBy was not created.', 1;
IF COL_LENGTH(N'dbo.EM_Schedules', N'MediaPreparationID') IS NULL
    THROW 51065, 'Required column dbo.EM_Schedules.MediaPreparationID was not created.', 1;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ApprovedPointCount') IS NULL
    THROW 51066, 'Required column dbo.EM_Schedules.ApprovedPointCount was not created.', 1;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'MediaPreparationID') IS NULL
    THROW 51067, 'Required column dbo.EM_PlanSamples.MediaPreparationID was not created.', 1;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2CompletedAt') IS NULL
    THROW 51068, 'Required EM incubation phase fields were not created.', 1;
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL OR COL_LENGTH(N'dbo.EM_Events', N'MediaPreparationID') IS NULL
    THROW 51069, 'Required EM event prepared-media traceability was not created.', 1;
IF OBJECT_ID(N'dbo.CertificateDocumentSnapshots', N'U') IS NULL
    THROW 51070, 'Required immutable certificate snapshot table was not created.', 1;
IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NULL
    THROW 51073, 'Required immutable PRM certificate snapshot table was not created.', 1;
IF COL_LENGTH(N'dbo.Samples', N'IncubationStartedDateTime') IS NULL
    THROW 51071, 'Required water incubation timestamp was not created.', 1;
IF OBJECT_ID(N'dbo.EM_ScheduleSignatures', N'U') IS NULL
    THROW 51072, 'Required EM schedule signature table was not created.', 1;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureEnvironmentalMonitoringPlanningAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.EM_Plans', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_Plans
    (
        PlanID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Plans PRIMARY KEY,
        PlanNo NVARCHAR(50) NOT NULL,
        PlanType NVARCHAR(30) NOT NULL,
        SourceType NVARCHAR(20) NOT NULL,
        LoginDate DATE NOT NULL,
        SampleDueDate DATE NOT NULL,
        RequiredDate DATE NOT NULL,
        Status NVARCHAR(40) NOT NULL CONSTRAINT DF_EM_Plans_Status DEFAULT N'Draft',
        BatchName NVARCHAR(150) NULL,
        BatchNo NVARCHAR(100) NULL,
        InspectionLotNo NVARCHAR(100) NULL,
        SetNo NVARCHAR(50) NULL,
        PlanNotes NVARCHAR(MAX) NULL,
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Plans_CreatedAt DEFAULT SYSDATETIME(),
        DistributedBy NVARCHAR(100) NULL,
        DistributedAt DATETIME2(0) NULL,
        CollectedBy NVARCHAR(100) NULL,
        CollectedAt DATETIME2(0) NULL,
        IncubatedBy NVARCHAR(100) NULL,
        IncubatedAt DATETIME2(0) NULL,
        CancelledBy NVARCHAR(100) NULL,
        CancelledAt DATETIME2(0) NULL,
        CancellationReason NVARCHAR(500) NULL,
        CONSTRAINT UQ_EM_Plans_PlanNo UNIQUE (PlanNo),
        CONSTRAINT CK_EM_Plans_Dates CHECK (RequiredDate >= SampleDueDate AND SampleDueDate >= LoginDate)
    );
END;

IF OBJECT_ID(N'dbo.EM_Schedules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_Schedules
    (
        ScheduleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Schedules PRIMARY KEY,
        ScheduleName NVARCHAR(150) NOT NULL,
        PlanType NVARCHAR(30) NOT NULL,
        Frequency NVARCHAR(20) NOT NULL,
        NextDueDate DATE NOT NULL,
        DaysAhead INT NOT NULL CONSTRAINT DF_EM_Schedules_DaysAhead DEFAULT (0),
        IsActive BIT NOT NULL CONSTRAINT DF_EM_Schedules_Active DEFAULT (1),
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Schedules_CreatedAt DEFAULT SYSDATETIME()
    );
END;

IF COL_LENGTH(N'dbo.EM_Schedules', N'Method') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD Method NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'SamplingLocation') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD SamplingLocation NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'MediaUsed') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD MediaUsed NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'MediaLotNo') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD MediaLotNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'IncludeNegativeControl') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD IncludeNegativeControl BIT NOT NULL CONSTRAINT DF_EM_Schedules_Negative DEFAULT (1);
IF COL_LENGTH(N'dbo.EM_Schedules', N'EmployeeID') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD EmployeeID NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'EmployeeName') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD EmployeeName NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'LastGeneratedDate') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD LastGeneratedDate DATE NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ApprovedPointCount') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovedPointCount INT NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ReviewedBy') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ReviewedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ReviewedAt') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ReviewedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ApprovedBy') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ApprovedAt') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'MediaPreparationID') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD MediaPreparationID INT NULL;

IF OBJECT_ID(N'dbo.EM_ScheduleAreas', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_ScheduleAreas
    (
        ScheduleAreaID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_ScheduleAreas PRIMARY KEY,
        ScheduleID INT NOT NULL,
        AreaID INT NOT NULL,
        CONSTRAINT FK_EM_ScheduleAreas_Schedules FOREIGN KEY (ScheduleID) REFERENCES dbo.EM_Schedules(ScheduleID),
        CONSTRAINT UQ_EM_ScheduleAreas UNIQUE (ScheduleID, AreaID)
    );
END;

IF OBJECT_ID(N'dbo.EM_PlanSamples', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_PlanSamples
    (
        PlanSampleID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_PlanSamples PRIMARY KEY,
        PlanID INT NOT NULL,
        AreaID INT NULL,
        Method NVARCHAR(100) NOT NULL,
        SamplingLocation NVARCHAR(200) NULL,
        SampleCode NVARCHAR(80) NOT NULL,
        IsNegativeControl BIT NOT NULL CONSTRAINT DF_EM_PlanSamples_Negative DEFAULT (0),
        EmployeeID NVARCHAR(50) NULL,
        EmployeeName NVARCHAR(150) NULL,
        MediaUsed NVARCHAR(150) NULL,
        MediaLotNo NVARCHAR(100) NULL,
        Status NVARCHAR(40) NOT NULL CONSTRAINT DF_EM_PlanSamples_Status DEFAULT N'Planned',
        PlateCondition NVARCHAR(100) NULL,
        KitCondition NVARCHAR(100) NULL,
        SamplingStart DATETIME2(0) NULL,
        SamplingEnd DATETIME2(0) NULL,
        TransportMinC DECIMAL(5,2) NULL,
        TransportMaxC DECIMAL(5,2) NULL,
        IncubationStart DATETIME2(0) NULL,
        IncubationEnd DATETIME2(0) NULL,
        BacteriaIncubatorID NVARCHAR(50) NULL,
        FungiIncubatorID NVARCHAR(50) NULL,
        NegativeControlResult NVARCHAR(50) NULL,
        EventID INT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_PlanSamples_CreatedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_EM_PlanSamples_Plans FOREIGN KEY (PlanID) REFERENCES dbo.EM_Plans(PlanID),
        CONSTRAINT UQ_EM_PlanSamples_Code UNIQUE (PlanID, SampleCode)
    );
END;

IF OBJECT_ID(N'dbo.EM_PlanSignatures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_PlanSignatures
    (
        SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_PlanSignatures PRIMARY KEY,
        PlanID INT NOT NULL,
        ActionType NVARCHAR(60) NOT NULL,
        ActionReason NVARCHAR(500) NOT NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        MeaningOfSignature NVARCHAR(255) NOT NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_PlanSignatures_SignedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_EM_PlanSignatures_Plans FOREIGN KEY (PlanID) REFERENCES dbo.EM_Plans(PlanID)
    );
END;

IF OBJECT_ID(N'dbo.EM_ScheduleSignatures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_ScheduleSignatures
    (
        SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_ScheduleSignatures PRIMARY KEY,
        ScheduleID INT NOT NULL,
        ActionType NVARCHAR(100) NOT NULL,
        ActionReason NVARCHAR(MAX) NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        MeaningOfSignature NVARCHAR(255) NOT NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_ScheduleSignatures_SignedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_EM_ScheduleSignatures_Schedules FOREIGN KEY(ScheduleID) REFERENCES dbo.EM_Schedules(ScheduleID)
    );
    CREATE INDEX IX_EM_ScheduleSignatures_Schedule ON dbo.EM_ScheduleSignatures(ScheduleID,SignedAt DESC);
END;

IF COL_LENGTH(N'dbo.EM_Events', N'PlanID') IS NULL
    ALTER TABLE dbo.EM_Events ADD PlanID INT NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'NegativeControlResult') IS NULL
    ALTER TABLE dbo.EM_Events ADD NegativeControlResult NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'PlanSampleID') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD PlanSampleID INT NULL;
IF COL_LENGTH(N'dbo.EM_Plans', N'ScheduleID') IS NULL
    ALTER TABLE dbo.EM_Plans ADD ScheduleID INT NULL;
IF COL_LENGTH(N'dbo.EM_Plans', N'CompletedBy') IS NULL
    ALTER TABLE dbo.EM_Plans ADD CompletedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Plans', N'CompletedAt') IS NULL
    ALTER TABLE dbo.EM_Plans ADD CompletedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_Schedules', N'ApprovalStatus') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_EM_Schedules_ApprovalStatus DEFAULT N'Draft';
IF COL_LENGTH(N'dbo.EM_Schedules', N'VersionNo') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD VersionNo INT NOT NULL CONSTRAINT DF_EM_Schedules_VersionNo DEFAULT(1);
IF COL_LENGTH(N'dbo.EM_Schedules', N'EffectiveFrom') IS NULL
    ALTER TABLE dbo.EM_Schedules ADD EffectiveFrom DATE NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'MediaPreparationID') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD MediaPreparationID INT NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'OperationalState') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD OperationalState NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'ActivityBatchNo') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD ActivityBatchNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'PersonnelCount') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD PersonnelCount INT NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'TransportStart') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD TransportStart DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'TransportEnd') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD TransportEnd DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1Start') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1Start DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1End') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1End DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1Temp') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1Temp NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2Start') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2Start DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2End') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2End DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2Temp') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2Temp NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'ControlReadBy') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD ControlReadBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'ControlReadAt') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD ControlReadAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'PlannedIncubationEnd') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD PlannedIncubationEnd DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1TargetEnd') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1TargetEnd DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1ActualTempC') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1ActualTempC DECIMAL(5,2) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1CompletedBy') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1CompletedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase1CompletedAt') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase1CompletedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2TargetEnd') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2TargetEnd DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2ActualTempC') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2ActualTempC DECIMAL(5,2) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2CompletedBy') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2CompletedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_PlanSamples', N'IncubationPhase2CompletedAt') IS NULL
    ALTER TABLE dbo.EM_PlanSamples ADD IncubationPhase2CompletedAt DATETIME2(0) NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_EM_Plans_StatusDue' AND object_id=OBJECT_ID(N'dbo.EM_Plans'))
    CREATE INDEX IX_EM_Plans_StatusDue ON dbo.EM_Plans(Status, SampleDueDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_EM_PlanSamples_PlanStatus' AND object_id=OBJECT_ID(N'dbo.EM_PlanSamples'))
    CREATE INDEX IX_EM_PlanSamples_PlanStatus ON dbo.EM_PlanSamples(PlanID, Status);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'IX_EM_PlanSamples_Area' AND object_id=OBJECT_ID(N'dbo.EM_PlanSamples'))
    CREATE INDEX IX_EM_PlanSamples_Area ON dbo.EM_PlanSamples(AreaID);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name=N'UX_EM_Plans_ScheduleDue' AND object_id=OBJECT_ID(N'dbo.EM_Plans'))
    EXEC sys.sp_executesql N'CREATE UNIQUE INDEX UX_EM_Plans_ScheduleDue ON dbo.EM_Plans(ScheduleID, SampleDueDate) WHERE ScheduleID IS NOT NULL;';

IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260718_002')
    INSERT dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260718_002', N'Environmental Monitoring plan, schedule, sample lifecycle, signatures, transport and incubation traceability.');
IF NOT EXISTS (SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey = N'20260719_001')
    INSERT dbo.LIMS_SchemaVersions (VersionKey, Description)
    VALUES (N'20260719_001', N'Operational EM schedule configuration, area mappings, duplicate prevention, and due-plan generation.');";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureConnectedDatabaseAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = "SELECT DB_NAME();";
            await using var command = new SqlCommand(sql, connection, transaction);
            object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
            string databaseName = Convert.ToString(result)?.Trim() ?? string.Empty;

            if (!databaseName.Equals(AppConfig.DatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"PharmaLIMS connected to database '{databaseName}' instead of '{AppConfig.DatabaseName}'. " +
                    "Check appsettings.json before continuing.");
            }
        }


        private static async Task EnsurePrmWorkflowDependenciesAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL
    THROW 51100, 'Required table dbo.PRM_Samples does not exist in the PharmaLIMS database.', 1;
IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL
    THROW 51101, 'Required table dbo.PRM_SampleTests does not exist in the PharmaLIMS database.', 1;
IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL
    THROW 51102, 'Required table dbo.PRM_Certificates does not exist in the PharmaLIMS database.', 1;
IF OBJECT_ID(N'dbo.PRM_CertificateHistory', N'U') IS NULL
    THROW 51103, 'Required table dbo.PRM_CertificateHistory does not exist in the PharmaLIMS database.', 1;
IF OBJECT_ID(N'dbo.QualityEvents', N'U') IS NULL
    THROW 51104, 'Required table dbo.QualityEvents does not exist in the PharmaLIMS database.', 1;

IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PRM_CertificateSnapshots
    (
        SnapshotID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_CertificateSnapshots PRIMARY KEY,
        CertificateID INT NOT NULL,
        SampleID INT NOT NULL,
        CertificateNumber NVARCHAR(60) NOT NULL,
        HtmlContent NVARCHAR(MAX) NOT NULL,
        SnapshotHash NVARCHAR(128) NOT NULL,
        CreatedBy NVARCHAR(120) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL,
        CONSTRAINT FK_PRM_CertificateSnapshots_Certificate FOREIGN KEY(CertificateID) REFERENCES dbo.PRM_Certificates(CertificateID),
        CONSTRAINT UQ_PRM_CertificateSnapshots_Certificate UNIQUE(CertificateID)
    );
    CREATE INDEX IX_PRM_CertificateSnapshots_Sample ON dbo.PRM_CertificateSnapshots(SampleID,CreatedAt DESC);
END;

IF OBJECT_ID(N'dbo.PRM_SpecificationTests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PRM_SpecificationTests
    (
        SpecificationTestID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SpecificationTests PRIMARY KEY,
        SpecificationNo NVARCHAR(120) NOT NULL,
        SampleCategory NVARCHAR(40) NOT NULL,
        VersionNo INT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Version DEFAULT (1),
        TestCode NVARCHAR(40) NOT NULL,
        TestName NVARCHAR(160) NOT NULL,
        SpecificationText NVARCHAR(500) NOT NULL,
        Unit NVARCHAR(50) NULL,
        ResultType NVARCHAR(60) NOT NULL,
        SpecificationLimit DECIMAL(18,3) NULL,
        RequiredTest BIT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Required DEFAULT (1),
        SortOrder INT NULL,
        ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Approval DEFAULT N'Draft',
        ApprovedBy NVARCHAR(120) NULL,
        ApprovedDate DATETIME2(0) NULL,
        EffectiveDate DATE NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Active DEFAULT (0),
        CreatedBy NVARCHAR(120) NULL,
        CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Created DEFAULT SYSDATETIME(),
        CONSTRAINT UQ_PRM_SpecificationTests UNIQUE (SpecificationNo, SampleCategory, VersionNo, TestCode)
    );
END;

IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
    ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedBy NVARCHAR(120) NULL;
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NULL
    ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedDate DATETIME2(0) NULL;

IF OBJECT_ID(N'dbo.PRM_SpecificationSignatures',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PRM_SpecificationSignatures
    (
        SignatureID BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_SpecificationSignatures PRIMARY KEY,
        SpecificationNo NVARCHAR(120) NOT NULL,
        SampleCategory NVARCHAR(40) NOT NULL,
        VersionNo INT NOT NULL,
        ActionType NVARCHAR(60) NOT NULL,
        ActionReason NVARCHAR(MAX) NOT NULL,
        SignedBy NVARCHAR(120) NOT NULL,
        MeaningOfSignature NVARCHAR(255) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_SpecificationSignatures_SignedAt DEFAULT SYSDATETIME()
    );
    CREATE INDEX IX_PRM_SpecificationSignatures_Record
        ON dbo.PRM_SpecificationSignatures(SpecificationNo,SampleCategory,VersionNo,SignedAt DESC);
END;

IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PRM_ElectronicSignatures
    (
        SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_ElectronicSignatures PRIMARY KEY,
        SampleID INT NOT NULL,
        ActionType NVARCHAR(80) NOT NULL,
        SignedBy NVARCHAR(120) NOT NULL,
        MeaningOfSignature NVARCHAR(255) NOT NULL,
        ActionReason NVARCHAR(MAX) NOT NULL,
        UserRole NVARCHAR(80) NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_ElectronicSignatures_SignedAt DEFAULT SYSDATETIME(),
        CONSTRAINT FK_PRM_ElectronicSignatures_Samples FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID)
    );
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.PRM_ElectronicSignatures')
      AND name = N'IX_PRM_ElectronicSignatures_Sample_Action'
)
BEGIN
    CREATE INDEX IX_PRM_ElectronicSignatures_Sample_Action
        ON dbo.PRM_ElectronicSignatures(SampleID, ActionType, SignedAt DESC);
END;

IF OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.QualityEventAffectedResults
    (
        AffectedResultID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_QualityEventAffectedResults PRIMARY KEY,
        QualityEventID INT NOT NULL,
        SampleTestID INT NULL,
        TestID INT NULL,
        TestName NVARCHAR(200) NULL,
        ResultValue NVARCHAR(200) NULL,
        SpecificationLimit NVARCHAR(500) NULL,
        Unit NVARCHAR(50) NULL,
        FailureType NVARCHAR(120) NULL,
        CreatedDate DATETIME2(0) NOT NULL CONSTRAINT DF_QualityEventAffectedResults_Created DEFAULT SYSDATETIME()
    );
END;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text,
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }


        private static async Task EnsureEnvironmentalMonitoringWorkflowAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
    THROW 51010, 'Required table dbo.EM_Events does not exist in the PharmaLIMS database.', 1;

IF COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NULL
    ALTER TABLE dbo.EM_Events ADD WorkflowStatus NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'ResultsEnteredBy') IS NULL
    ALTER TABLE dbo.EM_Events ADD ResultsEnteredBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'ResultsEnteredDate') IS NULL
    ALTER TABLE dbo.EM_Events ADD ResultsEnteredDate DATETIME NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SubmittedBy') IS NULL
    ALTER TABLE dbo.EM_Events ADD SubmittedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SubmittedDate') IS NULL
    ALTER TABLE dbo.EM_Events ADD SubmittedDate DATETIME NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'ReviewedBy') IS NULL
    ALTER TABLE dbo.EM_Events ADD ReviewedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'ReviewedDate') IS NULL
    ALTER TABLE dbo.EM_Events ADD ReviewedDate DATETIME NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'ApprovedBy') IS NULL
    ALTER TABLE dbo.EM_Events ADD ApprovedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'ApprovedDate') IS NULL
    ALTER TABLE dbo.EM_Events ADD ApprovedDate DATETIME NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'FinalResult') IS NULL
    ALTER TABLE dbo.EM_Events ADD FinalResult NVARCHAR(50) NULL;

EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ALTER COLUMN WorkflowStatus NVARCHAR(50) NULL;';
EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_Events ALTER COLUMN FinalResult NVARCHAR(50) NULL;';

IF OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NULL
    THROW 51011, 'Required table dbo.EM_EventPlates does not exist in the PharmaLIMS database.', 1;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'Status') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD Status NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'ColoniesObserved') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD ColoniesObserved NVARCHAR(500) NULL;
EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_EventPlates ALTER COLUMN Status NVARCHAR(50) NULL;';
EXEC sys.sp_executesql N'ALTER TABLE dbo.EM_EventPlates ALTER COLUMN ColoniesObserved NVARCHAR(500) NULL;';

IF OBJECT_ID(N'dbo.EM_EventSignatures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_EventSignatures
    (
        SignatureID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_EventSignatures PRIMARY KEY,
        EventID INT NOT NULL,
        EventNo NVARCHAR(50) NULL,
        ActionType NVARCHAR(100) NOT NULL,
        ActionReason NVARCHAR(MAX) NULL,
        SignedBy NVARCHAR(100) NOT NULL,
        UserRole NVARCHAR(100) NULL,
        MeaningOfSignature NVARCHAR(255) NOT NULL,
        SignedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_EventSignatures_SignedAt DEFAULT SYSDATETIME()
    );
END;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text,
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureEnvironmentalMonitoringExtensionsAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
    THROW 51200, 'Required table dbo.EM_Events does not exist.', 1;

IF OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NULL
    THROW 51201, 'Required table dbo.EM_EventPlates does not exist.', 1;

IF OBJECT_ID(N'dbo.EM_Areas', N'U') IS NULL
    THROW 51202, 'Required table dbo.EM_Areas does not exist.', 1;

IF OBJECT_ID(N'dbo.EM_AreaTemplates', N'U') IS NULL
    THROW 51203, 'Required table dbo.EM_AreaTemplates does not exist.', 1;

IF COL_LENGTH(N'dbo.EM_Events', N'MonitoringCategory') IS NULL
    ALTER TABLE dbo.EM_Events ADD MonitoringCategory NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'DispensingBooth') IS NULL
    ALTER TABLE dbo.EM_Events ADD DispensingBooth NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'MaterialName') IS NULL
    ALTER TABLE dbo.EM_Events ADD MaterialName NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'BatchNo') IS NULL
    ALTER TABLE dbo.EM_Events ADD BatchNo NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeId') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeId NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeName') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeName NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeDepartment') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeDepartment NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'EmployeeShift') IS NULL
    ALTER TABLE dbo.EM_Events ADD EmployeeShift NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SamplingStage') IS NULL
    ALTER TABLE dbo.EM_Events ADD SamplingStage NVARCHAR(50) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceLocation') IS NULL
    ALTER TABLE dbo.EM_Events ADD SurfaceLocation NVARCHAR(200) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceType') IS NULL
    ALTER TABLE dbo.EM_Events ADD SurfaceType NVARCHAR(80) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SurfaceAreaCm2') IS NULL
    ALTER TABLE dbo.EM_Events ADD SurfaceAreaCm2 DECIMAL(10,2) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'SwabKitLot') IS NULL
    ALTER TABLE dbo.EM_Events ADD SwabKitLot NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'DiluentLot') IS NULL
    ALTER TABLE dbo.EM_Events ADD DiluentLot NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.EM_Events', N'RecoveryVolumeMl') IS NULL
    ALTER TABLE dbo.EM_Events ADD RecoveryVolumeMl DECIMAL(10,2) NULL;

IF COL_LENGTH(N'dbo.EM_EventPlates', N'SampleSite') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD SampleSite NVARCHAR(150) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'SurfaceType') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD SurfaceType NVARCHAR(80) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'PersonnelSide') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD PersonnelSide NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'SurfaceAreaCm2') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD SurfaceAreaCm2 DECIMAL(10,2) NULL;
IF COL_LENGTH(N'dbo.EM_EventPlates', N'RecoveryVolumeMl') IS NULL
    ALTER TABLE dbo.EM_EventPlates ADD RecoveryVolumeMl DECIMAL(10,2) NULL;

IF OBJECT_ID(N'dbo.EM_MonitoringMethods', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_MonitoringMethods
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_MonitoringMethods PRIMARY KEY,
        MethodName NVARCHAR(100) NOT NULL,
        Category NVARCHAR(50) NOT NULL,
        DefaultUnit NVARCHAR(30) NOT NULL,
        RequiresEmployee BIT NOT NULL CONSTRAINT DF_EM_MonitoringMethods_RequiresEmployee DEFAULT (0),
        RequiresSurfaceDetails BIT NOT NULL CONSTRAINT DF_EM_MonitoringMethods_RequiresSurfaceDetails DEFAULT (0),
        IsActive BIT NOT NULL CONSTRAINT DF_EM_MonitoringMethods_IsActive DEFAULT (1),
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_MonitoringMethods_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT UQ_EM_MonitoringMethods_MethodName UNIQUE (MethodName)
    );
END;

MERGE dbo.EM_MonitoringMethods AS target
USING (VALUES
    (N'Active Air Sampling', N'Air', N'CFU/m3', 0, 0),
    (N'Settle Plate', N'Air', N'CFU/plate', 0, 0),
    (N'Contact Plate', N'Surface', N'CFU/plate', 0, 1),
    (N'Surface Swab', N'Surface', N'CFU/swab', 0, 1),
    (N'Personnel Monitoring', N'Personnel', N'CFU/glove', 1, 0)
) AS source(MethodName, Category, DefaultUnit, RequiresEmployee, RequiresSurfaceDetails)
ON target.MethodName = source.MethodName
WHEN MATCHED THEN UPDATE SET
    target.Category = source.Category,
    target.DefaultUnit = source.DefaultUnit,
    target.RequiresEmployee = source.RequiresEmployee,
    target.RequiresSurfaceDetails = source.RequiresSurfaceDetails,
    target.IsActive = 1
WHEN NOT MATCHED THEN INSERT
    (MethodName, Category, DefaultUnit, RequiresEmployee, RequiresSurfaceDetails, IsActive)
    VALUES
    (source.MethodName, source.Category, source.DefaultUnit, source.RequiresEmployee, source.RequiresSurfaceDetails, 1);

IF OBJECT_ID(N'dbo.EM_Personnel', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.EM_Personnel
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_EM_Personnel PRIMARY KEY,
        EmployeeId NVARCHAR(50) NOT NULL,
        EmployeeName NVARCHAR(150) NOT NULL,
        Department NVARCHAR(100) NULL,
        IsActive BIT NOT NULL CONSTRAINT DF_EM_Personnel_IsActive DEFAULT (1),
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_EM_Personnel_CreatedAt DEFAULT (SYSDATETIME()),
        CONSTRAINT UQ_EM_Personnel_EmployeeId UNIQUE (EmployeeId)
    );
END;

-- These exact rows were generated by the retired D29/D30-only seed.
-- Preserve history but deactivate them; current surface/personnel registration is dynamic by selected area.
UPDATE T
SET T.IsActive = 0
FROM dbo.EM_AreaTemplates T
INNER JOIN dbo.EM_Areas A ON A.Id = T.AreaId
WHERE A.AreaCode IN (N'D29', N'D30')
  AND T.Method IN (N'Contact Plate', N'Surface Swab', N'Personnel Monitoring')
  AND T.PlateCode IN
  (
      N'D29-CP-BALANCE', N'D29-CP-WORKSURFACE',
      N'D29-SW-BALANCEPAN', N'D29-SW-CONTROLPANEL', N'D29-SW-DOORHANDLE',
      N'D29-PM-LEFTGLOVE', N'D29-PM-RIGHTGLOVE',
      N'D30-CP-BALANCE', N'D30-CP-WORKSURFACE',
      N'D30-SW-BALANCEPAN', N'D30-SW-CONTROLPANEL', N'D30-SW-DOORHANDLE',
      N'D30-PM-LEFTGLOVE', N'D30-PM-RIGHTGLOVE'
  );

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EM_Events_MonitoringCategory' AND object_id = OBJECT_ID(N'dbo.EM_Events'))
    EXEC(N'CREATE INDEX IX_EM_Events_MonitoringCategory ON dbo.EM_Events(MonitoringCategory, EventDate);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_EM_Events_EmployeeId' AND object_id = OBJECT_ID(N'dbo.EM_Events'))
    EXEC(N'CREATE INDEX IX_EM_Events_EmployeeId ON dbo.EM_Events(EmployeeId, EventDate) WHERE EmployeeId IS NOT NULL;');
";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureMediaQualificationWorkflowAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
    THROW 51000, 'Required table dbo.MediaQualifications does not exist in the PharmaLIMS database.', 1;

IF COL_LENGTH('dbo.MediaQualifications', 'MediaQualificationID') IS NULL
    THROW 51001, 'Required column dbo.MediaQualifications.MediaQualificationID does not exist.', 1;

IF COL_LENGTH('dbo.MediaQualifications', 'ReleasedBy') IS NULL
    ALTER TABLE dbo.MediaQualifications ADD ReleasedBy NVARCHAR(100) NULL;

IF COL_LENGTH('dbo.MediaQualifications', 'ReviewDate') IS NULL
    ALTER TABLE dbo.MediaQualifications ADD ReviewDate DATETIME2(0) NULL;

IF COL_LENGTH('dbo.MediaQualifications', 'ReleaseDate') IS NULL
    ALTER TABLE dbo.MediaQualifications ADD ReleaseDate DATETIME2(0) NULL;

IF COL_LENGTH('dbo.MediaQualifications', 'QualificationStatus') IS NULL
    ALTER TABLE dbo.MediaQualifications ADD QualificationStatus NVARCHAR(30) NULL;

-- SQL Server compiles a batch before executing ALTER TABLE. Any statement that
-- references a newly added column must therefore run in a separate dynamic batch.
IF COL_LENGTH('dbo.MediaQualifications', 'OverallResult') IS NOT NULL
   AND COL_LENGTH('dbo.MediaQualifications', 'ReviewedBy') IS NOT NULL
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE dbo.MediaQualifications
        SET QualificationStatus =
            CASE
                WHEN UPPER(LTRIM(RTRIM(ISNULL(OverallResult, '''')))) IN (''FAIL'', ''FAILED'', ''REJECTED'')
                    THEN ''Failed''
                WHEN UPPER(LTRIM(RTRIM(ISNULL(OverallResult, '''')))) = ''PASS''
                     AND NULLIF(LTRIM(RTRIM(ISNULL(ReviewedBy, ''''))), '''') IS NOT NULL
                    THEN ''Qualified''
                ELSE ''Pending Review''
            END
        WHERE QualificationStatus IS NULL
           OR LTRIM(RTRIM(QualificationStatus)) = '''';';
END
ELSE
BEGIN
    EXEC sys.sp_executesql N'
        UPDATE dbo.MediaQualifications
        SET QualificationStatus = N''Pending Review''
        WHERE QualificationStatus IS NULL
           OR LTRIM(RTRIM(QualificationStatus)) = N'''';';
END;

EXEC sys.sp_executesql N'
    ALTER TABLE dbo.MediaQualifications
    ALTER COLUMN QualificationStatus NVARCHAR(30) NOT NULL;';

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id = dc.parent_object_id
       AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID(N'dbo.MediaQualifications')
      AND c.name = N'QualificationStatus'
)
BEGIN
    EXEC sys.sp_executesql N'
        ALTER TABLE dbo.MediaQualifications
        ADD CONSTRAINT DF_MediaQualifications_QualificationStatus
            DEFAULT (N''Pending Review'') FOR QualificationStatus;';
END;

IF OBJECT_ID(N'dbo.MediaQualificationTests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MediaQualificationTests
    (
        MediaQualificationTestID INT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_MediaQualificationTests PRIMARY KEY,
        MediaQualificationID INT NOT NULL,
        TestName NVARCHAR(100) NULL,
        OrganismName NVARCHAR(200) NULL,
        ATCCNumber NVARCHAR(80) NULL,
        InoculumLevel NVARCHAR(80) NULL,
        ExpectedResult NVARCHAR(200) NULL,
        ActualResult NVARCHAR(200) NULL,
        ControlCount DECIMAL(18,2) NULL,
        TestCount DECIMAL(18,2) NULL,
        RecoveryPercent DECIMAL(9,2) NULL,
        IncubationConditions NVARCHAR(200) NULL,
        TestResult NVARCHAR(30) NULL,
        Remarks NVARCHAR(MAX) NULL,
        CreatedDate DATETIME2(0) NOT NULL
            CONSTRAINT DF_MediaQualificationTests_CreatedDate DEFAULT (SYSDATETIME()),
        CONSTRAINT FK_MediaQualificationTests_MediaQualifications
            FOREIGN KEY (MediaQualificationID)
            REFERENCES dbo.MediaQualifications(MediaQualificationID)
            ON DELETE CASCADE
    );

    CREATE INDEX IX_MediaQualificationTests_MediaQualificationID
        ON dbo.MediaQualificationTests(MediaQualificationID, MediaQualificationTestID);
END
ELSE
BEGIN
    IF COL_LENGTH('dbo.MediaQualificationTests', 'MediaQualificationTestID') IS NULL
        THROW 51002, 'Required column dbo.MediaQualificationTests.MediaQualificationTestID does not exist.', 1;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'MediaQualificationID') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD MediaQualificationID INT NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'TestName') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD TestName NVARCHAR(100) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'OrganismName') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD OrganismName NVARCHAR(200) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'ATCCNumber') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD ATCCNumber NVARCHAR(80) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'InoculumLevel') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD InoculumLevel NVARCHAR(80) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'ExpectedResult') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD ExpectedResult NVARCHAR(200) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'ActualResult') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD ActualResult NVARCHAR(200) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'ControlCount') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD ControlCount DECIMAL(18,2) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'TestCount') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD TestCount DECIMAL(18,2) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'RecoveryPercent') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD RecoveryPercent DECIMAL(9,2) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'IncubationConditions') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD IncubationConditions NVARCHAR(200) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'TestResult') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD TestResult NVARCHAR(30) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'Remarks') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD Remarks NVARCHAR(MAX) NULL;
    IF COL_LENGTH('dbo.MediaQualificationTests', 'CreatedDate') IS NULL
        ALTER TABLE dbo.MediaQualificationTests ADD CreatedDate DATETIME2(0) NULL;
END;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text,
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task EnsureCultureMediaInventoryAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            const string sql = @"
IF OBJECT_ID(N'dbo.CultureMediaLots', N'U') IS NULL
    THROW 51030, 'Required table dbo.CultureMediaLots does not exist.', 1;
IF OBJECT_ID(N'dbo.MediaPreparations', N'U') IS NULL
    THROW 51031, 'Required table dbo.MediaPreparations does not exist.', 1;

IF COL_LENGTH(N'dbo.CultureMediaLots', N'InitialStockG') IS NULL
    ALTER TABLE dbo.CultureMediaLots ADD InitialStockG DECIMAL(18,3) NULL;
IF COL_LENGTH(N'dbo.CultureMediaLots', N'CurrentStockG') IS NULL
    ALTER TABLE dbo.CultureMediaLots ADD CurrentStockG DECIMAL(18,3) NULL;
IF COL_LENGTH(N'dbo.CultureMediaLots', N'StockStatus') IS NULL
    ALTER TABLE dbo.CultureMediaLots ADD StockStatus NVARCHAR(30) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'PowderQuantityG') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD PowderQuantityG DECIMAL(18,3) NULL;

EXEC sys.sp_executesql N'
UPDATE dbo.CultureMediaLots
SET InitialStockG = COALESCE(InitialStockG, TRY_CONVERT(DECIMAL(18,3), QuantityReceived));

UPDATE l
SET CurrentStockG = CASE
        WHEN l.CurrentStockG IS NOT NULL THEN l.CurrentStockG
        WHEN NOT EXISTS (SELECT 1 FROM dbo.MediaPreparations p WHERE p.MediaLotID = l.MediaLotID)
             THEN l.InitialStockG
        ELSE NULL
    END
FROM dbo.CultureMediaLots l;

UPDATE dbo.CultureMediaLots
SET StockStatus = CASE
    WHEN CurrentStockG IS NULL THEN N''Requires Reconciliation''
    WHEN CurrentStockG <= 0 THEN N''Depleted''
    WHEN UPPER(LTRIM(RTRIM(ISNULL(ReceiptStatus, N'''')))) = N''REJECTED'' THEN N''Rejected''
    WHEN UPPER(LTRIM(RTRIM(ISNULL(ReceiptStatus, N'''')))) IN (N''RELEASED'', N''ACCEPTED'') THEN N''Available''
    ELSE N''Quarantine'' END
WHERE StockStatus IS NULL OR LTRIM(RTRIM(StockStatus)) = N'''';';

IF OBJECT_ID(N'dbo.CultureMediaStockTransactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CultureMediaStockTransactions
    (
        StockTransactionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaStockTransactions PRIMARY KEY,
        MediaLotID INT NOT NULL,
        MediaPreparationID INT NULL,
        TransactionType NVARCHAR(40) NOT NULL,
        QuantityChangeG DECIMAL(18,3) NOT NULL,
        BalanceBeforeG DECIMAL(18,3) NULL,
        BalanceAfterG DECIMAL(18,3) NOT NULL,
        ReferenceNo NVARCHAR(80) NULL,
        Reason NVARCHAR(500) NULL,
        PerformedBy NVARCHAR(100) NOT NULL,
        PerformedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaStockTransactions_PerformedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_CultureMediaStockTransactions_Lot FOREIGN KEY(MediaLotID) REFERENCES dbo.CultureMediaLots(MediaLotID),
        CONSTRAINT FK_CultureMediaStockTransactions_Preparation FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID),
        CONSTRAINT CK_CultureMediaStockTransactions_NonZero CHECK(QuantityChangeG <> 0)
    );
    CREATE INDEX IX_CultureMediaStockTransactions_LotDate
        ON dbo.CultureMediaStockTransactions(MediaLotID, PerformedAt DESC, StockTransactionID DESC);
END
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'BalanceBeforeG') IS NULL
        ALTER TABLE dbo.CultureMediaStockTransactions ADD BalanceBeforeG DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'Reason') IS NULL
        ALTER TABLE dbo.CultureMediaStockTransactions ADD Reason NVARCHAR(500) NULL;
END;

EXEC sys.sp_executesql N'
INSERT INTO dbo.CultureMediaStockTransactions
(MediaLotID, MediaPreparationID, TransactionType, QuantityChangeG, BalanceBeforeG, BalanceAfterG, ReferenceNo, Reason, PerformedBy, PerformedAt)
SELECT
    l.MediaLotID,
    NULL,
    N''HistoricalOpeningBalance'',
    l.CurrentStockG,
    0,
    l.CurrentStockG,
    l.LotNumber,
    N''Historical balance captured during controlled migration; verify by physical reconciliation.'',
    N''System Migration'',
    SYSUTCDATETIME()
FROM dbo.CultureMediaLots l
WHERE l.CurrentStockG IS NOT NULL
  AND l.CurrentStockG > 0
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.CultureMediaStockTransactions st
      WHERE st.MediaLotID = l.MediaLotID
  );';

IF OBJECT_ID(N'dbo.CultureMediaStockReconciliations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CultureMediaStockReconciliations
    (
        ReconciliationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaStockReconciliations PRIMARY KEY,
        MediaLotID INT NOT NULL,
        SystemBalanceG DECIMAL(18,3) NULL,
        PhysicalCountG DECIMAL(18,3) NOT NULL,
        DifferenceG DECIMAL(18,3) NOT NULL,
        Reason NVARCHAR(500) NOT NULL,
        ReconciledBy NVARCHAR(100) NOT NULL,
        ReconciledAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaStockReconciliations_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_CultureMediaStockReconciliations_Lot FOREIGN KEY(MediaLotID) REFERENCES dbo.CultureMediaLots(MediaLotID),
        CONSTRAINT CK_CultureMediaStockReconciliations_Physical CHECK(PhysicalCountG >= 0)
    );
    CREATE INDEX IX_CultureMediaStockReconciliations_LotDate
        ON dbo.CultureMediaStockReconciliations(MediaLotID, ReconciledAt DESC, ReconciliationID DESC);
END;

IF COL_LENGTH(N'dbo.MediaPreparations', N'VisualCheckedBy') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD VisualCheckedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'VisualCheckedAt') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD VisualCheckedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'SterilityReviewedBy') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD SterilityReviewedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'SterilityReviewedAt') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD SterilityReviewedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'ReleasedBy') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD ReleasedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'ReleasedAt') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD ReleasedAt DATETIME2(0) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'RejectedBy') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD RejectedBy NVARCHAR(100) NULL;
IF COL_LENGTH(N'dbo.MediaPreparations', N'RejectedAt') IS NULL
    ALTER TABLE dbo.MediaPreparations ADD RejectedAt DATETIME2(0) NULL;

IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.EM_Events', N'MediaPreparationID') IS NULL
        ALTER TABLE dbo.EM_Events ADD MediaPreparationID INT NULL;

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.foreign_keys
        WHERE name = N'FK_EM_Events_MediaPreparation'
          AND parent_object_id = OBJECT_ID(N'dbo.EM_Events')
    )
        EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EM_Events WITH CHECK
        ADD CONSTRAINT FK_EM_Events_MediaPreparation
            FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID);';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_EM_Events_MediaPreparationID'
          AND object_id = OBJECT_ID(N'dbo.EM_Events')
    )
        EXEC sys.sp_executesql N'
        CREATE INDEX IX_EM_Events_MediaPreparationID
            ON dbo.EM_Events(MediaPreparationID, EventDate DESC)
            WHERE MediaPreparationID IS NOT NULL;';

    EXEC sys.sp_executesql N'
    UPDATE e
    SET MediaPreparationID = matched.MediaPreparationID
    FROM dbo.EM_Events e
    CROSS APPLY
    (
        SELECT TOP (1) p.MediaPreparationID
        FROM dbo.MediaPreparations p
        WHERE UPPER(LTRIM(RTRIM(p.MediaPreparationNo))) = UPPER(LTRIM(RTRIM(ISNULL(e.MediaLotNo, N''''))))
        ORDER BY p.MediaPreparationID DESC
    ) matched
    WHERE e.MediaPreparationID IS NULL;';

    IF OBJECT_ID(N'dbo.EM_PlanSamples', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.EM_PlanSamples', N'MediaPreparationID') IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql N'
        UPDATE e
        SET MediaPreparationID = planMedia.MediaPreparationID
        FROM dbo.EM_Events e
        CROSS APPLY
        (
            SELECT TOP (1) ps.MediaPreparationID
            FROM dbo.EM_PlanSamples ps
            WHERE ps.PlanID = e.PlanID
              AND ps.MediaPreparationID IS NOT NULL
            ORDER BY ps.PlanSampleID
        ) planMedia
        WHERE e.MediaPreparationID IS NULL
          AND e.PlanID IS NOT NULL;';
    END;
END;

IF OBJECT_ID(N'dbo.CultureMediaPreparationDispositions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CultureMediaPreparationDispositions
    (
        DispositionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaPreparationDispositions PRIMARY KEY,
        MediaPreparationID INT NOT NULL,
        DispositionType NVARCHAR(50) NOT NULL,
        PowderQuantityG DECIMAL(18,3) NOT NULL CONSTRAINT DF_CultureMediaPreparationDispositions_Powder DEFAULT (0),
        Reason NVARCHAR(500) NOT NULL,
        DisposedBy NVARCHAR(100) NOT NULL,
        DisposedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaPreparationDispositions_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_CultureMediaPreparationDispositions_Preparation FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID),
        CONSTRAINT CK_CultureMediaPreparationDispositions_Powder CHECK(PowderQuantityG >= 0)
    );
    CREATE INDEX IX_CultureMediaPreparationDispositions_Preparation
        ON dbo.CultureMediaPreparationDispositions(MediaPreparationID, DisposedAt DESC);
END;

IF OBJECT_ID(N'dbo.CultureMediaPrintHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CultureMediaPrintHistory
    (
        PrintHistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaPrintHistory PRIMARY KEY,
        EntityType NVARCHAR(50) NOT NULL,
        EntityID INT NOT NULL,
        RecordNumber NVARCHAR(100) NULL,
        DocumentType NVARCHAR(100) NOT NULL,
        PrintSequence INT NOT NULL,
        PrintedBy NVARCHAR(100) NOT NULL,
        PrintedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaPrintHistory_PrintedAt DEFAULT SYSUTCDATETIME(),
        Reason NVARCHAR(500) NULL,
        CONSTRAINT CK_CultureMediaPrintHistory_Sequence CHECK(PrintSequence > 0)
    );
    CREATE UNIQUE INDEX UX_CultureMediaPrintHistory_Sequence
        ON dbo.CultureMediaPrintHistory(EntityType, EntityID, DocumentType, PrintSequence);
    CREATE INDEX IX_CultureMediaPrintHistory_Record
        ON dbo.CultureMediaPrintHistory(RecordNumber, PrintedAt DESC);
END;

IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CultureMediaQualificationRequirements
    (
        RequirementID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaQualificationRequirements PRIMARY KEY,
        MediaTypePattern NVARCHAR(100) NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Pattern DEFAULT N'%',
        TestName NVARCHAR(100) NOT NULL,
        IsRequired BIT NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Required DEFAULT (1),
        MinimumRecoveryPercent DECIMAL(9,2) NULL,
        MaximumRecoveryPercent DECIMAL(9,2) NULL,
        EffectiveDate DATE NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Effective DEFAULT CAST(GETDATE() AS date),
        IsActive BIT NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Active DEFAULT (0),
        ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Status DEFAULT N'Draft',
        ReviewedBy NVARCHAR(100) NULL,
        ReviewedAt DATETIME2(0) NULL,
        ApprovedBy NVARCHAR(100) NULL,
        ApprovedAt DATETIME2(0) NULL,
        CONSTRAINT CK_CultureMediaQualificationRequirements_Recovery CHECK
        (
            (MinimumRecoveryPercent IS NULL OR MinimumRecoveryPercent >= 0)
            AND (MaximumRecoveryPercent IS NULL OR MaximumRecoveryPercent >= 0)
            AND (MinimumRecoveryPercent IS NULL OR MaximumRecoveryPercent IS NULL OR MinimumRecoveryPercent <= MaximumRecoveryPercent)
        )
    );
    CREATE UNIQUE INDEX UX_CultureMediaQualificationRequirements_Active
        ON dbo.CultureMediaQualificationRequirements(MediaTypePattern, TestName, EffectiveDate);
END;

IF NOT EXISTS (SELECT 1 FROM dbo.CultureMediaQualificationRequirements)
BEGIN
    INSERT INTO dbo.CultureMediaQualificationRequirements
    (MediaTypePattern, TestName, IsRequired, MinimumRecoveryPercent, MaximumRecoveryPercent, EffectiveDate, IsActive, ApprovalStatus, ApprovedBy, ApprovedAt)
    VALUES
    (N'%', N'pH Check', 1, NULL, NULL, CAST(GETDATE() AS date), 0, N'Draft', NULL, NULL),
    (N'%', N'Growth Promotion', 1, 50.00, 200.00, CAST(GETDATE() AS date), 0, N'Draft', NULL, NULL),
    (N'%', N'Indicative Property', 1, NULL, NULL, CAST(GETDATE() AS date), 0, N'Draft', NULL, NULL),
    (N'%', N'Inhibitory Property', 1, NULL, NULL, CAST(GETDATE() AS date), 0, N'Draft', NULL, NULL),
    (N'%', N'Preincubation Check', 1, NULL, NULL, CAST(GETDATE() AS date), 0, N'Draft', NULL, NULL);
END;";

            await using var command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text,
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task PrepareAuthenticationLoginCompatibilityAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            // Controlled reconciliation for known legacy dbo.Users shapes seen in field upgrades.
            // It is intentionally fail-safe:
            // - existing non-NULL values are preserved;
            // - NULL security/permission bits are canonicalized to 0 (inactive/no permission);
            // - no password, role, activation=1 or permission=1 value is ever created;
            // - incompatible storage types still fail closed for manual review.
            const string sql = @"
IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
    THROW 55020, 'Authentication compatibility requires dbo.Users before 20260911_000.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'PasswordHashNew'
      AND system_type_id<>TYPE_ID(N'nvarchar')
)
    THROW 55021, 'Existing dbo.Users.PasswordHashNew is not NVARCHAR and cannot be widened automatically.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'PasswordHashNew'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length<>-1
      AND max_length<1024
      AND is_nullable=1
)
    ALTER TABLE dbo.Users ALTER COLUMN PasswordHashNew NVARCHAR(512) NULL;
ELSE IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'PasswordHashNew'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length<>-1
      AND max_length<1024
      AND is_nullable=0
)
    ALTER TABLE dbo.Users ALTER COLUMN PasswordHashNew NVARCHAR(512) NOT NULL;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'PasswordSalt'
      AND system_type_id<>TYPE_ID(N'nvarchar')
)
    THROW 55022, 'Existing dbo.Users.PasswordSalt is not NVARCHAR and cannot be widened automatically.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'PasswordSalt'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length<>-1
      AND max_length<512
      AND is_nullable=1
)
    ALTER TABLE dbo.Users ALTER COLUMN PasswordSalt NVARCHAR(256) NULL;
ELSE IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'PasswordSalt'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length<>-1
      AND max_length<512
      AND is_nullable=0
)
    ALTER TABLE dbo.Users ALTER COLUMN PasswordSalt NVARCHAR(256) NOT NULL;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'FailedLoginAttempts'
      AND system_type_id<>TYPE_ID(N'int')
)
    THROW 55023, 'Existing dbo.Users.FailedLoginAttempts is not INT and cannot be normalized automatically.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'FailedLoginAttempts'
      AND system_type_id=TYPE_ID(N'int')
      AND is_nullable=1
)
BEGIN
    UPDATE dbo.Users
    SET FailedLoginAttempts=0
    WHERE FailedLoginAttempts IS NULL;
    ALTER TABLE dbo.Users ALTER COLUMN FailedLoginAttempts INT NOT NULL;
END;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'IsLocked'
      AND system_type_id<>TYPE_ID(N'bit')
)
    THROW 55024, 'Existing dbo.Users.IsLocked is not BIT and cannot be normalized automatically.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'IsLocked'
      AND system_type_id=TYPE_ID(N'bit')
      AND is_nullable=1
)
BEGIN
    UPDATE dbo.Users
    SET IsLocked=0
    WHERE IsLocked IS NULL;
    ALTER TABLE dbo.Users ALTER COLUMN IsLocked BIT NOT NULL;
END;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'IsActive'
      AND system_type_id<>TYPE_ID(N'bit')
)
    THROW 55025, 'Existing dbo.Users.IsActive is not BIT and requires controlled manual reconciliation.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'IsActive'
      AND system_type_id=TYPE_ID(N'bit')
      AND is_nullable=1
)
BEGIN
    -- NULL activation is fail-safe: preserve every explicit 0/1 value and treat only NULL as inactive.
    UPDATE dbo.Users
    SET IsActive=0
    WHERE IsActive IS NULL;
    ALTER TABLE dbo.Users ALTER COLUMN IsActive BIT NOT NULL;
END;

DECLARE @PermissionColumn SYSNAME;
DECLARE PermissionColumns CURSOR LOCAL FAST_FORWARD FOR
SELECT PermissionColumn
FROM (VALUES
    (N'CanAccessWater'), (N'CanAccessEM'), (N'CanRegisterSamples'), (N'CanEnterResults'),
    (N'CanReviewResults'), (N'CanApproveResults'), (N'CanIssueCOA'), (N'CanCancelCOA'),
    (N'CanAccessReports'), (N'CanManageUsers'), (N'CanManageSettings')
) AS permissions(PermissionColumn);

OPEN PermissionColumns;
FETCH NEXT FROM PermissionColumns INTO @PermissionColumn;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'dbo.Users', @PermissionColumn) IS NOT NULL
    BEGIN
        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.Users')
              AND name=@PermissionColumn
              AND system_type_id<>TYPE_ID(N'bit')
        )
        BEGIN
            CLOSE PermissionColumns;
            DEALLOCATE PermissionColumns;
            THROW 55026, 'An existing dbo.Users permission column is not BIT and requires controlled manual reconciliation.', 1;
        END;

        IF EXISTS
        (
            SELECT 1 FROM sys.columns
            WHERE object_id=OBJECT_ID(N'dbo.Users')
              AND name=@PermissionColumn
              AND system_type_id=TYPE_ID(N'bit')
              AND is_nullable=1
        )
        BEGIN
            DECLARE @PermissionSql NVARCHAR(MAX) =
                N'UPDATE dbo.Users SET ' + QUOTENAME(@PermissionColumn) + N'=0 WHERE ' + QUOTENAME(@PermissionColumn) + N' IS NULL;' +
                N' ALTER TABLE dbo.Users ALTER COLUMN ' + QUOTENAME(@PermissionColumn) + N' BIT NOT NULL;';
            EXEC sys.sp_executesql @PermissionSql;
        END;
    END;

    FETCH NEXT FROM PermissionColumns INTO @PermissionColumn;
END;
CLOSE PermissionColumns;
DEALLOCATE PermissionColumns;

IF COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1 FROM sys.columns
       WHERE object_id=OBJECT_ID(N'dbo.Users')
         AND name=N'AuthenticationRowVersion'
         AND system_type_id=189
         AND is_nullable=0
   )
    THROW 55027, 'Existing dbo.Users.AuthenticationRowVersion is not ROWVERSION and requires controlled manual reconciliation.', 1;

IF COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion') IS NULL
   AND EXISTS
   (
       SELECT 1 FROM sys.columns
       WHERE object_id=OBJECT_ID(N'dbo.Users')
         AND system_type_id=189
   )
    THROW 55028, 'dbo.Users already has another ROWVERSION column; AuthenticationRowVersion cannot be created automatically.', 1;";

            await using SqlCommand command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text,
                CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120)
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task PrepareUserAdministrationSecurityCompatibilityAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            // 20260915_000 remains checksum-controlled. This helper repairs only the safe
            // legacy nullable BIT shape before the historical SQL is replayed.
            const string sql = @"
IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
    THROW 55110, 'User administration compatibility requires dbo.Users before 20260915_000.', 1;

IF COL_LENGTH(N'dbo.Users',N'MustChangePassword') IS NOT NULL
   AND EXISTS
   (
       SELECT 1 FROM sys.columns
       WHERE object_id=OBJECT_ID(N'dbo.Users')
         AND name=N'MustChangePassword'
         AND system_type_id<>TYPE_ID(N'bit')
   )
    THROW 55111, 'Existing dbo.Users.MustChangePassword is not BIT and requires controlled manual reconciliation.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.Users')
      AND name=N'MustChangePassword'
      AND system_type_id=TYPE_ID(N'bit')
      AND is_nullable=1
)
BEGIN
    UPDATE dbo.Users
    SET MustChangePassword=0
    WHERE MustChangePassword IS NULL;
    ALTER TABLE dbo.Users ALTER COLUMN MustChangePassword BIT NOT NULL;
END;

IF COL_LENGTH(N'dbo.Users',N'PasswordChangedAt') IS NOT NULL
   AND NOT EXISTS
   (
       SELECT 1 FROM sys.columns
       WHERE object_id=OBJECT_ID(N'dbo.Users')
         AND name=N'PasswordChangedAt'
         AND system_type_id=TYPE_ID(N'datetime2')
         AND scale=0
         AND is_nullable=1
   )
    THROW 55112, 'Existing dbo.Users.PasswordChangedAt has an incompatible shape; automatic conversion could lose audit timestamp precision and is blocked.', 1;";

            await using SqlCommand command = new SqlCommand(sql, connection, transaction)
            {
                CommandType = CommandType.Text,
                CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120)
            };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }
}
