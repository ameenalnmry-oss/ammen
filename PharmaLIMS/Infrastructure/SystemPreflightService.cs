using PharmaLIMS.Services;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace PharmaLIMS.Infrastructure
{
    public sealed class SystemPreflightCheck
    {
        public string Area { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Check { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
    }

    public sealed class SystemPreflightReport
    {
        public DateTime ExecutedAt { get; init; }
        public string Server { get; init; } = string.Empty;
        public string Database { get; init; } = string.Empty;
        public IReadOnlyList<SystemPreflightCheck> Checks { get; init; } = Array.Empty<SystemPreflightCheck>();

        public int BlockerCount => Checks.Count(item => item.Status == "BLOCKER");
        public int WarningCount => Checks.Count(item => item.Status == "WARNING");
        public int PassCount => Checks.Count(item => item.Status == "PASS");
        public bool CanProceed => BlockerCount == 0;
    }

    /// <summary>
    /// Read-only runtime verification against the actual configured SQL Server database.
    /// This service never applies migrations or changes regulated records.
    /// </summary>
    public sealed class SystemPreflightService
    {
        private readonly DatabaseConnection _database;
        private readonly SemaphoreSlim _runGate = new(1, 1);

        public SystemPreflightService(DatabaseConnection database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        /// <summary>
        /// Bounded, read-only operational readiness probe used as a Development convenience.
        /// It deliberately avoids historical integrity scans and migration-ledger traversal.
        /// Production workflow authorization uses the full System Preflight instead.
        /// </summary>
        public async Task<SystemPreflightReport> RunOperationalReadinessAsync(CancellationToken cancellationToken = default)
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var checks = new List<SystemPreflightCheck>();
            string connectedServer = string.Empty;
            string connectedDatabase = string.Empty;
            CheckConfiguration(checks);

            try
            {
                DataTable connectionInfo = await _database.ExecuteQueryAsync(@"
SELECT
    CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) AS ServerName,
    DB_NAME() AS DatabaseName,
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS SqlVersion;",
                    parameters: null,
                    commandTimeoutSeconds: Math.Max(10, AppConfig.CommandTimeoutSeconds),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (connectionInfo.Rows.Count == 0)
                    throw new InvalidOperationException("SQL Server returned no operational readiness connection row.");

                DataRow connectionRow = connectionInfo.Rows[0];
                connectedServer = Convert.ToString(connectionRow["ServerName"]) ?? string.Empty;
                connectedDatabase = Convert.ToString(connectionRow["DatabaseName"]) ?? string.Empty;
                string sqlVersion = Convert.ToString(connectionRow["SqlVersion"]) ?? string.Empty;

                if (!connectedDatabase.Equals(AppConfig.DatabaseName, StringComparison.OrdinalIgnoreCase))
                {
                    Add(checks, "Database", "BLOCKER", "Configured database identity",
                        $"Configured database is '{AppConfig.DatabaseName}' but the active connection is '{connectedDatabase}'.");
                    return BuildReport(checks, connectedServer, connectedDatabase);
                }

                Add(checks, "Database", "PASS", "Operational database connectivity",
                    $"Connected to {connectedServer} / {connectedDatabase}; SQL Server {sqlVersion}.");

                if (await IsDatabaseMaintenanceActiveAsync(cancellationToken).ConfigureAwait(false))
                {
                    Add(checks, "Database", "BLOCKER", "Database lifecycle coordination",
                        "Database Maintenance currently holds the controlled schema-maintenance lease. Wait for maintenance to finish, then rerun preflight.");
                    return BuildReport(checks, connectedServer, connectedDatabase);
                }

                DataTable result = await _database.ExecuteQueryAsync(@"
SELECT
    CASE WHEN OBJECT_ID(N'dbo.Users',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasUsers,
    CASE WHEN OBJECT_ID(N'dbo.Samples',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasSamples,
    CASE WHEN OBJECT_ID(N'dbo.SampleTests',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasSampleTests,
    CASE WHEN OBJECT_ID(N'dbo.AuditTrail',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasAuditTrail,
    CASE WHEN OBJECT_ID(N'dbo.WaterTestProfiles',N'U') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfiles',N'ProfileID') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfiles',N'ProfileCode') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfiles',N'IsActive') IS NOT NULL
         THEN 1 ELSE 0 END AS HasWaterTestProfiles,
    CASE WHEN OBJECT_ID(N'dbo.WaterTestProfileTests',N'U') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfileTests',N'ProfileID') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfileTests',N'TestID') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfileTests',N'IsActive') IS NOT NULL
         THEN 1 ELSE 0 END AS HasWaterTestProfileTests,
    CASE WHEN OBJECT_ID(N'dbo.WaterSpecifications',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasWaterSpecifications,
    CASE WHEN OBJECT_ID(N'dbo.WaterTestProfileSignatures',N'U') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterTestProfiles',N'ControlledReference') IS NOT NULL
              AND COL_LENGTH(N'dbo.WaterSpecifications',N'ProfileID') IS NOT NULL
         THEN 1 ELSE 0 END AS HasWaterControlledProfileWorkflow,
    CASE WHEN OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NOT NULL AND COL_LENGTH(N'dbo.PRM_Samples',N'SampleStatus') IS NOT NULL THEN 1 ELSE 0 END AS HasPrmCore,
    CASE WHEN OBJECT_ID(N'dbo.EM_Events',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasEmCore,
    CASE WHEN OBJECT_ID(N'dbo.EM_Events',N'U') IS NOT NULL
              AND COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NOT NULL
              AND COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NOT NULL
              AND COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NOT NULL
              AND COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NOT NULL
         THEN 1 ELSE 0 END AS HasEmTrendSnapshotColumns,
    CASE WHEN EXISTS
         (
             SELECT 1 FROM sys.triggers
             WHERE parent_id=OBJECT_ID(N'dbo.EM_Events')
               AND name=N'TRG_EM_Events_ProtectAreaSnapshot_20260830'
               AND is_disabled=0
         ) THEN 1 ELSE 0 END AS HasEmTrendSnapshotGuard,
    CASE WHEN NOT EXISTS
         (
             SELECT 1 FROM (VALUES
                 (N'dbo.Users',N'AuthenticationRowVersion'),
                 (N'dbo.EM_Events',N'ResultRowVersion'),
                 (N'dbo.EM_EventPlates',N'ResultRowVersion'),
                 (N'dbo.SampleTests',N'ResultRowVersion')
             ) expected(TableName,ColumnName)
             LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(expected.TableName) AND c.name=expected.ColumnName
             WHERE c.column_id IS NULL OR c.system_type_id<>189 OR c.is_nullable<>0
         )
              AND EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
                         AND name=N'ResultCalculationVersion' AND system_type_id=52 AND is_nullable=1)
              AND EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
                         AND name=N'ResultCFU' AND system_type_id IN (106,108) AND precision=28 AND scale=12 AND is_nullable=1)
         THEN 1 ELSE 0 END AS HasReviewRemediationSchema,
    CASE WHEN OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL THEN 1 ELSE 0 END AS HasQualityEvents;",
                    parameters: null,
                    commandTimeoutSeconds: Math.Max(10, AppConfig.CommandTimeoutSeconds),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (result.Rows.Count == 0)
                    throw new InvalidOperationException("SQL Server returned no operational readiness schema row.");

                DataRow row = result.Rows[0];
                string[] requiredFlags =
                {
                    "HasUsers", "HasSamples", "HasSampleTests", "HasAuditTrail",
                    "HasWaterTestProfiles", "HasWaterTestProfileTests", "HasWaterSpecifications",
                    "HasWaterControlledProfileWorkflow",
                    "HasPrmCore", "HasEmCore", "HasEmTrendSnapshotColumns",
                    "HasEmTrendSnapshotGuard", "HasReviewRemediationSchema", "HasQualityEvents"
                };
                string[] missing = requiredFlags
                    .Where(name => Convert.ToInt32(row[name]) != 1)
                    .ToArray();

                if (missing.Length == 0)
                {
                    Add(checks, "Database Schema", "PASS", "Operational core schema",
                        "Core Water, EM, PRM, identity, audit, and Quality Event objects required to open workflows are present.");
                }
                else
                {
                    Add(checks, "Database Schema", "BLOCKER", "Operational core schema",
                        "Missing operational schema markers: " + string.Join(", ", missing) +
                        ". Run explicit Development Database Maintenance before relying on the affected workflow.");
                }
            }
            catch (OperationCanceledException)
            {
                Add(checks, "Database", "BLOCKER", "Operational readiness timeout",
                    "The bounded operational readiness probe did not complete in time. No database change was attempted.");
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)
            {
                ApplicationLogger.Error("Operational readiness database probe timed out or was blocked.", ex);
                Add(checks, "Database", "BLOCKER", "Operational database responsiveness",
                    "The database remained busy beyond the bounded readiness window. Wait for active database work to finish, then rerun preflight; no schema change was attempted.");
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Operational readiness database probe failed.", ex);
                Add(checks, "Database", "BLOCKER", "Operational database connectivity",
                    UserFacingError.SafeMessage(ex, "Operational readiness database connection"));
            }
            finally
            {
                _runGate.Release();
            }

            return BuildReport(checks, connectedServer, connectedDatabase);
        }

        public async Task<SystemPreflightReport> RunAsync()
        {
            await _runGate.WaitAsync().ConfigureAwait(false);
            var checks = new List<SystemPreflightCheck>();
            string connectedServer = string.Empty;
            string connectedDatabase = string.Empty;
            PreflightMaintenanceLease? maintenanceLease = null;

            CheckConfiguration(checks);

            try
            {
                try
                {
                    DataTable connectionInfo = await _database.ExecuteQueryAsync(@"
SELECT
    CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')) AS ServerName,
    DB_NAME() AS DatabaseName,
    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS SqlVersion;").ConfigureAwait(false);

                    if (connectionInfo.Rows.Count == 0)
                        throw new InvalidOperationException("SQL Server returned no connection identity row.");

                    DataRow row = connectionInfo.Rows[0];
                    connectedServer = Convert.ToString(row["ServerName"]) ?? string.Empty;
                    connectedDatabase = Convert.ToString(row["DatabaseName"]) ?? string.Empty;
                    string sqlVersion = Convert.ToString(row["SqlVersion"]) ?? string.Empty;

                    if (!connectedDatabase.Equals(AppConfig.DatabaseName, StringComparison.OrdinalIgnoreCase))
                    {
                        Add(checks, "Database", "BLOCKER", "Configured database identity",
                            $"Configured database is '{AppConfig.DatabaseName}' but the active connection is '{connectedDatabase}'.");
                        return BuildReport(checks, connectedServer, connectedDatabase);
                    }

                    Add(checks, "Database", "PASS", "Database connectivity",
                        $"Connected to {connectedServer} / {connectedDatabase}; SQL Server {sqlVersion}.");
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Error("Runtime system preflight database connection failed.", ex);
                    Add(checks, "Database", "BLOCKER", "Database connectivity",
                        BuildPreflightDatabaseFailureDetails(ex));
                    return BuildReport(checks, connectedServer, connectedDatabase);
                }

                try
                {
                    maintenanceLease = await TryAcquireFullPreflightMaintenanceLeaseAsync().ConfigureAwait(false);
                    if (maintenanceLease == null)
                    {
                        Add(checks, "Database", "BLOCKER", "Database lifecycle coordination",
                            "Database Maintenance currently holds the controlled schema-maintenance lease. Close this window and rerun preflight after maintenance finishes.");
                        return BuildReport(checks, connectedServer, connectedDatabase);
                    }

                    Add(checks, "Database", "PASS", "Database lifecycle coordination",
                        "A transaction-owned shared verification lease protects this preflight run from overlapping Database Maintenance. The lease is released automatically when preflight finishes.");
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Error("Runtime system preflight maintenance-coordination lease failed.", ex);
                    Add(checks, "Database", "BLOCKER", "Database lifecycle coordination",
                        BuildPreflightDatabaseFailureDetails(ex));
                    return BuildReport(checks, connectedServer, connectedDatabase);
                }

                string verificationStage = "Required operational objects";
                try
                {
                    verificationStage = "Required operational objects";
                    await CheckRequiredObjectsAsync(checks).ConfigureAwait(false);
                    verificationStage = "Release-critical database readiness";
                    await CheckReleaseCriticalSchemaContractsAsync(checks).ConfigureAwait(false);
                    if (checks.Any(item => item.Status == "BLOCKER" && item.Area == "Database Schema"))
                        return BuildReport(checks, connectedServer, connectedDatabase);

                    verificationStage = "Critical workflow columns";
                    await CheckCriticalSchemaColumnsAsync(checks).ConfigureAwait(false);
                    verificationStage = "Critical relational controls";
                    await CheckCriticalSchemaControlsAsync(checks).ConfigureAwait(false);

                    if (checks.Any(item => item.Status == "BLOCKER" && item.Area == "Database Schema"))
                        return BuildReport(checks, connectedServer, connectedDatabase);

                    verificationStage = "Migration ledger";
                    await CheckMigrationLedgerAsync(checks).ConfigureAwait(false);
                    if (checks.Any(item => item.Status == "BLOCKER" && item.Area == "Deployment"))
                        return BuildReport(checks, connectedServer, connectedDatabase);

                    verificationStage = "Controlled master data";
                    await CheckControlledMasterDataAsync(checks).ConfigureAwait(false);
                    verificationStage = "Historical EM evidence";
                    await CheckHistoricalEmSnapshotIntegrityAsync(checks).ConfigureAwait(false);
                    verificationStage = "Identity integrity";
                    await CheckIdentityIntegrityAsync(checks).ConfigureAwait(false);
                    verificationStage = "PRM certificate integrity";
                    await CheckPrmCertificateIntegrityAsync(checks).ConfigureAwait(false);
                    verificationStage = "Water certificate integrity";
                    await CheckWaterCertificateIntegrityAsync(checks).ConfigureAwait(false);
                    verificationStage = "Legacy reissue lifecycle";
                    await CheckLegacyCertificateReissueLifecycleAsync(checks).ConfigureAwait(false);
                    verificationStage = "Cross-module workflow state";
                    await CheckWorkflowIntegrityAsync(checks).ConfigureAwait(false);
                    verificationStage = "Protected compliance records";
                    await CheckComplianceRecordProtectionAsync(checks).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Error($"Runtime system preflight verification query failed at stage '{verificationStage}'.", ex);
                    if (IsPreflightTimeoutOrLock(ex))
                    {
                        Add(checks, "Database", "BLOCKER", "Preflight database responsiveness",
                            "Stage: " + verificationStage + ". " + BuildPreflightDatabaseFailureDetails(ex));
                    }
                    else
                    {
                        string diagnostic = ex is SqlException sqlException
                            ? " SQL error " + sqlException.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) + "."
                            : string.Empty;
                        Add(checks, "Database", "BLOCKER", "Preflight verification failure",
                            "Stage: " + verificationStage + ". The read-only verification check failed unexpectedly." + diagnostic +
                            " Review the PharmaLIMS application log for the recorded exception. No migration or workflow record was changed.");
                    }
                }

                return BuildReport(checks, connectedServer, connectedDatabase);
            }
            finally
            {
                maintenanceLease?.Dispose();
                _runGate.Release();
            }
        }

        private async Task<PreflightMaintenanceLease?> TryAcquireFullPreflightMaintenanceLeaseAsync()
        {
            SqlConnection connection = _database.CreateConnection();
            SqlTransaction? transaction = null;
            try
            {
                await connection.OpenAsync().ConfigureAwait(false);
                transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);

                await using SqlCommand command = new SqlCommand(
                    DatabaseLifecycleCoordinationContract.AcquirePreflightSharedTransactionLockSql,
                    connection,
                    transaction)
                {
                    CommandTimeout = Math.Max(10, AppConfig.CommandTimeoutSeconds)
                };

                object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                int lockResult = result == null || result == DBNull.Value
                    ? -999
                    : Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);

                if (lockResult >= 0)
                    return new PreflightMaintenanceLease(connection, transaction);

                try
                {
                    transaction.Rollback();
                }
                finally
                {
                    transaction.Dispose();
                    transaction = null;
                    connection.Dispose();
                }

                if (lockResult == -1)
                    return null;

                throw new InvalidOperationException(
                    DatabaseLifecycleCoordinationContract.DescribeNegativeResult(lockResult));
            }
            catch
            {
                try
                {
                    if (transaction?.Connection != null)
                        transaction.Rollback();
                }
                catch (Exception cleanupException)
                {
                    ApplicationLogger.Warning(
                        "Full System Preflight transaction-owned lifecycle lease rollback failed during cleanup.",
                        cleanupException);
                }

                transaction?.Dispose();
                connection.Dispose();
                throw;
            }
        }

        private sealed class PreflightMaintenanceLease : IDisposable
        {
            private SqlConnection? _connection;
            private SqlTransaction? _transaction;

            internal PreflightMaintenanceLease(SqlConnection connection, SqlTransaction transaction)
            {
                _connection = connection;
                _transaction = transaction;
            }

            public void Dispose()
            {
                SqlTransaction? transaction = _transaction;
                SqlConnection? connection = _connection;
                _transaction = null;
                _connection = null;

                try
                {
                    if (transaction?.Connection != null)
                        transaction.Rollback();
                }
                catch (Exception cleanupException)
                {
                    ApplicationLogger.Warning(
                        "Full System Preflight transaction-owned lifecycle lease rollback failed during disposal.",
                        cleanupException);
                }
                finally
                {
                    transaction?.Dispose();
                    connection?.Dispose();
                }
            }
        }

        private async Task<bool> IsDatabaseMaintenanceActiveAsync(CancellationToken cancellationToken)
        {
            object? result = await _database.ExecuteScalarAsync(@"
SELECT APPLOCK_TEST(N'public', N'PharmaLIMS.SchemaMigration', N'Shared', N'Session');",
                parameters: null,
                commandTimeoutSeconds: Math.Max(10, AppConfig.CommandTimeoutSeconds),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (result == null || result == DBNull.Value)
                throw new InvalidOperationException("SQL Server did not return a database-maintenance coordination result.");

            int compatible = Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
            if (compatible == 1)
                return false;
            if (compatible == 0)
                return true;

            throw new InvalidOperationException(
                "SQL Server returned an unexpected APPLOCK_TEST result for database-maintenance coordination: " +
                compatible.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        private static bool IsPreflightTimeoutOrLock(Exception exception)
        {
            if (exception is OperationCanceledException ||
                exception is TimeoutException ||
                (exception is SqlException sqlException && (sqlException.Number == -2 || sqlException.Number == 1222)))
            {
                return true;
            }

            return exception.InnerException != null && IsPreflightTimeoutOrLock(exception.InnerException);
        }

        private static SqlException? FindPreflightSqlException(Exception exception)
        {
            Exception? current = exception;
            while (current != null)
            {
                if (current is SqlException sqlException)
                    return sqlException;
                current = current.InnerException;
            }

            return null;
        }

        private static string BuildPreflightDatabaseFailureDetails(Exception exception)
        {
            SqlException? sqlException = FindPreflightSqlException(exception);
            string sqlDetail = sqlException == null
                ? string.Empty
                : $"SQL error {sqlException.Number}. ";

            if (IsPreflightTimeoutOrLock(exception))
            {
                return sqlDetail +
                       "The database did not complete the read-only preflight operation within the controlled window. " +
                       "No migration or workflow record was changed.";
            }

            return sqlDetail +
                   "The read-only preflight operation failed for a non-timeout reason. Review the recorded application log. " +
                   "No migration or workflow record was changed.";
        }

        private static void CheckConfiguration(List<SystemPreflightCheck> checks)
        {
            if (!AppConfig.IsProduction && !AppConfig.IsDevelopment)
            {
                Add(checks, "Configuration", "BLOCKER", "Environment name",
                    $"Unsupported environment '{AppConfig.EnvironmentName}'. Only Production or Development are controlled values.");
            }
            else
            {
                Add(checks, "Configuration", "PASS", "Environment name",
                    $"Environment is {AppConfig.EnvironmentName}.");
            }

            if (AppConfig.IsProduction && AppConfig.ApplyStartupDatabaseUpdates)
            {
                Add(checks, "Configuration", "BLOCKER", "Production migration mode",
                    "Production is configured to apply database updates at application startup.");
            }
            else
            {
                Add(checks, "Configuration", "PASS", "Startup database update policy",
                    AppConfig.ApplyStartupDatabaseUpdates
                        ? "Controlled startup database updates are enabled for Development."
                        : "Startup database updates are disabled.");
            }

            if (!AppConfig.EnforceAuditTrail)
                Add(checks, "Compliance", "BLOCKER", "Audit trail enforcement", "Audit trail enforcement is disabled.");
            else
                Add(checks, "Compliance", "PASS", "Audit trail enforcement", "Audit trail enforcement is enabled.");

            if (!AppConfig.EnforceElectronicSignatureStorage)
                Add(checks, "Compliance", "BLOCKER", "Electronic signature storage", "Electronic signature storage enforcement is disabled.");
            else
                Add(checks, "Compliance", "PASS", "Electronic signature storage", "Electronic signature storage enforcement is enabled.");

            bool sessionTimeoutInPolicy =
                AppConfig.SessionTimeoutMinutes >= AppConfig.MinimumSessionTimeoutMinutes &&
                AppConfig.SessionTimeoutMinutes <= AppConfig.MaximumSessionTimeoutMinutes;
            if (AppConfig.IsProduction && !sessionTimeoutInPolicy)
            {
                Add(checks, "Security", "BLOCKER", "Session timeout policy",
                    $"Configured idle timeout is {AppConfig.SessionTimeoutMinutes} minutes; Production requires " +
                    $"{AppConfig.MinimumSessionTimeoutMinutes}..{AppConfig.MaximumSessionTimeoutMinutes} minutes.");
            }
            else
            {
                Add(checks, "Security", "PASS", "Session timeout policy",
                    $"Configured idle timeout is {AppConfig.SessionTimeoutMinutes} minutes " +
                    $"(controlled range {AppConfig.MinimumSessionTimeoutMinutes}..{AppConfig.MaximumSessionTimeoutMinutes}).");
            }
        }

        private async Task CheckRequiredObjectsAsync(List<SystemPreflightCheck> checks)
        {
            const string sql = @"
DECLARE @Missing TABLE (ObjectName nvarchar(256) NOT NULL);
INSERT @Missing(ObjectName)
SELECT N'dbo.' + required.TableName
FROM (VALUES
    (N'Users'), (N'Samples'), (N'SampleTests'), (N'Tests'),
    (N'Certificates'), (N'CertificateDocumentSnapshots'), (N'CertificateLifecycleAudit'), (N'LegacyCertificateEvidenceReconciliations'),
    (N'AuditTrail'), (N'ElectronicSignatures'), (N'UserAdministrationSignatures'), (N'QualityEvents'),
    (N'QualityEventAffectedResults'), (N'QualityEventActions'), (N'QualityEventChecklistQuestions'), (N'QualityEventChecklistAnswers'),
    (N'QualityEventRootCauseWhys'), (N'QualityEventImpactAssessments'), (N'QualityEventCAPAItems'),
    (N'QualityEventRetesting'), (N'QualityEventDistribution'), (N'QualityEventInvestigationEvidenceHistory'), (N'QualityEventRelatedItems'),
    (N'QualityEventSignatures'), (N'QualityEventPrintHistory'),
    (N'WaterSamplingPoints'), (N'WaterTestProfiles'), (N'WaterTestProfileTests'), (N'WaterSpecifications'), (N'WaterTestProfileSignatures'),
    (N'Water_Plans'), (N'Water_PlanSamples'), (N'Water_PlanSampleTests'),
    (N'Water_PlanSampleAttempts'), (N'Water_PlanSignatures'),
    (N'LIMS_NumberSequences'), (N'MediaNumberSequences'), (N'CertificatePrintHistory'),
    (N'EM_Plans'), (N'EM_Schedules'), (N'EM_ScheduleAreas'), (N'EM_ScheduleSignatures'), (N'EM_SchedulePointSnapshots'), (N'EM_PlanSamples'), (N'EM_PlanSignatures'),
    (N'EM_Events'), (N'EM_EventPlates'), (N'EM_EventSignatures'), (N'EM_LimitSnapshotReconciliations'),
    (N'EM_GradeLimits'), (N'EM_GradeLimitSignatures'),
    (N'CultureMediaLots'), (N'MediaPreparations'), (N'MediaQualifications'), (N'CultureMediaSignatures'), (N'CultureMediaPrintHistory'),
    (N'CultureMediaQualificationRequirements'), (N'MediaQualificationRequirementSnapshots'),
    (N'PRM_NumberSequences'), (N'PRM_Samples'), (N'PRM_SampleTests'),
    (N'PRM_SpecificationTests'), (N'PRM_SpecificationSignatures'), (N'PRM_TimingMigrationHistory'), (N'PRM_TimingMigrationTestEvidence'), (N'PRM_SpecificationTimingReapprovalHistory'), (N'PRM_TimingGovernanceMigrationState'), (N'PRM_TimingQELegacyLinkCorrections'),
    (N'PRM_ElectronicSignatures'), (N'PRM_Certificates'), (N'PRM_CertificateHistory'), (N'PRM_CertificateSnapshots'),
    (N'EMTrendReviewSnapshots'),
    (N'LIMS_SchemaVersions')
) required(TableName)
WHERE OBJECT_ID(N'dbo.' + required.TableName, N'U') IS NULL;
SELECT ObjectName FROM @Missing ORDER BY ObjectName;";

            DataTable missing = await _database.ExecuteQueryAsync(sql).ConfigureAwait(false);
            if (missing.Rows.Count == 0)
            {
                Add(checks, "Database Schema", "PASS", "Required operational objects",
                    "Core Water, EM, Culture Media, PRM, audit, signature, certificate, and migration-ledger tables are present.");
                return;
            }

            string details = string.Join(", ", missing.Rows.Cast<DataRow>().Select(row => Convert.ToString(row[0]) ?? string.Empty));
            Add(checks, "Database Schema", "BLOCKER", "Required operational objects", "Missing: " + details);
        }

        private async Task CheckReleaseCriticalSchemaContractsAsync(List<SystemPreflightCheck> checks)
        {
            DataTable readiness = await _database.ExecuteQueryAsync(@"
SELECT 1 AS SortOrder,
       N'Identity concurrency contract' AS CheckName,
       N'dbo.Users.AuthenticationRowVersion' AS ContractName,
       N'ROWVERSION NOT NULL' AS ExpectedContract,
       N'20260910_000_Review_Result_And_Authentication_Concurrency / 20260911_000_Authentication_Login_Compatibility_Backfill' AS MigrationKey,
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.Users')
             AND name=N'AuthenticationRowVersion'
             AND system_type_id=189
             AND max_length=8
             AND is_nullable=0
       ) THEN 1 ELSE 0 END AS IsReady
UNION ALL
SELECT 2,
       N'Temporary-password enforcement contract',
       N'dbo.Users.MustChangePassword',
       N'BIT NOT NULL',
       N'20260915_000_User_Administration_Security_Hardening',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.Users')
             AND name=N'MustChangePassword'
             AND system_type_id=TYPE_ID(N'bit')
             AND is_nullable=0
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 3,
       N'Password-change evidence contract',
       N'dbo.Users.PasswordChangedAt',
       N'DATETIME2(0) NULL',
       N'20260915_000_User_Administration_Security_Hardening',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.Users')
             AND name=N'PasswordChangedAt'
             AND system_type_id=TYPE_ID(N'datetime2')
             AND scale=0
             AND is_nullable=1
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 4,
       N'User creation timestamp schema contract',
       N'dbo.Users.CreatedAt',
       N'DATETIME2(0)',
       N'20260917_000_User_Administration_Write_Compatibility',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.Users')
             AND name=N'CreatedAt'
             AND system_type_id=TYPE_ID(N'datetime2')
             AND scale=0
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 5,
       N'User update timestamp schema contract',
       N'dbo.Users.UpdatedAt',
       N'DATETIME2(0)',
       N'20260917_000_User_Administration_Write_Compatibility',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.Users')
             AND name=N'UpdatedAt'
             AND system_type_id=TYPE_ID(N'datetime2')
             AND scale=0
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 6,
       N'EM event concurrency contract',
       N'dbo.EM_Events.ResultRowVersion',
       N'ROWVERSION NOT NULL',
       N'20260910_000_Review_Result_And_Authentication_Concurrency',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.EM_Events')
             AND name=N'ResultRowVersion'
             AND system_type_id=189
             AND max_length=8
             AND is_nullable=0
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 7,
       N'EM plate concurrency contract',
       N'dbo.EM_EventPlates.ResultRowVersion',
       N'ROWVERSION NOT NULL',
       N'20260910_000_Review_Result_And_Authentication_Concurrency',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
             AND name=N'ResultRowVersion'
             AND system_type_id=189
             AND max_length=8
             AND is_nullable=0
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 8,
       N'Sample result concurrency contract',
       N'dbo.SampleTests.ResultRowVersion',
       N'ROWVERSION NOT NULL',
       N'20260910_000_Review_Result_And_Authentication_Concurrency',
       CASE WHEN EXISTS
       (
           SELECT 1 FROM sys.columns
           WHERE object_id=OBJECT_ID(N'dbo.SampleTests')
             AND name=N'ResultRowVersion'
             AND system_type_id=189
             AND max_length=8
             AND is_nullable=0
       ) THEN 1 ELSE 0 END
UNION ALL
SELECT 9,
       N'User administration signature evidence contract',
       N'dbo.UserAdministrationSignatures',
       N'append-only electronic-signature evidence schema',
       N'20260917_001_User_Administration_Signature_Evidence',
       CASE WHEN
           OBJECT_ID(N'dbo.UserAdministrationSignatures',N'U') IS NOT NULL
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
           AND EXISTS
           (
               SELECT 1 FROM sys.indexes
               WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND name=N'PK_UserAdministrationSignatures'
                 AND is_primary_key=1 AND is_unique=1 AND is_disabled=0
           )
           AND EXISTS
           (
               SELECT 1
               FROM sys.foreign_keys fk
               INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id=fk.object_id
               INNER JOIN sys.columns parentColumn ON parentColumn.object_id=fkc.parent_object_id AND parentColumn.column_id=fkc.parent_column_id
               INNER JOIN sys.columns referencedColumn ON referencedColumn.object_id=fkc.referenced_object_id AND referencedColumn.column_id=fkc.referenced_column_id
               WHERE fk.parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND fk.name=N'FK_UserAdministrationSignatures_TargetUser'
                 AND fk.referenced_object_id=OBJECT_ID(N'dbo.Users')
                 AND parentColumn.name=N'TargetUserID'
                 AND referencedColumn.name=N'UserID'
                 AND fk.is_disabled=0 AND fk.is_not_trusted=0
           )
           AND 3 =
           (
               SELECT COUNT(*)
               FROM sys.check_constraints
               WHERE parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND name IN
                 (
                     N'CK_UserAdministrationSignatures_ActionReason_Specific',
                     N'CK_UserAdministrationSignatures_Meaning_NotBlank',
                     N'CK_UserAdministrationSignatures_SignedBy_NotBlank'
                 )
                 AND is_disabled=0 AND is_not_trusted=0
           )
           AND EXISTS
           (
               SELECT 1
               FROM sys.default_constraints dc
               INNER JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id
               WHERE dc.parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND c.name=N'SignedAt'
                 AND UPPER(dc.definition) LIKE N'%SYSUTCDATETIME%'
           )
           AND EXISTS
           (
               SELECT 1
               FROM sys.default_constraints dc
               INNER JOIN sys.columns c ON c.object_id=dc.parent_object_id AND c.column_id=dc.parent_column_id
               WHERE dc.parent_object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND c.name=N'SourceApplication'
                 AND UPPER(dc.definition) LIKE N'%PHARMALIMS%'
           )
           AND EXISTS
           (
               SELECT 1 FROM sys.indexes
               WHERE object_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND name=N'IX_UserAdministrationSignatures_Target_SignedAt'
                 AND is_disabled=0
           )
           AND EXISTS
           (
               SELECT 1
               FROM sys.triggers triggerRow
               INNER JOIN sys.sql_modules module ON module.object_id=triggerRow.object_id
               WHERE triggerRow.parent_id=OBJECT_ID(N'dbo.UserAdministrationSignatures')
                 AND triggerRow.name=N'TRG_UserAdministrationSignatures_AppendOnly'
                 AND triggerRow.is_disabled=0
                 AND EXISTS (SELECT 1 FROM sys.trigger_events ev WHERE ev.object_id=triggerRow.object_id AND ev.type_desc=N'UPDATE')
                 AND EXISTS (SELECT 1 FROM sys.trigger_events ev WHERE ev.object_id=triggerRow.object_id AND ev.type_desc=N'DELETE')
                 AND LOWER(module.definition) LIKE N'%user administration signature evidence is append-only and cannot be updated or deleted%'
           )
       THEN 1 ELSE 0 END
ORDER BY SortOrder;").ConfigureAwait(false);

            foreach (DataRow row in readiness.Rows)
            {
                string checkName = Convert.ToString(row["CheckName"]) ?? "Release-critical schema contract";
                string contractName = Convert.ToString(row["ContractName"]) ?? string.Empty;
                string expected = Convert.ToString(row["ExpectedContract"]) ?? string.Empty;
                string migrationKey = Convert.ToString(row["MigrationKey"]) ?? string.Empty;
                bool isReady = Convert.ToInt32(row["IsReady"]) == 1;

                if (isReady)
                {
                    Add(checks, "Database Schema", "PASS", checkName,
                        contractName + " matches the required " + expected + " schema contract.");
                }
                else
                {
                    string deploymentAction = AppConfig.IsDevelopment
                        ? "Run the signed Development Database Maintenance action, then rerun System Preflight."
                        : "Apply the approved checksum-controlled deployment migration, then rerun System Preflight.";
                    Add(checks, "Database Schema", "BLOCKER", checkName,
                        "Migration Required: " + contractName + " is missing or incompatible. Expected " + expected +
                        ". Required controlled migration: " + migrationKey + ". " + deploymentAction);
                }
            }
        }

        private async Task CheckCriticalSchemaColumnsAsync(List<SystemPreflightCheck> checks)
        {
            DataTable missing = await _database.ExecuteQueryAsync(@"
DECLARE @Required TABLE(TableName sysname NOT NULL, ColumnName sysname NOT NULL);
INSERT @Required(TableName,ColumnName) VALUES
-- Identity / migration ledger required by preflight itself
(N'EM_EventPlates',N'ResultCalculationVersion'),
(N'Users',N'UserID'),(N'Users',N'Username'),(N'Users',N'IsActive'),(N'Users',N'PasswordHashNew'),(N'Users',N'PasswordSalt'),
(N'Tests',N'TestID'),(N'Tests',N'TestName'),(N'Tests',N'TestCategory'),(N'Tests',N'SortOrder'),(N'Tests',N'IsActive'),
(N'LIMS_SchemaVersions',N'VersionKey'),(N'LIMS_SchemaVersions',N'MigrationChecksum'),
-- Water controlled profiles / result workflow
(N'Certificates',N'CertificateID'),(N'Certificates',N'CertificateNumber'),(N'Certificates',N'SampleID'),(N'Certificates',N'CertificateStatus'),
(N'Certificates',N'Status'),(N'Certificates',N'IssueDate'),(N'Certificates',N'ReportHash'),(N'Certificates',N'ReissuedFromCertificateID'),
(N'CertificateDocumentSnapshots',N'CertificateID'),
(N'LegacyCertificateEvidenceReconciliations',N'ReconciliationID'),(N'LegacyCertificateEvidenceReconciliations',N'CertificateModule'),
(N'LegacyCertificateEvidenceReconciliations',N'CertificateID'),(N'LegacyCertificateEvidenceReconciliations',N'SupersedesReconciliationID'),
(N'LegacyCertificateEvidenceReconciliations',N'CertificateNumberSnapshot'),(N'LegacyCertificateEvidenceReconciliations',N'SampleIDSnapshot'),
(N'LegacyCertificateEvidenceReconciliations',N'IssueDateSnapshot'),(N'LegacyCertificateEvidenceReconciliations',N'CertificateStatusSnapshot'),
(N'LegacyCertificateEvidenceReconciliations',N'LegacyConditionSnapshot'),(N'LegacyCertificateEvidenceReconciliations',N'EvidenceReference'),
(N'LegacyCertificateEvidenceReconciliations',N'EvidenceSummary'),(N'LegacyCertificateEvidenceReconciliations',N'Disposition'),
(N'LegacyCertificateEvidenceReconciliations',N'Reason'),(N'LegacyCertificateEvidenceReconciliations',N'SignedBy'),
(N'LegacyCertificateEvidenceReconciliations',N'MeaningOfSignature'),(N'LegacyCertificateEvidenceReconciliations',N'SignedAt'),
(N'LegacyCertificateEvidenceReconciliations',N'ReconciliationSchemaVersion'),
(N'WaterTestProfiles',N'ProfileID'),(N'WaterTestProfiles',N'ProfileCode'),(N'WaterTestProfiles',N'IsActive'),
(N'WaterTestProfiles',N'ApprovalStatus'),(N'WaterTestProfiles',N'EffectiveFrom'),(N'WaterTestProfiles',N'EffectiveTo'),
(N'WaterTestProfiles',N'ApprovedBy'),(N'WaterTestProfiles',N'ApprovedAt'),(N'WaterTestProfiles',N'ControlledReference'),
(N'WaterTestProfileTests',N'ProfileID'),(N'WaterTestProfileTests',N'TestID'),(N'WaterTestProfileTests',N'IsActive'),
(N'WaterSpecifications',N'ProfileID'),(N'WaterSpecifications',N'TestID'),(N'WaterSpecifications',N'SpecificationText'),
(N'WaterSpecifications',N'ApprovalStatus'),(N'WaterSpecifications',N'IsActive'),
(N'WaterTestProfileSignatures',N'SignatureID'),(N'WaterTestProfileSignatures',N'ProfileID'),
(N'WaterTestProfileSignatures',N'ActionType'),(N'WaterTestProfileSignatures',N'MeaningOfSignature'),
(N'WaterTestProfileSignatures',N'ActionReason'),(N'WaterTestProfileSignatures',N'SignedBy'),(N'WaterTestProfileSignatures',N'SignedAt'),
(N'Water_Plans',N'WaterPlanID'),(N'Water_Plans',N'PlanNo'),(N'Water_Plans',N'WaterType'),(N'Water_Plans',N'SourceType'),
(N'Water_Plans',N'Frequency'),(N'Water_Plans',N'DueDate'),(N'Water_Plans',N'RequiredDate'),(N'Water_Plans',N'AnalysisProfile'),
(N'Water_Plans',N'Status'),(N'Water_Plans',N'Notes'),(N'Water_Plans',N'CreatedBy'),
(N'Water_PlanSamples',N'WaterPlanSampleID'),(N'Water_PlanSamples',N'WaterPlanID'),(N'Water_PlanSamples',N'PointID'),
(N'Water_PlanSamples',N'PointCode'),(N'Water_PlanSamples',N'PointName'),(N'Water_PlanSamples',N'Location'),
(N'Water_PlanSamples',N'AnalysisProfile'),(N'Water_PlanSamples',N'Status'),
(N'Water_PlanSampleTests',N'WaterPlanSampleID'),(N'Water_PlanSampleTests',N'TestID'),
(N'Water_PlanSampleAttempts',N'WaterPlanSampleID'),(N'Water_PlanSampleAttempts',N'SampleID'),(N'Water_PlanSampleAttempts',N'Outcome'),
(N'Water_PlanSignatures',N'WaterPlanID'),(N'Water_PlanSignatures',N'ActionType'),(N'Water_PlanSignatures',N'ActionReason'),
(N'Water_PlanSignatures',N'SignedBy'),(N'Water_PlanSignatures',N'UserRole'),(N'Water_PlanSignatures',N'MeaningOfSignature'),
(N'SampleTests',N'ResultValue'),(N'SampleTests',N'ResultStatus'),(N'SampleTests',N'LimitDescription'),
-- EM schedule snapshots / excursions / approved limit snapshots / signatures
(N'EM_Schedules',N'ScheduleID'),(N'EM_Schedules',N'ApprovalStatus'),(N'EM_Schedules',N'ApprovedPointCount'),
(N'EM_SchedulePointSnapshots',N'ScheduleID'),(N'EM_SchedulePointSnapshots',N'SnapshotSequence'),
(N'EM_SchedulePointSnapshots',N'AreaID'),(N'EM_SchedulePointSnapshots',N'MethodSnapshot'),(N'EM_SchedulePointSnapshots',N'LocationSnapshot'),
(N'EM_PlanSamples',N'CollectionExcursion'),(N'EM_PlanSamples',N'IncubationPhase1Excursion'),(N'EM_PlanSamples',N'IncubationPhase2Excursion'),
(N'EM_EventPlates',N'AlertLimitSnapshot'),(N'EM_EventPlates',N'ActionLimitSnapshot'),
(N'EM_EventPlates',N'ResultUnitSnapshot'),(N'EM_EventPlates',N'AirVolumeLitersSnapshot'),
(N'EM_LimitSnapshotReconciliations',N'ReconciliationID'),(N'EM_LimitSnapshotReconciliations',N'PlateID'),
(N'EM_LimitSnapshotReconciliations',N'AlertLimitSnapshot'),(N'EM_LimitSnapshotReconciliations',N'ActionLimitSnapshot'),
(N'EM_LimitSnapshotReconciliations',N'ResultUnitSnapshot'),(N'EM_LimitSnapshotReconciliations',N'AirVolumeLitersSnapshot'),
(N'EM_LimitSnapshotReconciliations',N'EvidenceReference'),(N'EM_LimitSnapshotReconciliations',N'Reason'),
(N'EM_LimitSnapshotReconciliations',N'SignedBy'),(N'EM_LimitSnapshotReconciliations',N'ReconciliationSchemaVersion'),
(N'EM_Events',N'Id'),(N'EM_Events',N'WorkflowStatus'),(N'EM_Events',N'ApprovedBy'),
(N'EM_GradeLimits',N'IsActive'),(N'EM_EventSignatures',N'EventID'),(N'EM_EventSignatures',N'ActionType'),
-- Quality Event linkage and evidence
(N'QualityEvents',N'QualityEventID'),(N'QualityEvents',N'CurrentStatus'),(N'QualityEvents',N'SourceModule'),(N'QualityEvents',N'SourceRecordID'),
(N'QualityEventInvestigationEvidenceHistory',N'HistoryID'),(N'QualityEventInvestigationEvidenceHistory',N'QualityEventID'),(N'QualityEventInvestigationEvidenceHistory',N'OldRowsJson'),(N'QualityEventInvestigationEvidenceHistory',N'NewRowsJson'),(N'QualityEventInvestigationEvidenceHistory',N'ChangeReason'),
(N'QualityEventAffectedResults',N'AffectedResultID'),(N'QualityEventAffectedResults',N'QualityEventID'),
(N'QualityEventAffectedResults',N'SampleTestID'),(N'QualityEventAffectedResults',N'SourceModule'),(N'QualityEventAffectedResults',N'SourceResultID'),
(N'QualityEventAffectedResults',N'ResultValue'),(N'QualityEventAffectedResults',N'SpecificationLimit'),
(N'QualityEventAffectedResults',N'SpecificationNumericLimit'),(N'QualityEventAffectedResults',N'EvidenceSchemaVersion'),
(N'QualityEventAffectedResults',N'FailureType'),
(N'QualityEventActions',N'QualityEventID'),(N'QualityEventActions',N'ActionType'),
(N'QualityEventChecklistQuestions',N'ExpectedAnswer'),(N'QualityEventChecklistQuestions',N'QuestionLogic'),
(N'QualityEventChecklistAnswers',N'QualityEventID'),(N'QualityEventChecklistAnswers',N'QuestionID'),
-- Culture Media qualification approval gate
(N'CultureMediaLots',N'ReceiptStatus'),(N'CultureMediaLots',N'ExpiryDate'),
(N'CultureMediaQualificationRequirements',N'RequirementID'),(N'CultureMediaQualificationRequirements',N'IsActive'),
(N'CultureMediaQualificationRequirements',N'ApprovalStatus'),(N'CultureMediaQualificationRequirements',N'ReviewedBy'),
(N'CultureMediaQualificationRequirements',N'ReviewedAt'),(N'CultureMediaQualificationRequirements',N'ApprovedBy'),
(N'CultureMediaQualificationRequirements',N'ApprovedAt'),(N'CultureMediaQualificationRequirements',N'MinimumIncubationHours'),
(N'CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours'),(N'CultureMediaQualificationRequirements',N'TimingConfirmedBy'),(N'CultureMediaQualificationRequirements',N'TimingConfirmedAt'),
(N'MediaQualifications',N'QualificationStartedAt'),(N'MediaQualifications',N'MinimumIncubationHoursSnapshot'),(N'MediaQualifications',N'IncubationCompletedAt'),
(N'MediaQualificationRequirementSnapshots',N'MediaQualificationID'),(N'MediaQualificationRequirementSnapshots',N'RequirementID'),
(N'MediaQualificationRequirementSnapshots',N'MinimumIncubationHoursSnapshot'),(N'MediaQualificationRequirementSnapshots',N'TimingConfirmedBy'),(N'MediaQualificationRequirementSnapshots',N'TimingConfirmedAt'),
-- PRM controlled specification master
(N'PRM_SpecificationTests',N'SpecificationTestID'),(N'PRM_SpecificationTests',N'ApprovalStatus'),
(N'PRM_SpecificationTests',N'ReviewedBy'),(N'PRM_SpecificationTests',N'ReviewedDate'),
(N'PRM_SpecificationTests',N'IsDefaultForCategory'),(N'PRM_SpecificationTests',N'ItemCode'),
(N'PRM_SpecificationTests',N'ProductionStage'),(N'PRM_SpecificationTests',N'CompendialReference'),(N'PRM_SpecificationTests',N'MinimumElapsedHours'),
(N'PRM_SampleTests',N'MinimumElapsedHours'),
(N'PRM_TimingMigrationHistory',N'HasControlledQualityEventEvidence'),(N'PRM_TimingMigrationHistory',N'PreviousAnalysisStartedDate'),(N'PRM_TimingMigrationHistory',N'AnalysisStartSignatureAt'),(N'PRM_TimingMigrationHistory',N'AnalysisStartProvenanceIssue'),
(N'PRM_TimingMigrationTestEvidence',N'SampleTestID'),(N'PRM_TimingMigrationTestEvidence',N'OriginalEnteredDate'),(N'PRM_TimingMigrationTestEvidence',N'EligibleAt'),(N'PRM_TimingMigrationTestEvidence',N'AnalysisStartSignatureAt'),(N'PRM_TimingMigrationTestEvidence',N'AnalysisStartProvenanceIssue'),
(N'PRM_TimingGovernanceMigrationState',N'StateKey'),(N'PRM_TimingGovernanceMigrationState',N'CompletedAt'),
(N'PRM_TimingQELegacyLinkCorrections',N'SampleID'),(N'PRM_TimingQELegacyLinkCorrections',N'TimingMigrationHistoryID'),(N'PRM_TimingQELegacyLinkCorrections',N'CorrectedAt'),
-- PRM workflow / certificates
(N'PRM_Samples',N'SampleID'),(N'PRM_Samples',N'SampleNumber'),(N'PRM_Samples',N'SampleStatus'),(N'PRM_Samples',N'ResultInterpretation'),(N'PRM_Samples',N'AnalysisStartedDate'),(N'PRM_Samples',N'AnalysisCompletedDate'),
(N'PRM_Samples',N'TimingReconciliationStatus'),(N'PRM_Samples',N'TimingReconciledBy'),(N'PRM_Samples',N'TimingReconciledAt'),(N'PRM_Samples',N'TimingReconciliationReason'),
(N'PRM_NumberSequences',N'SequenceName'),(N'PRM_NumberSequences',N'CurrentYear'),(N'PRM_NumberSequences',N'LastNumber'),
(N'PRM_Certificates',N'CertificateID'),(N'PRM_Certificates',N'CertificateNumber'),(N'PRM_Certificates',N'SampleID'),
(N'PRM_Certificates',N'CertificateType'),(N'PRM_Certificates',N'ReportTitle'),(N'PRM_Certificates',N'IssueDate'),
(N'PRM_Certificates',N'IssuedBy'),(N'PRM_Certificates',N'CertificateStatus'),(N'PRM_Certificates',N'RevisionNo'),
(N'PRM_Certificates',N'IsCancelled'),(N'PRM_Certificates',N'CancelledBy'),(N'PRM_Certificates',N'CancelledDate'),
(N'PRM_Certificates',N'CancellationReason'),(N'PRM_Certificates',N'ReissuedFromCertificateID'),
(N'PRM_Certificates',N'VerificationCode'),(N'PRM_Certificates',N'ReportHash'),(N'PRM_Certificates',N'CreatedBy'),(N'PRM_Certificates',N'CreatedDate'),
(N'PRM_CertificateHistory',N'CertificateID'),(N'PRM_CertificateHistory',N'ActionName'),(N'PRM_CertificateHistory',N'ActionBy'),(N'PRM_CertificateHistory',N'ActionDate'),(N'PRM_CertificateHistory',N'Reason'),
(N'PRM_CertificateSnapshots',N'CertificateID'),(N'PRM_CertificateSnapshots',N'SampleID'),(N'PRM_CertificateSnapshots',N'CertificateNumber'),
(N'PRM_CertificateSnapshots',N'HtmlContent'),(N'PRM_CertificateSnapshots',N'SnapshotHash'),(N'PRM_CertificateSnapshots',N'CreatedBy'),(N'PRM_CertificateSnapshots',N'CreatedAt'),
(N'PRM_ElectronicSignatures',N'SignatureID'),(N'PRM_ElectronicSignatures',N'SampleID'),(N'PRM_ElectronicSignatures',N'ActionType'),
(N'PRM_ElectronicSignatures',N'SignedBy'),(N'PRM_ElectronicSignatures',N'MeaningOfSignature'),(N'PRM_ElectronicSignatures',N'ActionReason'),(N'PRM_ElectronicSignatures',N'UserRole'),(N'PRM_ElectronicSignatures',N'SignedAt'),
-- External trend controlled approval snapshots
(N'EMTrendReviewSnapshots',N'MethodName'),(N'EMTrendReviewSnapshots',N'UnitName'),(N'EMTrendReviewSnapshots',N'PeriodDefinitionJson'),
(N'EMTrendReviewSnapshots',N'SourceBatchManifestSha256'),(N'EMTrendReviewSnapshots',N'SnapshotHashSha256'),
(N'EMTrendReviewSnapshots',N'ReviewerRole'),(N'EMTrendReviewSnapshots',N'SignatureMeaning'),(N'EMTrendReviewSnapshots',N'SignatureReason'),(N'EMTrendReviewSnapshots',N'SignedAt');

SELECT N'dbo.' + r.TableName + N'.' + r.ColumnName AS MissingColumn
FROM @Required r
WHERE OBJECT_ID(N'dbo.' + r.TableName,N'U') IS NULL
   OR COL_LENGTH(N'dbo.' + r.TableName,r.ColumnName) IS NULL
ORDER BY r.TableName,r.ColumnName;" ).ConfigureAwait(false);

            if (missing.Rows.Count == 0)
            {
                Add(checks, "Database Schema", "PASS", "Critical workflow columns",
                    "Water, EM, Quality Event, PRM certificate, and External Trend critical columns are present.");
            }
            else
            {
                Add(checks, "Database Schema", "BLOCKER", "Critical workflow columns",
                    "Missing/incomplete: " + JoinDetails(missing));
            }
        }

        private async Task CheckCriticalSchemaControlsAsync(List<SystemPreflightCheck> checks)
        {
            DataTable missing = await _database.ExecuteQueryAsync(@"
DECLARE @Missing TABLE(ControlName nvarchar(256) NOT NULL);

/* Review remediation: these must be actual advancing rowversions, not binary(8). */
INSERT @Missing(ControlName)
SELECT expected.TableName+N'.'+expected.ColumnName+N' ROWVERSION NOT NULL'
FROM (VALUES
    (N'dbo.Users',N'AuthenticationRowVersion'),
    (N'dbo.EM_Events',N'ResultRowVersion'),
    (N'dbo.EM_EventPlates',N'ResultRowVersion'),
    (N'dbo.SampleTests',N'ResultRowVersion')
) expected(TableName,ColumnName)
LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(expected.TableName) AND c.name=expected.ColumnName
WHERE c.column_id IS NULL OR c.system_type_id<>189 OR c.is_nullable<>0;
IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
    AND name=N'ResultCFU' AND system_type_id IN(106,108) AND precision=28 AND scale=12 AND is_nullable=1)
    INSERT @Missing VALUES(N'EM_EventPlates.ResultCFU DECIMAL(28,12) NULL');
IF NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.EM_EventPlates')
    AND name=N'ResultCalculationVersion' AND system_type_id=52 AND is_nullable=1)
    INSERT @Missing VALUES(N'EM_EventPlates.ResultCalculationVersion SMALLINT NULL');


/* Water controlled-master contract used by direct registration and Water Planning. */
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
      AND name=N'ProfileCode'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length=40
      AND is_nullable=0
      AND is_computed=0
)
    INSERT @Missing VALUES(N'WaterTestProfiles.ProfileCode NVARCHAR(20) NOT NULL');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
      AND name=N'IsActive'
      AND system_type_id=TYPE_ID(N'bit')
      AND is_nullable=0
      AND is_computed=0
)
    INSERT @Missing VALUES(N'WaterTestProfiles.IsActive BIT NOT NULL');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
      AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
      AND is_disabled=0
      AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'WaterTestProfileTests trusted FK to WaterTestProfiles');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileTests')
      AND referenced_object_id=OBJECT_ID(N'dbo.Tests')
      AND is_disabled=0
      AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'WaterTestProfileTests trusted FK to Tests');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.WaterSpecifications')
      AND referenced_object_id=OBJECT_ID(N'dbo.Tests')
      AND is_disabled=0
      AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'WaterSpecifications trusted FK to Tests');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.WaterSpecifications')
      AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
      AND is_disabled=0
      AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'WaterSpecifications trusted FK to WaterTestProfiles');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.WaterTestProfileSignatures')
      AND referenced_object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
      AND is_disabled=0
      AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'WaterTestProfileSignatures trusted FK to WaterTestProfiles');

IF OBJECT_ID(N'dbo.TRG_WaterTestProfileSignatures_AppendOnly',N'TR') IS NULL
    INSERT @Missing VALUES(N'WaterTestProfileSignatures append-only trigger');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.WaterTestProfiles')
      AND name=N'UX_WaterTestProfiles_OneActiveCode_20260907_000'
      AND is_unique=1
      AND has_filter=1
)
    INSERT @Missing VALUES(N'WaterTestProfiles one-active-version unique filtered index');

/* PRM Quality Event affected-result contract: PRM supports both numeric and qualitative evidence. */
IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'ResultValue'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND (max_length=-1 OR max_length>=400)
      AND is_computed=0
)
    INSERT @Missing VALUES(N'QualityEventAffectedResults.ResultValue NVARCHAR(200+)');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'SourceResultID'
      AND system_type_id=TYPE_ID(N'int')
      AND is_nullable=1
      AND is_computed=0
)
    INSERT @Missing VALUES(N'QualityEventAffectedResults.SourceResultID nullable INT');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'SampleTestID'
      AND system_type_id=TYPE_ID(N'int')
      AND is_nullable=1
      AND is_computed=0
)
    INSERT @Missing VALUES(N'QualityEventAffectedResults.SampleTestID nullable INT');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'SpecificationNumericLimit'
      AND system_type_id=TYPE_ID(N'decimal')
      AND is_computed=0
)
    INSERT @Missing VALUES(N'QualityEventAffectedResults.SpecificationNumericLimit DECIMAL');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'EvidenceSchemaVersion'
      AND system_type_id=TYPE_ID(N'tinyint')
      AND is_nullable=0
      AND is_computed=0
)
    INSERT @Missing VALUES(N'QualityEventAffectedResults.EvidenceSchemaVersion TINYINT NOT NULL');

IF OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NULL
    INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations');
ELSE
BEGIN
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NULL
        INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.LegacyQualityEventID');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NULL
        INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.ReplacementQualityEventID');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NULL
        INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.ElectronicSignatureID');
    IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NULL
        INSERT @Missing VALUES(N'PRM_QualityEventEvidenceReconciliations.ReconciliationSchemaVersion');
    IF OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002',N'TR') IS NULL
        INSERT @Missing VALUES(N'TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002');
END;

IF OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory',N'U') IS NULL
    INSERT @Missing VALUES(N'QualityEventInvestigationEvidenceHistory');
ELSE
BEGIN
    IF OBJECT_ID(N'dbo.TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001',N'TR') IS NULL
        INSERT @Missing VALUES(N'TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001');
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory')
          AND name=N'IX_QEInvestigationEvidenceHistory_Event_20260828_001'
          AND is_disabled=0
    )
        INSERT @Missing VALUES(N'IX_QEInvestigationEvidenceHistory_Event_20260828_001');
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory')
          AND name=N'CK_QEInvestigationEvidenceHistory_OldJson_20260828_001'
          AND is_disabled=0 AND is_not_trusted=0
    )
        INSERT @Missing VALUES(N'CK_QEInvestigationEvidenceHistory_OldJson_20260828_001');
    IF NOT EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory')
          AND name=N'CK_QEInvestigationEvidenceHistory_NewJson_20260828_001'
          AND is_disabled=0 AND is_not_trusted=0
    )
        INSERT @Missing VALUES(N'CK_QEInvestigationEvidenceHistory_NewJson_20260828_001');
END;

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'CK_PRM_SpecificationTests_Approval'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_PRM_SpecificationTests_Approval');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'CK_CultureMediaQualificationRequirements_Approval_20260823'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_CultureMediaQualificationRequirements_Approval_20260823');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'MinimumElapsedHours'
      AND system_type_id=TYPE_ID(N'decimal')
      AND precision=9 AND scale=2 AND is_nullable=1
)
    INSERT @Missing VALUES(N'PRM_SpecificationTests.MinimumElapsedHours schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
      AND name=N'MinimumElapsedHours'
      AND system_type_id=TYPE_ID(N'decimal')
      AND precision=9 AND scale=2 AND is_nullable=1
)
    INSERT @Missing VALUES(N'PRM_SampleTests.MinimumElapsedHours schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'MinimumIncubationHours'
      AND system_type_id=TYPE_ID(N'decimal')
      AND precision=9 AND scale=2 AND is_nullable=1
)
    INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.MinimumIncubationHours schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
      AND name=N'QualificationStartedAt'
      AND system_type_id=TYPE_ID(N'datetime2')
      AND scale=0 AND is_nullable=1
)
    INSERT @Missing VALUES(N'MediaQualifications.QualificationStartedAt schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
      AND name=N'MinimumIncubationHoursSnapshot'
      AND system_type_id=TYPE_ID(N'decimal')
      AND precision=9 AND scale=2 AND is_nullable=1
)
    INSERT @Missing VALUES(N'MediaQualifications.MinimumIncubationHoursSnapshot schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications')
      AND name=N'IncubationCompletedAt'
      AND system_type_id=TYPE_ID(N'datetime2')
      AND scale=0 AND is_nullable=1
)
    INSERT @Missing VALUES(N'MediaQualifications.IncubationCompletedAt schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'CK_PRM_SpecificationTests_MinimumElapsedHours_20260905'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_PRM_SpecificationTests_MinimumElapsedHours_20260905');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
      AND name=N'CK_PRM_SampleTests_MinimumElapsedHours_20260905'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_PRM_SampleTests_MinimumElapsedHours_20260905');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'CK_CultureMediaQualificationRequirements_MinHours_20260905'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_CultureMediaQualificationRequirements_MinHours_20260905');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.MediaQualifications')
      AND name=N'CK_MediaQualifications_MinHoursSnapshot_20260905'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_MediaQualifications_MinHoursSnapshot_20260905');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
      AND name=N'TR_PRM_TimingMigrationHistory_AppendOnly_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_PRM_TimingMigrationHistory_AppendOnly_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence')
      AND name=N'TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.PRM_SpecificationTimingReapprovalHistory')
      AND name=N'TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots')
      AND name=N'TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.PRM_TimingGovernanceMigrationState')
      AND name=N'TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.CultureMediaLots')
      AND name=N'TR_CultureMediaLots_FinalReleaseExpiryGate_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_CultureMediaLots_FinalReleaseExpiryGate_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.PRM_TimingQELegacyLinkCorrections')
      AND name=N'TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'CK_PRM_Samples_TimingReconciliation_20260906'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_PRM_Samples_TimingReconciliation_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'TimingReconciliationStatus'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length=60 AND is_nullable=0
)
    INSERT @Missing VALUES(N'PRM_Samples.TimingReconciliationStatus schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
      AND name=N'ReconciliationDisposition'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND is_nullable=0
      AND (max_length=-1 OR max_length>=160)
)
    INSERT @Missing VALUES(N'PRM_TimingMigrationHistory.ReconciliationDisposition >= NVARCHAR(80)');

IF EXISTS
(
    SELECT 1
    FROM dbo.PRM_Samples s
    INNER JOIN dbo.PRM_TimingQELegacyLinkCorrections c ON c.SampleID=s.SampleID
    WHERE UPPER(LTRIM(RTRIM(ISNULL(s.SampleStatus,N''))))=N'IN PROGRESS'
      AND UPPER(LTRIM(RTRIM(ISNULL(s.ReportStatus,N''))))=N'RESULTS ENTERED'
)
    INSERT @Missing VALUES(N'PRM corrected timing sample ReportStatus contract');

IF NOT EXISTS
(
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c
        ON c.object_id=dc.parent_object_id
       AND c.column_id=dc.parent_column_id
    WHERE dc.parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND c.name=N'TimingReconciliationStatus'
      AND UPPER(REPLACE(REPLACE(REPLACE(CONVERT(NVARCHAR(MAX),dc.definition),N'(',N''),N')',N''),N' ',N''))
          IN (N'N''NOTREQUIRED''', N'''NOTREQUIRED''')
)
    INSERT @Missing VALUES(N'PRM_Samples.TimingReconciliationStatus default Not Required');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'TimingReconciledBy'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length=240 AND is_nullable=1
)
    INSERT @Missing VALUES(N'PRM_Samples.TimingReconciledBy schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'TimingReconciledAt'
      AND system_type_id=TYPE_ID(N'datetime2')
      AND scale=0 AND is_nullable=1
)
    INSERT @Missing VALUES(N'PRM_Samples.TimingReconciledAt schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'TimingReconciliationReason'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length=2000 AND is_nullable=1
)
    INSERT @Missing VALUES(N'PRM_Samples.TimingReconciliationReason schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'TimingConfirmedMinimumIncubationHours'
      AND system_type_id=TYPE_ID(N'decimal')
      AND precision=9 AND scale=2 AND is_nullable=1
)
    INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.TimingConfirmedMinimumIncubationHours schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'TimingConfirmedBy'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND max_length=200 AND is_nullable=1
)
    INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.TimingConfirmedBy schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'TimingConfirmedAt'
      AND system_type_id=TYPE_ID(N'datetime2')
      AND scale=0 AND is_nullable=1
)
    INSERT @Missing VALUES(N'CultureMediaQualificationRequirements.TimingConfirmedAt schema contract');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots')
      AND name=N'CK_MediaQualificationReqSnapshots_MinHours'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_MediaQualificationReqSnapshots_MinHours');

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots')
      AND name=N'FK_MediaQualificationReqSnapshots_Qualification'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'FK_MediaQualificationReqSnapshots_Qualification');

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
      AND name=N'FK_PRM_SampleTests_SourceSpecification_20260824'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'FK_PRM_SampleTests_SourceSpecification_20260824');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'CK_PRM_Samples_SpecificationVersion_20260824'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_PRM_Samples_SpecificationVersion_20260824');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'IX_PRM_SpecificationTests_ExactScope_20260824'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'IX_PRM_SpecificationTests_ExactScope_20260824');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
      AND name=N'IX_PRM_SampleTests_FrozenSource_20260824'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'IX_PRM_SampleTests_FrozenSource_20260824');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.EM_GradeLimits')
      AND name=N'CK_EM_GradeLimits_NonNegative_20260819'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_EM_GradeLimits_NonNegative_20260819');

IF NOT EXISTS
(
    SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id=OBJECT_ID(N'dbo.EM_GradeLimits')
      AND name=N'CK_EM_GradeLimits_ActionGEAlert_20260819'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'CK_EM_GradeLimits_ActionGEAlert_20260819');

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.EM_GradeLimitSignatures')
      AND name=N'FK_EM_GradeLimitSignatures_GradeLimit'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'FK_EM_GradeLimitSignatures_GradeLimit');

/* v204 EM trend historical-context integrity. */
IF COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NULL INSERT @Missing VALUES(N'EM_Events.AreaCodeSnapshot');
IF COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NULL INSERT @Missing VALUES(N'EM_Events.AreaNameSnapshot');
IF COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NULL INSERT @Missing VALUES(N'EM_Events.GradeSnapshot');
IF COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NULL INSERT @Missing VALUES(N'EM_Events.AreaSnapshotSource');
IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.EM_Events')
      AND name=N'TRG_EM_Events_ProtectAreaSnapshot_20260830'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_EM_Events_ProtectAreaSnapshot_20260830');

/* v193 Water/EM planning integrity controls. */
IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.Water_PlanSampleTests')
      AND name=N'FK_Water_PlanSampleTests_Tests_20260828'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'FK_Water_PlanSampleTests_Tests_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.foreign_keys
    WHERE parent_object_id=OBJECT_ID(N'dbo.Water_PlanSampleAttempts')
      AND name=N'FK_Water_PlanSampleAttempts_Samples_20260828'
      AND is_disabled=0 AND is_not_trusted=0
)
    INSERT @Missing VALUES(N'FK_Water_PlanSampleAttempts_Samples_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.Water_PlanSampleTests')
      AND name=N'TRG_Water_PlanSampleTests_FreezeDistributed_20260828'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_Water_PlanSampleTests_FreezeDistributed_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.Water_PlanSamples')
      AND name=N'TRG_Water_PlanSamples_ProtectDistributed_20260828'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_Water_PlanSamples_ProtectDistributed_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.EM_SchedulePointSnapshots')
      AND name=N'TRG_EM_SchedulePointSnapshots_AppendOnly_20260828'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_EM_SchedulePointSnapshots_AppendOnly_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.EM_EventPlates')
      AND name=N'TRG_EM_EventPlates_FreezeLimits_20260828'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_EM_EventPlates_FreezeLimits_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.EM_EventPlates')
      AND name=N'TRG_EM_EventPlates_ProtectLimits_20260828'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_EM_EventPlates_ProtectLimits_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
      AND name=N'TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
      AND name=N'TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908');

IF OBJECT_ID(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909',N'FN') IS NULL
    INSERT @Missing VALUES(N'fn_LegacyCertificateEvidenceIsPlaceholder_20260909');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
      AND name=N'TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
      AND name=N'IX_LegacyCertificateEvidenceReconciliations_Certificate'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'IX_LegacyCertificateEvidenceReconciliations_Certificate');


IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
      AND name=N'UX_LegacyCertificateEvidenceReconciliations_Root'
      AND is_unique=1 AND is_disabled=0
)
    INSERT @Missing VALUES(N'UX_LegacyCertificateEvidenceReconciliations_Root');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations')
      AND name=N'UX_LegacyCertificateEvidenceReconciliations_Supersedes'
      AND is_unique=1 AND is_disabled=0
)
    INSERT @Missing VALUES(N'UX_LegacyCertificateEvidenceReconciliations_Supersedes');

IF NOT EXISTS
(
    SELECT 1 FROM sys.triggers
    WHERE parent_id=OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations')
      AND name=N'TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes
    WHERE object_id=OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations')
      AND name=N'IX_EM_LimitSnapshotReconciliations_Plate'
      AND is_disabled=0
)
    INSERT @Missing VALUES(N'IX_EM_LimitSnapshotReconciliations_Plate');

IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes AS i
    WHERE i.object_id=OBJECT_ID(N'dbo.EM_GradeLimits')
      AND i.is_unique=1 AND i.is_disabled=0 AND i.has_filter=0
      AND (SELECT COUNT(*) FROM sys.index_columns AS ic
           WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
      AND EXISTS
      (
          SELECT 1 FROM sys.index_columns AS ic
          INNER JOIN sys.columns AS c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
          WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id
            AND ic.key_ordinal=1 AND c.name=N'Id'
      )
)
    INSERT @Missing VALUES(N'Unique key on dbo.EM_GradeLimits(Id)');

SELECT ControlName FROM @Missing ORDER BY ControlName;" ).ConfigureAwait(false);

            if (missing.Rows.Count == 0)
            {
                Add(checks, "Database Schema", "PASS", "Critical relational controls",
                    "Quality Event immutable history plus PRM, Water, Culture Media, EM, and legacy-certificate evidence/quality controls are present, enabled, and trusted.");
            }
            else
            {
                Add(checks, "Database Schema", "BLOCKER", "Critical relational controls",
                    "Missing, disabled, or untrusted: " + JoinDetails(missing));
            }
        }

        private async Task CheckControlledMasterDataAsync(List<SystemPreflightCheck> checks)
        {
            DataTable result = await _database.ExecuteQueryAsync(@"
DECLARE @Expected TABLE(SortOrder int NOT NULL PRIMARY KEY);
INSERT @Expected(SortOrder) VALUES
(7900),(7910),(7920),(7930),(7940),(7950),(7960),(7970),(7980),
(8010),(8020),(8030),(8040),(8050),(8060),(8070),(8080),(8090),(8100),(8110),(8120),(8130),(8140),(8150),(8160),(8170),(8180),(8190),(8200),(8210),(8220),(8230),(8240),(8245),(8250),(8260),(8270),(8280),(8290),(8300),(8310),(8320),(8330),(8340),(8350),(8360),(8370);

IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NULL
BEGIN
    SELECT SortOrder FROM @Expected ORDER BY SortOrder;
    RETURN;
END;

SELECT e.SortOrder
FROM @Expected e
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.QualityEventChecklistQuestions q
    WHERE q.SortOrder=e.SortOrder
      AND ISNULL(q.IsActive,1)=1
      AND ISNULL(q.IsRequired,0)=1
      AND ISNULL(q.AppliesToTestCategory,N'')=N'Environmental Monitoring'
      AND ISNULL(q.ExpectedAnswer,N'')=N'Yes'
      AND ISNULL(q.QuestionLogic,N'')=N'PositiveCheck'
);" ).ConfigureAwait(false);

            if (result.Rows.Count == 0)
            {
                Add(checks, "Master Data", "PASS", "EM investigation checklist",
                    "All controlled Environmental Monitoring investigation checklist definitions are active and complete.");
            }
            else
            {
                Add(checks, "Master Data", "BLOCKER", "EM investigation checklist",
                    "Controlled EM checklist definitions are missing or inactive at SortOrder: " +
                    string.Join(", ", result.Rows.Cast<DataRow>().Select(row => Convert.ToString(row[0]) ?? string.Empty)) +
                    ". Run explicit Development Database Maintenance before operational use.");
            }

            int prmProfileGovernanceSchemaReady = await _database.ExecuteScalarAsync<int>(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_SpecificationSignatures',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ApprovedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ApprovedDate') IS NOT NULL
THEN 1 ELSE 0 END;").ConfigureAwait(false);

            if (prmProfileGovernanceSchemaReady != 1)
            {
                Add(checks, "Master Data", "WARNING", "PRM approved-profile signature evidence",
                    "PRM specification-signature governance objects are incomplete. Approved PRM profiles cannot be treated as authoritative until controlled migrations are applied.");
            }
            else
            {
                DataTable unsupportedApprovedProfiles = await _database.ExecuteQueryAsync(@"
SELECT DISTINCT
    configured.SpecificationNo,
    configured.SampleCategory,
    configured.VersionNo
FROM dbo.PRM_SpecificationTests configured
WHERE configured.ApprovalStatus=N'Approved'
  AND configured.IsActive=1
  AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate<=CAST(SYSDATETIME() AS date))
  AND NOT (
      ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913'
      AND (
          ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913'
          OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'
      )
  )
  AND NOT (
      NULLIF(LTRIM(RTRIM(ISNULL(configured.ReviewedBy,N''))),N'') IS NOT NULL
      AND configured.ReviewedDate IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(configured.ApprovedBy,N''))),N'') IS NOT NULL
      AND configured.ApprovedDate IS NOT NULL
      AND configured.ReviewedDate<=configured.ApprovedDate
      AND EXISTS
      (
          SELECT 1
          FROM dbo.PRM_SpecificationSignatures reviewSig
          INNER JOIN dbo.PRM_SpecificationSignatures approveSig
              ON approveSig.SpecificationNo=reviewSig.SpecificationNo
             AND approveSig.SampleCategory=reviewSig.SampleCategory
             AND approveSig.VersionNo=reviewSig.VersionNo
          WHERE reviewSig.SpecificationNo=configured.SpecificationNo
            AND reviewSig.SampleCategory=configured.SampleCategory
            AND reviewSig.VersionNo=configured.VersionNo
            AND reviewSig.ActionType=N'Review Specification'
            AND approveSig.ActionType=N'Approve Specification'
            AND UPPER(LTRIM(RTRIM(reviewSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ReviewedBy)))
            AND UPPER(LTRIM(RTRIM(approveSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ApprovedBy)))
            AND NULLIF(LTRIM(RTRIM(reviewSig.ActionReason)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(reviewSig.MeaningOfSignature)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(approveSig.ActionReason)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(approveSig.MeaningOfSignature)),N'') IS NOT NULL
            AND reviewSig.SignedAt<=approveSig.SignedAt
            AND (@RequireIndependentApprover=0 OR
                 UPPER(LTRIM(RTRIM(reviewSig.SignedBy)))<>UPPER(LTRIM(RTRIM(approveSig.SignedBy))))
      )
  )
ORDER BY configured.SampleCategory,configured.SpecificationNo,configured.VersionNo;",
                    new[]
                    {
                        new SqlParameter("@RequireIndependentApprover", SqlDbType.Bit) { Value = AppConfig.IsProduction }
                    }).ConfigureAwait(false);

                if (unsupportedApprovedProfiles.Rows.Count == 0)
                {
                    Add(checks, "Master Data", "PASS", "PRM approved-profile signature evidence",
                        "Every active Approved PRM profile has matching Review/Approve specification-signature evidence for its version.");
                }
                else
                {
                    string examples = string.Join(", ", unsupportedApprovedProfiles.Rows.Cast<DataRow>().Take(6).Select(row =>
                        (Convert.ToString(row["SpecificationNo"]) ?? string.Empty) + " / " +
                        (Convert.ToString(row["SampleCategory"]) ?? string.Empty) + " v" +
                        Convert.ToString(row["VersionNo"], System.Globalization.CultureInfo.InvariantCulture)));
                    Add(checks, "Master Data", AppConfig.IsProduction ? "BLOCKER" : "WARNING",
                        "PRM approved-profile signature evidence",
                        "Active Approved PRM profile versions without matching controlled Review/Approve signature evidence=" +
                        unsupportedApprovedProfiles.Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                        ". These versions are excluded from new PRM registration. Examples: " + examples +
                        ". Open Specification Master, create/review the intended controlled version, and complete Review Specification -> Approve Specification with electronic signatures.");
                }
            }

            int waterProfileSchemaReady = await _database.ExecuteScalarAsync<int>(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.WaterTestProfileSignatures',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.WaterTestProfiles',N'ControlledReference') IS NOT NULL
    AND COL_LENGTH(N'dbo.WaterSpecifications',N'ProfileID') IS NOT NULL
THEN 1 ELSE 0 END;").ConfigureAwait(false);

            if (waterProfileSchemaReady != 1)
            {
                Add(checks, "Master Data", "WARNING", "Water controlled test profiles",
                    "The v232 Water controlled-profile workflow schema is not installed yet. Run Database Maintenance before configuring PW/PTW master data.");
                return;
            }

            DataTable missingWaterProfiles = await _database.ExecuteQueryAsync(@"
DECLARE @ExpectedWaterProfiles TABLE(ProfileCode nvarchar(20) NOT NULL PRIMARY KEY);
INSERT @ExpectedWaterProfiles(ProfileCode) VALUES(N'PW'),(N'PTW');

SELECT expected.ProfileCode
FROM @ExpectedWaterProfiles expected
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.WaterTestProfiles profile
    WHERE UPPER(LTRIM(RTRIM(profile.ProfileCode)))=expected.ProfileCode
      AND ISNULL(profile.IsActive,0)=1
      AND profile.ApprovalStatus=N'Approved'
      AND profile.ApprovedBy IS NOT NULL
      AND profile.ApprovedAt IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(profile.ControlledReference,N''))),N'') IS NOT NULL
      AND (profile.EffectiveFrom IS NULL OR profile.EffectiveFrom<=CAST(GETDATE() AS date))
      AND (profile.EffectiveTo IS NULL OR profile.EffectiveTo>=CAST(GETDATE() AS date))
      AND EXISTS
      (
          SELECT 1
          FROM dbo.WaterTestProfileTests profileTest
          INNER JOIN dbo.Tests test
              ON test.TestID=profileTest.TestID
             AND ISNULL(test.IsActive,0)=1
          WHERE profileTest.ProfileID=profile.ProfileID
            AND ISNULL(profileTest.IsActive,1)=1
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.WaterTestProfileTests profileTest
          INNER JOIN dbo.Tests test
              ON test.TestID=profileTest.TestID
             AND ISNULL(test.IsActive,0)=1
          WHERE profileTest.ProfileID=profile.ProfileID
            AND ISNULL(profileTest.IsActive,1)=1
            AND NOT EXISTS
            (
                SELECT 1
                FROM dbo.WaterSpecifications specification
                WHERE specification.ProfileID=profile.ProfileID
                  AND specification.TestID=profileTest.TestID
                  AND ISNULL(specification.IsActive,0)=1
                  AND specification.ApprovalStatus=N'Approved'
                  AND (specification.PointCode IS NULL OR LTRIM(RTRIM(specification.PointCode))=N'')
                  AND NULLIF(LTRIM(RTRIM(specification.SpecificationText)),N'') IS NOT NULL
                  AND (specification.EffectiveFrom IS NULL OR specification.EffectiveFrom<=CAST(GETDATE() AS date))
                  AND (specification.EffectiveTo IS NULL OR specification.EffectiveTo>=CAST(GETDATE() AS date))
            )
      )
)
ORDER BY expected.ProfileCode;" ).ConfigureAwait(false);

            if (missingWaterProfiles.Rows.Count == 0)
            {
                Add(checks, "Master Data", "PASS", "Water controlled test profiles",
                    "PW and PTW each have one currently effective, approved controlled profile with active tests and approved specification evidence.");
            }
            else
            {
                Add(checks, "Master Data", "WARNING", "Water controlled test profiles",
                    "No complete currently effective approved controlled profile is configured for: " +
                    string.Join(", ", missingWaterProfiles.Rows.Cast<DataRow>().Select(row => Convert.ToString(row[0]) ?? string.Empty)) +
                    ". Direct Water sample registration remains fail-closed for the affected profile until versioned master data is reviewed and approved; existing distributed Water Plans retain their frozen test assignments.");
            }
        }


        private async Task CheckHistoricalEmSnapshotIntegrityAsync(List<SystemPreflightCheck> checks)
        {
            string evidenceSql = @"
;WITH EffectiveEvidence AS
(
    SELECT P.Id, ISNULL(E.WorkflowStatus,N'') AS WorkflowStatus, EVID.EvidenceComplete
    FROM dbo.EM_EventPlates P
    INNER JOIN dbo.EM_Events E ON E.Id=P.EventId
" + EmLimitEvidenceSql.Joins() + @"
)
SELECT
    SUM(CASE WHEN EvidenceComplete=0 THEN 1 ELSE 0 END) AS UnresolvedTotal,
    SUM(CASE WHEN EvidenceComplete=0 AND UPPER(LTRIM(RTRIM(WorkflowStatus))) NOT IN(N'APPROVED',N'COMPLETED',N'CANCELLED',N'CLOSED') THEN 1 ELSE 0 END) AS UnresolvedActive
FROM EffectiveEvidence;";
            DataTable result = await _database.ExecuteQueryAsync(evidenceSql).ConfigureAwait(false);

            int unresolvedTotal = result.Rows.Count == 0 || result.Rows[0]["UnresolvedTotal"] == DBNull.Value
                ? 0 : Convert.ToInt32(result.Rows[0]["UnresolvedTotal"]);
            int unresolvedActive = result.Rows.Count == 0 || result.Rows[0]["UnresolvedActive"] == DBNull.Value
                ? 0 : Convert.ToInt32(result.Rows[0]["UnresolvedActive"]);

            if (unresolvedActive > 0)
            {
                DataTable affectedEvents = await _database.ExecuteQueryAsync(@"
;WITH EffectiveEvidence AS
(
    SELECT P.Id,
           P.EventId,
           CASE WHEN P.AlertLimitSnapshot IS NOT NULL AND P.ActionLimitSnapshot IS NOT NULL
                      AND NULLIF(LTRIM(RTRIM(ISNULL(P.ResultUnitSnapshot,N''))),N'') IS NOT NULL
                      AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(P.AirVolumeLitersSnapshot,0)>0)
                THEN 1
                WHEN R.ReconciliationID IS NOT NULL THEN 1
                ELSE 0 END AS EvidenceComplete
    FROM dbo.EM_EventPlates P
    OUTER APPLY
    (
        SELECT TOP(1) X.ReconciliationID
        FROM dbo.EM_LimitSnapshotReconciliations X
        WHERE X.PlateID=P.Id
        ORDER BY X.ReconciliationID DESC
    ) R
)
SELECT TOP (20)
       E.Id AS EventID,
       ISNULL(NULLIF(LTRIM(RTRIM(E.EventNo)),N''),N'EventID ' + CONVERT(nvarchar(20),E.Id)) AS EventNo,
       COUNT_BIG(*) AS UnresolvedPlateCount
FROM EffectiveEvidence X
INNER JOIN dbo.EM_Events E ON E.Id=X.EventId
WHERE X.EvidenceComplete=0
  AND UPPER(LTRIM(RTRIM(ISNULL(E.WorkflowStatus,N'')))) NOT IN(N'APPROVED',N'COMPLETED',N'CANCELLED',N'CLOSED')
GROUP BY E.Id,E.EventNo
ORDER BY COUNT_BIG(*) DESC,E.Id;" ).ConfigureAwait(false);

                string affectedSummary = string.Join(", ", affectedEvents.Rows.Cast<DataRow>()
                    .Select(row =>
                    {
                        string eventNo = Convert.ToString(row["EventNo"]) ?? string.Empty;
                        string plateCount = Convert.ToString(row["UnresolvedPlateCount"]) ?? "0";
                        return eventNo + " (" + plateCount + " plate(s))";
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

                string detail =
                    $"{unresolvedActive} active EM plate(s) have incomplete frozen limit evidence and no signed reconciliation. " +
                    "Use EM Results Entry > Reconcile Historical Snapshot with the historically approved limits, controlled evidence reference, reason, and QA electronic signature before result workflow use. " +
                    $"Total unresolved historical plates: {unresolvedTotal}.";

                if (!string.IsNullOrWhiteSpace(affectedSummary))
                    detail += " Affected active event(s): " + affectedSummary + ".";

                Add(checks, "Environmental Monitoring", "BLOCKER", "Historical EM limit evidence", detail);
            }
            else if (unresolvedTotal > 0)
            {
                Add(checks, "Environmental Monitoring", "WARNING", "Historical EM limit evidence",
                    $"{unresolvedTotal} closed/historical EM plate(s) have incomplete frozen limit evidence and no signed reconciliation. " +
                    "Use System Preflight > Reconcile Historical EM only when controlled historical evidence is available; the original closed records remain unchanged. " +
                    "Until reconciled, they remain fail-closed if reopened or reprinted with live evaluation.");
            }
            else
            {
                Add(checks, "Environmental Monitoring", "PASS", "Historical EM limit evidence",
                    "All EM plates have either a native frozen limit snapshot or signed append-only reconciliation evidence.");
            }
        }

        
        private async Task CheckMigrationLedgerAsync(List<SystemPreflightCheck> checks)
        {
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "Database", "MigrationManifest.json");
            if (!File.Exists(manifestPath))
            {
                Add(checks, "Deployment", "BLOCKER", "Controlled migration manifest",
                    "Database/MigrationManifest.json is missing from the running application package.");
                return;
            }

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("migrations", out JsonElement migrations) || migrations.ValueKind != JsonValueKind.Array)
            {
                Add(checks, "Deployment", "BLOCKER", "Controlled migration manifest", "The manifest does not contain a migration list.");
                return;
            }

            DataTable ledger = await _database.ExecuteQueryAsync(@"
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(100)) AS VersionKey, CAST(NULL AS nvarchar(128)) AS MigrationChecksum WHERE 1=0;
END
ELSE
BEGIN
    SELECT VersionKey, MigrationChecksum FROM dbo.LIMS_SchemaVersions;
END").ConfigureAwait(false);

            var recorded = ledger.Rows.Cast<DataRow>()
                .Where(row => !row.IsNull("VersionKey"))
                .ToDictionary(
                    row => Convert.ToString(row["VersionKey"]) ?? string.Empty,
                    row => Convert.ToString(row["MigrationChecksum"]) ?? string.Empty,
                    StringComparer.Ordinal);

            var missingKeys = new List<string>();
            var checksumMismatches = new List<string>();
            var missingPackageFiles = new List<string>();
            var packageChecksumMismatches = new List<string>();
            string migrationsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", "Migrations")) +
                                    Path.DirectorySeparatorChar;

            foreach (JsonElement migration in migrations.EnumerateArray())
            {
                string versionKey = migration.GetProperty("versionKey").GetString() ?? string.Empty;
                string expectedHash = migration.GetProperty("sha256").GetString() ?? string.Empty;
                string relativeFile = migration.GetProperty("file").GetString() ?? string.Empty;
                string migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", relativeFile));

                if (string.IsNullOrWhiteSpace(relativeFile) ||
                    !migrationPath.StartsWith(migrationsRoot, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(migrationPath))
                {
                    missingPackageFiles.Add(versionKey);
                }
                else
                {
                    byte[] fileBytes = await File.ReadAllBytesAsync(migrationPath).ConfigureAwait(false);
                    string actualPackageHash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
                    if (!actualPackageHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        packageChecksumMismatches.Add(versionKey);
                }

                if (!recorded.TryGetValue(versionKey, out string? actualHash))
                {
                    string supersededBy = migration.TryGetProperty("supersededBy", out JsonElement supersededElement)
                        ? supersededElement.GetString() ?? string.Empty
                        : string.Empty;

                    if (!string.IsNullOrWhiteSpace(supersededBy))
                    {
                        string supersedingExpectedHash = string.Empty;
                        foreach (JsonElement candidate in migrations.EnumerateArray())
                        {
                            string candidateKey = candidate.GetProperty("versionKey").GetString() ?? string.Empty;
                            if (candidateKey.Equals(supersededBy, StringComparison.Ordinal))
                            {
                                supersedingExpectedHash = candidate.GetProperty("sha256").GetString() ?? string.Empty;
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(supersedingExpectedHash) &&
                            recorded.TryGetValue(supersededBy, out string? supersedingActualHash) &&
                            string.Equals(supersedingExpectedHash, supersedingActualHash, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    missingKeys.Add(versionKey);
                    continue;
                }

                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                    checksumMismatches.Add(versionKey);
            }

            if (missingPackageFiles.Count > 0 || packageChecksumMismatches.Count > 0 || checksumMismatches.Count > 0)
            {
                var details = new List<string>();
                if (missingPackageFiles.Count > 0)
                    details.Add("missing packaged SQL: " + string.Join(", ", missingPackageFiles));
                if (packageChecksumMismatches.Count > 0)
                    details.Add("packaged SQL hash mismatch: " + string.Join(", ", packageChecksumMismatches));
                if (checksumMismatches.Count > 0)
                    details.Add("database ledger checksum mismatch: " + string.Join(", ", checksumMismatches));

                Add(checks, "Deployment", "BLOCKER", "Migration checksums",
                    string.Join("; ", details) + ". The package, manifest, and database ledger must agree byte-for-byte.");
            }
            else
            {
                Add(checks, "Deployment", "PASS", "Migration checksums",
                    "Installed migration SQL bytes, packaged manifest hashes, and recorded database ledger checksums agree.");
            }

            if (missingKeys.Count > 0)
            {
                Add(checks, "Deployment", "BLOCKER", "Applied migrations",
                    "Controlled migration(s) not recorded in this database: " + string.Join(", ", missingKeys));
            }
            else
            {
                Add(checks, "Deployment", "PASS", "Applied migrations", "All manifest migrations are recorded in the database ledger.");
            }
        }

        private async Task CheckIdentityIntegrityAsync(List<SystemPreflightCheck> checks)
        {
            DataTable identityFindings = await _database.ExecuteQueryAsync(@"
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    SELECT N'Missing dbo.Users.' AS Details;
    RETURN;
END;

SELECT Details
FROM
(
    SELECT N'Blank username on UserID ' + CONVERT(nvarchar(20), UserID) AS Details
    FROM dbo.Users
    WHERE NULLIF(LTRIM(RTRIM(Username)), N'') IS NULL

    UNION ALL

    SELECT N'Duplicate normalized username: ' + LOWER(LTRIM(RTRIM(Username)))
    FROM dbo.Users
    WHERE NULLIF(LTRIM(RTRIM(Username)), N'') IS NOT NULL
    GROUP BY LOWER(LTRIM(RTRIM(Username)))
    HAVING COUNT_BIG(*) > 1
) findings;" ).ConfigureAwait(false);

            if (identityFindings.Rows.Count == 0)
                Add(checks, "Identity", "PASS", "User identity", "No blank or duplicate normalized usernames were detected.");
            else
                Add(checks, "Identity", "BLOCKER", "User identity", JoinDetails(identityFindings));

            DataTable legacyCredentialFindings = await _database.ExecuteQueryAsync(@"
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    SELECT N'Missing dbo.Users.' AS Details;
    RETURN;
END;

SELECT N'Active account pending one-time secure credential migration: ' +
       ISNULL(NULLIF(LTRIM(RTRIM(Username)),N''), N'UserID ' + CONVERT(nvarchar(20), UserID)) AS Details
FROM dbo.Users
WHERE ISNULL(IsActive,1) = 1
  AND
  (
      NULLIF(LTRIM(RTRIM(PasswordHashNew)),N'') IS NULL
      OR NULLIF(LTRIM(RTRIM(PasswordSalt)),N'') IS NULL
  );" ).ConfigureAwait(false);

            if (legacyCredentialFindings.Rows.Count == 0)
            {
                Add(checks, "Identity", "PASS", "Secure credentials",
                    "All active accounts use current secure credential fields.");
            }
            else
            {
                Add(checks, "Identity", AppConfig.IsProduction ? "BLOCKER" : "WARNING", "Secure credential transition",
                    JoinDetails(legacyCredentialFindings) +
                    (AppConfig.IsProduction
                        ? " Production use is blocked until every active account has a current PBKDF2-SHA256 credential. Complete the controlled password reset/migration or deactivate the account before release."
                        : " Each listed account is upgraded atomically to PBKDF2-SHA256 only after that account's existing password is successfully verified at interactive sign-in. No default password or authentication bypass is used."));
            }
        }

        private async Task CheckPrmCertificateIntegrityAsync(List<SystemPreflightCheck> checks)
        {
            DataTable findings = await _database.ExecuteQueryAsync(@"
DECLARE @Findings TABLE
(
    Severity nvarchar(20) NOT NULL,
    Details nvarchar(1000) NOT NULL
);
DECLARE @SnapshotCutover datetime2(0) = NULL;

IF OBJECT_ID(N'tempdb..#ResolvedLegacyCertificates',N'U') IS NOT NULL
    DROP TABLE #ResolvedLegacyCertificates;

CREATE TABLE #ResolvedLegacyCertificates
(
    CertificateModule nvarchar(20) NOT NULL,
    CertificateID int NOT NULL,
    PRIMARY KEY(CertificateModule,CertificateID)
);

IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909',N'FN') IS NOT NULL
BEGIN
    EXEC(N'
    ;WITH Ranked AS
    (
        SELECT CertificateModule,CertificateID,Disposition,EvidenceReference,EvidenceSummary,Reason,
               ROW_NUMBER() OVER(PARTITION BY CertificateModule,CertificateID ORDER BY ReconciliationID DESC) AS rn
        FROM dbo.LegacyCertificateEvidenceReconciliations
    )
    INSERT #ResolvedLegacyCertificates(CertificateModule,CertificateID)
    SELECT CertificateModule,CertificateID
    FROM Ranked
    WHERE rn=1
      AND Disposition=N''LEGACY_HISTORICAL_RECORD_RETAINED''
      AND dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(EvidenceReference)=0
      AND dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(EvidenceSummary)=0
      AND dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(Reason)=0;');
END;


IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'AppliedAt') IS NOT NULL
BEGIN
    SELECT TOP (1) @SnapshotCutover = AppliedAt
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey = N'20260722_003'
    ORDER BY AppliedAt;
END;

IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NOT NULL
BEGIN
    INSERT @Findings(Severity, Details)
    SELECT N'BLOCKER',
           N'PRM sample ' + CONVERT(nvarchar(20), SampleID) + N' has multiple active certificates.'
    FROM dbo.PRM_Certificates
    WHERE UPPER(ISNULL(CertificateStatus, N'')) = N'ACTIVE'
    GROUP BY SampleID
    HAVING COUNT_BIG(*) > 1;

    IF COL_LENGTH(N'dbo.PRM_Certificates', N'IssueDate') IS NULL
    BEGIN
        IF EXISTS
        (
            SELECT 1
            FROM dbo.PRM_Certificates
            WHERE UPPER(ISNULL(CertificateStatus, N'')) = N'ACTIVE'
        )
            INSERT @Findings(Severity, Details)
            VALUES(N'BLOCKER', N'PRM certificate snapshot-control cutover cannot be evaluated because IssueDate is unavailable.');
    END
    ELSE
    BEGIN
        INSERT @Findings(Severity, Details)
        SELECT
            CASE WHEN @SnapshotCutover IS NULL OR IssueDate >= @SnapshotCutover
                 THEN N'BLOCKER' ELSE N'WARNING' END,
            N'Active PRM certificate ' + ISNULL(CertificateNumber, N'(unknown)') +
            CASE WHEN @SnapshotCutover IS NOT NULL AND IssueDate < @SnapshotCutover
                 THEN N' predates immutable-snapshot control and has a legacy/non-current report hash.'
                 ELSE N' has an invalid report hash after immutable-snapshot control activation.' END
        FROM dbo.PRM_Certificates c
        WHERE UPPER(ISNULL(c.CertificateStatus, N'')) = N'ACTIVE'
          AND (c.ReportHash IS NULL OR LEN(LTRIM(RTRIM(c.ReportHash))) <> 64)
          AND NOT
          (
              @SnapshotCutover IS NOT NULL AND c.IssueDate < @SnapshotCutover
              AND EXISTS
              (
                  SELECT 1 FROM #ResolvedLegacyCertificates r
                  WHERE r.CertificateModule=N'PRM' AND r.CertificateID=c.CertificateID
              )
          );

        IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NOT NULL
        BEGIN
            INSERT @Findings(Severity, Details)
            SELECT
                CASE WHEN @SnapshotCutover IS NULL OR c.IssueDate >= @SnapshotCutover
                     THEN N'BLOCKER' ELSE N'WARNING' END,
                N'Active PRM certificate ' + ISNULL(c.CertificateNumber, N'(unknown)') +
                CASE WHEN @SnapshotCutover IS NOT NULL AND c.IssueDate < @SnapshotCutover
                     THEN N' predates immutable-snapshot control and has no native issue snapshot.'
                     ELSE N' has no immutable snapshot after immutable-snapshot control activation.' END
            FROM dbo.PRM_Certificates c
            WHERE UPPER(ISNULL(c.CertificateStatus, N'')) = N'ACTIVE'
              AND NOT EXISTS
              (
                  SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID = c.CertificateID
              )
              AND NOT
              (
                  @SnapshotCutover IS NOT NULL AND c.IssueDate < @SnapshotCutover
                  AND EXISTS
                  (
                      SELECT 1 FROM #ResolvedLegacyCertificates r
                      WHERE r.CertificateModule=N'PRM' AND r.CertificateID=c.CertificateID
                  )
              );
        END;
    END;
END;

IF OBJECT_ID(N'tempdb..#ResolvedLegacyCertificates',N'U') IS NOT NULL
    DROP TABLE #ResolvedLegacyCertificates;

SELECT Severity, Details
FROM @Findings
ORDER BY CASE Severity WHEN N'BLOCKER' THEN 0 ELSE 1 END, Details;" ).ConfigureAwait(false);

            List<string> blockers = findings.Rows.Cast<DataRow>()
                .Where(row => string.Equals(Convert.ToString(row["Severity"]), "BLOCKER", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["Details"]) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            List<string> warnings = findings.Rows.Cast<DataRow>()
                .Where(row => string.Equals(Convert.ToString(row["Severity"]), "WARNING", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["Details"]) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (blockers.Count == 0 && warnings.Count == 0)
            {
                Add(checks, "PRM", "PASS", "PRM certificate integrity",
                    "No duplicate active PRM certificates, unreconciled pre-cutover legacy evidence, or post-control hash/snapshot defects were detected.");
                return;
            }

            if (blockers.Count > 0)
            {
                Add(checks, "PRM", "BLOCKER", "PRM certificate integrity",
                    string.Join(" | ", blockers));
            }

            if (warnings.Count > 0)
            {
                if (AppConfig.IsDevelopment)
                {
                    Add(checks, "PRM", "PASS", "Development legacy PRM evidence",
                        string.Join(" | ", warnings) +
                        " These pre-control records are non-blocking in the Development environment. If they are disposable test data, do not fabricate/reconcile retrospective evidence and do not migrate them into Production. " +
                        "If regulated historical records were intentionally loaded into Development, use the signed reconciliation/reissue workflow before relying on them as controlled originals. Post-control defects remain BLOCKER findings.");
                }
                else
                {
                    Add(checks, "PRM", "WARNING", "Legacy PRM certificate evidence",
                        string.Join(" | ", warnings) +
                        " Use System Preflight > Reconcile Legacy Certificates for signed QA disposition when controlled historical evidence is available. " +
                        "If a prior signed reconciliation contains placeholder/non-evidence text, supersede it with meaningful evidence wording. " +
                        "A signed CONTROLLED_REISSUE_REQUIRED disposition documents the QA decision but intentionally does not clear this warning while the legacy certificate remains active; complete the controlled reissue in the PRM certificate workflow. " +
                        "Do not fabricate retrospective snapshots or hashes.");
                }
            }
        }

        
        private async Task CheckWaterCertificateIntegrityAsync(List<SystemPreflightCheck> checks)
        {
            DataTable findings = await _database.ExecuteQueryAsync(@"
DECLARE @Findings TABLE
(
    Severity nvarchar(20) NOT NULL,
    Details nvarchar(1000) NOT NULL
);
DECLARE @SnapshotCutover datetime2(0) = NULL;

IF OBJECT_ID(N'tempdb..#ResolvedLegacyCertificates',N'U') IS NOT NULL
    DROP TABLE #ResolvedLegacyCertificates;

CREATE TABLE #ResolvedLegacyCertificates
(
    CertificateModule nvarchar(20) NOT NULL,
    CertificateID int NOT NULL,
    PRIMARY KEY(CertificateModule,CertificateID)
);

IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909',N'FN') IS NOT NULL
BEGIN
    EXEC(N'
    ;WITH Ranked AS
    (
        SELECT CertificateModule,CertificateID,Disposition,EvidenceReference,EvidenceSummary,Reason,
               ROW_NUMBER() OVER(PARTITION BY CertificateModule,CertificateID ORDER BY ReconciliationID DESC) AS rn
        FROM dbo.LegacyCertificateEvidenceReconciliations
    )
    INSERT #ResolvedLegacyCertificates(CertificateModule,CertificateID)
    SELECT CertificateModule,CertificateID
    FROM Ranked
    WHERE rn=1
      AND Disposition=N''LEGACY_HISTORICAL_RECORD_RETAINED''
      AND dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(EvidenceReference)=0
      AND dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(EvidenceSummary)=0
      AND dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909(Reason)=0;');
END;


IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'AppliedAt') IS NOT NULL
BEGIN
    SELECT TOP (1) @SnapshotCutover = AppliedAt
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey = N'20260722_003'
    ORDER BY AppliedAt;
END;

IF OBJECT_ID(N'dbo.Certificates', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.Certificates', N'SampleID') IS NOT NULL
       AND COL_LENGTH(N'dbo.Certificates', N'CertificateStatus') IS NOT NULL
    BEGIN
        INSERT @Findings(Severity, Details)
        SELECT N'BLOCKER',
               N'Water/general sample ' + CONVERT(nvarchar(20), SampleID) + N' has multiple active certificates.'
        FROM dbo.Certificates
        WHERE UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(CertificateStatus)),N''),ISNULL(Status,N''))))) IN (N'ACTIVE', N'ISSUED')
        GROUP BY SampleID
        HAVING COUNT_BIG(*) > 1;
    END;

    IF OBJECT_ID(N'dbo.CertificateDocumentSnapshots', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.Certificates', N'CertificateID') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dbo.Certificates', N'IssueDate') IS NULL
        BEGIN
            IF EXISTS
            (
                SELECT 1
                FROM dbo.Certificates
                WHERE UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(CertificateStatus)),N''),ISNULL(Status,N''))))) IN (N'ACTIVE', N'ISSUED')
            )
                INSERT @Findings(Severity, Details)
                VALUES(N'BLOCKER', N'Water/general certificate snapshot-control cutover cannot be evaluated because IssueDate is unavailable.');
        END
        ELSE
        BEGIN
            INSERT @Findings(Severity, Details)
            SELECT
                CASE WHEN @SnapshotCutover IS NULL OR c.IssueDate >= @SnapshotCutover
                     THEN N'BLOCKER' ELSE N'WARNING' END,
                N'Active water/general certificate ' + ISNULL(c.CertificateNumber, N'(unknown)') +
                CASE WHEN @SnapshotCutover IS NOT NULL AND c.IssueDate < @SnapshotCutover
                     THEN N' predates immutable-snapshot control and has no native issue snapshot.'
                     ELSE N' has no immutable snapshot after immutable-snapshot control activation.' END
            FROM dbo.Certificates c
            WHERE UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''),ISNULL(c.Status,N''))))) IN (N'ACTIVE', N'ISSUED')
              AND NOT EXISTS
              (
                  SELECT 1 FROM dbo.CertificateDocumentSnapshots s WHERE s.CertificateID = c.CertificateID
              )
              AND NOT
              (
                  @SnapshotCutover IS NOT NULL AND c.IssueDate < @SnapshotCutover
                  AND EXISTS
                  (
                      SELECT 1 FROM #ResolvedLegacyCertificates r
                      WHERE r.CertificateModule=N'WATER' AND r.CertificateID=c.CertificateID
                  )
              );
        END;
    END;
END;

IF OBJECT_ID(N'tempdb..#ResolvedLegacyCertificates',N'U') IS NOT NULL
    DROP TABLE #ResolvedLegacyCertificates;

SELECT Severity, Details
FROM @Findings
ORDER BY CASE Severity WHEN N'BLOCKER' THEN 0 ELSE 1 END, Details;" ).ConfigureAwait(false);

            List<string> blockers = findings.Rows.Cast<DataRow>()
                .Where(row => string.Equals(Convert.ToString(row["Severity"]), "BLOCKER", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["Details"]) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            List<string> warnings = findings.Rows.Cast<DataRow>()
                .Where(row => string.Equals(Convert.ToString(row["Severity"]), "WARNING", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["Details"]) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

            if (blockers.Count == 0 && warnings.Count == 0)
            {
                Add(checks, "Water / Certificates", "PASS", "Certificate snapshot integrity",
                    "No duplicate active certificates, unreconciled pre-cutover legacy evidence, or post-control missing snapshots were detected.");
                return;
            }

            if (blockers.Count > 0)
            {
                Add(checks, "Water / Certificates", "BLOCKER", "Certificate snapshot integrity",
                    string.Join(" | ", blockers));
            }

            if (warnings.Count > 0)
            {
                if (AppConfig.IsDevelopment)
                {
                    Add(checks, "Water / Certificates", "PASS", "Development legacy certificate evidence",
                        string.Join(" | ", warnings) +
                        " These pre-control records are non-blocking in the Development environment. If they are disposable test data, do not fabricate/reconcile retrospective snapshots and do not migrate them into Production. " +
                        "If regulated historical records were intentionally loaded into Development, use the signed reconciliation/reissue workflow before relying on them as controlled originals. Post-control defects remain BLOCKER findings.");
                }
                else
                {
                    Add(checks, "Water / Certificates", "WARNING", "Legacy certificate snapshot evidence",
                        string.Join(" | ", warnings) +
                        " Use System Preflight > Reconcile Legacy Certificates for signed QA disposition when controlled historical evidence is available. " +
                        "If a prior signed reconciliation contains placeholder/non-evidence text, supersede it with meaningful evidence wording. " +
                        "A signed CONTROLLED_REISSUE_REQUIRED disposition documents the QA decision but intentionally does not clear this warning while the legacy certificate remains active; complete controlled cancellation/reissue in Water Results and issue the replacement certificate through the normal certificate workflow. " +
                        "Do not fabricate retrospective snapshots.");
                }
            }
        }

        
        private async Task CheckLegacyCertificateReissueLifecycleAsync(List<SystemPreflightCheck> checks)
        {
            DataTable findings = await _database.ExecuteQueryAsync(@"
CREATE TABLE #LegacyReissueFindings
(
    CertificateModule nvarchar(20) NOT NULL,
    CertificateNumber nvarchar(100) NOT NULL,
    Details nvarchar(1000) NOT NULL
);

IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PRM_Certificates',N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.PRM_CertificateSnapshots',N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Certificates',N'ReissuedFromCertificateID') IS NOT NULL
   AND COL_LENGTH(N'dbo.PRM_Certificates',N'ReportHash') IS NOT NULL
BEGIN
    EXEC(N'
    ;WITH Ranked AS
    (
        SELECT CertificateID,Disposition,
               ROW_NUMBER() OVER(PARTITION BY CertificateModule,CertificateID ORDER BY ReconciliationID DESC) AS rn
        FROM dbo.LegacyCertificateEvidenceReconciliations
        WHERE CertificateModule=N''PRM''
    )
    INSERT #LegacyReissueFindings(CertificateModule,CertificateNumber,Details)
    SELECT N''PRM'',ISNULL(oldc.CertificateNumber,N''(unknown)''),
           N''Signed CONTROLLED_REISSUE_REQUIRED disposition for legacy PRM certificate '' + ISNULL(oldc.CertificateNumber,N''(unknown)'') +
           N'' is incomplete: the original certificate is no longer active, but no active linked replacement with a valid report hash and native immutable snapshot was found.''
    FROM Ranked r
    JOIN dbo.PRM_Certificates oldc ON oldc.CertificateID=r.CertificateID
    WHERE r.rn=1
      AND r.Disposition=N''CONTROLLED_REISSUE_REQUIRED''
      AND UPPER(LTRIM(RTRIM(ISNULL(oldc.CertificateStatus,N''''))))<>N''ACTIVE''
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.PRM_Certificates replacement
          WHERE replacement.ReissuedFromCertificateID=oldc.CertificateID
            AND UPPER(LTRIM(RTRIM(ISNULL(replacement.CertificateStatus,N''''))))=N''ACTIVE''
            AND LEN(LTRIM(RTRIM(ISNULL(replacement.ReportHash,N''''))))=64
            AND EXISTS
            (
                SELECT 1 FROM dbo.PRM_CertificateSnapshots snap
                WHERE snap.CertificateID=replacement.CertificateID
            )
      );');
END;

IF OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.Certificates',N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Certificates',N'ReissuedFromCertificateID') IS NOT NULL
   AND COL_LENGTH(N'dbo.Certificates',N'ReportHash') IS NOT NULL
BEGIN
    EXEC(N'
    ;WITH Ranked AS
    (
        SELECT CertificateID,Disposition,
               ROW_NUMBER() OVER(PARTITION BY CertificateModule,CertificateID ORDER BY ReconciliationID DESC) AS rn
        FROM dbo.LegacyCertificateEvidenceReconciliations
        WHERE CertificateModule=N''WATER''
    )
    INSERT #LegacyReissueFindings(CertificateModule,CertificateNumber,Details)
    SELECT N''WATER'',ISNULL(oldc.CertificateNumber,N''(unknown)''),
           N''Signed CONTROLLED_REISSUE_REQUIRED disposition for legacy water/general certificate '' + ISNULL(oldc.CertificateNumber,N''(unknown)'') +
           N'' is incomplete: the original certificate is no longer active/issued, but no active linked replacement with a valid report hash and native immutable snapshot was found.''
    FROM Ranked r
    JOIN dbo.Certificates oldc ON oldc.CertificateID=r.CertificateID
    WHERE r.rn=1
      AND r.Disposition=N''CONTROLLED_REISSUE_REQUIRED''
      AND UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(oldc.CertificateStatus)),N''''),ISNULL(oldc.Status,N''''))))) NOT IN(N''ACTIVE'',N''ISSUED'')
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.Certificates replacement
          WHERE replacement.ReissuedFromCertificateID=oldc.CertificateID
            AND UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(replacement.CertificateStatus)),N''''),ISNULL(replacement.Status,N''''))))) IN(N''ACTIVE'',N''ISSUED'')
            AND LEN(LTRIM(RTRIM(ISNULL(replacement.ReportHash,N''''))))=64
            AND EXISTS
            (
                SELECT 1 FROM dbo.CertificateDocumentSnapshots snap
                WHERE snap.CertificateID=replacement.CertificateID
            )
      );');
END;

SELECT CertificateModule,CertificateNumber,Details
FROM #LegacyReissueFindings
ORDER BY CertificateModule,CertificateNumber;" ).ConfigureAwait(false);

            if (findings.Rows.Count == 0)
            {
                Add(checks, "Certificates", "PASS", "Legacy controlled reissue lifecycle",
                    "No signed legacy controlled-reissue decision is stranded after cancellation without a compliant linked replacement certificate/report. Active legacy certificates still awaiting reissue remain evaluated by the PRM and Water legacy-certificate checks above.");
                return;
            }

            Add(checks, "Certificates", "WARNING", "Legacy controlled reissue lifecycle",
                JoinDetails(findings) +
                " Open System Preflight > Reconcile Legacy Certificates, select the affected certificate, and use Open Controlled Reissue Workflow to complete replacement issuance through the original module workflow. " +
                "The historical certificate remains traceable; do not fabricate retrospective snapshots or hashes.");
        }


        private async Task CheckWorkflowIntegrityAsync(List<SystemPreflightCheck> checks)
        {
            DataTable findings = await _database.ExecuteQueryAsync(@"
DECLARE @Findings TABLE (Severity nvarchar(20) NOT NULL, Details nvarchar(1000) NOT NULL);

IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.PRM_Samples', N'SampleStatus') IS NOT NULL
       AND COL_LENGTH(N'dbo.PRM_Samples', N'ResultInterpretation') IS NOT NULL
    BEGIN
        INSERT @Findings(Severity, Details)
        SELECT N'BLOCKER', N'PRM sample ' + ISNULL(SampleNumber, CONVERT(nvarchar(20), SampleID)) + N' is approved but overall interpretation is not Conforms.'
        FROM dbo.PRM_Samples
        WHERE UPPER(ISNULL(SampleStatus, N'')) = N'APPROVED'
          AND UPPER(ISNULL(ResultInterpretation, N'')) NOT IN (N'CONFORMS', N'PASS');
    END;
END;

IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.EM_Events', N'WorkflowStatus') IS NOT NULL
   AND COL_LENGTH(N'dbo.EM_Events', N'ApprovedBy') IS NOT NULL
BEGIN
    INSERT @Findings(Severity, Details)
    SELECT N'BLOCKER', N'EM event ' + CONVERT(nvarchar(20), Id) + N' is Approved without ApprovedBy attribution.'
    FROM dbo.EM_Events
    WHERE UPPER(ISNULL(WorkflowStatus, N'')) = N'APPROVED'
      AND NULLIF(LTRIM(RTRIM(ApprovedBy)), N'') IS NULL;
END;

SELECT Severity, Details FROM @Findings ORDER BY Severity, Details;" ).ConfigureAwait(false);

            if (findings.Rows.Count == 0)
            {
                Add(checks, "Workflow", "PASS", "Cross-module workflow state", "No selected Approved-state attribution or interpretation inconsistency was detected.");
                return;
            }

            string blockerDetails = string.Join(" | ", findings.Rows.Cast<DataRow>()
                .Where(row => string.Equals(Convert.ToString(row["Severity"]), "BLOCKER", StringComparison.OrdinalIgnoreCase))
                .Select(row => Convert.ToString(row["Details"]) ?? string.Empty));

            if (!string.IsNullOrWhiteSpace(blockerDetails))
                Add(checks, "Workflow", "BLOCKER", "Cross-module workflow state", blockerDetails);
        }

        private async Task CheckComplianceRecordProtectionAsync(List<SystemPreflightCheck> checks)
        {
            DataTable result = await _database.ExecuteQueryAsync(
                ComplianceRecordProtectionContract.QuerySql).ConfigureAwait(false);

            var unprotected = result.Rows.Cast<DataRow>()
                .Where(row =>
                    Convert.ToInt32(row["TableExists"]) != 1 ||
                    Convert.ToInt32(row["HasExpectedEnabledTrigger"]) != 1 ||
                    Convert.ToInt32(row["ProtectsUpdate"]) != 1 ||
                    Convert.ToInt32(row["ProtectsDelete"]) != 1 ||
                    Convert.ToInt32(row["HasCanonicalAppendOnlyBody"]) != 1)
                .Select(row =>
                    $"{Convert.ToString(row["TableName"])} -> {Convert.ToString(row["TriggerName"])} " +
                    $"[table={Convert.ToInt32(row["TableExists"])}, enabled={Convert.ToInt32(row["HasExpectedEnabledTrigger"])}, " +
                    $"update={Convert.ToInt32(row["ProtectsUpdate"])}, " +
                    $"delete={Convert.ToInt32(row["ProtectsDelete"])}, " +
                    $"canonical={Convert.ToInt32(row["HasCanonicalAppendOnlyBody"])}]")
                .ToList();

            if (unprotected.Count == 0)
            {
                Add(checks, "Compliance", "PASS", "Protected compliance records",
                    "Every controlled compliance table exists and has its exact enabled append-only trigger with UPDATE/DELETE protection. SQL integration also behaviorally verifies that protected UPDATE/DELETE operations are rejected.");
                return;
            }

            string severity = AppConfig.IsProduction ? "BLOCKER" : "WARNING";
            Add(checks, "Compliance", severity, "Protected compliance records",
                "Append-only protection contract failed for: " + string.Join(" | ", unprotected) +
                ". Restore the controlled trigger using Database Maintenance/change control, then rerun System Preflight.");
        }

        private static string JoinDetails(DataTable table)
        {
            return string.Join(" | ", table.Rows.Cast<DataRow>()
                .Select(row => Convert.ToString(row[0]) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static void Add(List<SystemPreflightCheck> checks, string area, string status, string check, string details)
        {
            checks.Add(new SystemPreflightCheck
            {
                Area = area,
                Status = status,
                Check = check,
                Details = details
            });
        }

        private static SystemPreflightReport BuildReport(
            IReadOnlyList<SystemPreflightCheck> checks,
            string server,
            string database)
        {
            return new SystemPreflightReport
            {
                ExecutedAt = DateTime.Now,
                Server = server,
                Database = database,
                Checks = checks
            };
        }
    }
}
