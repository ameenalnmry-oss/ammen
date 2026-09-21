using PharmaLIMS.Services;
using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Interfaces;
using PharmaLIMS.Repositories;

namespace PharmaLIMS
{
    public partial class CultureMediaPreparation
    {
        private void BtnRejectPreparation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReleasePermission();
                if (_selectedPreparationId <= 0 || !IsPreparationUnderRelease(_selectedPreparationId))
                {
                    MessageBox.Show("Select a saved preparation with status Under Release first.", "Culture Media", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(TxtPreparationRemarks.Text))
                {
                    MessageBox.Show("A documented rejection / disposal reason is required.", "Culture Media", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (MessageBox.Show("Reject this prepared media batch and record the consumed powder as rejected preparation waste?", "Reject Preparation", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                if (!ConfirmCultureMediaSignature("Reject Prepared Media", TxtPreparationNo.Text, _selectedPreparationId, out string signedBy, out string signatureReason))
                    return;

                int rejectedPreparationId = _selectedPreparationId;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                        conn, tx, signedBy, "reject prepared culture media");

                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaPreparations
SET ReleaseStatus = 'Rejected',
    SterilityReview = CASE WHEN SterilityReview = 'Passed' THEN SterilityReview ELSE 'Failed' END,
    RejectedBy = @RejectedBy,
    RejectedAt = SYSUTCDATETIME(),
    Remarks = @Remarks,
    UpdatedAt = SYSUTCDATETIME()
WHERE MediaPreparationID = @MediaPreparationID
  AND ReleaseStatus = 'Under Release';",
                        new[]
                        {
                            new SqlParameter("@RejectedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                            new SqlParameter("@Remarks", SqlDbType.NVarChar, -1) { Value = TxtPreparationRemarks.Text.Trim() },
                            new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = rejectedPreparationId }
                        }, conn, tx);
                    if (affected != 1)
                        throw new DBConcurrencyException("The preparation is no longer available for rejection.");

                    DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.CultureMediaPreparationDispositions
(MediaPreparationID, DispositionType, PowderQuantityG, Reason, DisposedBy, DisposedAt)
SELECT MediaPreparationID, 'RejectedPreparation', ISNULL(PowderQuantityG, 0), @Reason, @DisposedBy, SYSUTCDATETIME()
FROM dbo.MediaPreparations
WHERE MediaPreparationID = @MediaPreparationID;",
                        new[]
                        {
                            new SqlParameter("@Reason", SqlDbType.NVarChar, 500) { Value = signatureReason },
                            new SqlParameter("@DisposedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                            new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = rejectedPreparationId }
                        }, conn, tx);
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "MediaPreparations", rejectedPreparationId,
                        "Prepared Media Rejected and Disposition Recorded", "Under Release", "Rejected",
                        signatureReason, signedBy, TxtPreparationNo.Text);
                });
                ClearPendingCultureMediaSignature();

                _selectedPreparationPrintId = rejectedPreparationId;
                _selectedPreparationId = 0;
                if (TxtPreparationReleaseStatus != null)
                    TxtPreparationReleaseStatus.Text = "Rejected";
                _ = LoadAllDataAsync();
                ShowToast("Prepared media rejected and disposition recorded", "⛔");
                SetStatus("Prepared media rejected");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error rejecting media preparation", ex);
            }
        }

        private bool ValidateMediaLotForPreparation(int mediaLotId, DateTime preparationDate, DateTime? preparationExpiry)
        {
            DataTable table = _repository.Load(CultureMediaQuery.ValidateMediaLotForPreparation,
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId });

            if (table.Rows.Count != 1)
            {
                MessageBox.Show("The selected stored media lot was not found.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DataRow row = table.Rows[0];
            string status = NormalizeStatus(RowString(row, "ReceiptStatus"));
            if (!status.Equals("Released", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "Only a released dehydrated media lot can be used for preparation.",
                    "Culture Media Workflow",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            DateTime? lotExpiry = RowDate(row, "ExpiryDate");
            if (lotExpiry.HasValue && preparationDate.Date > lotExpiry.Value.Date)
            {
                MessageBox.Show("The selected dehydrated media lot is expired and cannot be used.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (lotExpiry.HasValue && preparationExpiry.HasValue && preparationExpiry.Value.Date > lotExpiry.Value.Date)
            {
                MessageBox.Show(
                    "Prepared media expiry cannot be later than the source dehydrated media lot expiry.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private bool ValidatePreparationSourceLotForRelease(int preparationId)
        {
            DataTable table = _repository.Load(CultureMediaQuery.ValidatePreparationSourceLotForRelease,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });

            if (table.Rows.Count != 1)
            {
                MessageBox.Show("The source dehydrated media lot could not be verified.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DataRow row = table.Rows[0];
            string status = NormalizeStatus(RowString(row, "ReceiptStatus"));
            DateTime? expiry = RowDate(row, "ExpiryDate");
            if (!status.Equals("Released", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The source dehydrated media lot is no longer Released.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DateTime databaseToday = DatabaseHelper.GetAuthoritativeDatabaseTime().Date;
            if (expiry.HasValue && expiry.Value.Date < databaseToday)
            {
                MessageBox.Show("The source dehydrated media lot has expired. The prepared batch cannot be released.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private bool IsPreparationUnderRelease(int preparationId)
        {
            if (preparationId <= 0)
                return false;

            object? result = _repository.ReadScalar(CultureMediaScalar.PreparationReleaseStatus,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });

            string status = NormalizeStatus(result?.ToString() ?? string.Empty);
            return status.Equals("Under Release", StringComparison.OrdinalIgnoreCase);
        }

        private bool ValidatePreparation()
        {
            if (!TryResolveSelectedMediaLot(out _, out _, out _))
            {
                MessageBox.Show("Select Stored Media Batch first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!DpPreparationDate.SelectedDate.HasValue || !DpPreparationExpiry.SelectedDate.HasValue)
            {
                MessageBox.Show("Preparation Date and Prepared Media Expiry are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (DpPreparationExpiry.SelectedDate.Value <= DpPreparationDate.SelectedDate.Value)
            {
                MessageBox.Show("Preparation Expiry Date must be after Preparation Date.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtQuantityPrepared.Text) || string.IsNullOrWhiteSpace(TxtBatchSize.Text))
            {
                MessageBox.Show("Quantity Prepared and Batch Size are required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (!TryParseGramQuantity(TxtQuantityWeighedG.Text, out decimal powderQuantityG))
            {
                MessageBox.Show("Quantity Weighed is required in grams and must be greater than zero.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            TxtQuantityWeighedG.Text = powderQuantityG.ToString("0.###", CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(TxtFinalPH.Text) ||
                !decimal.TryParse(TxtFinalPH.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal ph) ||
                ph < 0m || ph > 14m)
            {
                MessageBox.Show("A valid Final pH between 0 and 14 is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(TxtAppearance.Text))
            {
                MessageBox.Show("Appearance is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string[] requiredSopValues =
            {
                TxtPreparationLoadNo.Text,
                TxtPreparationMpmNo.Text,
                TxtPreparationPurifiedWaterMl.Text,
                TxtPreparationQuantityDispensed.Text,
                TxtPreparationBalanceNo.Text,
                TxtPreparationEquipmentCode.Text,
                TxtPreparationPhMeterNo.Text,
                TxtPreparationSterilizationMethod.Text,
                TxtPreparationAdditives.Text
            };
            if (requiredSopValues.Any(string.IsNullOrWhiteSpace))
            {
                MessageBox.Show(
                    "SOP A6 is incomplete. Load No., MPM No., Purified Water, Quantity Dispensed, Balance No., Equipment Code, pH Meter No., Sterilization Method, and Additives (or None) are required.",
                    "Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            bool usesAutoclave = TxtPreparationSterilizationMethod.Text.Contains("autoclave", StringComparison.OrdinalIgnoreCase) ||
                                TxtPreparationSterilizationMethod.Text.Contains("steam", StringComparison.OrdinalIgnoreCase);
            if (usesAutoclave &&
                (string.IsNullOrWhiteSpace(TxtAutoclaveCycleNo.Text) ||
                 string.IsNullOrWhiteSpace(TxtAutoclaveTemperature.Text) ||
                 string.IsNullOrWhiteSpace(TxtAutoclaveHoldingTime.Text)))
            {
                MessageBox.Show("Autoclave Cycle No., Temperature, and Holding Time are required for steam sterilization.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (TxtAutoclaveTemperature.Text.Contains('/') || TxtAutoclaveTemperature.Text.Contains('\\') ||
                TxtAutoclaveHoldingTime.Text.Contains('/') || TxtAutoclaveHoldingTime.Text.Contains('\\'))
            {
                MessageBox.Show("Record autoclave temperature and holding time in their separate fields.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private bool ValidatePreparationReleaseGate()
        {
            DataTable table = LoadPreparationReleaseGateRecord(_selectedPreparationId);
            if (table.Rows.Count != 1)
            {
                MessageBox.Show("The prepared media record could not be verified.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            DataRow row = table.Rows[0];
            if (!NormalizeSterilityReview(RowString(row, "SterilityReview")).Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(RowString(row, "SterilityReviewedBy")) ||
                !RowDate(row, "SterilityReviewedAt").HasValue)
            {
                MessageBox.Show("A signed Sterility Review = Passed is required before final release.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (string.IsNullOrWhiteSpace(RowString(row, "VisualCheckedBy")) || !RowDate(row, "VisualCheckedAt").HasValue)
            {
                MessageBox.Show("A signed SOP A7 visual check is required before final release.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            string visualConclusion = LoadSopField("MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "Conclusion");
            if (!visualConclusion.Equals("Satisfactory", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("SOP A7 Visual Check conclusion must be Satisfactory.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            DateTime? expiry = RowDate(row, "ExpiryDate");
            DateTime databaseToday = DatabaseHelper.GetAuthoritativeDatabaseTime().Date;
            if (!expiry.HasValue || expiry.Value.Date < databaseToday)
            {
                MessageBox.Show("A valid, unexpired Prepared Media Expiry is required.", "Release Gate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private static void ValidatePreparationReleaseGateInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int preparationId)
        {
            using var command = new SqlCommand(@"
SELECT TOP (1)
    p.ReleaseStatus,
    p.ExpiryDate,
    p.SterilityReview,
    p.SterilityReviewedBy,
    p.SterilityReviewedAt,
    p.VisualCheckedBy,
    p.VisualCheckedAt,
    l.ReceiptStatus,
    l.ExpiryDate AS SourceLotExpiry,
    visualConclusion.FieldValue AS VisualConclusion,
    CONVERT(date, SYSDATETIME()) AS DatabaseDate
FROM dbo.MediaPreparations p WITH (UPDLOCK, HOLDLOCK)
INNER JOIN dbo.CultureMediaLots l WITH (UPDLOCK, HOLDLOCK) ON l.MediaLotID = p.MediaLotID
OUTER APPLY
(
    SELECT TOP (1) f.FieldValue
    FROM dbo.CultureMediaSopFields f WITH (UPDLOCK, HOLDLOCK)
    WHERE f.EntityType = N'MediaPreparation'
      AND f.EntityID = p.MediaPreparationID
      AND f.AnnexureCode = N'1035-L-0005/A7'
      AND f.FieldName = N'Conclusion'
) visualConclusion
WHERE p.MediaPreparationID = @MediaPreparationID;", conn, tx);
            command.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = preparationId;
            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException("The preparation release record was not found during final release.");

            string releaseStatus = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            DateTime? preparedExpiry = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            string sterilityReview = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            string sterilityReviewedBy = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            DateTime? sterilityReviewedAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
            string visualCheckedBy = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
            DateTime? visualCheckedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
            string sourceLotStatus = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
            DateTime? sourceLotExpiry = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
            string visualConclusion = reader.IsDBNull(9) ? string.Empty : reader.GetString(9);
            DateTime databaseDate = reader.GetDateTime(10).Date;

            if (!releaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase) ||
                !sterilityReview.Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(sterilityReviewedBy) || !sterilityReviewedAt.HasValue ||
                string.IsNullOrWhiteSpace(visualCheckedBy) || !visualCheckedAt.HasValue ||
                !visualConclusion.Equals("Satisfactory", StringComparison.OrdinalIgnoreCase) ||
                !preparedExpiry.HasValue || preparedExpiry.Value.Date < databaseDate ||
                !sourceLotStatus.Equals("Released", StringComparison.OrdinalIgnoreCase) ||
                (sourceLotExpiry.HasValue && sourceLotExpiry.Value.Date < databaseDate))
            {
                throw new InvalidOperationException(
                    "The preparation no longer satisfies the signed visual, sterility, expiry, or source-lot release gates.");
            }
        }

        private DataTable LoadPreparationReleaseGateRecord(int preparationId)
        {
            return _repository.Load(CultureMediaQuery.LoadPreparationReleaseGateRecord,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });
        }

        private SqlParameter[] BuildPreparationParameters(string preparationNo, int mediaId, int lotId, int preparationId = 0)
        {
            return new[]
            {
                new SqlParameter("@MediaPreparationNo", SqlDbType.NVarChar, 50) { Value = preparationNo },
                new SqlParameter("@MediaID", SqlDbType.Int) { Value = mediaId },
                new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = lotId > 0 ? (object)lotId : DBNull.Value },
                new SqlParameter("@PreparationDate", SqlDbType.Date) { Value = DbDate(DpPreparationDate.SelectedDate) },
                new SqlParameter("@ExpiryDate", SqlDbType.Date) { Value = DbDate(DpPreparationExpiry.SelectedDate) },
                new SqlParameter("@QuantityPrepared", SqlDbType.NVarChar, 80) { Value = DbValue(TxtQuantityPrepared.Text) },
                new SqlParameter("@PowderQuantityG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = ParseGramQuantity(TxtQuantityWeighedG.Text) },
                new SqlParameter("@PreparedBy", SqlDbType.NVarChar, 100) { Value = DbValue(TxtPreparedBy.Text) },
                new SqlParameter("@BatchSize", SqlDbType.NVarChar, 80) { Value = DbValue(TxtBatchSize.Text) },
                new SqlParameter("@FinalPH", SqlDbType.Decimal) { Precision = 10, Scale = 2, Value = DbDecimal(TxtFinalPH.Text) },
                new SqlParameter("@Appearance", SqlDbType.NVarChar, 200) { Value = DbValue(TxtAppearance.Text) },
                new SqlParameter("@AutoclaveCycleNo", SqlDbType.NVarChar, 100) { Value = DbValue(TxtAutoclaveCycleNo.Text) },
                new SqlParameter("@AutoclaveTemperature", SqlDbType.NVarChar, 50) { Value = DbValue(TxtAutoclaveTemperature.Text) },
                new SqlParameter("@AutoclaveHoldingTime", SqlDbType.NVarChar, 50) { Value = DbValue(TxtAutoclaveHoldingTime.Text) },
                new SqlParameter("@SterilityReview", SqlDbType.NVarChar, 30) { Value = DbValue(NormalizeSterilityReview(ComboText(CmbPreparationSterilityReview))) },
                new SqlParameter("@Remarks", SqlDbType.NVarChar) { Value = DbValue(TxtPreparationRemarks.Text) },
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId }
            };
        }

        private void InsertCultureMediaStockTransactionInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int mediaLotId,
            int? mediaPreparationId,
            string transactionType,
            decimal quantityChangeG,
            decimal balanceBeforeG,
            decimal balanceAfterG,
            string referenceNo,
            string reason,
            string performedBy)
        {
            if (quantityChangeG == 0m)
                return;

            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.CultureMediaStockTransactions
(MediaLotID, MediaPreparationID, TransactionType, QuantityChangeG, BalanceBeforeG, BalanceAfterG, ReferenceNo, Reason, PerformedBy)
VALUES
(@MediaLotID, @MediaPreparationID, @TransactionType, @QuantityChangeG, @BalanceBeforeG, @BalanceAfterG, @ReferenceNo, @Reason, @PerformedBy);",
                new[]
                {
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId },
                    new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = mediaPreparationId.HasValue ? mediaPreparationId.Value : DBNull.Value },
                    new SqlParameter("@TransactionType", SqlDbType.NVarChar, 40) { Value = transactionType },
                    new SqlParameter("@QuantityChangeG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = quantityChangeG },
                    new SqlParameter("@BalanceBeforeG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = balanceBeforeG },
                    new SqlParameter("@BalanceAfterG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = balanceAfterG },
                    new SqlParameter("@ReferenceNo", SqlDbType.NVarChar, 80) { Value = DbValue(referenceNo) },
                    new SqlParameter("@Reason", SqlDbType.NVarChar, 500) { Value = DbValue(reason) },
                    new SqlParameter("@PerformedBy", SqlDbType.NVarChar, 100) { Value = performedBy }
                }, conn, tx);
        }

        private void SaveReceiptSopFieldsInTransaction(SqlConnection conn, SqlTransaction tx, int mediaLotId)
        {
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A2", "SerialNumberOfMedium", TxtReceiptSerialNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A2", "MpmNo", TxtReceiptMpmNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A2", "PackNo", TxtReceiptPackNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A2", "TotalPacksReceived", TxtReceiptTotalPacks.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfOpening", FormatIsoDate(DpReceiptOpeningDate.SelectedDate));
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A4", "PackSize", TxtReceiptPackSize.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaLot", mediaLotId, "1035-L-0005/A4", "InitialBalanceQuantity", TxtReceiptQuantity.Text);
        }

        private void SaveReceiptSopFields(int mediaLotId)
        {
            if (mediaLotId <= 0)
                return;

            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "SerialNumberOfMedium", TxtReceiptSerialNo.Text);
            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "MpmNo", TxtReceiptMpmNo.Text);
            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "PackNo", TxtReceiptPackNo.Text);
            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "TotalPacksReceived", TxtReceiptTotalPacks.Text);
            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfOpening", FormatIsoDate(DpReceiptOpeningDate.SelectedDate));
            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A4", "PackSize", TxtReceiptPackSize.Text);
            SaveSopField("MediaLot", mediaLotId, "1035-L-0005/A4", "InitialBalanceQuantity", TxtReceiptQuantity.Text);
        }

        private void LoadReceiptSopFields(int mediaLotId)
        {
            if (mediaLotId <= 0)
                return;

            TxtReceiptSerialNo.Text = LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "SerialNumberOfMedium");
            TxtReceiptMpmNo.Text = LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "MpmNo");
            TxtReceiptPackNo.Text = LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "PackNo");
            TxtReceiptTotalPacks.Text = LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "TotalPacksReceived");
            DpReceiptOpeningDate.SelectedDate = ParseIsoDate(LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfOpening"));
            DpReceiptLotReleaseDate.SelectedDate = ParseIsoDate(LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfReleaseOfLot"));
            TxtReceiptPackSize.Text = LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A4", "PackSize");
        }

        private void SaveReleaseSopFields(int qualificationId)
        {
            if (qualificationId <= 0)
                return;

            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "ArNo", TxtReleaseArNo.Text);
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "BottleNoOrPackIdentity", TxtReleaseBottleNo.Text);
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "PhOfMedia", TxtReleaseMediaPh.Text);
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "PhAdjustment", ComboText(CmbReleasePhAdjustment));
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "BacterialDilutionArNo", TxtReleaseBacterialDilutionArNo.Text);
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "FungalDilutionArNo", TxtReleaseFungalDilutionArNo.Text);
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A8", "AnaerobicJarNo", TxtReleaseAnaerobicJarNo.Text);
            SaveSopField("MediaQualification", qualificationId, "1035-L-0005/A8", "Conclusion", ComboText(CmbReleaseConclusion));
        }

        private void LoadReleaseSopFields(int qualificationId)
        {
            if (qualificationId <= 0)
                return;

            TxtReleaseArNo.Text = LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "ArNo");
            TxtReleaseBottleNo.Text = LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "BottleNoOrPackIdentity");
            TxtReleaseMediaPh.Text = LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "PhOfMedia");
            SetComboText(CmbReleasePhAdjustment, LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "PhAdjustment"));
            TxtReleaseBacterialDilutionArNo.Text = LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "BacterialDilutionArNo");
            TxtReleaseFungalDilutionArNo.Text = LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A5", "FungalDilutionArNo");
            TxtReleaseAnaerobicJarNo.Text = LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A8", "AnaerobicJarNo");
            SetComboText(CmbReleaseConclusion, LoadSopField("MediaQualification", qualificationId, "1035-L-0005/A8", "Conclusion"));
        }

        private void SavePreparationSopFields(int preparationId)
        {
            if (preparationId <= 0)
                return;

            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "LoadNo", TxtPreparationLoadNo.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MpmNo", TxtPreparationMpmNo.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PurifiedWaterMl", TxtPreparationPurifiedWaterMl.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "QuantityDispensed", TxtPreparationQuantityDispensed.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "BalanceNo", TxtPreparationBalanceNo.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "EquipmentCode", TxtPreparationEquipmentCode.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PhMeterNo", TxtPreparationPhMeterNo.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MethodOfSterilization", TxtPreparationSterilizationMethod.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "Additives", TxtPreparationAdditives.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "EquipmentCode", TxtPreparationVisualEquipmentCode.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "VisualObservations", TxtPreparationVisualObservations.Text);
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "Conclusion", ComboText(CmbPreparationVisualConclusion));
            SaveSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "CheckedBy", TxtPreparationVisualCheckedBy.Text);
        }

        private void SavePreparationSopFieldsInTransaction(SqlConnection conn, SqlTransaction tx, int preparationId)
        {
            if (preparationId <= 0)
                throw new ArgumentOutOfRangeException(nameof(preparationId));

            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "LoadNo", TxtPreparationLoadNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "MpmNo", TxtPreparationMpmNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "PurifiedWaterMl", TxtPreparationPurifiedWaterMl.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "QuantityDispensed", TxtPreparationQuantityDispensed.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "BalanceNo", TxtPreparationBalanceNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "EquipmentCode", TxtPreparationEquipmentCode.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "PhMeterNo", TxtPreparationPhMeterNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "MethodOfSterilization", TxtPreparationSterilizationMethod.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A6", "Additives", TxtPreparationAdditives.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A7", "EquipmentCode", TxtPreparationVisualEquipmentCode.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A7", "VisualObservations", TxtPreparationVisualObservations.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A7", "Conclusion", ComboText(CmbPreparationVisualConclusion));
            SaveSopFieldInTransaction(conn, tx, "MediaPreparation", preparationId, "1035-L-0005/A7", "CheckedBy", TxtPreparationVisualCheckedBy.Text);
        }

        private void AdjustCultureMediaStockInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int mediaLotId,
            int preparationId,
            string preparationNo,
            decimal originalPowderQuantityG,
            decimal newPowderQuantityG,
            string changeReason)
        {
            decimal quantityDifferenceG = newPowderQuantityG - originalPowderQuantityG;
            if (quantityDifferenceG == 0m)
                return;

            string receiptStatus;
            decimal currentStockG;
            using (var command = new SqlCommand(@"
SELECT ReceiptStatus, CurrentStockG
FROM dbo.CultureMediaLots WITH (UPDLOCK, HOLDLOCK)
WHERE MediaLotID = @MediaLotID;", conn, tx))
            {
                command.Parameters.Add("@MediaLotID", SqlDbType.Int).Value = mediaLotId;
                using SqlDataReader reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The source dehydrated media lot was not found.");

                receiptStatus = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                if (reader.IsDBNull(1))
                    throw new InvalidOperationException("The selected media lot stock has not been reconciled in grams.");
                currentStockG = reader.GetDecimal(1);
            }

            if (!NormalizeStatus(receiptStatus).Equals("Released", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a Released dehydrated media lot can be issued to preparation.");

            if (quantityDifferenceG > 0m && currentStockG < quantityDifferenceG)
            {
                throw new InvalidOperationException(
                    $"Insufficient media stock. Available: {currentStockG:0.###} g; additional required: {quantityDifferenceG:0.###} g.");
            }

            decimal newBalanceG = currentStockG - quantityDifferenceG;
            if (newBalanceG < 0m)
                throw new InvalidOperationException("The media stock balance cannot become negative.");

            int updated = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.CultureMediaLots
SET CurrentStockG = @CurrentStockG,
    StockStatus = CASE WHEN @CurrentStockG <= 0 THEN 'Depleted' ELSE 'Available' END
WHERE MediaLotID = @MediaLotID;",
                new[]
                {
                    new SqlParameter("@CurrentStockG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = newBalanceG },
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId }
                }, conn, tx);
            if (updated != 1)
                throw new InvalidOperationException("The media stock balance could not be updated.");

            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.CultureMediaStockTransactions
(MediaLotID, MediaPreparationID, TransactionType, QuantityChangeG, BalanceBeforeG, BalanceAfterG, ReferenceNo, Reason, PerformedBy)
VALUES
(@MediaLotID, @MediaPreparationID, @TransactionType, @QuantityChangeG, @BalanceBeforeG, @BalanceAfterG, @ReferenceNo, @Reason, @PerformedBy);",
                new[]
                {
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = mediaLotId },
                    new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId },
                    new SqlParameter("@TransactionType", SqlDbType.NVarChar, 40) { Value = originalPowderQuantityG == 0m ? "IssueToPreparation" : "PreparationAdjustment" },
                    new SqlParameter("@QuantityChangeG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = -quantityDifferenceG },
                    new SqlParameter("@BalanceBeforeG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = currentStockG },
                    new SqlParameter("@BalanceAfterG", SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = newBalanceG },
                    new SqlParameter("@ReferenceNo", SqlDbType.NVarChar, 80) { Value = DbValue(preparationNo) },
                    new SqlParameter("@Reason", SqlDbType.NVarChar, 300) { Value = originalPowderQuantityG == 0m ? "Powder weighed for media preparation" : changeReason },
                    new SqlParameter("@PerformedBy", SqlDbType.NVarChar, 100) { Value = DbValue(_currentUser) }
                }, conn, tx);
        }

        private void LoadPreparationSopFields(int preparationId)
        {
            if (preparationId <= 0)
                return;

            TxtPreparationLoadNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "LoadNo");
            TxtPreparationMpmNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MpmNo");
            TxtPreparationPurifiedWaterMl.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PurifiedWaterMl");
            TxtPreparationQuantityDispensed.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "QuantityDispensed");
            TxtPreparationBalanceNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "BalanceNo");
            TxtPreparationEquipmentCode.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "EquipmentCode");
            TxtPreparationPhMeterNo.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PhMeterNo");
            TxtPreparationSterilizationMethod.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MethodOfSterilization");
            TxtPreparationAdditives.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "Additives");
            TxtPreparationVisualEquipmentCode.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "EquipmentCode");
            TxtPreparationVisualObservations.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "VisualObservations");
            SetComboText(CmbPreparationVisualConclusion, LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "Conclusion"));
            TxtPreparationVisualCheckedBy.Text = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "CheckedBy");
        }

        private void SaveSopField(string entityType, int entityId, string annexureCode, string fieldName, string value)
        {

            _repository.RunCommand(CultureMediaCommand.SaveSopField,
                new SqlParameter("@EntityType", SqlDbType.NVarChar, 50) { Value = entityType },
                new SqlParameter("@EntityID", SqlDbType.Int) { Value = entityId },
                new SqlParameter("@AnnexureCode", SqlDbType.NVarChar, 50) { Value = annexureCode },
                new SqlParameter("@FieldName", SqlDbType.NVarChar, 120) { Value = fieldName },
                new SqlParameter("@FieldValue", SqlDbType.NVarChar) { Value = DbValue(value) },
                new SqlParameter("@UserName", SqlDbType.NVarChar, 100) { Value = _currentUser });
        }

        private string LoadSopField(string entityType, int entityId, string annexureCode, string fieldName)
        {

            object? value = _repository.ReadScalar(CultureMediaScalar.LoadSopField,
                new SqlParameter("@EntityType", SqlDbType.NVarChar, 50) { Value = entityType },
                new SqlParameter("@EntityID", SqlDbType.Int) { Value = entityId },
                new SqlParameter("@AnnexureCode", SqlDbType.NVarChar, 50) { Value = annexureCode },
                new SqlParameter("@FieldName", SqlDbType.NVarChar, 120) { Value = fieldName });

            return value == null || value == DBNull.Value ? string.Empty : value.ToString() ?? string.Empty;
        }

        private void RecordCultureMediaPrint(string entityType, int entityId, string recordNumber, string documentType, string printedBy, string reason)
        {
            _repository.RunCommand(CultureMediaCommand.RecordCultureMediaPrint,
                new SqlParameter("@EntityType", SqlDbType.NVarChar, 50) { Value = entityType },
                new SqlParameter("@EntityID", SqlDbType.Int) { Value = entityId },
                new SqlParameter("@RecordNumber", SqlDbType.NVarChar, 100) { Value = DbValue(recordNumber) },
                new SqlParameter("@DocumentType", SqlDbType.NVarChar, 100) { Value = documentType },
                new SqlParameter("@PrintedBy", SqlDbType.NVarChar, 100) { Value = printedBy },
                new SqlParameter("@Reason", SqlDbType.NVarChar, 500) { Value = DbValue(reason) });
        }

        private bool ConfirmCultureMediaSignature(string action, string recordNumber, int recordId, out string signedBy, out string reason)
        {
            signedBy = _currentUser;
            reason = action;

            var signature = new ElectronicSignature(recordNumber, _currentUser, action, true)
            {
                Owner = this
            };

            bool? result = signature.ShowDialog();
            if (result != true || !signature.IsConfirmed)
                return false;

            signedBy = string.IsNullOrWhiteSpace(signature.SignedBy) ? _currentUser : signature.SignedBy;
            reason = string.IsNullOrWhiteSpace(signature.Reason) ? action : signature.Reason;

            _pendingSignatureEntityType = ResolveCultureMediaSignatureEntityType(action);
            _pendingSignatureEntityId = recordId;
            _pendingSignatureRecordNumber = recordNumber ?? string.Empty;
            _pendingSignatureAction = action;
            _pendingSignatureSignedBy = signedBy;
            _pendingSignatureMeaning = string.IsNullOrWhiteSpace(signature.Meaning) ? "I confirm this action" : signature.Meaning;
            _pendingSignatureReason = reason;

            return true;
        }


        private static string ResolveCultureMediaSignatureEntityType(string action)
        {
            if (action.Contains("Qualification", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Final Release of Media Lot", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Media Lot Release Report", StringComparison.OrdinalIgnoreCase))
                return "MediaQualification";
            if (action.Contains("Prepared", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Preparation", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Prepare Culture Media", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Visual Check", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("Sterility Review", StringComparison.OrdinalIgnoreCase))
                return "MediaPreparation";
            return "MediaLot";
        }

        private void StorePendingCultureMediaSignature()
        {
            if (_pendingSignatureEntityId <= 0)
                throw new InvalidOperationException("No confirmed culture-media electronic signature is pending.");

            AddCultureMediaSignature(
                _pendingSignatureEntityType,
                _pendingSignatureEntityId,
                _pendingSignatureRecordNumber,
                _pendingSignatureAction,
                _pendingSignatureSignedBy,
                _pendingSignatureMeaning,
                _pendingSignatureReason);

            ClearPendingCultureMediaSignature();
        }

        private void StorePendingCultureMediaSignatureInTransaction(SqlConnection conn, SqlTransaction tx)
        {
            if (_pendingSignatureEntityId <= 0)
                throw new InvalidOperationException("No confirmed culture-media electronic signature is pending.");

            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.CultureMediaSignatures
(EntityType, EntityID, RecordNumber, ActionType, ActionReason, SignedBy, MeaningOfSignature)
VALUES
(@EntityType, @EntityID, @RecordNumber, @ActionType, @ActionReason, @SignedBy, @MeaningOfSignature);",
                new[]
                {
                    new SqlParameter("@EntityType", SqlDbType.NVarChar, 50) { Value = _pendingSignatureEntityType },
                    new SqlParameter("@EntityID", SqlDbType.Int) { Value = _pendingSignatureEntityId },
                    new SqlParameter("@RecordNumber", SqlDbType.NVarChar, 100) { Value = DbValue(_pendingSignatureRecordNumber) },
                    new SqlParameter("@ActionType", SqlDbType.NVarChar, 100) { Value = _pendingSignatureAction },
                    new SqlParameter("@ActionReason", SqlDbType.NVarChar, -1) { Value = DbValue(_pendingSignatureReason) },
                    new SqlParameter("@SignedBy", SqlDbType.NVarChar, 100) { Value = _pendingSignatureSignedBy },
                    new SqlParameter("@MeaningOfSignature", SqlDbType.NVarChar, 255) { Value = _pendingSignatureMeaning }
                }, conn, tx);

            DatabaseHelper.AddAuditTrailAdvanced(
                conn,
                tx,
                _pendingSignatureEntityType,
                _pendingSignatureEntityId,
                _pendingSignatureAction,
                string.Empty,
                _pendingSignatureMeaning,
                _pendingSignatureReason,
                _pendingSignatureSignedBy,
                "ElectronicSignature",
                null,
                _pendingSignatureRecordNumber,
                "Culture Media");
        }

        private void ClearPendingCultureMediaSignature()
        {
            _pendingSignatureEntityType = string.Empty;
            _pendingSignatureEntityId = 0;
            _pendingSignatureRecordNumber = string.Empty;
            _pendingSignatureAction = string.Empty;
            _pendingSignatureSignedBy = string.Empty;
            _pendingSignatureMeaning = string.Empty;
            _pendingSignatureReason = string.Empty;
        }

        private void AddCultureMediaSignature(string entityType, int entityId, string recordNumber, string action, string signedBy, string meaning, string reason)
        {

            _repository.RunCommand(CultureMediaCommand.AddCultureMediaSignature,
                new SqlParameter("@EntityType", SqlDbType.NVarChar, 50) { Value = entityType },
                new SqlParameter("@EntityID", SqlDbType.Int) { Value = entityId },
                new SqlParameter("@RecordNumber", SqlDbType.NVarChar, 100) { Value = DbValue(recordNumber) },
                new SqlParameter("@ActionType", SqlDbType.NVarChar, 100) { Value = action },
                new SqlParameter("@ActionReason", SqlDbType.NVarChar) { Value = DbValue(reason) },
                new SqlParameter("@SignedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                new SqlParameter("@MeaningOfSignature", SqlDbType.NVarChar, 255) { Value = meaning });
        }

        private static void AddCultureMediaAuditInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string tableName,
            int recordId,
            string action,
            string oldValue,
            string newValue,
            string reason,
            string performedBy,
            string? recordNumber = null)
        {
            DatabaseHelper.AddAuditTrailAdvanced(
                connection,
                transaction,
                tableName,
                recordId,
                action,
                oldValue,
                newValue,
                reason,
                performedBy,
                null,
                null,
                recordNumber,
                "Culture Media");
        }

        private static void AddCultureMediaAudit(string tableName, int recordId, string action, string oldValue, string newValue, string reason, string performedBy)
        {
            try
            {
                DatabaseHelper.AddAuditTrailAdvanced(
                    tableName,
                    recordId,
                    action,
                    oldValue,
                    newValue,
                    reason,
                    performedBy,
                    null,
                    null,
                    null,
                    "Culture Media");
            }
            catch
            {
                if (AppConfig.EnforceAuditTrail)
                    throw;
            }
        }

        private static string FormatIsoDate(DateTime? value)
            => value.HasValue ? value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

        private static string FormatIsoDateTime(DateTime? value)
            => value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

        private static DateTime? ParseIsoDate(string value)
            => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) ? date : (DateTime?)null;
    }
}
