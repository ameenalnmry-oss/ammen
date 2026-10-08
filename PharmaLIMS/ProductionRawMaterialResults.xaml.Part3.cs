using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Globalization;

namespace PharmaLIMS
{
    public partial class ProductionRawMaterialResults
    {
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
