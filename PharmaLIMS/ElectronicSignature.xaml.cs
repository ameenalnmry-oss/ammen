using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS
{
    public partial class ElectronicSignature : Window
    {
        private readonly IAuthService? _authService;

        public bool IsConfirmed { get; private set; }
        public string Meaning { get; private set; } = string.Empty;
        public string Reason { get; private set; } = string.Empty;
        public string SignedBy { get; private set; } = string.Empty;

        private string recordNumber = string.Empty;
        private string userName = string.Empty;
        private string actionType = string.Empty;
        private bool validatePassword = true;
        private int failedPasswordAttempts;
        private bool isConfirming;
        private const int MaximumPasswordAttempts = 3;

        public ElectronicSignature()
        {
            _authService = App.ServiceProvider?.GetService<IAuthService>();
            InitializeComponent();
            InitializeDefaults();
        }

        public ElectronicSignature(IAuthService authService) : this()
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            Configure(string.Empty, string.Empty, "Electronic Signature", true);
        }

        public ElectronicSignature(string sampleCode, string userName, string action) : this()
        {
            Configure(sampleCode, userName, action, true);
        }

        public ElectronicSignature(
            string sampleCode,
            string userName,
            string action,
            bool requirePasswordValidation) : this()
        {
            Configure(sampleCode, userName, action, requirePasswordValidation);
        }

        /// <summary>
        /// Sets the regulated record and action displayed by a DI-created signature window.
        /// Password validation is required by default and should only be disabled for a
        /// documented, non-regulated workflow.
        /// </summary>
        public void Configure(
            string? record,
            string? requestedUserName,
            string? action,
            bool requirePasswordValidation = true)
        {
            if (!requirePasswordValidation && AppConfig.IsProduction)
            {
                throw new InvalidOperationException(
                    "Electronic signatures require current-user password validation in Production.");
            }

            recordNumber = record?.Trim() ?? string.Empty;
            actionType = action?.Trim() ?? string.Empty;
            userName = ResolveDisplayUserName(requestedUserName);
            validatePassword = requirePasswordValidation;

            IsConfirmed = false;
            Meaning = string.Empty;
            Reason = string.Empty;
            SignedBy = string.Empty;
            failedPasswordAttempts = 0;

            LoadData();
        }

        private void InitializeDefaults()
        {
            lblSample.Text = "N/A";
            lblUser.Text = GetSignatureDisplayUserName();
            lblAction.Text = "Electronic Signature";
            lblDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            lblError.Visibility = Visibility.Collapsed;

            LoadDefaultComboItems();
        }

        private void LoadData()
        {
            lblSample.Text = string.IsNullOrWhiteSpace(recordNumber) ? "N/A" : recordNumber;
            lblUser.Text = GetSignatureDisplayUserName();
            lblAction.Text = string.IsNullOrWhiteSpace(actionType) ? "Electronic Signature" : actionType;
            lblDateTime.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            lblError.Visibility = Visibility.Collapsed;

            if (lblPasswordHelp != null)
            {
                string authenticatedUsername = GetAuthenticatedUsername();
                lblPasswordHelp.Text = string.IsNullOrWhiteSpace(authenticatedUsername)
                    ? "Enter the same PharmaLIMS password used to sign in for the account shown above."
                    : $"Enter the same PharmaLIMS password used to sign in for account '{authenticatedUsername}'.";
            }

            if (pnlPassword != null)
                pnlPassword.Visibility = validatePassword ? Visibility.Visible : Visibility.Collapsed;

            LoadDefaultComboItems();

            if (validatePassword && txtPassword != null)
                txtPassword.Focus();
        }

        private string ResolveDisplayUserName(string? requestedUserName)
        {
            string authenticatedDisplayName = GetDisplayUserName();
            if (!authenticatedDisplayName.Equals("Unknown User", StringComparison.OrdinalIgnoreCase))
                return authenticatedDisplayName;

            return string.IsNullOrWhiteSpace(requestedUserName)
                ? "Unknown User"
                : requestedUserName.Trim();
        }

        private string GetSignatureDisplayUserName()
        {
            var user = _authService?.GetCurrentUser();
            string username = user?.Username?.Trim() ?? GetAuthenticatedUsername();
            string fullName = user?.FullName?.Trim() ?? Login.CurrentUserFullName?.Trim() ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(username) &&
                !fullName.Equals(username, StringComparison.OrdinalIgnoreCase))
            {
                return fullName + " (" + username + ")";
            }

            if (!string.IsNullOrWhiteSpace(username))
                return username;

            return string.IsNullOrWhiteSpace(fullName) ? "Unknown User" : fullName;
        }

        private string GetDisplayUserName()
        {
            var user = _authService?.GetCurrentUser();

            if (user != null && !string.IsNullOrWhiteSpace(user.FullName))
                return user.FullName.Trim();

            if (user != null && !string.IsNullOrWhiteSpace(user.Username))
                return user.Username.Trim();

            if (!string.IsNullOrWhiteSpace(Login.CurrentUserFullName))
                return Login.CurrentUserFullName.Trim();

            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            return "Unknown User";
        }

        private string GetAuthenticatedUsername()
        {
            var user = _authService?.GetCurrentUser();

            if (user != null && !string.IsNullOrWhiteSpace(user.Username))
                return user.Username.Trim();

            return string.IsNullOrWhiteSpace(Login.CurrentUser)
                ? string.Empty
                : Login.CurrentUser.Trim();
        }

        private void LoadDefaultComboItems()
        {
            if (cboMeaning.Items.Count == 0)
            {
                cboMeaning.Items.Add("I certify that this action is performed by me");
                cboMeaning.Items.Add("I approve this action");
                cboMeaning.Items.Add("I confirm this action");
                cboMeaning.Items.Add("I have reviewed and verified this data");
            }

            if (cboReason.Items.Count == 0)
            {
                cboReason.Items.Add("Routine data entry");
                cboReason.Items.Add("Result entry");
                cboReason.Items.Add("Correction of data entry error");
                cboReason.Items.Add("Review of data");
                cboReason.Items.Add("Approval of results");
                cboReason.Items.Add("Certificate issuance");
                cboReason.Items.Add("Certificate cancellation");
                cboReason.Items.Add("OOS investigation");
                cboReason.Items.Add("Quality event closure");
                cboReason.Items.Add("EM result entry");
                cboReason.Items.Add("Database maintenance");
                cboReason.Items.Add("User account administration");
            }

            ApplyDefaultSelections();
        }

        private void ApplyDefaultSelections()
        {
            if (IsDatabaseMaintenanceAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I approve this action");
                SelectComboText(cboReason, "Database maintenance");
            }
            else if (IsUserAdministrationAction())
            {
                // User administration changes alter security and release authority. Do not allow
                // a generic preselected reason to satisfy the signature. The manager must type
                // a specific justification for the exact account change being authorized.
                cboReason.IsEditable = true;
                SelectComboText(cboMeaning, "I approve this action");
                cboReason.SelectedIndex = -1;
                cboReason.Text = string.Empty;
            }
            else if (IsQualityEventClosureAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I approve this action");
                SelectComboText(cboReason, "Quality event closure");
            }
            else if (IsApprovalAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I approve this action");
                SelectComboText(cboReason, "Approval of results");
            }
            else if (IsReviewAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I have reviewed and verified this data");
                SelectComboText(cboReason, "Review of data");
            }
            else if (IsCertificateCancellationAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I confirm this action");
                SelectComboText(cboReason, "Certificate cancellation");
            }
            else if (IsCertificateAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I approve this action");
                SelectComboText(cboReason, "Certificate issuance");
            }
            else if (IsEMResultEntryAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I certify that this action is performed by me");
                SelectComboText(cboReason, "EM result entry");
            }
            else if (IsResultEntryAction())
            {
                cboReason.IsEditable = false;
                SelectComboText(cboMeaning, "I certify that this action is performed by me");
                SelectComboText(cboReason, "Result entry");
            }
            else
            {
                cboReason.IsEditable = false;
                if (cboMeaning.SelectedIndex < 0 && cboMeaning.Items.Count > 0)
                    cboMeaning.SelectedIndex = 0;

                if (cboReason.SelectedIndex < 0 && cboReason.Items.Count > 0)
                    cboReason.SelectedIndex = 0;
            }
        }

        private static void SelectComboText(ComboBox combo, string text)
        {
            if (combo == null)
                return;

            foreach (object item in combo.Items)
            {
                string value = item is ComboBoxItem comboBoxItem
                    ? comboBoxItem.Content?.ToString() ?? string.Empty
                    : item?.ToString() ?? string.Empty;

                if (value.Equals(text, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private bool IsResultEntryAction()
        {
            string action = GetNormalizedAction();

            return action.Contains("result entry") ||
                   action.Contains("results entry") ||
                   action.Contains("enter result") ||
                   action.Contains("result");
        }

        private bool IsEMResultEntryAction()
        {
            string action = GetNormalizedAction();

            return action.Contains("em result") ||
                   action.Contains("environmental monitoring");
        }

        private bool IsReviewAction()
        {
            string action = GetNormalizedAction();

            return action.Contains("review") ||
                   action.Contains("submit");
        }

        private bool IsApprovalAction()
        {
            string action = GetNormalizedAction();

            return action.Contains("approve") ||
                   action.Contains("approval");
        }

        private bool IsCertificateCancellationAction()
        {
            string action = GetNormalizedAction();
            return (action.Contains("certificate") || action.Contains("coa")) &&
                   (action.Contains("cancel") || action.Contains("cancellation"));
        }

        private bool IsCertificateAction()
        {
            string action = GetNormalizedAction();

            return action.Contains("certificate") ||
                   action.Contains("coa");
        }

        private bool IsUserAdministrationAction()
        {
            return actionType.Contains("User Account", StringComparison.OrdinalIgnoreCase) ||
                   actionType.Contains("Password Reset", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsDatabaseMaintenanceAction()
        {
            string action = GetNormalizedAction();
            return action.Contains("database maintenance") || action.Contains("schema maintenance");
        }

        private bool IsQualityEventClosureAction()
        {
            string action = GetNormalizedAction();

            return action.Contains("quality event closure") ||
                   action.Contains("event closure") ||
                   action.Contains("closure");
        }

        private string GetNormalizedAction()
        {
            return actionType.Trim().ToLowerInvariant();
        }

        private async Task<bool> CheckPasswordAsync(string password)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            try
            {
                if (_authService?.GetCurrentUser() == null)
                {
                    ApplicationLogger.Warning(
                        "Electronic signature validation was blocked because no authenticated service session was available.");
                    return false;
                }

                return await _authService.ValidateCurrentUserPasswordAsync(password);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Electronic signature password validation failed.", ex);
                return false;
            }
        }

        private static string GetComboText(ComboBox combo)
        {
            if (combo == null)
                return string.Empty;

            if (combo.SelectedItem is ComboBoxItem comboBoxItem)
                return comboBoxItem.Content?.ToString()?.Trim() ?? string.Empty;

            if (combo.SelectedItem != null)
                return combo.SelectedItem.ToString()?.Trim() ?? string.Empty;

            return combo.Text?.Trim() ?? string.Empty;
        }

        private void ShowError(string message)
        {
            lblError.Text = message;
            lblError.Visibility = Visibility.Visible;
        }

        private async void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (isConfirming)
                return;

            isConfirming = true;
            btnConfirm.IsEnabled = false;
            btnCancel.IsEnabled = false;
            lblError.Visibility = Visibility.Collapsed;

            try
            {
                string authenticatedUsername = GetAuthenticatedUsername();
                if (string.IsNullOrWhiteSpace(authenticatedUsername))
                {
                    ShowError("No active logged-in user was found. Please sign in again.");
                    return;
                }

                if (validatePassword)
                {
                    string password = txtPassword.Password;

                    if (string.IsNullOrWhiteSpace(password))
                    {
                        ShowError("Please enter your password.");
                        txtPassword.Focus();
                        return;
                    }

                    bool isValidPassword = await CheckPasswordAsync(password);
                    txtPassword.Clear();

                    if (!isValidPassword)
                    {
                        failedPasswordAttempts++;
                        txtPassword.Focus();

                        if (_authService?.GetCurrentUser() == null)
                        {
                            ShowError("The authenticated session is no longer available. Close this window, sign in again, and retry the action.");
                            return;
                        }

                        if (failedPasswordAttempts >= MaximumPasswordAttempts)
                        {
                            ApplicationLogger.Warning(
                                $"Maximum electronic-signature password attempts exceeded for account '{authenticatedUsername}'.");

                            ShowError("Too many invalid attempts. Close this window and start the action again.");
                            return;
                        }

                        ShowError($"Invalid password for account '{authenticatedUsername}'. Enter the same PharmaLIMS password used to sign in.");
                        return;
                    }
                }

                Meaning = GetComboText(cboMeaning);

                if (string.IsNullOrWhiteSpace(Meaning))
                {
                    ShowError("Please select the meaning of signature.");
                    cboMeaning.Focus();
                    return;
                }

                Reason = GetComboText(cboReason);

                if (string.IsNullOrWhiteSpace(Reason))
                {
                    ShowError("Please enter or select a reason for this action.");
                    cboReason.Focus();
                    return;
                }

                if (Reason.Length < 5)
                {
                    ShowError("Please enter a meaningful reason for this action.");
                    cboReason.Focus();
                    return;
                }

                if (IsUserAdministrationAction() &&
                    (cboReason.SelectedIndex >= 0 || Reason.Length < 10 ||
                     Reason.Equals("User account administration", StringComparison.OrdinalIgnoreCase)))
                {
                    ShowError("Type a specific justification for this exact user-account change; a generic preset reason is not sufficient.");
                    cboReason.Focus();
                    return;
                }

                SignedBy = authenticatedUsername;
                IsConfirmed = true;
                DialogResult = true;
                Close();
            }
            finally
            {
                isConfirming = false;
                btnCancel.IsEnabled = true;

                if (!IsConfirmed && failedPasswordAttempts < MaximumPasswordAttempts)
                    btnConfirm.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (isConfirming)
                return;

            IsConfirmed = false;
            DialogResult = false;
            Close();
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                BtnConfirm_Click(btnConfirm, e);
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                BtnCancel_Click(btnCancel, e);
            }
        }

        private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            lblError.Visibility = Visibility.Collapsed;
        }
    }
}
