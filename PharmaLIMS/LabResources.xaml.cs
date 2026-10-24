using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;

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
        private int _activityHistoryRequest;
        private int _equipmentLoadRequest;
        private int _sampleChoicesRequest;

        public LabResources()
        {
            InitializeComponent();
            _authService = App.ServiceProvider?.GetService<IAuthService>();
            Loaded += LabResources_Loaded;
        }

        public LabResources(IAuthService authService) : this() => _authService = authService;

        private async void LabResources_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDefaults();
            await LoadEquipmentAsync();
            if (CboSourceModule.SelectedIndex < 0) CboSourceModule.SelectedIndex = 0;
            await LoadAvailableSamplesAsync();
        }

        private void ApplyDefaults()
        {
            if (CboStatus.SelectedIndex < 0) CboStatus.SelectedIndex = 0;
            if (CboQualification.SelectedIndex < 0) CboQualification.SelectedIndex = 3;
            if (CboCalibration.SelectedIndex < 0) CboCalibration.SelectedIndex = 3;
            ChkCalibrationRequired.IsChecked = true;
            ChkGmpCritical.IsChecked = true;
        }

        private async Task LoadEquipmentAsync()
        {
            int request = ++_equipmentLoadRequest;
            try
            {
                LblStatus.Text = "Loading equipment records...";
                DataTable table = await _database.ExecuteQueryAsync(@"
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
ORDER BY CASE WHEN e.IsActive=1 THEN 0 ELSE 1 END, e.EquipmentCode;", commandTimeoutSeconds: 20);

                if (!IsLoaded || request != _equipmentLoadRequest) return;
                _allEquipment.Clear();
                foreach (DataRow row in table.Rows)
                    _allEquipment.Add(EquipmentRow.From(row));

                ApplySearchFilter();
                UpdateSummary();
                LblStatus.Text = $"Loaded {_allEquipment.Count} equipment record(s).";
            }
            catch (Exception ex)
            {
                if (!IsLoaded || request != _equipmentLoadRequest) return;
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

        private void LoadEquipmentActivityHistory(int equipmentId) =>
            _ = LoadEquipmentActivityHistoryAsync(equipmentId);

        private async Task LoadEquipmentActivityHistoryAsync(int equipmentId)
        {
            int request = ++_activityHistoryRequest;
            GridActivityHistory.ItemsSource = null;
            LblActivityHistory.Text = "Loading activity history...";
            try
            {
                DataTable exists = await _database.ExecuteQueryAsync("SELECT OBJECT_ID(N'dbo.MicroEquipmentActivities',N'U') AS TableId;", commandTimeoutSeconds: 15);
                if (request != _activityHistoryRequest || !IsLoaded || _selectedEquipmentId != equipmentId) return;
                if (exists.Rows.Count == 0 || exists.Rows[0]["TableId"] == DBNull.Value)
                {
                    LblActivityHistory.Text = "Activity ledger migration is not yet applied. Equipment master remains available.";
                    return;
                }
                DataTable table = await _database.ExecuteQueryAsync(@"
SELECT TOP (100) ActivityID,ActivityType,ActivityStatus,PerformedBy,CreatedAt,
    (SELECT STRING_AGG(CONVERT(nvarchar(max),
        l.Module+N':'+COALESCE(NULLIF(l.SampleNumberSnapshot,N''),CONVERT(nvarchar(20),l.ParentRecordID))),N', ')
     FROM dbo.MicroEquipmentActivityLinks l WHERE l.ActivityID=MicroEquipmentActivities.ActivityID) AS LinkedSamples
FROM dbo.MicroEquipmentActivities
WHERE EquipmentID=@EquipmentID
ORDER BY ActivityID DESC;",
                    new[] { new SqlParameter("@EquipmentID", SqlDbType.Int) { Value = equipmentId } }, commandTimeoutSeconds: 15);
                if (request != _activityHistoryRequest || !IsLoaded || _selectedEquipmentId != equipmentId) return;
                GridActivityHistory.ItemsSource = table.DefaultView;
                LblActivityHistory.Text = table.Rows.Count == 0
                    ? "No confirmed or draft activity records for this equipment."
                    : $"Showing {table.Rows.Count} most recent activity records (read-only).";
            }
            catch (Exception ex)
            {
                if (request != _activityHistoryRequest || !IsLoaded || _selectedEquipmentId != equipmentId) return;
                LblActivityHistory.Text = "Activity history could not be loaded: " +
                    UserFacingError.SafeMessage(ex, "Equipment activity history");
            }
        }

        private void BtnPrintSelectedActivity_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(TxtActivityId.Text.Trim(), out long activityId) || activityId <= 0)
            {
                MessageBox.Show("Enter an Activity ID to print.", "Equipment Activity");
                return;
            }
            try
            {
                var printEvidence = LoadActivityPrintEvidence(activityId);
                DataTable activity = printEvidence.Activity;
                DataRow a = activity.Rows[0];
                if (!_selectedEquipmentId.HasValue || Convert.ToInt32(a["EquipmentID"], CultureInfo.InvariantCulture) != _selectedEquipmentId.Value)
                    throw new InvalidOperationException("Select the equipment belonging to this activity first.");
                DataTable signatures = printEvidence.Signatures;
                DataTable evidence = printEvidence.Evidence;
                DataTable calibrationPoints = printEvidence.Points;
                DataTable linkedSamples = printEvidence.Links;
                static string Field(DataRow r, string n) => r[n] == DBNull.Value ? "Not recorded" :
                    Convert.ToString(r[n], CultureInfo.InvariantCulture) ?? "Not recorded";
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return;
                var doc = new System.Windows.Documents.FlowDocument
                {
                    PageWidth = Math.Min(dialog.PrintableAreaWidth, 793.7),
                    PageHeight = Math.Min(dialog.PrintableAreaHeight, 1122.5),
                    PagePadding = new Thickness(32),
                    ColumnWidth = double.PositiveInfinity,
                    FontFamily = new System.Windows.Media.FontFamily("Arial"),
                    FontSize = 11
                };
                void Write(string value, bool title = false) =>
                    doc.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(value))
                    {
                        FontWeight = title ? FontWeights.Bold : FontWeights.Normal,
                        FontSize = title ? 14 : 11,
                        Margin = new Thickness(0, 0, 0, title ? 12 : 7)
                    });
                string logoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "medica-logo.png");
                if (System.IO.File.Exists(logoPath))
                {
                    var logo = new System.Windows.Controls.Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(logoPath, UriKind.Absolute)),
                        Width = 125,
                        Height = 64,
                        Stretch = System.Windows.Media.Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    doc.Blocks.Add(new System.Windows.Documents.BlockUIContainer(logo)
                    {
                        Margin = new Thickness(0, 0, 0, 12)
                    });
                }
                Write("MEDICA | MICROBIOLOGY LABORATORY", true);
                Write("EQUIPMENT ACTIVITY EXECUTION RECORD", true);
                Write("CONTROLLED RECORD COPY - NOT A CALIBRATION CERTIFICATE");
                Write("Record ID: " + Field(a, "ActivityID") + " | Type: " + Field(a, "ActivityType"), true);
                Write("Status: " + Field(a, "ActivityStatus"));
                if (Field(a, "ActivityStatus") != "Approved")
                    Write("NOT APPROVED - EXECUTION DRAFT / REVIEW COPY", true);
                Write("Equipment: " + Field(a, "EquipmentCodeSnapshot") + " - " + Field(a, "EquipmentNameSnapshot"));
                Write("Performed by: " + Field(a, "PerformedBy") + " | Created (UTC): " + Field(a, "CreatedAt"));
                Write("Actual start: " + Field(a, "ActualStartAt") + " | Actual end: " + Field(a, "ActualEndAt"));
                Write("Method / SOP: " + Field(a, "MethodReference"));
                Write("Execution evidence: " + Field(a, "ActivityReason"));
                Write("LINKED REGISTERED SAMPLES / EVENTS", true);
                if (linkedSamples.Rows.Count == 0)
                    Write("No sample/event linked. This print does not claim equipment use against an unrecorded sample.");
                foreach (DataRow linked in linkedSamples.Rows)
                    Write(Field(linked, "Module") + " | " + Field(linked, "SampleNumberSnapshot") +
                          " | Parent ID " + Field(linked, "ParentRecordID") +
                          (linked["ResultRecordID"] == DBNull.Value ? "" :
                           " | Optional Test/Plate " + Field(linked, "ResultRecordID")));

                Write("STRUCTURED EXECUTION EVIDENCE", true);
                if (evidence.Rows.Count == 0)
                    Write("No structured evidence available. This record is not sufficient as a calibration or cleaning certificate.");
                foreach (DataRow evidenceRow in evidence.Rows)
                {
                    Write("Evidence type: " + Field(evidenceRow, "EvidenceType") + " | Outcome: " + Field(evidenceRow, "Outcome"));
                    if (Field(evidenceRow, "EvidenceType") == "Calibration")
                    {
                        if (evidenceRow["NormalMinimum"] != DBNull.Value)
                            Write("Manual range: " + Field(evidenceRow, "NormalMinimum") + " to " +
                                  Field(evidenceRow, "NormalMaximum") + " | As-found: " + Field(evidenceRow, "ObservedValue"));
                        else
                            Write("Reference: " + Field(evidenceRow, "ReferenceValue") +
                                  " | As-found: " + Field(evidenceRow, "ObservedValue") +
                                  " | Absolute error: " + Field(evidenceRow, "AbsoluteError") +
                                  " | Tolerance: " + Field(evidenceRow, "MaximumAbsoluteError"));
                    }
                    else
                        Write("Cleaning agent / lot: " + Field(evidenceRow, "CleaningAgentLot"));
                    Write("Recorded by: " + Field(evidenceRow, "RecordedBy") + " | Recorded at (UTC): " + Field(evidenceRow, "RecordedAt"));
                }
                if (Field(a, "ActivityType") == "Calibration" && calibrationPoints.Rows.Count == 0)
                {
                    Write("CALIBRATION RESULT: INCOMPLETE - No measurement points are recorded.", true);
                    Write("This record cannot demonstrate calibration acceptance or release equipment.", true);
                }
                if (calibrationPoints.Rows.Count > 0)
                {
                    bool allPointsPass = calibrationPoints.AsEnumerable().All(p =>
                        string.Equals(Field(p, "Outcome"), "PASS", StringComparison.OrdinalIgnoreCase));
                    bool evidenceComplete = evidence.AsEnumerable().Any(r =>
                        string.Equals(Field(r, "EvidenceType"), "Calibration", StringComparison.OrdinalIgnoreCase));
                    string overall = !evidenceComplete ? "INCOMPLETE" : allPointsPass ? "PASS" : "FAIL";
                    Write("CALIBRATION OVERALL RESULT: " + overall, true);
                    if (!evidenceComplete)
                        Write("INCOMPLETE - Structured calibration evidence is missing. Equipment must not be released based on this record.", true);
                    else if (!allPointsPass)
                        Write("OUT OF RANGE - At least one measurement point failed. Equipment must not be released based on this record.", true);
                    Write("CALIBRATION MEASUREMENT POINTS", true);
                }
                if (calibrationPoints.Rows.Count > 0)
                {
                    var pointsTable = new System.Windows.Documents.Table { CellSpacing = 0 };
                    foreach (double width in new[] { 0.09, 0.24, 0.16, 0.12, 0.12, 0.12, 0.15 })
                        pointsTable.Columns.Add(new System.Windows.Documents.TableColumn
                        { Width = new GridLength(width, GridUnitType.Star) });
                    var group = new System.Windows.Documents.TableRowGroup();
                    pointsTable.RowGroups.Add(group);
                    void AddPointRow(string[] values, bool header)
                    {
                        var row = new System.Windows.Documents.TableRow();
                        foreach (string value in values)
                        {
                            var cell = new System.Windows.Documents.TableCell(
                                new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(value))
                                { Margin = new Thickness(2), FontSize = 9 })
                            {
                                BorderBrush = System.Windows.Media.Brushes.LightGray,
                                BorderThickness = new Thickness(0.4),
                                Padding = new Thickness(3),
                                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                                Background = header ? System.Windows.Media.Brushes.Gainsboro : System.Windows.Media.Brushes.White
                            };
                            row.Cells.Add(cell);
                        }
                        group.Rows.Add(row);
                    }
                    AddPointRow(new[] { "No.", "Range / Ref.", "Max error", "Observed", "Error", "Mode", "Result" }, true);
                    foreach (DataRow point in calibrationPoints.Rows)
                    {
                        bool rangeMode = point["NormalMinimum"] != DBNull.Value;
                        AddPointRow(new[]
                        {
                            Field(point, "PointNo"),
                            rangeMode ? Field(point, "NormalMinimum") + " - " + Field(point, "NormalMaximum")
                                : Field(point, "ReferenceValue"),
                            rangeMode ? "-" : Field(point, "MaxAbsoluteError"),
                            Field(point, "ObservedValue"),
                            rangeMode ? "-" : Field(point, "AbsoluteError"),
                            rangeMode ? "Manual" : "Reference",
                            Field(point, "Outcome")
                        }, false);
                    }
                    doc.Blocks.Add(pointsTable);
                }
                Write("RECORDED SIGNATURE STAGES", true);
                if (signatures.Rows.Count == 0)
                {
                    Write("No review/approval signatures are recorded.");
                    if (string.Equals(Field(a, "ActivityStatus"), "Approved", StringComparison.OrdinalIgnoreCase))
                        Write("SIGNATURE EVIDENCE MISSING - Approved status alone does not establish a signed controlled record.", true);
                }
                if (string.Equals(Field(a, "ActivityStatus"), "Approved", StringComparison.OrdinalIgnoreCase) &&
                    (!signatures.AsEnumerable().Any(s => string.Equals(Field(s, "SignatureMeaning"), "Reviewed", StringComparison.OrdinalIgnoreCase)) ||
                     !signatures.AsEnumerable().Any(s => string.Equals(Field(s, "SignatureMeaning"), "Approved", StringComparison.OrdinalIgnoreCase))))
                    Write("INCOMPLETE SIGNATURE CHAIN - Both Reviewed and Approved signature stages are required.", true);
                foreach (DataRow s in signatures.Rows)
                    Write(Field(s, "SignatureMeaning") + " | " + Field(s, "SignedBy") + " | " +
                          Field(s, "SignedAt") + " | Reason: " + Field(s, "SignatureReason"));
                Write("Printed " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) +
                      " by " + Login.CurrentUser + ". This print does not substitute for a calibration certificate or cleaning verification.");
                dialog.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)doc).DocumentPaginator,
                    "Equipment activity " + activityId.ToString(CultureInfo.InvariantCulture));
                LblStatus.Text = "Activity " + activityId + " sent to printer.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Print equipment activity"),
                    "Equipment Activity", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnPrintEquipmentHistory_Click(object sender, RoutedEventArgs e)
        {
            if (!_selectedEquipmentId.HasValue)
            {
                MessageBox.Show("Select a saved equipment record before printing.", "Equipment History");
                return;
            }
            try
            {
                int equipmentId = _selectedEquipmentId.Value;
                DataTable master = _database.ExecuteQuery(@"
SELECT EquipmentCode,EquipmentName,EquipmentType,Department,Location,
       Manufacturer,Model,SerialNumber,EquipmentStatus,QualificationStatus,
       CalibrationStatus,LastCalibrationDate,NextCalibrationDate,
       LastQualificationDate,NextQualificationDate,MethodReference
FROM dbo.LabEquipment WHERE EquipmentID=@EquipmentID;",
                    new[] { new SqlParameter("@EquipmentID", SqlDbType.Int) { Value = equipmentId } });
                if (master.Rows.Count != 1)
                    throw new InvalidOperationException("Saved equipment record was not found. Reload the equipment list.");

                DataTable history = _database.ExecuteQuery(@"
SELECT TOP (100) ActivityID,ActivityType,ActivityStatus,PerformedBy,
       ActualStartAt,ActualEndAt,MethodReference,ActivityReason,CreatedAt,
       (SELECT STRING_AGG(CONVERT(nvarchar(max),
           l.Module+N':'+COALESCE(NULLIF(l.SampleNumberSnapshot,N''),CONVERT(nvarchar(20),l.ParentRecordID))),N', ')
        FROM dbo.MicroEquipmentActivityLinks l WHERE l.ActivityID=MicroEquipmentActivities.ActivityID) AS LinkedSamples
FROM dbo.MicroEquipmentActivities
WHERE EquipmentID=@EquipmentID
ORDER BY ActivityID DESC;",
                    new[] { new SqlParameter("@EquipmentID", SqlDbType.Int) { Value = equipmentId } });

                DataTable activityCount = _database.ExecuteQuery(@"
SELECT COUNT_BIG(*) AS TotalActivities
FROM dbo.MicroEquipmentActivities
WHERE EquipmentID=@EquipmentID;",
                    new[] { new SqlParameter("@EquipmentID", SqlDbType.Int) { Value = equipmentId } });
                long totalActivities = Convert.ToInt64(activityCount.Rows[0]["TotalActivities"], CultureInfo.InvariantCulture);

                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true) return;
                DataRow equipment = master.Rows[0];
                static string Val(DataRow row, string column) =>
                    row[column] == DBNull.Value ? "Not recorded" : Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? "Not recorded";
                var document = new System.Windows.Documents.FlowDocument
                {
                    PageWidth = dialog.PrintableAreaWidth,
                    PageHeight = dialog.PrintableAreaHeight,
                    PagePadding = new Thickness(36),
                    ColumnWidth = double.PositiveInfinity,
                    FontFamily = new System.Windows.Media.FontFamily("Arial"),
                    FontSize = 10
                };
                void Line(string content, bool heading = false)
                {
                    document.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(content))
                    {
                        FontWeight = heading ? FontWeights.Bold : FontWeights.Normal,
                        FontSize = heading ? 13 : 10,
                        Margin = new Thickness(0, 0, 0, heading ? 9 : 5)
                    });
                }
                string historyLogoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "medica-logo.png");
                if (System.IO.File.Exists(historyLogoPath))
                {
                    var historyLogo = new System.Windows.Controls.Image
                    {
                        Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(historyLogoPath, UriKind.Absolute)),
                        Width = 125,
                        Height = 64,
                        Stretch = System.Windows.Media.Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    document.Blocks.Add(new System.Windows.Documents.BlockUIContainer(historyLogo)
                    {
                        Margin = new Thickness(0, 0, 0, 12)
                    });
                }
                Line("MEDICA | LABORATORY EQUIPMENT HISTORY", true);
                Line("UNCONTROLLED PRINT COPY - Review recorded status before GMP use.");
                Line("Generated: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)
                     + " | Printed by: " + Login.CurrentUser);
                Line("Equipment: " + Val(equipment, "EquipmentCode") + " - " + Val(equipment, "EquipmentName"), true);
                Line("Type: " + Val(equipment, "EquipmentType") + " | Department: " + Val(equipment, "Department"));
                Line("Location: " + Val(equipment, "Location") + " | Manufacturer: " + Val(equipment, "Manufacturer"));
                Line("Model: " + Val(equipment, "Model") + " | Serial: " + Val(equipment, "SerialNumber"));
                Line("Equipment status: " + Val(equipment, "EquipmentStatus")
                     + " | Qualification: " + Val(equipment, "QualificationStatus")
                     + " | Calibration: " + Val(equipment, "CalibrationStatus"));
                Line("Last / next calibration: " + Val(equipment, "LastCalibrationDate")
                     + " / " + Val(equipment, "NextCalibrationDate"));
                Line("Last / next qualification: " + Val(equipment, "LastQualificationDate")
                     + " / " + Val(equipment, "NextQualificationDate"));
                Line("Equipment SOP: " + Val(equipment, "MethodReference"));
                Line("ACTIVITY HISTORY (latest 100; draft and submitted records are not approved)", true);
                Line("Records included: " + history.Rows.Count.ToString(CultureInfo.InvariantCulture)
                     + " of " + totalActivities.ToString(CultureInfo.InvariantCulture) + " recorded activities.");
                if (totalActivities > history.Rows.Count)
                    Line("PARTIAL HISTORY COPY — earlier records are not included. Review electronic history for the full record.", true);
                if (history.Rows.Count == 0) Line("No activities recorded.");
                foreach (DataRow item in history.Rows)
                {
                    Line("#" + Val(item, "ActivityID") + " | " + Val(item, "ActivityType")
                         + " | STATUS: " + Val(item, "ActivityStatus"), true);
                    Line("Performed by: " + Val(item, "PerformedBy")
                         + " | Recorded UTC: " + Val(item, "CreatedAt"));
                    Line("Actual start: " + Val(item, "ActualStartAt")
                         + " | Actual end: " + Val(item, "ActualEndAt"));
                    Line("SOP: " + Val(item, "MethodReference"));
                    Line("Execution details: " + Val(item, "ActivityReason"));
                    Line("Linked samples: " + Val(item, "LinkedSamples"));
                }
                Line("This listing is not a calibration certificate, cleaning release or electronic signature.");
                dialog.PrintDocument(((System.Windows.Documents.IDocumentPaginatorSource)document).DocumentPaginator,
                    "MEDICA Equipment History - " + Val(equipment, "EquipmentCode"));
                LblStatus.Text = "Selected equipment history sent to the print system.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Print equipment history"),
                    "Equipment History", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                TxtActivityId.Text = activityId.ToString(CultureInfo.InvariantCulture);
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

        // The picker is a read-only view of registered source records, never a usage
        // record. It is refreshed on demand and when the operator changes modules.
        private async Task LoadAvailableSamplesAsync()
        {
            if (!IsLoaded) return;
            int request = ++_sampleChoicesRequest;
            string module = (CboSourceModule.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            string filter = TxtSampleSearch.Text.Trim();
            LstAvailableSamples.ItemsSource = null;
            LblAvailableSamples.Text = "Loading registered samples...";
            string sql = module switch
            {
                "PRM" => @"SELECT TOP(300) SampleID AS SourceID,
    CONCAT(SampleNumber,N' | ',SampleCategory,N' | ',COALESCE(NULLIF(MaterialName,N''),NULLIF(ProductName,N''),N'')) AS Label
FROM dbo.PRM_Samples
WHERE @Search=N'' OR SampleNumber LIKE N'%'+@Search+N'%'
ORDER BY SampleID DESC;",
                "EM" => @"SELECT TOP(300) Id AS SourceID,
    CONCAT(EventNo,N' | Environmental Monitoring') AS Label
FROM dbo.EM_Events
WHERE @Search=N'' OR EventNo LIKE N'%'+@Search+N'%'
ORDER BY Id DESC;",
                "WATER" => @"SELECT TOP(300) SampleID AS SourceID,
    CONCAT(SampleNumber,N' | ',SampleType) AS Label
FROM dbo.Samples
WHERE @Search=N'' OR SampleNumber LIKE N'%'+@Search+N'%'
ORDER BY SampleID DESC;",
                _ => string.Empty
            };
            if (sql.Length == 0)
            {
                LblAvailableSamples.Text = "Select PRM, EM, or WATER.";
                return;
            }
            try
            {
                DataTable table = await _database.ExecuteQueryAsync(sql,
                    new[] { new SqlParameter("@Search", SqlDbType.NVarChar, 100) { Value = filter } },
                    commandTimeoutSeconds: 20);
                if (request != _sampleChoicesRequest || !IsLoaded ||
                    (CboSourceModule.SelectedItem as ComboBoxItem)?.Content?.ToString() != module)
                    return;
                LstAvailableSamples.ItemsSource = table.DefaultView;
                LblAvailableSamples.Text = table.Rows.Count == 0
                    ? "No matching registered samples. Check the number or refresh."
                    : $"{table.Rows.Count} registered sample(s) available (latest 300). Click multiple samples, then Link. Older samples can be found by search.";
            }
            catch (Exception ex)
            {
                if (request != _sampleChoicesRequest || !IsLoaded) return;
                LblAvailableSamples.Text = "Unable to load samples: " +
                    UserFacingError.SafeMessage(ex, "Registered sample picker");
            }
        }

        private async void CboSourceModule_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await LoadAvailableSamplesAsync();
        }

        private async void BtnRefreshSamples_Click(object sender, RoutedEventArgs e) =>
            await LoadAvailableSamplesAsync();

        private async void TxtSampleSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            await LoadAvailableSamplesAsync();
        }

        private void LstAvailableSamples_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstAvailableSamples.SelectedItems.Count != 1)
            {
                TxtSourceParentId.Text = string.Empty;
                return;
            }
            if (LstAvailableSamples.SelectedItem is DataRowView row)
                TxtSourceParentId.Text = Convert.ToString(row["SourceID"], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private void BtnLinkActivity_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(TxtActivityId.Text.Trim(), out long activityId) || activityId <= 0)
            {
                MessageBox.Show("Select or enter a valid draft Activity ID.", "Activity Link");
                return;
            }
            string module = (CboSourceModule.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
            if (module is not ("PRM" or "EM" or "WATER"))
            {
                MessageBox.Show("Select PRM, EM, or WATER.", "Activity Link");
                return;
            }
            string rawResult = TxtSourceResultId.Text.Trim();
            int? resultId = null;
            if (rawResult.Length > 0)
            {
                if (!int.TryParse(rawResult, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
                {
                    MessageBox.Show("Optional Test/Plate ID must be a positive number when provided.", "Activity Link");
                    return;
                }
                resultId = parsed;
            }
            var parents = new List<int>();
            foreach (object selected in LstAvailableSamples.SelectedItems)
            {
                if (selected is DataRowView row)
                    parents.Add(Convert.ToInt32(row["SourceID"], CultureInfo.InvariantCulture));
            }
            if (parents.Count == 0 && int.TryParse(TxtSourceParentId.Text.Trim(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int manualParent) && manualParent > 0)
                parents.Add(manualParent);
            if (parents.Count == 0 || (resultId.HasValue && parents.Count != 1))
            {
                MessageBox.Show("Select at least one registered sample, or enter one Sample/Event ID manually. " +
                    "An optional Test/Plate ID can be used with ONE sample only.", "Activity Link");
                return;
            }
            try
            {
                _database.ExecuteInTransaction((connection, transaction) =>
                {
                    foreach (int parentId in parents)
                        Services.MicroEquipmentActivityService.AddLink(connection, transaction,
                            activityId, module, parentId, resultId, Login.CurrentUser);
                });
                if (_selectedEquipmentId.HasValue) LoadEquipmentActivityHistory(_selectedEquipmentId.Value);
                LblStatus.Text = $"Activity {activityId} linked to {parents.Count} verified {module} sample/event(s). " +
                    "No equipment use is confirmed until Confirm Actual Use.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Link equipment activity"), "Activity Link",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnConfirmActualUse_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(TxtActivityId.Text.Trim(), out long activityId) || activityId <= 0 ||
                !DateTimeOffset.TryParse(DpActualUseStart.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) ||
                !DateTimeOffset.TryParse(DpActualUseEnd.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ended) ||
                ended < started)
            {
                MessageBox.Show("Enter a valid activity ID, start and end timestamps with correct offsets.", "Confirm Actual Use");
                return;
            }
            try
            {
                _database.ExecuteInTransaction((connection, transaction) =>
                {
                    using var command = new SqlCommand(@"
SELECT VersionToken FROM dbo.MicroEquipmentActivities WITH (UPDLOCK,HOLDLOCK)
WHERE ActivityID=@Activity AND ActivityType=N'Use' AND ActivityStatus=N'Draft'
  AND PerformedBy=@Actor;", connection, transaction);
                    command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = activityId;
                    command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = Login.CurrentUser;
                    byte[]? token = command.ExecuteScalar() as byte[];
                    if (token == null || token.Length != 8)
                        throw new InvalidOperationException("Draft use activity does not exist or is owned by another performer.");
                    Services.MicroEquipmentActivityService.ConfirmUse(connection, transaction,
                        activityId, token, Login.CurrentUser, started, ended);
                });
                if (_selectedEquipmentId.HasValue) LoadEquipmentActivityHistory(_selectedEquipmentId.Value);
                LblStatus.Text = $"Use activity {activityId} submitted. Review and approval remain pending.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Confirm actual equipment use"), "Confirm Actual Use",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSubmitNonUseExecution_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(TxtActivityId.Text.Trim(), out long activityId) || activityId <= 0 ||
                !DateTimeOffset.TryParse(DpActualUseStart.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started) ||
                !DateTimeOffset.TryParse(DpActualUseEnd.Text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ended) ||
                ended < started || string.IsNullOrWhiteSpace(TxtNonUseExecutionDetails.Text))
            {
                MessageBox.Show("Enter a valid activity ID, execution description, and actual start/end times with offsets.",
                    "Submit Equipment Execution", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                _database.ExecuteInTransaction((connection, transaction) =>
                {
                    using var command = new SqlCommand(@"
SELECT VersionToken,ActivityType FROM dbo.MicroEquipmentActivities WITH(UPDLOCK,HOLDLOCK)
WHERE ActivityID=@Activity AND ActivityStatus=N'Draft' AND ActivityType<>N'Use'
AND PerformedBy=@Actor;", connection, transaction);
                    command.Parameters.Add("@Activity", SqlDbType.BigInt).Value = activityId;
                    command.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = Login.CurrentUser;
                    byte[] token;
                    string activityType;
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new InvalidOperationException("Draft execution is absent or no longer editable.");
                        token = (byte[])reader["VersionToken"];
                        activityType = Convert.ToString(reader["ActivityType"], CultureInfo.InvariantCulture) ?? "";
                    }
                    string evidence = TxtNonUseExecutionDetails.Text.Trim();
                    if (activityType == "Calibration")
                    {
                        bool Parse(string value, out decimal number) =>
                            decimal.TryParse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture, out number);
                        bool manualRange = !string.IsNullOrWhiteSpace(TxtCalibrationMinimum.Text) || !string.IsNullOrWhiteSpace(TxtCalibrationMaximum.Text);
                        if (manualRange && (!Parse(TxtCalibrationMinimum.Text, out decimal minimum) || !Parse(TxtCalibrationMaximum.Text, out decimal maximum) || minimum > maximum || decimal.Round(minimum,6)!=minimum || decimal.Round(maximum,6)!=maximum || Math.Abs(minimum)>=1000000000000m || Math.Abs(maximum)>=1000000000000m))
                            throw new InvalidOperationException("Enter a valid minimum and maximum, minimum <= maximum.");
                        if (manualRange && (!string.IsNullOrWhiteSpace(TxtCalibrationReference.Text) || !string.IsNullOrWhiteSpace(TxtCalibrationTolerance.Text)))
                            throw new InvalidOperationException("Use either manual normal range or reference/tolerance, not both.");
                        decimal reference = 0m, tolerance = 0m;
                        if (!Parse(TxtCalibrationObserved.Text, out decimal observed) ||
                            decimal.Round(observed,6)!=observed || Math.Abs(observed)>=1000000000000m ||
                            (!manualRange && (!Parse(TxtCalibrationReference.Text,out reference) ||
                            !Parse(TxtCalibrationTolerance.Text,out tolerance) || tolerance<0 ||
                            decimal.Round(reference,6)!=reference || decimal.Round(tolerance,6)!=tolerance ||
                            Math.Abs(reference)>=1000000000000m || tolerance>=1000000000000m)))
                            throw new InvalidOperationException("Valid observed reading and either manual range or reference/tolerance are required.");
                        string outcome = manualRange ?
                            (observed>=decimal.Parse(TxtCalibrationMinimum.Text,CultureInfo.InvariantCulture) && observed<=decimal.Parse(TxtCalibrationMaximum.Text,CultureInfo.InvariantCulture) ? "PASS":"FAIL") :
                            (Math.Abs(observed-reference)<=tolerance ? "PASS":"FAIL");
                        evidence = manualRange ?
                            "NormalMinimum="+TxtCalibrationMinimum.Text.Trim()+"; NormalMaximum="+TxtCalibrationMaximum.Text.Trim()+"; Observed="+observed.ToString(CultureInfo.InvariantCulture)+"; Outcome="+outcome+"; Notes="+evidence :
                            "Reference="+reference.ToString(CultureInfo.InvariantCulture)+"; AsFound="+observed.ToString(CultureInfo.InvariantCulture)+"; MaxAbsoluteError="+tolerance.ToString(CultureInfo.InvariantCulture)+"; Outcome="+outcome+"; Notes="+evidence;
                    }
                    else if (activityType is "Cleaning" or "Sanitization")
                    {
                        string agent = TxtCleaningAgent.Text.Trim();
                        string outcome = (CboCleaningOutcome.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                        if (string.IsNullOrWhiteSpace(agent) || agent.Contains(';') ||
                            agent.Contains("Outcome=", StringComparison.OrdinalIgnoreCase) ||
                            outcome is not ("Pass" or "Fail"))
                            throw new InvalidOperationException("Cleaning/sanitization requires agent/lot and verification outcome.");
                        evidence = "AgentLot=" + agent + "; Outcome=" + outcome.ToUpperInvariant() + "; Notes=" + evidence;
                    }
                    if (evidence.Length > 500) throw new InvalidOperationException("Execution evidence exceeds 500 characters.");
                    Services.MicroEquipmentActivityService.SubmitNonUseExecution(
                        connection, transaction, activityId, token, Login.CurrentUser,
                        started, ended, evidence);
                    if (activityType is "Calibration" or "Cleaning" or "Sanitization")
                    {
                        using var structured = new SqlCommand(@"
INSERT dbo.MicroEquipmentStructuredEvidence
(ActivityID,EvidenceType,ReferenceValue,ObservedValue,MaximumAbsoluteError,NormalMinimum,NormalMaximum,CleaningAgentLot,Outcome,RecordedBy)
VALUES(@Activity,@Type,@Reference,@Observed,@Tolerance,@Minimum,@Maximum,@Agent,@Outcome,@Actor);", connection, transaction);
                        structured.Parameters.Add("@Activity", SqlDbType.BigInt).Value = activityId;
                        structured.Parameters.Add("@Type", SqlDbType.NVarChar, 30).Value = activityType;
                        structured.Parameters.Add("@Actor", SqlDbType.NVarChar, 100).Value = Login.CurrentUser;
                        bool useRange = activityType == "Calibration" && !string.IsNullOrWhiteSpace(TxtCalibrationMinimum.Text);
                        var minParameter = structured.Parameters.Add("@Minimum",SqlDbType.Decimal);
                        minParameter.Precision = 18; minParameter.Scale = 6;
                        minParameter.Value = useRange ? decimal.Parse(TxtCalibrationMinimum.Text,CultureInfo.InvariantCulture) : DBNull.Value;
                        var maxParameter = structured.Parameters.Add("@Maximum",SqlDbType.Decimal);
                        maxParameter.Precision = 18; maxParameter.Scale = 6;
                        maxParameter.Value = useRange ? decimal.Parse(TxtCalibrationMaximum.Text,CultureInfo.InvariantCulture) : DBNull.Value;
                        if (activityType == "Calibration")
                        {
                            decimal reference = useRange ? 0m : decimal.Parse(TxtCalibrationReference.Text, CultureInfo.InvariantCulture);
                            decimal observed = decimal.Parse(TxtCalibrationObserved.Text, CultureInfo.InvariantCulture);
                            decimal tolerance = useRange ? 0m : decimal.Parse(TxtCalibrationTolerance.Text, CultureInfo.InvariantCulture);
                            var referenceParameter = structured.Parameters.Add("@Reference", SqlDbType.Decimal);
                            referenceParameter.Precision = 18;
                            referenceParameter.Scale = 6;
                            referenceParameter.Value = useRange ? DBNull.Value : reference;
                            var observedParameter = structured.Parameters.Add("@Observed", SqlDbType.Decimal);
                            observedParameter.Precision = 18;
                            observedParameter.Scale = 6;
                            observedParameter.Value = observed;
                            var toleranceParameter = structured.Parameters.Add("@Tolerance", SqlDbType.Decimal);
                            toleranceParameter.Precision = 18;
                            toleranceParameter.Scale = 6;
                            toleranceParameter.Value = useRange ? DBNull.Value : tolerance;
                            structured.Parameters.Add("@Agent", SqlDbType.NVarChar, 90).Value = DBNull.Value;
                            structured.Parameters.Add("@Outcome", SqlDbType.NVarChar, 10).Value =
                                useRange
                                    ? (observed >= decimal.Parse(TxtCalibrationMinimum.Text, CultureInfo.InvariantCulture) &&
                                       observed <= decimal.Parse(TxtCalibrationMaximum.Text, CultureInfo.InvariantCulture) ? "PASS" : "FAIL")
                                    : (Math.Abs(observed - reference) <= tolerance ? "PASS" : "FAIL");
                        }
                        else
                        {
                            structured.Parameters.Add("@Reference", SqlDbType.Decimal).Value = DBNull.Value;
                            structured.Parameters.Add("@Observed", SqlDbType.Decimal).Value = DBNull.Value;
                            structured.Parameters.Add("@Tolerance", SqlDbType.Decimal).Value = DBNull.Value;
                            structured.Parameters.Add("@Agent", SqlDbType.NVarChar, 90).Value = TxtCleaningAgent.Text.Trim();
                            structured.Parameters.Add("@Outcome", SqlDbType.NVarChar, 10).Value =
                                (CboCleaningOutcome.SelectedItem as ComboBoxItem)?.Content?.ToString()?.ToUpperInvariant() ?? "FAIL";
                        }
                        structured.ExecuteNonQuery();
                        if (activityType == "Calibration")
                        {
                            var lines = TxtCalibrationAdditionalPoints.Text.Split(
                                new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 99)
                                throw new InvalidOperationException("Calibration supports at most 100 measurement points.");
                            var points = new System.Collections.Generic.List<(decimal First, decimal Observed, decimal Third)>
                            {
                                (useRange ? decimal.Parse(TxtCalibrationMinimum.Text, CultureInfo.InvariantCulture)
                                          : decimal.Parse(TxtCalibrationReference.Text, CultureInfo.InvariantCulture),
                                 decimal.Parse(TxtCalibrationObserved.Text, CultureInfo.InvariantCulture),
                                 useRange ? decimal.Parse(TxtCalibrationMaximum.Text, CultureInfo.InvariantCulture)
                                          : decimal.Parse(TxtCalibrationTolerance.Text, CultureInfo.InvariantCulture))
                            };
                            foreach (string line in lines)
                            {
                                string[] parts = line.Trim().Split(',');
                                if (parts.Length != 3 ||
                                    !decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal r) ||
                                    !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal o) ||
                                    !decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out decimal t) ||
                                    (useRange ? r>t : t<0) || decimal.Round(r,6)!=r || decimal.Round(o,6)!=o || decimal.Round(t,6)!=t ||
                                    Math.Abs(r)>=1000000000000m || Math.Abs(o)>=1000000000000m || Math.Abs(t)>=1000000000000m)
                                    throw new InvalidOperationException("Invalid calibration point. Use reference,observed,max-error or minimum,observed,maximum, with up to 6 decimals.");
                                points.Add((r,o,t));
                            }
                            for (int index=0; index<points.Count; index++)
                            {
                                using var pointCommand = new SqlCommand(@"
INSERT dbo.MicroEquipmentCalibrationPoints(ActivityID,PointNo,ReferenceValue,ObservedValue,MaxAbsoluteError,NormalMinimum,NormalMaximum)
VALUES(@Activity,@Point,@Reference,@Observed,@Tolerance,@Minimum,@Maximum);",connection,transaction);
                                pointCommand.Parameters.Add("@Activity",SqlDbType.BigInt).Value=activityId;
                                pointCommand.Parameters.Add("@Point",SqlDbType.Int).Value=index+1;
                                var rp=pointCommand.Parameters.Add("@Reference",SqlDbType.Decimal); rp.Precision=18; rp.Scale=6; rp.Value=useRange ? DBNull.Value : points[index].First;
                                var op=pointCommand.Parameters.Add("@Observed",SqlDbType.Decimal); op.Precision=18; op.Scale=6; op.Value=points[index].Observed;
                                var tp=pointCommand.Parameters.Add("@Tolerance",SqlDbType.Decimal); tp.Precision=18; tp.Scale=6; tp.Value=useRange ? DBNull.Value : points[index].Third;
                                var minp=pointCommand.Parameters.Add("@Minimum",SqlDbType.Decimal); minp.Precision=18; minp.Scale=6; minp.Value=useRange ? points[index].First : DBNull.Value;
                                var maxp=pointCommand.Parameters.Add("@Maximum",SqlDbType.Decimal); maxp.Precision=18; maxp.Scale=6; maxp.Value=useRange ? points[index].Third : DBNull.Value;
                                pointCommand.ExecuteNonQuery();
                            }
                        }
                    }
                });
                if (_selectedEquipmentId.HasValue) LoadEquipmentActivityHistory(_selectedEquipmentId.Value);
                LblStatus.Text = "Equipment activity " + activityId +
                    " submitted with execution details. Review and approval remain pending.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Submit equipment execution"),
                    "Submit Equipment Execution", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Match the locally authenticated Windows account to the PharmaLIMS account.
        // A domain account or a foreign machine's same-named account is not accepted.
        // The independent trusted service still authenticates and authorizes the Windows token.
        private async void BtnReviewActivity_Click(object sender, RoutedEventArgs e) =>
            await SignActivityStageAsync("Reviewed");

        private async void BtnApproveActivity_Click(object sender, RoutedEventArgs e) =>
            await SignActivityStageAsync("Approved");

        private async Task SignActivityStageAsync(string stage)
        {
            var user = _authService?.GetCurrentUser();
            if (user is null || !user.IsActive || user.IsLocked || user.MustChangePassword ||
                (stage == "Reviewed" ? !user.CanReviewResults : !user.CanApproveResults))
            {
                MessageBox.Show("A current authorized account is required for this activity stage.", "Equipment Activity Signature");
                return;
            }
            if (!long.TryParse(TxtActivityId.Text.Trim(), out long id) || id <= 0 || !_selectedEquipmentId.HasValue)
            {
                MessageBox.Show("Select the equipment and enter a valid Activity ID.", "Equipment Activity Signature");
                return;
            }
            try
            {
                int equipmentId = _selectedEquipmentId.Value;
                string username = user.Username;
                DataTable row = await _database.ExecuteQueryAsync(
                    "SELECT VersionToken FROM dbo.MicroEquipmentActivities WHERE ActivityID=@Activity AND EquipmentID=@Equipment AND ActivityStatus=@Prior;",
                    new[] {
                        new SqlParameter("@Activity", SqlDbType.BigInt) { Value = id },
                        new SqlParameter("@Equipment", SqlDbType.Int) { Value = equipmentId },
                        new SqlParameter("@Prior", SqlDbType.NVarChar, 30) { Value = stage == "Reviewed" ? "Submitted" : "Reviewed" }
                    }, commandTimeoutSeconds:10);
                if (!IsLoaded || _selectedEquipmentId != equipmentId ||
                    !string.Equals(_authService?.GetCurrentUser()?.Username, username, StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("Selection or authenticated account changed. Reload before signing.");
                if (row.Rows.Count != 1 || row.Rows[0]["VersionToken"] is not byte[] version || version.Length != 8)
                    throw new InvalidOperationException("Activity stage or version changed. Reload before signing.");
                var databaseName = new SqlConnectionStringBuilder(AppConfig.ConnectionString).InitialCatalog;
                var request = new ActivitySigningRequest(Guid.NewGuid(), id, stage, TxtActivitySignatureReason.Text.Trim(),
                    DateTimeOffset.UtcNow, version, databaseName);
                var dialog = new TrustedEquipmentSignature(request, username, TrustedEquipmentSigningClient.CreateFromConfiguration()) { Owner = this };
                if (dialog.ShowDialog() != true || !dialog.IsConfirmed) return;
                await LoadEquipmentActivityHistoryAsync(equipmentId);
                LblStatus.Text = "Activity " + id + " stage signed by the trusted service: " + stage + ".";
                TxtActivitySignatureReason.Clear();
            }
            catch (Exception exception)
            {
                MessageBox.Show(UserFacingError.SafeMessage(exception, "Trusted equipment activity signing"),
                    "Equipment Activity Signature", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e) => ClearForm();

        private void ClearForm()
        {
            _selectedEquipmentId = null;
            _selectedRowVersion = null;
            ++_activityHistoryRequest;
            GridActivityHistory.ItemsSource = null;
            LblActivityHistory.Text = "Select equipment to view its activity history.";
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

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadEquipmentAsync();
        private async void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedEquipmentId.HasValue)
            {
                int id = _selectedEquipmentId.Value;
                await LoadEquipmentAsync();
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
                await LoadEquipmentAsync();
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (GridEquipment != null)
                ApplySearchFilter();
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
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
                ClearForm();
                await LoadEquipmentAsync();
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
