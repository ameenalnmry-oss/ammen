using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS
{
    public partial class LegacyCertificateEvidenceReconciliation : Window
    {
        private const string RetainDisposition = "LEGACY_HISTORICAL_RECORD_RETAINED";
        private const string ReissueDisposition = "CONTROLLED_REISSUE_REQUIRED";

        private readonly ObservableCollection<LegacyCertificateRow> _certificates = new();
        private LegacyCertificateRow? _selected;
        private bool _suppressSelectionChanged;
        private bool _isSupersedingCorrectionDraft;
        private int? _expectedSupersededReconciliationId;
        private string _expectedSupersededModule = string.Empty;
        private int _expectedSupersededCertificateId;

        public bool WasSaved { get; private set; }
        public bool RequiresPreflightRefresh { get; private set; }

        public LegacyCertificateEvidenceReconciliation()
        {
            InitializeComponent();
            Loaded += Window_Loaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsurePermission();
                EnsureSchema();
                cboDisposition.SelectedIndex = 0;
                LoadCertificates();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open legacy certificate evidence reconciliation.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Legacy certificate evidence reconciliation"),
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        private static void EnsurePermission()
        {
            if (string.IsNullOrWhiteSpace(Login.CurrentUser) || !DatabaseHelper.CanApproveResults(Login.CurrentUser))
                throw new UnauthorizedAccessException("QA approval permission is required to reconcile legacy certificate evidence.");
        }

        private static void EnsureSchema()
        {
            int ready = Convert.ToInt32(DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN OBJECT_ID(N'dbo.LegacyCertificateEvidenceReconciliations',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.TRG_LegacyCertificateEvidenceReconciliations_AppendOnly_20260908',N'TR') IS NOT NULL
 AND OBJECT_ID(N'dbo.TRG_LegacyCertificateEvidenceReconciliations_ValidateInsert_20260908',N'TR') IS NOT NULL
 AND OBJECT_ID(N'dbo.TRG_LegacyCertificateEvidenceReconciliations_EvidenceQuality_20260909',N'TR') IS NOT NULL
 AND OBJECT_ID(N'dbo.fn_LegacyCertificateEvidenceIsPlaceholder_20260909',N'FN') IS NOT NULL
 AND EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260722_003' AND AppliedAt IS NOT NULL)
 AND EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260909_000' AND AppliedAt IS NOT NULL)
 AND EXISTS(SELECT 1 FROM dbo.LIMS_SchemaVersions WHERE VersionKey=N'20260909_001' AND AppliedAt IS NOT NULL)
 THEN 1 ELSE 0 END;"), CultureInfo.InvariantCulture);

            if (ready != 1)
                throw new InvalidOperationException(
                    "Legacy certificate reconciliation schema is not current. Apply controlled migrations through 20260909_001 using Database Maintenance first.");
        }

        private void LoadCertificates()
        {
            _certificates.Clear();

            DataTable table = DatabaseHelper.ExecuteQuery(@"
DECLARE @SnapshotCutover DATETIME2(0)=NULL;
SELECT TOP(1) @SnapshotCutover=AppliedAt
FROM dbo.LIMS_SchemaVersions
WHERE VersionKey=N'20260722_003'
ORDER BY AppliedAt;

IF @SnapshotCutover IS NULL
    THROW 54828, 'Immutable certificate-snapshot cutover cannot be established.', 1;

SELECT
    N'PRM' AS CertificateModule,
    c.CertificateID,
    c.CertificateNumber,
    c.SampleID,
    c.IssueDate,
    c.CertificateStatus,
    CONCAT(
        CASE WHEN c.ReportHash IS NULL OR LEN(LTRIM(RTRIM(c.ReportHash)))<>64
             THEN N'Legacy/non-current report hash' ELSE N'' END,
        CASE WHEN (c.ReportHash IS NULL OR LEN(LTRIM(RTRIM(c.ReportHash)))<>64)
                   AND NOT EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID)
             THEN N'; ' ELSE N'' END,
        CASE WHEN NOT EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID)
             THEN N'No native issue snapshot' ELSE N'' END
    ) AS LegacyCondition,
    R.ReconciliationID,
    R.EvidenceReference,
    R.EvidenceSummary,
    R.Disposition,
    R.Reason,
    R.SignedBy,
    R.SignedAt
FROM dbo.PRM_Certificates c
OUTER APPLY
(
    SELECT TOP(1) X.ReconciliationID,X.EvidenceReference,X.EvidenceSummary,X.Disposition,X.Reason,X.SignedBy,X.SignedAt
    FROM dbo.LegacyCertificateEvidenceReconciliations X
    WHERE X.CertificateModule=N'PRM' AND X.CertificateID=c.CertificateID
    ORDER BY X.ReconciliationID DESC
) R
WHERE UPPER(LTRIM(RTRIM(ISNULL(c.CertificateStatus,N''))))=N'ACTIVE'
  AND c.IssueDate<@SnapshotCutover
  AND
  (
      c.ReportHash IS NULL OR LEN(LTRIM(RTRIM(c.ReportHash)))<>64
      OR NOT EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID)
  )

UNION ALL

SELECT
    N'WATER' AS CertificateModule,
    c.CertificateID,
    c.CertificateNumber,
    c.SampleID,
    c.IssueDate,
    ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''),ISNULL(c.Status,N'')) AS CertificateStatus,
    N'No native issue snapshot' AS LegacyCondition,
    R.ReconciliationID,
    R.EvidenceReference,
    R.EvidenceSummary,
    R.Disposition,
    R.Reason,
    R.SignedBy,
    R.SignedAt
FROM dbo.Certificates c
OUTER APPLY
(
    SELECT TOP(1) X.ReconciliationID,X.EvidenceReference,X.EvidenceSummary,X.Disposition,X.Reason,X.SignedBy,X.SignedAt
    FROM dbo.LegacyCertificateEvidenceReconciliations X
    WHERE X.CertificateModule=N'WATER' AND X.CertificateID=c.CertificateID
    ORDER BY X.ReconciliationID DESC
) R
WHERE UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''),ISNULL(c.Status,N''))))) IN(N'ACTIVE',N'ISSUED')
  AND c.IssueDate<@SnapshotCutover
  AND NOT EXISTS(SELECT 1 FROM dbo.CertificateDocumentSnapshots s WHERE s.CertificateID=c.CertificateID)
ORDER BY CertificateModule,IssueDate,CertificateNumber;");

            foreach (DataRow row in table.Rows)
                _certificates.Add(new LegacyCertificateRow(row));

            gridCertificates.ItemsSource = _certificates;
            if (_certificates.Count == 0)
            {
                _selected = null;
                ClearEntryFields();
                lblSelectedCertificate.Text = "No unresolved pre-cutover certificate evidence was found.";
                lblLegacyCondition.Text = "Observed legacy condition: N/A";
                lblSelectionSummary.Text = "No certificate requires QA reconciliation.";
                lblStatus.Text = "No unresolved legacy certificate evidence remains.";
                _isSupersedingCorrectionDraft = false;
                ResetSupersedingExpectation();
                SetEntryEditMode(false, showSupersedingAction: false);
                BtnSelectModule.IsEnabled = false;
                ConfigureSourceWorkflowButton(Array.Empty<LegacyCertificateRow>());
                return;
            }

            BtnSave.IsEnabled = true;
            BtnSelectModule.IsEnabled = true;
            _suppressSelectionChanged = true;
            try
            {
                gridCertificates.SelectedItems.Clear();
                gridCertificates.SelectedIndex = 0;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            RefreshSelectionContext(clearEntryFields: true);
        }

        private void gridCertificates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged)
                return;

            RefreshSelectionContext(clearEntryFields: true);
        }

        private void RefreshSelectionContext(bool clearEntryFields)
        {
            List<LegacyCertificateRow> selected = GetSelectedCertificates();
            _selected = selected.FirstOrDefault();
            ConfigureSourceWorkflowButton(selected);

            if (_selected == null)
            {
                lblSelectedCertificate.Text = "Select a certificate.";
                lblLegacyCondition.Text = "Observed legacy condition: N/A";
                lblSelectionSummary.Text = "Select one certificate, or use a controlled batch for one module.";
                _isSupersedingCorrectionDraft = false;
                ResetSupersedingExpectation();
                ClearEntryFields();
                SetEntryEditMode(false, showSupersedingAction: false);
                return;
            }

            if (selected.Count == 1)
            {
                LegacyCertificateRow row = selected[0];
                lblSelectedCertificate.Text = $"{row.Module} | {row.CertificateNumber} | SampleID {row.SampleID} | {row.IssueDateText}";
                lblLegacyCondition.Text = "Observed legacy condition: " + row.LegacyCondition;
                BtnSave.Content = "Sign and Save Reconciliation";

                if (clearEntryFields)
                {
                    _isSupersedingCorrectionDraft = false;
                    ResetSupersedingExpectation();
                    if (row.LatestReconciliationID.HasValue)
                    {
                        DisplaySignedReconciliation(row);
                        SetEntryEditMode(false, showSupersedingAction: true);

                        if (row.HasEvidenceQualityIssue)
                        {
                            lblSignedRecordMode.Text =
                                $"Signed reconciliation #{row.LatestReconciliationID} is immutable and contains placeholder/incomplete evidence. " +
                                "It is displayed read-only. Start a superseding correction to replace the QA disposition with meaningful evidence.";
                            lblStatus.Text =
                                "A signed QA disposition already exists for this certificate, but its evidence does not satisfy the current quality gate. " +
                                "Use Start Superseding Correction; no save is permitted while the signed record is only being viewed.";
                        }
                        else
                        {
                            lblSignedRecordMode.Text =
                                $"Signed reconciliation #{row.LatestReconciliationID} is immutable and is displayed read-only. " +
                                "Start a superseding correction only when the signed evidence or disposition must be corrected.";
                            lblStatus.Text = row.Disposition.Equals(ReissueDisposition, StringComparison.OrdinalIgnoreCase)
                                ? "The signed QA disposition already requires controlled cancellation/reissue. Complete the actual certificate reissue in the source certificate workflow; use a superseding correction only to correct the signed evidence or disposition."
                                : "The signed QA disposition is retained as immutable evidence. Start a superseding correction only when a controlled correction is required.";
                        }
                    }
                    else
                    {
                        ClearEntryFields();
                        SetEntryEditMode(true, showSupersedingAction: false);
                        lblStatus.Text = "Fail-closed default: Controlled cancellation/reissue required. Select Retain only when actual controlled historical evidence has been reviewed and can be cited.";
                    }
                }

                lblSelectionSummary.Text = row.LatestReconciliationID.HasValue
                    ? "1 certificate selected; signed disposition is displayed read-only."
                    : "1 unreconciled certificate selected. Use module batch selection when the same evidence package applies.";
                return;
            }

            bool sameModule = selected.All(row => row.Module.Equals(_selected.Module, StringComparison.OrdinalIgnoreCase));
            int alreadyReconciled = selected.Count(row => row.LatestReconciliationID.HasValue);

            lblSelectedCertificate.Text = $"{selected.Count} certificates selected | {selected.Select(x => x.Module).Distinct(StringComparer.OrdinalIgnoreCase).Count()} module(s)";
            lblLegacyCondition.Text = sameModule
                ? "Observed legacy conditions vary by certificate; each row retains its own condition snapshot."
                : "Batch save blocked: PRM and WATER certificates cannot be mixed in one signed batch.";
            BtnSave.Content = $"Sign and Save Selected Reconciliations ({selected.Count})";

            if (clearEntryFields)
            {
                _isSupersedingCorrectionDraft = false;
                ResetSupersedingExpectation();
                ClearEntryFields();
            }

            if (!sameModule)
            {
                SetEntryEditMode(false, showSupersedingAction: false);
                lblSelectionSummary.Text = $"{selected.Count} selected — mixed modules are not allowed.";
                lblStatus.Text = "Batch reconciliation requires all selected certificates to belong to the same module.";
                return;
            }

            bool batchEditable = alreadyReconciled == 0;
            SetEntryEditMode(batchEditable, showSupersedingAction: false);
            lblSelectionSummary.Text = $"{selected.Count} selected in {selected[0].Module}; {alreadyReconciled} already reconciled.";
            lblStatus.Text = batchEditable
                ? "Batch mode: one controlled evidence package and one electronic signature append one independent reconciliation row per certificate."
                : "Batch save is blocked when any selected certificate already has signed evidence. Superseding corrections must be started and signed one certificate at a time.";
        }

        private void ResetSupersedingExpectation()
        {
            _expectedSupersededReconciliationId = null;
            _expectedSupersededModule = string.Empty;
            _expectedSupersededCertificateId = 0;
        }

        private void DisplaySignedReconciliation(LegacyCertificateRow row)
        {
            txtEvidenceReference.Text = row.EvidenceReference;
            txtEvidenceSummary.Text = row.EvidenceSummary;
            SelectDisposition(row.Disposition);
            txtReason.Text = row.ReconciliationReason;
        }

        private void SetEntryEditMode(bool editable, bool showSupersedingAction)
        {
            txtEvidenceReference.IsReadOnly = !editable;
            txtEvidenceSummary.IsReadOnly = !editable;
            txtReason.IsReadOnly = !editable;
            cboDisposition.IsEnabled = editable;
            BtnPrepareReissue.IsEnabled = editable;
            BtnSave.IsEnabled = editable;
            SignedRecordBanner.Visibility = showSupersedingAction ? Visibility.Visible : Visibility.Collapsed;
            BtnStartSuperseding.IsEnabled = showSupersedingAction;
        }

        private void BtnStartSuperseding_Click(object sender, RoutedEventArgs e)
        {
            List<LegacyCertificateRow> selected = GetSelectedCertificates();
            if (selected.Count != 1)
            {
                MessageBox.Show(
                    "Select exactly one certificate with an existing signed reconciliation.",
                    "Superseding Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            LegacyCertificateRow row = selected[0];
            if (row.LatestReconciliationID is not int reconciliationId)
            {
                MessageBox.Show(
                    "Select exactly one certificate with an existing signed reconciliation.",
                    "Superseding Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            MessageBoxResult confirmation = MessageBox.Show(
                $"Signed reconciliation #{reconciliationId} will remain immutable. A new electronically signed reconciliation will supersede it. Continue?",
                "Start Superseding Correction",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;

            _isSupersedingCorrectionDraft = true;
            _expectedSupersededReconciliationId = reconciliationId;
            _expectedSupersededModule = row.Module;
            _expectedSupersededCertificateId = row.CertificateID;
            SetEntryEditMode(true, showSupersedingAction: false);
            PrepareControlledReissueDraft(row);
            BtnSave.Content = "Sign and Save Superseding Reconciliation";
            lblStatus.Text =
                $"Superseding draft started for signed reconciliation #{reconciliationId}. " +
                "The original signed row remains unchanged. Review all draft fields before electronic signature; select Retain only if actual controlled historical evidence supports retention.";
        }

        private void BtnSelectModule_Click(object sender, RoutedEventArgs e)
        {
            LegacyCertificateRow? anchor = gridCertificates.SelectedItem as LegacyCertificateRow ?? _selected;
            if (anchor == null)
            {
                MessageBox.Show("Select a certificate first.", "Certificate Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<LegacyCertificateRow> matching = _certificates
                .Where(row => row.Module.Equals(anchor.Module, StringComparison.OrdinalIgnoreCase) && !row.LatestReconciliationID.HasValue)
                .ToList();

            if (matching.Count == 0)
            {
                MessageBox.Show("No unreconciled certificates remain in this module.", "Certificate Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _suppressSelectionChanged = true;
            try
            {
                gridCertificates.SelectedItems.Clear();
                foreach (LegacyCertificateRow row in matching)
                    gridCertificates.SelectedItems.Add(row);
                gridCertificates.ScrollIntoView(matching[0]);
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            RefreshSelectionContext(clearEntryFields: true);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (!TryValidateSaveRequest(out string validationMessage))
            {
                MessageBox.Show(
                    validationMessage,
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                SaveSelectedReconciliations();
            }
            catch (Exception ex)
            {
                // UserFacingError performs the single diagnostic log write and returns a safe reference.
                // Do not log the same exception again here; duplicate ERROR rows distort AI session review.
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Legacy certificate evidence reconciliation"),
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool TryValidateSaveRequest(out string message)
        {
            List<LegacyCertificateRow> selected = GetSelectedCertificates();
            if (selected.Count == 0)
            {
                message = "Select at least one certificate first.";
                return false;
            }

            if (selected.Count == 1 && selected[0].LatestReconciliationID.HasValue && !_isSupersedingCorrectionDraft)
            {
                message =
                    "The selected certificate already has a signed immutable QA disposition. " +
                    "Use Start Superseding Correction before entering or signing replacement evidence.";
                return false;
            }

            if (_isSupersedingCorrectionDraft)
            {
                LegacyCertificateRow row = selected[0];
                if (selected.Count != 1 || !_expectedSupersededReconciliationId.HasValue ||
                    !_expectedSupersededModule.Equals(row.Module, StringComparison.OrdinalIgnoreCase) ||
                    _expectedSupersededCertificateId != row.CertificateID ||
                    row.LatestReconciliationID != _expectedSupersededReconciliationId)
                {
                    message = "The signed reconciliation changed after this superseding draft was started. Reload the certificate and start the correction again.";
                    return false;
                }
            }

            if (selected.Count > 1 && selected.Any(row => row.LatestReconciliationID.HasValue))
            {
                message = "Batch save is not permitted when any selected certificate already has signed evidence. Superseding corrections must be started one certificate at a time.";
                return false;
            }

            if (!selected.All(row => row.Module.Equals(selected[0].Module, StringComparison.OrdinalIgnoreCase)))
            {
                message = "Batch reconciliation requires all selected certificates to belong to the same module.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtEvidenceReference.Text))
            {
                message = "Controlled Evidence Reference is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtEvidenceSummary.Text))
            {
                message = "Evidence Summary is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(GetSelectedDisposition()))
            {
                message = "QA Disposition is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtReason.Text))
            {
                message = "Reconciliation Reason is required.";
                return false;
            }

            try
            {
                ValidateReconciliationEvidenceQuality(
                    GetSelectedDisposition(),
                    txtEvidenceReference.Text.Trim(),
                    txtEvidenceSummary.Text.Trim(),
                    txtReason.Text.Trim());
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
                return false;
            }

            message = string.Empty;
            return true;
        }

        private void SaveSelectedReconciliations()
        {
            List<LegacyCertificateRow> selected = GetSelectedCertificates();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select at least one certificate first.", "Certificate Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EnsurePermission();

            if (!selected.All(row => row.Module.Equals(selected[0].Module, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Batch reconciliation requires all selected certificates to belong to the same module.");

            if (selected.Count > 1 && selected.Any(row => row.LatestReconciliationID.HasValue))
                throw new InvalidOperationException("Batch reconciliation cannot include a certificate that already has signed evidence. Superseding corrections must be performed one certificate at a time.");

            int? expectedSupersededReconciliationId = null;
            if (_isSupersedingCorrectionDraft)
            {
                if (selected.Count != 1 || !_expectedSupersededReconciliationId.HasValue ||
                    !_expectedSupersededModule.Equals(selected[0].Module, StringComparison.OrdinalIgnoreCase) ||
                    _expectedSupersededCertificateId != selected[0].CertificateID)
                {
                    throw new DBConcurrencyException("The superseding draft no longer matches the selected signed reconciliation. Reload and start the correction again.");
                }
                expectedSupersededReconciliationId = _expectedSupersededReconciliationId.Value;
            }

            string evidenceReference = txtEvidenceReference.Text.Trim();
            string evidenceSummary = txtEvidenceSummary.Text.Trim();
            string reason = txtReason.Text.Trim();
            string disposition = GetSelectedDisposition();

            if (string.IsNullOrWhiteSpace(evidenceReference))
                throw new InvalidOperationException("Controlled Evidence Reference is required.");
            if (string.IsNullOrWhiteSpace(evidenceSummary))
                throw new InvalidOperationException("Evidence Summary is required.");
            if (string.IsNullOrWhiteSpace(disposition))
                throw new InvalidOperationException("QA Disposition is required.");
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Reconciliation Reason is required.");

            ValidateReconciliationEvidenceQuality(disposition, evidenceReference, evidenceSummary, reason);

            string module = selected[0].Module;
            string signatureScope = selected.Count == 1
                ? $"{module} / {selected[0].CertificateNumber}"
                : $"Batch: {selected.Count} {module} legacy certificates";
            string signatureAction = selected.Count == 1
                ? "Reconcile Legacy Certificate Evidence"
                : "Batch Reconcile Legacy Certificate Evidence";

            var signature = new ElectronicSignature(signatureScope, Login.CurrentUser, signatureAction, true) { Owner = this };
            if (signature.ShowDialog() != true || !signature.IsConfirmed)
                return;

            string batchId = selected.Count > 1 ? Guid.NewGuid().ToString("N").ToUpperInvariant() : string.Empty;
            var reconciliationIds = new List<int>();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string role = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection,
                    transaction,
                    signature.SignedBy,
                    "CanApproveResults",
                    selected.Count == 1 ? "reconcile legacy certificate evidence" : "batch reconcile legacy certificate evidence");

                DateTime snapshotCutover = ReadSnapshotCutover(connection, transaction);

                foreach (LegacyCertificateRow row in selected.OrderBy(x => x.Module).ThenBy(x => x.CertificateID))
                {
                    LockedCertificate current = LockAndReadCertificate(connection, transaction, row.Module, row.CertificateID);
                    ValidateLegacyCandidate(current, snapshotCutover);

                    if (!current.CertificateNumber.Equals(row.CertificateNumber, StringComparison.Ordinal) || current.SampleID != row.SampleID)
                        throw new DBConcurrencyException($"Certificate {row.CertificateNumber} identity changed before reconciliation. No reconciliation was committed.");

                    int? latestId = null;
                    string oldValue = "Unreconciled legacy certificate evidence";
                    using (SqlCommand latest = new SqlCommand(@"
SELECT TOP(1) ReconciliationID,EvidenceReference,EvidenceSummary,Disposition,Reason,SignedBy,SignedAt
FROM dbo.LegacyCertificateEvidenceReconciliations WITH(UPDLOCK,HOLDLOCK)
WHERE CertificateModule=@Module AND CertificateID=@CertificateID
ORDER BY ReconciliationID DESC;", connection, transaction))
                    {
                        latest.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = row.Module;
                        latest.Parameters.Add("@CertificateID", SqlDbType.Int).Value = row.CertificateID;
                        using SqlDataReader reader = latest.ExecuteReader();
                        if (reader.Read())
                        {
                            latestId = Convert.ToInt32(reader["ReconciliationID"], CultureInfo.InvariantCulture);
                            oldValue = $"Reconciliation #{latestId}; Disposition={reader["Disposition"]}; Evidence={reader["EvidenceReference"]}; SignedBy={reader["SignedBy"]}; SignedAt={reader["SignedAt"]}";
                        }
                    }

                    if (selected.Count > 1 && latestId.HasValue)
                        throw new DBConcurrencyException($"Certificate {row.CertificateNumber} was reconciled by another action before this batch was saved. No batch record has been committed.");

                    if (expectedSupersededReconciliationId.HasValue)
                    {
                        if (!latestId.HasValue || latestId.Value != expectedSupersededReconciliationId.Value)
                            throw new DBConcurrencyException($"Signed reconciliation #{expectedSupersededReconciliationId.Value} was superseded by another action before this correction was signed. Reload and start the correction again.");
                    }
                    else if (selected.Count == 1 && !row.LatestReconciliationID.HasValue && latestId.HasValue)
                    {
                        throw new DBConcurrencyException($"Certificate {row.CertificateNumber} was reconciled by another action before this reconciliation was signed. Reload and retry.");
                    }

                    string storedReason = selected.Count > 1
                        ? $"{reason}{Environment.NewLine}Batch reconciliation ID: {batchId}; signed scope: {selected.Count} {module} certificates."
                        : reason;

                    using SqlCommand insert = new SqlCommand(@"
DECLARE @Inserted TABLE(ReconciliationID INT NOT NULL);
INSERT dbo.LegacyCertificateEvidenceReconciliations
(
    CertificateModule,CertificateID,SupersedesReconciliationID,CertificateNumberSnapshot,SampleIDSnapshot,
    IssueDateSnapshot,CertificateStatusSnapshot,LegacyConditionSnapshot,EvidenceReference,EvidenceSummary,
    Disposition,Reason,SignedBy,UserRole,MeaningOfSignature,SignatureReason,SignedAt,SourceWorkstation,ReconciliationSchemaVersion
)
OUTPUT INSERTED.ReconciliationID INTO @Inserted(ReconciliationID)
VALUES
(
    @Module,@CertificateID,@Supersedes,@CertificateNumber,@SampleID,
    @IssueDate,@Status,@Condition,@EvidenceReference,@EvidenceSummary,
    @Disposition,@Reason,@SignedBy,@UserRole,@Meaning,@SignatureReason,SYSUTCDATETIME(),@Workstation,1
);
SELECT TOP(1) ReconciliationID FROM @Inserted;", connection, transaction);

                    insert.Parameters.Add("@Module", SqlDbType.NVarChar, 20).Value = current.Module;
                    insert.Parameters.Add("@CertificateID", SqlDbType.Int).Value = current.CertificateID;
                    insert.Parameters.Add("@Supersedes", SqlDbType.Int).Value = expectedSupersededReconciliationId.HasValue
                        ? expectedSupersededReconciliationId.Value
                        : (latestId.HasValue ? latestId.Value : DBNull.Value);
                    insert.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 100).Value = current.CertificateNumber;
                    insert.Parameters.Add("@SampleID", SqlDbType.Int).Value = current.SampleID;
                    insert.Parameters.Add("@IssueDate", SqlDbType.DateTime2).Value = current.IssueDate;
                    insert.Parameters.Add("@Status", SqlDbType.NVarChar, 60).Value = current.CertificateStatus;
                    insert.Parameters.Add("@Condition", SqlDbType.NVarChar, 1000).Value = current.LegacyCondition;
                    insert.Parameters.Add("@EvidenceReference", SqlDbType.NVarChar, 500).Value = evidenceReference;
                    insert.Parameters.Add("@EvidenceSummary", SqlDbType.NVarChar, -1).Value = evidenceSummary;
                    insert.Parameters.Add("@Disposition", SqlDbType.NVarChar, 80).Value = disposition;
                    insert.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = storedReason;
                    insert.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 120).Value = signature.SignedBy;
                    insert.Parameters.Add("@UserRole", SqlDbType.NVarChar, 100).Value = role;
                    insert.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning;
                    insert.Parameters.Add("@SignatureReason", SqlDbType.NVarChar, 1000).Value = string.IsNullOrWhiteSpace(signature.Reason) ? DBNull.Value : signature.Reason;
                    insert.Parameters.Add("@Workstation", SqlDbType.NVarChar, 200).Value = Environment.MachineName;

                    int reconciliationId = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                    reconciliationIds.Add(reconciliationId);

                    string newValue = $"Reconciliation #{reconciliationId}; Module={current.Module}; Certificate={current.CertificateNumber}; Condition={current.LegacyCondition}; Disposition={disposition}; Evidence={evidenceReference}" +
                        (selected.Count > 1 ? $"; BatchID={batchId}" : string.Empty);
                    string auditReason = storedReason +
                        " | Evidence summary: " + evidenceSummary +
                        " | Meaning: " + signature.Meaning +
                        (string.IsNullOrWhiteSpace(signature.Reason) ? string.Empty : " | E-signature reason: " + signature.Reason);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "LegacyCertificateEvidenceReconciliations",
                        reconciliationId,
                        latestId.HasValue ? "Legacy Certificate Reconciliation Superseded" :
                        selected.Count > 1 ? "Legacy Certificate Batch Reconciled" : "Legacy Certificate Reconciled",
                        oldValue,
                        newValue,
                        auditReason,
                        signature.SignedBy,
                        "LegacyCertificateEvidence",
                        current.LegacyCondition,
                        current.CertificateNumber,
                        current.Module == "PRM" ? "PRM" : "Water / Certificates");
                }
            });

            WasSaved = true;
            _isSupersedingCorrectionDraft = false;
            ResetSupersedingExpectation();
            int savedCount = reconciliationIds.Count;
            lblStatus.Text = selected.Count == 1
                ? $"Signed reconciliation #{reconciliationIds[0]} saved. The issued certificate was not changed."
                : $"Signed batch {batchId} saved for {savedCount} certificates. Each certificate received an independent append-only reconciliation record.";

            LoadCertificates();

            MessageBox.Show(
                selected.Count == 1
                    ? "Legacy certificate evidence was reconciled successfully. The original certificate and native snapshot/hash state remain unchanged."
                    : $"Legacy certificate evidence was reconciled successfully for {savedCount} certificates. Each certificate has its own immutable QA disposition. Batch ID: {batchId}",
                "Certificate Reconciliation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private static DateTime ReadSnapshotCutover(SqlConnection connection, SqlTransaction transaction)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT TOP(1) AppliedAt
FROM dbo.LIMS_SchemaVersions WITH(HOLDLOCK)
WHERE VersionKey=N'20260722_003'
ORDER BY AppliedAt;", connection, transaction);
            object? value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("Immutable certificate-snapshot cutover cannot be established.");
            return Convert.ToDateTime(value, CultureInfo.InvariantCulture);
        }

        private static LockedCertificate LockAndReadCertificate(SqlConnection connection, SqlTransaction transaction, string module, int certificateId)
        {
            bool isPrm = module.Equals("PRM", StringComparison.OrdinalIgnoreCase);
            string sql = isPrm
                ? @"
SELECT c.CertificateID,c.CertificateNumber,c.SampleID,c.IssueDate,c.CertificateStatus,c.ReportHash,
       CASE WHEN EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots s WHERE s.CertificateID=c.CertificateID) THEN 1 ELSE 0 END AS HasNativeSnapshot
FROM dbo.PRM_Certificates c WITH(UPDLOCK,HOLDLOCK)
WHERE c.CertificateID=@CertificateID;"
                : @"
SELECT c.CertificateID,c.CertificateNumber,c.SampleID,c.IssueDate,
       ISNULL(NULLIF(LTRIM(RTRIM(c.CertificateStatus)),N''),ISNULL(c.Status,N'')) AS CertificateStatus,
       CAST(NULL AS nvarchar(128)) AS ReportHash,
       CASE WHEN EXISTS(SELECT 1 FROM dbo.CertificateDocumentSnapshots s WHERE s.CertificateID=c.CertificateID) THEN 1 ELSE 0 END AS HasNativeSnapshot
FROM dbo.Certificates c WITH(UPDLOCK,HOLDLOCK)
WHERE c.CertificateID=@CertificateID;";

            using SqlCommand command = new SqlCommand(sql, connection, transaction);
            command.Parameters.Add("@CertificateID", SqlDbType.Int).Value = certificateId;
            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new DBConcurrencyException($"{module} certificate ID {certificateId} no longer exists.");

            string hash = reader["ReportHash"] == DBNull.Value ? string.Empty : Convert.ToString(reader["ReportHash"], CultureInfo.InvariantCulture) ?? string.Empty;
            bool hasSnapshot = Convert.ToInt32(reader["HasNativeSnapshot"], CultureInfo.InvariantCulture) == 1;
            string condition = isPrm
                ? BuildPrmLegacyCondition(hash, hasSnapshot)
                : hasSnapshot ? string.Empty : "No native issue snapshot";

            return new LockedCertificate
            {
                Module = isPrm ? "PRM" : "WATER",
                CertificateID = Convert.ToInt32(reader["CertificateID"], CultureInfo.InvariantCulture),
                CertificateNumber = Convert.ToString(reader["CertificateNumber"], CultureInfo.InvariantCulture) ?? string.Empty,
                SampleID = Convert.ToInt32(reader["SampleID"], CultureInfo.InvariantCulture),
                IssueDate = Convert.ToDateTime(reader["IssueDate"], CultureInfo.InvariantCulture),
                CertificateStatus = Convert.ToString(reader["CertificateStatus"], CultureInfo.InvariantCulture) ?? string.Empty,
                LegacyCondition = condition,
                HasNativeSnapshot = hasSnapshot,
                ReportHash = hash
            };
        }

        private static void ValidateLegacyCandidate(LockedCertificate current, DateTime snapshotCutover)
        {
            if (current.IssueDate >= snapshotCutover)
                throw new InvalidOperationException($"Certificate {current.CertificateNumber} is not a pre-cutover legacy certificate and cannot be reconciled through this workflow.");

            if (current.Module == "PRM")
            {
                if (!current.CertificateStatus.Equals("Active", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"PRM certificate {current.CertificateNumber} is no longer Active.");
                if (current.HasNativeSnapshot && current.ReportHash.Trim().Length == 64)
                    throw new InvalidOperationException($"PRM certificate {current.CertificateNumber} no longer has an unresolved legacy evidence condition.");
            }
            else
            {
                if (!current.CertificateStatus.Equals("Active", StringComparison.OrdinalIgnoreCase) &&
                    !current.CertificateStatus.Equals("Issued", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Water/general certificate {current.CertificateNumber} is no longer Active/Issued.");
                if (current.HasNativeSnapshot)
                    throw new InvalidOperationException($"Water/general certificate {current.CertificateNumber} now has a native snapshot and must not be reconciled as legacy evidence.");
            }
        }

        private static string BuildPrmLegacyCondition(string reportHash, bool hasSnapshot)
        {
            var findings = new List<string>();
            if (reportHash.Trim().Length != 64)
                findings.Add("Legacy/non-current report hash");
            if (!hasSnapshot)
                findings.Add("No native issue snapshot");
            return string.Join("; ", findings);
        }

        private List<LegacyCertificateRow> GetSelectedCertificates()
        {
            return gridCertificates.SelectedItems
                .Cast<object>()
                .OfType<LegacyCertificateRow>()
                .OrderBy(row => row.Module)
                .ThenBy(row => row.CertificateID)
                .ToList();
        }

        private string GetSelectedDisposition()
        {
            if (cboDisposition.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag.Trim();
            return string.Empty;
        }

        private static void ValidateReconciliationEvidenceQuality(string disposition, string evidenceReference, string evidenceSummary, string reason)
        {
            bool isRetain = disposition.Equals(RetainDisposition, StringComparison.OrdinalIgnoreCase);
            bool isReissue = disposition.Equals(ReissueDisposition, StringComparison.OrdinalIgnoreCase);

            if (!isRetain && !isReissue)
                throw new InvalidOperationException("Unsupported QA disposition for legacy certificate reconciliation.");

            if (IsPlaceholderEvidenceText(evidenceReference))
                throw new InvalidOperationException(
                    "Controlled Evidence Reference must identify an actual reviewed record or document. " +
                    "Placeholder text such as NA/not found is not acceptable for any signed reconciliation.");

            if (IsPlaceholderEvidenceText(evidenceSummary))
                throw new InvalidOperationException(
                    "Evidence Summary must describe the actual record/evidence reviewed and the observed legacy condition. " +
                    "Placeholder text such as NA/not found is not acceptable for any signed reconciliation.");

            if (IsPlaceholderEvidenceText(reason))
                throw new InvalidOperationException(
                    "Reconciliation Reason must explain the QA decision. Placeholder text such as NA/not found is not acceptable for any signed reconciliation.");

            if (isRetain && evidenceSummary.Length < 20)
                throw new InvalidOperationException(
                    "Retain as documented legacy historical record requires a meaningful summary of the controlled historical evidence actually reviewed. " +
                    "If sufficient historical evidence is unavailable, use Controlled cancellation/reissue required.");
        }

        private void ConfigureSourceWorkflowButton(IReadOnlyCollection<LegacyCertificateRow> selected)
        {
            LegacyCertificateRow? row = selected.Count == 1 ? selected.First() : null;
            bool ready = row != null &&
                         row.LatestReconciliationID.HasValue &&
                         row.Disposition.Equals(ReissueDisposition, StringComparison.OrdinalIgnoreCase) &&
                         !row.HasEvidenceQualityIssue &&
                         row.SampleID > 0;

            BtnOpenSourceWorkflow.IsEnabled = ready;
            BtnOpenSourceWorkflow.Content = row == null
                ? "Open Controlled Reissue Workflow"
                : row.Module.Equals("WATER", StringComparison.OrdinalIgnoreCase)
                    ? "Open Water Reissue Workflow"
                    : row.Module.Equals("PRM", StringComparison.OrdinalIgnoreCase)
                        ? "Open PRM Reissue Workflow"
                        : "Open Controlled Reissue Workflow";

            BtnOpenSourceWorkflow.ToolTip = ready
                ? "Open the original controlled certificate workflow for this sample. Cancellation/reissue remains subject to the source workflow permissions, electronic signatures, quality gates and audit trail."
                : "A valid signed CONTROLLED_REISSUE_REQUIRED disposition for one selected certificate is required before routing to the source reissue workflow.";
        }

        private void BtnOpenSourceWorkflow_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                List<LegacyCertificateRow> selected = GetSelectedCertificates();
                if (selected.Count != 1)
                    throw new InvalidOperationException("Select exactly one certificate before opening the controlled source reissue workflow.");

                LegacyCertificateRow row = selected[0];
                if (!row.LatestReconciliationID.HasValue ||
                    !row.Disposition.Equals(ReissueDisposition, StringComparison.OrdinalIgnoreCase) ||
                    row.HasEvidenceQualityIssue)
                {
                    throw new InvalidOperationException(
                        "A valid signed CONTROLLED_REISSUE_REQUIRED disposition with meaningful evidence is required before the source reissue workflow can be opened from this screen.");
                }

                EnsurePermission();

                string instructions = row.Module.Equals("WATER", StringComparison.OrdinalIgnoreCase)
                    ? "Water Results will open with the source sample loaded. Complete the controlled lifecycle in the source workflow: Cancel COA with a documented reason and electronic signature, then issue the replacement certificate through Certificate so the new immutable snapshot is created."
                    : "PRM Results will open with the source sample selected and the signed QA reconciliation reason prefilled. Use Reissue Legacy Certificate; standalone cancellation is disabled in this routed mode so cancellation and linked replacement issuance remain one controlled transaction with a native immutable snapshot.";

                MessageBoxResult confirmation = MessageBox.Show(
                    instructions + "\n\nThe reconciliation screen will remain open and will re-check the certificate lifecycle when the source workflow closes. Continue?",
                    "Open Controlled Reissue Workflow",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (confirmation != MessageBoxResult.Yes)
                    return;

                if (_isSupersedingCorrectionDraft)
                {
                    MessageBoxResult discardDraft = MessageBox.Show(
                        "An unsaved superseding correction draft is open. It is not required to complete the already signed reissue disposition. Discard the unsaved draft and continue to the controlled source reissue workflow?",
                        "Discard Unsaved Superseding Draft",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (discardDraft != MessageBoxResult.Yes)
                        return;

                    _isSupersedingCorrectionDraft = false;
                    ResetSupersedingExpectation();
                    DisplaySignedReconciliation(row);
                    SetEntryEditMode(false, showSupersedingAction: true);
                    BtnSave.Content = "Sign and Save Reconciliation";
                }

                SourceReissueState before = ReadSourceReissueState(row);
                Window workflow = CreateSourceReissueWorkflow(row);
                workflow.Owner = this;
                workflow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                workflow.WindowState = WindowState.Maximized;
                workflow.ShowDialog();

                SourceReissueState after = ReadSourceReissueState(row);
                bool lifecycleChanged = before.LegacyCertificateActive != after.LegacyCertificateActive ||
                                        before.ReplacementCertificateID != after.ReplacementCertificateID ||
                                        before.ReplacementHasNativeSnapshot != after.ReplacementHasNativeSnapshot ||
                                        before.ReplacementHasValidHash != after.ReplacementHasValidHash;
                if (lifecycleChanged)
                    RequiresPreflightRefresh = true;

                if (after.HasCompliantReplacement)
                {
                    LoadCertificates();
                    RequiresPreflightRefresh = true;
                    lblStatus.Text =
                        $"Controlled reissue completed for {row.CertificateNumber}. Replacement {after.ReplacementCertificateNumber} is active and has native immutable issue evidence. Close this window to refresh System Preflight.";
                }
                else if (!after.LegacyCertificateActive)
                {
                    // Keep the original row selected in this session even though the source query would now
                    // exclude the cancelled legacy certificate. This preserves the direct routing action so
                    // the user can reopen the source workflow and finish replacement issuance immediately.
                    RequiresPreflightRefresh = true;
                    ConfigureSourceWorkflowButton(new[] { row });
                    lblStatus.Text =
                        $"Legacy certificate {row.CertificateNumber} is cancelled/inactive, but a compliant linked replacement certificate was not detected. Use {BtnOpenSourceWorkflow.Content} again to complete replacement issuance; System Preflight remains fail-closed.";
                }
                else
                {
                    LoadCertificates();
                    lblStatus.Text =
                        $"Source workflow closed without completing the controlled reissue for {row.CertificateNumber}. The legacy certificate remains active and the Preflight warning remains.";
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open source certificate reissue workflow from legacy reconciliation.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Open controlled source reissue workflow"),
                    "Certificate Reissue Workflow",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static Window CreateSourceReissueWorkflow(LegacyCertificateRow row)
        {
            if (row.Module.Equals("WATER", StringComparison.OrdinalIgnoreCase))
            {
                IServiceProvider services = App.ServiceProvider
                    ?? throw new InvalidOperationException("Application services are not available.");
                if (services.GetService(typeof(ResultsEntry)) is not ResultsEntry waterResults)
                    throw new InvalidOperationException("Water Results workflow could not be created.");

                waterResults.ConfigureLegacyCertificateReissueContext(
                    row.SampleID,
                    row.CertificateID,
                    row.LatestReconciliationID ?? 0,
                    row.CertificateNumber,
                    row.ReconciliationReason);
                waterResults.LoadSample(row.SampleID);
                return waterResults;
            }

            if (row.Module.Equals("PRM", StringComparison.OrdinalIgnoreCase))
            {
                return new ProductionRawMaterialResults(
                    row.SampleID,
                    row.CertificateID,
                    row.LatestReconciliationID ?? 0,
                    row.CertificateNumber,
                    row.ReconciliationReason);
            }

            throw new InvalidOperationException($"Unsupported certificate module '{row.Module}'.");
        }

        private static SourceReissueState ReadSourceReissueState(LegacyCertificateRow row)
        {
            SqlParameter[] parameters =
            {
                new SqlParameter("@CertificateID", SqlDbType.Int) { Value = row.CertificateID }
            };

            string query;
            if (row.Module.Equals("PRM", StringComparison.OrdinalIgnoreCase))
            {
                query = @"
SELECT
    CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(oldc.CertificateStatus,N''))))=N'ACTIVE' THEN 1 ELSE 0 END AS LegacyCertificateActive,
    replacement.CertificateID AS ReplacementCertificateID,
    replacement.CertificateNumber AS ReplacementCertificateNumber,
    CASE WHEN replacement.CertificateID IS NOT NULL
              AND UPPER(LTRIM(RTRIM(ISNULL(replacement.CertificateStatus,N''))))=N'ACTIVE'
         THEN 1 ELSE 0 END AS ReplacementActive,
    CASE WHEN replacement.CertificateID IS NOT NULL
              AND LEN(LTRIM(RTRIM(ISNULL(replacement.ReportHash,N''))))=64
         THEN 1 ELSE 0 END AS ReplacementHasValidHash,
    CASE WHEN replacement.CertificateID IS NOT NULL
              AND EXISTS(SELECT 1 FROM dbo.PRM_CertificateSnapshots snap WHERE snap.CertificateID=replacement.CertificateID)
         THEN 1 ELSE 0 END AS ReplacementHasNativeSnapshot
FROM dbo.PRM_Certificates oldc
OUTER APPLY
(
    SELECT TOP(1) c2.CertificateID,c2.CertificateNumber,c2.CertificateStatus,c2.ReportHash
    FROM dbo.PRM_Certificates c2
    WHERE c2.ReissuedFromCertificateID=oldc.CertificateID
    ORDER BY c2.CertificateID DESC
) replacement
WHERE oldc.CertificateID=@CertificateID;";
            }
            else if (row.Module.Equals("WATER", StringComparison.OrdinalIgnoreCase))
            {
                query = @"
SELECT
    CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(oldc.CertificateStatus)),N''),ISNULL(oldc.Status,N''))))) IN(N'ACTIVE',N'ISSUED')
         THEN 1 ELSE 0 END AS LegacyCertificateActive,
    replacement.CertificateID AS ReplacementCertificateID,
    replacement.CertificateNumber AS ReplacementCertificateNumber,
    CASE WHEN replacement.CertificateID IS NOT NULL
              AND UPPER(LTRIM(RTRIM(ISNULL(NULLIF(LTRIM(RTRIM(replacement.CertificateStatus)),N''),ISNULL(replacement.Status,N''))))) IN(N'ACTIVE',N'ISSUED')
         THEN 1 ELSE 0 END AS ReplacementActive,
    CASE WHEN replacement.CertificateID IS NOT NULL
              AND LEN(LTRIM(RTRIM(ISNULL(replacement.ReportHash,N''))))=64
         THEN 1 ELSE 0 END AS ReplacementHasValidHash,
    CASE WHEN replacement.CertificateID IS NOT NULL
              AND EXISTS(SELECT 1 FROM dbo.CertificateDocumentSnapshots snap WHERE snap.CertificateID=replacement.CertificateID)
         THEN 1 ELSE 0 END AS ReplacementHasNativeSnapshot
FROM dbo.Certificates oldc
OUTER APPLY
(
    SELECT TOP(1) c2.CertificateID,c2.CertificateNumber,c2.CertificateStatus,c2.Status,c2.ReportHash
    FROM dbo.Certificates c2
    WHERE c2.ReissuedFromCertificateID=oldc.CertificateID
    ORDER BY c2.CertificateID DESC
) replacement
WHERE oldc.CertificateID=@CertificateID;";
            }
            else
            {
                throw new InvalidOperationException($"Unsupported certificate module '{row.Module}'.");
            }

            DataTable state = DatabaseHelper.ExecuteQuery(query, parameters);
            if (state.Rows.Count != 1)
                throw new InvalidOperationException($"Certificate {row.CertificateNumber} could not be re-evaluated after the source workflow closed.");

            DataRow result = state.Rows[0];
            return new SourceReissueState
            {
                LegacyCertificateActive = Convert.ToInt32(result["LegacyCertificateActive"], CultureInfo.InvariantCulture) == 1,
                ReplacementCertificateID = result["ReplacementCertificateID"] == DBNull.Value ? null : Convert.ToInt32(result["ReplacementCertificateID"], CultureInfo.InvariantCulture),
                ReplacementCertificateNumber = Convert.ToString(result["ReplacementCertificateNumber"], CultureInfo.InvariantCulture) ?? string.Empty,
                ReplacementActive = Convert.ToInt32(result["ReplacementActive"], CultureInfo.InvariantCulture) == 1,
                ReplacementHasValidHash = Convert.ToInt32(result["ReplacementHasValidHash"], CultureInfo.InvariantCulture) == 1,
                ReplacementHasNativeSnapshot = Convert.ToInt32(result["ReplacementHasNativeSnapshot"], CultureInfo.InvariantCulture) == 1
            };
        }

        private void BtnPrepareReissue_Click(object sender, RoutedEventArgs e)
        {
            List<LegacyCertificateRow> selected = GetSelectedCertificates();
            if (selected.Count == 1 && selected[0].LatestReconciliationID.HasValue && !_isSupersedingCorrectionDraft)
            {
                MessageBox.Show(
                    "The existing QA disposition is signed and read-only. Use Start Superseding Correction before preparing replacement evidence.",
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (selected.Count != 1)
            {
                MessageBox.Show(
                    "Select one certificate to prepare a controlled reissue draft.",
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PrepareControlledReissueDraft(selected[0]);
            lblStatus.Text =
                "CONTROLLED_REISSUE_REQUIRED draft prepared from the displayed PharmaLIMS legacy condition. " +
                "Review and confirm the wording and Evidence Reference before electronic signature.";
        }

        private void PrepareControlledReissueDraft(LegacyCertificateRow row)
        {
            // A superseding reissue draft must not silently inherit a prior evidence reference.
            // The current controlled system record is the evidence for the observed missing-snapshot/hash condition;
            // QA may replace this reference only after reviewing another actual controlled record.
            txtEvidenceReference.Text = $"PharmaLIMS certificate record {row.CertificateNumber}; Runtime System Preflight";

            txtEvidenceSummary.Text =
                $"Current PharmaLIMS record review confirms that certificate {row.CertificateNumber} predates immutable certificate-snapshot control and has the observed legacy condition: {row.LegacyCondition}. " +
                "This reconciliation does not create or imply a retrospective issue snapshot or report hash.";

            txtReason.Text =
                "Controlled cancellation/reissue is required because the active legacy certificate lacks the native immutable issue evidence required by the current controlled certificate workflow. " +
                "The original historical certificate remains unchanged and traceable.";

            SelectDisposition(ReissueDisposition);
        }

        private static bool IsPlaceholderEvidenceText(string value)
        {
            string normalized = (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Replace("\t", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("/", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace(";", string.Empty, StringComparison.Ordinal)
                .Replace(",", string.Empty, StringComparison.Ordinal)
                .Replace("(", string.Empty, StringComparison.Ordinal)
                .Replace(")", string.Empty, StringComparison.Ordinal)
                .Replace("[", string.Empty, StringComparison.Ordinal)
                .Replace("]", string.Empty, StringComparison.Ordinal)
                .Replace("{", string.Empty, StringComparison.Ordinal)
                .Replace("}", string.Empty, StringComparison.Ordinal)
                .Replace("\\", string.Empty, StringComparison.Ordinal)
                .Replace("|", string.Empty, StringComparison.Ordinal);

            int batchMetadataIndex = normalized.IndexOf("BATCHRECONCILIATIONID", StringComparison.Ordinal);
            if (batchMetadataIndex > 0)
                normalized = normalized[..batchMetadataIndex];

            return normalized.Length == 0 || normalized is
                "NA" or
                "NONE" or
                "NOTAPPLICABLE" or
                "NOTAVAILABLE" or
                "NOTFOUND" or
                "NOTFOUNDRESULT" or
                "RESULTNOTFOUND" or
                "NOEVIDENCE" or
                "EVIDENCENOTFOUND" or
                "EVIDENCENOTAVAILABLE" or
                "NOCONTROLLEDEVIDENCE" or
                "NODOCUMENTEDEVIDENCE" or
                "MISSING" or
                "UNKNOWN";
        }

        private void SelectDisposition(string disposition)
        {
            foreach (object item in cboDisposition.Items)
            {
                if (item is ComboBoxItem combo && combo.Tag is string tag && tag.Equals(disposition, StringComparison.OrdinalIgnoreCase))
                {
                    cboDisposition.SelectedItem = combo;
                    return;
                }
            }
            cboDisposition.SelectedIndex = 0;
        }

        private void ClearEntryFields()
        {
            txtEvidenceReference.Text = string.Empty;
            txtEvidenceSummary.Text = string.Empty;
            txtReason.Text = string.Empty;
            cboDisposition.SelectedIndex = 0;
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsurePermission();
                EnsureSchema();
                LoadCertificates();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to refresh legacy certificate evidence reconciliation.", ex);
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex, "Refresh certificate reconciliation"),
                    "Certificate Reconciliation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private sealed class SourceReissueState
        {
            public bool LegacyCertificateActive { get; init; }
            public int? ReplacementCertificateID { get; init; }
            public string ReplacementCertificateNumber { get; init; } = string.Empty;
            public bool ReplacementActive { get; init; }
            public bool ReplacementHasValidHash { get; init; }
            public bool ReplacementHasNativeSnapshot { get; init; }
            public bool HasCompliantReplacement =>
                ReplacementCertificateID.HasValue &&
                ReplacementActive &&
                ReplacementHasValidHash &&
                ReplacementHasNativeSnapshot;
        }

        private sealed class LockedCertificate
        {
            public string Module { get; init; } = string.Empty;
            public int CertificateID { get; init; }
            public string CertificateNumber { get; init; } = string.Empty;
            public int SampleID { get; init; }
            public DateTime IssueDate { get; init; }
            public string CertificateStatus { get; init; } = string.Empty;
            public string LegacyCondition { get; init; } = string.Empty;
            public bool HasNativeSnapshot { get; init; }
            public string ReportHash { get; init; } = string.Empty;
        }

        private sealed class LegacyCertificateRow
        {
            public LegacyCertificateRow(DataRow row)
            {
                Module = Convert.ToString(row["CertificateModule"], CultureInfo.InvariantCulture) ?? string.Empty;
                CertificateID = Convert.ToInt32(row["CertificateID"], CultureInfo.InvariantCulture);
                CertificateNumber = Convert.ToString(row["CertificateNumber"], CultureInfo.InvariantCulture) ?? string.Empty;
                SampleID = Convert.ToInt32(row["SampleID"], CultureInfo.InvariantCulture);
                IssueDate = Convert.ToDateTime(row["IssueDate"], CultureInfo.InvariantCulture);
                CertificateStatus = Convert.ToString(row["CertificateStatus"], CultureInfo.InvariantCulture) ?? string.Empty;
                LegacyCondition = Convert.ToString(row["LegacyCondition"], CultureInfo.InvariantCulture) ?? string.Empty;
                LatestReconciliationID = row["ReconciliationID"] == DBNull.Value ? null : Convert.ToInt32(row["ReconciliationID"], CultureInfo.InvariantCulture);
                EvidenceReference = Convert.ToString(row["EvidenceReference"], CultureInfo.InvariantCulture) ?? string.Empty;
                EvidenceSummary = Convert.ToString(row["EvidenceSummary"], CultureInfo.InvariantCulture) ?? string.Empty;
                Disposition = Convert.ToString(row["Disposition"], CultureInfo.InvariantCulture) ?? string.Empty;
                ReconciliationReason = Convert.ToString(row["Reason"], CultureInfo.InvariantCulture) ?? string.Empty;
                ReconciledBy = Convert.ToString(row["SignedBy"], CultureInfo.InvariantCulture) ?? string.Empty;
                SignedAt = row["SignedAt"] == DBNull.Value ? null : Convert.ToDateTime(row["SignedAt"], CultureInfo.InvariantCulture);
            }

            public string Module { get; }
            public int CertificateID { get; }
            public string CertificateNumber { get; }
            public int SampleID { get; }
            public DateTime IssueDate { get; }
            public string IssueDateText => IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            public string CertificateStatus { get; }
            public string LegacyCondition { get; }
            public int? LatestReconciliationID { get; }
            public string EvidenceReference { get; }
            public string EvidenceSummary { get; }
            public string Disposition { get; }
            public string ReconciliationReason { get; }
            public string ReconciledBy { get; }
            public DateTime? SignedAt { get; }
            public bool HasEvidenceQualityIssue =>
                LatestReconciliationID.HasValue &&
                (IsPlaceholderEvidenceText(EvidenceReference) ||
                 IsPlaceholderEvidenceText(EvidenceSummary) ||
                 IsPlaceholderEvidenceText(ReconciliationReason));

            public bool HasRetainEvidenceQualityIssue =>
                HasEvidenceQualityIssue &&
                Disposition.Equals(RetainDisposition, StringComparison.OrdinalIgnoreCase);

            public bool HasReissueEvidenceQualityIssue =>
                HasEvidenceQualityIssue &&
                Disposition.Equals(ReissueDisposition, StringComparison.OrdinalIgnoreCase);

            public string ReconciliationStatus => LatestReconciliationID.HasValue
                ? HasRetainEvidenceQualityIssue
                    ? $"#{LatestReconciliationID} — Retain evidence invalid; supersede"
                    : HasReissueEvidenceQualityIssue
                        ? $"#{LatestReconciliationID} — Reissue evidence incomplete; supersede"
                        : $"#{LatestReconciliationID} — {FriendlyDisposition(Disposition)}"
                : "Not reconciled";

            private static string FriendlyDisposition(string disposition)
            {
                return disposition switch
                {
                    RetainDisposition => "Legacy retained",
                    ReissueDisposition => "Reissue required",
                    _ => string.IsNullOrWhiteSpace(disposition) ? "N/A" : disposition
                };
            }
        }
    }
}
