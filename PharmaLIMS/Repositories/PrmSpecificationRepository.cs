using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Interfaces;
using System;
using System.Data;
using System.Globalization;
using System.Text;

namespace PharmaLIMS.Repositories
{
    public sealed class PrmSpecificationRepository : IPrmSpecificationRepository
    {
        private const int CategoryAliasCount = 6;

        // Authoritative Approved PRM profiles must carry attributable Review and
        // Approve electronic-signature evidence for the exact master-data version.
        // Production additionally requires independent reviewer/approver identities.
        internal const string ApprovedProfileEvidencePredicateSql = @"
      AND NULLIF(LTRIM(RTRIM(ISNULL(configured.ReviewedBy,N''))),N'') IS NOT NULL
      AND configured.ReviewedDate IS NOT NULL
      AND NULLIF(LTRIM(RTRIM(ISNULL(configured.ApprovedBy,N''))),N'') IS NOT NULL
      AND configured.ApprovedDate IS NOT NULL
      AND configured.ReviewedDate <= configured.ApprovedDate
      AND EXISTS
      (
          SELECT 1
          FROM dbo.PRM_SpecificationSignatures reviewSig
          INNER JOIN dbo.PRM_SpecificationSignatures approveSig
              ON approveSig.SpecificationNo=reviewSig.SpecificationNo
             AND approveSig.SampleCategory=reviewSig.SampleCategory
             AND approveSig.VersionNo=reviewSig.VersionNo
          WHERE reviewSig.SpecificationNo=configured.SpecificationNo
            AND reviewSig.SampleCategory=configured.SampleCategory
            AND reviewSig.VersionNo=configured.VersionNo
            AND reviewSig.ActionType=N'Review Specification'
            AND approveSig.ActionType=N'Approve Specification'
            AND UPPER(LTRIM(RTRIM(reviewSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ReviewedBy)))
            AND UPPER(LTRIM(RTRIM(approveSig.SignedBy)))=UPPER(LTRIM(RTRIM(configured.ApprovedBy)))
            AND NULLIF(LTRIM(RTRIM(reviewSig.ActionReason)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(reviewSig.MeaningOfSignature)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(approveSig.ActionReason)),N'') IS NOT NULL
            AND NULLIF(LTRIM(RTRIM(approveSig.MeaningOfSignature)),N'') IS NOT NULL
            AND reviewSig.SignedAt <= approveSig.SignedAt
            AND (@RequireIndependentApprover=0 OR
                 UPPER(LTRIM(RTRIM(reviewSig.SignedBy)))<>UPPER(LTRIM(RTRIM(approveSig.SignedBy))))
      )";

        private readonly DatabaseConnection _database;

        public PrmSpecificationRepository()
            : this(new DatabaseConnection())
        {
        }

        public PrmSpecificationRepository(DatabaseConnection database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public int EnsureSampleTestsAssigned(int sampleId)
        {
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            int assignedTestCount = 0;
            _database.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, Login.CurrentUser, "CanEnterResults", "reload PRM specification tests");
                if (!CanCompleteHistoricalAssignment(connection, transaction, sampleId))
                    throw new InvalidOperationException("PRM tests can be reloaded only before any result is entered and before submission for review.");
                assignedTestCount = EnsureSampleTestsAssigned(connection, transaction, sampleId);
            });

            return assignedTestCount;
        }

        internal int EnsureSampleTestsAssigned(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            (string specificationNo, string sampleCategory) = ResolveEffectiveSampleDefinition(connection, transaction, sampleId);
            if (string.IsNullOrWhiteSpace(specificationNo) || string.IsNullOrWhiteSpace(sampleCategory))
                return 0;

            AssignCompatibleTests(connection, transaction, sampleId, specificationNo, sampleCategory);
            return CountAssignedTests(connection, transaction, sampleId);
        }

        public int ReplaceUnenteredSampleTests(int sampleId)
        {
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            int assignedTestCount = 0;
            _database.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, Login.CurrentUser, "CanRegisterSamples", "replace PRM specification tests");
                assignedTestCount = ReplaceUnenteredSampleTests(connection, transaction, sampleId);
            });

            return assignedTestCount;
        }

        internal int ReplaceUnenteredSampleTests(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            if (sampleId <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleId));

            (string specificationNo, string sampleCategory) = ResolveEffectiveSampleDefinition(connection, transaction, sampleId);

            using (SqlCommand validationCommand = new SqlCommand(@"
SELECT
    LTRIM(RTRIM(ISNULL(sample.SampleStatus, N''))) AS SampleStatus,
    SUM(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(test.ResultValue, N''))), N'') IS NOT NULL THEN 1 ELSE 0 END) AS EnteredResults
FROM dbo.PRM_Samples sample WITH (UPDLOCK, HOLDLOCK)
LEFT JOIN dbo.PRM_SampleTests test WITH (UPDLOCK, HOLDLOCK) ON test.SampleID = sample.SampleID
WHERE sample.SampleID = @SampleID
GROUP BY sample.SampleStatus;", connection, transaction))
            {
                validationCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                validationCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                using SqlDataReader reader = validationCommand.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected PRM sample was not found.");

                string status = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                int enteredResults = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);

                if (!status.Equals("Registered", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The specification can be changed only while the PRM sample is Registered.");
                if (enteredResults > 0)
                    throw new InvalidOperationException("The specification cannot be changed after results have been entered.");
            }

            using (SqlCommand deleteCommand = new SqlCommand(
                "DELETE FROM dbo.PRM_SampleTests WHERE SampleID = @SampleID;",
                connection,
                transaction))
            {
                deleteCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                deleteCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                deleteCommand.ExecuteNonQuery();
            }

            if (!string.IsNullOrWhiteSpace(specificationNo) && !string.IsNullOrWhiteSpace(sampleCategory))
                AssignCompatibleTests(connection, transaction, sampleId, specificationNo, sampleCategory);

            return CountAssignedTests(connection, transaction, sampleId);
        }

        private static (string SpecificationNo, string SampleCategory) ResolveEffectiveSampleDefinition(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            string currentSpecification;
            string sampleCategory;

            using (SqlCommand sampleCommand = new SqlCommand(@"
SELECT
    LTRIM(RTRIM(ISNULL(SpecificationNo,N''))),
    LTRIM(RTRIM(ISNULL(SampleCategory,N'')))
FROM dbo.PRM_Samples
WHERE SampleID=@SampleID;", connection, transaction))
            {
                sampleCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                using SqlDataReader reader = sampleCommand.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("The selected PRM sample was not found.");

                currentSpecification = reader.GetString(0);
                sampleCategory = reader.GetString(1);
            }

            if (string.IsNullOrWhiteSpace(sampleCategory))
                return (currentSpecification, sampleCategory);

            if (!string.IsNullOrWhiteSpace(currentSpecification) &&
                HasApprovedDefinition(connection, transaction, currentSpecification, sampleCategory))
                return (currentSpecification, sampleCategory);

            // Never auto-link a current master profile to a historical sample. A controlled
            // specification choice must be made in registration and recorded with its version.
            return (string.Empty, sampleCategory);
        }

        private static string FindSingleApprovedProfile(
            SqlConnection connection,
            SqlTransaction transaction,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
;WITH ApprovedProfiles AS
(
    SELECT
        LTRIM(RTRIM(configured.SpecificationNo)) AS SpecificationNo,
        MAX(CASE WHEN ISNULL(configured.IsDefaultForCategory,0)=1 THEN 1 ELSE 0 END) AS IsDefault
    FROM dbo.PRM_SpecificationTests configured
    WHERE UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory,N''))))=UPPER(LTRIM(RTRIM(@Category)))
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.ApprovalStatus,N''))))=N'APPROVED'
      AND configured.IsActive=1
      AND NOT (ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913' AND (ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913' OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'))
" + ApprovedProfileEvidencePredicateSql + @"
      AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate<=CAST(GETDATE() AS date))
    GROUP BY LTRIM(RTRIM(configured.SpecificationNo))
),
Summary AS
(
    SELECT
        COUNT(1) AS ApprovedCount,
        SUM(IsDefault) AS DefaultCount,
        MIN(SpecificationNo) AS OnlyApproved,
        MIN(CASE WHEN IsDefault=1 THEN SpecificationNo END) AS DefaultApproved
    FROM ApprovedProfiles
)
SELECT CASE
         WHEN DefaultCount=1 THEN DefaultApproved
         WHEN ApprovedCount=1 THEN OnlyApproved
         ELSE N''
       END
FROM Summary;", connection, transaction);
            command.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = sampleCategory;
            command.Parameters.Add("@RequireIndependentApprover", SqlDbType.Bit).Value = AppConfig.IsProduction;
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        }

        private static void AssignCompatibleTests(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string specificationNo,
            string sampleCategory)
        {
            // Load the configured definition first, then complete any missing legacy rows.
            // Do not stop after the first two configured tests because older samples may carry
            // additional required objectionable-organism tests for the same specification.
            TryAssignFromCategory(connection, transaction, sampleId, specificationNo, sampleCategory);

            // Stability samples can use a finished-product definition when the same specification
            // number was historically maintained under the finished-product category.
            if (IsStabilityCategory(sampleCategory))
                TryAssignFromCategory(connection, transaction, sampleId, specificationNo, "Finished Product");

            // Legacy recovery is development-only and explicitly controlled by configuration.
            // Production requires an approved, effective specification definition.
            if (AppConfig.AllowLegacyPrmSpecificationFallback &&
                CountAssignedTests(connection, transaction, sampleId) == 0 &&
                CanCompleteHistoricalAssignment(connection, transaction, sampleId))
            {
                int sourceSampleId = FindHistoricalItemSourceSampleId(connection, transaction, sampleId);
                if (sourceSampleId > 0)
                {
                    AssignHistoricalTestsFromSource(connection, transaction, sampleId, sourceSampleId);
                    string legacyMessage =
                        "PRM sample " + sampleId.ToString(CultureInfo.InvariantCulture) +
                        " recovered test definitions from historical sample " +
                        sourceSampleId.ToString(CultureInfo.InvariantCulture) +
                        " under the development-only compatibility setting.";
                    ApplicationLogger.Warning(legacyMessage);
                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection, transaction, "PRM_Samples", sampleId,
                        "Legacy Specification Recovery", string.Empty,
                        sourceSampleId.ToString(CultureInfo.InvariantCulture),
                        legacyMessage, Login.CurrentUser,
                        "SpecificationNo", null, null, "PRM");
                }
            }
        }

        private static bool TryAssignFromCategory(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string specificationNo,
            string sourceCategory)
        {
            bool definitionFound = false;

            if (HasApprovedDefinition(connection, transaction, specificationNo, sourceCategory))
            {
                AssignApprovedTests(connection, transaction, sampleId, specificationNo, sourceCategory);
                return true;
            }

            if (!AppConfig.AllowLegacyPrmSpecificationFallback)
                return false;

            if (HasConfiguredDefinition(connection, transaction, specificationNo, sourceCategory))
            {
                AssignLatestConfiguredTests(connection, transaction, sampleId, specificationNo, sourceCategory);
                definitionFound = true;
            }

            if (HasHistoricalDefinition(connection, transaction, sampleId, specificationNo, sourceCategory))
            {
                AssignHistoricalSampleTests(connection, transaction, sampleId, specificationNo, sourceCategory);
                definitionFound = true;
            }

            if (definitionFound)
            {
                string message = "Legacy PRM test-definition fallback was used for specification " +
                    specificationNo + " and category " + sourceCategory + ".";
                ApplicationLogger.Warning(message);
                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "PRM_Samples", sampleId,
                    "Legacy Specification Fallback", string.Empty, specificationNo,
                    message, Login.CurrentUser,
                    "SpecificationNo", null, null, "PRM");
            }

            return definitionFound;
        }

        private static bool HasApprovedDefinition(
            SqlConnection connection,
            SqlTransaction transaction,
            string specificationNo,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.PRM_SpecificationTests configured
    WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory, N'')))) IN
          (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
      AND UPPER(LTRIM(RTRIM(configured.ApprovalStatus))) = N'APPROVED'
      AND configured.IsActive = 1
      AND NOT (ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913' AND (ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913' OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'))
" + ApprovedProfileEvidencePredicateSql + @"
      AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate <= CAST(GETDATE() AS DATE))
)
THEN 1 ELSE 0 END;", connection, transaction);

            AddDefinitionParameters(command, specificationNo, sampleCategory);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private static bool HasConfiguredDefinition(
            SqlConnection connection,
            SqlTransaction transaction,
            string specificationNo,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.PRM_SpecificationTests configured
    WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory, N'')))) IN
          (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
)
THEN 1 ELSE 0 END;", connection, transaction);

            AddDefinitionParameters(command, specificationNo, sampleCategory);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private static bool HasHistoricalDefinition(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string specificationNo,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT CASE WHEN EXISTS
(
    SELECT 1
    FROM dbo.PRM_Samples source
    WHERE source.SampleID <> @SampleID
      AND LTRIM(RTRIM(ISNULL(source.SpecificationNo, N''))) = @SpecificationNo
      AND UPPER(LTRIM(RTRIM(ISNULL(source.SampleCategory, N'')))) IN
          (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
      AND EXISTS
      (
          SELECT 1
          FROM dbo.PRM_SampleTests sourceTest
          WHERE sourceTest.SampleID = source.SampleID
      )
)
THEN 1 ELSE 0 END;", connection, transaction);

            AddAssignmentParameters(command, sampleId, specificationNo, sampleCategory);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private static void AssignApprovedTests(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string specificationNo,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
;WITH SampleContext AS
(
    SELECT
        CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(SampleCategory,N'')))) IN(N'RAW MATERIAL',N'RAW MATERIALS',N'RM')
             THEN LTRIM(RTRIM(ISNULL(MaterialCode,N'')))
             ELSE LTRIM(RTRIM(ISNULL(ProductCode,N''))) END AS ItemCode,
        LTRIM(RTRIM(ISNULL(ProductionStage,N''))) AS ProductionStage,
        SpecificationVersionNo
    FROM dbo.PRM_Samples WITH(UPDLOCK,HOLDLOCK)
    WHERE SampleID=@SampleID
),
ApprovedVersion AS
(
    SELECT COALESCE(MAX(context.SpecificationVersionNo),MAX(configured.VersionNo)) AS VersionNo
    FROM dbo.PRM_SpecificationTests configured
    CROSS JOIN SampleContext context
    WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory, N'')))) IN
          (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
      AND UPPER(LTRIM(RTRIM(configured.ApprovalStatus))) = N'APPROVED'
      AND configured.IsActive = 1
      AND NOT (ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913' AND (ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913' OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'))
" + ApprovedProfileEvidencePredicateSql + @"
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.ItemCode,N'')))) IN (UPPER(context.ItemCode),N'*')
      AND
      (
          UPPER(@Category1) NOT IN(N'PRODUCTION / IN-PROCESS',N'PRODUCTION/IN-PROCESS',N'PRODUCTION - IN-PROCESS',N'PRODUCTION IN-PROCESS',N'IN-PROCESS',N'IN PROCESS')
          OR UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N'')))) IN (UPPER(context.ProductionStage),N'*')
      )
      AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate <= CAST(GETDATE() AS DATE))
)
INSERT INTO dbo.PRM_SampleTests
(
    SampleID, TestCode, TestName, SpecificationText, Unit, ResultValue, ResultType,
    SpecificationLimit, Interpretation, Remarks, RequiredTest, MinimumElapsedHours, SortOrder, EnteredBy, EnteredDate,
    SourceSpecificationTestID, SpecificationVersionNo, SpecificationItemCode, SpecificationProductionStage
)
SELECT
    @SampleID, configured.TestCode, configured.TestName, configured.SpecificationText,
    configured.Unit, NULL, configured.ResultType, configured.SpecificationLimit,
    N'Not Tested', NULL, configured.RequiredTest, configured.MinimumElapsedHours, configured.SortOrder, NULL, NULL,
    configured.SpecificationTestID, configured.VersionNo, configured.ItemCode, configured.ProductionStage
FROM dbo.PRM_SpecificationTests configured
INNER JOIN ApprovedVersion approved ON approved.VersionNo = configured.VersionNo
CROSS JOIN SampleContext context
WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
  AND UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory, N'')))) IN
      (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
  AND UPPER(LTRIM(RTRIM(configured.ApprovalStatus))) = N'APPROVED'
  AND configured.IsActive = 1
  AND NOT (ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913' AND (ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913' OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'))
" + ApprovedProfileEvidencePredicateSql + @"
  AND UPPER(LTRIM(RTRIM(ISNULL(configured.ItemCode,N'')))) IN (UPPER(context.ItemCode),N'*')
  AND
  (
      UPPER(@Category1) NOT IN(N'PRODUCTION / IN-PROCESS',N'PRODUCTION/IN-PROCESS',N'PRODUCTION - IN-PROCESS',N'PRODUCTION IN-PROCESS',N'IN-PROCESS',N'IN PROCESS')
      OR UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N'')))) IN (UPPER(context.ProductionStage),N'*')
  )
  AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate <= CAST(GETDATE() AS DATE))
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.PRM_SampleTests assigned
      WHERE assigned.SampleID = @SampleID
        AND UPPER(LTRIM(RTRIM(ISNULL(assigned.TestCode, assigned.TestName)))) =
            UPPER(LTRIM(RTRIM(ISNULL(configured.TestCode, configured.TestName))))
  );", connection, transaction);

            AddAssignmentParameters(command, sampleId, specificationNo, sampleCategory);
            command.ExecuteNonQuery();
        }

        private static void AssignLatestConfiguredTests(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string specificationNo,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
;WITH LatestVersion AS
(
    SELECT MAX(configured.VersionNo) AS VersionNo
    FROM dbo.PRM_SpecificationTests configured
    WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory, N'')))) IN
          (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
)
INSERT INTO dbo.PRM_SampleTests
(
    SampleID, TestCode, TestName, SpecificationText, Unit, ResultValue, ResultType,
    SpecificationLimit, Interpretation, Remarks, RequiredTest, MinimumElapsedHours, SortOrder, EnteredBy, EnteredDate
)
SELECT
    @SampleID, configured.TestCode, configured.TestName, configured.SpecificationText,
    configured.Unit, NULL, configured.ResultType, configured.SpecificationLimit,
    N'Not Tested', NULL, ISNULL(configured.RequiredTest, 1), configured.MinimumElapsedHours, configured.SortOrder, NULL, NULL
FROM dbo.PRM_SpecificationTests configured
INNER JOIN LatestVersion latest ON latest.VersionNo = configured.VersionNo
WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
  AND UPPER(LTRIM(RTRIM(ISNULL(configured.SampleCategory, N'')))) IN
      (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.PRM_SampleTests assigned
      WHERE assigned.SampleID = @SampleID
        AND UPPER(LTRIM(RTRIM(ISNULL(assigned.TestCode, assigned.TestName)))) =
            UPPER(LTRIM(RTRIM(ISNULL(configured.TestCode, configured.TestName))))
  );", connection, transaction);

            AddAssignmentParameters(command, sampleId, specificationNo, sampleCategory);
            command.ExecuteNonQuery();
        }

        private static void AssignHistoricalSampleTests(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId,
            string specificationNo,
            string sampleCategory)
        {
            using SqlCommand command = new SqlCommand(@"
;WITH LatestSourceSample AS
(
    SELECT TOP (1) source.SampleID
    FROM dbo.PRM_Samples source
    WHERE source.SampleID <> @SampleID
      AND LTRIM(RTRIM(ISNULL(source.SpecificationNo, N''))) = @SpecificationNo
      AND UPPER(LTRIM(RTRIM(ISNULL(source.SampleCategory, N'')))) IN
          (UPPER(@Category1), UPPER(@Category2), UPPER(@Category3), UPPER(@Category4), UPPER(@Category5), UPPER(@Category6))
      AND EXISTS
      (
          SELECT 1
          FROM dbo.PRM_SampleTests sourceTest
          WHERE sourceTest.SampleID = source.SampleID
      )
    ORDER BY
        CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(source.SampleStatus, N'')))) IN
            (N'APPROVED', N'CERTIFICATE ISSUED', N'REPORT ISSUED') THEN 0 ELSE 1 END,
        ISNULL(source.ApprovedDate, source.CreatedDate) DESC,
        source.SampleID DESC
),
SourceTests AS
(
    SELECT
        test.TestCode,
        test.TestName,
        test.SpecificationText,
        test.Unit,
        test.ResultType,
        test.SpecificationLimit,
        ISNULL(test.RequiredTest, 1) AS RequiredTest,
        test.MinimumElapsedHours,
        ISNULL(test.SortOrder, test.SampleTestID) AS SortOrder,
        ROW_NUMBER() OVER
        (
            PARTITION BY UPPER(LTRIM(RTRIM(ISNULL(test.TestCode, test.TestName))))
            ORDER BY test.SampleTestID
        ) AS RowNumber
    FROM dbo.PRM_SampleTests test
    INNER JOIN LatestSourceSample source ON source.SampleID = test.SampleID
)
INSERT INTO dbo.PRM_SampleTests
(
    SampleID, TestCode, TestName, SpecificationText, Unit, ResultValue, ResultType,
    SpecificationLimit, Interpretation, Remarks, RequiredTest, MinimumElapsedHours, SortOrder, EnteredBy, EnteredDate
)
SELECT
    @SampleID, source.TestCode, source.TestName, source.SpecificationText,
    source.Unit, NULL, source.ResultType, source.SpecificationLimit,
    N'Not Tested', NULL, source.RequiredTest, source.MinimumElapsedHours, source.SortOrder, NULL, NULL
FROM SourceTests source
WHERE source.RowNumber = 1
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.PRM_SampleTests assigned
      WHERE assigned.SampleID = @SampleID
        AND UPPER(LTRIM(RTRIM(ISNULL(assigned.TestCode, assigned.TestName)))) =
            UPPER(LTRIM(RTRIM(ISNULL(source.TestCode, source.TestName))))
  );", connection, transaction);

            AddAssignmentParameters(command, sampleId, specificationNo, sampleCategory);
            command.ExecuteNonQuery();
        }

        private static bool CanCompleteHistoricalAssignment(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT
    LTRIM(RTRIM(ISNULL(sample.SampleStatus, N''))) AS SampleStatus,
    SUM(CASE WHEN NULLIF(LTRIM(RTRIM(ISNULL(test.ResultValue, N''))), N'') IS NOT NULL THEN 1 ELSE 0 END) AS EnteredResults
FROM dbo.PRM_Samples sample
LEFT JOIN dbo.PRM_SampleTests test ON test.SampleID = sample.SampleID
WHERE sample.SampleID = @SampleID
GROUP BY sample.SampleStatus;", connection, transaction);
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;

            using SqlDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return false;

            string status = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            int enteredResults = reader.IsDBNull(1)
                ? 0
                : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);

            return enteredResults == 0 &&
                   (status.Equals("Registered", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("In Progress", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Results Entered", StringComparison.OrdinalIgnoreCase));
        }

        private static int FindHistoricalItemSourceSampleId(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            string targetSpecification;
            string targetCategory;
            string targetProductCode;
            string targetProductName;
            string targetMaterialCode;
            string targetMaterialName;
            string targetTestsRequired;

            using (SqlCommand targetCommand = new SqlCommand(@"
SELECT
    LTRIM(RTRIM(ISNULL(SpecificationNo, N''))) AS SpecificationNo,
    LTRIM(RTRIM(ISNULL(SampleCategory, N''))) AS SampleCategory,
    LTRIM(RTRIM(ISNULL(ProductCode, N''))) AS ProductCode,
    LTRIM(RTRIM(ISNULL(ProductName, N''))) AS ProductName,
    LTRIM(RTRIM(ISNULL(MaterialCode, N''))) AS MaterialCode,
    LTRIM(RTRIM(ISNULL(MaterialName, N''))) AS MaterialName,
    LTRIM(RTRIM(ISNULL(TestsRequired, N''))) AS TestsRequired
FROM dbo.PRM_Samples
WHERE SampleID = @SampleID;", connection, transaction))
            {
                targetCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
                using SqlDataReader targetReader = targetCommand.ExecuteReader();
                if (!targetReader.Read())
                    return 0;

                targetSpecification = targetReader.GetString(0);
                targetCategory = targetReader.GetString(1);
                targetProductCode = targetReader.GetString(2);
                targetProductName = targetReader.GetString(3);
                targetMaterialCode = targetReader.GetString(4);
                targetMaterialName = targetReader.GetString(5);
                targetTestsRequired = targetReader.GetString(6);
            }

            int bestSampleId = 0;
            int bestIdentityScore = 0;
            bool bestSameSpecification = false;
            int bestTestCount = 0;
            int bestCategoryRank = int.MaxValue;
            int bestStatusRank = int.MaxValue;
            DateTime bestReferenceDate = DateTime.MinValue;

            using SqlCommand sourceCommand = new SqlCommand(@"
SELECT TOP (1000)
    source.SampleID,
    LTRIM(RTRIM(ISNULL(source.SpecificationNo, N''))) AS SpecificationNo,
    LTRIM(RTRIM(ISNULL(source.SampleCategory, N''))) AS SampleCategory,
    LTRIM(RTRIM(ISNULL(source.ProductCode, N''))) AS ProductCode,
    LTRIM(RTRIM(ISNULL(source.ProductName, N''))) AS ProductName,
    LTRIM(RTRIM(ISNULL(source.MaterialCode, N''))) AS MaterialCode,
    LTRIM(RTRIM(ISNULL(source.MaterialName, N''))) AS MaterialName,
    LTRIM(RTRIM(ISNULL(source.TestsRequired, N''))) AS TestsRequired,
    LTRIM(RTRIM(ISNULL(source.SampleStatus, N''))) AS SampleStatus,
    COALESCE(source.ApprovedDate, source.ModifiedDate, source.CreatedDate, CONVERT(DATETIME2(0), N'19000101')) AS ReferenceDate,
    testSummary.TestCount
FROM dbo.PRM_Samples source
CROSS APPLY
(
    SELECT COUNT(1) AS TestCount
    FROM dbo.PRM_SampleTests sourceTest
    WHERE sourceTest.SampleID = source.SampleID
      AND NULLIF(LTRIM(RTRIM(ISNULL(sourceTest.TestName, N''))), N'') IS NOT NULL
) testSummary
WHERE source.SampleID <> @SampleID
  AND testSummary.TestCount > 0
  AND UPPER(LTRIM(RTRIM(ISNULL(source.SampleStatus, N'')))) NOT IN
      (N'CANCELLED', N'CANCELED', N'REJECTED', N'VOID', N'VOIDED')
ORDER BY source.SampleID DESC;", connection, transaction);
            sourceCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;

            using SqlDataReader sourceReader = sourceCommand.ExecuteReader();
            while (sourceReader.Read())
            {
                int sourceSampleId = sourceReader.GetInt32(0);
                string sourceSpecification = sourceReader.GetString(1);
                string sourceCategory = sourceReader.GetString(2);
                string sourceProductCode = sourceReader.GetString(3);
                string sourceProductName = sourceReader.GetString(4);
                string sourceMaterialCode = sourceReader.GetString(5);
                string sourceMaterialName = sourceReader.GetString(6);
                string sourceTestsRequired = sourceReader.GetString(7);
                string sourceStatus = sourceReader.GetString(8);
                DateTime sourceReferenceDate = sourceReader.GetDateTime(9);
                int sourceTestCount = Convert.ToInt32(sourceReader.GetValue(10), CultureInfo.InvariantCulture);

                int categoryRank = GetHistoricalCategoryRank(targetCategory, sourceCategory);
                if (categoryRank < 0)
                    continue;

                int identityScore = GetHistoricalIdentityScore(
                    targetCategory,
                    targetProductCode,
                    targetProductName,
                    targetMaterialCode,
                    targetMaterialName,
                    sourceProductCode,
                    sourceProductName,
                    sourceMaterialCode,
                    sourceMaterialName);
                if (identityScore <= 0)
                    continue;

                string targetRequiredKey = NormalizeCategoryKey(targetTestsRequired);
                string sourceRequiredKey = NormalizeCategoryKey(sourceTestsRequired);
                if (targetRequiredKey.Length > 0 &&
                    targetRequiredKey.Equals(sourceRequiredKey, StringComparison.OrdinalIgnoreCase))
                {
                    identityScore += 5;
                }

                bool sameSpecification =
                    !string.IsNullOrWhiteSpace(targetSpecification) &&
                    targetSpecification.Equals(sourceSpecification, StringComparison.OrdinalIgnoreCase);
                int statusRank = GetHistoricalStatusRank(sourceStatus);

                if (IsBetterHistoricalCandidate(
                    identityScore,
                    sameSpecification,
                    sourceTestCount,
                    categoryRank,
                    statusRank,
                    sourceReferenceDate,
                    sourceSampleId,
                    bestIdentityScore,
                    bestSameSpecification,
                    bestTestCount,
                    bestCategoryRank,
                    bestStatusRank,
                    bestReferenceDate,
                    bestSampleId))
                {
                    bestSampleId = sourceSampleId;
                    bestIdentityScore = identityScore;
                    bestSameSpecification = sameSpecification;
                    bestTestCount = sourceTestCount;
                    bestCategoryRank = categoryRank;
                    bestStatusRank = statusRank;
                    bestReferenceDate = sourceReferenceDate;
                }
            }

            return bestSampleId;
        }

        private static int GetHistoricalCategoryRank(string targetCategory, string sourceCategory)
        {
            string targetGroup = GetCategoryGroup(targetCategory);
            string sourceGroup = GetCategoryGroup(sourceCategory);

            if (targetGroup.Equals(sourceGroup, StringComparison.OrdinalIgnoreCase))
                return 0;

            if (targetGroup.Equals("STABILITY", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceGroup.Equals("FINISHED PRODUCT", StringComparison.OrdinalIgnoreCase))
                    return 1;
                if (sourceGroup.Equals("PRODUCTION / IN-PROCESS", StringComparison.OrdinalIgnoreCase))
                    return 2;
            }

            return -1;
        }

        private static int GetHistoricalIdentityScore(
            string targetCategory,
            string targetProductCode,
            string targetProductName,
            string targetMaterialCode,
            string targetMaterialName,
            string sourceProductCode,
            string sourceProductName,
            string sourceMaterialCode,
            string sourceMaterialName)
        {
            bool rawMaterial = GetCategoryGroup(targetCategory)
                .Equals("RAW MATERIAL", StringComparison.OrdinalIgnoreCase);

            string targetCode = NormalizeCategoryKey(rawMaterial ? targetMaterialCode : targetProductCode);
            string sourceCode = NormalizeCategoryKey(rawMaterial ? sourceMaterialCode : sourceProductCode);
            if (targetCode.Length > 0 && sourceCode.Length > 0 &&
                targetCode.Equals(sourceCode, StringComparison.OrdinalIgnoreCase))
            {
                return 120;
            }

            string targetName = rawMaterial ? targetMaterialName : targetProductName;
            string sourceName = rawMaterial ? sourceMaterialName : sourceProductName;
            string targetFullKey = NormalizeCategoryKey(targetName);
            string sourceFullKey = NormalizeCategoryKey(sourceName);
            if (targetFullKey.Length == 0 || sourceFullKey.Length == 0)
                return 0;

            if (targetFullKey.Equals(sourceFullKey, StringComparison.OrdinalIgnoreCase))
                return 110;

            string targetBaseKey = NormalizeItemBaseName(targetName);
            string sourceBaseKey = NormalizeItemBaseName(sourceName);
            if (targetBaseKey.Length == 0 || sourceBaseKey.Length == 0)
                return 0;

            if (targetBaseKey.Equals(sourceBaseKey, StringComparison.OrdinalIgnoreCase))
                return 105;

            int shorterLength = Math.Min(targetBaseKey.Length, sourceBaseKey.Length);
            if (shorterLength >= 5 &&
                (targetBaseKey.StartsWith(sourceBaseKey, StringComparison.OrdinalIgnoreCase) ||
                 sourceBaseKey.StartsWith(targetBaseKey, StringComparison.OrdinalIgnoreCase)))
            {
                return 95;
            }

            double similarity = CalculateIdentitySimilarity(targetBaseKey, sourceBaseKey);
            if (shorterLength >= 5 && similarity >= 0.84d)
                return 90;
            if (shorterLength >= 7 && similarity >= 0.72d)
                return 80;

            return 0;
        }

        private static string NormalizeItemBaseName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder normalized = new StringBuilder(value.Length);
            StringBuilder token = new StringBuilder();

            void AppendToken()
            {
                if (token.Length == 0)
                    return;

                string tokenValue = token.ToString().ToUpperInvariant();
                token.Clear();
                if (!IsIdentityNoiseToken(tokenValue))
                    normalized.Append(tokenValue);
            }

            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character))
                    token.Append(character);
                else
                    AppendToken();
            }

            AppendToken();
            return normalized.ToString();
        }

        private static bool IsIdentityNoiseToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return true;

            bool hasDigit = false;
            bool hasLetter = false;
            foreach (char character in token)
            {
                hasDigit |= char.IsDigit(character);
                hasLetter |= char.IsLetter(character);
            }

            if (hasDigit && !hasLetter)
                return true;

            if (hasDigit &&
                (token.EndsWith("MG", StringComparison.OrdinalIgnoreCase) ||
                 token.EndsWith("MCG", StringComparison.OrdinalIgnoreCase) ||
                 token.EndsWith("G", StringComparison.OrdinalIgnoreCase) ||
                 token.EndsWith("KG", StringComparison.OrdinalIgnoreCase) ||
                 token.EndsWith("ML", StringComparison.OrdinalIgnoreCase) ||
                 token.EndsWith("L", StringComparison.OrdinalIgnoreCase) ||
                 token.EndsWith("IU", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return token is
                "TABLET" or "TABLETS" or "TAB" or "TABS" or
                "CAPSULE" or "CAPSULES" or "CAP" or "CAPS" or
                "SYRUP" or "SUSPENSION" or "SOLUTION" or
                "INJECTION" or "INJECTABLE" or "VIAL" or "AMPOULE" or "AMP" or
                "CREAM" or "OINTMENT" or "GEL" or "LOTION" or
                "SACHET" or "SACHETS" or "POWDER" or "GRANULES";
        }

        private static double CalculateIdentitySimilarity(string left, string right)
        {
            if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
                return 1d;
            if (left.Length == 0 || right.Length == 0)
                return 0d;

            int[] previous = new int[right.Length + 1];
            int[] current = new int[right.Length + 1];
            for (int column = 0; column <= right.Length; column++)
                previous[column] = column;

            for (int row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (int column = 1; column <= right.Length; column++)
                {
                    int substitutionCost = char.ToUpperInvariant(left[row - 1]) == char.ToUpperInvariant(right[column - 1]) ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + substitutionCost);
                }

                (previous, current) = (current, previous);
            }

            int distance = previous[right.Length];
            return 1d - (distance / (double)Math.Max(left.Length, right.Length));
        }

        private static int GetHistoricalStatusRank(string status)
        {
            string key = NormalizeCategoryKey(status);
            if (key is "CERTIFICATEISSUED" or "REPORTISSUED" or "APPROVED")
                return 0;
            if (key == "REVIEWED")
                return 1;
            if (key == "RESULTSENTERED")
                return 2;
            if (key == "INPROGRESS")
                return 3;
            if (key == "REGISTERED")
                return 4;

            return 5;
        }

        private static bool IsBetterHistoricalCandidate(
            int identityScore,
            bool sameSpecification,
            int testCount,
            int categoryRank,
            int statusRank,
            DateTime referenceDate,
            int sampleId,
            int bestIdentityScore,
            bool bestSameSpecification,
            int bestTestCount,
            int bestCategoryRank,
            int bestStatusRank,
            DateTime bestReferenceDate,
            int bestSampleId)
        {
            if (identityScore != bestIdentityScore)
                return identityScore > bestIdentityScore;
            if (sameSpecification != bestSameSpecification)
                return sameSpecification;
            if (testCount != bestTestCount)
                return testCount > bestTestCount;
            if (categoryRank != bestCategoryRank)
                return categoryRank < bestCategoryRank;
            if (statusRank != bestStatusRank)
                return statusRank < bestStatusRank;
            if (referenceDate != bestReferenceDate)
                return referenceDate > bestReferenceDate;

            return sampleId > bestSampleId;
        }

        private static void AssignHistoricalTestsFromSource(
            SqlConnection connection,
            SqlTransaction transaction,
            int targetSampleId,
            int sourceSampleId)
        {
            using SqlCommand command = new SqlCommand(@"
;WITH SourceTests AS
(
    SELECT
        test.TestCode,
        test.TestName,
        test.SpecificationText,
        test.Unit,
        test.ResultType,
        test.SpecificationLimit,
        ISNULL(test.RequiredTest, 1) AS RequiredTest,
        test.MinimumElapsedHours,
        ISNULL(test.SortOrder, test.SampleTestID) AS SortOrder,
        ROW_NUMBER() OVER
        (
            PARTITION BY UPPER(LTRIM(RTRIM(COALESCE(NULLIF(test.TestCode, N''), test.TestName))))
            ORDER BY test.SampleTestID
        ) AS RowNumber
    FROM dbo.PRM_SampleTests test
    WHERE test.SampleID = @SourceSampleID
)
INSERT INTO dbo.PRM_SampleTests
(
    SampleID, TestCode, TestName, SpecificationText, Unit, ResultValue, ResultType,
    SpecificationLimit, Interpretation, Remarks, RequiredTest, MinimumElapsedHours, SortOrder, EnteredBy, EnteredDate
)
SELECT
    @TargetSampleID,
    source.TestCode,
    source.TestName,
    source.SpecificationText,
    source.Unit,
    NULL,
    source.ResultType,
    source.SpecificationLimit,
    N'Not Tested',
    NULL,
    source.RequiredTest,
    source.MinimumElapsedHours,
    source.SortOrder,
    NULL,
    NULL
FROM SourceTests source
WHERE source.RowNumber = 1
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.PRM_SampleTests assigned
      WHERE assigned.SampleID = @TargetSampleID
        AND UPPER(LTRIM(RTRIM(COALESCE(NULLIF(assigned.TestCode, N''), assigned.TestName)))) =
            UPPER(LTRIM(RTRIM(COALESCE(NULLIF(source.TestCode, N''), source.TestName))))
  );", connection, transaction);

            command.Parameters.Add("@TargetSampleID", SqlDbType.Int).Value = targetSampleId;
            command.Parameters.Add("@SourceSampleID", SqlDbType.Int).Value = sourceSampleId;
            command.ExecuteNonQuery();
        }

        private static string GetCategoryGroup(string sampleCategory)
        {
            string key = NormalizeCategoryKey(sampleCategory);

            if (key == "STABILITY" || key == "STABILITYSTUDY" || key == "STABILITYSAMPLE" || key == "STABILITYSAMPLES")
                return "STABILITY";
            if (key == "FINISHEDPRODUCT" || key == "FINISHEDPRODUCTAFTERPACKAGING" || key == "FP")
                return "FINISHED PRODUCT";
            if (key == "PRODUCTIONINPROCESS" || key == "PRODUCTION" || key == "INPROCESS")
                return "PRODUCTION / IN-PROCESS";
            if (key == "RAWMATERIAL" || key == "RAWMATERIALS" || key == "RM")
                return "RAW MATERIAL";

            return key;
        }

        private static void AddAssignmentParameters(
            SqlCommand command,
            int sampleId,
            string specificationNo,
            string sampleCategory)
        {
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            AddDefinitionParameters(command, specificationNo, sampleCategory);
        }

        private static void AddDefinitionParameters(
            SqlCommand command,
            string specificationNo,
            string sampleCategory)
        {
            command.Parameters.Add("@SpecificationNo", SqlDbType.NVarChar, 240).Value = specificationNo;
            command.Parameters.Add("@RequireIndependentApprover", SqlDbType.Bit).Value = AppConfig.IsProduction;

            string[] aliases = GetCategoryAliases(sampleCategory);
            for (int index = 0; index < CategoryAliasCount; index++)
            {
                command.Parameters.Add("@Category" + (index + 1).ToString(CultureInfo.InvariantCulture), SqlDbType.NVarChar, 80)
                    .Value = aliases[index];
            }
        }

        private static string[] GetCategoryAliases(string sampleCategory)
        {
            string key = NormalizeCategoryKey(sampleCategory);

            if (key == "STABILITY" || key == "STABILITYSTUDY" || key == "STABILITYSAMPLE" || key == "STABILITYSAMPLES")
            {
                return new[]
                {
                    "Stability",
                    "Stability Study",
                    "Stability Sample",
                    "Stability Samples",
                    "__UNUSED_STABILITY_1__",
                    "__UNUSED_STABILITY_2__"
                };
            }

            if (key == "FINISHEDPRODUCT" || key == "FINISHEDPRODUCTAFTERPACKAGING" || key == "FP")
            {
                return new[]
                {
                    "Finished Product",
                    "Finished Product - After Packaging",
                    "Finished Product After Packaging",
                    "FP",
                    "__UNUSED_FINISHED_1__",
                    "__UNUSED_FINISHED_2__"
                };
            }

            if (key == "PRODUCTIONINPROCESS" || key == "PRODUCTION" || key == "INPROCESS")
            {
                return new[]
                {
                    "Production / In-Process",
                    "Production/In-Process",
                    "Production - In-Process",
                    "Production In-Process",
                    "In-Process",
                    "In Process"
                };
            }

            if (key == "RAWMATERIAL" || key == "RAWMATERIALS" || key == "RM")
            {
                return new[]
                {
                    "Raw Material",
                    "Raw Materials",
                    "Raw Material Sample",
                    "Raw Material Samples",
                    "RM",
                    "__UNUSED_RAW_1__"
                };
            }

            string exact = (sampleCategory ?? string.Empty).Trim();
            return new[]
            {
                exact,
                "__UNUSED_GENERIC_1__",
                "__UNUSED_GENERIC_2__",
                "__UNUSED_GENERIC_3__",
                "__UNUSED_GENERIC_4__",
                "__UNUSED_GENERIC_5__"
            };
        }

        private static bool IsStabilityCategory(string sampleCategory)
        {
            string key = NormalizeCategoryKey(sampleCategory);
            return key == "STABILITY" || key == "STABILITYSTUDY" || key == "STABILITYSAMPLE" || key == "STABILITYSAMPLES";
        }

        private static string NormalizeCategoryKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            char[] buffer = new char[value.Length];
            int length = 0;
            foreach (char character in value)
            {
                if (!char.IsLetterOrDigit(character))
                    continue;

                buffer[length++] = char.ToUpperInvariant(character);
            }

            return new string(buffer, 0, length);
        }

        private static int CountAssignedTests(SqlConnection connection, SqlTransaction transaction, int sampleId)
        {
            using SqlCommand countCommand = new SqlCommand(
                "SELECT COUNT(1) FROM dbo.PRM_SampleTests WHERE SampleID = @SampleID;",
                connection,
                transaction);
            countCommand.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            return Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
