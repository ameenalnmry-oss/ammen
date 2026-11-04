using Microsoft.Data.SqlClient;
using System.Data;
internal static class CultureReleaseGateProbe
{
private static void ValidatePreparationReleaseGateInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int preparationId)
        {
            using var command = new SqlCommand(@"
SELECT TOP (1)
    p.ReleaseStatus,
    p.ExpiryDate,
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
            string visualCheckedBy = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            DateTime? visualCheckedAt = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            string sourceLotStatus = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            DateTime? sourceLotExpiry = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
            string visualConclusion = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
            DateTime databaseDate = reader.GetDateTime(7).Date;

            if (!releaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(visualCheckedBy) || !visualCheckedAt.HasValue ||
                !visualConclusion.Equals("Satisfactory", StringComparison.OrdinalIgnoreCase) ||
                !preparedExpiry.HasValue || preparedExpiry.Value.Date < databaseDate ||
                !sourceLotStatus.Equals("Released", StringComparison.OrdinalIgnoreCase) ||
                (sourceLotExpiry.HasValue && sourceLotExpiry.Value.Date < databaseDate))
            {
                throw new InvalidOperationException(
                    "The preparation no longer satisfies the signed visual, expiry, or source-lot release gates.");
            }
        }
internal static void Validate(SqlConnection c,SqlTransaction tx)=>ValidatePreparationReleaseGateInTransaction(c,tx,1);
}
