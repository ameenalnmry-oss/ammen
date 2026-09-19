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
    public partial class EMLegacySnapshotReconciliation : Window
    {
        private readonly int _eventId;
        private readonly string _eventNo;
        private readonly ObservableCollection<LegacyPlateRow> _plates = new();
        private LegacyPlateRow? _selected;
        private bool _suppressSelectionChanged;

        public bool WasSaved { get; private set; }

        /// <summary>
        /// eventId == 0 opens the controlled all-unresolved-historical-events batch view, including closed records.
        /// Each saved row remains an independent append-only reconciliation record; original EM plate rows are never rewritten.
        /// </summary>
        public EMLegacySnapshotReconciliation(int eventId, string eventNo)
        {
            InitializeComponent();
            _eventId = eventId;
            _eventNo = eventNo?.Trim() ?? string.Empty;
            lblEvent.Text = eventId == 0
                ? "EM Events: All unresolved historical events (active and closed)"
                : $"EM Event: {(_eventNo.Length == 0 ? _eventId.ToString(CultureInfo.InvariantCulture) : _eventNo)}";
            Loaded += Window_Loaded;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsurePermission();
                EnsureSchema();
                LoadPlates();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open historical EM snapshot reconciliation.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Historical EM snapshot reconciliation"), "EM Reconciliation", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private static void EnsurePermission()
        {
            if (string.IsNullOrWhiteSpace(Login.CurrentUser) || !DatabaseHelper.CanApproveResults(Login.CurrentUser))
                throw new UnauthorizedAccessException("QA approval permission is required to reconcile historical EM limit evidence.");
        }

        private static void EnsureSchema()
        {
            int ready = Convert.ToInt32(DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN OBJECT_ID(N'dbo.EM_LimitSnapshotReconciliations',N'U') IS NOT NULL
 AND OBJECT_ID(N'dbo.TRG_EM_LimitSnapshotReconciliations_AppendOnly_20260828',N'TR') IS NOT NULL
 AND COL_LENGTH(N'dbo.EM_EventPlates',N'AlertLimitSnapshot') IS NOT NULL
 AND COL_LENGTH(N'dbo.EM_EventPlates',N'ActionLimitSnapshot') IS NOT NULL
 AND COL_LENGTH(N'dbo.EM_EventPlates',N'ResultUnitSnapshot') IS NOT NULL
 AND COL_LENGTH(N'dbo.EM_EventPlates',N'AirVolumeLitersSnapshot') IS NOT NULL
 THEN 1 ELSE 0 END;"), CultureInfo.InvariantCulture);
            if (ready != 1)
                throw new InvalidOperationException("Historical EM reconciliation schema is not installed. Apply controlled migration 20260828_003 through Database Maintenance first.");
        }

        private void LoadPlates()
        {
            _plates.Clear();
            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT P.Id AS PlateID,P.EventId,
       ISNULL(NULLIF(LTRIM(RTRIM(E.EventNo)),N''),N'EventID ' + CONVERT(nvarchar(20),E.Id)) AS EventNo,
       P.PlateCode,P.Method,ISNULL(A.Grade,N'') Grade,
       CASE WHEN P.AlertLimitSnapshot IS NOT NULL AND P.ActionLimitSnapshot IS NOT NULL
                  AND NULLIF(LTRIM(RTRIM(ISNULL(P.ResultUnitSnapshot,N''))),N'') IS NOT NULL
                  AND (UPPER(LTRIM(RTRIM(P.Method)))<>N'ACTIVE AIR SAMPLING' OR ISNULL(P.AirVolumeLitersSnapshot,0)>0)
            THEN 1 ELSE 0 END AS NativeSnapshotComplete,
       R.ReconciliationID,R.AlertLimitSnapshot AS ReconciledAlert,R.ActionLimitSnapshot AS ReconciledAction,
       R.ResultUnitSnapshot AS ReconciledUnit,R.AirVolumeLitersSnapshot AS ReconciledAirVolume,
       ISNULL(R.EvidenceReference,N'') EvidenceReference,ISNULL(R.Reason,N'') ReconciliationReason,
       ISNULL(R.SignedBy,N'') ReconciledBy,R.SignedAt,
       L.AlertLimitTotal AS CurrentAlert,L.ActionLimitTotal AS CurrentAction,L.AirVolumeLiters AS CurrentAirVolume
FROM dbo.EM_EventPlates P
INNER JOIN dbo.EM_Events E ON E.Id=P.EventId
INNER JOIN dbo.EM_Areas A ON A.Id=E.AreaId
OUTER APPLY
(
    SELECT TOP(1) X.* FROM dbo.EM_LimitSnapshotReconciliations X
    WHERE X.PlateID=P.Id ORDER BY X.ReconciliationID DESC
) R
OUTER APPLY
(
    SELECT TOP(1) G.AlertLimitTotal,G.ActionLimitTotal,G.AirVolumeLiters
    FROM dbo.EM_GradeLimits G
    WHERE ISNULL(G.IsActive,1)=1
      AND UPPER(LTRIM(RTRIM(ISNULL(G.Grade,N''))))=UPPER(LTRIM(RTRIM(ISNULL(A.Grade,N''))))
      AND UPPER(LTRIM(RTRIM(ISNULL(G.Method,N''))))=UPPER(LTRIM(RTRIM(ISNULL(P.Method,N''))))
    ORDER BY G.Id DESC
) L
WHERE (@Event=0 OR P.EventId=@Event)
ORDER BY E.EventNo,E.Id,P.SequenceNo,P.Id;", new[] { new SqlParameter("@Event", SqlDbType.Int) { Value = _eventId } });

            foreach (DataRow row in table.Rows)
            {
                bool nativeComplete = Convert.ToInt32(row["NativeSnapshotComplete"], CultureInfo.InvariantCulture) == 1;
                if (nativeComplete)
                    continue;
                _plates.Add(new LegacyPlateRow(row));
            }

            gridPlates.ItemsSource = _plates;
            if (_plates.Count > 0)
            {
                _suppressSelectionChanged = true;
                try
                {
                    gridPlates.SelectedItems.Clear();
                    gridPlates.SelectedIndex = 0;
                }
                finally
                {
                    _suppressSelectionChanged = false;
                }
                RefreshSelectionContext(clearEntryFields: true);
            }
            else
            {
                _selected = null;
                ClearFields();
                lblStatus.Text = "No historical snapshot reconciliation is required for this event scope.";
                lblSelectionSummary.Text = "No unresolved plates remain in this scope.";
            }
        }

        private void gridPlates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged)
                return;

            RefreshSelectionContext(clearEntryFields: true);
        }

        private void RefreshSelectionContext(bool clearEntryFields)
        {
            List<LegacyPlateRow> selected = GetSelectedPlates();
            _selected = selected.FirstOrDefault();

            if (_selected == null)
            {
                ClearFields();
                lblSelectionSummary.Text = "Select one plate, then use batch selection for the same Grade + Method.";
                return;
            }

            if (selected.Count == 1)
            {
                LegacyPlateRow row = selected[0];
                lblSelectedPlate.Text = $"Plate {row.PlateCode} | {row.EventNo} | {row.Grade} | {row.Method}";
                lblCurrentMaster.Text = "Current approved master (reference only): " + row.CurrentMasterText;

                if (clearEntryFields)
                {
                    if (row.LatestReconciliationID.HasValue)
                    {
                        txtAlert.Text = FormatDecimal(row.ReconciledAlert);
                        txtAction.Text = FormatDecimal(row.ReconciledAction);
                        txtUnit.Text = row.ReconciledUnit;
                        txtAirVolume.Text = row.ReconciledAirVolume?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                        txtEvidenceReference.Text = row.EvidenceReference;
                        txtReason.Text = string.Empty;
                        lblStatus.Text = "This plate already has signed reconciliation evidence. Saving again will append a signed superseding correction.";
                    }
                    else
                    {
                        ClearEntryFields();
                        lblStatus.Text = "Enter values proven by controlled historical evidence. Current master values are reference only.";
                    }
                }

                txtAirVolume.IsEnabled = IsActiveAir(row.Method);
                BtnSave.Content = "Sign and Save Reconciliation";
                lblSelectionSummary.Text = row.LatestReconciliationID.HasValue
                    ? "1 plate selected; it already has signed evidence."
                    : "1 unreconciled plate selected. Use 'Select all matching unreconciled' to create a controlled batch.";
                return;
            }

            bool sameGradeMethod = SelectedRowsAreCompatible(selected);
            int eventCount = selected.Select(x => x.EventID).Distinct().Count();
            string grade = _selected.Grade;
            string method = _selected.Method;

            lblSelectedPlate.Text = $"{selected.Count} plates selected | {grade} | {method} | {eventCount} event(s)";
            lblCurrentMaster.Text = BuildBatchCurrentMasterReference(selected);
            txtAirVolume.IsEnabled = sameGradeMethod && IsActiveAir(method);
            BtnSave.Content = $"Sign and Save Selected Reconciliations ({selected.Count})";

            if (!sameGradeMethod)
            {
                lblStatus.Text = "Batch save is blocked: all selected plates must have the same Grade and Method.";
                lblSelectionSummary.Text = $"{selected.Count} selected — incompatible Grade/Method mix.";
                return;
            }

            int alreadyReconciled = selected.Count(x => x.LatestReconciliationID.HasValue);
            lblSelectionSummary.Text = $"{selected.Count} selected across {eventCount} event(s); {alreadyReconciled} already reconciled.";
            lblStatus.Text = alreadyReconciled == 0
                ? "Batch mode: one controlled evidence set and one electronic signature will append one independent record per selected plate."
                : "Batch save is blocked when any selected plate already has signed evidence. Superseding corrections must be signed one plate at a time.";

            if (clearEntryFields)
                ClearEntryFields();
        }

        private void BtnSelectMatching_Click(object sender, RoutedEventArgs e)
        {
            LegacyPlateRow? anchor = gridPlates.SelectedItem as LegacyPlateRow ?? _selected;
            if (anchor == null)
            {
                MessageBox.Show("Select a plate first.", "EM Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            List<LegacyPlateRow> matching = _plates
                .Where(row => !row.LatestReconciliationID.HasValue &&
                              SameText(row.Grade, anchor.Grade) &&
                              SameText(row.Method, anchor.Method))
                .ToList();

            if (matching.Count == 0)
            {
                MessageBox.Show("No unreconciled plates match the selected Grade + Method in this scope.", "EM Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _suppressSelectionChanged = true;
            try
            {
                gridPlates.SelectedItems.Clear();
                foreach (LegacyPlateRow row in matching)
                    gridPlates.SelectedItems.Add(row);
                gridPlates.ScrollIntoView(matching[0]);
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            RefreshSelectionContext(clearEntryFields: true);
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveSelectedReconciliations();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to save historical EM snapshot reconciliation.", ex);
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Historical EM snapshot reconciliation"), "EM Reconciliation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveSelectedReconciliations()
        {
            List<LegacyPlateRow> selected = GetSelectedPlates();
            if (selected.Count == 0)
            {
                MessageBox.Show("Select at least one EM plate first.", "EM Reconciliation", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EnsurePermission();

            if (!SelectedRowsAreCompatible(selected))
                throw new InvalidOperationException("Batch reconciliation requires every selected plate to have the same Grade and Method.");

            if (selected.Count > 1 && selected.Any(row => row.LatestReconciliationID.HasValue))
                throw new InvalidOperationException("Batch reconciliation cannot include a plate that already has signed evidence. Superseding corrections must be performed one plate at a time.");

            if (!TryDecimal(txtAlert.Text, out decimal alert) || alert < 0m)
                throw new InvalidOperationException("Enter the historically approved non-negative Alert Limit.");
            if (!TryDecimal(txtAction.Text, out decimal actionLimit) || actionLimit < alert)
                throw new InvalidOperationException("Enter the historically approved Action Limit; it cannot be lower than the Alert Limit.");

            string unit = txtUnit.Text.Trim();
            if (string.IsNullOrWhiteSpace(unit))
                throw new InvalidOperationException("Historical Result Unit is required.");

            string method = selected[0].Method;
            int? airVolume = null;
            if (IsActiveAir(method))
            {
                if (!int.TryParse(txtAirVolume.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
                    throw new InvalidOperationException("A positive historical air volume is required for Active Air Sampling.");
                airVolume = parsed;
            }

            string evidence = txtEvidenceReference.Text.Trim();
            string reason = txtReason.Text.Trim();
            if (string.IsNullOrWhiteSpace(evidence))
                throw new InvalidOperationException("Controlled Evidence Reference is required.");
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Reconciliation Reason is required.");

            int eventCount = selected.Select(row => row.EventID).Distinct().Count();
            string grade = selected[0].Grade;
            string signatureScope = selected.Count == 1
                ? $"{selected[0].EventNo} / {selected[0].PlateCode}"
                : $"Batch: {selected.Count} plates / {eventCount} events / {grade} / {method}";

            string signatureAction = selected.Count == 1
                ? "Reconcile Historical EM Limit Snapshot"
                : "Batch Reconcile Historical EM Limit Snapshots";

            var signature = new ElectronicSignature(signatureScope, Login.CurrentUser, signatureAction, true) { Owner = this };
            if (signature.ShowDialog() != true || !signature.IsConfirmed)
                return;

            string batchId = selected.Count > 1
                ? Guid.NewGuid().ToString("N").ToUpperInvariant()
                : string.Empty;
            var reconciliationIds = new List<int>();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string role = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection,
                    transaction,
                    signature.SignedBy,
                    "CanApproveResults",
                    selected.Count == 1
                        ? "reconcile historical EM limit evidence"
                        : "batch reconcile historical EM limit evidence");

                foreach (LegacyPlateRow row in selected.OrderBy(x => x.EventID).ThenBy(x => x.PlateID))
                {
                    using (SqlCommand plateLock = new SqlCommand(@"
SELECT P.AlertLimitSnapshot,P.ActionLimitSnapshot,P.ResultUnitSnapshot,P.AirVolumeLitersSnapshot,P.Method
FROM dbo.EM_EventPlates P WITH(UPDLOCK,HOLDLOCK)
WHERE P.Id=@Plate AND P.EventId=@Event;", connection, transaction))
                    {
                        plateLock.Parameters.Add("@Plate", SqlDbType.Int).Value = row.PlateID;
                        plateLock.Parameters.Add("@Event", SqlDbType.Int).Value = row.EventID;
                        using SqlDataReader reader = plateLock.ExecuteReader();
                        if (!reader.Read())
                            throw new DBConcurrencyException($"EM plate {row.EventNo} / {row.PlateCode} no longer exists in its event.");

                        bool nativeComplete = reader["AlertLimitSnapshot"] != DBNull.Value &&
                            reader["ActionLimitSnapshot"] != DBNull.Value &&
                            !string.IsNullOrWhiteSpace(Convert.ToString(reader["ResultUnitSnapshot"], CultureInfo.InvariantCulture)) &&
                            (!IsActiveAir(Convert.ToString(reader["Method"], CultureInfo.InvariantCulture) ?? string.Empty) ||
                             (reader["AirVolumeLitersSnapshot"] != DBNull.Value &&
                              Convert.ToInt32(reader["AirVolumeLitersSnapshot"], CultureInfo.InvariantCulture) > 0));

                        if (nativeComplete)
                            throw new InvalidOperationException($"{row.EventNo} / {row.PlateCode} now has a complete native frozen snapshot; reconciliation is no longer permitted.");
                    }

                    int? latestId = null;
                    string oldValue = "Unresolved historical snapshot";
                    using (SqlCommand latest = new SqlCommand(@"
SELECT TOP(1) ReconciliationID,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot,EvidenceReference,SignedBy,SignedAt
FROM dbo.EM_LimitSnapshotReconciliations WITH(UPDLOCK,HOLDLOCK)
WHERE PlateID=@Plate ORDER BY ReconciliationID DESC;", connection, transaction))
                    {
                        latest.Parameters.Add("@Plate", SqlDbType.Int).Value = row.PlateID;
                        using SqlDataReader reader = latest.ExecuteReader();
                        if (reader.Read())
                        {
                            latestId = Convert.ToInt32(reader["ReconciliationID"], CultureInfo.InvariantCulture);
                            oldValue = $"Reconciliation #{latestId}; Alert={reader["AlertLimitSnapshot"]}; Action={reader["ActionLimitSnapshot"]}; Unit={reader["ResultUnitSnapshot"]}; AirVolume={reader["AirVolumeLitersSnapshot"]}; Evidence={reader["EvidenceReference"]}; SignedBy={reader["SignedBy"]}; SignedAt={reader["SignedAt"]}";
                        }
                    }

                    if (selected.Count > 1 && latestId.HasValue)
                        throw new DBConcurrencyException($"{row.EventNo} / {row.PlateCode} was reconciled by another action before this batch was saved. No batch record has been committed.");

                    string storedReason = selected.Count > 1
                        ? $"{reason}{Environment.NewLine}Batch reconciliation ID: {batchId}; signed scope: {selected.Count} plates / {eventCount} events / {grade} / {method}."
                        : reason;

                    using SqlCommand insert = new SqlCommand(@"
DECLARE @InsertedReconciliation TABLE(ReconciliationID INT NOT NULL);

INSERT dbo.EM_LimitSnapshotReconciliations
(PlateID,SupersedesReconciliationID,AlertLimitSnapshot,ActionLimitSnapshot,ResultUnitSnapshot,AirVolumeLitersSnapshot,
 EvidenceReference,Reason,SignedBy,UserRole,MeaningOfSignature,SignedAt,SourceWorkstation,ReconciliationSchemaVersion)
OUTPUT INSERTED.ReconciliationID INTO @InsertedReconciliation(ReconciliationID)
VALUES(@Plate,@Supersedes,@Alert,@Action,@Unit,@AirVolume,@Evidence,@Reason,@User,@Role,@Meaning,SYSUTCDATETIME(),@Workstation,1);

SELECT TOP(1) ReconciliationID
FROM @InsertedReconciliation;", connection, transaction);

                    insert.Parameters.Add("@Plate", SqlDbType.Int).Value = row.PlateID;
                    insert.Parameters.Add("@Supersedes", SqlDbType.Int).Value = latestId.HasValue ? latestId.Value : DBNull.Value;
                    SqlParameter alertParameter = insert.Parameters.Add("@Alert", SqlDbType.Decimal);
                    alertParameter.Precision = 18;
                    alertParameter.Scale = 3;
                    alertParameter.Value = alert;
                    SqlParameter actionParameter = insert.Parameters.Add("@Action", SqlDbType.Decimal);
                    actionParameter.Precision = 18;
                    actionParameter.Scale = 3;
                    actionParameter.Value = actionLimit;
                    insert.Parameters.Add("@Unit", SqlDbType.NVarChar, 30).Value = unit;
                    insert.Parameters.Add("@AirVolume", SqlDbType.Int).Value = airVolume.HasValue ? airVolume.Value : DBNull.Value;
                    insert.Parameters.Add("@Evidence", SqlDbType.NVarChar, 500).Value = evidence;
                    insert.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = storedReason;
                    insert.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                    insert.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = role;
                    insert.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning;
                    insert.Parameters.Add("@Workstation", SqlDbType.NVarChar, 200).Value = Environment.MachineName;

                    int reconciliationId = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
                    reconciliationIds.Add(reconciliationId);

                    string batchSuffix = selected.Count > 1 ? $"; BatchID={batchId}" : string.Empty;
                    string newValue = $"Reconciliation #{reconciliationId}; Alert={alert.ToString(CultureInfo.InvariantCulture)}; Action={actionLimit.ToString(CultureInfo.InvariantCulture)}; Unit={unit}; AirVolume={(airVolume.HasValue ? airVolume.Value.ToString(CultureInfo.InvariantCulture) : "N/A")}; Evidence={evidence}{batchSuffix}";
                    string auditReason = storedReason +
                        " | Evidence: " + evidence +
                        " | Meaning: " + signature.Meaning +
                        (string.IsNullOrWhiteSpace(signature.Reason) ? string.Empty : " | E-signature reason: " + signature.Reason);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "EM_LimitSnapshotReconciliations",
                        reconciliationId,
                        latestId.HasValue ? "EM Historical Snapshot Reconciliation Superseded" :
                        selected.Count > 1 ? "EM Historical Snapshot Batch Reconciled" :
                        "EM Historical Snapshot Reconciled",
                        oldValue,
                        newValue,
                        auditReason,
                        signature.SignedBy,
                        "LimitSnapshotEvidence",
                        row.Method,
                        row.EventNo,
                        "Environmental Monitoring");
                }
            });

            WasSaved = true;
            if (selected.Count == 1)
            {
                int reconciliationId = reconciliationIds[0];
                lblStatus.Text = $"Signed reconciliation #{reconciliationId} saved. The original EM plate record was not changed.";
            }
            else
            {
                lblStatus.Text = $"Signed batch {batchId} saved for {reconciliationIds.Count} plates. Each plate received an independent append-only reconciliation record.";
            }

            int savedCount = reconciliationIds.Count;
            LoadPlates();

            MessageBox.Show(
                selected.Count == 1
                    ? "Historical EM evidence was appended successfully. Reload the EM event to use the signed reconciled snapshot."
                    : $"Historical EM evidence was appended successfully for {savedCount} plates in one controlled batch. Each plate has its own immutable reconciliation record. Batch ID: {batchId}",
                "EM Reconciliation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private List<LegacyPlateRow> GetSelectedPlates() =>
            gridPlates.SelectedItems.Cast<object>()
                .OfType<LegacyPlateRow>()
                .Distinct()
                .ToList();

        private static bool SelectedRowsAreCompatible(IReadOnlyList<LegacyPlateRow> rows)
        {
            if (rows.Count == 0)
                return false;

            string grade = rows[0].Grade;
            string method = rows[0].Method;
            return rows.All(row => SameText(row.Grade, grade) && SameText(row.Method, method));
        }

        private static string BuildBatchCurrentMasterReference(IReadOnlyList<LegacyPlateRow> rows)
        {
            if (rows.Count == 0)
                return "Current approved master: N/A";

            LegacyPlateRow first = rows[0];
            bool sameReference = rows.All(row =>
                row.CurrentAlert == first.CurrentAlert &&
                row.CurrentAction == first.CurrentAction &&
                row.CurrentAirVolume == first.CurrentAirVolume);

            return sameReference
                ? "Current approved master (reference only): " + first.CurrentMasterText
                : "Current approved master references differ across the selected plates. They are reference-only and are never copied into historical evidence.";
        }

        private static bool SameText(string left, string right) =>
            string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

        private void BtnRefresh_Click(object sender, RoutedEventArgs e) => LoadPlates();
        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void ClearFields()
        {
            lblSelectedPlate.Text = "Select a plate.";
            lblCurrentMaster.Text = "Current approved master: N/A";
            ClearEntryFields();
            txtAirVolume.IsEnabled = false;
            BtnSave.Content = "Sign and Save Selected Reconciliation(s)";
        }

        private void ClearEntryFields()
        {
            txtAlert.Text = string.Empty;
            txtAction.Text = string.Empty;
            txtUnit.Text = string.Empty;
            txtAirVolume.Text = string.Empty;
            txtEvidenceReference.Text = string.Empty;
            txtReason.Text = string.Empty;
        }

        private static bool TryDecimal(string text, out decimal value) =>
            decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
            decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        private static bool IsActiveAir(string method) =>
            method.Trim().Equals("Active Air Sampling", StringComparison.OrdinalIgnoreCase);

        private static string FormatDecimal(decimal? value) =>
            value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;

        public sealed class LegacyPlateRow
        {
            public LegacyPlateRow(DataRow row)
            {
                PlateID = Convert.ToInt32(row["PlateID"], CultureInfo.InvariantCulture);
                EventID = Convert.ToInt32(row["EventID"], CultureInfo.InvariantCulture);
                EventNo = Convert.ToString(row["EventNo"], CultureInfo.InvariantCulture) ?? string.Empty;
                PlateCode = Convert.ToString(row["PlateCode"], CultureInfo.InvariantCulture) ?? string.Empty;
                Method = Convert.ToString(row["Method"], CultureInfo.InvariantCulture) ?? string.Empty;
                Grade = Convert.ToString(row["Grade"], CultureInfo.InvariantCulture) ?? string.Empty;
                LatestReconciliationID = row["ReconciliationID"] == DBNull.Value ? null : Convert.ToInt32(row["ReconciliationID"], CultureInfo.InvariantCulture);
                ReconciledAlert = row["ReconciledAlert"] == DBNull.Value ? null : Convert.ToDecimal(row["ReconciledAlert"], CultureInfo.InvariantCulture);
                ReconciledAction = row["ReconciledAction"] == DBNull.Value ? null : Convert.ToDecimal(row["ReconciledAction"], CultureInfo.InvariantCulture);
                ReconciledUnit = Convert.ToString(row["ReconciledUnit"], CultureInfo.InvariantCulture) ?? string.Empty;
                ReconciledAirVolume = row["ReconciledAirVolume"] == DBNull.Value ? null : Convert.ToInt32(row["ReconciledAirVolume"], CultureInfo.InvariantCulture);
                EvidenceReference = Convert.ToString(row["EvidenceReference"], CultureInfo.InvariantCulture) ?? string.Empty;
                ReconciliationReason = Convert.ToString(row["ReconciliationReason"], CultureInfo.InvariantCulture) ?? string.Empty;
                ReconciledBy = Convert.ToString(row["ReconciledBy"], CultureInfo.InvariantCulture) ?? string.Empty;
                SignedAt = row["SignedAt"] == DBNull.Value ? null : Convert.ToDateTime(row["SignedAt"], CultureInfo.InvariantCulture);
                CurrentAlert = row["CurrentAlert"] == DBNull.Value ? null : Convert.ToDecimal(row["CurrentAlert"], CultureInfo.InvariantCulture);
                CurrentAction = row["CurrentAction"] == DBNull.Value ? null : Convert.ToDecimal(row["CurrentAction"], CultureInfo.InvariantCulture);
                CurrentAirVolume = row["CurrentAirVolume"] == DBNull.Value ? null : Convert.ToInt32(row["CurrentAirVolume"], CultureInfo.InvariantCulture);
            }

            public int PlateID { get; }
            public int EventID { get; }
            public string EventNo { get; }
            public string PlateCode { get; }
            public string Method { get; }
            public string Grade { get; }
            public int? LatestReconciliationID { get; }
            public decimal? ReconciledAlert { get; }
            public decimal? ReconciledAction { get; }
            public string ReconciledUnit { get; }
            public int? ReconciledAirVolume { get; }
            public string EvidenceReference { get; }
            public string ReconciliationReason { get; }
            public string ReconciledBy { get; }
            public DateTime? SignedAt { get; }
            public decimal? CurrentAlert { get; }
            public decimal? CurrentAction { get; }
            public int? CurrentAirVolume { get; }
            public string SnapshotStatus => "Native snapshot incomplete";
            public string ReconciliationStatus => LatestReconciliationID.HasValue
                ? $"#{LatestReconciliationID} by {ReconciledBy} at {SignedAt:yyyy-MM-dd HH:mm}"
                : "Not reconciled";
            public string CurrentMasterText => CurrentAlert.HasValue && CurrentAction.HasValue
                ? $"Alert {CurrentAlert:0.###}; Action {CurrentAction:0.###}; Air volume {(CurrentAirVolume.HasValue ? CurrentAirVolume.Value.ToString(CultureInfo.InvariantCulture) + " L" : "N/A")}"
                : "No current approved Grade/Method limit found";
        }
    }
}
