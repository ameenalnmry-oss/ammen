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
    public partial class EMResultsEntry : Window
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IAuthService _authService;

        private int currentEventId = 0;
        private string currentEventNo = "";
        private int currentPlanId = 0;
        private string currentPlanNo = "";
        private string currentNegativeControlResult = "";
        private string currentUser = "";
        private string workflowStatus = "Pending";
        private string currentFinalResult = "Pending";
        private int currentQualityEventId = 0;
        private string currentQualityEventNo = "";
        private string currentQualityEventStatus = "";
        private string currentQualityEventQaDisposition = "";
        private string currentQualityEventRootCause = "";
        private string currentQualityEventCapa = "";
        private string currentQualityEventClosedBy = "";
        private string currentQualityEventClosedDate = "";

        private string currentSamplingTimeFrom = "";
        private string currentSamplingTimeTo = "";
        private string currentActivityNoOfPersons = "";
        private string currentAirSamplerNo = "";
        private string currentAirSamplingTime = "";
        private string currentSanitizationDetails = "";
        private string currentSanitizationTime = "";
        private string currentDisinfectantUsed = "";
        private string currentIncubationTemperature = "";
        private string currentIncubatorNo1 = "";
        private string currentIncubatorNo2 = "";
        private string currentIncubationStart = "";
        private string currentIncubationEnd = "";
        private string currentRemarks = "";
        private string currentMonitoringCategory = "";
        private string currentDispensingBooth = "";
        private string currentMaterialName = "";
        private string currentBatchNo = "";
        private string currentEmployeeId = "";
        private string currentEmployeeName = "";
        private string currentEmployeeDepartment = "";
        private string currentEmployeeShift = "";
        private string currentSamplingStage = "";
        private string currentSurfaceLocation = "";
        private string currentSurfaceType = "";
        private string currentSurfaceAreaCm2 = "";
        private string currentSwabKitLot = "";
        private string currentDiluentLot = "";
        private string currentRecoveryVolumeMl = "";

        private List<EMPlateResultItem> plateItems = new List<EMPlateResultItem>();
        private DataTable _loadedPlateSnapshot;
        private bool isSaving = false;
        private bool isWorkflowActionInProgress = false;
        private bool reportPrintedThisSession = false;
        private DateTime reportGeneratedAt = DateTime.MinValue;
        private string reportVerificationReference = "";
        private string reportPrintedByDisplay = "";

        // UI permission snapshot. Regulated actions still revalidate authorization
        // against the database when the user executes the action.
        private bool currentCanEditResults = false;
        private bool currentCanSubmitForReview = false;
        private bool currentCanReviewResults = false;
        private bool currentCanApproveResults = false;
        private bool currentCanManageSettings = false;

        private sealed class PaginatorSource : IDocumentPaginatorSource
        {
            public PaginatorSource(DocumentPaginator paginator)
            {
                DocumentPaginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
            }

            public DocumentPaginator DocumentPaginator { get; }
        }

        private sealed class FramedDocumentPaginator : DocumentPaginator
        {
            private readonly DocumentPaginator _innerPaginator;
            private readonly Brush _outerBorderBrush;
            private readonly Brush _innerBorderBrush;
            private readonly string _verificationReference;

            public FramedDocumentPaginator(DocumentPaginator innerPaginator, string verificationReference)
            {
                _innerPaginator = innerPaginator ?? throw new ArgumentNullException(nameof(innerPaginator));
                _verificationReference = verificationReference ?? "";
                _outerBorderBrush = new SolidColorBrush(Color.FromRgb(15, 76, 129));
                _innerBorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184));

                if (_outerBorderBrush.CanFreeze)
                    _outerBorderBrush.Freeze();

                if (_innerBorderBrush.CanFreeze)
                    _innerBorderBrush.Freeze();
            }

            public override bool IsPageCountValid => _innerPaginator.IsPageCountValid;
            public override int PageCount => _innerPaginator.PageCount;
            public override IDocumentPaginatorSource Source => _innerPaginator.Source;

            public override Size PageSize
            {
                get => _innerPaginator.PageSize;
                set => _innerPaginator.PageSize = value;
            }

            public override DocumentPage GetPage(int pageNumber)
            {
                DocumentPage page = _innerPaginator.GetPage(pageNumber);
                if (page == DocumentPage.Missing)
                    return page;

                var root = new ContainerVisual();

                var background = new DrawingVisual();
                using (DrawingContext dc = background.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(new Point(0, 0), page.Size));
                }

                root.Children.Add(background);
                root.Children.Add(page.Visual);

                var decoration = new DrawingVisual();
                using (DrawingContext dc = decoration.RenderOpen())
                {
                    Rect outer = new Rect(8, 8, Math.Max(0, page.Size.Width - 16), Math.Max(0, page.Size.Height - 16));
                    Rect inner = new Rect(13, 13, Math.Max(0, page.Size.Width - 26), Math.Max(0, page.Size.Height - 26));
                    dc.DrawRectangle(null, new Pen(_outerBorderBrush, 2.0), outer);
                    dc.DrawRectangle(null, new Pen(_innerBorderBrush, 0.75), inner);

                    string pageText = "Page " + (pageNumber + 1).ToString(CultureInfo.InvariantCulture) +
                                      " of " + PageCount.ToString(CultureInfo.InvariantCulture);
                    var pageNumberText = new FormattedText(
                        pageText,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Arial"),
                        8.2,
                        new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                        1.0);

                    dc.DrawText(
                        pageNumberText,
                        new Point(page.Size.Width - pageNumberText.Width - 28, page.Size.Height - 29));

                    if (!string.IsNullOrWhiteSpace(_verificationReference))
                    {
                        var referenceText = new FormattedText(
                            "Verification: " + _verificationReference,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            new Typeface("Arial"),
                            7.2,
                            new SolidColorBrush(Color.FromRgb(100, 116, 139)),
                            1.0);

                        dc.DrawText(referenceText, new Point(28, page.Size.Height - 28));
                    }
                }

                root.Children.Add(decoration);
                return new DocumentPage(root, page.Size, page.BleedBox, page.ContentBox);
            }
        }

        public EMResultsEntry(IServiceProvider serviceProvider, IAuthService authService)
        {
            InitializeComponent();
            _serviceProvider = serviceProvider;
            _authService = authService;

            InitializeUser();
            SetFinalResultStatus("");
            RefreshCounters();
            UpdateWorkflowButtons();
        }

        public void OpenEvent(string eventNo)
        {
            if (string.IsNullOrWhiteSpace(eventNo)) return;
            txtEventNo.Text = eventNo.Trim();
            LoadEMEvent(eventNo.Trim());
        }

        public class EMPlateResultItem : System.ComponentModel.INotifyPropertyChanged
        {
            private int? totalCount;
            private int? airVolumeLiters;
            private string remarks = "";
            private string status = "Pending";

            public int PlateId { get; set; }
            public int EventId { get; set; }
            public string Method { get; set; } = "";
            public string PlateCode { get; set; } = "";
            public int SequenceNo { get; set; }
            public string Grade { get; set; } = "";
            public string Unit { get; set; } = "";
            public decimal? AlertLimit { get; set; }
            public decimal? ActionLimit { get; set; }
            public bool HasLimitSnapshot { get; set; }
            public int? LimitReconciliationId { get; set; }
            public string LimitEvidenceSource { get; set; } = "Native Frozen Snapshot";

            public int? TotalCount
            {
                get => totalCount;
                set
                {
                    if (totalCount != value)
                    {
                        totalCount = value;
                        OnPropertyChanged(nameof(TotalCount));
                    }
                }
            }

            public int? AirVolumeLiters
            {
                get => airVolumeLiters;
                set
                {
                    if (airVolumeLiters != value)
                    {
                        airVolumeLiters = value;
                        OnPropertyChanged(nameof(AirVolumeLiters));
                    }
                }
            }

            public string ResultCFU { get; set; } = "";

            public string Status
            {
                get => status;
                set
                {
                    if (status != value)
                    {
                        status = value;
                        OnPropertyChanged(nameof(Status));
                    }
                }
            }

            public string Remarks
            {
                get => remarks;
                set
                {
                    if (remarks != value)
                    {
                        remarks = value;
                        OnPropertyChanged(nameof(Remarks));
                    }
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string name) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        private class EMPlateLimitInfo
        {
            public string Unit { get; set; } = "";
            public decimal? AlertLimit { get; set; }
            public decimal? ActionLimit { get; set; }
            public int? AirVolumeLiters { get; set; }
        }

        private class AuditEntry
        {
            public string TableName { get; set; } = "";
            public int RecordId { get; set; }
            public string Action { get; set; } = "";
            public string OldValue { get; set; } = "";
            public string NewValue { get; set; } = "";
            public string Reason { get; set; } = "";
            public string PerformedBy { get; set; } = "";
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                lblStatus.Text = "Validating controlled EM database schema...";
                await EnsureEMStatusColumnsAreTextAsync();
                await LoadUserPermissionsAsync();
                UpdateWorkflowButtons();
                txtEventNo.Focus();
                Keyboard.Focus(txtEventNo);
                UpdateWorkflowProgress();
            }
            catch (Exception ex)
            {
                string message = Infrastructure.UserFacingError.SafeMessage(ex, "EM Results Entry initialization");
                MessageBox.Show(message, "EM Results Entry", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
                return;

            if (Keyboard.FocusedElement is DataGridCell)
            {
                CommitAndMoveToNextGridCell();
                e.Handled = true;
                return;
            }

            WindowUsability.TryMoveFocusOnEnter(e);
        }

        private void CommitAndMoveToNextGridCell()
        {
            DataGrid grid = dgActiveAirPlates.IsKeyboardFocusWithin ? dgActiveAirPlates : dgSettlePlates;
            if (grid == null)
                return;

            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);

            int rowIndex = grid.Items.IndexOf(grid.CurrentItem);
            int columnIndex = grid.Columns.IndexOf(grid.CurrentColumn);
            if (rowIndex < 0 || columnIndex < 0)
                return;

            int nextColumn = columnIndex + 1;
            int nextRow = rowIndex;

            while (nextColumn < grid.Columns.Count && grid.Columns[nextColumn].IsReadOnly)
                nextColumn++;

            if (nextColumn >= grid.Columns.Count)
            {
                nextRow++;
                nextColumn = 0;
                while (nextColumn < grid.Columns.Count && grid.Columns[nextColumn].IsReadOnly)
                    nextColumn++;
            }

            if (nextRow >= grid.Items.Count || nextColumn >= grid.Columns.Count)
                return;

            grid.SelectedIndex = nextRow;
            grid.CurrentCell = new DataGridCellInfo(grid.Items[nextRow], grid.Columns[nextColumn]);
            grid.ScrollIntoView(grid.Items[nextRow], grid.Columns[nextColumn]);
            grid.BeginEdit();
        }

        private void UpdateWorkflowProgress()
        {
            if (StepEntry == null)
                return;

            ResetWorkflowStep(StepEntry, StepEntryText);
            ResetWorkflowStep(StepSubmitted, StepSubmittedText);
            ResetWorkflowStep(StepReviewed, StepReviewedText);
            ResetWorkflowStep(StepApproved, StepApprovedText);
            ResetWorkflowStep(StepPrinted, StepPrintedText);

            if (currentEventId <= 0)
            {
                lblWorkflowHint.Text = "Load an EM event to view progress.";
                return;
            }

            string status = (workflowStatus ?? "Pending").Trim();
            SetWorkflowStepComplete(StepEntry, StepEntryText);

            if (status.Equals("Under Review", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Reviewed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                SetWorkflowStepComplete(StepSubmitted, StepSubmittedText);

            if (status.Equals("Reviewed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                SetWorkflowStepComplete(StepReviewed, StepReviewedText);

            if (status.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                SetWorkflowStepComplete(StepApproved, StepApprovedText);

            if (reportPrintedThisSession)
                SetWorkflowStepComplete(StepPrinted, StepPrintedText);

            lblWorkflowHint.Text = "Current stage: " + status + ". Entered plates: " + GetPlateCompletionText() + ".";
        }

        private static void ResetWorkflowStep(Border border, TextBlock text)
        {
            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E2E8F0"));
            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
            text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#475569"));
        }

        private static void SetWorkflowStepComplete(Border border, TextBlock text)
        {
            border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
            border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
            text.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#166534"));
        }

        private void InitializeUser()
        {
            var user = _authService.GetCurrentUser();

            if (user != null && !string.IsNullOrWhiteSpace(user.Username))
            {
                currentUser = user.Username.Trim();
                return;
            }

            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
            {
                currentUser = Login.CurrentUser.Trim();
                return;
            }

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to enter or review EM results.");
        }

        private async Task EnsureEMStatusColumnsAreTextAsync()
        {
            const string compatibilityQuery = @"
SELECT COUNT(1)
FROM sys.columns c
INNER JOIN sys.tables t ON t.object_id = c.object_id
INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE SCHEMA_NAME(t.schema_id) = N'dbo'
  AND ty.name = N'nvarchar'
  AND
  (
      (t.name = N'EM_EventPlates' AND c.name = N'Status' AND c.max_length >= 100)
      OR (t.name = N'EM_Events' AND c.name = N'FinalResult' AND c.max_length >= 100)
      OR (t.name = N'EM_Events' AND c.name = N'WorkflowStatus' AND c.max_length >= 100)
      OR (t.name = N'EM_EventPlates' AND c.name = N'ColoniesObserved' AND c.max_length >= 1000)
  );";

            object compatible = await Task.Run(() => DatabaseHelper.ExecuteScalar(compatibilityQuery));

            if (Convert.ToInt32(compatible, CultureInfo.InvariantCulture) != 4)
            {
                throw new InvalidOperationException(
                    "EM workflow columns are missing or incompatible. Apply Database/Migrations/20260714_001_Harden_GMP_Workflows.sql.");
            }

            bool snapshotReady = await Task.Run(IsEmLimitSnapshotSchemaReady);
            if (!snapshotReady)
            {
                string guidance = AppConfig.IsProduction
                    ? "Apply the approved database deployment before using EM Results Entry."
                    : "Run System Preflight and the explicit Development Database Maintenance action before using EM Results Entry.";

                throw new InvalidOperationException(
                    "Controlled EM limit snapshots/reconciliation controls are not installed. No database migration is allowed from an operational EM screen. " + guidance);
            }
        }

        private static bool IsEmLimitSnapshotSchemaReady()
        {
            object limitSnapshotReady = DatabaseHelper.ExecuteScalar(@"
SELECT CASE
         WHEN COL_LENGTH(N'dbo.EM_EventPlates',N'AlertLimitSnapshot') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_EventPlates',N'ActionLimitSnapshot') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_EventPlates',N'ResultUnitSnapshot') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_EventPlates',N'AirVolumeLitersSnapshot') IS NOT NULL
          AND COL_LENGTH(N'dbo.EM_GradeLimits',N'IsActive') IS NOT NULL
          AND OBJECT_ID(N'dbo.EM_GradeLimitSignatures',N'U') IS NOT NULL
          AND OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NOT NULL
          AND OBJECT_ID(N'dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828',N'TR') IS NOT NULL
          AND OBJECT_ID(N'dbo.TRG_EM_EventPlates_FreezeLimits_20260828',N'TR') IS NOT NULL
         THEN 1 ELSE 0 END;");

            return Convert.ToInt32(limitSnapshotReady, CultureInfo.InvariantCulture) == 1;
        }

        private string GetCurrentUserRole()
        {
            var user = _authService.GetCurrentUser();
            if (user != null && !string.IsNullOrWhiteSpace(user.Role))
                return user.Role;

            if (!string.IsNullOrWhiteSpace(Login.CurrentUserRole))
                return Login.CurrentUserRole;

            return "";
        }


        private async Task LoadUserPermissionsAsync()
        {
            string username = Login.CurrentUser ?? "";
            var permissions = await Task.Run(() =>
            {
                return (
                    CanEdit: DatabaseHelper.CanEditResults(username),
                    CanSubmit: DatabaseHelper.CanSubmitForReview(username),
                    CanReview: DatabaseHelper.CanReviewResults(username),
                    CanApprove: DatabaseHelper.CanApproveResults(username),
                    CanManage: DatabaseHelper.CanManageSettings(username));
            });

            currentCanEditResults = permissions.CanEdit;
            currentCanSubmitForReview = permissions.CanSubmit;
            currentCanReviewResults = permissions.CanReview;
            currentCanApproveResults = permissions.CanApprove;
            currentCanManageSettings = permissions.CanManage;

            BtnSaveResults.IsEnabled = currentCanEditResults;
            BtnCalculate.IsEnabled = currentCanEditResults;
            if (BtnManageLimits != null)
                BtnManageLimits.IsEnabled = currentCanManageSettings;
            if (BtnReconcileLegacySnapshot != null)
                BtnReconcileLegacySnapshot.IsEnabled = currentCanApproveResults;
            SetResultsGridsReadOnly(!currentCanEditResults);

            if (!currentCanEditResults)
            {
                lblStatus.Text = "Read-only mode. You don't have edit permissions.";
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
            }
        }

        private void SetResultsGridsReadOnly(bool isReadOnly)
        {
            if (dgActiveAirPlates != null)
                dgActiveAirPlates.IsReadOnly = isReadOnly;

            if (dgSettlePlates != null)
                dgSettlePlates.IsReadOnly = isReadOnly;
        }

        private bool AreAllPlateResultsEntered()
        {
            return plateItems != null &&
                   plateItems.Count > 0 &&
                   plateItems.Count(HasEnteredResult) == plateItems.Count;
        }

        private string GetPlateCompletionText()
        {
            int total = plateItems == null ? 0 : plateItems.Count;
            int entered = plateItems == null ? 0 : plateItems.Count(HasEnteredResult);

            return entered.ToString(CultureInfo.InvariantCulture) + " of " + total.ToString(CultureInfo.InvariantCulture);
        }

        private bool CanPrintApprovedEMReport(out string message)
        {
            message = "";

            if (currentEventId <= 0)
            {
                message = "No EM event is loaded.";
                return false;
            }

            if (!workflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase))
            {
                message = "EM Result Report can be printed only after Review and Approval. Current status: " + workflowStatus;
                return false;
            }

            if (!AreAllPlateResultsEntered())
            {
                message = "All plate results must be entered before printing the approved EM Result Report. Entered: " + GetPlateCompletionText() + ".";
                return false;
            }

            EMPlateResultItem missingLimits = plateItems.FirstOrDefault(item =>
                !item.AlertLimit.HasValue ||
                !item.ActionLimit.HasValue ||
                (IsActiveAirSampling(item.Method) &&
                 (!item.AirVolumeLiters.HasValue || item.AirVolumeLiters.Value <= 0)));
            if (missingLimits != null)
            {
                message = "Approved EM limits are incomplete for " + missingLimits.Grade +
                          " / " + missingLimits.Method +
                          ". Complete the recorded limits before printing the report.";
                return false;
            }

            if (HasOosResults() && !IsCurrentQualityEventClosed())
            {
                message = "Final EM report cannot be printed because one or more results are OOS/ACTION and the linked Quality Event investigation is not closed. Open/close the investigation first.";
                return false;
            }

            string databaseMessage;
            if (!DatabaseHelper.CanPrintEMResultReport(currentEventId, out databaseMessage))
            {
                message = databaseMessage;
                return false;
            }

            return true;
        }

        private void UpdateWorkflowButtons()
        {
            bool hasEvent = currentEventId > 0;
            bool canEdit = currentCanEditResults;
            bool canSubmit = currentCanSubmitForReview;
            bool canReview = currentCanReviewResults;
            bool canApprove = currentCanApproveResults;
            bool allPlatesEntered = AreAllPlateResultsEntered();
            bool oosGateOk = !HasOosResults() || IsCurrentQualityEventClosed();
            bool adminOverride = IsAdminWorkflowOverrideAllowed();
            bool currentUserCanReviewThisRecord = adminOverride || !HasCurrentUserSignedAction("EM Result Entry");
            bool currentUserCanApproveThisRecord = adminOverride || (!HasCurrentUserSignedAction("EM Result Entry") && !HasCurrentUserSignedAction("EM Review"));
            bool resultsLocked = IsEMResultsLockedForEditing();

            if (BtnSubmitReview != null)
                BtnSubmitReview.IsEnabled = hasEvent &&
                    canSubmit &&
                    allPlatesEntered &&
                    workflowStatus.Equals("Results Entered", StringComparison.OrdinalIgnoreCase);

            if (BtnReview != null)
                BtnReview.IsEnabled = hasEvent && canReview &&
                    currentUserCanReviewThisRecord &&
                    workflowStatus.Equals("Under Review", StringComparison.OrdinalIgnoreCase);

            if (BtnApprove != null)
                BtnApprove.IsEnabled = hasEvent && canApprove &&
                    currentUserCanApproveThisRecord &&
                    oosGateOk &&
                    workflowStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase);

            if (BtnPrintReport != null)
                BtnPrintReport.IsEnabled = hasEvent &&
                    allPlatesEntered &&
                    oosGateOk &&
                    workflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase);

            if (BtnSaveResults != null)
                BtnSaveResults.IsEnabled = canEdit &&
                    !resultsLocked;

            if (BtnCalculate != null)
                BtnCalculate.IsEnabled = canEdit &&
                    !resultsLocked;

            if (BtnManageLimits != null)
                BtnManageLimits.IsEnabled = currentCanManageSettings;
            if (BtnReconcileLegacySnapshot != null)
                BtnReconcileLegacySnapshot.IsEnabled = currentCanApproveResults;

            SetResultsGridsReadOnly(!canEdit || resultsLocked);

            if (BtnOpenDeviation != null)
            {
                bool qualityEventRelevant = hasEvent && (currentQualityEventId > 0 || HasQualityEventTriggerResults());
                BtnOpenDeviation.Visibility = qualityEventRelevant ? Visibility.Visible : Visibility.Collapsed;
                BtnOpenDeviation.IsEnabled = qualityEventRelevant;
                BtnOpenDeviation.Content = currentQualityEventId > 0 ? "Open Quality Event" : "Create Quality Event";
            }

            if (InvestigationBadge != null)
                InvestigationBadge.Visibility = hasEvent && (currentQualityEventId > 0 || HasQualityEventTriggerResults())
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            UpdateWorkflowProgress();
        }

        private bool IsEMResultsLockedForEditing()
        {
            return workflowStatus.Equals("Under Review", StringComparison.OrdinalIgnoreCase) ||
                   workflowStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase) ||
                   workflowStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase);
        }

        private void txtEventNo_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                LoadEMEventFromSearch();
        }

        private void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            LoadEMEventFromSearch();
        }

        private void BtnLoadPending_Click(object sender, RoutedEventArgs e)
        {
            LoadPendingEMEvents();
        }

        private void BtnManageLimits_Click(object sender, RoutedEventArgs e)
        {
            EMPlateResultItem missing = plateItems.FirstOrDefault(p => !p.AlertLimit.HasValue || !p.ActionLimit.HasValue);
            OpenApprovedLimitsManager(missing?.Grade ?? lblGrade.Text, missing?.Method);
        }

        private void BtnReconcileLegacySnapshot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (currentEventId <= 0)
                    throw new InvalidOperationException("Load the historical EM event first.");
                if (!DatabaseHelper.CanApproveResults(Login.CurrentUser ?? string.Empty))
                    throw new UnauthorizedAccessException("QA approval permission is required to reconcile historical EM limit evidence.");

                var dialog = new EMLegacySnapshotReconciliation(currentEventId, currentEventNo) { Owner = this };
                if (dialog.ShowDialog() == true || dialog.WasSaved)
                {
                    LoadPlates(currentEventId);
                    CalculateAllStatuses();
                    RefreshGrid();
                    RefreshCounters();
                    UpdateWorkflowButtons();
                    lblStatus.Text = "Signed historical EM snapshot reconciliation loaded. Review the evidence before continuing workflow.";
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to reconcile historical EM limit evidence.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Historical EM snapshot reconciliation"), "EM Reconciliation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenApprovedLimitsManager(string grade, string method)
        {
            if (!DatabaseHelper.CanManageSettings(Login.CurrentUser ?? ""))
            {
                MessageBox.Show(
                    "Your account does not have Settings permission to manage controlled EM limits.",
                    "Permission Denied",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var dialog = new EMLimitsManagement(grade, method) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.WasSaved && currentEventId > 0)
            {
                LoadPlates(currentEventId);
                CalculateAllStatuses();
                RefreshGrid();
                RefreshCounters();
                UpdateWorkflowButtons();
                lblStatus.Text = "Approved EM limits reloaded. Review the calculated status and save results again before submission.";
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
            }
        }

        private void BtnCalculate_Click(object sender, RoutedEventArgs e)
        {
            if (!DatabaseHelper.CanEditResults(Login.CurrentUser ?? ""))
            {
                MessageBox.Show("You don't have permission to calculate results.", "Permission Denied",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            CalculateAllStatuses();
            RefreshGrid();
            RefreshCounters();
            lblStatus.Text = "Statuses calculated.";
        }

        private async void BtnSaveResults_Click(object sender, RoutedEventArgs e)
        {
            if (isSaving) return;

            if (!DatabaseHelper.CanEditResults(Login.CurrentUser ?? ""))
            {
                MessageBox.Show("You don't have permission to save results.", "Permission Denied",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!EnsureControlledEmPlanningProvenance("Result Entry"))
                return;

            await SaveResultsAsync();
        }

        private async void BtnSubmitReview_Click(object sender, RoutedEventArgs e)
        {
            if (!DatabaseHelper.CanSubmitForReview(Login.CurrentUser ?? ""))
            {
                MessageBox.Show("You don't have permission to submit EM results for review.", "Permission Denied",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!EnsureControlledEmPlanningProvenance("Submit for Review"))
                return;

            if (plateItems.Any(p => !p.AlertLimit.HasValue || !p.ActionLimit.HasValue ||
                                    p.Status.Equals("Not Assessed", StringComparison.OrdinalIgnoreCase) ||
                                    p.Status.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)))
            {
                EMPlateResultItem missing = plateItems.FirstOrDefault(p =>
                    !p.AlertLimit.HasValue || !p.ActionLimit.HasValue ||
                    p.Status.Equals("Not Assessed", StringComparison.OrdinalIgnoreCase) ||
                    p.Status.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase));

                bool canManageLimits = currentCanManageSettings;
                string methodDetail = missing == null ? "" :
                    $"\n\nMissing configuration: {missing.Grade} / {missing.Method}.";

                if (canManageLimits)
                {
                    MessageBoxResult answer = MessageBox.Show(
                        "Submission is blocked because one or more monitoring methods do not have approved Alert and Action limits." +
                        methodDetail +
                        "\n\nOnly site-approved limits may be entered. Open Approved EM Limits setup now?",
                        "Approved Limits Required",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (answer == MessageBoxResult.Yes)
                        OpenApprovedLimitsManager(missing?.Grade, missing?.Method);
                }
                else
                {
                    MessageBox.Show(
                        "Submission is blocked because one or more monitoring methods do not have approved Alert and Action limits." +
                        methodDetail +
                        "\n\nAsk an authorized Settings user to record the site-approved values in Approved EM Limits.",
                        "Approved Limits Required",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;
            }

            bool negativeControlApplies = currentPlanId > 0 || !string.IsNullOrWhiteSpace(currentNegativeControlResult);
            if (negativeControlApplies &&
                !string.Equals(currentNegativeControlResult, "No Growth", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Submission is blocked until the incubated negative control is read as No Growth. Growth requires a linked investigation and invalidates the monitoring run.",
                    "Negative Control Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!AreAllPlateResultsEntered())
            {
                MessageBox.Show(
                    "All plate results must be entered before submitting for review. Entered: " + GetPlateCompletionText() + ".",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            await PerformEMWorkflowActionAsync(
                "Submit for Review",
                "EM Submit for Review",
                (meaning, reason) => DatabaseHelper.SubmitEMEventForReview(currentEventId, currentEventNo, currentUser, meaning, reason));
        }

        private async void BtnReview_Click(object sender, RoutedEventArgs e)
        {
            if (!DatabaseHelper.CanReviewResults(Login.CurrentUser ?? ""))
            {
                MessageBox.Show("You don't have permission to review EM results.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!EnsureControlledEmPlanningProvenance("Review"))
                return;

            if (!IsAdminWorkflowOverrideAllowed() && HasCurrentUserSignedAction("EM Result Entry"))
            {
                MessageBox.Show("The same user cannot review their own EM result entry.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await PerformEMWorkflowActionAsync(
                "Review",
                "EM Review",
                (meaning, reason) => DatabaseHelper.ReviewEMEvent(currentEventId, currentEventNo, currentUser, meaning, reason));
        }

        private async void BtnApprove_Click(object sender, RoutedEventArgs e)
        {
            if (!DatabaseHelper.CanApproveResults(Login.CurrentUser ?? ""))
            {
                MessageBox.Show("You don't have permission to approve EM results.", "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!EnsureControlledEmPlanningProvenance("Approval"))
                return;

            if (!IsAdminWorkflowOverrideAllowed() && (HasCurrentUserSignedAction("EM Result Entry") || HasCurrentUserSignedAction("EM Review")))
            {
                MessageBox.Show("The same user cannot approve EM results that they entered or reviewed.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (HasOosResults() && !IsCurrentQualityEventClosed())
            {
                MessageBox.Show("This EM event contains OOS/ACTION result(s). Approval is blocked until a linked Quality Event investigation is closed with QA disposition.", "Investigation Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await PerformEMWorkflowActionAsync(
                "Approve",
                "EM Approval",
                (meaning, reason) => DatabaseHelper.ApproveEMEvent(currentEventId, currentEventNo, currentUser, meaning, reason));
        }

        private bool EnsureControlledEmPlanningProvenance(string actionName)
        {
            if (!AppConfig.IsProduction || currentPlanId > 0)
                return true;

            MessageBox.Show(
                actionName + " is blocked for this direct / legacy EM event in Production. " +
                "Production EM results must originate from EM Planning after controlled collection, negative control, " +
                "two-phase incubation completion, and signed release to Results Entry. Historical records remain viewable and reportable.",
                "Controlled EM Workflow Required",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        private async Task PerformEMWorkflowActionAsync(string actionName, string signatureAction, Action<string, string> action)
        {
            if (isWorkflowActionInProgress)
                return;

            if (currentEventId <= 0)
            {
                MessageBox.Show("No EM event is loaded.", actionName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var signatureWindow = _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(currentEventNo, currentUser, signatureAction, true);
            signatureWindow.Owner = this;
            signatureWindow.ShowInTaskbar = false;
            signatureWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            signatureWindow.Loaded += (_, _) =>
            {
                signatureWindow.Topmost = true;
                signatureWindow.Activate();
                signatureWindow.Focus();
                signatureWindow.Topmost = false;
            };

            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            isWorkflowActionInProgress = true;
            SetWorkflowBusyState(true, actionName + " in progress...");

            try
            {
                string meaning = signatureWindow.Meaning;
                string reason = signatureWindow.Reason;
                int eventId = currentEventId;

                await Task.Run(() => action(meaning, reason));
                workflowStatus = await Task.Run(() => DatabaseHelper.GetEMWorkflowStatus(eventId));

                SetFinalResultStatus(currentFinalResult);
                CheckLinkedDeviation();
                UpdateWorkflowButtons();

                lblStatus.Text = "EM event workflow updated: " + workflowStatus;
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));

                MessageBox.Show(
                    "EM workflow action completed successfully.\n\nCurrent Status: " + workflowStatus,
                    actionName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Unable to complete workflow action: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    actionName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                lblStatus.Text = "Workflow action failed: " + Infrastructure.UserFacingError.SafeMessage(ex);
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
            finally
            {
                isWorkflowActionInProgress = false;
                SetWorkflowBusyState(false, null);
                UpdateWorkflowButtons();
            }
        }

        private void SetWorkflowBusyState(bool isBusy, string statusText)
        {
            Mouse.OverrideCursor = isBusy ? Cursors.Wait : null;

            if (BtnSubmitReview != null) BtnSubmitReview.IsEnabled = !isBusy;
            if (BtnReview != null) BtnReview.IsEnabled = !isBusy;
            if (BtnApprove != null) BtnApprove.IsEnabled = !isBusy;
            if (BtnSaveResults != null) BtnSaveResults.IsEnabled = !isBusy;
            if (BtnCalculate != null) BtnCalculate.IsEnabled = !isBusy;
            if (BtnManageLimits != null) BtnManageLimits.IsEnabled = !isBusy && currentCanManageSettings;
            if (BtnReconcileLegacySnapshot != null) BtnReconcileLegacySnapshot.IsEnabled = !isBusy && currentCanApproveResults;
            if (BtnPrintReport != null) BtnPrintReport.IsEnabled = !isBusy;

            if (isBusy && !string.IsNullOrWhiteSpace(statusText))
            {
                lblStatus.Text = statusText;
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
            }
        }

        private void BtnPrintReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (currentEventId <= 0)
                {
                    MessageBox.Show("No EM event is loaded.", "Print Report", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                CalculateAllStatuses();
                RefreshGrid();
                RefreshCounters();

                string validationMessage;
                if (!CanPrintApprovedEMReport(out validationMessage))
                {
                    MessageBox.Show(validationMessage, "Print Report", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                FlowDocument document = BuildEMResultReportDocument();
                ShowEMReportPreview(document);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error printing EM result report: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Print Report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                lblStatus.Text = "Error printing report: " + Infrastructure.UserFacingError.SafeMessage(ex);
                lblStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
            }
        }
        private void ShowEMReportPreview(FlowDocument document)
        {
            document.PageWidth = 793;
            document.PageHeight = 1122;
            document.PagePadding = new Thickness(42, 38, 42, 48);
            document.ColumnWidth = double.PositiveInfinity;

            var viewer = new FlowDocumentPageViewer
            {
                Document = document,
                Zoom = 92,
                Margin = new Thickness(12),

            };

            var printButton = new Button
            {
                Content = "Print",
                Width = 125,
                Height = 38,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F5F8F")),
                Foreground = Brushes.White
            };
            var closeButton = new Button
            {
                Content = "Close",
                Width = 110,
                Height = 38,
                FontWeight = FontWeights.SemiBold
            };
            var commands = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(14, 8, 14, 14)
            };
            commands.Children.Add(printButton);
            commands.Children.Add(closeButton);

            var root = new DockPanel { Background = Brushes.White };
            DockPanel.SetDock(commands, Dock.Bottom);
            root.Children.Add(commands);
            root.Children.Add(viewer);

            var previewWindow = new Window
            {
                Title = "Environmental Monitoring Report Preview",
                Owner = this,
                Width = 1180,
                Height = 860,
                MinWidth = 900,
                MinHeight = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };

            printButton.Click += (_, _) =>
            {
                var printDialog = new PrintDialog();

                if (printDialog.ShowDialog() != true)
                    return;

                try
                {
                    // Create a separate document for printing.
                    // Do not reuse the document currently attached to the preview viewer.
                    FlowDocument printDocument = BuildEMResultReportDocument();

                    printDocument.PageWidth = printDialog.PrintableAreaWidth;
                    printDocument.PageHeight = printDialog.PrintableAreaHeight;
                    printDocument.PagePadding = new Thickness(42, 38, 42, 48);
                    printDocument.ColumnWidth = double.PositiveInfinity;

                    IDocumentPaginatorSource paginatorSource =
                        CreateFramedPaginatorSource(printDocument);

                    printDialog.PrintDocument(
                        paginatorSource.DocumentPaginator,
                        "Environmental Monitoring Result Report - " + currentEventNo);

                    DatabaseHelper.AddEMEventSignature(
                        currentEventId,
                        currentEventNo,
                        "EM Report Print",
                        currentUser,
                        "Printed approved EM Result Report",
                        "Approved Environmental Monitoring Result Report printed.");

                    DatabaseHelper.AddAuditTrailAdvanced(
                        "EM_Events",
                        currentEventId,
                        "Print EM Result Report",
                        "",
                        currentEventNo,
                        "Approved Environmental Monitoring result report printed",
                        currentUser);

                    reportPrintedThisSession = true;
                    UpdateWorkflowProgress();

                    lblStatus.Text = "EM result report printed.";
                    lblStatus.Foreground =
                        new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#10B981"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Unable to print the Environmental Monitoring report.\n\n" +
                        Infrastructure.UserFacingError.SafeMessage(ex),
                        "Print Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            };
            closeButton.Click += (_, _) => previewWindow.Close();
            previewWindow.ShowDialog();
        }

        private IDocumentPaginatorSource CreateFramedPaginatorSource(FlowDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            IDocumentPaginatorSource source = document;
            source.DocumentPaginator.ComputePageCount();
            return new PaginatorSource(
                new FramedDocumentPaginator(source.DocumentPaginator, reportVerificationReference));
        }

        private FlowDocument BuildEMResultReportDocument()
        {
            reportGeneratedAt = DatabaseHelper.GetAuthoritativeDatabaseTime();
            reportPrintedByDisplay = GetUserDisplayName(currentUser);
            reportVerificationReference = FirstNonEmpty(currentEventNo) + "-EMRR-" +
                                          reportGeneratedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            FlowDocument document = new FlowDocument
            {
                PageWidth = 793,
                PageHeight = 1122,
                PagePadding = new Thickness(42, 38, 42, 48),
                FontFamily = new FontFamily("Arial"),
                FontSize = 9,
                ColumnWidth = double.PositiveInfinity
            };

            AddEMReportHeader(document);

            AddReportSectionTitle(document, "1. Event Information");
            document.Blocks.Add(MakeFourColumnTable(
                "EM Event No", FirstNonEmpty(lblEventNo.Text),
                "EM Plan No", ReportValueOrNA(currentPlanNo),
                "Event Date", FirstNonEmpty(lblEventDate.Text),
                "Area", FirstNonEmpty(lblArea.Text),
                "Class / Grade", FirstNonEmpty(lblGrade.Text),
                "Final Result", GetEMReportFinalResultDisplay(),
                "Record Type", "Environmental Monitoring Result Report"));

            AddReportSectionTitle(document, "2. Sampling Information");
            List<string> samplingValues = new List<string>
            {
                "Activity / Condition", ReportValueOrNA(currentActivityNoOfPersons)
            };

            if (string.IsNullOrWhiteSpace(currentSamplingTimeTo))
            {
                samplingValues.Add("Sampling Time");
                samplingValues.Add(FormatTimeForReport(currentSamplingTimeFrom));
            }
            else
            {
                samplingValues.Add("Exposure Start Time");
                samplingValues.Add(FormatTimeForReport(currentSamplingTimeFrom));
                samplingValues.Add("Exposure End Time");
                samplingValues.Add(FormatTimeForReport(currentSamplingTimeTo));
            }

            if (HasActiveAirPlates())
            {
                samplingValues.Add("Air Sampler No.");
                samplingValues.Add(ReportValueOrNA(currentAirSamplerNo));
                samplingValues.Add("Air Volume");
                samplingValues.Add(GetReportAirVolumeSummary());
            }

            samplingValues.AddRange(new[]
            {
                "Sanitization Details", ReportValueOrNA(currentSanitizationDetails),
                "Sanitization Time", FormatTimeForReport(currentSanitizationTime)
            });

            if (!string.IsNullOrWhiteSpace(currentDispensingBooth))
            {
                samplingValues.Add("Dispensing Booth");
                samplingValues.Add(currentDispensingBooth);
            }

            if (!string.IsNullOrWhiteSpace(currentMaterialName) || !string.IsNullOrWhiteSpace(currentBatchNo))
            {
                samplingValues.Add("Material / Product");
                samplingValues.Add(ReportValueOrNA(currentMaterialName));
                samplingValues.Add("Batch No.");
                samplingValues.Add(ReportValueOrNA(currentBatchNo));
            }

            if (!string.IsNullOrWhiteSpace(currentEmployeeId) || !string.IsNullOrWhiteSpace(currentEmployeeName))
            {
                samplingValues.Add("Employee");
                samplingValues.Add((currentEmployeeId + " - " + currentEmployeeName).Trim(' ', '-'));
                samplingValues.Add("Department / Shift");
                samplingValues.Add((currentEmployeeDepartment + " / " + currentEmployeeShift).Trim(' ', '/'));
                samplingValues.Add("Sampling Stage");
                samplingValues.Add(ReportValueOrNA(currentSamplingStage));
            }

            if (!string.IsNullOrWhiteSpace(currentSurfaceLocation))
            {
                samplingValues.Add("Surface / Sample Site");
                samplingValues.Add(currentSurfaceLocation);
                samplingValues.Add("Surface Type");
                samplingValues.Add(ReportValueOrNA(currentSurfaceType));
                samplingValues.Add("Surface Area");
                samplingValues.Add(string.IsNullOrWhiteSpace(currentSurfaceAreaCm2) ? "N/A" : currentSurfaceAreaCm2 + " cm²");
            }

            if (!string.IsNullOrWhiteSpace(currentSwabKitLot) || !string.IsNullOrWhiteSpace(currentDiluentLot))
            {
                samplingValues.Add("Swab Kit Lot");
                samplingValues.Add(ReportValueOrNA(currentSwabKitLot));
                samplingValues.Add("Diluent / Recovery Volume");
                samplingValues.Add(ReportValueOrNA(currentDiluentLot) + " / " +
                    (string.IsNullOrWhiteSpace(currentRecoveryVolumeMl) ? "N/A" : currentRecoveryVolumeMl + " mL"));
            }

            document.Blocks.Add(MakeFourColumnTable(samplingValues.ToArray()));

            AddReportSectionTitle(document, "3. Media and Incubation");
            document.Blocks.Add(MakeFourColumnTable(
                "Media Used", ReportValueOrNA(lblMediaUsed.Text),
                "Media Lot / Preparation Ref.", ReportValueOrNA(lblMediaLotNo.Text),
                "Incubation Temperature", FormatTemperatureForReport(currentIncubationTemperature),
                "Bacteria Incubator ID", ReportValueOrNA(currentIncubatorNo1),
                "Fungi Incubator ID", ReportValueOrNA(currentIncubatorNo2),
                "Incubation Start", ReportValueOrNA(currentIncubationStart),
                "Incubation End", ReportValueOrNA(currentIncubationEnd),
                "Negative Control", ReportValueOrNA(currentNegativeControlResult),
                "Remarks", ReportValueOrNA(currentRemarks)));

            AddReportSectionTitle(document, "4. Result Summary");
            document.Blocks.Add(MakeSummaryDashboardTable());

            AddReportSectionTitle(document, "5. Plate Results");
            AddEMPlateResultsTable(document);

            AddReportSectionTitle(document, "6. Result Evaluation");
            document.Blocks.Add(BuildResultEvaluationBox());

            AddReportSectionTitle(document, "7. Final GMP Conclusion");
            document.Blocks.Add(MakeConclusionBox(BuildEMFinalConclusion()));

            if (currentQualityEventId > 0)
            {
                AddReportSectionTitle(document, "8. Quality Event / Investigation");
                document.Blocks.Add(MakeTwoColumnTable(
                    "Quality Event No.", FirstNonEmpty(currentQualityEventNo, currentQualityEventId.ToString(CultureInfo.InvariantCulture)),
                    "Investigation Status", ReportValueOrNA(currentQualityEventStatus),
                    "Root Cause", ReportValueOrNA(currentQualityEventRootCause),
                    "CAPA", ReportValueOrNA(currentQualityEventCapa),
                    "QA Disposition", ReportValueOrNA(currentQualityEventQaDisposition),
                    "Closed By", ReportValueOrNA(currentQualityEventClosedBy),
                    "Closed Date", ReportValueOrNA(currentQualityEventClosedDate),
                    "Linked EM Event", FirstNonEmpty(currentEventNo)));
            }
            else if (HasOosResults())
            {
                AddReportSectionTitle(document, "8. Quality Event / Investigation");
                document.Blocks.Add(MakeTwoColumnTable(
                    "Investigation Required", "Yes",
                    "Quality Event No.", "Not linked",
                    "Investigation Status", "Pending / Required before final approval",
                    "QA Disposition", "Not available"));
            }

            int electronicSignatureSectionNo = currentQualityEventId > 0 || HasOosResults() ? 9 : 8;
            int electronicRecordSectionNo = electronicSignatureSectionNo + 1;

            AddReportSectionTitle(document, electronicSignatureSectionNo.ToString(CultureInfo.InvariantCulture) + ". Electronic Signatures");
            AddEMSignatureSection(document);

            AddReportSectionTitle(document, electronicRecordSectionNo.ToString(CultureInfo.InvariantCulture) + ". Electronic Record Summary");
            document.Blocks.Add(MakeFourColumnTable(
                "Printed By", FirstNonEmpty(reportPrintedByDisplay, currentUser),
                "Printed At", reportGeneratedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                "Software", "PharmaLIMS",
                "Document Revision", "05",
                "Signature Control", "Electronic signature and audit trail required",
                "Report Status", "Controlled electronic record",
                "Verification Reference", reportVerificationReference,
                "Record Classification", "Environmental Monitoring Report"));

            AddEMReportFooter(document);
            return document;
        }

        private void AddEMReportHeader(FlowDocument document)
        {
            Brush navy = CreateReportBrush("#0F4C81");
            Brush darkNavy = CreateReportBrush("#0B3558");

            Table headerTable = new Table { CellSpacing = 0 };
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(135) });
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(385) });
            headerTable.Columns.Add(new TableColumn { Width = new GridLength(180) });

            TableRowGroup group = new TableRowGroup();
            headerTable.RowGroups.Add(group);
            TableRow row = new TableRow();

            TableCell logoCell = new TableCell
            {
                Background = Brushes.White,
                BorderBrush = CreateReportBrush("#CBD5E1"),
                BorderThickness = new Thickness(0.7),
                Padding = new Thickness(5)
            };

            Image logo = TryCreateReportLogo(122, 58);
            if (logo != null)
            {
                BlockUIContainer logoBlock = new BlockUIContainer(logo) { TextAlignment = TextAlignment.Center };
                logoCell.Blocks.Add(logoBlock);
            }
            else
            {
                Paragraph fallbackLogo = new Paragraph(new Run("MEDICA"))
                {
                    TextAlignment = TextAlignment.Center,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = navy,
                    Margin = new Thickness(0)
                };
                logoCell.Blocks.Add(fallbackLogo);
            }
            row.Cells.Add(logoCell);

            TableCell companyCell = new TableCell
            {
                Background = navy,
                Padding = new Thickness(10, 11, 10, 9),
                BorderBrush = navy,
                BorderThickness = new Thickness(0.7)
            };

            companyCell.Blocks.Add(new Paragraph(new Run("MEDICA PHARMACEUTICAL INDUSTRY"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });
            companyCell.Blocks.Add(new Paragraph(new Run("Microbiology Department"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Margin = new Thickness(0)
            });
            row.Cells.Add(companyCell);

            string controlText =
                "Form No: MQC-F-EM-001\n" +
                "Procedure Ref: MQC-I-0018\n" +
                "Version: 01 | Design Rev: 05\n" +
                "Issue Date: " + reportGeneratedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            TableCell infoCell = MakeReportCell(controlText, false, 8.2, TextAlignment.Right, navy, Brushes.White);
            infoCell.Padding = new Thickness(8, 8, 8, 7);
            infoCell.BorderBrush = navy;
            row.Cells.Add(infoCell);

            group.Rows.Add(row);
            document.Blocks.Add(headerTable);

            Paragraph title = new Paragraph(new Run("ENVIRONMENTAL MONITORING RESULT REPORT"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 7, 0, 0),
                Padding = new Thickness(8, 7, 8, 7),
                Background = darkNavy,
                Foreground = Brushes.White,
                BorderBrush = darkNavy,
                BorderThickness = new Thickness(1)
            };
            document.Blocks.Add(title);

            Paragraph subtitle = new Paragraph(new Run("Controlled Environmental Monitoring Record - Not a Certificate of Analysis"))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateReportBrush("#475569"),
                Margin = new Thickness(0, 3, 0, 7)
            };
            document.Blocks.Add(subtitle);

            Table meta = new Table { CellSpacing = 0 };
            meta.Columns.Add(new TableColumn { Width = new GridLength(130) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(220) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(130) });
            meta.Columns.Add(new TableColumn { Width = new GridLength(220) });
            TableRowGroup metaGroup = new TableRowGroup();
            meta.RowGroups.Add(metaGroup);

            string eventReference = FirstNonEmpty(currentEventNo);
            string reportNo = !string.IsNullOrWhiteSpace(eventReference)
                ? "EMRR-" + eventReference
                : "EMRR-EVENT-" + (currentEventId <= 0 ? "0000" : currentEventId.ToString("0000", CultureInfo.InvariantCulture));

            TableRow metaRow1 = new TableRow();
            metaRow1.Cells.Add(MakeReportCell("Report No", true, 8.6, TextAlignment.Left, CreateReportBrush("#D9EAF7"), darkNavy));
            metaRow1.Cells.Add(MakeReportCell(reportNo, true, 8.6));
            metaRow1.Cells.Add(MakeReportCell("EM Event No", true, 8.6, TextAlignment.Left, CreateReportBrush("#D9EAF7"), darkNavy));
            metaRow1.Cells.Add(MakeReportCell(FirstNonEmpty(currentEventNo), true, 8.6));
            metaGroup.Rows.Add(metaRow1);

            TableRow metaRow2 = new TableRow();
            metaRow2.Cells.Add(MakeReportCell("Printed Date", true, 8.6, TextAlignment.Left, CreateReportBrush("#D9EAF7"), darkNavy));
            metaRow2.Cells.Add(MakeReportCell(reportGeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), false, 8.6));
            metaRow2.Cells.Add(MakeReportCell("Printed By", true, 8.6, TextAlignment.Left, CreateReportBrush("#D9EAF7"), darkNavy));
            metaRow2.Cells.Add(MakeReportCell(FirstNonEmpty(reportPrintedByDisplay, currentUser), false, 8.6));
            metaGroup.Rows.Add(metaRow2);

            if (currentQualityEventId > 0)
            {
                TableRow metaRow3 = new TableRow();
                metaRow3.Cells.Add(MakeReportCell("Investigation Ref.", true, 8.6, TextAlignment.Left, CreateReportBrush("#D9EAF7"), darkNavy));
                metaRow3.Cells.Add(MakeReportCell(FirstNonEmpty(currentQualityEventNo, currentQualityEventId.ToString(CultureInfo.InvariantCulture)), false, 8.6));
                metaRow3.Cells.Add(MakeReportCell("Investigation Status", true, 8.6, TextAlignment.Left, CreateReportBrush("#D9EAF7"), darkNavy));
                metaRow3.Cells.Add(MakeReportCell(ReportValueOrNA(currentQualityEventStatus), false, 8.6));
                metaGroup.Rows.Add(metaRow3);
            }

            document.Blocks.Add(meta);
        }

        private Image TryCreateReportLogo(double width, double height)
        {
            try
            {
                string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string localLogo = Path.Combine(baseDirectory, "medica-logo.png");

                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();

                if (File.Exists(localLogo))
                    bitmap.UriSource = new Uri(localLogo, UriKind.Absolute);
                else
                    bitmap.UriSource = new Uri("pack://siteoforigin:,,,/medica-logo.png", UriKind.Absolute);

                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                Image image = new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                return image;
            }
            catch
            {
                return null;
            }
        }

        private static string GetUserDisplayName(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return string.Empty;

            try
            {
                object value = DatabaseHelper.ExecuteScalar(
                    "SELECT COALESCE(NULLIF(LTRIM(RTRIM(FullName)),N''),Username) FROM dbo.Users WHERE Username=@Username;",
                    new[] { new SqlParameter("@Username", SqlDbType.NVarChar, 100) { Value = username.Trim() } });
                string displayName = value == null || value == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                return string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim();
            }
            catch
            {
                return username.Trim();
            }
        }

        private bool HasAnyPlateStatus(string status)
        {
            if (plateItems == null || plateItems.Count == 0)
                return false;

            foreach (EMPlateResultItem item in plateItems)
            {
                if ((item.Status ?? "").Trim().Equals(status, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private string GetEMReportFinalResultDisplay()
        {
            string workflow = FirstNonEmpty(workflowStatus);
            string result = plateItems != null && plateItems.Count > 0
                ? CalculateFinalResult()
                : FirstNonEmpty(currentFinalResult, lblFinalResult.Text);

            if (HasAnyPlateStatus("OOS") || result.Equals("OOS", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCurrentQualityEventClosed() && workflow.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                    return "ACTION / OOS - QA Disposition Completed";

                return "ACTION / OOS - Investigation Required";
            }

            if (HasAnyPlateStatus("Alert") || result.Equals("Alert", StringComparison.OrdinalIgnoreCase))
            {
                if (workflow.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                    return "Approved with Alert";

                return "Alert";
            }

            if (workflow.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                return "Approved";

            if (result.Equals("Results Entered", StringComparison.OrdinalIgnoreCase) ||
                result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return "Within Limits";

            return result;
        }

        private string BuildEMFinalConclusion()
        {
            string workflow = FirstNonEmpty(workflowStatus);
            string result = plateItems != null && plateItems.Count > 0
                ? CalculateFinalResult()
                : FirstNonEmpty(currentFinalResult, lblFinalResult.Text);

            if (HasAnyPlateStatus("OOS") || result.Equals("OOS", StringComparison.OrdinalIgnoreCase))
            {
                if (IsCurrentQualityEventClosed() && workflow.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                    return "Final Conclusion: One or more results exceeded the action limit and remain classified as an ACTION / OOS excursion. The related Quality Event investigation has been completed and the QA disposition required by the controlled workflow has been recorded. Workflow approval documents review and disposition of the event; it does not convert the excursion into a within-limit result.";

                return "Final Conclusion: One or more results exceeded the action limit. This EM event requires Quality Event investigation and QA disposition before final approval.";
            }

            if (HasAnyPlateStatus("Alert") || result.Equals("Alert", StringComparison.OrdinalIgnoreCase))
            {
                if (workflow.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                    return "Final Conclusion: One or more Environmental Monitoring result(s) exceeded the alert level. The event was reviewed and approved with follow-up/trending as applicable according to the approved procedure.";

                return "Final Conclusion: One or more Environmental Monitoring result(s) exceeded the alert level. Trend review and follow-up are required according to the approved procedure.";
            }

            if (workflow.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                return "Final Conclusion: All documented Environmental Monitoring result(s) are within the applicable limits and the event has been reviewed and approved.";

            if (result.Equals("Results Entered", StringComparison.OrdinalIgnoreCase) ||
                result.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return "Final Conclusion: The documented Environmental Monitoring result(s) are within the applicable limits based on the available electronic record.";

            return "Final Conclusion: The Environmental Monitoring result is " + result + " and workflow status is " + workflow + ".";
        }

        private void AddEMSignatureSection(FlowDocument document)
        {
            Table table = new Table { CellSpacing = 7 };
            table.Columns.Add(new TableColumn { Width = new GridLength(172) });
            table.Columns.Add(new TableColumn { Width = new GridLength(172) });
            table.Columns.Add(new TableColumn { Width = new GridLength(172) });
            table.Columns.Add(new TableColumn { Width = new GridLength(172) });

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            DataTable signatures = currentEventId > 0
                ? DatabaseHelper.GetEMEventSignatures(currentEventId)
                : new DataTable();

            DataRow resultEntry = FindSignature(signatures, "EM Result Entry");
            DataRow review = FindSignature(signatures, "EM Review");
            DataRow approval = FindSignature(signatures, "EM Approval");
            DataRow print = FindSignature(signatures, "EM Report Print");

            string printedDate = GetSignatureDate(print);
            if (string.IsNullOrWhiteSpace(printedDate))
                printedDate = reportGeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            TableRow row = new TableRow();
            row.Cells.Add(MakeSignatureBox(
                "Entered By", "Result Entry",
                GetSignatureValue(resultEntry, "SignerDisplayName", GetSignatureValue(resultEntry, "SignedBy", "Not signed")),
                GetSignatureDate(resultEntry),
                GetSignatureValue(resultEntry, "MeaningOfSignature", "Electronic result entry")));

            row.Cells.Add(MakeSignatureBox(
                "Reviewed By", "Review",
                GetSignatureValue(review, "SignerDisplayName", GetSignatureValue(review, "SignedBy", "Not reviewed")),
                GetSignatureDate(review),
                GetSignatureValue(review, "MeaningOfSignature", "Independent data review")));

            row.Cells.Add(MakeSignatureBox(
                "Approved By", "Approval",
                GetSignatureValue(approval, "SignerDisplayName", GetSignatureValue(approval, "SignedBy", "Not approved")),
                GetSignatureDate(approval),
                GetSignatureValue(approval, "MeaningOfSignature", "Final approval")));

            row.Cells.Add(MakeSignatureBox(
                "Printed By", "Report Print",
                GetSignatureValue(print, "SignerDisplayName", GetSignatureValue(print, "SignedBy", FirstNonEmpty(reportPrintedByDisplay, currentUser))),
                printedDate,
                GetSignatureValue(print, "MeaningOfSignature", "Printed from controlled PharmaLIMS records")));

            group.Rows.Add(row);
            document.Blocks.Add(table);
        }

        private DataRow FindSignature(DataTable signatures, string actionType)
        {
            if (signatures == null || signatures.Rows.Count == 0)
                return null;

            foreach (DataRow row in signatures.Rows)
            {
                string currentAction = row.Table.Columns.Contains("ActionType")
                    ? row.GetSafeString("ActionType")
                    : "";

                if (currentAction.Equals(actionType, StringComparison.OrdinalIgnoreCase))
                    return row;
            }

            return null;
        }

        private string GetSignatureValue(DataRow row, string columnName, string fallback)
        {
            if (row == null)
                return fallback;

            string value = row.GetSafeString(columnName);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private string GetSignatureDate(DataRow row)
        {
            if (row == null || !row.Table.Columns.Contains("SignedAt") || row["SignedAt"] == DBNull.Value)
                return "";

            if (DateTime.TryParse(row["SignedAt"].ToString(), out DateTime signedAt))
                return signedAt.ToString("yyyy-MM-dd HH:mm");

            return row["SignedAt"].ToString();
        }

        private TableCell MakeSignatureBox(string title, string step, string signedBy, string signedAt, string meaning)
        {
            TableCell cell = new TableCell();
            cell.BorderBrush = CreateReportBrush("#94A3B8");
            cell.BorderThickness = new Thickness(0.8);
            cell.Padding = new Thickness(0);
            cell.Background = Brushes.White;

            Paragraph titleParagraph = new Paragraph(new Run(title ?? ""));
            titleParagraph.TextAlignment = TextAlignment.Center;
            titleParagraph.FontSize = 9.2;
            titleParagraph.FontWeight = FontWeights.Bold;
            titleParagraph.Foreground = Brushes.White;
            titleParagraph.Background = CreateReportBrush("#0F4C81");
            titleParagraph.Padding = new Thickness(5, 4, 5, 4);
            titleParagraph.Margin = new Thickness(0);
            cell.Blocks.Add(titleParagraph);

            Paragraph userParagraph = new Paragraph();
            userParagraph.Inlines.Add(new Run("Name: ") { FontWeight = FontWeights.Bold });
            userParagraph.Inlines.Add(new Run(signedBy ?? ""));
            userParagraph.FontSize = 8.8;
            userParagraph.Margin = new Thickness(7, 6, 7, 2);
            cell.Blocks.Add(userParagraph);

            Paragraph dateParagraph = new Paragraph();
            dateParagraph.Inlines.Add(new Run("Date: ") { FontWeight = FontWeights.Bold });
            dateParagraph.Inlines.Add(new Run(signedAt ?? ""));
            dateParagraph.FontSize = 8.5;
            dateParagraph.Margin = new Thickness(7, 0, 7, 2);
            cell.Blocks.Add(dateParagraph);

            Paragraph stepParagraph = new Paragraph();
            stepParagraph.Inlines.Add(new Run("Action: ") { FontWeight = FontWeights.Bold });
            stepParagraph.Inlines.Add(new Run(step ?? ""));
            stepParagraph.FontSize = 8.5;
            stepParagraph.Margin = new Thickness(7, 0, 7, 2);
            cell.Blocks.Add(stepParagraph);

            Paragraph meaningParagraph = new Paragraph(new Run(meaning ?? ""));
            meaningParagraph.FontSize = 8.0;
            meaningParagraph.Foreground = CreateReportBrush("#475569");
            meaningParagraph.Margin = new Thickness(7, 2, 7, 6);
            meaningParagraph.TextAlignment = TextAlignment.Left;
            cell.Blocks.Add(meaningParagraph);

            return cell;
        }

        private void AddEMReportFooter(FlowDocument document)
        {
            Paragraph separator = new Paragraph(new Run(""))
            {
                BorderBrush = CreateReportBrush("#94A3B8"),
                BorderThickness = new Thickness(0, 0.7, 0, 0),
                Margin = new Thickness(0, 11, 0, 4)
            };
            document.Blocks.Add(separator);

            Paragraph footer = new Paragraph
            {
                FontSize = 7.8,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateReportBrush("#64748B"),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0)
            };
            footer.Inlines.Add(new Run("CONTROLLED ELECTRONIC RECORD | Generated by PharmaLIMS | Electronic signatures and audit trail required\n"));
            footer.Inlines.Add(new Run("This report summarizes Environmental Monitoring records and is not a Certificate of Analysis."));
            document.Blocks.Add(footer);
        }

        private bool HasActiveAirPlates()
        {
            return plateItems != null && plateItems.Any(item => IsActiveAirSampling(item.Method));
        }

        private bool HasSettlePlateResults()
        {
            return plateItems != null && plateItems.Any(item => IsSettlePlate(item.Method));
        }

        private string BuildResultEvaluationText()
        {
            bool hasSettle = HasSettlePlateResults();
            bool hasActive = HasActiveAirPlates();

            if (hasSettle && !hasActive)
                return "Settle Plate results are evaluated as direct CFU/plate counts. Results are interpreted against the recorded approved alert and action limits used for this report. This document is an EM result report and is not a Certificate of Analysis.";

            if (hasActive && !hasSettle)
                return "Active Air Sampling results are expressed as CFU/m3. The sampled air volume is recorded in liters and the result is normalized to one cubic meter using: CFU/m3 = Total Colony Count / Sampled Air Volume (L) x 1000. The factor 1000 is only a unit conversion factor from liters to one cubic meter and does not represent the sampled air volume. Results are interpreted against the recorded approved alert and action limits used for this report. This document is an EM result report and is not a Certificate of Analysis.";

            return "Settle Plate results are evaluated as direct CFU/plate counts. Active Air Sampling results are expressed as CFU/m3. The sampled air volume is recorded in liters and the result is normalized to one cubic meter using: CFU/m3 = Total Colony Count / Sampled Air Volume (L) x 1000. The factor 1000 is only a unit conversion factor from liters to one cubic meter and does not represent the sampled air volume. Results are interpreted against the recorded approved alert and action limits used for this report. This document is an EM result report and is not a Certificate of Analysis.";
        }

        private Table BuildResultEvaluationBox()
        {
            Table table = new Table { CellSpacing = 0 };
            table.Columns.Add(new TableColumn { Width = new GridLength(690) });
            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);
            TableRow row = new TableRow();
            TableCell cell = new TableCell
            {
                Background = CreateReportBrush("#EFF6FF"),
                BorderBrush = CreateReportBrush("#7CB7DF"),
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(10, 7, 10, 7)
            };

            System.Windows.Documents.List list = BuildResultEvaluationList();
            cell.Blocks.Add(list);
            row.Cells.Add(cell);
            group.Rows.Add(row);
            return table;
        }

        private System.Windows.Documents.List BuildResultEvaluationList()
        {
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = TextMarkerStyle.Disc,
                Margin = new Thickness(18, 0, 0, 0),
                Padding = new Thickness(0),
                FontSize = 9.0,
                Foreground = CreateReportBrush("#1E3A5F")
            };

            if (HasSettlePlateResults())
                list.ListItems.Add(MakeEvaluationListItem("Settle Plate results are evaluated as direct CFU/plate counts."));

            if (HasActiveAirPlates())
            {
                list.ListItems.Add(MakeEvaluationListItem("Active Air Sampling results are normalized to CFU/m3 using: Total Colony Count / Sampled Air Volume (L) x 1000."));
                list.ListItems.Add(MakeEvaluationListItem("The factor 1000 converts liters to one cubic meter and does not represent the sampled air volume."));
            }

            list.ListItems.Add(MakeEvaluationListItem("Results are interpreted against the recorded approved alert and action limits used for this report."));
            if (plateItems.Any(item => item.LimitReconciliationId.HasValue))
                list.ListItems.Add(MakeEvaluationListItem("One or more historical EM limit snapshots were supplied by signed, append-only reconciliation evidence; the original plate records were not rewritten."));
            list.ListItems.Add(MakeEvaluationListItem("This controlled document is an Environmental Monitoring result report and is not a Certificate of Analysis."));
            return list;
        }

        private ListItem MakeEvaluationListItem(string text)
        {
            return new ListItem(new Paragraph(new Run(text ?? ""))
            {
                Margin = new Thickness(0, 0, 0, 2),
                LineHeight = 12
            });
        }
        private void AddEMPlateResultsTable(FlowDocument document)
        {
            if (plateItems == null || plateItems.Count == 0)
            {
                document.Blocks.Add(MakeParagraph("No plate records found."));
                return;
            }

            bool includeAirVolume = HasActiveAirPlates();
            Table table = new Table { CellSpacing = 0 };

            if (includeAirVolume)
            {
                table.Columns.Add(new TableColumn { Width = new GridLength(82) });
                table.Columns.Add(new TableColumn { Width = new GridLength(68) });
                table.Columns.Add(new TableColumn { Width = new GridLength(47) });
                table.Columns.Add(new TableColumn { Width = new GridLength(70) });
                table.Columns.Add(new TableColumn { Width = new GridLength(54) });
                table.Columns.Add(new TableColumn { Width = new GridLength(170) });
                table.Columns.Add(new TableColumn { Width = new GridLength(70) });
                table.Columns.Add(new TableColumn { Width = new GridLength(127) });
            }
            else
            {
                table.Columns.Add(new TableColumn { Width = new GridLength(105) });
                table.Columns.Add(new TableColumn { Width = new GridLength(85) });
                table.Columns.Add(new TableColumn { Width = new GridLength(75) });
                table.Columns.Add(new TableColumn { Width = new GridLength(205) });
                table.Columns.Add(new TableColumn { Width = new GridLength(78) });
                table.Columns.Add(new TableColumn { Width = new GridLength(140) });
            }

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);
            Brush headerBackground = CreateReportBrush("#0F4C81");
            Brush headerForeground = Brushes.White;
            double headerFontSize = includeAirVolume ? 7.7 : 8.3;

            TableRow header = new TableRow();
            header.Cells.Add(MakeReportCell("Method", true, headerFontSize, TextAlignment.Left, headerBackground, headerForeground));
            header.Cells.Add(MakeReportCell("Plate Code", true, headerFontSize, TextAlignment.Left, headerBackground, headerForeground));
            if (includeAirVolume)
                header.Cells.Add(MakeReportCell("Raw", true, headerFontSize, TextAlignment.Center, headerBackground, headerForeground));
            header.Cells.Add(MakeReportCell("Result", true, headerFontSize, TextAlignment.Center, headerBackground, headerForeground));
            if (includeAirVolume)
                header.Cells.Add(MakeReportCell("Air Vol. L", true, headerFontSize, TextAlignment.Center, headerBackground, headerForeground));
            header.Cells.Add(MakeReportCell("Approved Limits / Specification", true, headerFontSize, TextAlignment.Left, headerBackground, headerForeground));
            header.Cells.Add(MakeReportCell("Status", true, headerFontSize, TextAlignment.Center, headerBackground, headerForeground));
            header.Cells.Add(MakeReportCell("Remarks", true, headerFontSize, TextAlignment.Left, headerBackground, headerForeground));
            group.Rows.Add(header);

            int rowIndex = 0;
            foreach (EMPlateResultItem item in plateItems)
            {
                string unit = GetReportUnitDisplay(item);
                string result = GetReportResultDisplay(item);
                string resultWithUnit = string.IsNullOrWhiteSpace(result) ? "N/A" : result + " " + unit;
                string status = NormalizeStatusForReport(item.Status);
                string remarks = ReportValueOrNA(item.Remarks);
                string specification = BuildCompactSpecificationText(item);
                Brush rowBackground = rowIndex % 2 == 0 ? Brushes.White : CreateReportBrush("#F8FAFC");

                TableRow row = new TableRow();
                double rowFontSize = includeAirVolume ? 7.7 : 8.2;
                row.Cells.Add(MakeReportCell(item.Method, false, rowFontSize, TextAlignment.Left, rowBackground, Brushes.Black));
                row.Cells.Add(MakeReportCell(FormatPlateCodeForReport(item.PlateCode), true, rowFontSize, TextAlignment.Left, rowBackground, Brushes.Black));

                if (includeAirVolume)
                {
                    string raw = item.TotalCount.HasValue ? item.TotalCount.Value.ToString(CultureInfo.InvariantCulture) : "N/A";
                    row.Cells.Add(MakeReportCell(raw, false, rowFontSize, TextAlignment.Center, rowBackground, Brushes.Black));
                }

                row.Cells.Add(MakeReportCell(resultWithUnit, true, rowFontSize, TextAlignment.Center, rowBackground, Brushes.Black));

                if (includeAirVolume)
                {
                    string airVolume = IsActiveAirSampling(item.Method)
                        ? (item.AirVolumeLiters.HasValue && item.AirVolumeLiters.Value > 0
                            ? item.AirVolumeLiters.Value.ToString("N0", CultureInfo.InvariantCulture)
                            : "N/A")
                        : "N/A";
                    row.Cells.Add(MakeReportCell(airVolume, false, rowFontSize, TextAlignment.Center, rowBackground, Brushes.Black));
                }

                row.Cells.Add(MakeReportCell(specification, false, rowFontSize, TextAlignment.Left, rowBackground, Brushes.Black));
                row.Cells.Add(MakeReportCell(status, true, rowFontSize, TextAlignment.Center,
                    GetStatusBackgroundBrush(item.Status), GetStatusForegroundBrush(item.Status)));
                row.Cells.Add(MakeReportCell(remarks, false, rowFontSize, TextAlignment.Left, rowBackground, Brushes.Black));
                group.Rows.Add(row);
                rowIndex++;
            }

            document.Blocks.Add(table);
        }

        private string BuildEMSpecificationText(EMPlateResultItem item)
        {
            if (item == null)
                return "Not defined";

            string unit = GetReportUnitDisplay(item);
            string alert = item.AlertLimit.HasValue
                ? "Alert limit: NMT " + item.AlertLimit.Value.ToString(CultureInfo.InvariantCulture) + " " + unit
                : "Alert limit: Not defined";

            string action = item.ActionLimit.HasValue
                ? "Action limit: NMT " + item.ActionLimit.Value.ToString(CultureInfo.InvariantCulture) + " " + unit
                : "Action limit: Not defined";

            if (IsActiveAirSampling(item.Method))
                return alert + "; " + action + "; Result normalized to CFU/m3 using: Total Colony Count / Sampled Air Volume (L) x 1000. The factor 1000 is a liters-to-one-cubic-meter conversion factor.";

            if (IsSettlePlate(item.Method))
                return alert + "; " + action + "; Result evaluated as direct CFU/plate count.";

            return alert + "; " + action + ".";
        }

        private string GetReportResultDisplay(EMPlateResultItem item)
        {
            if (item == null || !item.TotalCount.HasValue)
                return "";

            if (IsActiveAirSampling(item.Method))
                return FirstNonEmpty(item.ResultCFU, "");

            return item.TotalCount.Value.ToString(CultureInfo.InvariantCulture);
        }

        private string GetReportUnitDisplay(EMPlateResultItem item)
        {
            if (item == null)
                return "CFU";

            if (IsActiveAirSampling(item.Method))
                return "CFU/m3";

            if (IsSettlePlate(item.Method) || IsContactPlate(item.Method))
                return "CFU/plate";

            if (IsSurfaceSwab(item.Method))
                return "CFU/swab";

            if (IsPersonnelMonitoring(item.Method))
                return "CFU/glove";

            return FirstNonEmpty(item.Unit, "CFU");
        }

        private Paragraph MakeParagraph(string text, bool bold = false, double fontSize = 10.5)
        {
            Paragraph paragraph = new Paragraph(new Run(text ?? ""));
            paragraph.Margin = new Thickness(0, 2, 0, 4);
            paragraph.FontSize = fontSize;

            if (bold)
                paragraph.FontWeight = FontWeights.Bold;

            return paragraph;
        }

        private Brush CreateReportBrush(string hex)
        {
            try
            {
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            }
            catch
            {
                return Brushes.Transparent;
            }
        }

        private string FormatLimitValue(decimal? value)
        {
            if (!value.HasValue)
                return "Not defined";

            return value.Value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private string FormatPlateCodeForReport(string plateCode)
        {
            string value = (plateCode ?? "").Trim();

            if (string.IsNullOrWhiteSpace(value))
                return "";

            Match match = System.Text.RegularExpressions.Regex.Match(value, @"^(D\d+)(SP|AS)(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                string area = match.Groups[1].Value.ToUpperInvariant();
                string method = match.Groups[2].Value.ToUpperInvariant();
                string seq = match.Groups[3].Value.PadLeft(2, '0');
                return area + "-" + method + "-" + seq;
            }

            return value;
        }

        private string FormatAirSamplingTimeForReport()
        {
            string direct = FormatTimeForReport(currentAirSamplingTime);

            if (!direct.Equals("Not documented", StringComparison.OrdinalIgnoreCase))
                return direct;

            string from = FormatTimeForReport(currentSamplingTimeFrom);
            string to = FormatTimeForReport(currentSamplingTimeTo);

            if (!from.Equals("Not documented", StringComparison.OrdinalIgnoreCase) &&
                !to.Equals("Not documented", StringComparison.OrdinalIgnoreCase))
                return from + " - " + to;

            return "Not documented";
        }

        private string GetReportAirVolumeSummary()
        {
            if (plateItems == null || plateItems.Count == 0)
                return "Not documented";

            List<int> volumes = plateItems
                .Where(item => IsActiveAirSampling(item.Method) && item.AirVolumeLiters.HasValue && item.AirVolumeLiters.Value > 0)
                .Select(item => item.AirVolumeLiters.Value)
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            if (volumes.Count == 0)
                return "Not documented";

            return string.Join(", ", volumes.Select(value => value.ToString("N0", CultureInfo.InvariantCulture) + " L"));
        }

        private Brush GetStatusBackgroundBrush(string status)
        {
            string value = (status ?? "").Trim();

            if (value.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#DCFCE7");

            if (value.Equals("Alert", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#FEF3C7");

            if (value.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("ACTION / OOS", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#FEE2E2");

            if (value.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#E2E8F0");

            return CreateReportBrush("#F8FAFC");
        }

        private Brush GetStatusForegroundBrush(string status)
        {
            string value = (status ?? "").Trim();

            if (value.Equals("PASS", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#166534");

            if (value.Equals("Alert", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#92400E");

            if (value.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("ACTION / OOS", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#991B1B");

            if (value.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                return CreateReportBrush("#334155");

            return Brushes.Black;
        }

        private Table MakeSummaryDashboardTable()
        {
            Table table = new Table { CellSpacing = 6 };
            for (int i = 0; i < 6; i++)
                table.Columns.Add(new TableColumn { Width = new GridLength(113) });

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);
            TableRow row = new TableRow();
            row.Cells.Add(MakeDashboardCell("TOTAL PLATES", GetPlateCountByStatus(null) + " Plates", "#EAF4FB", "#0F4C81"));
            row.Cells.Add(MakeDashboardCell("ENTERED", lblEnteredPlates.Text + " Plates", "#EEF2FF", "#3730A3"));
            row.Cells.Add(MakeDashboardCell("PASS", GetPlateCountByStatus("PASS") + " Plates", "#DCFCE7", "#166534"));
            row.Cells.Add(MakeDashboardCell("ALERT", GetPlateCountByStatus("Alert") + " Plates", "#FEF3C7", "#92400E"));
            row.Cells.Add(MakeDashboardCell("ACTION / OOS", GetPlateCountByStatus("OOS") + " Plates", "#FEE2E2", "#991B1B"));
            row.Cells.Add(MakeDashboardCell("FINAL DECISION", GetEMReportFinalResultDisplay(), "#E2E8F0", "#0F172A"));
            group.Rows.Add(row);
            return table;
        }

        private TableCell MakeDashboardCell(string label, string value, string backgroundHex, string foregroundHex)
        {
            TableCell cell = new TableCell();
            cell.Background = CreateReportBrush(backgroundHex);
            cell.BorderBrush = CreateReportBrush("#CBD5E1");
            cell.BorderThickness = new Thickness(0.5);
            cell.Padding = new Thickness(8, 7, 8, 7);

            Paragraph labelParagraph = new Paragraph(new Run(label ?? ""));
            labelParagraph.Margin = new Thickness(0, 0, 0, 3);
            labelParagraph.FontSize = 7.2;
            labelParagraph.FontWeight = FontWeights.Bold;
            labelParagraph.Foreground = CreateReportBrush("#475569");
            labelParagraph.TextAlignment = TextAlignment.Center;
            cell.Blocks.Add(labelParagraph);

            Paragraph valueParagraph = new Paragraph(new Run(value ?? ""));
            valueParagraph.Margin = new Thickness(0);
            valueParagraph.FontSize = (value ?? "").Length > 14 ? 8.2 : 13;
            valueParagraph.FontWeight = FontWeights.Bold;
            valueParagraph.Foreground = CreateReportBrush(foregroundHex);
            valueParagraph.TextAlignment = TextAlignment.Center;
            cell.Blocks.Add(valueParagraph);

            return cell;
        }


        private Table MakeFourColumnTable(params string[] values)
        {
            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(new TableColumn { Width = new GridLength(125) });
            table.Columns.Add(new TableColumn { Width = new GridLength(220) });
            table.Columns.Add(new TableColumn { Width = new GridLength(125) });
            table.Columns.Add(new TableColumn { Width = new GridLength(220) });

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (int i = 0; i < values.Length; i += 4)
            {
                TableRow row = new TableRow();

                string label1 = i < values.Length ? values[i] : "";
                string value1 = i + 1 < values.Length ? values[i + 1] : "";
                string label2 = i + 2 < values.Length ? values[i + 2] : "";
                string value2 = i + 3 < values.Length ? values[i + 3] : "";

                row.Cells.Add(MakeReportCell(label1, true, 8.4, TextAlignment.Left, CreateReportBrush("#E8EEF5"), CreateReportBrush("#0F4C81")));
                row.Cells.Add(MakeReportCell(value1, false, 8.4));

                if (string.IsNullOrWhiteSpace(label2))
                {
                    TableCell filler = MakeReportCell("", false, 8.4, TextAlignment.Left, Brushes.White, Brushes.Black);
                    filler.ColumnSpan = 2;
                    row.Cells.Add(filler);
                }
                else
                {
                    row.Cells.Add(MakeReportCell(label2, true, 8.4, TextAlignment.Left, CreateReportBrush("#E8EEF5"), CreateReportBrush("#0F4C81")));
                    row.Cells.Add(MakeReportCell(value2, false, 8.4));
                }

                group.Rows.Add(row);
            }

            return table;
        }

        private Table MakeTwoColumnTable(params string[] values)
        {
            Table table = new Table();
            table.CellSpacing = 0;
            table.Columns.Add(new TableColumn { Width = new GridLength(165) });
            table.Columns.Add(new TableColumn { Width = new GridLength(525) });

            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);

            for (int i = 0; i + 1 < values.Length; i += 2)
            {
                TableRow row = new TableRow();
                row.Cells.Add(MakeReportCell(values[i], true, 8.4, TextAlignment.Left, CreateReportBrush("#E8EEF5"), CreateReportBrush("#0F4C81")));
                row.Cells.Add(MakeReportCell(values[i + 1], false, 8.4));
                group.Rows.Add(row);
            }

            return table;
        }
        private TableCell MakeReportCell(string text, bool bold = false, double fontSize = 9, TextAlignment alignment = TextAlignment.Left, Brush background = null, Brush foreground = null)
        {
            Paragraph paragraph = new Paragraph(new Run(text ?? ""));
            paragraph.Margin = new Thickness(0);
            paragraph.FontSize = fontSize;
            paragraph.TextAlignment = alignment;
            paragraph.LineHeight = fontSize + 3;

            TableCell cell = new TableCell(paragraph);
            cell.Padding = new Thickness(5, 3, 5, 3);
            cell.BorderBrush = CreateReportBrush("#CBD5E1");
            cell.BorderThickness = new Thickness(0.5);

            if (background != null)
                cell.Background = background;

            if (foreground != null)
                cell.Foreground = foreground;

            if (bold)
                cell.FontWeight = FontWeights.Bold;

            return cell;
        }
        private void AddReportSectionTitle(FlowDocument document, string title)
        {
            Paragraph paragraph = new Paragraph(new Run(title ?? ""));
            paragraph.FontSize = 11.5;
            paragraph.FontWeight = FontWeights.Bold;
            paragraph.Foreground = Brushes.White;
            paragraph.Background = CreateReportBrush("#0F4C81");
            paragraph.Padding = new Thickness(7, 4, 7, 4);
            paragraph.Margin = new Thickness(0, 8, 0, 3);
            paragraph.BorderBrush = CreateReportBrush("#0B3558");
            paragraph.BorderThickness = new Thickness(0.8);
            paragraph.KeepWithNext = true;
            document.Blocks.Add(paragraph);
        }

        private Table MakeConclusionBox(string text)
        {
            bool oos = HasOosResults();
            bool alert = HasAlertResults();
            Brush background = oos ? CreateReportBrush("#FEE2E2") : alert ? CreateReportBrush("#FEF3C7") : CreateReportBrush("#DCFCE7");
            Brush foreground = oos ? CreateReportBrush("#991B1B") : alert ? CreateReportBrush("#92400E") : CreateReportBrush("#166534");
            string decision = oos ? "ACTION / OOS" : alert ? "ALERT" : "PASS";

            Table table = new Table { CellSpacing = 0 };
            table.Columns.Add(new TableColumn { Width = new GridLength(690) });
            TableRowGroup group = new TableRowGroup();
            table.RowGroups.Add(group);
            TableRow row = new TableRow();
            TableCell cell = new TableCell
            {
                Background = background,
                BorderBrush = foreground,
                BorderThickness = new Thickness(1.1),
                Padding = new Thickness(12, 8, 12, 9)
            };

            cell.Blocks.Add(new Paragraph(new Run("FINAL DECISION: " + decision))
            {
                TextAlignment = TextAlignment.Center,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = foreground,
                Margin = new Thickness(0, 0, 0, 5)
            });
            cell.Blocks.Add(new Paragraph(new Run(text ?? ""))
            {
                TextAlignment = TextAlignment.Left,
                FontSize = 9.2,
                FontWeight = FontWeights.SemiBold,
                Foreground = foreground,
                Margin = new Thickness(0),
                LineHeight = 13
            });
            row.Cells.Add(cell);
            group.Rows.Add(row);
            return table;
        }

        private string ReportValueOrNA(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();
        }

        private string BuildCompactSpecificationText(EMPlateResultItem item)
        {
            if (item == null)
                return "N/A";

            string unit = GetReportUnitDisplay(item);
            string alert = item.AlertLimit.HasValue
                ? "Alert: " + item.AlertLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit
                : "Alert: N/A";
            string action = item.ActionLimit.HasValue
                ? "Action: " + item.ActionLimit.Value.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit
                : "Action: N/A";

            return alert + " | " + action;
        }

        private string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "Not documented";
        }

        private bool HasOosResults()
        {
            return HasAnyPlateStatus("OOS");
        }

        private bool HasAlertResults()
        {
            return HasAnyPlateStatus("Alert");
        }

        private bool HasQualityEventTriggerResults()
        {
            return HasOosResults() || HasAlertResults();
        }

        private bool IsCurrentQualityEventClosed()
        {
            if (currentQualityEventId <= 0)
                return false;

            string status = (currentQualityEventStatus ?? "").Trim();
            // Keep UI release semantics identical to the transactional EM approval gate.
            // Approved is not terminal closure; cancelled/rejected events do not release the area.
            bool closed = status.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                          status.Equals("QA Closed", StringComparison.OrdinalIgnoreCase);

            // Fail closed: only explicit EM release dispositions allow the linked
            // monitoring record to continue. Retest/resample approval or monitoring
            // still required are not final area-release decisions.
            string disposition = (currentQualityEventQaDisposition ?? "").Trim();
            bool qaReleased =
                disposition.Equals("Approved", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Area Released after Corrective Action", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Accept with Justification", StringComparison.OrdinalIgnoreCase);

            return closed && qaReleased;
        }

        private bool IsAdminWorkflowOverrideAllowed()
        {
            try
            {
                if (!AppConfig.DevelopmentAdminFullPermissions)
                    return false;

                var user = _authService.GetCurrentUser();
                string role = user?.Role ?? Login.CurrentUserRole ?? "";

                if (string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(Login.CurrentUser))
                    role = (DatabaseHelper.GetUserRole(Login.CurrentUser) ?? "").Trim();

                return role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                       role.Equals("Administrator", StringComparison.OrdinalIgnoreCase) ||
                       role.Equals("System Admin", StringComparison.OrdinalIgnoreCase) ||
                       role.Equals("System Administrator", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Infrastructure.ApplicationLogger.Warning("Unable to verify the EM development Admin workflow override. Override denied.", ex);
                return false;
            }
        }

        private bool HasCurrentUserSignedAction(string actionType)
        {
            if (currentEventId <= 0 || string.IsNullOrWhiteSpace(actionType))
                return false;

            try
            {
                DataTable signatures = DatabaseHelper.GetEMEventSignatures(currentEventId);
                if (signatures == null || signatures.Rows.Count == 0)
                    return false;

                string loginUser = (Login.CurrentUser ?? "").Trim();

                foreach (DataRow row in signatures.Rows)
                {
                    string rowAction = row.Table.Columns.Contains("ActionType") ? row.GetSafeString("ActionType") : "";
                    if (!rowAction.Equals(actionType, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string signedBy = row.Table.Columns.Contains("SignedBy") ? row.GetSafeString("SignedBy").Trim() : "";
                    if (!string.IsNullOrWhiteSpace(loginUser) &&
                        signedBy.Equals(loginUser, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Fail closed: if the signature history cannot be verified, do not
                // permit an independent-review/approval step to proceed on the
                // assumption that the current user has never signed this event.
                Infrastructure.ApplicationLogger.Warning(
                    "Unable to verify EM signature history for segregation-of-duties enforcement. Workflow action blocked.",
                    ex);
                return true;
            }

            return false;
        }

        private void LoadQualityEventSummary(int qualityEventId)
        {
            currentQualityEventStatus = "";
            currentQualityEventQaDisposition = "";
            currentQualityEventRootCause = "";
            currentQualityEventCapa = "";
            currentQualityEventClosedBy = "";
            currentQualityEventClosedDate = "";

            if (qualityEventId <= 0 || !TableExists("QualityEvents"))
                return;

            try
            {
                string idColumn = FirstExistingColumn("QualityEvents", "QualityEventID", "QualityEventId", "Id", "EventID", "EventId");
                if (string.IsNullOrWhiteSpace(idColumn))
                    return;

                string query = "SELECT TOP 1 * FROM dbo.QualityEvents WHERE [" + idColumn + "] = @QualityEventId;";
                SqlParameter[] pars = { new SqlParameter("@QualityEventId", qualityEventId) };
                DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);
                if (dt.Rows.Count == 0)
                    return;

                DataRow row = dt.Rows[0];
                currentQualityEventStatus = FirstExistingValue(row, "CurrentStatus", "Status", "EventStatus", "InvestigationStatus");
                currentQualityEventQaDisposition = FirstExistingValue(row, "FinalDisposition", "QAConclusion", "QADisposition", "Disposition");
                currentQualityEventRootCause = FirstExistingValue(row, "RootCauseDetails", "RootCause", "RootCauseCategory");

                string capaRequired = FirstExistingValue(row, "CAPARequired", "CapaRequired");
                if (string.IsNullOrWhiteSpace(capaRequired))
                    currentQualityEventCapa = FirstExistingValue(row, "CAPA", "Capa", "CorrectiveAction", "PreventiveAction");
                else
                    currentQualityEventCapa = NormalizeBoolDisplay(capaRequired);

                currentQualityEventClosedBy = FirstExistingValue(row, "ClosedBy", "QAClosedBy", "ApprovedBy");
                currentQualityEventClosedDate = FirstExistingDateValue(row, "ClosedDate", "QAClosedDate", "ApprovedDate");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Unable to load Quality Event summary: " + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private string FirstExistingValue(DataRow row, params string[] columns)
        {
            if (row == null || row.Table == null)
                return "";

            foreach (string column in columns)
            {
                if (row.Table.Columns.Contains(column))
                {
                    string value = row.GetSafeString(column);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }

            return "";
        }

        private string FirstExistingDateValue(DataRow row, params string[] columns)
        {
            if (row == null || row.Table == null)
                return "";

            foreach (string column in columns)
            {
                if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                    continue;

                if (DateTime.TryParse(row[column].ToString(), out DateTime value))
                    return value.ToString("yyyy-MM-dd HH:mm");

                string raw = row[column].ToString();
                if (!string.IsNullOrWhiteSpace(raw))
                    return raw.Trim();
            }

            return "";
        }

        private string NormalizeBoolDisplay(string value)
        {
            string normalized = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return "Not documented";

            if (normalized.Equals("1") || normalized.Equals("true", StringComparison.OrdinalIgnoreCase))
                return "Required";

            if (normalized.Equals("0") || normalized.Equals("false", StringComparison.OrdinalIgnoreCase))
                return "Not Required";

            return normalized;
        }

        private string NormalizeStatusForReport(string status)
        {
            string value = (status ?? "").Trim();
            if (value.Equals("OOS", StringComparison.OrdinalIgnoreCase))
                return "ACTION / OOS";
            return string.IsNullOrWhiteSpace(value) ? "Pending" : value;
        }

        private string GetPlateCountByStatus(string status)
        {
            if (plateItems == null)
                return "0";

            if (string.IsNullOrWhiteSpace(status))
                return plateItems.Count.ToString(CultureInfo.InvariantCulture);

            int count = plateItems.Count(x => (x.Status ?? "").Trim().Equals(status, StringComparison.OrdinalIgnoreCase));
            return count.ToString(CultureInfo.InvariantCulture);
        }
        private string FormatTimeForReport(string value)
        {
            string text = (value ?? "").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "Not documented";

            text = text.Replace(",", ".").Trim();

            if (text.Equals("00:00", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("0:00", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("00.00", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("0.00", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("00", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("0", StringComparison.OrdinalIgnoreCase))
                return "Not documented";

            Match dotTime = System.Text.RegularExpressions.Regex.Match(text, @"^(\d{1,2})\.(\d{1,2})$");
            if (dotTime.Success)
            {
                int hour = Convert.ToInt32(dotTime.Groups[1].Value, CultureInfo.InvariantCulture);
                int minute = Convert.ToInt32(dotTime.Groups[2].Value, CultureInfo.InvariantCulture);

                if (hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59)
                    return hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
            }

            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan time))
            {
                if (time.TotalMinutes == 0)
                    return "Not documented";

                return time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dateTimeValue))
            {
                if (dateTimeValue.Hour == 0 && dateTimeValue.Minute == 0)
                    return "Not documented";

                return dateTimeValue.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hourOnly) && hourOnly >= 0 && hourOnly <= 23)
            {
                if (hourOnly == 0)
                    return "Not documented";

                return hourOnly.ToString("00", CultureInfo.InvariantCulture) + ":00";
            }

            return text;
        }

        private string FormatTemperatureForReport(string value)
        {
            string text = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return "Not documented";

            if (text.Contains("°") || text.Contains("C", StringComparison.OrdinalIgnoreCase))
                return text;

            return text + " °C";
        }

        private void CheckLinkedDeviation()
        {
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

            if (currentEventId <= 0 && string.IsNullOrWhiteSpace(currentEventNo))
                return;

            try
            {
                DataTable found = FindLinkedQualityEvent();

                if (found != null && found.Rows.Count > 0)
                {
                    DataRow row = found.Rows[0];

                    currentQualityEventId = row.GetSafeInt("QualityEventId");
                    currentQualityEventNo = row.GetSafeString("QualityEventNo");
                    LoadQualityEventSummary(currentQualityEventId);

                    if (BtnOpenDeviation != null)
                    {
                        BtnOpenDeviation.Visibility = Visibility.Visible;
                        BtnOpenDeviation.IsEnabled = true;
                    }

                    if (InvestigationBadge != null)
                        InvestigationBadge.Visibility = Visibility.Visible;
                }
            }
            catch
            {
                currentQualityEventId = 0;
                currentQualityEventNo = "";

                if (BtnOpenDeviation != null)
                    BtnOpenDeviation.Visibility = Visibility.Collapsed;

                if (InvestigationBadge != null)
                    InvestigationBadge.Visibility = Visibility.Collapsed;
            }
        }

        private DataTable FindLinkedQualityEvent()
        {
            if (!TableExists("QualityEvents"))
                return new DataTable();

            string idColumn = FirstExistingColumn("QualityEvents",
                "QualityEventID", "QualityEventId", "Id", "EventID", "EventId");

            if (string.IsNullOrWhiteSpace(idColumn))
                return new DataTable();

            string numberColumn = FirstExistingColumn("QualityEvents",
                "QualityEventNo", "QualityEventNumber", "EventNo", "DeviationNo", "InvestigationNo", "RecordNo");

            List<string> conditions = new List<string>();

            AddConditionIfColumnExists(conditions, "QualityEvents", "SourceRecordID", "TRY_CONVERT(INT, SourceRecordID) = @EventId");
            AddConditionIfColumnExists(conditions, "QualityEvents", "SourceRecordId", "TRY_CONVERT(INT, SourceRecordId) = @EventId");
            AddConditionIfColumnExists(conditions, "QualityEvents", "RelatedRecordID", "TRY_CONVERT(INT, RelatedRecordID) = @EventId");
            AddConditionIfColumnExists(conditions, "QualityEvents", "RelatedRecordId", "TRY_CONVERT(INT, RelatedRecordId) = @EventId");

            AddConditionIfColumnExists(conditions, "QualityEvents", "SourceReferenceNo", "SourceReferenceNo = @EventNo");
            AddConditionIfColumnExists(conditions, "QualityEvents", "RelatedRecordNo", "RelatedRecordNo = @EventNo");
            AddConditionIfColumnExists(conditions, "QualityEvents", "ReferenceNo", "ReferenceNo = @EventNo");
            AddConditionIfColumnExists(conditions, "QualityEvents", "EventNo", "EventNo = @EventNo");
            AddConditionIfColumnExists(conditions, "QualityEvents", "SampleNumber", "SampleNumber = @EventNo");

            if (conditions.Count == 0)
                return new DataTable();

            string qualityEventNoSelect = string.IsNullOrWhiteSpace(numberColumn)
                ? "CAST([" + idColumn + "] AS NVARCHAR(50))"
                : "CAST([" + numberColumn + "] AS NVARCHAR(100))";

            string query = @"
                SELECT TOP 1
                    TRY_CONVERT(INT, [" + idColumn + @"]) AS QualityEventId,
                    " + qualityEventNoSelect + @" AS QualityEventNo
                FROM QualityEvents
                WHERE (" + string.Join(" OR ", conditions) + @")
                ORDER BY TRY_CONVERT(INT, [" + idColumn + @"]) DESC;";

            SqlParameter[] pars =
            {
                new SqlParameter("@EventId", currentEventId),
                new SqlParameter("@EventNo", currentEventNo ?? "")
            };

            DataTable result = DatabaseHelper.ExecuteQuery(query, pars);

            if (result.Rows.Count > 0)
                return result;

            return FindLinkedQualityEventFromRelatedItems(idColumn, numberColumn);
        }

        private DataTable FindLinkedQualityEventFromRelatedItems(string qualityEventIdColumn, string qualityEventNoColumn)
        {
            if (!TableExists("QualityEventRelatedItems"))
                return new DataTable();

            string relatedQualityEventIdColumn = FirstExistingColumn("QualityEventRelatedItems",
                "QualityEventID", "QualityEventId", "EventID", "EventId");

            if (string.IsNullOrWhiteSpace(relatedQualityEventIdColumn))
                return new DataTable();

            List<string> relatedConditions = new List<string>();

            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "RelatedRecordID", "TRY_CONVERT(INT, R.RelatedRecordID) = @EventId");
            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "RelatedRecordId", "TRY_CONVERT(INT, R.RelatedRecordId) = @EventId");
            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "RecordID", "TRY_CONVERT(INT, R.RecordID) = @EventId");
            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "RecordId", "TRY_CONVERT(INT, R.RecordId) = @EventId");

            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "RelatedRecordNo", "R.RelatedRecordNo = @EventNo");
            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "ReferenceNo", "R.ReferenceNo = @EventNo");
            AddConditionIfColumnExists(relatedConditions, "QualityEventRelatedItems", "RecordNo", "R.RecordNo = @EventNo");

            if (relatedConditions.Count == 0)
                return new DataTable();

            string qualityEventNoSelect = string.IsNullOrWhiteSpace(qualityEventNoColumn)
                ? "CAST(Q.[" + qualityEventIdColumn + "] AS NVARCHAR(50))"
                : "CAST(Q.[" + qualityEventNoColumn + "] AS NVARCHAR(100))";

            string query = @"
                SELECT TOP 1
                    TRY_CONVERT(INT, Q.[" + qualityEventIdColumn + @"]) AS QualityEventId,
                    " + qualityEventNoSelect + @" AS QualityEventNo
                FROM QualityEventRelatedItems R
                INNER JOIN QualityEvents Q
                    ON TRY_CONVERT(INT, Q.[" + qualityEventIdColumn + @"]) = TRY_CONVERT(INT, R.[" + relatedQualityEventIdColumn + @"])
                WHERE (" + string.Join(" OR ", relatedConditions) + @")
                ORDER BY TRY_CONVERT(INT, Q.[" + qualityEventIdColumn + @"]) DESC;";

            SqlParameter[] pars =
            {
                new SqlParameter("@EventId", currentEventId),
                new SqlParameter("@EventNo", currentEventNo ?? "")
            };

            return DatabaseHelper.ExecuteQuery(query, pars);
        }

        private bool TableExists(string tableName)
        {
            string query = @"
                SELECT COUNT(1)
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_SCHEMA = 'dbo'
                  AND TABLE_NAME = @TableName;";

            SqlParameter[] pars =
            {
                new SqlParameter("@TableName", tableName)
            };

            DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);
            if (dt.Rows.Count == 0)
                return false;

            object value = dt.Rows[0][0];
            return value != null && value != DBNull.Value && Convert.ToInt32(value) > 0;
        }

        private bool ColumnExists(string tableName, string columnName)
        {
            string query = @"
                SELECT COUNT(1)
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @TableName
                  AND COLUMN_NAME = @ColumnName;";

            SqlParameter[] pars =
            {
                new SqlParameter("@TableName", tableName),
                new SqlParameter("@ColumnName", columnName)
            };

            DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);
            if (dt.Rows.Count == 0)
                return false;

            object value = dt.Rows[0][0];
            return value != null && value != DBNull.Value && Convert.ToInt32(value) > 0;
        }

        private string FirstExistingColumn(string tableName, params string[] candidateColumns)
        {
            foreach (string column in candidateColumns)
            {
                if (ColumnExists(tableName, column))
                    return column;
            }

            return "";
        }
    }
}
