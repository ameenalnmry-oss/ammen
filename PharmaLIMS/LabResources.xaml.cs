using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS
{
    public partial class LabResources : Window
    {
        private readonly DatabaseConnection _database = new();
        private readonly List<EquipmentRow> _allEquipment = new();
        private int? _selectedEquipmentId;
        private byte[]? _selectedRowVersion;

        public LabResources()
        {
            InitializeComponent();
            Loaded += LabResources_Loaded;
        }

        private void LabResources_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDefaults();
            LoadEquipment();
        }

        private void ApplyDefaults()
        {
            if (CboStatus.SelectedIndex < 0) CboStatus.SelectedIndex = 0;
            if (CboQualification.SelectedIndex < 0) CboQualification.SelectedIndex = 3;
            if (CboCalibration.SelectedIndex < 0) CboCalibration.SelectedIndex = 3;
            ChkCalibrationRequired.IsChecked = true;
            ChkGmpCritical.IsChecked = true;
        }

        private void LoadEquipment()
        {
            try
            {
                DataTable table = _database.ExecuteQuery(@"
SELECT
    e.EquipmentID,
    e.EquipmentCode,
    e.EquipmentName,
    e.EquipmentType,
    e.Department,
    e.Location,
    e.Manufacturer,
    e.Model,
    e.SerialNumber,
    e.GmpCritical,
    e.EquipmentStatus,
    e.QualificationStatus,
    e.LastQualificationDate,
    e.NextQualificationDate,
    e.CalibrationRequired,
    e.CalibrationStatus,
    e.LastCalibrationDate,
    e.NextCalibrationDate,
    e.MethodReference,
    e.Notes,
    e.IsActive,
    e.RowVersion,
    ISNULL(uses.ControlledUses,N'No controlled operational use mapped.') AS ControlledUses,
    CASE
        WHEN e.QualificationStatus = N'Not Required' THEN N'Not Required'
        WHEN e.NextQualificationDate IS NOT NULL AND e.NextQualificationDate < CAST(SYSDATETIME() AS date) THEN N'Expired'
        WHEN e.NextQualificationDate IS NOT NULL
             AND e.NextQualificationDate <= DATEADD(day,30,CAST(SYSDATETIME() AS date))
             AND e.QualificationStatus = N'Qualified' THEN N'Due Soon'
        ELSE e.QualificationStatus
    END AS EffectiveQualificationStatus,
    CASE
        WHEN e.CalibrationRequired = 0 OR e.CalibrationStatus = N'Not Required' THEN N'Not Required'
        WHEN e.NextCalibrationDate IS NOT NULL AND e.NextCalibrationDate < CAST(SYSDATETIME() AS date) THEN N'Expired'
        WHEN e.NextCalibrationDate IS NOT NULL
             AND e.NextCalibrationDate <= DATEADD(day,30,CAST(SYSDATETIME() AS date))
             AND e.CalibrationStatus = N'Calibrated' THEN N'Due Soon'
        ELSE e.CalibrationStatus
    END AS EffectiveCalibrationStatus,
    CASE
        WHEN e.IsActive = 0 OR e.EquipmentStatus <> N'Active' THEN N'BLOCKED'
        WHEN e.QualificationStatus NOT IN (N'Qualified',N'Due Soon',N'Not Required') THEN N'BLOCKED'
        WHEN e.NextQualificationDate IS NOT NULL AND e.NextQualificationDate < CAST(SYSDATETIME() AS date) THEN N'BLOCKED'
        WHEN e.CalibrationRequired = 1
             AND e.CalibrationStatus NOT IN (N'Calibrated',N'Due Soon',N'Not Required') THEN N'BLOCKED'
        WHEN e.CalibrationRequired = 1
             AND e.NextCalibrationDate IS NOT NULL
             AND e.NextCalibrationDate < CAST(SYSDATETIME() AS date) THEN N'BLOCKED'
        ELSE N'AVAILABLE'
    END AS UseReadiness
FROM dbo.LabEquipment e
OUTER APPLY
(
    SELECT STRING_AGG(
               CONVERT(nvarchar(max),u.UseCategory + N': ' + u.UseDescription),
               NCHAR(10)
           ) WITHIN GROUP (ORDER BY u.SortOrder,u.OperationalUseID) AS ControlledUses
    FROM dbo.LabEquipmentOperationalUses u
    WHERE u.EquipmentID=e.EquipmentID
      AND u.IsActive=1
) uses
ORDER BY CASE WHEN e.IsActive=1 THEN 0 ELSE 1 END, e.EquipmentCode;");

                _allEquipment.Clear();
                foreach (DataRow row in table.Rows)
                    _allEquipment.Add(EquipmentRow.From(row));

                ApplySearchFilter();
                UpdateSummary();
                LblStatus.Text = $"Loaded {_allEquipment.Count} equipment record(s).";
            }
            catch (Exception ex)
            {
                LblStatus.Text = "Equipment master is unavailable.";
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Laboratory Resources"),
                    "Laboratory Resources",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void UpdateSummary()
        {
            int available = 0;
            int blocked = 0;
            int dueSoon = 0;

            foreach (EquipmentRow row in _allEquipment)
            {
                if (row.UseReadiness == "AVAILABLE") available++;
                else blocked++;

                if (row.EffectiveCalibrationStatus == "Due Soon" ||
                    row.EffectiveQualificationStatus == "Due Soon")
                    dueSoon++;
            }

            LblSummary.Text = $"Available {available} | Blocked {blocked} | Due soon {dueSoon}";
        }

        private void ApplySearchFilter()
        {
            string term = TxtSearch.Text?.Trim() ?? string.Empty;
            if (term.Length == 0)
            {
                GridEquipment.ItemsSource = null;
                GridEquipment.ItemsSource = _allEquipment;
                return;
            }

            var filtered = _allEquipment.FindAll(row =>
                Contains(row.EquipmentCode, term) ||
                Contains(row.EquipmentName, term) ||
                Contains(row.EquipmentType, term) ||
                Contains(row.SerialNumber, term) ||
                Contains(row.Location, term) ||
                Contains(row.EquipmentStatus, term) ||
                Contains(row.UseReadiness, term) ||
                Contains(row.ControlledUses, term));

            GridEquipment.ItemsSource = null;
            GridEquipment.ItemsSource = filtered;
        }

        private static bool Contains(string? value, string term) =>
            !string.IsNullOrWhiteSpace(value) &&
            value.Contains(term, StringComparison.OrdinalIgnoreCase);

        private void GridEquipment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridEquipment.SelectedItem is not EquipmentRow row)
                return;

            _selectedEquipmentId = row.EquipmentID;
            LoadEquipmentActivityHistory(row.EquipmentID);
            _selectedRowVersion = row.RowVersion;
            LblMode.Text = $"Editing {row.EquipmentCode}";

            TxtCode.Text = row.EquipmentCode;
            TxtName.Text = row.EquipmentName;
            TxtType.Text = row.EquipmentType;
            TxtDepartment.Text = row.Department;
            TxtLocation.Text = row.Location;
            TxtManufacturer.Text = row.Manufacturer;
            TxtModel.Text = row.Model;
            TxtSerial.Text = row.SerialNumber;
            ChkGmpCritical.IsChecked = row.GmpCritical;
            SelectCombo(CboStatus, row.EquipmentStatus);
            SelectCombo(CboQualification, row.QualificationStatus);
            DpLastQualification.SelectedDate = row.LastQualificationDate;
            DpNextQualification.SelectedDate = row.NextQualificationDate;
            ChkCalibrationRequired.IsChecked = row.CalibrationRequired;
            SelectCombo(CboCalibration, row.CalibrationStatus);
            DpLastCalibration.SelectedDate = row.LastCalibrationDate;
            DpNextCalibration.SelectedDate = row.NextCalibrationDate;
            TxtMethodReference.Text = row.MethodReference;
            TxtControlledUses.Text = row.ControlledUses;
            TxtNotes.Text = row.Notes;
        }

        private void LoadEquipmentActivityHistory(int equipmentId)
        {
            GridActivityHistory.ItemsSource = null;
            try
            {
                DataTable exists = _database.ExecuteQuery("SELECT OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') AS TableId;");
                if (exists.Rows.Count == 0 || exists.Rows[0]["TableId"] == DBNull.Value)
                {
                    LblActivityHistory.Text = "Activity ledger migration is not yet applied. Equipment master remains available.";
                    return;
                }
                DataTable table = _database.ExecuteQuery(@"
SELECT TOP (100) ActivityID,ActivityType,ActivityStatus,PerformedBy,CreatedAt
FROM dbo.MicroEquipmentActivities
WHERE EquipmentID=@EquipmentID
ORDER BY ActivityID DESC;",
                    new[] { new SqlParameter("@EquipmentID", SqlDbType.Int) { Value = equipmentId } });
                GridActivityHistory.ItemsSource = table.DefaultView;
                LblActivityHistory.Text = table.Rows.Count == 0
                    ? "No confirmed or draft activity records for this equipment."
                    : $"Showing {table.Rows.Count} most recent activity records (read-only).";
            }
            catch (Exception ex)
            {
                LblActivityHistory.Text = "Activity history could not be loaded: " +
                    UserFacingError.SafeMessage(ex, "Equipment activity history");
            }
        }

        private void BtnCreateActivityDraft_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectedEquipmentId.HasValue)
            {
                MessageBox.Show("Select and save an equipment record first.", "Activity draft");
                return;
            }
            string type = (CboActivityType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            string method = TxtActivityMethod.Text.Trim();
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(method))
            {
                MessageBox.Show("Choose activity type and provide an approved method / SOP reference.", "Activity draft");
                return;
            }
            try
            {
                long activityId = 0;
                _database.ExecuteInTransaction((connection, transaction) =>
                {
                    activityId = Services.MicroEquipmentActivityService.CreateDraft(connection, transaction,
                        _selectedEquipmentId.Value, type, Login.CurrentUser, method);
                });
                LoadEquipmentActivityHistory(_selectedEquipmentId.Value);
                LblStatus.Text = $"Activity draft {activityId} created. No actual use or approval has been recorded.";
                TxtActivityMethod.Clear();
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Create equipment activity draft"),
                    "Activity draft", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnLinkActivity_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(TxtActivityId.Text.Trim(), out long activityId) || activityId <= 0 ||
                !int.TryParse(TxtSourceParentId.Text.Trim(), out int parentId) || parentId <= 0 ||
                !int.TryParse(TxtSourceResultId.Text.Trim(), out int resultId) || resultId <= 0)
            {
                MessageBox.Show("Enter valid positive Activity, Sample/Event and Test/Plate IDs.", "Activity Link");
                return;
            }
            string module = (CboSourceModule.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (module is not ("PRM" or "EM" or "WATER"))
            {
                MessageBox.Show("Select PRM, EM, or WATER.", "Activity Link");
                return;
            }
            try
            {
                _database.ExecuteInTransaction((connection, transaction) =>
                    Services.MicroEquipmentActivityService.AddLink(connection, transaction,
                        activityId, module, parentId, resultId, Login.CurrentUser));
                if (_selectedEquipmentId.HasValue) LoadEquipmentActivityHistory(_selectedEquipmentId.Value);
                LblStatus.Text = $"Activity {activityId} linked to verified {module} test {resultId}. Actual use is not yet confirmed.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Link equipment activity"), "Activity Link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e) => ClearForm();

        private void ClearForm()
        {
            _selectedEquipmentId = null;
            _selectedRowVersion = null;
            GridEquipment.SelectedItem = null;
            LblMode.Text = "New record";
            TxtCode.Clear();
            TxtName.Clear();
            TxtType.Clear();
            TxtDepartment.Clear();
            TxtLocation.Clear();
            TxtManufacturer.Clear();
            TxtModel.Clear();
            TxtSerial.Clear();
            DpLastQualification.SelectedDate = null;
            DpNextQualification.SelectedDate = null;
            DpLastCalibration.SelectedDate = null;
            DpNextCalibration.SelectedDate = null;
            TxtMethodReference.Clear();
            TxtControlledUses.Text = "Controlled operational uses are assigned by the approved equipment-use matrix.";
            TxtNotes.Clear();
            CboStatus.SelectedIndex = 0;
            CboQualification.SelectedIndex = 3;
            CboCalibration.SelectedIndex = 3;
            ChkCalibrationRequired.IsChecked = true;
            ChkGmpCritical.IsChecked = true;
            TxtCode.Focus();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadEquipment();
        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedEquipmentId.HasValue)
            {
                int id = _selectedEquipmentId.Value;
                LoadEquipment();
                foreach (EquipmentRow row in _allEquipment)
                    if (row.EquipmentID == id)
                    {
                        GridEquipment.SelectedItem = row;
                        GridEquipment.ScrollIntoView(row);
                        break;
                    }
            }
            else
            {
                LoadEquipment();
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (GridEquipment != null)
                ApplySearchFilter();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!(Login.CanManageSettings ||
                  string.Equals(Login.CurrentUserRole, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    "Equipment master changes require system-settings permission.",
                    "Permission Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!ValidateForm(out string validationMessage))
            {
                MessageBox.Show(validationMessage, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string recordCode = TxtCode.Text.Trim();
            string action = _selectedEquipmentId.HasValue
                ? "Laboratory Equipment Master Update"
                : "Laboratory Equipment Master Create";

            var signature = new ElectronicSignature(recordCode, Login.CurrentUser, action, true)
            {
                Owner = this
            };
            signature.ShowDialog();
            if (!signature.IsConfirmed)
                return;

            try
            {
                SaveEquipment(signature);
                LoadEquipment();
                ClearForm();
                LblStatus.Text = "Equipment record saved with electronic signature and audit evidence.";
            }
            catch (SqlException ex) when (ex.Number == 2601 || ex.Number == 2627)
            {
                MessageBox.Show(
                    "Equipment Code must be unique. Another record already uses this code.",
                    "Duplicate Equipment Code",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Save laboratory equipment"),
                    "Laboratory Resources",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool ValidateForm(out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(TxtCode.Text) ||
                string.IsNullOrWhiteSpace(TxtName.Text) ||
                string.IsNullOrWhiteSpace(TxtType.Text))
            {
                message = "Equipment Code, Equipment Name, and Equipment Type are required.";
                return false;
            }

            if (DpLastQualification.SelectedDate.HasValue &&
                DpNextQualification.SelectedDate.HasValue &&
                DpNextQualification.SelectedDate.Value.Date < DpLastQualification.SelectedDate.Value.Date)
            {
                message = "Next Qualification date cannot be earlier than Last Qualification date.";
                return false;
            }

            if (DpLastCalibration.SelectedDate.HasValue &&
                DpNextCalibration.SelectedDate.HasValue &&
                DpNextCalibration.SelectedDate.Value.Date < DpLastCalibration.SelectedDate.Value.Date)
            {
                message = "Next Calibration date cannot be earlier than Last Calibration date.";
                return false;
            }

            if (ChkCalibrationRequired.IsChecked == true &&
                ComboText(CboCalibration) == "Not Required")
            {
                message = "Calibration Required cannot be selected while Calibration Status is Not Required.";
                return false;
            }

            return true;
        }

        private void SaveEquipment(ElectronicSignature signature)
        {
            _database.ExecuteInTransaction((connection, transaction) =>
            {
                string signedBy = signature.SignedBy.Trim();
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection,
                    transaction,
                    signedBy,
                    "CanManageSettings",
                    "maintain laboratory equipment master data");

                string oldJson = string.Empty;
                int equipmentId;

                if (_selectedEquipmentId.HasValue)
                {
                    if (_selectedRowVersion == null)
                        throw new InvalidOperationException("The selected equipment version is unavailable. Reload the record.");

                    using (var oldCommand = new SqlCommand(@"
SELECT EquipmentCode,EquipmentName,EquipmentType,Department,Location,Manufacturer,Model,SerialNumber,
       GmpCritical,EquipmentStatus,QualificationStatus,LastQualificationDate,NextQualificationDate,
       CalibrationRequired,CalibrationStatus,LastCalibrationDate,NextCalibrationDate,MethodReference,Notes,IsActive
FROM dbo.LabEquipment WITH (UPDLOCK,HOLDLOCK)
WHERE EquipmentID=@EquipmentID AND RowVersion=@ExpectedRowVersion;", connection, transaction))
                    {
                        oldCommand.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = _selectedEquipmentId.Value;
                        oldCommand.Parameters.Add("@ExpectedRowVersion", SqlDbType.Timestamp, 8).Value = _selectedRowVersion;
                        using var reader = oldCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("This equipment record was changed by another session. Reload before saving.");
                        oldJson = SnapshotFromReader(reader);
                    }

                    using var update = new SqlCommand(@"
UPDATE dbo.LabEquipment
SET EquipmentCode=@Code,
    EquipmentName=@Name,
    EquipmentType=@Type,
    Department=@Department,
    Location=@Location,
    Manufacturer=@Manufacturer,
    Model=@Model,
    SerialNumber=@Serial,
    GmpCritical=@GmpCritical,
    EquipmentStatus=@EquipmentStatus,
    QualificationStatus=@QualificationStatus,
    LastQualificationDate=@LastQualificationDate,
    NextQualificationDate=@NextQualificationDate,
    CalibrationRequired=@CalibrationRequired,
    CalibrationStatus=@CalibrationStatus,
    LastCalibrationDate=@LastCalibrationDate,
    NextCalibrationDate=@NextCalibrationDate,
    MethodReference=@MethodReference,
    Notes=@Notes,
    IsActive=CASE WHEN @EquipmentStatus=N'Retired' THEN 0 ELSE 1 END,
    UpdatedBy=@UpdatedBy,
    UpdatedAt=SYSUTCDATETIME()
WHERE EquipmentID=@EquipmentID AND RowVersion=@ExpectedRowVersion;", connection, transaction);
                    AddParameters(update, signedBy);
                    update.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = _selectedEquipmentId.Value;
                    update.Parameters.Add("@ExpectedRowVersion", SqlDbType.Timestamp, 8).Value = _selectedRowVersion;
                    if (update.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("This equipment record changed before save. Reload and try again.");
                    equipmentId = _selectedEquipmentId.Value;
                }
                else
                {
                    using var insert = new SqlCommand(@"
INSERT dbo.LabEquipment
(EquipmentCode,EquipmentName,EquipmentType,Department,Location,Manufacturer,Model,SerialNumber,
 GmpCritical,EquipmentStatus,QualificationStatus,LastQualificationDate,NextQualificationDate,
 CalibrationRequired,CalibrationStatus,LastCalibrationDate,NextCalibrationDate,MethodReference,Notes,
 IsActive,CreatedBy)
OUTPUT INSERTED.EquipmentID
VALUES
(@Code,@Name,@Type,@Department,@Location,@Manufacturer,@Model,@Serial,
 @GmpCritical,@EquipmentStatus,@QualificationStatus,@LastQualificationDate,@NextQualificationDate,
 @CalibrationRequired,@CalibrationStatus,@LastCalibrationDate,@NextCalibrationDate,@MethodReference,@Notes,
 CASE WHEN @EquipmentStatus=N'Retired' THEN 0 ELSE 1 END,@UpdatedBy);", connection, transaction);
                    AddParameters(insert, signedBy);
                    equipmentId = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                string newJson;
                using (var state = new SqlCommand(@"
SELECT EquipmentCode,EquipmentName,EquipmentType,Department,Location,Manufacturer,Model,SerialNumber,
       GmpCritical,EquipmentStatus,QualificationStatus,LastQualificationDate,NextQualificationDate,
       CalibrationRequired,CalibrationStatus,LastCalibrationDate,NextCalibrationDate,MethodReference,Notes,IsActive
FROM dbo.LabEquipment
WHERE EquipmentID=@EquipmentID;", connection, transaction))
                {
                    state.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId;
                    using var reader = state.ExecuteReader();
                    if (!reader.Read())
                        throw new InvalidOperationException("Equipment state could not be reloaded after save.");
                    newJson = SnapshotFromReader(reader);
                }

                using (var evidence = new SqlCommand(@"
INSERT dbo.LabEquipmentSignatures
(EquipmentID,ActionType,MeaningOfSignature,ActionReason,OldStateJson,NewStateJson,SignedBy,UserRole,SignedAt,SourceWorkstation)
VALUES
(@EquipmentID,@ActionType,@Meaning,@Reason,@OldState,@NewState,@SignedBy,@UserRole,SYSUTCDATETIME(),@Workstation);", connection, transaction))
                {
                    evidence.Parameters.Add("@EquipmentID", SqlDbType.Int).Value = equipmentId;
                    evidence.Parameters.Add("@ActionType", SqlDbType.NVarChar, 50).Value =
                        _selectedEquipmentId.HasValue ? "Update" : "Create";
                    evidence.Parameters.Add("@Meaning", SqlDbType.NVarChar, 250).Value = signature.Meaning;
                    evidence.Parameters.Add("@Reason", SqlDbType.NVarChar, 500).Value = signature.Reason;
                    evidence.Parameters.Add("@OldState", SqlDbType.NVarChar, -1).Value =
                        string.IsNullOrWhiteSpace(oldJson) ? DBNull.Value : oldJson;
                    evidence.Parameters.Add("@NewState", SqlDbType.NVarChar, -1).Value = newJson;
                    evidence.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signedBy;
                    evidence.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value =
                        string.IsNullOrWhiteSpace(Login.CurrentUserRole) ? DBNull.Value : Login.CurrentUserRole;
                    evidence.Parameters.Add("@Workstation", SqlDbType.NVarChar, 100).Value = Environment.MachineName;
                    evidence.ExecuteNonQuery();
                }

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "LabEquipment",
                    equipmentId,
                    _selectedEquipmentId.HasValue ? "Laboratory Equipment Updated" : "Laboratory Equipment Created",
                    oldJson,
                    newJson,
                    signature.Reason,
                    signedBy);
            });
        }

        private void AddParameters(SqlCommand command, string signedBy)
        {
            command.Parameters.Add("@Code", SqlDbType.NVarChar, 50).Value = TxtCode.Text.Trim();
            command.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = TxtName.Text.Trim();
            command.Parameters.Add("@Type", SqlDbType.NVarChar, 100).Value = TxtType.Text.Trim();
            command.Parameters.Add("@Department", SqlDbType.NVarChar, 100).Value = Db(TxtDepartment.Text);
            command.Parameters.Add("@Location", SqlDbType.NVarChar, 200).Value = Db(TxtLocation.Text);
            command.Parameters.Add("@Manufacturer", SqlDbType.NVarChar, 150).Value = Db(TxtManufacturer.Text);
            command.Parameters.Add("@Model", SqlDbType.NVarChar, 100).Value = Db(TxtModel.Text);
            command.Parameters.Add("@Serial", SqlDbType.NVarChar, 100).Value = Db(TxtSerial.Text);
            command.Parameters.Add("@GmpCritical", SqlDbType.Bit).Value = ChkGmpCritical.IsChecked == true;
            command.Parameters.Add("@EquipmentStatus", SqlDbType.NVarChar, 30).Value = ComboText(CboStatus);
            command.Parameters.Add("@QualificationStatus", SqlDbType.NVarChar, 30).Value = ComboText(CboQualification);
            command.Parameters.Add("@LastQualificationDate", SqlDbType.Date).Value =
                DpLastQualification.SelectedDate.HasValue ? DpLastQualification.SelectedDate.Value.Date : DBNull.Value;
            command.Parameters.Add("@NextQualificationDate", SqlDbType.Date).Value =
                DpNextQualification.SelectedDate.HasValue ? DpNextQualification.SelectedDate.Value.Date : DBNull.Value;
            command.Parameters.Add("@CalibrationRequired", SqlDbType.Bit).Value = ChkCalibrationRequired.IsChecked == true;
            command.Parameters.Add("@CalibrationStatus", SqlDbType.NVarChar, 30).Value = ComboText(CboCalibration);
            command.Parameters.Add("@LastCalibrationDate", SqlDbType.Date).Value =
                DpLastCalibration.SelectedDate.HasValue ? DpLastCalibration.SelectedDate.Value.Date : DBNull.Value;
            command.Parameters.Add("@NextCalibrationDate", SqlDbType.Date).Value =
                DpNextCalibration.SelectedDate.HasValue ? DpNextCalibration.SelectedDate.Value.Date : DBNull.Value;
            command.Parameters.Add("@MethodReference", SqlDbType.NVarChar, 200).Value = Db(TxtMethodReference.Text);
            command.Parameters.Add("@Notes", SqlDbType.NVarChar, 500).Value = Db(TxtNotes.Text);
            command.Parameters.Add("@UpdatedBy", SqlDbType.NVarChar, 100).Value = signedBy;
        }

        private static object Db(string? value) =>
            string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        private static string ComboText(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item)
                return Convert.ToString(item.Content, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            return combo.Text?.Trim() ?? string.Empty;
        }

        private static void SelectCombo(ComboBox combo, string value)
        {
            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem comboItem &&
                    string.Equals(Convert.ToString(comboItem.Content), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = comboItem;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private static string SnapshotFromReader(SqlDataReader reader)
        {
            var state = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object value = reader.GetValue(i);
                state[reader.GetName(i)] = value == DBNull.Value ? null : value;
            }
            return JsonSerializer.Serialize(state);
        }

        private sealed class EquipmentRow
        {
            public int EquipmentID { get; init; }
            public string EquipmentCode { get; init; } = string.Empty;
            public string EquipmentName { get; init; } = string.Empty;
            public string EquipmentType { get; init; } = string.Empty;
            public string Department { get; init; } = string.Empty;
            public string Location { get; init; } = string.Empty;
            public string Manufacturer { get; init; } = string.Empty;
            public string Model { get; init; } = string.Empty;
            public string SerialNumber { get; init; } = string.Empty;
            public bool GmpCritical { get; init; }
            public string EquipmentStatus { get; init; } = string.Empty;
            public string QualificationStatus { get; init; } = string.Empty;
            public DateTime? LastQualificationDate { get; init; }
            public DateTime? NextQualificationDate { get; init; }
            public bool CalibrationRequired { get; init; }
            public string CalibrationStatus { get; init; } = string.Empty;
            public DateTime? LastCalibrationDate { get; init; }
            public DateTime? NextCalibrationDate { get; init; }
            public string MethodReference { get; init; } = string.Empty;
            public string Notes { get; init; } = string.Empty;
            public string ControlledUses { get; init; } = string.Empty;
            public string EffectiveQualificationStatus { get; init; } = string.Empty;
            public string EffectiveCalibrationStatus { get; init; } = string.Empty;
            public string UseReadiness { get; init; } = string.Empty;
            public byte[] RowVersion { get; init; } = Array.Empty<byte>();

            public static EquipmentRow From(DataRow row) => new()
            {
                EquipmentID = Convert.ToInt32(row["EquipmentID"], CultureInfo.InvariantCulture),
                EquipmentCode = Text(row, "EquipmentCode"),
                EquipmentName = Text(row, "EquipmentName"),
                EquipmentType = Text(row, "EquipmentType"),
                Department = Text(row, "Department"),
                Location = Text(row, "Location"),
                Manufacturer = Text(row, "Manufacturer"),
                Model = Text(row, "Model"),
                SerialNumber = Text(row, "SerialNumber"),
                GmpCritical = Convert.ToBoolean(row["GmpCritical"], CultureInfo.InvariantCulture),
                EquipmentStatus = Text(row, "EquipmentStatus"),
                QualificationStatus = Text(row, "QualificationStatus"),
                LastQualificationDate = Date(row, "LastQualificationDate"),
                NextQualificationDate = Date(row, "NextQualificationDate"),
                CalibrationRequired = Convert.ToBoolean(row["CalibrationRequired"], CultureInfo.InvariantCulture),
                CalibrationStatus = Text(row, "CalibrationStatus"),
                LastCalibrationDate = Date(row, "LastCalibrationDate"),
                NextCalibrationDate = Date(row, "NextCalibrationDate"),
                MethodReference = Text(row, "MethodReference"),
                Notes = Text(row, "Notes"),
                ControlledUses = Text(row, "ControlledUses"),
                EffectiveQualificationStatus = Text(row, "EffectiveQualificationStatus"),
                EffectiveCalibrationStatus = Text(row, "EffectiveCalibrationStatus"),
                UseReadiness = Text(row, "UseReadiness"),
                RowVersion = (byte[])row["RowVersion"]
            };

            private static string Text(DataRow row, string column) =>
                row[column] == DBNull.Value ? string.Empty : Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty;

            private static DateTime? Date(DataRow row, string column) =>
                row[column] == DBNull.Value ? null : Convert.ToDateTime(row[column], CultureInfo.InvariantCulture);
        }
    }
}
