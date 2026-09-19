using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PharmaLIMS.Infrastructure
{
    public sealed partial class StartupDatabaseMigrator
    {
        private static void ValidateCompleteMigrationPayload(JsonElement root, JsonElement migrations)
        {
            // Validate every controlled file before any migration can modify the database.
            string databaseRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database")) + Path.DirectorySeparatorChar;
            var uniqueVersionKeys = new HashSet<string>(StringComparer.Ordinal);
            var uniqueFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static void ValidateFile(JsonElement entry, string rootPath, HashSet<string> files, string label)
            {
                string relativeFile = entry.TryGetProperty("file", out JsonElement fileElement) ? fileElement.GetString() ?? string.Empty : string.Empty;
                string expectedHash = entry.TryGetProperty("sha256", out JsonElement hashElement) ? hashElement.GetString() ?? string.Empty : string.Empty;
                string fullPath = Path.GetFullPath(Path.Combine(rootPath, relativeFile));
                if (string.IsNullOrWhiteSpace(relativeFile) ||
                    !Regex.IsMatch(expectedHash, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
                    !fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) ||
                    !files.Add(relativeFile) || !File.Exists(fullPath))
                    throw new InvalidOperationException($"Controlled database payload validation failed before any migration: invalid or missing {label} file '{relativeFile}'.");

                string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant();
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Controlled database payload validation failed before any migration: checksum mismatch for '{relativeFile}'.");
            }

            if (!root.TryGetProperty("freshInstallBaseline", out JsonElement baseline) || baseline.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The controlled database migration manifest contains no fresh-install baseline.");
            ValidateFile(baseline, databaseRoot, uniqueFiles, "baseline");

            foreach (JsonElement entry in migrations.EnumerateArray())
            {
                string versionKey = entry.TryGetProperty("versionKey", out JsonElement keyElement) ? keyElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(versionKey) || !uniqueVersionKeys.Add(versionKey))
                    throw new InvalidOperationException($"Controlled database payload validation failed before any migration: duplicate or blank version key '{versionKey}'.");
                ValidateFile(entry, databaseRoot, uniqueFiles, "migration");
            }
        }

        internal const string UserAdministrationWriteCompatibilityMigrationKey = "20260917_000";
        internal const string UserAdministrationSignatureEvidenceMigrationKey = "20260917_001";

        private const string UserAdministrationSecurityPostconditionSql = @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.Users',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'MustChangePassword'
          AND system_type_id=TYPE_ID(N'bit')
          AND is_nullable=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'PasswordChangedAt'
          AND system_type_id=TYPE_ID(N'datetime2')
          AND scale=0
          AND is_nullable=1
    )
THEN 1 ELSE 0 END;";


        private const string UserAdministrationWriteCompatibilityPostconditionSql = @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.Users',N'U') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'CreatedAt'
          AND system_type_id=TYPE_ID(N'datetime2')
          AND scale=0
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.Users')
          AND name=N'UpdatedAt'
          AND system_type_id=TYPE_ID(N'datetime2')
          AND scale=0
    )
THEN 1 ELSE 0 END;";

        private const string UserAdministrationSignatureEvidencePostconditionSql = @"
SELECT CASE WHEN
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
THEN 1 ELSE 0 END;";
        /// <summary>
        /// Verifies that a controlled migration is present in the installed package, that the
        /// packaged SQL bytes match the signed manifest hash, and that the database ledger
        /// records the same checksum. This method performs no DDL/DML and is safe for
        /// production startup under a least-privilege runtime database account.
        /// </summary>
        public async Task VerifyControlledMigrationAsync(string versionKey)
        {
            if (string.IsNullOrWhiteSpace(versionKey))
                throw new ArgumentException("A controlled migration version key is required.", nameof(versionKey));

            string normalizedVersionKey = versionKey.Trim();
            ValidateInstalledMigrationPackage();

            string manifestPath = Path.Combine(AppContext.BaseDirectory, "Database", "MigrationManifest.json");
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("migrations", out JsonElement migrations) ||
                migrations.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The controlled migration manifest contains no migration list.");
            }

            JsonElement? requested = null;
            foreach (JsonElement migration in migrations.EnumerateArray())
            {
                string key = migration.GetProperty("versionKey").GetString() ?? string.Empty;
                if (key.Equals(normalizedVersionKey, StringComparison.Ordinal))
                {
                    requested = migration;
                    break;
                }
            }

            if (!requested.HasValue)
                throw new InvalidOperationException(
                    $"Controlled migration '{normalizedVersionKey}' is not present in Database/MigrationManifest.json.");

            JsonElement entry = requested.Value;
            string relativeFile = entry.GetProperty("file").GetString() ?? string.Empty;
            string expectedHash = (entry.GetProperty("sha256").GetString() ?? string.Empty).Trim().ToLowerInvariant();
            string migrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", relativeFile));
            string migrationsRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Database", "Migrations")) +
                                    Path.DirectorySeparatorChar;

            if (string.IsNullOrWhiteSpace(relativeFile) ||
                string.IsNullOrWhiteSpace(expectedHash) ||
                !migrationPath.StartsWith(migrationsRoot, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(migrationPath))
            {
                throw new InvalidOperationException(
                    $"Controlled migration package is incomplete for '{normalizedVersionKey}'.");
            }

            byte[] migrationBytes = await File.ReadAllBytesAsync(migrationPath).ConfigureAwait(false);
            string actualFileHash = Convert.ToHexString(SHA256.HashData(migrationBytes)).ToLowerInvariant();
            if (!actualFileHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Controlled migration package checksum mismatch for '{normalizedVersionKey}'. No database change was attempted.");
            }

            DataTable ledger = await _database.ExecuteQueryAsync(@"
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(128)) AS MigrationChecksum WHERE 1=0;
END
ELSE
BEGIN
    SELECT MigrationChecksum
    FROM dbo.LIMS_SchemaVersions
    WHERE VersionKey=@VersionKey;
END",
                new[]
                {
                    new SqlParameter("@VersionKey", SqlDbType.NVarChar, 100)
                    {
                        Value = normalizedVersionKey
                    }
                }).ConfigureAwait(false);

            if (ledger.Rows.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Migration Required: controlled database migration '{normalizedVersionKey}' is not recorded. " +
                    (AppConfig.IsProduction
                        ? "Production startup is verify-only and did not modify the database. Apply the approved deployment package, then restart PharmaLIMS."
                        : "Development verification did not modify the database. Restart the Debug build to run the bounded authentication reconciliation, or run Database Maintenance."));
            }

            string recordedHash = Convert.ToString(ledger.Rows[0]["MigrationChecksum"])?.Trim() ?? string.Empty;
            if (!recordedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Migration Required: database ledger checksum for '{normalizedVersionKey}' does not match the installed controlled package. " +
                    "No database change was attempted.");
            }

            // A matching ledger checksum proves which controlled SQL was recorded, but it does not
            // prove that the physical schema still satisfies that migration after manual drift, a
            // damaged restore, or an older startup-migrator defect. Authentication-critical
            // migrations therefore receive a second, read-only structural contract check before
            // Login is allowed to query dbo.Users. No DDL/DML is executed here.
            if (normalizedVersionKey.Equals(AuthenticationLoginCompatibilityMigrationKey, StringComparison.Ordinal) ||
                normalizedVersionKey.Equals(UserAdministrationSecurityMigrationKey, StringComparison.Ordinal) ||
                normalizedVersionKey.Equals(UserAdministrationWriteCompatibilityMigrationKey, StringComparison.Ordinal) ||
                normalizedVersionKey.Equals(UserAdministrationSignatureEvidenceMigrationKey, StringComparison.Ordinal))
            {
                await using SqlConnection verificationConnection = _database.CreateConnection();
                await verificationConnection.OpenAsync().ConfigureAwait(false);

                bool postconditionsSatisfied = await RecordedMigrationPostconditionsSatisfiedAsync(
                    normalizedVersionKey,
                    verificationConnection,
                    transaction: null).ConfigureAwait(false);

                if (!postconditionsSatisfied)
                {
                    throw new InvalidOperationException(
                        $"Migration Required: database schema does not satisfy the controlled structural contract for '{normalizedVersionKey}'. " +
                        (AppConfig.IsProduction
                            ? "Production startup is verify-only and did not modify the database. Run the approved deployment process, then restart PharmaLIMS."
                            : "Development verification found legacy authentication schema drift. Restart the Debug build to run the bounded authentication reconciliation, or run Database Maintenance."));
                }
            }

            ApplicationLogger.Information(
                $"Controlled migration '{normalizedVersionKey}' verified read-only against package bytes, database ledger, and required schema postconditions.");
        }

    }
}
