#nullable enable
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using PharmaLIMS.TrustedSigning;
using System;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PharmaLIMS;

public partial class ResultsEntry
{
    private string? _loadedTrustedWaterSnapshotHash;
    private WaterWorkflowSnapshot? _loadedTrustedWaterSnapshot;

    private static WaterWorkflowSnapshot ReadTrustedWaterSnapshot(int sampleId)
    {
        WaterWorkflowSnapshot? snapshot = null;
        // The shared query uses SELECT-only HOLDLOCK; no service privileges or schema changes.
        DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            snapshot = WaterWorkflowSnapshot.Load(connection, transaction, sampleId));
        return snapshot ?? throw new InvalidOperationException("Water workflow snapshot could not be loaded.");
    }

    private WaterWorkflowSnapshot? CaptureTrustedWaterLoadSnapshot(string sampleNumber)
    {
        DataTable identities = DatabaseHelper.ExecuteQuery(
            "SELECT SampleID FROM dbo.Samples WHERE SampleNumber=@sampleNumber;",
            new[] { new SqlParameter("@sampleNumber", sampleNumber) });
        if (identities.Rows.Count == 0) return null;
        if (identities.Rows.Count != 1) throw new DBConcurrencyException("Ambiguous Water sample number. Loading was blocked.");
        return ReadTrustedWaterSnapshot(Convert.ToInt32(identities.Rows[0]["SampleID"], CultureInfo.InvariantCulture));
    }

    private void FinishTrustedWaterLoadSnapshot(WaterWorkflowSnapshot? before, int sampleId)
    {
        _loadedTrustedWaterSnapshotHash = null;
        _loadedTrustedWaterSnapshot = null;
        WaterWorkflowSnapshot after = ReadTrustedWaterSnapshot(sampleId);
        if (before is null || _loadedWaterSnapshot is null ||
            !string.Equals(before.Hash, after.Hash, StringComparison.Ordinal) ||
            Convert.ToInt32(after.Header.Rows[0]["SampleID"], CultureInfo.InvariantCulture) != sampleId ||
            !string.Equals(Convert.ToString(after.Header.Rows[0]["SampleNumber"], CultureInfo.InvariantCulture), GetSignatureRecordNumber(), StringComparison.Ordinal) ||
            !string.Equals(Convert.ToString(after.Header.Rows[0]["Status"], CultureInfo.InvariantCulture), GetSampleStatus(), StringComparison.Ordinal))
            throw new DBConcurrencyException("The Water sample changed while its results were loading. Reload before signing.");
        // Only the original hash that bracketed the visible header/grid is eligible for signing.
        _loadedTrustedWaterSnapshot = before;
        _loadedTrustedWaterSnapshotHash = before.Hash;
    }

    private WaterWorkflowRow[] CaptureTrustedWaterVisibleRows() => resultItems.Select(item => new WaterWorkflowRow(
        item.SampleTestID, item.ResultValue ?? "", item.Remarks ?? "", item.EquipmentID,
        item.SupportingEquipmentCodes ?? "", item.TestKitCode ?? "", item.TestKitLot ?? "", LocalTime(item.TestKitExpiry),
        item.LesSampleTemperatureC ?? "", item.LesVerificationReference ?? "", item.LesVerificationConfirmed,
        item.LesExecutionRemarks ?? "")).OrderBy(row => row.SampleTestId).ToArray();

    private static DateTime? LocalTime(DateTime? value) => value.HasValue
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified) : null;

    private WaterWorkflowRequest CaptureTrustedWaterRequest(string operation, DateTime? started, WaterWorkflowTimes? times)
    {
        if (currentSampleId <= 0 || string.IsNullOrEmpty(_loadedTrustedWaterSnapshotHash))
            throw new DBConcurrencyException("Reload the complete Water sample before signing a new operation.");
        var target = new SqlConnectionStringBuilder(AppConfig.ConnectionString);
        var request = new WaterWorkflowRequest(target.DataSource, target.InitialCatalog, operation, currentSampleId,
            GetSignatureRecordNumber(), GetSampleStatus(), _loadedTrustedWaterSnapshotHash,
            LocalTime(started), times, operation is "StartAnalysis" or "CorrectTimes" ? Array.Empty<WaterWorkflowRow>() : CaptureTrustedWaterVisibleRows());
        WaterWorkflowPolicy.RequireTarget(AppConfig.ConnectionString, request, requireIntegratedSecurity: false);
        return request;
    }

    private string CaptureTrustedWaterUiState()
    {
        var target = new SqlConnectionStringBuilder(AppConfig.ConnectionString);
        return JsonSerializer.Serialize(new
        {
            SampleId = currentSampleId, SampleNumber = GetSignatureRecordNumber(), Status = GetSampleStatus(),
            Server = target.DataSource, Database = target.InitialCatalog, Actor = GetCurrentUserName(),
            LoadedSnapshot = _loadedTrustedWaterSnapshotHash, Rows = CaptureTrustedWaterVisibleRows()
        });
    }

    private void EnsureTrustedWaterUiUnchanged(string capturedUi)
    {
        CommitWaterResultGridEdits();
        if (!string.Equals(capturedUi, CaptureTrustedWaterUiState(), StringComparison.Ordinal))
        {
            TrustedApplicationSession.Clear();
            throw new DBConcurrencyException("The sample, target, account or visible input changed during signing. No request was dispatched. Reload before continuing.");
        }
    }

    private async Task RunTrustedWaterWorkflowAsync(string operation)
    {
        if (_isWorkflowBusy || _isSavingResults || _isRefreshingEquipmentAtSave || currentSampleId <= 0) return;
        _isWorkflowBusy = true;
        PendingWaterWorkflow? pending = null;
        string actor = "";
        int sampleId = currentSampleId;
        try
        {
            actor = GetCurrentUserName();
            CommitWaterResultGridEdits();
            string initialUi = CaptureTrustedWaterUiState();
            var recovery = new WaterWorkflowRecovery();
            pending = recovery.Load(actor, sampleId);
            string action = "WaterWorkflow";
            if (pending is not null)
            {
                MessageBoxResult choice = MessageBox.Show(this,
                    "An unresolved Water operation is saved for this account and sample.\n\n" +
                    "Original operation: " + pending.Payload.Operation + "\nRequest: " + pending.RequestId +
                    "\nOriginal reason: " + pending.OriginalReason + "\nOriginal meaning: " + pending.OriginalMeaning +
                    "\n\nYes: recover/retry that exact saved operation.\nNo: permanently cancel that request; an already committed result will be returned.\nCancel: leave it unresolved.",
                    "Recover Water Workflow", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.No) action = "WaterWorkflowCancel";
            }
            if (pending is null && operation == "RecoverOnly")
            {
                MessageBox.Show(this, "There is no saved Water operation for this account and sample.",
                    "Water Workflow Recovery", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DateTime? started = null;
            WaterWorkflowTimes? times = null;
            WaterWorkflowRequest payload;
            if (pending is null)
            {
                if (!await ValidateTrustedWaterInputAsync(operation)) return;
                EnsureTrustedWaterUiUnchanged(initialUi);
                if (operation == "StartAnalysis")
                {
                    DateTime? recorded = ReadNullableDateTime(_loadedTrustedWaterSnapshot!.Header.Rows[0], "AnalysisStartedDateTime");
                    if (recorded.HasValue) started = recorded.Value;
                    else
                    {
                        if (!TryPromptForAnalysisStart(out DateTime selected)) return;
                        started = selected;
                    }
                }
                else if (operation == "CorrectTimes")
                {
                    DataRow header = _loadedTrustedWaterSnapshot!.Header.Rows[0];
                    var original = new WaterTimestampSnapshot
                    {
                        SamplingDateTime = ReadNullableDateTime(header, "SamplingDateTime"),
                        ReceivedDateTime = ReadNullableDateTime(header, "ReceivedDateTime"),
                        IncubationStartedDateTime = ReadNullableDateTime(header, "IncubationStartedDateTime"),
                        IncubationEndDate = ReadNullableDateTime(header, "IncubationEndDate"),
                        AnalysisStartedDateTime = ReadNullableDateTime(header, "AnalysisStartedDateTime"),
                        AnalysisCompletedDateTime = ReadNullableDateTime(header, "AnalysisCompletedDateTime")
                    };
                    if (!TryPromptForWaterTimestampCorrection(original, out WaterTimestampSnapshot corrected)) return;
                    times = new WaterWorkflowTimes(LocalTime(corrected.SamplingDateTime), LocalTime(corrected.ReceivedDateTime),
                        LocalTime(corrected.IncubationStartedDateTime), LocalTime(corrected.IncubationEndDate), LocalTime(corrected.AnalysisStartedDateTime));
                }
                EnsureTrustedWaterUiUnchanged(initialUi);
                payload = CaptureTrustedWaterRequest(operation, started, times);
            }
            else
            {
                EnsureTrustedWaterUiUnchanged(initialUi);
                payload = pending.Payload;
            }
            // Capture and validate the entire immutable payload BEFORE a fresh signature.
            string canonical = WaterWorkflowPolicy.CanonicalPayload(payload);
            string signedUi = CaptureTrustedWaterUiState();
            var signature = _serviceProvider.GetRequiredService<ElectronicSignature>();
            signature.Configure(payload.SampleNumber, actor, TrustedWaterSignatureAction(payload.Operation), true);
            signature.Owner = this;
            if (pending is not null)
            {
                FreezeWaterRecoverySignatureChoice(signature, "cboReason", pending.OriginalReason);
                FreezeWaterRecoverySignatureChoice(signature, "cboMeaning", pending.OriginalMeaning);
            }
            if (signature.ShowDialog() != true || !signature.IsConfirmed)
            {
                TrustedApplicationSession.Clear();
                return;
            }
            if (pending is not null && (!string.Equals(signature.Reason, pending.OriginalReason, StringComparison.Ordinal) ||
                !string.Equals(signature.Meaning, pending.OriginalMeaning, StringComparison.Ordinal)))
                throw new InvalidOperationException("Recovery must retain the original signature reason and meaning.");
            if (!string.Equals(signature.SignedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The Water signature must match the active account.");
            EnsureTrustedWaterUiUnchanged(signedUi);
            if (pending is null && !string.Equals(canonical,
                WaterWorkflowPolicy.CanonicalPayload(CaptureTrustedWaterRequest(operation, started, times)), StringComparison.Ordinal))
                throw new DBConcurrencyException("The signed Water payload changed. No request was dispatched.");
            // A failed durable save prevents dispatch. Unknown service outcomes never remove this file.
            TrustedApplicationSession.RequireWaterWorkflowFrame(payload, actor,
                pending?.OriginalReason ?? signature.Reason, pending?.OriginalMeaning ?? signature.Meaning);
            pending ??= recovery.SaveNew(actor, payload, signature.Reason, signature.Meaning);
            WaterWorkflowResult result = await TrustedApplicationSession.WaterWorkflowAsync(action, pending.RequestId, pending.Payload,
                actor, pending.OriginalReason, pending.OriginalMeaning);
            if (result.State == "Pending")
                throw new InvalidOperationException("The original Water workflow remains pending. Recover or cancel the same saved request.");
            try { recovery.Complete(actor, sampleId, pending.RequestId, result.State); }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Water workflow resolved but local recovery cleanup failed; request " + pending.RequestId, ex);
                MessageBox.Show(this, "The service returned " + result.State + ". The local recovery record could not be cleared; the next recovery will return the same outcome.",
                    "Water Workflow Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // A committed receipt is historical evidence; reload current persisted state rather than assigning its old status to the window.
            await LoadSampleAsync(result.SampleNumber);
            string message = result.State == "Cancelled"
                ? "The original request was cancelled permanently. You may now prepare a new Water operation."
                : "Water operation committed: " + pending.Payload.Operation + "\nSample: " + result.SampleNumber +
                    "\nRecorded status: " + result.Status + "\nCompleted tests: " + result.CompletedTests.ToString(CultureInfo.InvariantCulture) +
                    "\nPending tests: " + result.PendingTests.ToString(CultureInfo.InvariantCulture) +
                    (string.IsNullOrWhiteSpace(result.QualityEventSummary) ? "" : "\nQuality events: " + result.QualityEventSummary);
            MessageBox.Show(this, message, "Water Workflow", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TrustedApplicationSession.Clear();
            ApplicationLogger.Error("Trusted Water workflow was not confirmed" + (pending is null ? "." : "; request " + pending.RequestId + ". Recovery retained."), ex);
            MessageBox.Show(this, UserFacingError.SafeMessage(ex) + (pending is null ? "" :
                "\n\nDo not create a replacement request. Use a Water workflow action to recover or cancel the original saved request.\nRequest: " + pending.RequestId),
                "Water Workflow Needs Attention", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isWorkflowBusy = false;
            try { UpdateWorkflowButtons(); }
            catch (Exception ex) { ApplicationLogger.Error("Water workflow button refresh failed; reload the sample before continuing.", ex); }
        }
    }

    private async void BtnRecoverWaterWorkflow_Click(object sender, RoutedEventArgs e)
    {
        if (AppConfig.TrustedSigningEnabled) await RunTrustedWaterWorkflowAsync("RecoverOnly");
    }

    private void UpdateTrustedWaterRecoveryButton()
    {
        if (BtnRecoverWaterWorkflow is null) return;
        BtnRecoverWaterWorkflow.Visibility = Visibility.Collapsed;
        BtnRecoverWaterWorkflow.IsEnabled = !_isWorkflowBusy;
        if (!AppConfig.TrustedSigningEnabled || currentSampleId <= 0) return;
        try
        {
            if (new WaterWorkflowRecovery().Load(GetCurrentUserName(), currentSampleId) is not null)
                BtnRecoverWaterWorkflow.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable pending files must stay visible and block replacement requests.
            BtnRecoverWaterWorkflow.Visibility = Visibility.Visible;
            ApplicationLogger.Error("Water recovery availability could not be verified.", ex);
        }
    }

    private static string TrustedWaterSignatureAction(string operation) => operation switch
    {
        "StartAnalysis" => "Analysis Start", "SaveResults" => "Result Entry", "SubmitReview" => "Submit for Review",
        "Review" => "Technical Review", "Approve" => "QA Approval", "CorrectTimes" => "Water Timestamp Correction",
        _ => throw new ArgumentException("Unsupported Water workflow operation.")
    };

    private static void FreezeWaterRecoverySignatureChoice(ElectronicSignature signature, string name, string original)
    {
        if (signature.FindName(name) is not ComboBox combo)
            throw new InvalidOperationException("The original Water recovery signature cannot be displayed safely.");
        combo.Items.Add(original);
        combo.SelectedIndex = combo.Items.Count - 1;
        combo.Text = original;
        combo.IsEditable = false;
        combo.IsEnabled = false;
    }

    private async Task<bool> ValidateTrustedWaterInputAsync(string operation)
    {
        if (!IsWaterSampleType(currentSampleType) || _loadedTrustedWaterSnapshot is null || string.IsNullOrEmpty(_loadedTrustedWaterSnapshotHash))
            throw new DBConcurrencyException("Reload the complete Water sample before signing.");
        string status = GetSampleStatus();
        bool permitted = operation switch
        {
            "StartAnalysis" => CanStartAnalysis(), "SaveResults" => CanEnterResults(), "SubmitReview" => CanSubmitForReview(),
            "Review" => CanReviewSample(), "Approve" => CanApproveSample(), "CorrectTimes" => CanCorrectSampleTimes(), _ => false
        };
        if (!permitted) throw new UnauthorizedAccessException("You do not have permission for this Water operation.");
        bool stateAllowed = operation switch
        {
            "StartAnalysis" => status is "Registered" or "Incubation",
            "SaveResults" => !IsLockedStatus(status) && (status is "In Analysis" or "Partially Entered" or "Results Entered" or "Alert" or "OOS" || WaterWorkflowEvidenceGuard.IsPendingCompletionStatus(status)),
            "SubmitReview" => status is "Results Entered" or "Alert" or "Released After Investigation",
            "Review" => status == "Under Review", "Approve" => status == "Reviewed",
            "CorrectTimes" => !IsLockedStatus(status) && !DatabaseHelper.HasCertificate(currentSampleId), _ => false
        };
        if (!stateAllowed) throw new InvalidOperationException("The Water sample status does not permit this operation.");
        if (operation == "SaveResults")
        {
            RefreshCalculatedStatuses();
            if (!resultItems.Any(item => !string.IsNullOrWhiteSpace(item.ResultValue)))
                throw new InvalidOperationException("No results entered.");
            if (!await RefreshWaterEquipmentBeforeSaveAsync()) return false;
            if (WaterWorkflowEvidenceGuard.IsPendingCompletionStatus(status))
                WaterWorkflowEvidenceGuard.EnsurePendingOnlyEdits(_loadedWaterSnapshot, _loadedWaterDisplayEvidence, CaptureWaterDisplayEvidence());
            foreach (ResultItem item in resultItems)
            {
                if (string.IsNullOrWhiteSpace(item.ResultValue) ||
                    WaterWorkflowEvidenceGuard.IsPendingCompletionStatus(status) && WasWaterResultCompletedAtLoad(item.SampleTestID)) continue;
                item.PassFail = CalculatePassFail(item);
                if (item.PassFail is "Invalid" or "NOT ASSESSED")
                    throw new InvalidOperationException("Invalid result value or incomplete frozen limits for test: " + item.TestName);
                bool edited = !_loadedWaterDisplayValues.TryGetValue(item.SampleTestID, out string? original) ||
                    !string.Equals(original, item.ResultValue ?? "", StringComparison.Ordinal);
                if (edited && TryNormalizeResultForSave(item, out decimal value) && !WaterResultValueContract.IsExactlyRepresentable(value))
                    throw new InvalidOperationException("Result for " + item.TestName + " exceeds the exact decimal(18,4) write contract. No value was rounded.");
                if (!WaterResourceSelectionValid(item) || !ValidateLesExecutionBeforeSave(item)) return false;
                if (item.PassFail is "ALERT" or "OOS" && string.IsNullOrWhiteSpace(item.Remarks))
                    throw new InvalidOperationException("Remarks are required for " + item.PassFail + " test " + item.TestName + ".");
            }
        }
        if (operation is "SubmitReview" or "Review" or "Approve")
        {
            EnsureWaterVisibleEvidenceSavedAtLoad();
            if (DatabaseHelper.GetPendingResultsCount(currentSampleId) > 0)
                throw new InvalidOperationException("Pending results must be completed before this Water operation.");
            if (resultItems.Any(item => CalculatePassFail(item) is "Invalid" or "NOT ASSESSED"))
                throw new InvalidOperationException("One or more Water results are invalid or cannot be assessed.");
            if (operation is "Review" or "Approve" && !DatabaseHelper.ValidateSampleWorkflowSeparation(currentSampleId,
                currentUser, operation == "Review" ? "Review" : "Approval", out string separation))
                throw new InvalidOperationException(separation);
            if (!DatabaseHelper.CanApproveSampleRecord(currentSampleId, out string quality))
                throw new InvalidOperationException(quality);
        }
        return true;
    }
}
