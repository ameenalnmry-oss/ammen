#nullable disable

using Microsoft.Data.SqlClient;
using PharmaLIMS.Services.Investigations;
using System;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS
{
    public partial class PRMQualityEventInvestigation : Window
    {
        private readonly int qualityEventId;
        private readonly string currentUser;
        private readonly string currentRole;

        private int loadedSampleId = 0;
        private string sampleNumber = "";
        private string eventNumber = "";
        private string currentStatus = "";
        private DateTime? loadedEventCreatedDate;

        private DataTable headerTable = new DataTable();
        private DataTable checklistTable = new DataTable();
        private DataTable affectedResultsTable = new DataTable();
        private DataTable actionsTable = new DataTable();
        private bool isLoadingEvent;

        public PRMQualityEventInvestigation()
        {
            InitializeComponent();

            qualityEventId = 0;
            currentUser = ResolveCurrentUser(null);
            currentRole = Login.CurrentUserRole ?? "";

            lblStatus.Text = "No Quality Event loaded.";
        }

        public PRMQualityEventInvestigation(int qualityEventId)
        {
            InitializeComponent();

            this.qualityEventId = qualityEventId;
            currentUser = ResolveCurrentUser(null);
            currentRole = Login.CurrentUserRole ?? "";

            Loaded += PRMQualityEventInvestigation_Loaded;
        }

        public PRMQualityEventInvestigation(int qualityEventId, string currentUser, string currentRole)
        {
            InitializeComponent();

            this.qualityEventId = qualityEventId;
            this.currentUser = ResolveCurrentUser(currentUser);
            this.currentRole = string.IsNullOrWhiteSpace(currentRole) ? (Login.CurrentUserRole ?? "") : currentRole;

            Loaded += PRMQualityEventInvestigation_Loaded;
        }

        private async void PRMQualityEventInvestigation_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= PRMQualityEventInvestigation_Loaded;
            await LoadEventAsync();
        }

        private static string ResolveCurrentUser(string requestedUser)
        {
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to work with quality-event records.");
        }

        private bool CanClosePrmQualityEvent() => DatabaseHelper.CanCloseQualityEvent(currentUser);
        private bool CanManagePrmQualityEvent() =>
            DatabaseHelper.CanReviewResults(currentUser) || DatabaseHelper.CanApproveResults(currentUser);

        private async Task LoadEventAsync()
        {
            if (isLoadingEvent) return;
            try
            {
                isLoadingEvent = true;
                IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                if (qualityEventId <= 0)
                {
                    MessageBox.Show(
                        "No PRM Quality Event was selected.",
                        "PRM Quality Event",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                PRMEventLoadData loaded = await Task.Run(() =>
                {
                    DataTable header = PRMQualityEventInvestigationService.GetPRMQualityEventHeader(qualityEventId);
                    if (header == null || header.Rows.Count == 0)
                        return null;

                    string categoryText = PRMQualityEventInvestigationService.GetPRMSampleCategory(header.Rows[0]);
                    DataTable checklist = PRMQualityEventInvestigationService.GetPRMQualityEventChecklist(qualityEventId, categoryText) ?? new DataTable();
                    return new PRMEventLoadData
                    {
                        Header = header,
                        CategoryText = categoryText,
                        Checklist = checklist,
                        AffectedResults = SafeLoadAffectedResults(),
                        Actions = SafeLoadActions()
                    };
                });

                if (loaded == null)
                {
                    MessageBox.Show(
                        "PRM Quality Event record was not found.",
                        "PRM Quality Event",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    Close();
                    return;
                }

                headerTable = loaded.Header;
                DataRow row = headerTable.Rows[0];

                loadedSampleId = GetSafeInt(row, "SampleID");
                sampleNumber = GetSafeString(row, "SampleNumber");
                eventNumber = GetSafeString(row, "EventNumber");
                currentStatus = GetSafeString(row, "CurrentStatus");

                lblEventNumber.Text = eventNumber;
                lblEventType.Text = GetSafeString(row, "EventType");
                lblSampleNumber.Text = sampleNumber;
                lblDetectedBy.Text = GetSafeString(row, "DetectedBy");
                lblDetectionSource.Text = GetSafeString(row, "DetectionSource");
                lblHeaderStatus.Text = string.IsNullOrWhiteSpace(currentStatus) ? "Open" : currentStatus;

                DateTime? detectedDate = GetSafeDateTime(row, "DetectedDate");
                loadedEventCreatedDate = row.Table.Columns.Contains("CreatedDate")
                    ? GetSafeDateTime(row, "CreatedDate") ?? detectedDate
                    : detectedDate;
                lblDetectedDate.Text = detectedDate.HasValue ? detectedDate.Value.ToString("yyyy-MM-dd HH:mm") : "";

                string prmType = PRMQualityEventInvestigationService.ResolvePRMChecklistCategory(loaded.CategoryText);
                lblPRMType.Text = prmType;

                lblMaterialProduct.Text = FirstNonEmpty(
                    GetSafeString(row, "MaterialName"),
                    GetSafeString(row, "ProductName"),
                    GetSafeString(row, "MaterialCode"),
                    GetSafeString(row, "ProductCode"));

                lblBatchLot.Text = FirstNonEmpty(
                    GetSafeString(row, "BatchNo"),
                    GetSafeString(row, "ManufacturerLotNo"),
                    GetSafeString(row, "SupplierLotNo"),
                    GetSafeString(row, "GRNNo"));

                SelectComboText(cboSeverity, GetSafeString(row, "Severity"));
                SelectComboText(cboFinalDisposition, GetSafeString(row, "FinalDisposition"));
                SelectComboText(cboRootCauseCategory, GetSafeString(row, "RootCauseCategory"));

                txtInitialDescription.Text = GetSafeString(row, "InitialDescription");
                txtImmediateAction.Text = GetSafeString(row, "ImmediateAction");
                txtRootCauseDetails.Text = GetSafeString(row, "RootCauseDetails");
                txtImpactAssessment.Text = GetSafeString(row, "ImpactAssessment");
                txtQAConclusion.Text = GetSafeString(row, "QAConclusion");

                chkCAPARequired.IsChecked =
                    row.Table.Columns.Contains("CAPARequired") &&
                    row["CAPARequired"] != DBNull.Value &&
                    Convert.ToBoolean(row["CAPARequired"]);

                checklistTable = loaded.Checklist;
                NormalizeChecklistAnswers(checklistTable);
                BindChecklistPhaseViews();

                affectedResultsTable = loaded.AffectedResults;
                dgAffectedResults.ItemsSource = affectedResultsTable.DefaultView;

                actionsTable = loaded.Actions;
                dgActions.ItemsSource = actionsTable.DefaultView;

                UpdateButtons();

                lblStatus.Text = "Loaded " + eventNumber + " / " + prmType + ".";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Error loading PRM investigation QualityEventId={qualityEventId}.", ex);
                MessageBox.Show(
                    "Error loading PRM investigation: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "PRM Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                isLoadingEvent = false;
                IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }


        private void BindChecklistPhaseViews()
        {
            if (checklistTable == null)
            {
                dgChecklist.ItemsSource = null;

                if (dgPhaseIIChecklist != null)
                    dgPhaseIIChecklist.ItemsSource = null;

                if (dgClosureChecklist != null)
                    dgClosureChecklist.ItemsSource = null;

                return;
            }

            DataView phaseIView = new DataView(checklistTable);
            phaseIView.RowFilter =
                "SectionName LIKE 'A.%' OR " +
                "SectionName LIKE 'B.%' OR " +
                "SectionName LIKE 'C.%' OR " +
                "SectionName LIKE 'D. Results%' OR " +
                "SectionName LIKE 'Phase I%' OR " +
                "SectionName LIKE 'PRM - General%'";

            DataView phaseIIView = new DataView(checklistTable);
            phaseIIView.RowFilter =
                "SectionName LIKE 'D. Bioburden%' OR " +
                "SectionName LIKE 'E. Manufacturing%' OR " +
                "SectionName LIKE 'Phase II%'";

            DataView closureView = new DataView(checklistTable);
            closureView.RowFilter =
                "SectionName LIKE 'E. Assignable%' OR " +
                "SectionName LIKE 'G. Assignable%' OR " +
                "SectionName LIKE '%Closure%'";

            dgChecklist.ItemsSource = phaseIView;

            if (dgPhaseIIChecklist != null)
                dgPhaseIIChecklist.ItemsSource = phaseIIView;

            if (dgClosureChecklist != null)
                dgClosureChecklist.ItemsSource = closureView;
        }

        private string ResolveChecklistPhaseName(DataRow row)
        {
            string section = GetSafeString(row, "SectionName");

            if (section.StartsWith("D. Bioburden", StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith("E. Manufacturing", StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith("Phase II", StringComparison.OrdinalIgnoreCase))
                return "Phase II - Full Scale Investigation";

            if (section.StartsWith("E. Assignable", StringComparison.OrdinalIgnoreCase) ||
                section.StartsWith("G. Assignable", StringComparison.OrdinalIgnoreCase) ||
                section.IndexOf("Closure", StringComparison.OrdinalIgnoreCase) >= 0)
                return "QA Closure";

            return "Phase I - Initial Laboratory Investigation";
        }

        private DataGrid ResolveChecklistGridForRow(DataRow row)
        {
            string phase = ResolveChecklistPhaseName(row);

            if (phase.StartsWith("Phase II", StringComparison.OrdinalIgnoreCase) && dgPhaseIIChecklist != null)
                return dgPhaseIIChecklist;

            if (phase.Equals("QA Closure", StringComparison.OrdinalIgnoreCase) && dgClosureChecklist != null)
                return dgClosureChecklist;

            return dgChecklist;
        }

        private DataTable SafeLoadAffectedResults()
        {
            try
            {
                return PRMQualityEventInvestigationService.GetPRMAffectedResults(qualityEventId) ?? new DataTable();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Unable to load affected PRM results for QualityEventId={qualityEventId}.", ex);
                return new DataTable();
            }
        }

        private DataTable SafeLoadActions()
        {
            try
            {
                return PRMQualityEventInvestigationService.GetPRMActions(qualityEventId) ?? new DataTable();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Unable to load PRM actions for QualityEventId={qualityEventId}.", ex);
                return new DataTable();
            }
        }

        private void UpdateButtons()
        {
            bool isClosed =
                currentStatus.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                currentStatus.Equals("QA Closed", StringComparison.OrdinalIgnoreCase) ||
                currentStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                currentStatus.Equals("Rejected Closed", StringComparison.OrdinalIgnoreCase);

            bool canManage = CanManagePrmQualityEvent();
            BtnSave.IsEnabled = !isClosed && canManage;
            BtnSubmitQA.IsEnabled = !isClosed && canManage && !currentStatus.Equals("QA Review", StringComparison.OrdinalIgnoreCase);
            BtnRefresh.IsEnabled = true;
            BtnPrintReport.IsEnabled = qualityEventId > 0;

            if (BtnCloseInvestigation != null)
                BtnCloseInvestigation.IsEnabled =
                    !isClosed &&
                    currentStatus.Equals("QA Review", StringComparison.OrdinalIgnoreCase) &&
                    CanClosePrmQualityEvent() &&
                    qualityEventId > 0;
            if (BtnAddAction != null)
                BtnAddAction.IsEnabled = !isClosed && canManage;

            txtInitialDescription.IsReadOnly = isClosed;
            txtImmediateAction.IsReadOnly = isClosed;
            txtRootCauseDetails.IsReadOnly = isClosed;
            txtImpactAssessment.IsReadOnly = isClosed;
            txtQAConclusion.IsReadOnly = isClosed;
            txtActionNote.IsReadOnly = isClosed;

            cboSeverity.IsEnabled = !isClosed;
            cboFinalDisposition.IsEnabled = !isClosed;
            cboRootCauseCategory.IsEnabled = !isClosed;
            chkCAPARequired.IsEnabled = !isClosed;

            dgChecklist.IsReadOnly = isClosed;

            if (dgPhaseIIChecklist != null)
                dgPhaseIIChecklist.IsReadOnly = isClosed;

            if (dgClosureChecklist != null)
                dgClosureChecklist.IsReadOnly = isClosed;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!SaveInvestigation(currentStatus))
                    return;

                await LoadEventAsync();

                MessageBox.Show(
                    "PRM investigation saved successfully.",
                    "PRM Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error saving PRM investigation: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "PRM Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnSubmitQA_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!ValidateBeforeSubmitToQA())
                    return;

                ElectronicSignature signature = new ElectronicSignature(
                    string.IsNullOrWhiteSpace(eventNumber) ? qualityEventId.ToString(CultureInfo.InvariantCulture) : eventNumber,
                    currentUser,
                    "PRM Quality Event Submit to QA",
                    true)
                {
                    Owner = this
                };
                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                SubmitInvestigationToQa(signature);

                await LoadEventAsync();

                MessageBox.Show(
                    "PRM Quality Event submitted to QA review.",
                    "PRM Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error submitting PRM investigation to QA: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "PRM Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadEventAsync();
        }

        private void BtnAddAction_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (qualityEventId <= 0)
                    return;

                string note = txtActionNote.Text.Trim();
                bool isCapaAction = chkCAPARequired.IsChecked == true;

                if (string.IsNullOrWhiteSpace(note))
                {
                    MessageBox.Show(
                        "Enter an action note first.",
                        "PRM Quality Event",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    txtActionNote.Focus();
                    return;
                }

                if (isCapaAction && note.Length < 20)
                {
                    MessageBox.Show(
                        "CAPA action evidence must describe the corrective/preventive action clearly (minimum 20 characters).",
                        "PRM CAPA",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    txtActionNote.Focus();
                    return;
                }

                string actionType = isCapaAction ? "PRM CAPA Action" : "PRM Investigation Note";

                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    DatabaseHelper.EnsureQualityEventManagementAuthorizationInTransaction(
                        connection, transaction, currentUser, "add a PRM Quality Event action");
                    object lockedStatus = ExecuteScalarInTransaction(connection, transaction, @"
SELECT CurrentStatus FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
WHERE QualityEventID=@QualityEventID;",
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId });
                    string persistedStatus = Convert.ToString(lockedStatus, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (IsClosedQualityEventStatus(persistedStatus))
                        throw new InvalidOperationException("The PRM Quality Event is closed and cannot accept new action notes.");

                    DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
VALUES(@QualityEventID,@ActionType,@Note,@User,SYSDATETIME());",
                        new[]
                        {
                            new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                            new SqlParameter("@ActionType", SqlDbType.NVarChar, 80) { Value = actionType },
                            new SqlParameter("@Note", SqlDbType.NVarChar, -1) { Value = note },
                            new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = currentUser }
                        }, connection, transaction);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection, transaction, "QualityEvents", qualityEventId,
                        "Add PRM Investigation Action", persistedStatus, persistedStatus,
                        note, currentUser, "CurrentStatus", null, sampleNumber, "PRM Quality Event");
                });

                txtActionNote.Clear();

                actionsTable = SafeLoadActions();
                dgActions.ItemsSource = actionsTable.DefaultView;

                lblStatus.Text = "Action note added.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error adding action note: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "PRM Quality Event",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnPrintReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (qualityEventId <= 0)
                {
                    MessageBox.Show(
                        "No PRM Quality Event is loaded.",
                        "Print Report",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                PrintDialog printDialog = new PrintDialog();
                if (printDialog.ShowDialog() != true)
                    return;

                DatabaseHelper.AddAuditTrailAdvanced(
                    "QualityEvents",
                    qualityEventId,
                    "PRM Quality Event Report Print Requested",
                    "",
                    lblEventNumber.Text,
                    "A controlled print request was accepted before the document was sent to the printer.",
                    currentUser);

                FlowDocument document = BuildPRMInvestigationReportDocument();
                document.PageHeight = printDialog.PrintableAreaHeight;
                document.PageWidth = printDialog.PrintableAreaWidth;

                IDocumentPaginatorSource paginatorSource = document;
                printDialog.PrintDocument(
                    paginatorSource.DocumentPaginator,
                    "PRM Quality Event Investigation Report - " + lblEventNumber.Text);

                TryAddPrintHistory();

                DatabaseHelper.AddAuditTrailAdvanced(
                    "QualityEvents",
                    qualityEventId,
                    "PRM Quality Event Report Print Completed",
                    "Requested",
                    "Completed",
                    "PRM Quality Event report was sent to the selected printer.",
                    currentUser);
            }
            catch (Exception ex)
            {
                try
                {
                    if (qualityEventId > 0)
                    {
                        DatabaseHelper.AddAuditTrailAdvanced(
                            "QualityEvents",
                            qualityEventId,
                            "PRM Quality Event Report Print Failed",
                            "Requested",
                            "Failed",
                            Infrastructure.UserFacingError.SafeMessage(ex),
                            currentUser);
                    }
                }
                catch (Exception auditException)
                {
                    ApplicationLogger.Warning("Unable to record the failed print attempt.", auditException);
                }

                MessageBox.Show(
                    "Error printing PRM report: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Print Report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnCloseInvestigation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!CanClosePrmQualityEvent())
                {
                    MessageBox.Show(
                        "Only QA/Admin users with approval permission can close a PRM Quality Event.",
                        "Permission Denied",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!ValidateBeforeCloseInvestigation())
                    return;

                MessageBoxResult confirm = MessageBox.Show(
                    "Close this PRM Quality Event Investigation?\n\n" +
                    "After closure, the investigation will become read-only and the related PRM result approval gate may proceed according to workflow.\n\n" +
                    "If eligible pre-v183 PRM evidence exists, this same QA signature will also create an explicit, immutable reconciliation link to this controlled replacement investigation; historical evidence will not be rewritten.",
                    "Close PRM Investigation",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;

                ElectronicSignature signature = new ElectronicSignature(
                    string.IsNullOrWhiteSpace(eventNumber) ? qualityEventId.ToString(CultureInfo.InvariantCulture) : eventNumber,
                    currentUser,
                    "PRM Quality Event Closure and Legacy Evidence Reconciliation (when applicable)",
                    true)
                {
                    Owner = this
                };

                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                int reconciledLegacyEventCount = 0;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string authorizedRole = DatabaseHelper.EnsureQaClosureAuthorizationInTransaction(
                        conn, tx, signature.SignedBy, "close a PRM Quality Event");

                    string lockedStatus = Convert.ToString(ExecuteScalarInTransaction(conn, tx, @"
SELECT CurrentStatus
FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
WHERE QualityEventID = @QualityEventID
  AND UPPER(LTRIM(RTRIM(ISNULL(SourceModule, N'')))) = N'PRM';",
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }),
                        CultureInfo.InvariantCulture) ?? string.Empty;

                    if (!lockedStatus.Equals("QA Review", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The PRM Quality Event is no longer in QA Review. Closure was not committed.");

                    EnsurePrmCapaClosureEvidenceInTransaction(
                        conn,
                        tx,
                        qualityEventId,
                        chkCAPARequired.IsChecked == true);

                    EnsureAffectedPrmEvidenceMatchesCurrentResultsInTransaction(conn, tx, qualityEventId);
                    EnsureReplacementEventCoversCurrentAndLegacyEvidenceInTransaction(
                        conn, tx, qualityEventId, loadedSampleId);

                    DatabaseHelper.SaveQualityEventInvestigation(
                        conn,
                        tx,
                        qualityEventId,
                        GetComboText(cboSeverity),
                        txtInitialDescription.Text.Trim(),
                        txtImmediateAction.Text.Trim(),
                        GetComboText(cboRootCauseCategory),
                        txtRootCauseDetails.Text.Trim(),
                        txtImpactAssessment.Text.Trim(),
                        chkCAPARequired.IsChecked == true,
                        txtQAConclusion.Text.Trim(),
                        GetComboText(cboFinalDisposition),
                        "QA Review",
                        signature.SignedBy);

                    PRMQualityEventInvestigationService.SavePRMQualityEventChecklistAnswers(
                        conn,
                        tx,
                        qualityEventId,
                        checklistTable,
                        signature.SignedBy);

                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.QualityEvents
SET
    CurrentStatus = 'Closed',
    ClosedBy = @ClosedBy,
    ClosedDate = SYSDATETIME(),
    ModifiedBy = @ModifiedBy,
    ModifiedDate = SYSDATETIME()
WHERE QualityEventID = @QualityEventID
  AND CurrentStatus = N'QA Review'
  AND UPPER(LTRIM(RTRIM(ISNULL(SourceModule, N'')))) = N'PRM';",
                    new[]
                    {
                        new SqlParameter("@ClosedBy", SqlDbType.NVarChar, 100) { Value = signature.SignedBy },
                        new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 100) { Value = signature.SignedBy },
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }
                    }, conn, tx);

                    if (affected != 1)
                        throw new InvalidOperationException("The Quality Event is no longer in QA Review. Closure was not committed.");

                    int closureSignatureId = Convert.ToInt32(ExecuteScalarInTransaction(conn, tx, @"
INSERT INTO dbo.PRM_ElectronicSignatures
(
    SampleID, ActionType, SignedBy, MeaningOfSignature, ActionReason, UserRole, SignedAt
)
OUTPUT INSERTED.SignatureID
VALUES
(
    @SampleID, N'Quality Event Closure', @SignedBy, @Meaning, @Reason, @UserRole, SYSDATETIME()
);",
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = loadedSampleId },
                        new SqlParameter("@SignedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                        new SqlParameter("@Meaning", SqlDbType.NVarChar, 255) { Value = signature.Meaning },
                        new SqlParameter("@Reason", SqlDbType.NVarChar, -1) { Value = signature.Reason },
                        new SqlParameter("@UserRole", SqlDbType.NVarChar, 80) { Value = authorizedRole }), CultureInfo.InvariantCulture);

                    reconciledLegacyEventCount = CreateLegacyPrmEvidenceReconciliationsInTransaction(
                        conn,
                        tx,
                        qualityEventId,
                        loadedSampleId,
                        closureSignatureId,
                        signature.SignedBy,
                        signature.Reason);

                    DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT dbo.QualityEventActions
(
    QualityEventID, ActionType, ActionDescription, PerformedBy, PerformedDate
)
VALUES
(
    @QualityEventID, N'Close Investigation', @ActionDescription, @PerformedBy, SYSDATETIME()
);",
                        new[]
                        {
                            new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                            new SqlParameter("@ActionDescription", SqlDbType.NVarChar, -1)
                            {
                                Value = "PRM Quality Event Investigation closed after QA review and documented final disposition. Reason: " + signature.Reason
                            },
                            new SqlParameter("@PerformedBy", SqlDbType.NVarChar, 100) { Value = signature.SignedBy }
                        }, conn, tx);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn,
                        tx,
                        "QualityEvents",
                        qualityEventId,
                        "Close PRM Quality Event Investigation",
                        "QA Review",
                        "Closed",
                        signature.Reason,
                        signature.SignedBy,
                        "CurrentStatus",
                        null,
                        sampleNumber,
                        "PRM Quality Event");

                    if (reconciledLegacyEventCount > 0)
                    {
                        DatabaseHelper.AddAuditTrailAdvanced(
                            conn,
                            tx,
                            "PRM_QualityEventEvidenceReconciliations",
                            qualityEventId,
                            "Reconcile Historical PRM Investigation Evidence",
                            "Legacy evidence unresolved",
                            "Explicitly reconciled by controlled replacement investigation",
                            "Reconciled " + reconciledLegacyEventCount.ToString(CultureInfo.InvariantCulture) +
                            " historical Quality Event(s) without rewriting legacy affected-result rows. " + signature.Reason,
                            signature.SignedBy,
                            "ReplacementQualityEventID",
                            null,
                            sampleNumber,
                            "PRM Quality Event Evidence Reconciliation");
                    }
                });

                _ = LoadEventAsync();

                MessageBox.Show(
                    reconciledLegacyEventCount > 0
                        ? "PRM Quality Event Investigation closed successfully. Historical PRM evidence reconciled: " + reconciledLegacyEventCount.ToString(CultureInfo.InvariantCulture) + "."
                        : "PRM Quality Event Investigation closed successfully.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error closing PRM investigation: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnCloseWindow_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private static void EnsurePrmCapaClosureEvidenceInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int qualityEventId,
            bool capaRequired)
        {
            if (!capaRequired)
                return;

            using SqlCommand command = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.QualityEventActions WITH (UPDLOCK, HOLDLOCK)
    WHERE QualityEventID = @QualityEventID
      AND UPPER(LTRIM(RTRIM(ISNULL(ActionType,N'')))) = N'PRM CAPA ACTION'
      AND NULLIF(LTRIM(RTRIM(ISNULL(ActionDescription,N''))),N'') IS NOT NULL
)
THEN 1 ELSE 0 END;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;

            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "PRM Quality Event closure is blocked because CAPA is required but no explicit PRM CAPA Action with a documented description exists in the locked database evidence.");
            }
        }

        private bool ValidateBeforeCloseInvestigation()
        {
            if (qualityEventId <= 0)
                return false;

            if (!currentStatus.Equals("QA Review", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Investigation can be closed only after it is submitted to QA Review.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            if (string.IsNullOrWhiteSpace(GetComboText(cboSeverity)))
            {
                MessageBox.Show(
                    "Severity is required before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                cboSeverity.Focus();
                return false;
            }

            string disposition = GetComboText(cboFinalDisposition);
            if (string.IsNullOrWhiteSpace(disposition) ||
                disposition.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Investigation disposition must be selected before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                cboFinalDisposition.Focus();
                return false;
            }

            if (disposition.Equals("Retest / Resample Required", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Retest Required", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "The investigation cannot be closed while retest/resample is still required. Record and assess the controlled follow-up testing first, then select a final investigation disposition.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                cboFinalDisposition.Focus();
                return false;
            }

            if (disposition.Equals("Accepted with Justification", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Released", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Batch/Material Hold", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "This legacy value represents a batch/material decision rather than an investigation outcome. Select a controlled investigation disposition before closure. Final batch/material release or rejection remains outside this screen.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                cboFinalDisposition.Focus();
                return false;
            }

            string rootCause = GetComboText(cboRootCauseCategory);
            if (string.IsNullOrWhiteSpace(rootCause) ||
                rootCause.Equals("Pending Investigation", StringComparison.OrdinalIgnoreCase) ||
                rootCause.Equals("Undetermined", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Root cause type/category must be finalized before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                cboRootCauseCategory.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtInitialDescription.Text))
            {
                MessageBox.Show(
                    "Initial description is required before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtInitialDescription.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtImmediateAction.Text))
            {
                MessageBox.Show(
                    "Immediate action is required before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtImmediateAction.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtRootCauseDetails.Text))
            {
                MessageBox.Show(
                    "Root cause details are required before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtRootCauseDetails.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtImpactAssessment.Text))
            {
                MessageBox.Show(
                    "Impact assessment is required before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtImpactAssessment.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtQAConclusion.Text))
            {
                MessageBox.Show(
                    "QA conclusion is required before closure.",
                    "Close Investigation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtQAConclusion.Focus();
                return false;
            }

            if (!ValidateRequiredChecklist("closure"))
                return false;

            if (chkCAPARequired.IsChecked == true)
            {
                actionsTable = SafeLoadActions();
                bool hasExplicitCapaAction = false;
                if (actionsTable != null)
                {
                    foreach (DataRow actionRow in actionsTable.Rows)
                    {
                        if (actionRow.RowState == DataRowState.Deleted)
                            continue;

                        string actionType = actionRow.Table.Columns.Contains("ActionType")
                            ? Convert.ToString(actionRow["ActionType"], CultureInfo.InvariantCulture) ?? string.Empty
                            : string.Empty;
                        string description = actionRow.Table.Columns.Contains("ActionDescription")
                            ? Convert.ToString(actionRow["ActionDescription"], CultureInfo.InvariantCulture) ?? string.Empty
                            : string.Empty;

                        if (actionType.Equals("PRM CAPA Action", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(description))
                        {
                            hasExplicitCapaAction = true;
                            break;
                        }
                    }
                }

                if (!hasExplicitCapaAction)
                {
                    MessageBox.Show(
                        "CAPA is required. Add at least one explicit PRM CAPA action before closure. System workflow actions and ordinary investigation notes do not satisfy CAPA evidence.",
                        "Close Investigation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }

            return true;
        }

        private bool SaveInvestigation(string status)
        {
            if (qualityEventId <= 0)
                return false;

            CommitGridEdits();
            NormalizeChecklistAnswers(checklistTable);

            string oldStatus = currentStatus;
            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureQualityEventManagementAuthorizationInTransaction(
                    connection, transaction, currentUser, "save a PRM Quality Event investigation");
                string lockedStatus = Convert.ToString(ExecuteScalarInTransaction(connection, transaction, @"
SELECT CurrentStatus FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
WHERE QualityEventID=@QualityEventID;",
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }), CultureInfo.InvariantCulture) ?? string.Empty;
                if (IsClosedQualityEventStatus(lockedStatus))
                    throw new InvalidOperationException("The PRM Quality Event is closed and cannot be modified.");
                if (!lockedStatus.Equals(oldStatus, StringComparison.OrdinalIgnoreCase))
                    throw new DBConcurrencyException("The PRM Quality Event status changed. Reload before saving.");

                DatabaseHelper.SaveQualityEventInvestigation(
                    connection,
                    transaction,
                    qualityEventId,
                    GetComboText(cboSeverity),
                    txtInitialDescription.Text.Trim(),
                    txtImmediateAction.Text.Trim(),
                    GetComboText(cboRootCauseCategory),
                    txtRootCauseDetails.Text.Trim(),
                    txtImpactAssessment.Text.Trim(),
                    chkCAPARequired.IsChecked == true,
                    txtQAConclusion.Text.Trim(),
                    GetComboText(cboFinalDisposition),
                    status,
                    currentUser);

                PRMQualityEventInvestigationService.SavePRMQualityEventChecklistAnswers(
                    connection,
                    transaction,
                    qualityEventId,
                    checklistTable,
                    currentUser);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "QualityEvents",
                    qualityEventId,
                    "PRM Quality Event Investigation Update",
                    oldStatus,
                    status,
                    "PRM investigation and checklist updated.",
                    currentUser,
                    null,
                    null,
                    sampleNumber,
                    "PRM Quality Event");
            });

            currentStatus = status;
            return true;
        }

        private void SubmitInvestigationToQa(ElectronicSignature signature)
        {
            if (loadedSampleId <= 0)
                throw new InvalidOperationException("The PRM Quality Event is not linked to a valid PRM sample. Submission was blocked.");

            CommitGridEdits();
            NormalizeChecklistAnswers(checklistTable);
            string oldStatus = currentStatus;

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string authorizedRole = DatabaseHelper.EnsureQualityEventManagementAuthorizationInTransaction(
                    connection, transaction, signature.SignedBy, "submit a PRM Quality Event to QA review");

                string lockedStatus = Convert.ToString(ExecuteScalarInTransaction(connection, transaction, @"
SELECT CurrentStatus FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
WHERE QualityEventID=@QualityEventID;",
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }), CultureInfo.InvariantCulture) ?? string.Empty;

                if (IsClosedQualityEventStatus(lockedStatus) || lockedStatus.Equals("QA Review", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The PRM Quality Event is closed or already in QA Review.");
                if (!lockedStatus.Equals(oldStatus, StringComparison.OrdinalIgnoreCase))
                    throw new DBConcurrencyException("The PRM Quality Event status changed. Reload before submitting.");

                DatabaseHelper.SaveQualityEventInvestigation(
                    connection, transaction, qualityEventId,
                    GetComboText(cboSeverity), txtInitialDescription.Text.Trim(), txtImmediateAction.Text.Trim(),
                    GetComboText(cboRootCauseCategory), txtRootCauseDetails.Text.Trim(), txtImpactAssessment.Text.Trim(),
                    chkCAPARequired.IsChecked == true, txtQAConclusion.Text.Trim(), GetComboText(cboFinalDisposition),
                    lockedStatus, signature.SignedBy);

                PRMQualityEventInvestigationService.SavePRMQualityEventChecklistAnswers(
                    connection, transaction, qualityEventId, checklistTable, signature.SignedBy);

                int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.QualityEvents
SET CurrentStatus=N'QA Review',ModifiedBy=@User,ModifiedDate=SYSDATETIME()
WHERE QualityEventID=@QualityEventID AND CurrentStatus=@ExpectedStatus;",
                    new[]
                    {
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = signature.SignedBy },
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@ExpectedStatus", SqlDbType.NVarChar, 60) { Value = lockedStatus }
                    }, connection, transaction);
                if (affected != 1)
                    throw new DBConcurrencyException("The PRM Quality Event changed before submission.");

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT dbo.PRM_ElectronicSignatures
(SampleID,ActionType,SignedBy,MeaningOfSignature,ActionReason,UserRole,SignedAt)
VALUES(@SampleID,N'Quality Event Submit to QA',@SignedBy,@Meaning,@Reason,@Role,SYSDATETIME());",
                    new[]
                    {
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = loadedSampleId },
                        new SqlParameter("@SignedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                        new SqlParameter("@Meaning", SqlDbType.NVarChar, 255) { Value = signature.Meaning },
                        new SqlParameter("@Reason", SqlDbType.NVarChar, -1) { Value = signature.Reason },
                        new SqlParameter("@Role", SqlDbType.NVarChar, 100) { Value = authorizedRole }
                    }, connection, transaction);

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT dbo.QualityEventActions(QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate)
VALUES(@QualityEventID,N'Submit to QA',@Description,@User,SYSDATETIME());",
                    new[]
                    {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@Description", SqlDbType.NVarChar, -1) { Value = "PRM investigation submitted to QA review. Reason: " + signature.Reason },
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = signature.SignedBy }
                    }, connection, transaction);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "QualityEvents", qualityEventId,
                    "Submit PRM Quality Event to QA", lockedStatus, "QA Review",
                    signature.Reason, signature.SignedBy, "CurrentStatus", null,
                    sampleNumber, "PRM Quality Event");
            });

            currentStatus = "QA Review";
        }

        private static object ExecuteScalarInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string commandText,
            params SqlParameter[] parameters)
        {
            using SqlCommand command = new SqlCommand(commandText, connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            if (parameters != null && parameters.Length > 0)
                command.Parameters.AddRange(parameters);
            return command.ExecuteScalar();
        }

        private static void EnsureAffectedPrmEvidenceMatchesCurrentResultsInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int eventId)
        {
            object mismatch = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.QualityEventAffectedResults affected WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.QualityEvents qualityEvent WITH (UPDLOCK, HOLDLOCK)
    ON qualityEvent.QualityEventID = affected.QualityEventID
LEFT JOIN dbo.PRM_SampleTests currentResult WITH (UPDLOCK, HOLDLOCK)
    ON currentResult.SampleTestID = affected.SourceResultID
   AND currentResult.SampleID = qualityEvent.SourceRecordID
WHERE affected.QualityEventID = @QualityEventID
  AND affected.SourceModule = N'PRM'
  AND qualityEvent.SourceModule = N'PRM'
  AND
  (
      currentResult.SampleTestID IS NULL
      OR ISNULL(affected.EvidenceSchemaVersion, 0) <> 1
      OR CONVERT(VARBINARY(MAX), ISNULL(affected.TestName, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.TestName, N''))
      OR CONVERT(VARBINARY(MAX), ISNULL(affected.ResultValue, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.ResultValue, N''))
      OR CONVERT(VARBINARY(MAX), ISNULL(affected.SpecificationLimit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.SpecificationText, N''))
      OR NOT (affected.SpecificationNumericLimit = currentResult.SpecificationLimit OR (affected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
      OR CONVERT(VARBINARY(MAX), ISNULL(affected.Unit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.Unit, N''))
      OR CONVERT(VARBINARY(MAX), ISNULL(affected.FailureType, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.Interpretation, N''))
  );",
                new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = eventId });

            if (Convert.ToInt32(mismatch ?? 0, CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "PRM investigation closure is blocked because the affected-result evidence no longer matches the current immutable result/specification snapshot. " +
                    "Do not overwrite investigated results; retain the original evidence and record any repeat/retest/resample separately.");
            }
        }

        private static void EnsureReplacementEventCoversCurrentAndLegacyEvidenceInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int replacementQualityEventId,
            int sampleId)
        {
            object schemaReady = ExecuteScalarInTransaction(connection, transaction, @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NOT NULL
THEN 1 ELSE 0 END;");

            if (Convert.ToInt32(schemaReady ?? 0, CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException(
                    "PRM historical-evidence reconciliation schema is not ready. Run the controlled database-maintenance step before QA closure.");

            object untraceableLegacy = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.QualityEvents legacyEvent WITH (UPDLOCK,HOLDLOCK)
INNER JOIN dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK,HOLDLOCK)
    ON legacyAffected.QualityEventID=legacyEvent.QualityEventID
   AND legacyAffected.SourceModule=N'PRM'
WHERE legacyEvent.SourceModule=N'PRM'
  AND legacyEvent.SourceRecordID=@SampleID
  AND legacyEvent.CurrentStatus=N'Closed'
  AND legacyEvent.QualityEventID<@ReplacementQualityEventID
  AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
  AND legacyAffected.SourceResultID IS NULL;",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId },
                new SqlParameter("@ReplacementQualityEventID", SqlDbType.Int) { Value = replacementQualityEventId });

            if (Convert.ToInt32(untraceableLegacy ?? 0, CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException(
                    "QA closure is blocked because historical PRM evidence contains an untraceable source-result link. The legacy record was not modified; controlled database/QA remediation is required.");

            object missingCoverage = ExecuteScalarInTransaction(connection, transaction, @"
;WITH RequiredResults AS
(
    SELECT st.SampleTestID
    FROM dbo.PRM_SampleTests st WITH (UPDLOCK,HOLDLOCK)
    WHERE st.SampleID=@SampleID
      AND ISNULL(st.RequiredTest,1)=1
      AND st.Interpretation IN(N'Does Not Conform',N'Check Required')

    UNION

    SELECT legacyAffected.SourceResultID
    FROM dbo.QualityEvents legacyEvent WITH (UPDLOCK,HOLDLOCK)
    INNER JOIN dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK,HOLDLOCK)
        ON legacyAffected.QualityEventID=legacyEvent.QualityEventID
       AND legacyAffected.SourceModule=N'PRM'
    WHERE legacyEvent.SourceModule=N'PRM'
      AND legacyEvent.SourceRecordID=@SampleID
      AND legacyEvent.CurrentStatus=N'Closed'
      AND legacyEvent.QualityEventID<@ReplacementQualityEventID
      AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
      AND legacyAffected.SourceResultID IS NOT NULL
)
SELECT COUNT(1)
FROM RequiredResults requiredResult
LEFT JOIN dbo.PRM_SampleTests currentResult WITH (UPDLOCK,HOLDLOCK)
    ON currentResult.SampleTestID=requiredResult.SampleTestID
   AND currentResult.SampleID=@SampleID
WHERE currentResult.SampleTestID IS NULL
   OR NOT EXISTS
   (
       SELECT 1
       FROM dbo.QualityEventAffectedResults replacementAffected WITH (UPDLOCK,HOLDLOCK)
       WHERE replacementAffected.QualityEventID=@ReplacementQualityEventID
         AND replacementAffected.SourceModule=N'PRM'
         AND replacementAffected.SourceResultID=currentResult.SampleTestID
         AND ISNULL(replacementAffected.EvidenceSchemaVersion,0)=1
         AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.TestName,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.TestName,N''))
         AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.ResultValue,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.ResultValue,N''))
         AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.SpecificationLimit,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.SpecificationText,N''))
         AND (replacementAffected.SpecificationNumericLimit=currentResult.SpecificationLimit OR (replacementAffected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
         AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.Unit,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.Unit,N''))
         AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.FailureType,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.Interpretation,N''))
   );",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId },
                new SqlParameter("@ReplacementQualityEventID", SqlDbType.Int) { Value = replacementQualityEventId });

            if (Convert.ToInt32(missingCoverage ?? 0, CultureInfo.InvariantCulture) > 0)
                throw new InvalidOperationException(
                    "QA closure is blocked because the replacement investigation does not capture every current nonconforming/check-required result and every traceable legacy v0 evidence source using the current immutable v1 snapshot.");
        }

        private static int CreateLegacyPrmEvidenceReconciliationsInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int replacementQualityEventId,
            int sampleId,
            int electronicSignatureId,
            string reconciledBy,
            string reason)
        {
            object count = ExecuteScalarInTransaction(connection, transaction, @"
DECLARE @Inserted TABLE
(
    ReconciliationID INT NOT NULL,
    LegacyQualityEventID INT NOT NULL
);

INSERT dbo.PRM_QualityEventEvidenceReconciliations
(
    LegacyQualityEventID,
    ReplacementQualityEventID,
    SampleID,
    ReconciledBy,
    ReconciledAt,
    ReconciliationReason,
    ElectronicSignatureID,
    ReconciliationSchemaVersion
)
OUTPUT INSERTED.ReconciliationID,INSERTED.LegacyQualityEventID
INTO @Inserted(ReconciliationID,LegacyQualityEventID)
SELECT
    legacyEvent.QualityEventID,
    @ReplacementQualityEventID,
    @SampleID,
    @ReconciledBy,
    SYSDATETIME(),
    @Reason,
    @ElectronicSignatureID,
    1
FROM dbo.QualityEvents legacyEvent WITH (UPDLOCK,HOLDLOCK)
WHERE legacyEvent.SourceModule=N'PRM'
  AND legacyEvent.SourceRecordID=@SampleID
  AND legacyEvent.CurrentStatus=N'Closed'
  AND legacyEvent.QualityEventID<@ReplacementQualityEventID
  AND EXISTS
  (
      SELECT 1
      FROM dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK,HOLDLOCK)
      WHERE legacyAffected.QualityEventID=legacyEvent.QualityEventID
        AND legacyAffected.SourceModule=N'PRM'
        AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
  )
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK,HOLDLOCK)
      WHERE legacyAffected.QualityEventID=legacyEvent.QualityEventID
        AND legacyAffected.SourceModule=N'PRM'
        AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
        AND
        (
            legacyAffected.SourceResultID IS NULL
            OR NOT EXISTS
            (
                SELECT 1
                FROM dbo.QualityEventAffectedResults replacementAffected WITH (UPDLOCK,HOLDLOCK)
                INNER JOIN dbo.PRM_SampleTests currentResult WITH (UPDLOCK,HOLDLOCK)
                    ON currentResult.SampleTestID=replacementAffected.SourceResultID
                   AND currentResult.SampleID=@SampleID
                WHERE replacementAffected.QualityEventID=@ReplacementQualityEventID
                  AND replacementAffected.SourceModule=N'PRM'
                  AND replacementAffected.SourceResultID=legacyAffected.SourceResultID
                  AND ISNULL(replacementAffected.EvidenceSchemaVersion,0)=1
                  AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.TestName,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.TestName,N''))
                  AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.ResultValue,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.ResultValue,N''))
                  AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.SpecificationLimit,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.SpecificationText,N''))
                  AND (replacementAffected.SpecificationNumericLimit=currentResult.SpecificationLimit OR (replacementAffected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
                  AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.Unit,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.Unit,N''))
                  AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.FailureType,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.Interpretation,N''))
            )
        )
  )
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.PRM_QualityEventEvidenceReconciliations existing WITH (UPDLOCK,HOLDLOCK)
      WHERE existing.LegacyQualityEventID=legacyEvent.QualityEventID
        AND existing.ReplacementQualityEventID=@ReplacementQualityEventID
  );

INSERT dbo.QualityEventActions
(
    QualityEventID,ActionType,ActionDescription,PerformedBy,PerformedDate
)
SELECT
    @ReplacementQualityEventID,
    N'Legacy Evidence Reconciliation',
    N'Historical PRM Quality Event ' + ISNULL(NULLIF(legacyEvent.EventNumber,N''),CONVERT(NVARCHAR(20),legacyEvent.QualityEventID)) +
    N' explicitly reconciled by this controlled replacement investigation. Historical affected-result evidence was not rewritten.',
    @ReconciledBy,
    SYSDATETIME()
FROM @Inserted insertedRec
INNER JOIN dbo.QualityEvents legacyEvent
    ON legacyEvent.QualityEventID=insertedRec.LegacyQualityEventID;

SELECT COUNT(1) FROM @Inserted;",
                new SqlParameter("@ReplacementQualityEventID", SqlDbType.Int) { Value = replacementQualityEventId },
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId },
                new SqlParameter("@ReconciledBy", SqlDbType.NVarChar, 120) { Value = reconciledBy },
                new SqlParameter("@Reason", SqlDbType.NVarChar, 1000)
                {
                    Value = "QA closure explicitly reconciles eligible historical pre-v183 PRM evidence to the controlled replacement investigation. " + (reason ?? string.Empty)
                },
                new SqlParameter("@ElectronicSignatureID", SqlDbType.Int) { Value = electronicSignatureId });

            return Convert.ToInt32(count ?? 0, CultureInfo.InvariantCulture);
        }

        private static bool IsClosedQualityEventStatus(string status)
        {
            return status.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Rejected Closed", StringComparison.OrdinalIgnoreCase);
        }

        private bool ValidateBeforeSubmitToQA()
        {
            if (string.IsNullOrWhiteSpace(txtInitialDescription.Text))
            {
                MessageBox.Show(
                    "Initial description is required.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtInitialDescription.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtImmediateAction.Text))
            {
                MessageBox.Show(
                    "Immediate action is required.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtImmediateAction.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtImpactAssessment.Text))
            {
                MessageBox.Show(
                    "Impact assessment is required before QA review.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtImpactAssessment.Focus();
                return false;
            }

            if (!ValidateRequiredChecklist("QA review"))
                return false;

            return true;
        }

        private bool ValidateRequiredChecklist(string stage)
        {
            CommitGridEdits();
            NormalizeChecklistAnswers(checklistTable);

            if (checklistTable == null || checklistTable.Rows.Count == 0)
            {
                MessageBox.Show(
                    "No controlled PRM investigation checklist is configured. Apply migration 20260824_001 before continuing.",
                    "Checklist Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                bool isRequired =
                    row.Table.Columns.Contains("IsRequired") &&
                    row["IsRequired"] != DBNull.Value &&
                    Convert.ToBoolean(row["IsRequired"]);

                string answer = GetSafeString(row, "AnswerValue");
                string expected = GetSafeString(row, "ExpectedAnswer");
                string comments = GetSafeString(row, "Comments");

                if (isRequired && string.IsNullOrWhiteSpace(answer))
                {
                    MessageBox.Show(
                        "Required PRM checklist answer is missing before " + stage + ".\n\n" +
                        "Section: " + GetSafeString(row, "SectionName") + "\n" +
                        "Question: " + GetSafeString(row, "QuestionText") + "\n\n" +
                        "Select Yes, No, or N/A in the Answer column.",
                        "PRM Checklist",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    FocusChecklistRow(row);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(answer) &&
                    AnswerRequiresComment(answer, expected) &&
                    string.IsNullOrWhiteSpace(comments))
                {
                    MessageBox.Show(
                        "Comment/evidence is required before " + stage + " because the answer indicates a potential finding.\n\n" +
                        "Section: " + GetSafeString(row, "SectionName") + "\n" +
                        "Question: " + GetSafeString(row, "QuestionText") + "\n" +
                        "Answer: " + answer,
                        "PRM Checklist",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    FocusChecklistRow(row);
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(answer) &&
                    AnswerRequiresComment(answer, expected) &&
                    comments.Trim().Length < 10)
                {
                    MessageBox.Show(
                        "Comment/evidence is too short for a finding answer.\n\n" +
                        "Section: " + GetSafeString(row, "SectionName") + "\n" +
                        "Question: " + GetSafeString(row, "QuestionText") + "\n\n" +
                        "Enter a meaningful explanation or evidence reference.",
                        "PRM Checklist",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    FocusChecklistRow(row);
                    return false;
                }
            }

            return true;
        }

        private bool AnswerRequiresComment(string answer, string expected)
        {
            string a = (answer ?? "").Trim();
            string e = (expected ?? "").Trim();

            if (string.IsNullOrWhiteSpace(a))
                return false;

            if (a.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                return true;

            if (string.IsNullOrWhiteSpace(e))
                return a.Equals("No", StringComparison.OrdinalIgnoreCase);

            if (e.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                return !a.Equals("Yes", StringComparison.OrdinalIgnoreCase);

            if (e.Equals("No", StringComparison.OrdinalIgnoreCase))
                return !a.Equals("No", StringComparison.OrdinalIgnoreCase);

            return false;
        }

        private void CommitGridEdits()
        {
            try
            {
                dgChecklist?.CommitEdit(DataGridEditingUnit.Cell, true);
                dgChecklist?.CommitEdit(DataGridEditingUnit.Row, true);

                dgPhaseIIChecklist?.CommitEdit(DataGridEditingUnit.Cell, true);
                dgPhaseIIChecklist?.CommitEdit(DataGridEditingUnit.Row, true);

                dgClosureChecklist?.CommitEdit(DataGridEditingUnit.Cell, true);
                dgClosureChecklist?.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to commit one or more PRM investigation checklist edits.", ex);
            }
        }

        private void NormalizeChecklistAnswers(DataTable table)
        {
            if (table == null || !table.Columns.Contains("AnswerValue"))
                return;

            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string answer = GetSafeString(row, "AnswerValue");

                if (answer.Contains("Yes", StringComparison.OrdinalIgnoreCase))
                    row["AnswerValue"] = "Yes";
                else if (answer.Contains("No", StringComparison.OrdinalIgnoreCase))
                    row["AnswerValue"] = "No";
                else if (answer.Contains("N/A", StringComparison.OrdinalIgnoreCase) || answer.Contains("NA", StringComparison.OrdinalIgnoreCase))
                    row["AnswerValue"] = "N/A";
                else if (string.IsNullOrWhiteSpace(answer))
                    row["AnswerValue"] = "";
            }
        }

        private void FocusChecklistRow(DataRow row)
        {
            if (row == null || checklistTable == null)
                return;

            try
            {
                DataGrid targetGrid = ResolveChecklistGridForRow(row);
                if (targetGrid == null)
                    targetGrid = dgChecklist;

                foreach (object item in targetGrid.Items)
                {
                    if (item is DataRowView view && ReferenceEquals(view.Row, row))
                    {
                        targetGrid.SelectedItem = view;
                        targetGrid.ScrollIntoView(view);
                        targetGrid.Focus();
                        return;
                    }
                }

                foreach (DataRowView view in checklistTable.DefaultView)
                {
                    if (ReferenceEquals(view.Row, row))
                    {
                        dgChecklist.SelectedItem = view;
                        dgChecklist.ScrollIntoView(view);
                        dgChecklist.Focus();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to focus the selected PRM investigation checklist row.", ex);
            }
        }

        private FlowDocument BuildPRMInvestigationReportDocument()
        {
            FlowDocument document = new FlowDocument();
            document.PagePadding = new Thickness(36);
            document.FontFamily = new FontFamily("Segoe UI");
            document.FontSize = 10.5;
            document.ColumnWidth = double.PositiveInfinity;

            AddReportHeader(document);
            AddSummarySection(document);
            AddChecklistSection(document);
            AddAffectedResultsSection(document);
            AddActionsSection(document);
            AddQAConclusionSection(document);
            AddReportFooter(document);

            return document;
        }

        private void AddReportHeader(FlowDocument document)
        {
            Table headerTable = new Table();
            headerTable.CellSpacing = 0;
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(120) });
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(380) });
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(220) });

            TableRowGroup group = new TableRowGroup();
            headerTable.RowGroups.Add(group);

            TableRow row = new TableRow();

            TableCell logoCell = MakeReportCell("M", true, 26, TextAlignment.Center);
            logoCell.Padding = new Thickness(10);
            row.Cells.Add(logoCell);

            TableCell companyCell = new TableCell();
            companyCell.Background = new SolidColorBrush(Color.FromRgb(31, 45, 64));
            companyCell.Padding = new Thickness(10);
            companyCell.BorderBrush = new SolidColorBrush(Color.FromRgb(31, 45, 64));
            companyCell.BorderThickness = new Thickness(0.5);

            Paragraph company = new Paragraph(new Run("MEDICA PHARMACEUTICAL INDUSTRY"));
            company.TextAlignment = TextAlignment.Center;
            company.FontSize = 16;
            company.FontWeight = FontWeights.Bold;
            company.Foreground = Brushes.White;
            company.Margin = new Thickness(0, 0, 0, 3);

            Paragraph dept = new Paragraph(new Run("Quality Control / PRM Investigation"));
            dept.TextAlignment = TextAlignment.Center;
            dept.FontSize = 10.5;
            dept.Foreground = Brushes.White;
            dept.Margin = new Thickness(0);

            companyCell.Blocks.Add(company);
            companyCell.Blocks.Add(dept);
            row.Cells.Add(companyCell);

            TableCell infoCell = MakeReportCell(
                "Form No: MQC-F-PRM-QE-001\nVersion: 01\nPage: Generated Report",
                false,
                8.5,
                TextAlignment.Right);
            infoCell.Background = new SolidColorBrush(Color.FromRgb(31, 45, 64));
            infoCell.Foreground = Brushes.White;
            infoCell.BorderBrush = new SolidColorBrush(Color.FromRgb(31, 45, 64));
            row.Cells.Add(infoCell);

            group.Rows.Add(row);
            document.Blocks.Add(headerTable);

            Paragraph title = new Paragraph(new Run("PRM QUALITY EVENT INVESTIGATION REPORT"));
            title.TextAlignment = TextAlignment.Center;
            title.FontSize = 18;
            title.FontWeight = FontWeights.Bold;
            title.Margin = new Thickness(0, 12, 0, 2);
            document.Blocks.Add(title);

            Paragraph subtitle = new Paragraph(new Run("Raw Material / Stability / Production / Finished Product Investigation"));
            subtitle.TextAlignment = TextAlignment.Center;
            subtitle.FontSize = 11;
            subtitle.Foreground = Brushes.DimGray;
            subtitle.Margin = new Thickness(0, 0, 0, 10);
            document.Blocks.Add(subtitle);

            AddTwoColumnTable(document,
                "Report No", GenerateInvestigationReportNumber(),
                "Event No", lblEventNumber.Text,
                "Event Type", lblEventType.Text,
                "PRM Type", lblPRMType.Text,
                "Sample No", lblSampleNumber.Text,
                "Status", lblHeaderStatus.Text,
                "Detected By", lblDetectedBy.Text,
                "Detected Date", lblDetectedDate.Text,
                "Material/Product", lblMaterialProduct.Text,
                "Batch/Lot", lblBatchLot.Text);
        }

        private void AddSummarySection(FlowDocument document)
        {
            AddSectionTitle(document, "1. Investigation Summary");

            AddTwoColumnTable(document,
                "Severity", GetComboText(cboSeverity),
                "Root Cause Category", GetComboText(cboRootCauseCategory),
                "Final Disposition", GetComboText(cboFinalDisposition),
                "CAPA Required", chkCAPARequired.IsChecked == true ? "Yes" : "No");

            document.Blocks.Add(MakeParagraph("Initial Description", true));
            document.Blocks.Add(MakeParagraph(FirstNonEmpty(txtInitialDescription.Text)));

            document.Blocks.Add(MakeParagraph("Immediate Action", true));
            document.Blocks.Add(MakeParagraph(FirstNonEmpty(txtImmediateAction.Text)));

            document.Blocks.Add(MakeParagraph("Root Cause Details", true));
            document.Blocks.Add(MakeParagraph(FirstNonEmpty(txtRootCauseDetails.Text)));

            document.Blocks.Add(MakeParagraph("Impact Assessment", true));
            document.Blocks.Add(MakeParagraph(FirstNonEmpty(txtImpactAssessment.Text)));
        }

        private void AddChecklistSection(FlowDocument document)
        {
            AddSectionTitle(document, "2. Investigation Checklist by Phase");

            if (checklistTable == null || checklistTable.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No checklist records found."));
                return;
            }

            AddChecklistPhaseReportSection(document, "2.1 Phase I - Initial Laboratory Investigation", "Phase I - Initial Laboratory Investigation");
            AddChecklistPhaseReportSection(document, "2.2 Phase II - Full Scale Investigation", "Phase II - Full Scale Investigation");
            AddChecklistPhaseReportSection(document, "2.3 QA Closure Checklist", "QA Closure");
        }

        private void AddChecklistPhaseReportSection(FlowDocument document, string title, string phaseName)
        {
            bool hasRows = false;

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                if (ResolveChecklistPhaseName(row).Equals(phaseName, StringComparison.OrdinalIgnoreCase))
                {
                    hasRows = true;
                    break;
                }
            }

            if (!hasRows)
                return;

            Paragraph phaseTitle = new Paragraph(new Run(title));
            phaseTitle.FontSize = 12.5;
            phaseTitle.FontWeight = FontWeights.Bold;
            phaseTitle.Margin = new Thickness(0, 8, 0, 3);
            phaseTitle.Foreground = new SolidColorBrush(Color.FromRgb(31, 45, 64));
            document.Blocks.Add(phaseTitle);

            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(new TableColumn { Width = new GridLength(145) });
            table.Columns.Add(new TableColumn { Width = new GridLength(350) });
            table.Columns.Add(new TableColumn { Width = new GridLength(70) });
            table.Columns.Add(new TableColumn { Width = new GridLength(220) });

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportCell("Section", true, 9));
            header.Cells.Add(MakeReportCell("Question", true, 9));
            header.Cells.Add(MakeReportCell("Answer", true, 9, TextAlignment.Center));
            header.Cells.Add(MakeReportCell("Comments / Evidence", true, 9));
            group.Rows.Add(header);

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                if (!ResolveChecklistPhaseName(row).Equals(phaseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                TableRow tr = new TableRow();
                tr.Cells.Add(MakeReportCell(GetSafeString(row, "SectionName"), false, 8.5));
                tr.Cells.Add(MakeReportCell(GetSafeString(row, "QuestionText"), false, 8.5));
                tr.Cells.Add(MakeReportCell(GetSafeString(row, "AnswerValue"), false, 8.5, TextAlignment.Center));
                tr.Cells.Add(MakeReportCell(GetSafeString(row, "Comments"), false, 8.5));
                group.Rows.Add(tr);
            }

            document.Blocks.Add(table);
        }

        private void AddAffectedResultsSection(FlowDocument document)
        {
            AddSectionTitle(document, "3. Affected Result(s)");

            if (affectedResultsTable == null || affectedResultsTable.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No affected result records found."));
                return;
            }

            AddDataTable(document, affectedResultsTable);
        }

        private void AddActionsSection(FlowDocument document)
        {
            AddSectionTitle(document, "4. Action History");

            if (actionsTable == null || actionsTable.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No action records found."));
                return;
            }

            AddDataTable(document, actionsTable);
        }

        private void AddQAConclusionSection(FlowDocument document)
        {
            AddSectionTitle(document, "5. QA Conclusion and Disposition");

            AddTwoColumnTable(document,
                "Final Disposition", GetComboText(cboFinalDisposition),
                "CAPA Required", chkCAPARequired.IsChecked == true ? "Yes" : "No",
                "Investigation Status", lblHeaderStatus.Text,
                "Printed By", currentUser);

            document.Blocks.Add(MakeParagraph("QA Conclusion", true));
            document.Blocks.Add(MakeParagraph(FirstNonEmpty(txtQAConclusion.Text)));
        }

        private void AddReportFooter(FlowDocument document)
        {
            Paragraph separator = new Paragraph(new Run(""));
            separator.BorderBrush = Brushes.LightGray;
            separator.BorderThickness = new Thickness(0, 0.5, 0, 0);
            separator.Margin = new Thickness(0, 12, 0, 4);
            document.Blocks.Add(separator);

            Paragraph footer = new Paragraph(new Run(
                "This report is electronically generated from PharmaLIMS. " +
                "Investigation records, checklist answers, and print activity are captured in the application audit trail.\n" +
                "Verification Reference: " + BuildVerificationCode()));
            footer.FontSize = 9;
            footer.Foreground = Brushes.DimGray;
            footer.TextAlignment = TextAlignment.Center;
            footer.Margin = new Thickness(0, 0, 0, 2);
            document.Blocks.Add(footer);
        }

        private string GenerateInvestigationReportNumber()
        {
            if (!loadedEventCreatedDate.HasValue)
                throw new InvalidOperationException("PRM Quality Event Created Date is missing. A controlled investigation report number cannot be generated.");

            return "PRM-QEIR-" + loadedEventCreatedDate.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                   "-" + qualityEventId.ToString("0000", CultureInfo.InvariantCulture);
        }

        private string BuildVerificationCode()
        {
            string no = string.IsNullOrWhiteSpace(eventNumber) ? "PRM-QE-" + qualityEventId.ToString("0000") : eventNumber;
            return no + "-PRM-QEIR-V1";
        }

        private void AddSectionTitle(FlowDocument document, string title)
        {
            Paragraph paragraph = new Paragraph(new Run(title ?? ""));
            paragraph.FontSize = 14;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Margin = new Thickness(0, 12, 0, 4);
            paragraph.BorderBrush = Brushes.LightGray;
            paragraph.BorderThickness = new Thickness(0, 0, 0, 0.5);
            document.Blocks.Add(paragraph);
        }

        private Paragraph MakeParagraph(string text, bool bold = false, double fontSize = 10.5)
        {
            Paragraph paragraph = new Paragraph(new Run(text ?? ""));
            paragraph.Margin = new Thickness(0, 2, 0, 5);
            paragraph.FontSize = fontSize;

            if (bold)
                paragraph.FontWeight = FontWeights.Bold;

            return paragraph;
        }

        private TableCell MakeReportCell(string text, bool bold = false, double fontSize = 9, TextAlignment alignment = TextAlignment.Left)
        {
            Paragraph paragraph = new Paragraph(new Run(text ?? ""));
            paragraph.Margin = new Thickness(0);
            paragraph.FontSize = fontSize;
            paragraph.TextAlignment = alignment;
            paragraph.LineHeight = fontSize + 3;

            TableCell cell = new TableCell(paragraph);
            cell.Padding = new Thickness(4);
            cell.BorderBrush = Brushes.LightGray;
            cell.BorderThickness = new Thickness(0.5);

            if (bold)
                cell.FontWeight = FontWeights.Bold;

            return cell;
        }

        private void AddTwoColumnTable(FlowDocument document, params string[] values)
        {
            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(new TableColumn { Width = new GridLength(150) });
            table.Columns.Add(new TableColumn { Width = new GridLength(220) });
            table.Columns.Add(new TableColumn { Width = new GridLength(150) });
            table.Columns.Add(new TableColumn { Width = new GridLength(220) });

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (int i = 0; i < values.Length; i += 4)
            {
                TableRow row = new TableRow();

                row.Cells.Add(MakeReportCell(values.Length > i ? values[i] : "", true));
                row.Cells.Add(MakeReportCell(values.Length > i + 1 ? values[i + 1] : ""));

                row.Cells.Add(MakeReportCell(values.Length > i + 2 ? values[i + 2] : "", true));
                row.Cells.Add(MakeReportCell(values.Length > i + 3 ? values[i + 3] : ""));

                group.Rows.Add(row);
            }

            document.Blocks.Add(table);
        }

        private void AddDataTable(FlowDocument document, DataTable table)
        {
            if (table == null || table.Rows.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No records found."));
                return;
            }

            int maxColumns = Math.Min(table.Columns.Count, 6);

            Table reportTable = new Table();
            reportTable.CellSpacing = 0;

            for (int i = 0; i < maxColumns; i++)
                reportTable.Columns.Add(new TableColumn());

            TableRowGroup group = new TableRowGroup();
            reportTable.RowGroups.Add(group);

            TableRow header = new TableRow();
            for (int i = 0; i < maxColumns; i++)
                header.Cells.Add(MakeReportCell(table.Columns[i].ColumnName, true, 8.5));
            group.Rows.Add(header);

            foreach (DataRow dataRow in table.Rows)
            {
                if (dataRow.RowState == DataRowState.Deleted)
                    continue;

                TableRow row = new TableRow();

                for (int i = 0; i < maxColumns; i++)
                    row.Cells.Add(MakeReportCell(GetSafeString(dataRow, table.Columns[i].ColumnName), false, 8));

                group.Rows.Add(row);
            }

            document.Blocks.Add(reportTable);
        }

        private void TryAddPrintHistory()
        {
            try
            {
                DatabaseHelper.AddQualityEventPrintHistory(qualityEventId, currentUser);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to add PRM quality-event print history.", ex);
            }
        }

        private void SelectComboText(ComboBox combo, string value)
        {
            if (combo == null)
                return;

            string v = value ?? "";

            foreach (object item in combo.Items)
            {
                if (item is ComboBoxItem comboBoxItem)
                {
                    string text = comboBoxItem.Content?.ToString() ?? "";
                    if (text.Equals(v, StringComparison.OrdinalIgnoreCase))
                    {
                        combo.SelectedItem = comboBoxItem;
                        return;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(v))
            {
                ComboBoxItem legacyItem = new ComboBoxItem
                {
                    Content = v,
                    IsEnabled = false,
                    ToolTip = "Legacy stored value. Select a current controlled investigation disposition before saving/closure."
                };
                combo.Items.Add(legacyItem);
                combo.SelectedItem = legacyItem;
                return;
            }

            if (combo.Items.Count > 0 && combo.SelectedIndex < 0)
                combo.SelectedIndex = 0;
        }

        private string GetComboText(ComboBox combo)
        {
            if (combo == null)
                return "";

            if (combo.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "";

            return combo.SelectedItem?.ToString() ?? combo.Text ?? "";
        }

        private string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private string GetSafeString(DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return "";

            if (row[columnName] == null || row[columnName] == DBNull.Value)
                return "";

            return row[columnName].ToString() ?? "";
        }

        private int GetSafeInt(DataRow row, string columnName)
        {
            string value = GetSafeString(row, columnName);
            if (int.TryParse(value, out int result))
                return result;

            return 0;
        }

        private DateTime? GetSafeDateTime(DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return null;

            if (row[columnName] == null || row[columnName] == DBNull.Value)
                return null;

            if (DateTime.TryParse(row[columnName].ToString(), out DateTime result))
                return result;

            return null;
        }

        private sealed class PRMEventLoadData
        {
            public DataTable Header { get; init; } = new DataTable();
            public string CategoryText { get; init; } = string.Empty;
            public DataTable Checklist { get; init; } = new DataTable();
            public DataTable AffectedResults { get; init; } = new DataTable();
            public DataTable Actions { get; init; } = new DataTable();
        }
    }
}
