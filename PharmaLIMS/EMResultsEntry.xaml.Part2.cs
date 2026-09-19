#nullable disable
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PharmaLIMS
{
    public partial class EMResultsEntry
    {
        private void AddConditionIfColumnExists(List<string> conditions, string tableName, string columnName, string conditionSql)
        {
            if (ColumnExists(tableName, columnName))
                conditions.Add(conditionSql);
        }

        private void BtnOpenDeviation_Click(object sender, RoutedEventArgs e)
        {
            if (currentEventId <= 0)
            {
                MessageBox.Show("No EM event is loaded.", "Quality Event", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CalculateAllStatuses();
            RefreshGrid();
            RefreshCounters();

            if (!HasQualityEventTriggerResults() && currentQualityEventId <= 0)
            {
                MessageBox.Show(
                    "No ALERT/ACTION/OOS result is available for this EM event. Quality Event is not required.",
                    "Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (currentQualityEventId <= 0)
            {
                CheckLinkedDeviation();
            }

            if (currentQualityEventId <= 0)
            {
                if (!CreateEMQualityEventFromCurrentResults())
                    return;

                CheckLinkedDeviation();
            }

            if (currentQualityEventId <= 0)
            {
                MessageBox.Show(
                    "Unable to create or locate a linked Quality Event Investigation for this EM event.",
                    "Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            OpenCurrentQualityEventWindow();
            CheckLinkedDeviation();
            UpdateWorkflowButtons();
        }

        private void OpenCurrentQualityEventWindow()
        {
            try
            {
                if (currentQualityEventId <= 0)
                {
                    MessageBox.Show("No Quality Event is linked to this EM event.", "Quality Event",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Controlled EM checklist master data is verified by System Preflight.
                // Operational screens never seed or alter checklist definitions.
                // Use constructor with qualityEventId
                var investigationWindow = new QualityEventInvestigation(currentQualityEventId);
                investigationWindow.Owner = this;
                investigationWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to open Quality Event Investigation: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Quality Event", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private bool CreateEMQualityEventFromCurrentResults()
        {
            if (!TableExists("QualityEvents"))
            {
                MessageBox.Show("Quality Events table is not available. Please install the Quality Event database update first.",
                    "Quality Event", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            try
            {
                string eventType = HasOosResults() ? "OOS" : "Alert";
                string severity = HasOosResults() ? "Major" : "Minor";
                string profile = GetCurrentEMInvestigationProfile();
                string eventNumber = GenerateEMQualityEventNumber();
                string description = BuildEMQualityEventDescription(eventType, profile);
                string immediateAction = HasOosResults()
                    ? "EM event placed under Quality Event Investigation. Final approval and final report printing are blocked pending QA disposition."
                    : "Alert result recorded. Trend follow-up and QA assessment required according to the approved EM procedure.";

                List<string> columns = new List<string>();
                List<string> values = new List<string>();
                List<SqlParameter> pars = new List<SqlParameter>();

                AddInsertValue(columns, values, pars, "QualityEvents", "EventNumber", "@EventNumber", eventNumber);
                AddInsertValue(columns, values, pars, "QualityEvents", "QualityEventNo", "@EventNumber", eventNumber);
                AddInsertValue(columns, values, pars, "QualityEvents", "EventType", "@EventType", eventType);
                AddInsertValue(columns, values, pars, "QualityEvents", "Severity", "@Severity", severity);
                AddInsertValue(columns, values, pars, "QualityEvents", "SampleID", "@SampleID", DBNull.Value);
                AddInsertValue(columns, values, pars, "QualityEvents", "SampleNumber", "@SampleNumber", currentEventNo);
                AddInsertValue(columns, values, pars, "QualityEvents", "CurrentStatus", "@CurrentStatus", "Open");
                AddInsertValue(columns, values, pars, "QualityEvents", "Status", "@CurrentStatus", "Open");
                AddInsertValue(columns, values, pars, "QualityEvents", "InvestigationStatus", "@CurrentStatus", "Open");
                AddInsertValue(columns, values, pars, "QualityEvents", "DetectedBy", "@DetectedBy", currentUser);
                AddInsertValue(columns, values, pars, "QualityEvents", "DetectedDate", "GETDATE()", null, false);
                AddInsertValue(columns, values, pars, "QualityEvents", "DetectionSource", "@DetectionSource", "Environmental Monitoring");
                AddInsertValue(columns, values, pars, "QualityEvents", "SourceType", "@DetectionSource", "Environmental Monitoring");
                AddInsertValue(columns, values, pars, "QualityEvents", "SourceRecordID", "@SourceRecordID", currentEventId);
                AddInsertValue(columns, values, pars, "QualityEvents", "SourceRecordId", "@SourceRecordID", currentEventId);
                AddInsertValue(columns, values, pars, "QualityEvents", "RelatedRecordID", "@SourceRecordID", currentEventId);
                AddInsertValue(columns, values, pars, "QualityEvents", "RelatedRecordId", "@SourceRecordID", currentEventId);
                AddInsertValue(columns, values, pars, "QualityEvents", "SourceReferenceNo", "@SourceReferenceNo", currentEventNo);
                AddInsertValue(columns, values, pars, "QualityEvents", "RelatedRecordNo", "@SourceReferenceNo", currentEventNo);
                AddInsertValue(columns, values, pars, "QualityEvents", "ReferenceNo", "@SourceReferenceNo", currentEventNo);
                AddInsertValue(columns, values, pars, "QualityEvents", "InitialDescription", "@InitialDescription", description);
                AddInsertValue(columns, values, pars, "QualityEvents", "ImmediateAction", "@ImmediateAction", immediateAction);
                AddInsertValue(columns, values, pars, "QualityEvents", "InvestigationProfile", "@InvestigationProfile", profile);
                AddInsertValue(columns, values, pars, "QualityEvents", "CAPARequired", "@CAPARequired", HasOosResults() ? 1 : 0);
                AddInsertValue(columns, values, pars, "QualityEvents", "CreatedDate", "GETDATE()", null, false);
                AddInsertValue(columns, values, pars, "QualityEvents", "ModifiedBy", "@ModifiedBy", currentUser);
                AddInsertValue(columns, values, pars, "QualityEvents", "ModifiedDate", "GETDATE()", null, false);

                if (columns.Count == 0)
                    throw new InvalidOperationException("QualityEvents table does not contain supported columns.");

                string idColumn = FirstExistingColumn("QualityEvents", "QualityEventID", "QualityEventId", "Id", "EventID", "EventId");
                if (string.IsNullOrWhiteSpace(idColumn))
                    throw new InvalidOperationException("QualityEvents primary key column was not found.");

                string query = "INSERT INTO dbo.QualityEvents (" + string.Join(", ", columns) + ") OUTPUT INSERTED.[" + idColumn.Replace("]", "]]", StringComparison.Ordinal) + "] VALUES (" + string.Join(", ", values) + ");";
                int createdQualityEventId = 0;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    using (SqlCommand createCmd = new SqlCommand(query, conn, tx))
                    {
                        createCmd.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        createCmd.Parameters.AddRange(pars.ToArray());

                        object created = createCmd.ExecuteScalar();
                        if (created == null || created == DBNull.Value)
                            throw new InvalidOperationException("Quality Event was not created.");

                        createdQualityEventId = Convert.ToInt32(created, CultureInfo.InvariantCulture);
                    }

                    int affectedResultCount = InsertEMQualityEventAffectedResults(createdQualityEventId, conn, tx);
                    if (affectedResultCount <= 0)
                        throw new InvalidOperationException("No EM ALERT/ACTION/OOS result was linked to the Quality Event. The Quality Event was not committed.");

                    InsertEMQualityEventAction(
                        createdQualityEventId,
                        "System Opened",
                        "Quality Event opened from EM Results Entry for " + eventType + " result(s).",
                        currentUser,
                        conn,
                        tx);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn,
                        tx,
                        "QualityEvents",
                        createdQualityEventId,
                        "Open Quality Event",
                        "",
                        "EM Event=" + currentEventNo + "; EventType=" + eventType + "; Profile=" + profile,
                        "Quality Event opened from EM Results Entry.",
                        currentUser);
                });

                currentQualityEventId = createdQualityEventId;
                currentQualityEventNo = eventNumber;

                MessageBox.Show(
                    "Quality Event Investigation created successfully.\n\nQuality Event No.: " + currentQualityEventNo,
                    "Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Unable to create Quality Event Investigation: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        private void AddInsertValue(List<string> columns, List<string> values, List<SqlParameter> pars, string tableName, string columnName, string sqlValue, object parameterValue, bool addParameter = true)
        {
            if (!ColumnExists(tableName, columnName))
                return;

            string normalizedColumn = "[" + columnName + "]";

            if (columns.Any(c => c.Equals(normalizedColumn, StringComparison.OrdinalIgnoreCase)))
                return;

            columns.Add(normalizedColumn);
            values.Add(sqlValue);

            if (addParameter &&
                sqlValue.StartsWith("@", StringComparison.OrdinalIgnoreCase) &&
                !pars.Any(p => p.ParameterName.Equals(sqlValue, StringComparison.OrdinalIgnoreCase)))
            {
                pars.Add(new SqlParameter(sqlValue, parameterValue ?? DBNull.Value));
            }
        }

        private string GenerateEMQualityEventNumber()
        {
            return "QE-EM-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        }

        private string GetCurrentEMInvestigationProfile()
        {
            bool hasActive = plateItems.Any(p => IsActiveAirSampling(p.Method));
            bool hasSettle = plateItems.Any(p => IsSettlePlate(p.Method));
            bool hasContact = plateItems.Any(p => IsContactPlate(p.Method));
            bool hasSwab = plateItems.Any(p => IsSurfaceSwab(p.Method));
            bool hasPersonnel = plateItems.Any(p => IsPersonnelMonitoring(p.Method));

            int methodGroups = new[] { hasActive, hasSettle, hasContact, hasSwab, hasPersonnel }.Count(v => v);
            if (methodGroups > 1)
                return "Environmental Monitoring - Mixed Event";

            if (hasActive) return "Environmental Monitoring - Active Air Sampling";
            if (hasSettle) return "Environmental Monitoring - Settle Plate";
            if (hasContact) return "Environmental Monitoring - Contact Plate";
            if (hasSwab) return "Environmental Monitoring - Surface Swab";
            if (hasPersonnel) return "Environmental Monitoring - Personnel Monitoring";

            return "Environmental Monitoring";
        }

        private string BuildEMQualityEventDescription(string eventType, string profile)
        {
            List<string> affected = plateItems
                .Where(p => string.Equals(p.Status, "OOS", StringComparison.OrdinalIgnoreCase) || string.Equals(p.Status, "Alert", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.PlateCode + " (" + p.Method + ": " + FirstNonEmpty(p.ResultCFU, p.TotalCount?.ToString(CultureInfo.InvariantCulture)) + " " + GetReportUnitDisplay(p) + ", " + p.Status + ")")
                .ToList();

            return "Environmental Monitoring " + eventType + " detected for EM Event " + currentEventNo + ". Profile: " + profile + ". Affected plate(s): " + string.Join("; ", affected) + ".";
        }

        private int InsertEMQualityEventAffectedResults(
            int qualityEventId,
            SqlConnection conn,
            SqlTransaction tx)
        {
            if (!TableExists("QualityEventAffectedResults"))
                throw new InvalidOperationException("QualityEventAffectedResults table is missing. Run System Preflight before EM workflow use.");

            int inserted = 0;
            foreach (EMPlateResultItem item in plateItems)
            {
                if (!string.Equals(item.Status, "OOS", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.Status, "Alert", StringComparison.OrdinalIgnoreCase))
                    continue;

                List<string> columns = new List<string>();
                List<string> values = new List<string>();
                List<SqlParameter> pars = new List<SqlParameter>();

                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "QualityEventID", "@QualityEventID", qualityEventId);
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "QualityEventId", "@QualityEventID", qualityEventId);
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "SampleTestID", "@SampleTestID", DBNull.Value);
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "TestID", "@TestID", DBNull.Value);
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "TestName", "@TestName", item.Method + " - " + item.PlateCode);
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "ResultValue", "@ResultValue", GetReportResultDisplay(item));
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "SpecificationLimit", "@SpecificationLimit", BuildEMSpecificationLimitText(item));
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "Unit", "@Unit", GetReportUnitDisplay(item));
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "FailureType", "@FailureType", item.Status);
                AddInsertValue(columns, values, pars, "QualityEventAffectedResults", "CreatedDate", "GETDATE()", null, false);

                if (columns.Count == 0)
                    throw new InvalidOperationException("QualityEventAffectedResults does not contain supported columns.");

                string query = "INSERT INTO dbo.QualityEventAffectedResults (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", values) + ");";
                int rows = DatabaseHelper.ExecuteNonQueryWithTransaction(query, pars.ToArray(), conn, tx);
                if (rows != 1)
                    throw new InvalidOperationException("An EM affected result could not be linked to the Quality Event.");
                inserted += rows;
            }

            return inserted;
        }

        private string BuildEMSpecificationLimitText(EMPlateResultItem item)
        {
            string unit = GetReportUnitDisplay(item);
            string alert = item.AlertLimit.HasValue ? "Alert NMT " + item.AlertLimit.Value.ToString(CultureInfo.InvariantCulture) + " " + unit : "Alert Not defined";
            string action = item.ActionLimit.HasValue ? "Action NMT " + item.ActionLimit.Value.ToString(CultureInfo.InvariantCulture) + " " + unit : "Action Not defined";
            return alert + "; " + action;
        }

        private void InsertEMQualityEventAction(
            int qualityEventId,
            string actionType,
            string actionDescription,
            string performedBy,
            SqlConnection conn,
            SqlTransaction tx)
        {
            if (!TableExists("QualityEventActions"))
                throw new InvalidOperationException("QualityEventActions table is missing. Run System Preflight before EM workflow use.");

            List<string> columns = new List<string>();
            List<string> values = new List<string>();
            List<SqlParameter> pars = new List<SqlParameter>();

            AddInsertValue(columns, values, pars, "QualityEventActions", "QualityEventID", "@QualityEventID", qualityEventId);
            AddInsertValue(columns, values, pars, "QualityEventActions", "QualityEventId", "@QualityEventID", qualityEventId);
            AddInsertValue(columns, values, pars, "QualityEventActions", "ActionType", "@ActionType", actionType);
            AddInsertValue(columns, values, pars, "QualityEventActions", "ActionDescription", "@ActionDescription", actionDescription);
            AddInsertValue(columns, values, pars, "QualityEventActions", "PerformedBy", "@PerformedBy", performedBy);
            AddInsertValue(columns, values, pars, "QualityEventActions", "PerformedDate", "GETDATE()", null, false);

            if (columns.Count == 0)
                throw new InvalidOperationException("QualityEventActions does not contain supported columns.");

            string query = "INSERT INTO dbo.QualityEventActions (" + string.Join(", ", columns) + ") VALUES (" + string.Join(", ", values) + ");";
            int rows = DatabaseHelper.ExecuteNonQueryWithTransaction(query, pars.ToArray(), conn, tx);
            if (rows != 1)
                throw new InvalidOperationException("The Quality Event opening action could not be recorded.");
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void dgPlates_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (isSaving) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                CalculateAllStatuses();
                RefreshGrid();
                RefreshCounters();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void LoadEMEventFromSearch()
        {
            string eventNo = txtEventNo.Text.Trim();

            if (string.IsNullOrWhiteSpace(eventNo))
            {
                MessageBox.Show("Please enter EM Event No.", "Search", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadEMEvent(eventNo);
        }

        private void LoadPendingEMEvents()
        {
            try
            {
                string query = @"
                    SELECT TOP 50
                        E.EventNo,
                        E.EventDate,
                        A.AreaCode,
                        A.Grade,
                        COALESCE(NULLIF(LTRIM(RTRIM(E.WorkflowStatus)), ''), 'Pending') AS WorkflowStatus,
                        COALESCE(NULLIF(LTRIM(RTRIM(E.FinalResult)), ''), 'Pending') AS ResultStatus,
                        ISNULL(P.PlanNo,N'') AS PlanNo
                    FROM EM_Events E
                    INNER JOIN EM_Areas A ON E.AreaId = A.Id
                    LEFT JOIN EM_Plans P ON P.PlanID=E.PlanID
                    WHERE UPPER(COALESCE(NULLIF(LTRIM(RTRIM(E.WorkflowStatus)), ''), 'PENDING'))
                          NOT IN ('APPROVED', 'CLOSED', 'CANCELLED')
                    ORDER BY E.Id DESC;";

                DataTable dt = DatabaseHelper.ExecuteQuery(query);

                if (dt.Rows.Count == 0)
                {
                    MessageBox.Show("No open EM events found.", "Open EM Events",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Window picker = new Window
                {
                    Title = "Open EM Events",
                    Owner = this,
                    Width = 760,
                    Height = 480,
                    MinWidth = 620,
                    MinHeight = 360,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Grid layout = new Grid { Margin = new Thickness(12) };
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                TextBlock instruction = new TextBlock
                {
                    Text = "Select an open EM event. Workflow and microbiological result are shown separately.",
                    Margin = new Thickness(0, 0, 0, 10),
                    FontWeight = FontWeights.SemiBold
                };
                Grid.SetRow(instruction, 0);
                layout.Children.Add(instruction);

                DataGrid eventsGrid = new DataGrid
                {
                    ItemsSource = dt.DefaultView,
                    AutoGenerateColumns = false,
                    IsReadOnly = true,
                    SelectionMode = DataGridSelectionMode.Single,
                    SelectionUnit = DataGridSelectionUnit.FullRow,
                    CanUserAddRows = false,
                    HeadersVisibility = DataGridHeadersVisibility.Column
                };
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Event No.", Binding = new Binding("EventNo"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Date", Binding = new Binding("EventDate") { StringFormat = "yyyy-MM-dd" }, Width = new DataGridLength(1.2, DataGridLengthUnitType.Star) });
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Area", Binding = new Binding("AreaCode"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Grade", Binding = new Binding("Grade"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Plan No.", Binding = new Binding("PlanNo"), Width = new DataGridLength(1.5, DataGridLengthUnitType.Star) });
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Workflow", Binding = new Binding("WorkflowStatus"), Width = new DataGridLength(1.35, DataGridLengthUnitType.Star) });
                eventsGrid.Columns.Add(new DataGridTextColumn { Header = "Result", Binding = new Binding("ResultStatus"), Width = new DataGridLength(1.25, DataGridLengthUnitType.Star) });
                Grid.SetRow(eventsGrid, 1);
                layout.Children.Add(eventsGrid);

                StackPanel actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                Button openButton = new Button { Content = "Open Event", Width = 120, Height = 34, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                Button cancelButton = new Button { Content = "Cancel", Width = 90, Height = 34, IsCancel = true };
                actions.Children.Add(openButton);
                actions.Children.Add(cancelButton);
                Grid.SetRow(actions, 2);
                layout.Children.Add(actions);
                picker.Content = layout;

                Action openSelected = () =>
                {
                    if (eventsGrid.SelectedItem is DataRowView)
                        picker.DialogResult = true;
                };
                openButton.Click += (sender, args) => openSelected();
                eventsGrid.MouseDoubleClick += (sender, args) => openSelected();

                if (picker.ShowDialog() == true && eventsGrid.SelectedItem is DataRowView selected)
                {
                    string selectedEventNo = selected["EventNo"]?.ToString() ?? "";
                    txtEventNo.Text = selectedEventNo;
                    LoadEMEvent(selectedEventNo);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading pending EM events: " + Infrastructure.UserFacingError.SafeMessage(ex),
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadEMEvent(string eventNo)
        {
            try
            {
                ClearDisplay();

                string eventQuery = @"
                    SELECT
                        E.Id,
                        E.PlanID,
                        E.EventNo,
                        E.EventDate,
                        E.MediaUsed,
                        E.MediaLotNo,
                        E.FinalResult,

                        E.SanitizationDetails,
                        E.SanitizationTime,
                        E.DisinfectantUsed,
                        E.SamplingTimeFrom,
                        E.SamplingTimeTo,
                        E.ActivityNoOfPersons,
                        E.AirSamplerNo,
                        E.AirSamplingTime,
                        E.IncubationTemperature,
                        COALESCE(NULLIF(E.IncubatorNo1,N''),
                            (SELECT TOP 1 S.BacteriaIncubatorID FROM EM_PlanSamples S WHERE S.PlanID=E.PlanID AND S.IsNegativeControl=0 ORDER BY S.PlanSampleID)) AS IncubatorNo1,
                        COALESCE(NULLIF(E.IncubatorNo2,N''),
                            (SELECT TOP 1 S.FungiIncubatorID FROM EM_PlanSamples S WHERE S.PlanID=E.PlanID AND S.IsNegativeControl=0 ORDER BY S.PlanSampleID)) AS IncubatorNo2,
                        E.IncubationStart,
                        E.IncubationEnd,
                        E.Remarks,
                        E.MonitoringCategory,
                        E.DispensingBooth,
                        E.MaterialName,
                        E.BatchNo,
                        E.EmployeeId,
                        E.EmployeeName,
                        E.EmployeeDepartment,
                        E.EmployeeShift,
                        E.SamplingStage,
                        E.SurfaceLocation,
                        E.SurfaceType,
                        E.SurfaceAreaCm2,
                        E.SwabKitLot,
                        E.DiluentLot,
                        E.RecoveryVolumeMl,
                        COALESCE(NULLIF(E.NegativeControlResult,N''),
                            (SELECT TOP 1 S.NegativeControlResult FROM EM_PlanSamples S WHERE S.PlanID=E.PlanID AND S.IsNegativeControl=1 ORDER BY S.PlanSampleID)) AS NegativeControlResult,
                        ISNULL(P.PlanNo,N'') AS PlanNo,

                        A.AreaCode,
                        A.AreaName,
                        A.AreaGroup,
                        A.Grade
                    FROM EM_Events E
                    INNER JOIN EM_Areas A ON E.AreaId = A.Id
                    LEFT JOIN EM_Plans P ON P.PlanID=E.PlanID
                    WHERE E.EventNo = @eventNo;";

                SqlParameter[] eventPars =
                {
                    new SqlParameter("@eventNo", eventNo)
                };

                DataTable eventTable = DatabaseHelper.ExecuteQuery(eventQuery, eventPars);

                if (eventTable.Rows.Count == 0)
                {
                    MessageBox.Show($"EM Event {eventNo} not found.", "Not Found",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    ClearDisplay();
                    return;
                }

                DataRow row = eventTable.Rows[0];

                currentEventId = row.GetSafeInt("Id");
                currentPlanId = row.GetSafeInt("PlanID");
                currentEventNo = row.GetSafeString("EventNo");
                currentPlanNo = row.GetSafeString("PlanNo");
                currentNegativeControlResult = row.GetSafeString("NegativeControlResult");
                currentFinalResult = row.GetSafeString("FinalResult");
                if (string.IsNullOrWhiteSpace(currentFinalResult))
                    currentFinalResult = "Pending";

                lblEventNo.Text = currentEventNo;
                lblPlanNo.Text = string.IsNullOrWhiteSpace(currentPlanNo) ? "N/A" : currentPlanNo;
                lblEventDate.Text = row.GetSafeDateTime("EventDate")?.ToString("yyyy-MM-dd") ?? "";
                lblArea.Text = $"{row.GetSafeString("AreaCode")} - {row.GetSafeString("AreaName")} - {row.GetSafeString("AreaGroup")}";
                lblGrade.Text = row.GetSafeString("Grade");
                lblMediaUsed.Text = row.GetSafeString("MediaUsed");
                lblMediaLotNo.Text = row.GetSafeString("MediaLotNo");

                currentSanitizationDetails = row.GetSafeString("SanitizationDetails");
                currentSanitizationTime = row.GetSafeString("SanitizationTime");
                currentDisinfectantUsed = row.GetSafeString("DisinfectantUsed");
                currentSamplingTimeFrom = row.GetSafeString("SamplingTimeFrom");
                currentSamplingTimeTo = row.GetSafeString("SamplingTimeTo");
                currentActivityNoOfPersons = row.GetSafeString("ActivityNoOfPersons");
                currentAirSamplerNo = row.GetSafeString("AirSamplerNo");
                currentAirSamplingTime = row.GetSafeString("AirSamplingTime");
                currentIncubationTemperature = row.GetSafeString("IncubationTemperature");
                currentIncubatorNo1 = row.GetSafeString("IncubatorNo1");
                currentIncubatorNo2 = row.GetSafeString("IncubatorNo2");
                currentIncubationStart = row.GetSafeString("IncubationStart");
                currentIncubationEnd = row.GetSafeString("IncubationEnd");
                currentRemarks = row.GetSafeString("Remarks");
                currentMonitoringCategory = row.GetSafeString("MonitoringCategory");
                currentDispensingBooth = row.GetSafeString("DispensingBooth");
                currentMaterialName = row.GetSafeString("MaterialName");
                currentBatchNo = row.GetSafeString("BatchNo");
                currentEmployeeId = row.GetSafeString("EmployeeId");
                currentEmployeeName = row.GetSafeString("EmployeeName");
                currentEmployeeDepartment = row.GetSafeString("EmployeeDepartment");
                currentEmployeeShift = row.GetSafeString("EmployeeShift");
                currentSamplingStage = row.GetSafeString("SamplingStage");
                currentSurfaceLocation = row.GetSafeString("SurfaceLocation");
                currentSurfaceType = row.GetSafeString("SurfaceType");
                currentSurfaceAreaCm2 = row.GetSafeString("SurfaceAreaCm2");
                currentSwabKitLot = row.GetSafeString("SwabKitLot");
                currentDiluentLot = row.GetSafeString("DiluentLot");
                currentRecoveryVolumeMl = row.GetSafeString("RecoveryVolumeMl");

                workflowStatus = DatabaseHelper.GetEMWorkflowStatus(currentEventId);

                LoadPlates(currentEventId);
                CalculateAllStatuses();
                RefreshGrid();
                RefreshCounters();

                SetFinalResultStatus(currentFinalResult);
                CheckLinkedDeviation();
                UpdateWorkflowButtons();

                lblStatus.Text = "EM event loaded successfully. Workflow status: " + workflowStatus;
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading EM event: " + Infrastructure.UserFacingError.SafeMessage(ex),
                                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadPlates(int eventId)
        {
            plateItems.Clear();

            _loadedPlateSnapshot = null;
            string platesQuery = PlateSnapshotSql(false);

            SqlParameter[] pars =
            {
                new SqlParameter("@eventId", eventId)
            };

            DataTable dt = DatabaseHelper.ExecuteQuery(platesQuery, pars);

            foreach (DataRow row in dt.Rows)
            {
                string method = row.GetSafeString("Method");
                string grade = row.GetSafeString("Grade");

                bool hasAlertSnapshot = row["AlertLimitSnapshot"] != DBNull.Value;
                bool hasActionSnapshot = row["ActionLimitSnapshot"] != DBNull.Value;
                bool activeAir = IsActiveAirSampling(method);
                bool hasAirVolumeSnapshot = !activeAir || row["AirVolumeLitersSnapshot"] != DBNull.Value;
                string frozenUnit = row.GetSafeString("ResultUnitSnapshot");
                if (!hasAlertSnapshot || !hasActionSnapshot || string.IsNullOrWhiteSpace(frozenUnit) || !hasAirVolumeSnapshot)
                {
                    throw new InvalidOperationException(
                        $"Approved EM limit snapshot is missing for plate {row.GetSafeString("PlateCode")} ({grade} / {method}). " +
                        "PharmaLIMS will not substitute current master limits for historical evidence. Use Reconcile Historical Snapshot with QA electronic signature and a controlled evidence reference.");
                }

                decimal? effectiveAlert = row.GetSafeDecimal("AlertLimitSnapshot");
                decimal? effectiveAction = row.GetSafeDecimal("ActionLimitSnapshot");
                string effectiveUnit = frozenUnit;
                int? effectiveAirVolume = activeAir
                    ? row.GetSafeInt("AirVolumeLitersSnapshot", 0)
                    : null;

                EMPlateResultItem item = new EMPlateResultItem
                {
                    PlateId = row.GetSafeInt("Id"),
                    EventId = row.GetSafeInt("EventId"),
                    Method = method,
                    PlateCode = row.GetSafeString("PlateCode"),
                    SequenceNo = row.GetSafeInt("SequenceNo"),
                    Grade = grade,
                    Unit = effectiveUnit,
                    AlertLimit = effectiveAlert,
                    ActionLimit = effectiveAction,
                    HasLimitSnapshot = hasAlertSnapshot && hasActionSnapshot && !string.IsNullOrWhiteSpace(frozenUnit) && hasAirVolumeSnapshot,
                    LimitReconciliationId = row["ReconciliationID"] == DBNull.Value ? null : row.GetSafeInt("ReconciliationID"),
                    LimitEvidenceSource = row.GetSafeString("EvidenceSource"),
                    TotalCount = EmResultCalculator.ReadStoredCount(row["TotalCount"]),
                    AirVolumeLiters = effectiveAirVolume,
                    ResultCFU = row.GetSafeString("ResultCFU"),
                    Status = row.GetSafeString("Status"),
                    Remarks = row.GetSafeString("ColoniesObserved")
                };

                if (string.IsNullOrWhiteSpace(item.Status))
                    item.Status = "Pending";

                if (IsDirectCountMethod(method))
                {
                    item.AirVolumeLiters = null;
                    if (item.TotalCount.HasValue && string.IsNullOrWhiteSpace(item.ResultCFU))
                        item.ResultCFU = item.TotalCount.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (!item.AlertLimit.HasValue || !item.ActionLimit.HasValue)
                {
                    item.Status = "Warning: No Limits";
                }

                plateItems.Add(item);
            }

            _loadedPlateSnapshot = dt.Copy();
            _loadedPlateSnapshot.AcceptChanges();

            if (plateItems.Count == 0)
            {
                MessageBox.Show("No plates found for this EM event.", "No Plates",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private bool IsActiveAirSampling(string method)
        {
            return EmResultCalculator.IsActiveAirSampling(method);
        }

        private bool IsSettlePlate(string method)
        {
            return method.TrimSafe().ContainsIgnoreCase("settle");
        }

        private bool IsContactPlate(string method)
        {
            return method.TrimSafe().ContainsIgnoreCase("contact plate");
        }

        private bool IsSurfaceSwab(string method)
        {
            return method.TrimSafe().ContainsIgnoreCase("surface swab") ||
                   method.TrimSafe().Equals("swab", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPersonnelMonitoring(string method)
        {
            return method.TrimSafe().ContainsIgnoreCase("personnel") ||
                   method.TrimSafe().ContainsIgnoreCase("glove");
        }

        private bool IsDirectCountMethod(string method)
        {
            return EmResultCalculator.IsDirectCountMethod(method);
        }

        private bool HasEnteredResult(EMPlateResultItem item)
        {
            return item?.TotalCount.HasValue == true;
        }

        private void CalculateAllStatuses()
        {
            foreach (EMPlateResultItem item in plateItems)
            {
                CalculateItemResult(item);
                item.Status = CalculatePlateStatus(item);
            }
        }

        private void CalculateItemResult(EMPlateResultItem item)
        {
            if (item == null) return;
            if (item.TotalCount < 0) { item.ResultCFU = ""; return; }
            if (IsDirectCountMethod(item.Method)) item.AirVolumeLiters = null;
            // Never parse a formatted result to decide conformity.
            item.ResultCFU = EmResultCalculator.Format(
                EmResultCalculator.CalculateValue(item.TotalCount, item.Method, item.AirVolumeLiters));
        }

        private string CalculatePlateStatus(EMPlateResultItem item)
        {
            if (item == null) return "Pending";
            if (item.TotalCount < 0) return "Invalid";
            return EmResultCalculator.Calculate(
                item.TotalCount, item.Method, item.AirVolumeLiters, item.AlertLimit, item.ActionLimit).Status;
        }

        private decimal? GetResultValue(EMPlateResultItem item)
        {
            return item == null || item.TotalCount < 0 ? null : EmResultCalculator.CalculateValue(
                item.TotalCount, item.Method, item.AirVolumeLiters);
        }

        private string CalculateFinalResult()
        {
            bool hasOos = false;
            bool hasAlert = false;
            bool hasPending = false;
            bool hasEntered = false;

            foreach (EMPlateResultItem item in plateItems)
            {
                if (HasEnteredResult(item))
                    hasEntered = true;
                else
                    hasPending = true;

                if (string.Equals(item.Status, "OOS", StringComparison.OrdinalIgnoreCase))
                    hasOos = true;
                else if (string.Equals(item.Status, "Alert", StringComparison.OrdinalIgnoreCase))
                    hasAlert = true;
            }

            if (hasOos) return "OOS";
            if (hasAlert) return "Alert";
            if (hasPending) return hasEntered ? "Partially Entered" : "Pending";
            return "Results Entered";
        }

        private bool ValidateResultItem(EMPlateResultItem item)
        {
            if (item == null) return false;

            if (IsActiveAirSampling(item.Method) && item.TotalCount.HasValue)
            {
                if (!item.AirVolumeLiters.HasValue || item.AirVolumeLiters.Value <= 0)
                {
                    MessageBox.Show(
                        $"Air Volume is required for Active Air Sampling.\n\nPlate: {item.PlateCode}",
                        "Validation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }

            if (item.Status == "OOS" && string.IsNullOrWhiteSpace(item.Remarks))
            {
                MessageBox.Show(
                    $"Remarks are required for OOS result.\n\nPlate: {item.PlateCode}",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (item.TotalCount.HasValue && item.TotalCount.Value < 0)
            {
                MessageBox.Show(
                    $"Total Count cannot be negative.\n\nPlate: {item.PlateCode}",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private async Task SaveResultsAsync()
        {
            if (currentEventId == 0)
            {
                MessageBox.Show("No EM event loaded.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!EnsureControlledEmPlanningProvenance("Result Entry"))
                return;

            if (IsEMResultsLockedForEditing())
            {
                MessageBox.Show(
                    "EM results are locked after submission for review. Current status: " + workflowStatus + ".",
                    "Locked Record",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateWorkflowButtons();
                return;
            }

            if (plateItems.Count == 0)
            {
                MessageBox.Show("No plates found to save.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            dgActiveAirPlates.CommitEdit(DataGridEditingUnit.Cell, true);
            dgActiveAirPlates.CommitEdit(DataGridEditingUnit.Row, true);
            dgSettlePlates.CommitEdit(DataGridEditingUnit.Cell, true);
            dgSettlePlates.CommitEdit(DataGridEditingUnit.Row, true);
            // Validate negative inputs before calculating to avoid a UI event exception.
            if (plateItems.Any(item => item.TotalCount < 0))
            {
                MessageBox.Show("Total Count cannot be negative.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CalculateAllStatuses();

            bool hasResults = false;

            foreach (EMPlateResultItem item in plateItems)
            {
                if (HasEnteredResult(item))
                {
                    hasResults = true;
                    break;
                }
            }

            if (!hasResults)
            {
                MessageBox.Show("No EM results entered.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (EMPlateResultItem item in plateItems)
            {
                if (!ValidateResultItem(item))
                    return;
            }

            CalculateAllStatuses();

            try
            {
                await EnsureEMStatusColumnsAreTextAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Infrastructure.UserFacingError.SafeMessage(ex, "EM schema validation"),
                    "EM Results Entry",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var signatureWindow = _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(currentEventNo, currentUser, "EM Result Entry", true);
            signatureWindow.Owner = this;

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            isSaving = true;
            BtnSaveResults.IsEnabled = false;
            BtnSaveResults.Content = "Saving...";

            try
            {
                string finalResult = string.Empty;
                DatabaseHelper.ExecuteInTransaction((conn, transaction) =>
                {
                    finalResult = PersistEmResultsInTransaction(conn, transaction, signatureWindow);
                });

                // Refresh the original versions only AFTER a successful commit.
                LoadPlates(currentEventId);
                CalculateAllStatuses();

                currentFinalResult = finalResult;
                workflowStatus = DatabaseHelper.GetEMWorkflowStatus(currentEventId);
                SetFinalResultStatus(currentFinalResult);
                RefreshGrid();
                RefreshCounters();
                CheckLinkedDeviation();
                UpdateWorkflowButtons();

                MessageBox.Show(
                    $"EM results saved successfully!\n\nFinal Result: {finalResult}",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                lblStatus.Text = "EM results saved successfully.";
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Error saving EM results: {Infrastructure.UserFacingError.SafeMessage(ex)}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                lblStatus.Text = "Error saving results: " + Infrastructure.UserFacingError.SafeMessage(ex);
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
            finally
            {
                isSaving = false;
                BtnSaveResults.Content = "Save Results";
                UpdateWorkflowButtons();
            }
        }

        private void RefreshGrid()
        {
            List<EMPlateResultItem> activeAirItems = plateItems
                .Where(item => IsActiveAirSampling(item.Method))
                .OrderBy(item => item.SequenceNo)
                .ThenBy(item => item.PlateCode)
                .ToList();

            List<EMPlateResultItem> settlePlateItems = plateItems
                .Where(item => !IsActiveAirSampling(item.Method))
                .OrderBy(item => item.SequenceNo)
                .ThenBy(item => item.PlateCode)
                .ToList();

            if (pnlActiveAirResults != null)
                pnlActiveAirResults.Visibility = activeAirItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (dgActiveAirPlates != null)
            {
                dgActiveAirPlates.ItemsSource = null;
                dgActiveAirPlates.ItemsSource = activeAirItems;
                dgActiveAirPlates.Items.Refresh();
            }

            if (dgSettlePlates != null)
            {
                dgSettlePlates.ItemsSource = null;
                dgSettlePlates.ItemsSource = settlePlateItems;
                dgSettlePlates.Items.Refresh();
            }
        }

        private void RefreshCounters()
        {
            int total = plateItems.Count;
            int entered = plateItems.Count(HasEnteredResult);

            lblTotalPlates.Text = total.ToString(CultureInfo.InvariantCulture);
            lblEnteredPlates.Text = entered.ToString(CultureInfo.InvariantCulture);
        }

        private void SetFinalResultStatus(string status)
        {
            string normalizedStatus = (status ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalizedStatus))
                normalizedStatus = "Pending";

            lblFinalResult.Text = EnumHelper.GetStatusDisplayName(normalizedStatus);
            string color = EnumHelper.GetStatusColor(normalizedStatus);
            finalResultBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private void ClearDisplay()
        {
            currentEventId = 0;
            currentPlanId = 0;
            currentEventNo = "";
            currentPlanNo = "";
            currentNegativeControlResult = "";
            reportPrintedThisSession = false;
            workflowStatus = "Pending";
            currentFinalResult = "Pending";
            currentQualityEventId = 0;
            currentQualityEventNo = "";
            currentQualityEventStatus = "";
            currentQualityEventQaDisposition = "";
            currentQualityEventRootCause = "";
            currentQualityEventCapa = "";
            currentQualityEventClosedBy = "";
            currentQualityEventClosedDate = "";

            if (BtnOpenDeviation != null)
                BtnOpenDeviation.Visibility = Visibility.Collapsed;

            if (InvestigationBadge != null)
                InvestigationBadge.Visibility = Visibility.Collapsed;

            currentSamplingTimeFrom = "";
            currentSamplingTimeTo = "";
            currentActivityNoOfPersons = "";
            currentAirSamplerNo = "";
            currentAirSamplingTime = "";
            currentSanitizationDetails = "";
            currentSanitizationTime = "";
            currentDisinfectantUsed = "";
            currentIncubationTemperature = "";
            currentIncubatorNo1 = "";
            currentIncubatorNo2 = "";
            currentIncubationStart = "";
            currentIncubationEnd = "";
            currentRemarks = "";
            currentMonitoringCategory = "";
            currentDispensingBooth = "";
            currentMaterialName = "";
            currentBatchNo = "";
            currentEmployeeId = "";
            currentEmployeeName = "";
            currentEmployeeDepartment = "";
            currentEmployeeShift = "";
            currentSamplingStage = "";
            currentSurfaceLocation = "";
            currentSurfaceType = "";
            currentSurfaceAreaCm2 = "";
            currentSwabKitLot = "";
            currentDiluentLot = "";
            currentRecoveryVolumeMl = "";

            lblEventNo.Text = "";
            lblPlanNo.Text = "N/A";
            lblEventDate.Text = "";
            lblArea.Text = "";
            lblGrade.Text = "";
            lblMediaUsed.Text = "";
            lblMediaLotNo.Text = "";

            SetFinalResultStatus("");
            plateItems.Clear();
            RefreshGrid();
            RefreshCounters();
            UpdateWorkflowButtons();

            lblStatus.Text = "Ready";
            lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#64748B"));
        }
    }
}
