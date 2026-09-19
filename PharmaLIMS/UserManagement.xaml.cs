using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using PharmaLIMS.Services;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS
{
    public partial class UserManagement : Window
    {
        private readonly UserAdministrationService _administration;
        private User? _selectedUser;
        private bool _loadingSelection;

        public UserManagement(UserAdministrationService administration)
        {
            _administration = administration ?? throw new ArgumentNullException(nameof(administration));
            InitializeComponent();
            lblCurrentManager.Text = "Manager: " + (string.IsNullOrWhiteSpace(Login.CurrentUserFullName)
                ? Login.CurrentUser
                : Login.CurrentUserFullName + " (" + Login.CurrentUser + ")");
            BeginNewUser();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!DatabaseHelper.CanManageUsers(Login.CurrentUser))
            {
                MessageBox.Show(
                    "Access denied. Current User Management permission could not be verified.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Close();
                return;
            }

            await ReloadUsersAsync().ConfigureAwait(true);
        }

        private async Task ReloadUsersAsync(int? selectUserId = null)
        {
            try
            {
                LblStatus.Text = "Loading users...";
                IReadOnlyList<User> users = await _administration.GetUsersAsync(Login.CurrentUser).ConfigureAwait(true);
                GridUsers.ItemsSource = users;

                if (selectUserId.HasValue)
                {
                    User? match = users.FirstOrDefault(user => user.UserId == selectUserId.Value);
                    if (match != null)
                    {
                        GridUsers.SelectedItem = match;
                        GridUsers.ScrollIntoView(match);
                    }
                }

                LblStatus.Text = $"Loaded {users.Count} user account{(users.Count == 1 ? string.Empty : "s")}.";
            }
            catch (UnauthorizedAccessException ex)
            {
                ApplicationLogger.Warning("User Management access ended because current CanManageUsers permission could not be verified: " + ex.Message);
                LblStatus.Text = "User Management permission is no longer available.";
                MessageBox.Show(
                    "Your current User Management permission could not be verified. The window will close.",
                    "Permission Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to load User Management accounts.", ex);
                LblStatus.Text = "Unable to load users.";
                MessageBox.Show(UserFacingError.SafeMessage(ex, "User Management"), "User Management", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) =>
            await ReloadUsersAsync(_selectedUser?.UserId).ConfigureAwait(true);

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            GridUsers.SelectedItem = null;
            BeginNewUser();
        }

        private void BeginNewUser()
        {
            _selectedUser = null;
            _loadingSelection = true;
            try
            {
                lblEditorTitle.Text = "New User";
                lblAccountState.Text = "Create a named PharmaLIMS account. The initial password is temporary and must be changed at first sign-in.";
                ResetSecurityEditorEnabledState();
                TxtUsername.IsReadOnly = false;
                TxtUsername.Text = string.Empty;
                TxtFullName.Text = string.Empty;
                CboRole.SelectedIndex = -1;
                TxtDepartment.Text = "Microbiology";
                TxtSection.Text = string.Empty;
                ChkActive.IsChecked = true;
                SetPermissions(false);
                PwdPassword.Clear();
                PwdConfirm.Clear();
                BtnResetPassword.IsEnabled = false;
                BtnUnlock.IsEnabled = false;
                TxtUsername.Focus();
            }
            finally
            {
                _loadingSelection = false;
            }
        }

        private void GridUsers_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSelection)
                return;

            if (GridUsers.SelectedItem is not User user)
                return;

            _selectedUser = user;
            _loadingSelection = true;
            try
            {
                lblEditorTitle.Text = "Edit User";
                lblAccountState.Text = user.IsLocked
                    ? "Account is currently locked."
                    : user.IsActive ? "Active account." : "Inactive account.";
                TxtUsername.IsReadOnly = true;
                TxtUsername.Text = user.Username;
                TxtFullName.Text = user.FullName;
                SelectRole(user.Role);
                TxtDepartment.Text = user.Department;
                TxtSection.Text = user.Section;
                ChkActive.IsChecked = user.IsActive;
                ChkAccessWater.IsChecked = user.CanAccessWater;
                ChkAccessEM.IsChecked = user.CanAccessEM;
                ChkRegisterSamples.IsChecked = user.CanRegisterSamples;
                ChkEnterResults.IsChecked = user.CanEnterResults;
                ChkReviewResults.IsChecked = user.CanReviewResults;
                ChkApproveResults.IsChecked = user.CanApproveResults;
                ChkIssueCOA.IsChecked = user.CanIssueCOA;
                ChkCancelCOA.IsChecked = user.CanCancelCOA;
                ChkAccessReports.IsChecked = user.CanAccessReports;
                ChkManageUsers.IsChecked = user.CanManageUsers;
                ChkManageSettings.IsChecked = user.CanManageSettings;
                PwdPassword.Clear();
                PwdConfirm.Clear();
                ApplySecurityEditPolicy(user);
                bool editingSelf = user.Username.Equals(Login.CurrentUser, StringComparison.OrdinalIgnoreCase);
                BtnResetPassword.IsEnabled = !editingSelf;
                BtnUnlock.IsEnabled = !editingSelf && (user.IsLocked || user.FailedLoginAttempts > 0);
                PwdPassword.IsEnabled = !editingSelf;
                PwdConfirm.IsEnabled = !editingSelf;
            }
            finally
            {
                _loadingSelection = false;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                User candidate = BuildUserFromEditor();
                bool isNew = _selectedUser == null;

                if (isNew)
                    EnsurePasswordsMatch(requireValue: true);

                string action = isNew ? "User Account Creation" : "User Account and Permission Update";
                ElectronicSignature signature = new ElectronicSignature(
                    isNew ? candidate.Username : _selectedUser!.Username,
                    Login.CurrentUser,
                    action,
                    true) { Owner = this };

                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                SetBusy(true);
                int selectedId;
                if (isNew)
                {
                    selectedId = await _administration.CreateUserAsync(
                        candidate,
                        PwdPassword.Password,
                        Login.CurrentUser,
                        SignatureReason(signature),
                        SignatureMeaning(signature),
                        SignatureSigner(signature)).ConfigureAwait(true);
                    LblStatus.Text = $"User '{candidate.Username}' created successfully.";
                }
                else
                {
                    candidate.UserId = _selectedUser!.UserId;
                    await _administration.UpdateUserAsync(
                        candidate,
                        Login.CurrentUser,
                        SignatureReason(signature),
                        SignatureMeaning(signature),
                        SignatureSigner(signature)).ConfigureAwait(true);
                    selectedId = candidate.UserId;
                    LblStatus.Text = $"User '{candidate.Username}' updated successfully.";
                }

                PwdPassword.Clear();
                PwdConfirm.Clear();
                await ReloadUsersAsync(selectedId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("User Management save failed.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "User Management"), "User Management", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void BtnResetPassword_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
                return;

            User target = _selectedUser;
            try
            {
                EnsurePasswordsMatch(requireValue: true);
                ElectronicSignature signature = new ElectronicSignature(
                    target.Username,
                    Login.CurrentUser,
                    "User Password Reset",
                    true) { Owner = this };

                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                SetBusy(true);
                await _administration.ResetPasswordAsync(
                    target.UserId,
                    PwdPassword.Password,
                    Login.CurrentUser,
                    SignatureReason(signature),
                    SignatureMeaning(signature),
                    SignatureSigner(signature),
                    target.AuthenticationRowVersion).ConfigureAwait(true);

                PwdPassword.Clear();
                PwdConfirm.Clear();
                LblStatus.Text = $"Password reset completed for '{target.Username}'.";
                await ReloadUsersAsync(target.UserId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("User password reset failed.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Password reset"), "User Management", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
                return;

            User target = _selectedUser;
            try
            {
                ElectronicSignature signature = new ElectronicSignature(
                    target.Username,
                    Login.CurrentUser,
                    "User Account Unlock",
                    true) { Owner = this };

                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                SetBusy(true);
                await _administration.UnlockUserAsync(
                    target.UserId,
                    Login.CurrentUser,
                    SignatureReason(signature),
                    SignatureMeaning(signature),
                    SignatureSigner(signature),
                    target.AuthenticationRowVersion).ConfigureAwait(true);
                LblStatus.Text = $"Account '{target.Username}' unlocked.";
                await ReloadUsersAsync(target.UserId).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("User account unlock failed.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Account unlock"), "User Management", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private User BuildUserFromEditor()
        {
            string role = CboRole.Text?.Trim() ?? string.Empty;
            return new User
            {
                UserId = _selectedUser?.UserId ?? 0,
                Username = TxtUsername.Text.Trim(),
                FullName = TxtFullName.Text.Trim(),
                Role = role,
                Department = TxtDepartment.Text.Trim(),
                Section = TxtSection.Text.Trim(),
                IsActive = ChkActive.IsChecked == true,
                AuthenticationRowVersion = _selectedUser?.AuthenticationRowVersion.ToArray() ?? Array.Empty<byte>(),
                CanAccessWater = ChkAccessWater.IsChecked == true,
                CanAccessEM = ChkAccessEM.IsChecked == true,
                CanRegisterSamples = ChkRegisterSamples.IsChecked == true,
                CanEnterResults = ChkEnterResults.IsChecked == true,
                CanReviewResults = ChkReviewResults.IsChecked == true,
                CanApproveResults = ChkApproveResults.IsChecked == true,
                CanIssueCOA = ChkIssueCOA.IsChecked == true,
                CanCancelCOA = ChkCancelCOA.IsChecked == true,
                CanAccessReports = ChkAccessReports.IsChecked == true,
                CanManageUsers = ChkManageUsers.IsChecked == true,
                CanManageSettings = ChkManageSettings.IsChecked == true
            };
        }

        private void SetPermissions(bool value)
        {
            ChkAccessWater.IsChecked = value;
            ChkAccessEM.IsChecked = value;
            ChkRegisterSamples.IsChecked = value;
            ChkEnterResults.IsChecked = value;
            ChkReviewResults.IsChecked = value;
            ChkApproveResults.IsChecked = value;
            ChkIssueCOA.IsChecked = value;
            ChkCancelCOA.IsChecked = value;
            ChkAccessReports.IsChecked = value;
            ChkManageUsers.IsChecked = value;
            ChkManageSettings.IsChecked = value;
        }

        private void EnsurePasswordsMatch(bool requireValue)
        {
            if (requireValue && string.IsNullOrEmpty(PwdPassword.Password))
                throw new InvalidOperationException("Enter the password and confirmation.");
            if (!string.Equals(PwdPassword.Password, PwdConfirm.Password, StringComparison.Ordinal))
                throw new InvalidOperationException("Password and confirmation do not match.");
        }

        private static string SignatureReason(ElectronicSignature signature) =>
            signature.Reason?.Trim() ?? string.Empty;

        private static string SignatureMeaning(ElectronicSignature signature) =>
            signature.Meaning?.Trim() ?? string.Empty;

        private static string SignatureSigner(ElectronicSignature signature) =>
            signature.SignedBy?.Trim() ?? string.Empty;

        private void SelectRole(string role)
        {
            foreach (object item in CboRole.Items)
            {
                if (item is ComboBoxItem combo &&
                    string.Equals(combo.Content?.ToString(), role, StringComparison.OrdinalIgnoreCase))
                {
                    CboRole.SelectedItem = item;
                    return;
                }
            }
            CboRole.SelectedIndex = -1;
        }

        private void ApplySecurityEditPolicy(User user)
        {
            bool editingSelf = user.Username.Equals(Login.CurrentUser, StringComparison.OrdinalIgnoreCase);
            TxtFullName.IsEnabled = !editingSelf;
            TxtDepartment.IsEnabled = !editingSelf;
            TxtSection.IsEnabled = !editingSelf;
            ChkActive.IsEnabled = !editingSelf;
            CboRole.IsEnabled = !editingSelf;
            ChkAccessWater.IsEnabled = !editingSelf;
            ChkAccessEM.IsEnabled = !editingSelf;
            ChkRegisterSamples.IsEnabled = !editingSelf;
            ChkEnterResults.IsEnabled = !editingSelf;
            ChkReviewResults.IsEnabled = !editingSelf;
            ChkApproveResults.IsEnabled = !editingSelf;
            ChkIssueCOA.IsEnabled = !editingSelf;
            ChkCancelCOA.IsEnabled = !editingSelf;
            ChkAccessReports.IsEnabled = !editingSelf;
            ChkManageUsers.IsEnabled = !editingSelf;
            ChkManageSettings.IsEnabled = !editingSelf;

            if (editingSelf)
                lblAccountState.Text += " Administrative identity, status, Role, and permissions require another authorized User Manager to change.";
        }

        private void ResetSecurityEditorEnabledState()
        {
            TxtFullName.IsEnabled = true;
            TxtDepartment.IsEnabled = true;
            TxtSection.IsEnabled = true;
            ChkActive.IsEnabled = true;
            CboRole.IsEnabled = true;
            ChkAccessWater.IsEnabled = true;
            ChkAccessEM.IsEnabled = true;
            ChkRegisterSamples.IsEnabled = true;
            ChkEnterResults.IsEnabled = true;
            ChkReviewResults.IsEnabled = true;
            ChkApproveResults.IsEnabled = true;
            ChkIssueCOA.IsEnabled = true;
            ChkCancelCOA.IsEnabled = true;
            ChkAccessReports.IsEnabled = true;
            ChkManageUsers.IsEnabled = true;
            ChkManageSettings.IsEnabled = true;
            PwdPassword.IsEnabled = true;
            PwdConfirm.IsEnabled = true;
        }

        private void SetBusy(bool busy)
        {
            bool editingSelf = _selectedUser != null && _selectedUser.Username.Equals(Login.CurrentUser, StringComparison.OrdinalIgnoreCase);
            GridUsers.IsEnabled = !busy;
            BtnNew.IsEnabled = !busy;
            BtnSave.IsEnabled = !busy && !editingSelf;
            BtnRefresh.IsEnabled = !busy;
            BtnResetPassword.IsEnabled = !busy && _selectedUser != null && !editingSelf;
            BtnUnlock.IsEnabled = !busy && _selectedUser != null && !editingSelf && (_selectedUser.IsLocked || _selectedUser.FailedLoginAttempts > 0);
        }
    }
}
