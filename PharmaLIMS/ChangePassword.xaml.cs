using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Windows;

namespace PharmaLIMS
{
    public partial class ChangePassword : Window
    {
        private readonly IAuthService _authService;
        private bool _busy;

        public ChangePassword(IAuthService authService)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            InitializeComponent();

            string username = _authService.GetCurrentUser()?.Username ?? Login.CurrentUser;
            LblAccount.Text = string.IsNullOrWhiteSpace(username)
                ? "Authenticated account"
                : "Account: " + username;

            Loaded += (_, _) => PwdCurrent.Focus();
        }

        private async void BtnChange_Click(object sender, RoutedEventArgs e)
        {
            if (_busy)
                return;

            LblError.Visibility = Visibility.Collapsed;
            if (!string.Equals(PwdNew.Password, PwdConfirm.Password, StringComparison.Ordinal))
            {
                ShowError("New password and confirmation do not match.");
                return;
            }

            try
            {
                SetBusy(true);
                bool changed = await _authService.ChangeCurrentUserPasswordAsync(
                    PwdCurrent.Password,
                    PwdNew.Password).ConfigureAwait(true);

                PwdCurrent.Clear();
                PwdNew.Clear();
                PwdConfirm.Clear();

                if (!changed)
                {
                    ShowError("The current password could not be verified or the account changed. Try again or contact an authorized User Manager.");
                    return;
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Self-service password change failed.", ex);
                ShowError(UserFacingError.SafeMessage(ex, "Change Password"));
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            BtnChange.IsEnabled = !busy;
            BtnCancel.IsEnabled = !busy;
            BtnChange.Content = busy ? "Changing..." : "Change Password";
        }

        private void ShowError(string message)
        {
            LblError.Text = message;
            LblError.Visibility = Visibility.Visible;
            PwdCurrent.Focus();
        }
    }
}
