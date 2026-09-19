using PharmaLIMS;
using PharmaLIMS.Infrastructure;

internal static class Program
{
    private const string ConfirmationArgument = "--confirm-development-maintenance";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (!args.Any(arg => arg.Equals(ConfirmationArgument, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(
                    "Maintenance was not confirmed. Re-run with " + ConfirmationArgument +
                    " after verifying the Development database target.");
                return 2;
            }

            if (!AppConfig.IsDevelopment)
            {
                Console.Error.WriteLine(
                    "Database maintenance runner is Development-only. Production schema changes must use the approved deployment process.");
                return 3;
            }

            Console.WriteLine("PharmaLIMS controlled Development database maintenance");
            Console.WriteLine("Server: " + AppConfig.SqlServerName);
            Console.WriteLine("Database: " + AppConfig.DatabaseName);
            Console.WriteLine("Environment: " + AppConfig.EnvironmentName);
            Console.WriteLine();

            var database = new DatabaseConnection();
            var migrator = new StartupDatabaseMigrator(database);
            migrator.ProgressChanged += progress => Console.WriteLine("[MAINTENANCE] " + progress);

            await migrator.ApplyRequiredUpdatesAsync().ConfigureAwait(false);

            var preflight = new SystemPreflightService(database);
            SystemPreflightReport report = await preflight.RunAsync().ConfigureAwait(false);

            foreach (SystemPreflightCheck check in report.Checks)
                Console.WriteLine($"[{check.Status}] {check.Area} / {check.Check}: {check.Details}");

            if (!report.CanProceed)
            {
                Console.Error.WriteLine($"Maintenance completed but System Preflight has {report.BlockerCount} blocker(s).");
                return 4;
            }

            Console.WriteLine("Maintenance completed. System Preflight PASS.");
            return 0;
        }
        catch (Exception ex)
        {
            ApplicationLogger.Error("Standalone Development database maintenance failed.", ex);
            Console.Error.WriteLine(UserFacingError.SafeMessage(ex, "Development database maintenance"));
            Console.Error.WriteLine("Log folder: " + ApplicationLogger.LogDirectory);
            return 1;
        }
    }
}
