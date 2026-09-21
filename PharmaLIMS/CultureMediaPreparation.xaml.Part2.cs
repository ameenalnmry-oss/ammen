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
        private static void EnsureQualificationTestTimingEvidenceInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int qualificationId,
            string actionName)
        {
            object? invalidValue = ExecuteScalarInTransaction(conn, tx, @"
SELECT
    (
        SELECT COUNT(1)
        FROM dbo.MediaQualificationTests test WITH(UPDLOCK,HOLDLOCK)
        INNER JOIN dbo.MediaQualifications qualification WITH(UPDLOCK,HOLDLOCK)
            ON qualification.MediaQualificationID=test.MediaQualificationID
        LEFT JOIN dbo.MediaQualificationRequirementSnapshots snapshot WITH(UPDLOCK,HOLDLOCK)
            ON snapshot.MediaQualificationID=test.MediaQualificationID
           AND UPPER(LTRIM(RTRIM(snapshot.TestName)))=UPPER(LTRIM(RTRIM(test.TestName)))
        WHERE test.MediaQualificationID=@MediaQualificationID
          AND
          (
              snapshot.SnapshotID IS NULL
              OR qualification.QualificationStartedAt IS NULL
              OR test.CreatedDate < DATEADD(MINUTE,
                    CONVERT(INT,CEILING(CONVERT(DECIMAL(18,4),snapshot.MinimumIncubationHoursSnapshot) * 60.0)),
                    qualification.QualificationStartedAt)
          )
    )
    +
    (
        SELECT COUNT(1)
        FROM dbo.MediaQualificationRequirementSnapshots snapshot WITH(UPDLOCK,HOLDLOCK)
        WHERE snapshot.MediaQualificationID=@MediaQualificationID
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.MediaQualificationTests test WITH(UPDLOCK,HOLDLOCK)
              WHERE test.MediaQualificationID=snapshot.MediaQualificationID
                AND UPPER(LTRIM(RTRIM(test.TestName)))=UPPER(LTRIM(RTRIM(snapshot.TestName)))
          )
    );",
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId });

            int invalidCount = invalidValue == null || invalidValue == DBNull.Value
                ? int.MaxValue
                : Convert.ToInt32(invalidValue, CultureInfo.InvariantCulture);
            if (invalidCount <= 0)
                return;

            if (AppConfig.AllowEarlyMicrobiologyResults)
                return;

            throw new InvalidOperationException(
                actionName + " is blocked because one or more Culture Media qualification tests are missing their frozen requirement timing evidence, were recorded before their individual frozen eligibility time, or a required frozen test has no result row.");
        }

        private static SqlParameter CreateDecimalParameter(string name, decimal value)
        {
            var parameter = new SqlParameter(name, SqlDbType.Decimal)
            {
                Precision = 9,
                Scale = 2,
                Value = value
            };
            return parameter;
        }

        private static decimal? RowNullableDecimal(DataRow row, string columnName)
        {
            if (!row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value)
                return null;
            return Convert.ToDecimal(row[columnName], CultureInfo.InvariantCulture);
        }

        private bool CanSynchronizeReleasedStatusFromSelectedReport(int mediaLotId, out DateTime? qualificationDate)
        {
            qualificationDate = null;
            if (_selectedReleaseReportId <= 0 || mediaLotId <= 0)
                return false;

            DataTable report = _repository.Load(CultureMediaQuery.LoadSelectedReleaseReportSummary,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = _selectedReleaseReportId });

            if (report.Rows.Count != 1)
                return false;

            DataRow reportRow = report.Rows[0];
            if (RowInt(reportRow, "MediaLotID") != mediaLotId ||
                !RowString(reportRow, "OverallResult").Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(RowString(reportRow, "ReviewedBy")))
            {
                return false;
            }

            DataTable tests = _repository.Load(CultureMediaQuery.LoadSelectedReleaseReportTests,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = _selectedReleaseReportId });

            if (!HasCompletePassingReleaseTests(tests, mediaLotId))
                return false;

            qualificationDate = RowDate(reportRow, "QualificationDate");
            return true;
        }

        private static bool IsReleaseTestRowComplete(DataRow row, string requiredTest, decimal? minimumRecovery, decimal? maximumRecovery)
        {
            string organismOrCheck = RowString(row, "OrganismName");

            if (string.IsNullOrWhiteSpace(organismOrCheck))
                return false;

            if (requiredTest.Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase))
            {
                if (organismOrCheck.Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase) ||
                    organismOrCheck.Equals("GPT", StringComparison.OrdinalIgnoreCase) ||
                    organismOrCheck.Equals("Organism", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!decimal.TryParse(RowString(row, "RecoveryPercent"), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal recoveryPercent))
                    return false;
                if (minimumRecovery.HasValue && recoveryPercent < minimumRecovery.Value)
                    return false;
                if (maximumRecovery.HasValue && recoveryPercent > maximumRecovery.Value)
                    return false;

                return !string.IsNullOrWhiteSpace(RowString(row, "ATCCNumber")) &&
                       !string.IsNullOrWhiteSpace(RowString(row, "InoculumLevel")) &&
                       !string.IsNullOrWhiteSpace(RowString(row, "ControlCount")) &&
                       !string.IsNullOrWhiteSpace(RowString(row, "TestCount")) &&
                       !string.IsNullOrWhiteSpace(RowString(row, "IncubationConditions"));
            }

            if (requiredTest.Equals("pH Check", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrWhiteSpace(RowString(row, "ActualResult")) ||
                       !string.IsNullOrWhiteSpace(RowString(row, "InoculumLevel"));

            return !string.IsNullOrWhiteSpace(RowString(row, "ExpectedResult")) &&
                   !string.IsNullOrWhiteSpace(RowString(row, "ActualResult"));
        }

        private void UpdateReleaseNextAction()
        {
            if (TxtReleaseNextAction == null)
                return;

            if (_selectedLotId <= 0)
            {
                TxtReleaseNextAction.Text = "Select a media lot to start release qualification.";
                if (TxtReleaseChecklist != null)
                    TxtReleaseChecklist.Text = "Release Gate Checklist will appear after selecting a media lot.";
                return;
            }

            var missing = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(TxtRelMediaName.Text))
                missing.Add("load selected lot");
            if (!DpReleaseDate.SelectedDate.HasValue)
                missing.Add("test date");
            if (string.IsNullOrWhiteSpace(TxtReleasePerformedBy.Text))
                missing.Add("performed by");
            if (string.IsNullOrWhiteSpace(TxtReleaseArNo.Text))
                missing.Add("AR No.");
            if (string.IsNullOrWhiteSpace(TxtReleaseBottleNo.Text))
                missing.Add("bottle / pack identity");
            if (string.IsNullOrWhiteSpace(TxtReleaseMediaPh.Text))
                missing.Add("pH of media");

            if (ComboText(CmbOverallResult).Equals("Pass", StringComparison.OrdinalIgnoreCase) && !HasCompletePassingReleaseTests())
                missing.Add("five complete passing SOP checks with GPT organism details");

            TxtReleaseNextAction.Text = missing.Count == 0
                ? "Ready: save release report, then print Released Label after status is Released."
                : "Missing before release: " + string.Join(", ", missing) + ".";

            if (TxtReleaseChecklist != null)
            {
                TxtReleaseChecklist.Text =
                    BuildChecklistLine("Media lot loaded", !string.IsNullOrWhiteSpace(TxtRelMediaName.Text)) + Environment.NewLine +
                    BuildChecklistLine("Test date recorded", DpReleaseDate.SelectedDate.HasValue) + Environment.NewLine +
                    BuildChecklistLine("Performed by recorded", !string.IsNullOrWhiteSpace(TxtReleasePerformedBy.Text)) + Environment.NewLine +
                    BuildChecklistLine("Independent review pending after save", _selectedReleaseReportId <= 0 || string.IsNullOrWhiteSpace(TxtReleaseReviewedBy.Text)) + Environment.NewLine +
                    BuildChecklistLine("A5 AR No. recorded", !string.IsNullOrWhiteSpace(TxtReleaseArNo.Text)) + Environment.NewLine +
                    BuildChecklistLine("A5 bottle / pack identity recorded", !string.IsNullOrWhiteSpace(TxtReleaseBottleNo.Text)) + Environment.NewLine +
                    BuildChecklistLine("A5 pH recorded", !string.IsNullOrWhiteSpace(TxtReleaseMediaPh.Text)) + Environment.NewLine +
                    BuildChecklistLine("A5/A8 conclusion satisfactory", ComboText(CmbReleaseConclusion).Equals("Satisfactory", StringComparison.OrdinalIgnoreCase)) + Environment.NewLine +
                    BuildChecklistLine("Five SOP checks passing with GPT organism details", !ComboText(CmbOverallResult).Equals("Pass", StringComparison.OrdinalIgnoreCase) || HasCompletePassingReleaseTests());
            }
        }

        private static string BuildChecklistLine(string label, bool isOk)
            => (isOk ? "OK - " : "MISSING - ") + label;

        private void UpdatePreparationNextAction()
        {
            if (TxtPreparationNextAction == null)
                return;

            var missing = new System.Collections.Generic.List<string>();
            if (!TryResolveSelectedMediaLot(out _, out _, out _))
                missing.Add("released stored media batch");
            if (!DpPreparationDate.SelectedDate.HasValue)
                missing.Add("preparation date");
            if (string.IsNullOrWhiteSpace(TxtPreparedBy.Text))
                missing.Add("prepared by");
            if (string.IsNullOrWhiteSpace(TxtQuantityPrepared.Text))
                missing.Add("quantity prepared");
            if (!TryParseGramQuantity(TxtQuantityWeighedG.Text, out _))
                missing.Add("quantity weighed in grams");
            if (string.IsNullOrWhiteSpace(TxtPreparationLoadNo.Text))
                missing.Add("load no.");
            if (string.IsNullOrWhiteSpace(TxtPreparationMpmNo.Text))
                missing.Add("MPM no.");
            if (!NormalizeSterilityReview(ComboText(CmbPreparationSterilityReview)).Equals("Passed", StringComparison.OrdinalIgnoreCase))
                missing.Add("sterility review passed");
            if (!ComboText(CmbPreparationVisualConclusion).Equals("Satisfactory", StringComparison.OrdinalIgnoreCase))
                missing.Add("A7 visual check satisfactory");

            TxtPreparationNextAction.Text = missing.Count == 0
                ? "Ready: save preparation, release it, then print prepared media label."
                : "Missing before preparation release: " + string.Join(", ", missing) + ".";
        }

        private void InsertReleaseOrganism(int qualificationId, DataRow row)
        {

            _repository.RunCommand(CultureMediaCommand.InsertReleaseOrganism,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId },
                new SqlParameter("@TestName", SqlDbType.NVarChar, 100) { Value = DbValue(RowString(row, "TestName")) },
                new SqlParameter("@OrganismName", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "OrganismName")) },
                new SqlParameter("@ATCCNumber", SqlDbType.NVarChar, 80) { Value = DbValue(RowString(row, "ATCCNumber")) },
                new SqlParameter("@InoculumLevel", SqlDbType.NVarChar, 80) { Value = DbValue(RowString(row, "InoculumLevel")) },
                new SqlParameter("@ExpectedResult", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "ExpectedResult")) },
                new SqlParameter("@ActualResult", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "ActualResult")) },
                new SqlParameter("@ControlCount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = DbDecimal(RowString(row, "ControlCount")) },
                new SqlParameter("@TestCount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = DbDecimal(RowString(row, "TestCount")) },
                new SqlParameter("@RecoveryPercent", SqlDbType.Decimal) { Precision = 9, Scale = 2, Value = DbDecimal(RowString(row, "RecoveryPercent")) },
                new SqlParameter("@IncubationConditions", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "IncubationConditions")) },
                new SqlParameter("@TestResult", SqlDbType.NVarChar, 30) { Value = DbValue(RowString(row, "TestResult")) },
                new SqlParameter("@Remarks", SqlDbType.NVarChar) { Value = DbValue(RowString(row, "Remarks")) });
        }

        private static object? ExecuteScalarInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string sql,
            params SqlParameter[] parameters)
        {
            using var command = new SqlCommand(sql, conn, tx)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            if (parameters?.Length > 0)
                command.Parameters.AddRange(parameters);
            return command.ExecuteScalar();
        }

        private static void InsertReleaseOrganismInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            int qualificationId,
            DataRow row)
        {
            const string sql = @"
INSERT INTO dbo.MediaQualificationTests
(MediaQualificationID, TestName, OrganismName, ATCCNumber, InoculumLevel, ExpectedResult, ActualResult, ControlCount, TestCount, RecoveryPercent, IncubationConditions, TestResult, Remarks)
VALUES
(@MediaQualificationID, @TestName, @OrganismName, @ATCCNumber, @InoculumLevel, @ExpectedResult, @ActualResult, @ControlCount, @TestCount, @RecoveryPercent, @IncubationConditions, @TestResult, @Remarks);";

            DatabaseHelper.ExecuteNonQueryWithTransaction(sql,
                new[]
                {
                    new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = qualificationId },
                    new SqlParameter("@TestName", SqlDbType.NVarChar, 100) { Value = DbValue(RowString(row, "TestName")) },
                    new SqlParameter("@OrganismName", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "OrganismName")) },
                    new SqlParameter("@ATCCNumber", SqlDbType.NVarChar, 80) { Value = DbValue(RowString(row, "ATCCNumber")) },
                    new SqlParameter("@InoculumLevel", SqlDbType.NVarChar, 80) { Value = DbValue(RowString(row, "InoculumLevel")) },
                    new SqlParameter("@ExpectedResult", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "ExpectedResult")) },
                    new SqlParameter("@ActualResult", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "ActualResult")) },
                    new SqlParameter("@ControlCount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = DbDecimal(RowString(row, "ControlCount")) },
                    new SqlParameter("@TestCount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = DbDecimal(RowString(row, "TestCount")) },
                    new SqlParameter("@RecoveryPercent", SqlDbType.Decimal) { Precision = 9, Scale = 2, Value = DbDecimal(RowString(row, "RecoveryPercent")) },
                    new SqlParameter("@IncubationConditions", SqlDbType.NVarChar, 200) { Value = DbValue(RowString(row, "IncubationConditions")) },
                    new SqlParameter("@TestResult", SqlDbType.NVarChar, 30) { Value = DbValue(RowString(row, "TestResult")) },
                    new SqlParameter("@Remarks", SqlDbType.NVarChar, -1) { Value = DbValue(RowString(row, "Remarks")) }
                }, conn, tx);
        }

        private void SaveReleaseSopFieldsInTransaction(SqlConnection conn, SqlTransaction tx, int qualificationId)
        {
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A5", "ArNo", TxtReleaseArNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A5", "BottleNoOrPackIdentity", TxtReleaseBottleNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A5", "PhOfMedia", TxtReleaseMediaPh.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A5", "PhAdjustment", ComboText(CmbReleasePhAdjustment));
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A5", "BacterialDilutionArNo", TxtReleaseBacterialDilutionArNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A5", "FungalDilutionArNo", TxtReleaseFungalDilutionArNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A8", "AnaerobicJarNo", TxtReleaseAnaerobicJarNo.Text);
            SaveSopFieldInTransaction(conn, tx, "MediaQualification", qualificationId, "1035-L-0005/A8", "Conclusion", ComboText(CmbReleaseConclusion));
        }

        private static DateTime ReadAuthoritativeUtcInTransaction(SqlConnection conn, SqlTransaction tx)
        {
            using SqlCommand command = new SqlCommand("SELECT SYSUTCDATETIME();", conn, tx)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };

            object? value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value)
                throw new InvalidOperationException("SQL Server did not return the authoritative UTC timestamp.");

            DateTime databaseUtc = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
            return DateTime.SpecifyKind(databaseUtc, DateTimeKind.Utc);
        }

        private void SaveSopFieldInTransaction(
            SqlConnection conn,
            SqlTransaction tx,
            string entityType,
            int entityId,
            string annexureCode,
            string fieldName,
            string value)
        {
            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
MERGE dbo.CultureMediaSopFields WITH (HOLDLOCK) AS target
USING (SELECT @EntityType AS EntityType, @EntityID AS EntityID, @AnnexureCode AS AnnexureCode, @FieldName AS FieldName) AS source
ON target.EntityType = source.EntityType
   AND target.EntityID = source.EntityID
   AND target.AnnexureCode = source.AnnexureCode
   AND target.FieldName = source.FieldName
WHEN MATCHED THEN
    UPDATE SET FieldValue = @FieldValue, UpdatedBy = @UserName, UpdatedAt = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (EntityType, EntityID, AnnexureCode, FieldName, FieldValue, CreatedBy)
    VALUES (@EntityType, @EntityID, @AnnexureCode, @FieldName, @FieldValue, @UserName);",
                new[]
                {
                    new SqlParameter("@EntityType", SqlDbType.NVarChar, 50) { Value = entityType },
                    new SqlParameter("@EntityID", SqlDbType.Int) { Value = entityId },
                    new SqlParameter("@AnnexureCode", SqlDbType.NVarChar, 50) { Value = annexureCode },
                    new SqlParameter("@FieldName", SqlDbType.NVarChar, 120) { Value = fieldName },
                    new SqlParameter("@FieldValue", SqlDbType.NVarChar, -1) { Value = DbValue(value) },
                    new SqlParameter("@UserName", SqlDbType.NVarChar, 100) { Value = _currentUser }
                }, conn, tx);
        }

        private void BtnAddOrganism_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaEntryAccess()) return;
            string testType = ComboText(CmbReleaseTestType).Trim();
            string organism = ComboText(CmbOrganism).Trim();
            if (string.IsNullOrWhiteSpace(organism) && !testType.Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase))
                organism = testType;

            if (string.IsNullOrWhiteSpace(organism))
            {
                MessageBox.Show("Enter or select organism name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool isGrowthPromotion = IsGrowthPromotionTest(testType);
            string atccNumber = isGrowthPromotion ? TxtATCC.Text.Trim() : string.Empty;
            string inoculumOrCondition = TxtInoculum.Text.Trim();
            string controlCount = isGrowthPromotion ? TxtControlCount.Text.Trim() : string.Empty;
            string testCount = isGrowthPromotion ? TxtTestCount.Text.Trim() : string.Empty;
            string recoveryPercent = isGrowthPromotion ? CalculateRecoveryPercent(controlCount, testCount) : string.Empty;

            if (isGrowthPromotion &&
                (string.IsNullOrWhiteSpace(atccNumber) ||
                 string.IsNullOrWhiteSpace(inoculumOrCondition) ||
                 string.IsNullOrWhiteSpace(controlCount) ||
                 string.IsNullOrWhiteSpace(testCount) ||
                 string.IsNullOrWhiteSpace(recoveryPercent)))
            {
                MessageBox.Show("Growth Promotion requires an ATCC reference, inoculum level, valid Control Count, and valid Test Count.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string testResult = ComboText(CmbTestResult);
            decimal minimumRecovery = 50m;
            decimal maximumRecovery = 200m;
            if (isGrowthPromotion)
            {
                DataTable rules = LoadApplicableQualificationRequirements(_selectedLotId);
                DataRow? gptRule = rules.AsEnumerable().FirstOrDefault(r => RowString(r, "TestName").Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase));
                minimumRecovery = gptRule == null ? 50m : RowNullableDecimal(gptRule, "MinimumRecoveryPercent") ?? 50m;
                maximumRecovery = gptRule == null ? 200m : RowNullableDecimal(gptRule, "MaximumRecoveryPercent") ?? 200m;
            }
            if (isGrowthPromotion && decimal.TryParse(recoveryPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal recovery))
            {
                testResult = recovery >= minimumRecovery && recovery <= maximumRecovery ? "Pass" : "Fail";
                SetComboText(CmbTestResult, testResult);
            }

            _currentOrganisms.Rows.Add(
                testType,
                organism,
                atccNumber,
                inoculumOrCondition,
                TxtExpected.Text.Trim(),
                TxtActual.Text.Trim(),
                controlCount,
                testCount,
                recoveryPercent,
                TxtIncubationConditions.Text.Trim(),
                testResult,
                isGrowthPromotion ? $"GPT acceptance automatically evaluated at {minimumRecovery:0.##}-{maximumRecovery:0.##}% recovery." : string.Empty);

            SetComboText(CmbOverallResult, CalculateQualificationResult(_currentOrganisms));

            // Add to combo if new
            bool exists = false;
            foreach (object item in CmbOrganism.Items)
            {
                if (string.Equals(item?.ToString(), organism, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
            if (!exists)
                CmbOrganism.Items.Add(organism);

            // Clear fields
            CmbOrganism.Text = string.Empty;
            TxtATCC.Clear();
            TxtInoculum.Clear();
            TxtExpected.Clear();
            TxtActual.Clear();
            SetComboText(CmbTestResult, "Pass");

            ShowToast($"Added organism: {organism}", "🧬");
        }

        private void CmbReleaseTestType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateReleaseTestTypeFields();
        }

        private void UpdateReleaseTestTypeFields()
        {
            if (CmbReleaseTestType == null || LblReleaseOrganism == null)
                return;

            string testType = ComboText(CmbReleaseTestType);

            CmbOrganism.IsEnabled = true;
            TxtATCC.IsEnabled = true;
            TxtInoculum.IsEnabled = true;
            TxtExpected.IsEnabled = true;
            TxtActual.IsEnabled = true;
            TxtControlCount.IsEnabled = true;
            TxtTestCount.IsEnabled = true;

            CmbOrganism.Text = string.Empty;
            TxtATCC.Clear();
            TxtInoculum.Clear();
            TxtExpected.Clear();
            TxtActual.Clear();
            TxtControlCount.Clear();
            TxtTestCount.Clear();

            if (testType.Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase))
            {
                LblReleaseOrganism.Text = "Organism";
                LblReleaseAtcc.Text = "ATCC";
                LblReleaseInoculum.Text = "Inoculum";
                LblReleaseExpected.Text = "Expected Growth";
                LblReleaseActual.Text = "Actual Growth";
                if (string.IsNullOrWhiteSpace(TxtInoculum.Text))
                    TxtInoculum.Text = "10-100 CFU";
                if (string.IsNullOrWhiteSpace(TxtExpected.Text))
                    TxtExpected.Text = "Growth comparable to control";
                return;
            }

            if (testType.Equals("pH Check", StringComparison.OrdinalIgnoreCase))
            {
                LblReleaseOrganism.Text = "Check";
                LblReleaseAtcc.Text = "Reference";
                LblReleaseInoculum.Text = "Measured pH";
                LblReleaseExpected.Text = "Acceptance Criteria";
                LblReleaseActual.Text = "Actual Result";
                CmbOrganism.Text = "pH of Media";
                TxtATCC.IsEnabled = false;
                TxtControlCount.IsEnabled = false;
                TxtTestCount.IsEnabled = false;
                TxtInoculum.Text = TxtReleaseMediaPh?.Text ?? string.Empty;
                TxtExpected.Text = "Within approved MPM / SOP range";
                TxtActual.Text = TxtReleaseMediaPh?.Text ?? string.Empty;
                return;
            }

            if (testType.Equals("Preincubation Check", StringComparison.OrdinalIgnoreCase))
            {
                LblReleaseOrganism.Text = "Check";
                LblReleaseAtcc.Text = "Incubation Condition";
                LblReleaseInoculum.Text = "Incubation Period";
                LblReleaseExpected.Text = "Expected Result";
                LblReleaseActual.Text = "Actual Observation";
                CmbOrganism.Text = "Preincubation Check";
                TxtATCC.IsEnabled = false;
                TxtControlCount.IsEnabled = false;
                TxtTestCount.IsEnabled = false;
                TxtInoculum.Text = "As per approved SOP";
                TxtExpected.Text = "No contamination / no growth";
                return;
            }

            LblReleaseOrganism.Text = "Property Check";
            LblReleaseAtcc.Text = "Reference Organism / NA";
            LblReleaseInoculum.Text = "Challenge / Condition";
            LblReleaseExpected.Text = "Expected Result";
            LblReleaseActual.Text = "Actual Result";
            CmbOrganism.Text = testType;
            TxtATCC.IsEnabled = false;
            TxtControlCount.IsEnabled = false;
            TxtTestCount.IsEnabled = false;
            TxtInoculum.Text = "As per approved SOP";
            TxtExpected.Text = testType + " satisfactory";
        }

        private void CmbOrganism_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbOrganism?.SelectedItem is ComboBoxItem item)
            {
                string atcc = item.Tag?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(atcc))
                    TxtATCC.Text = atcc;

                if (string.IsNullOrWhiteSpace(TxtInoculum.Text))
                    TxtInoculum.Text = "10-100 CFU";

                if (string.IsNullOrWhiteSpace(TxtExpected.Text))
                    TxtExpected.Text = "Growth comparable to control";
            }
        }

        private void BtnRemoveOrganism_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaEntryAccess()) return;
            if (DgCurrentOrganisms.SelectedItem is DataRowView view)
            {
                _currentOrganisms.Rows.Remove(view.Row);
                ShowToast("Organism removed", "🗑️");
            }
        }

        private void CmbPreparationMediaLot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            if (CmbPreparationMediaLot == null || CmbPreparationMediaLot.SelectedValue == null)
                return;

            if (!int.TryParse(CmbPreparationMediaLot.SelectedValue.ToString(), out int mediaLotId) || mediaLotId <= 0)
                return;

            _selectedLotId = mediaLotId;

            if (CmbPreparationMediaLot.SelectedItem is DataRowView view)
            {
                DataRow row = view.Row;

                int defaultDays = GetDefaultExpiryDays(row);
                if (DpPreparationDate != null && DpPreparationDate.SelectedDate.HasValue && DpPreparationExpiry != null)
                    DpPreparationExpiry.SelectedDate = DpPreparationDate.SelectedDate.Value.AddDays(defaultDays);

                UpdatePreparationNextAction();
                SetStatus("Stored media batch selected for preparation");
            }
        }

        private void DgReceipts_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            var row = RowFromGrid(DgReceipts);
            if (row == null)
            {
                pnlSidePanel.Visibility = Visibility.Collapsed;
                return;
            }

            _selectedLotId = RowInt(row, "MediaLotID");

            if (CmbPreparationMediaLot != null && _selectedLotId > 0)
                CmbPreparationMediaLot.SelectedValue = _selectedLotId;
            if (CmbReleaseMediaLot != null && _selectedLotId > 0)
                CmbReleaseMediaLot.SelectedValue = _selectedLotId;

            pnlSidePanel.Visibility = Visibility.Visible;
            lblDetailTitle.Text = $"Lot: {RowString(row, "LotNumber")}";

            FillPanelDetails(row);
            LoadGPTHistory(_selectedLotId);
        }

        private void DgReceipts_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_selectedLotId > 0)
                BtnEditLot_Click(sender, e);
        }

        private void DgReleaseReports_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            var row = RowFromGrid(DgReleaseReports);
            if (row == null) return;

            int reportLotId = RowInt(row, "MediaLotID");
            if (reportLotId > 0)
            {
                _selectedLotId = reportLotId;

                bool wasLoading = _isLoading;
                _isLoading = true;
                try
                {
                    if (CmbReleaseMediaLot != null)
                        CmbReleaseMediaLot.SelectedValue = reportLotId;
                }
                finally
                {
                    _isLoading = wasLoading;
                }

                DataRow? lotRow = GetReleaseLotRow(reportLotId);
                if (lotRow != null)
                {
                    TxtRelPrepNo.Text = "Lot Receipt: " + RowString(lotRow, "LotNumber");
                    TxtRelMediaName.Text = RowString(lotRow, "MediaCode") + " - " + RowString(lotRow, "MediaName");
                    TxtRelLotNo.Text = RowString(lotRow, "LotNumber");
                    TxtRelPH.Text = RowString(lotRow, "COANumber");
                    TxtRelStatus.Text = NormalizeStatus(RowString(lotRow, "ReceiptStatus"));
                }
            }

            _selectedReleaseReportId = RowInt(row, "MediaQualificationID");
            TxtReleaseReportNo.Text = RowString(row, "QualificationNo");
            DpReleaseDate.SelectedDate = RowDate(row, "QualificationDate");
            TxtReleasePerformedBy.Text = RowString(row, "PerformedBy");
            TxtReleaseReviewedBy.Text = RowString(row, "ReviewedBy");
            TxtReleaseRemarks.Text = RowString(row, "Remarks");
            SetComboText(CmbOverallResult, RowString(row, "OverallResult"));

            LoadOrganismsForReport(_selectedReleaseReportId);
            LoadReleaseSopFields(_selectedReleaseReportId);
            string selectedQualificationStatus = RowString(row, "QualificationStatus");
            if (selectedQualificationStatus.Equals("In Progress", StringComparison.OrdinalIgnoreCase))
            {
                decimal? minimumHours = RowNullableDecimal(row, "MinimumIncubationHoursSnapshot");
                DateTime? startedAt = RowDate(row, "QualificationStartedAt");
                TxtReleaseNextAction.Text =
                    "In Progress - incubation started " +
                    (startedAt.HasValue ? startedAt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "at an unavailable time") +
                    (minimumHours.HasValue ? "; controlled minimum " + minimumHours.Value.ToString("0.##", CultureInfo.InvariantCulture) + " hour(s)." : ".");
            }
            else
            {
                UpdateReleaseNextAction();
            }
            SetStatus("Release report selected - " + selectedQualificationStatus);
        }

        private void LoadOrganismsForReport(int releaseReportId)
        {

            DataTable table = _repository.Load(CultureMediaQuery.LoadOrganismsForReport,
                new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = releaseReportId });

            _currentOrganisms.Clear();
            foreach (DataRow row in table.Rows)
            {
                _currentOrganisms.Rows.Add(
                    RowString(row, "TestName"),
                    RowString(row, "OrganismName"),
                    RowString(row, "ATCCNumber"),
                    RowString(row, "InoculumLevel"),
                    RowString(row, "ExpectedResult"),
                    RowString(row, "ActualResult"),
                    RowString(row, "ControlCount"),
                    RowString(row, "TestCount"),
                    RowString(row, "RecoveryPercent"),
                    RowString(row, "IncubationConditions"),
                    RowString(row, "TestResult"),
                    RowString(row, "Remarks"));
            }
        }

        private void DgPreparations_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;

            var row = RowFromGrid(DgPreparations);
            if (row == null) return;

            string releaseStatus = NormalizeStatus(RowString(row, "ReleaseStatus"));
            _selectedPreparationPrintId = RowInt(row, "MediaPreparationID");

            TxtPreparationNo.Text = RowString(row, "MediaPreparationNo");

            if (CmbPreparationMediaLot != null)
                CmbPreparationMediaLot.SelectedValue = RowInt(row, "MediaLotID");

            DpPreparationDate.SelectedDate = RowDate(row, "PreparationDate");
            DpPreparationExpiry.SelectedDate = RowDate(row, "ExpiryDate");
            TxtQuantityPrepared.Text = RowString(row, "QuantityPrepared");
            TxtQuantityWeighedG.Text = RowString(row, "PowderQuantityG");
            TxtPreparedBy.Text = RowString(row, "PreparedBy");
            TxtBatchSize.Text = RowString(row, "BatchSize");
            TxtFinalPH.Text = RowString(row, "FinalPH");
            TxtAppearance.Text = RowString(row, "Appearance");
            TxtAutoclaveCycleNo.Text = RowString(row, "AutoclaveCycleNo");
            TxtAutoclaveTemperature.Text = RowString(row, "AutoclaveTemperature");
            TxtAutoclaveHoldingTime.Text = RowString(row, "AutoclaveHoldingTime");
            string sterilityReview = NormalizeSterilityReview(RowString(row, "SterilityReview"));
            SetComboText(CmbPreparationSterilityReview, string.IsNullOrWhiteSpace(sterilityReview) ? "Pending" : sterilityReview);
            TxtPreparationReviewedBy.Text = LoadSopField("MediaPreparation", RowInt(row, "MediaPreparationID"), "1035-L-0005/A7", "SterilityReviewedBy");
            TxtPreparationRemarks.Text = RowString(row, "Remarks");
            if (TxtPreparationReleaseStatus != null)
                TxtPreparationReleaseStatus.Text = releaseStatus;
            LoadPreparationSopFields(RowInt(row, "MediaPreparationID"));

            if (releaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase))
            {
                _selectedPreparationId = RowInt(row, "MediaPreparationID");
                UpdatePreparationNextAction();
                SetStatus("Preparation selected for editing / release");
                return;
            }

            _selectedPreparationId = 0;
            UpdatePreparationNextAction();
            SetStatus("Released or rejected preparations are locked. Use New Preparation to create another batch.");
        }

        private void txtGlobalSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isLoading) return;
            ApplyFilters();
        }

        private void CboFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            try
            {
                if (_isLoading)
                    return;

                if (DgReceipts == null)
                    return;

                if (DgReceipts.ItemsSource == null)
                    return;

                if (DgReceipts.ItemsSource is not DataView view)
                    return;

                var filters = new System.Collections.Generic.List<string>();

                // Status filter
                if (cboFilterStatus != null)
                {
                    string status = string.Empty;

                    if (cboFilterStatus.SelectedItem is ComboBoxItem statusItem && statusItem.Tag != null)
                        status = statusItem.Tag.ToString() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(status) && !status.Equals("All", StringComparison.OrdinalIgnoreCase))
                    {
                        if (status.Equals("Released", StringComparison.OrdinalIgnoreCase))
                            filters.Add("(ReceiptStatus = 'Released' OR ReceiptStatus = 'Accepted')");
                        else
                            filters.Add($"ReceiptStatus = '{EscapeRowFilterValue(status)}'");
                    }
                }

                // Media filter
                if (cboFilterMedia != null)
                {
                    int mediaId = SelectedInt(cboFilterMedia);
                    if (mediaId > 0)
                        filters.Add($"MediaID = {mediaId}");
                }

                // Search filter
                if (txtGlobalSearch != null)
                {
                    string search = (txtGlobalSearch.Text ?? string.Empty).Trim();

                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        string safeSearch = EscapeRowFilterValue(search);

                        filters.Add($@"
(
    Convert(MediaCode, 'System.String') LIKE '%{safeSearch}%'
    OR Convert(MediaName, 'System.String') LIKE '%{safeSearch}%'
    OR Convert(LotNumber, 'System.String') LIKE '%{safeSearch}%'
    OR Convert(Supplier, 'System.String') LIKE '%{safeSearch}%'
)");
                    }
                }

                view.RowFilter = filters.Count > 0 ? string.Join(" AND ", filters) : string.Empty;

                if (lblRecordCount != null)
                    UpdateRecordCount(view.Count);
            }
            catch (Exception ex)
            {
                SetStatus("Filter error");
                Infrastructure.ApplicationLogger.Error("Culture media filter failed.", ex);
                MessageBox.Show(
                    "The culture media filter could not be applied. Review the application log for technical details.",
                    "Culture Media",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==================== PRINTING ====================

        private void BtnPrintReceipt_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaPrintAccess()) return;
            int lotId = _selectedLotId;
            if (sender is Button btn && btn.Tag != null)
                lotId = Convert.ToInt32(btn.Tag);

            if (lotId <= 0)
            {
                ShowToast("Please select a lot first", "⚠️");
                return;
            }

            try
            {
                if (!ConfirmCultureMediaSignature("Print Media Stock Label", "Media Lot " + lotId, lotId, out string signedBy, out string signatureReason))
                    return;


                DataTable table = _repository.Load(
                    CultureMediaQuery.LoadMediaLotReceiptForPrint,
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = lotId });

                if (table.Rows.Count == 0)
                {
                    ClearPendingCultureMediaSignature();
                    ShowToast("Selected lot was not found", "⚠️");
                    return;
                }

                DataRow row = table.Rows[0];
                string mediaDisplay = (RowString(row, "MediaCode") + " - " + RowString(row, "MediaName")).Trim(' ', '-');
                string serialNumber = LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "SerialNumberOfMedium");
                if (string.IsNullOrWhiteSpace(serialNumber))
                    serialNumber = RowString(row, "MediaCode") + "-" + RowString(row, "MediaLotID");

                bool printed = LabelPrintingService.PrintDehydratedMediumPackLabel(
                    this,
                    serialNumber,
                    LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "MpmNo"),
                    string.Empty,
                    RowDate(row, "ReceivedDate"),
                    LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "PackNo"),
                    LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "TotalPacksReceived"),
                    ParseIsoDate(LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "DateOfOpening")),
                    ParseIsoDate(LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "DateOfReleaseOfLot")),
                    RowString(row, "ReceivedBy"),
                    mediaDisplay,
                    RowString(row, "LotNumber"),
                    RowDate(row, "ExpiryDate"),
                    NormalizeStatus(RowString(row, "ReceiptStatus")));

                if (printed)
                {
                    StorePendingCultureMediaSignature();
                    RecordCultureMediaPrint("MediaLot", lotId, RowString(row, "LotNumber"), "Media Stock Label", signedBy, signatureReason);
                    AddCultureMediaAudit("CultureMediaLots", lotId, "Media Stock Label Printed", "", "Printed", signatureReason, signedBy);
                    ShowToast($"Stock label printed for lot {RowString(row, "LotNumber")}", "🏷️");
                }
                else
                {
                    ClearPendingCultureMediaSignature();
                }
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error printing stock label", ex);
            }
        }

        private void BtnPrintReleaseReport_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaPrintAccess()) return;
            int reportId = _selectedReleaseReportId;
            if (sender is Button btn && btn.Tag != null)
                reportId = Convert.ToInt32(btn.Tag);

            if (reportId <= 0)
            {
                ShowToast("Please select a report first", "⚠️");
                return;
            }

            try
            {
                DataTable header = _repository.Load(CultureMediaQuery.LoadReleaseReportHeaderForPrint,
                    new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = reportId });

                if (header.Rows.Count == 0)
                {
                    ShowToast("Selected release report was not found", "⚠️");
                    return;
                }

                DataTable tests = _repository.Load(CultureMediaQuery.LoadReleaseReportTestsForPrint,
                    new SqlParameter("@MediaQualificationID", SqlDbType.Int) { Value = reportId });

                DataRow reportRow = header.Rows[0];
                string reportNo = RowString(reportRow, "QualificationNo");
                if (!ConfirmCultureMediaSignature("Print Media Lot Release Report", reportNo, reportId, out string signedBy, out string printReason))
                    return;

                bool printed = ShowCultureMediaDocumentPreview(
                    BuildReleaseReportDocument(reportRow, tests),
                    "Culture Media Qualification Report Preview",
                    "Culture Media Qualification Report",
                    "MediaQualification",
                    reportId,
                    reportNo,
                    signedBy,
                    printReason);
                if (printed)
                    AddCultureMediaAudit("MediaQualifications", reportId, "Media Lot Release Report Printed", string.Empty, "Printed", printReason, signedBy);
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error opening release report", ex);
            }
        }

        private FlowDocument BuildReleaseReportDocument(DataRow row, DataTable tests)
        {
            var document = new FlowDocument
            {
                PageWidth = 793,
                PageHeight = 1122,
                PagePadding = new Thickness(20, 18, 20, 18),
                ColumnWidth = double.PositiveInfinity,
                ColumnGap = 0,
                IsColumnWidthFlexible = false,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10
            };

            string storedOverall = RowString(row, "OverallResult");
            string calculatedOverall = CalculateQualificationResult(tests);
            string receiptStatus = NormalizeStatus(RowString(row, "ReceiptStatus"));
            bool isReleased = receiptStatus.Equals("Released", StringComparison.OrdinalIgnoreCase) &&
                              storedOverall.Equals("Pass", StringComparison.OrdinalIgnoreCase) &&
                              calculatedOverall.Equals("Pass", StringComparison.OrdinalIgnoreCase) &&
                              RowString(row, "QualificationStatus").Equals("Released", StringComparison.OrdinalIgnoreCase) &&
                              !string.IsNullOrWhiteSpace(RowString(row, "ReleasedBy")) &&
                              RowDate(row, "ReleaseDate").HasValue;

            string storedWorkflowStatus = RowString(row, "QualificationStatus");
            string qualificationStatus = storedWorkflowStatus.Equals("In Progress", StringComparison.OrdinalIgnoreCase)
                ? "In Progress"
                : calculatedOverall.Equals("Fail", StringComparison.OrdinalIgnoreCase)
                    ? "Failed"
                    : calculatedOverall.Equals("Pass", StringComparison.OrdinalIgnoreCase)
                        ? (isReleased ? "Qualified" : "Pending Release")
                        : "Pending Review";
            string reportOverall = isReleased ? "Pass" : calculatedOverall;
            string releasedBy = isReleased ? RowString(row, "ReleasedBy") : "Not Released";
            int mediaLotId = RowInt(row, "MediaLotID");
            string dateOpened = mediaLotId > 0
                ? LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfOpening")
                : string.Empty;
            string storedReleaseDate = mediaLotId > 0
                ? LoadSopField("MediaLot", mediaLotId, "1035-L-0005/A2", "DateOfReleaseOfLot")
                : string.Empty;
            string releaseDate = isReleased ? FirstNonEmpty(FormatIsoDate(RowDate(row, "ReleaseDate")), storedReleaseDate) : "—";
            string remarks = RowString(row, "Remarks");
            if (isReleased && string.IsNullOrWhiteSpace(remarks))
                remarks = "All qualification tests met the predefined acceptance criteria.";

            bool isReviewed = !string.IsNullOrWhiteSpace(RowString(row, "ReviewedBy")) && RowDate(row, "ReviewDate").HasValue;
            string reportTitle = isReleased
                ? "CULTURE MEDIA LOT QUALIFICATION AND RELEASE REPORT"
                : "CULTURE MEDIA LOT QUALIFICATION REPORT";

            var reportFrame = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0),
                Background = Brushes.White
            };
            reportFrame.Columns.Add(new TableColumn { Width = new GridLength(737) });
            var frameGroup = new TableRowGroup();
            var frameRow = new TableRow();
            var frameCell = new TableCell
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                BorderThickness = new Thickness(1.6),
                Padding = new Thickness(10, 9, 10, 9),
                Background = Brushes.White
            };
            frameRow.Cells.Add(frameCell);
            frameGroup.Rows.Add(frameRow);
            reportFrame.RowGroups.Add(frameGroup);
            BlockCollection reportBlocks = frameCell.Blocks;

            reportBlocks.Add(BuildReleaseReportHeader(RowString(row, "QualificationNo")));
            reportBlocks.Add(MakeReportTitle(reportTitle));

            var info = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 10) };
            info.Columns.Add(new TableColumn { Width = new GridLength(360) });
            info.Columns.Add(new TableColumn { Width = new GridLength(360) });
            var infoGroup = new TableRowGroup();
            info.RowGroups.Add(infoGroup);
            AddInfoRow(infoGroup, "Media", (RowString(row, "MediaCode") + " - " + RowString(row, "MediaName")).Trim(' ', '-'), "Supplier", RowString(row, "Supplier"));
            AddInfoRow(infoGroup, "Manufacturer", RowString(row, "Manufacturer"), "Manufacturer Lot No.", FirstNonEmpty(RowString(row, "ManufacturerLot"), RowString(row, "LotNumber")));
            AddInfoRow(infoGroup, "Received Date", FormatIsoDate(RowDate(row, "ReceivedDate")), "Date Opened", dateOpened);
            AddInfoRow(infoGroup, "Expiry Date", FormatIsoDate(RowDate(row, "ExpiryDate")), "Storage Conditions", RowString(row, "StorageCondition"));
            string qualificationTiming =
                FormatIsoDateTime(RowDate(row, "QualificationStartedAt")) + " / " +
                FormatIsoDateTime(RowDate(row, "IncubationCompletedAt"));
            string minimumIncubation = RowNullableDecimal(row, "MinimumIncubationHoursSnapshot")?.ToString("0.##", CultureInfo.InvariantCulture) ?? "N/A";
            AddInfoRow(infoGroup, "COA Reference", RowString(row, "COANumber"), "GPT Start / Completion", qualificationTiming);
            AddInfoRow(infoGroup, "Controlled Minimum Incubation", minimumIncubation + " hour(s)", "Test Date", FormatIsoDate(RowDate(row, "QualificationDate")));
            AddInfoRow(infoGroup, "Review Date", FormatIsoDate(RowDate(row, "ReviewDate")), "Release Date", releaseDate);
            AddInfoRow(infoGroup, "Report Generated Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), "Media Status", receiptStatus);
            AddInfoRow(infoGroup, "Qualification Status", qualificationStatus, "Overall Result", reportOverall);
            AddInfoRow(infoGroup, "Report Revision", "00", "SOP Number / Revision", "1035-L-0005 / Current Approved Revision");
            AddInfoRow(infoGroup, "Remarks", remarks, "Document Control", "Controlled Copy");
            // SOP and remarks are already displayed in the controlled information block above.
            reportBlocks.Add(info);

            reportBlocks.Add(MakeSectionHeading("QUALIFICATION TEST RESULTS"));
            var testTable = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
            double[] widths = { 76, 104, 60, 132, 112, 88, 62, 52 };
            foreach (double width in widths)
                testTable.Columns.Add(new TableColumn { Width = new GridLength(width) });

            var group = new TableRowGroup();
            testTable.RowGroups.Add(group);
            AddTableHeader(group, "Test", "Organism / Check", "Reference", "Expected / Acceptance", "Actual / Observation", "Control / Test", "Recovery %", "Result");
            foreach (DataRow test in tests.Rows)
            {
                bool growthPromotion = IsGrowthPromotionTest(RowString(test, "TestName"));
                string expectedAcceptance = FirstNonEmpty(RowString(test, "ExpectedResult"), RowString(test, "IncubationConditions"));
                string actualObservation = FirstNonEmpty(RowString(test, "ActualResult"), RowString(test, "InoculumLevel"));
                string reference = growthPromotion ? ReportValueOrNa(RowString(test, "ATCCNumber")) : "N/A";
                string counts = growthPromotion
                    ? ReportValueOrNa(RowString(test, "ControlCount")) + " / " + ReportValueOrNa(RowString(test, "TestCount"))
                    : "N/A";
                string recovery = growthPromotion ? ReportValueOrNa(RowString(test, "RecoveryPercent")) : "N/A";

                var tr = new TableRow();
                tr.Cells.Add(MakeReportCell(RowString(test, "TestName"), false));
                tr.Cells.Add(MakeReportCell(RowString(test, "OrganismName"), false));
                tr.Cells.Add(MakeReportCell(reference, false));
                tr.Cells.Add(MakeReportCell(expectedAcceptance, false));
                tr.Cells.Add(MakeReportCell(actualObservation, false));
                tr.Cells.Add(MakeReportCell(counts, false));
                tr.Cells.Add(MakeReportCell(recovery, false));
                tr.Cells.Add(MakeReportCell(RowString(test, "TestResult"), false));
                group.Rows.Add(tr);
            }
            reportBlocks.Add(testTable);

            reportBlocks.Add(MakeSectionHeading(isReleased ? "FINAL DISPOSITION" : "QUALIFICATION REVIEW STATUS"));
            var disposition = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
            disposition.Columns.Add(new TableColumn { Width = new GridLength(360) });
            disposition.Columns.Add(new TableColumn { Width = new GridLength(360) });
            var dispositionGroup = new TableRowGroup();
            disposition.RowGroups.Add(dispositionGroup);

            if (isReleased)
            {
                AddInfoRow(dispositionGroup, "Disposition", "Released", "Release Date", releaseDate);
                AddInfoRow(dispositionGroup, "Performed By", RowString(row, "PerformedBy"), "Reviewed By", RowString(row, "ReviewedBy"));
                AddInfoRow(dispositionGroup, "Released By", releasedBy, "Release Date", releaseDate);
            }
            else
            {
                AddInfoRow(dispositionGroup, "Qualification State", isReviewed ? qualificationStatus : "Pending Independent Review", "Review Date", FormatIsoDate(RowDate(row, "ReviewDate")));
                AddInfoRow(dispositionGroup, "Performed By", RowString(row, "PerformedBy"), "Reviewed By", isReviewed ? RowString(row, "ReviewedBy") : "Pending");
                AddInfoRow(dispositionGroup, "Final Release", "Not yet authorized", "Release Date", "N/A");
            }
            reportBlocks.Add(disposition);

            var footer = new Paragraph(new Run("Generated by PharmaLIMS | Controlled Copy | Page 1 of 1"))
            {
                FontSize = 8,
                Foreground = Brushes.DimGray,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            };
            reportBlocks.Add(footer);
            document.Blocks.Add(reportFrame);
            return document;
        }

        private static bool IsGrowthPromotionTest(string testName)
            => testName.Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase);

        private static string ReportValueOrNa(string value)
            => string.IsNullOrWhiteSpace(value) ? "N/A" : value.Trim();

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return string.Empty;
        }

        private static string CalculateRecoveryPercent(string controlCount, string testCount)
        {
            if (!decimal.TryParse(controlCount, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal control) || control <= 0 ||
                !decimal.TryParse(testCount, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal test) || test < 0)
                return string.Empty;

            return ((test / control) * 100m).ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string CalculateQualificationResult(DataTable tests)
        {
            if (tests == null || tests.Rows.Count == 0)
                return "Pending";

            bool hasPending = false;
            foreach (DataRow test in tests.Rows)
            {
                string result = RowString(test, "TestResult");
                if (result.Equals("Fail", StringComparison.OrdinalIgnoreCase) || result.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    return "Fail";
                if (!result.Equals("Pass", StringComparison.OrdinalIgnoreCase) && !result.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                    hasPending = true;
            }

            return hasPending ? "Pending" : "Pass";
        }

        private bool ValidateGrowthPromotionCoverage()
        {
            string mediaIdentity = TxtRelMediaName.Text.ToUpperInvariant();
            bool isGeneralPurposeMedia = mediaIdentity.Contains("TSA") ||
                                         mediaIdentity.Contains("TRYPTIC") ||
                                         mediaIdentity.Contains("TRYPTONE SOY");
            if (!isGeneralPurposeMedia)
                return true;

            bool hasBacterium = false;
            bool hasFungus = false;
            int growthPromotionRows = 0;
            foreach (DataRow row in _currentOrganisms.Rows)
            {
                if (!RowString(row, "TestName").Equals("Growth Promotion", StringComparison.OrdinalIgnoreCase))
                    continue;

                growthPromotionRows++;
                string organism = RowString(row, "OrganismName").ToUpperInvariant();
                if (organism.Contains("CANDIDA") || organism.Contains("ASPERGILLUS"))
                    hasFungus = true;
                else if (!string.IsNullOrWhiteSpace(organism))
                    hasBacterium = true;
            }

            if (growthPromotionRows < 2 || !hasBacterium || !hasFungus)
            {
                MessageBox.Show(
                    "General-purpose TSA qualification requires the approved SOP organism panel, including at least one bacterial and one fungal challenge organism.",
                    "GPT Organism Coverage",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private Block BuildReleaseReportHeader(string reportNo)
        {
            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                BorderThickness = new Thickness(0.8)
            };
            table.Columns.Add(new TableColumn { Width = new GridLength(190) });
            table.Columns.Add(new TableColumn { Width = new GridLength(330) });
            table.Columns.Add(new TableColumn { Width = new GridLength(200) });
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            var row = new TableRow();

            var logoCell = MakeReportCell(string.Empty, false);
            Image? logo = TryCreateReportLogo(110, 45);
            if (logo != null)
                logoCell.Blocks.Add(new BlockUIContainer(logo) { TextAlignment = TextAlignment.Center });
            else
                logoCell.Blocks.Add(new Paragraph(new Run("MEDICA")) { FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center });
            row.Cells.Add(logoCell);

            var companyCell = MakeReportCell("MEDICA PHARMACEUTICAL INDUSTRY\nMicrobiology Department", true);
            companyCell.Background = new SolidColorBrush(Color.FromRgb(22, 54, 82));
            companyCell.Foreground = Brushes.White;
            companyCell.FontSize = 10;
            row.Cells.Add(companyCell);

            var reportNoCell = MakeReportCell("Report No.\n" + reportNo, true);
            reportNoCell.Background = new SolidColorBrush(Color.FromRgb(235, 241, 247));
            reportNoCell.Foreground = new SolidColorBrush(Color.FromRgb(22, 54, 82));
            row.Cells.Add(reportNoCell);
            group.Rows.Add(row);
            return table;
        }

        private static Paragraph MakeReportTitle(string text)
        {
            return new Paragraph(new Run(text))
            {
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                TextAlignment = TextAlignment.Center,
                BorderBrush = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                BorderThickness = new Thickness(0, 0, 0, 1.2),
                Padding = new Thickness(0, 4, 0, 7),
                Margin = new Thickness(0, 2, 0, 9)
            };
        }

        private static Paragraph MakeSectionHeading(string text)
        {
            return new Paragraph(new Run(text))
            {
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                BorderThickness = new Thickness(0.8),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 8, 0, 4)
            };
        }

        private static void AddInfoRow(TableRowGroup group, string label1, string value1, string label2, string value2)
        {
            var row = new TableRow();
            row.Cells.Add(MakeReportCell(label1 + ": " + value1, false));
            row.Cells.Add(MakeReportCell(label2 + ": " + value2, false));
            group.Rows.Add(row);
        }

        private static void AddTableHeader(TableRowGroup group, params string[] headers)
        {
            var row = new TableRow();
            foreach (string header in headers)
                row.Cells.Add(MakeReportCell(header, true));
            group.Rows.Add(row);
        }

        private static TableCell MakeReportCell(string text, bool bold)
        {
            var cell = new TableCell(new Paragraph(new Run(text ?? string.Empty)))
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(145, 158, 171)),
                BorderThickness = new Thickness(0.55),
                Padding = new Thickness(5, 4, 5, 4),
                FontSize = 9
            };
            if (bold)
                cell.FontWeight = FontWeights.Bold;
            return cell;
        }

        private Image? TryCreateReportLogo(double width, double height)
        {
            try
            {
                string localLogo = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medica-logo.png");
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = File.Exists(localLogo)
                    ? new Uri(localLogo, UriKind.Absolute)
                    : new Uri("pack://siteoforigin:,,,/medica-logo.png", UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                return new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Uniform
                };
            }
            catch
            {
                return null;
            }
        }

        private bool ShowCultureMediaDocumentPreview(
            FlowDocument document,
            string windowTitle,
            string printJobName,
            string entityType,
            int entityId,
            string recordNumber,
            string printedBy,
            string printReason)
        {
            document.PageWidth = 793;
            document.PageHeight = 1122;
            document.PagePadding = new Thickness(20, 18, 20, 18);
            document.ColumnWidth = double.PositiveInfinity;

            var viewer = new FlowDocumentScrollViewer
            {
                Document = document,
                Zoom = 110,
                IsToolBarVisible = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(12),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8EEF5"))
            };

            var printButton = new Button
            {
                Content = "Print Controlled Copy",
                Width = 175,
                Height = 38,
                Margin = new Thickness(0, 0, 10, 0),
                FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F5F8F")),
                Foreground = Brushes.White
            };
            var closeButton = new Button
            {
                Content = "Close",
                Width = 110,
                Height = 38,
                FontWeight = FontWeights.SemiBold
            };
            var commands = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(14, 8, 14, 14)
            };
            commands.Children.Add(printButton);
            commands.Children.Add(closeButton);

            var root = new DockPanel { Background = Brushes.White };
            DockPanel.SetDock(commands, Dock.Bottom);
            root.Children.Add(commands);
            root.Children.Add(viewer);

            var window = new Window
            {
                Title = windowTitle,
                Owner = this,
                Width = 1180,
                Height = 860,
                MinWidth = 900,
                MinHeight = 650,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = root
            };

            bool printed = false;
            printButton.Click += (_, _) =>
            {
                var dialog = new PrintDialog();
                if (dialog.ShowDialog() != true)
                    return;

                double originalWidth = document.PageWidth;
                double originalHeight = document.PageHeight;
                Thickness originalPadding = document.PagePadding;
                try
                {
                    document.PageWidth = dialog.PrintableAreaWidth;
                    document.PageHeight = dialog.PrintableAreaHeight;
                    document.PagePadding = new Thickness(28);
                    dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, printJobName);
                    StorePendingCultureMediaSignature();
                    RecordCultureMediaPrint(entityType, entityId, recordNumber, printJobName, printedBy, printReason);
                    printed = true;
                    printButton.IsEnabled = false;
                    printButton.Content = "Printed";
                }
                finally
                {
                    document.PageWidth = originalWidth;
                    document.PageHeight = originalHeight;
                    document.PagePadding = originalPadding;
                }
            };
            closeButton.Click += (_, _) => window.Close();
            window.ShowDialog();
            if (!printed)
                ClearPendingCultureMediaSignature();
            return printed;
        }

        private void BtnPrintReleasedLabel_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaPrintAccess()) return;
            int lotId = _selectedLotId;
            if (CmbReleaseMediaLot != null && CmbReleaseMediaLot.SelectedValue != null &&
                int.TryParse(CmbReleaseMediaLot.SelectedValue.ToString(), out int selectedLotId) && selectedLotId > 0)
            {
                lotId = selectedLotId;
            }

            if (lotId <= 0)
            {
                ShowToast("Select Media Lot to Release first", "⚠️");
                return;
            }

            try
            {

                DataTable table = _repository.Load(
                    CultureMediaQuery.LoadReleasedMediaLabelData,
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = lotId });

                if (table.Rows.Count == 0)
                {
                    ShowToast("Selected lot was not found", "⚠️");
                    return;
                }

                DataRow row = table.Rows[0];
                string status = NormalizeStatus(RowString(row, "ReceiptStatus"));
                if (!status.Equals("Released", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        $"Released Label cannot be printed because the selected media lot status is {status}. Save a complete PASS release report first.",
                        "Released Label",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!ConfirmCultureMediaSignature("Print Released Media Label", "Media Lot " + lotId, lotId, out string signedBy, out string signatureReason))
                    return;

                DateTime? releaseDate = ParseIsoDate(LoadSopField("MediaLot", lotId, "1035-L-0005/A2", "DateOfReleaseOfLot"));
                if (!releaseDate.HasValue)
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show("The released-media label cannot be printed because the approved release date is missing.", "Released Label", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string mediaDisplay = (RowString(row, "MediaCode") + " - " + RowString(row, "MediaName")).Trim(' ', '-');
                bool printed = LabelPrintingService.PrintReleasedMediaLabel(
                    this,
                    mediaDisplay,
                    RowString(row, "LotNumber"),
                    releaseDate,
                    RowDate(row, "ExpiryDate"),
                    signedBy);

                if (printed)
                {
                    StorePendingCultureMediaSignature();
                    RecordCultureMediaPrint("MediaLot", lotId, RowString(row, "LotNumber"), "Released Media Label", signedBy, signatureReason);
                    AddCultureMediaAudit("CultureMediaLots", lotId, "Released Media Label Printed", "", "Printed", signatureReason, signedBy);
                    ShowToast($"Released label printed for lot {RowString(row, "LotNumber")}", "🏷️");
                }
                else
                {
                    ClearPendingCultureMediaSignature();
                }
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error printing released media label", ex);
            }
        }

        private void BtnPrintPreparation_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaPrintAccess()) return;
            int preparationId = _selectedPreparationPrintId > 0 ? _selectedPreparationPrintId : _selectedPreparationId;
            if (preparationId <= 0)
            {
                ShowToast("Please select a saved preparation first", "⚠️");
                return;
            }

            try
            {
                DataTable table = _repository.Load(CultureMediaQuery.LoadPreparationForPrint,
                    new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });

                if (table.Rows.Count != 1)
                {
                    ShowToast("Preparation record was not found", "⚠️");
                    return;
                }

                DataRow row = table.Rows[0];
                string preparationNo = RowString(row, "MediaPreparationNo");
                if (!ConfirmCultureMediaSignature("Print Media Preparation Record", preparationNo, preparationId, out string signedBy, out string printReason))
                    return;

                bool printed = ShowCultureMediaDocumentPreview(
                    BuildPreparationReportDocument(row),
                    "Culture Media Preparation Record Preview",
                    "Culture Media Preparation Record",
                    "MediaPreparation",
                    preparationId,
                    preparationNo,
                    signedBy,
                    printReason);

                if (printed)
                {
                    AddCultureMediaAudit("MediaPreparations", preparationId, "Media Preparation Record Printed", string.Empty, "Printed", printReason, signedBy);
                    ShowToast($"Preparation record printed for {preparationNo}", "🖨️");
                }
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error printing media preparation record", ex);
            }
        }

        private FlowDocument BuildPreparationReportDocument(DataRow row)
        {
            int preparationId = RowInt(row, "MediaPreparationID");
            string preparationNo = RowString(row, "MediaPreparationNo");
            string additives = LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "Additives");
            var document = new FlowDocument
            {
                PageWidth = 793,
                PageHeight = 1122,
                PagePadding = new Thickness(20, 18, 20, 18),
                ColumnWidth = double.PositiveInfinity,
                ColumnGap = 0,
                IsColumnWidthFlexible = false,
                FontFamily = new FontFamily("Arial"),
                FontSize = 10
            };

            var reportFrame = new Table { CellSpacing = 0, Margin = new Thickness(0), Background = Brushes.White };
            reportFrame.Columns.Add(new TableColumn { Width = new GridLength(737) });
            var frameGroup = new TableRowGroup();
            var frameRow = new TableRow();
            var frameCell = new TableCell
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(22, 54, 82)),
                BorderThickness = new Thickness(1.6),
                Padding = new Thickness(10, 9, 10, 9),
                Background = Brushes.White
            };
            frameRow.Cells.Add(frameCell);
            frameGroup.Rows.Add(frameRow);
            reportFrame.RowGroups.Add(frameGroup);
            BlockCollection blocks = frameCell.Blocks;

            blocks.Add(BuildReleaseReportHeader(preparationNo));
            blocks.Add(MakeReportTitle("CULTURE MEDIA PREPARATION AND RELEASE RECORD"));

            var info = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 10) };
            info.Columns.Add(new TableColumn { Width = new GridLength(360) });
            info.Columns.Add(new TableColumn { Width = new GridLength(360) });
            var infoGroup = new TableRowGroup();
            info.RowGroups.Add(infoGroup);
            AddInfoRow(infoGroup, "Preparation No.", preparationNo, "Status", RowString(row, "ReleaseStatus"));
            AddInfoRow(infoGroup, "Media", (RowString(row, "MediaCode") + " - " + RowString(row, "MediaName")).Trim(' ', '-'), "Media Type", RowString(row, "MediaType"));
            AddInfoRow(infoGroup, "Source Lot", FirstNonEmpty(RowString(row, "ManufacturerLot"), RowString(row, "LotNumber")), "Supplier", RowString(row, "Supplier"));
            AddInfoRow(infoGroup, "Supplier COA", RowString(row, "COANumber"), "Source Lot Expiry", FormatIsoDate(RowDate(row, "SourceLotExpiry")));
            AddInfoRow(infoGroup, "Preparation Date", FormatIsoDate(RowDate(row, "PreparationDate")), "Use Before", FormatIsoDate(RowDate(row, "ExpiryDate")));
            AddInfoRow(infoGroup, "Quantity Prepared", RowString(row, "QuantityPrepared"), "Powder Weighed (g)", RowString(row, "PowderQuantityG"));
            AddInfoRow(infoGroup, "Batch Size", RowString(row, "BatchSize"), "Final pH", RowString(row, "FinalPH"));
            AddInfoRow(infoGroup, "Appearance", RowString(row, "Appearance"), "Additives / Supplements", string.IsNullOrWhiteSpace(additives) ? "None recorded" : additives);
            blocks.Add(info);

            blocks.Add(MakeSectionHeading("SOP A6 PREPARATION AND STERILIZATION"));
            var a6 = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
            a6.Columns.Add(new TableColumn { Width = new GridLength(360) });
            a6.Columns.Add(new TableColumn { Width = new GridLength(360) });
            var a6Group = new TableRowGroup();
            a6.RowGroups.Add(a6Group);
            AddInfoRow(a6Group, "Load No.", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "LoadNo"), "MPM No.", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MpmNo"));
            AddInfoRow(a6Group, "Purified Water (ml)", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PurifiedWaterMl"), "Quantity Dispensed", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "QuantityDispensed"));
            AddInfoRow(a6Group, "Balance No.", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "BalanceNo"), "Equipment Code", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "EquipmentCode"));
            AddInfoRow(a6Group, "pH Meter No.", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "PhMeterNo"), "Sterilization Method", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "MethodOfSterilization"));
            AddInfoRow(a6Group, "Autoclave Cycle", RowString(row, "AutoclaveCycleNo"), "Temperature / Holding", RowString(row, "AutoclaveTemperature") + " °C / " + RowString(row, "AutoclaveHoldingTime") + " min");
            blocks.Add(a6);

            blocks.Add(MakeSectionHeading("SOP A7 VISUAL AND STERILITY RELEASE CHECKS"));
            var a7 = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10) };
            a7.Columns.Add(new TableColumn { Width = new GridLength(360) });
            a7.Columns.Add(new TableColumn { Width = new GridLength(360) });
            var a7Group = new TableRowGroup();
            a7.RowGroups.Add(a7Group);
            AddInfoRow(a7Group, "Visual Equipment", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "EquipmentCode"), "Visual Conclusion", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "Conclusion"));
            AddInfoRow(a7Group, "Visual Observations", LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "VisualObservations"), "Visual Checked By", FirstNonEmpty(RowString(row, "VisualCheckedBy"), LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A7", "CheckedBy")));
            AddInfoRow(a7Group, "Sterility Review", RowString(row, "SterilityReview"), "Sterility Reviewed By", RowString(row, "SterilityReviewedBy"));
            AddInfoRow(a7Group, "Released By", RowString(row, "ReleasedBy"), "Release Date / Time", FormatIsoDateTime(RowDate(row, "ReleasedAt")));
            blocks.Add(a7);

            blocks.Add(MakeSectionHeading("ATTRIBUTABLE SIGNATURES AND REMARKS"));
            var sign = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
            sign.Columns.Add(new TableColumn { Width = new GridLength(240) });
            sign.Columns.Add(new TableColumn { Width = new GridLength(240) });
            sign.Columns.Add(new TableColumn { Width = new GridLength(240) });
            var signGroup = new TableRowGroup();
            sign.RowGroups.Add(signGroup);
            AddTableHeader(signGroup, "Prepared By", "Visual Checked By", "Sterility Reviewed / Released By");
            var signRow = new TableRow();
            signRow.Cells.Add(MakeReportCell(RowString(row, "PreparedBy"), false));
            signRow.Cells.Add(MakeReportCell(RowString(row, "VisualCheckedBy"), false));
            signRow.Cells.Add(MakeReportCell(FirstNonEmpty(RowString(row, "SterilityReviewedBy"), "Not reviewed") + " / " + FirstNonEmpty(RowString(row, "ReleasedBy"), "Not released"), false));
            signGroup.Rows.Add(signRow);
            blocks.Add(sign);
            blocks.Add(new Paragraph(new Run("Remarks: " + FirstNonEmpty(RowString(row, "Remarks"), "None"))) { Margin = new Thickness(0, 4, 0, 6) });
            blocks.Add(new Paragraph(new Run("Electronically generated controlled record. Printed copies are uncontrolled unless issued through the PharmaLIMS print workflow."))
            {
                FontSize = 8,
                Foreground = Brushes.DimGray,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
            document.Blocks.Add(reportFrame);
            return document;
        }

        private void BtnPrintLabel_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureCultureMediaPrintAccess()) return;
            int preparationId = _selectedPreparationPrintId > 0 ? _selectedPreparationPrintId : _selectedPreparationId;
            if (preparationId <= 0)
            {
                ShowToast("Please select a saved preparation first", "⚠️");
                return;
            }

            try
            {
                if (!ConfirmCultureMediaSignature("Print Prepared Media Label", "Media Preparation " + preparationId, preparationId, out string signedBy, out string signatureReason))
                    return;


                DataTable table = _repository.Load(
                    CultureMediaQuery.LoadPreparedMediaLabelData,
                    new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });

                if (table.Rows.Count == 0)
                {
                    ClearPendingCultureMediaSignature();
                    ShowToast("Preparation record was not found", "⚠️");
                    return;
                }

                DataRow row = table.Rows[0];
                string mediaDisplay = (RowString(row, "MediaCode") + " - " + RowString(row, "MediaName")).Trim(' ', '-');

                bool printed = LabelPrintingService.PrintCultureMediaLabel(
                    this,
                    RowString(row, "MediaPreparationNo"),
                    mediaDisplay,
                    RowString(row, "LotNumber"),
                    RowDate(row, "PreparationDate"),
                    RowDate(row, "ExpiryDate"),
                    RowString(row, "PreparedBy"),
                    RowString(row, "ReleaseStatus"),
                    RowString(row, "FinalPH"),
                    RowString(row, "QuantityPrepared"),
                    LoadSopField("MediaPreparation", preparationId, "1035-L-0005/A6", "Additives"));

                if (printed)
                {
                    StorePendingCultureMediaSignature();
                    RecordCultureMediaPrint("MediaPreparation", preparationId, RowString(row, "MediaPreparationNo"), "Prepared Media Label", signedBy, signatureReason);
                    AddCultureMediaAudit("MediaPreparations", preparationId, "Prepared Media Label Printed", "", "Printed", signatureReason, signedBy);
                    ShowToast($"Label printed for {RowString(row, "MediaPreparationNo")}", "🏷️");
                }
                else
                {
                    ClearPendingCultureMediaSignature();
                }
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error printing culture media label", ex);
            }
        }

        private bool TryResolveSelectedMediaLot(out int mediaLotId, out int mediaId, out DataRow? mediaRow)
        {
            mediaLotId = 0;
            mediaId = 0;
            mediaRow = null;

            if (CmbPreparationMediaLot != null && CmbPreparationMediaLot.SelectedItem is DataRowView selectedLotView)
            {
                DataRow comboRow = selectedLotView.Row;
                mediaLotId = RowInt(comboRow, "MediaLotID");
                mediaId = RowInt(comboRow, "MediaID");
                mediaRow = comboRow;

                if (mediaLotId > 0 && mediaId > 0)
                {
                    _selectedLotId = mediaLotId;
                    return true;
                }
            }

            if (CmbPreparationMediaLot != null && CmbPreparationMediaLot.SelectedValue != null &&
                int.TryParse(CmbPreparationMediaLot.SelectedValue.ToString(), out int comboLotId) && comboLotId > 0)
            {
                DataRow? comboGridRow = FindReceiptRowByLotId(comboLotId);
                if (comboGridRow != null)
                {
                    mediaLotId = RowInt(comboGridRow, "MediaLotID");
                    mediaId = RowInt(comboGridRow, "MediaID");
                    mediaRow = comboGridRow;

                    if (mediaLotId > 0 && mediaId > 0)
                    {
                        _selectedLotId = mediaLotId;
                        return true;
                    }
                }

                _selectedLotId = comboLotId;
            }

            DataRow? selectedRow = RowFromGrid(DgReceipts);
            if (selectedRow != null)
            {
                mediaLotId = RowInt(selectedRow, "MediaLotID");
                mediaId = RowInt(selectedRow, "MediaID");
                mediaRow = selectedRow;

                if (mediaLotId > 0 && mediaId > 0)
                {
                    _selectedLotId = mediaLotId;
                    return true;
                }
            }

            if (_selectedLotId > 0)
            {
                DataRow? rowFromGrid = FindReceiptRowByLotId(_selectedLotId);
                if (rowFromGrid != null)
                {
                    mediaLotId = RowInt(rowFromGrid, "MediaLotID");
                    mediaId = RowInt(rowFromGrid, "MediaID");
                    mediaRow = rowFromGrid;

                    if (mediaLotId > 0 && mediaId > 0)
                        return true;
                }


                DataTable data = _repository.Load(CultureMediaQuery.ResolveMediaLotBySelectedLotId,
                    new SqlParameter("@MediaLotID", SqlDbType.Int) { Value = _selectedLotId });

                if (data.Rows.Count > 0)
                {
                    mediaRow = data.Rows[0];
                    mediaLotId = RowInt(mediaRow, "MediaLotID");
                    mediaId = RowInt(mediaRow, "MediaID");

                    if (mediaLotId > 0 && mediaId > 0)
                        return true;
                }
            }

            string visibleLotNo = txtDetailLotNo?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(visibleLotNo))
            {

                DataTable data = _repository.Load(CultureMediaQuery.ResolveMediaLotByMediaId,
                    new SqlParameter("@LotNumber", SqlDbType.NVarChar, 100) { Value = visibleLotNo });

                if (data.Rows.Count > 0)
                {
                    mediaRow = data.Rows[0];
                    mediaLotId = RowInt(mediaRow, "MediaLotID");
                    mediaId = RowInt(mediaRow, "MediaID");

                    if (mediaLotId > 0 && mediaId > 0)
                    {
                        _selectedLotId = mediaLotId;
                        return true;
                    }
                }
            }

            return false;
        }

        private DataRow? FindReceiptRowByLotId(int lotId)
        {
            if (lotId <= 0 || DgReceipts == null || DgReceipts.ItemsSource == null)
                return null;

            if (DgReceipts.ItemsSource is DataView view)
            {
                foreach (DataRowView rowView in view)
                {
                    DataRow row = rowView.Row;
                    if (RowInt(row, "MediaLotID") == lotId)
                        return row;
                }
            }

            return null;
        }

        // ==================== PREPARATION ====================

        private void BtnNewPreparation_Click(object sender, RoutedEventArgs e)
        {
            _selectedPreparationId = 0;
            TxtPreparationNo.Text = "Generated on Save";
            DpPreparationDate.SelectedDate = DateTime.Today;
            DpPreparationExpiry.SelectedDate = DateTime.Today.AddDays(14);
            TxtQuantityPrepared.Clear();
            TxtQuantityWeighedG.Clear();
            TxtPreparedBy.Text = string.Empty;
            TxtBatchSize.Clear();
            TxtFinalPH.Clear();
            TxtAppearance.Clear();
            TxtAutoclaveCycleNo.Clear();
            TxtAutoclaveTemperature.Clear();
            TxtAutoclaveHoldingTime.Clear();
            TxtPreparationLoadNo.Clear();
            TxtPreparationMpmNo.Clear();
            TxtPreparationPurifiedWaterMl.Clear();
            TxtPreparationQuantityDispensed.Clear();
            TxtPreparationBalanceNo.Clear();
            TxtPreparationEquipmentCode.Clear();
            TxtPreparationPhMeterNo.Clear();
            TxtPreparationSterilizationMethod.Clear();
            TxtPreparationAdditives.Clear();
            TxtPreparationVisualEquipmentCode.Clear();
            TxtPreparationVisualObservations.Clear();
            SetComboText(CmbPreparationVisualConclusion, "Pending");
            TxtPreparationVisualCheckedBy.Text = string.Empty;
            SetComboText(CmbPreparationSterilityReview, "Pending");
            TxtPreparationReviewedBy.Text = string.Empty;
            TxtPreparationRemarks.Clear();
            if (TxtPreparationReleaseStatus != null) TxtPreparationReleaseStatus.Text = "Under Release";
            _selectedPreparationPrintId = 0;
            DgPreparations.SelectedItem = null;

            if (CmbPreparationMediaLot != null && _selectedLotId > 0)
                CmbPreparationMediaLot.SelectedValue = _selectedLotId;

            ShowToast("New preparation ready", "📋");
            SetStatus("New preparation ready");
        }

        private void BtnSavePreparation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaEntryPermission();
                if (!ValidatePreparation())
                    return;

                if (!TryResolveSelectedMediaLot(out int mediaLotId, out int mediaId, out DataRow? mediaRow))
                {
                    MessageBox.Show("Select Stored Media Batch in the Media Preparation tab first.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!ValidateMediaLotForPreparation(mediaLotId, DpPreparationDate.SelectedDate!.Value, DpPreparationExpiry.SelectedDate))
                    return;
                if (_selectedPreparationId > 0 && !IsPreparationUnderRelease(_selectedPreparationId))
                {
                    MessageBox.Show(
                        "Released or rejected preparations are locked. Click New Preparation to start a separate batch.",
                        "Culture Media",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                bool isNew = _selectedPreparationId == 0;
                if (!isNew && HasSignedPreparationControls(_selectedPreparationId))
                {
                    MessageBox.Show(
                        "This preparation is locked because a visual check or sterility review has already been electronically signed. Reject the record and create a new preparation when a correction is required.",
                        "Signed Preparation Locked",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string preparationNo = isNew ? _repository.GenerateNumber("MediaPreparation", "MP") : TxtPreparationNo.Text.Trim();
                string signatureAction = isNew ? "Prepare Culture Media" : "Amend Culture Media Preparation";
                if (!ConfirmCultureMediaSignature(signatureAction, preparationNo, _selectedPreparationId, out string signedBy, out string signatureReason))
                    return;

                TxtPreparedBy.Text = signedBy;
                decimal powderQuantityG = ParseGramQuantity(TxtQuantityWeighedG.Text);
                int savedPreparationId = _selectedPreparationId;

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    if (isNew)
                    {
                        const string insertSql = @"
INSERT INTO dbo.MediaPreparations
(MediaPreparationNo, MediaID, MediaLotID, PreparationDate, ExpiryDate, QuantityPrepared, PowderQuantityG, PreparedBy, BatchSize, FinalPH, Appearance, AutoclaveCycleNo, AutoclaveTemperature, AutoclaveHoldingTime, SterilityReview, ReleaseStatus, Remarks)
OUTPUT INSERTED.MediaPreparationID
VALUES
(@MediaPreparationNo, @MediaID, @MediaLotID, @PreparationDate, @ExpiryDate, @QuantityPrepared, @PowderQuantityG, @PreparedBy, @BatchSize, @FinalPH, @Appearance, @AutoclaveCycleNo, @AutoclaveTemperature, @AutoclaveHoldingTime, 'Pending', 'Under Release', @Remarks);";

                        object? id = ExecuteScalarInTransaction(conn, tx, insertSql, BuildPreparationParameters(preparationNo, mediaId, mediaLotId));
                        savedPreparationId = Convert.ToInt32(id ?? throw new InvalidOperationException("The media preparation ID was not returned by the database."), CultureInfo.InvariantCulture);
                        _pendingSignatureEntityId = savedPreparationId;
                        _pendingSignatureRecordNumber = preparationNo;
                        AdjustCultureMediaStockInTransaction(conn, tx, mediaLotId, savedPreparationId, preparationNo, 0m, powderQuantityG, signatureReason);
                    }
                    else
                    {
                        decimal originalPowderQuantityG;
                        int originalLotId;
                        using (var lockCommand = new SqlCommand(@"
SELECT MediaLotID, ISNULL(PowderQuantityG, 0)
FROM dbo.MediaPreparations WITH (UPDLOCK, HOLDLOCK)
WHERE MediaPreparationID = @MediaPreparationID
  AND ReleaseStatus = 'Under Release';", conn, tx))
                        {
                            lockCommand.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = savedPreparationId;
                            using SqlDataReader reader = lockCommand.ExecuteReader();
                            if (!reader.Read())
                                throw new DBConcurrencyException("Only preparations under release can be amended.");
                            originalLotId = reader.GetInt32(0);
                            originalPowderQuantityG = reader.GetDecimal(1);
                        }
                        if (originalLotId != mediaLotId)
                            throw new InvalidOperationException("The source dehydrated media lot cannot be changed after the preparation is first saved.");

                        const string updateSql = @"
UPDATE dbo.MediaPreparations
SET MediaID = @MediaID,
    MediaLotID = @MediaLotID,
    PreparationDate = @PreparationDate,
    ExpiryDate = @ExpiryDate,
    QuantityPrepared = @QuantityPrepared,
    PowderQuantityG = @PowderQuantityG,
    PreparedBy = @PreparedBy,
    BatchSize = @BatchSize,
    FinalPH = @FinalPH,
    Appearance = @Appearance,
    AutoclaveCycleNo = @AutoclaveCycleNo,
    AutoclaveTemperature = @AutoclaveTemperature,
    AutoclaveHoldingTime = @AutoclaveHoldingTime,
    Remarks = @Remarks,
    UpdatedAt = SYSUTCDATETIME()
WHERE MediaPreparationID = @MediaPreparationID
  AND ReleaseStatus = 'Under Release';";
                        int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(
                            updateSql,
                            BuildPreparationParameters(preparationNo, mediaId, mediaLotId, savedPreparationId),
                            conn,
                            tx);
                        if (affected != 1)
                            throw new DBConcurrencyException("The preparation changed before the amendment could be saved.");

                        AdjustCultureMediaStockInTransaction(
                            conn, tx, mediaLotId, savedPreparationId, preparationNo,
                            originalPowderQuantityG, powderQuantityG, signatureReason);
                    }

                    SavePreparationSopFieldsInTransaction(conn, tx, savedPreparationId);
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(
                        conn, tx, "MediaPreparations", savedPreparationId,
                        isNew ? "Media Preparation Created" : "Media Preparation Amended",
                        isNew ? string.Empty : "Under Release", "Under Release",
                        signatureReason, signedBy, preparationNo);
                });

                ClearPendingCultureMediaSignature();
                _selectedPreparationId = savedPreparationId;
                _selectedPreparationPrintId = savedPreparationId;
                TxtPreparationNo.Text = preparationNo;
                if (TxtPreparationReleaseStatus != null)
                    TxtPreparationReleaseStatus.Text = "Under Release";

                UpdatePreparationNextAction();
                _ = LoadAllDataAsync();
                ShowToast(isNew ? "Preparation saved and signed" : "Preparation amendment saved and signed", "✅");
                SetStatus("Preparation saved with electronic signature and stock traceability");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error saving media preparation", ex);
            }
        }

        private void BtnSignPreparationVisualCheck_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReviewPermission();
                if (_selectedPreparationId <= 0 || !IsPreparationUnderRelease(_selectedPreparationId))
                {
                    MessageBox.Show("Select a saved preparation under release first.", "Visual Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (HasVisualCheckSignature(_selectedPreparationId))
                {
                    MessageBox.Show("The visual check has already been electronically signed and is locked.", "Visual Check", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (string.IsNullOrWhiteSpace(TxtPreparationVisualEquipmentCode.Text) ||
                    string.IsNullOrWhiteSpace(TxtPreparationVisualObservations.Text) ||
                    ComboText(CmbPreparationVisualConclusion).Equals("Pending", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(ComboText(CmbPreparationVisualConclusion)))
                {
                    MessageBox.Show("Equipment Code, Visual Observations, and a final Visual Check Conclusion are required.", "Visual Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!ConfirmCultureMediaSignature("Visual Check Prepared Media", TxtPreparationNo.Text, _selectedPreparationId, out string signedBy, out string reason))
                    return;

                string preparedBy = GetPreparationPreparedBy(_selectedPreparationId);
                if (!IsDevelopmentAdminOverride() && string.Equals(preparedBy, signedBy, StringComparison.OrdinalIgnoreCase))
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show("The preparer cannot independently sign the visual check.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn,
                        tx,
                        signedBy,
                        "CanReviewResults",
                        "sign prepared-media visual review");

                    string lockedPreparedBy;
                    string lockedReleaseStatus;
                    string lockedVisualCheckedBy;
                    using (SqlCommand workflowCommand = new SqlCommand(@"
SELECT ISNULL(PreparedBy,N''), ISNULL(ReleaseStatus,N''), ISNULL(VisualCheckedBy,N'')
FROM dbo.MediaPreparations WITH (UPDLOCK, HOLDLOCK)
WHERE MediaPreparationID = @MediaPreparationID;", conn, tx))
                    {
                        workflowCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        workflowCommand.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = _selectedPreparationId;
                        using SqlDataReader reader = workflowCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected media preparation no longer exists.");
                        lockedPreparedBy = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        lockedReleaseStatus = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        lockedVisualCheckedBy = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                    }

                    if (!lockedReleaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(lockedVisualCheckedBy))
                    {
                        throw new DBConcurrencyException("The preparation is no longer available for visual-check sign-off.");
                    }

                    if (!IsDevelopmentAdminOverrideForRole(signerRole) &&
                        string.Equals(lockedPreparedBy, signedBy.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The preparer cannot independently sign the visual check.");
                    }

                    DateTime checkedAtUtc = ReadAuthoritativeUtcInTransaction(conn, tx);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "EquipmentCode", TxtPreparationVisualEquipmentCode.Text);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "VisualObservations", TxtPreparationVisualObservations.Text);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "Conclusion", ComboText(CmbPreparationVisualConclusion));
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "CheckedBy", signedBy);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "CheckedAtUtc", checkedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaPreparations
SET VisualCheckedBy = @VisualCheckedBy,
    VisualCheckedAt = @CheckedAtUtc,
    UpdatedAt = @CheckedAtUtc
WHERE MediaPreparationID = @MediaPreparationID
  AND ReleaseStatus = 'Under Release'
  AND VisualCheckedBy IS NULL
  AND VisualCheckedAt IS NULL;",
                        new[]
                        {
                            new SqlParameter("@VisualCheckedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                            new SqlParameter("@CheckedAtUtc", SqlDbType.DateTime2) { Value = checkedAtUtc },
                            new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = _selectedPreparationId }
                        }, conn, tx);
                    if (affected != 1)
                        throw new DBConcurrencyException("The preparation is no longer available for visual-check sign-off.");
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "MediaPreparations", _selectedPreparationId,
                        "Prepared Media Visual Check Signed", "Pending", ComboText(CmbPreparationVisualConclusion),
                        reason, signedBy, TxtPreparationNo.Text);
                });
                ClearPendingCultureMediaSignature();
                TxtPreparationVisualCheckedBy.Text = signedBy;
                UpdatePreparationNextAction();
                ShowToast("Visual check signed", "✅");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error signing visual check", ex);
            }
        }

        private void BtnSignPreparationSterilityReview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReviewPermission();
                if (_selectedPreparationId <= 0 || !IsPreparationUnderRelease(_selectedPreparationId))
                {
                    MessageBox.Show("Select a saved preparation under release first.", "Sterility Review", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (HasSterilityReviewSignature(_selectedPreparationId))
                {
                    MessageBox.Show("The sterility review has already been electronically signed and is locked.", "Sterility Review", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string reviewResult = NormalizeSterilityReview(ComboText(CmbPreparationSterilityReview));
                if (!reviewResult.Equals("Passed", StringComparison.OrdinalIgnoreCase) &&
                    !reviewResult.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Select Passed or Failed before signing the sterility review.", "Sterility Review", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!ConfirmCultureMediaSignature("Sterility Review Prepared Media", TxtPreparationNo.Text, _selectedPreparationId, out string signedBy, out string reason))
                    return;

                string preparedBy = GetPreparationPreparedBy(_selectedPreparationId);
                if (!IsDevelopmentAdminOverride() && string.Equals(preparedBy, signedBy, StringComparison.OrdinalIgnoreCase))
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show("The preparer cannot independently sign the sterility review.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        conn,
                        tx,
                        signedBy,
                        "CanReviewResults",
                        "sign prepared-media sterility review");

                    string lockedPreparedBy;
                    string lockedReleaseStatus;
                    string lockedSterilityReviewedBy;
                    using (SqlCommand workflowCommand = new SqlCommand(@"
SELECT ISNULL(PreparedBy,N''), ISNULL(ReleaseStatus,N''), ISNULL(SterilityReviewedBy,N'')
FROM dbo.MediaPreparations WITH (UPDLOCK, HOLDLOCK)
WHERE MediaPreparationID = @MediaPreparationID;", conn, tx))
                    {
                        workflowCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        workflowCommand.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = _selectedPreparationId;
                        using SqlDataReader reader = workflowCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected media preparation no longer exists.");
                        lockedPreparedBy = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        lockedReleaseStatus = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        lockedSterilityReviewedBy = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                    }

                    if (!lockedReleaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(lockedSterilityReviewedBy))
                    {
                        throw new DBConcurrencyException("The preparation is no longer available for sterility-review sign-off.");
                    }

                    if (!IsDevelopmentAdminOverrideForRole(signerRole) &&
                        string.Equals(lockedPreparedBy, signedBy.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("The preparer cannot independently sign the sterility review.");
                    }

                    DateTime reviewedAtUtc = ReadAuthoritativeUtcInTransaction(conn, tx);
                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaPreparations
SET SterilityReview = @SterilityReview,
    SterilityReviewedBy = @ReviewedBy,
    SterilityReviewedAt = @ReviewedAtUtc,
    UpdatedAt = @ReviewedAtUtc
WHERE MediaPreparationID = @MediaPreparationID
  AND ReleaseStatus = 'Under Release'
  AND SterilityReviewedBy IS NULL
  AND SterilityReviewedAt IS NULL;",
                        new[]
                        {
                            new SqlParameter("@SterilityReview", SqlDbType.NVarChar, 30) { Value = reviewResult },
                            new SqlParameter("@ReviewedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                            new SqlParameter("@ReviewedAtUtc", SqlDbType.DateTime2) { Value = reviewedAtUtc },
                            new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = _selectedPreparationId }
                        }, conn, tx);
                    if (affected != 1)
                        throw new DBConcurrencyException("The preparation is no longer available for sterility-review sign-off.");
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "SterilityReview", reviewResult);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "SterilityReviewedBy", signedBy);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", _selectedPreparationId, "1035-L-0005/A7", "SterilityReviewedAtUtc", reviewedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "MediaPreparations", _selectedPreparationId,
                        "Prepared Media Sterility Review Signed", "Pending", reviewResult, reason, signedBy,
                        TxtPreparationNo.Text);
                });
                ClearPendingCultureMediaSignature();
                TxtPreparationReviewedBy.Text = signedBy;
                UpdatePreparationNextAction();
                ShowToast("Sterility review signed", "✅");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error signing sterility review", ex);
            }
        }

        private string GetPreparationPreparedBy(int preparationId)
        {
            object? value = _repository.ReadScalar(
                CultureMediaScalar.PreparationPreparedBy,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });
            return value == null || value == DBNull.Value ? string.Empty : value.ToString()?.Trim() ?? string.Empty;
        }

        private bool HasSignedPreparationControls(int preparationId)
        {
            object? value = _repository.ReadScalar(CultureMediaScalar.HasSignedPreparationControls,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });
            return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }

        private bool HasVisualCheckSignature(int preparationId)
        {
            object? value = _repository.ReadScalar(CultureMediaScalar.HasVisualCheckSignature,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });
            return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }

        private bool HasSterilityReviewSignature(int preparationId)
        {
            object? value = _repository.ReadScalar(CultureMediaScalar.HasSterilityReviewSignature,
                new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = preparationId });
            return value != null && value != DBNull.Value && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
        }

        private void BtnReleasePreparation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                RequireCultureMediaReleasePermission();
                if (_selectedPreparationId <= 0 || !IsPreparationUnderRelease(_selectedPreparationId))
                {
                    MessageBox.Show("Select a saved preparation with status Under Release first.", "Culture Media", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!ValidatePreparationReleaseGate() || !ValidatePreparationSourceLotForRelease(_selectedPreparationId))
                    return;
                if (MessageBox.Show("Release this prepared media batch for use?", "Release Preparation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                if (!ConfirmCultureMediaSignature("Final Release Prepared Media", TxtPreparationNo.Text, _selectedPreparationId, out string signedBy, out string signatureReason))
                    return;

                DataTable gate = LoadPreparationReleaseGateRecord(_selectedPreparationId);
                if (gate.Rows.Count != 1)
                    throw new InvalidOperationException("The preparation release record was not found.");
                DataRow gateRow = gate.Rows[0];
                string preparedBy = RowString(gateRow, "PreparedBy");
                string visualCheckedBy = RowString(gateRow, "VisualCheckedBy");
                string sterilityReviewedBy = RowString(gateRow, "SterilityReviewedBy");
                if (!IsDevelopmentAdminOverride() &&
                    (string.Equals(preparedBy, signedBy, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(visualCheckedBy, signedBy, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(sterilityReviewedBy, signedBy, StringComparison.OrdinalIgnoreCase)))
                {
                    ClearPendingCultureMediaSignature();
                    MessageBox.Show("The final releaser must be independent of the preparer, visual checker, and sterility reviewer.", "Segregation of Duties", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int releasedPreparationId = _selectedPreparationId;
                DatabaseHelper.ExecuteInTransaction((conn, tx) =>
                {
                    string signerRole = DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                        conn,
                        tx,
                        signedBy,
                        "release prepared culture media");

                    string lockedPreparedBy;
                    string lockedVisualCheckedBy;
                    string lockedSterilityReviewedBy;
                    string lockedReleaseStatus;
                    using (SqlCommand workflowCommand = new SqlCommand(@"
SELECT ISNULL(PreparedBy,N''),
       ISNULL(VisualCheckedBy,N''),
       ISNULL(SterilityReviewedBy,N''),
       ISNULL(ReleaseStatus,N'')
FROM dbo.MediaPreparations WITH (UPDLOCK, HOLDLOCK)
WHERE MediaPreparationID = @MediaPreparationID;", conn, tx))
                    {
                        workflowCommand.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                        workflowCommand.Parameters.Add("@MediaPreparationID", SqlDbType.Int).Value = releasedPreparationId;
                        using SqlDataReader reader = workflowCommand.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The selected media preparation no longer exists.");
                        lockedPreparedBy = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                        lockedVisualCheckedBy = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                        lockedSterilityReviewedBy = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                        lockedReleaseStatus = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                    }

                    if (!lockedReleaseStatus.Equals("Under Release", StringComparison.OrdinalIgnoreCase))
                        throw new DBConcurrencyException("The preparation is no longer Under Release.");

                    if (!IsDevelopmentAdminOverrideForRole(signerRole) &&
                        (string.Equals(lockedPreparedBy, signedBy.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(lockedVisualCheckedBy, signedBy.Trim(), StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(lockedSterilityReviewedBy, signedBy.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            "The final releaser must be independent of the preparer, visual checker, and sterility reviewer.");
                    }

                    ValidatePreparationReleaseGateInTransaction(conn, tx, releasedPreparationId);
                    DateTime releasedAtUtc = ReadAuthoritativeUtcInTransaction(conn, tx);
                    int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
UPDATE dbo.MediaPreparations
SET ReleaseStatus = 'Released',
    ReleasedBy = @ReleasedBy,
    ReleasedAt = @ReleasedAtUtc,
    Remarks = CONCAT(ISNULL(Remarks, ''), CHAR(13) + CHAR(10), 'Preparation released after signed visual and sterility review.'),
    UpdatedAt = @ReleasedAtUtc
WHERE MediaPreparationID = @MediaPreparationID
  AND ReleaseStatus = 'Under Release'
  AND SterilityReview = 'Passed'
  AND SterilityReviewedBy IS NOT NULL
  AND VisualCheckedBy IS NOT NULL
  AND ExpiryDate IS NOT NULL
  AND ExpiryDate >= CAST(GETDATE() AS date);",
                        new[]
                        {
                            new SqlParameter("@ReleasedBy", SqlDbType.NVarChar, 100) { Value = signedBy },
                            new SqlParameter("@ReleasedAtUtc", SqlDbType.DateTime2) { Value = releasedAtUtc },
                            new SqlParameter("@MediaPreparationID", SqlDbType.Int) { Value = releasedPreparationId }
                        }, conn, tx);
                    if (affected != 1)
                        throw new DBConcurrencyException("The preparation is no longer eligible for final release.");
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", releasedPreparationId, "1035-L-0005/A7", "ReleasedBy", signedBy);
                    SaveSopFieldInTransaction(conn, tx, "MediaPreparation", releasedPreparationId, "1035-L-0005/A7", "ReleasedAtUtc", releasedAtUtc.ToString("O", CultureInfo.InvariantCulture));
                    StorePendingCultureMediaSignatureInTransaction(conn, tx);
                    AddCultureMediaAuditInTransaction(conn, tx, "MediaPreparations", releasedPreparationId,
                        "Prepared Media Finally Released", "Under Release", "Released", signatureReason, signedBy,
                        TxtPreparationNo.Text);
                });
                ClearPendingCultureMediaSignature();

                _selectedPreparationPrintId = releasedPreparationId;
                _selectedPreparationId = 0;
                if (TxtPreparationReleaseStatus != null)
                    TxtPreparationReleaseStatus.Text = "Released";
                UpdatePreparationNextAction();
                _ = LoadAllDataAsync();
                ShowToast("Prepared media released for use", "✅");
                SetStatus("Prepared media released");
            }
            catch (Exception ex)
            {
                ClearPendingCultureMediaSignature();
                ShowError("Error releasing media preparation", ex);
            }
        }
    }
}
