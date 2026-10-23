using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
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
        private readonly IAuthService? _authService;
        private readonly List<EquipmentRow> _allEquipment = new();
        private int? _selectedEquipmentId;
        private byte[]? _selectedRowVersion;

        public LabResources()
        {
            InitializeComponent();
            _authService = App.ServiceProvider?.GetService<IAuthService>();
            Loaded += LabResources_Loaded;
        }

        public LabResources(IAuthService authService) : this() => _authService = authService;

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
            var user = _authService?.GetCurrentUser();
            if (user == null || !user.CanManageSettings)
            {
                MessageBox.Show("An authenticated account with system-settings permission is required.", "Permission Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ValidateForm(out string validationMessage))
            {
                MessageBox.Show(validationMessage, "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                if (_selectedEquipmentId.HasValue && _selectedRowVersion?.Length != 8)
                    throw new InvalidOperationException("Reload the equipment record before saving.");
                var request = new EquipmentSigningRequest(Guid.NewGuid(),
                    _selectedEquipmentId.HasValue ? SigningRequestPolicy.EquipmentUpdate : SigningRequestPolicy.EquipmentCreate,
                    _selectedEquipmentId, TxtCode.Text.Trim(), "", DateTimeOffset.UtcNow, _selectedRowVersion, BuildProposedState());
                var signature = new TrustedEquipmentSignature(request, user.Username, TrustedEquipmentSigningClient.CreateFromConfiguration()) { Owner = this };
                if (signature.ShowDialog() != true || !signature.IsConfirmed) return;
                LoadEquipment();
                ClearForm();
                LblStatus.Text = "Equipment record saved by the trusted signing service with signature and audit evidence.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Trusted equipment signing"), "Laboratory Resources", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private string BuildProposedState() => JsonSerializer.Serialize(new
        {
            EquipmentCode = TxtCode.Text.Trim(), EquipmentName = TxtName.Text.Trim(), EquipmentType = TxtType.Text.Trim(),
            Department = NullIfBlank(TxtDepartment.Text), Location = NullIfBlank(TxtLocation.Text),
            Manufacturer = NullIfBlank(TxtManufacturer.Text), Model = NullIfBlank(TxtModel.Text), SerialNumber = NullIfBlank(TxtSerial.Text),
            GmpCritical = ChkGmpCritical.IsChecked == true, EquipmentStatus = ComboText(CboStatus),
            QualificationStatus = ComboText(CboQualification), LastQualificationDate = DpLastQualification.SelectedDate?.Date,
            NextQualificationDate = DpNextQualification.SelectedDate?.Date, CalibrationRequired = ChkCalibrationRequired.IsChecked == true,
            CalibrationStatus = ComboText(CboCalibration), LastCalibrationDate = DpLastCalibration.SelectedDate?.Date,
            NextCalibrationDate = DpNextCalibration.SelectedDate?.Date, MethodReference = NullIfBlank(TxtMethodReference.Text), Notes = NullIfBlank(TxtNotes.Text)
        });

        private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
