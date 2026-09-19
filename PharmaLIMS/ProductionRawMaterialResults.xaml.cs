using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Repositories;
using PharmaLIMS.Services;
using System;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

#nullable disable

namespace PharmaLIMS
{
    public partial class ProductionRawMaterialResults : Window
    {
        private readonly PrmSpecificationRepository _prmSpecificationRepository = new();
        private int _selectedSampleId = 0;
        private readonly int _initialSampleId = 0;
        private readonly int _initialLegacyCertificateId = 0;
        private readonly int _initialLegacyReconciliationId = 0;
        private readonly string _initialLegacyCertificateNumber = string.Empty;
        private readonly string _initialControlledReissueReason = string.Empty;
        private bool _controlledLegacyReissueRoute = false;
        private bool _controlledLegacyReissueCompleted = false;
        private bool _isLoading = false;
        private DataTable _resultsTable;
        private bool _analysisStartRecorded = false;
        private string _timingReconciliationStatus = "Not Required";
        private string _selectedResultGroup = string.Empty;
        private Button _runtimeQualityEventButton;
        private Button _floatingQualityEventButton;

        private const string PrmQualityEventStateSql = @"
;WITH SampleEvents AS
(
    SELECT
        qe.QualityEventID,
        qe.CurrentStatus,
        qe.ClosedBy,
        qe.ClosedDate,
        qe.RootCauseCategory,
        qe.RootCauseDetails,
        qe.ImpactAssessment,
        qe.QAConclusion,
        qe.FinalDisposition
    FROM dbo.QualityEvents qe WITH (UPDLOCK, HOLDLOCK)
    WHERE qe.SourceModule = N'PRM'
      AND qe.SourceRecordID = @SampleID
),
ValidLegacyReconciliations AS
(
    SELECT DISTINCT rec.LegacyQualityEventID
    FROM dbo.PRM_QualityEventEvidenceReconciliations rec WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN SampleEvents legacyEvent
        ON legacyEvent.QualityEventID = rec.LegacyQualityEventID
    INNER JOIN SampleEvents replacementEvent
        ON replacementEvent.QualityEventID = rec.ReplacementQualityEventID
    INNER JOIN dbo.PRM_ElectronicSignatures signatureEvidence WITH (UPDLOCK, HOLDLOCK)
        ON signatureEvidence.SignatureID = rec.ElectronicSignatureID
       AND signatureEvidence.SampleID = rec.SampleID
       AND CONVERT(VARBINARY(MAX), ISNULL(signatureEvidence.SignedBy, N'')) =
           CONVERT(VARBINARY(MAX), ISNULL(rec.ReconciledBy, N''))
       AND signatureEvidence.ActionType = N'Quality Event Closure'
    WHERE rec.SampleID = @SampleID
      AND rec.ReconciliationSchemaVersion = 1
      AND rec.ReplacementQualityEventID > rec.LegacyQualityEventID
      AND CONVERT(VARBINARY(MAX), ISNULL(rec.ReconciledBy, N'')) = CONVERT(VARBINARY(MAX), ISNULL(replacementEvent.ClosedBy, N''))
      AND rec.ReconciledAt >= replacementEvent.ClosedDate
      AND replacementEvent.CurrentStatus = N'Closed'
      AND NULLIF(LTRIM(RTRIM(ISNULL(replacementEvent.ClosedBy, N''))), N'') IS NOT NULL
      AND replacementEvent.ClosedDate IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(replacementEvent.RootCauseCategory, N''))), N'') IS NOT NULL
      AND replacementEvent.RootCauseCategory NOT IN (N'Pending Investigation', N'Undetermined')
      AND NULLIF(LTRIM(RTRIM(ISNULL(replacementEvent.RootCauseDetails, N''))), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(replacementEvent.ImpactAssessment, N''))), N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(replacementEvent.QAConclusion, N''))), N'') IS NOT NULL
      AND replacementEvent.FinalDisposition IN
      (
          N'Confirmed OOS - Original Result Retained',
          N'OOS Invalidated - Assignable Laboratory Cause',
          N'OOS Not Confirmed - Scientifically Justified',
          N'Escalated to Batch / Material Disposition'
      )
      AND EXISTS
      (
          SELECT 1
          FROM dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK, HOLDLOCK)
          WHERE legacyAffected.QualityEventID = rec.LegacyQualityEventID
            AND legacyAffected.SourceModule = N'PRM'
            AND ISNULL(legacyAffected.EvidenceSchemaVersion, 0) <> 1
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK, HOLDLOCK)
          WHERE legacyAffected.QualityEventID = rec.LegacyQualityEventID
            AND legacyAffected.SourceModule = N'PRM'
            AND ISNULL(legacyAffected.EvidenceSchemaVersion, 0) <> 1
            AND
            (
                legacyAffected.SourceResultID IS NULL
                OR NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.QualityEventAffectedResults replacementAffected WITH (UPDLOCK, HOLDLOCK)
                    INNER JOIN dbo.PRM_SampleTests currentResult WITH (UPDLOCK, HOLDLOCK)
                        ON currentResult.SampleTestID = replacementAffected.SourceResultID
                       AND currentResult.SampleID = @SampleID
                    WHERE replacementAffected.QualityEventID = rec.ReplacementQualityEventID
                      AND replacementAffected.SourceModule = N'PRM'
                      AND replacementAffected.SourceResultID = legacyAffected.SourceResultID
                      AND ISNULL(replacementAffected.EvidenceSchemaVersion, 0) = 1
                      AND CONVERT(VARBINARY(MAX), ISNULL(replacementAffected.TestName, N'')) = CONVERT(VARBINARY(MAX), ISNULL(currentResult.TestName, N''))
                      AND CONVERT(VARBINARY(MAX), ISNULL(replacementAffected.ResultValue, N'')) = CONVERT(VARBINARY(MAX), ISNULL(currentResult.ResultValue, N''))
                      AND CONVERT(VARBINARY(MAX), ISNULL(replacementAffected.SpecificationLimit, N'')) = CONVERT(VARBINARY(MAX), ISNULL(currentResult.SpecificationText, N''))
                      AND (replacementAffected.SpecificationNumericLimit = currentResult.SpecificationLimit OR (replacementAffected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
                      AND CONVERT(VARBINARY(MAX), ISNULL(replacementAffected.Unit, N'')) = CONVERT(VARBINARY(MAX), ISNULL(currentResult.Unit, N''))
                      AND CONVERT(VARBINARY(MAX), ISNULL(replacementAffected.FailureType, N'')) = CONVERT(VARBINARY(MAX), ISNULL(currentResult.Interpretation, N''))
                )
            )
      )
)
SELECT CASE
    WHEN EXISTS
    (
        SELECT 1
        FROM SampleEvents
        WHERE ISNULL(CurrentStatus, N'Open') NOT IN (N'Closed', N'QA Closed', N'Cancelled', N'Rejected Closed')
    ) THEN 1
    WHEN EXISTS
    (
        SELECT 1
        FROM SampleEvents qe
        WHERE qe.CurrentStatus = N'Closed'
          AND
          (
              EXISTS
              (
                  SELECT 1
                  FROM dbo.QualityEventAffectedResults affected WITH (UPDLOCK, HOLDLOCK)
                  LEFT JOIN dbo.PRM_SampleTests st WITH (UPDLOCK, HOLDLOCK)
                      ON st.SampleTestID = affected.SourceResultID
                     AND st.SampleID = @SampleID
                  WHERE affected.QualityEventID = qe.QualityEventID
                    AND affected.SourceModule = N'PRM'
                    AND ISNULL(affected.EvidenceSchemaVersion, 0) = 1
                    AND
                    (
                        st.SampleTestID IS NULL
                        OR CONVERT(VARBINARY(MAX), ISNULL(affected.TestName, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(st.TestName, N''))
                        OR CONVERT(VARBINARY(MAX), ISNULL(affected.ResultValue, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(st.ResultValue, N''))
                        OR CONVERT(VARBINARY(MAX), ISNULL(affected.SpecificationLimit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(st.SpecificationText, N''))
                        OR NOT (affected.SpecificationNumericLimit = st.SpecificationLimit OR (affected.SpecificationNumericLimit IS NULL AND st.SpecificationLimit IS NULL))
                        OR CONVERT(VARBINARY(MAX), ISNULL(affected.Unit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(st.Unit, N''))
                        OR CONVERT(VARBINARY(MAX), ISNULL(affected.FailureType, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(st.Interpretation, N''))
                    )
              )
              OR
              (
                  EXISTS
                  (
                      SELECT 1
                      FROM dbo.QualityEventAffectedResults affected WITH (UPDLOCK, HOLDLOCK)
                      WHERE affected.QualityEventID = qe.QualityEventID
                        AND affected.SourceModule = N'PRM'
                        AND ISNULL(affected.EvidenceSchemaVersion, 0) <> 1
                  )
                  AND NOT EXISTS
                  (
                      SELECT 1
                      FROM ValidLegacyReconciliations validRec
                      WHERE validRec.LegacyQualityEventID = qe.QualityEventID
                  )
              )
          )
    ) THEN 3
    WHEN NOT EXISTS (SELECT 1 FROM SampleEvents) THEN 2
    WHEN EXISTS
    (
        SELECT 1
        FROM SampleEvents qe
        WHERE qe.CurrentStatus = N'Closed'
          AND NULLIF(LTRIM(RTRIM(ISNULL(qe.ClosedBy, N''))), N'') IS NOT NULL
          AND qe.ClosedDate IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(ISNULL(qe.RootCauseCategory, N''))), N'') IS NOT NULL
          AND qe.RootCauseCategory NOT IN (N'Pending Investigation', N'Undetermined')
          AND NULLIF(LTRIM(RTRIM(ISNULL(qe.RootCauseDetails, N''))), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(ISNULL(qe.ImpactAssessment, N''))), N'') IS NOT NULL
          AND NULLIF(LTRIM(RTRIM(ISNULL(qe.QAConclusion, N''))), N'') IS NOT NULL
          AND qe.FinalDisposition IN
          (
              N'Confirmed OOS - Original Result Retained',
              N'OOS Invalidated - Assignable Laboratory Cause',
              N'OOS Not Confirmed - Scientifically Justified',
              N'Escalated to Batch / Material Disposition'
          )
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.PRM_SampleTests st WITH (UPDLOCK, HOLDLOCK)
              WHERE st.SampleID = @SampleID
                AND ISNULL(st.RequiredTest, 1) = 1
                AND st.Interpretation IN (N'Does Not Conform', N'Check Required')
                AND NOT EXISTS
                (
                    SELECT 1
                    FROM dbo.QualityEventAffectedResults affected WITH (UPDLOCK, HOLDLOCK)
                    WHERE affected.QualityEventID = qe.QualityEventID
                      AND affected.SourceModule = N'PRM'
                      AND affected.SourceResultID = st.SampleTestID
                      AND ISNULL(affected.EvidenceSchemaVersion, 0) = 1
                      AND CONVERT(VARBINARY(MAX), ISNULL(affected.TestName, N'')) = CONVERT(VARBINARY(MAX), ISNULL(st.TestName, N''))
                      AND CONVERT(VARBINARY(MAX), ISNULL(affected.ResultValue, N'')) = CONVERT(VARBINARY(MAX), ISNULL(st.ResultValue, N''))
                      AND CONVERT(VARBINARY(MAX), ISNULL(affected.SpecificationLimit, N'')) = CONVERT(VARBINARY(MAX), ISNULL(st.SpecificationText, N''))
                      AND (affected.SpecificationNumericLimit = st.SpecificationLimit OR (affected.SpecificationNumericLimit IS NULL AND st.SpecificationLimit IS NULL))
                      AND CONVERT(VARBINARY(MAX), ISNULL(affected.Unit, N'')) = CONVERT(VARBINARY(MAX), ISNULL(st.Unit, N''))
                      AND CONVERT(VARBINARY(MAX), ISNULL(affected.FailureType, N'')) = CONVERT(VARBINARY(MAX), ISNULL(st.Interpretation, N''))
                )
          )
    ) THEN 0
    ELSE 3
END;";


        private static bool RoleIs(params string[] roles)
        {
            string currentRole = GetCurrentUserRole();
            foreach (string role in roles)
            {
                if (currentRole.Equals(role, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsAdminUser()
        {
            string currentRole = DatabaseHelper.GetUserRole(GetCurrentUserDisplayName());
            return currentRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                   currentRole.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
        }
        private static bool CanEnterPrmResults() => DatabaseHelper.CanEditResults(GetCurrentUserDisplayName());
        private static bool CanSubmitPrmResults() => DatabaseHelper.CanSubmitForReview(GetCurrentUserDisplayName());
        private static bool CanReviewPrmResults() => DatabaseHelper.CanReviewResults(GetCurrentUserDisplayName());
        private static bool CanApprovePrmResults() => DatabaseHelper.CanApproveResults(GetCurrentUserDisplayName());
        private static bool CanIssuePrmCertificate() => DatabaseHelper.CanIssueCertificate(GetCurrentUserDisplayName());
        private static bool CanCancelPrmCertificate() => DatabaseHelper.CanCancelCertificate(GetCurrentUserDisplayName());
        private static bool CanManagePrmQualityEvent() =>
            DatabaseHelper.CanReviewResults(GetCurrentUserDisplayName()) ||
            DatabaseHelper.CanApproveResults(GetCurrentUserDisplayName());

        private static bool IsResultEntryStatus(string status)
        {
            return IsOneOf(status, "Registered", "In Progress", "Results Entered");
        }

        public ProductionRawMaterialResults()
        {
            InitializeComponent();
            Loaded += ProductionRawMaterialResults_Loaded;
        }

        public ProductionRawMaterialResults(int sampleId) : this()
        {
            _initialSampleId = sampleId;
        }

        public ProductionRawMaterialResults(
            int sampleId,
            int legacyCertificateId,
            int legacyReconciliationId,
            string legacyCertificateNumber,
            string controlledReissueReason) : this(sampleId)
        {
            _initialLegacyCertificateId = legacyCertificateId;
            _initialLegacyReconciliationId = legacyReconciliationId;
            _initialLegacyCertificateNumber = (legacyCertificateNumber ?? string.Empty).Trim();
            _initialControlledReissueReason = BuildControlledReissueReason(legacyReconciliationId, _initialLegacyCertificateNumber, controlledReissueReason);
            _controlledLegacyReissueRoute =
                legacyCertificateId > 0 &&
                legacyReconciliationId > 0 &&
                !string.IsNullOrWhiteSpace(_initialControlledReissueReason);
        }

        private async void ProductionRawMaterialResults_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _isLoading = true;
                TxtCurrentUser.Text = "User: " + GetCurrentUserDisplayName() + " | Role: " + GetCurrentUserRole();
                TxtStatus.Text = "Checking PRM database objects...";
                PrmSchemaReadinessResult schemaReadiness =
                    await PrmSchemaReadinessService.EnsureResultsReadyAsync();
                if (!schemaReadiness.IsReady)
                {
                    TxtStatus.Text = schemaReadiness.Message;
                    MessageBox.Show(schemaReadiness.Message,
                        "PRM Database Readiness", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ClearSampleInfo();

                if (_initialSampleId > 0)
                {
                    _selectedResultGroup = GetResultGroupForSampleId(_initialSampleId);
                    ShowResultsView();
                    _isLoading = false;
                    await LoadSamplesAsync();
                    _isLoading = true;
                    SelectSampleById(_initialSampleId);
                }
                else
                {
                    ShowChoiceView();
                }

                if (_controlledLegacyReissueRoute)
                    ApplyControlledLegacyReissueUiState();
                else
                    TxtStatus.Text = "Select Production, Raw Material, or Stability results workflow.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error opening Production / Raw Material Results Entry:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Module Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void ShowChoiceView()
        {
            ChoiceView.Visibility = Visibility.Visible;
            ResultsView.Visibility = Visibility.Collapsed;
            _selectedResultGroup = string.Empty;
            ClearSampleInfo();
            if (DgSamples != null)
                DgSamples.ItemsSource = null;
            if (_runtimeQualityEventButton != null)
                _runtimeQualityEventButton.Visibility = Visibility.Collapsed;
            if (_floatingQualityEventButton != null)
                _floatingQualityEventButton.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Choose the results module: Production, Raw Material, or Stability.";
        }

        private void ShowResultsView()
        {
            ChoiceView.Visibility = Visibility.Collapsed;
            ResultsView.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(EnsureQualityEventButtonExists), System.Windows.Threading.DispatcherPriority.Loaded);

            if (_selectedResultGroup == "PR")
                TxtSamplesTitle.Text = "Production Samples";
            else if (_selectedResultGroup == "RM")
                TxtSamplesTitle.Text = "Raw Material Samples";
            else if (_selectedResultGroup == "ST")
                TxtSamplesTitle.Text = "Stability Samples";
            else
                TxtSamplesTitle.Text = "Samples";
        }

        private static string GetCurrentUserDisplayName()
        {
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required for Production and Raw Material results workflows.");
        }

        private static string GetCurrentUserRole()
        {
            return string.IsNullOrWhiteSpace(Login.CurrentUserRole)
                ? string.Empty
                : Login.CurrentUserRole.Trim();
        }

        private ElectronicSignature RequestPrmSignature(string action)
        {
            string record = string.IsNullOrWhiteSpace(TxtSampleNo?.Text)
                ? _selectedSampleId.ToString(CultureInfo.InvariantCulture)
                : TxtSampleNo.Text.Trim();

            ElectronicSignature signature = new ElectronicSignature(
                record,
                GetCurrentUserDisplayName(),
                action,
                true)
            {
                Owner = this
            };

            return signature.ShowDialog() == true && signature.IsConfirmed
                ? signature
                : null;
        }

        private void UpdateWorkflowControls()
        {
            bool hasSample = _selectedSampleId > 0;
            string status = hasSample ? (TxtSampleStatus.Text ?? string.Empty).Trim() : string.Empty;
            string overall = hasSample ? (TxtOverallInterpretation.Text ?? string.Empty).Trim() : string.Empty;
            string category = hasSample ? (TxtCategory.Text ?? string.Empty).Trim() : string.Empty;
            bool reconciliationRequired = _timingReconciliationStatus.Equals("Required", StringComparison.OrdinalIgnoreCase);
            bool timingReconciled = _timingReconciliationStatus.Equals("Reconciled", StringComparison.OrdinalIgnoreCase);
            bool historicalTimingClosed = _timingReconciliationStatus.Equals("Historical Closed", StringComparison.OrdinalIgnoreCase);
            bool editable = hasSample && IsResultEntryStatus(status) && CanEnterPrmResults() && !timingReconciled && !historicalTimingClosed;
            bool hasCertificate = hasSample && !string.IsNullOrWhiteSpace(TxtCertificateNo?.Text);

            if (DgResults != null)
                DgResults.IsReadOnly = !editable;

            if (BtnStartAnalysis != null)
                BtnStartAnalysis.IsEnabled = hasSample && !_analysisStartRecorded && CanEnterPrmResults() && !timingReconciled && !historicalTimingClosed && IsOneOf(status, "Registered", "In Progress", "Results Entered");
            if (BtnTimingReconciliation != null)
            {
                BtnTimingReconciliation.IsEnabled = hasSample && reconciliationRequired && CanApprovePrmResults() && IsOneOf(status, "Results Entered");
                BtnTimingReconciliation.ToolTip = reconciliationRequired
                    ? "QA-controlled reconciliation is required because legacy timing evidence was incomplete or too early. Re-enter affected results after eligibility, then reconcile."
                    : "No PRM timing reconciliation is required for this sample.";
            }
            if (BtnSaveResults != null)
                BtnSaveResults.IsEnabled = editable;
            if (BtnReloadTests != null)
                BtnReloadTests.IsEnabled = editable && !reconciliationRequired;
            if (BtnSubmitReview != null)
                BtnSubmitReview.IsEnabled = hasSample && !historicalTimingClosed && CanSubmitPrmResults() && IsOneOf(status, "Results Entered");
            if (BtnReview != null)
                BtnReview.IsEnabled = hasSample && !historicalTimingClosed && CanReviewPrmResults() && IsOneOf(status, "Under Review");

            if (BtnApprove != null)
            {
                bool baseApprovalAllowed = hasSample && !historicalTimingClosed && CanApprovePrmResults() && IsOneOf(status, "Reviewed");
                bool qualityApprovalAllowed = true;
                string approvalBlockReason = string.Empty;

                if (baseApprovalAllowed)
                    qualityApprovalAllowed = IsCurrentPrmQualityStateApprovable(overall, out approvalBlockReason);

                BtnApprove.IsEnabled = baseApprovalAllowed && qualityApprovalAllowed;
                BtnApprove.ToolTip = BtnApprove.IsEnabled
                    ? "Approve the reviewed analytical result. This action does not release or reject the batch/material."
                    : BuildDisabledWorkflowToolTip(baseApprovalAllowed, approvalBlockReason, status, "approval");
            }

            bool certificateQualityAllowed = true;
            string certificateBlockReason = string.Empty;
            bool baseIssueAllowed = hasSample && !historicalTimingClosed && CanIssuePrmCertificate() && IsOneOf(status, "Approved", "Certificate Issued") && !hasCertificate;
            if (baseIssueAllowed)
                certificateQualityAllowed = IsCurrentPrmCertificateStateIssuable(category, overall, out certificateBlockReason);

            if (BtnIssueCertificate != null)
            {
                BtnIssueCertificate.IsEnabled = baseIssueAllowed && certificateQualityAllowed;
                BtnIssueCertificate.ToolTip = BtnIssueCertificate.IsEnabled
                    ? "Issue the controlled microbiology certificate/report. Final batch/material disposition remains outside this screen."
                    : BuildDisabledWorkflowToolTip(baseIssueAllowed, certificateBlockReason, status, "certificate/report issuance");
            }

            if (BtnPrintCertificate != null)
                BtnPrintCertificate.IsEnabled = hasSample && hasCertificate &&
                    (DatabaseHelper.CanAccessReports(GetCurrentUserDisplayName()) || CanIssuePrmCertificate());
            if (BtnPreviewCurrentLayout != null)
                BtnPreviewCurrentLayout.IsEnabled = hasSample && hasCertificate &&
                    (DatabaseHelper.CanAccessReports(GetCurrentUserDisplayName()) || CanIssuePrmCertificate());
            if (BtnCancelCertificate != null)
            {
                BtnCancelCertificate.IsEnabled = hasSample && CanCancelPrmCertificate() && hasCertificate;
                BtnCancelCertificate.ToolTip = BtnCancelCertificate.IsEnabled
                    ? "Cancel the active certificate/report with a documented reason and electronic signature."
                    : "Certificate/report cancellation is not available for the current selection or permission set.";
            }
            if (BtnReissueCertificate != null)
            {
                BtnReissueCertificate.Content = "Reissue";
                bool baseReissueAllowed = hasSample && !historicalTimingClosed && CanIssuePrmCertificate() && hasCertificate && IsOneOf(status, "Approved", "Certificate Issued");
                bool reissueQualityAllowed = true;
                string reissueBlockReason = string.Empty;
                if (baseReissueAllowed)
                    reissueQualityAllowed = IsCurrentPrmCertificateStateIssuable(category, overall, out reissueBlockReason);

                BtnReissueCertificate.IsEnabled = baseReissueAllowed && reissueQualityAllowed;
                BtnReissueCertificate.ToolTip = BtnReissueCertificate.IsEnabled
                    ? "Reissue the controlled microbiology certificate/report with a documented reason and electronic signature."
                    : BuildDisabledWorkflowToolTip(baseReissueAllowed, reissueBlockReason, status, "certificate/report reissue");
            }
            if (TxtCertificateReason != null)
                TxtCertificateReason.IsReadOnly = !hasSample || (!CanCancelPrmCertificate() && !CanIssuePrmCertificate());

            ApplyControlledLegacyReissueUiState();
        }

        private void ApplyControlledLegacyReissueUiState()
        {
            if (!_controlledLegacyReissueRoute ||
                _controlledLegacyReissueCompleted ||
                _selectedSampleId <= 0 ||
                _selectedSampleId != _initialSampleId)
            {
                return;
            }

            DataRow activeCertificate = GetActiveCertificateRow();
            if (activeCertificate == null)
            {
                TxtStatus.Text =
                    $"Controlled legacy reissue route #{_initialLegacyReconciliationId} was loaded, but the targeted certificate is no longer active. Return to Legacy Certificate Evidence Reconciliation and refresh the lifecycle state.";
                return;
            }

            int activeCertificateId = ToInt(activeCertificate, "CertificateID");
            if (activeCertificateId != _initialLegacyCertificateId)
            {
                TxtStatus.Text =
                    $"Controlled legacy reissue route targets {_initialLegacyCertificateNumber}, but a different active certificate is now present. The routed reason was not applied. Return to Legacy Certificate Evidence Reconciliation and refresh.";
                return;
            }

            if (TxtCertificateReason != null && string.IsNullOrWhiteSpace(TxtCertificateReason.Text))
                TxtCertificateReason.Text = _initialControlledReissueReason;

            if (BtnCancelCertificate != null)
            {
                BtnCancelCertificate.IsEnabled = false;
                BtnCancelCertificate.ToolTip =
                    "Standalone cancellation is disabled for a certificate opened from signed legacy reissue reconciliation. Use Reissue Legacy Certificate so cancellation and replacement issuance remain one controlled transaction.";
            }

            if (BtnReissueCertificate != null)
            {
                BtnReissueCertificate.Content = "Reissue Legacy Certificate";
                if (BtnReissueCertificate.IsEnabled)
                {
                    BtnReissueCertificate.ToolTip =
                        $"Complete signed legacy reconciliation #{_initialLegacyReconciliationId}: atomically cancel {_initialLegacyCertificateNumber} and issue its linked replacement with a native immutable snapshot.";
                    TxtStatus.Text =
                        $"Controlled legacy reissue loaded for {_initialLegacyCertificateNumber}. The signed QA reason has been prefilled. Use Reissue Legacy Certificate; do not cancel the legacy certificate separately.";
                }
                else
                {
                    TxtStatus.Text =
                        $"Controlled legacy reissue loaded for {_initialLegacyCertificateNumber}, but Reissue is currently blocked. {BtnReissueCertificate.ToolTip}";
                }
            }
        }

        private static string BuildControlledReissueReason(
            int legacyReconciliationId,
            string legacyCertificateNumber,
            string? signedQaReason)
        {
            string certificateNumber = string.IsNullOrWhiteSpace(legacyCertificateNumber)
                ? "legacy certificate"
                : legacyCertificateNumber.Trim();
            string prefix =
                $"Controlled reissue required per signed Legacy Certificate Evidence Reconciliation #{legacyReconciliationId} for {certificateNumber}.";
            string qaReason = (signedQaReason ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(qaReason))
                return prefix;

            string combined = prefix + " QA reason: " + qaReason;
            return combined.Length <= 500 ? combined : prefix;
        }

        private bool IsCurrentPrmQualityStateApprovable(string overall, out string blockReason)
        {
            blockReason = string.Empty;
            string normalized = (overall ?? string.Empty).Trim();
            bool hasAnyQualityEvent = HasAnyPrmQualityEventMinimal();

            if (hasAnyQualityEvent)
            {
                try
                {
                    int qualityEventState = GetPrmQualityEventState();
                    if (qualityEventState == 1)
                    {
                        blockReason = "Approval blocked: an open PRM Quality Event / Investigation exists for this sample. Continue and complete the investigation first.";
                        return false;
                    }

                    if (qualityEventState == 3)
                    {
                        blockReason = "Approval blocked: closed PRM investigation evidence is incomplete, unsupported, legacy/unversioned, or no longer matches the current result/specification snapshot.";
                        return false;
                    }

                    if (qualityEventState == 2)
                    {
                        blockReason = "Approval blocked: the PRM Quality Event state could not be reconciled.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Warning(
                        "PRM approval UI gate could not verify Quality Event readiness for SampleID=" +
                        _selectedSampleId.ToString(CultureInfo.InvariantCulture) + ". " + ex.Message);
                    blockReason = "Approval blocked: the PRM Quality Event state could not be verified from the controlled database schema.";
                    return false;
                }
            }

            if (normalized.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!normalized.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
            {
                blockReason = "Approval blocked: the overall interpretation is incomplete or still requires technical review.";
                return false;
            }

            if (!hasAnyQualityEvent)
            {
                blockReason = "Approval blocked: the nonconforming result requires a PRM Quality Event / Investigation before approval.";
                return false;
            }

            return true;
        }

        private bool IsCurrentPrmCertificateStateIssuable(string category, string overall, out string blockReason)
        {
            blockReason = string.Empty;
            string normalized = (overall ?? string.Empty).Trim();
            bool hasAnyQualityEvent = HasAnyPrmQualityEventMinimal();

            if (hasAnyQualityEvent)
            {
                try
                {
                    int qualityEventState = GetPrmQualityEventState();
                    if (qualityEventState == 1)
                    {
                        blockReason = "Certificate/report issuance blocked: an open PRM Quality Event / Investigation exists for this sample.";
                        return false;
                    }

                    if (qualityEventState == 3)
                    {
                        blockReason = "Certificate/report issuance blocked: closed PRM investigation evidence is incomplete, unsupported, legacy/unversioned, or no longer matches the current result/specification snapshot.";
                        return false;
                    }

                    if (qualityEventState == 2)
                    {
                        blockReason = "Certificate/report issuance blocked: the PRM Quality Event state could not be reconciled.";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Warning(
                        "PRM certificate UI gate could not verify Quality Event readiness for SampleID=" +
                        _selectedSampleId.ToString(CultureInfo.InvariantCulture) + ". " + ex.Message);
                    blockReason = "Certificate/report issuance blocked: the PRM Quality Event state could not be verified from the controlled database schema.";
                    return false;
                }
            }

            if (normalized.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!normalized.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
            {
                blockReason = "Certificate/report issuance blocked: the final interpretation is incomplete or requires review.";
                return false;
            }

            if (!IsControlledNonReleaseReportCategory(category))
            {
                blockReason = "Certificate of Analysis issuance blocked: Raw Material and Finished Product COAs require a final Conforms interpretation.";
                return false;
            }

            if (!hasAnyQualityEvent)
            {
                blockReason = "Controlled nonconforming report issuance blocked: a completed PRM investigation is required.";
                return false;
            }

            return true;
        }

        private static string BuildDisabledWorkflowToolTip(
            bool baseGateAllowed,
            string qualityBlockReason,
            string status,
            string action)
        {
            if (!baseGateAllowed)
            {
                string currentStatus = string.IsNullOrWhiteSpace(status) ? "No sample selected" : status;
                return action + " is not available at the current workflow status: " + currentStatus + ".";
            }

            return string.IsNullOrWhiteSpace(qualityBlockReason)
                ? action + " is currently blocked by the controlled workflow."
                : qualityBlockReason;
        }


        private async void BtnSearch_Click(object sender, RoutedEventArgs e)
        {
            await LoadSamplesAsync();
        }

        private async void TxtSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await LoadSamplesAsync();
                e.Handled = true;
            }
        }

        private async void BtnChooseProductionResults_Click(object sender, RoutedEventArgs e)
        {
            _selectedResultGroup = "PR";
            ShowResultsView();
            ClearSampleInfo();
            await LoadSamplesAsync();
            TxtStatus.Text = "Production results workflow loaded. Finished Product is included here.";
        }

        private async void BtnChooseRawResults_Click(object sender, RoutedEventArgs e)
        {
            _selectedResultGroup = "RM";
            ShowResultsView();
            ClearSampleInfo();
            await LoadSamplesAsync();
            TxtStatus.Text = "Raw Material results workflow loaded.";
        }

        private async void BtnChooseStabilityResults_Click(object sender, RoutedEventArgs e)
        {
            _selectedResultGroup = "ST";
            ShowResultsView();
            ClearSampleInfo();
            await LoadSamplesAsync();
            TxtStatus.Text = "Stability results workflow loaded.";
        }

        private void BtnBackToType_Click(object sender, RoutedEventArgs e)
        {
            ShowChoiceView();
        }

        private void LoadSamples()
        {
            string sql = @"
SELECT TOP 500
    SampleID,
    SampleNumber,
    SampleCategory,
    CASE WHEN SampleCategory = N'Raw Material' THEN ISNULL(MaterialName, N'') ELSE ISNULL(ProductName, N'') END AS ItemName,
    CASE WHEN SampleCategory = N'Raw Material' THEN COALESCE(NULLIF(ManufacturerLotNo, N''), SupplierLotNo, N'') ELSE ISNULL(BatchNo, N'') END AS LotOrBatch,
    SampleStatus,
    ResultInterpretation,
    ReportStatus
FROM dbo.PRM_Samples
WHERE
    (
        @group = N''
        OR (@group = N'PR' AND SampleCategory IN (N'Production / In-Process', N'Finished Product'))
        OR (@group = N'RM' AND SampleCategory = N'Raw Material')
        OR (@group = N'ST' AND SampleCategory = N'Stability')
    )
    AND
    (@search = N''
       OR SampleNumber LIKE N'%' + @search + N'%'
       OR MaterialName LIKE N'%' + @search + N'%'
       OR ProductName LIKE N'%' + @search + N'%'
       OR ManufacturerLotNo LIKE N'%' + @search + N'%'
       OR SupplierLotNo LIKE N'%' + @search + N'%'
       OR BatchNo LIKE N'%' + @search + N'%')
ORDER BY SampleID DESC;";

            DataTable table = DatabaseHelper.ExecuteQuery(sql, new[]
            {
                new SqlParameter("@search", SqlDbType.NVarChar, 200) { Value = (TxtSearch.Text ?? string.Empty).Trim() },
                new SqlParameter("@group", SqlDbType.NVarChar, 10) { Value = _selectedResultGroup ?? string.Empty }
            });

            DgSamples.ItemsSource = table.DefaultView;
        }

        private async Task LoadSamplesAsync()
        {
            if (_isLoading)
                return;

            string search = (TxtSearch.Text ?? string.Empty).Trim();
            string group = _selectedResultGroup ?? string.Empty;
            _isLoading = true;
            BtnSearch.IsEnabled = false;
            DgSamples.IsEnabled = false;
            TxtStatus.Text = "Loading samples...";

            try
            {
                DataTable table = await Task.Run(() => LoadSamplesTable(search, group));
                DgSamples.ItemsSource = table.DefaultView;
                TxtStatus.Text = table.Rows.Count.ToString(CultureInfo.InvariantCulture) + " sample(s) loaded.";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Loading PRM samples failed.", ex);
                DgSamples.ItemsSource = null;
                TxtStatus.Text = "Samples could not be loaded. Check the database connection and application log.";
                ShowOperationError("Load Samples", ex);
            }
            finally
            {
                BtnSearch.IsEnabled = true;
                DgSamples.IsEnabled = true;
                _isLoading = false;
            }
        }

        private static DataTable LoadSamplesTable(string search, string group)
        {
            const string sql = @"
SELECT TOP 500
    SampleID,
    SampleNumber,
    SampleCategory,
    CASE WHEN SampleCategory = N'Raw Material' THEN ISNULL(MaterialName, N'') ELSE ISNULL(ProductName, N'') END AS ItemName,
    CASE WHEN SampleCategory = N'Raw Material' THEN COALESCE(NULLIF(ManufacturerLotNo, N''), SupplierLotNo, N'') ELSE ISNULL(BatchNo, N'') END AS LotOrBatch,
    SampleStatus,
    ResultInterpretation,
    ReportStatus
FROM dbo.PRM_Samples
WHERE
    (
        @group = N''
        OR (@group = N'PR' AND SampleCategory IN (N'Production / In-Process', N'Finished Product'))
        OR (@group = N'RM' AND SampleCategory = N'Raw Material')
        OR (@group = N'ST' AND SampleCategory = N'Stability')
    )
    AND
    (@search = N''
       OR SampleNumber LIKE N'%' + @search + N'%'
       OR MaterialName LIKE N'%' + @search + N'%'
       OR ProductName LIKE N'%' + @search + N'%'
       OR ManufacturerLotNo LIKE N'%' + @search + N'%'
       OR SupplierLotNo LIKE N'%' + @search + N'%'
       OR BatchNo LIKE N'%' + @search + N'%')
ORDER BY SampleID DESC;";

            return DatabaseHelper.ExecuteQuery(sql, new[]
            {
                new SqlParameter("@search", SqlDbType.NVarChar, 200) { Value = search },
                new SqlParameter("@group", SqlDbType.NVarChar, 10) { Value = group }
            }, commandTimeoutSeconds: 10);
        }

        private string GetResultGroupForSampleId(int sampleId)
        {
            object categoryObj = DatabaseHelper.ExecuteScalar(
                "SELECT SampleCategory FROM dbo.PRM_Samples WHERE SampleID = @SampleID",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId } });

            string category = categoryObj == null || categoryObj == DBNull.Value ? string.Empty : Convert.ToString(categoryObj, CultureInfo.InvariantCulture);
            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
                return "RM";
            if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
                return "ST";
            return "PR";
        }

        private void SelectSampleById(int sampleId)
        {
            LoadSelectedSample(sampleId);

            if (DgSamples.ItemsSource is DataView view)
            {
                foreach (DataRowView row in view)
                {
                    if (ToInt(row, "SampleID") == sampleId)
                    {
                        DgSamples.SelectedItem = row;
                        DgSamples.ScrollIntoView(row);
                        break;
                    }
                }
            }
        }

        private void DgSamples_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || DgSamples.SelectedItem == null)
                return;

            if (DgSamples.SelectedItem is DataRowView row && row.Row.Table.Columns.Contains("SampleID"))
                LoadSelectedSample(ToInt(row, "SampleID"));
        }

        private void LoadSelectedSample(int sampleId)
        {
            DataTable table = DatabaseHelper.ExecuteQuery(
                @"SELECT S.*,COALESCE(NULLIF(LTRIM(RTRIM(U.FullName)),N''),S.SampledBy) AS SampledByDisplay
FROM dbo.PRM_Samples S
LEFT JOIN dbo.Users U ON U.Username=S.SampledBy
WHERE S.SampleID=@SampleID;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId } });

            if (table.Rows.Count == 0)
                return;

            DataRow r = table.Rows[0];
            _selectedSampleId = sampleId;

            TxtSampleNo.Text = S(r, "SampleNumber");
            TxtCategory.Text = S(r, "SampleCategory");
            TxtItemName.Text = GetItemName(r);
            TxtLotBatch.Text = GetLotOrBatch(r);
            TxtSampleStatus.Text = S(r, "SampleStatus");
            TxtOverallInterpretation.Text = string.IsNullOrWhiteSpace(S(r, "ResultInterpretation")) ? "Not Tested" : S(r, "ResultInterpretation");
            TxtReportStatus.Text = string.IsNullOrWhiteSpace(S(r, "ReportStatus")) ? "Not Issued" : S(r, "ReportStatus");
            _analysisStartRecorded = r.Table.Columns.Contains("AnalysisStartedDate") && r["AnalysisStartedDate"] != DBNull.Value;
            _timingReconciliationStatus = r.Table.Columns.Contains("TimingReconciliationStatus")
                ? (S(r, "TimingReconciliationStatus").Trim().Length == 0 ? "Not Required" : S(r, "TimingReconciliationStatus").Trim())
                : "Not Required";
            TxtSelectedSampleHeader.Text = TxtSampleNo.Text + " - " + TxtCategory.Text;
            TxtCertificateReason.Text = string.Empty;

            LoadApprovedTestsForSample(r);
            LoadResultsForSample();
            LoadActiveCertificateNo();
            UpdateQualityEventSummary();
            Dispatcher.BeginInvoke(new Action(EnsureQualityEventButtonExists), System.Windows.Threading.DispatcherPriority.Loaded);
            UpdateQualityEventButtonState();
            UpdateWorkflowControls();
            TxtStatus.Text = "Selected sample " + TxtSampleNo.Text + ".";
        }

        private static string GetItemName(DataRow row)
        {
            string category = S(row, "SampleCategory");
            return category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase) ? S(row, "MaterialName") : S(row, "ProductName");
        }

        private static string GetLotOrBatch(DataRow row)
        {
            string category = S(row, "SampleCategory");
            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
            {
                string manufacturerLot = S(row, "ManufacturerLotNo");
                return string.IsNullOrWhiteSpace(manufacturerLot) ? S(row, "SupplierLotNo") : manufacturerLot;
            }
            return S(row, "BatchNo");
        }

        private void ClearSampleInfo()
        {
            _selectedSampleId = 0;
            TxtSampleNo.Text = string.Empty;
            TxtCategory.Text = string.Empty;
            TxtItemName.Text = string.Empty;
            TxtLotBatch.Text = string.Empty;
            TxtSampleStatus.Text = string.Empty;
            TxtOverallInterpretation.Text = string.Empty;
            TxtReportStatus.Text = string.Empty;
            TxtCertificateNo.Text = string.Empty;
            TxtQualityEventNo.Text = "None";
            TxtInvestigationStatus.Text = "Not Required";
            TxtInvestigationDisposition.Text = "—";
            _analysisStartRecorded = false;
            _timingReconciliationStatus = "Not Required";
            TxtSelectedSampleHeader.Text = "No sample selected";
            DgResults.ItemsSource = null;
            UpdateQualityEventButtonState();
            UpdateWorkflowControls();
        }


        private void LoadResultsForSample()
        {
            if (_selectedSampleId <= 0)
            {
                DgResults.ItemsSource = null;
                return;
            }

            string sql = @"
SELECT
    SampleTestID,
    SampleID,
    TestName,
    SpecificationText,
    Unit,
    ResultValue,
    ResultType,
    TestCode,
    SpecificationLimit,
    ISNULL(RequiredTest, 1) AS RequiredTest,
    MinimumElapsedHours,
    Interpretation,
    Remarks,
    EnteredBy,
    EnteredDate,
    CONVERT(NVARCHAR(20), EnteredDate, 120) AS EnteredDateText,
    ISNULL(SortOrder, SampleTestID) AS SortOrder
FROM dbo.PRM_SampleTests
WHERE SampleID = @SampleID
ORDER BY ISNULL(SortOrder, SampleTestID), SampleTestID;";

            _resultsTable = DatabaseHelper.ExecuteQuery(sql, new[]
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId }
            });

            DgResults.ItemsSource = _resultsTable.DefaultView;
            UpdateWorkflowControls();
            TxtStatus.Text = "Loaded " + _resultsTable.Rows.Count + " microbiology test(s).";
        }

        private void BtnReloadTests_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                DataRow sample = GetCurrentSampleRow();
                if (!CanEnterPrmResults())
                    throw new InvalidOperationException("You do not have permission to load approved PRM tests.");

                if (!IsResultEntryStatus(GetCurrentSampleStatus()))
                    throw new InvalidOperationException("Tests cannot be changed after the sample is submitted for review.");
                EnsurePrmTimingResultEntryAllowed("Reload Missing Tests", allowRequiredReentry: false);

                LoadApprovedTestsForSample(sample, true);
                LoadResultsForSample();
                TxtStatus.Text = "Compatible specification tests were loaded.";
            }
            catch (Exception ex)
            {
                ShowOperationError("Reload Missing Tests", ex);
            }
        }

        private void LoadApprovedTestsForSample(DataRow sample, bool requireConfiguredSpecification = false)
        {
            if (sample == null || _selectedSampleId <= 0)
                return;

            // Selecting or viewing a historical sample must be strictly read-only.
            // Test assignment is permitted only through the explicit Reload Missing Tests action.
            if (!requireConfiguredSpecification)
                return;

            int assignedTests = _prmSpecificationRepository.EnsureSampleTestsAssigned(_selectedSampleId);
            if (assignedTests == 0 && requireConfiguredSpecification)
            {
                string category = S(sample, "SampleCategory").Trim();
                throw new InvalidOperationException(
                    "No approved inspection profile is selected for category " + category +
                    ". Approve an item/stage-specific profile in Specification Master, then select it in sample registration.");
            }
        }

        private void BtnStartAnalysis_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryRecordPrmAnalysisStart(false))
                    return;

                MessageBox.Show(
                    "Analysis start date/time recorded successfully.",
                    "Start Analysis",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Start Analysis", ex);
            }
        }

        private bool EnsureAnalysisStartedForResultSave()
        {
            if (_analysisStartRecorded)
                return true;

            DataRow sample = GetCurrentSampleRow();
            EnsurePrmTimingResultEntryAllowed("Analysis Start", allowRequiredReentry: true);
            if (sample.Table.Columns.Contains("AnalysisStartedDate") && sample["AnalysisStartedDate"] != DBNull.Value)
            {
                _analysisStartRecorded = true;
                UpdateWorkflowControls();
                return true;
            }

            MessageBoxResult continueResult = MessageBox.Show(
                "Analysis start date/time is not recorded.\n\n" +
                "PharmaLIMS will record the actual Analysis Start date/time with a separate electronic signature before saving these results.\n\n" +
                "Continue?",
                "Analysis Start Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (continueResult != MessageBoxResult.Yes)
                return false;

            return TryRecordPrmAnalysisStart(true);
        }

        private bool TryRecordPrmAnalysisStart(bool invokedFromResultSave)
        {
            RequireSample();
            if (!CanEnterPrmResults())
                throw new InvalidOperationException("You do not have permission to start PRM analysis.");

            DataRow sample = GetCurrentSampleRow();
            if (sample.Table.Columns.Contains("AnalysisStartedDate") && sample["AnalysisStartedDate"] != DBNull.Value)
            {
                _analysisStartRecorded = true;
                UpdateWorkflowControls();
                return true;
            }

            string status = S(sample, "SampleStatus").Trim();
            if (!IsOneOf(status, "Registered", "In Progress", "Results Entered"))
            {
                throw new InvalidOperationException(
                    "Analysis Start is allowed only for Registered / In Progress / Results Entered samples.");
            }

            DateTime analysisStartedAt = default;

            ElectronicSignature signature = RequestPrmSignature("PRM Analysis Start");
            if (signature == null)
                return false;

            bool recordedNow = false;
            string resultingStatus = status;

            DatabaseHelper.ExecuteInTransaction((conn, tx) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    conn,
                    tx,
                    signature.SignedBy,
                    "CanEnterResults",
                    "start PRM analysis");
                EnsurePrmTimingResultEntryAllowedInTransaction(conn, tx, "Analysis Start", allowRequiredReentry: true);

                object lockedStatusValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT LTRIM(RTRIM(ISNULL(SampleStatus, N'')))
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

                if (lockedStatusValue == null || lockedStatusValue == DBNull.Value)
                    throw new InvalidOperationException("The selected PRM sample was not found.");

                string lockedStatus = Convert.ToString(lockedStatusValue, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                if (!IsOneOf(lockedStatus, "Registered", "In Progress", "Results Entered"))
                {
                    throw new InvalidOperationException(
                        "The sample status changed before Analysis Start could be recorded. Reload and try again.");
                }

                object existingStart = ExecuteScalarInTransaction(conn, tx, @"
SELECT AnalysisStartedDate
FROM dbo.PRM_Samples
WHERE SampleID = @SampleID;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

                if (existingStart != null && existingStart != DBNull.Value)
                {
                    resultingStatus = lockedStatus;
                    return;
                }

                object sampleDateValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT SampleDateTime
FROM dbo.PRM_Samples
WHERE SampleID = @SampleID;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

                if (sampleDateValue == null || sampleDateValue == DBNull.Value)
                {
                    throw new InvalidOperationException(
                        "Sample Date / Time is missing. Correct the PRM registration before starting analysis.");
                }

                DateTime sampleDateTime = Convert.ToDateTime(sampleDateValue, CultureInfo.InvariantCulture);
                DateTime databaseNow = Convert.ToDateTime(
                    ExecuteScalarInTransaction(conn, tx, "SELECT SYSDATETIME();"),
                    CultureInfo.InvariantCulture);

                if (databaseNow < sampleDateTime)
                    throw new InvalidOperationException(
                        "SQL Server time is earlier than Sample Date / Time. Correct the authoritative database clock or the controlled sample registration before starting analysis.");

                // Production and Development both record the actual authoritative SQL Server time.
                // A user-selectable/backdated Analysis Start is intentionally not supported.
                analysisStartedAt = databaseNow;

                resultingStatus = lockedStatus.Equals("Registered", StringComparison.OrdinalIgnoreCase)
                    ? "In Progress"
                    : lockedStatus;

                int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Samples
SET AnalysisStartedDate = @AnalysisStartedDate,
    AnalysisCompletedDate = CASE
        WHEN SampleStatus = N'Results Entered' THEN AnalysisCompletedDate
        ELSE NULL
    END,
    SampleStatus = @NewStatus,
    ModifiedBy = @ModifiedBy,
    ModifiedDate = SYSDATETIME()
WHERE SampleID = @SampleID
  AND AnalysisStartedDate IS NULL
  AND SampleStatus = @ExpectedStatus;",
                    new[]
                    {
                        new SqlParameter("@AnalysisStartedDate", SqlDbType.DateTime2) { Value = analysisStartedAt },
                        new SqlParameter("@NewStatus", SqlDbType.NVarChar, 60) { Value = resultingStatus },
                        new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@ExpectedStatus", SqlDbType.NVarChar, 60) { Value = lockedStatus }
                    }, conn, tx);

                if (affected != 1)
                    throw new InvalidOperationException("Analysis Start could not be stored. No PRM workflow change was committed.");

                AddPrmElectronicSignatureInTransaction(conn, tx, "Analysis Start", signature);
                DatabaseHelper.AddAuditTrailAdvanced(
                    conn,
                    tx,
                    "PRM_Samples",
                    _selectedSampleId,
                    "Analysis Start",
                    "AnalysisStartedDate=NULL; SampleStatus=" + lockedStatus,
                    "AnalysisStartedDate=" + analysisStartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    "; SampleStatus=" + resultingStatus,
                    signature.Reason,
                    signature.SignedBy,
                    "AnalysisStartedDate",
                    null,
                    TxtSampleNo?.Text,
                    "PRM");

                recordedNow = true;
            });

            _analysisStartRecorded = true;
            TxtSampleStatus.Text = resultingStatus;
            UpdateWorkflowControls();

            if (recordedNow)
            {
                TxtStatus.Text = "Analysis started at " +
                    analysisStartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                    (invokedFromResultSave ? "; continuing Save Results." : ".");
            }

            return true;
        }

        private void BtnTimingReconciliation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanApprovePrmResults())
                    throw new InvalidOperationException("QA approval permission is required to reconcile PRM timing evidence.");

                string status = GetCurrentSampleStatus();
                if (!status.Equals("Results Entered", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Timing reconciliation is allowed only after affected results have been re-entered and the sample is in Results Entered status.");

                ElectronicSignature signature = RequestPrmSignature("PRM Timing Reconciliation");
                if (signature == null)
                    return;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn, tx, signature.SignedBy, "CanApproveResults", "reconcile PRM timing evidence");

                    string lockedStatus;
                    string reconciliationStatus;
                    DateTime? analysisStartedAt;
                    using (SqlCommand command = new SqlCommand(@"
SELECT
    LTRIM(RTRIM(ISNULL(SampleStatus,N''))),
    LTRIM(RTRIM(ISNULL(TimingReconciliationStatus,N'Not Required'))),
    AnalysisStartedDate
FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID;", conn, tx))
                    {
                        command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        command.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                        using SqlDataReader reader = command.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected PRM sample no longer exists.");
                        lockedStatus = reader.GetString(0).Trim();
                        reconciliationStatus = reader.GetString(1).Trim();
                        analysisStartedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                    }

                    if (!lockedStatus.Equals("Results Entered", StringComparison.OrdinalIgnoreCase))
                        throw new DBConcurrencyException("The PRM sample status changed before timing reconciliation. Reload and try again.");
                    if (!reconciliationStatus.Equals("Required", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("This sample does not require PRM timing reconciliation.");
                    if (!analysisStartedAt.HasValue)
                        throw new InvalidOperationException("Analysis Start is missing. Re-enter results only after a controlled Analysis Start has been recorded.");

                    object sameEntrant = ExecuteScalarInTransaction(conn, tx, @"
SELECT COUNT(1)
FROM dbo.PRM_SampleTests WITH(UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
  AND ISNULL(RequiredTest,1)=1
  AND NULLIF(LTRIM(RTRIM(ISNULL(ResultValue,N''))),N'') IS NOT NULL
  AND UPPER(LTRIM(RTRIM(ISNULL(EnteredBy,N''))))=UPPER(LTRIM(RTRIM(@SignedBy)));",
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@SignedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy });
                    if (!IsAdminWorkflowOverrideAllowed() && Convert.ToInt32(sameEntrant, CultureInfo.InvariantCulture) > 0)
                        throw new InvalidOperationException("Timing reconciliation must be performed by QA independently from the analyst who entered the affected PRM results.");

                    object incomplete = ExecuteScalarInTransaction(conn, tx, @"
SELECT COUNT(1)
FROM dbo.PRM_SampleTests WITH(UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
  AND ISNULL(RequiredTest,1)=1
  AND NULLIF(LTRIM(RTRIM(ISNULL(ResultValue,N''))),N'') IS NULL;",
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });
                    if (Convert.ToInt32(incomplete, CultureInfo.InvariantCulture) > 0)
                        throw new InvalidOperationException("All required PRM results must be completed before timing reconciliation.");

                    object invalidEvidence = ExecuteScalarInTransaction(conn, tx, @"
SELECT COUNT(1)
FROM dbo.PRM_SampleTests t WITH(UPDLOCK,HOLDLOCK)
WHERE t.SampleID=@SampleID
  AND ISNULL(t.RequiredTest,1)=1
  AND (
        t.MinimumElapsedHours IS NULL
        OR t.EnteredDate IS NULL
        OR t.EnteredDate < DATEADD(SECOND,
            CONVERT(INT, CEILING(CONVERT(DECIMAL(18,4),t.MinimumElapsedHours) * 3600.0)),
            @AnalysisStartedDate)
      );",
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@AnalysisStartedDate", SqlDbType.DateTime2) { Value = analysisStartedAt.Value });
                    if (Convert.ToInt32(invalidEvidence, CultureInfo.InvariantCulture) > 0)
                    {
                        throw new InvalidOperationException(
                            "Timing reconciliation is still blocked. Re-enter every affected result after its frozen Minimum Elapsed Hours eligibility time, then retry QA reconciliation.");
                    }

                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Samples
SET TimingReconciliationStatus=N'Reconciled',
    TimingReconciledBy=@ReconciledBy,
    TimingReconciledAt=SYSDATETIME(),
    TimingReconciliationReason=@Reason,
    ModifiedBy=@ReconciledBy,
    ModifiedDate=SYSDATETIME()
WHERE SampleID=@SampleID
  AND SampleStatus=N'Results Entered'
  AND TimingReconciliationStatus=N'Required';",
                        new[]
                        {
                            new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                            new SqlParameter("@ReconciledBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                            new SqlParameter("@Reason", SqlDbType.NVarChar, 1000) { Value = signature.Reason }
                        }, conn, tx);
                    if (affected != 1)
                        throw new DBConcurrencyException("Timing reconciliation state changed before QA confirmation. Reload and try again.");

                    AddPrmElectronicSignatureInTransaction(conn, tx, "Timing Reconciliation", signature, signerRole);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn, tx, "PRM_Samples", _selectedSampleId,
                        "PRM Timing Reconciliation", "Required", "Reconciled", signature.Reason,
                        signature.SignedBy, "TimingReconciliationStatus", null,
                        TxtSampleNo?.Text, "PRM");
                });

                _timingReconciliationStatus = "Reconciled";
                RefreshSelectedSampleLight();
                UpdateWorkflowControls();
                MessageBox.Show(
                    "PRM timing evidence reconciled by QA. The sample may now proceed through the normal Submit Review workflow.",
                    "Timing Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Timing Reconciliation", ex);
            }
        }

        private void BtnSaveResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanEnterPrmResults())
                    throw new InvalidOperationException("You do not have permission to enter or edit PRM results.");

                CommitGridEdit();
                if (!EnsureAnalysisStartedForResultSave())
                    return;

                string status = GetCurrentSampleStatus();
                if (!IsResultEntryStatus(status))
                    throw new InvalidOperationException("PRM results are locked after submission for review. Current status: " + status + ".");

                EnsureTestsAssigned();
                EnsurePrmTimingResultEntryAllowed("Save Results", allowRequiredReentry: true);
                ValidatePrmNumericResultFormatsBeforeSignature();

                ElectronicSignature signature = RequestPrmSignature("PRM Result Entry");
                if (signature == null)
                    return;

                string overall = "Not Tested";
                string newStatus = "In Progress";

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn, tx, signature.SignedBy, "CanEnterResults", "save PRM results");
                    EnsurePrmTimingResultEntryAllowedInTransaction(conn, tx, "Save Results", allowRequiredReentry: true);

                    // Lock and compare the complete result snapshot, not only rows edited
                    // in this window. A different-row change in another window must
                    // invalidate this save before any stale sample summary is written.
                    PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                        conn, tx, _selectedSampleId, _resultsTable);

                    SaveResultsFromGrid(conn, tx, signature);

                    PrmAuthoritativeSampleResultState authoritative =
                        PrmSampleResultStateService.ReadAuthoritativeStateForUpdate(conn, tx, _selectedSampleId);
                    PrmSampleResultStateService.EnsurePersistedInterpretationsMatchEvidence(authoritative, "Save Results");
                    overall = authoritative.OverallInterpretation;
                    newStatus = authoritative.AllRequiredResultsEntered ? "Results Entered" : "In Progress";

                    int affected = UpdatePrmSampleInTransaction(conn, tx, newStatus, overall, null, status);
                    if (affected != 1)
                        throw new InvalidOperationException("The sample status changed while results were being saved. No changes were committed.");
                    if (newStatus.Equals("Results Entered", StringComparison.OrdinalIgnoreCase))
                    {
                        int completed = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Samples
SET AnalysisCompletedDate = COALESCE(AnalysisCompletedDate, SYSDATETIME()),
    ModifiedBy = @ModifiedBy,
    ModifiedDate = SYSDATETIME()
WHERE SampleID = @SampleID
  AND AnalysisStartedDate IS NOT NULL;",
                            new[]
                            {
                                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                                new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy }
                            }, conn, tx);
                        if (completed != 1)
                            throw new InvalidOperationException("Analysis start date/time is missing. Use Start Analysis before completing results.");
                    }
                    AddPrmElectronicSignatureInTransaction(conn, tx, "Result Entry", signature, signerRole);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn, tx, "PRM_Samples", _selectedSampleId,
                        "Result Entry", status, newStatus, signature.Reason,
                        signature.SignedBy, "SampleStatus", null,
                        TxtSampleNo?.Text, "PRM");
                });

                AcceptResultChanges();
                RefreshSelectedSampleLight();
                MessageBox.Show("Results saved. Overall interpretation: " + overall + ".", "Save Results", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Save Results", ex);
            }
        }

        private void BtnSubmitReview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanSubmitPrmResults())
                    throw new InvalidOperationException("You do not have permission to submit PRM results for review.");

                string status = GetCurrentSampleStatus();
                if (!IsOneOf(status, "Results Entered"))
                    throw new InvalidOperationException("Only samples with Results Entered status can be submitted for review.");

                CommitGridEdit();
                EnsureTestsAssigned();

                string overall = UpdateOverallInterpretation();
                if (HasPendingResultChanges())
                    throw new InvalidOperationException(
                        "Unsaved result or derived interpretation changes exist. Use Save Results with an electronic signature before submitting for review.");

                if (!AreAllRequiredResultsEntered())
                    throw new InvalidOperationException("All required test results must be entered before Submit Review.");

                EnsureWorkflowInterpretationIsComplete(overall, "Submit Review");
                ElectronicSignature signature = RequestPrmSignature("PRM Submit for Review");
                if (signature == null)
                    return;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn, tx, signature.SignedBy, "CanEnterResults", "submit PRM results for review");
                    EnsurePrmTimingReconciliationClearedInTransaction(conn, tx, "Submit Review");
                    EnsureAllPrmResultTimingGatesElapsedInTransaction(conn, tx, signature, "Submit Review");

                    DataTable authoritativeRows = PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                        conn, tx, _selectedSampleId, _resultsTable);
                    PrmAuthoritativeSampleResultState authoritative =
                        PrmSampleResultStateService.DeriveAuthoritativeState(authoritativeRows);
                    if (!authoritative.AllRequiredResultsEntered)
                        throw new InvalidOperationException("All required test results must be entered before Submit Review.");
                    PrmSampleResultStateService.EnsurePersistedInterpretationsMatchEvidence(authoritative, "Submit Review");
                    EnsureWorkflowInterpretationIsComplete(authoritative.OverallInterpretation, "Submit Review");

                    int affected = UpdatePrmSampleInTransaction(
                        conn, tx, "Under Review", authoritative.OverallInterpretation, null, status);
                    if (affected != 1)
                        throw new InvalidOperationException("The sample status changed before submission. Reload and try again.");
                    AddPrmElectronicSignatureInTransaction(conn, tx, "Submit Review", signature, signerRole);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn, tx, "PRM_Samples", _selectedSampleId,
                        "Submit Review", status, "Under Review", signature.Reason,
                        signature.SignedBy, "SampleStatus", null,
                        TxtSampleNo?.Text, "PRM");
                });

                RefreshSelectedSampleLight();
                MessageBox.Show("Results submitted for review.", "Submit Review", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Submit Review", ex);
            }
        }

        private void BtnReview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanReviewPrmResults())
                    throw new InvalidOperationException("You do not have permission to review PRM results.");

                string status = GetCurrentSampleStatus();
                if (!IsOneOf(status, "Under Review"))
                    throw new InvalidOperationException("Review is allowed only when sample status is Under Review.");

                if (!IsAdminWorkflowOverrideAllowed() && HasCurrentUserSignedPrmAction("Result Entry", "Submit Review"))
                    throw new InvalidOperationException("The user who entered or submitted these results cannot perform the technical review.");

                string localOverall = UpdateOverallInterpretation();
                EnsureWorkflowInterpretationIsComplete(localOverall, "Technical Review");

                ElectronicSignature signature = RequestPrmSignature("PRM Technical Review");
                if (signature == null)
                    return;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn,
                        tx,
                        signature.SignedBy,
                        "CanReviewResults",
                        "review PRM results");

                    string lockedStatus = GetLockedPrmSampleStatusInTransaction(conn, tx);
                    if (!lockedStatus.Equals("Under Review", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The PRM sample status changed while the electronic signature was being completed. " +
                            "Review was cancelled. Current status: " + lockedStatus + ".");
                    }

                    EnsurePrmTimingReconciliationClearedInTransaction(conn, tx, "Technical Review");

                    if (!IsAdminWorkflowOverrideAllowed() &&
                        HasSignerPerformedPrmActionInTransaction(conn, tx, signature.SignedBy, "Result Entry", "Submit Review"))
                    {
                        throw new InvalidOperationException(
                            "The user who entered or submitted these results cannot perform the technical review.");
                    }

                    DataTable authoritativeRows = PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                        conn, tx, _selectedSampleId, _resultsTable);
                    PrmAuthoritativeSampleResultState authoritative =
                        PrmSampleResultStateService.DeriveAuthoritativeState(authoritativeRows);
                    if (!authoritative.AllRequiredResultsEntered)
                        throw new InvalidOperationException("Technical Review is blocked because one or more required PRM tests are incomplete.");
                    PrmSampleResultStateService.EnsurePersistedInterpretationsMatchEvidence(authoritative, "Technical Review");
                    EnsureWorkflowInterpretationIsComplete(authoritative.OverallInterpretation, "Technical Review");

                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Samples
SET SampleStatus = N'Reviewed',
    ResultInterpretation = @ResultInterpretation,
    ReviewedBy = @ReviewedBy,
    ReviewedDate = SYSDATETIME(),
    ModifiedBy = @ModifiedBy,
    ModifiedDate = SYSDATETIME()
WHERE SampleID = @SampleID
  AND SampleStatus = N'Under Review';",
                    new[]
                    {
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@ResultInterpretation", SqlDbType.NVarChar, 60) { Value = authoritative.OverallInterpretation },
                        new SqlParameter("@ReviewedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                        new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy }
                    }, conn, tx);

                    if (affected != 1)
                        throw new InvalidOperationException("The sample is no longer Under Review. No review was committed.");

                    AddPrmElectronicSignatureInTransaction(conn, tx, "Review", signature, signerRole);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn,
                        tx,
                        "PRM_Samples",
                        _selectedSampleId,
                        "Technical Review",
                        lockedStatus,
                        "Reviewed",
                        signature.Reason,
                        signature.SignedBy,
                        "SampleStatus",
                        null,
                        TxtSampleNo?.Text,
                        "PRM");
                });

                RefreshSelectedSample();
                MessageBox.Show("Sample results reviewed and review details recorded.", "Review", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Review", ex);
            }
        }

        private async void BtnApprove_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanApprovePrmResults())
                    throw new InvalidOperationException("You do not have permission to approve PRM results.");

                string status = GetCurrentSampleStatus();
                if (!IsOneOf(status, "Reviewed"))
                    throw new InvalidOperationException("Approve is allowed only after Review.");

                if (!IsAdminWorkflowOverrideAllowed() && HasCurrentUserSignedPrmAction("Result Entry", "Submit Review", "Review"))
                    throw new InvalidOperationException("The result entrant or technical reviewer cannot perform QA approval for the same PRM sample.");

                string overall = UpdateOverallInterpretation();
                EnsureWorkflowInterpretationIsComplete(overall, "QA Approval");

                // Approval must prove the controlled Quality Event schema is available even
                // for Conforms; schema drift must never be interpreted as "no event".
                await EnsurePrmQualityEventSchemaReadyForActionAsync();

                bool isNonconforming = overall.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase);
                bool hasAnyQualityEvent = HasAnyPrmQualityEventMinimal();
                int qualityEventState = 0;

                if (isNonconforming || hasAnyQualityEvent)
                {
                    qualityEventState = GetPrmQualityEventState();

                    if (qualityEventState == 1)
                        throw new InvalidOperationException("Approval is blocked because an open PRM Quality Event exists for this sample. Complete and close the investigation before approval.");

                    if (qualityEventState == 2 && isNonconforming)
                        throw new InvalidOperationException("Approval is blocked because at least one test is Does Not Conform. Open a Quality Event / Investigation first.");

                    if (qualityEventState == 3)
                        throw new InvalidOperationException("Approval is blocked because the closed PRM investigation is incomplete, has an unsupported disposition, or its affected-result evidence no longer matches the current result/specification snapshot.");
                }

                ElectronicSignature signature = RequestPrmSignature("PRM QA Approval");
                if (signature == null)
                    return;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn,
                        tx,
                        signature.SignedBy,
                        "CanApproveResults",
                        "approve PRM results");

                    string lockedStatus = GetLockedPrmSampleStatusInTransaction(conn, tx);
                    if (!lockedStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "The PRM sample status changed while the electronic signature was being completed. " +
                            "Approval was cancelled. Current status: " + lockedStatus + ".");
                    }

                    EnsurePrmTimingReconciliationClearedInTransaction(conn, tx, "QA Approval");

                    if (!IsAdminWorkflowOverrideAllowed() &&
                        HasSignerPerformedPrmActionInTransaction(
                            conn,
                            tx,
                            signature.SignedBy,
                            "Result Entry",
                            "Submit Review",
                            "Review"))
                    {
                        throw new InvalidOperationException(
                            "The result entrant or technical reviewer cannot perform QA approval for the same PRM sample.");
                    }

                    DataTable authoritativeRows = PrmSampleResultStateService.LockAndValidateLoadedSnapshot(
                        conn, tx, _selectedSampleId, _resultsTable);
                    PrmAuthoritativeSampleResultState authoritative =
                        PrmSampleResultStateService.DeriveAuthoritativeState(authoritativeRows);
                    if (!authoritative.AllRequiredResultsEntered)
                        throw new InvalidOperationException("QA Approval is blocked because one or more required PRM tests are incomplete.");
                    PrmSampleResultStateService.EnsurePersistedInterpretationsMatchEvidence(authoritative, "QA Approval");

                    string persistedInterpretation = authoritative.OverallInterpretation;
                    EnsureWorkflowInterpretationIsComplete(persistedInterpretation, "QA Approval");

                    bool persistedNonconforming = persistedInterpretation.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase);
                    bool hasAnyPersistedQualityEvent = HasAnyPrmQualityEventMinimalInTransaction(conn, tx);
                    if (persistedNonconforming || hasAnyPersistedQualityEvent)
                    {
                        int persistedQualityEventState = GetPrmQualityEventStateInTransaction(conn, tx);
                        if (persistedQualityEventState == 1)
                        {
                            throw new InvalidOperationException(
                                "Approval is blocked because an open PRM Quality Event exists for this sample.");
                        }

                        if (persistedQualityEventState == 2 && persistedNonconforming)
                        {
                            throw new InvalidOperationException(
                                "Approval is blocked because at least one test is Does Not Conform. " +
                                "A completed PRM Quality Event / Investigation is required before approval.");
                        }

                        if (persistedQualityEventState == 3)
                        {
                            throw new InvalidOperationException(
                                "Approval is blocked because the closed PRM investigation is incomplete, has an unsupported disposition, or its affected-result evidence no longer matches the current result/specification snapshot.");
                        }
                    }

                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Samples
SET SampleStatus = N'Approved',
    ResultInterpretation = @ResultInterpretation,
    ReportStatus = N'Not Issued',
    ApprovedBy = @ApprovedBy,
    ApprovedDate = SYSDATETIME(),
    ModifiedBy = @ModifiedBy,
    ModifiedDate = SYSDATETIME()
WHERE SampleID = @SampleID
  AND SampleStatus = N'Reviewed';",
                    new[]
                    {
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@ResultInterpretation", SqlDbType.NVarChar, 60) { Value = persistedInterpretation },
                        new SqlParameter("@ApprovedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                        new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy }
                    }, conn, tx);

                    if (affected != 1)
                        throw new InvalidOperationException("The sample is no longer Reviewed. No approval was committed.");

                    AddPrmElectronicSignatureInTransaction(conn, tx, "Approval", signature, signerRole);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn,
                        tx,
                        "PRM_Samples",
                        _selectedSampleId,
                        "QA Approval",
                        lockedStatus,
                        "Approved",
                        signature.Reason,
                        signature.SignedBy,
                        "SampleStatus",
                        null,
                        TxtSampleNo?.Text,
                        "PRM");
                });

                RefreshSelectedSample();
                MessageBox.Show("Sample results approved and approval details recorded. Certificate / report can now be issued.", "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Approve", ex);
            }
        }


        private async void BtnIssueCertificate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanIssuePrmCertificate())
                    throw new InvalidOperationException("You do not have permission to issue PRM certificates/reports.");

                string status = GetCurrentSampleStatus();
                if (!IsOneOf(status, "Approved", "Certificate Issued"))
                    throw new InvalidOperationException("Certificate / report can be issued only after approval.");

                if (HasActiveCertificate())
                    throw new InvalidOperationException("An active certificate / report already exists. Use Print, Cancel, or Reissue.");

                await EnsurePrmCertificateSchemaReadyForActionAsync();
                await EnsurePrmQualityEventSchemaReadyForActionAsync();
                string overall = UpdateOverallInterpretation();
                EnsureCertificateInterpretationIsIssuable(overall);

                ElectronicSignature signature = RequestPrmSignature("PRM Certificate Issuance");
                if (signature == null)
                    return;

                string certNo = IssueCertificate(false, string.Empty, 0, signature);
                RefreshSelectedSample();
                MessageBox.Show("Certificate / report issued: " + certNo, "Issue Certificate", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Issue Certificate", ex);
            }
        }

        private void BtnPrintCertificate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                DataRow cert = GetActiveCertificateRow();
                if (cert == null)
                    throw new InvalidOperationException("No active certificate / report found for this sample.");

                if (!DatabaseHelper.CanAccessReports(GetCurrentUserDisplayName()) && !CanIssuePrmCertificate())
                    throw new InvalidOperationException("You do not have permission to view or print PRM certificates/reports.");

                string certificateNumber = S(cert, "CertificateNumber");
                string html = LoadPrmCertificateSnapshotHtml(ToInt(cert, "CertificateID"), out string integrityMessage);
                if (string.IsNullOrWhiteSpace(html))
                {
                    if (AppConfig.IsProduction)
                        throw new InvalidOperationException(integrityMessage + " Cancel and reissue the document to create a controlled immutable snapshot.");

                    html = PRMCertificateTemplate.Build(GetCurrentSampleRow(), GetResultsTable(), cert, GetPrmElectronicSignatures());
                    html = html.Replace("<body>", "<body><div style='background:#fff3cd;border:2px solid #b45309;padding:8px;text-align:center;font-family:Arial;font-weight:bold'>LEGACY DEVELOPMENT PREVIEW - IMMUTABLE ISSUE SNAPSHOT UNAVAILABLE</div>", StringComparison.OrdinalIgnoreCase);
                }

                DatabaseHelper.AddAuditTrailAdvanced(
                    "PRM_Certificates",
                    ToInt(cert, "CertificateID"),
                    "PRM Certificate Open Requested",
                    "Active",
                    "Active",
                    integrityMessage,
                    GetCurrentUserDisplayName(),
                    "CertificateStatus",
                    null,
                    certificateNumber,
                    "PRM Certificate");

                Infrastructure.ControlledTempFiles.WriteHtmlAndOpen(
                    "PharmaLIMS_PRM_Certificate_" + PRMCertificateTemplate.SafeFileName(certificateNumber),
                    html);
                DatabaseHelper.AddAuditTrailAdvanced(
                    "PRM_Certificates",
                    ToInt(cert, "CertificateID"),
                    "PRM Certificate Open Completed",
                    "Requested",
                    "Opened",
                    "Controlled certificate content opened from the immutable issue snapshot.",
                    GetCurrentUserDisplayName(),
                    "CertificateStatus",
                    null,
                    certificateNumber,
                    "PRM Certificate");
                TxtStatus.Text = "Certificate opened: " + certificateNumber + ".";
            }
            catch (Exception ex)
            {
                ShowOperationError("Print Certificate", ex);
            }
        }

        private void BtnPreviewCurrentLayout_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                DataRow cert = GetActiveCertificateRow();
                if (cert == null)
                    throw new InvalidOperationException("No active certificate / report found for this sample.");

                if (!DatabaseHelper.CanAccessReports(GetCurrentUserDisplayName()) && !CanIssuePrmCertificate())
                    throw new InvalidOperationException("You do not have permission to preview PRM certificate/report layouts.");

                string certificateNumber = S(cert, "CertificateNumber");
                string html = PRMCertificateTemplate.Build(GetCurrentSampleRow(), GetResultsTable(), cert, GetPrmElectronicSignatures());
                const string previewBanner = "<div style='position:fixed;z-index:9999;left:8mm;right:8mm;top:3mm;background:#FFF7ED;border:2px solid #D97706;color:#9A3412;padding:7px;text-align:center;font-family:Arial;font-weight:900'>PREVIEW - CURRENT TEMPLATE LAYOUT - NOT A CONTROLLED RECORD - ISSUED SNAPSHOT UNCHANGED</div>";
                const string previewWatermark = "<div style='position:fixed;z-index:9998;left:0;right:0;top:120mm;text-align:center;font-family:Arial;font-size:62px;font-weight:900;color:#B91C1C;opacity:.12;transform:rotate(-24deg);pointer-events:none'>PREVIEW - NOT CONTROLLED</div>";
                html = html.Replace("<body>", "<body>" + previewBanner + previewWatermark, StringComparison.OrdinalIgnoreCase);

                DatabaseHelper.AddAuditTrailAdvanced(
                    "PRM_Certificates",
                    ToInt(cert, "CertificateID"),
                    "PRM Current Template Preview Opened",
                    "Active",
                    "Previewed",
                    "Current layout preview opened with a permanent PREVIEW watermark; immutable issued snapshot was not modified.",
                    GetCurrentUserDisplayName(),
                    "CertificateStatus",
                    null,
                    certificateNumber,
                    "PRM Certificate");

                Infrastructure.ControlledTempFiles.WriteHtmlAndOpen(
                    "PharmaLIMS_PRM_TEMPLATE_PREVIEW_" + PRMCertificateTemplate.SafeFileName(certificateNumber),
                    html);
                TxtStatus.Text = "Current template preview opened. The issued snapshot remains unchanged.";
            }
            catch (Exception ex)
            {
                ShowOperationError("Preview Current Layout", ex);
            }
        }

        private void BtnCancelCertificate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                DataRow cert = GetActiveCertificateRow();
                if (cert == null)
                    throw new InvalidOperationException("No active certificate / report found to cancel.");

                if (!CanCancelPrmCertificate())
                    throw new InvalidOperationException("You do not have permission to cancel PRM certificates/reports.");

                if (_controlledLegacyReissueRoute &&
                    !_controlledLegacyReissueCompleted &&
                    _selectedSampleId == _initialSampleId &&
                    ToInt(cert, "CertificateID") == _initialLegacyCertificateId)
                {
                    throw new InvalidOperationException(
                        "This certificate was opened from a signed legacy reissue reconciliation. Standalone cancellation is disabled because it can leave the lifecycle incomplete. Use Reissue Legacy Certificate so cancellation and linked replacement issuance occur atomically.");
                }

                string reason = (TxtCertificateReason.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("Cancellation reason is required.");

                MessageBoxResult confirm = MessageBox.Show("Cancel certificate/report " + S(cert, "CertificateNumber") + "?", "Cancel Certificate", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes)
                    return;

                ElectronicSignature signature = RequestPrmSignature("PRM Certificate Cancellation");
                if (signature == null)
                    return;

                CancelCertificate(ToInt(cert, "CertificateID"), reason, signature);
                RefreshSelectedSample();
                MessageBox.Show("Certificate / report cancelled.", "Cancel Certificate", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Cancel Certificate", ex);
            }
        }

        private async void BtnReissueCertificate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();
                if (!CanIssuePrmCertificate())
                    throw new InvalidOperationException("You do not have permission to reissue PRM certificates/reports.");

                string status = GetCurrentSampleStatus();
                if (!IsOneOf(status, "Approved", "Certificate Issued"))
                    throw new InvalidOperationException("Reissue is allowed only after approval.");

                string reason = (TxtCertificateReason.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(reason))
                    throw new InvalidOperationException("Reissue reason is required.");

                DataRow active = GetActiveCertificateRow();
                if (active == null)
                    throw new InvalidOperationException("An active certificate/report is required for reissue.");

                await EnsurePrmCertificateSchemaReadyForActionAsync();
                await EnsurePrmQualityEventSchemaReadyForActionAsync();
                string overall = UpdateOverallInterpretation();
                EnsureCertificateInterpretationIsIssuable(overall);

                int oldId = ToInt(active, "CertificateID");
                if (_controlledLegacyReissueRoute &&
                    !_controlledLegacyReissueCompleted &&
                    _selectedSampleId == _initialSampleId &&
                    oldId != _initialLegacyCertificateId)
                {
                    throw new InvalidOperationException(
                        "The active certificate no longer matches the certificate referenced by the signed legacy reissue reconciliation. Return to Legacy Certificate Evidence Reconciliation and refresh before proceeding.");
                }

                ElectronicSignature signature = RequestPrmSignature("PRM Certificate Reissue");
                if (signature == null)
                    return;

                string certNo = IssueCertificate(true, reason, oldId, signature);
                if (_controlledLegacyReissueRoute && oldId == _initialLegacyCertificateId)
                    _controlledLegacyReissueCompleted = true;
                RefreshSelectedSample();
                MessageBox.Show("Certificate / report reissued: " + certNo, "Reissue Certificate", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ShowOperationError("Reissue Certificate", ex);
            }
        }

        private void RouteToDevelopmentDatabaseMaintenance()
        {
            if (!AppConfig.IsDevelopment)
            {
                MessageBox.Show(
                    "The installed PRM database schema is not compatible with this application version. " +
                    "Production database changes must be applied through the approved controlled deployment process.",
                    "PRM Database Update Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!(IsAdminUser() || DatabaseHelper.CanManageSettings(GetCurrentUserDisplayName())))
            {
                MessageBox.Show(
                    "The PRM database schema requires a controlled update. An administrator or user with system-settings permission must run Database Maintenance.",
                    "PRM Database Update Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            CommitGridEdit();
            if (HasPendingResultChanges())
            {
                MessageBox.Show(
                    "Unsaved result changes are present. Save or discard those changes before starting Database Maintenance.",
                    "Unsaved Results",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (Owner is not MainWindow mainWindow)
            {
                MessageBox.Show(
                    "Return to the main dashboard and run Database Maintenance, then reopen PRM Results.",
                    "PRM Database Update Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            MessageBoxResult updateNow = MessageBox.Show(
                "The database update required by PRM Investigation has not been applied.\n\n" +
                "Close this PRM Results window and start the signed Database Maintenance action now?",
                "PRM Database Update Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (updateNow != MessageBoxResult.Yes)
                return;

            Close();
            mainWindow.Activate();
            mainWindow.RequestDevelopmentDatabaseMaintenanceFromWorkflow();
        }

        private async void BtnQualityEvent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireSample();

                // v186: the stale-schema state is actionable instead of a disabled
                // dead-end.  The workflow itself still performs no DDL; it closes and
                // hands control back to the centralized, signed maintenance action.
                if (!IsPrmLegacyReconciliationSchemaReady())
                {
                    RouteToDevelopmentDatabaseMaintenance();
                    return;
                }

                CommitGridEdit();
                string overall = UpdateOverallInterpretation();
                if (HasPendingResultChanges())
                    throw new InvalidOperationException(
                        "Unsaved result or derived interpretation changes exist. Use Save Results with an electronic signature before opening the investigation.");

                // v185 runtime gate: only the minimal legacy-compatible event lookup may
                // run before the controlled schema-readiness probe.  v184 queried the
                // new reconciliation table first; on an upgraded database where the
                // signed Database Maintenance step had not yet applied 20260827_002,
                // that produced a raw SqlException instead of a controlled readiness
                // instruction when Continue Investigation was pressed.
                int eventId = GetOpenPrmQualityEventId();

                await EnsurePrmQualityEventSchemaReadyForActionAsync();

                bool legacyReconciliationRequired = false;
                if (eventId <= 0)
                {
                    legacyReconciliationRequired = HasUnreconciledLegacyPrmEvidence();
                    if (!legacyReconciliationRequired)
                    {
                        int latestEventId = GetLatestPrmQualityEventId();
                        if (latestEventId > 0 &&
                            (!IsPrmInvestigationRequired(overall) || DoesPrmQualityEventCoverCurrentAffectedResults(latestEventId)))
                        {
                            eventId = latestEventId;
                        }
                    }
                }

                if (eventId <= 0 && !IsPrmInvestigationRequired(overall) && !legacyReconciliationRequired)
                {
                    MessageBox.Show(
                        "No investigation is required for this PRM sample because the current overall interpretation is: " + overall + ".",
                        "Quality Event",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    UpdateQualityEventButtonState();
                    return;
                }

                // The readiness gate is read-only. Re-read the event state after the
                // gate to handle another user creating the event between the initial
                // lightweight lookup and this action.
                int openEventAfterGate = GetOpenPrmQualityEventId();
                if (openEventAfterGate > 0)
                {
                    eventId = openEventAfterGate;
                }
                else if (eventId <= 0)
                {
                    legacyReconciliationRequired = HasUnreconciledLegacyPrmEvidence();
                    if (!legacyReconciliationRequired)
                    {
                        int latestEventId = GetLatestPrmQualityEventId();
                        if (latestEventId > 0 &&
                            (!IsPrmInvestigationRequired(overall) || DoesPrmQualityEventCoverCurrentAffectedResults(latestEventId)))
                        {
                            eventId = latestEventId;
                        }
                    }
                }

                if (eventId <= 0)
                {
                    if (!CanManagePrmQualityEvent())
                        throw new InvalidOperationException("You do not have permission to open a PRM Quality Event.");

                    if (legacyReconciliationRequired)
                        eventId = CreatePrmQualityEvent(true);
                    else
                        eventId = CreatePrmQualityEvent();

                    TxtStatus.Text = "PRM Quality Event opened: " + GetPrmQualityEventNumber(eventId) + ". Opening investigation...";
                    UpdateQualityEventSummary();
                }

                PRMQualityEventInvestigation window = new PRMQualityEventInvestigation(
                    eventId,
                    GetCurrentUserDisplayName(),
                    GetCurrentUserRole());

                window.Owner = this;
                window.ShowDialog();

                RefreshSelectedSample();
                UpdateQualityEventButtonState();
            }
            catch (Exception ex)
            {
                ShowOperationError("Quality Event", ex);
            }
        }


        private void CommitGridEdit()
        {
            DgResults.CommitEdit(DataGridEditingUnit.Cell, true);
            DgResults.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void ValidatePrmNumericResultFormatsBeforeSignature()
        {
            if (_resultsTable == null)
                return;

            foreach (DataRow row in _resultsTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string resultType = row["ResultType"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(row["ResultType"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (!resultType.Contains("Numeric", StringComparison.OrdinalIgnoreCase))
                    continue;

                string result = row["ResultValue"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(result))
                    continue;

                string testName = row.Table.Columns.Contains("TestName") && row["TestName"] != DBNull.Value
                    ? Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? "Numeric PRM test"
                    : "Numeric PRM test";

                if (!PrmNumericSpecificationEvaluator.TryParseControlledDecimal(result, out decimal numericValue))
                {
                    throw new InvalidOperationException(
                        "Numeric PRM result for '" + testName +
                        "' must use digits with an optional decimal point only (for example 1000 or 12.5). " +
                        "Commas and thousands separators are not accepted because they are ambiguous.");
                }

                if (numericValue < 0m)
                    throw new InvalidOperationException("Numeric PRM result for '" + testName + "' cannot be negative.");
            }
        }

        private void SaveResultsFromGrid(SqlConnection conn, SqlTransaction tx, ElectronicSignature signature)
        {
            if (_resultsTable == null)
                LoadResultsForSample();

            if (_resultsTable == null)
                return;

            foreach (DataRow row in _resultsTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                int testId = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture);
                string currentResult = row["ResultValue"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture) ?? string.Empty;
                string currentRemarks = row["Remarks"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(row["Remarks"], CultureInfo.InvariantCulture) ?? string.Empty;
                string resultType = row["ResultType"] == DBNull.Value ? string.Empty : Convert.ToString(row["ResultType"], CultureInfo.InvariantCulture);
                string specification = row["SpecificationText"] == DBNull.Value ? string.Empty : Convert.ToString(row["SpecificationText"], CultureInfo.InvariantCulture);
                string testName = row.Table.Columns.Contains("TestName") && row["TestName"] != DBNull.Value
                    ? Convert.ToString(row["TestName"], CultureInfo.InvariantCulture)
                    : string.Empty;
                string testCode = row.Table.Columns.Contains("TestCode") && row["TestCode"] != DBNull.Value
                    ? Convert.ToString(row["TestCode"], CultureInfo.InvariantCulture)
                    : string.Empty;
                string specificationLimit = row.Table.Columns.Contains("SpecificationLimit") && row["SpecificationLimit"] != DBNull.Value
                    ? Convert.ToString(row["SpecificationLimit"], CultureInfo.InvariantCulture)
                    : string.Empty;

                string interpretation = CalculateInterpretation(
                    resultType,
                    specification,
                    currentResult,
                    specificationLimit,
                    testCode,
                    testName);
                SetDerivedInterpretationIfChanged(row, interpretation);

                if (row.RowState == DataRowState.Unchanged)
                    continue;

                string expectedResult = GetOriginalString(row, "ResultValue");
                string expectedInterpretation = GetOriginalString(row, "Interpretation");
                string expectedRemarks = GetOriginalString(row, "Remarks");
                string expectedEnteredBy = GetOriginalString(row, "EnteredBy");
                DateTime? expectedEnteredDate = GetOriginalDateTime(row, "EnteredDate");

                bool resultChanged = !string.Equals(expectedResult, currentResult, StringComparison.Ordinal);
                bool interpretationChanged = !string.Equals(expectedInterpretation, interpretation ?? string.Empty, StringComparison.Ordinal);
                bool remarksChanged = !string.Equals(expectedRemarks, currentRemarks, StringComparison.Ordinal);

                if (!resultChanged && !interpretationChanged && !remarksChanged)
                    continue;

                bool evidenceChanged = resultChanged || interpretationChanged;
                if (evidenceChanged)
                    EnsurePrmResultEvidenceIsMutableInTransaction(conn, tx, testId);

                string? storedResult = NormalizeNullableResultText(currentResult);
                string? storedRemarks = NormalizeNullableResultText(currentRemarks);

                if (resultChanged && storedResult != null)
                    EnsurePrmResultTimingGateInTransaction(conn, tx, row, testId, testCode, testName, signature);

                string persistedOldResult;
                string persistedOldInterpretation;
                string persistedOldRemarks;
                string persistedOldEnteredBy;
                DateTime? persistedOldEnteredDate;
                string persistedNewResult;
                string persistedNewInterpretation;
                string persistedNewRemarks;
                string persistedNewEnteredBy;
                DateTime? persistedNewEnteredDate;

                using (SqlCommand update = new SqlCommand(PrmResultPersistenceContract.GuardedUpdateSql, conn, tx))
                {
                    update.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    update.Parameters.Add("@SampleTestID", SqlDbType.Int).Value = testId;
                    update.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                    update.Parameters.Add("@ResultChanged", SqlDbType.Bit).Value = resultChanged;
                    update.Parameters.Add("@InterpretationChanged", SqlDbType.Bit).Value = interpretationChanged;
                    update.Parameters.Add("@RemarksChanged", SqlDbType.Bit).Value = remarksChanged;
                    update.Parameters.Add("@ResultValue", SqlDbType.NVarChar, 200).Value = storedResult == null ? DBNull.Value : storedResult;
                    update.Parameters.Add("@Interpretation", SqlDbType.NVarChar, 60).Value = interpretation ?? string.Empty;
                    update.Parameters.Add("@Remarks", SqlDbType.NVarChar, 500).Value = storedRemarks == null ? DBNull.Value : storedRemarks;
                    update.Parameters.Add("@EnteredBy", SqlDbType.NVarChar, 120).Value = GetCurrentUserDisplayName();
                    AddNullableStringParameter(update, "@ExpectedResultValue", 200, GetOriginalNullableString(row, "ResultValue"));
                    AddNullableStringParameter(update, "@ExpectedInterpretation", 60, GetOriginalNullableString(row, "Interpretation"));
                    AddNullableStringParameter(update, "@ExpectedRemarks", 500, GetOriginalNullableString(row, "Remarks"));
                    AddNullableStringParameter(update, "@ExpectedEnteredBy", 120, GetOriginalNullableString(row, "EnteredBy"));
                    update.Parameters.Add("@ExpectedEnteredDate", SqlDbType.DateTime2).Value = expectedEnteredDate.HasValue
                        ? expectedEnteredDate.Value
                        : DBNull.Value;

                    using SqlDataReader reader = update.ExecuteReader();
                    if (!reader.Read())
                    {
                        throw new DBConcurrencyException(
                            "PRM result " + testId.ToString(CultureInfo.InvariantCulture) +
                            " changed in the database after this window loaded it, or the sample workflow status changed. " +
                            "Reload the sample before saving; no stale result was overwritten.");
                    }

                    persistedOldResult = ReadNullableString(reader, 0);
                    persistedOldInterpretation = ReadNullableString(reader, 1);
                    persistedOldRemarks = ReadNullableString(reader, 2);
                    persistedOldEnteredBy = ReadNullableString(reader, 3);
                    persistedOldEnteredDate = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                    persistedNewResult = ReadNullableString(reader, 5);
                    persistedNewInterpretation = ReadNullableString(reader, 6);
                    persistedNewRemarks = ReadNullableString(reader, 7);
                    persistedNewEnteredBy = ReadNullableString(reader, 8);
                    persistedNewEnteredDate = reader.IsDBNull(9) ? null : reader.GetDateTime(9);

                    if (reader.Read())
                        throw new InvalidOperationException("PRM result persistence returned more than one row for a single SampleTestID.");
                }

                string auditAction = remarksChanged && !resultChanged && !interpretationChanged
                    ? "PRM Result Remarks Update"
                    : "PRM Result Entry";
                string auditField = remarksChanged && !resultChanged && !interpretationChanged
                    ? "Remarks"
                    : "ResultValue";

                DatabaseHelper.AddAuditTrailAdvanced(
                    conn,
                    tx,
                    "PRM_SampleTests",
                    testId,
                    auditAction,
                    BuildPrmResultAuditValue(persistedOldResult, persistedOldInterpretation, persistedOldRemarks, persistedOldEnteredBy, persistedOldEnteredDate),
                    BuildPrmResultAuditValue(persistedNewResult, persistedNewInterpretation, persistedNewRemarks, persistedNewEnteredBy, persistedNewEnteredDate),
                    signature.Reason,
                    signature.SignedBy,
                    auditField,
                    testName,
                    TxtSampleNo?.Text,
                    "PRM");
            }
        }

        private static string GetOriginalString(DataRow row, string columnName)
        {
            return GetOriginalNullableString(row, columnName) ?? string.Empty;
        }

        private static string? GetOriginalNullableString(DataRow row, string columnName)
        {
            if (row?.Table == null || !row.Table.Columns.Contains(columnName))
                return null;

            DataRowVersion version = row.HasVersion(DataRowVersion.Original)
                ? DataRowVersion.Original
                : DataRowVersion.Current;
            object value = row[columnName, version];
            return value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static DateTime? GetOriginalDateTime(DataRow row, string columnName)
        {
            if (row?.Table == null || !row.Table.Columns.Contains(columnName))
                return null;

            DataRowVersion version = row.HasVersion(DataRowVersion.Original)
                ? DataRowVersion.Original
                : DataRowVersion.Current;
            object value = row[columnName, version];
            return value == DBNull.Value ? null : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private static string? NormalizeNullableResultText(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ? null : normalized;
        }

        private static void AddNullableStringParameter(SqlCommand command, string name, int size, string? value)
        {
            command.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value == null ? DBNull.Value : value;
        }

        private static string ReadNullableString(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }

        private static string BuildPrmResultAuditValue(
            string result,
            string interpretation,
            string remarks,
            string enteredBy,
            DateTime? enteredDate)
        {
            return "ResultValue=" + (result ?? string.Empty) +
                   "; Interpretation=" + (interpretation ?? string.Empty) +
                   "; Remarks=" + (remarks ?? string.Empty) +
                   "; EnteredBy=" + (enteredBy ?? string.Empty) +
                   "; EnteredDate=" + (enteredDate.HasValue
                       ? enteredDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                       : string.Empty);
        }


        private void EnsurePrmResultTimingGateInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            DataRow row,
            int sampleTestId,
            string testCode,
            string testName,
            ElectronicSignature signature)
        {
            if (!row.Table.Columns.Contains("MinimumElapsedHours") || row["MinimumElapsedHours"] == DBNull.Value)
            {
                throw new InvalidOperationException(
                    "The approved PRM test definition does not contain a frozen Minimum Elapsed Hours value for " +
                    (string.IsNullOrWhiteSpace(testName) ? testCode : testName) +
                    ". Apply the controlled database migration and use an approved specification before entering results.");
            }

            decimal minimumElapsedHours = Convert.ToDecimal(row["MinimumElapsedHours"], CultureInfo.InvariantCulture);
            if (minimumElapsedHours < 0m)
                throw new InvalidOperationException("Minimum Elapsed Hours cannot be negative for a PRM test.");

            DateTime analysisStartedAt;
            DateTime databaseNow;
            using (SqlCommand clockCommand = new SqlCommand(@"
SELECT AnalysisStartedDate, SYSDATETIME()
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;", conn, tx))
            {
                clockCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                clockCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                using SqlDataReader reader = clockCommand.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected PRM sample no longer exists.");
                if (reader.IsDBNull(0))
                    throw new InvalidOperationException("Analysis start date/time is missing. Use Start Analysis before entering results.");
                analysisStartedAt = reader.GetDateTime(0);
                databaseNow = reader.GetDateTime(1);
            }

            DateTime eligibleAt = analysisStartedAt.AddHours(Convert.ToDouble(minimumElapsedHours, CultureInfo.InvariantCulture));
            if (databaseNow >= eligibleAt)
                return;

            string testLabel = string.IsNullOrWhiteSpace(testName) ? testCode : testName;
            string timingEvidence =
                "AnalysisStartedAt=" + analysisStartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "; MinimumElapsedHours=" + minimumElapsedHours.ToString("0.##", CultureInfo.InvariantCulture) +
                "; EarliestResultTime=" + eligibleAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "; DatabaseNow=" + databaseNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            if (!AppConfig.AllowEarlyMicrobiologyResults)
            {
                throw new InvalidOperationException(
                    "Result entry is blocked until the controlled minimum elapsed time is complete for " + testLabel +
                    ". Earliest permitted database time: " + eligibleAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
            }

            DatabaseHelper.AddAuditTrailAdvanced(
                conn, tx, "PRM_SampleTests", sampleTestId,
                "Development PRM Timing Override", timingEvidence,
                "Early result entry permitted in Development only", signature.Reason,
                signature.SignedBy, "MinimumElapsedHours", testLabel,
                TxtSampleNo?.Text, "PRM");
        }

        private void EnsurePrmTimingResultEntryAllowed(string actionName, bool allowRequiredReentry)
        {
            DataRow sample = GetCurrentSampleRow();
            string timingStatus = sample.Table.Columns.Contains("TimingReconciliationStatus")
                ? S(sample, "TimingReconciliationStatus").Trim()
                : "Not Required";

            if (timingStatus.Equals("Historical Closed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(actionName + " is blocked because this legacy PRM record contains immutable historical timing/certificate/Quality Event evidence and cannot be rewritten. Register a new controlled analysis/sample if further testing is required.");
            if (timingStatus.Equals("Reconciled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(actionName + " is blocked because QA Timing Reconciliation has already locked the re-entered result evidence. Continue to Submit Review; do not alter the reconciled results.");
            if (!allowRequiredReentry && timingStatus.Equals("Required", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(actionName + " is blocked while PRM Timing Reconciliation is required. The frozen test assignment must not be reloaded or replaced; re-enter only the affected result values after eligibility.");
        }

        private void EnsurePrmTimingResultEntryAllowedInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string actionName,
            bool allowRequiredReentry)
        {
            object value = ExecuteScalarInTransaction(conn, tx, @"
SELECT LTRIM(RTRIM(ISNULL(TimingReconciliationStatus,N'Not Required')))
FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID;",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });
            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("The selected PRM sample no longer exists.");

            string timingStatus = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (timingStatus.Equals("Historical Closed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(actionName + " is blocked because this legacy PRM record is historical and immutable after timing-governance migration.");
            if (timingStatus.Equals("Reconciled", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(actionName + " is blocked because QA Timing Reconciliation has already locked the re-entered result evidence.");
            if (!allowRequiredReentry && timingStatus.Equals("Required", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(actionName + " is blocked while PRM Timing Reconciliation is required.");
        }

        private void EnsurePrmTimingReconciliationClearedInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string actionName)
        {
            object value = ExecuteScalarInTransaction(conn, tx, @"
SELECT LTRIM(RTRIM(ISNULL(TimingReconciliationStatus,N'Not Required')))
FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID;",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("The selected PRM sample no longer exists.");

            string timingStatus = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (timingStatus.Equals("Required", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    actionName + " is blocked because this sample requires controlled PRM Timing Reconciliation. " +
                    "Re-enter affected results after their frozen eligibility times and obtain the independent QA reconciliation signature first.");
            }

            if (timingStatus.Equals("Historical Closed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    actionName + " is blocked for this legacy sample because migration 20260906_001 identified historical timing evidence that cannot be rewritten. " +
                    "Existing immutable issued history remains preserved, but no new approval/certificate action is permitted from this record.");
            }
        }

        private void EnsureAllPrmResultTimingGatesElapsedInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            ElectronicSignature signature,
            string actionName)
        {
            DateTime analysisStartedAt;
            DateTime databaseNow;
            decimal maximumMinimumHours;
            int missingTimingCount;

            using (SqlCommand command = new SqlCommand(@"
SELECT
    s.AnalysisStartedDate,
    SYSDATETIME() AS DatabaseNow,
    ISNULL(MAX(CASE WHEN ISNULL(t.RequiredTest,1)=1 THEN t.MinimumElapsedHours ELSE 0 END),0) AS MaximumMinimumHours,
    SUM(CASE WHEN ISNULL(t.RequiredTest,1)=1 AND t.MinimumElapsedHours IS NULL THEN 1 ELSE 0 END) AS MissingTimingCount
FROM dbo.PRM_Samples s WITH (UPDLOCK, HOLDLOCK)
LEFT JOIN dbo.PRM_SampleTests t WITH (UPDLOCK, HOLDLOCK) ON t.SampleID=s.SampleID
WHERE s.SampleID=@SampleID
GROUP BY s.AnalysisStartedDate;", conn, tx))
            {
                command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                command.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                using SqlDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected PRM sample no longer exists.");
                if (reader.IsDBNull(0))
                    throw new InvalidOperationException("Analysis start date/time is missing. Use Start Analysis before workflow submission.");
                analysisStartedAt = reader.GetDateTime(0);
                databaseNow = reader.GetDateTime(1);
                maximumMinimumHours = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
                missingTimingCount = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
            }

            if (missingTimingCount > 0)
            {
                throw new InvalidOperationException(
                    "One or more required PRM tests do not contain a frozen Minimum Elapsed Hours value. " +
                    "Apply the controlled migration and reload tests from an approved specification before " + actionName + ".");
            }

            DateTime eligibleAt = analysisStartedAt.AddHours(Convert.ToDouble(maximumMinimumHours, CultureInfo.InvariantCulture));
            if (databaseNow >= eligibleAt)
                return;

            string timingEvidence =
                "AnalysisStartedAt=" + analysisStartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "; MaximumMinimumElapsedHours=" + maximumMinimumHours.ToString("0.##", CultureInfo.InvariantCulture) +
                "; EarliestWorkflowTime=" + eligibleAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                "; DatabaseNow=" + databaseNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            if (!AppConfig.AllowEarlyMicrobiologyResults)
            {
                throw new InvalidOperationException(
                    actionName + " is blocked until all required PRM minimum elapsed times are complete. " +
                    "Earliest permitted database time: " + eligibleAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ".");
            }

            DatabaseHelper.AddAuditTrailAdvanced(
                conn, tx, "PRM_Samples", _selectedSampleId,
                "Development PRM Timing Override", timingEvidence,
                actionName + " permitted early in Development only", signature.Reason,
                signature.SignedBy, "AnalysisCompletedDate", null,
                TxtSampleNo?.Text, "PRM");
        }

        private static void EnsurePrmResultEvidenceIsMutableInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleTestId)
        {
            object ready = ExecuteScalarInTransaction(connection, transaction, @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NOT NULL
THEN 1 ELSE 0 END;");

            if (Convert.ToInt32(ready ?? 0, CultureInfo.InvariantCulture) != 1)
                return;

            object linked = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.QualityEventAffectedResults affected WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.QualityEvents qualityEvent WITH (UPDLOCK, HOLDLOCK)
    ON qualityEvent.QualityEventID = affected.QualityEventID
WHERE affected.SourceModule = N'PRM'
  AND affected.SourceResultID = @SampleTestID
  AND qualityEvent.SourceModule = N'PRM';",
                new SqlParameter("@SampleTestID", SqlDbType.Int) { Value = sampleTestId });

            if (Convert.ToInt32(linked ?? 0, CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException(
                    "This PRM result is already part of controlled Quality Event evidence and cannot be overwritten. " +
                    "Retain the original result and document any repeat/retest/resample as new traceable evidence under the investigation.");
            }
        }

        private static bool SetDerivedInterpretationIfChanged(DataRow row, string interpretation)
        {
            if (row == null || row.RowState == DataRowState.Deleted ||
                row.Table == null || !row.Table.Columns.Contains("Interpretation"))
            {
                return false;
            }

            string current = row["Interpretation"] == DBNull.Value
                ? string.Empty
                : Convert.ToString(row["Interpretation"], CultureInfo.InvariantCulture) ?? string.Empty;
            string next = interpretation ?? string.Empty;

            if (string.Equals(current, next, StringComparison.Ordinal))
                return false;

            row["Interpretation"] = next;
            return true;
        }

        private void AcceptResultChanges()
        {
            _resultsTable?.AcceptChanges();
        }

        private bool HasPendingResultChanges()
        {
            if (_resultsTable == null)
                return false;

            return _resultsTable.GetChanges() != null;
        }

        private void EnsureTestsAssigned()
        {
            if (_resultsTable == null || _resultsTable.Rows.Count == 0)
                throw new InvalidOperationException("No tests are assigned to this PRM sample. Load the compatible specification tests first.");
        }

        private string GetLockedPrmSampleStatusInTransaction(SqlConnection connection, SqlTransaction transaction)
        {
            object statusValue = ExecuteScalarInTransaction(connection, transaction, @"
SELECT LTRIM(RTRIM(ISNULL(SampleStatus, N'')))
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

            if (statusValue == null || statusValue == DBNull.Value)
                throw new InvalidOperationException("The selected PRM sample no longer exists.");

            return Convert.ToString(statusValue, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        private bool HasSignerPerformedPrmActionInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string signedBy,
            params string[] actionTypes)
        {
            if (_selectedSampleId <= 0 || string.IsNullOrWhiteSpace(signedBy) || actionTypes == null || actionTypes.Length == 0)
                return false;

            foreach (string actionType in actionTypes)
            {
                object count = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.PRM_ElectronicSignatures WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID
  AND ActionType = @ActionType
  AND SignedBy = @SignedBy;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                    new SqlParameter("@ActionType", SqlDbType.NVarChar, 80) { Value = actionType ?? string.Empty },
                    new SqlParameter("@SignedBy", SqlDbType.NVarChar, 120) { Value = signedBy.Trim() });

                if (Convert.ToInt32(count ?? 0, CultureInfo.InvariantCulture) > 0)
                    return true;
            }

            return false;
        }

        private bool IsMinimalPrmQualityEventLookupReady()
        {
            object ready = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NOT NULL
THEN 1 ELSE 0 END;");

            return Convert.ToInt32(ready ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        private bool HasAnyPrmQualityEventMinimal()
        {
            if (!IsMinimalPrmQualityEventLookupReady())
                return false;

            object count = DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1)
FROM dbo.QualityEvents
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

            return Convert.ToInt32(count ?? 0, CultureInfo.InvariantCulture) > 0;
        }
    }
}
