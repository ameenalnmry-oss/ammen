using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string ExpectedLoginTitle = "PharmaLIMS - Login";
    private const string ReadinessEventVariable = "PHARMALIMS_RUNTIME_SMOKE_READY_EVENT";
    private const string ConnectionVariable = "PHARMALIMS_PRODUCTION_SMOKE_CONNECTION_STRING";
    private static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("PharmaLIMS.DatabaseCredential.v1");

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Production artifact smoke requires Windows because PharmaLIMS is WPF and DPAPI-protected configuration is Windows-specific.");
            return 2;
        }

        string appPath = ReadArgument(args, "--app") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(appPath))
        {
            Console.Error.WriteLine("Usage: PharmaLIMS.ProductionArtifactSmoke --app <published PharmaLIMS.exe>");
            return 2;
        }

        appPath = Path.GetFullPath(appPath);
        if (!File.Exists(appPath))
        {
            Console.Error.WriteLine("Published PharmaLIMS executable was not found: " + appPath);
            return 2;
        }

        string? connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine(ConnectionVariable + " is required and must target a controlled, pre-migrated Production-smoke SQL Server database with trusted TLS.");
            return 2;
        }

        string appDirectory = Path.GetDirectoryName(appPath) ?? throw new InvalidOperationException("Published application directory could not be resolved.");
        string manifestPath = Path.Combine(appDirectory, "Database", "MigrationManifest.json");
        string settingsPath = Path.Combine(appDirectory, "appsettings.json");
        byte[]? originalSettings = File.Exists(settingsPath) ? await File.ReadAllBytesAsync(settingsPath) : null;
        Process? application = null;
        string readinessEventName = @"Local\PharmaLIMS_ProductionArtifactSmoke_LoginReady_" + Guid.NewGuid().ToString("N");
        using EventWaitHandle readinessEvent = new(false, EventResetMode.ManualReset, readinessEventName);

        try
        {
            SqlConnectionStringBuilder builder = ValidateSmokeConnectionString(connectionString);
            await VerifyMigrationLedgerAsync(connectionString, manifestPath);
            string beforeFingerprint = await CaptureDatabaseFingerprintAsync(connectionString);

            await WriteTemporaryProductionSettingsAsync(settingsPath, builder);

            ProcessStartInfo startInfo = new(appPath)
            {
                UseShellExecute = false,
                WorkingDirectory = appDirectory
            };
            startInfo.Environment.Remove("PHARMALIMS_CONNECTION_STRING");
            startInfo.Environment.Remove("PHARMALIMS_DPAPI_PROTECTED_PASSWORD");
            startInfo.Environment["PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES"] = "false";
            startInfo.Environment[ReadinessEventVariable] = readinessEventName;

            application = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Published PharmaLIMS process could not be started.");

            string observedTitle = await WaitForInteractiveLoginAsync(application, readinessEvent, TimeSpan.FromSeconds(60));
            if (!observedTitle.Equals(ExpectedLoginTitle, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected '{ExpectedLoginTitle}' but observed '{observedTitle}'.");

            string afterFingerprint = await CaptureDatabaseFingerprintAsync(connectionString);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(beforeFingerprint),
                    Convert.FromHexString(afterFingerprint)))
            {
                throw new InvalidOperationException(
                    "The database fingerprint changed while the published Production binary started. " +
                    "Production startup must be verify-only and must not perform DDL or DML before Login readiness.");
            }

            Console.WriteLine("Production Artifact Smoke PASS: published Release/Production binary reached interactive Login with trusted-TLS configuration and the database fingerprint remained unchanged.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Production Artifact Smoke FAILED.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (application != null)
            {
                try
                {
                    if (!application.HasExited)
                    {
                        application.Kill(entireProcessTree: true);
                        await application.WaitForExitAsync();
                    }
                }
                catch (Exception cleanupEx)
                {
                    Console.Error.WriteLine("Application cleanup warning: " + cleanupEx.Message);
                }
                application.Dispose();
            }

            try
            {
                if (originalSettings == null)
                {
                    if (File.Exists(settingsPath))
                        File.Delete(settingsPath);
                }
                else
                {
                    await File.WriteAllBytesAsync(settingsPath, originalSettings);
                }
            }
            catch (Exception restoreEx)
            {
                Console.Error.WriteLine("Production appsettings restore warning: " + restoreEx.Message);
            }
        }
    }

    private static SqlConnectionStringBuilder ValidateSmokeConnectionString(string connectionString)
    {
        SqlConnectionStringBuilder builder = new(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
            throw new InvalidOperationException("Production smoke connection string must include Server and Database.");
        if (builder.TrustServerCertificate)
            throw new InvalidOperationException("Production smoke requires TrustServerCertificate=false.");

        string encrypt = builder.ContainsKey("Encrypt") ? builder["Encrypt"]?.ToString() ?? string.Empty : string.Empty;
        bool encrypted = encrypt.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                         encrypt.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) ||
                         encrypt.Equals("Strict", StringComparison.OrdinalIgnoreCase);
        if (!encrypted)
            throw new InvalidOperationException("Production smoke requires Encrypt=true/mandatory/strict.");

        if (!builder.IntegratedSecurity && (string.IsNullOrWhiteSpace(builder.UserID) || string.IsNullOrEmpty(builder.Password)))
            throw new InvalidOperationException("SQL-auth Production smoke requires User ID and password in the protected CI/runtime secret.");

        return builder;
    }

    private static async Task WriteTemporaryProductionSettingsAsync(string settingsPath, SqlConnectionStringBuilder builder)
    {
        string protectedPassword = string.Empty;
        if (!builder.IntegratedSecurity)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(builder.Password);
            try
            {
                byte[] encrypted = ProtectedData.Protect(plaintext, DpapiEntropy, DataProtectionScope.CurrentUser);
                protectedPassword = Convert.ToBase64String(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        var document = new
        {
            Environment = "Production",
            Application = new
            {
                SiteName = "PharmaLIMS Controlled Production Smoke",
                DepartmentName = "Microbiology Department",
                WorkstationName = Environment.MachineName,
                ApplicationMode = "MultiDepartment"
            },
            Database = new
            {
                Server = builder.DataSource,
                Database = builder.InitialCatalog,
                IntegratedSecurity = builder.IntegratedSecurity,
                UserId = builder.IntegratedSecurity ? string.Empty : builder.UserID,
                Password = string.Empty,
                DpapiProtectedPassword = protectedPassword,
                Encrypt = true,
                TrustServerCertificate = false,
                CommandTimeoutSeconds = 30
            },
            Runtime = new
            {
                ApplyStartupDatabaseUpdates = false,
                SessionTimeoutMinutes = 15,
                DevelopmentAdminFullPermissions = false,
                AllowEarlyMicrobiologyResults = false,
                AllowLegacyPrmSpecificationFallback = false
            },
            Compliance = new
            {
                EnforceAuditTrail = true,
                EnforceElectronicSignatureStorage = true
            }
        };

        string json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(settingsPath, json, new UTF8Encoding(false));
    }

    private static async Task VerifyMigrationLedgerAsync(string connectionString, string manifestPath)
    {
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Published MigrationManifest.json is missing.", manifestPath);

        using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        JsonElement migrations = manifest.RootElement.GetProperty("migrations");

        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (SqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync();
            await using SqlCommand command = new(@"
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
    THROW 56001, 'LIMS_SchemaVersions is missing from the Production-smoke database.', 1;
SELECT VersionKey, ISNULL(MigrationChecksum,N'') AS MigrationChecksum
FROM dbo.LIMS_SchemaVersions;", connection);
            await using SqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                recorded[reader.GetString(0)] = reader.GetString(1);
        }

        var missing = new List<string>();
        var mismatched = new List<string>();
        foreach (JsonElement migration in migrations.EnumerateArray())
        {
            string key = migration.GetProperty("versionKey").GetString() ?? string.Empty;
            string expected = migration.GetProperty("sha256").GetString() ?? string.Empty;
            if (!recorded.TryGetValue(key, out string? actual))
            {
                string supersededBy = migration.TryGetProperty("supersededBy", out JsonElement value)
                    ? value.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(supersededBy) && recorded.ContainsKey(supersededBy))
                    continue;
                missing.Add(key);
            }
            else if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                mismatched.Add(key);
            }
        }

        if (missing.Count > 0 || mismatched.Count > 0)
        {
            throw new InvalidOperationException(
                "Production-smoke database is not aligned with the packaged migration manifest. " +
                (missing.Count > 0 ? "Missing: " + string.Join(", ", missing) + ". " : string.Empty) +
                (mismatched.Count > 0 ? "Checksum mismatch: " + string.Join(", ", mismatched) + "." : string.Empty));
        }
    }

    private static async Task<string> CaptureDatabaseFingerprintAsync(string connectionString)
    {
        var lines = new List<string>();
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();

        await using (SqlCommand schemaCommand = new(@"
SELECT N'OBJECT|' + s.name + N'.' + o.name + N'|' + CONVERT(nvarchar(20),o.type) + N'|' + CONVERT(nvarchar(33),o.modify_date,126)
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.is_ms_shipped=0
UNION ALL
SELECT N'COLUMN|' + s.name + N'.' + o.name + N'|' + c.name + N'|' + TYPE_NAME(c.user_type_id) + N'|' + CONVERT(nvarchar(20),c.max_length) + N'|' + CONVERT(nvarchar(20),c.precision) + N'|' + CONVERT(nvarchar(20),c.scale) + N'|' + CONVERT(nvarchar(1),c.is_nullable)
FROM sys.columns c JOIN sys.objects o ON o.object_id=c.object_id JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE o.is_ms_shipped=0
UNION ALL
SELECT N'TRIGGER|' + s.name + N'.' + o.name + N'|' + t.name + N'|' + CONVERT(nvarchar(1),t.is_disabled) + N'|' + ISNULL(m.definition,N'')
FROM sys.triggers t JOIN sys.objects o ON o.object_id=t.parent_id JOIN sys.schemas s ON s.schema_id=o.schema_id LEFT JOIN sys.sql_modules m ON m.object_id=t.object_id
WHERE o.is_ms_shipped=0
ORDER BY 1;", connection))
        await using (SqlDataReader reader = await schemaCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(0));
        }

        var tables = new List<(string Schema, string Table)>();
        await using (SqlCommand tableCommand = new(@"
SELECT s.name, t.name
FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
WHERE t.is_ms_shipped=0
ORDER BY s.name,t.name;", connection))
        await using (SqlDataReader reader = await tableCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                tables.Add((reader.GetString(0), reader.GetString(1)));
        }

        using var quoteBuilder = new SqlCommandBuilder();
        foreach ((string schema, string table) in tables)
        {
            string quoted = quoteBuilder.QuoteIdentifier(schema) + "." + quoteBuilder.QuoteIdentifier(table);
            var rowHashes = new List<string>();
            await using SqlCommand dataCommand = new($"SELECT * FROM {quoted};", connection) { CommandTimeout = 120 };
            await using SqlDataReader dataReader = await dataCommand.ExecuteReaderAsync();
            while (await dataReader.ReadAsync())
            {
                using var rowStream = new MemoryStream();
                for (int i = 0; i < dataReader.FieldCount; i++)
                {
                    AppendFingerprintValue(rowStream, dataReader.GetName(i));
                    object value = dataReader.IsDBNull(i) ? DBNull.Value : dataReader.GetValue(i);
                    AppendFingerprintValue(rowStream, value);
                }
                rowHashes.Add(Convert.ToHexString(SHA256.HashData(rowStream.ToArray())).ToLowerInvariant());
            }
            rowHashes.Sort(StringComparer.Ordinal);
            byte[] rowSetBytes = Encoding.UTF8.GetBytes(string.Join("\n", rowHashes));
            string rowSetHash = Convert.ToHexString(SHA256.HashData(rowSetBytes)).ToLowerInvariant();
            lines.Add($"DATA-SHA256|{schema}.{table}|{rowHashes.Count}|{rowSetHash}");
        }

        lines.Sort(StringComparer.Ordinal);
        byte[] bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void AppendFingerprintValue(Stream stream, object value)
    {
        string text = value switch
        {
            DBNull => "<NULL>",
            byte[] bytes => "0x" + Convert.ToHexString(bytes),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };
        byte[] bytesValue = Encoding.UTF8.GetBytes(text);
        byte[] length = BitConverter.GetBytes(bytesValue.Length);
        stream.Write(length, 0, length.Length);
        stream.Write(bytesValue, 0, bytesValue.Length);
    }

    private static async Task<string> WaitForInteractiveLoginAsync(Process process, EventWaitHandle readinessEvent, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string lastTitle = string.Empty;
        bool readinessReceived = false;

        while (stopwatch.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new InvalidOperationException("Published PharmaLIMS exited before Login became interactive. ExitCode=" + process.ExitCode);

            process.Refresh();
            lastTitle = process.MainWindowTitle ?? string.Empty;
            readinessReceived = readinessEvent.WaitOne(0);
            if (readinessReceived && lastTitle.Equals(ExpectedLoginTitle, StringComparison.Ordinal))
                return lastTitle;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Published Production Login did not become interactive within {timeout.TotalSeconds:0} seconds. " +
            $"ReadinessSignal={readinessReceived}; LastTitle='{lastTitle}'.");
    }

    private static string? ReadArgument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }
        return null;
    }
}
