using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Diagnostics;

namespace PharmaLIMS
{
    public partial class App : Application
    {
        private IServiceProvider? _serviceProvider;
        private bool _shutdownRequestedAfterUnhandledException;
        private DispatcherTimer? _sessionTimeoutTimer;
        private long _lastUserActivityTimestamp = Stopwatch.GetTimestamp();
        private bool _sessionTimeoutInProgress;

        public static IServiceProvider? ServiceProvider => (Application.Current as App)?._serviceProvider;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterGlobalExceptionHandlers();

            try
            {
                string connectionString = AppConfig.ConnectionString;
                ApplicationLogger.Information(
                    $"Starting PharmaLIMS. Server='{AppConfig.SqlServerName}', Database='{AppConfig.DatabaseName}', Environment='{AppConfig.EnvironmentName}'.");

                var services = new ServiceCollection();

                services.AddSingleton<DatabaseConnection>();
                services.AddSingleton<StartupDatabaseMigrator>();
                services.AddSingleton<SystemPreflightService>();
                services.AddSingleton<UserRepository>();
                services.AddSingleton<UserAdministrationService>();
                services.AddSingleton<IAuthService, AuthService>();

                services.AddTransient<QualityEventRepository>();
                services.AddTransient<IQualityEventService, QualityEventService>();

                services.AddTransient<Login>();
                services.AddTransient<MainWindow>();
                services.AddTransient<NewSampleDialog>();
                services.AddTransient<ResultsEntry>();
                services.AddTransient<EMResultsEntry>();
                services.AddTransient<EMPlanning>();
                services.AddTransient<QualityEventInvestigation>();
                services.AddTransient<ReportCertificate>();
                services.AddTransient<ReportsTrends>();
                services.AddTransient<ElectronicSignature>();
                services.AddTransient<SampleManagement>();
                services.AddTransient<SampleDetails>();
                services.AddTransient<AISystemReview>();
                services.AddTransient<SystemPreflight>();
                services.AddTransient<UserManagement>();
                services.AddTransient<ChangePassword>();

                _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

                var loginWindow = _serviceProvider.GetRequiredService<Login>();
                MainWindow = loginWindow;

                // Login must not query a partially upgraded Users table. Before credentials
                // can be entered, run only the checksum-controlled additive identity-security
                // prerequisites required by authentication, mandatory password change, and controlled
                // User Management signature evidence. These migrations never change passwords,
                // activation, permissions, roles, or laboratory/sample/result data. All broader
                // schema/deployment migrations remain explicit maintenance actions.
                loginWindow.SetStartupBusy(true, "Verifying secure sign-in database structure...");
                loginWindow.Show();

                StartupDatabaseMigrator startupMigrator =
                    _serviceProvider.GetRequiredService<StartupDatabaseMigrator>();

                // Production startup is verification-only. Runtime processes must never mutate
                // the validated database schema merely because a workstation started.
                // A Debug/Development build automatically reconciles ONLY the four controlled
                // authentication/user-administration prerequisite migrations needed before Login and User Management can operate safely.
                // This bounded path is intentionally separate from broader Database Maintenance:
                // it does not apply unrelated migrations and cannot grant roles/permissions.
                if (AppConfig.IsDevelopment)
                {
                    await startupMigrator.ApplyControlledMigrationAsync(
                        StartupDatabaseMigrator.AuthenticationLoginCompatibilityMigrationKey);
                    await startupMigrator.ApplyControlledMigrationAsync(
                        StartupDatabaseMigrator.UserAdministrationSecurityMigrationKey);
                    await startupMigrator.ApplyControlledMigrationAsync(
                        StartupDatabaseMigrator.UserAdministrationWriteCompatibilityMigrationKey);
                    await startupMigrator.ApplyControlledMigrationAsync(
                        StartupDatabaseMigrator.UserAdministrationSignatureEvidenceMigrationKey);
                }
                else
                {
                    await startupMigrator.VerifyControlledMigrationAsync(
                        StartupDatabaseMigrator.AuthenticationLoginCompatibilityMigrationKey);
                    await startupMigrator.VerifyControlledMigrationAsync(
                        StartupDatabaseMigrator.UserAdministrationSecurityMigrationKey);
                    await startupMigrator.VerifyControlledMigrationAsync(
                        StartupDatabaseMigrator.UserAdministrationWriteCompatibilityMigrationKey);
                    await startupMigrator.VerifyControlledMigrationAsync(
                        StartupDatabaseMigrator.UserAdministrationSignatureEvidenceMigrationKey);
                }

                loginWindow.SetStartupBusy(false);
                SignalRuntimeSmokeLoginReady();
                ApplicationLogger.Information(
                    "Authentication and user-administration security schema verified without uncontrolled startup DDL. " +
                    "Production startup is verify-only; broader migrations remain controlled deployment/maintenance actions. Development auto-reconciles authentication prerequisites only.");

                StartSessionTimeoutMonitor();

                ApplicationLogger.Information("PharmaLIMS startup completed and the login window was displayed.");
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Application startup failed.", ex);

                string userMessage = GetStartupErrorMessage(ex);

                MessageBox.Show(
                    "PharmaLIMS could not start.\n\n" +
                    userMessage +
                    "\n\nLog folder:\n" + ApplicationLogger.LogDirectory,
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(1);
            }
        }

        private static void SignalRuntimeSmokeLoginReady()
        {
            const string variableName = "PHARMALIMS_RUNTIME_SMOKE_READY_EVENT";
            string? eventName = Environment.GetEnvironmentVariable(variableName);
            if (string.IsNullOrWhiteSpace(eventName))
                return;

            try
            {
                using EventWaitHandle readyEvent = EventWaitHandle.OpenExisting(eventName);
                readyEvent.Set();
            }
            catch (Exception ex)
            {
                // This signal exists only for the controlled RuntimeSmoke child process.
                // A missing/invalid test event must never change normal application startup.
                ApplicationLogger.Warning("RuntimeSmoke readiness signal could not be emitted. " + ex.Message);
            }
        }

        private static string GetStartupErrorMessage(Exception exception)
        {
            for (Exception? current = exception; current != null; current = current.InnerException)
            {
                if (current is DatabaseMigrationException migrationException)
                    return migrationException.OperatorMessage;

                if (current is Microsoft.Data.SqlClient.SqlException sqlException)
                {
                    if (sqlException.Number == 1222)
                    {
                        return "Database startup could not obtain the required schema lock within the allowed time. " +
                               "Close other running PharmaLIMS/debug sessions and any open SSMS transaction, then start again.";
                    }

                    if (sqlException.Number == -2)
                    {
                        return "Database startup timed out while applying or verifying controlled updates. " +
                               "Check SQL Server availability and the PharmaLIMS log for the last migration step.";
                    }
                }
            }

            return Infrastructure.UserFacingError.SafeMessage(exception, "PharmaLIMS startup");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            StopSessionTimeoutMonitor();
            UnregisterGlobalExceptionHandlers();

            if (_serviceProvider is IDisposable disposable)
                disposable.Dispose();

            base.OnExit(e);
        }

        private void StartSessionTimeoutMonitor()
        {
            _lastUserActivityTimestamp = Stopwatch.GetTimestamp();
            InputManager.Current.PreProcessInput += OnUserInput;

            _sessionTimeoutTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _sessionTimeoutTimer.Tick += OnSessionTimeoutTick;
            _sessionTimeoutTimer.Start();
        }

        private void StopSessionTimeoutMonitor()
        {
            InputManager.Current.PreProcessInput -= OnUserInput;

            if (_sessionTimeoutTimer == null)
                return;

            _sessionTimeoutTimer.Stop();
            _sessionTimeoutTimer.Tick -= OnSessionTimeoutTick;
            _sessionTimeoutTimer = null;
        }

        private void OnUserInput(object sender, PreProcessInputEventArgs e)
        {
            if (_serviceProvider?.GetService<IAuthService>()?.IsUserLoggedIn() == true)
                _lastUserActivityTimestamp = Stopwatch.GetTimestamp();
        }

        private void OnSessionTimeoutTick(object? sender, EventArgs e)
        {
            if (_sessionTimeoutInProgress || _serviceProvider == null)
                return;

            IAuthService authService = _serviceProvider.GetRequiredService<IAuthService>();
            if (!authService.IsUserLoggedIn())
            {
                _lastUserActivityTimestamp = Stopwatch.GetTimestamp();
                return;
            }

            if (Stopwatch.GetElapsedTime(_lastUserActivityTimestamp) < TimeSpan.FromMinutes(AppConfig.SessionTimeoutMinutes))
                return;

            _sessionTimeoutInProgress = true;

            try
            {
                string username = authService.GetCurrentUser()?.Username ?? Login.CurrentUser;
                ApplicationLogger.Information(
                    $"User session timed out after {AppConfig.SessionTimeoutMinutes} minutes of inactivity. User='{username}'.");

                authService.ClearCurrentUser();
                Login.ClearCurrentUserContext();

                Login loginWindow = _serviceProvider.GetRequiredService<Login>();
                MainWindow = loginWindow;
                loginWindow.Show();
                loginWindow.Activate();

                foreach (Window window in Windows.Cast<Window>().ToArray())
                {
                    if (ReferenceEquals(window, loginWindow))
                        continue;

                    window.Hide();
                    window.Close();
                }

                MessageBox.Show(
                    "Your session ended after a period of inactivity. Sign in again to continue.",
                    "Session Timeout",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Automatic session timeout failed.", ex);
                Shutdown(1);
            }
            finally
            {
                _lastUserActivityTimestamp = Stopwatch.GetTimestamp();
                _sessionTimeoutInProgress = false;
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void UnregisterGlobalExceptionHandlers()
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ApplicationLogger.Error(
                "Unhandled UI exception. The application will close to protect data integrity.",
                e.Exception);

            e.Handled = true;

            if (_shutdownRequestedAfterUnhandledException)
                return;

            _shutdownRequestedAfterUnhandledException = true;

            MessageBox.Show(
                "An unexpected error occurred. PharmaLIMS will close to protect data integrity.\n\n" +
                "Log folder:\n" + ApplicationLogger.LogDirectory,
                "Application Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }

        private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        {
            ApplicationLogger.Error(
                "Unhandled application-domain exception.",
                e.ExceptionObject as Exception ??
                new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal error"));
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ApplicationLogger.Error("Unobserved background task exception.", e.Exception);
            e.SetObserved();
        }
    }
}
