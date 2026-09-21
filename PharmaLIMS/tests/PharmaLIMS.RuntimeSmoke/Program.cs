using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

internal static class Program
{
    private const string ExpectedLoginTitle = "PharmaLIMS - Login";
    private const string ReadinessEventVariable = "PHARMALIMS_RUNTIME_SMOKE_READY_EVENT";

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Runtime smoke requires Windows because PharmaLIMS is a WPF application.");
            return 2;
        }

        string? projectRoot = FindProjectRoot();
        if (projectRoot == null)
        {
            Console.Error.WriteLine("PharmaLIMS project root was not found.");
            return 2;
        }

        string masterConnectionString = Environment.GetEnvironmentVariable("PHARMALIMS_TEST_MASTER_CONNECTION_STRING")
            ?? "Server=(localdb)\\MSSQLLocalDB;Integrated Security=true;Encrypt=false;TrustServerCertificate=true;Connection Timeout=15;";
        string databaseName = "PharmaLIMS_RuntimeSmoke_" + Guid.NewGuid().ToString("N")[..12];
        string databaseConnectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        Process? application = null;
        var appSettingsSnapshots = new List<AppSettingsSnapshot>();
        string readinessEventName = @"Local\PharmaLIMS_RuntimeSmoke_LoginReady_" + Guid.NewGuid().ToString("N");
        using EventWaitHandle readinessEvent = new(false, EventResetMode.ManualReset, readinessEventName);
        try
        {
            await ExecuteAsync(masterConnectionString, $"CREATE DATABASE [{databaseName}];", 60);
            Console.WriteLine("Created disposable runtime-smoke database " + databaseName + ".");

            string appPath = ReadArgument(args, "--app")
                ?? Path.Combine(projectRoot, "bin", "Debug", "net8.0-windows7.0", "PharmaLIMS.exe");
            appPath = Path.GetFullPath(appPath);
            if (!File.Exists(appPath))
                throw new FileNotFoundException("Debug PharmaLIMS executable was not found. Build PharmaLIMS Debug before RuntimeSmoke.", appPath);

            // AppConfig intentionally verifies the connected database against the controlled
            // Database:Database setting. RuntimeSmoke uses a disposable database, so bind the
            // copied Debug appsettings files to that disposable identity instead of weakening
            // the production database-identity guard or teaching AppConfig to trust its own
            // connection-string override.
            string smokeSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            string applicationSettingsPath = Path.Combine(
                Path.GetDirectoryName(appPath) ?? projectRoot,
                "appsettings.json");

            appSettingsSnapshots.Add(BindRuntimeSmokeDatabaseIdentity(smokeSettingsPath, databaseName));
            if (!Path.GetFullPath(applicationSettingsPath).Equals(
                    Path.GetFullPath(smokeSettingsPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                appSettingsSnapshots.Add(BindRuntimeSmokeDatabaseIdentity(applicationSettingsPath, databaseName));
            }

            // RuntimeSmoke is intentionally executed as Debug/Development. This allows LocalDB
            // without weakening the Production TLS/credential policy. The Release WPF binary is
            // built separately by the same release runner. Schema creation here uses the actual
            // controlled migrator, not a test reimplementation.
            Environment.SetEnvironmentVariable("PHARMALIMS_CONNECTION_STRING", databaseConnectionString);
            Environment.SetEnvironmentVariable("PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES", "false");

            DatabaseConnection database = new();
            StartupDatabaseMigrator migrator = new(database);
            migrator.ProgressChanged += message => Console.WriteLine("[MIGRATION] " + message);
            await migrator.ApplyRequiredUpdatesAsync().ConfigureAwait(false);

            ProcessStartInfo startInfo = new(appPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appPath) ?? projectRoot
            };
            startInfo.Environment["PHARMALIMS_CONNECTION_STRING"] = databaseConnectionString;
            startInfo.Environment["PHARMALIMS_APPLY_STARTUP_DATABASE_UPDATES"] = "false";
            startInfo.Environment[ReadinessEventVariable] = readinessEventName;

            application = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PharmaLIMS process could not be started.");

            string observedTitle = await WaitForInteractiveLoginAsync(application, readinessEvent, TimeSpan.FromSeconds(45));
            if (!observedTitle.Equals(ExpectedLoginTitle, StringComparison.Ordinal))
                throw new InvalidOperationException($"Expected '{ExpectedLoginTitle}' but observed '{observedTitle}'.");

            Console.WriteLine("WPF RuntimeSmoke PASS: controlled migrations completed, Login readiness signal was received, and the Login window is interactive.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WPF RuntimeSmoke FAILED.");
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

            for (int index = appSettingsSnapshots.Count - 1; index >= 0; index--)
            {
                try
                {
                    File.WriteAllBytes(
                        appSettingsSnapshots[index].Path,
                        appSettingsSnapshots[index].OriginalBytes);
                }
                catch (Exception cleanupEx)
                {
                    Console.Error.WriteLine("Runtime-smoke appsettings restore warning: " + cleanupEx.Message);
                }
            }

            try
            {
                SqlConnection.ClearAllPools();
                await ExecuteAsync(masterConnectionString, $@"
IF DB_ID(N'{databaseName}') IS NOT NULL
BEGIN
    ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [{databaseName}];
END;", 60);
            }
            catch (Exception cleanupEx)
            {
                Console.Error.WriteLine("Runtime-smoke database cleanup warning: " + cleanupEx.Message);
            }
        }
    }

    private static async Task<string> WaitForInteractiveLoginAsync(
        Process process,
        EventWaitHandle readinessEvent,
        TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string lastTitle = string.Empty;
        bool readinessReceived = false;

        while (stopwatch.Elapsed < timeout)
        {
            if (process.HasExited)
                throw new InvalidOperationException("PharmaLIMS exited before Login became interactive. ExitCode=" + process.ExitCode);

            process.Refresh();
            lastTitle = process.MainWindowTitle ?? string.Empty;
            readinessReceived = readinessEvent.WaitOne(0);

            if (readinessReceived && lastTitle.Equals(ExpectedLoginTitle, StringComparison.Ordinal))
                return lastTitle;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Login did not become interactive within {timeout.TotalSeconds:0} seconds. " +
            $"ReadinessSignal={readinessReceived}; LastTitle='{lastTitle}'.");
    }

    private static AppSettingsSnapshot BindRuntimeSmokeDatabaseIdentity(
        string settingsPath,
        string databaseName)
    {
        if (!File.Exists(settingsPath))
        {
            throw new FileNotFoundException(
                "RuntimeSmoke requires the copied Development appsettings.json before AppConfig is initialized.",
                settingsPath);
        }

        byte[] originalBytes = File.ReadAllBytes(settingsPath);
        JsonNode root = JsonNode.Parse(originalBytes)
            ?? throw new InvalidOperationException("RuntimeSmoke appsettings.json is empty or invalid JSON.");
        JsonObject database = root["Database"] as JsonObject
            ?? throw new InvalidOperationException("RuntimeSmoke appsettings.json is missing the Database object.");

        database["Database"] = databaseName;
        string controlledJson = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(settingsPath, controlledJson + Environment.NewLine);

        return new AppSettingsSnapshot(settingsPath, originalBytes);
    }

    private sealed record AppSettingsSnapshot(string Path, byte[] OriginalBytes);

    private static async Task ExecuteAsync(string connectionString, string sql, int timeoutSeconds)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection) { CommandTimeout = timeoutSeconds };
        await command.ExecuteNonQueryAsync();
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
}
