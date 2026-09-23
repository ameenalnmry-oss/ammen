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
    public partial class ProductionRawMaterialResults
    {
        private bool HasAnyPrmQualityEventMinimalInTransaction(SqlConnection connection, SqlTransaction transaction)
        {
            object ready = ExecuteScalarInTransaction(connection, transaction, @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NOT NULL
THEN 1 ELSE 0 END;");

            if (Convert.ToInt32(ready ?? 0, CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "PRM approval/certificate action is blocked because the controlled Quality Event schema is incomplete. " +
                    "Apply and verify the approved database migration before continuing.");
            }

            object count = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID;",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

            return Convert.ToInt32(count ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private bool HasOpenPrmQualityEventMinimal()
        {
            if (!IsMinimalPrmQualityEventLookupReady())
                return false;

            object count = DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1)
FROM dbo.QualityEvents
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID
  AND ISNULL(CurrentStatus, N'Open') NOT IN (N'Closed', N'QA Closed', N'Cancelled', N'Rejected Closed');",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

            return Convert.ToInt32(count ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private bool HasOpenPrmQualityEventMinimalInTransaction(SqlConnection connection, SqlTransaction transaction)
        {
            object ready = ExecuteScalarInTransaction(connection, transaction, @"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NOT NULL
THEN 1 ELSE 0 END;");

            if (Convert.ToInt32(ready ?? 0, CultureInfo.InvariantCulture) != 1)
                return false;

            object count = ExecuteScalarInTransaction(connection, transaction, @"
SELECT COUNT(1)
FROM dbo.QualityEvents WITH (UPDLOCK, HOLDLOCK)
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID
  AND ISNULL(CurrentStatus, N'Open') NOT IN (N'Closed', N'QA Closed', N'Cancelled', N'Rejected Closed');",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

            return Convert.ToInt32(count ?? 0, CultureInfo.InvariantCulture) > 0;
        }

        private int GetPrmQualityEventStateInTransaction(SqlConnection connection, SqlTransaction transaction)
        {
            object state = ExecuteScalarInTransaction(connection, transaction, PrmQualityEventStateSql,
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

            return state == null || state == DBNull.Value
                ? 2
                : Convert.ToInt32(state, CultureInfo.InvariantCulture);
        }

        private int GetPrmQualityEventState()
        {
            EnsureCentralQualityEventCompatibility();
            object state = DatabaseHelper.ExecuteScalar(
                PrmQualityEventStateSql.Replace(" WITH (UPDLOCK, HOLDLOCK)", string.Empty, StringComparison.Ordinal),
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

            return state == null || state == DBNull.Value
                ? 2
                : Convert.ToInt32(state, CultureInfo.InvariantCulture);
        }

        private int UpdatePrmSampleInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string sampleStatus,
            string resultInterpretation,
            string reportStatus,
            string expectedStatus)
        {
            return DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Samples
SET SampleStatus = COALESCE(@SampleStatus, SampleStatus),
    ResultInterpretation = COALESCE(@ResultInterpretation, ResultInterpretation),
    ReportStatus = COALESCE(@ReportStatus, ReportStatus),
    ModifiedBy = @ModifiedBy,
    ModifiedDate = SYSDATETIME()
WHERE SampleID = @SampleID
  AND (@ExpectedStatus IS NULL OR SampleStatus = @ExpectedStatus);",
                new[]
                {
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                    new SqlParameter("@SampleStatus", SqlDbType.NVarChar, 60) { Value = string.IsNullOrWhiteSpace(sampleStatus) ? (object)DBNull.Value : sampleStatus },
                    new SqlParameter("@ResultInterpretation", SqlDbType.NVarChar, 60) { Value = string.IsNullOrWhiteSpace(resultInterpretation) ? (object)DBNull.Value : resultInterpretation },
                    new SqlParameter("@ReportStatus", SqlDbType.NVarChar, 60) { Value = string.IsNullOrWhiteSpace(reportStatus) ? (object)DBNull.Value : reportStatus },
                    new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 120) { Value = GetCurrentUserDisplayName() },
                    new SqlParameter("@ExpectedStatus", SqlDbType.NVarChar, 60) { Value = string.IsNullOrWhiteSpace(expectedStatus) ? (object)DBNull.Value : expectedStatus }
                }, conn, tx);
        }

        private void AddPrmElectronicSignatureInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string actionType,
            ElectronicSignature signature,
            string signerRole = null)
        {
            if (signature == null || !signature.IsConfirmed || string.IsNullOrWhiteSpace(signature.SignedBy))
                throw new InvalidOperationException("A valid current-user electronic signature is required.");

            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.PRM_ElectronicSignatures
(
    SampleID, ActionType, SignedBy, MeaningOfSignature, ActionReason, UserRole, SignedAt
)
VALUES
(
    @SampleID, @ActionType, @SignedBy, @Meaning, @Reason, @UserRole, SYSDATETIME()
);",
                new[]
                {
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                    new SqlParameter("@ActionType", SqlDbType.NVarChar, 80) { Value = actionType },
                    new SqlParameter("@SignedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy.Trim() },
                    new SqlParameter("@Meaning", SqlDbType.NVarChar, 255) { Value = signature.Meaning.Trim() },
                    new SqlParameter("@Reason", SqlDbType.NVarChar, -1) { Value = signature.Reason.Trim() },
                    new SqlParameter("@UserRole", SqlDbType.NVarChar, 80)
                    {
                        Value = string.IsNullOrWhiteSpace(signerRole)
                            ? (string.IsNullOrWhiteSpace(GetCurrentUserRole()) ? (object)DBNull.Value : GetCurrentUserRole())
                            : signerRole.Trim()
                    }
                }, conn, tx);
        }

        private bool HasCurrentUserSignedPrmAction(params string[] actionTypes)
        {
            if (_selectedSampleId <= 0 || actionTypes == null || actionTypes.Length == 0)
                return false;

            foreach (string actionType in actionTypes)
            {
                object result = DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1)
FROM dbo.PRM_ElectronicSignatures
WHERE SampleID = @SampleID
  AND ActionType = @ActionType
  AND SignedBy = @SignedBy;",
                    new[]
                    {
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@ActionType", SqlDbType.NVarChar, 80) { Value = actionType },
                        new SqlParameter("@SignedBy", SqlDbType.NVarChar, 120) { Value = GetCurrentUserDisplayName() }
                    });

                if (Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0)
                    return true;
            }

            return false;
        }

        private static bool IsAdminWorkflowOverrideAllowed()
        {
            return AppConfig.DevelopmentAdminFullPermissions && IsAdminUser();
        }

        private DataTable GetPrmElectronicSignatures()
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT S.SignatureID,S.SampleID,S.ActionType,S.SignedBy,
       COALESCE(NULLIF(LTRIM(RTRIM(U.FullName)),N''),S.SignedBy) AS SignerDisplayName,
       S.MeaningOfSignature,S.ActionReason,S.UserRole,S.SignedAt
FROM dbo.PRM_ElectronicSignatures S
LEFT JOIN dbo.Users U ON U.Username=S.SignedBy
WHERE S.SampleID=@SampleID
ORDER BY S.SignatureID;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });
        }

        private void AddPrmAudit(string action, string oldValue, string newValue, string reason)
        {
            DatabaseHelper.AddAuditTrailAdvanced(
                "PRM_Samples",
                _selectedSampleId,
                action,
                oldValue,
                newValue,
                reason,
                GetCurrentUserDisplayName(),
                null,
                null,
                TxtSampleNo?.Text,
                "PRM");
        }

        private static string CalculateInterpretation(
            string resultType,
            string specification,
            string result,
            string specificationLimit = "",
            string testCode = "",
            string testName = "")
        {
            if (string.IsNullOrWhiteSpace(result))
                return "Not Tested";

            string normalizedType = resultType ?? string.Empty;
            string normalizedResult = result.Trim();

            if (IsQualitativeAbsenceTest(normalizedType, specification))
            {
                if (IsNegativeQualitativeResult(normalizedResult))
                    return "Conforms";

                if (IsPositiveQualitativeResult(normalizedResult))
                    return "Does Not Conform";

                return "Check Required";
            }

            if (normalizedType.Contains("Numeric", StringComparison.OrdinalIgnoreCase))
            {
                if (!PrmNumericSpecificationEvaluator.TryParseControlledDecimal(normalizedResult, out decimal value))
                    return "Check Required";
                if (value < 0m)
                    return "Check Required";

                return PrmNumericSpecificationEvaluator.Evaluate(
                    value,
                    specification,
                    specificationLimit,
                    testCode,
                    testName);
            }

            if (normalizedResult.Equals("Pass", StringComparison.OrdinalIgnoreCase) || normalizedResult.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
                return "Conforms";
            if (normalizedResult.Equals("Fail", StringComparison.OrdinalIgnoreCase) || normalizedResult.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
                return "Does Not Conform";

            return "Check Required";
        }

        private static void EnsureWorkflowInterpretationIsComplete(string overall, string action)
        {
            if (overall.Equals("Check Required", StringComparison.OrdinalIgnoreCase) ||
                overall.Equals("Not Tested", StringComparison.OrdinalIgnoreCase) ||
                overall.Equals("In Progress", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(overall))
            {
                throw new InvalidOperationException(action + " is blocked because one or more required tests are incomplete or require interpretation.");
            }
        }

        private void EnsureCertificateInterpretationIsIssuable(string overall)
        {
            string category = S(GetCurrentSampleRow(), "SampleCategory");
            bool hasAnyQualityEvent = HasAnyPrmQualityEventMinimal();

            if (hasAnyQualityEvent)
            {
                int qualityEventState = GetPrmQualityEventState();
                if (qualityEventState == 1)
                    throw new InvalidOperationException(
                        "Certificate / report issuance is blocked while a PRM Quality Event / Investigation remains open for this sample.");
                if (qualityEventState == 3)
                    throw new InvalidOperationException(
                        "Certificate / report issuance is blocked because closed PRM investigation evidence is incomplete, unsupported, legacy/unversioned, or no longer matches the current result/specification snapshot.");
            }

            if (overall.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
                return;

            if (!overall.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Certificate / report issuance is blocked because the persisted final interpretation is incomplete or requires review.");

            if (!IsControlledNonReleaseReportCategory(category))
                throw new InvalidOperationException(
                    "Raw Material and Finished Product Certificates of Analysis can be issued only when the final interpretation is Conforms.");

            if (!hasAnyQualityEvent)
                throw new InvalidOperationException(
                    "A closed PRM Quality Event / Investigation is required before issuing a nonconforming In-Process or Stability report.");
        }

        private static bool IsControlledNonReleaseReportCategory(string category)
        {
            return category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("In-Process", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("Stability", StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureCertificateInterpretationIsIssuableInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string category,
            string overall)
        {
            bool hasAnyQualityEvent = HasAnyPrmQualityEventMinimalInTransaction(connection, transaction);
            if (hasAnyQualityEvent)
            {
                object investigationState = ExecuteScalarInTransaction(connection, transaction, PrmQualityEventStateSql,
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

                int state = investigationState == null || investigationState == DBNull.Value
                    ? 2
                    : Convert.ToInt32(investigationState, CultureInfo.InvariantCulture);

                if (state == 1)
                    throw new InvalidOperationException(
                        "The controlled certificate/report cannot be issued while a PRM Quality Event remains open.");

                if (state == 3)
                    throw new InvalidOperationException(
                        "The controlled certificate/report cannot be issued because closed PRM investigation evidence is incomplete, unsupported, legacy/unversioned, or no longer matches the current result/specification snapshot.");
            }

            if (overall.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
                return;

            if (!overall.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Certificate / report issuance is blocked because the persisted final interpretation is incomplete or requires review.");

            if (!IsControlledNonReleaseReportCategory(category))
                throw new InvalidOperationException(
                    "Raw Material and Finished Product Certificates of Analysis can be issued only when the final interpretation is Conforms.");

            if (!hasAnyQualityEvent)
                throw new InvalidOperationException(
                    "A closed PRM Quality Event / Investigation is required before issuing a nonconforming In-Process or Stability report.");
        }

        private static bool IsQualitativeAbsenceTest(string resultType, string specification)
        {
            string normalizedType = resultType ?? string.Empty;
            string normalizedSpecification = specification ?? string.Empty;

            return normalizedType.Contains("Presence", StringComparison.OrdinalIgnoreCase) ||
                   normalizedType.Contains("Qualitative", StringComparison.OrdinalIgnoreCase) ||
                   normalizedSpecification.Contains("Absent", StringComparison.OrdinalIgnoreCase) ||
                   normalizedSpecification.Contains("Absence", StringComparison.OrdinalIgnoreCase) ||
                   normalizedSpecification.Contains("Not Detected", StringComparison.OrdinalIgnoreCase) ||
                   normalizedSpecification.Contains("Negative", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNegativeQualitativeResult(string result)
        {
            string value = (result ?? string.Empty).Trim();
            return value.Equals("Absent", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Negative", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Not Detected", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("No Growth", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Nil", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPositiveQualitativeResult(string result)
        {
            string value = (result ?? string.Empty).Trim();
            return value.Equals("Present", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Presence", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Positive", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Detected", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("Growth", StringComparison.OrdinalIgnoreCase);
        }

        private static decimal ExtractFirstNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            StringBuilder sb = new StringBuilder();
            foreach (char ch in text)
            {
                if (char.IsDigit(ch) || ch == '.')
                    sb.Append(ch);
                else if (sb.Length > 0)
                    break;
            }

            decimal value;
            return decimal.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private bool AreAllRequiredResultsEntered()
        {
            if (_resultsTable != null && _resultsTable.Rows.Count > 0 &&
                _resultsTable.Columns.Contains("RequiredTest") &&
                _resultsTable.Columns.Contains("ResultValue"))
            {
                foreach (DataRow row in _resultsTable.Rows)
                {
                    if (row.RowState == DataRowState.Deleted)
                        continue;

                    bool isRequiredTest = true;
                    if (row["RequiredTest"] != DBNull.Value)
                        isRequiredTest = Convert.ToBoolean(row["RequiredTest"], CultureInfo.InvariantCulture);

                    string value = row["ResultValue"] == DBNull.Value ? string.Empty : Convert.ToString(row["ResultValue"], CultureInfo.InvariantCulture);
                    if (isRequiredTest && string.IsNullOrWhiteSpace(value))
                        return false;
                }

                return true;
            }

            int missing = Convert.ToInt32(DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1)
FROM dbo.PRM_SampleTests
WHERE SampleID = @SampleID
  AND ISNULL(RequiredTest, 1) = 1
  AND NULLIF(LTRIM(RTRIM(ISNULL(ResultValue, N''))), N'') IS NULL;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } }), CultureInfo.InvariantCulture);

            return missing == 0;
        }

        private string UpdateOverallInterpretation()
        {
            if (_selectedSampleId <= 0)
                return "Not Tested";

            DataTable table = _resultsTable;
            if (table == null || table.Rows.Count == 0 || !table.Columns.Contains("Interpretation"))
            {
                table = DatabaseHelper.ExecuteQuery(
                    @"SELECT Interpretation, ISNULL(RequiredTest, 1) AS RequiredTest,
                             ResultValue, ResultType, SpecificationText,
                             SpecificationLimit, TestCode, TestName
                      FROM dbo.PRM_SampleTests WHERE SampleID = @SampleID",
                    new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });
            }

            bool hasDnc = false;
            bool hasCheck = false;
            bool hasNotTested = false;
            bool hasConforms = false;

            foreach (DataRow row in table.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                if (table.Columns.Contains("RequiredTest") && row["RequiredTest"] != DBNull.Value &&
                    !Convert.ToBoolean(row["RequiredTest"], CultureInfo.InvariantCulture) &&
                    string.IsNullOrWhiteSpace(S(row, "ResultValue")))
                {
                    continue;
                }

                string interpretation;
                if (table.Columns.Contains("ResultType") &&
                    table.Columns.Contains("SpecificationText") &&
                    table.Columns.Contains("ResultValue"))
                {
                    interpretation = CalculateInterpretation(
                        S(row, "ResultType"),
                        S(row, "SpecificationText"),
                        S(row, "ResultValue"),
                        S(row, "SpecificationLimit"),
                        S(row, "TestCode"),
                        S(row, "TestName"));

                    if (table.Columns.Contains("Interpretation"))
                        SetDerivedInterpretationIfChanged(row, interpretation);
                }
                else
                {
                    interpretation = S(row, "Interpretation");
                }
                if (interpretation.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase)) hasDnc = true;
                else if (interpretation.Equals("Check Required", StringComparison.OrdinalIgnoreCase)) hasCheck = true;
                else if (interpretation.Equals("Not Tested", StringComparison.OrdinalIgnoreCase)) hasNotTested = true;
                else if (interpretation.Equals("Conforms", StringComparison.OrdinalIgnoreCase)) hasConforms = true;
            }

            string overall = "Not Tested";
            if (hasDnc) overall = "Does Not Conform";
            else if (hasCheck) overall = "Check Required";
            else if (hasNotTested) overall = "In Progress";
            else if (hasConforms) overall = "Conforms";

            return overall;
        }

        private string IssueCertificate(
            bool isReissue,
            string reason,
            int reissuedFromCertificateId,
            ElectronicSignature signature)
        {
            DataRow sample = null;
            DataTable results = null;
            string category = string.Empty;
            string prefix = string.Empty;
            string reportTitle = string.Empty;
            string certNo = string.Empty;
            DataTable signatureSnapshot = null;
            string issuanceStage = "issuer authorization";

            try
            {
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    issuanceStage = "issuer authorization";
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                    conn,
                    tx,
                    signature.SignedBy,
                    "CanIssueCOA",
                    isReissue ? "reissue PRM certificate/report" : "issue PRM certificate/report");

                    issuanceStage = "approved sample validation";
                    DataTable lockedSample = new DataTable();
                using (SqlCommand sampleCommand = new SqlCommand(@"
SELECT S.*,COALESCE(NULLIF(LTRIM(RTRIM(U.FullName)),N''),S.SampledBy) AS SampledByDisplay
FROM dbo.PRM_Samples S WITH(UPDLOCK,HOLDLOCK)
LEFT JOIN dbo.Users U WITH(HOLDLOCK) ON U.Username=S.SampledBy
WHERE S.SampleID=@SampleID;", conn, tx))
                {
                    sampleCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                    using SqlDataReader reader = sampleCommand.ExecuteReader();
                    lockedSample.Load(reader);
                }
                if (lockedSample.Rows.Count != 1)
                    throw new InvalidOperationException("The selected PRM sample was not found during certificate issuance.");
                sample = lockedSample.Rows[0];
                category = S(sample, "SampleCategory");
                prefix = GetCertificatePrefix(category);
                reportTitle = GetReportTitle(category);

                results = new DataTable();
                using (SqlCommand resultsCommand = new SqlCommand(@"
SELECT * FROM dbo.PRM_SampleTests WITH(UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
ORDER BY ISNULL(SortOrder,SampleTestID),SampleTestID;", conn, tx))
                {
                    resultsCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                    using SqlDataReader reader = resultsCommand.ExecuteReader();
                    results.Load(reader);
                }
                if (results.Rows.Count == 0)
                    throw new InvalidOperationException("Certificate issuance is blocked because no frozen PRM test results were found.");

                object statusValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT SampleStatus
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });

                string status = statusValue == null || statusValue == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(statusValue, CultureInfo.InvariantCulture);

                if (!IsOneOf(status, "Approved", "Certificate Issued"))
                    throw new InvalidOperationException("The PRM sample is no longer approved for certificate issuance.");

                bool controlledHistoricalLegacyReissue = false;
                if (isReissue &&
                    _controlledLegacyReissueRoute &&
                    !_controlledLegacyReissueCompleted &&
                    _selectedSampleId == _initialSampleId &&
                    reissuedFromCertificateId == _initialLegacyCertificateId)
                {
                    object reconciliationAuthorized = ExecuteScalarInTransaction(conn, tx, @"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.LegacyCertificateEvidenceReconciliations R WITH(UPDLOCK,HOLDLOCK)
    WHERE R.ReconciliationID=@ReconciliationID
      AND R.CertificateModule=N'PRM'
      AND R.CertificateID=@CertificateID
      AND UPPER(LTRIM(RTRIM(ISNULL(R.Disposition,N''))))=N'CONTROLLED_REISSUE_REQUIRED'
      AND NULLIF(LTRIM(RTRIM(ISNULL(R.EvidenceReference,N''))),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(R.EvidenceSummary,N''))),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(R.Reason,N''))),N'') IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(R.SignedBy,N''))),N'') IS NOT NULL
      AND R.SignedAt IS NOT NULL
      AND R.ReconciliationID=
      (
          SELECT MAX(R2.ReconciliationID)
          FROM dbo.LegacyCertificateEvidenceReconciliations R2 WITH(UPDLOCK,HOLDLOCK)
          WHERE R2.CertificateModule=N'PRM'
            AND R2.CertificateID=@CertificateID
      )
)
THEN 1 ELSE 0 END;",
                        new SqlParameter("@ReconciliationID", SqlDbType.Int) { Value = _initialLegacyReconciliationId },
                        new SqlParameter("@CertificateID", SqlDbType.Int) { Value = _initialLegacyCertificateId });

                    if (Convert.ToInt32(reconciliationAuthorized ?? 0, CultureInfo.InvariantCulture) != 1)
                    {
                        throw new InvalidOperationException(
                            "Controlled legacy reissue is blocked because the routed signed QA reconciliation is no longer the latest valid CONTROLLED_REISSUE_REQUIRED disposition for this certificate. Return to Legacy Certificate Evidence Reconciliation and refresh.");
                    }

                    controlledHistoricalLegacyReissue = true;
                }

                EnsurePrmTimingReconciliationClearedInTransaction(
                    conn,
                    tx,
                    "Certificate / Report Issuance",
                    allowHistoricalClosedForControlledLegacyReissue: controlledHistoricalLegacyReissue);

                object analysisStartedValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT AnalysisStartedDate
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });
                object analysisCompletedValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT AnalysisCompletedDate
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;",
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });
                bool analysisStartedPresent = analysisStartedValue != null && analysisStartedValue != DBNull.Value;
                bool analysisCompletedPresent = analysisCompletedValue != null && analysisCompletedValue != DBNull.Value;
                if (!analysisStartedPresent || !analysisCompletedPresent)
                {
                    if (!controlledHistoricalLegacyReissue)
                    {
                        throw new InvalidOperationException(
                            "Certificate issuance is blocked because Analysis Started and Analysis Completed date/time are required.");
                    }
                }
                else
                {
                    DateTime analysisStarted = Convert.ToDateTime(analysisStartedValue, CultureInfo.InvariantCulture);
                    DateTime analysisCompleted = Convert.ToDateTime(analysisCompletedValue, CultureInfo.InvariantCulture);
                    if (analysisCompleted < analysisStarted)
                        throw new InvalidOperationException("Certificate issuance is blocked because Analysis Completed precedes Analysis Started.");
                }

                string authoritativeOverall;
                if (controlledHistoricalLegacyReissue)
                {
                    issuanceStage = "controlled historical result evidence reconstruction";
                    results = LoadControlledHistoricalLegacyResultsInTransaction(
                        conn,
                        tx,
                        reissuedFromCertificateId);
                    authoritativeOverall = DeriveControlledHistoricalLegacyInterpretation(results);
                }
                else
                {
                    PrmAuthoritativeSampleResultState authoritative =
                        PrmSampleResultStateService.DeriveAuthoritativeState(results);
                    if (!authoritative.AllRequiredResultsEntered)
                        throw new InvalidOperationException(
                            "Certificate issuance is blocked because one or more required PRM tests are incomplete.");
                    PrmSampleResultStateService.EnsurePersistedInterpretationsMatchEvidence(
                        authoritative, "Certificate / Report Issuance");
                    EnsureWorkflowInterpretationIsComplete(
                        authoritative.OverallInterpretation, "Certificate / Report Issuance");
                    authoritativeOverall = authoritative.OverallInterpretation;
                }

                string persistedInterpretation = S(sample, "ResultInterpretation").Trim();
                if (!persistedInterpretation.Equals(authoritativeOverall, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        controlledHistoricalLegacyReissue
                            ? "Controlled legacy reissue is blocked because the historical result evidence does not match the persisted final interpretation of the issued sample."
                            : "Certificate issuance is blocked because the approved sample summary no longer matches the authoritative PRM test evidence.");
                }

                EnsureCertificateInterpretationIsIssuableInTransaction(
                    conn,
                    tx,
                    category,
                    authoritativeOverall);

                int latestCertificateId = 0;
                string latestCertificateStatus = string.Empty;
                string latestCancellationReason = string.Empty;
                string latestCancelledBy = string.Empty;
                DateTime? latestCancelledDate = null;
                using (SqlCommand latestCertificateCommand = new SqlCommand(@"
SELECT TOP(1)
    CertificateID,
    LTRIM(RTRIM(ISNULL(CertificateStatus,N''))) AS CertificateStatus,
    LTRIM(RTRIM(ISNULL(CancellationReason,N''))) AS CancellationReason,
    LTRIM(RTRIM(ISNULL(CancelledBy,N''))) AS CancelledBy,
    CancelledDate
FROM dbo.PRM_Certificates WITH (UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
ORDER BY ISNULL(RevisionNo,-1) DESC, CertificateID DESC;", conn, tx))
                {
                    latestCertificateCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    latestCertificateCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                    using SqlDataReader reader = latestCertificateCommand.ExecuteReader();
                    if (reader.Read())
                    {
                        latestCertificateId = reader.GetInt32(0);
                        latestCertificateStatus = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        latestCancellationReason = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                        latestCancelledBy = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                        latestCancelledDate = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
                    }
                }

                if (isReissue)
                {
                    if (reissuedFromCertificateId <= 0 || latestCertificateId != reissuedFromCertificateId)
                    {
                        throw new InvalidOperationException(
                            "Reissue must reference the latest PRM certificate/report for this sample. The certificate lifecycle changed; reload before continuing.");
                    }

                    if (latestCertificateStatus.Equals("Active", StringComparison.OrdinalIgnoreCase))
                    {
                        int cancelled = CancelCertificateInTransaction(conn, tx, reissuedFromCertificateId, "Reissued: " + reason, signature.SignedBy);
                        if (cancelled != 1)
                            throw new InvalidOperationException("The active certificate changed before reissue. Nothing was changed.");
                        AddCertificateHistoryInTransaction(conn, tx, reissuedFromCertificateId, "Cancelled for Reissue", reason, signature.SignedBy);
                        DatabaseHelper.AddAuditTrailAdvanced(
                            conn,
                            tx,
                            "PRM_Certificates",
                            reissuedFromCertificateId,
                            "Certificate Cancelled for Reissue",
                            "Active",
                            "Cancelled",
                            signature.Reason,
                            signature.SignedBy,
                            "CertificateStatus",
                            null,
                            null,
                            "PRM Certificate");
                    }
                    else if (latestCertificateStatus.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(latestCancellationReason) ||
                            string.IsNullOrWhiteSpace(latestCancelledBy) ||
                            !latestCancelledDate.HasValue)
                        {
                            throw new InvalidOperationException(
                                "The latest cancelled PRM certificate/report lacks complete cancellation evidence. Reissue is blocked until the lifecycle record is reconciled.");
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "The latest PRM certificate/report is neither Active nor Cancelled. Reissue is blocked until the certificate lifecycle is reconciled.");
                    }
                }
                else if (latestCertificateId > 0)
                {
                    throw new InvalidOperationException(
                        "A prior PRM certificate/report already exists for this sample. A replacement must use the controlled Reissue workflow so ReissuedFromCertificateID remains traceable.");
                }

                    issuanceStage = "certificate numbering";
                    certNo = GenerateCertificateNumber(conn, tx, prefix);
                int revision = GetNextRevisionNo(conn, tx);
                DateTime issueDate = Convert.ToDateTime(
                    ExecuteScalarInTransaction(conn, tx, "SELECT SYSDATETIME();"),
                    CultureInfo.InvariantCulture);
                string hash = BuildPrmReportDataHash(sample, results, certNo, revision, reportTitle, issueDate, signature);
                string verification = hash.Substring(0, 12);

                    issuanceStage = "certificate record creation";
                    object certificateIdValue = ExecuteScalarInTransaction(conn, tx, @"
INSERT INTO dbo.PRM_Certificates
(
    CertificateNumber, SampleID, CertificateType, ReportTitle, IssueDate, IssuedBy,
    CertificateStatus, RevisionNo, ReissuedFromCertificateID, CancellationReason,
    VerificationCode, ReportHash, CreatedBy, CreatedDate
)
VALUES
(
    @CertificateNumber, @SampleID, @CertificateType, @ReportTitle, @IssueDate, @IssuedBy,
    N'Active', @RevisionNo, @ReissuedFromCertificateID, @CancellationReason,
    @VerificationCode, @ReportHash, @CreatedBy, SYSDATETIME()
);
SELECT CAST(SCOPE_IDENTITY() AS int);",
                    new SqlParameter("@CertificateNumber", SqlDbType.NVarChar, 60) { Value = certNo },
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                    new SqlParameter("@CertificateType", SqlDbType.NVarChar, 80) { Value = prefix },
                    new SqlParameter("@ReportTitle", SqlDbType.NVarChar, 200) { Value = reportTitle },
                    new SqlParameter("@IssueDate", SqlDbType.DateTime2) { Value = issueDate },
                    new SqlParameter("@IssuedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                    new SqlParameter("@RevisionNo", SqlDbType.Int) { Value = revision },
                    new SqlParameter("@ReissuedFromCertificateID", SqlDbType.Int) { Value = reissuedFromCertificateId > 0 ? (object)reissuedFromCertificateId : DBNull.Value },
                    new SqlParameter("@CancellationReason", SqlDbType.NVarChar, 500) { Value = string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason },
                    new SqlParameter("@VerificationCode", SqlDbType.NVarChar, 80) { Value = verification },
                    new SqlParameter("@ReportHash", SqlDbType.NVarChar, 120) { Value = hash },
                    new SqlParameter("@CreatedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy });

                    int certificateId = Convert.ToInt32(certificateIdValue, CultureInfo.InvariantCulture);
                    string issueAction = isReissue ? "Certificate Reissue" : "Certificate Issuance";
                    issuanceStage = "certificate history recording";
                    AddCertificateHistoryInTransaction(conn, tx, certificateId, isReissue ? "Reissued" : "Issued", reason, signature.SignedBy);
                    issuanceStage = "electronic signature recording";
                    AddPrmElectronicSignatureInTransaction(conn, tx, issueAction, signature, signerRole);

                    issuanceStage = "immutable snapshot generation";

                    // Build the immutable document from the electronic-signature rows that are
                // actually persisted inside this transaction. Do not synthesize an identity
                // row inside a DataTable loaded from SQL metadata; identity/read-only/unique
                // metadata varies by provider and can make Rows.Add fail even though the
                // underlying signature was recorded successfully.
                signatureSnapshot = LoadPrmElectronicSignatureSnapshotInTransaction(conn, tx);

                if (controlledHistoricalLegacyReissue)
                {
                    bool hasResultEntry = false;
                    bool hasReview = false;
                    bool hasApproval = false;
                    bool hasReissueSignature = false;
                    foreach (DataRow signatureRow in signatureSnapshot.Rows)
                    {
                        string actionType = Convert.ToString(signatureRow["ActionType"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        if (actionType.Equals("Result Entry", StringComparison.OrdinalIgnoreCase))
                            hasResultEntry = true;
                        else if (actionType.Equals("Review", StringComparison.OrdinalIgnoreCase))
                            hasReview = true;
                        else if (actionType.Equals("Approval", StringComparison.OrdinalIgnoreCase))
                            hasApproval = true;
                        else if (actionType.Equals("Certificate Reissue", StringComparison.OrdinalIgnoreCase))
                            hasReissueSignature = true;
                    }

                    if (!hasResultEntry || !hasReview || !hasApproval || !hasReissueSignature)
                    {
                        throw new InvalidOperationException(
                            "Controlled legacy reissue is blocked because the replacement certificate would not contain a complete Result Entry / Review / Approval / Certificate Reissue electronic-signature chain. No retrospective signatures will be fabricated.");
                    }
                }

                DataRow certificateSnapshotRow = BuildPrmCertificateSnapshotRow(
                    certificateId, certNo, prefix, reportTitle, issueDate, signature.SignedBy,
                    revision, reissuedFromCertificateId, reason, verification, hash);
                string htmlSnapshot = PRMCertificateTemplate.Build(sample, results, certificateSnapshotRow, signatureSnapshot);
                string htmlSnapshotHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(htmlSnapshot)));
                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT dbo.PRM_CertificateSnapshots
(CertificateID,SampleID,CertificateNumber,HtmlContent,SnapshotHash,CreatedBy,CreatedAt)
VALUES(@CertificateID,@SampleID,@CertificateNumber,@HtmlContent,@SnapshotHash,@CreatedBy,@CreatedAt);",
                    new[]
                    {
                        new SqlParameter("@CertificateID", SqlDbType.Int) { Value = certificateId },
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@CertificateNumber", SqlDbType.NVarChar, 60) { Value = certNo },
                        new SqlParameter("@HtmlContent", SqlDbType.NVarChar, -1) { Value = htmlSnapshot },
                        new SqlParameter("@SnapshotHash", SqlDbType.NVarChar, 128) { Value = htmlSnapshotHash },
                        new SqlParameter("@CreatedBy", SqlDbType.NVarChar, 120) { Value = signature.SignedBy },
                        new SqlParameter("@CreatedAt", SqlDbType.DateTime2) { Value = issueDate }
                    }, conn, tx);

                    issuanceStage = "sample finalization";
                    int sampleUpdated = UpdatePrmSampleInTransaction(conn, tx, "Certificate Issued", null, "Certificate Issued", status);
                if (sampleUpdated != 1)
                    throw new InvalidOperationException("The PRM sample changed during certificate issuance. No certificate was issued.");

                    issuanceStage = "audit recording";
                    if (!status.Equals("Certificate Issued", StringComparison.OrdinalIgnoreCase))
                {
                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn, tx, "PRM_Samples", _selectedSampleId, issueAction,
                        status, "Certificate Issued", signature.Reason, signature.SignedBy,
                        "SampleStatus", null, TxtSampleNo?.Text, "PRM");
                }

                    DatabaseHelper.AddAuditTrailAdvanced(
                        conn, tx, "PRM_Certificates", certificateId, issueAction,
                        S(sample, "ReportStatus"), "Certificate Issued", signature.Reason,
                        signature.SignedBy, "CertificateStatus", null, certNo, "PRM Certificate");
                });
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    "Certificate / report issuance failed during " + issuanceStage + ". The transaction was rolled back.",
                    ex);
            }
            catch (DataException ex)
            {
                throw new InvalidOperationException(
                    "Certificate / report issuance failed during " + issuanceStage + ". The transaction was rolled back.",
                    ex);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    "Certificate / report issuance failed during " + issuanceStage + ". The transaction was rolled back.",
                    ex);
            }

            return certNo;
        }

        private DataTable LoadControlledHistoricalLegacyResultsInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int sourceCertificateId)
        {
            DataTable historical = new DataTable();
            using (SqlCommand command = new SqlCommand(@"
SELECT
    t.*,
    e.EvidenceID AS HistoricalEvidenceID,
    e.OriginalResultValue AS HistoricalResultValue,
    e.OriginalInterpretation AS HistoricalInterpretation,
    e.OriginalRemarks AS HistoricalRemarks,
    e.OriginalEnteredBy AS HistoricalEnteredBy,
    e.OriginalEnteredDate AS HistoricalEnteredDate,
    c.IssueDate AS SourceCertificateIssueDate
FROM dbo.PRM_SampleTests t WITH(UPDLOCK,HOLDLOCK)
INNER JOIN dbo.PRM_Certificates c WITH(UPDLOCK,HOLDLOCK)
    ON c.CertificateID=@CertificateID
   AND c.SampleID=t.SampleID
LEFT JOIN dbo.PRM_TimingMigrationTestEvidence e WITH(HOLDLOCK)
    ON e.SampleID=t.SampleID
   AND e.SampleTestID=t.SampleTestID
WHERE t.SampleID=@SampleID
  AND NULLIF(
        LTRIM(RTRIM(ISNULL(
            CASE WHEN e.EvidenceID IS NOT NULL THEN e.OriginalResultValue ELSE t.ResultValue END,
            N''))),
        N'') IS NOT NULL
ORDER BY ISNULL(t.SortOrder,t.SampleTestID),t.SampleTestID;", conn, tx))
            {
                command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                command.Parameters.Add("@CertificateID", SqlDbType.Int).Value = sourceCertificateId;
                command.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                using SqlDataReader reader = command.ExecuteReader();
                historical.Load(reader);
            }

            if (historical.Rows.Count == 0)
            {
                throw new InvalidOperationException(
                    "Controlled legacy reissue is blocked because no attributable historical PRM result evidence was found for the source certificate.");
            }

            bool hasRequiredResult = false;
            foreach (DataRow row in historical.Rows)
            {
                bool hasCapturedMigrationEvidence =
                    historical.Columns.Contains("HistoricalEvidenceID") &&
                    row["HistoricalEvidenceID"] != DBNull.Value;

                if (hasCapturedMigrationEvidence)
                {
                    row["ResultValue"] = row["HistoricalResultValue"] == DBNull.Value
                        ? DBNull.Value
                        : row["HistoricalResultValue"];
                    row["Interpretation"] = row["HistoricalInterpretation"] == DBNull.Value
                        ? DBNull.Value
                        : row["HistoricalInterpretation"];
                    row["Remarks"] = row["HistoricalRemarks"] == DBNull.Value
                        ? DBNull.Value
                        : row["HistoricalRemarks"];
                    row["EnteredBy"] = row["HistoricalEnteredBy"] == DBNull.Value
                        ? DBNull.Value
                        : row["HistoricalEnteredBy"];
                    row["EnteredDate"] = row["HistoricalEnteredDate"] == DBNull.Value
                        ? DBNull.Value
                        : row["HistoricalEnteredDate"];
                }

                string resultValue = S(row, "ResultValue").Trim();
                string enteredBy = S(row, "EnteredBy").Trim();
                if (string.IsNullOrWhiteSpace(resultValue) ||
                    string.IsNullOrWhiteSpace(enteredBy) ||
                    row["EnteredDate"] == DBNull.Value)
                {
                    throw new InvalidOperationException(
                        "Controlled legacy reissue is blocked because the historical result evidence is incomplete or unattributable. No retrospective result evidence will be created.");
                }

                DateTime enteredDate = Convert.ToDateTime(row["EnteredDate"], CultureInfo.InvariantCulture);
                DateTime sourceIssueDate = Convert.ToDateTime(
                    row["SourceCertificateIssueDate"],
                    CultureInfo.InvariantCulture);
                if (enteredDate > sourceIssueDate)
                {
                    throw new InvalidOperationException(
                        "Controlled legacy reissue is blocked because a persisted PRM result was entered after the source certificate issue date. Historical evidence cannot be reconstructed safely.");
                }

                if (!historical.Columns.Contains("RequiredTest") ||
                    row["RequiredTest"] == DBNull.Value ||
                    Convert.ToBoolean(row["RequiredTest"], CultureInfo.InvariantCulture))
                {
                    hasRequiredResult = true;
                }
            }

            if (!hasRequiredResult)
            {
                throw new InvalidOperationException(
                    "Controlled legacy reissue is blocked because no required historical PRM result evidence is available.");
            }

            return historical;
        }

        private static string DeriveControlledHistoricalLegacyInterpretation(DataTable historicalResults)
        {
            bool hasConforms = false;
            bool hasDoesNotConform = false;

            foreach (DataRow row in historicalResults.Rows)
            {
                string interpretation = S(row, "Interpretation").Trim();
                if (interpretation.Equals("Conforms", StringComparison.OrdinalIgnoreCase))
                {
                    hasConforms = true;
                    continue;
                }

                if (interpretation.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase))
                {
                    hasDoesNotConform = true;
                    continue;
                }

                throw new InvalidOperationException(
                    "Controlled legacy reissue is blocked because a historical PRM result has an incomplete or non-final persisted interpretation. The legacy result will not be reinterpreted retrospectively.");
            }

            if (hasDoesNotConform)
                return "Does Not Conform";
            if (hasConforms)
                return "Conforms";

            throw new InvalidOperationException(
                "Controlled legacy reissue is blocked because no final historical PRM interpretation is available.");
        }

        private void CancelCertificate(int certificateId, string reason, ElectronicSignature signature)
        {
            DatabaseHelper.ExecuteInTransaction((conn, tx) =>
            {
                string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                    conn,
                    tx,
                    signature.SignedBy,
                    "CanCancelCOA",
                    "cancel PRM certificate/report");

                string lockedStatus = GetLockedPrmSampleStatusInTransaction(conn, tx);
                if (!lockedStatus.Equals("Certificate Issued", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Certificate cancellation is allowed only while the PRM sample is in Certificate Issued status. " +
                        "Current status: " + lockedStatus + ".");
                }

                int affected = CancelCertificateInTransaction(conn, tx, certificateId, reason, signature.SignedBy);
                if (affected != 1)
                    throw new InvalidOperationException("The certificate is no longer active. Nothing was cancelled.");

                AddCertificateHistoryInTransaction(conn, tx, certificateId, "Cancelled", reason, signature.SignedBy);
                AddPrmElectronicSignatureInTransaction(conn, tx, "Certificate Cancellation", signature, signerRole);

                int sampleUpdated = UpdatePrmSampleInTransaction(conn, tx, "Approved", null, "Cancelled", lockedStatus);
                if (sampleUpdated != 1)
                    throw new InvalidOperationException("The PRM sample could not be updated. Certificate cancellation was rolled back.");

                DatabaseHelper.AddAuditTrailAdvanced(
                    conn, tx, "PRM_Samples", _selectedSampleId, "Certificate Cancellation",
                    lockedStatus, "Approved", signature.Reason, signature.SignedBy,
                    "SampleStatus", null, TxtSampleNo?.Text, "PRM");

                DatabaseHelper.AddAuditTrailAdvanced(
                    conn, tx, "PRM_Certificates", certificateId, "Certificate Cancellation",
                    "Active", "Cancelled", signature.Reason, signature.SignedBy,
                    "CertificateStatus", null, TxtCertificateNo?.Text, "PRM Certificate");
            });
        }

        private int CancelCertificateInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int certificateId,
            string reason,
            string signedBy)
        {
            return DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.PRM_Certificates
SET CertificateStatus = N'Cancelled',
    IsCancelled = 1,
    CancelledBy = @CancelledBy,
    CancelledDate = SYSDATETIME(),
    CancellationReason = @CancellationReason
WHERE CertificateID = @CertificateID AND SampleID = @SampleID AND CertificateStatus = N'Active';",
                new[]
                {
                    new SqlParameter("@CertificateID", SqlDbType.Int) { Value = certificateId },
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                    new SqlParameter("@CancelledBy", SqlDbType.NVarChar, 120) { Value = signedBy },
                    new SqlParameter("@CancellationReason", SqlDbType.NVarChar, 500) { Value = reason }
                }, conn, tx);
        }

        private void AddCertificateHistoryInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int certificateId,
            string action,
            string reason,
            string actionBy)
        {
            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT INTO dbo.PRM_CertificateHistory (CertificateID, ActionName, ActionBy, ActionDate, Reason)
VALUES (@CertificateID, @ActionName, @ActionBy, SYSDATETIME(), @Reason);",
                new[]
                {
                    new SqlParameter("@CertificateID", SqlDbType.Int) { Value = certificateId },
                    new SqlParameter("@ActionName", SqlDbType.NVarChar, 80) { Value = action },
                    new SqlParameter("@ActionBy", SqlDbType.NVarChar, 120) { Value = actionBy },
                    new SqlParameter("@Reason", SqlDbType.NVarChar, 500) { Value = string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason }
                }, conn, tx);
        }

        private string GenerateCertificateNumber(SqlConnection conn, SqlTransaction tx, string prefix)
        {
            object result = ExecuteScalarInTransaction(conn, tx, @"
DECLARE @Year int = YEAR(SYSDATETIME());
DECLARE @CurrentYear int = NULL;
DECLARE @SequenceLast int = NULL;
DECLARE @ExistingMax int = 0;
DECLARE @NextNumber int;
DECLARE @CertificateNumber nvarchar(60);
DECLARE @NumberPrefix nvarchar(60) = @Prefix + N'-' + CONVERT(nvarchar(4), @Year) + N'-';

-- Lock the controlled sequence row (or its key range when it does not yet exist).
SELECT
    @CurrentYear = CurrentYear,
    @SequenceLast = LastNumber
FROM dbo.PRM_NumberSequences WITH (UPDLOCK, HOLDLOCK)
WHERE SequenceName = @SequenceName;

-- Reconcile the sequence with certificates that already exist in the database.
-- This protects upgraded installations where historical certificates pre-date
-- the CERT_* sequence row or where the sequence counter is behind history.
SELECT @ExistingMax = ISNULL(MAX(TRY_CONVERT(int,
    SUBSTRING(CertificateNumber, LEN(@NumberPrefix) + 1, 20))), 0)
FROM dbo.PRM_Certificates WITH (UPDLOCK, HOLDLOCK)
WHERE LEFT(CertificateNumber, LEN(@NumberPrefix)) = @NumberPrefix
  AND TRY_CONVERT(int, SUBSTRING(CertificateNumber, LEN(@NumberPrefix) + 1, 20)) IS NOT NULL;

IF @SequenceLast IS NULL
BEGIN
    SET @NextNumber = @ExistingMax + 1;
    INSERT dbo.PRM_NumberSequences
        (SequenceName, Prefix, CurrentYear, LastNumber, LastUpdated)
    VALUES
        (@SequenceName, @Prefix, @Year, @NextNumber, SYSDATETIME());
END
ELSE
BEGIN
    SET @NextNumber = CASE
        WHEN @CurrentYear = @Year AND @SequenceLast > @ExistingMax THEN @SequenceLast + 1
        ELSE @ExistingMax + 1
    END;

    UPDATE dbo.PRM_NumberSequences
    SET Prefix = @Prefix,
        CurrentYear = @Year,
        LastNumber = @NextNumber,
        LastUpdated = SYSDATETIME()
    WHERE SequenceName = @SequenceName;
END;

SET @CertificateNumber = @NumberPrefix +
    CASE WHEN @NextNumber <= 9999
         THEN RIGHT(N'0000' + CONVERT(nvarchar(20), @NextNumber), 4)
         ELSE CONVERT(nvarchar(20), @NextNumber)
    END;

-- Defensive compatibility guard for legacy/manual certificate numbers.
WHILE EXISTS
(
    SELECT 1
    FROM dbo.PRM_Certificates WITH (UPDLOCK, HOLDLOCK)
    WHERE CertificateNumber = @CertificateNumber
)
BEGIN
    SET @NextNumber = @NextNumber + 1;
    UPDATE dbo.PRM_NumberSequences
    SET LastNumber = @NextNumber, LastUpdated = SYSDATETIME()
    WHERE SequenceName = @SequenceName;

    SET @CertificateNumber = @NumberPrefix +
        CASE WHEN @NextNumber <= 9999
             THEN RIGHT(N'0000' + CONVERT(nvarchar(20), @NextNumber), 4)
             ELSE CONVERT(nvarchar(20), @NextNumber)
        END;
END;

SELECT @CertificateNumber;",
                new SqlParameter("@SequenceName", SqlDbType.NVarChar, 60) { Value = "CERT_" + prefix },
                new SqlParameter("@Prefix", SqlDbType.NVarChar, 20) { Value = prefix });

            string number = result == null || result == DBNull.Value
                ? string.Empty
                : Convert.ToString(result, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(number))
                throw new InvalidOperationException("A controlled PRM certificate/report number could not be generated.");

            return number;
        }

        private static Task EnsurePrmCertificateSchemaReadyForActionAsync()
        {
            // Operational workflow actions are read-only with respect to database
            // schema. Development DDL is performed only by the signed Database
            // Maintenance action on the main dashboard.
            EnsurePrmCertificateSchemaCompatibility();
            return Task.CompletedTask;
        }

        private static void EnsurePrmCertificateSchemaCompatibility()
        {
            object missingValue = DatabaseHelper.ExecuteScalar(@"
DECLARE @Missing TABLE (ObjectName nvarchar(260) NOT NULL);

IF OBJECT_ID(N'dbo.PRM_NumberSequences', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_NumberSequences');
IF OBJECT_ID(N'dbo.PRM_Certificates', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_Certificates');
IF OBJECT_ID(N'dbo.PRM_CertificateHistory', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_CertificateHistory');
IF OBJECT_ID(N'dbo.PRM_CertificateSnapshots', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_CertificateSnapshots');
IF OBJECT_ID(N'dbo.PRM_ElectronicSignatures', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_ElectronicSignatures');
IF OBJECT_ID(N'dbo.PRM_Samples', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_Samples');
IF OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NULL INSERT @Missing VALUES (N'dbo.PRM_SampleTests');

IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'SequenceName') IS NULL INSERT @Missing VALUES (N'PRM_NumberSequences.SequenceName');
IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'Prefix') IS NULL INSERT @Missing VALUES (N'PRM_NumberSequences.Prefix');
IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'CurrentYear') IS NULL INSERT @Missing VALUES (N'PRM_NumberSequences.CurrentYear');
IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'LastNumber') IS NULL INSERT @Missing VALUES (N'PRM_NumberSequences.LastNumber');
IF COL_LENGTH(N'dbo.PRM_NumberSequences',N'LastUpdated') IS NULL INSERT @Missing VALUES (N'PRM_NumberSequences.LastUpdated');

IF COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateID') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CertificateID');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateNumber') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CertificateNumber');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'SampleID') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.SampleID');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateType') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CertificateType');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'ReportTitle') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.ReportTitle');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'IssueDate') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.IssueDate');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'IssuedBy') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.IssuedBy');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CertificateStatus') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CertificateStatus');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'RevisionNo') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.RevisionNo');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'IsCancelled') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.IsCancelled');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CancelledBy') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CancelledBy');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CancelledDate') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CancelledDate');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CancellationReason') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CancellationReason');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'ReissuedFromCertificateID') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.ReissuedFromCertificateID');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'VerificationCode') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.VerificationCode');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'ReportHash') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.ReportHash');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CreatedBy') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CreatedBy');
IF COL_LENGTH(N'dbo.PRM_Certificates',N'CreatedDate') IS NULL INSERT @Missing VALUES (N'PRM_Certificates.CreatedDate');

IF COL_LENGTH(N'dbo.PRM_CertificateHistory',N'CertificateID') IS NULL INSERT @Missing VALUES (N'PRM_CertificateHistory.CertificateID');
IF COL_LENGTH(N'dbo.PRM_CertificateHistory',N'ActionName') IS NULL INSERT @Missing VALUES (N'PRM_CertificateHistory.ActionName');
IF COL_LENGTH(N'dbo.PRM_CertificateHistory',N'ActionBy') IS NULL INSERT @Missing VALUES (N'PRM_CertificateHistory.ActionBy');
IF COL_LENGTH(N'dbo.PRM_CertificateHistory',N'ActionDate') IS NULL INSERT @Missing VALUES (N'PRM_CertificateHistory.ActionDate');
IF COL_LENGTH(N'dbo.PRM_CertificateHistory',N'Reason') IS NULL INSERT @Missing VALUES (N'PRM_CertificateHistory.Reason');

IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'CertificateID') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.CertificateID');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'SampleID') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.SampleID');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'CertificateNumber') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.CertificateNumber');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'HtmlContent') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.HtmlContent');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'SnapshotHash') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.SnapshotHash');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'CreatedBy') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.CreatedBy');
IF COL_LENGTH(N'dbo.PRM_CertificateSnapshots',N'CreatedAt') IS NULL INSERT @Missing VALUES (N'PRM_CertificateSnapshots.CreatedAt');

IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'SignatureID') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.SignatureID');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'SampleID') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.SampleID');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'ActionType') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.ActionType');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'SignedBy') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.SignedBy');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'MeaningOfSignature') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.MeaningOfSignature');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'ActionReason') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.ActionReason');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'UserRole') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.UserRole');
IF COL_LENGTH(N'dbo.PRM_ElectronicSignatures',N'SignedAt') IS NULL INSERT @Missing VALUES (N'PRM_ElectronicSignatures.SignedAt');

IF COL_LENGTH(N'dbo.PRM_Samples',N'SampleID') IS NULL INSERT @Missing VALUES (N'PRM_Samples.SampleID');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SampleNumber') IS NULL INSERT @Missing VALUES (N'PRM_Samples.SampleNumber');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SampleCategory') IS NULL INSERT @Missing VALUES (N'PRM_Samples.SampleCategory');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SampleStatus') IS NULL INSERT @Missing VALUES (N'PRM_Samples.SampleStatus');
IF COL_LENGTH(N'dbo.PRM_Samples',N'ResultInterpretation') IS NULL INSERT @Missing VALUES (N'PRM_Samples.ResultInterpretation');
IF COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisStartedDate') IS NULL INSERT @Missing VALUES (N'PRM_Samples.AnalysisStartedDate');
IF COL_LENGTH(N'dbo.PRM_Samples',N'AnalysisCompletedDate') IS NULL INSERT @Missing VALUES (N'PRM_Samples.AnalysisCompletedDate');
IF COL_LENGTH(N'dbo.PRM_Samples',N'ReviewedDate') IS NULL INSERT @Missing VALUES (N'PRM_Samples.ReviewedDate');
IF COL_LENGTH(N'dbo.PRM_Samples',N'ApprovedDate') IS NULL INSERT @Missing VALUES (N'PRM_Samples.ApprovedDate');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationNo') IS NULL INSERT @Missing VALUES (N'PRM_Samples.SpecificationNo');
IF COL_LENGTH(N'dbo.PRM_Samples',N'SpecificationVersionNo') IS NULL INSERT @Missing VALUES (N'PRM_Samples.SpecificationVersionNo');

IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SampleTestID') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.SampleTestID');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SampleID') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.SampleID');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'TestName') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.TestName');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SpecificationText') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.SpecificationText');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'ResultValue') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.ResultValue');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'Unit') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.Unit');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'Interpretation') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.Interpretation');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'Remarks') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.Remarks');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'MinimumElapsedHours') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.MinimumElapsedHours');
IF COL_LENGTH(N'dbo.PRM_SampleTests',N'SortOrder') IS NULL INSERT @Missing VALUES (N'PRM_SampleTests.SortOrder');

SELECT STRING_AGG(ObjectName, N', ') FROM @Missing;");

            string missing = missingValue == null || missingValue == DBNull.Value
                ? string.Empty
                : Convert.ToString(missingValue, CultureInfo.InvariantCulture) ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(missing))
            {
                throw new InvalidOperationException(
                    "PRM certificate database schema is incomplete. Missing: " + missing +
                    ". Run System Preflight and explicit Development Database Maintenance before certificate/report issuance.");
            }
        }

        private int GetNextRevisionNo(SqlConnection conn, SqlTransaction tx)
        {
            object result = ExecuteScalarInTransaction(conn, tx,
                "SELECT ISNULL(MAX(RevisionNo), 0) + 1 FROM dbo.PRM_Certificates WITH (UPDLOCK, HOLDLOCK) WHERE SampleID = @SampleID",
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId });
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        private static object ExecuteScalarInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string sql,
            params SqlParameter[] parameters)
        {
            using (SqlCommand command = new SqlCommand(sql, conn, tx))
            {
                command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                if (parameters != null && parameters.Length > 0)
                    command.Parameters.AddRange(parameters);
                return command.ExecuteScalar();
            }
        }

        private static string BuildPrmReportDataHash(
            DataRow sample,
            DataTable results,
            string certificateNumber,
            int revision,
            string reportTitle,
            DateTime issueDate,
            ElectronicSignature signature)
        {
            StringBuilder canonical = new StringBuilder();
            canonical.Append("CertificateNumber=").Append(certificateNumber).Append('\n');
            canonical.Append("Revision=").Append(revision.ToString(CultureInfo.InvariantCulture)).Append('\n');
            canonical.Append("ReportTitle=").Append(reportTitle).Append('\n');
            canonical.Append("IssueDate=").Append(issueDate.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            canonical.Append("SampleID=").Append(S(sample, "SampleID")).Append('\n');
            canonical.Append("SampleNumber=").Append(S(sample, "SampleNumber")).Append('\n');
            canonical.Append("Category=").Append(S(sample, "SampleCategory")).Append('\n');
            canonical.Append("SpecificationNo=").Append(S(sample, "SpecificationNo")).Append('\n');
            canonical.Append("SpecificationVersionNo=").Append(S(sample, "SpecificationVersionNo")).Append('\n');
            canonical.Append("MaterialCode=").Append(S(sample, "MaterialCode")).Append('\n');
            canonical.Append("Manufacturer=").Append(S(sample, "Manufacturer")).Append('\n');
            canonical.Append("Supplier=").Append(S(sample, "Supplier")).Append('\n');
            canonical.Append("GRNNo=").Append(S(sample, "GRNNo")).Append('\n');
            canonical.Append("ProductCode=").Append(S(sample, "ProductCode")).Append('\n');
            canonical.Append("ProductionStage=").Append(S(sample, "ProductionStage")).Append('\n');
            canonical.Append("SampleSource=").Append(S(sample, "SampleSource")).Append('\n');
            canonical.Append("SampledFrom=").Append(S(sample, "SampledFrom")).Append('\n');
            canonical.Append("MachineLineNo=").Append(S(sample, "MachineLineNo")).Append('\n');
            canonical.Append("StorageCondition=").Append(S(sample, "StorageCondition")).Append('\n');
            canonical.Append("StabilityChamberNo=").Append(S(sample, "StabilityChamberNo")).Append('\n');
            canonical.Append("StabilityProtocolNo=").Append(S(sample, "StabilityProtocolNo")).Append('\n');
            canonical.Append("ResultInterpretation=").Append(S(sample, "ResultInterpretation")).Append('\n');
            canonical.Append("ReviewedBy=").Append(S(sample, "ReviewedBy")).Append('\n');
            canonical.Append("ReviewedDate=").Append(S(sample, "ReviewedDate")).Append('\n');
            canonical.Append("ApprovedBy=").Append(S(sample, "ApprovedBy")).Append('\n');
            canonical.Append("ApprovedDate=").Append(S(sample, "ApprovedDate")).Append('\n');

            foreach (DataRow result in results.Select(null, "SampleTestID ASC"))
            {
                canonical.Append("Test=")
                    .Append(S(result, "SampleTestID")).Append('|')
                    .Append(S(result, "TestCode")).Append('|')
                    .Append(S(result, "TestName")).Append('|')
                    .Append(S(result, "SpecificationText")).Append('|')
                    .Append(S(result, "ResultValue")).Append('|')
                    .Append(S(result, "Unit")).Append('|')
                    .Append(S(result, "Interpretation")).Append('|')
                    .Append(S(result, "Remarks")).Append('|')
                    .Append(S(result, "EnteredBy")).Append('|')
                    .Append(S(result, "EnteredDate")).Append('\n');
            }

            canonical.Append("SignatureAction=").Append(signature.Meaning).Append('\n');
            canonical.Append("SignedBy=").Append(signature.SignedBy).Append('\n');
            canonical.Append("SignatureReason=").Append(signature.Reason).Append('\n');

            byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToString());
            return Convert.ToHexString(SHA256.HashData(bytes));
        }

        private static string GetCertificatePrefix(string category)
        {
            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase)) return "COA-RM";
            if (category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase)) return "COA-FP";
            if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase)) return "RPT-ST";
            return "RPT-IP";
        }

        private static string GetReportTitle(string category)
        {
            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase)) return "Raw Material Microbiological Certificate of Analysis";
            if (category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase)) return "Finished Product Microbiological Certificate of Analysis";
            if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase)) return "Stability Microbiological Test Report";
            return "In-Process Microbiological Test Report";
        }

        private static DataRow BuildPrmCertificateSnapshotRow(
            int certificateId,
            string certificateNumber,
            string certificateType,
            string reportTitle,
            DateTime issueDate,
            string issuedBy,
            int revisionNo,
            int reissuedFromCertificateId,
            string cancellationReason,
            string verificationCode,
            string reportHash)
        {
            DataTable table = new DataTable();
            table.Columns.Add("CertificateID", typeof(int));
            table.Columns.Add("CertificateNumber", typeof(string));
            table.Columns.Add("CertificateType", typeof(string));
            table.Columns.Add("ReportTitle", typeof(string));
            table.Columns.Add("IssueDate", typeof(DateTime));
            table.Columns.Add("IssuedBy", typeof(string));
            table.Columns.Add("CertificateStatus", typeof(string));
            table.Columns.Add("RevisionNo", typeof(int));
            table.Columns.Add("ReissuedFromCertificateID", typeof(int));
            table.Columns.Add("CancellationReason", typeof(string));
            table.Columns.Add("VerificationCode", typeof(string));
            table.Columns.Add("ReportHash", typeof(string));

            DataRow row = table.NewRow();
            row["CertificateID"] = certificateId;
            row["CertificateNumber"] = certificateNumber;
            row["CertificateType"] = certificateType;
            row["ReportTitle"] = reportTitle;
            row["IssueDate"] = issueDate;
            row["IssuedBy"] = issuedBy;
            row["CertificateStatus"] = "Active";
            row["RevisionNo"] = revisionNo;
            row["ReissuedFromCertificateID"] = reissuedFromCertificateId > 0 ? reissuedFromCertificateId : DBNull.Value;
            row["CancellationReason"] = string.IsNullOrWhiteSpace(cancellationReason) ? DBNull.Value : cancellationReason;
            row["VerificationCode"] = verificationCode;
            row["ReportHash"] = reportHash;
            table.Rows.Add(row);
            return row;
        }

        private DataTable LoadPrmElectronicSignatureSnapshotInTransaction(
            SqlConnection connection,
            SqlTransaction transaction)
        {
            DataTable snapshot = new DataTable();
            using SqlCommand command = new SqlCommand(@"
SELECT S.SignatureID,S.SampleID,S.ActionType,S.SignedBy,
       COALESCE(NULLIF(LTRIM(RTRIM(U.FullName)),N''),S.SignedBy) AS SignerDisplayName,
       S.MeaningOfSignature,S.ActionReason,S.UserRole,S.SignedAt
FROM dbo.PRM_ElectronicSignatures S WITH(HOLDLOCK)
LEFT JOIN dbo.Users U WITH(HOLDLOCK) ON U.Username=S.SignedBy
WHERE S.SampleID=@SampleID
ORDER BY S.SignatureID;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
            using SqlDataReader reader = command.ExecuteReader();
            snapshot.Load(reader);

            if (snapshot.Rows.Count == 0)
                throw new InvalidOperationException("Certificate / report issuance is blocked because the electronic-signature chain is empty.");

            return snapshot;
        }

        private static string LoadPrmCertificateSnapshotHtml(
            int certificateId,
            int expectedSampleId,
            string expectedCertificateNumber,
            out string message)
        {
            message = "Immutable PRM certificate snapshot verified.";
            if (certificateId <= 0)
            {
                message = "This legacy PRM certificate has no immutable issue snapshot.";
                return string.Empty;
            }

            if (expectedSampleId <= 0 || string.IsNullOrWhiteSpace(expectedCertificateNumber))
                throw new InvalidOperationException("PRM certificate snapshot validation requires the active sample and certificate number.");

            object tableCount = DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1) FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo') AND name=N'PRM_CertificateSnapshots';");
            if (Convert.ToInt32(tableCount, CultureInfo.InvariantCulture) != 1)
            {
                message = "This legacy PRM certificate has no immutable issue snapshot.";
                return string.Empty;
            }

            DataTable snapshot = DatabaseHelper.ExecuteQuery(@"
SELECT TOP(2) SnapshotID,SampleID,CertificateNumber,HtmlContent,SnapshotHash
FROM dbo.PRM_CertificateSnapshots
WHERE CertificateID=@CertificateID
ORDER BY SnapshotID DESC;",
                new[] { new SqlParameter("@CertificateID", SqlDbType.Int) { Value = certificateId } });
            if (snapshot.Rows.Count == 0)
            {
                message = "This legacy PRM certificate has no immutable issue snapshot.";
                return string.Empty;
            }

            if (snapshot.Rows.Count != 1)
                throw new InvalidOperationException("PRM certificate snapshot integrity validation failed because multiple immutable snapshots exist for one certificate.");

            int snapshotSampleId = ToInt(snapshot.Rows[0], "SampleID");
            string snapshotCertificateNumber = S(snapshot.Rows[0], "CertificateNumber");
            if (snapshotSampleId != expectedSampleId ||
                !snapshotCertificateNumber.Equals(expectedCertificateNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("PRM certificate snapshot identity does not match the active certificate. Opening has been blocked.");
            }

            string html = S(snapshot.Rows[0], "HtmlContent");
            string storedHash = S(snapshot.Rows[0], "SnapshotHash");
            string actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
            if (string.IsNullOrWhiteSpace(html) || !actualHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PRM certificate snapshot integrity validation failed. Opening has been blocked.");

            return html;
        }

        private DataRow GetActiveCertificateRow()
        {
            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1 * FROM dbo.PRM_Certificates
WHERE SampleID = @SampleID AND CertificateStatus = N'Active'
ORDER BY ISNULL(RevisionNo,-1) DESC, CertificateID DESC;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });
            return table.Rows.Count == 0 ? null : table.Rows[0];
        }

        private DataRow GetLatestCertificateRow()
        {
            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1 * FROM dbo.PRM_Certificates
WHERE SampleID = @SampleID
ORDER BY ISNULL(RevisionNo,-1) DESC, CertificateID DESC;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });
            return table.Rows.Count == 0 ? null : table.Rows[0];
        }

        private bool HasActiveCertificate()
        {
            return GetActiveCertificateRow() != null;
        }

        private void LoadActiveCertificateNo()
        {
            DataRow cert = GetActiveCertificateRow();
            TxtCertificateNo.Text = cert == null ? string.Empty : S(cert, "CertificateNumber");
        }

        private void UpdateQualityEventSummary()
        {
            if (TxtQualityEventNo == null || TxtInvestigationStatus == null || TxtInvestigationDisposition == null)
                return;

            if (_selectedSampleId <= 0)
            {
                TxtQualityEventNo.Text = "None";
                TxtInvestigationStatus.Text = "Not Required";
                TxtInvestigationDisposition.Text = "—";
                return;
            }

            try
            {
                if (!IsMinimalPrmQualityEventLookupReady())
                {
                    TxtQualityEventNo.Text = "Unavailable";
                    TxtInvestigationStatus.Text = "Schema not ready";
                    TxtInvestigationDisposition.Text = "—";
                    return;
                }

                DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT TOP 1
    QualityEventID,
    EventNumber,
    CurrentStatus,
    FinalDisposition
FROM dbo.QualityEvents
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID
ORDER BY QualityEventID DESC;",
                    new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

                if (table.Rows.Count == 0)
                {
                    string overall = (TxtOverallInterpretation?.Text ?? string.Empty).Trim();
                    TxtQualityEventNo.Text = "None";
                    TxtInvestigationStatus.Text = IsPrmInvestigationRequired(overall)
                        ? "Required - not opened"
                        : "Not Required";
                    TxtInvestigationDisposition.Text = "—";
                    return;
                }

                DataRow row = table.Rows[0];
                string eventNumber = S(row, "EventNumber");
                int eventId = ToInt(row, "QualityEventID");
                TxtQualityEventNo.Text = string.IsNullOrWhiteSpace(eventNumber)
                    ? "PRM-QE-" + eventId.ToString("0000", CultureInfo.InvariantCulture)
                    : eventNumber;

                string eventStatus = S(row, "CurrentStatus");
                TxtInvestigationStatus.Text = string.IsNullOrWhiteSpace(eventStatus) ? "Open" : eventStatus;

                string disposition = S(row, "FinalDisposition");
                TxtInvestigationDisposition.Text = string.IsNullOrWhiteSpace(disposition) ||
                    disposition.Equals("Pending", StringComparison.OrdinalIgnoreCase)
                    ? "Pending"
                    : disposition;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning(
                    "Unable to refresh PRM Quality Event summary for SampleID=" +
                    _selectedSampleId.ToString(CultureInfo.InvariantCulture) + ".",
                    ex);
                TxtQualityEventNo.Text = "Unavailable";
                TxtInvestigationStatus.Text = "Unable to verify";
                TxtInvestigationDisposition.Text = "—";
            }
        }

        private int GetOpenPrmQualityEventId()
        {
            if (!IsMinimalPrmQualityEventLookupReady())
                return 0;

            object result = DatabaseHelper.ExecuteScalar(@"
SELECT TOP 1 QualityEventID
FROM dbo.QualityEvents
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID
  AND ISNULL(CurrentStatus, N'Open') NOT IN (N'Closed', N'QA Closed', N'Cancelled', N'Rejected Closed')
ORDER BY QualityEventID DESC;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }


        private bool HasOpenPrmQualityEvent()
        {
            return GetOpenPrmQualityEventId() > 0;
        }


        private int CreatePrmQualityEvent()
        {
            return CreatePrmQualityEvent(false);
        }

        private int CreatePrmQualityEvent(bool legacyReconciliationMode)
        {
            EnsureCentralQualityEventCompatibility();

            DataRow sample = GetCurrentSampleRow();
            string overall = UpdateOverallInterpretation();
            string eventNumber = string.Empty;
            string actor = GetCurrentUserDisplayName();
            string description = legacyReconciliationMode
                ? "Controlled PRM investigation opened to reconcile historical pre-v183 affected-result evidence without rewriting the legacy record. " +
                  "Sample: " + S(sample, "SampleNumber") +
                  ", Category: " + S(sample, "SampleCategory") +
                  ", Current interpretation: " + overall + "."
                : "PRM microbiology result requires investigation before approval/certificate. " +
                  "Sample: " + S(sample, "SampleNumber") +
                  ", Category: " + S(sample, "SampleCategory") +
                  ", Interpretation: " + overall + ".";

            int qualityEventId = 0;
            string creationStage = "authorization";

            try
            {
                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    creationStage = "authorization";
                    DatabaseHelper.EnsureQualityEventManagementAuthorizationInTransaction(
                        connection, transaction, actor, "open a PRM Quality Event");

                    creationStage = "controlled sample lock";
                    using (SqlCommand applicationLock = new SqlCommand(@"
DECLARE @LockResult int;
EXEC @LockResult=sys.sp_getapplock
    @Resource=@Resource,
    @LockMode=N'Exclusive',
    @LockOwner=N'Transaction',
    @LockTimeout=15000;
SELECT @LockResult;", connection, transaction))
                    {
                        applicationLock.Parameters.Add("@Resource", SqlDbType.NVarChar, 255).Value =
                            "PharmaLIMS:PRM-QualityEvent:" + _selectedSampleId.ToString(CultureInfo.InvariantCulture);
                        if (Convert.ToInt32(applicationLock.ExecuteScalar(), CultureInfo.InvariantCulture) < 0)
                            throw new InvalidOperationException("Unable to obtain the controlled PRM Quality Event lock. Retry the action.");
                    }

                    creationStage = "existing-investigation check";
                    using (SqlCommand duplicateCheck = new SqlCommand(@"
SELECT TOP(1) QualityEventID
FROM dbo.QualityEvents WITH(UPDLOCK,HOLDLOCK)
WHERE SourceModule=N'PRM'
  AND SourceRecordID=@SampleID
  AND ISNULL(CurrentStatus,N'Open') NOT IN(N'Closed',N'QA Closed',N'Cancelled',N'Rejected Closed');", connection, transaction))
                    {
                        duplicateCheck.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                        object existing = duplicateCheck.ExecuteScalar();
                        if (existing != null && existing != DBNull.Value)
                            throw new InvalidOperationException("An open PRM Quality Event already exists for this sample.");
                    }

                    creationStage = "Quality Event number allocation";
                    eventNumber = GeneratePrmQualityEventNumber(connection, transaction);

                    creationStage = "Quality Event record creation";
                    using (SqlCommand create = new SqlCommand(@"
INSERT INTO dbo.QualityEvents
(
    EventNumber,
    EventType,
    Severity,
    SampleID,
    SampleNumber,
    SourceModule,
    SourceRecordID,
    CurrentStatus,
    DetectedBy,
    DetectedDate,
    DetectionSource,
    InitialDescription,
    ImmediateAction,
    CAPARequired,
    CreatedBy,
    CreatedDate
)
OUTPUT inserted.QualityEventID
VALUES
(
    @EventNumber,
    N'PRM Microbiology Result Deviation',
    CASE WHEN @Overall = N'Does Not Conform' THEN N'Major' ELSE N'Minor' END,
    NULL,
    @SampleNumber,
    N'PRM',
    @SourceRecordID,
    N'Open',
    @DetectedBy,
    SYSDATETIME(),
    N'Production / Raw Material Results Entry',
    @Description,
    N'Sample placed under PRM Quality Event investigation workflow.',
    0,
    @CreatedBy,
    SYSDATETIME()
);", connection, transaction))
                    {
                        create.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        create.Parameters.Add("@EventNumber", SqlDbType.NVarChar, 60).Value = eventNumber;
                        create.Parameters.Add("@Overall", SqlDbType.NVarChar, 60).Value = overall;
                        create.Parameters.Add("@SampleNumber", SqlDbType.NVarChar, 80).Value = S(sample, "SampleNumber");
                        create.Parameters.Add("@SourceRecordID", SqlDbType.Int).Value = _selectedSampleId;
                        create.Parameters.Add("@DetectedBy", SqlDbType.NVarChar, 120).Value = actor;
                        create.Parameters.Add("@Description", SqlDbType.NVarChar, 1000).Value = description;
                        create.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 120).Value = actor;
                        qualityEventId = Convert.ToInt32(create.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }

                    creationStage = "affected-result linkage";
                    AddAffectedPrmResultsToQualityEvent(connection, transaction, qualityEventId, legacyReconciliationMode);

                    creationStage = "action-history creation";
                    DatabaseHelper.ExecuteNonQueryWithTransaction(@"
INSERT dbo.QualityEventActions
(
    QualityEventID, ActionType, ActionDescription, PerformedBy, PerformedDate
)
VALUES
(
    @QualityEventID, N'Open PRM Quality Event', @Description, @PerformedBy, SYSDATETIME()
);",
                        new[]
                        {
                            new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                            new SqlParameter("@Description", SqlDbType.NVarChar, -1) { Value = description },
                            new SqlParameter("@PerformedBy", SqlDbType.NVarChar, 100) { Value = actor }
                        }, connection, transaction);

                    creationStage = "audit evidence creation";
                    DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                        "QualityEvents",
                        qualityEventId,
                        "Open PRM Quality Event",
                        "",
                        eventNumber,
                        description,
                        actor,
                        "CurrentStatus",
                        null,
                        eventNumber,
                        "PRM");
                });
            }
            catch (SqlException ex)
            {
                ApplicationLogger.Error(
                    $"PRM Quality Event creation failed during {creationStage}. SampleID={_selectedSampleId}.",
                    ex);
                throw new InvalidOperationException(
                    "PRM Quality Event creation failed during " + creationStage +
                    ". The transaction was rolled back; no partial record was committed.",
                    ex);
            }

            return qualityEventId;
        }


        private void EnsureQualityEventButtonExists()
        {
            try
            {
                Button existingNamedButton = FindName("BtnQualityEvent") as Button;
                if (existingNamedButton != null)
                {
                    existingNamedButton.Visibility = Visibility.Visible;
                    existingNamedButton.Width = 180;
                    existingNamedButton.Height = 46;
                    existingNamedButton.Margin = new Thickness(5);
                    _runtimeQualityEventButton = existingNamedButton;
                    if (_floatingQualityEventButton != null)
                        _floatingQualityEventButton.Visibility = Visibility.Collapsed;
                    UpdateQualityEventButtonState();
                    return;
                }

                Panel targetPanel = WorkflowActionsPanel ?? FindWorkflowPanel();
                if (targetPanel == null)
                    return;

                if (_runtimeQualityEventButton != null && targetPanel.Children.Contains(_runtimeQualityEventButton))
                    return;

                _runtimeQualityEventButton = new Button
                {
                    Content = "Investigation",
                    Width = 180,
                    Height = 46,
                    MinWidth = 180,
                    Margin = new Thickness(5),
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromRgb(126, 58, 242)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Visibility = Visibility.Visible,
                    IsEnabled = true
                };

                _runtimeQualityEventButton.Click += BtnQualityEvent_Click;

                // Put it at the beginning of the workflow row so it cannot be hidden at the far right.
                targetPanel.Children.Insert(0, _runtimeQualityEventButton);

                UpdateQualityEventButtonState();
            }
            catch
            {
                // Runtime button creation must not block opening the PRM results screen.
            }
        }

        private void EnsureFloatingQualityEventButton()
        {
            try
            {
                if (_floatingQualityEventButton != null)
                    return;

                Panel rootPanel = Content as Panel;
                if (rootPanel == null)
                    return;

                _floatingQualityEventButton = new Button
                {
                    Content = "Investigation",
                    Width = 190,
                    Height = 42,
                    MinWidth = 190,
                    Margin = new Thickness(360, 130, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    FontWeight = FontWeights.Bold,
                    Background = new SolidColorBrush(Color.FromRgb(126, 58, 242)),
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Visibility = Visibility.Visible,
                    IsEnabled = true,
                    ToolTip = "Open / continue PRM Quality Event Investigation"
                };

                _floatingQualityEventButton.Click += BtnQualityEvent_Click;
                Panel.SetZIndex(_floatingQualityEventButton, 9999);
                rootPanel.Children.Add(_floatingQualityEventButton);
            }
            catch
            {
                // Floating button is supportive only.
            }
        }

        private Panel FindWorkflowPanel()
        {
            Button startButton = FindName("BtnStartAnalysis") as Button ?? FindButtonByContent("Start Analysis");
            Button submitReviewButton = FindName("BtnSubmitReview") as Button ?? FindButtonByContent("Submit Review");
            Button reviewButton = FindName("BtnReview") as Button ?? FindButtonByContent("Review");
            Button approveButton = FindName("BtnApprove") as Button ?? FindButtonByContent("Approve");
            Button issueButton = FindName("BtnIssueCertificate") as Button ?? FindButtonByContent("Issue Certificate");
            Button saveButton = FindName("BtnSaveResults") as Button ?? FindButtonByContent("Save Results");

            Button reference =
                startButton ??
                saveButton ??
                submitReviewButton ??
                reviewButton ??
                approveButton ??
                issueButton;

            return reference == null ? null : FindParentPanel(reference);
        }

        private Button FindButtonByContent(string contentText)
        {
            return FindButtonByContentRecursive(this, contentText);
        }

        private static Button FindButtonByContentRecursive(DependencyObject parent, string contentText)
        {
            if (parent == null)
                return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);

                if (child is Button button)
                {
                    string content = button.Content == null ? string.Empty : button.Content.ToString();
                    if (content.IndexOf(contentText, StringComparison.OrdinalIgnoreCase) >= 0)
                        return button;
                }

                Button found = FindButtonByContentRecursive(child, contentText);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static Panel FindParentPanel(DependencyObject child)
        {
            DependencyObject current = child;

            while (current != null)
            {
                DependencyObject parent = VisualTreeHelper.GetParent(current);

                if (parent is StackPanel || parent is WrapPanel || parent is UniformGrid)
                    return parent as Panel;

                current = parent;
            }

            return null;
        }

        private void UpdateQualityEventButtonState()
        {
            Button qualityButton = FindName("BtnQualityEvent") as Button ?? _runtimeQualityEventButton ?? _floatingQualityEventButton;
            if (qualityButton == null)
                return;

            try
            {
                string content = "Investigation";
                bool enabled = _selectedSampleId > 0;

                if (_selectedSampleId <= 0)
                {
                    content = "Investigation";
                    enabled = false;
                }
                else
                {
                    // v185: fail closed at the UI before any v184 reconciliation SQL
                    // is evaluated against a database that has not yet received the
                    // controlled 20260827_001/002 maintenance migrations.
                    if (!IsPrmLegacyReconciliationSchemaReady())
                    {
                        if (AppConfig.IsDevelopment && (IsAdminUser() || DatabaseHelper.CanManageSettings(GetCurrentUserDisplayName())))
                        {
                            content = "Update Database";
                            enabled = true;
                        }
                        else
                        {
                            content = "Database Update Required";
                            enabled = false;
                        }
                    }
                    else
                    {
                        int openEventId = GetOpenPrmQualityEventId();
                        if (openEventId > 0)
                        {
                            content = "Continue Investigation";
                            enabled = true;
                        }
                        else
                        {
                            string overall = string.IsNullOrWhiteSpace(TxtOverallInterpretation.Text)
                                ? UpdateOverallInterpretation()
                                : TxtOverallInterpretation.Text.Trim();

                            bool legacyReconciliationRequired = HasUnreconciledLegacyPrmEvidence();
                            int latestEventId = GetLatestPrmQualityEventId();
                            if (legacyReconciliationRequired)
                            {
                                content = "Reconcile Legacy Investigation";
                                enabled = true;
                            }
                            else if (latestEventId > 0 &&
                                (!IsPrmInvestigationRequired(overall) || DoesPrmQualityEventCoverCurrentAffectedResults(latestEventId)))
                            {
                                content = "View Investigation";
                                enabled = true;
                            }
                            else if (IsPrmInvestigationRequired(overall))
                            {
                                content = "Open Investigation";
                                enabled = true;
                            }
                            else
                            {
                                content = "No Investigation Required";
                                enabled = false;
                            }
                        }
                    }
                }

                ApplyQualityEventButtonState(qualityButton, content, enabled);

                if (_runtimeQualityEventButton != null && !ReferenceEquals(_runtimeQualityEventButton, qualityButton))
                    ApplyQualityEventButtonState(_runtimeQualityEventButton, content, enabled);

                if (_floatingQualityEventButton != null && !ReferenceEquals(_floatingQualityEventButton, qualityButton))
                    ApplyQualityEventButtonState(_floatingQualityEventButton, content, enabled);
            }
            catch
            {
                ApplyQualityEventButtonState(qualityButton, "Investigation", _selectedSampleId > 0);

                if (_runtimeQualityEventButton != null)
                    ApplyQualityEventButtonState(_runtimeQualityEventButton, "Investigation", _selectedSampleId > 0);

                if (_floatingQualityEventButton != null)
                    ApplyQualityEventButtonState(_floatingQualityEventButton, "Investigation", _selectedSampleId > 0);
            }
        }


        private static void ApplyQualityEventButtonState(Button button, string content, bool isEnabled)
        {
            if (button == null)
                return;

            button.Content = content;
            button.IsEnabled = isEnabled;
            button.Visibility = Visibility.Visible;
            button.Width = Math.Max(button.Width, 180);
            button.MinWidth = Math.Max(button.MinWidth, 180);
            button.Height = Math.Max(button.Height, 42);
            button.Background = new SolidColorBrush(Color.FromRgb(126, 58, 242));
            button.Foreground = Brushes.White;
            button.FontWeight = FontWeights.Bold;
            button.BorderThickness = new Thickness(0);
            if (content.Equals("Update Database", StringComparison.OrdinalIgnoreCase))
            {
                button.ToolTip = "Close PRM Results and start the centralized signed Development Database Maintenance action.";
                button.Background = new SolidColorBrush(Color.FromRgb(180, 120, 0));
            }
            else if (content.Equals("Database Update Required", StringComparison.OrdinalIgnoreCase))
            {
                button.ToolTip = "The installed database schema is not ready for this PRM investigation workflow.";
            }
            else
            {
                button.ToolTip = "Open / continue the controlled PRM Quality Event investigation.";
            }
        }

        private bool IsPrmInvestigationRequired(string overall)
        {
            return overall.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase) ||
                   overall.Equals("Check Required", StringComparison.OrdinalIgnoreCase) ||
                   overall.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                   overall.Equals("OOT", StringComparison.OrdinalIgnoreCase) ||
                   overall.Equals("FAIL", StringComparison.OrdinalIgnoreCase) ||
                   overall.Equals("Failed", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsPrmLegacyReconciliationSchemaReady()
        {
            if (_selectedSampleId <= 0 || !IsMinimalPrmQualityEventLookupReady())
                return false;

            object ready = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'SampleID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciledBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciledAt') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002',N'TR') IS NOT NULL
THEN 1 ELSE 0 END;");

            return Convert.ToInt32(ready ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        private bool HasUnreconciledLegacyPrmEvidence()
        {
            // Never compile or execute the v184 reconciliation query against a
            // pre-migration database.  SQL Server can bind later statements in an
            // IF/RETURN batch before the guard branch runs, which is why the v184
            // guard still surfaced an Invalid object/column SqlException.
            if (_selectedSampleId <= 0 || !IsPrmLegacyReconciliationSchemaReady())
                return false;

            object value = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.QualityEvents legacyEvent
    WHERE legacyEvent.SourceModule=N'PRM'
      AND legacyEvent.SourceRecordID=@SampleID
      AND legacyEvent.CurrentStatus=N'Closed'
      AND EXISTS
      (
          SELECT 1
          FROM dbo.QualityEventAffectedResults legacyAffected
          WHERE legacyAffected.QualityEventID=legacyEvent.QualityEventID
            AND legacyAffected.SourceModule=N'PRM'
            AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
      )
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.PRM_QualityEventEvidenceReconciliations rec
          INNER JOIN dbo.QualityEvents replacementEvent
              ON replacementEvent.QualityEventID=rec.ReplacementQualityEventID
             AND replacementEvent.SourceModule=N'PRM'
             AND replacementEvent.SourceRecordID=@SampleID
             AND replacementEvent.CurrentStatus=N'Closed'
          INNER JOIN dbo.PRM_ElectronicSignatures signatureEvidence
              ON signatureEvidence.SignatureID=rec.ElectronicSignatureID
             AND signatureEvidence.SampleID=@SampleID
             AND CONVERT(VARBINARY(MAX),ISNULL(signatureEvidence.SignedBy,N''))=
                 CONVERT(VARBINARY(MAX),ISNULL(rec.ReconciledBy,N''))
             AND signatureEvidence.ActionType=N'Quality Event Closure'
          WHERE rec.LegacyQualityEventID=legacyEvent.QualityEventID
            AND rec.SampleID=@SampleID
            AND rec.ReconciliationSchemaVersion=1
            AND rec.ReplacementQualityEventID>rec.LegacyQualityEventID
            AND CONVERT(VARBINARY(MAX),ISNULL(rec.ReconciledBy,N''))=CONVERT(VARBINARY(MAX),ISNULL(replacementEvent.ClosedBy,N''))
            AND rec.ReconciledAt>=replacementEvent.ClosedDate
            AND NOT EXISTS
            (
                SELECT 1
                FROM dbo.QualityEventAffectedResults oldAffected
                WHERE oldAffected.QualityEventID=legacyEvent.QualityEventID
                  AND oldAffected.SourceModule=N'PRM'
                  AND ISNULL(oldAffected.EvidenceSchemaVersion,0)<>1
                  AND
                  (
                      oldAffected.SourceResultID IS NULL
                      OR NOT EXISTS
                      (
                          SELECT 1
                          FROM dbo.QualityEventAffectedResults replacementAffected
                          INNER JOIN dbo.PRM_SampleTests currentResult
                              ON currentResult.SampleTestID=replacementAffected.SourceResultID
                             AND currentResult.SampleID=@SampleID
                          WHERE replacementAffected.QualityEventID=rec.ReplacementQualityEventID
                            AND replacementAffected.SourceModule=N'PRM'
                            AND replacementAffected.SourceResultID=oldAffected.SourceResultID
                            AND ISNULL(replacementAffected.EvidenceSchemaVersion,0)=1
                            AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.TestName,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.TestName,N''))
                            AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.ResultValue,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.ResultValue,N''))
                            AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.SpecificationLimit,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.SpecificationText,N''))
                            AND (replacementAffected.SpecificationNumericLimit=currentResult.SpecificationLimit OR (replacementAffected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
                            AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.Unit,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.Unit,N''))
                            AND CONVERT(VARBINARY(MAX),ISNULL(replacementAffected.FailureType,N''))=CONVERT(VARBINARY(MAX),ISNULL(currentResult.Interpretation,N''))
                      )
                  )
            )
      )
) THEN 1 ELSE 0 END;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

            return Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture) == 1;
        }

        private int GetLatestPrmQualityEventId()
        {
            if (!IsMinimalPrmQualityEventLookupReady())
                return 0;

            object result = DatabaseHelper.ExecuteScalar(@"
SELECT TOP 1 QualityEventID
FROM dbo.QualityEvents
WHERE SourceModule = N'PRM'
  AND SourceRecordID = @SampleID
ORDER BY QualityEventID DESC;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

            return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }

        private bool DoesPrmQualityEventCoverCurrentAffectedResults(int qualityEventId)
        {
            if (qualityEventId <= 0 || _selectedSampleId <= 0)
                return false;

            try
            {
                object ready = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.PRM_SampleTests', N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventAffectedResults', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests', N'SampleTestID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_SampleTests', N'Interpretation') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'QualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SourceResultID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'SpecificationNumericLimit') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults', N'EvidenceSchemaVersion') IS NOT NULL
THEN 1 ELSE 0 END;");

                if (Convert.ToInt32(ready ?? 0, CultureInfo.InvariantCulture) != 1)
                    return false;

                object missing = DatabaseHelper.ExecuteScalar(@"
SELECT
    (
        SELECT COUNT(1)
        FROM dbo.PRM_SampleTests st
        WHERE st.SampleID = @SampleID
          AND st.Interpretation IN (N'Does Not Conform', N'Check Required')
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.QualityEventAffectedResults affected
              WHERE affected.QualityEventID = @QualityEventID
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
    +
    CASE WHEN EXISTS
    (
        SELECT 1
        FROM dbo.QualityEventAffectedResults affected
        LEFT JOIN dbo.PRM_SampleTests currentResult
          ON currentResult.SampleTestID = affected.SourceResultID
         AND currentResult.SampleID = @SampleID
        WHERE affected.QualityEventID = @QualityEventID
          AND affected.SourceModule = N'PRM'
          AND
          (
              currentResult.SampleTestID IS NULL
              OR ISNULL(affected.EvidenceSchemaVersion, 0) <> 1
              OR CONVERT(VARBINARY(MAX), ISNULL(affected.TestName, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.TestName, N''))
              OR CONVERT(VARBINARY(MAX), ISNULL(affected.ResultValue, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.ResultValue, N''))
              OR CONVERT(VARBINARY(MAX), ISNULL(affected.SpecificationLimit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.SpecificationText, N''))
              OR NOT (affected.SpecificationNumericLimit = currentResult.SpecificationLimit OR (affected.SpecificationNumericLimit IS NULL AND currentResult.SpecificationLimit IS NULL))
              OR CONVERT(VARBINARY(MAX), ISNULL(affected.Unit, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.Unit, N''))
              OR CONVERT(VARBINARY(MAX), ISNULL(affected.FailureType, N'')) <> CONVERT(VARBINARY(MAX), ISNULL(currentResult.Interpretation, N''))
          )
    ) THEN 1 ELSE 0 END;",
                    new[]
                    {
                        new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId },
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId }
                    });

                return Convert.ToInt32(missing ?? 0, CultureInfo.InvariantCulture) == 0;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning(
                    "Unable to verify affected-result coverage for PRM QualityEventID=" +
                    qualityEventId.ToString(CultureInfo.InvariantCulture) + ".",
                    ex);
                return false;
            }
        }

        private string GetPrmQualityEventNumber(int qualityEventId)
        {
            object result = DatabaseHelper.ExecuteScalar(@"
SELECT EventNumber
FROM dbo.QualityEvents
WHERE QualityEventID = @QualityEventID;",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } });

            string value = result == null || result == DBNull.Value ? string.Empty : Convert.ToString(result, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(value) ? "PRM-QE-" + qualityEventId.ToString("0000", CultureInfo.InvariantCulture) : value;
        }

        private string GeneratePrmQualityEventNumber(SqlConnection connection, SqlTransaction transaction)
        {
            using SqlCommand yearCommand = new SqlCommand("SELECT DATEPART(YEAR, SYSDATETIME());", connection, transaction);
            int year = Convert.ToInt32(yearCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            using SqlCommand command = new SqlCommand(@"
DECLARE @Prefix NVARCHAR(20)=N'PRM-QE';
DECLARE @Stem NVARCHAR(40)=@Prefix+N'-'+CONVERT(NVARCHAR(4),@Year)+N'-';
DECLARE @ExistingEventMax INT=0;

/*
    Legacy PRM Quality Events were originally numbered from MAX(EventNumber) and
    therefore may pre-date the PRM_NumberSequences row. Reconcile the controlled
    sequence with the authoritative event records inside the same transaction
    before allocating the next number. This prevents duplicate EventNumber values
    such as PRM-QE-2026-0001 after an upgrade.
*/
SELECT @ExistingEventMax=ISNULL(MAX(TRY_CONVERT(INT, SUBSTRING(EventNumber,LEN(@Stem)+1,20))),0)
FROM dbo.QualityEvents WITH (UPDLOCK,HOLDLOCK)
WHERE EventNumber LIKE @Stem+N'%'
  AND TRY_CONVERT(INT, SUBSTRING(EventNumber,LEN(@Stem)+1,20)) IS NOT NULL;

MERGE dbo.PRM_NumberSequences WITH (HOLDLOCK) AS target
USING (SELECT N'PRM_QE' AS SequenceName) AS source
ON target.SequenceName = source.SequenceName
WHEN MATCHED THEN
    UPDATE SET
        Prefix = @Prefix,
        LastNumber = CASE
            WHEN target.CurrentYear = @Year
                THEN CASE
                    WHEN ISNULL(target.LastNumber,0) > @ExistingEventMax
                        THEN target.LastNumber + 1
                    ELSE @ExistingEventMax + 1
                END
            ELSE @ExistingEventMax + 1
        END,
        CurrentYear = @Year,
        LastUpdated = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (SequenceName, Prefix, CurrentYear, LastNumber, LastUpdated)
    VALUES (N'PRM_QE', @Prefix, @Year, @ExistingEventMax + 1, SYSDATETIME())
OUTPUT inserted.Prefix + N'-' + CONVERT(NVARCHAR(4), inserted.CurrentYear) + N'-'
       + RIGHT(N'0000' + CONVERT(NVARCHAR(20), inserted.LastNumber),
               CASE WHEN LEN(CONVERT(NVARCHAR(20), inserted.LastNumber)) > 4
                    THEN LEN(CONVERT(NVARCHAR(20), inserted.LastNumber)) ELSE 4 END);",
                connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@Year", SqlDbType.Int).Value = year;
            object result = command.ExecuteScalar();

            string number = result == null || result == DBNull.Value ? string.Empty : Convert.ToString(result, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(number))
                throw new InvalidOperationException("A PRM Quality Event number could not be generated.");
            return number;
        }

        private static async Task EnsurePrmQualityEventSchemaReadyForActionAsync()
        {
            PrmSchemaReadinessResult readiness =
                await PrmSchemaReadinessService.EnsureQualityEventReadyAsync();

            if (!readiness.IsReady)
                throw new InvalidOperationException(readiness.Message);
        }

        private void EnsureCentralQualityEventCompatibility()
        {
            DataTable readiness = DatabaseHelper.ExecuteQuery(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventActions',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.QualityEventChecklistAnswers',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.PRM_QualityEventEvidenceReconciliations',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.TR_PRM_QEEvidenceReconciliation_Immutable_20260827_002',N'TR') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_NumberSequences',N'LastUpdated') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'SourceRecordID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'CurrentStatus') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'InitialDescription') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'RootCauseDetails') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'ImpactAssessment') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'QAConclusion') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEvents',N'FinalDisposition') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceModule') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SourceResultID') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'SpecificationNumericLimit') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventAffectedResults',N'EvidenceSchemaVersion') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'LegacyQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReplacementQualityEventID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'SampleID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ElectronicSignatureID') IS NOT NULL
    AND COL_LENGTH(N'dbo.PRM_QualityEventEvidenceReconciliations',N'ReconciliationSchemaVersion') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'ActionType') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'ActionDescription') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'PerformedBy') IS NOT NULL
    AND COL_LENGTH(N'dbo.QualityEventActions',N'PerformedDate') IS NOT NULL
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEvents')
          AND name=N'SampleID' AND system_type_id=TYPE_ID(N'int') AND is_nullable=1
    )
    AND EXISTS
    (
        SELECT 1 FROM sys.columns
        WHERE object_id=OBJECT_ID(N'dbo.QualityEventAffectedResults')
          AND name=N'SampleTestID' AND system_type_id=TYPE_ID(N'int') AND is_nullable=1
    )
THEN 1 ELSE 0 END;", null, commandTimeoutSeconds: 5);

            object ready = readiness.Rows.Count == 0 || readiness.Columns.Count == 0
                ? 0
                : readiness.Rows[0][0];

            if (Convert.ToInt32(ready, CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException(
                    "The PRM Quality Event schema is incomplete. Investigation is blocked until the controlled Development database-maintenance step is completed; no workflow button or application startup will modify the schema automatically.");
            }
        }

        private void AddAffectedPrmResultsToQualityEvent(
            SqlConnection connection,
            SqlTransaction transaction,
            int qualityEventId,
            bool includeLegacyReconciliationEvidence)
        {
            using SqlCommand query = new SqlCommand(@"
SELECT
    SampleTestID,
    TestName,
    ResultValue,
    SpecificationText,
    SpecificationLimit,
    Unit,
    Interpretation
FROM dbo.PRM_SampleTests WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID
  AND
  (
      Interpretation IN (N'Does Not Conform', N'Check Required')
      OR
      (
          @IncludeLegacy = 1
          AND SampleTestID IN
          (
              SELECT legacyAffected.SourceResultID
              FROM dbo.QualityEvents legacyEvent WITH (UPDLOCK, HOLDLOCK)
              INNER JOIN dbo.QualityEventAffectedResults legacyAffected WITH (UPDLOCK, HOLDLOCK)
                  ON legacyAffected.QualityEventID=legacyEvent.QualityEventID
                 AND legacyAffected.SourceModule=N'PRM'
              WHERE legacyEvent.SourceModule=N'PRM'
                AND legacyEvent.SourceRecordID=@SampleID
                AND legacyEvent.CurrentStatus=N'Closed'
                AND ISNULL(legacyAffected.EvidenceSchemaVersion,0)<>1
                AND legacyAffected.SourceResultID IS NOT NULL
          )
      )
  )
ORDER BY ISNULL(SortOrder, SampleTestID), SampleTestID;", connection, transaction);
            query.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            query.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
            query.Parameters.Add("@IncludeLegacy", SqlDbType.Bit).Value = includeLegacyReconciliationEvidence;

            var affected = new List<(int SampleTestID, string TestName, string ResultValue, string Specification, decimal? SpecificationNumericLimit, string Unit, string FailureType)>();
            using (SqlDataReader reader = query.ExecuteReader())
            {
                while (reader.Read())
                {
                    affected.Add((
                        Convert.ToInt32(reader["SampleTestID"], CultureInfo.InvariantCulture),
                        Convert.ToString(reader["TestName"], CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(reader["ResultValue"], CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(reader["SpecificationText"], CultureInfo.InvariantCulture) ?? string.Empty,
                        reader["SpecificationLimit"] == DBNull.Value
                            ? (decimal?)null
                            : Convert.ToDecimal(reader["SpecificationLimit"], CultureInfo.InvariantCulture),
                        Convert.ToString(reader["Unit"], CultureInfo.InvariantCulture) ?? string.Empty,
                        Convert.ToString(reader["Interpretation"], CultureInfo.InvariantCulture) ?? string.Empty));
                }
            }

            if (affected.Count == 0)
            {
                throw new InvalidOperationException(
                    includeLegacyReconciliationEvidence
                        ? "A legacy reconciliation investigation cannot be opened because no current PRM result can be traced to the historical evidence. Database/QA remediation is required; legacy history was not rewritten."
                        : "A PRM Quality Event cannot be opened without at least one traceable nonconforming or check-required result.");
            }

            foreach (var row in affected)
            {
                using SqlCommand insert = new SqlCommand(@"
IF NOT EXISTS
(
    SELECT 1
    FROM dbo.QualityEventAffectedResults
    WHERE QualityEventID = @QualityEventID
      AND SourceModule = N'PRM'
      AND SourceResultID = @SourceResultID
)
BEGIN
    INSERT INTO dbo.QualityEventAffectedResults
    (
        QualityEventID,
        SampleTestID,
        SourceModule,
        SourceResultID,
        TestID,
        TestName,
        ResultValue,
        SpecificationLimit,
        SpecificationNumericLimit,
        Unit,
        FailureType,
        EvidenceSchemaVersion,
        CreatedDate
    )
    VALUES
    (
        @QualityEventID,
        NULL,
        N'PRM',
        @SourceResultID,
        NULL,
        @TestName,
        @ResultValue,
        @SpecificationLimit,
        @SpecificationNumericLimit,
        @Unit,
        @FailureType,
        1,
        SYSDATETIME()
    );
END;", connection, transaction);
                insert.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                insert.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;
                insert.Parameters.Add("@SourceResultID", SqlDbType.Int).Value = row.SampleTestID;
                insert.Parameters.Add("@TestName", SqlDbType.NVarChar, 200).Value = row.TestName;
                insert.Parameters.Add("@ResultValue", SqlDbType.NVarChar, 200).Value = row.ResultValue;
                insert.Parameters.Add("@SpecificationLimit", SqlDbType.NVarChar, 500).Value = row.Specification;
                SqlParameter numericLimit = insert.Parameters.Add("@SpecificationNumericLimit", SqlDbType.Decimal);
                numericLimit.Precision = 18;
                numericLimit.Scale = 3;
                numericLimit.Value = row.SpecificationNumericLimit.HasValue
                    ? row.SpecificationNumericLimit.Value
                    : (object)DBNull.Value;
                insert.Parameters.Add("@Unit", SqlDbType.NVarChar, 50).Value = row.Unit;
                insert.Parameters.Add("@FailureType", SqlDbType.NVarChar, 120).Value = row.FailureType;
                if (insert.ExecuteNonQuery() < 1)
                    throw new InvalidOperationException("The affected PRM result could not be linked to the Quality Event.");
            }
        }

        private DataRow GetCurrentSampleRow()
        {
            RequireSample();
            DataTable table = DatabaseHelper.ExecuteQuery(
                @"SELECT S.*,COALESCE(NULLIF(LTRIM(RTRIM(U.FullName)),N''),S.SampledBy) AS SampledByDisplay
FROM dbo.PRM_Samples S
LEFT JOIN dbo.Users U ON U.Username=S.SampledBy
WHERE S.SampleID=@SampleID;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });
            if (table.Rows.Count == 0)
                throw new InvalidOperationException("Selected sample was not found.");
            return table.Rows[0];
        }

        private DataTable GetResultsTable()
        {
            return DatabaseHelper.ExecuteQuery(@"
SELECT * FROM dbo.PRM_SampleTests
WHERE SampleID = @SampleID
ORDER BY ISNULL(SortOrder, SampleTestID), SampleTestID;",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });
        }

        private string GetCurrentSampleStatus()
        {
            return S(GetCurrentSampleRow(), "SampleStatus");
        }

        private void RefreshSelectedSample()
        {
            int id = _selectedSampleId;
            LoadSamples();
            LoadSelectedSample(id);
        }

        private void RefreshSelectedSampleLight()
        {
            int id = _selectedSampleId;
            if (id <= 0)
                return;

            LoadSelectedSample(id);
        }

        private void RequireSample()
        {
            if (_selectedSampleId <= 0)
                throw new InvalidOperationException("Select a sample first.");
        }

        private static bool IsOneOf(string value, params string[] values)
        {
            foreach (string v in values)
            {
                if (string.Equals(value, v, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int ToInt(DataRowView row, string column)
        {
            if (row == null || !row.Row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return 0;
            return Convert.ToInt32(row[column], CultureInfo.InvariantCulture);
        }

        private static int ToInt(DataRow row, string column)
        {
            if (row == null || !row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return 0;
            return Convert.ToInt32(row[column], CultureInfo.InvariantCulture);
        }

        private static string S(DataRow row, string column)
        {
            if (row == null || !row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return string.Empty;
            return Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static void ShowOperationError(string operation, Exception exception)
        {
            MessageBox.Show(
                Infrastructure.UserFacingError.SafeMessage(exception, operation),
                operation,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
