using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace PharmaLIMS
{
    public partial class ResultsEntry
    {
        private bool _isRefreshingEquipmentAtSave;

        // The water result window may remain open while equipment readiness changes
        // in Laboratory Resources. Refresh from SQL before validating and signing,
        // without ever weakening the signed transaction's server-side recheck.
        private async System.Threading.Tasks.Task<bool> RefreshWaterEquipmentBeforeSaveAsync()
        {
            _isRefreshingEquipmentAtSave = true;
            try
            {
                int sampleIdAtRefresh = currentSampleId;
                var freshEquipment = await System.Threading.Tasks.Task.Run(
                    () => LabEquipmentUsageService.LoadAvailableEquipment(_equipmentDatabase));
                if (currentSampleId != sampleIdAtRefresh)
                {
                    lblStatus.Text = "Sample changed while refreshing equipment. Reload before saving.";
                    return false;
                }
                AvailableEquipmentChoices.Clear();
                foreach (LabEquipmentChoice equipment in freshEquipment)
                    AvailableEquipmentChoices.Add(equipment);
                foreach (ResultItem loaded in resultItems)
                    loaded.EquipmentChoices = WaterTestResourcePolicy.FilterEquipment(loaded.TestName, freshEquipment);
                dgResults.Items.Refresh();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex, "Refresh laboratory equipment"),
                    "Equipment Readiness", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                _isRefreshingEquipmentAtSave = false;
            }
        }

        public partial class ResultItem
        {
            public string SupportingEquipmentCodes { get; set; } = "";
            public string TestKitCode { get; set; } = "";
            public string TestKitLot { get; set; } = "";
            public DateTime? TestKitExpiry { get; set; }
        }
        private static void PopulateLesExecutionFields(
            ResultItem item,
            IReadOnlyDictionary<int, WaterLesExecutionSnapshot> snapshots)
        {
            item.HasStructuredLes = WaterLesExecutionService.IsStructuredLesTest(item.TestID);
            item.LesProcedureReference = WaterLesExecutionService.GetProcedureReference(item.TestID);
            item.LesGuidance = WaterLesExecutionService.GetGuidance(item.TestID);

            if (!snapshots.TryGetValue(item.SampleTestID, out WaterLesExecutionSnapshot? les) || les == null)
                return;

            item.LesSampleTemperatureC = les.SampleTemperatureC.HasValue
                ? les.SampleTemperatureC.Value.ToString("0.##", CultureInfo.InvariantCulture)
                : "";
            item.LesVerificationReference = les.VerificationReference;
            item.LesVerificationConfirmed = les.VerificationConfirmed;
            item.LesExecutionRemarks = les.ExecutionRemarks;
        }

        private static void PopulateResourceEvidence(
            ResultItem item, IReadOnlyDictionary<int, WaterResourceSnapshot> snapshots)
        {
            if (!snapshots.TryGetValue(item.SampleTestID, out WaterResourceSnapshot? resource))
                return;
            item.SupportingEquipmentCodes = resource.SupportingEquipmentCodes;
            item.TestKitCode = resource.KitCode;
            item.TestKitLot = resource.KitLot;
            item.TestKitExpiry = resource.KitExpiry;
        }

        private bool WaterResourceSelectionValid(ResultItem item)
        {
            try
            {
                WaterResourceEvidenceService.Validate(item.TestName, item.ResultValue,
                    item.EquipmentID, item.SupportingEquipmentCodes, item.TestKitCode,
                    item.TestKitLot, item.TestKitExpiry, AvailableEquipmentChoices.ToList());
                return true;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(UserFacingError.SafeMessage(ex), "Water Resources",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private static bool ValidateLesExecutionBeforeSave(ResultItem item)
        {
            try
            {
                WaterLesExecutionService.ValidateForSave(
                    item.TestID,
                    item.TestName,
                    item.ResultValue,
                    item.LesSampleTemperatureC,
                    item.LesVerificationReference,
                    item.LesVerificationConfirmed);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(
                    UserFacingError.SafeMessage(ex),
                    "LES Execution Evidence",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
        }

        private void AppendLesExecutionEvidenceInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            ResultItem item,
            string signedBy,
            string signerRole,
            string meaning,
            string reason)
        {
            if (!WaterLesExecutionService.IsStructuredLesTest(item.TestID) ||
                string.IsNullOrWhiteSpace(item.ResultValue))
                return;

            int equipmentId = item.EquipmentID
                ?? throw new InvalidOperationException("Controlled equipment is required before LES evidence can be recorded.");

            WaterLesExecutionService.AppendEvidenceInTransaction(
                connection,
                transaction,
                currentSampleId,
                item.SampleTestID,
                item.TestID,
                item.ResultValue,
                equipmentId,
                item.LesSampleTemperatureC,
                item.LesVerificationReference,
                item.LesVerificationConfirmed,
                item.LesExecutionRemarks,
                signedBy,
                signerRole,
                meaning,
                reason);
        }
    }
}
