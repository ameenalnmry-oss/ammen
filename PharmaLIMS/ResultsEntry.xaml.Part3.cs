#nullable disable
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace PharmaLIMS
{
    public partial class ResultsEntry
    {
        private DataTable _loadedWaterSnapshot;
        private readonly Dictionary<int, string> _loadedWaterDisplayValues = new Dictionary<int, string>();

        private static DataTable ReadWaterRows(
            SqlConnection connection, SqlTransaction transaction, string sql, params SqlParameter[] parameters)
        {
            using SqlCommand command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.AddRange(parameters);
            using SqlDataReader reader = command.ExecuteReader();
            DataTable rows = new DataTable();
            rows.Load(reader);
            return rows;
        }

        private ResultItem WaterItemFromEvidence(DataRow row)
        {
            ResultItem item = new ResultItem
            {
                SampleTestID = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture),
                TestID = row["TestID"] == DBNull.Value ? 0 : Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture),
                TestName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? "",
                Unit = Convert.ToString(row["Unit"], CultureInfo.InvariantCulture) ?? "",
                AlertLimit = row["AlertLimit"] == DBNull.Value ? null : Convert.ToDecimal(row["AlertLimit"], CultureInfo.InvariantCulture),
                ActionLimit = row["ActionLimit"] == DBNull.Value ? null : Convert.ToDecimal(row["ActionLimit"], CultureInfo.InvariantCulture),
                SpecificationText = Convert.ToString(row["LimitDescription"], CultureInfo.InvariantCulture) ?? "",
                ResultValue = Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture) ?? "",
                Remarks = Convert.ToString(row["Remarks"], CultureInfo.InvariantCulture) ?? ""
            };
            // Preserve the established legacy water-profile policy, but use only
            // the locked row's test identity and specifications, never grid metadata.
            if (Convert.ToInt32(row["HasSpecSnapshot"], CultureInfo.InvariantCulture) == 0)
                ApplyEffectiveSpecification(item);
            return item;
        }

        private bool PersistWaterEdits(
            SqlConnection connection, SqlTransaction transaction, ElectronicSignature signature)
        {
            DataTable locked = ReadWaterRows(connection, transaction, WaterResultSnapshotSql.Build(true),
                new SqlParameter("@sampleId", currentSampleId));
            ResultSnapshotGuard.EnsureMatches(_loadedWaterSnapshot, locked, "SampleTestID", "SampleID", currentSampleId);

            DataTable visible = locked.Clone();
            foreach (DataRow row in locked.Rows)
                if (!IsRemovedTest(Convert.ToString(row["TestName"]))) visible.ImportRow(row);
            ResultSnapshotGuard.EnsureVisibleKeys(resultItems.Select(item => item.SampleTestID), visible, "SampleTestID");

            Dictionary<int, DataRow> byId = locked.Rows.Cast<DataRow>()
                .ToDictionary(row => Convert.ToInt32(row["SampleTestID"]));
            bool anyResultChanged = false;
            var expectedWrittenValues = new Dictionary<int, decimal>();
            foreach (ResultItem edited in resultItems.OrderBy(item => item.SampleTestID))
            {
                DataRow source = byId[edited.SampleTestID];
                ResultItem authoritative = WaterItemFromEvidence(source);
                if (!_loadedWaterDisplayValues.TryGetValue(edited.SampleTestID, out string originalDisplay))
                    throw new DBConcurrencyException("The original water result display is missing. Reload the sample.");

                bool inputEdited = !string.Equals(edited.ResultValue ?? "", originalDisplay, StringComparison.Ordinal);
                object resultValue = source["ResultValue"];
                if (inputEdited)
                {
                    if (string.IsNullOrWhiteSpace(edited.ResultValue))
                    {
                        if (source["ResultValue"] != DBNull.Value)
                            throw new InvalidOperationException("An existing result cannot be silently cleared. Use the controlled result correction/invalidation workflow.");
                    }
                    else
                    {
                        authoritative.ResultValue = edited.ResultValue;
                        if (!TryNormalizeResultForSave(authoritative, out decimal normalized))
                            throw new InvalidOperationException("Invalid result value for test: " + authoritative.TestName);
                        resultValue = GetPersistedResultValue(authoritative, normalized);
                    }
                }

                // Notes-only saves retain the exact database numeric value and all
                // result-status/specification evidence, including its original precision.
                bool resultChanged = !ResultSnapshotGuard.Equivalent(resultValue, source["ResultValue"]);
                authoritative.ResultValue = Convert.ToString(resultValue, CultureInfo.InvariantCulture) ?? "";
                authoritative.Remarks = (edited.Remarks ?? "").Trim();
                string status = CalculatePassFail(authoritative);
                if ((status == "OOS" || status == "ALERT") && authoritative.Remarks.Length == 0)
                    throw new InvalidOperationException("Remarks are required for " + status + " test " + authoritative.TestName + ".");
                object remarks = authoritative.Remarks.Length == 0 ? DBNull.Value : authoritative.Remarks;
                bool notesChanged = !ResultSnapshotGuard.Equivalent(remarks, source["Remarks"]);
                if (!resultChanged && !notesChanged) continue;

                string setClause = resultChanged
                    ? @"ResultValue=@resultValue, Remarks=@remarks, ResultStatus=@resultStatus,
                        DeviationType=@deviationType"
                    : "Remarks=@remarks";
                DataTable delta = ReadWaterRows(connection, transaction, @"
DECLARE @Changes TABLE(OldValue nvarchar(max), NewValue nvarchar(max));
UPDATE dbo.SampleTests SET " + setClause + @"
OUTPUT CONCAT(N'ResultValue=',deleted.ResultValue,N'; Remarks=',deleted.Remarks,
              N'; ResultStatus=',deleted.ResultStatus,N'; DeviationType=',deleted.DeviationType,
              N'; LimitDescription=',deleted.LimitDescription),
       CONCAT(N'ResultValue=',inserted.ResultValue,N'; Remarks=',inserted.Remarks,
              N'; ResultStatus=',inserted.ResultStatus,N'; DeviationType=',inserted.DeviationType,
              N'; LimitDescription=',inserted.LimitDescription)
INTO @Changes
WHERE SampleTestID=@sampleTestId AND SampleID=@sampleId AND ResultRowVersion=@expectedVersion;
SELECT OldValue,NewValue FROM @Changes;",
                    new SqlParameter("@resultValue", SqlDbType.Decimal) { Precision = 18, Scale = 4, Value = resultChanged ? resultValue : DBNull.Value },
                    new SqlParameter("@remarks", SqlDbType.NVarChar, -1) { Value = remarks },
                    new SqlParameter("@resultStatus", SqlDbType.NVarChar, 50) { Value = status },
                    new SqlParameter("@deviationType", SqlDbType.NVarChar, 100) { Value = (object)BuildDeviationType(authoritative, status) ?? DBNull.Value },
                    new SqlParameter("@sampleTestId", edited.SampleTestID),
                    new SqlParameter("@sampleId", currentSampleId),
                    new SqlParameter("@expectedVersion", SqlDbType.Binary, 8) { Value = source["ResultRowVersion"] });
                if (delta.Rows.Count != 1)
                    throw new DBConcurrencyException("The water result changed or is missing. No changes were committed.");
                if (resultChanged)
                {
                    decimal expectedValue = Convert.ToDecimal(resultValue, CultureInfo.InvariantCulture);
                    expectedWrittenValues.Add(edited.SampleTestID, expectedValue);
                }
                anyResultChanged |= resultChanged;
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "SampleTests", edited.SampleTestID,
                    resultChanged ? "Water Result Entry" : "Water Result Remarks",
                    Convert.ToString(delta.Rows[0]["OldValue"]), Convert.ToString(delta.Rows[0]["NewValue"]),
                    signature.Reason, signature.SignedBy, resultChanged ? "ResultValue" : "Remarks",
                    authoritative.TestName, GetSignatureRecordNumber(), "Water");
            }
            if (expectedWrittenValues.Count > 0)
            {
                // Re-read after all updates/triggers, under the same parent/set locks.
                // A mismatch aborts this transaction, including its signatures/audit.
                DataTable saved = ReadWaterRows(connection, transaction, WaterResultSnapshotSql.Build(true),
                    new SqlParameter("@sampleId", currentSampleId));
                var persisted = saved.Rows.Cast<DataRow>()
                    .ToDictionary(row => Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture));
                foreach (KeyValuePair<int, decimal> expected in expectedWrittenValues)
                {
                    if (!persisted.TryGetValue(expected.Key, out DataRow row) ||
                        !WaterResultValueContract.StoredValueMatches(expected.Value, row["ResultValue"]))
                        throw new InvalidOperationException("Water result precision changed during persistence. " +
                            "No results were committed; review the ResultValue storage contract.");
                }
            }
            return anyResultChanged;
        }

        private void UpdateWaterEvidenceReconciliationButton(bool certificateStateVerified, bool hasActiveCertificate, bool locked)
        {
            bool evidenceStateVerified = true;
            bool hasMissingEvidence = false;
            try
            {
                hasMissingEvidence = IsWaterSampleType(currentSampleType) && HasMissingWaterLimitEvidence();
            }
            catch (Exception ex)
            {
                evidenceStateVerified = false;
                ApplicationLogger.Error(
                    $"Workflow button refresh could not verify immutable Water specification evidence for SampleID {currentSampleId}.", ex);
            }

            BtnReconcileWaterEvidence.Visibility = IsWaterSampleType(currentSampleType)
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnReconcileWaterEvidence.IsEnabled =
                certificateStateVerified &&
                evidenceStateVerified &&
                hasMissingEvidence &&
                CanApproveSample() &&
                !locked &&
                !hasActiveCertificate;
        }

        private bool HasMissingWaterLimitEvidence()
        {
            if (currentSampleId <= 0)
                return false;

            DataTable rows = DatabaseHelper.ExecuteQuery(@"
SELECT TOP (1) st.SampleTestID
FROM dbo.SampleTests st
INNER JOIN dbo.Samples s ON s.SampleID=st.SampleID
WHERE st.SampleID=@SampleID
  AND s.SampleType IN(N'Purified Water',N'Potable Water')
  AND NULLIF(LTRIM(RTRIM(ISNULL(st.LimitDescription,N''))),N'') IS NULL;",
                new[] { new SqlParameter("@SampleID", currentSampleId) });
            return rows.Rows.Count > 0;
        }

        private static string ExtractAuditLimitDescription(string auditValue)
        {
            const string marker = "LimitDescription=";
            if (string.IsNullOrWhiteSpace(auditValue))
                return string.Empty;

            int index = auditValue.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                return string.Empty;

            return auditValue.Substring(index + marker.Length).Trim();
        }

        private void BtnReconcileWaterEvidence_Click(object sender, RoutedEventArgs e)
        {
            if (_isWorkflowBusy || currentSampleId <= 0)
                return;
            if (!IsWaterSampleType(currentSampleType))
            {
                MessageBox.Show("Water evidence reconciliation is available only for water samples.",
                    "Water Evidence Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!CanApproveSample())
            {
                MessageBox.Show("QA approval permission is required to reconcile immutable Water specification evidence.",
                    "Permission Denied", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string statusBefore = GetSampleStatus();
            if (IsLockedStatus(statusBefore) || DatabaseHelper.HasCertificate(currentSampleId))
            {
                MessageBox.Show("Water specification evidence is locked after review/approval or while an active certificate exists.",
                    "Water Evidence Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!DatabaseHelper.HasOpenQualityEvent(currentSampleId))
            {
                MessageBox.Show("An open Quality Event is required before post-control Water specification evidence can be reconciled.",
                    "Water Evidence Reconciliation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DataTable missing = DatabaseHelper.ExecuteQuery(@"
SELECT st.SampleTestID,COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName
FROM dbo.SampleTests st
LEFT JOIN dbo.Tests t ON t.TestID=st.TestID
WHERE st.SampleID=@SampleID
  AND NULLIF(LTRIM(RTRIM(ISNULL(st.LimitDescription,N''))),N'') IS NULL
ORDER BY st.SampleTestID;",
                new[] { new SqlParameter("@SampleID", currentSampleId) });
            if (missing.Rows.Count == 0)
            {
                MessageBox.Show("No missing immutable Water specification snapshots were found for this sample.",
                    "Water Evidence Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateWorkflowButtons();
                return;
            }

            string testNames = string.Join(", ", missing.Rows.Cast<DataRow>()
                .Select(row => Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            MessageBoxResult confirmation = MessageBox.Show(
                "This controlled action will restore only missing LimitDescription snapshots from the original append-only Water Result Entry audit evidence, and only when that evidence exactly matches the approved specification effective when the sample was registered.\n\n" +
                "Results, statuses, remarks, Quality Events, and certificates will not be changed.\n\n" +
                "Affected tests: " + testNames + "\n\nContinue?",
                "Water Evidence Reconciliation", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
                return;

            ElectronicSignature signatureWindow = _serviceProvider.GetRequiredService<ElectronicSignature>();
            signatureWindow.Configure(GetSignatureRecordNumber(), currentUser, "Water Evidence Reconciliation", true);
            signatureWindow.Owner = this;
            if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                return;

            string sampleNumber = GetSignatureRecordNumber();
            _isWorkflowBusy = true;
            SetButtonBusy(BtnReconcileWaterEvidence, true, "Reconciling...", "Reconcile Evidence");
            try
            {
                int reconciledCount = 0;
                ExecuteInLocalTransaction((connection, transaction) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection, transaction, signatureWindow.SignedBy, "CanQaApproveResults",
                        "reconcile immutable Water specification evidence");
                    string lockedStatus = LockAndValidateSampleStatusInTransaction(
                        connection, transaction, statusBefore, "Water evidence reconciliation");
                    if (IsLockedStatus(lockedStatus))
                        throw new InvalidOperationException("Water specification evidence is locked because the sample has entered review or approval.");

                    int activeCertificates = Convert.ToInt32(ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.Certificates WITH (UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
  AND ISNULL(IsCancelled,0)=0
  AND ISNULL(CertificateStatus,ISNULL(Status,N'Active')) IN (N'Active',N'Issued');",
                        new SqlParameter("@SampleID", currentSampleId)), CultureInfo.InvariantCulture);
                    if (activeCertificates > 0)
                        throw new InvalidOperationException("An active certificate now exists. Water evidence reconciliation was cancelled.");

                    object qualityEventValue = ExecuteScalarInTransaction(connection, transaction, @"
SELECT TOP (1) EventNumber
FROM dbo.QualityEvents WITH (UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
  AND UPPER(LTRIM(RTRIM(ISNULL(CurrentStatus,N'OPEN')))) NOT IN(N'CLOSED',N'QA CLOSED',N'CANCELLED',N'REJECTED CLOSED')
ORDER BY QualityEventID DESC;",
                        new SqlParameter("@SampleID", currentSampleId));
                    string qualityEventNumber = Convert.ToString(qualityEventValue, CultureInfo.InvariantCulture) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(qualityEventNumber))
                        throw new InvalidOperationException("The required open Quality Event is no longer available. Reconciliation was cancelled.");

                    DataTable candidates = ReadWaterRows(connection, transaction, @"
SELECT
    st.SampleTestID,
    st.TestID,
    COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName,
    st.ResultRowVersion,
    s.SampleNumber,
    s.SampleType,
    s.PointCodeSnapshot,
    ISNULL(s.CreatedDate,s.SamplingDateTime) AS RegistrationDate,
    auditEvidence.AuditID,
    auditEvidence.OldValue AS AuditOldValue,
    auditEvidence.NewValue AS AuditNewValue,
    approved.SpecificationText AS ApprovedSpecificationText
FROM dbo.SampleTests st WITH (UPDLOCK,HOLDLOCK)
INNER JOIN dbo.Samples s WITH (HOLDLOCK) ON s.SampleID=st.SampleID
LEFT JOIN dbo.Tests t ON t.TestID=st.TestID
OUTER APPLY
(
    SELECT TOP (1) a.AuditID,a.OldValue,a.NewValue
    FROM dbo.AuditTrail a WITH (HOLDLOCK)
    WHERE a.TableName=N'SampleTests'
      AND a.RecordID=st.SampleTestID
      AND a.Action=N'Water Result Entry'
      AND (a.SampleNumber=s.SampleNumber OR a.SampleNumber IS NULL)
      AND CHARINDEX(N'LimitDescription=',ISNULL(a.OldValue,N''))>0
    ORDER BY a.AuditID ASC
) auditEvidence
OUTER APPLY
(
    SELECT TOP (1) specification.SpecificationText
    FROM dbo.WaterTestProfiles profile
    INNER JOIN dbo.WaterSpecifications specification ON specification.ProfileID=profile.ProfileID
    WHERE UPPER(LTRIM(RTRIM(profile.ProfileCode)))=
          CASE
              WHEN UPPER(LTRIM(RTRIM(s.SampleType))) IN(N'PURIFIED WATER',N'PW') THEN N'PW'
              WHEN UPPER(LTRIM(RTRIM(s.SampleType))) IN(N'POTABLE WATER',N'PTW') THEN N'PTW'
              ELSE N''
          END
      AND ISNULL(profile.IsActive,0)=1
      AND profile.ApprovalStatus=N'Approved'
      AND profile.ApprovedBy IS NOT NULL
      AND profile.ApprovedAt IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(profile.ControlledReference,N''))),N'') IS NOT NULL
      AND (profile.EffectiveFrom IS NULL OR profile.EffectiveFrom<=CAST(ISNULL(s.CreatedDate,s.SamplingDateTime) AS date))
      AND (profile.EffectiveTo IS NULL OR profile.EffectiveTo>=CAST(ISNULL(s.CreatedDate,s.SamplingDateTime) AS date))
      AND specification.TestID=st.TestID
      AND ISNULL(specification.IsActive,0)=1
      AND specification.ApprovalStatus=N'Approved'
      AND specification.ApprovedBy IS NOT NULL
      AND specification.ApprovedAt IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(specification.SpecificationText)),N'') IS NOT NULL
      AND (specification.EffectiveFrom IS NULL OR specification.EffectiveFrom<=CAST(ISNULL(s.CreatedDate,s.SamplingDateTime) AS date))
      AND (specification.EffectiveTo IS NULL OR specification.EffectiveTo>=CAST(ISNULL(s.CreatedDate,s.SamplingDateTime) AS date))
      AND (specification.PointCode IS NULL OR specification.PointCode=N'' OR specification.PointCode=ISNULL(s.PointCodeSnapshot,N''))
    ORDER BY
      CASE WHEN specification.PointCode=ISNULL(s.PointCodeSnapshot,N'') THEN 0 ELSE 1 END,
      specification.EffectiveFrom DESC,
      specification.SpecificationID DESC
) approved
WHERE st.SampleID=@SampleID
  AND s.SampleType IN(N'Purified Water',N'Potable Water')
  AND NULLIF(LTRIM(RTRIM(ISNULL(st.LimitDescription,N''))),N'') IS NULL
ORDER BY st.SampleTestID;",
                        new SqlParameter("@SampleID", currentSampleId));

                    if (candidates.Rows.Count == 0)
                        throw new DBConcurrencyException("No missing Water specification evidence remains. Reload the sample.");

                    foreach (DataRow row in candidates.Rows)
                    {
                        int sampleTestId = Convert.ToInt32(row["SampleTestID"], CultureInfo.InvariantCulture);
                        int auditId = row["AuditID"] == DBNull.Value ? 0 : Convert.ToInt32(row["AuditID"], CultureInfo.InvariantCulture);
                        string auditOldSpecification = ExtractAuditLimitDescription(
                            Convert.ToString(row["AuditOldValue"], CultureInfo.InvariantCulture) ?? string.Empty);
                        string auditNewSpecification = ExtractAuditLimitDescription(
                            Convert.ToString(row["AuditNewValue"], CultureInfo.InvariantCulture) ?? string.Empty);
                        string approvedSpecification = Convert.ToString(row["ApprovedSpecificationText"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        string testName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? sampleTestId.ToString(CultureInfo.InvariantCulture);

                        if (auditId <= 0 || string.IsNullOrWhiteSpace(auditOldSpecification))
                            throw new InvalidOperationException("Original immutable audit evidence is unavailable for " + testName + ". No reconciliation was committed.");
                        if (!string.IsNullOrWhiteSpace(auditNewSpecification))
                            throw new InvalidOperationException("The audit history for " + testName + " does not show the known snapshot-loss pattern. No reconciliation was committed.");
                        if (string.IsNullOrWhiteSpace(approvedSpecification) ||
                            !string.Equals(auditOldSpecification, approvedSpecification, StringComparison.Ordinal))
                            throw new InvalidOperationException("Original audit evidence does not exactly match the approved registration-time specification for " + testName + ". No reconciliation was committed.");

                        int affected = ExecuteNonQueryInTransaction(connection, transaction, @"
UPDATE dbo.SampleTests
SET LimitDescription=@LimitDescription
WHERE SampleTestID=@SampleTestID
  AND SampleID=@SampleID
  AND NULLIF(LTRIM(RTRIM(ISNULL(LimitDescription,N''))),N'') IS NULL
  AND ResultRowVersion=@ExpectedVersion;",
                            new SqlParameter("@LimitDescription", SqlDbType.NVarChar, 500) { Value = approvedSpecification },
                            new SqlParameter("@SampleTestID", sampleTestId),
                            new SqlParameter("@SampleID", currentSampleId),
                            new SqlParameter("@ExpectedVersion", SqlDbType.Binary, 8) { Value = row["ResultRowVersion"] });
                        if (affected != 1)
                            throw new DBConcurrencyException("Water specification evidence changed during reconciliation. No changes were committed.");

                        DatabaseHelper.AddAuditTrailAdvanced(
                            connection,
                            transaction,
                            "SampleTests",
                            sampleTestId,
                            "Water Specification Snapshot Reconciled",
                            "LimitDescription=<missing>",
                            "LimitDescription=" + approvedSpecification + "; RestoredFromAuditID=" + auditId.ToString(CultureInfo.InvariantCulture) + "; QualityEvent=" + qualityEventNumber,
                            signatureWindow.Reason,
                            signatureWindow.SignedBy,
                            "LimitDescription",
                            testName,
                            sampleNumber,
                            "Water");
                        reconciledCount++;
                    }

                    AddSampleElectronicSignatureInTransaction(
                        connection,
                        transaction,
                        "Water Evidence Reconciliation",
                        "QA-controlled restoration of immutable Water registration specification evidence; " + qualityEventNumber,
                        signatureWindow.Reason,
                        signatureWindow.SignedBy,
                        signerRole);
                });

                LoadSampleFromUi(sampleNumber);
                lblStatus.Text = "Water specification evidence reconciled from original audit evidence.";
                MessageBox.Show(
                    reconciledCount.ToString(CultureInfo.InvariantCulture) +
                    " missing Water specification snapshot(s) were restored from original audit evidence. Results and workflow status were not changed.",
                    "Water Evidence Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Water specification evidence reconciliation failed.", ex);
                MessageBox.Show("Water evidence reconciliation failed: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Water Evidence Reconciliation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isWorkflowBusy = false;
                SetButtonBusy(BtnReconcileWaterEvidence, false, "Reconciling...", "Reconcile Evidence");
                UpdateWorkflowButtons();
            }
        }

        private (int Completed, int Passed, int Alerts, int Oos, int Pending) ReadWaterSummary(
            SqlConnection connection, SqlTransaction transaction)
        {
            DataTable rows = ReadWaterRows(connection, transaction, WaterResultSnapshotSql.Build(true),
                new SqlParameter("@sampleId", currentSampleId));
            int completed = 0, passed = 0, alerts = 0, oos = 0, pending = 0;
            foreach (DataRow row in rows.Rows)
            {
                if (row["ResultValue"] == DBNull.Value) { pending++; continue; }
                if (IsRemovedTest(Convert.ToString(row["TestName"], CultureInfo.InvariantCulture))) continue;
                completed++;
                string status = CalculatePassFail(WaterItemFromEvidence(row));
                if (status == "PASS") passed++;
                else if (status == "ALERT") alerts++;
                else if (status == "OOS") oos++;
                else throw new InvalidOperationException("A persisted water result cannot be assessed. No workflow change was committed.");
            }
            return (completed, passed, alerts, oos, pending);
        }
    }
}
