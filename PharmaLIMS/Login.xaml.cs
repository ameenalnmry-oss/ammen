using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using Microsoft.Data.SqlClient;
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace PharmaLIMS
{
    [SupportedOSPlatform("windows7.0")]
    public partial class Login : Window
    {
        private readonly IAuthService? _authService;
        private readonly IServiceProvider? _serviceProvider;
        private bool _isSigningIn;
        private static TimeSpan SignInTimeout => TimeSpan.FromSeconds(Math.Max(40, AppConfig.CommandTimeoutSeconds + 15));

        public Login()
        {
            InitializeComponent();
            ResetCurrentUserContext();
            Loaded += async (_, _) =>
            {
                if (txtUsername.IsEnabled)
                    txtUsername.Focus();

                await WarmDatabaseConnectionAsync();
            };
        }

        public Login(IAuthService authService, IServiceProvider serviceProvider) : this()
        {
            _authService = authService;
            _serviceProvider = serviceProvider;
        }

        public static string CurrentUser = string.Empty;
        public static string CurrentUserRole = string.Empty;
        public static string CurrentUserFullName = string.Empty;
        public static string CurrentDepartment = string.Empty;
        public static string CurrentSection = string.Empty;
        public static bool CanAccessWater;
        public static bool CanAccessEM;
        public static bool CanRegisterSamples;
        public static bool CanEnterResults;
        public static bool CanReviewResults;
        public static bool CanApproveResults;
        public static bool CanIssueCOA;
        public static bool CanCancelCOA;
        public static bool CanAccessReports;
        public static bool CanManageUsers;
        public static bool CanManageSettings;

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (_isSigningIn)
                return;

            string username = txtUsername.Text.Trim();
            string password = txtPassword.Password;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                ShowError("Please enter username and password.");
                return;
            }

            _isSigningIn = true;
            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "Signing in...";
            lblError.Visibility = Visibility.Collapsed;
            bool authenticationCompleted = false;

            try
            {
                if (_authService == null || _serviceProvider == null)
                    throw new InvalidOperationException("Authentication services are not available.");

                ApplicationLogger.Information($"Sign-in started for account '{username}'.");
                using var signInCts = new CancellationTokenSource(SignInTimeout);
                var user = await _authService.AuthenticateAsync(username, password, signInCts.Token);
                txtPassword.Clear();

                if (user == null)
                {
                    ResetCurrentUserContext();
                    ShowError("Invalid username or password, or the account is unavailable.");
                    txtPassword.Focus();
                    return;
                }

                authenticationCompleted = true;

                if (user.MustChangePassword)
                {
                    ApplicationLogger.Information(
                        $"Account '{username}' authenticated with a temporary password. Mandatory personal password change is required before workspace access.");

                    var passwordChange = new ChangePassword(_authService) { Owner = this };
                    if (passwordChange.ShowDialog() != true)
                    {
                        ClearFailedSessionState(authenticationCompleted);
                        ShowError("Password change is required before PharmaLIMS can be opened. Sign in again and complete the password change.");
                        return;
                    }

                    user = _authService.GetCurrentUser();
                    if (user == null || user.MustChangePassword)
                    {
                        ClearFailedSessionState(authenticationCompleted);
                        ShowError("The required password change could not be confirmed. No workspace session was created.");
                        return;
                    }
                }

                ApplicationLogger.Information($"Authentication completed for account '{username}'. Opening the main workspace.");

                CurrentUser = user.Username ?? string.Empty;
                CurrentUserRole = user.Role ?? string.Empty;
                CurrentUserFullName = user.FullName ?? string.Empty;
                CurrentDepartment = user.Department ?? string.Empty;
                CurrentSection = user.Section ?? string.Empty;
                bool grantDevelopmentAdminPermissions =
                    AppConfig.DevelopmentAdminFullPermissions &&
                    IsAdministrativeRole(CurrentUserRole);

                CanAccessWater = grantDevelopmentAdminPermissions || user.CanAccessWater;
                CanAccessEM = grantDevelopmentAdminPermissions || user.CanAccessEM;
                CanRegisterSamples = grantDevelopmentAdminPermissions || user.CanRegisterSamples;
                CanEnterResults = grantDevelopmentAdminPermissions || user.CanEnterResults;
                CanReviewResults = grantDevelopmentAdminPermissions || user.CanReviewResults;
                CanApproveResults = grantDevelopmentAdminPermissions || user.CanApproveResults;
                CanIssueCOA = grantDevelopmentAdminPermissions || user.CanIssueCOA;
                CanCancelCOA = grantDevelopmentAdminPermissions || user.CanCancelCOA;
                CanAccessReports = grantDevelopmentAdminPermissions || user.CanAccessReports;
                CanManageUsers = grantDevelopmentAdminPermissions || user.CanManageUsers;
                CanManageSettings = grantDevelopmentAdminPermissions || user.CanManageSettings;

                var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                Application.Current.MainWindow = mainWindow;
                mainWindow.WindowState = WindowState.Maximized;
                mainWindow.Show();
                mainWindow.Activate();
                Close();
            }
            catch (OperationCanceledException ex)
            {
                txtPassword.Clear();
                ClearFailedSessionState(authenticationCompleted);
                ShowError(authenticationCompleted
                    ? "Password was accepted, but the PharmaLIMS workspace timed out while opening. The session was cancelled; review the application log and System/Database readiness."
                    : "Sign-in timed out because the database is busy or unavailable. No session was created. Close any active database maintenance or open SQL transaction, then try again.");
                ApplicationLogger.Warning(authenticationCompleted
                    ? "Authentication succeeded but workspace opening timed out: " + Infrastructure.UserFacingError.SafeMessage(ex)
                    : "Sign-in timed out before authentication completed: " + Infrastructure.UserFacingError.SafeMessage(ex));
            }
            catch (SqlException ex) when (ex.Number == 1222 || ex.Number == -2)
            {
                txtPassword.Clear();
                ClearFailedSessionState(authenticationCompleted);
                if (authenticationCompleted)
                {
                    ShowError("Password was accepted, but the PharmaLIMS workspace could not open because SQL Server remained busy. The session was cancelled. Review the log and database readiness.");
                    ApplicationLogger.Error("Authentication succeeded but workspace opening failed because SQL Server was busy or locked.", ex);
                }
                else
                {
                    ShowError(ex.Number == 1222
                        ? "Sign-in is blocked by a database lock. No session was created. Close the session holding the database lock and try again."
                        : "Sign-in timed out while waiting for SQL Server. No session was created. Check SQL Server and try again.");
                    ApplicationLogger.Error("Login failed because SQL Server was busy or locked.", ex);
                }
            }
            catch (Exception ex)
            {
                txtPassword.Clear();
                ClearFailedSessionState(authenticationCompleted);
                if (authenticationCompleted)
                {
                    ShowError("Password was accepted, but the PharmaLIMS workspace could not open. The session was cancelled. Review the application log and System/Database readiness; this is not a password rejection.");
                    ApplicationLogger.Error("Authentication succeeded but the main workspace failed to open.", ex);
                }
                else
                {
                    ShowError("Unable to sign in because of an application or database error. Check the application log; no session was created.");
                    ApplicationLogger.Error("Login failed before authentication completed because of an application or database error.", ex);
                }
            }
            finally
            {
                _isSigningIn = false;

                if (IsVisible)
                {
                    BtnLogin.IsEnabled = true;
                    BtnLogin.Content = "Sign In";
                }
            }
        }

        private void ClearFailedSessionState(bool authenticationCompleted)
        {
            if (authenticationCompleted)
                _authService?.ClearCurrentUser();

            ResetCurrentUserContext();
        }

        private static bool IsAdministrativeRole(string? role)
        {
            return role != null &&
                   (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        }

        internal static void ClearCurrentUserContext()
        {
            CurrentUser = string.Empty;
            CurrentUserRole = string.Empty;
            CurrentUserFullName = string.Empty;
            CurrentDepartment = string.Empty;
            CurrentSection = string.Empty;
            CanAccessWater = false;
            CanAccessEM = false;
            CanRegisterSamples = false;
            CanEnterResults = false;
            CanReviewResults = false;
            CanApproveResults = false;
            CanIssueCOA = false;
            CanCancelCOA = false;
            CanAccessReports = false;
            CanManageUsers = false;
            CanManageSettings = false;
        }

        private static void ResetCurrentUserContext()
        {
            ClearCurrentUserContext();
        }

        internal void SetStartupBusy(bool isBusy, string message = "")
        {
            txtUsername.IsEnabled = !isBusy;
            txtPassword.IsEnabled = !isBusy;
            BtnLogin.IsEnabled = !isBusy;
            BtnLogin.Content = isBusy ? "Preparing database..." : "Sign In";

            if (isBusy)
            {
                lblError.Foreground = Brushes.SlateGray;
                lblError.Text = string.IsNullOrWhiteSpace(message)
                    ? "Preparing database. Please wait..."
                    : message;
                lblError.Visibility = Visibility.Visible;
            }
            else
            {
                lblError.Foreground = Brushes.Red;
                lblError.Text = string.Empty;
                lblError.Visibility = Visibility.Collapsed;
                txtUsername.Focus();
            }
        }

        private void ShowError(string message)
        {
            lblError.Text = message;
            lblError.Visibility = Visibility.Visible;
        }

        private static async Task WarmDatabaseConnectionAsync()
        {
            try
            {
                using var warmupCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using SqlConnection connection = new(AppConfig.ConnectionString);
                await connection.OpenAsync(warmupCts.Token);
            }
            catch (OperationCanceledException)
            {
                ApplicationLogger.Warning("Database connection warm-up exceeded 5 seconds and was cancelled. Login remains available.");
            }
            catch (Exception ex)
            {
                // Login itself will present a controlled user-facing error if the
                // database remains unavailable. Pre-warming must never block typing.
                ApplicationLogger.Warning("Database connection warm-up did not complete: " + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }
    }
}
