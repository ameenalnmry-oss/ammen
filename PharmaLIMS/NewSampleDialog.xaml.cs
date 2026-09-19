using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Models;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PharmaLIMS
{
    public partial class NewSampleDialog : Window
    {
        private readonly IAuthService _authService;

        private DataTable _pointsTable = new DataTable();
        private DataTable _testsTable = new DataTable();

        private readonly HashSet<int> _selectedTestIds = new HashSet<int>();
        private readonly Dictionary<string, HashSet<int>> _waterProfileTestIds =
            new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        private string _currentTestFilter = "PHYSICAL";
        private bool _isInitializing = false;
        private bool _isDirty = false;
        private bool _isPlanRegistration;
        private int? _waterPlanSampleId;
        private readonly HashSet<int> _plannedWaterTestIds = new HashSet<int>();
        private int _validatedEmMediaPreparationId;

        public int RegisteredSampleId { get; private set; }
        public string RegisteredSampleNumber { get; private set; } = "";
        public string RegisteredSampleStatus { get; private set; } = "";

        public NewSampleDialog(IAuthService authService)
        {
            InitializeComponent();
            _authService = authService;

            _isInitializing = true;

            rbPurifiedWater.IsChecked = true;
            txtSystemRegisteredAt.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            // GMP rule: actual sampling, lab receipt, and incubation start times are manual entries.
            // Only system registration timestamp is automatic.
            dpSamplingDate.SelectedDate = null;
            txtSamplingTime.Text = string.Empty;
            dpReceivedDate.SelectedDate = null;
            txtReceivedTime.Text = string.Empty;
            dpAnalysisStartedDate.SelectedDate = null;
            txtAnalysisStartedTime.Text = string.Empty;

            txtSampledBy.Text = GetCurrentDisplayUser();
            txtReceivedBy.Text = GetCurrentDisplayUser();

            LoadTests();
            ApplyRegistrationMode();

            _isInitializing = false;
            _isDirty = false;

            AttachDirtyEvents();
            UpdatePreview();
        }

        private string GetCurrentDisplayUser()
        {
            var user = _authService.GetCurrentUser();

            if (user != null && !string.IsNullOrWhiteSpace(user.FullName))
                return user.FullName.Trim();

            if (user != null && !string.IsNullOrWhiteSpace(user.Username))
                return user.Username.Trim();

            if (!string.IsNullOrWhiteSpace(Login.CurrentUserFullName))
                return Login.CurrentUserFullName.Trim();

            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to register a sample.");
        }

        private void AttachDirtyEvents()
        {
            AttachTextChanged(txtSampledBy);
            AttachTextChanged(txtRemarks);
            AttachTextChanged(txtSamplingTime);
            AttachTextChanged(txtReceivedTime);
            AttachTextChanged(txtAnalysisStartedTime);
            AttachComboChanged(cboActivity);
            AttachTextChanged(txtSamplingTimeFrom);
            AttachTextChanged(txtSamplingTimeTo);
            AttachTextChanged(txtAirSamplerNo);
            AttachTextChanged(txtSanitizationDetails);
            AttachTextChanged(txtSanitizationTime);
            AttachTextChanged(txtMediaUsed);
            AttachTextChanged(txtMediaLotNo);
            AttachTextChanged(txtIncubationTemp);
            AttachTextChanged(txtBacteriaIncubator);
            AttachTextChanged(txtFungiIncubator);
            AttachTextChanged(txtContainerCount);
            AttachTextChanged(txtSampleVolume);
            AttachTextChanged(txtReceiptTemperature);
            AttachTextChanged(txtReceivedBy);
            AttachTextChanged(txtReceiptDeviationReason);
            AttachComboChanged(cboContainerCondition);
            AttachComboChanged(cboReceiptDecision);

            if (cboReceiptDecision != null)
                cboReceiptDecision.SelectionChanged += (_, __) => UpdateWaterIncubationFieldsVisibility();

            if (dpSamplingDate != null)
            {
                dpSamplingDate.SelectedDateChanged += (_, __) =>
                {
                    CalculateIncubationEnd();
                    MarkDirty(_, __);
                    UpdatePreview();
                };
            }

            if (dpReceivedDate != null)
            {
                dpReceivedDate.SelectedDateChanged += (_, __) =>
                {
                    MarkDirty(_, __);
                    UpdatePreview();
                };
            }

            if (dpAnalysisStartedDate != null)
            {
                dpAnalysisStartedDate.SelectedDateChanged += (_, __) =>
                {
                    CalculateIncubationEnd();
                    MarkDirty(_, __);
                    UpdatePreview();
                };
            }
        }

        private void AttachTextChanged(TextBox textBox)
        {
            if (textBox != null)
                textBox.TextChanged += (s, e) =>
                {
                    if (textBox == txtSamplingTime || textBox == txtAnalysisStartedTime)
                        CalculateIncubationEnd();

                    MarkDirty(s, e);
                    UpdatePreview();
                };
        }

        private void AttachComboChanged(ComboBox comboBox)
        {
            if (comboBox != null)
                comboBox.SelectionChanged += (s, e) =>
                {
                    MarkDirty(s, e);
                    UpdatePreview();
                };
        }

        private string GetSelectedActivity()
        {
            if (cboActivity == null)
                return "Normal Operation";

            if (!string.IsNullOrWhiteSpace(cboActivity.Text))
                return cboActivity.Text.Trim();

            if (cboActivity.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "Normal Operation";

            return "Normal Operation";
        }

        private static string GetComboSelectionText(ComboBox comboBox)
        {
            if (comboBox?.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString()?.Trim() ?? "";

            return comboBox?.SelectedItem?.ToString()?.Trim() ?? comboBox?.Text?.Trim() ?? "";
        }

        private void MarkDirty(object? sender, EventArgs e)
        {
            if (!_isInitializing)
                _isDirty = true;
        }

        private bool IsWaterMode()
        {
            return rbPurifiedWater != null &&
                   rbPotableWater != null &&
                   (rbPurifiedWater.IsChecked == true || rbPotableWater.IsChecked == true);
        }

        private bool IsPurifiedWaterMode()
        {
            return rbPurifiedWater != null && rbPurifiedWater.IsChecked == true;
        }

        private bool IsPotableWaterMode()
        {
            return rbPotableWater != null && rbPotableWater.IsChecked == true;
        }

        private bool IsEnvironmentalMode()
        {
            return rbEnvironmental != null && rbEnvironmental.IsChecked == true;
        }

        private string GetRegistrationTypeDisplay()
        {
            if (IsPurifiedWaterMode())
                return "Purified Water (PWS)";

            if (IsPotableWaterMode())
                return "Potable Water (PTWS)";

            return "Environmental Monitoring";
        }

        private string GetAutoStatus()
        {
            if (!IsWaterMode())
                return "Pending";

            if (GetComboSelectionText(cboReceiptDecision).Equals("Rejected", StringComparison.OrdinalIgnoreCase))
                return "Rejected";

            return HasSelectedMicrobiologicalTest() ? "Incubation" : "Registered";
        }

        private bool HasSelectedMicrobiologicalTest()
        {
            if (_selectedTestIds.Count == 0 || _testsTable == null)
                return false;

            return _testsTable.AsEnumerable().Any(row =>
            {
                if (!int.TryParse(row["test_id"]?.ToString(), out int testId) ||
                    !_selectedTestIds.Contains(testId))
                    return false;

                string category = row["test_type"]?.ToString() ?? "";
                return category.IndexOf("micro", StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        private int GetIncubationDays()
        {
            if (IsWaterMode() && cboWaterIncubationProgram?.SelectedItem is ComboBoxItem selected &&
                int.TryParse(selected.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) && days > 0)
            {
                return days;
            }

            return 5;
        }

        private int GetEarliestIncubationDays()
        {
            if (!IsWaterMode())
                return 3;

            string program = (cboWaterIncubationProgram?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
            if (program.StartsWith("TAMC —", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (program.StartsWith("TYMC", StringComparison.OrdinalIgnoreCase))
                return 5;

            return GetIncubationDays();
        }

        private string GetWaterPrefix()
        {
            return IsPurifiedWaterMode() ? "PW" : "PTW";
        }

        private string GetNextWaterSampleNumber()
        {
            return DatabaseHelper.GetNextWaterSampleNumber(GetWaterPrefix());
        }

        private string GetPreviewNumberText()
        {
            return "Generated on Save";
        }

        private string GetCurrentWaterProfileCode()
        {
            if (IsPurifiedWaterMode())
                return "PW";

            if (IsPotableWaterMode())
                return "PTW";

            return "";
        }

        private HashSet<int> GetAllowedWaterTestIds()
        {
            string profileCode = GetCurrentWaterProfileCode();

            if (string.IsNullOrWhiteSpace(profileCode))
                return new HashSet<int>();

            if (_waterProfileTestIds.TryGetValue(profileCode, out HashSet<int>? cachedIds) && cachedIds != null)
                return cachedIds;

            HashSet<int> ids = new HashSet<int>();
            DataTable dt = DatabaseHelper.GetActiveWaterTestIdsForProfile(profileCode);

            foreach (DataRow row in dt.Rows)
            {
                if (int.TryParse(row["TestID"]?.ToString(), out int testId))
                    ids.Add(testId);
            }

            if (ids.Count == 0)
                throw new InvalidOperationException(
                    "The controlled Water Test Profile contains no active tests. Registration is blocked.");

            _waterProfileTestIds[profileCode] = ids;
            return ids;
        }

        private bool IsAllowedCurrentWaterTest(int testId)
        {
            if (!IsWaterMode())
                return false;

            HashSet<int> allowedIds = GetAllowedWaterTestIds();
            return allowedIds.Contains(testId);
        }

        private IEnumerable<DataRow> GetCurrentVisibleTestRows()
        {
            IEnumerable<DataRow> rows = _testsTable.AsEnumerable();

            if (IsWaterMode())
            {
                if (_isPlanRegistration && _plannedWaterTestIds.Count > 0)
                {
                    rows = rows.Where(r =>
                        int.TryParse(r["test_id"]?.ToString(), out int testId) &&
                        _plannedWaterTestIds.Contains(testId));
                }
                else
                {
                    HashSet<int> allowedIds = GetAllowedWaterTestIds();
                    rows = rows.Where(r =>
                    {
                        if (!int.TryParse(r["test_id"]?.ToString(), out int testId))
                            return false;
                        return allowedIds.Contains(testId);
                    });
                }
            }

            if (_currentTestFilter == "PHYSICAL")
            {
                rows = rows.Where(r =>
                    (r["test_type"]?.ToString() ?? "")
                    .IndexOf("physical", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            else if (_currentTestFilter == "CHEMICAL")
            {
                rows = rows.Where(r =>
                    (r["test_type"]?.ToString() ?? "")
                    .IndexOf("chemical", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            else if (_currentTestFilter == "MICRO")
            {
                rows = rows.Where(r =>
                    (r["test_type"]?.ToString() ?? "")
                    .IndexOf("micro", StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return rows;
        }

        private void ClearSelectedTests()
        {
            _selectedTestIds.Clear();
            UpdateSelectedCount();
            UpdateWaterIncubationFieldsVisibility();
        }

        private void RegistrationType_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing)
                return;

            ApplyRegistrationMode();
            MarkDirty(sender, e);
            UpdatePreview();
        }

        private void SetWaterTimingFieldsVisibility(bool isWater)
        {
            Visibility visibility = isWater ? Visibility.Visible : Visibility.Collapsed;

            if (lblReceivedDate != null) lblReceivedDate.Visibility = visibility;
            if (dpReceivedDate != null) dpReceivedDate.Visibility = visibility;
            if (lblReceivedTime != null) lblReceivedTime.Visibility = visibility;
            if (txtReceivedTime != null) txtReceivedTime.Visibility = visibility;
            UpdateWaterIncubationFieldsVisibility();
        }

        private void UpdateWaterIncubationFieldsVisibility()
        {
            bool sampleAccepted = !GetComboSelectionText(cboReceiptDecision)
                .Equals("Rejected", StringComparison.OrdinalIgnoreCase);
            bool showIncubation = IsWaterMode() && sampleAccepted && HasSelectedMicrobiologicalTest();
            Visibility visibility = showIncubation ? Visibility.Visible : Visibility.Collapsed;

            if (lblAnalysisStartedDate != null) lblAnalysisStartedDate.Visibility = visibility;
            if (dpAnalysisStartedDate != null) dpAnalysisStartedDate.Visibility = visibility;
            if (lblAnalysisStartedTime != null) lblAnalysisStartedTime.Visibility = visibility;
            if (txtAnalysisStartedTime != null) txtAnalysisStartedTime.Visibility = visibility;
            if (lblWaterIncubationProgram != null) lblWaterIncubationProgram.Visibility = visibility;
            if (cboWaterIncubationProgram != null) cboWaterIncubationProgram.Visibility = visibility;
            if (lblWaterIncubatorId != null) lblWaterIncubatorId.Visibility = visibility;
            if (txtWaterIncubatorId != null) txtWaterIncubatorId.Visibility = visibility;
            if (lblEarliestReading != null) lblEarliestReading.Visibility = visibility;
            if (txtEarliestReading != null) txtEarliestReading.Visibility = visibility;
            if (lblIncubationEnd != null) lblIncubationEnd.Visibility = visibility;
            if (txtIncubationEnd != null) txtIncubationEnd.Visibility = visibility;
            if (lblIncubationRule != null) lblIncubationRule.Visibility = visibility;
            if (txtIncubationRule != null) txtIncubationRule.Visibility = visibility;

            if (txtStatus != null && IsWaterMode())
                txtStatus.Text = GetAutoStatus();

            if (showIncubation)
                CalculateIncubationEnd();
        }

        private void ApplyRegistrationMode()
        {
            bool isWater = IsWaterMode();
            bool isEm = IsEnvironmentalMode();

            SetWaterTimingFieldsVisibility(isWater);
            if (lblSamplingTime != null) lblSamplingTime.Visibility = isWater ? Visibility.Visible : Visibility.Collapsed;
            if (txtSamplingTime != null) txtSamplingTime.Visibility = isWater ? Visibility.Visible : Visibility.Collapsed;

            if (grpEmDetails != null)
                grpEmDetails.Visibility = isEm ? Visibility.Visible : Visibility.Collapsed;

            if (grpEmMedia != null)
                grpEmMedia.Visibility = isEm ? Visibility.Visible : Visibility.Collapsed;

            if (grpEmGuidance != null)
                grpEmGuidance.Visibility = isEm ? Visibility.Visible : Visibility.Collapsed;

            if (grpWaterTests != null)
                grpWaterTests.Visibility = isWater ? Visibility.Visible : Visibility.Collapsed;

            if (grpWaterReceipt != null)
                grpWaterReceipt.Visibility = isWater ? Visibility.Visible : Visibility.Collapsed;

            if (cboActivity != null && isEm && string.IsNullOrWhiteSpace(cboActivity.Text))
                cboActivity.Text = "Normal Operation";

            if (txtStatus != null)
                txtStatus.Text = GetAutoStatus();

            ClearSelectedTests();
            _currentTestFilter = "PHYSICAL";

            if (isWater)
            {
                lblModeSummary.Text = IsPurifiedWaterMode() ? "PURIFIED WATER" : "POTABLE WATER";

                if (lblPointCode != null)
                    lblPointCode.Text = "Sample Point No.";

                if (lblPointDescription != null)
                    lblPointDescription.Text = "Point Location / Description";

                LoadWaterPoints();

                if (txtSampleNo != null)
                    txtSampleNo.Text = GetPreviewNumberText();

                RenderTests(_currentTestFilter);

                // Hide additional EM fields
                if (grpEmDetails != null)
                    grpEmDetails.Visibility = Visibility.Collapsed;

                if (grpEmMedia != null)
                    grpEmMedia.Visibility = Visibility.Collapsed;

                if (grpEmGuidance != null)
                    grpEmGuidance.Visibility = Visibility.Collapsed;

                // Show water tests
                if (grpWaterTests != null)
                    grpWaterTests.Visibility = Visibility.Visible;
            }
            else
            {
                lblModeSummary.Text = "ENVIRONMENTAL MONITORING";

                if (lblPointCode != null)
                    lblPointCode.Text = "EM Area";

                if (lblPointDescription != null)
                    lblPointDescription.Text = "Area Details";

                LoadEnvironmentalFilters();
                LoadEnvironmentalAreas();

                if (txtSampleNo != null)
                    txtSampleNo.Text = GetPreviewNumberText();

                UpdateExpectedPlates();

                // Hide water tests
                if (grpWaterTests != null)
                    grpWaterTests.Visibility = Visibility.Collapsed;

                // Show EM fields
                if (grpEmDetails != null)
                    grpEmDetails.Visibility = Visibility.Visible;

                if (grpEmMedia != null)
                    grpEmMedia.Visibility = Visibility.Visible;

                if (grpEmGuidance != null)
                    grpEmGuidance.Visibility = Visibility.Visible;
            }

            CalculateIncubationEnd();
            UpdateMethodDependentFields();
            UpdatePreview();
        }

        private DateTime GetSelectedSamplingDateTime()
        {
            if (IsEnvironmentalMode())
                return GetManualDateTime(dpSamplingDate, txtSamplingTimeFrom, "Sampling Date / Time");

            return GetManualDateTime(dpSamplingDate, txtSamplingTime, "Sampling Date / Time");
        }

        public void PrepareWaterPlanRegistration(string waterType, int pointId, DateTime samplingDate, string analysisProfile = "Full", int? waterPlanSampleId = null)
        {
            if (!waterPlanSampleId.HasValue || waterPlanSampleId.Value <= 0)
                throw new InvalidOperationException("A controlled Water Plan sample reference is required for plan-based registration.");

            _isPlanRegistration = true;
            _waterPlanSampleId = waterPlanSampleId.Value;
            _isInitializing = true;
            _selectedTestIds.Clear();
            _plannedWaterTestIds.Clear();

            rbPurifiedWater.IsChecked = waterType.Equals("Purified", StringComparison.OrdinalIgnoreCase);
            rbPotableWater.IsChecked = !rbPurifiedWater.IsChecked;
            ApplyRegistrationMode();

            DataTable planState = ExecuteDataTable(@"
SELECT ps.PointID,ps.Status AS SampleStatus,p.Status AS PlanStatus
FROM dbo.Water_PlanSamples ps
INNER JOIN dbo.Water_Plans p ON p.WaterPlanID=ps.WaterPlanID
WHERE ps.WaterPlanSampleID=@ID;", new SqlParameter("@ID", waterPlanSampleId.Value));
            if (planState.Rows.Count != 1)
                throw new InvalidOperationException("The selected Water Plan sample no longer exists. Refresh the Water Plan.");
            DataRow state = planState.Rows[0];
            int controlledPointId = Convert.ToInt32(state["PointID"], CultureInfo.InvariantCulture);
            string planStatus = state["PlanStatus"]?.ToString() ?? string.Empty;
            string sampleStatus = state["SampleStatus"]?.ToString() ?? string.Empty;
            if (controlledPointId != pointId)
                throw new InvalidOperationException("The Water Plan sampling point changed. Refresh before registration.");
            if (!planStatus.Equals("Distributed", StringComparison.OrdinalIgnoreCase) &&
                !planStatus.Equals("In Collection", StringComparison.OrdinalIgnoreCase) &&
                !planStatus.Equals("Recollection Required", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The Water Plan must be distributed before sample registration.");
            if (!sampleStatus.Equals("Distributed", StringComparison.OrdinalIgnoreCase) &&
                !sampleStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase) &&
                !sampleStatus.Equals("Recollection Required", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This Water Plan row is not available for controlled registration.");

            DataTable planned = ExecuteDataTable(@"
SELECT planTest.TestID
FROM dbo.Water_PlanSampleTests planTest
INNER JOIN dbo.Tests test ON test.TestID=planTest.TestID
WHERE planTest.WaterPlanSampleID=@ID
ORDER BY planTest.TestID;", new SqlParameter("@ID", waterPlanSampleId.Value));
            foreach (DataRow row in planned.Rows)
                _plannedWaterTestIds.Add(Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture));
            if (_plannedWaterTestIds.Count == 0)
                throw new InvalidOperationException("The distributed Water Plan row has no controlled tests. Registration is blocked.");

            foreach (DataRow row in _testsTable.Rows)
            {
                if (!int.TryParse(row["test_id"]?.ToString(), out int testId))
                    continue;
                if (_plannedWaterTestIds.Contains(testId))
                    _selectedTestIds.Add(testId);
            }
            if (!_selectedTestIds.SetEquals(_plannedWaterTestIds))
                throw new InvalidOperationException("One or more tests frozen in the distributed Water Plan are missing from the controlled test master. Registration is blocked; do not substitute tests.");

            if (btnSelectAll != null) btnSelectAll.IsEnabled = false;
            if (btnClearAll != null) btnClearAll.IsEnabled = false;
            RenderTests(_currentTestFilter);
            UpdateSelectedCount();
            UpdateWaterIncubationFieldsVisibility();
            foreach (DataRowView item in cboPointCode.Items)
            {
                if (Convert.ToInt32(item["Id"], CultureInfo.InvariantCulture) == pointId)
                {
                    cboPointCode.SelectedItem = item;
                    break;
                }
            }
            dpSamplingDate.SelectedDate = samplingDate.Date;
            _isInitializing = false;
            _isDirty = false;
            UpdatePreview();
        }

        private DateTime GetReceivedInLabDateTime()
        {
            return GetManualDateTime(dpReceivedDate, txtReceivedTime, "Received in Lab Date / Time");
        }

        private DateTime GetIncubationStartedDateTime()
        {
            return GetManualDateTime(dpAnalysisStartedDate, txtAnalysisStartedTime, "Incubation Start Date / Time");
        }

        private DateTime GetManualDateTime(DatePicker datePicker, TextBox timeBox, string fieldName)
        {
            if (datePicker == null || !datePicker.SelectedDate.HasValue)
                throw new InvalidOperationException(fieldName + " is required. Please enter the actual date manually.");

            string timeText = timeBox == null ? "" : timeBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(timeText))
                throw new InvalidOperationException(fieldName + " is required. Please enter the actual time manually using HH:mm format.");

            if (TimeSpan.TryParseExact(timeText, @"hh\:mm", CultureInfo.InvariantCulture, out TimeSpan exactTime))
                return datePicker.SelectedDate.Value.Date.Add(exactTime);

            if (TimeSpan.TryParse(timeText, CultureInfo.InvariantCulture, out TimeSpan parsedTime))
                return datePicker.SelectedDate.Value.Date.Add(parsedTime);

            throw new InvalidOperationException("Invalid " + fieldName + ". Use HH:mm format, for example 08:30 or 14:05.");
        }

        private void ValidateWaterDateSequence(DateTime samplingDateTime, DateTime receivedDateTime, DateTime incubationStartedDateTime)
        {
            if (receivedDateTime < samplingDateTime)
                throw new InvalidOperationException("Received in Lab Date / Time cannot be earlier than Sampling Date / Time.");

            if (incubationStartedDateTime < receivedDateTime)
                throw new InvalidOperationException("Incubation Start Date / Time cannot be earlier than Received in Lab Date / Time.");
        }

        private static void ValidateWaterManualTimestampsAgainstDatabaseTime(
            DateTime samplingDateTime,
            DateTime receivedDateTime,
            DateTime? incubationStartedDateTime,
            DateTime databaseNow)
        {
            if (receivedDateTime < samplingDateTime)
                throw new InvalidOperationException("Received in Lab Date / Time cannot be earlier than Sampling Date / Time.");

            if (incubationStartedDateTime.HasValue && incubationStartedDateTime.Value < receivedDateTime)
                throw new InvalidOperationException("Incubation Start Date / Time cannot be earlier than Received in Lab Date / Time.");

            DateTime latestAllowed = databaseNow.AddMinutes(1);
            if (samplingDateTime > latestAllowed)
                throw new InvalidOperationException("Sampling Date / Time cannot be in the future according to the authoritative database clock.");
            if (receivedDateTime > latestAllowed)
                throw new InvalidOperationException("Received in Lab Date / Time cannot be in the future according to the authoritative database clock.");
            if (incubationStartedDateTime.HasValue && incubationStartedDateTime.Value > latestAllowed)
                throw new InvalidOperationException("Incubation Start Date / Time cannot be in the future according to the authoritative database clock.");
        }

        private void CalculateIncubationEnd()
        {
            if (txtIncubationEnd == null)
                return;

            if (!IsWaterMode())
            {
                DateTime emBase;
                try
                {
                    emBase = GetSelectedSamplingDateTime();
                }
                catch
                {
                    if (txtEarliestReading != null) txtEarliestReading.Text = "Enter actual sampling date/time";
                    txtIncubationEnd.Text = "Enter actual sampling date/time";
                    return;
                }
                txtIncubationEnd.Text = emBase.AddDays(5).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                if (txtEarliestReading != null) txtEarliestReading.Text = emBase.AddDays(3).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                return;
            }

            DateTime incubationStart;
            try
            {
                incubationStart = GetIncubationStartedDateTime();
            }
            catch
            {
                if (txtEarliestReading != null) txtEarliestReading.Text = "Enter actual incubation start";
                txtIncubationEnd.Text = "Enter actual incubation start";
                return;
            }

            DateTime earliest = incubationStart.AddDays(GetEarliestIncubationDays());
            DateTime latest = incubationStart.AddDays(GetIncubationDays());
            if (txtEarliestReading != null)
                txtEarliestReading.Text = earliest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            txtIncubationEnd.Text = latest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private void WaterIncubationProgram_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CalculateIncubationEnd();
            if (!_isInitializing) _isDirty = true;
            UpdatePreview();
        }

        private void LoadWaterPoints()
        {
            try
            {
                string waterType = IsPurifiedWaterMode()
                    ? "Purified"
                    : "Potable";

                string sql = @"
                    SELECT 
                        Id,
                        PointCode,
                        PointName,
                        WaterType,
                        Location,
                        Status
                    FROM dbo.WaterSamplingPoints
                    WHERE ISNULL(Status,'') = 'Active'
                      AND WaterType = @WaterType
                    ORDER BY PointCode";

                DataTable dt = ExecuteDataTable(sql, new SqlParameter("@WaterType", waterType));

                _pointsTable = new DataTable();
                _pointsTable.Columns.Add("Id", typeof(int));
                _pointsTable.Columns.Add("Code", typeof(string));
                _pointsTable.Columns.Add("DisplayText", typeof(string));
                _pointsTable.Columns.Add("DescriptionText", typeof(string));
                _pointsTable.Columns.Add("LocationText", typeof(string));
                _pointsTable.Columns.Add("WaterType", typeof(string));

                foreach (DataRow row in dt.Rows)
                {
                    string pointCode = row["PointCode"]?.ToString() ?? "";
                    string pointName = row["PointName"]?.ToString() ?? "";
                    string location = row["Location"]?.ToString() ?? "";
                    string rowWaterType = row["WaterType"]?.ToString() ?? "";

                    string description = (pointName + " - " + location).Trim().Trim('-').Trim();

                    _pointsTable.Rows.Add(
                        Convert.ToInt32(row["Id"]),
                        pointCode,
                        pointCode,
                        description,
                        location,
                        rowWaterType);
                }

                BindPointsTable();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading water sampling points:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadEnvironmentalFilters()
        {
            try
            {
                if (cboAreaGroup == null || cboGrade == null)
                    return;

                bool oldInit = _isInitializing;
                _isInitializing = true;

                DataTable dtAreas = ExecuteDataTable(@"
                    SELECT DISTINCT AreaGroup, Grade
                    FROM dbo.EM_Areas
                    WHERE IsActive = 1
                    ORDER BY AreaGroup, Grade");

                cboAreaGroup.Items.Clear();
                cboAreaGroup.Items.Add("All");

                foreach (DataRow row in dtAreas.Rows)
                {
                    string value = row["AreaGroup"]?.ToString() ?? "";
                    AddComboItemIfMissing(cboAreaGroup, value);
                }

                cboAreaGroup.SelectedIndex = 0;

                cboGrade.Items.Clear();
                cboGrade.Items.Add("All");

                foreach (DataRow row in dtAreas.Rows)
                {
                    string value = row["Grade"]?.ToString() ?? "";
                    AddComboItemIfMissing(cboGrade, value);
                }

                cboGrade.SelectedIndex = 0;

                _isInitializing = oldInit;
            }
            catch (Exception ex)
            {
                _isInitializing = false;
                MessageBox.Show("Error loading EM filters:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AddComboItemIfMissing(ComboBox combo, string value)
        {
            if (combo == null || string.IsNullOrWhiteSpace(value))
                return;

            foreach (object item in combo.Items)
            {
                if (string.Equals(item?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            combo.Items.Add(value);
        }

        private void LoadEnvironmentalAreas()
        {
            try
            {
                var parameters = new List<SqlParameter>();

                StringBuilder sql = new StringBuilder(@"
                    SELECT 
                        Id,
                        AreaCode,
                        AreaName,
                        AreaGroup,
                        Grade
                    FROM dbo.EM_Areas
                    WHERE IsActive = 1");

                if (cboAreaGroup != null &&
                    cboAreaGroup.SelectedItem != null &&
                    cboAreaGroup.SelectedItem.ToString() != "All")
                {
                    sql.Append(" AND AreaGroup = @AreaGroup");
                    parameters.Add(new SqlParameter("@AreaGroup", cboAreaGroup.SelectedItem.ToString()));
                }

                if (cboGrade != null &&
                    cboGrade.SelectedItem != null &&
                    cboGrade.SelectedItem.ToString() != "All")
                {
                    sql.Append(" AND Grade = @Grade");
                    parameters.Add(new SqlParameter("@Grade", cboGrade.SelectedItem.ToString()));
                }

                sql.Append(@"
                    ORDER BY
                        TRY_CONVERT(INT, REPLACE(UPPER(LTRIM(RTRIM(AreaCode))), 'D', '')),
                        AreaCode");

                DataTable dt = ExecuteDataTable(sql.ToString(), parameters.ToArray());

                _pointsTable = new DataTable();
                _pointsTable.Columns.Add("Id", typeof(int));
                _pointsTable.Columns.Add("Code", typeof(string));
                _pointsTable.Columns.Add("DisplayText", typeof(string));
                _pointsTable.Columns.Add("DescriptionText", typeof(string));
                _pointsTable.Columns.Add("LocationText", typeof(string));
                _pointsTable.Columns.Add("AreaGroup", typeof(string));
                _pointsTable.Columns.Add("Grade", typeof(string));

                foreach (DataRow row in dt.Rows)
                {
                    string areaCode = row["AreaCode"]?.ToString() ?? "";
                    string areaName = row["AreaName"]?.ToString() ?? "";
                    string areaGroup = row["AreaGroup"]?.ToString() ?? "";
                    string grade = row["Grade"]?.ToString() ?? "";

                    string display = areaCode + " - " + areaName;
                    string description = areaCode + " - " + areaName + " - " + areaGroup + " - " + grade;

                    _pointsTable.Rows.Add(
                        Convert.ToInt32(row["Id"]),
                        areaCode,
                        display,
                        description,
                        areaName,
                        areaGroup,
                        grade);
                }

                BindPointsTable();
                UpdateAllowedEmMethods();
                UpdateExpectedPlates();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading EM areas:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BindPointsTable()
        {
            if (cboPointCode == null)
                return;

            cboPointCode.ItemsSource = _pointsTable.DefaultView;
            cboPointCode.DisplayMemberPath = "DisplayText";
            cboPointCode.SelectedValuePath = "Code";

            if (cboPointCode.Items.Count > 0)
                cboPointCode.SelectedIndex = 0;
            else if (txtPointDescription != null)
                txtPointDescription.Text = "";
        }

        private void EmFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing || !IsEnvironmentalMode())
                return;

            LoadEnvironmentalAreas();
            MarkDirty(sender, e);
            UpdatePreview();
        }

        private void CboPointCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cboPointCode != null && cboPointCode.SelectedItem is DataRowView rowView)
            {
                if (txtPointDescription != null)
                    txtPointDescription.Text = rowView["DescriptionText"]?.ToString() ?? "";
            }
            else
            {
                if (txtPointDescription != null)
                    txtPointDescription.Text = "";
            }

            if (!_isInitializing)
                _isDirty = true;

            UpdateAllowedEmMethods();
            UpdateExpectedPlates();
            UpdatePreview();
        }

        private void LoadTests()
        {
            try
            {
                _testsTable = ExecuteDataTable(@"
                    SELECT 
                        TestID AS test_id,
                        TestName AS test_name,
                        TestCategory AS test_type
                    FROM dbo.Tests
                    ORDER BY TestCategory, SortOrder, TestName");

                RenderTests(_currentTestFilter);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading tests:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RenderTests(string filter)
        {
            _currentTestFilter = filter;

            if (spTests == null)
                return;

            spTests.Children.Clear();

            IEnumerable<DataRow> rows;
            try
            {
                rows = GetCurrentVisibleTestRows().ToList();
            }
            catch (InvalidOperationException ex) when (IsWaterMode() && !_isPlanRegistration)
            {
                spTests.Children.Add(new TextBlock
                {
                    Text = ex.Message + "\nUse Manage Controlled Profiles to configure an approved PW/PTW profile.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = System.Windows.Media.Brushes.DarkRed,
                    Margin = new Thickness(8),
                    FontSize = 13
                });
                if (btnSelectAll != null) btnSelectAll.IsEnabled = false;
                if (btnClearAll != null) btnClearAll.IsEnabled = false;
                UpdateSelectedCount();
                return;
            }

            if (btnSelectAll != null) btnSelectAll.IsEnabled = !_isPlanRegistration;
            if (btnClearAll != null) btnClearAll.IsEnabled = !_isPlanRegistration;

            foreach (DataRow row in rows)
            {
                int testId = Convert.ToInt32(row["test_id"]);
                string testName = row["test_name"]?.ToString() ?? "";
                string testType = row["test_type"]?.ToString() ?? "";

                CheckBox chk = new CheckBox
                {
                    Content = testName,
                    Tag = testId,
                    IsChecked = _selectedTestIds.Contains(testId),
                    Margin = new Thickness(6),
                    FontSize = 15,
                    ToolTip = _isPlanRegistration ? testType + " | Controlled by distributed Water Plan" : testType,
                    IsEnabled = !_isPlanRegistration
                };

                chk.Checked += TestCheckBox_Changed;
                chk.Unchecked += TestCheckBox_Changed;

                spTests.Children.Add(chk);
            }

            UpdateSelectedCount();
        }

        private void TestCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isPlanRegistration)
            {
                if (sender is CheckBox lockedCheckBox && lockedCheckBox.Tag is int lockedTestId)
                    lockedCheckBox.IsChecked = _plannedWaterTestIds.Contains(lockedTestId);
                return;
            }

            if (sender is CheckBox chk && chk.Tag is int testId)
            {
                if (!IsAllowedCurrentWaterTest(testId))
                {
                    chk.IsChecked = false;
                    return;
                }

                if (chk.IsChecked == true)
                    _selectedTestIds.Add(testId);
                else
                    _selectedTestIds.Remove(testId);

                UpdateSelectedCount();
                UpdateWaterIncubationFieldsVisibility();

                if (!_isInitializing)
                    _isDirty = true;

                UpdatePreview();
            }
        }

        private void UpdateSelectedCount()
        {
            if (txtSelectedCount != null)
                txtSelectedCount.Text = "Selected: " + _selectedTestIds.Count.ToString(CultureInfo.InvariantCulture) + " tests";
        }

        private void FilterTests_Click(object sender, RoutedEventArgs e)
        {
            if (sender == btnPhysical)
                RenderTests("PHYSICAL");
            else if (sender == btnChemical)
                RenderTests("CHEMICAL");
            else if (sender == btnMicro)
                RenderTests("MICRO");
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlanRegistration)
                throw new InvalidOperationException("Tests are frozen by the distributed Water Plan and cannot be changed during registration.");
            IEnumerable<DataRow> rows = GetCurrentVisibleTestRows();

            foreach (DataRow row in rows)
            {
                if (int.TryParse(row["test_id"]?.ToString(), out int testId))
                    _selectedTestIds.Add(testId);
            }

            RenderTests(_currentTestFilter);
            UpdateWaterIncubationFieldsVisibility();
            _isDirty = true;
            UpdatePreview();
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isPlanRegistration)
                throw new InvalidOperationException("Tests are frozen by the distributed Water Plan and cannot be changed during registration.");
            IEnumerable<DataRow> rows = GetCurrentVisibleTestRows();

            foreach (DataRow row in rows)
            {
                if (int.TryParse(row["test_id"]?.ToString(), out int testId))
                    _selectedTestIds.Remove(testId);
            }

            RenderTests(_currentTestFilter);
            UpdateWaterIncubationFieldsVisibility();
            _isDirty = true;
            UpdatePreview();
        }

        private void BtnManageWaterProfiles_Click(object sender, RoutedEventArgs e)
        {
            string username = Login.CurrentUser?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(username) || !DatabaseHelper.CanManageSettings(username))
            {
                MessageBox.Show(
                    "Settings permission is required to manage controlled Water Test Profiles.",
                    "Permission Denied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string profileCode = GetCurrentWaterProfileCode();
            if (string.IsNullOrWhiteSpace(profileCode))
            {
                MessageBox.Show(
                    "Select Purified Water or Potable Water before opening controlled profile management.",
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new WaterTestProfileManagement(profileCode)
            {
                Owner = this
            };

            dialog.ShowDialog();

            if (!dialog.WasChanged)
                return;

            _waterProfileTestIds.Clear();
            ClearSelectedTests();
            LoadTests();
            RenderTests(_currentTestFilter);
            UpdateWaterIncubationFieldsVisibility();
            UpdatePreview();
        }

        private void CboActivity_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing)
                _isDirty = true;

            UpdatePreview();
        }

        private void CboMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMethodDependentFields();
            UpdateExpectedPlates();

            if (!_isInitializing)
                _isDirty = true;

            UpdatePreview();
        }

        private void UpdateMethodDependentFields()
        {
            string method = GetSelectedMethod();
            bool exposureRangeMethod =
                string.Equals(method, "Settle Plate", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, "Both Methods", StringComparison.OrdinalIgnoreCase);

            if (lblSamplingTimeFrom != null)
                lblSamplingTimeFrom.Text = exposureRangeMethod ? "Exposure Start Time" : "Sampling Time";
            if (lblSamplingTimeTo != null)
                lblSamplingTimeTo.Visibility = exposureRangeMethod ? Visibility.Visible : Visibility.Collapsed;
            if (txtSamplingTimeTo != null)
                txtSamplingTimeTo.Visibility = exposureRangeMethod ? Visibility.Visible : Visibility.Collapsed;

            bool showAirFields =
                string.Equals(method, "Active Air Sampling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(method, "Both Methods", StringComparison.OrdinalIgnoreCase);

            bool personnelMethod = string.Equals(method, "Personnel Monitoring", StringComparison.OrdinalIgnoreCase);
            bool surfaceMethod = string.Equals(method, "Surface Swab", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(method, "Contact Plate", StringComparison.OrdinalIgnoreCase);
            bool extendedMethod = personnelMethod || surfaceMethod;

            Visibility airVisibility = IsEnvironmentalMode() && showAirFields
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (lblAirSamplerNo != null)
                lblAirSamplerNo.Visibility = airVisibility;

            if (txtAirSamplerNo != null)
                txtAirSamplerNo.Visibility = airVisibility;

            if (grpEmExtendedContext != null)
                grpEmExtendedContext.Visibility = IsEnvironmentalMode() && extendedMethod
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            Visibility employeeVisibility = personnelMethod ? Visibility.Visible : Visibility.Collapsed;
            if (lblEmployeeId != null) lblEmployeeId.Visibility = employeeVisibility;
            if (txtEmployeeId != null) txtEmployeeId.Visibility = employeeVisibility;
            if (lblEmployeeName != null) lblEmployeeName.Visibility = employeeVisibility;
            if (txtEmployeeName != null) txtEmployeeName.Visibility = employeeVisibility;
            if (lblEmployeeDepartment != null) lblEmployeeDepartment.Visibility = employeeVisibility;
            if (txtEmployeeDepartment != null) txtEmployeeDepartment.Visibility = employeeVisibility;
            if (lblEmployeeShift != null) lblEmployeeShift.Visibility = employeeVisibility;
            if (cboEmployeeShift != null) cboEmployeeShift.Visibility = employeeVisibility;

            Visibility surfaceVisibility = surfaceMethod ? Visibility.Visible : Visibility.Collapsed;
            if (lblSurfaceLocation != null) lblSurfaceLocation.Visibility = surfaceVisibility;
            if (txtSurfaceLocation != null) txtSurfaceLocation.Visibility = surfaceVisibility;
            if (lblSurfaceArea != null) lblSurfaceArea.Visibility = surfaceVisibility;
            if (txtSurfaceAreaCm2 != null) txtSurfaceAreaCm2.Visibility = surfaceVisibility;

            bool swabMethod = string.Equals(method, "Surface Swab", StringComparison.OrdinalIgnoreCase);
            Visibility swabVisibility = swabMethod ? Visibility.Visible : Visibility.Collapsed;
            if (lblSwabKitLot != null) lblSwabKitLot.Visibility = swabVisibility;
            if (txtSwabKitLot != null) txtSwabKitLot.Visibility = swabVisibility;
            if (lblDiluentLot != null) lblDiluentLot.Visibility = swabVisibility;
            if (txtDiluentLot != null) txtDiluentLot.Visibility = swabVisibility;
            if (txtRecoveryVolumeMl != null) txtRecoveryVolumeMl.Visibility = swabVisibility;
            if (lblSurfaceType != null) lblSurfaceType.Visibility = surfaceVisibility;
            if (cboSurfaceType != null) cboSurfaceType.Visibility = surfaceVisibility;
        }

        private void UpdateAllowedEmMethods()
        {
            if (!IsEnvironmentalMode())
                return;

            // Keep approved methods visible so changing the area never makes a configured
            // method appear to have been deleted. Area/grade scope is enforced on save.
            if (itemContactPlate != null) itemContactPlate.Visibility = Visibility.Visible;
            if (itemSurfaceSwab != null) itemSurfaceSwab.Visibility = Visibility.Visible;
            if (itemPersonnelMonitoring != null) itemPersonnelMonitoring.Visibility = Visibility.Visible;
        }

        private bool IsSelectedMethodAllowedForArea(out string message)
        {
            message = "";
            if (cboPointCode?.SelectedItem is not DataRowView area)
                return false;

            string method = GetSelectedMethod();
            string grade = (area["Grade"]?.ToString() ?? "").Trim().ToUpperInvariant();
            string group = (area["AreaGroup"]?.ToString() ?? "").Trim().ToUpperInvariant();
            bool gradeC = grade == "C" || grade.Contains("GRADE C");
            bool gradeD = grade == "D" || grade.Contains("GRADE D");
            bool iso8 = grade.Contains("ISO 8") || grade.Contains("ISO8");
            bool unclassified = grade.Contains("UNCLASSIFIED") || grade.Contains("NOT CLASSIFIED") || grade == "CNC";
            bool microbiology = group.Contains("MICRO") || group.Contains("LABORATORY");
            bool approvedExtendedScope = gradeD || iso8 || unclassified || (microbiology && (gradeC || gradeD));

            if ((method == "Contact Plate" || method == "Surface Swab") && !approvedExtendedScope)
            {
                message = "Contact Plate and Surface Swab are permitted for Grade D, ISO 8 or Unclassified areas, and Grade C/D microbiology areas.";
                return false;
            }

            if (method == "Personnel Monitoring" && !approvedExtendedScope)
            {
                message = "Personnel Monitoring is permitted for Grade D, ISO 8 or Unclassified areas, and Grade C/D microbiology areas.";
                return false;
            }

            return true;
        }

        private string GetSelectedMethod()
        {
            if (cboMethod == null)
                return "Both Methods";

            if (cboMethod.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "Both Methods";

            return "Both Methods";
        }

        private string GetMethodForDatabase()
        {
            string method = GetSelectedMethod();
            return method == "Both Methods" ? "Both" : method;
        }

        private int GetSelectedAreaId()
        {
            if (cboPointCode != null && cboPointCode.SelectedItem is DataRowView rowView)
                return Convert.ToInt32(rowView["Id"]);

            return 0;
        }

        private int GetExpectedPlateCount()
        {
            if (!IsEnvironmentalMode())
                return 0;

            int areaId = GetSelectedAreaId();

            if (areaId <= 0)
                return 0;

            string selectedMethod = GetSelectedMethod();

            // These methods are event-context driven and apply to every active EM area.
            // The operator supplies the actual surface/site or employee on this screen.
            if (selectedMethod.Equals("Contact Plate", StringComparison.OrdinalIgnoreCase) ||
                selectedMethod.Equals("Surface Swab", StringComparison.OrdinalIgnoreCase))
                return 1;

            if (selectedMethod.Equals("Personnel Monitoring", StringComparison.OrdinalIgnoreCase))
                return 2;

            string method = GetMethodForDatabase();

            string sql = @"
                SELECT COUNT(1)
                FROM dbo.EM_AreaTemplates
                WHERE AreaId = @AreaId
                  AND ISNULL(IsActive, 1) = 1
                  AND PlateCode IS NOT NULL
                  AND LTRIM(RTRIM(PlateCode)) <> ''";

            List<SqlParameter> parameters = new List<SqlParameter>
            {
                new SqlParameter("@AreaId", areaId)
            };

            if (!method.Equals("Both", StringComparison.OrdinalIgnoreCase))
            {
                sql += " AND UPPER(LTRIM(RTRIM(Method))) = UPPER(@Method)";
                parameters.Add(new SqlParameter("@Method", method));
            }

            object result = ExecuteScalar(sql, parameters.ToArray());

            int templateCount = result == null || result == DBNull.Value
                ? 0
                : Convert.ToInt32(result);

            if (templateCount > 0)
                return templateCount;

            return 0;
        }

        private void UpdateExpectedPlates()
        {
            if (!IsEnvironmentalMode())
            {
                if (txtExpectedPlates != null)
                    txtExpectedPlates.Text = "";

                return;
            }

            int count = 0;

            try
            {
                count = GetExpectedPlateCount();
            }
            catch
            {
                count = 0;
            }

            if (txtExpectedPlates != null)
                txtExpectedPlates.Text = count.ToString(CultureInfo.InvariantCulture) + " plate(s)";
        }

        private void UpdatePreview()
        {
            if (lblPreviewNumber != null)
                lblPreviewNumber.Text = string.IsNullOrWhiteSpace(txtSampleNo?.Text) ? "-" : txtSampleNo.Text.Trim();

            if (lblPreviewType != null)
                lblPreviewType.Text = GetRegistrationTypeDisplay();

            if (lblPreviewLocation != null)
                lblPreviewLocation.Text = string.IsNullOrWhiteSpace(txtPointDescription?.Text) ? "-" : txtPointDescription.Text.Trim();

            if (lblPreviewMethod != null)
            {
                if (IsEnvironmentalMode())
                    lblPreviewMethod.Text = GetSelectedMethod() + " | Expected: " + (txtExpectedPlates?.Text ?? "");
                else
                    lblPreviewMethod.Text = _selectedTestIds.Count.ToString(CultureInfo.InvariantCulture) + " test(s) selected";
            }

            if (lblPreviewStatus != null)
                lblPreviewStatus.Text = string.IsNullOrWhiteSpace(txtStatus?.Text) ? "-" : txtStatus.Text.Trim();
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClearFormMessage();

                if (!DatabaseHelper.CanRegisterSamples(Login.CurrentUser ?? string.Empty))
                {
                    MessageBox.Show("You do not have permission to register samples.",
                        "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (IsWaterMode() && !IsWaterRegistrationSchemaReady(out string schemaMessage))
                {
                    MessageBox.Show(schemaMessage,
                        "Database Update Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                btnSave.IsEnabled = false;

                if (IsWaterMode())
                    await SaveWaterSample();
                else
                    await SaveEnvironmentalEvent();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnSave.IsEnabled = true;
            }
        }

        private void ShowFormMessage(string message, Control? focusControl = null)
        {
            lblFormMessage.Text = message;
            lblFormMessage.Visibility = Visibility.Visible;

            focusControl?.Focus();
        }

        private void ClearFormMessage()
        {
            lblFormMessage.Text = "";
            lblFormMessage.Visibility = Visibility.Collapsed;
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                btnSave.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                e.Handled = true;
                return;
            }

            WindowUsability.TryMoveFocusOnEnter(e);
        }

        private bool IsWaterRegistrationSchemaReady(out string message)
        {
            string[] requiredColumns =
            {
                "ReceivedBy",
                "ContainerCount",
                "SampleVolume",
                "ContainerCondition",
                "ReceiptTemperature",
                "ReceiptDecision",
                "ReceiptDeviationReason",
                "WaterIncubationProgram",
                "WaterIncubatorID"
            };

            var missingColumns = new List<string>();

            foreach (string columnName in requiredColumns)
            {
                object? result = DatabaseHelper.ExecuteScalar(
                    "SELECT COL_LENGTH(N'dbo.Samples', @ColumnName)",
                    new[] { new SqlParameter("@ColumnName", columnName) });

                if (result == null || result == DBNull.Value)
                    missingColumns.Add(columnName);
            }

            if (missingColumns.Count == 0)
            {
                message = "";
                return true;
            }

            message =
                "The sample-registration database update has not been applied.\n\n" +
                "Missing columns: " + string.Join(", ", missingColumns) + ".\n\n" +
                "Run Database/20260713_SampleRegistrationCompliance.sql once, then try again.";
            return false;
        }

        private async Task SaveWaterSample()
        {
            if (cboPointCode == null || cboPointCode.SelectedItem == null)
            {
                MessageBox.Show("Please select a water sampling point.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_selectedTestIds.Count == 0)
            {
                MessageBox.Show("Please select at least one test.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_isPlanRegistration &&
                (!_waterPlanSampleId.HasValue || _plannedWaterTestIds.Count == 0 || !_selectedTestIds.SetEquals(_plannedWaterTestIds)))
            {
                throw new InvalidOperationException("The selected tests do not exactly match the distributed Water Plan. Registration is blocked; refresh the plan and retry.");
            }

            if (txtSampledBy == null || string.IsNullOrWhiteSpace(txtSampledBy.Text))
            {
                MessageBox.Show("Please enter Sampled By.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(txtContainerCount?.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int containerCount) ||
                containerCount <= 0)
            {
                MessageBox.Show("Container Count must be a whole number greater than zero.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(txtSampleVolume?.Text) ||
                string.IsNullOrWhiteSpace(txtReceiptTemperature?.Text) ||
                string.IsNullOrWhiteSpace(txtReceivedBy?.Text))
            {
                MessageBox.Show("Sample Volume, Receipt Temperature, and Received By are required.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string containerCondition = GetComboSelectionText(cboContainerCondition);
            string receiptDecision = GetComboSelectionText(cboReceiptDecision);
            string receiptReason = txtReceiptDeviationReason?.Text.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(containerCondition) || string.IsNullOrWhiteSpace(receiptDecision))
            {
                MessageBox.Show("Container Condition and Receipt Decision are required.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (receiptDecision.Equals("Rejected", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(receiptReason))
            {
                MessageBox.Show("Deviation / Rejection Reason is required for a rejected sample.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime samplingDateTime = GetSelectedSamplingDateTime();
            DateTime receivedDateTime = GetReceivedInLabDateTime();
            bool sampleAccepted = receiptDecision.Equals("Accepted", StringComparison.OrdinalIgnoreCase);
            bool requiresIncubation = sampleAccepted && HasSelectedMicrobiologicalTest();
            string registrationStatus = sampleAccepted
                ? (requiresIncubation ? "Incubation" : "Registered")
                : "Rejected";

            if (requiresIncubation && string.IsNullOrWhiteSpace(txtWaterIncubatorId?.Text))
            {
                MessageBox.Show("Incubator ID is required when microbiological water tests are selected.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DateTime? incubationStartedDateTime = requiresIncubation
                ? GetIncubationStartedDateTime()
                : null;

            if (receivedDateTime < samplingDateTime)
                throw new InvalidOperationException("Received in Lab Date / Time cannot be earlier than Sampling Date / Time.");

            if (incubationStartedDateTime.HasValue)
                ValidateWaterDateSequence(samplingDateTime, receivedDateTime, incubationStartedDateTime.GetValueOrDefault());

            DataRowView pointRow = (DataRowView)cboPointCode.SelectedItem;
            int pointId = Convert.ToInt32(pointRow["Id"]);
            string pointCode = pointRow["Code"]?.ToString() ?? "";
            string pointDescription = pointRow["DescriptionText"]?.ToString() ?? "";

            string sampleType = IsPurifiedWaterMode()
                ? "Purified Water"
                : "Potable Water";

            string finalSampleNumber = GetNextWaterSampleNumber();

            if (string.IsNullOrWhiteSpace(finalSampleNumber))
                throw new InvalidOperationException("Failed to generate a water sample number.");

            if (txtSampleNo != null)
                txtSampleNo.Text = finalSampleNumber;

            ElectronicSignature? registrationSignature = ConfirmRegistrationSignature(
                finalSampleNumber,
                "Water Sample Registration");

            if (registrationSignature == null)
            {
                DatabaseHelper.AddAuditTrailAdvanced("LIMS_NumberSequences", 0, "Water Sample Number Voided", finalSampleNumber, "Voided",
                    "Registration electronic signature was cancelled before sample creation", GetCurrentDisplayUser(), "SampleNumber", "", finalSampleNumber, "Water");
                return;
            }

            int sampleId = 0;
            int insertedTests = 0;
            var testSnapshots = new Dictionary<int, (string Name, string Category, string Unit, decimal? Alert, decimal? Action, string Specification)>();
            foreach (int testId in _selectedTestIds)
            {
                DataTable master = ExecuteDataTable(@"SELECT TestName,ISNULL(TestCategory,N'') TestCategory,ISNULL(Unit,N'') Unit,AlertLimit,ActionLimit FROM dbo.Tests WHERE TestID=@TestID",
                    new SqlParameter("@TestID", testId));
                if (master.Rows.Count == 0) throw new InvalidOperationException("Selected water test no longer exists: " + testId.ToString(CultureInfo.InvariantCulture));
                DataRow m = master.Rows[0];
                decimal? alert = null;
                decimal? action = null;
                string specificationText = string.Empty;
                DataTable effective = DatabaseHelper.GetEffectiveWaterTestSpecification(sampleType, testId, pointCode);
                if (effective.Rows.Count > 0)
                {
                    DataRow specification = effective.Rows[0];
                    alert = specification["AlertLimit"] != DBNull.Value
                        ? Convert.ToDecimal(specification["AlertLimit"], CultureInfo.InvariantCulture)
                        : specification["LowerLimit"] != DBNull.Value
                            ? Convert.ToDecimal(specification["LowerLimit"], CultureInfo.InvariantCulture)
                            : null;
                    action = specification["ActionLimit"] != DBNull.Value
                        ? Convert.ToDecimal(specification["ActionLimit"], CultureInfo.InvariantCulture)
                        : specification["UpperLimit"] != DBNull.Value
                            ? Convert.ToDecimal(specification["UpperLimit"], CultureInfo.InvariantCulture)
                            : null;
                    specificationText = specification["SpecificationText"]?.ToString()?.Trim() ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(specificationText))
                    throw new InvalidOperationException("Selected water test has no effective approved specification text: " + (m["TestName"]?.ToString() ?? testId.ToString(CultureInfo.InvariantCulture)) + ".");

                testSnapshots[testId] = (m["TestName"]?.ToString() ?? "", m["TestCategory"]?.ToString() ?? "", m["Unit"]?.ToString() ?? "", alert, action, specificationText);
            }

            using (SqlConnection con = new SqlConnection(AppConfig.ConnectionString))
            {
                await con.OpenAsync();

                using (SqlTransaction tran = con.BeginTransaction())
                {
                    string registrationStage = "authorization";
                    try
                    {
                        DatabaseHelper.EnsureUserPermissionInTransaction(
                            con, tran, registrationSignature.SignedBy, "CanRegisterSamples", "register water samples");

                        registrationStage = "authoritative timestamp validation";
                        DateTime databaseNow;
                        using (SqlCommand clockCommand = new SqlCommand("SELECT SYSDATETIME();", con, tran))
                        {
                            clockCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            object? databaseTimeValue = await clockCommand.ExecuteScalarAsync();
                            if (databaseTimeValue == null || databaseTimeValue == DBNull.Value)
                                throw new InvalidOperationException("The authoritative database timestamp could not be read.");
                            databaseNow = Convert.ToDateTime(databaseTimeValue, CultureInfo.InvariantCulture);
                        }
                        ValidateWaterManualTimestampsAgainstDatabaseTime(
                            samplingDateTime, receivedDateTime, incubationStartedDateTime, databaseNow);

                        int? controlledWaterPlanId = null;
                        string controlledPlanSampleOldStatus = string.Empty;
                        if (_isPlanRegistration)
                        {
                            registrationStage = "controlled Water Plan validation";
                            using SqlCommand planLock = new SqlCommand(@"
SELECT ps.WaterPlanID,ps.PointID,ps.Status AS SampleStatus,p.Status AS PlanStatus
FROM dbo.Water_PlanSamples ps WITH(UPDLOCK,HOLDLOCK)
INNER JOIN dbo.Water_Plans p WITH(UPDLOCK,HOLDLOCK) ON p.WaterPlanID=ps.WaterPlanID
WHERE ps.WaterPlanSampleID=@PlanSampleID
  AND (ps.SampleID IS NULL OR ps.Status=N'Rejected');", con, tran);
                            planLock.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            planLock.Parameters.Add("@PlanSampleID", SqlDbType.Int).Value = _waterPlanSampleId!.Value;
                            using SqlDataReader planReader = await planLock.ExecuteReaderAsync();
                            if (!await planReader.ReadAsync())
                                throw new DBConcurrencyException("The Water Plan row was already linked or changed. Refresh and retry.");
                            controlledWaterPlanId = planReader.GetInt32(0);
                            int controlledPointId = planReader.GetInt32(1);
                            controlledPlanSampleOldStatus = planReader.IsDBNull(2) ? string.Empty : planReader.GetString(2);
                            string controlledPlanStatus = planReader.IsDBNull(3) ? string.Empty : planReader.GetString(3);
                            if (controlledPointId != pointId)
                                throw new DBConcurrencyException("The Water Plan point changed before registration. Refresh and retry.");
                            if (!controlledPlanStatus.Equals("Distributed", StringComparison.OrdinalIgnoreCase) &&
                                !controlledPlanStatus.Equals("In Collection", StringComparison.OrdinalIgnoreCase) &&
                                !controlledPlanStatus.Equals("Recollection Required", StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("The Water Plan is no longer available for registration.");
                            planReader.Close();

                            var lockedPlanTests = new HashSet<int>();
                            using SqlCommand testLock = new SqlCommand(@"
SELECT TestID FROM dbo.Water_PlanSampleTests WITH(HOLDLOCK)
WHERE WaterPlanSampleID=@PlanSampleID ORDER BY TestID;", con, tran);
                            testLock.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            testLock.Parameters.Add("@PlanSampleID", SqlDbType.Int).Value = _waterPlanSampleId.Value;
                            using SqlDataReader testReader = await testLock.ExecuteReaderAsync();
                            while (await testReader.ReadAsync()) lockedPlanTests.Add(testReader.GetInt32(0));
                            testReader.Close();
                            if (lockedPlanTests.Count == 0 || !_selectedTestIds.SetEquals(lockedPlanTests))
                                throw new DBConcurrencyException("The Water Plan test assignment changed after the registration window opened. No sample was created; refresh and retry.");
                        }

                        registrationStage = "water sample insert";
                        using (SqlCommand cmd = new SqlCommand(@"
                            INSERT INTO dbo.Samples
                            (
                                SampleNumber,
                                PointID,
                                PointCodeSnapshot,
                                PointNameSnapshot,
                                PointLocationSnapshot,
                                SampleType,
                                SamplingMethod,
                                SamplingDateTime,
                                ReceivedDateTime,
                                IncubationStartedDateTime,
                                SampledBy,
                                ReceivedBy,
                                ContainerCount,
                                SampleVolume,
                                ContainerCondition,
                                ReceiptTemperature,
                                ReceiptDecision,
                                ReceiptDeviationReason,
                                WaterIncubationProgram,
                                WaterIncubatorID,
                                IncubationEndDate,
                                Status,
                                LabelPrinted,
                                CreatedDate
                            )
                            OUTPUT INSERTED.SampleID
                            VALUES
                            (
                                @SampleNumber,
                                @PointID,
                                @PointCodeSnapshot,
                                @PointNameSnapshot,
                                @PointLocationSnapshot,
                                @SampleType,
                                @SamplingMethod,
                                @SamplingDateTime,
                                @ReceivedDateTime,
                                @IncubationStartedDateTime,
                                @SampledBy,
                                @ReceivedBy,
                                @ContainerCount,
                                @SampleVolume,
                                @ContainerCondition,
                                @ReceiptTemperature,
                                @ReceiptDecision,
                                @ReceiptDeviationReason,
                                @WaterIncubationProgram,
                                @WaterIncubatorID,
                                @IncubationEndDate,
                                @Status,
                                @LabelPrinted,
                                GETDATE()
                            );", con, tran))
                        {
                            cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            cmd.Parameters.AddExplicit("@SampleNumber", SqlDbType.NVarChar, finalSampleNumber, size: 100);
                            cmd.Parameters.AddExplicit("@PointID", SqlDbType.Int, pointId);
                            cmd.Parameters.AddExplicit("@PointCodeSnapshot", SqlDbType.NVarChar, pointCode, size: 100);
                            cmd.Parameters.AddExplicit("@PointNameSnapshot", SqlDbType.NVarChar, pointRow["DescriptionText"]?.ToString() ?? "", size: 200);
                            cmd.Parameters.AddExplicit("@PointLocationSnapshot", SqlDbType.NVarChar, pointRow["LocationText"]?.ToString() ?? "", size: 200);
                            cmd.Parameters.AddExplicit("@SampleType", SqlDbType.NVarChar, sampleType, size: 50);
                            cmd.Parameters.AddExplicit("@SamplingMethod", SqlDbType.NVarChar, "N/A", size: 50);
                            cmd.Parameters.AddExplicit("@SamplingDateTime", SqlDbType.DateTime2, samplingDateTime);
                            cmd.Parameters.AddExplicit("@ReceivedDateTime", SqlDbType.DateTime2, receivedDateTime);
                            cmd.Parameters.AddExplicit("@IncubationStartedDateTime", SqlDbType.DateTime2, incubationStartedDateTime.HasValue ? (object)incubationStartedDateTime.GetValueOrDefault() : DBNull.Value);
                            cmd.Parameters.AddExplicit("@SampledBy", SqlDbType.NVarChar, txtSampledBy.Text.Trim(), size: 100);
                            cmd.Parameters.AddExplicit("@ReceivedBy", SqlDbType.NVarChar, txtReceivedBy.Text.Trim(), size: 100);
                            cmd.Parameters.AddExplicit("@ContainerCount", SqlDbType.Int, containerCount);
                            cmd.Parameters.AddExplicit("@SampleVolume", SqlDbType.NVarChar, txtSampleVolume.Text.Trim(), size: 50);
                            cmd.Parameters.AddExplicit("@ContainerCondition", SqlDbType.NVarChar, containerCondition, size: 100);
                            cmd.Parameters.AddExplicit("@ReceiptTemperature", SqlDbType.NVarChar, txtReceiptTemperature.Text.Trim(), size: 50);
                            cmd.Parameters.AddExplicit("@ReceiptDecision", SqlDbType.NVarChar, receiptDecision, size: 50);
                            cmd.Parameters.AddExplicit("@ReceiptDeviationReason", SqlDbType.NVarChar, DbValue(receiptReason), size: -1);
                            cmd.Parameters.AddExplicit("@WaterIncubationProgram", SqlDbType.NVarChar, requiresIncubation ? GetComboSelectionText(cboWaterIncubationProgram) : (object)DBNull.Value, size: 100);
                            cmd.Parameters.AddExplicit("@WaterIncubatorID", SqlDbType.NVarChar, requiresIncubation ? txtWaterIncubatorId.Text.Trim() : (object)DBNull.Value, size: 100);
                            cmd.Parameters.AddExplicit("@IncubationEndDate", SqlDbType.DateTime2, requiresIncubation && incubationStartedDateTime.HasValue
                                    ? (object)incubationStartedDateTime.GetValueOrDefault().AddDays(GetIncubationDays())
                                    : DBNull.Value);
                            cmd.Parameters.AddExplicit("@Status", SqlDbType.NVarChar, registrationStatus, size: 50);
                            cmd.Parameters.AddExplicit("@LabelPrinted", SqlDbType.Bit, false);

                            object? result = await cmd.ExecuteScalarAsync();

                            if (result == null || result == DBNull.Value)
                                throw new InvalidOperationException("Sample insert did not return a SampleID. Registration was cancelled.");

                            sampleId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                        }

                        registrationStage = "controlled test snapshot insert";
                        foreach (int testId in _selectedTestIds.OrderBy(id => id))
                        {
                            using (SqlCommand cmdTest = new SqlCommand(@"
                                INSERT INTO dbo.SampleTests
                                (SampleID,TestID,TestNameSnapshot,TestCategorySnapshot,UnitSnapshot,AlertLimitSnapshot,ActionLimitSnapshot,LimitDescription)
                                VALUES(@SampleID,@TestID,@TestName,@Category,@Unit,@Alert,@Action,@LimitDescription);", con, tran))
                            {
                                cmdTest.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                                cmdTest.Parameters.AddExplicit("@SampleID", SqlDbType.Int, sampleId);
                                cmdTest.Parameters.AddExplicit("@TestID", SqlDbType.Int, testId);
                                var snapshot = testSnapshots[testId];
                                cmdTest.Parameters.AddExplicit("@TestName", SqlDbType.NVarChar, snapshot.Name, size: 200);
                                cmdTest.Parameters.AddExplicit("@Category", SqlDbType.NVarChar, snapshot.Category, size: 100);
                                cmdTest.Parameters.AddExplicit("@Unit", SqlDbType.NVarChar, snapshot.Unit, size: 50);
                                cmdTest.Parameters.AddExplicit("@Alert", SqlDbType.Decimal, snapshot.Alert.HasValue ? (object)snapshot.Alert.Value : DBNull.Value, precision: 18, scale: 6);
                                cmdTest.Parameters.AddExplicit("@Action", SqlDbType.Decimal, snapshot.Action.HasValue ? (object)snapshot.Action.Value : DBNull.Value, precision: 18, scale: 6);
                                cmdTest.Parameters.AddExplicit("@LimitDescription", SqlDbType.NVarChar, snapshot.Specification, size: 500);
                                await cmdTest.ExecuteNonQueryAsync();
                                insertedTests++;
                            }
                        }

                        if (insertedTests <= 0)
                            throw new InvalidOperationException("Sample was created but no tests were inserted. Registration was cancelled.");

                        if (_isPlanRegistration)
                        {
                            registrationStage = "Water Plan sample link";
                            string linkedStatus = registrationStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ? "Rejected" : "Registered";
                            using SqlCommand link = new SqlCommand(@"
INSERT dbo.Water_PlanSampleAttempts(WaterPlanSampleID,SampleID,SampleNumber,Outcome,LinkedBy)
VALUES(@PlanSampleID,@SampleID,@SampleNumber,@Outcome,@User);
UPDATE dbo.Water_PlanSamples
SET SampleID=@SampleID,SampleNumber=@SampleNumber,RegisteredAt=SYSDATETIME(),Status=@Outcome
WHERE WaterPlanSampleID=@PlanSampleID AND (SampleID IS NULL OR Status=N'Rejected');
SELECT @@ROWCOUNT;", con, tran);
                            link.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            link.Parameters.Add("@PlanSampleID", SqlDbType.Int).Value = _waterPlanSampleId!.Value;
                            link.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                            link.Parameters.Add("@SampleNumber", SqlDbType.NVarChar, 50).Value = finalSampleNumber;
                            link.Parameters.Add("@Outcome", SqlDbType.NVarChar, 40).Value = linkedStatus;
                            link.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = registrationSignature.SignedBy;
                            int linkedRows = Convert.ToInt32(await link.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                            if (linkedRows != 1)
                                throw new DBConcurrencyException("The Water Plan row changed before linkage. The entire sample registration was rolled back.");

                            using SqlCommand planStatus = new SqlCommand(@"
UPDATE dbo.Water_Plans
SET Status=CASE
 WHEN EXISTS(SELECT 1 FROM dbo.Water_PlanSamples WHERE WaterPlanID=@PlanID AND Status=N'Rejected') THEN N'Recollection Required'
 WHEN NOT EXISTS(SELECT 1 FROM dbo.Water_PlanSamples WHERE WaterPlanID=@PlanID AND Status<>N'Registered') THEN N'Registered'
 ELSE N'In Collection' END
WHERE WaterPlanID=@PlanID;", con, tran);
                            planStatus.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            planStatus.Parameters.Add("@PlanID", SqlDbType.Int).Value = controlledWaterPlanId!.Value;
                            await planStatus.ExecuteNonQueryAsync();

                            DatabaseHelper.AddAuditTrailAdvanced(
                                con, tran, "Water_PlanSamples", _waterPlanSampleId.Value, "Planned Water Sample Linked",
                                controlledPlanSampleOldStatus, linkedStatus, "Sample registration and Water Plan linkage committed atomically",
                                registrationSignature.SignedBy, "SampleID", null, finalSampleNumber, "Water");
                        }

                        registrationStage = "electronic signature persistence";
                        InsertSampleRegistrationSignature(
                            con,
                            tran,
                            sampleId,
                            registrationSignature,
                            "Water Sample Registration");

                        registrationStage = "registration audit trail";
                        DatabaseHelper.AddAuditTrailAdvanced(
                            con,
                            tran,
                            "Samples",
                            sampleId,
                            "Water Sample Registered",
                            "",
                            registrationStatus,
                            registrationSignature.Reason,
                            registrationSignature.SignedBy,
                            "Status",
                            "",
                            finalSampleNumber,
                            "Water");

                        tran.Commit();
                    }
                    catch (Exception saveException)
                    {
                        tran.Rollback();
                        try
                        {
                            DatabaseHelper.AddAuditTrailAdvanced("LIMS_NumberSequences", 0, "Water Sample Number Voided", finalSampleNumber, "Voided",
                                "Water registration transaction failed: " + saveException.GetBaseException().Message, registrationSignature.SignedBy,
                                "SampleNumber", "", finalSampleNumber, "Water");
                        }
                        catch (Exception auditException)
                        {
                            ApplicationLogger.Warning(
                                "Unable to record the voided water sample number after registration rollback.",
                                auditException);
                        }
                        if (saveException is SqlException sqlException && (sqlException.Number == 2601 || sqlException.Number == 2627))
                        {
                            throw new InvalidOperationException(
                                "Water sample registration was blocked because a generated sample number or controlled registration record already exists. Refresh the registration window and retry once.",
                                saveException);
                        }

                        if (saveException is SqlException)
                        {
                            throw new InvalidOperationException(
                                "Water sample registration failed during " + registrationStage + ". No data were committed.",
                                saveException);
                        }

                        throw;
                    }
                }
            }

            MessageBox.Show(
                "Water sample registered successfully!\n\n" +
                "Sample No: " + finalSampleNumber + "\n" +
                "Sample Point No: " + pointCode + "\n" +
                "Point: " + pointDescription + "\n" +
                "Sampling Date/Time: " + samplingDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "\n" +
                "Status: " + registrationStatus + "\n" +
                "Tests Inserted: " + insertedTests.ToString(CultureInfo.InvariantCulture),
                "Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            MessageBoxResult printLabel = MessageBox.Show(
                "Do you want to preview and print the water sample label now?",
                "Water Sample Label",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (printLabel == MessageBoxResult.Yes)
            {
                bool printed = LabelPrintingService.PrintWaterSampleLabel(
                    this,
                    finalSampleNumber,
                    sampleType,
                    pointCode + " - " + pointDescription,
                    samplingDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    txtSampledBy.Text.Trim(),
                    registrationStatus);

                if (printed)
                {
                    DatabaseHelper.ExecuteNonQuery(
                        "UPDATE dbo.Samples SET LabelPrinted = 1 WHERE SampleID = @SampleID",
                        new[] { new SqlParameter("@SampleID", sampleId) });

                    DatabaseHelper.AddAuditTrailAdvanced(
                        "Samples",
                        sampleId,
                        "Water Sample Label Printed",
                        "Not Printed",
                        "Printed",
                        "Label printed after registration",
                        registrationSignature.SignedBy,
                        "LabelPrinted",
                        "",
                        finalSampleNumber,
                        "Water");
                }
            }

            _isDirty = false;
            RegisteredSampleId = sampleId;
            RegisteredSampleNumber = finalSampleNumber;
            RegisteredSampleStatus = registrationStatus;
            if (_isPlanRegistration)
                DialogResult = true;
            else
                Close();
        }

        private ElectronicSignature? ConfirmRegistrationSignature(string recordNumber, string actionType)
        {
            var signatureWindow = new ElectronicSignature(_authService);
            signatureWindow.Configure(recordNumber, Login.CurrentUser, actionType, true);
            signatureWindow.Owner = this;

            return signatureWindow.ShowDialog() == true && signatureWindow.IsConfirmed
                ? signatureWindow
                : null;
        }

        private static void InsertSampleRegistrationSignature(
            SqlConnection con,
            SqlTransaction tran,
            int sampleId,
            ElectronicSignature signature,
            string actionType)
        {
            using SqlCommand cmd = new SqlCommand(@"
                INSERT INTO dbo.ElectronicSignatures
                (
                    SampleID,
                    ActionType,
                    ActionReason,
                    SignedBy,
                    MeaningOfSignature,
                    UserRole,
                    SignedAt
                )
                VALUES
                (
                    @SampleID,
                    @ActionType,
                    @ActionReason,
                    @SignedBy,
                    @Meaning,
                    @UserRole,
                    GETDATE()
                );", con, tran);

            cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            cmd.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            cmd.Parameters.Add("@ActionType", SqlDbType.NVarChar, 100).Value = actionType;
            cmd.Parameters.Add("@ActionReason", SqlDbType.NVarChar, -1).Value = signature.Reason;
            cmd.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
            cmd.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning;
            cmd.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(Login.CurrentUserRole) ? "Unknown" : Login.CurrentUserRole;
            cmd.ExecuteNonQuery();
        }


        private bool TryParseEmTime(string? timeText, string fieldName, out TimeSpan timeValue)
        {
            timeValue = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(timeText))
            {
                MessageBox.Show(fieldName + " is required. Use HH:mm format, for example 09:20.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string trimmed = timeText.Trim();

            if (TimeSpan.TryParseExact(trimmed, @"hh\:mm", CultureInfo.InvariantCulture, out timeValue))
                return true;

            if (TimeSpan.TryParse(trimmed, CultureInfo.InvariantCulture, out timeValue))
                return true;

            MessageBox.Show(fieldName + " is invalid. Use HH:mm format, for example 09:20.",
                "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        private bool ValidateEnvironmentalRegistrationInputs()
        {
            if (cboPointCode == null || cboPointCode.SelectedItem == null)
            {
                MessageBox.Show("Please select an environmental monitoring area.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!IsSelectedMethodAllowedForArea(out string methodRestriction))
            {
                MessageBox.Show(methodRestriction, "Method Not Allowed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            int expectedPlates = GetExpectedPlateCount();
            if (expectedPlates <= 0)
            {
                MessageBox.Show(
                    "No active EM templates are defined for the selected area and method.\n\nPlease configure EM area templates before registration.",
                    "No EM Plate Templates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            string method = GetSelectedMethod();
            bool exposureRangeMethod = method == "Settle Plate" || method == "Both Methods";

            if (!TryParseEmTime(txtSamplingTimeFrom?.Text,
                    exposureRangeMethod ? "Exposure Start Time" : "Sampling Time", out TimeSpan fromTime))
                return false;

            TimeSpan toTime = default;
            if (exposureRangeMethod &&
                !TryParseEmTime(txtSamplingTimeTo?.Text, "Exposure End Time", out toTime))
                return false;

            if (exposureRangeMethod && toTime <= fromTime)
            {
                MessageBox.Show("Exposure End Time must be after Exposure Start Time.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(txtSanitizationDetails?.Text) &&
                string.IsNullOrWhiteSpace(txtSanitizationTime?.Text))
            {
                MessageBox.Show("Sanitization Time is required when Sanitization Details are entered.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(txtSanitizationTime?.Text) &&
                !TryParseEmTime(txtSanitizationTime.Text, "Sanitization Time", out _))
                return false;

            if ((method == "Active Air Sampling" || method == "Both Methods") &&
                string.IsNullOrWhiteSpace(txtAirSamplerNo?.Text))
            {
                MessageBox.Show("Air Sampler No. is required for Active Air Sampling.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (method == "Personnel Monitoring")
            {
                if (string.IsNullOrWhiteSpace(txtEmployeeId?.Text) ||
                    string.IsNullOrWhiteSpace(txtEmployeeName?.Text))
                {
                    MessageBox.Show("Employee ID and Employee Name are required for Personnel Monitoring.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (method == "Surface Swab" || method == "Contact Plate")
            {
                if (string.IsNullOrWhiteSpace(txtSurfaceLocation?.Text))
                {
                    MessageBox.Show("Surface / Sample Site is required for surface monitoring.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                string surfaceType = (cboSurfaceType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(surfaceType))
                {
                    MessageBox.Show("Surface Type is required for Contact Plate and Surface Swab.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (method == "Contact Plate" && !surfaceType.Equals("Flat Surface", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Contact Plate is intended for flat surfaces. Select Flat Surface or use Surface Swab.",
                        "Method Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (method == "Surface Swab" && surfaceType.Equals("Flat Surface", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Use Contact Plate for accessible flat surfaces. Surface Swab is for curved, uneven, or machine internal surfaces.",
                        "Method Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!decimal.TryParse(txtSurfaceAreaCm2?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal surfaceArea) || surfaceArea <= 0)
                {
                    MessageBox.Show("Surface Area must be a positive number in cm².",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (method == "Surface Swab")
            {
                if (string.IsNullOrWhiteSpace(txtSwabKitLot?.Text))
                {
                    MessageBox.Show("Swab Kit Lot is required for Surface Swab.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (!decimal.TryParse(txtRecoveryVolumeMl?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal recoveryVolume) || recoveryVolume <= 0)
                {
                    MessageBox.Show("Recovery Volume must be a positive number in mL.",
                        "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(GetSelectedActivity()))
            {
                MessageBox.Show("Activity / Condition is required.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtMediaUsed?.Text))
            {
                MessageBox.Show("Please enter Media Used.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtIncubationTemp?.Text))
            {
                MessageBox.Show("Incubation Program is required.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtBacteriaIncubator?.Text) ||
                string.IsNullOrWhiteSpace(txtFungiIncubator?.Text))
            {
                MessageBox.Show("Bacteria Incubator ID and Fungi Incubator ID are required.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            _validatedEmMediaPreparationId = 0;
            string mediaLotNo = txtMediaLotNo?.Text.Trim() ?? "";
            string airSamplerNo = txtAirSamplerNo?.Text.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(mediaLotNo))
            {
                MessageBox.Show("Please enter Released Media Preparation No.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (!string.IsNullOrWhiteSpace(airSamplerNo) &&
                string.Equals(mediaLotNo, airSamplerNo, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Released Media Preparation No. cannot be the same as Air Sampler No.\n\n" +
                    "Air Sampler No. identifies the equipment.\n" +
                    "Released Media Preparation No. must identify the approved prepared-media batch.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (mediaLotNo.StartsWith("MQC-I-", StringComparison.OrdinalIgnoreCase) ||
                mediaLotNo.StartsWith("E -", StringComparison.OrdinalIgnoreCase) ||
                mediaLotNo.StartsWith("E-", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Released Media Preparation No. looks like an equipment/instrument number.\n\n" +
                    "Please enter the released prepared-media number, not the Air Sampler No. or incubator ID.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (!IsReleasedPreparedMedia(mediaLotNo, out int mediaPreparationId, out string mediaReleaseGateMessage))
            {
                ShowFormMessage(mediaReleaseGateMessage, txtMediaLotNo);
                return false;
            }
            _validatedEmMediaPreparationId = mediaPreparationId;

            return true;
        }

        private bool IsReleasedPreparedMedia(string mediaPreparationNo, out int mediaPreparationId, out string gateMessage)
        {
            mediaPreparationId = 0;
            DataTable table = ExecuteDataTable(@"
SELECT TOP (1)
    p.MediaPreparationID,
    p.MediaPreparationNo,
    p.ReleaseStatus,
    p.SterilityReview,
    p.ExpiryDate
FROM dbo.MediaPreparations p
WHERE UPPER(LTRIM(RTRIM(p.MediaPreparationNo))) = UPPER(LTRIM(RTRIM(@MediaPreparationNo)))
ORDER BY p.MediaPreparationID DESC;",
                new SqlParameter("@MediaPreparationNo", SqlDbType.NVarChar, 100) { Value = mediaPreparationNo });

            if (table.Rows.Count == 0)
            {
                gateMessage =
                    $"Media Release Gate: prepared-media number {mediaPreparationNo} was not found. " +
                    "Select a released preparation from Culture Media Management; do not enter the dehydrated-media lot number.";
                return false;
            }

            DataRow row = table.Rows[0];
            string preparationNo = row["MediaPreparationNo"] == DBNull.Value
                ? "Not assigned"
                : row["MediaPreparationNo"].ToString()?.Trim() ?? "Not assigned";
            string releaseStatus = row["ReleaseStatus"] == DBNull.Value
                ? "Not set"
                : row["ReleaseStatus"].ToString()?.Trim() ?? "Not set";
            string sterilityReview = row["SterilityReview"] == DBNull.Value
                ? "Not set"
                : row["SterilityReview"].ToString()?.Trim() ?? "Not set";
            DateTime? expiryDate = row["ExpiryDate"] == DBNull.Value
                ? null
                : Convert.ToDateTime(row["ExpiryDate"], CultureInfo.InvariantCulture).Date;

            if (!releaseStatus.Equals("Released", StringComparison.OrdinalIgnoreCase) ||
                !sterilityReview.Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
                !expiryDate.HasValue || expiryDate.Value < DateTime.Today)
            {
                gateMessage =
                    $"Media Release Gate: preparation {preparationNo} has Release Status = {releaseStatus}, " +
                    $"Sterility Review = {sterilityReview}, and Use-Before Date = " +
                    (expiryDate.HasValue ? expiryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "Not set") + ". " +
                    "Only a released, sterility-approved, unexpired prepared-media batch can be used for EM.";
                return false;
            }

            mediaPreparationId = Convert.ToInt32(row["MediaPreparationID"], CultureInfo.InvariantCulture);
            gateMessage = string.Empty;
            return true;
        }

        private async Task SaveEnvironmentalEvent()
        {
            if (AppConfig.IsProduction)
            {
                MessageBox.Show(
                    "Direct EM event registration is disabled in Production. Use EM Planning so collection, negative control, two-phase incubation, and release to Results Entry remain controlled and traceable.",
                    "Controlled EM Workflow Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!ValidateEnvironmentalRegistrationInputs())
                return;

            if (txtSampledBy == null || string.IsNullOrWhiteSpace(txtSampledBy.Text))
            {
                MessageBox.Show("Please enter Sampled By.",
                    "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string method = GetSelectedMethod();

            DateTime samplingDateTime = GetSelectedSamplingDateTime();

            int expectedPlates = GetExpectedPlateCount();

            DataRowView areaRow = (DataRowView)cboPointCode.SelectedItem;
            int areaId = Convert.ToInt32(areaRow["Id"]);

            DateTime incubationEnd = ParseIncubationEnd();

            string methodForDb = GetMethodForDatabase();

            ElectronicSignature? registrationSignature = ConfirmRegistrationSignature(
                "New EM Event",
                "Environmental Monitoring Registration");

            if (registrationSignature == null)
                return;

            int eventId;
            int platesCreated;
            string finalEventNumber = "";

            using (SqlConnection con = new SqlConnection(AppConfig.ConnectionString))
            {
                await con.OpenAsync();

                using SqlTransaction tran = con.BeginTransaction(IsolationLevel.Serializable);

                try
                {
                    finalEventNumber = DatabaseHelper.GetNextEMEventNumber(con, tran);

                    if (string.IsNullOrWhiteSpace(finalEventNumber))
                        throw new InvalidOperationException("Failed to generate an environmental monitoring event number.");

                    using (SqlCommand eventCommand = new SqlCommand(@"
                        INSERT INTO dbo.EM_Events
                        (
                            EventNo, AreaId, EventDate, AreaCodeSnapshot, AreaNameSnapshot, GradeSnapshot, AreaSnapshotSource, ARNo,
                            SanitizationDetails, SanitizationTime, DisinfectantUsed,
                            MediaUsed, MediaLotNo, MediaPreparationID, SamplingTimeFrom, SamplingTimeTo,
                            ActivityNoOfPersons, AirSamplerNo, AirSamplingTime,
                            IncubationTemperature, IncubatorNo1, IncubatorNo2,
                            IncubationStart, IncubationEnd, FinalResult, Remarks,
                            MonitoringCategory, DispensingBooth, MaterialName, BatchNo,
                            EmployeeId, EmployeeName, EmployeeDepartment, EmployeeShift,
                            SamplingStage, SurfaceLocation, SurfaceType, SurfaceAreaCm2,
                            SwabKitLot, DiluentLot, RecoveryVolumeMl, CreatedAt
                        )
                        OUTPUT INSERTED.Id
                        VALUES
                        (
                            @EventNo, @AreaId, @EventDate, @AreaCodeSnapshot, @AreaNameSnapshot, @GradeSnapshot, N'Native event creation', NULL,
                            @SanitizationDetails, @SanitizationTime, NULL,
                            @MediaUsed, @MediaLotNo, @MediaPreparationID, @SamplingTimeFrom, @SamplingTimeTo,
                            @Activity, @AirSamplerNo, NULL,
                            @IncubationProgram, @BacteriaIncubator, @FungiIncubator,
                            @IncubationStart, @IncubationEnd, 'Pending', @Remarks,
                            @MonitoringCategory, @DispensingBooth, @MaterialName, @BatchNo,
                            @EmployeeId, @EmployeeName, @EmployeeDepartment, @EmployeeShift,
                            @SamplingStage, @SurfaceLocation, @SurfaceType, @SurfaceAreaCm2,
                            @SwabKitLot, @DiluentLot, @RecoveryVolumeMl, GETDATE()
                        );", con, tran))
                    {
                        eventCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        eventCommand.Parameters.Add("@EventNo", SqlDbType.NVarChar, 100).Value = finalEventNumber;
                        eventCommand.Parameters.Add("@AreaId", SqlDbType.Int).Value = areaId;
                        eventCommand.Parameters.Add("@EventDate", SqlDbType.DateTime).Value = samplingDateTime;
                        eventCommand.Parameters.Add("@AreaCodeSnapshot", SqlDbType.NVarChar, 100).Value = Convert.ToString(areaRow["Code"], CultureInfo.InvariantCulture) ?? string.Empty;
                        eventCommand.Parameters.Add("@AreaNameSnapshot", SqlDbType.NVarChar, 200).Value = Convert.ToString(areaRow["LocationText"], CultureInfo.InvariantCulture) ?? string.Empty;
                        eventCommand.Parameters.Add("@GradeSnapshot", SqlDbType.NVarChar, 100).Value = Convert.ToString(areaRow["Grade"], CultureInfo.InvariantCulture) ?? string.Empty;
                        eventCommand.Parameters.Add("@SanitizationDetails", SqlDbType.NVarChar, -1).Value = DbValue(txtSanitizationDetails?.Text);
                        eventCommand.Parameters.Add("@SanitizationTime", SqlDbType.NVarChar, 50).Value = DbValue(txtSanitizationTime?.Text);
                        eventCommand.Parameters.Add("@MediaUsed", SqlDbType.NVarChar, 200).Value = txtMediaUsed.Text.Trim();
                        eventCommand.Parameters.Add("@MediaLotNo", SqlDbType.NVarChar, 100).Value = txtMediaLotNo.Text.Trim();
                        eventCommand.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = _validatedEmMediaPreparationId;
                        eventCommand.Parameters.Add("@SamplingTimeFrom", SqlDbType.NVarChar, 50).Value = txtSamplingTimeFrom.Text.Trim();
                        bool exposureRangeMethod = method == "Settle Plate" || method == "Both Methods";
                        eventCommand.Parameters.Add("@SamplingTimeTo", SqlDbType.NVarChar, 50).Value =
                            exposureRangeMethod ? txtSamplingTimeTo.Text.Trim() : DBNull.Value;
                        eventCommand.Parameters.Add("@Activity", SqlDbType.NVarChar, 200).Value = GetSelectedActivity();
                        eventCommand.Parameters.Add("@AirSamplerNo", SqlDbType.NVarChar, 100).Value = DbValue(txtAirSamplerNo?.Text);
                        eventCommand.Parameters.Add("@IncubationProgram", SqlDbType.NVarChar, 200).Value = txtIncubationTemp.Text.Trim();
                        eventCommand.Parameters.Add("@BacteriaIncubator", SqlDbType.NVarChar, 100).Value = txtBacteriaIncubator.Text.Trim();
                        eventCommand.Parameters.Add("@FungiIncubator", SqlDbType.NVarChar, 100).Value = txtFungiIncubator.Text.Trim();
                        eventCommand.Parameters.Add("@IncubationStart", SqlDbType.NVarChar, 50).Value = samplingDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                        eventCommand.Parameters.Add("@IncubationEnd", SqlDbType.NVarChar, 50).Value = incubationEnd.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                        eventCommand.Parameters.Add("@Remarks", SqlDbType.NVarChar, -1).Value = DbValue(txtRemarks?.Text);

                        string monitoringCategory = method == "Personnel Monitoring" ? "Personnel" :
                            (method == "Surface Swab" || method == "Contact Plate" ? "Surface" : "Air");
                        eventCommand.Parameters.Add("@MonitoringCategory", SqlDbType.NVarChar, 50).Value = monitoringCategory;
                        // The selected EM area already identifies any dispensing/weighing booth.
                        // Keep the legacy database column empty instead of asking for duplicate manual data.
                        eventCommand.Parameters.Add("@DispensingBooth", SqlDbType.NVarChar, 100).Value = DBNull.Value;
                        eventCommand.Parameters.Add("@MaterialName", SqlDbType.NVarChar, 200).Value = DbValue(txtEmMaterialName?.Text);
                        eventCommand.Parameters.Add("@BatchNo", SqlDbType.NVarChar, 100).Value = DbValue(txtEmBatchNo?.Text);
                        eventCommand.Parameters.Add("@EmployeeId", SqlDbType.NVarChar, 50).Value = DbValue(txtEmployeeId?.Text);
                        eventCommand.Parameters.Add("@EmployeeName", SqlDbType.NVarChar, 150).Value = DbValue(txtEmployeeName?.Text);
                        eventCommand.Parameters.Add("@EmployeeDepartment", SqlDbType.NVarChar, 100).Value = DbValue(txtEmployeeDepartment?.Text);
                        eventCommand.Parameters.Add("@EmployeeShift", SqlDbType.NVarChar, 50).Value = DbValue(cboEmployeeShift?.Text);
                        eventCommand.Parameters.Add("@SamplingStage", SqlDbType.NVarChar, 50).Value = DbValue(cboSamplingStage?.Text);
                        eventCommand.Parameters.Add("@SurfaceLocation", SqlDbType.NVarChar, 200).Value = DbValue(txtSurfaceLocation?.Text);
                        eventCommand.Parameters.Add("@SurfaceType", SqlDbType.NVarChar, 80).Value = DbValue((cboSurfaceType?.SelectedItem as ComboBoxItem)?.Content?.ToString());
                        eventCommand.Parameters.Add("@SurfaceAreaCm2", SqlDbType.Decimal).Value =
                            decimal.TryParse(txtSurfaceAreaCm2?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal surfaceAreaValue)
                                ? surfaceAreaValue : DBNull.Value;
                        eventCommand.Parameters["@SurfaceAreaCm2"].Precision = 10;
                        eventCommand.Parameters["@SurfaceAreaCm2"].Scale = 2;
                        eventCommand.Parameters.Add("@SwabKitLot", SqlDbType.NVarChar, 100).Value = DbValue(txtSwabKitLot?.Text);
                        eventCommand.Parameters.Add("@DiluentLot", SqlDbType.NVarChar, 100).Value = DbValue(txtDiluentLot?.Text);
                        eventCommand.Parameters.Add("@RecoveryVolumeMl", SqlDbType.Decimal).Value =
                            decimal.TryParse(txtRecoveryVolumeMl?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal recoveryVolumeValue)
                                ? recoveryVolumeValue : DBNull.Value;
                        eventCommand.Parameters["@RecoveryVolumeMl"].Precision = 10;
                        eventCommand.Parameters["@RecoveryVolumeMl"].Scale = 2;

                        object? result = await eventCommand.ExecuteScalarAsync();

                        if (result == null || result == DBNull.Value)
                            throw new InvalidOperationException("EM event insert did not return an Event ID. Registration was cancelled.");

                        eventId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                    }

                    using (SqlCommand plateCommand = new SqlCommand(@"
                        INSERT INTO dbo.EM_EventPlates
                        (
                            EventId, Method, PlateCode, SequenceNo,
                            TotalCount, FungalCount, ColoniesObserved,
                            CorrectedCount, ResultCFU, Status,
                            SampleSite, SurfaceType, SurfaceAreaCm2, RecoveryVolumeMl, CreatedAt
                        )
                        SELECT
                            @EventId, LTRIM(RTRIM(T.Method)), LTRIM(RTRIM(T.PlateCode)), T.SequenceNo,
                            NULL, NULL, NULL, NULL, NULL, 'Pending',
                            COALESCE(NULLIF(LTRIM(RTRIM(T.PlateCode)), ''), @SurfaceLocation),
                            @SurfaceType, @SurfaceAreaCm2, @RecoveryVolumeMl, GETDATE()
                        FROM dbo.EM_AreaTemplates T
                        WHERE T.AreaId = @AreaId
                          AND ISNULL(T.IsActive, 1) = 1
                          AND T.PlateCode IS NOT NULL
                          AND LTRIM(RTRIM(T.PlateCode)) <> ''
                          AND @Method NOT IN ('Contact Plate', 'Surface Swab', 'Personnel Monitoring')
                          AND (@Method = 'Both' OR UPPER(LTRIM(RTRIM(T.Method))) = UPPER(@Method));", con, tran))
                    {
                        plateCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        plateCommand.Parameters.Add("@EventId", SqlDbType.Int).Value = eventId;
                        plateCommand.Parameters.Add("@AreaId", SqlDbType.Int).Value = areaId;
                        plateCommand.Parameters.Add("@Method", SqlDbType.NVarChar, 100).Value = methodForDb;

                        plateCommand.Parameters.Add("@SurfaceLocation", SqlDbType.NVarChar, 200).Value = DbValue(txtSurfaceLocation?.Text);
                        plateCommand.Parameters.Add("@SurfaceType", SqlDbType.NVarChar, 80).Value = DbValue((cboSurfaceType?.SelectedItem as ComboBoxItem)?.Content?.ToString());
                        plateCommand.Parameters.Add("@SurfaceAreaCm2", SqlDbType.Decimal).Value =
                            decimal.TryParse(txtSurfaceAreaCm2?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal plateSurfaceArea)
                                ? plateSurfaceArea : DBNull.Value;
                        plateCommand.Parameters["@SurfaceAreaCm2"].Precision = 10;
                        plateCommand.Parameters["@SurfaceAreaCm2"].Scale = 2;
                        plateCommand.Parameters.Add("@RecoveryVolumeMl", SqlDbType.Decimal).Value =
                            decimal.TryParse(txtRecoveryVolumeMl?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal plateRecoveryVolume)
                                ? plateRecoveryVolume : DBNull.Value;
                        plateCommand.Parameters["@RecoveryVolumeMl"].Precision = 10;
                        plateCommand.Parameters["@RecoveryVolumeMl"].Scale = 2;
                        platesCreated = await plateCommand.ExecuteNonQueryAsync();
                    }

                    if (platesCreated == 0 &&
                        (method == "Contact Plate" || method == "Surface Swab" || method == "Personnel Monitoring"))
                    {
                        string areaCode = areaRow.Row.Table.Columns.Contains("AreaCode")
                            ? areaRow["AreaCode"]?.ToString()?.Trim() ?? "EM"
                            : "EM";

                        if (method == "Personnel Monitoring")
                        {
                            using SqlCommand personnelCommand = new SqlCommand(@"
                                INSERT INTO dbo.EM_EventPlates
                                (
                                    EventId, Method, PlateCode, SequenceNo,
                                    TotalCount, FungalCount, ColoniesObserved,
                                    CorrectedCount, ResultCFU, Status,
                                    SampleSite, PersonnelSide, SurfaceType, SurfaceAreaCm2, RecoveryVolumeMl, CreatedAt
                                )
                                VALUES
                                (@EventId, N'Personnel Monitoring', @LeftCode, 1,
                                 NULL, NULL, NULL, NULL, NULL, N'Pending',
                                 N'Left Glove', N'Left', N'Gloved Finger Dab', NULL, NULL, GETDATE()),
                                (@EventId, N'Personnel Monitoring', @RightCode, 2,
                                 NULL, NULL, NULL, NULL, NULL, N'Pending',
                                 N'Right Glove', N'Right', N'Gloved Finger Dab', NULL, NULL, GETDATE());", con, tran);

                            personnelCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            personnelCommand.Parameters.Add("@EventId", SqlDbType.Int).Value = eventId;
                            personnelCommand.Parameters.Add("@LeftCode", SqlDbType.NVarChar, 100).Value = areaCode + "-PM-LEFTGLOVE";
                            personnelCommand.Parameters.Add("@RightCode", SqlDbType.NVarChar, 100).Value = areaCode + "-PM-RIGHTGLOVE";
                            platesCreated = await personnelCommand.ExecuteNonQueryAsync();
                        }
                        else
                        {
                            string suffix = method == "Contact Plate" ? "CP" : "SW";
                            string site = txtSurfaceLocation?.Text.Trim() ?? "Surface";

                            using SqlCommand surfaceCommand = new SqlCommand(@"
                                INSERT INTO dbo.EM_EventPlates
                                (
                                    EventId, Method, PlateCode, SequenceNo,
                                    TotalCount, FungalCount, ColoniesObserved,
                                    CorrectedCount, ResultCFU, Status,
                                    SampleSite, SurfaceType, SurfaceAreaCm2, RecoveryVolumeMl, CreatedAt
                                )
                                VALUES
                                (@EventId, @Method, @PlateCode, 1,
                                 NULL, NULL, NULL, NULL, NULL, N'Pending',
                                 @SampleSite, @SurfaceType, @SurfaceAreaCm2, @RecoveryVolumeMl, GETDATE());", con, tran);

                            surfaceCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                            surfaceCommand.Parameters.Add("@EventId", SqlDbType.Int).Value = eventId;
                            surfaceCommand.Parameters.Add("@Method", SqlDbType.NVarChar, 100).Value = method;
                            surfaceCommand.Parameters.Add("@PlateCode", SqlDbType.NVarChar, 100).Value = areaCode + "-" + suffix + "-MANUAL";
                            surfaceCommand.Parameters.Add("@SampleSite", SqlDbType.NVarChar, 200).Value = site;
                            surfaceCommand.Parameters.Add("@SurfaceType", SqlDbType.NVarChar, 80).Value =
                                (cboSurfaceType?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                            surfaceCommand.Parameters.Add("@SurfaceAreaCm2", SqlDbType.Decimal).Value =
                                decimal.TryParse(txtSurfaceAreaCm2?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal fallbackSurfaceArea)
                                    ? fallbackSurfaceArea : DBNull.Value;
                            surfaceCommand.Parameters["@SurfaceAreaCm2"].Precision = 10;
                            surfaceCommand.Parameters["@SurfaceAreaCm2"].Scale = 2;
                            surfaceCommand.Parameters.Add("@RecoveryVolumeMl", SqlDbType.Decimal).Value =
                                decimal.TryParse(txtRecoveryVolumeMl?.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal fallbackRecoveryVolume)
                                    ? fallbackRecoveryVolume : DBNull.Value;
                            surfaceCommand.Parameters["@RecoveryVolumeMl"].Precision = 10;
                            surfaceCommand.Parameters["@RecoveryVolumeMl"].Scale = 2;
                            platesCreated = await surfaceCommand.ExecuteNonQueryAsync();
                        }
                    }

                    if (platesCreated != expectedPlates)
                    {
                        throw new InvalidOperationException(
                            "EM registration was cancelled because the created plate count did not match the active template. " +
                            "Expected: " + expectedPlates.ToString(CultureInfo.InvariantCulture) +
                            ", created: " + platesCreated.ToString(CultureInfo.InvariantCulture) + ".");
                    }

                    using (SqlCommand signatureCommand = new SqlCommand(@"
                        INSERT INTO dbo.EM_EventSignatures
                        (
                            EventID, EventNo, ActionType, ActionReason,
                            SignedBy, UserRole, MeaningOfSignature, SignedAt
                        )
                        VALUES
                        (
                            @EventID, @EventNo, N'EM Registration', @Reason,
                            @SignedBy, @UserRole, @Meaning, GETDATE()
                        );", con, tran))
                    {
                        signatureCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        signatureCommand.Parameters.Add("@EventID", SqlDbType.Int).Value = eventId;
                        signatureCommand.Parameters.Add("@EventNo", SqlDbType.NVarChar, 50).Value = finalEventNumber;
                        signatureCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = DbValue(registrationSignature.Reason);
                        signatureCommand.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = registrationSignature.SignedBy;
                        signatureCommand.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value =
                            string.IsNullOrWhiteSpace(Login.CurrentUserRole) ? "Unknown" : Login.CurrentUserRole;
                        signatureCommand.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = registrationSignature.Meaning;
                        await signatureCommand.ExecuteNonQueryAsync();
                    }

                    tran.Commit();
                }
                catch
                {
                    tran.Rollback();
                    throw;
                }
            }

            if (txtSampleNo != null)
                txtSampleNo.Text = finalEventNumber;

            DatabaseHelper.AddAuditTrailAdvanced(
                "EM_Events",
                eventId,
                "Environmental Monitoring Event Registered",
                "",
                "Pending",
                registrationSignature.Reason,
                registrationSignature.SignedBy,
                "WorkflowStatus",
                "",
                finalEventNumber,
                "Environmental Monitoring");

            MessageBox.Show(
                "Environmental monitoring event saved successfully!\n\n" +
                "Event No: " + finalEventNumber + "\n" +
                "Method: " + method + "\n" +
                "Expected Plates: " + expectedPlates.ToString(CultureInfo.InvariantCulture) + "\n" +
                "Plates Created: " + platesCreated.ToString(CultureInfo.InvariantCulture),
                "Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            _isDirty = false;
            Close();
        }

        private static object DbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_isDirty)
            {
                MessageBoxResult result = MessageBox.Show(
                    "Are you sure you want to cancel?\nAll entered data will be lost.",
                    "Confirm Cancel",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            Close();
        }

        private DataTable ExecuteDataTable(string sql, params SqlParameter[] parameters)
        {
            using SqlConnection con = new SqlConnection(AppConfig.ConnectionString);
            con.Open();

            using SqlCommand cmd = new SqlCommand(sql, con);
            cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;

            if (parameters != null && parameters.Length > 0)
                cmd.Parameters.AddRange(parameters);

            using SqlDataAdapter da = new SqlDataAdapter(cmd);
            DataTable dt = new DataTable();
            da.Fill(dt);

            return dt;
        }

        private object ExecuteScalar(string sql, params SqlParameter[] parameters)
        {
            using SqlConnection con = new SqlConnection(AppConfig.ConnectionString);
            con.Open();

            using SqlCommand cmd = new SqlCommand(sql, con);
            cmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;

            if (parameters != null && parameters.Length > 0)
                cmd.Parameters.AddRange(parameters);

            return cmd.ExecuteScalar();
        }

        private DateTime ParseIncubationEnd()
        {
            if (txtIncubationEnd != null &&
                DateTime.TryParse(txtIncubationEnd.Text.Trim(), out DateTime dt))
            {
                return dt;
            }

            return GetSelectedSamplingDateTime().AddDays(GetIncubationDays());
        }
    }
}
