using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS
{
    public partial class EMLimitsManagement : Window
    {
        private static readonly string[] SupportedMethods =
        {
            "Settle Plate",
            "Active Air Sampling",
            "Contact Plate",
            "Surface Swab",
            "Personnel Monitoring"
        };

        private readonly string _requestedGrade;
        private readonly string _requestedMethod;
        private bool _isInitializing;
        private int _currentLimitId;

        public bool WasSaved { get; private set; }

        public EMLimitsManagement(string? grade = null, string? method = null)
        {
            InitializeComponent();
            _requestedGrade = grade?.Trim() ?? string.Empty;
            _requestedMethod = method?.Trim() ?? string.Empty;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!CanManageLimits())
                {
                    MessageBox.Show(
                        "Your account does not have permission to manage controlled EM limits. Ask an authorized Settings user to record the approved values.",
                        "Permission Denied",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    DialogResult = false;
                    Close();
                    return;
                }

                EnsureRequiredSchema();
                LoadSelectors();
                LoadCurrentLimit();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open Approved EM Limits management.", ex);
                MessageBox.Show(
                    "Unable to load Approved EM Limits. " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Approved EM Limits",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                DialogResult = false;
                Close();
            }
        }

        private bool CanManageLimits()
        {
            return !string.IsNullOrWhiteSpace(Login.CurrentUser) &&
                   DatabaseHelper.CanManageSettings(Login.CurrentUser);
        }

        private static void EnsureRequiredSchema()
        {
            int ready = Convert.ToInt32(DatabaseHelper.ExecuteScalar(@"
SELECT CASE
         WHEN OBJECT_ID(N'dbo.EM_GradeLimits',N'U') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'Id') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'Grade') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'Method') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'AlertLimitTotal') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'ActionLimitTotal') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'AirVolumeLiters') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'IsActive') IS NOT NULL
          AND OBJECT_ID(N'dbo.EM_GradeLimitSignatures',N'U') IS NOT NULL
         THEN 1 ELSE 0 END;"), CultureInfo.InvariantCulture);

            if (ready != 1)
            {
                throw new InvalidOperationException(
                    "The controlled EM-limit schema is not installed. Apply Database/Migrations/20260819_001_EM_Approved_Limits_Snapshot_Control.sql first.");
            }
        }

        private void LoadSelectors()
        {
            _isInitializing = true;
            try
            {
                cboMethod.Items.Clear();
                foreach (string method in SupportedMethods)
                    cboMethod.Items.Add(method);

                cboGrade.Items.Clear();
                DataTable grades = DatabaseHelper.ExecuteQuery(@"
SELECT DISTINCT LTRIM(RTRIM(Grade)) AS Grade
FROM dbo.EM_Areas
WHERE ISNULL(IsActive,1)=1
  AND NULLIF(LTRIM(RTRIM(Grade)),N'') IS NOT NULL
ORDER BY LTRIM(RTRIM(Grade));");

                foreach (DataRow row in grades.Rows)
                {
                    string grade = Convert.ToString(row["Grade"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(grade))
                        cboGrade.Items.Add(grade);
                }

                if (!string.IsNullOrWhiteSpace(_requestedGrade) &&
                    !cboGrade.Items.Cast<object>().Any(item => string.Equals(item?.ToString(), _requestedGrade, StringComparison.OrdinalIgnoreCase)))
                {
                    cboGrade.Items.Add(_requestedGrade);
                }

                SelectComboText(cboGrade, _requestedGrade);
                if (cboGrade.SelectedIndex < 0 && cboGrade.Items.Count > 0)
                    cboGrade.SelectedIndex = 0;

                SelectComboText(cboMethod, _requestedMethod);
                if (cboMethod.SelectedIndex < 0 && cboMethod.Items.Count > 0)
                    cboMethod.SelectedIndex = 0;
            }
            finally
            {
                _isInitializing = false;
            }

            UpdateMethodFields();
        }

        private static void SelectComboText(ComboBox combo, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested))
                return;

            foreach (object item in combo.Items)
            {
                if (string.Equals(item?.ToString(), requested, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private void SelectionChanged_LoadLimit(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || !IsLoaded)
                return;

            UpdateMethodFields();
            LoadCurrentLimit();
        }

        private void UpdateMethodFields()
        {
            string method = SelectedMethod();
            txtUnit.Text = GetDefaultUnit(method);
            bool activeAir = method.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase);
            txtAirVolume.IsEnabled = activeAir;

            if (!activeAir)
                txtAirVolume.Text = string.Empty;
            else if (string.IsNullOrWhiteSpace(txtAirVolume.Text))
                txtAirVolume.Text = "1000";
        }

        private void LoadCurrentLimit()
        {
            string grade = SelectedGrade();
            string method = SelectedMethod();

            _currentLimitId = 0;
            txtAlertLimit.Text = string.Empty;
            txtActionLimit.Text = string.Empty;
            if (!method.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase))
                txtAirVolume.Text = string.Empty;

            if (string.IsNullOrWhiteSpace(grade) || string.IsNullOrWhiteSpace(method))
            {
                lblCurrentRecord.Text = "Select a grade and monitoring method.";
                return;
            }

            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP (1) Id, Grade, Method, AlertLimitTotal, ActionLimitTotal, AirVolumeLiters
FROM dbo.EM_GradeLimits
WHERE ISNULL(IsActive,1)=1
  AND UPPER(LTRIM(RTRIM(ISNULL(Grade,N''))))=UPPER(LTRIM(RTRIM(@Grade)))
  AND UPPER(LTRIM(RTRIM(ISNULL(Method,N''))))=UPPER(LTRIM(RTRIM(@Method)))
ORDER BY Id DESC;",
                new[]
                {
                    new SqlParameter("@Grade", SqlDbType.NVarChar, 100) { Value = grade },
                    new SqlParameter("@Method", SqlDbType.NVarChar, 100) { Value = method }
                });

            if (table.Rows.Count == 0)
            {
                lblCurrentRecord.Text = "No approved values are recorded for this grade / method. Enter the site-approved limits and save.";
                if (method.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase))
                    txtAirVolume.Text = "1000";
                return;
            }

            DataRow row = table.Rows[0];
            _currentLimitId = Convert.ToInt32(row["Id"], CultureInfo.InvariantCulture);
            txtAlertLimit.Text = FormatDecimal(row["AlertLimitTotal"]);
            txtActionLimit.Text = FormatDecimal(row["ActionLimitTotal"]);

            if (method.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase))
                txtAirVolume.Text = row["AirVolumeLiters"] == DBNull.Value ? "1000" : Convert.ToString(row["AirVolumeLiters"], CultureInfo.InvariantCulture) ?? "1000";

            lblCurrentRecord.Text =
                $"Record #{_currentLimitId} | Grade: {grade} | Method: {method} | " +
                $"Alert: {txtAlertLimit.Text} | Action: {txtActionLimit.Text}";
        }

        private static string FormatDecimal(object value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;

            return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadCurrentLimit();
                lblStatus.Text = "Current approved values reloaded.";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to reload Approved EM Limits.", ex);
                MessageBox.Show("Unable to reload limits: " + Infrastructure.UserFacingError.SafeMessage(ex), "Approved EM Limits", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!CanManageLimits())
            {
                MessageBox.Show("Your Settings permission is no longer active.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string grade = SelectedGrade();
            string method = SelectedMethod();
            if (string.IsNullOrWhiteSpace(grade) || string.IsNullOrWhiteSpace(method))
            {
                MessageBox.Show("Select both Class / Grade and Monitoring Method.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseDecimal(txtAlertLimit.Text, out decimal alert) || alert < 0m)
            {
                MessageBox.Show("Enter a valid non-negative Alert Limit.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParseDecimal(txtActionLimit.Text, out decimal action) || action < 0m)
            {
                MessageBox.Show("Enter a valid non-negative Action Limit.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (action < alert)
            {
                MessageBox.Show("Action Limit cannot be lower than Alert Limit.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int? airVolume = null;
            if (method.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(txtAirVolume.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedVolume) || parsedVolume <= 0)
                {
                    MessageBox.Show("Enter a valid positive Air Volume in liters for Active Air Sampling.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                airVolume = parsedVolume;
            }

            string recordKey = grade + " / " + method;
            var signature = new ElectronicSignature(recordKey, Login.CurrentUser, "EM Approved Limits Configuration", true)
            {
                Owner = this
            };

            if (signature.ShowDialog() != true || !signature.IsConfirmed)
                return;

            BtnSave.IsEnabled = false;
            lblStatus.Text = "Saving controlled EM limits...";

            try
            {
                int savedId = 0;
                string oldValue = string.Empty;
                string newValue = BuildLimitText(grade, method, alert, action, airVolume);

                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection,
                        transaction,
                        signature.SignedBy,
                        "CanManageSettings",
                        "configure approved Environmental Monitoring limits");

                    using var select = new SqlCommand(@"
SELECT TOP (1) Id, AlertLimitTotal, ActionLimitTotal, AirVolumeLiters
FROM dbo.EM_GradeLimits WITH (UPDLOCK,HOLDLOCK)
WHERE ISNULL(IsActive,1)=1
  AND UPPER(LTRIM(RTRIM(ISNULL(Grade,N''))))=UPPER(LTRIM(RTRIM(@Grade)))
  AND UPPER(LTRIM(RTRIM(ISNULL(Method,N''))))=UPPER(LTRIM(RTRIM(@Method)))
ORDER BY Id DESC;", connection, transaction);
                    select.Parameters.Add("@Grade", SqlDbType.NVarChar, 100).Value = grade;
                    select.Parameters.Add("@Method", SqlDbType.NVarChar, 100).Value = method;

                    int existingId = 0;
                    decimal? oldAlert = null;
                    decimal? oldAction = null;
                    int? oldAirVolume = null;

                    using (SqlDataReader reader = select.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            existingId = reader.GetInt32(reader.GetOrdinal("Id"));
                            oldAlert = reader.IsDBNull(reader.GetOrdinal("AlertLimitTotal")) ? null : reader.GetDecimal(reader.GetOrdinal("AlertLimitTotal"));
                            oldAction = reader.IsDBNull(reader.GetOrdinal("ActionLimitTotal")) ? null : reader.GetDecimal(reader.GetOrdinal("ActionLimitTotal"));
                            oldAirVolume = reader.IsDBNull(reader.GetOrdinal("AirVolumeLiters")) ? null : reader.GetInt32(reader.GetOrdinal("AirVolumeLiters"));
                        }
                    }

                    oldValue = existingId > 0
                        ? BuildLimitText(grade, method, oldAlert, oldAction, oldAirVolume)
                        : "No configured record";

                    if (existingId > 0)
                    {
                        using var update = new SqlCommand(@"
UPDATE dbo.EM_GradeLimits
SET Grade=@Grade,
    Method=@Method,
    AlertLimitTotal=@Alert,
    ActionLimitTotal=@Action,
    AirVolumeLiters=@AirVolume,
    IsActive=1,
    LastModifiedBy=@User,
    LastModifiedAt=SYSUTCDATETIME()
WHERE Id=@Id;", connection, transaction);
                        update.Parameters.Add("@Grade", SqlDbType.NVarChar, 100).Value = grade;
                        update.Parameters.Add("@Method", SqlDbType.NVarChar, 100).Value = method;
                        update.Parameters.Add("@Alert", SqlDbType.Decimal).Value = alert;
                        update.Parameters["@Alert"].Precision = 18;
                        update.Parameters["@Alert"].Scale = 3;
                        update.Parameters.Add("@Action", SqlDbType.Decimal).Value = action;
                        update.Parameters["@Action"].Precision = 18;
                        update.Parameters["@Action"].Scale = 3;
                        update.Parameters.Add("@AirVolume", SqlDbType.Int).Value = airVolume.HasValue ? airVolume.Value : DBNull.Value;
                        update.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = Login.CurrentUser;
                        update.Parameters.Add("@Id", SqlDbType.Int).Value = existingId;
                        if (update.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("The selected EM limit record changed while it was being saved.");
                        savedId = existingId;
                    }
                    else
                    {
                        using var insert = new SqlCommand(@"
INSERT dbo.EM_GradeLimits
    (Grade,Method,AlertLimitTotal,ActionLimitTotal,AirVolumeLiters,IsActive,LastModifiedBy,LastModifiedAt)
VALUES
    (@Grade,@Method,@Alert,@Action,@AirVolume,1,@User,SYSUTCDATETIME());
SELECT CONVERT(int,SCOPE_IDENTITY());", connection, transaction);
                        insert.Parameters.Add("@Grade", SqlDbType.NVarChar, 100).Value = grade;
                        insert.Parameters.Add("@Method", SqlDbType.NVarChar, 100).Value = method;
                        insert.Parameters.Add("@Alert", SqlDbType.Decimal).Value = alert;
                        insert.Parameters["@Alert"].Precision = 18;
                        insert.Parameters["@Alert"].Scale = 3;
                        insert.Parameters.Add("@Action", SqlDbType.Decimal).Value = action;
                        insert.Parameters["@Action"].Precision = 18;
                        insert.Parameters["@Action"].Scale = 3;
                        insert.Parameters.Add("@AirVolume", SqlDbType.Int).Value = airVolume.HasValue ? airVolume.Value : DBNull.Value;
                        insert.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = Login.CurrentUser;
                        savedId = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }

                    using (var sign = new SqlCommand(@"
INSERT dbo.EM_GradeLimitSignatures
    (GradeLimitID,ActionType,MeaningOfSignature,Reason,SignedBy,UserRole,SignedAt,SourceWorkstation)
VALUES
    (@Id,N'Configure Approved EM Limits',@Meaning,@Reason,@SignedBy,@Role,SYSUTCDATETIME(),@Workstation);", connection, transaction))
                    {
                        sign.Parameters.Add("@Id", SqlDbType.Int).Value = savedId;
                        sign.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning;
                        sign.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(signature.Reason) ? DBNull.Value : signature.Reason;
                        sign.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        sign.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = string.IsNullOrWhiteSpace(signerRole) ? DBNull.Value : signerRole;
                        sign.Parameters.Add("@Workstation", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
                        sign.ExecuteNonQuery();
                    }

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "EM_GradeLimits",
                        savedId,
                        existingId > 0 ? "EM Approved Limit Update" : "EM Approved Limit Create",
                        oldValue,
                        newValue,
                        signature.Reason + " | Meaning: " + signature.Meaning,
                        signature.SignedBy,
                        "AlertLimitTotal/ActionLimitTotal",
                        method,
                        grade,
                        "Environmental Monitoring");
                });

                _currentLimitId = savedId;
                WasSaved = true;
                lblStatus.Text = "Approved EM limits recorded successfully.";
                MessageBox.Show(
                    "Approved EM limits were recorded with electronic-signature and audit evidence. They apply to future EM plates only. Historical plates with incomplete frozen evidence require the separate signed Historical Snapshot Reconciliation workflow.",
                    "Approved EM Limits",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to save Approved EM Limits.", ex);
                lblStatus.Text = "Save failed. No controlled limit change was completed.";
                MessageBox.Show("Unable to save approved limits: " + Infrastructure.UserFacingError.SafeMessage(ex), "Approved EM Limits", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnSave.IsEnabled = true;
            }
        }

        private static string BuildLimitText(string grade, string method, decimal? alert, decimal? action, int? airVolume)
        {
            return $"Grade={grade}; Method={method}; Alert={FormatNullable(alert)}; Action={FormatNullable(action)}; AirVolumeL={(airVolume.HasValue ? airVolume.Value.ToString(CultureInfo.InvariantCulture) : "N/A")}";
        }

        private static string FormatNullable(decimal? value) =>
            value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "Not defined";

        private static bool TryParseDecimal(string text, out decimal value)
        {
            string clean = text?.Trim() ?? string.Empty;
            return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
                   decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        }

        private string SelectedGrade() => cboGrade.SelectedItem?.ToString()?.Trim() ?? string.Empty;
        private string SelectedMethod() => cboMethod.SelectedItem?.ToString()?.Trim() ?? string.Empty;

        private static string GetDefaultUnit(string method)
        {
            if (method.Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase)) return "CFU/m3";
            if (method.Equals("Surface Swab", StringComparison.OrdinalIgnoreCase)) return "CFU/swab";
            if (method.Equals("Personnel Monitoring", StringComparison.OrdinalIgnoreCase)) return "CFU/glove";
            if (method.Equals("Settle Plate", StringComparison.OrdinalIgnoreCase) || method.Equals("Contact Plate", StringComparison.OrdinalIgnoreCase)) return "CFU/plate";
            return "CFU";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
