using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;

#nullable disable

namespace PharmaLIMS
{
    // Certificate issue/snapshot/lifecycle operations plus shared safe-conversion/status helpers.
    public static partial class DatabaseHelper
    {
        public static bool HasCertificate(int sampleId)
        {
            if (!TableExists("Certificates"))
                return false;

            string query;

            if (ColumnExists("Certificates", "IsCancelled"))
            {
                query = @"
                    SELECT COUNT(*)
                    FROM Certificates
                    WHERE SampleID = @sampleId
                      AND ISNULL(IsCancelled, 0) = 0
                      AND ISNULL(CertificateStatus, ISNULL(Status, 'Active')) IN ('Active', 'Issued')";
            }
            else
            {
                query = "SELECT COUNT(*) FROM Certificates WHERE SampleID = @sampleId";
            }

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId)
            };

            object result = ExecuteScalar(query, pars);
            return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
        }

        public static DataTable GetLatestCertificateInfo(int sampleId)
        {
            if (!TableExists("Certificates"))
                return new DataTable();

            string query;

            if (ColumnExists("Certificates", "IsCancelled"))
            {
                query = @"
                    SELECT TOP 1
                        CertificateID,
                        CertificateNumber,
                        SampleID,
                        SampleNumber,
                        IssueDate,
                        IssuedBy,
                        Status,
                        RevisionNo,
                        IsCancelled,
                        CancelledBy,
                        CancelledDate,
                        CancellationReason,
                        ReissuedFromCertificateID,
                        VerificationCode,
                        ReportHash,
                        CertificateStatus
                    FROM Certificates
                    WHERE SampleID = @sampleId
                      AND ISNULL(IsCancelled, 0) = 0
                    ORDER BY CertificateID DESC";
            }
            else
            {
                query = @"
                    SELECT TOP 1
                        CertificateID,
                        CertificateNumber,
                        SampleID,
                        SampleNumber,
                        IssueDate,
                        IssuedBy,
                        Status
                    FROM Certificates
                    WHERE SampleID = @sampleId
                    ORDER BY CertificateID DESC";
            }

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId)
            };

            return ExecuteQuery(query, pars);
        }

        public static DataTable GetIssuedCertificateSnapshotHeader(int sampleId)
        {
            const string query = @"
;WITH LatestSnapshot AS
(
    SELECT TOP(1)
        snap.SnapshotContent,
        certificate.CertificateNumber,
        certificate.IssueDate,
        certificate.IssuedBy,
        certificate.VerificationCode,
        certificate.RevisionNo,
        certificate.CertificateStatus,
        certificate.ReportHash
    FROM dbo.CertificateDocumentSnapshots snap
    INNER JOIN dbo.Certificates certificate ON certificate.CertificateID=snap.CertificateID
    WHERE snap.SampleID=@SampleID
      AND ISNULL(certificate.IsCancelled,0)=0
      AND UPPER(ISNULL(certificate.CertificateStatus,certificate.Status)) IN(N'ACTIVE',N'ISSUED')
    ORDER BY certificate.CertificateID DESC
)
SELECT
    item.SampleNumber,
    item.SampleType,
    item.SamplingDateTime,
    item.CreatedDate,
    item.ReceivedDateTime,
    item.AnalysisStartedDateTime,
    item.AnalysisCompletedDateTime,
    item.SampledBy,
    item.Status,
    item.SamplingPointCode AS PointCode,
    item.SamplingPoint AS Location,
    snapshot.CertificateNumber,
    snapshot.IssuedBy AS CertificateIssuedBy,
    snapshot.IssueDate AS CertificateIssueDate,
    snapshot.VerificationCode,
    snapshot.RevisionNo,
    snapshot.CertificateStatus,
    snapshot.ReportHash,
    item.AnalystName,
    item.AnalystSignedAt,
    item.AnalystMeaning,
    item.AnalystRole,
    item.ReviewedByName,
    item.ReviewedBySignedAt,
    item.ReviewedByMeaning,
    item.ReviewedByRole,
    item.ApprovedByName,
    item.ApprovedBySignedAt,
    item.ApprovedByMeaning,
    item.ApprovedByRole,
    item.IssuedByName,
    item.IssuedBySignedAt,
    item.IssuedByMeaning,
    item.IssuedByRole
FROM LatestSnapshot snapshot
CROSS APPLY OPENJSON
(
    CASE WHEN ISJSON(snapshot.SnapshotContent)=1
              AND LEFT(LTRIM(snapshot.SnapshotContent),1)=N'{'
         THEN snapshot.SnapshotContent ELSE N'{}' END
)
WITH
(
    SampleNumber NVARCHAR(100) N'$.SampleNumber',
    SampleType NVARCHAR(100) N'$.SampleType',
    SamplingDateTime DATETIME2 N'$.SamplingDateTime',
    CreatedDate DATETIME2 N'$.CreatedDate',
    ReceivedDateTime DATETIME2 N'$.ReceivedDateTime',
    AnalysisStartedDateTime DATETIME2 N'$.AnalysisStartedDateTime',
    AnalysisCompletedDateTime DATETIME2 N'$.AnalysisCompletedDateTime',
    SampledBy NVARCHAR(100) N'$.SampledBy',
    Status NVARCHAR(50) N'$.Status',
    SamplingPointCode NVARCHAR(100) N'$.SamplingPointCode',
    SamplingPoint NVARCHAR(300) N'$.SamplingPoint',
    AnalystName NVARCHAR(100) N'$.AnalystName',
    AnalystSignedAt DATETIME2 N'$.AnalystSignedAt',
    AnalystMeaning NVARCHAR(255) N'$.AnalystMeaning',
    AnalystRole NVARCHAR(100) N'$.AnalystRole',
    ReviewedByName NVARCHAR(100) N'$.ReviewedByName',
    ReviewedBySignedAt DATETIME2 N'$.ReviewedBySignedAt',
    ReviewedByMeaning NVARCHAR(255) N'$.ReviewedByMeaning',
    ReviewedByRole NVARCHAR(100) N'$.ReviewedByRole',
    ApprovedByName NVARCHAR(100) N'$.ApprovedByName',
    ApprovedBySignedAt DATETIME2 N'$.ApprovedBySignedAt',
    ApprovedByMeaning NVARCHAR(255) N'$.ApprovedByMeaning',
    ApprovedByRole NVARCHAR(100) N'$.ApprovedByRole',
    IssuedByName NVARCHAR(100) N'$.IssuedByName',
    IssuedBySignedAt DATETIME2 N'$.IssuedBySignedAt',
    IssuedByMeaning NVARCHAR(255) N'$.IssuedByMeaning',
    IssuedByRole NVARCHAR(100) N'$.IssuedByRole'
) item;";
            return ExecuteQuery(query, new[]
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId }
            });
        }

        public static DataTable GetIssuedCertificateSnapshotTests(int sampleId)
        {
            const string query = @"
;WITH LatestSnapshot AS
(
    SELECT TOP(1) snap.SnapshotContent
    FROM dbo.CertificateDocumentSnapshots snap
    INNER JOIN dbo.Certificates certificate ON certificate.CertificateID=snap.CertificateID
    WHERE snap.SampleID=@SampleID
      AND ISNULL(certificate.IsCancelled,0)=0
      AND UPPER(ISNULL(certificate.CertificateStatus,certificate.Status)) IN(N'ACTIVE',N'ISSUED')
    ORDER BY certificate.CertificateID DESC
)
SELECT
    item.TestName,
    item.TestCategory,
    item.Unit,
    item.AlertLimit,
    item.ActionLimit,
    item.ResultValue,
    item.ResultStatus,
    item.LimitDescription
FROM LatestSnapshot snapshot
CROSS APPLY OPENJSON
(
    CASE WHEN ISJSON(snapshot.SnapshotContent)=1
              AND LEFT(LTRIM(snapshot.SnapshotContent),1)=N'{'
         THEN snapshot.SnapshotContent ELSE N'{}' END,
    N'$.Tests'
)
WITH
(
    TestName NVARCHAR(300) N'$.TestName',
    TestCategory NVARCHAR(100) N'$.TestCategory',
    Unit NVARCHAR(100) N'$.Unit',
    AlertLimit DECIMAL(18,6) N'$.AlertLimit',
    ActionLimit DECIMAL(18,6) N'$.ActionLimit',
    ResultValue NVARCHAR(MAX) N'$.ResultValue',
    ResultStatus NVARCHAR(50) N'$.ResultStatus',
    LimitDescription NVARCHAR(MAX) N'$.LimitDescription'
) item;";
            return ExecuteQuery(query, new[]
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId }
            });
        }

        public static bool HasIssuedCertificateSnapshot(int sampleId)
        {
            object result = ExecuteScalar(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.CertificateDocumentSnapshots snap
    INNER JOIN dbo.Certificates certificate ON certificate.CertificateID=snap.CertificateID
    WHERE snap.SampleID=@SampleID
      AND ISNULL(certificate.IsCancelled,0)=0
      AND UPPER(ISNULL(certificate.CertificateStatus,certificate.Status)) IN(N'ACTIVE',N'ISSUED')
) THEN 1 ELSE 0 END;", new[]
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId }
            });
            return result != null && result != DBNull.Value && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
        }

        public static bool ValidateIssuedCertificateSnapshot(int sampleId, out string message)
        {
            message = string.Empty;
            DataTable snapshot = ExecuteQuery(@"
SELECT TOP(1) snap.SnapshotContent,snap.SnapshotHash,certificate.ReportHash
FROM dbo.CertificateDocumentSnapshots snap
INNER JOIN dbo.Certificates certificate ON certificate.CertificateID=snap.CertificateID
WHERE snap.SampleID=@SampleID
  AND ISNULL(certificate.IsCancelled,0)=0
  AND UPPER(ISNULL(certificate.CertificateStatus,certificate.Status)) IN(N'ACTIVE',N'ISSUED')
ORDER BY certificate.CertificateID DESC;", new[]
            {
                new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId }
            });
            if (snapshot.Rows.Count == 0)
            {
                message = "The immutable issued-document snapshot is missing.";
                return false;
            }

            string content = snapshot.Rows[0].GetSafeString("SnapshotContent");
            string storedSnapshotHash = snapshot.Rows[0].GetSafeString("SnapshotHash");
            string certificateHash = snapshot.Rows[0].GetSafeString("ReportHash");
            if (!ValidateCertificateSnapshotJson(content, out string jsonValidationMessage))
            {
                message = jsonValidationMessage;
                return false;
            }

            using SHA256 sha = SHA256.Create();
            string calculatedHash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", string.Empty);
            if (!calculatedHash.Equals(storedSnapshotHash, StringComparison.OrdinalIgnoreCase) ||
                !calculatedHash.Equals(certificateHash, StringComparison.OrdinalIgnoreCase))
            {
                message = "The issued certificate snapshot hash does not match the stored document hash.";
                return false;
            }
            return true;
        }

        private static bool ValidateCertificateSnapshotJson(string content, out string message)
        {
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(content))
            {
                message = "The immutable issued-certificate snapshot is empty.";
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(content);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    message = "The immutable issued-certificate snapshot is not a JSON object.";
                    return false;
                }

                if (!root.TryGetProperty("SampleID", out JsonElement sampleIdElement) ||
                    sampleIdElement.ValueKind == JsonValueKind.Null ||
                    !root.TryGetProperty("SampleNumber", out JsonElement sampleNumberElement) ||
                    sampleNumberElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(sampleNumberElement.GetString()) ||
                    !root.TryGetProperty("Tests", out JsonElement testsElement) ||
                    testsElement.ValueKind != JsonValueKind.Array)
                {
                    message = "The immutable issued-certificate snapshot is structurally incomplete.";
                    return false;
                }

                return true;
            }
            catch (JsonException)
            {
                message = "The immutable issued-certificate snapshot contains malformed JSON. The historical document cannot be reconstructed from current master data; controlled cancellation/reissue or documented reconciliation is required.";
                return false;
            }
        }

        private static string BuildReportHash(string certificateNumber, int sampleId, string sampleNumber, string issuedBy)
        {
            string raw =
                (certificateNumber ?? "") + "|" +
                sampleId.ToString() + "|" +
                (sampleNumber ?? "") + "|" +
                GetAuthoritativeDatabaseTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "|" +
                (issuedBy ?? "");

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder builder = new StringBuilder(hash.Length * 2);

                foreach (byte b in hash)
                    builder.Append(b.ToString("X2"));

                return builder.ToString();
            }
        }

        private static string GetNextCertificateNumber(SqlConnection connection, SqlTransaction transaction)
        {
            DateTime databaseNow = GetAuthoritativeDatabaseTime(connection, transaction);
            string dateKey = databaseNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            int yearNo = databaseNow.Year;
            const string sequenceKey = "COA-GLOBAL";

            using SqlCommand command = new SqlCommand(@"
DECLARE @StoredLast INT = 0;
DECLARE @ExistingLast INT = 0;
DECLARE @NextNumber INT;

SELECT @StoredLast = ISNULL(MAX(LastNumber), 0)
FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
WHERE SequenceKey = @SequenceKey
   OR (NumberKey = N'COA' AND YearNo = @YearNo);

SELECT @ExistingLast = ISNULL(MAX(TRY_CONVERT(INT,
    CASE WHEN CHARINDEX(N'-', REVERSE(CertificateNumber)) > 0
         THEN RIGHT(CertificateNumber, CHARINDEX(N'-', REVERSE(CertificateNumber)) - 1)
         ELSE NULL END)), 0)
FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK);

SET @NextNumber = CASE WHEN @StoredLast > @ExistingLast THEN @StoredLast ELSE @ExistingLast END + 1;

IF EXISTS
(
    SELECT 1 FROM dbo.LIMS_NumberSequences WITH (UPDLOCK, HOLDLOCK)
    WHERE SequenceKey = @SequenceKey
       OR (NumberKey = N'COA' AND YearNo = @YearNo)
)
BEGIN
    UPDATE dbo.LIMS_NumberSequences
    SET LastNumber = @NextNumber, UpdatedAt = SYSDATETIME(), SequenceKey = @SequenceKey
    WHERE SequenceKey = @SequenceKey
       OR (NumberKey = N'COA' AND YearNo = @YearNo);
END
ELSE
BEGIN
    INSERT dbo.LIMS_NumberSequences(NumberKey,YearNo,LastNumber,Prefix,CreatedAt,UpdatedAt,SequenceKey)
    VALUES(N'COA',@YearNo,@NextNumber,N'COA',SYSDATETIME(),SYSDATETIME(),@SequenceKey);
END;
SELECT @NextNumber;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SequenceKey", SqlDbType.NVarChar, 100).Value = sequenceKey;
            command.Parameters.Add("@YearNo", SqlDbType.Int).Value = yearNo;
            int next = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            return "COA-" + dateKey + "-" + next.ToString("0000", CultureInfo.InvariantCulture);
        }

        private static void ValidateLegacyCompletedResultStatusGapsInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string effectiveUser,
            int expectedGapCount)
        {
            var unresolvedNonConformingTests = new List<string>();
            int observedGapCount = 0;

            using (SqlCommand command = new SqlCommand(@"
SELECT
    st.SampleTestID,
    COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'') AS TestName,
    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.UnitSnapshot,N'') ELSE ISNULL(t.Unit,N'') END AS Unit,
    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
    st.ResultValue
FROM dbo.SampleTests st WITH (UPDLOCK,HOLDLOCK)
LEFT JOIN dbo.Tests t WITH (HOLDLOCK) ON t.TestID=st.TestID
WHERE st.SampleID=@SampleID
  AND NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(max),st.ResultValue))),N'') IS NOT NULL
  AND NULLIF(LTRIM(RTRIM(ISNULL(st.ResultStatus,N''))),N'') IS NULL
ORDER BY st.SampleTestID;", connection, transaction))
            {
                command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                using SqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    observedGapCount++;
                    string testName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                    string unit = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                    decimal? alertLimit = reader.IsDBNull(3)
                        ? (decimal?)null
                        : Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
                    decimal? actionLimit = reader.IsDBNull(4)
                        ? (decimal?)null
                        : Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture);
                    object resultValue = reader.IsDBNull(5) ? null : reader.GetValue(5);

                    string derivedStatus = AssessLegacyCompletedWaterResultStatus(
                        testName, unit, resultValue, alertLimit, actionLimit);
                    if (derivedStatus.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                        derivedStatus.Equals("INVALID", StringComparison.OrdinalIgnoreCase))
                    {
                        unresolvedNonConformingTests.Add(
                            string.IsNullOrWhiteSpace(testName) ? "SampleTestID " + reader.GetInt32(0).ToString(CultureInfo.InvariantCulture) : testName);
                    }
                }
            }

            if (observedGapCount != expectedGapCount)
                throw new DBConcurrencyException("Water result-status evidence changed during certificate issuance. Reload the sample and try again.");

            if (unresolvedNonConformingTests.Count > 0)
            {
                string joined = string.Join(", ", unresolvedNonConformingTests.GetRange(0, Math.Min(5, unresolvedNonConformingTests.Count)));
                throw new InvalidOperationException(
                    "Certificate issuance found completed legacy result(s) with missing ResultStatus that cannot be safely treated as conforming: " +
                    joined + ". Use the controlled result-correction / investigation workflow and obtain QA re-approval before issuance.");
            }

            AddAuditTrailAdvanced(
                connection,
                transaction,
                "Samples",
                sampleId,
                "Legacy ResultStatus Compatibility Check",
                expectedGapCount.ToString(CultureInfo.InvariantCulture) + " completed result(s) had blank ResultStatus",
                "Stored results/specifications re-evaluated at certificate issuance; no approved source result was modified",
                "Controlled compatibility validation before immutable certificate snapshot creation",
                effectiveUser,
                "ResultStatus",
                null,
                null,
                "Water");
        }

        private static string AssessLegacyCompletedWaterResultStatus(
            string testName,
            string unit,
            object resultValue,
            decimal? alertLimit,
            decimal? actionLimit)
        {
            string raw = Convert.ToString(resultValue, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            if (raw.Length == 0)
                return "PENDING";

            string normalizedName = (testName ?? string.Empty).Trim().ToLowerInvariant();
            string normalizedUnit = (unit ?? string.Empty).Trim().ToLowerInvariant();
            bool isAppearance = normalizedName.Contains("appearance") || normalizedName.Contains("color") ||
                                normalizedName.Contains("colour") || normalizedName.Contains("clarity");
            bool isResidualChlorine = normalizedName.Contains("residual chlorine") ||
                                      normalizedName.Contains("free chlorine") || normalizedName == "chlorine";
            bool isPh = normalizedName == "ph" || normalizedName.Contains("ph value") ||
                        normalizedName.Contains("ph test") || normalizedName.Contains("ph ");
            bool isAbsencePresence = normalizedUnit == "absence" ||
                                     normalizedName.Contains("e. coli") || normalizedName.Contains("salmonella") ||
                                     normalizedName.Contains("staphylococcus") || normalizedName.Contains("pseudomonas") ||
                                     normalizedName.Contains("burkholderia") || normalizedName.Contains("candida") ||
                                     normalizedName.Contains("clostridia") || normalizedName.Contains("bile tolerant") ||
                                     normalizedName.Contains("gram-negative") || normalizedName.Contains("aureus");
            bool isComplianceQualitative = !isResidualChlorine &&
                (normalizedName == "acidity" || normalizedName == "ammonium" || normalizedName == "ammonia" ||
                 normalizedName == "chloride" || normalizedName == "chlorides" || normalizedName == "nitrate" ||
                 normalizedName == "nitrates" || normalizedName == "sulphate" || normalizedName == "sulphates" ||
                 normalizedName == "sulfate" || normalizedName == "sulfates" || normalizedName.Contains("heavy metals") ||
                 normalizedName.Contains("oxidisable substances") || normalizedName.Contains("oxidizable substances"));

            string numericText = raw.Replace(',', '.');
            bool hasNumeric = decimal.TryParse(
                numericText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal numericValue);

            if (isAppearance)
            {
                if (hasNumeric)
                    return numericValue <= 0m ? "PASS" : "OOS";
                return raw.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Colorless", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Clear and Colorless", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("OK", StringComparison.OrdinalIgnoreCase)
                    ? "PASS" : "OOS";
            }

            if (isComplianceQualitative)
            {
                if (hasNumeric)
                    return numericValue <= 0m ? "PASS" : "OOS";
                return raw.Equals("Complies", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Comply", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Conform", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Pass", StringComparison.OrdinalIgnoreCase)
                    ? "PASS" : "OOS";
            }

            if (isAbsencePresence)
            {
                if (hasNumeric)
                    return numericValue <= 0m ? "PASS" : "OOS";
                return raw.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Absent", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Negative", StringComparison.OrdinalIgnoreCase)
                    ? "PASS" : "OOS";
            }

            if (!hasNumeric)
                return "INVALID";

            if (isPh || isResidualChlorine)
            {
                if (alertLimit.HasValue && numericValue < alertLimit.Value)
                    return "OOS";
                if (actionLimit.HasValue && numericValue > actionLimit.Value)
                    return "OOS";
                return "PASS";
            }

            if (actionLimit.HasValue && numericValue > actionLimit.Value)
                return "OOS";
            if (alertLimit.HasValue && numericValue > alertLimit.Value)
                return "ALERT";
            return "PASS";
        }

        private static string ValidateCertificateIssuanceGatesInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string effectiveUser)
        {
            bool isDevelopmentAdministrator;
            string authorizedRole = string.Empty;
            using (SqlCommand permissionCommand = new SqlCommand(@"
SELECT TOP (1)
       ISNULL(IsActive,0),
       CONVERT(bit, CASE
           WHEN ISNULL(IsLocked,0)=1 AND (LockedUntil IS NULL OR LockedUntil > SYSDATETIME()) THEN 1
           ELSE 0
       END),
       LockedUntil,
       ISNULL(CanIssueCOA,0),
       ISNULL(Role,N'')
FROM dbo.Users WITH (UPDLOCK,HOLDLOCK)
WHERE UPPER(LTRIM(RTRIM(Username)))=UPPER(LTRIM(RTRIM(@Username)));", connection, transaction))
            {
                permissionCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                permissionCommand.Parameters.Add("@Username", SqlDbType.NVarChar, 100).Value = effectiveUser;
                using SqlDataReader reader = permissionCommand.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The authenticated certificate issuer was not found.");

                bool isActive = reader.GetBoolean(0);
                bool isLocked = reader.GetBoolean(1);
                bool canIssue = reader.GetBoolean(3);
                authorizedRole = reader.GetString(4).Trim();
                isDevelopmentAdministrator = AppConfig.DevelopmentAdminFullPermissions && IsAdministrativeRole(authorizedRole);

                if (!isActive || isLocked)
                    throw new InvalidOperationException("The certificate issuer account is inactive or locked.");
                if (!canIssue && !isDevelopmentAdministrator)
                    throw new UnauthorizedAccessException("The authenticated user is not authorized to issue certificates.");
            }

            int legacyStatusGapCount;
            using (SqlCommand resultCommand = new SqlCommand(@"
SELECT
    COUNT(1) AS TestCount,
    SUM(CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(max),ResultValue))),N'') IS NULL
                  OR UPPER(LTRIM(RTRIM(ISNULL(ResultStatus,N'')))) IN (N'PENDING',N'PARTIALLY ENTERED')
             THEN 1 ELSE 0 END) AS IncompleteCount,
    SUM(CASE WHEN NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(max),ResultValue))),N'') IS NOT NULL
                  AND NULLIF(LTRIM(RTRIM(ISNULL(ResultStatus,N''))),N'') IS NULL
             THEN 1 ELSE 0 END) AS LegacyStatusGapCount
FROM dbo.SampleTests WITH (UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID;", connection, transaction))
            {
                resultCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                resultCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                using SqlDataReader reader = resultCommand.ExecuteReader();
                reader.Read();
                int testCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                int incompleteCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                legacyStatusGapCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                if (testCount == 0 || incompleteCount > 0)
                    throw new InvalidOperationException("All assigned tests must have completed results before certificate issuance.");
            }

            if (legacyStatusGapCount > 0)
            {
                ValidateLegacyCompletedResultStatusGapsInTransaction(
                    connection, transaction, sampleId, effectiveUser, legacyStatusGapCount);
            }

            using (SqlCommand qualityEventCommand = new SqlCommand(@"
IF EXISTS
(
    SELECT 1 FROM dbo.QualityEvents WITH (UPDLOCK,HOLDLOCK)
    WHERE SampleID=@SampleID
      AND ISNULL(CurrentStatus,N'') NOT IN (N'Closed',N'QA Closed',N'Cancelled',N'Rejected Closed')
)
    SELECT 1;
ELSE IF EXISTS
(
    SELECT 1
    FROM dbo.SampleTests st WITH (UPDLOCK,HOLDLOCK)
    WHERE st.SampleID=@SampleID
      AND UPPER(ISNULL(st.ResultStatus,N'')) IN (N'OOS',N'ACTION',N'FAIL')
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.QualityEventAffectedResults qar WITH (UPDLOCK,HOLDLOCK)
          INNER JOIN dbo.QualityEvents qe WITH (UPDLOCK,HOLDLOCK)
              ON qe.QualityEventID=qar.QualityEventID
          WHERE qe.SampleID=@SampleID
            AND qar.SampleTestID=st.SampleTestID
            AND ISNULL(qe.CurrentStatus,N'')=N'Closed'
            AND ISNULL(qe.FinalDisposition,N'') IN
            (
                N'Accept with Justification',N'Release After Investigation',
                N'Released After Investigation',N'Retest Accepted',N'Resample Accepted'
            )
      )
)
    SELECT 2;
ELSE
    SELECT 0;", connection, transaction))
            {
                qualityEventCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                qualityEventCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                int qualityBlock = Convert.ToInt32(qualityEventCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (qualityBlock == 1)
                    throw new InvalidOperationException("Certificate issuance is blocked while a Quality Event is open.");
                if (qualityBlock == 2)
                    throw new InvalidOperationException("OOS, action-limit, or failed results require a closed Quality Event with an acceptable QA disposition before certificate issuance.");
            }

            string analyst = string.Empty;
            string reviewer = string.Empty;
            string approver = string.Empty;
            using (SqlCommand signatureCommand = new SqlCommand(@"
SELECT
    (SELECT TOP(1) SignedBy FROM dbo.ElectronicSignatures WITH (UPDLOCK,HOLDLOCK)
     WHERE SampleID=@SampleID AND ActionType IN(N'Result Entry',N'Results Entry',N'Enter Results',N'Entered Results')
     ORDER BY SignedAt DESC),
    (SELECT TOP(1) SignedBy FROM dbo.ElectronicSignatures WITH (UPDLOCK,HOLDLOCK)
     WHERE SampleID=@SampleID AND ActionType IN(N'Review',N'Review Sample',N'Reviewed')
     ORDER BY SignedAt DESC),
    (SELECT TOP(1) SignedBy FROM dbo.ElectronicSignatures WITH (UPDLOCK,HOLDLOCK)
     WHERE SampleID=@SampleID AND ActionType IN(N'Approve',N'Approve Sample',N'Approval',N'Approved')
     ORDER BY SignedAt DESC);", connection, transaction))
            {
                signatureCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                signatureCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                using SqlDataReader reader = signatureCommand.ExecuteReader();
                reader.Read();
                analyst = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                reviewer = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                approver = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
            }

            if (string.IsNullOrWhiteSpace(analyst) || string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(approver))
                throw new InvalidOperationException("Result-entry, technical-review, and QA-approval electronic signatures are required before certificate issuance.");

            if (!isDevelopmentAdministrator)
            {
                if (analyst.Equals(reviewer, StringComparison.OrdinalIgnoreCase) ||
                    analyst.Equals(approver, StringComparison.OrdinalIgnoreCase) ||
                    reviewer.Equals(approver, StringComparison.OrdinalIgnoreCase) ||
                    effectiveUser.Equals(analyst, StringComparison.OrdinalIgnoreCase) ||
                    effectiveUser.Equals(reviewer, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Certificate issuance violates the required GMP separation of duties.");
            }

            return authorizedRole;
        }

        public static string IssueCertificateAtomic(
            int sampleId,
            string issuedBy,
            string signatureReason,
            string meaningOfSignature = "COA Issuance",
            int? expectedReissuedFromCertificateId = null)
        {
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            string effectiveUser = ResolveAuthenticatedSigner(issuedBy);
            string userRole = string.Empty;
            string certificateNumber = string.Empty;

            ExecuteInTransaction((connection, transaction) =>
            {
                using (SqlCommand requiredObjects = new SqlCommand(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.Certificates',N'U') IS NOT NULL AND
    OBJECT_ID(N'dbo.CertificateDocumentSnapshots',N'U') IS NOT NULL AND
    OBJECT_ID(N'dbo.CertificateLifecycleAudit',N'U') IS NOT NULL AND
    OBJECT_ID(N'dbo.ElectronicSignatures',N'U') IS NOT NULL AND
    OBJECT_ID(N'dbo.AuditTrail',N'U') IS NOT NULL AND
    OBJECT_ID(N'dbo.QualityEvents',N'U') IS NOT NULL AND
    OBJECT_ID(N'dbo.QualityEventAffectedResults',N'U') IS NOT NULL
THEN 1 ELSE 0 END;", connection, transaction))
                {
                    if (Convert.ToInt32(requiredObjects.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                        throw new InvalidOperationException("Certificate compliance tables are incomplete. Apply the current database migration before issuance.");
                }

                string sampleNumber;
                string sampleStatus;
                using (SqlCommand sampleCommand = new SqlCommand(@"
SELECT SampleNumber, Status
FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID=@SampleID;", connection, transaction))
                {
                    sampleCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    using SqlDataReader reader = sampleCommand.ExecuteReader();
                    if (!reader.Read())
                        throw new InvalidOperationException("The selected sample was not found.");
                    sampleNumber = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    sampleStatus = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                }

                if (!sampleStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase) &&
                    !sampleStatus.Equals("COA Cancelled", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The sample must be Approved or COA Cancelled before issuance.");

                userRole = ValidateCertificateIssuanceGatesInTransaction(
                    connection, transaction, sampleId, effectiveUser);

                if (expectedReissuedFromCertificateId.HasValue)
                {
                    using SqlCommand expectedSource = new SqlCommand(@"
SELECT CASE WHEN
    EXISTS
    (
        SELECT 1
        FROM dbo.Certificates WITH (UPDLOCK,HOLDLOCK)
        WHERE CertificateID=@CertificateID
          AND SampleID=@SampleID
          AND (ISNULL(IsCancelled,0)=1 OR UPPER(ISNULL(CertificateStatus,ISNULL(Status,N'')))=N'CANCELLED')
    )
    AND @CertificateID =
    (
        SELECT TOP(1) CertificateID
        FROM dbo.Certificates WITH (UPDLOCK,HOLDLOCK)
        WHERE SampleID=@SampleID
        ORDER BY ISNULL(RevisionNo,-1) DESC, CertificateID DESC
    )
THEN 1 ELSE 0 END;", connection, transaction);
                    expectedSource.Parameters.Add("@CertificateID", SqlDbType.Int).Value = expectedReissuedFromCertificateId.Value;
                    expectedSource.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    if (Convert.ToInt32(expectedSource.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                    {
                        throw new InvalidOperationException(
                            "The expected legacy certificate is not the latest cancelled certificate for this sample. " +
                            "Replacement issuance was stopped to preserve the reissue link and prevent a fork in the certificate reissue lineage. Refresh the controlled reconciliation before continuing.");
                    }
                }

                using (SqlCommand duplicateCommand = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.Certificates WITH (UPDLOCK,HOLDLOCK)
WHERE SampleID=@SampleID
  AND ISNULL(IsCancelled,0)=0
  AND UPPER(ISNULL(CertificateStatus,Status)) IN (N'ACTIVE',N'ISSUED');", connection, transaction))
                {
                    duplicateCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    if (Convert.ToInt32(duplicateCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                        throw new InvalidOperationException("An active certificate already exists for this sample.");
                }

                certificateNumber = GetNextCertificateNumber(connection, transaction);
                DateTime issueDate = GetAuthoritativeDatabaseTime(connection, transaction);

                string snapshotContent;
                using (SqlCommand snapshotCommand = new SqlCommand(@"
SELECT
    @CertificateNumber AS CertificateNumber,
    @IssueDate AS IssueDate,
    @IssuedBy AS IssuedBy,
    @IssuedBy AS IssuedByName,
    @IssueDate AS IssuedBySignedAt,
    @IssuedMeaning AS IssuedByMeaning,
    @IssuedRole AS IssuedByRole,
    @IssuedReason AS IssuedByReason,
    s.SampleID,s.SampleNumber,s.SampleType,s.CreatedDate,s.SampledBy,
    COALESCE(NULLIF(s.PointCodeSnapshot,N''),N'') AS SamplingPointCode,
    COALESCE(NULLIF(s.PointLocationSnapshot,N''),NULLIF(s.PointNameSnapshot,N''),N'') AS SamplingPoint,
    s.Status,s.SamplingDateTime,s.ReceivedDateTime,s.AnalysisStartedDateTime,s.AnalysisCompletedDateTime,
    analyst.SignedBy AS AnalystName,analyst.SignedAt AS AnalystSignedAt,
    analyst.MeaningOfSignature AS AnalystMeaning,analyst.UserRole AS AnalystRole,
    reviewer.SignedBy AS ReviewedByName,reviewer.SignedAt AS ReviewedBySignedAt,
    reviewer.MeaningOfSignature AS ReviewedByMeaning,reviewer.UserRole AS ReviewedByRole,
    approver.SignedBy AS ApprovedByName,approver.SignedAt AS ApprovedBySignedAt,
    approver.MeaningOfSignature AS ApprovedByMeaning,approver.UserRole AS ApprovedByRole,
    JSON_QUERY((
        SELECT st.SampleTestID,
               COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName,
               CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.TestCategorySnapshot ELSE t.TestCategory END AS TestCategory,
               CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END AS Unit,
               CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
               CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
               st.LimitDescription,st.ResultValue,st.ResultStatus,st.DeviationType
        FROM dbo.SampleTests st
        LEFT JOIN dbo.Tests t ON t.TestID=st.TestID
        WHERE st.SampleID=s.SampleID
        ORDER BY st.SampleTestID
        FOR JSON PATH, INCLUDE_NULL_VALUES
    )) AS Tests
FROM dbo.Samples s
OUTER APPLY
(
    SELECT TOP(1) es.SignedBy,es.SignedAt,es.MeaningOfSignature,es.UserRole
    FROM dbo.ElectronicSignatures es
    WHERE es.SampleID=s.SampleID
      AND es.ActionType IN(N'Result Entry',N'Results Entry',N'Enter Results',N'Entered Results')
    ORDER BY es.SignedAt DESC
) analyst
OUTER APPLY
(
    SELECT TOP(1) es.SignedBy,es.SignedAt,es.MeaningOfSignature,es.UserRole
    FROM dbo.ElectronicSignatures es
    WHERE es.SampleID=s.SampleID
      AND es.ActionType IN(N'Review',N'Review Sample',N'Reviewed')
    ORDER BY es.SignedAt DESC
) reviewer
OUTER APPLY
(
    SELECT TOP(1) es.SignedBy,es.SignedAt,es.MeaningOfSignature,es.UserRole
    FROM dbo.ElectronicSignatures es
    WHERE es.SampleID=s.SampleID
      AND es.ActionType IN(N'Approve',N'Approve Sample',N'Approval',N'Approved')
    ORDER BY es.SignedAt DESC
) approver
WHERE s.SampleID=@SampleID
FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES;", connection, transaction))
                {
                    snapshotCommand.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 50).Value = certificateNumber;
                    snapshotCommand.Parameters.Add("@IssueDate", SqlDbType.DateTime2).Value = issueDate;
                    snapshotCommand.Parameters.Add("@IssuedBy", SqlDbType.NVarChar, 100).Value = effectiveUser;
                    snapshotCommand.Parameters.Add("@IssuedMeaning", SqlDbType.NVarChar, 255).Value = meaningOfSignature;
                    snapshotCommand.Parameters.Add("@IssuedRole", SqlDbType.NVarChar, 100).Value = userRole;
                    snapshotCommand.Parameters.Add("@IssuedReason", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(signatureReason) ? (object)DBNull.Value : signatureReason.Trim();
                    snapshotCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;

                    // SQL Server may return a large FOR JSON value in multiple rows.
                    // Concatenate every chunk before validating and hashing the immutable snapshot.
                    using SqlDataReader snapshotReader = snapshotCommand.ExecuteReader();
                    StringBuilder snapshotBuilder = new StringBuilder();
                    while (snapshotReader.Read())
                    {
                        if (!snapshotReader.IsDBNull(0))
                            snapshotBuilder.Append(snapshotReader.GetString(0));
                    }
                    snapshotContent = snapshotBuilder.ToString();
                }

                if (string.IsNullOrWhiteSpace(snapshotContent))
                    throw new InvalidOperationException("The immutable certificate snapshot could not be generated.");
                if (!ValidateCertificateSnapshotJson(snapshotContent, out string snapshotValidationMessage))
                {
                    throw new InvalidOperationException(
                        "The certificate issue snapshot could not be generated as complete valid JSON. " +
                        "Issuance was rolled back and no certificate was created. " + snapshotValidationMessage);
                }

                string snapshotHash;
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(snapshotContent));
                    snapshotHash = BitConverter.ToString(hash).Replace("-", string.Empty);
                }

                int certificateId;
                using (SqlCommand insertCertificate = new SqlCommand(@"
DECLARE @PreviousRevisionNo INT = ISNULL((SELECT MAX(RevisionNo) FROM dbo.Certificates WHERE SampleID=@SampleID),-1);
DECLARE @PreviousCancelledCertificateID INT =
CASE WHEN @ExpectedReissuedFromCertificateID IS NOT NULL THEN @ExpectedReissuedFromCertificateID ELSE
(
    SELECT TOP(1) CertificateID FROM dbo.Certificates
    WHERE SampleID=@SampleID AND ISNULL(IsCancelled,0)=1
    ORDER BY RevisionNo DESC,CertificateID DESC
) END;
INSERT dbo.Certificates
(
    CertificateNumber,SampleID,SampleNumber,IssueDate,IssuedBy,Status,RevisionNo,
    IsCancelled,VerificationCode,ReportHash,CertificateStatus,ReissuedFromCertificateID
)
OUTPUT INSERTED.CertificateID
VALUES
(
    @CertificateNumber,@SampleID,@SampleNumber,@IssueDate,@IssuedBy,N'Active',
    @PreviousRevisionNo+1,0,@CertificateNumber,@ReportHash,N'Active',@PreviousCancelledCertificateID
);", connection, transaction))
                {
                    insertCertificate.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 50).Value = certificateNumber;
                    insertCertificate.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    insertCertificate.Parameters.Add("@SampleNumber", SqlDbType.NVarChar, 100).Value = (object)sampleNumber ?? DBNull.Value;
                    insertCertificate.Parameters.Add("@IssueDate", SqlDbType.DateTime2).Value = issueDate;
                    insertCertificate.Parameters.Add("@IssuedBy", SqlDbType.NVarChar, 100).Value = effectiveUser;
                    insertCertificate.Parameters.Add("@ReportHash", SqlDbType.NVarChar, 128).Value = snapshotHash;
                    insertCertificate.Parameters.Add("@ExpectedReissuedFromCertificateID", SqlDbType.Int).Value =
                        expectedReissuedFromCertificateId.HasValue
                            ? expectedReissuedFromCertificateId.Value
                            : (object)DBNull.Value;
                    certificateId = Convert.ToInt32(insertCertificate.ExecuteScalar(), CultureInfo.InvariantCulture);
                }

                using (SqlCommand snapshotInsert = new SqlCommand(@"
INSERT dbo.CertificateDocumentSnapshots
(CertificateID,SampleID,CertificateNumber,SnapshotContent,SnapshotHash,CreatedBy,CreatedAt)
VALUES(@CertificateID,@SampleID,@CertificateNumber,@SnapshotContent,@SnapshotHash,@CreatedBy,@CreatedAt);", connection, transaction))
                {
                    snapshotInsert.Parameters.Add("@CertificateID", SqlDbType.Int).Value = certificateId;
                    snapshotInsert.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    snapshotInsert.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 50).Value = certificateNumber;
                    snapshotInsert.Parameters.Add("@SnapshotContent", SqlDbType.NVarChar, -1).Value = snapshotContent;
                    snapshotInsert.Parameters.Add("@SnapshotHash", SqlDbType.NVarChar, 128).Value = snapshotHash;
                    snapshotInsert.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 100).Value = effectiveUser;
                    snapshotInsert.Parameters.Add("@CreatedAt", SqlDbType.DateTime2).Value = issueDate;
                    snapshotInsert.ExecuteNonQuery();
                }

                using (SqlCommand signatureCommand = new SqlCommand(@"
INSERT dbo.ElectronicSignatures
(SampleID,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole,SignedAt)
VALUES(@SampleID,N'COA Issuance',@Reason,@SignedBy,@Meaning,@Role,@SignedAt);", connection, transaction))
                {
                    signatureCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    signatureCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(signatureReason) ? (object)DBNull.Value : signatureReason.Trim();
                    signatureCommand.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = effectiveUser;
                    signatureCommand.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = meaningOfSignature;
                    signatureCommand.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = userRole;
                    signatureCommand.Parameters.Add("@SignedAt", SqlDbType.DateTime2).Value = issueDate;
                    signatureCommand.ExecuteNonQuery();
                }

                using (SqlCommand lifecycleCommand = new SqlCommand(@"
INSERT dbo.CertificateLifecycleAudit
(CertificateID,SampleID,CertificateNumber,ActionType,ActionName,OldStatus,NewStatus,Reason,PerformedBy,PerformedAt)
VALUES(@CertificateID,@SampleID,@CertificateNumber,N'Issued',N'Issued',NULL,N'Active',@Reason,@PerformedBy,@PerformedAt);", connection, transaction))
                {
                    lifecycleCommand.Parameters.Add("@CertificateID", SqlDbType.Int).Value = certificateId;
                    lifecycleCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    lifecycleCommand.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 50).Value = certificateNumber;
                    lifecycleCommand.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = string.IsNullOrWhiteSpace(signatureReason) ? "Initial certificate issuance" : signatureReason.Trim();
                    lifecycleCommand.Parameters.Add("@PerformedBy", SqlDbType.NVarChar, 100).Value = effectiveUser;
                    lifecycleCommand.Parameters.Add("@PerformedAt", SqlDbType.DateTime2).Value = issueDate;
                    lifecycleCommand.ExecuteNonQuery();
                }

                using (SqlCommand updateSample = new SqlCommand(
                    "UPDATE dbo.Samples SET Status=N'COA Issued' WHERE SampleID=@SampleID AND Status IN(N'Approved',N'COA Cancelled');",
                    connection, transaction))
                {
                    updateSample.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    if (updateSample.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("The sample status changed while the certificate was being issued.");
                }

                using (SqlCommand syncWater = new SqlCommand(@"
IF OBJECT_ID(N'dbo.Water_PlanSamples',N'U') IS NOT NULL AND OBJECT_ID(N'dbo.Water_Plans',N'U') IS NOT NULL
BEGIN
    UPDATE ps SET Status=N'Approved'
    FROM dbo.Water_PlanSamples ps WHERE ps.SampleID=@SampleID;

    UPDATE p SET Status=CASE
      WHEN p.Status=N'Cancelled' THEN p.Status
      WHEN EXISTS(SELECT 1 FROM dbo.Water_PlanSamples x WHERE x.WaterPlanID=p.WaterPlanID AND x.Status=N'Rejected') THEN N'Recollection Required'
      WHEN NOT EXISTS
      (
        SELECT 1 FROM dbo.Water_PlanSamples x LEFT JOIN dbo.Samples s ON s.SampleID=x.SampleID
        WHERE x.WaterPlanID=p.WaterPlanID AND (x.SampleID IS NULL OR ISNULL(s.Status,N'') NOT IN(N'Approved',N'COA Issued'))
      ) THEN N'Completed'
      ELSE N'Analysis In Progress' END
    FROM dbo.Water_Plans p
    WHERE EXISTS(SELECT 1 FROM dbo.Water_PlanSamples ps WHERE ps.WaterPlanID=p.WaterPlanID AND ps.SampleID=@SampleID);
END;", connection, transaction))
                {
                    syncWater.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                    syncWater.ExecuteNonQuery();
                }

                AddAuditTrailAdvanced(
                    connection, transaction, "Certificates", certificateId, "COA Issuance",
                    string.Empty, certificateNumber, signatureReason, effectiveUser,
                    "CertificateNumber", null, sampleNumber, "Certificate Issuance");
            });

            return certificateNumber;
        }

        public static string IssueCertificate(int sampleId, string issuedBy)
        {
            return IssueCertificateAtomic(
                sampleId,
                issuedBy,
                "Certificate issuance through the compatibility API",
                "Certificate issued from an approved immutable result snapshot");
        }

        public static int AddCertificateLifecycleAudit(
            int sampleId,
            string certificateNumber,
            string actionType,
            string oldStatus,
            string newStatus,
            string reason,
            string performedBy)
        {
            performedBy = ResolveAuthenticatedSigner(performedBy);

            if (!TableExists("CertificateLifecycleAudit"))
                return 0;

            bool hasSampleId = ColumnExists("CertificateLifecycleAudit", "SampleID");
            bool hasActionType = ColumnExists("CertificateLifecycleAudit", "ActionType");
            bool hasActionName = ColumnExists("CertificateLifecycleAudit", "ActionName");
            bool hasPerformedAt = ColumnExists("CertificateLifecycleAudit", "PerformedAt");

            if (!hasActionType && !hasActionName)
                throw new InvalidOperationException("CertificateLifecycleAudit has no supported action column.");

            // Some upgraded databases retain both legacy ActionName (NOT NULL) and
            // current ActionType. Populate both whenever present to keep printing,
            // cancellation, and reissuance compatible with either schema generation.
            List<string> insertColumnList = new List<string> { "CertificateID" };
            List<string> selectColumnList = new List<string> { "CertificateID" };
            if (hasSampleId)
            {
                insertColumnList.Add("SampleID");
                selectColumnList.Add("@sampleId");
            }
            insertColumnList.Add("CertificateNumber");
            selectColumnList.Add("@certificateNumber");
            if (hasActionType)
            {
                insertColumnList.Add("ActionType");
                selectColumnList.Add("@actionType");
            }
            if (hasActionName)
            {
                insertColumnList.Add("ActionName");
                selectColumnList.Add("@actionType");
            }
            insertColumnList.AddRange(new[] { "OldStatus", "NewStatus", "Reason", "PerformedBy" });
            selectColumnList.AddRange(new[] { "@oldStatus", "@newStatus", "@reason", "@performedBy" });
            if (hasPerformedAt)
            {
                insertColumnList.Add("PerformedAt");
                selectColumnList.Add("SYSDATETIME()");
            }

            string insertColumns = string.Join(", ", insertColumnList);
            string selectColumns = string.Join(", ", selectColumnList);

            string query = $@"
                INSERT INTO CertificateLifecycleAudit
                (
                    {insertColumns}
                )
                SELECT TOP 1
                    {selectColumns}
                FROM Certificates
                WHERE CertificateNumber = @certificateNumber
                ORDER BY CertificateID DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId),
                new SqlParameter("@certificateNumber", string.IsNullOrWhiteSpace(certificateNumber) ? (object)DBNull.Value : certificateNumber),
                new SqlParameter("@actionType", string.IsNullOrWhiteSpace(actionType) ? "Unspecified" : actionType.Trim()),
                new SqlParameter("@oldStatus", string.IsNullOrWhiteSpace(oldStatus) ? (object)DBNull.Value : oldStatus),
                new SqlParameter("@newStatus", string.IsNullOrWhiteSpace(newStatus) ? (object)DBNull.Value : newStatus),
                new SqlParameter("@reason", string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason),
                new SqlParameter("@performedBy", performedBy)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static int AddCertificatePrintHistory(int sampleId, string certificateNumber, string printedBy)
        {
            printedBy = ResolveAuthenticatedSigner(printedBy);

            if (!TableExists("CertificatePrintHistory"))
                return 0;

            bool hasSampleId = ColumnExists("CertificatePrintHistory", "SampleID");

            string insertColumns = hasSampleId
                ? "CertificateID, SampleID, CertificateNumber, PrintedBy"
                : "CertificateID, CertificateNumber, PrintedBy";

            string selectColumns = hasSampleId
                ? "CertificateID, @sampleId, @certificateNumber, @printedBy"
                : "CertificateID, @certificateNumber, @printedBy";

            string query = $@"
                INSERT INTO CertificatePrintHistory
                (
                    {insertColumns}
                )
                SELECT TOP 1
                    {selectColumns}
                FROM Certificates
                WHERE CertificateNumber = @certificateNumber
                ORDER BY CertificateID DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", sampleId),
                new SqlParameter("@certificateNumber", string.IsNullOrWhiteSpace(certificateNumber) ? (object)DBNull.Value : certificateNumber),
                new SqlParameter("@printedBy", printedBy)
            };

            return ExecuteNonQuery(query, pars);
        }

        public static string CancelCertificateControlled(int sampleId, string cancelledBy, string reason)
        {
            return CancelCertificateControlledCore(sampleId, null, cancelledBy, reason, null, null);
        }

        public static string CancelCertificateControlled(
            int sampleId,
            string cancelledBy,
            string reason,
            string meaningOfSignature,
            string signatureReason)
        {
            return CancelCertificateControlledCore(sampleId, null, cancelledBy, reason, meaningOfSignature, signatureReason);
        }

        public static string CancelCertificateControlled(
            int sampleId,
            int expectedCertificateId,
            string cancelledBy,
            string reason)
        {
            if (expectedCertificateId <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedCertificateId));

            return CancelCertificateControlledCore(sampleId, expectedCertificateId, cancelledBy, reason, null, null);
        }

        public static string CancelCertificateControlled(
            int sampleId,
            int expectedCertificateId,
            string cancelledBy,
            string reason,
            string meaningOfSignature,
            string signatureReason)
        {
            if (expectedCertificateId <= 0)
                throw new ArgumentOutOfRangeException(nameof(expectedCertificateId));

            return CancelCertificateControlledCore(sampleId, expectedCertificateId, cancelledBy, reason, meaningOfSignature, signatureReason);
        }

        private static string CancelCertificateControlledCore(
            int sampleId,
            int? expectedCertificateId,
            string cancelledBy,
            string reason,
            string meaningOfSignature,
            string signatureReason)
        {
            cancelledBy = ResolveAuthenticatedSigner(cancelledBy);

            if (!CanCancelCertificate(cancelledBy))
                return "The authenticated user does not have COA cancellation permission.";

            if (!TableExists("Certificates"))
                return "Certificates table does not exist.";

            if (!ColumnExists("Certificates", "IsCancelled"))
                return "COA Management database update is not installed. Run 002_COA_Management.sql first.";

            if (string.IsNullOrWhiteSpace(reason))
                return "Cancellation reason is required.";

            if (AppConfig.EnforceElectronicSignatureStorage &&
                (string.IsNullOrWhiteSpace(meaningOfSignature) || string.IsNullOrWhiteSpace(signatureReason)))
            {
                return "A complete electronic signature meaning and reason are required for COA cancellation.";
            }

            try
            {
                ExecuteInTransaction((connection, transaction) =>
                {
                    string userRole = EnsureUserPermissionInTransaction(
                        connection,
                        transaction,
                        cancelledBy,
                        "CanCancelCOA",
                        "cancel a certificate");

                    string certificateNumber;
                    string oldSampleStatus;

                    using (SqlCommand selectCertificate = new SqlCommand(@"
SELECT TOP 1 CertificateNumber
FROM dbo.Certificates WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID
  AND (@ExpectedCertificateID IS NULL OR CertificateID=@ExpectedCertificateID)
  AND ISNULL(IsCancelled, 0) = 0
  AND ISNULL(CertificateStatus, ISNULL(Status, N'')) <> N'Cancelled'
ORDER BY CertificateID DESC;", connection, transaction))
                    {
                        selectCertificate.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                        selectCertificate.Parameters.Add("@ExpectedCertificateID", SqlDbType.Int).Value =
                            expectedCertificateId.HasValue ? expectedCertificateId.Value : (object)DBNull.Value;
                        object value = selectCertificate.ExecuteScalar();
                        certificateNumber = value == null || value == DBNull.Value
                            ? string.Empty
                            : Convert.ToString(value, CultureInfo.InvariantCulture);
                    }

                    if (string.IsNullOrWhiteSpace(certificateNumber))
                        throw new InvalidOperationException("No active certificate was found for this sample.");

                    using (SqlCommand selectSample = new SqlCommand(@"
SELECT ISNULL(Status, N'')
FROM dbo.Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;", connection, transaction))
                    {
                        selectSample.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                        object value = selectSample.ExecuteScalar();
                        oldSampleStatus = value == null || value == DBNull.Value
                            ? string.Empty
                            : Convert.ToString(value, CultureInfo.InvariantCulture);
                    }

                    using (SqlCommand cancelCertificate = new SqlCommand(@"
UPDATE dbo.Certificates
SET IsCancelled = 1,
    CancelledBy = @CancelledBy,
    CancelledDate = SYSDATETIME(),
    CancellationReason = @Reason,
    Status = N'Cancelled',
    CertificateStatus = N'Cancelled'
WHERE CertificateNumber = @CertificateNumber
  AND SampleID = @SampleID
  AND (@ExpectedCertificateID IS NULL OR CertificateID=@ExpectedCertificateID)
  AND ISNULL(IsCancelled, 0) = 0;", connection, transaction))
                    {
                        cancelCertificate.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                        cancelCertificate.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 50).Value = certificateNumber;
                        cancelCertificate.Parameters.Add("@ExpectedCertificateID", SqlDbType.Int).Value =
                            expectedCertificateId.HasValue ? expectedCertificateId.Value : (object)DBNull.Value;
                        cancelCertificate.Parameters.Add("@CancelledBy", SqlDbType.NVarChar, 120).Value = cancelledBy;
                        cancelCertificate.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = reason.Trim();

                        if (cancelCertificate.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("Certificate cancellation failed because the certificate changed before commit.");
                    }

                    using (SqlCommand updateSample = new SqlCommand(@"
UPDATE dbo.Samples
SET Status = N'COA Cancelled'
WHERE SampleID = @SampleID;", connection, transaction))
                    {
                        updateSample.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                        if (updateSample.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("The sample status could not be updated. Certificate cancellation was rolled back.");
                    }

                    if (AppConfig.EnforceElectronicSignatureStorage)
                    {
                        using SqlCommand signatureCommand = new SqlCommand(@"
INSERT dbo.ElectronicSignatures
(SampleID,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole,SignedAt)
VALUES(@SampleID,N'COA Cancellation',@ActionReason,@SignedBy,@Meaning,@Role,SYSDATETIME());", connection, transaction);
                        signatureCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                        signatureCommand.Parameters.Add("@ActionReason", SqlDbType.NVarChar, -1).Value = signatureReason.Trim();
                        signatureCommand.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = cancelledBy;
                        signatureCommand.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = meaningOfSignature.Trim();
                        signatureCommand.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = userRole;
                        if (signatureCommand.ExecuteNonQuery() != 1)
                            throw new InvalidOperationException("The COA cancellation electronic signature could not be stored. The cancellation was rolled back.");
                    }

                    AddCertificateLifecycleAuditInTransaction(connection, transaction, sampleId, certificateNumber, "Cancelled", "Active", "Cancelled", reason, cancelledBy);
                    AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "Certificates",
                        sampleId,
                        "COA Cancellation",
                        "CertificateNumber=" + certificateNumber + "; SampleStatus=" + oldSampleStatus,
                        "CertificateStatus=Cancelled; SampleStatus=COA Cancelled",
                        reason +
                        (string.IsNullOrWhiteSpace(meaningOfSignature) ? string.Empty : " | Meaning: " + meaningOfSignature.Trim()) +
                        (string.IsNullOrWhiteSpace(signatureReason) ? string.Empty : " | E-signature reason: " + signatureReason.Trim()),
                        cancelledBy,
                        "CertificateStatus",
                        null,
                        certificateNumber,
                        "Water COA");
                });

                return "Certificate cancelled successfully.";
            }
            catch (InvalidOperationException ex)
            {
                return Infrastructure.UserFacingError.SafeMessage(ex, "Certificate cancellation");
            }
        }

        private static int AddCertificateLifecycleAuditInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string certificateNumber,
            string actionType,
            string oldStatus,
            string newStatus,
            string reason,
            string performedBy)
        {
            if (!TableExistsInTransaction(connection, transaction, "CertificateLifecycleAudit"))
                return 0;

            bool hasSampleId = ColumnExistsInTransaction(connection, transaction, "CertificateLifecycleAudit", "SampleID");
            bool hasActionType = ColumnExistsInTransaction(connection, transaction, "CertificateLifecycleAudit", "ActionType");
            bool hasActionName = ColumnExistsInTransaction(connection, transaction, "CertificateLifecycleAudit", "ActionName");
            bool hasPerformedAt = ColumnExistsInTransaction(connection, transaction, "CertificateLifecycleAudit", "PerformedAt");

            if (!hasActionType && !hasActionName)
                throw new InvalidOperationException("CertificateLifecycleAudit has no supported action column.");

            List<string> insertColumnList = new List<string> { "CertificateID" };
            List<string> selectColumnList = new List<string> { "CertificateID" };

            if (hasSampleId)
            {
                insertColumnList.Add("SampleID");
                selectColumnList.Add("@SampleID");
            }

            insertColumnList.Add("CertificateNumber");
            selectColumnList.Add("@CertificateNumber");

            if (hasActionType)
            {
                insertColumnList.Add("ActionType");
                selectColumnList.Add("@ActionType");
            }

            if (hasActionName)
            {
                insertColumnList.Add("ActionName");
                selectColumnList.Add("@ActionType");
            }

            insertColumnList.AddRange(new[] { "OldStatus", "NewStatus", "Reason", "PerformedBy" });
            selectColumnList.AddRange(new[] { "@OldStatus", "@NewStatus", "@Reason", "@PerformedBy" });

            if (hasPerformedAt)
            {
                insertColumnList.Add("PerformedAt");
                selectColumnList.Add("SYSDATETIME()");
            }

            using SqlCommand command = new SqlCommand($@"
INSERT INTO dbo.CertificateLifecycleAudit
(
    {string.Join(", ", insertColumnList)}
)
SELECT TOP 1
    {string.Join(", ", selectColumnList)}
FROM dbo.Certificates
WHERE SampleID = @SampleID
  AND CertificateNumber = @CertificateNumber
ORDER BY CertificateID DESC;", connection, transaction);

            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            command.Parameters.Add("@CertificateNumber", SqlDbType.NVarChar, 50).Value =
                string.IsNullOrWhiteSpace(certificateNumber) ? (object)DBNull.Value : certificateNumber;
            command.Parameters.Add("@ActionType", SqlDbType.NVarChar, 80).Value =
                string.IsNullOrWhiteSpace(actionType) ? "Unspecified" : actionType.Trim();
            command.Parameters.Add("@OldStatus", SqlDbType.NVarChar, 80).Value =
                string.IsNullOrWhiteSpace(oldStatus) ? (object)DBNull.Value : oldStatus.Trim();
            command.Parameters.Add("@NewStatus", SqlDbType.NVarChar, 80).Value =
                string.IsNullOrWhiteSpace(newStatus) ? (object)DBNull.Value : newStatus.Trim();
            command.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value =
                string.IsNullOrWhiteSpace(reason) ? (object)DBNull.Value : reason.Trim();
            command.Parameters.Add("@PerformedBy", SqlDbType.NVarChar, 120).Value =
                string.IsNullOrWhiteSpace(performedBy) ? (object)DBNull.Value : performedBy.Trim();

            return command.ExecuteNonQuery();
        }

        private static bool TableExistsInTransaction(SqlConnection connection, SqlTransaction transaction, string tableName)
        {
            using SqlCommand command = new SqlCommand(
                "SELECT CASE WHEN OBJECT_ID(@QualifiedName, N'U') IS NULL THEN 0 ELSE 1 END;",
                connection,
                transaction);
            command.Parameters.Add("@QualifiedName", SqlDbType.NVarChar, 260).Value = "dbo." + tableName;
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private static bool ColumnExistsInTransaction(SqlConnection connection, SqlTransaction transaction, string tableName, string columnName)
        {
            using SqlCommand command = new SqlCommand(
                "SELECT CASE WHEN COL_LENGTH(@QualifiedName, @ColumnName) IS NULL THEN 0 ELSE 1 END;",
                connection,
                transaction);
            command.Parameters.Add("@QualifiedName", SqlDbType.NVarChar, 260).Value = "dbo." + tableName;
            command.Parameters.Add("@ColumnName", SqlDbType.NVarChar, 128).Value = columnName;
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        public static string CancelCertificate(int sampleId, string cancelledBy, string reason)
        {
            // Compatibility entry point delegates to the transactional, permission-checked implementation.
            return CancelCertificateControlled(sampleId, cancelledBy, reason);
        }


    }

    public static class SafeExtensions
    {
        public static string GetSafeString(this DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return "";

            object value = row[columnName];
            return value == DBNull.Value || value == null ? "" : value.ToString();
        }

        public static int GetSafeInt(this DataRow row, string columnName, int defaultValue = 0)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return defaultValue;

            object value = row[columnName];

            if (value == DBNull.Value || value == null)
                return defaultValue;

            if (int.TryParse(value.ToString(), out int result))
                return result;

            return defaultValue;
        }

        public static decimal? GetSafeDecimal(this DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return null;

            object value = row[columnName];

            if (value == DBNull.Value || value == null)
                return null;

            string raw = value.ToString()
                .Trim()
                
                .Replace(",", ".");

            if (decimal.TryParse(
                    raw,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out decimal result))
            {
                return result;
            }

            return null;
        }

        public static DateTime? GetSafeDateTime(this DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return null;

            object value = row[columnName];

            if (value == DBNull.Value || value == null)
                return null;

            if (DateTime.TryParse(value.ToString(), out DateTime result))
                return result;

            return null;
        }

        public static string TrimSafe(this string value)
        {
            return value == null ? "" : value.Trim();
        }

        public static bool EqualsIgnoreCase(this string value, string compareTo)
        {
            return string.Equals(value, compareTo, StringComparison.OrdinalIgnoreCase);
        }

        public static bool ContainsIgnoreCase(this string value, string searchTerm)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(searchTerm))
                return false;

            return value.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static object ToDbValue(this object value)
        {
            return value ?? DBNull.Value;
        }

        public static object ToDbValue(this int? value)
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        public static object ToDbValue(this decimal? value)
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        public static object ToDbValue(this DateTime? value)
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }
    }

    public static class EnumHelper
    {
        public static string GetStatusColor(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return "#64748B";

            string s = status.Trim().ToLowerInvariant();

            if (s == "pass" ||
                s == "passed" ||
                s == "approved" ||
                s == "completed" ||
                s == "results entered" ||
                s == "coa issued" ||
                s == "conform" ||
                s == "conforms")
                return "#10B981";

            if (s == "alert" ||
                s == "partially entered" ||
                s == "in progress" ||
                s == "under review" ||
                s == "reviewed" ||
                s == "in analysis" ||
                s == "registered")
                return "#F59E0B";

            if (s == "oos" ||
                s == "failed" ||
                s == "non-conform" ||
                s == "non conform")
                return "#EF4444";

            if (s == "pending")
                return "#64748B";

            return "#3B82F6";
        }

        public static string GetStatusDisplayName(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return "Pending";

            string s = status.Trim();

            if (s.EqualsIgnoreCase("oos"))
                return "OOS";

            if (s.EqualsIgnoreCase("pass"))
                return "PASS";

            if (s.EqualsIgnoreCase("coa issued"))
                return "COA Issued";

            if (s.EqualsIgnoreCase("reviewed"))
                return "Reviewed";

            if (s.EqualsIgnoreCase("results entered"))
                return "Results Entered";

            if (s.EqualsIgnoreCase("partially entered"))
                return "Partially Entered";

            return s;
        }
    }
}
