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
    internal sealed class DatabaseMigrationException : Exception
    {
        internal string VersionKey { get; }
        internal string Stage { get; }
        internal int SqlErrorNumber { get; }
        internal string OperatorMessage { get; }

        internal DatabaseMigrationException(
            string versionKey,
            string stage,
            int sqlErrorNumber,
            string operatorMessage,
            Exception innerException)
            : base(operatorMessage, innerException)
        {
            VersionKey = versionKey;
            Stage = stage;
            SqlErrorNumber = sqlErrorNumber;
            OperatorMessage = operatorMessage;
        }

        internal static DatabaseMigrationException FromSql(
            string versionKey,
            string stage,
            SqlException exception)
        {
            string detail = BuildSafeSqlDetail(exception);
            string message =
                $"Controlled migration '{versionKey}' stopped during {stage}. {detail} " +
                "The failing migration was not recorded as successful.";

            return new DatabaseMigrationException(
                versionKey,
                stage,
                exception.Number,
                message,
                exception);
        }

        private static string BuildSafeSqlDetail(SqlException exception)
        {
            bool mayShowDatabaseDetail =
                exception.Number >= 50000 ||
                exception.Number is 102 or 156 or 207 or 208 or 245 or 515 or 547 or 8114 or 8152 or 2601 or 2627 or 2628 ||
                (exception.Number >= 11700 && exception.Number <= 11799);

            int sqlLine = exception.Errors.Count > 0 ? exception.Errors[0].LineNumber : 0;
            string lineText = sqlLine > 0 ? $" at SQL line {sqlLine}" : string.Empty;

            if (!mayShowDatabaseDetail)
                return $"SQL Server error {exception.Number}{lineText}.";

            string detail = (exception.Message ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            if (string.IsNullOrWhiteSpace(detail))
                return $"SQL Server error {exception.Number}{lineText}.";

            // SQL Server 2628 includes the table/column contract we need for
            // maintenance diagnosis, but can also echo a truncated business value.
            // Preserve the structural detail while redacting the value itself.
            if (exception.Number == 2628)
            {
                Match contract = Regex.Match(
                    detail,
                    @"^(?<contract>.*?\bcolumn\s+'[^']+'\.)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                detail = contract.Success
                    ? contract.Groups["contract"].Value.Trim() + " Truncated value redacted."
                    : "String or binary data would be truncated; the value was redacted.";
            }

            if (detail.Length > 320)
                detail = detail[..320].TrimEnd() + "...";

            return $"SQL Server error {exception.Number}{lineText}: {detail}";
        }
    }

    /// <summary>
    /// Applies additive, idempotent schema updates required by the installed application version.
    /// The migrator never drops tables, columns, constraints, or user data.
    /// </summary>
    public sealed partial class StartupDatabaseMigrator
    {
        private const string LegacyStartupBaselineKey = "20260722_003";
        private const string RetiredEmTrendHistoricalContextMigrationKey = "20260830_003";
        private const string TimingGatePrerequisiteMigrationKey = "20260905_000";
        private const string TimingGateMigrationKey = "20260905_001";
        private const string TimingGovernancePrerequisiteMigrationKey = "20260906_000";
        private const string TimingGovernanceDispositionWidthPrerequisiteMigrationKey = "20260906_000A";
        private const string TimingGovernanceDispositionWidthContractMigrationKey = "20260906_000B";
        private const string TimingGovernanceMigrationKey = "20260906_001";
        private const string PrmReportStatusContractRepairMigrationKey = "20260906_002A";
        private const string RetiredEmSnapshotRepairMigrationKey = "20260906_003";
        private const string CurrentEmSnapshotRepairMigrationKey = "20260906_004";
        internal const string AuthenticationLoginCompatibilityMigrationKey = "20260911_000";
        internal const string UserAdministrationSecurityMigrationKey = "20260915_000";
        private const int StartupMigrationLockTimeoutMilliseconds = 15000;
        private const int MigrationDdlLockTimeoutMilliseconds = 120000;
        private const int ResumableMigrationLockTimeoutMilliseconds = 120000;
        private const int ResumableMigrationStepCommandTimeoutSeconds = 180;
        private const string ResumableMigrationStepMarker = "-- PHARMALIMS_STEP:";
        private readonly DatabaseConnection _database;

        private sealed class ResumableMigrationStep
        {
            internal ResumableMigrationStep(string name, string sql)
            {
                Name = name;
                Sql = sql;
            }

            internal string Name { get; }
            internal string Sql { get; }
        }

        public event Action<string>? ProgressChanged;

        public StartupDatabaseMigrator(DatabaseConnection database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        private static void ValidateInstalledMigrationPackage()
        {
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "Database", "MigrationManifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("The controlled database migration manifest is missing from the running application package.", manifestPath);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            string manifestApplicationVersion = root.TryGetProperty("applicationVersion", out JsonElement manifestVersionElement)
                ? manifestVersionElement.GetString() ?? string.Empty
                : string.Empty;

            Assembly assembly = typeof(StartupDatabaseMigrator).Assembly;
            string runningApplicationVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion?
                .Split('+', StringSplitOptions.RemoveEmptyEntries)[0]
                .Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(runningApplicationVersion))
                runningApplicationVersion = assembly.GetName().Version?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(manifestApplicationVersion) ||
                !manifestApplicationVersion.Equals(runningApplicationVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Controlled database payload mismatch: running PharmaLIMS is '{runningApplicationVersion}', " +
                    $"but Database/MigrationManifest.json is '{manifestApplicationVersion}'. " +
                    "No migration was executed. Stop PharmaLIMS, delete the project's bin and obj folders, then Clean/Rebuild so the current controlled Database payload is copied to the output directory before running Database Maintenance again.");
            }

            if (!root.TryGetProperty("migrations", out JsonElement migrations) || migrations.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The controlled database migration manifest contains no migration list.");

            ValidateCompleteMigrationPayload(root, migrations);

            bool historicalTrendRetiredFound = false;
            bool snapshotRepairRetiredFound = false;
            bool currentFound = false;
            string historicalTrendRetiredRoute = string.Empty;
            string snapshotRepairRetiredRoute = string.Empty;
            int timingGatePrerequisiteIndex = -1;
            int timingGateIndex = -1;
            int timingGovernancePrerequisiteIndex = -1;
            int timingGovernanceDispositionWidthPrerequisiteIndex = -1;
            int timingGovernanceDispositionWidthContractIndex = -1;
            int timingGovernanceIndex = -1;
            int prmFinalReleaseMigrationIndex = -1;
            int prmReportStatusContractRepairIndex = -1;
            int migrationIndex = -1;
            foreach (JsonElement migration in migrations.EnumerateArray())
            {
                migrationIndex++;
                string versionKey = migration.GetProperty("versionKey").GetString() ?? string.Empty;
                if (versionKey.Equals(TimingGatePrerequisiteMigrationKey, StringComparison.Ordinal))
                    timingGatePrerequisiteIndex = migrationIndex;
                else if (versionKey.Equals(TimingGateMigrationKey, StringComparison.Ordinal))
                    timingGateIndex = migrationIndex;
                else if (versionKey.Equals(TimingGovernancePrerequisiteMigrationKey, StringComparison.Ordinal))
                    timingGovernancePrerequisiteIndex = migrationIndex;
                else if (versionKey.Equals(TimingGovernanceDispositionWidthPrerequisiteMigrationKey, StringComparison.Ordinal))
                    timingGovernanceDispositionWidthPrerequisiteIndex = migrationIndex;
                else if (versionKey.Equals(TimingGovernanceDispositionWidthContractMigrationKey, StringComparison.Ordinal))
                    timingGovernanceDispositionWidthContractIndex = migrationIndex;
                else if (versionKey.Equals(TimingGovernanceMigrationKey, StringComparison.Ordinal))
                    timingGovernanceIndex = migrationIndex;
                else if (versionKey.Equals("20260906_002", StringComparison.Ordinal))
                    prmFinalReleaseMigrationIndex = migrationIndex;
                else if (versionKey.Equals(PrmReportStatusContractRepairMigrationKey, StringComparison.Ordinal))
                    prmReportStatusContractRepairIndex = migrationIndex;
                if (versionKey.Equals(RetiredEmTrendHistoricalContextMigrationKey, StringComparison.Ordinal))
                {
                    historicalTrendRetiredFound = true;
                    historicalTrendRetiredRoute = migration.TryGetProperty("supersededBy", out JsonElement supersededElement)
                        ? supersededElement.GetString() ?? string.Empty
                        : string.Empty;
                }
                else if (versionKey.Equals(RetiredEmSnapshotRepairMigrationKey, StringComparison.Ordinal))
                {
                    snapshotRepairRetiredFound = true;
                    snapshotRepairRetiredRoute = migration.TryGetProperty("supersededBy", out JsonElement supersededElement)
                        ? supersededElement.GetString() ?? string.Empty
                        : string.Empty;
                }
                else if (versionKey.Equals(CurrentEmSnapshotRepairMigrationKey, StringComparison.Ordinal))
                {
                    currentFound = true;
                }
            }

            if (!historicalTrendRetiredFound || !snapshotRepairRetiredFound || !currentFound ||
                !historicalTrendRetiredRoute.Equals(CurrentEmSnapshotRepairMigrationKey, StringComparison.Ordinal) ||
                !snapshotRepairRetiredRoute.Equals(CurrentEmSnapshotRepairMigrationKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The running controlled Database payload is incomplete: both '{RetiredEmTrendHistoricalContextMigrationKey}' and '{RetiredEmSnapshotRepairMigrationKey}' must be retired in favor of '{CurrentEmSnapshotRepairMigrationKey}'. " +
                    "No migration was executed. Delete bin and obj, Clean/Rebuild, and run Database Maintenance again.");
            }

            if (timingGatePrerequisiteIndex < 0 || timingGateIndex < 0 ||
                timingGovernancePrerequisiteIndex < 0 ||
                timingGovernanceDispositionWidthPrerequisiteIndex < 0 ||
                timingGovernanceDispositionWidthContractIndex < 0 ||
                timingGovernanceIndex < 0 ||
                prmFinalReleaseMigrationIndex < 0 ||
                prmReportStatusContractRepairIndex < 0 ||
                timingGatePrerequisiteIndex >= timingGateIndex ||
                timingGovernancePrerequisiteIndex >= timingGovernanceDispositionWidthPrerequisiteIndex ||
                timingGovernanceDispositionWidthPrerequisiteIndex >= timingGovernanceDispositionWidthContractIndex ||
                timingGovernanceDispositionWidthContractIndex >= timingGovernanceIndex ||
                timingGovernanceIndex >= prmFinalReleaseMigrationIndex ||
                prmFinalReleaseMigrationIndex >= prmReportStatusContractRepairIndex)
            {
                throw new InvalidOperationException(
                    $"The running controlled Database payload is incomplete: '{TimingGatePrerequisiteMigrationKey}' must run before '{TimingGateMigrationKey}', " +
                    $"'{TimingGovernancePrerequisiteMigrationKey}' -> '{TimingGovernanceDispositionWidthPrerequisiteMigrationKey}' -> '{TimingGovernanceDispositionWidthContractMigrationKey}' -> '{TimingGovernanceMigrationKey}' must remain in that order, " +
                    $"and '20260906_002' must run before '{PrmReportStatusContractRepairMigrationKey}'. " +
                    "No migration was executed. Stop PharmaLIMS, delete bin and obj, Clean/Rebuild, and run Database Maintenance again.");
            }
        }

        public Task ApplyRequiredUpdatesAsync() => ApplyRequiredUpdatesCoreAsync(null);

        public Task ApplyRequiredUpdatesAsUserAsync(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("An authenticated user is required for interactive Database Maintenance.", nameof(username));

            return ApplyRequiredUpdatesCoreAsync(username.Trim());
        }

        private async Task ApplyRequiredUpdatesCoreAsync(string? authorizedUsername)
        {
            ValidateInstalledMigrationPackage();
            ApplicationLogger.Information("Controlled database update check started.");
            ProgressChanged?.Invoke("Quiescing pooled application database sessions...");
            SqlConnection.ClearAllPools();
            await Task.Delay(250).ConfigureAwait(false);
            ProgressChanged?.Invoke("Acquiring controlled database maintenance lease...");

            // Session-owned migration lease must never be returned to the SQL pool.
            // A physical close therefore guarantees release of the application lock.
            await using SqlConnection maintenanceLease = _database.CreateUnpooledConnection();
            await maintenanceLease.OpenAsync().ConfigureAwait(false);
            await AcquireMigrationSessionApplicationLockAsync(maintenanceLease).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(authorizedUsername))
            {
                await using SqlTransaction authorizationTransaction =
                    (SqlTransaction)await maintenanceLease.BeginTransactionAsync().ConfigureAwait(false);
                try
                {
                    DatabaseHelper.EnsureUserPermissionInTransaction(
                        maintenanceLease,
                        authorizationTransaction,
                        authorizedUsername,
                        "CanManageSettings",
                        "run Development Database Maintenance");
                    await authorizationTransaction.CommitAsync().ConfigureAwait(false);
                }
                catch
                {
                    await authorizationTransaction.RollbackAsync().ConfigureAwait(false);
                    throw;
                }
            }

            try
            {
                ProgressChanged?.Invoke("Inspecting controlled database baseline...");
                bool controlledFreshBaselinePresent = await ProvisionFreshDatabaseBaselineAsync().ConfigureAwait(false);
                if (controlledFreshBaselinePresent)
                {
                    ApplicationLogger.Information(
                        "Controlled fresh-install baseline verified. Applying or resuming every controlled migration from its original SQL bytes.");
                    await ApplyManifestMigrationsAsync(
                        null,
                        executeHistoricalMigrations: true,
                        skipUnrecordedHistoricalMigrations: false,
                        existingMaintenanceLease: maintenanceLease).ConfigureAwait(false);
                }
                else
                {
                    bool legacyBaselineRecorded = await IsLegacyStartupBaselineRecordedAsync().ConfigureAwait(false);
                    if (!legacyBaselineRecorded)
                    {
                        if (!AppConfig.IsDevelopment)
                        {
                            throw new InvalidOperationException(
                                $"The existing database has no checksum-verified '{LegacyStartupBaselineKey}' baseline. " +
                                "Automatic legacy ledger stamping is prohibited. Run the controlled legacy-baseline reconciliation before enabling updates.");
                        }

                        ApplicationLogger.Warning(
                            "Development database has no checksum-verified historical baseline. " +
                            "Entering non-destructive Development reconciliation mode: historical migrations are not stamped; " +
                            "current pending migrations will be applied from their controlled SQL bytes.");
                        await ApplyManifestMigrationsAsync(
                            null,
                            executeHistoricalMigrations: false,
                            skipUnrecordedHistoricalMigrations: true,
                            existingMaintenanceLease: maintenanceLease).ConfigureAwait(false);
                    }
                    else
                    {
                        ApplicationLogger.Information(
                            $"Checksum-verified legacy baseline '{LegacyStartupBaselineKey}' is recorded. Applying only unrecorded controlled migrations.");
                        await ApplyManifestMigrationsAsync(
                            null,
                            executeHistoricalMigrations: false,
                            skipUnrecordedHistoricalMigrations: false,
                            existingMaintenanceLease: maintenanceLease).ConfigureAwait(false);
                    }
                }
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)
            {
                throw new InvalidOperationException(
                    "Database Maintenance could not complete its controlled database check because the database remained busy. " +
                    "No incomplete migration is recorded as successful; maintenance can be run again after the blocking activity ends.",
                    ex);
            }
            finally
            {
                await ReleaseMigrationSessionApplicationLockAsync(maintenanceLease).ConfigureAwait(false);
            }

            ProgressChanged?.Invoke("Controlled database migrations complete.");
            ApplicationLogger.Information("Controlled database update check completed.");
        }

        public async Task ApplyControlledMigrationAsync(string versionKey)
        {
            if (string.IsNullOrWhiteSpace(versionKey))
                throw new ArgumentException("A controlled migration version key is required.", nameof(versionKey));

            string normalizedVersionKey = versionKey.Trim();
            ValidateInstalledMigrationPackage();
            ApplicationLogger.Information(
                $"On-demand controlled database migration check started for '{normalizedVersionKey}'.");

            await ApplyManifestMigrationsAsync(normalizedVersionKey).ConfigureAwait(false);

            ApplicationLogger.Information(
                $"On-demand controlled database migration check completed for '{normalizedVersionKey}'.");
        }

        private async Task<bool> IsLegacyStartupBaselineRecordedAsync()
        {
            // The exact controlled baseline and its checksum are required. A newer
            // text key or a NULL checksum is not proof that the baseline SQL ran.
            const string sql = @"
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
    SELECT CAST(0 AS INT);
ELSE IF COL_LENGTH(N'dbo.LIMS_SchemaVersions', N'VersionKey') IS NULL
    SELECT CAST(0 AS INT);
ELSE
    EXEC sys.sp_executesql N'
        SELECT CASE WHEN EXISTS
        (
            SELECT 1
            FROM dbo.LIMS_SchemaVersions
            WHERE VersionKey = @BaselineKey
              AND NULLIF(LTRIM(RTRIM(MigrationChecksum)), N'''') IS NOT NULL
        ) THEN 1 ELSE 0 END;',
        N'@BaselineKey NVARCHAR(100)',
        @BaselineKey = @BaselineKey;";

            return await _database.ExecuteScalarAsync<int>(
                sql,
                new[]
                {
                    new SqlParameter("@BaselineKey", SqlDbType.NVarChar, 100)
                    {
                        Value = LegacyStartupBaselineKey
                    }
                }).ConfigureAwait(false) == 1;
        }

        private static async Task ConfigureMigrationLockTimeoutAsync(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            await using SqlCommand command = new SqlCommand(
                $"SET LOCK_TIMEOUT {MigrationDdlLockTimeoutMilliseconds};",
                connection,
                transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private async Task ApplyManifestMigrationsAsync(string? onlyVersionKey = null)
        {
            await ApplyManifestMigrationsAsync(
                onlyVersionKey,
                executeHistoricalMigrations: false,
                skipUnrecordedHistoricalMigrations: false,
                existingMaintenanceLease: null).ConfigureAwait(false);
        }

        private async Task ApplyManifestMigrationsAsync(
            string? onlyVersionKey,
            bool executeHistoricalMigrations,
            bool skipUnrecordedHistoricalMigrations,
            SqlConnection? existingMaintenanceLease)
        {
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "Database", "MigrationManifest.json");
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("The controlled database migration manifest is missing.", manifestPath);

            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            string applicationVersion = root.TryGetProperty("applicationVersion", out JsonElement versionElement)
                ? versionElement.GetString() ?? string.Empty
                : string.Empty;
            string baselineThrough = root.TryGetProperty("baselineThrough", out JsonElement baselineElement)
                ? baselineElement.GetString() ?? string.Empty
                : string.Empty;

            if (!root.TryGetProperty("migrations", out JsonElement migrations) || migrations.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The controlled database migration manifest contains no migration list.");

            bool requestedMigrationFound = string.IsNullOrWhiteSpace(onlyVersionKey);

            // Full-chain updates keep one exclusive lease; single controlled migrations
            // acquire their own lease so preflight cannot interleave with migration.
            SqlConnection? ownedMaintenanceLease = null;
            if (existingMaintenanceLease == null)
            {
                // On-demand maintenance uses the same non-pooled lifecycle lease contract.
                ownedMaintenanceLease = _database.CreateUnpooledConnection();
                await ownedMaintenanceLease.OpenAsync().ConfigureAwait(false);
                await AcquireMigrationSessionApplicationLockAsync(ownedMaintenanceLease).ConfigureAwait(false);
                existingMaintenanceLease = ownedMaintenanceLease;
            }
            else if (existingMaintenanceLease.State != ConnectionState.Open)
            {
                throw new InvalidOperationException("The controlled database maintenance lease is not open.");
            }

            try
            {
                foreach (JsonElement migration in migrations.EnumerateArray())
                {
                    string versionKey = migration.GetProperty("versionKey").GetString() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(onlyVersionKey) &&
                        !versionKey.Equals(onlyVersionKey, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    requestedMigrationFound = true;
                    await ApplyManifestMigrationEntryAsync(
                        migration,
                        migrations,
                        applicationVersion,
                        baselineThrough,
                        executeHistoricalMigrations,
                        skipUnrecordedHistoricalMigrations,
                        onlyVersionKey).ConfigureAwait(false);
                }
            }
            finally
            {
                if (ownedMaintenanceLease != null)
                {
                    await ReleaseMigrationSessionApplicationLockAsync(ownedMaintenanceLease).ConfigureAwait(false);
                    await ownedMaintenanceLease.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (!requestedMigrationFound)
            {
                throw new InvalidOperationException(
                    $"Controlled database migration '{onlyVersionKey}' is not present in Database/MigrationManifest.json.");
            }
        }

        private async Task ApplyManifestMigrationEntryAsync(
            JsonElement migration,
            JsonElement migrations,
            string applicationVersion,
            string baselineThrough,
            bool executeHistoricalMigrations,
            bool skipUnrecordedHistoricalMigrations,
            string? onlyVersionKey)
        {
            string versionKey = migration.GetProperty("versionKey").GetString() ?? string.Empty;
            string description = migration.GetProperty("description").GetString() ?? versionKey;
            string relativeFile = migration.GetProperty("file").GetString() ?? string.Empty;
            string expectedHash = (migration.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant();
            string migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", relativeFile));
            string migrationsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", "Migrations")) + Path.DirectorySeparatorChar;

            if (string.IsNullOrWhiteSpace(versionKey) ||
                string.IsNullOrWhiteSpace(relativeFile) ||
                string.IsNullOrWhiteSpace(expectedHash) ||
                !migrationPath.StartsWith(migrationsRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(migrationPath))
            {
                throw new InvalidOperationException("Migration manifest entry is invalid or its SQL file is missing: " + versionKey);
            }

            byte[] migrationBytes = await File.ReadAllBytesAsync(migrationPath).ConfigureAwait(false);
            string actualHash = Convert.ToHexString(SHA256.HashData(migrationBytes)).ToLowerInvariant();
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Migration checksum mismatch: " + relativeFile);

            string supersededBy = migration.TryGetProperty("supersededBy", out JsonElement supersededElement)
                ? supersededElement.GetString() ?? string.Empty
                : string.Empty;
            string supersedingExpectedHash = string.Empty;
            if (!string.IsNullOrWhiteSpace(supersededBy))
            {
                foreach (JsonElement candidate in migrations.EnumerateArray())
                {
                    string candidateKey = candidate.GetProperty("versionKey").GetString() ?? string.Empty;
                    if (candidateKey.Equals(supersededBy, StringComparison.Ordinal))
                    {
                        supersedingExpectedHash = (candidate.GetProperty("sha256").GetString() ?? string.Empty).ToLowerInvariant();
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(supersedingExpectedHash))
                    throw new InvalidOperationException("Migration manifest supersedence target is missing: " + supersededBy);
            }

            bool isHistoricalBaseline = !string.IsNullOrWhiteSpace(baselineThrough) &&
                                        string.CompareOrdinal(versionKey, baselineThrough) <= 0;

            string executionMode = migration.TryGetProperty("executionMode", out JsonElement executionModeElement)
                ? executionModeElement.GetString() ?? string.Empty
                : string.Empty;
            bool isResumableCurrentState = executionMode.Equals("resumable", StringComparison.OrdinalIgnoreCase);

            ProgressChanged?.Invoke($"Checking {versionKey}...");

            if (isResumableCurrentState)
            {
                if (isHistoricalBaseline || !string.IsNullOrWhiteSpace(supersededBy))
                {
                    throw new InvalidOperationException(
                        $"Resumable migration '{versionKey}' must be a current, non-superseded migration entry.");
                }

                await ApplyResumableCurrentStateMigrationAsync(
                    versionKey,
                    description,
                    expectedHash,
                    applicationVersion,
                    migrationBytes).ConfigureAwait(false);
                ProgressChanged?.Invoke($"Completed {versionKey}.");
                return;
            }

            try
            {
                await _database.ExecuteInTransactionAsync(async (connection, transaction) =>
                {
                    await ConfigureMigrationLockTimeoutAsync(connection, transaction).ConfigureAwait(false);
                    await EnsureConnectedDatabaseAsync(connection, transaction).ConfigureAwait(false);
                    await EnsureSchemaVersioningAsync(connection, transaction).ConfigureAwait(false);

                    bool isRecorded;
                    string? recordedHash;
                    await using (SqlCommand lookup = new SqlCommand(@"
SELECT COUNT(1),MAX(MigrationChecksum)
FROM dbo.LIMS_SchemaVersions WITH (UPDLOCK,HOLDLOCK)
WHERE VersionKey=@VersionKey;", connection, transaction))
                    {
                        lookup.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        lookup.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                        await using SqlDataReader reader = await lookup.ExecuteReaderAsync().ConfigureAwait(false);
                        await reader.ReadAsync().ConfigureAwait(false);
                        isRecorded = reader.GetInt32(0) > 0;
                        recordedHash = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }

                    bool replayRecordedMigration = false;
                    if (!string.IsNullOrWhiteSpace(recordedHash))
                    {
                        if (!recordedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Recorded migration checksum does not match the controlled manifest: " + versionKey);

                        bool postconditionsSatisfied = await RecordedMigrationPostconditionsSatisfiedAsync(
                            versionKey,
                            connection,
                            transaction).ConfigureAwait(false);

                        if (postconditionsSatisfied)
                        {
                            await using SqlCommand alignVersion = new SqlCommand(@"
UPDATE dbo.LIMS_SchemaVersions
SET ApplicationVersion=COALESCE(NULLIF(ApplicationVersion,N''),@ApplicationVersion)
WHERE VersionKey=@VersionKey;", connection, transaction);
                            alignVersion.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            alignVersion.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                            alignVersion.Parameters.Add("@ApplicationVersion", SqlDbType.NVarChar, 50).Value = applicationVersion;
                            await alignVersion.ExecuteNonQueryAsync().ConfigureAwait(false);
                            return;
                        }

                        replayRecordedMigration = true;
                        ApplicationLogger.Warning(
                            $"Recorded migration '{versionKey}' has the expected checksum but required structural post-conditions are missing. " +
                            "Database Maintenance will replay the original checksum-controlled SQL bytes inside the current transaction; the existing ledger identity is not trusted as structural proof by itself.");
                        ProgressChanged?.Invoke($"Repairing recorded migration {versionKey} from its controlled SQL bytes...");
                    }

                    if (isRecorded && !replayRecordedMigration)
                    {
                        throw new InvalidOperationException(
                            "Recorded migration has no controlled checksum and cannot be auto-stamped: " + versionKey +
                            ". Run the controlled legacy-baseline reconciliation.");
                    }

                    if (!string.IsNullOrWhiteSpace(supersededBy))
                    {
                        string supersedingRecordedHash = string.Empty;
                        await using (SqlCommand supersedingLookup = new SqlCommand(@"
SELECT TOP 1 ISNULL(MigrationChecksum,N'')
FROM dbo.LIMS_SchemaVersions WITH (UPDLOCK,HOLDLOCK)
WHERE VersionKey=@VersionKey;", connection, transaction))
                        {
                            supersedingLookup.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            supersedingLookup.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = supersededBy;
                            object? supersedingValue = await supersedingLookup.ExecuteScalarAsync().ConfigureAwait(false);
                            supersedingRecordedHash = supersedingValue == null || supersedingValue == DBNull.Value
                                ? string.Empty
                                : Convert.ToString(supersedingValue) ?? string.Empty;
                        }

                        if (!string.IsNullOrWhiteSpace(supersedingRecordedHash) &&
                            !supersedingRecordedHash.Equals(supersedingExpectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "Recorded superseding migration checksum does not match the controlled manifest: " + supersededBy);
                        }

                        if (!string.IsNullOrWhiteSpace(onlyVersionKey))
                        {
                            throw new InvalidOperationException(
                                $"Controlled migration '{versionKey}' is retired and superseded by '{supersededBy}'. " +
                                "Runtime code must not request retired migrations.");
                        }

                        ApplicationLogger.Information(
                            $"Controlled database migration '{versionKey}' is retired and will be skipped in favor of current migration '{supersededBy}'.");
                        return;
                    }

                    if (isHistoricalBaseline && !executeHistoricalMigrations)
                    {
                        if (skipUnrecordedHistoricalMigrations)
                        {
                            ApplicationLogger.Warning(
                                "Development reconciliation skipped unrecorded historical migration without stamping it: " + versionKey);
                            return;
                        }

                        throw new InvalidOperationException(
                            "Historical migration is absent from the checksum-verified ledger and cannot be auto-stamped: " + versionKey +
                            ". Run the controlled legacy-baseline reconciliation.");
                    }

                    ApplicationLogger.Information($"Applying controlled database migration '{versionKey}': {description}");
                    ProgressChanged?.Invoke($"Applying {versionKey} - {description}");
                    await PrepareManifestMigrationCompatibilityAsync(versionKey, connection, transaction).ConfigureAwait(false);

                    string migrationSql = System.Text.Encoding.UTF8.GetString(migrationBytes).TrimStart('\uFEFF');
                    await using SqlCommand apply = new SqlCommand(migrationSql, connection, transaction);
                    apply.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 300);
                    await apply.ExecuteNonQueryAsync().ConfigureAwait(false);
                    ApplicationLogger.Information($"Controlled database migration '{versionKey}' SQL completed.");

                    await using SqlCommand record = new SqlCommand(@"
MERGE dbo.LIMS_SchemaVersions WITH (HOLDLOCK) AS target
USING (SELECT @VersionKey AS VersionKey) AS source
ON target.VersionKey=source.VersionKey
WHEN MATCHED THEN UPDATE SET
    Description=@Description,
    MigrationChecksum=@Checksum,
    ApplicationVersion=@ApplicationVersion
WHEN NOT MATCHED THEN INSERT
    (VersionKey,Description,MigrationChecksum,ApplicationVersion)
    VALUES(@VersionKey,@Description,@Checksum,@ApplicationVersion);", connection, transaction);
                    record.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    record.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                    record.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value = description;
                    record.Parameters.Add("@Checksum", SqlDbType.NVarChar, 128).Value = expectedHash;
                    record.Parameters.Add("@ApplicationVersion", SqlDbType.NVarChar, 50).Value = applicationVersion;
                    await record.ExecuteNonQueryAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)
            {
                throw new DatabaseMigrationException(
                    versionKey,
                    "schema execution",
                    ex.Number,
                    $"Controlled migration '{versionKey}' could not complete because the database remained busy. " +
                    "No ledger record for this migration was committed; Database Maintenance is restartable after the blocking activity ends.",
                    ex);
            }
            catch (SqlException ex)
            {
                throw DatabaseMigrationException.FromSql(versionKey, "schema execution", ex);
            }

            ProgressChanged?.Invoke($"Completed {versionKey}.");
        }

        private async Task ApplyResumableCurrentStateMigrationAsync(
            string versionKey,
            string description,
            string expectedHash,
            string applicationVersion,
            byte[] migrationBytes)
        {
            bool alreadyRecorded = false;

            try
            {
                await _database.ExecuteInTransactionAsync(async (connection, transaction) =>
                {
                    await ConfigureMigrationLockTimeoutAsync(connection, transaction).ConfigureAwait(false);
                    await EnsureConnectedDatabaseAsync(connection, transaction).ConfigureAwait(false);
                    await EnsureSchemaVersioningAsync(connection, transaction).ConfigureAwait(false);

                    bool isRecorded;
                    string? recordedHash;
                    await using (SqlCommand lookup = new SqlCommand(@"
SELECT COUNT(1),MAX(MigrationChecksum)
FROM dbo.LIMS_SchemaVersions WITH (UPDLOCK,HOLDLOCK)
WHERE VersionKey=@VersionKey;", connection, transaction))
                    {
                        lookup.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        lookup.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                        await using SqlDataReader reader = await lookup.ExecuteReaderAsync().ConfigureAwait(false);
                        await reader.ReadAsync().ConfigureAwait(false);
                        isRecorded = reader.GetInt32(0) > 0;
                        recordedHash = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }

                    if (!string.IsNullOrWhiteSpace(recordedHash))
                    {
                        if (!recordedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "Recorded migration checksum does not match the controlled manifest: " + versionKey);
                        }

                        await using SqlCommand alignVersion = new SqlCommand(@"
UPDATE dbo.LIMS_SchemaVersions
SET ApplicationVersion=COALESCE(NULLIF(ApplicationVersion,N''),@ApplicationVersion)
WHERE VersionKey=@VersionKey;", connection, transaction);
                        alignVersion.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        alignVersion.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                        alignVersion.Parameters.Add("@ApplicationVersion", SqlDbType.NVarChar, 50).Value = applicationVersion;
                        await alignVersion.ExecuteNonQueryAsync().ConfigureAwait(false);
                        alreadyRecorded = true;
                        return;
                    }

                    if (isRecorded)
                    {
                        throw new InvalidOperationException(
                            "Recorded migration has no controlled checksum and cannot be auto-stamped: " + versionKey +
                            ". Run the controlled legacy-baseline reconciliation.");
                    }
                }).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw DatabaseMigrationException.FromSql(versionKey, "migration ledger check", ex);
            }

            if (alreadyRecorded)
                return;

            ApplicationLogger.Information(
                $"Applying resumable current-state database migration '{versionKey}': {description}");
            ProgressChanged?.Invoke($"Applying {versionKey} - {description}");
            string currentStepLabel = "[0/?] preparing schema steps";

            try
            {
                await using SqlConnection connection = _database.CreateConnection();
                await connection.OpenAsync().ConfigureAwait(false);

                await using (SqlCommand session = new SqlCommand(
                    $"SET LOCK_TIMEOUT {ResumableMigrationLockTimeoutMilliseconds};",
                    connection))
                {
                    session.CommandTimeout = Math.Max(
                        AppConfig.CommandTimeoutSeconds,
                        (ResumableMigrationLockTimeoutMilliseconds / 1000) + 10);
                    await session.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                string migrationSql = Encoding.UTF8.GetString(migrationBytes).TrimStart('\uFEFF');
                List<ResumableMigrationStep> steps = SplitResumableMigrationSteps(migrationSql);
                for (int index = 0; index < steps.Count; index++)
                {
                    ResumableMigrationStep step = steps[index];
                    currentStepLabel = $"[{index + 1}/{steps.Count}] {step.Name}";
                    ProgressChanged?.Invoke($"Applying {versionKey} {currentStepLabel}");
                    ApplicationLogger.Information(
                        $"Applying resumable migration '{versionKey}' step {currentStepLabel}.");

                    await using SqlCommand applyStep = new SqlCommand(step.Sql, connection)
                    {
                        CommandTimeout = Math.Max(
                            AppConfig.CommandTimeoutSeconds,
                            ResumableMigrationStepCommandTimeoutSeconds)
                    };
                    await applyStep.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                ApplicationLogger.Information(
                    $"Resumable current-state database migration '{versionKey}' reached all post-condition checks.");
            }
            catch (SqlException ex) when (ex.Number == -2 || ex.Number == 1222)
            {
                throw new DatabaseMigrationException(
                    versionKey,
                    "resumable schema execution",
                    ex.Number,
                    $"Resumable migration '{versionKey}' stopped at {currentStepLabel} because the schema step remained blocked or exceeded the 180-second controlled step window. " +
                    "Completed idempotent steps remain committed, no migration ledger record was written, and Database Maintenance can resume safely from the guarded current state.",
                    ex);
            }
            catch (SqlException ex)
            {
                throw DatabaseMigrationException.FromSql(versionKey, $"resumable schema execution {currentStepLabel}", ex);
            }

            try
            {
                await _database.ExecuteInTransactionAsync(async (connection, transaction) =>
                {
                    await ConfigureMigrationLockTimeoutAsync(connection, transaction).ConfigureAwait(false);
                    await EnsureConnectedDatabaseAsync(connection, transaction).ConfigureAwait(false);
                    await EnsureSchemaVersioningAsync(connection, transaction).ConfigureAwait(false);

                    string? recordedHash;
                    bool isRecorded;
                    await using (SqlCommand verify = new SqlCommand(@"
SELECT COUNT(1),MAX(MigrationChecksum)
FROM dbo.LIMS_SchemaVersions WITH (UPDLOCK,HOLDLOCK)
WHERE VersionKey=@VersionKey;", connection, transaction))
                    {
                        verify.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        verify.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                        await using SqlDataReader reader = await verify.ExecuteReaderAsync().ConfigureAwait(false);
                        await reader.ReadAsync().ConfigureAwait(false);
                        isRecorded = reader.GetInt32(0) > 0;
                        recordedHash = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }

                    if (!string.IsNullOrWhiteSpace(recordedHash))
                    {
                        if (!recordedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "Recorded migration checksum does not match the controlled manifest: " + versionKey);
                        }
                        return;
                    }

                    if (isRecorded)
                    {
                        throw new InvalidOperationException(
                            "Recorded migration has no controlled checksum and cannot be auto-stamped: " + versionKey +
                            ". Run the controlled legacy-baseline reconciliation.");
                    }

                    await using SqlCommand record = new SqlCommand(@"
INSERT dbo.LIMS_SchemaVersions
    (VersionKey,Description,MigrationChecksum,ApplicationVersion)
VALUES
    (@VersionKey,@Description,@Checksum,@ApplicationVersion);", connection, transaction);
                    record.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    record.Parameters.Add("@VersionKey", SqlDbType.NVarChar, 100).Value = versionKey;
                    record.Parameters.Add("@Description", SqlDbType.NVarChar, 500).Value = description;
                    record.Parameters.Add("@Checksum", SqlDbType.NVarChar, 128).Value = expectedHash;
                    record.Parameters.Add("@ApplicationVersion", SqlDbType.NVarChar, 50).Value = applicationVersion;
                    if (await record.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
                        throw new InvalidOperationException("The controlled migration ledger record was not created: " + versionKey);
                }).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw DatabaseMigrationException.FromSql(versionKey, "migration ledger recording", ex);
            }
        }


        private static List<ResumableMigrationStep> SplitResumableMigrationSteps(string migrationSql)
        {
            ArgumentNullException.ThrowIfNull(migrationSql);

            var steps = new List<ResumableMigrationStep>();
            using var reader = new StringReader(migrationSql);
            var currentSql = new StringBuilder();
            string currentName = "Schema preparation";
            string? line;

            void FlushCurrentStep()
            {
                string sql = currentSql.ToString().Trim();
                currentSql.Clear();
                if (sql.Length == 0)
                    return;

                steps.Add(new ResumableMigrationStep(currentName, sql));
            }

            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(ResumableMigrationStepMarker, StringComparison.Ordinal))
                {
                    FlushCurrentStep();
                    currentName = trimmed[ResumableMigrationStepMarker.Length..].Trim();
                    if (string.IsNullOrWhiteSpace(currentName))
                        currentName = "Unnamed schema step";
                    continue;
                }

                currentSql.AppendLine(line);
            }

            FlushCurrentStep();
            if (steps.Count == 0)
                steps.Add(new ResumableMigrationStep("Schema reconciliation", migrationSql));

            return steps;
        }

        private static async Task AcquireMigrationSessionApplicationLockAsync(SqlConnection connection)
        {
            await using SqlCommand command = new SqlCommand(@"
DECLARE @LockResult INT;
EXEC @LockResult=sys.sp_getapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockMode=N'Exclusive',
    @LockOwner=N'Session',
    @LockTimeout=@LockTimeout;
IF @LockResult < 0
    THROW 53010, 'Database Maintenance is already active or a protected preflight is still finishing. Wait for that activity to complete and run maintenance again.', 1;", connection)
            {
                CommandTimeout = Math.Max(
                    AppConfig.CommandTimeoutSeconds,
                    (StartupMigrationLockTimeoutMilliseconds / 1000) + 5)
            };
            command.Parameters.Add("@LockTimeout", SqlDbType.Int).Value = StartupMigrationLockTimeoutMilliseconds;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        private static async Task ReleaseMigrationSessionApplicationLockAsync(SqlConnection connection)
        {
            bool releaseSucceeded = false;
            try
            {
                await using SqlCommand command = new SqlCommand(@"
DECLARE @ReleaseResult INT;
EXEC @ReleaseResult=sys.sp_releaseapplock
    @Resource=N'PharmaLIMS.SchemaMigration',
    @LockOwner=N'Session';
SELECT @ReleaseResult;", connection)
                {
                    CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 10)
                };

                object? result = await command.ExecuteScalarAsync().ConfigureAwait(false);
                int releaseResult = result == null || result == DBNull.Value ? -999 : Convert.ToInt32(result);
                releaseSucceeded = releaseResult >= 0;
                if (!releaseSucceeded)
                {
                    ApplicationLogger.Warning(
                        $"Database Maintenance could not explicitly release its lifecycle lease (result {releaseResult}). " +
                        "The SQL connection will be removed from the pool so the session lock cannot survive reuse.");
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning(
                    "Database Maintenance lifecycle lease release failed. The SQL connection will be removed from the pool. " + ex.Message);
            }
            finally
            {
                if (!releaseSucceeded)
                    SqlConnection.ClearPool(connection);
            }
        }

        private static async Task<bool> RecordedMigrationPostconditionsSatisfiedAsync(
            string versionKey,
            SqlConnection connection,
            SqlTransaction? transaction)
        {
            // A checksum-valid ledger row is evidence of file identity, not proof that
            // the corresponding schema survived older startup-migrator defects or
            // manual Development-era drift. Only migrations whose SQL is intentionally
            // idempotent are eligible for structural verification and controlled replay.
            string verificationSql = versionKey switch
            {
                "20260722_005" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'CompendialReference') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'IX_PRM_SpecificationTests_ItemCategoryStatus'
          AND is_disabled=0
    )
THEN 1 ELSE 0 END;",

                "20260811_001" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.ExternalTrendImportRows',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name=N'AreaClassification'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND max_length=60
          AND is_nullable=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name=N'CK_ExternalTrendImportRows_AreaClassification'
          AND is_disabled=0
          AND is_not_trusted=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.ExternalTrendImportRows')
          AND name=N'IX_ExternalTrendImportRows_ClassificationTrend'
          AND is_disabled=0
    )
THEN 1 ELSE 0 END;",

                "20260819_001" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.EM_GradeLimits',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_GradeLimits',N'Id') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_GradeLimits',N'IsActive') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_EventPlates',N'AlertLimitSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_EventPlates',N'ActionLimitSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_EventPlates',N'ResultUnitSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_EventPlates',N'AirVolumeLitersSnapshot') IS NOT NULL
    AND OBJECT_ID(N'dbo.EM_GradeLimitSignatures',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.EM_GradeLimits')
          AND name=N'CK_EM_GradeLimits_NonNegative_20260819'
          AND is_disabled=0 AND is_not_trusted=0
    )
    AND EXISTS
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
THEN 1 ELSE 0 END;",

                "20260822_006" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NOT NULL
    AND NOT EXISTS
    (
        SELECT 1 FROM dbo.PRM_SpecificationTests
        WHERE NOT
        (
            (ApprovalStatus=N'Draft' AND IsActive=0 AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
            OR (ApprovalStatus=N'Reviewed' AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
                AND IsActive=0 AND ApprovedBy IS NULL AND ApprovedDate IS NULL)
            OR (ApprovalStatus=N'Approved' AND ReviewedBy IS NOT NULL AND ReviewedDate IS NOT NULL
                AND ApprovedBy IS NOT NULL AND ApprovedDate IS NOT NULL)
            OR (ApprovalStatus=N'Obsolete' AND IsActive=0)
        )
    )
THEN 1 ELSE 0 END;",

                "20260823_001" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
          AND name=N'CK_PRM_SpecificationTests_Approval'
          AND is_disabled=0 AND is_not_trusted=0
    )
THEN 1 ELSE 0 END;",

                "20260825_007" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'QualityEventID') IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM dbo.QualityEvents WHERE QualityEventID IS NULL)
    AND (OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NULL OR COL_LENGTH(N'dbo.QualityEventAffectedResults',N'AffectedResultID') IS NOT NULL)
    AND (OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NULL OR COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventActionID') IS NOT NULL)
    AND (OBJECT_ID(N'dbo.QualityEventChecklistAnswers',N'U') IS NULL OR COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerID') IS NOT NULL)
    AND (OBJECT_ID(N'dbo.QualityEventPrintHistory',N'U') IS NULL OR COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventPrintID') IS NOT NULL)
THEN 1 ELSE 0 END;",

                "20260823_002" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.CultureMediaQualificationRequirements',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovalStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ReviewedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ReviewedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovedAt') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'CK_CultureMediaQualificationRequirements_Approval_20260823'
          AND is_disabled=0 AND is_not_trusted=0
    )
THEN 1 ELSE 0 END;",

                "20260824_001" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'StabilityChamberNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'StabilityProtocolNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SourceSpecificationTestID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationVersionNo') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationItemCode') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationProductionStage') IS NOT NULL
    AND EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SampleTests') AND name=N'FK_PRM_SampleTests_SourceSpecification_20260824' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'CK_PRM_Samples_SpecificationVersion_20260824' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests') AND name=N'IX_PRM_SpecificationTests_ExactScope_20260824' AND is_disabled=0)
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests') AND name=N'IX_PRM_SampleTests_FrozenSource_20260824' AND is_disabled=0)
    AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'IX_QualityEvents_SourceStatus_20260824' AND is_disabled=0)
THEN 1 ELSE 0 END;",

                "20260825_001" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisStartedDate') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisCompletedDate') IS NOT NULL
THEN 1 ELSE 0 END;",

                "20260827_001" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationNumericLimit') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.check_constraints
        WHERE parent_object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'CK_QualityEventAffectedResults_EvidenceSchemaVersion_20260827_001'
          AND is_disabled=0 AND is_not_trusted=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'IX_QEAffected_PRM_EvidenceBinding_20260827_001'
          AND is_disabled=0
    )
THEN 1 ELSE 0 END;",

                "20260827_002" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations')
          AND name=N'UX_PRM_QEEvidenceReconciliation_Pair_20260827_002'
          AND is_disabled=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations')
          AND name=N'IX_PRM_QEEvidenceReconciliation_Sample_20260827_002'
          AND is_disabled=0
    )
    AND OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002',N'TR') IS NOT NULL
THEN 1 ELSE 0 END;",

                "20260828_001" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'OldRowsJson') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'NewRowsJson') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventInvestigationEvidenceHistory',N'ChangeReason') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventInvestigationEvidenceHistory')
          AND name=N'IX_QEInvestigationEvidenceHistory_Event_20260828_001'
          AND is_disabled=0
    )
    AND OBJECT_ID(N'dbo.TR_QEInvestigationEvidenceHistory_AppendOnly_20260828_001',N'TR') IS NOT NULL
THEN 1 ELSE 0 END;",

                "20260905_000" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'MinimumIncubationHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'QualificationStartedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'MinimumIncubationHoursSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'IncubationCompletedAt') IS NOT NULL
THEN 1 ELSE 0 END;",

                "20260905_001" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'MinimumIncubationHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'QualificationStartedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'MinimumIncubationHoursSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.MediaQualifications',N'IncubationCompletedAt') IS NOT NULL
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests') AND name=N'CK_PRM_SpecificationTests_MinimumElapsedHours_20260905' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_SampleTests') AND name=N'CK_PRM_SampleTests_MinimumElapsedHours_20260905' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'CK_CultureMediaQualificationRequirements_MinHours_20260905' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.MediaQualifications') AND name=N'CK_MediaQualifications_MinHoursSnapshot_20260905' AND is_disabled=0 AND is_not_trusted=0)
THEN 1 ELSE 0 END;",

                "20260906_000" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationReason') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedAt') IS NOT NULL
THEN 1 ELSE 0 END;",

                "20260906_000A" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_TimingMigrationHistory',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND is_nullable=0
          AND (max_length=-1 OR max_length>=120)
    )
THEN 1 ELSE 0 END;",

                "20260906_000B" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_TimingMigrationHistory',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND is_nullable=0
          AND (max_length=-1 OR max_length>=160)
    )
THEN 1 ELSE 0 END;",

                "20260906_001" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledAt') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_TimingMigrationHistory',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
          AND name=N'ReconciliationDisposition'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND is_nullable=0
          AND (max_length=-1 OR max_length>=160)
    )
    AND COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'HasControlledQualityEventEvidence') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartSignatureAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartProvenanceIssue') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartSignatureAt') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_SpecificationTimingReapprovalHistory',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_TimingGovernanceMigrationState',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_TimingGovernanceMigrationState',N'CompletedAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedAt') IS NOT NULL
    AND OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots',N'U') IS NOT NULL
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots') AND name=N'CK_MediaQualificationReqSnapshots_MinHours' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID(N'dbo.MediaQualificationRequirementSnapshots') AND name=N'FK_MediaQualificationReqSnapshots_Qualification' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'CK_PRM_Samples_TimingReconciliation_20260906' AND is_disabled=0 AND is_not_trusted=0)
    AND EXISTS (SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'CK_CultureMediaQualificationRequirements_TimingConfirmation_20260906' AND is_disabled=0 AND is_not_trusted=0)
    AND OBJECT_ID(N'dbo.TR_PRM_TimingMigrationHistory_AppendOnly_20260906',N'TR') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_TimingMigrationTestEvidence_AppendOnly_20260906',N'TR') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_SpecTimingReapprovalHistory_AppendOnly_20260906',N'TR') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_MediaQualificationRequirementSnapshots_AppendOnly_20260906',N'TR') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_TimingGovernanceMigrationState_AppendOnly_20260906',N'TR') IS NOT NULL
THEN 1 ELSE 0 END;",

                "20260906_002" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_TimingQELegacyLinkCorrections',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_TimingQELegacyLinkCorrections',N'SampleID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_TimingQELegacyLinkCorrections',N'TimingMigrationHistoryID') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_TimingQELegacyLinkCorrections_AppendOnly_20260906',N'TR') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.CultureMediaLots')
          AND name=N'TR_CultureMediaLots_FinalReleaseExpiryGate_20260906'
          AND is_disabled=0
    )
THEN 1 ELSE 0 END;",

                "20260906_002A" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_TimingQELegacyLinkCorrections',N'U') IS NOT NULL
    AND NOT EXISTS
    (
        SELECT 1
        FROM dbo.PRM_Samples s
        INNER JOIN dbo.PRM_TimingQELegacyLinkCorrections c ON c.SampleID=s.SampleID
        WHERE UPPER(LTRIM(RTRIM(ISNULL(s.SampleStatus,N''))))=N'IN PROGRESS'
          AND UPPER(LTRIM(RTRIM(ISNULL(s.ReportStatus,N''))))=N'RESULTS ENTERED'
          AND s.ReviewedBy IS NULL
          AND s.ApprovedBy IS NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.PRM_Certificates cert WHERE cert.SampleID=s.SampleID)
    )
THEN 1 ELSE 0 END;",

                "20260906_003" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.EM_Events')
          AND name=N'TRG_EM_Events_ProtectAreaSnapshot_20260830'
          AND is_disabled=0
    )
THEN 1 ELSE 0 END;",

                "20260906_004" => @"
SELECT CASE WHEN
    COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NOT NULL
    AND COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.triggers
        WHERE parent_id=OBJECT_ID(N'dbo.EM_Events')
          AND name=N'TRG_EM_Events_ProtectAreaSnapshot_20260830'
          AND is_disabled=0
    )
THEN 1 ELSE 0 END;",

                "20260911_000" => @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.Users',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'PasswordHash') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'PasswordHashNew'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND (max_length=-1 OR max_length>=1024)
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'PasswordSalt'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND (max_length=-1 OR max_length>=512)
    )
    AND COL_LENGTH(N'dbo.Users',N'Department') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'Section') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'LastLogin') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'FailedLoginAttempts') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'IsLocked') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'LockedUntil') IS NOT NULL
    AND COL_LENGTH(N'dbo.Users',N'AuthenticationRowVersion') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'IsActive'
          AND system_type_id=TYPE_ID(N'bit') AND is_nullable=0
    )
    AND
    (
        SELECT COUNT(*)
        FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name IN
          (
              N'CanAccessWater', N'CanAccessEM', N'CanRegisterSamples', N'CanEnterResults',
              N'CanReviewResults', N'CanApproveResults', N'CanIssueCOA', N'CanCancelCOA',
              N'CanAccessReports', N'CanManageUsers', N'CanManageSettings'
          )
          AND system_type_id=TYPE_ID(N'bit')
          AND is_nullable=0
    ) = 11
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'AuthenticationRowVersion'
          AND system_type_id=189 AND is_nullable=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'FailedLoginAttempts'
          AND system_type_id=TYPE_ID(N'int') AND is_nullable=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'IsLocked'
          AND system_type_id=TYPE_ID(N'bit') AND is_nullable=0
    )
THEN 1 ELSE 0 END;",

                "20260915_000" => UserAdministrationSecurityPostconditionSql,

                "20260917_000" => UserAdministrationWriteCompatibilityPostconditionSql,

                "20260917_001" => UserAdministrationSignatureEvidencePostconditionSql,

                "20260906_005" => @"
SELECT CASE WHEN
    EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciliationStatus'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND max_length=60 AND is_nullable=0
    )
    AND EXISTS
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
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciledBy'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND max_length=240 AND is_nullable=1
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciledAt'
          AND system_type_id=TYPE_ID(N'datetime2')
          AND scale=0 AND is_nullable=1
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
          AND name=N'TimingReconciliationReason'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND max_length=2000 AND is_nullable=1
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'TimingConfirmedMinimumIncubationHours'
          AND system_type_id=TYPE_ID(N'decimal')
          AND precision=9 AND scale=2 AND is_nullable=1
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'TimingConfirmedBy'
          AND system_type_id=TYPE_ID(N'nvarchar')
          AND max_length=200 AND is_nullable=1
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
          AND name=N'TimingConfirmedAt'
          AND system_type_id=TYPE_ID(N'datetime2')
          AND scale=0 AND is_nullable=1
    )
THEN 1 ELSE 0 END;",

                _ => "SELECT 1;"
            };

            await using SqlCommand verification = new SqlCommand(verificationSql, connection)
            {
                CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120)
            };
            if (transaction != null)
                verification.Transaction = transaction;

            object? result = await verification.ExecuteScalarAsync().ConfigureAwait(false);
            return Convert.ToInt32(result ?? 0) == 1;
        }

        private static async Task PrepareManifestMigrationCompatibilityAsync(
            string versionKey,
            SqlConnection connection,
            SqlTransaction transaction)
        {
            // 20260911_000 is kept byte-for-byte for checksum/audit compatibility.
            // A bounded pre-execution helper repairs only known safe legacy Users schema drift.
            if (versionKey.Equals(AuthenticationLoginCompatibilityMigrationKey, StringComparison.Ordinal))
            {
                await PrepareAuthenticationLoginCompatibilityAsync(connection, transaction).ConfigureAwait(false);
                return;
            }

            if (versionKey.Equals(UserAdministrationSecurityMigrationKey, StringComparison.Ordinal))
            {
                await PrepareUserAdministrationSecurityCompatibilityAsync(connection, transaction).ConfigureAwait(false);
                return;
            }

            // 20260905_001 is intentionally kept byte-for-byte for auditability, but
            // it may reference timing columns later in the same SQL batch. The separate
            // 20260905_000 migration must therefore have committed those columns before
            // this batch is compiled. Do not materialize DDL inside this transaction.
            if (versionKey.Equals("20260905_001", StringComparison.Ordinal))
            {
                const string timingGatePrerequisiteVerificationSql = @"
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'MinimumElapsedHours') IS NULL
   OR COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NULL
   OR COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'MinimumIncubationHours') IS NULL
   OR COL_LENGTH(N'dbo.MediaQualifications',N'QualificationStartedAt') IS NULL
   OR COL_LENGTH(N'dbo.MediaQualifications',N'MinimumIncubationHoursSnapshot') IS NULL
   OR COL_LENGTH(N'dbo.MediaQualifications',N'IncubationCompletedAt') IS NULL
    THROW 54409, 'Compile-safe prerequisite 20260905_000 is incomplete; 20260905_001 will not be compiled.', 1;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests') AND name=N'MinimumElapsedHours' AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2))
    THROW 54410, 'Existing dbo.PRM_SpecificationTests.MinimumElapsedHours has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests') AND name=N'MinimumElapsedHours' AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2))
    THROW 54411, 'Existing dbo.PRM_SampleTests.MinimumElapsedHours has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'MinimumIncubationHours' AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2))
    THROW 54412, 'Existing dbo.CultureMediaQualificationRequirements.MinimumIncubationHours has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications') AND name=N'MinimumIncubationHoursSnapshot' AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2))
    THROW 54413, 'Existing dbo.MediaQualifications.MinimumIncubationHoursSnapshot has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.MediaQualifications') AND name IN (N'QualificationStartedAt',N'IncubationCompletedAt') AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0))
    THROW 54414, 'Existing dbo.MediaQualifications timing timestamps have an incompatible schema.', 1;";

                await using SqlCommand timingGatePrerequisiteVerification = new SqlCommand(
                    timingGatePrerequisiteVerificationSql,
                    connection,
                    transaction);
                timingGatePrerequisiteVerification.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await timingGatePrerequisiteVerification.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // 20260906_001 likewise requires its additive columns to exist in committed
            // schema before SQL Server compiles the legacy governance batch. The
            // 20260906_000 prerequisite materializes additive timing columns and the
            // 20260906_000A creates/widens the migration-history disposition column and
            // 20260906_000B strengthens that contract to NVARCHAR(80) before the
            // historical batch is compiled.
            if (versionKey.Equals("20260906_001", StringComparison.Ordinal))
            {
                const string timingGovernancePrerequisiteVerificationSql = @"
IF COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationStatus') IS NULL
   OR COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledBy') IS NULL
   OR COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciledAt') IS NULL
   OR COL_LENGTH(N'dbo.PRM_Samples',N'TimingReconciliationReason') IS NULL
   OR COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedMinimumIncubationHours') IS NULL
   OR COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedBy') IS NULL
   OR COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'TimingConfirmedAt') IS NULL
    THROW 54434, 'Compile-safe prerequisite 20260906_000 is incomplete; 20260906_001 will not be compiled.', 1;

IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory',N'U') IS NULL
    THROW 54475, 'Disposition-width prerequisites 20260906_000A/000B are incomplete; PRM_TimingMigrationHistory is missing before 20260906_001.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_TimingMigrationHistory')
      AND name=N'ReconciliationDisposition'
      AND system_type_id=TYPE_ID(N'nvarchar')
      AND is_nullable=0
      AND (max_length=-1 OR max_length>=160)
)
    THROW 54476, 'Disposition-width prerequisite 20260906_000B is incomplete; ReconciliationDisposition must hold at least 80 Unicode characters.', 1;

IF OBJECT_ID(N'dbo.PRM_TimingMigrationHistory',N'U') IS NOT NULL
   AND
   (
       COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'PreviousAnalysisStartedDate') IS NULL
       OR COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartSignatureAt') IS NULL
       OR COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'AnalysisStartProvenanceIssue') IS NULL
       OR COL_LENGTH(N'dbo.PRM_TimingMigrationHistory',N'HasControlledQualityEventEvidence') IS NULL
   )
    THROW 54435, 'Existing PRM_TimingMigrationHistory is incomplete before 20260906_001.', 1;

IF OBJECT_ID(N'dbo.PRM_TimingMigrationTestEvidence',N'U') IS NOT NULL
   AND
   (
       COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartSignatureAt') IS NULL
       OR COL_LENGTH(N'dbo.PRM_TimingMigrationTestEvidence',N'AnalysisStartProvenanceIssue') IS NULL
   )
    THROW 54436, 'Existing PRM_TimingMigrationTestEvidence is incomplete before 20260906_001.', 1;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciliationStatus' AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>60 OR is_nullable<>0))
    THROW 54437, 'Existing dbo.PRM_Samples.TimingReconciliationStatus has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciledBy' AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>240))
    THROW 54438, 'Existing dbo.PRM_Samples.TimingReconciledBy has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciledAt' AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0))
    THROW 54439, 'Existing dbo.PRM_Samples.TimingReconciledAt has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples') AND name=N'TimingReconciliationReason' AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>2000))
    THROW 54440, 'Existing dbo.PRM_Samples.TimingReconciliationReason has an incompatible schema.', 1;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements') AND name=N'TimingConfirmedMinimumIncubationHours' AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>9 OR scale<>2))
    THROW 54441, 'Existing Culture Media confirmed incubation timing has an incompatible schema.', 1;";

                await using SqlCommand timingGovernancePrerequisiteVerification = new SqlCommand(
                    timingGovernancePrerequisiteVerificationSql,
                    connection,
                    transaction);
                timingGovernancePrerequisiteVerification.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await timingGovernancePrerequisiteVerification.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }
            // 20260906_003 repairs EM area snapshot columns and then references those
            // columns in a static UPDATE later in the same SQL batch. SQL Server may
            // compile the UPDATE before the guarded ALTER TABLE statements execute,
            // producing error 207 on a drifted database. Materialize only the missing
            // additive columns in a preceding command, then execute the original
            // checksum-controlled migration bytes unchanged.
            if (versionKey.Equals("20260906_003", StringComparison.Ordinal))
            {
                const string emAreaSnapshotCompatibilitySql = @"
IF OBJECT_ID(N'dbo.EM_Events',N'U') IS NULL
    THROW 54232, 'Required table dbo.EM_Events is missing before 20260906_003 compatibility preparation.', 1;
IF OBJECT_ID(N'dbo.EM_Areas',N'U') IS NULL
    THROW 54233, 'Required table dbo.EM_Areas is missing before 20260906_003 compatibility preparation.', 1;

IF COL_LENGTH(N'dbo.EM_Events',N'AreaCodeSnapshot') IS NULL
    EXEC(N'ALTER TABLE dbo.EM_Events ADD AreaCodeSnapshot NVARCHAR(100) NULL;');
IF COL_LENGTH(N'dbo.EM_Events',N'AreaNameSnapshot') IS NULL
    EXEC(N'ALTER TABLE dbo.EM_Events ADD AreaNameSnapshot NVARCHAR(200) NULL;');
IF COL_LENGTH(N'dbo.EM_Events',N'GradeSnapshot') IS NULL
    EXEC(N'ALTER TABLE dbo.EM_Events ADD GradeSnapshot NVARCHAR(100) NULL;');
IF COL_LENGTH(N'dbo.EM_Events',N'AreaSnapshotSource') IS NULL
    EXEC(N'ALTER TABLE dbo.EM_Events ADD AreaSnapshotSource NVARCHAR(160) NULL;');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.EM_Events') AND name=N'AreaCodeSnapshot'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>200)
)
    THROW 54234, 'Existing dbo.EM_Events.AreaCodeSnapshot has an incompatible schema.', 1;
IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.EM_Events') AND name=N'AreaNameSnapshot'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>400)
)
    THROW 54235, 'Existing dbo.EM_Events.AreaNameSnapshot has an incompatible schema.', 1;
IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.EM_Events') AND name=N'GradeSnapshot'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>200)
)
    THROW 54236, 'Existing dbo.EM_Events.GradeSnapshot has an incompatible schema.', 1;
IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.EM_Events') AND name=N'AreaSnapshotSource'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>320)
)
    THROW 54237, 'Existing dbo.EM_Events.AreaSnapshotSource has an incompatible schema.', 1;";

                await using SqlCommand emAreaSnapshotCompatibility = new SqlCommand(
                    emAreaSnapshotCompatibilitySql,
                    connection,
                    transaction);
                emAreaSnapshotCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await emAreaSnapshotCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }
            // 20260722_005 adds ItemCode / CompendialReference and then creates an
            // index that references ItemCode in the same SQL batch. SQL Server can
            // compile the CREATE INDEX before ALTER TABLE has executed. Materialize
            // only the additive columns in a preceding command while preserving the
            // checksum-controlled migration bytes and its index definition.
            if (versionKey.Equals("20260722_005", StringComparison.Ordinal))
            {
                const string prmSpecificationLinkCompatibilitySql = @"
IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
    THROW 52500, 'Required table dbo.PRM_SpecificationTests is missing before 20260722_005.', 1;

IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ApprovalStatus') IS NULL
   OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsActive') IS NULL
   OR COL_LENGTH(N'dbo.PRM_SpecificationTests',N'EffectiveDate') IS NULL
    THROW 52501, 'dbo.PRM_SpecificationTests does not match the controlled specification-master baseline required by 20260722_005.', 1;

IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ItemCode NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'CompendialReference') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD CompendialReference NVARCHAR(160) NULL;');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'ItemCode'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>160)
)
    THROW 52502, 'Existing dbo.PRM_SpecificationTests.ItemCode has an incompatible schema.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'CompendialReference'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>320)
)
    THROW 52503, 'Existing dbo.PRM_SpecificationTests.CompendialReference has an incompatible schema.', 1;";

                await using SqlCommand prmSpecificationLinkCompatibility = new SqlCommand(
                    prmSpecificationLinkCompatibilitySql,
                    connection,
                    transaction);
                prmSpecificationLinkCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await prmSpecificationLinkCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // 20260811_001 creates AreaClassification and then references it in a
            // CHECK constraint and index in the same SQL batch. Pre-create only a
            // completely absent column. A nullable/partially applied legacy column
            // is deliberately left to the controlled migration's own fail-closed
            // reconciliation logic so existing classifications are never overwritten.
            if (versionKey.Equals("20260811_001", StringComparison.Ordinal))
            {
                const string externalTrendClassificationCompatibilitySql = @"
IF OBJECT_ID(N'dbo.ExternalTrendImportRows',N'U') IS NULL
    THROW 51090, 'Required table dbo.ExternalTrendImportRows is missing before 20260811_001.', 1;

IF COL_LENGTH(N'dbo.ExternalTrendImportRows',N'AreaClassification') IS NULL
    EXEC(N'ALTER TABLE dbo.ExternalTrendImportRows ADD AreaClassification NVARCHAR(30) NOT NULL CONSTRAINT DF_ExternalTrendImportRows_AreaClassification DEFAULT (N''Unspecified'');');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.ExternalTrendImportRows')
      AND name=N'AreaClassification'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>60)
)
    THROW 51092, 'Existing dbo.ExternalTrendImportRows.AreaClassification has an incompatible schema.', 1;";

                await using SqlCommand externalTrendClassificationCompatibility = new SqlCommand(
                    externalTrendClassificationCompatibilitySql,
                    connection,
                    transaction);
                externalTrendClassificationCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await externalTrendClassificationCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }
            // 20260827_001 adds EvidenceSchemaVersion and then references it in a
            // CHECK constraint and CREATE INDEX later in the same checksum-controlled
            // SQL batch. SQL Server compiles those later statements before the
            // conditional ALTER TABLE executes, producing Msg 207 on an upgraded
            // database where the column is absent. Materialize only the two additive
            // columns in a preceding command; the original migration still owns the
            // constraint, index, checksum, ledger entry, and legacy version-0 semantics.
            if (versionKey.Equals("20260827_001", StringComparison.Ordinal))
            {
                const string prmEvidenceVersionCompatibilitySql = @"
IF OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NULL
    THROW 53710, 'Required table dbo.QualityEventAffectedResults is missing before 20260827_001.', 1;

IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationNumericLimit') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SpecificationNumericLimit DECIMAL(18,3) NULL;');

IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD EvidenceSchemaVersion TINYINT NOT NULL CONSTRAINT DF_QualityEventAffectedResults_EvidenceSchemaVersion_20260827_001 DEFAULT (0) WITH VALUES;');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'EvidenceSchemaVersion'
      AND (system_type_id<>TYPE_ID(N'tinyint') OR is_nullable<>0)
)
    THROW 53711, 'Existing dbo.QualityEventAffectedResults.EvidenceSchemaVersion has an incompatible schema.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
      AND name=N'SpecificationNumericLimit'
      AND (system_type_id<>TYPE_ID(N'decimal') OR precision<>18 OR scale<>3)
)
    THROW 53712, 'Existing dbo.QualityEventAffectedResults.SpecificationNumericLimit has an incompatible schema.', 1;";

                await using SqlCommand prmEvidenceVersionCompatibility = new SqlCommand(
                    prmEvidenceVersionCompatibilitySql,
                    connection,
                    transaction);
                prmEvidenceVersionCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await prmEvidenceVersionCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // 20260827_002 conditionally creates the reconciliation table and then
            // creates indexes against it later in the same SQL batch. Pre-create the
            // exact controlled table through dynamic SQL when it is absent so SQL
            // Server compiles the original index statements against an existing
            // object. Existing tables are validated fail-closed and are never rewritten.
            if (versionKey.Equals("20260827_002", StringComparison.Ordinal))
            {
                const string prmEvidenceReconciliationCompatibilitySql = @"
IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL
    THROW 53720, 'Required table dbo.QualityEvents is missing before 20260827_002.', 1;
IF OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NULL
    THROW 53721, 'Required table dbo.QualityEventAffectedResults is missing before 20260827_002.', 1;
IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures',N'U') IS NULL
    THROW 53722, 'Required table dbo.PRM_ElectronicSignatures is missing before 20260827_002.', 1;
IF OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NULL
    THROW 53723, 'Required table dbo.PRM_Samples is missing before 20260827_002.', 1;

IF OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NULL
BEGIN
    EXEC(N'CREATE TABLE dbo.PRM_QualityEventEvidenceReconciliations
    (
        ReconciliationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_PRM_QEEvidenceReconciliations_20260827_002 PRIMARY KEY,
        LegacyQualityEventID INT NOT NULL,
        ReplacementQualityEventID INT NOT NULL,
        SampleID INT NOT NULL,
        ReconciledBy NVARCHAR(120) NOT NULL,
        ReconciledAt DATETIME2(0) NOT NULL CONSTRAINT DF_PRM_QEEvidenceReconciliations_Date_20260827_002 DEFAULT SYSDATETIME(),
        ReconciliationReason NVARCHAR(1000) NOT NULL,
        ElectronicSignatureID INT NOT NULL,
        ReconciliationSchemaVersion TINYINT NOT NULL CONSTRAINT DF_PRM_QEEvidenceReconciliations_Version_20260827_002 DEFAULT (1),
        CONSTRAINT CK_PRM_QEEvidenceReconciliations_DifferentEvents_20260827_002 CHECK (LegacyQualityEventID <> ReplacementQualityEventID),
        CONSTRAINT CK_PRM_QEEvidenceReconciliations_Version_20260827_002 CHECK (ReconciliationSchemaVersion = 1),
        CONSTRAINT FK_PRM_QEEvidenceReconciliations_LegacyEvent_20260827_002 FOREIGN KEY (LegacyQualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
        CONSTRAINT FK_PRM_QEEvidenceReconciliations_ReplacementEvent_20260827_002 FOREIGN KEY (ReplacementQualityEventID) REFERENCES dbo.QualityEvents(QualityEventID),
        CONSTRAINT FK_PRM_QEEvidenceReconciliations_Sample_20260827_002 FOREIGN KEY (SampleID) REFERENCES dbo.PRM_Samples(SampleID),
        CONSTRAINT FK_PRM_QEEvidenceReconciliations_Signature_20260827_002 FOREIGN KEY (ElectronicSignatureID) REFERENCES dbo.PRM_ElectronicSignatures(SignatureID)
    );');
END;

IF COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NULL
   OR COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NULL
   OR COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'SampleID') IS NULL
   OR COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NULL
   OR COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NULL
    THROW 53725, 'Existing dbo.PRM_QualityEventEvidenceReconciliations does not match the controlled 20260827_002 structure.', 1;";

                await using SqlCommand prmEvidenceReconciliationCompatibility = new SqlCommand(
                    prmEvidenceReconciliationCompatibilitySql,
                    connection,
                    transaction);
                prmEvidenceReconciliationCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await prmEvidenceReconciliationCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // 20260828_003 creates a signed historical-EM reconciliation table with
            // a foreign key to dbo.EM_EventPlates(Id). Some long-lived PharmaLIMS
            // databases predate the controlled baseline and contain the Id column
            // without a PRIMARY KEY / UNIQUE candidate key. SQL Server rejects the
            // foreign key with Msg 1776 in that shape. Repair only the missing
            // additive candidate-key prerequisite while keeping the checksum-
            // controlled 20260828_003 migration bytes unchanged.
            if (versionKey.Equals("20260828_003", StringComparison.Ordinal))
            {
                const string emEventPlateKeyCompatibilitySql = @"
IF OBJECT_ID(N'dbo.EM_EventPlates', N'U') IS NULL
    THROW 53550, 'Required table dbo.EM_EventPlates is missing before 20260828_003.', 1;

IF COL_LENGTH(N'dbo.EM_EventPlates', N'Id') IS NULL
    THROW 53551, 'Legacy dbo.EM_EventPlates does not contain the required Id column. Reconcile the controlled EM plate schema before applying 20260828_003.', 1;

IF EXISTS
(
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.EM_EventPlates')
      AND name = N'Id'
      AND system_type_id <> TYPE_ID(N'int')
)
    THROW 53552, 'Existing dbo.EM_EventPlates.Id is not INT. Reconcile the legacy EM plate schema before applying 20260828_003.', 1;

EXEC sys.sp_executesql N'
IF EXISTS (SELECT 1 FROM dbo.EM_EventPlates WHERE Id IS NULL)
    THROW 53553, ''Existing dbo.EM_EventPlates contains NULL Id values. Reconcile controlled EM plate data before applying 20260828_003.'', 1;

IF EXISTS
(
    SELECT Id
    FROM dbo.EM_EventPlates
    GROUP BY Id
    HAVING COUNT_BIG(*) > 1
)
    THROW 53554, ''Existing dbo.EM_EventPlates contains duplicate Id values. Reconcile controlled EM plate data before applying 20260828_003.'', 1;';

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes AS i
    WHERE i.object_id = OBJECT_ID(N'dbo.EM_EventPlates')
      AND i.is_unique = 1
      AND i.is_disabled = 0
      AND i.has_filter = 0
      AND
      (
          SELECT COUNT(*)
          FROM sys.index_columns AS ic
          WHERE ic.object_id = i.object_id
            AND ic.index_id = i.index_id
            AND ic.key_ordinal > 0
      ) = 1
      AND EXISTS
      (
          SELECT 1
          FROM sys.index_columns AS ic
          INNER JOIN sys.columns AS c
              ON c.object_id = ic.object_id
             AND c.column_id = ic.column_id
          WHERE ic.object_id = i.object_id
            AND ic.index_id = i.index_id
            AND ic.key_ordinal = 1
            AND c.name = N'Id'
      )
)
BEGIN
    EXEC(N'CREATE UNIQUE NONCLUSTERED INDEX UX_EM_EventPlates_Id_20260828_003 ON dbo.EM_EventPlates(Id);');
END;";

                await using SqlCommand emEventPlateKeyCompatibility = new SqlCommand(
                    emEventPlateKeyCompatibilitySql,
                    connection,
                    transaction);
                emEventPlateKeyCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await emEventPlateKeyCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            if (versionKey.Equals("20260819_001", StringComparison.Ordinal))
            {
                // Some legacy PharmaLIMS databases contain dbo.EM_GradeLimits.Id
                // without a PRIMARY KEY / UNIQUE candidate key. The controlled
                // 20260819_001 migration correctly creates a foreign key from
                // EM_GradeLimitSignatures(GradeLimitID) to EM_GradeLimits(Id), but
                // SQL Server rejects that foreign key when the legacy Id column is
                // not unique. Repair only the missing additive candidate-key
                // prerequisite; keep the controlled migration bytes/checksum intact.
                const string emGradeLimitKeySql = @"
IF OBJECT_ID(N'dbo.EM_GradeLimits', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.EM_GradeLimits', N'Id') IS NULL
        EXEC(N'ALTER TABLE dbo.EM_GradeLimits ADD Id INT IDENTITY(1,1) NOT NULL;');

    IF EXISTS
    (
        SELECT 1
        FROM sys.columns
        WHERE object_id = OBJECT_ID(N'dbo.EM_GradeLimits')
          AND name = N'Id'
          AND system_type_id <> TYPE_ID(N'int')
    )
        THROW 52020, 'Existing dbo.EM_GradeLimits.Id is not INT. Reconcile the legacy EM limit schema before applying 20260819_001.', 1;

    -- Id may have been created by the dynamic ALTER above. Keep all direct
    -- data references in a later dynamic batch so SQL Server never compiles them
    -- against the pre-ALTER shape of a legacy table.
    EXEC sys.sp_executesql N'
    IF EXISTS (SELECT 1 FROM dbo.EM_GradeLimits WHERE Id IS NULL)
        THROW 52021, ''Existing dbo.EM_GradeLimits contains NULL Id values. Reconcile controlled EM limit master data before applying 20260819_001.'', 1;

    IF EXISTS
    (
        SELECT Id
        FROM dbo.EM_GradeLimits
        GROUP BY Id
        HAVING COUNT_BIG(*) > 1
    )
        THROW 52022, ''Existing dbo.EM_GradeLimits contains duplicate Id values. Reconcile controlled EM limit master data before applying 20260819_001.'', 1;';

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes AS i
        WHERE i.object_id = OBJECT_ID(N'dbo.EM_GradeLimits')
          AND i.is_unique = 1
          AND i.is_disabled = 0
          AND i.has_filter = 0
          AND
          (
              SELECT COUNT(*)
              FROM sys.index_columns AS ic
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal > 0
          ) = 1
          AND EXISTS
          (
              SELECT 1
              FROM sys.index_columns AS ic
              INNER JOIN sys.columns AS c
                  ON c.object_id = ic.object_id
                 AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id
                AND ic.index_id = i.index_id
                AND ic.key_ordinal = 1
                AND c.name = N'Id'
          )
    )
    BEGIN
        EXEC(N'CREATE UNIQUE NONCLUSTERED INDEX UX_EM_GradeLimits_Id_20260819 ON dbo.EM_GradeLimits(Id);');
    END;
END;";

                await using SqlCommand emGradeLimitKey = new SqlCommand(
                    emGradeLimitKeySql,
                    connection,
                    transaction);
                emGradeLimitKey.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await emGradeLimitKey.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // Retired PRM Quality Event reconciliation migrations are intentionally
            // not prepared here. They are superseded by 20260826_001. The current
            // baseline is preserved byte-for-byte; this compatibility phase only
            // materializes additive columns in a separate command so SQL Server
            // cannot fail on compile-before-ALTER when upgrading a partial legacy
            // Development schema.
            if (versionKey.Equals("20260826_001", StringComparison.Ordinal))
            {
                const string prmQualityEventCompatibilitySql = @"
IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL
    THROW 53650, 'Required table dbo.QualityEvents is missing before 20260826_001.', 1;
IF OBJECT_ID(N'dbo.PRM_NumberSequences',N'U') IS NULL
    THROW 53651, 'Required table dbo.PRM_NumberSequences is missing before 20260826_001.', 1;

IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'LastUpdated') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_NumberSequences ADD LastUpdated DATETIME2(0) NULL;');

IF COL_LENGTH(N'dbo.QualityEvents',N'EventNumber') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventNumber NVARCHAR(60) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'EventType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD EventType NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'Severity') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD Severity NVARCHAR(60) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'SampleID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleID INT NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'SampleNumber') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SampleNumber NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'DetectedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedBy NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'DetectedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectedDate DATETIME2(0) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'DetectionSource') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD DetectionSource NVARCHAR(160) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'InitialDescription') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD InitialDescription NVARCHAR(MAX) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'ImmediateAction') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImmediateAction NVARCHAR(MAX) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'RootCauseCategory') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseCategory NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'RootCauseDetails') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD RootCauseDetails NVARCHAR(MAX) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'ImpactAssessment') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ImpactAssessment NVARCHAR(MAX) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'CAPARequired') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CAPARequired BIT NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'QAConclusion') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD QAConclusion NVARCHAR(MAX) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'FinalDisposition') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD FinalDisposition NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'ClosedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedBy NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'ClosedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ClosedDate DATETIME2(0) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'CreatedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedBy NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'CreatedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD CreatedDate DATETIME2(0) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'ModifiedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedBy NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'ModifiedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEvents ADD ModifiedDate DATETIME2(0) NULL;');

IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'SampleID' AND system_type_id=TYPE_ID(N'int') AND is_nullable=0)
    EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN SampleID INT NULL;');

IF COL_LENGTH(N'dbo.QualityEvents',N'QualityEventID') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEvents ADD QualityEventID INT NULL;');
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID' AND (system_type_id<>TYPE_ID(N'int') OR is_computed=1))
    THROW 53652, 'Existing QualityEvents.QualityEventID is not a compatible INT runtime key.', 1;
DECLARE @NullQualityEventID BIT=0;
EXEC sys.sp_executesql
    N'SELECT @HasNull=CASE WHEN EXISTS (SELECT 1 FROM dbo.QualityEvents WHERE QualityEventID IS NULL) THEN 1 ELSE 0 END;',
    N'@HasNull BIT OUTPUT',
    @HasNull=@NullQualityEventID OUTPUT;
IF @NullQualityEventID=1
    THROW 53654, 'NULL QualityEvents.QualityEventID values require controlled data review before migration.', 1;
IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEvents') AND name=N'QualityEventID' AND is_nullable=1)
    EXEC(N'ALTER TABLE dbo.QualityEvents ALTER COLUMN QualityEventID INT NOT NULL;');
DECLARE @DuplicateQualityEventID BIT=0;
EXEC sys.sp_executesql
    N'SELECT @Duplicate=CASE WHEN EXISTS
      (SELECT QualityEventID FROM dbo.QualityEvents WHERE QualityEventID IS NOT NULL GROUP BY QualityEventID HAVING COUNT_BIG(*)>1)
      THEN 1 ELSE 0 END;',
    N'@Duplicate BIT OUTPUT',
    @Duplicate=@DuplicateQualityEventID OUTPUT;
IF @DuplicateQualityEventID=1
    THROW 53653, 'Duplicate QualityEvents.QualityEventID values require controlled data review.', 1;
IF NOT EXISTS
(
    SELECT 1 FROM sys.indexes i
    WHERE i.object_id=OBJECT_ID(N'dbo.QualityEvents') AND i.is_unique=1 AND i.is_disabled=0
      AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0)=1
      AND EXISTS
      (
          SELECT 1 FROM sys.index_columns ic
          INNER JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
          WHERE ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal=1 AND c.name=N'QualityEventID'
      )
)
    EXEC(N'CREATE UNIQUE INDEX UX_QualityEvents_QualityEventID_20260826_Compat ON dbo.QualityEvents(QualityEventID) WHERE QualityEventID IS NOT NULL;');

IF OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'AffectedResultID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD AffectedResultID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'QualityEventID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SampleTestID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SampleTestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceModule NVARCHAR(80) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SourceResultID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'TestID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD TestID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'TestName') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD TestName NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'ResultValue') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD ResultValue NVARCHAR(200) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationLimit') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD SpecificationLimit NVARCHAR(500) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'Unit') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD Unit NVARCHAR(50) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'FailureType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD FailureType NVARCHAR(120) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventAffectedResults',N'CreatedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ADD CreatedDate DATETIME2(0) NULL;');
    IF EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults') AND name=N'SampleTestID' AND system_type_id=TYPE_ID(N'int') AND is_nullable=0)
        EXEC(N'ALTER TABLE dbo.QualityEventAffectedResults ALTER COLUMN SampleTestID INT NULL;');
END;

IF OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventActionID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventActionID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'QualityEventID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'ActionType') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ActionType NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'ActionDescription') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ActionDescription NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'PerformedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD PerformedBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'PerformedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD PerformedDate DATETIME2(0) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventActions',N'ElectronicSignatureID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventActions ADD ElectronicSignatureID INT NULL;');
END;

IF OBJECT_ID(N'dbo.QualityEventChecklistAnswers',N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerID BIGINT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'QualityEventID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'QuestionID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD QuestionID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnswerValue') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnswerValue NVARCHAR(250) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'Comments') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD Comments NVARCHAR(MAX) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnsweredBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventChecklistAnswers',N'AnsweredDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventChecklistAnswers ADD AnsweredDate DATETIME2(0) NULL;');
END;

IF OBJECT_ID(N'dbo.QualityEventPrintHistory',N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventPrintID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventPrintID BIGINT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'QualityEventID') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD QualityEventID INT NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedBy') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedBy NVARCHAR(100) NULL;');
    IF COL_LENGTH(N'dbo.QualityEventPrintHistory',N'PrintedDate') IS NULL EXEC(N'ALTER TABLE dbo.QualityEventPrintHistory ADD PrintedDate DATETIME2(0) NULL;');
END;";

                await using SqlCommand prmQualityEventCompatibility = new SqlCommand(
                    prmQualityEventCompatibilitySql,
                    connection,
                    transaction);
                prmQualityEventCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await prmQualityEventCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // Migration 20260823_002 adds Culture Media approval columns and then
            // references them later in the same SQL batch. SQL Server resolves
            // those column references before running the ALTER statements. Legacy
            // databases may also carry the historical baseline ledger while this
            // qualification-requirement table is absent or only partially present.
            // Reconcile the additive prerequisite shape in a separate command, in
            // the same migration transaction, then execute the original controlled
            // SQL bytes unchanged.
            if (versionKey.Equals("20260823_002", StringComparison.Ordinal))
            {
                const string cultureMediaApprovalCompatibilitySql = @"
IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements',N'U') IS NULL
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

IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovalStatus') IS NULL
    EXEC(N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ApprovalStatus NVARCHAR(30) NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Status_20260823_Compat DEFAULT N''Draft'' WITH VALUES;');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ReviewedBy') IS NULL
    EXEC(N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ReviewedBy NVARCHAR(100) NULL;');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ReviewedAt') IS NULL
    EXEC(N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ReviewedAt DATETIME2(0) NULL;');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovedBy') IS NULL
    EXEC(N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ApprovedBy NVARCHAR(100) NULL;');
IF COL_LENGTH(N'dbo.CultureMediaQualificationRequirements',N'ApprovedAt') IS NULL
    EXEC(N'ALTER TABLE dbo.CultureMediaQualificationRequirements ADD ApprovedAt DATETIME2(0) NULL;');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name=N'ApprovalStatus'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>60 OR is_nullable<>0)
)
    THROW 53210, 'Culture Media qualification ApprovalStatus has an incompatible schema.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name IN(N'ReviewedBy',N'ApprovedBy')
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>200)
)
    THROW 53211, 'Culture Media qualification reviewer/approver columns have incompatible schemas.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.CultureMediaQualificationRequirements')
      AND name IN(N'ReviewedAt',N'ApprovedAt')
      AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
)
    THROW 53212, 'Culture Media qualification review/approval timestamps have incompatible schemas.', 1;

EXEC sys.sp_executesql N'
IF NOT EXISTS (SELECT 1 FROM dbo.CultureMediaQualificationRequirements)
BEGIN
    INSERT dbo.CultureMediaQualificationRequirements
    (MediaTypePattern,TestName,IsRequired,MinimumRecoveryPercent,MaximumRecoveryPercent,EffectiveDate,IsActive,ApprovalStatus,ReviewedBy,ReviewedAt,ApprovedBy,ApprovedAt)
    VALUES
    (N''%'',N''pH Check'',1,NULL,NULL,CAST(GETDATE() AS date),0,N''Draft'',NULL,NULL,NULL,NULL),
    (N''%'',N''Growth Promotion'',1,50.00,200.00,CAST(GETDATE() AS date),0,N''Draft'',NULL,NULL,NULL,NULL),
    (N''%'',N''Indicative Property'',1,NULL,NULL,CAST(GETDATE() AS date),0,N''Draft'',NULL,NULL,NULL,NULL),
    (N''%'',N''Inhibitory Property'',1,NULL,NULL,CAST(GETDATE() AS date),0,N''Draft'',NULL,NULL,NULL,NULL),
    (N''%'',N''Preincubation Check'',1,NULL,NULL,CAST(GETDATE() AS date),0,N''Draft'',NULL,NULL,NULL,NULL);
END;';";

                await using SqlCommand cultureMediaApprovalCompatibility = new SqlCommand(
                    cultureMediaApprovalCompatibilitySql,
                    connection,
                    transaction);
                cultureMediaApprovalCompatibility.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await cultureMediaApprovalCompatibility.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // Migration 20260823_001 has the same compile-before-ALTER pattern for
            // the PRM specification review columns. Add only those columns in a
            // separate command; the original controlled migration still owns the
            // state constraint, remediation records, and ledger entry.
            if (versionKey.Equals("20260823_001", StringComparison.Ordinal))
            {
                const string prmReviewColumnsSql = @"
IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
    THROW 52023, 'Required table dbo.PRM_SpecificationTests is missing before 20260823_001.', 1;

IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedBy') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedBy NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ReviewedDate') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ReviewedDate DATETIME2(0) NULL;');
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'IsDefaultForCategory') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD IsDefaultForCategory BIT NOT NULL CONSTRAINT DF_PRM_SpecificationTests_Default_20260823 DEFAULT (0);');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'ReviewedBy'
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>240)
)
    THROW 52024, 'Existing PRM specification reviewer column has an incompatible type.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'ReviewedDate'
      AND (system_type_id<>TYPE_ID(N'datetime2') OR scale<>0)
)
    THROW 52025, 'Existing PRM specification review date has an incompatible type.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name=N'IsDefaultForCategory'
      AND system_type_id<>TYPE_ID(N'bit')
)
    THROW 52026, 'Existing PRM default-profile flag has an incompatible type.', 1;";

                await using SqlCommand prmReviewColumns = new SqlCommand(
                    prmReviewColumnsSql,
                    connection,
                    transaction);
                prmReviewColumns.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await prmReviewColumns.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            // SQL Server compiles a submitted batch before executing it. Migration
            // 20260824_001 adds nine PRM columns and later references them in
            // foreign keys and indexes in the same batch. Materialize only those
            // additive columns first, inside the same migration transaction, so
            // the original checksum-controlled SQL can compile and remain the
            // authority for constraints, indexes, checklist data, and recording.
            if (versionKey.Equals("20260824_001", StringComparison.Ordinal))
            {
                const string prmHardeningColumnsSql = @"
IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
    THROW 52030, 'Required table dbo.PRM_SpecificationTests is missing before 20260824_001.', 1;
IF OBJECT_ID(N'dbo.PRM_Samples',N'U') IS NULL
    THROW 52031, 'Required table dbo.PRM_Samples is missing before 20260824_001.', 1;
IF OBJECT_ID(N'dbo.PRM_SampleTests',N'U') IS NULL
    THROW 52032, 'Required table dbo.PRM_SampleTests is missing before 20260824_001.', 1;
IF OBJECT_ID(N'dbo.QualityEvents',N'U') IS NULL
    THROW 52038, 'Required table dbo.QualityEvents is missing before 20260824_001.', 1;
IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NULL
    THROW 52039, 'Required table dbo.QualityEventChecklistQuestions is missing before 20260824_001.', 1;

-- 20260824_001 references these columns later in the same SQL batch.
-- Materialize them first to prevent SQL Server compile-before-ALTER failures.
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceModule NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEvents ADD SourceRecordID INT NULL;');
IF COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEvents ADD CurrentStatus NVARCHAR(60) NULL;');

IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionID') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD QuestionID INT IDENTITY(1,1) NOT NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'SectionName') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD SectionName NVARCHAR(200) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionText') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD QuestionText NVARCHAR(MAX) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToEventType') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToEventType NVARCHAR(100) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToSampleType') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToSampleType NVARCHAR(100) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToTestCategory') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToTestCategory NVARCHAR(150) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AppliesToTestNameKeyword') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AppliesToTestNameKeyword NVARCHAR(200) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'AnswerType') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD AnswerType NVARCHAR(50) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'IsRequired') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD IsRequired BIT NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'ExpectedAnswer') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD ExpectedAnswer NVARCHAR(20) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionLogic') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD QuestionLogic NVARCHAR(50) NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'SortOrder') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD SortOrder INT NULL;');
IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'IsActive') IS NULL
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD IsActive BIT NULL;');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
      AND name=N'QuestionID'
      AND system_type_id<>TYPE_ID(N'int')
)
    THROW 52040, 'Existing Quality Event checklist question identity has an incompatible type.', 1;

IF NOT EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
      AND name=N'QuestionID' AND is_identity=1
)
AND NOT EXISTS
(
    SELECT 1 FROM sys.default_constraints d
    INNER JOIN sys.columns c ON c.object_id=d.parent_object_id AND c.column_id=d.parent_column_id
    WHERE d.parent_object_id=OBJECT_ID(N'dbo.QualityEventChecklistQuestions')
      AND c.name=N'QuestionID'
)
BEGIN
    IF OBJECT_ID(N'dbo.Seq_QualityEventChecklistQuestionID_20260824_Compat',N'SO') IS NULL
        EXEC(N'CREATE SEQUENCE dbo.Seq_QualityEventChecklistQuestionID_20260824_Compat AS INT START WITH 1 INCREMENT BY 1;');
    DECLARE @NextChecklistQuestionID BIGINT=1;
    EXEC sys.sp_executesql
        N'SELECT @Next=ISNULL(MAX(CONVERT(BIGINT,QuestionID)),0)+1 FROM dbo.QualityEventChecklistQuestions;',
        N'@Next BIGINT OUTPUT',
        @Next=@NextChecklistQuestionID OUTPUT;
    IF @NextChecklistQuestionID>2147483647
        THROW 52041, 'Quality Event checklist question identity range is exhausted.', 1;
    DECLARE @RestartChecklistQuestionSql NVARCHAR(4000);
    SET @RestartChecklistQuestionSql=N'ALTER SEQUENCE dbo.Seq_QualityEventChecklistQuestionID_20260824_Compat RESTART WITH '+CONVERT(NVARCHAR(30),@NextChecklistQuestionID)+N';';
    EXEC(@RestartChecklistQuestionSql);
    EXEC(N'ALTER TABLE dbo.QualityEventChecklistQuestions ADD CONSTRAINT DF_QualityEventChecklistQuestions_ID_20260824_Compat DEFAULT (NEXT VALUE FOR dbo.Seq_QualityEventChecklistQuestionID_20260824_Compat) FOR QuestionID;');
END;

IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ItemCode') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ItemCode NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.PRM_SpecificationTests',N'ProductionStage') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SpecificationTests ADD ProductionStage NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_Samples ADD SpecificationVersionNo INT NULL;');
IF COL_LENGTH(N'dbo.PRM_Samples',N'StabilityChamberNo') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_Samples ADD StabilityChamberNo NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.PRM_Samples',N'StabilityProtocolNo') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_Samples ADD StabilityProtocolNo NVARCHAR(120) NULL;');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SourceSpecificationTestID') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SampleTests ADD SourceSpecificationTestID INT NULL;');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationVersionNo') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SampleTests ADD SpecificationVersionNo INT NULL;');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationItemCode') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SampleTests ADD SpecificationItemCode NVARCHAR(80) NULL;');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationProductionStage') IS NULL
    EXEC(N'ALTER TABLE dbo.PRM_SampleTests ADD SpecificationProductionStage NVARCHAR(80) NULL;');

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationTests')
      AND name IN(N'ItemCode',N'ProductionStage')
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>160)
)
    THROW 52033, 'Existing PRM specification scope columns have incompatible types.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name=N'SpecificationVersionNo'
      AND system_type_id<>TYPE_ID(N'int')
)
    THROW 52034, 'Existing PRM sample specification version has an incompatible type.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_Samples')
      AND name IN(N'StabilityChamberNo',N'StabilityProtocolNo')
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>240)
)
    THROW 52035, 'Existing PRM Stability traceability columns have incompatible types.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
      AND name IN(N'SourceSpecificationTestID',N'SpecificationVersionNo')
      AND system_type_id<>TYPE_ID(N'int')
)
    THROW 52036, 'Existing PRM test provenance integer columns have incompatible types.', 1;

IF EXISTS
(
    SELECT 1 FROM sys.columns
    WHERE object_id=OBJECT_ID(N'dbo.PRM_SampleTests')
      AND name IN(N'SpecificationItemCode',N'SpecificationProductionStage')
      AND (system_type_id<>TYPE_ID(N'nvarchar') OR max_length<>160)
)
    THROW 52037, 'Existing PRM test provenance text columns have incompatible types.', 1;";

                await using SqlCommand prmHardeningColumns = new SqlCommand(
                    prmHardeningColumnsSql,
                    connection,
                    transaction);
                prmHardeningColumns.CommandTimeout = Math.Max(AppConfig.CommandTimeoutSeconds, 120);
                await prmHardeningColumns.ExecuteNonQueryAsync().ConfigureAwait(false);
                return;
            }

            return;
        }
    }
}
