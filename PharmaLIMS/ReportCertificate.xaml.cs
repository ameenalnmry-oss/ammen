#nullable disable
using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.ComponentModel;
using System.Windows.Data;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Printing;
using System.Threading.Tasks;

namespace PharmaLIMS
{
    public partial class ReportCertificate : Window
    {
        private int sampleId;
        private string currentUser = "";
        private string certificateNumber = "";
        private string currentSampleStatus = "";
        private string currentPointCodeForSpec = "";
        private string currentSampleType = "";
        private readonly List<TestResultDisplay> currentResults = new List<TestResultDisplay>();
        private bool certificateIssuanceInProgress;
        private int? _controlledReissueFromCertificateId;
        private int _controlledLegacyReconciliationId;
        private string _controlledLegacyCertificateNumber = "";
        private string _controlledLegacyReissueReason = "";

        // ===== Parameterless constructor for DI =====
        public ReportCertificate()
        {
            InitializeComponent();
            currentUser = GetCurrentUserName();
        }

        // ===== Constructor with sampleId =====
        public ReportCertificate(int sampleId) : this()
        {
            this.sampleId = sampleId;
            Loaded += ReportCertificate_Loaded;
        }

        public void ConfigureControlledLegacyReissue(
            int legacyCertificateId,
            int reconciliationId,
            string legacyCertificateNumber,
            string reconciliationReason)
        {
            if (legacyCertificateId <= 0)
                throw new ArgumentOutOfRangeException(nameof(legacyCertificateId));
            if (reconciliationId <= 0)
                throw new ArgumentOutOfRangeException(nameof(reconciliationId));
            if (string.IsNullOrWhiteSpace(legacyCertificateNumber))
                throw new ArgumentException("Legacy certificate number is required.", nameof(legacyCertificateNumber));
            if (string.IsNullOrWhiteSpace(reconciliationReason))
                throw new ArgumentException("Signed reconciliation reason is required.", nameof(reconciliationReason));

            _controlledReissueFromCertificateId = legacyCertificateId;
            _controlledLegacyReconciliationId = reconciliationId;
            _controlledLegacyCertificateNumber = legacyCertificateNumber.Trim();
            _controlledLegacyReissueReason = reconciliationReason.Trim();
        }

        private async void ReportCertificate_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ReportCertificate_Loaded;
            Mouse.OverrideCursor = Cursors.Wait;
            BtnIssueCertificate.IsEnabled = false;
            BtnPrint.IsEnabled = false;

            try
            {
                await LoadReportDataAsync();
                if (_controlledReissueFromCertificateId.HasValue && BtnIssueCertificate.Visibility == Visibility.Visible)
                {
                    BtnIssueCertificate.Content = IsPotableWater(currentSampleType)
                        ? "Issue Replacement Report"
                        : "Issue Replacement COA";
                    BtnIssueCertificate.ToolTip = $"Controlled replacement for legacy certificate {_controlledLegacyCertificateNumber}; reconciliation #{_controlledLegacyReconciliationId}.";
                }

                bool canPrint = await Task.Run(CanPrint);
                BtnPrint.IsEnabled = canPrint;
                BtnPrint.ToolTip = canPrint
                    ? "Print controlled report."
                    : "You don't have permission to print controlled reports.";
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private string GetCurrentUserName()
        {
            string username = Login.CurrentUser ?? "";
            if (!string.IsNullOrWhiteSpace(username))
                return username.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to open, issue, or print a certificate.");
        }

        private bool IsPurifiedWater(string sampleType)
        {
            sampleType = sampleType == null ? "" : sampleType.Trim().ToLowerInvariant();

            return sampleType == "purified water" ||
                   sampleType == "purified" ||
                   sampleType == "pw" ||
                   sampleType.Contains("purified") ||
                   sampleType.Contains("pws");
        }

        private bool IsPotableWater(string sampleType)
        {
            sampleType = sampleType == null ? "" : sampleType.Trim().ToLowerInvariant();

            return sampleType == "potable water" ||
                   sampleType == "potable" ||
                   sampleType == "drinking water" ||
                   sampleType.Contains("potable") ||
                   sampleType.Contains("drinking") ||
                   sampleType.Contains("ptws");
        }

        private bool IsRemovedTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName == "taste" ||
                   testName == "odor" ||
                   testName == "odour" ||
                   testName == "taste & odor" ||
                   testName == "taste and odor" ||
                   testName == "taste & odour" ||
                   testName == "taste and odour";
        }

        private bool IsAppearanceTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName.Contains("appearance") ||
                   testName.Contains("color") ||
                   testName.Contains("colour") ||
                   testName.Contains("clarity");
        }

        private bool IsPhTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName == "ph" ||
                   testName.Contains("ph value") ||
                   testName.Contains("ph test") ||
                   testName.Contains("ph ");
        }

        private bool IsConductivityTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();
            return testName.Contains("conductivity");
        }


        private bool IsHardnessTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName.Contains("hardness") ||
                   (testName.Contains("calcium") && testName.Contains("magnesium"));
        }

        private bool IsResidualChlorineTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName.Contains("residual chlorine") ||
                   testName.Contains("free chlorine") ||
                   testName == "chlorine";
        }

        private bool IsPathogenTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName.Contains("e. coli") ||
                   testName.Contains("salmonella") ||
                   testName.Contains("staphylococcus") ||
                   testName.Contains("pseudomonas") ||
                   testName.Contains("burkholderia") ||
                   testName.Contains("candida") ||
                   testName.Contains("clostridia") ||
                   testName.Contains("bile tolerant") ||
                   testName.Contains("gram-negative") ||
                   testName.Contains("aureus");
        }

        private bool IsTotalBacterialCountTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName.Contains("tamc") ||
                   testName.Contains("total aerobic microbial count") ||
                   testName.Contains("total bacterial count") ||
                   testName.Contains("total microbial count") ||
                   testName.Contains("aerobic microbial count");
        }

        private string FormatDateTimeForCertificate(object value)
        {
            if (value == null || value == DBNull.Value)
                return "";

            try
            {
                DateTime dt = Convert.ToDateTime(value);
                return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }


        private string FormatDateTimeOrNotRecorded(object value)
        {
            string formatted = FormatDateTimeForCertificate(value);
            return string.IsNullOrWhiteSpace(formatted) ? "Not recorded" : formatted;
        }

        private bool TableColumnExistsForReport(string tableName, string columnName)
        {
            try
            {
                string query = @"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = 'dbo'
                      AND TABLE_NAME = @TableName
                      AND COLUMN_NAME = @ColumnName";

                SqlParameter[] pars =
                {
                    new SqlParameter("@TableName", SqlDbType.NVarChar, 128) { Value = tableName },
                    new SqlParameter("@ColumnName", SqlDbType.NVarChar, 128) { Value = columnName }
                };

                object result = DatabaseHelper.ExecuteScalar(query, pars);
                return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureGlobalWaterDateColumnsForReport()
        {
            string[] requiredColumns =
            {
                "ReceivedDateTime", "AnalysisStartedDateTime", "AnalysisCompletedDateTime"
            };

            foreach (string column in requiredColumns)
            {
                if (!TableColumnExistsForReport("Samples", column))
                {
                    ApplicationLogger.Warning(
                        "Certificate timestamp column dbo.Samples." + column + " is missing. Apply the controlled database migration.");
                }
            }
        }

        private string FirstFormattedDate(DataRow row, params string[] columnNames)
        {
            if (row == null || columnNames == null)
                return "Not recorded";

            foreach (string columnName in columnNames)
            {
                if (row.Table.Columns.Contains(columnName))
                {
                    string formatted = FormatDateTimeForCertificate(row[columnName]);
                    if (!string.IsNullOrWhiteSpace(formatted))
                        return formatted;
                }
            }

            return "Not recorded";
        }

        private string GetShortHash(string hash)
        {
            hash = hash == null ? "" : hash.Trim();

            if (string.IsNullOrWhiteSpace(hash))
                return "Pending issuance";

            return hash.Length > 24 ? hash.Substring(0, 24) + "..." : hash;
        }

        private void UpdateCertificateHeader(
            string certNo,
            object issueDateValue,
            string verificationCode = "",
            object revisionNoValue = null,
            string certificateStatus = "",
            string reportHash = "")
        {
            certificateNumber = certNo == null ? "" : certNo.Trim();

            string displayedCertificateNumber = string.IsNullOrWhiteSpace(certificateNumber)
                ? "Pending issuance"
                : certificateNumber;

            string displayedVerificationCode = string.IsNullOrWhiteSpace(verificationCode)
                ? displayedCertificateNumber
                : verificationCode.Trim();

            string revisionText = revisionNoValue == null || revisionNoValue == DBNull.Value
                ? "Pending issuance"
                : revisionNoValue.ToString();

            string statusText = string.IsNullOrWhiteSpace(certificateStatus)
                ? "Pending issuance"
                : certificateStatus.Trim();

            bool isPotable = IsPotableWater(currentSampleType);
            string documentTerm = isPotable ? "Report" : "Certificate";

            lblCertificateNumber.Text = documentTerm + " No: " + displayedCertificateNumber;

            string issueDateText = FormatDateTimeForCertificate(issueDateValue);

            lblIssueDate.Text = "Issue Date: " +
                                (string.IsNullOrWhiteSpace(issueDateText) ? "Pending issuance" : issueDateText);

            lblVerificationCode.Text = "Verification Code: " + displayedVerificationCode;

            if (lblRevisionNo != null)
                lblRevisionNo.Text = "Revision No: " + revisionText;

            if (lblCertificateStatus != null)
                lblCertificateStatus.Text = documentTerm + " Status: " + statusText;

            if (lblReportHash != null)
            {
                lblReportHash.Text = "Document Hash: " + GetShortHash(reportHash);
                lblReportHash.ToolTip = string.IsNullOrWhiteSpace(reportHash) ? null : reportHash;
            }
        }

        private int ExtractPointNumber(string pointCode)
        {
            pointCode = pointCode == null ? "" : pointCode.Trim();

            if (string.IsNullOrWhiteSpace(pointCode))
                return 0;

            Match match = Regex.Match(pointCode, @"(\d+)");

            if (match.Success && int.TryParse(match.Groups[1].Value, out int pointNumber))
                return pointNumber;

            return 0;
        }

        private bool IsSoftWaterPoint(string sampleType)
        {
            if (!IsPotableWater(sampleType))
                return false;

            int pointNumber = ExtractPointNumber(currentPointCodeForSpec);
            return pointNumber >= 5;
        }

        private bool IsOosSampleStatus(string status)
        {
            status = status == null ? "" : status.Trim();

            return status.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Failed", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsSystemGenericName(string name)
        {
            name = name == null ? "" : name.Trim().ToLowerInvariant();

            return string.IsNullOrWhiteSpace(name) ||
                   name == "unknown";
        }

        private string NormalizeEnglishName(string name)
        {
            name = name == null ? "" : name.Trim();

            if (string.IsNullOrWhiteSpace(name))
                return "";

            if (IsSystemGenericName(name))
                return "";

            return name;
        }

        private string ChoosePersonName(string primary, string fallback)
        {
            string normalizedPrimary = NormalizeEnglishName(primary);
            string normalizedFallback = NormalizeEnglishName(fallback);

            if (!string.IsNullOrWhiteSpace(normalizedPrimary))
                return normalizedPrimary;

            if (!string.IsNullOrWhiteSpace(normalizedFallback))
                return normalizedFallback;

            if (!string.IsNullOrWhiteSpace(primary))
                return primary.Trim();

            if (!string.IsNullOrWhiteSpace(fallback))
                return fallback.Trim();

            return "";
        }

        private string GetSampledByDirect(string fallback)
        {
            try
            {
                string query = @"
                    SELECT TOP 1 SampledBy
                    FROM Samples
                    WHERE SampleID = @SampleID";

                SqlParameter[] pars =
                {
                    new SqlParameter("@SampleID", sampleId)
                };

                object result = DatabaseHelper.ExecuteScalar(query, pars);

                if (result != null && result != DBNull.Value)
                {
                    string value = result.ToString() ?? "";

                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Trim();
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to read SampledBy while building the certificate. The fallback value will be used.", ex);
            }

            return string.IsNullOrWhiteSpace(fallback) ? "N/A" : fallback.Trim();
        }

        private bool IsAbsencePresenceTest(string unit, string testName)
        {
            unit = unit == null ? "" : unit.Trim().ToLowerInvariant();
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            if (unit == "absence")
                return true;

            return testName.Contains("e. coli") ||
                   testName.Contains("salmonella") ||
                   testName.Contains("staphylococcus") ||
                   testName.Contains("pseudomonas") ||
                   testName.Contains("burkholderia") ||
                   testName.Contains("candida") ||
                   testName.Contains("clostridia") ||
                   testName.Contains("bile tolerant") ||
                   testName.Contains("gram-negative") ||
                   testName.Contains("aureus");
        }

        private bool IsComplianceQualitativeTest(string testName)
        {
            string normalized = (testName ?? string.Empty).Trim().ToLowerInvariant();

            if (normalized.Contains("residual chlorine") ||
                normalized.Contains("free chlorine") ||
                normalized.Contains("nitrates (as n)") ||
                normalized.Contains("nitrate (as n)"))
            {
                return false;
            }

            return normalized == "acidity" ||
                   normalized == "ammonium" ||
                   normalized == "ammonia" ||
                   normalized == "chloride" ||
                   normalized == "chlorides" ||
                   normalized == "nitrate" ||
                   normalized == "nitrates" ||
                   normalized == "sulphate" ||
                   normalized == "sulphates" ||
                   normalized == "sulfate" ||
                   normalized == "sulfates" ||
                   normalized.Contains("heavy metals") ||
                   normalized.Contains("oxidisable substances") ||
                   normalized.Contains("oxidizable substances");
        }

        private bool IsMicrobialCountTest(string testName)
        {
            testName = testName == null ? "" : testName.Trim().ToLowerInvariant();

            return testName.Contains("tamc") ||
                   testName.Contains("tymc") ||
                   testName.Contains("total microbial count") ||
                   testName.Contains("fungal count") ||
                   testName.Contains("aerobic microbial count") ||
                   testName.Contains("yeast") ||
                   testName.Contains("mold");
        }

        private string NormalizeSampleTypeForTitle(string sampleType)
        {
            sampleType = sampleType == null ? "" : sampleType.Trim();

            if (IsPurifiedWater(sampleType))
                return "PURIFIED WATER";

            if (IsPotableWater(sampleType))
                return "POTABLE WATER";

            if (sampleType.Equals("Environment", StringComparison.OrdinalIgnoreCase))
                return "ENVIRONMENTAL MONITORING";

            if (string.IsNullOrWhiteSpace(sampleType))
                return "SAMPLE";

            return sampleType.ToUpperInvariant();
        }

        private string GetAnalysisTypeTitle(List<TestResultDisplay> results)
        {
            bool hasPhysical = false;
            bool hasChemical = false;
            bool hasMicro = false;

            foreach (TestResultDisplay item in results)
            {
                string category = item.TestCategory == null ? "" : item.TestCategory.Trim().ToLowerInvariant();

                if (category == "physical")
                    hasPhysical = true;
                else if (category == "chemical")
                    hasChemical = true;
                else if (category == "microbiological")
                    hasMicro = true;
            }

            if (hasPhysical && hasChemical && hasMicro)
                return "WATER ANALYSIS CERTIFICATE";

            if (hasPhysical && hasChemical)
                return "PHYSICAL & CHEMICAL ANALYSIS CERTIFICATE";

            if (hasPhysical && hasMicro)
                return "PHYSICAL & MICROBIOLOGICAL ANALYSIS CERTIFICATE";

            if (hasChemical && hasMicro)
                return "CHEMICAL & MICROBIOLOGICAL ANALYSIS CERTIFICATE";

            if (hasPhysical)
                return "PHYSICAL ANALYSIS CERTIFICATE";

            if (hasChemical)
                return "CHEMICAL ANALYSIS CERTIFICATE";

            if (hasMicro)
                return "MICROBIOLOGICAL ANALYSIS CERTIFICATE";

            return "ANALYSIS CERTIFICATE";
        }

        private void UpdateAnalysisHeader(string sampleType, List<TestResultDisplay> results)
        {
            currentSampleType = sampleType == null ? "" : sampleType.Trim();

            bool potable = IsPotableWater(currentSampleType);

            if (lblMainDocumentTitle != null)
                lblMainDocumentTitle.Text = potable ? "WATER TEST REPORT" : "CERTIFICATE OF ANALYSIS";

            lblAnalysisReportTitle.Text = potable ? "POTABLE WATER" : "PURIFIED WATER";

            if (lblDocumentNumber != null)
                lblDocumentNumber.Text = potable
                    ? "Document No.: MQC-RPT-PTW-001"
                    : "Document No.: MQC-COA-PW-001";

            lblAnalysisReference.Text = potable
                ? "Testing performed against the approved potable-water specification and controlled laboratory methods"
                : "Testing performed against the approved purified-water specification and controlled laboratory methods";

            if (lblElectronicValidityText != null)
            {
                lblElectronicValidityText.Text = potable
                    ? "This electronic test report is generated within PharmaLIMS. Electronic signature manifestations and audit-trail records are retained with the source electronic record."
                    : "This electronic certificate of analysis is generated within PharmaLIMS. Electronic signature manifestations and audit-trail records are retained with the source electronic record.";
            }

            if (lblVerificationTitle != null)
                lblVerificationTitle.Text = potable ? "Report Verification" : "Certificate Verification";

            if (lblVerificationStatement != null)
                lblVerificationStatement.Text = "Verify this document using the verification code and document hash recorded above.";

            if (BtnIssueCertificate != null)
                BtnIssueCertificate.Content = potable ? "Issue Report" : "Issue Certificate";

            Title = potable ? "Potable Water Test Report" : "Purified Water Certificate of Analysis";
        }

        private bool TryGetDecimal(object value, out decimal result)
        {
            result = 0;

            if (value == null || value == DBNull.Value)
                return false;

            string raw = value.ToString().Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return false;

            raw = raw.Replace(",", ".");

            return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
        }

        private string FormatResultForCertificate(string testName, string unit, object resultValue)
        {
            if (resultValue == null || resultValue == DBNull.Value)
                return "Pending";

            string raw = resultValue.ToString().Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "Pending";

            if (IsAppearanceTest(testName))
            {
                decimal appearanceValue;

                if (TryGetDecimal(resultValue, out appearanceValue))
                    return appearanceValue <= 0 ? "Clear and Colorless" : "Not Clear / Colored";

                if (raw.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Clear and Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    return "Clear and Colorless";
                }

                if (raw.Equals("Turbid", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colored", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Coloured", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Does Not Conform", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Not OK", StringComparison.OrdinalIgnoreCase))
                {
                    return "Not Clear / Colored";
                }

                return raw;
            }

            if (IsComplianceQualitativeTest(testName))
            {
                decimal value;

                if (TryGetDecimal(resultValue, out value))
                    return value <= 0 ? "Complies" : "Does Not Comply";

                return raw;
            }

            if (IsAbsencePresenceTest(unit, testName))
            {
                decimal value;

                if (TryGetDecimal(resultValue, out value))
                    return value <= 0 ? "Absence" : "Presence";

                if (raw.Equals("Negative", StringComparison.OrdinalIgnoreCase))
                    return "Absence";

                if (raw.Equals("Positive", StringComparison.OrdinalIgnoreCase))
                    return "Presence";

                return raw;
            }

            decimal numeric;

            if (TryGetDecimal(resultValue, out numeric))
                return numeric.ToString("0.##", CultureInfo.InvariantCulture);

            return raw;
        }

        private string FormatLimitForCertificate(string sampleType, string testName, string unit, object alertLimit, object actionLimit)
        {
            if (IsAppearanceTest(testName))
                return "Clear and Colorless";

            if (IsComplianceQualitativeTest(testName))
                return "Complies with approved specification";

            if (IsPhTest(testName))
            {
                if (IsPurifiedWater(sampleType))
                    return "5.0 - 7.0";

                if (IsPotableWater(sampleType))
                    return "6.5 - 8.5";

                return "According to approved specification";
            }

            if (IsConductivityTest(testName))
            {
                if (IsPurifiedWater(sampleType))
                    return "NMT 2.0";

                if (IsPotableWater(sampleType))
                    return "NMT 500";

                decimal potableAction;

                if (TryGetDecimal(actionLimit, out potableAction) && potableAction > 0)
                    return "NMT " + potableAction.ToString("0.##", CultureInfo.InvariantCulture);

                return "According to approved specification";
            }

            if (IsHardnessTest(testName) && IsPotableWater(sampleType))
                return IsSoftWaterPoint(sampleType) ? "NMT 5" : "NMT 300";

            if (IsResidualChlorineTest(testName))
                return "2.0 - 4.0";

            if (IsPathogenTest(testName) && IsPotableWater(sampleType))
                return "Absence / 100 mL";

            if (IsTotalBacterialCountTest(testName) && IsPotableWater(sampleType))
                return "NMT 500";

            if (IsAbsencePresenceTest(unit, testName))
                return "Absence";

            if (IsMicrobialCountTest(testName))
            {
                decimal action;

                if (TryGetDecimal(actionLimit, out action) && action > 0)
                    return "NMT " + action.ToString("0.##", CultureInfo.InvariantCulture);

                return "According to approved specification";
            }

            decimal actionLimitValue;

            if (TryGetDecimal(actionLimit, out actionLimitValue) && actionLimitValue > 0)
                return "NMT " + actionLimitValue.ToString("0.##", CultureInfo.InvariantCulture);

            return "According to approved specification";
        }

        private string GetConformity(string sampleType, string testName, string unit, object resultValue, object alertLimit, object actionLimit)
        {
            if (resultValue == null || resultValue == DBNull.Value)
                return "Pending";

            string raw = resultValue.ToString().Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "Pending";

            if (IsAppearanceTest(testName))
            {
                decimal value;

                if (TryGetDecimal(resultValue, out value))
                    return value <= 0 ? "Conform" : "Non-Conform";

                if (raw.Equals("Clear", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Clear and Colorless", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Conforms", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("OK", StringComparison.OrdinalIgnoreCase))
                {
                    return "Conform";
                }

                return "Non-Conform";
            }

            if (IsComplianceQualitativeTest(testName))
            {
                decimal value;

                if (TryGetDecimal(resultValue, out value))
                    return value <= 0 ? "Conform" : "Non-Conform";

                return raw.Equals("Complies", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Comply", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Conform", StringComparison.OrdinalIgnoreCase) ||
                       raw.Equals("Pass", StringComparison.OrdinalIgnoreCase)
                    ? "Conform"
                    : "Non-Conform";
            }

            if (IsPhTest(testName))
            {
                decimal value;

                if (!TryGetDecimal(resultValue, out value))
                    return "Non-Conform";

                if (IsPurifiedWater(sampleType))
                    return value >= 5.0m && value <= 7.0m ? "Conform" : "Non-Conform";

                if (IsPotableWater(sampleType))
                    return value >= 6.5m && value <= 8.5m ? "Conform" : "Non-Conform";

                return "Conform";
            }

            if (IsConductivityTest(testName))
            {
                decimal value;

                if (!TryGetDecimal(resultValue, out value))
                    return "Non-Conform";

                if (IsPurifiedWater(sampleType))
                    return value <= 2.0m ? "Conform" : "Non-Conform";

                if (IsPotableWater(sampleType))
                    return value <= 500.0m ? "Conform" : "Non-Conform";

                decimal potableAction;

                if (TryGetDecimal(actionLimit, out potableAction) && potableAction > 0)
                    return value <= potableAction ? "Conform" : "Non-Conform";

                return "Conform";
            }

            if (IsHardnessTest(testName) && IsPotableWater(sampleType))
            {
                decimal value;

                if (!TryGetDecimal(resultValue, out value))
                    return "Non-Conform";

                decimal limit = IsSoftWaterPoint(sampleType) ? 5.0m : 300.0m;
                return value <= limit ? "Conform" : "Non-Conform";
            }

            if (IsResidualChlorineTest(testName))
            {
                decimal value;

                if (!TryGetDecimal(resultValue, out value))
                    return "Non-Conform";

                return value >= 2.0m && value <= 4.0m ? "Conform" : "Non-Conform";
            }

            if (IsPathogenTest(testName) && IsPotableWater(sampleType))
            {
                decimal value;

                if (TryGetDecimal(resultValue, out value))
                    return value <= 0 ? "Conform" : "Non-Conform";

                if (raw.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Absent", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Negative", StringComparison.OrdinalIgnoreCase))
                {
                    return "Conform";
                }

                return "Non-Conform";
            }

            if (IsTotalBacterialCountTest(testName) && IsPotableWater(sampleType))
            {
                decimal value;

                if (!TryGetDecimal(resultValue, out value))
                    return "Non-Conform";

                return value <= 500.0m ? "Conform" : "Non-Conform";
            }

            if (IsAbsencePresenceTest(unit, testName))
            {
                decimal value;

                if (TryGetDecimal(resultValue, out value))
                    return value <= 0 ? "Conform" : "Non-Conform";

                if (raw.Equals("Absence", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("Negative", StringComparison.OrdinalIgnoreCase))
                {
                    return "Conform";
                }

                return "Non-Conform";
            }

            decimal numericResult;
            decimal actionLimitValue;

            if (TryGetDecimal(resultValue, out numericResult) &&
                TryGetDecimal(actionLimit, out actionLimitValue) &&
                actionLimitValue > 0)
            {
                return numericResult <= actionLimitValue ? "Conform" : "Non-Conform";
            }

            return "Conform";
        }

        private string GetUnitForCertificate(string testName, string unit)
        {
            if (IsAppearanceTest(testName))
                return "-";

            if (IsAbsencePresenceTest(unit, testName) || IsComplianceQualitativeTest(testName))
                return "-";

            return unit ?? "";
        }

        private int GetDisplayOrder(string testName, string unit)
        {
            if (IsAppearanceTest(testName))
                return 0;

            if (IsPhTest(testName))
                return 1;

            if (IsConductivityTest(testName))
                return 2;

            if (IsAbsencePresenceTest(unit, testName))
                return 3;

            if (IsMicrobialCountTest(testName))
                return 4;

            return 5;
        }

        private int GetCategoryOrder(string category)
        {
            category = category == null ? "" : category.Trim().ToLowerInvariant();

            if (category == "physical")
                return 1;

            if (category == "chemical")
                return 2;

            if (category == "microbiological")
                return 3;

            return 9;
        }

        private string GetSectionTitle(string category)
        {
            category = category == null ? "" : category.Trim().ToLowerInvariant();

            if (category == "physical")
                return "PHYSICAL TESTS";

            if (category == "chemical")
                return "CHEMICAL TESTS";

            if (category == "microbiological")
                return "MICROBIOLOGICAL TESTS";

            return "OTHER TESTS";
        }

        private void UpdateFinalConclusion(List<TestResultDisplay> results)
        {
            bool hasPending = false;
            bool hasNonConform = IsOosSampleStatus(currentSampleStatus);

            foreach (TestResultDisplay item in results)
            {
                string status = item.Status == null ? "" : item.Status.Trim();

                if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    hasPending = true;

                if (status.Equals("Non-Conform", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("OOS", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    hasNonConform = true;
                }
            }

            if (hasPending)
            {
                lblFinalConclusion.Text = "FINAL CONCLUSION PENDING - ONE OR MORE TEST RESULTS HAVE NOT BEEN COMPLETED.";
                lblFinalConclusion.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#92400E"));
                FinalConclusionBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFBEB"));
                FinalConclusionBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                return;
            }

            if (hasNonConform)
            {
                lblFinalConclusion.Text = "THE TESTED SAMPLE DOES NOT COMPLY WITH THE APPROVED SPECIFICATION.";
                lblFinalConclusion.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#991B1B"));
                FinalConclusionBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF2F2"));
                FinalConclusionBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                return;
            }

            lblFinalConclusion.Text = "THE TESTED SAMPLE COMPLIES WITH THE APPROVED SPECIFICATION.";
            lblFinalConclusion.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#047857"));
            FinalConclusionBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ECFDF5"));
            FinalConclusionBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
        }

        private void LoadCertificateNumber()
        {
            try
            {
                string query = @"
                    SELECT TOP 1
                        CertificateNumber,
                        IssueDate,
                        VerificationCode,
                        RevisionNo,
                        CertificateStatus,
                        ReportHash
                    FROM Certificates
                    WHERE SampleID = @SampleID
                      AND ISNULL(IsCancelled, 0) = 0
                      AND ISNULL(CertificateStatus, ISNULL(Status, 'Active')) IN ('Active', 'Issued')
                    ORDER BY CertificateID DESC";

                SqlParameter[] pars =
                {
                    new SqlParameter("@SampleID", sampleId)
                };

                DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);

                if (dt.Rows.Count > 0)
                {
                    DataRow row = dt.Rows[0];
                    UpdateCertificateHeader(
                        row.GetSafeString("CertificateNumber"),
                        row["IssueDate"],
                        row.GetSafeString("VerificationCode"),
                        row.Table.Columns.Contains("RevisionNo") ? row["RevisionNo"] : null,
                        row.GetSafeString("CertificateStatus"),
                        row.GetSafeString("ReportHash"));
                }
                else
                {
                    UpdateCertificateHeader("", null);
                }
            }
            catch
            {
                UpdateCertificateHeader("", null);
            }
        }

        private string GetSampleNumber()
        {
            return lblSampleNumber != null ? (lblSampleNumber.Text ?? "").Trim() : "";
        }

        private void SetStatusBadge(string status)
        {
            string color = EnumHelper.GetStatusColor(status);
            statusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }

        private bool IsCurrentAdmin()
        {
            string role = Login.CurrentUserRole ?? "";

            return AppConfig.DevelopmentAdminFullPermissions &&
                   (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                    role.Equals("Administrator", StringComparison.OrdinalIgnoreCase));
        }

        private string GetCurrentRoleText()
        {
            string role = Login.CurrentUserRole ?? "";

            if (!string.IsNullOrWhiteSpace(role))
                return role.Trim();

            return IsCurrentAdmin() ? "Admin" : "User";
        }

        private void ApplyIssuePermission(bool canIssue)
        {
            if (BtnIssueCertificate != null)
            {
                BtnIssueCertificate.IsEnabled = canIssue;
                BtnIssueCertificate.ToolTip = canIssue
                    ? "Admin/authorized user can issue COA."
                    : "You don't have permission to issue certificates.";
            }
        }

        private bool CanIssue()
        {
            string username = Login.CurrentUser ?? "";
            return DatabaseHelper.CanIssueCertificate(username);
        }

        private bool CanPrint()
        {
            string username = Login.CurrentUser ?? "";
            return DatabaseHelper.CanAccessReports(username);
        }

        private async Task LoadReportDataAsync()
        {
            try
            {
                await Task.Run(EnsureGlobalWaterDateColumnsForReport);
                string sampleQuery = @"
                    SELECT 
                        s.SampleNumber, 
                        s.SampleType, 
                        s.SamplingDateTime,
                        s.CreatedDate,
                        s.ReceivedDateTime,
                        s.AnalysisStartedDateTime,
                        s.AnalysisCompletedDateTime,
                        s.SampledBy, 
                        s.Status, 
                        COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, p.PointCode) AS PointCode,
                        COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, p.Location) AS Location,
                        c.CertificateNumber AS CertificateNumber,
                        c.IssuedBy AS CertificateIssuedBy,
                        c.IssueDate AS CertificateIssueDate,
                        c.VerificationCode AS VerificationCode,
                        c.RevisionNo AS RevisionNo,
                        c.CertificateStatus AS CertificateStatus,
                        c.ReportHash AS ReportHash,

                        analyst.SignedBy AS AnalystName,
                        analyst.SignedAt AS AnalystSignedAt,
                        analyst.MeaningOfSignature AS AnalystMeaning,
                        analyst.UserRole AS AnalystRole,
                        reviewer.SignedBy AS ReviewedByName,
                        reviewer.SignedAt AS ReviewedBySignedAt,
                        reviewer.MeaningOfSignature AS ReviewedByMeaning,
                        reviewer.UserRole AS ReviewedByRole,
                        approver.SignedBy AS ApprovedByName,
                        approver.SignedAt AS ApprovedBySignedAt,
                        approver.MeaningOfSignature AS ApprovedByMeaning,
                        approver.UserRole AS ApprovedByRole,
                        issuer.SignedBy AS IssuedByName,
                        issuer.SignedAt AS IssuedBySignedAt,
                        issuer.MeaningOfSignature AS IssuedByMeaning,
                        issuer.UserRole AS IssuedByRole

                    FROM Samples s 
                    LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                    LEFT JOIN SamplingPoints p ON s.PointID = p.PointID 

                    OUTER APPLY
                    (
                        SELECT TOP 1
                            c2.CertificateNumber,
                            c2.IssuedBy,
                            c2.IssueDate,
                            c2.VerificationCode,
                            c2.RevisionNo,
                            c2.CertificateStatus,
                            c2.ReportHash
                        FROM Certificates c2
                        WHERE c2.SampleID = s.SampleID
                          AND ISNULL(c2.IsCancelled, 0) = 0
                          AND ISNULL(c2.CertificateStatus, ISNULL(c2.Status, 'Active')) IN ('Active', 'Issued')
                        ORDER BY c2.CertificateID DESC
                    ) c

                    OUTER APPLY
                    (
                        SELECT TOP 1 es.SignedBy, es.SignedAt, es.MeaningOfSignature, es.UserRole
                        FROM ElectronicSignatures es
                        WHERE es.SampleID = s.SampleID
                          AND es.ActionType IN 
                          (
                              'Result Entry',
                              'Results Entry',
                              'Enter Results',
                              'Entered Results'
                          )
                        ORDER BY es.SignedAt DESC
                    ) analyst

                    OUTER APPLY
                    (
                        SELECT TOP 1 es.SignedBy, es.SignedAt, es.MeaningOfSignature, es.UserRole
                        FROM ElectronicSignatures es
                        WHERE es.SampleID = s.SampleID
                          AND es.ActionType IN 
                          (
                              'Review',
                              'Review Sample',
                              'Reviewed'
                          )
                        ORDER BY es.SignedAt DESC
                    ) reviewer

                    OUTER APPLY
                    (
                        SELECT TOP 1 es.SignedBy, es.SignedAt, es.MeaningOfSignature, es.UserRole
                        FROM ElectronicSignatures es
                        WHERE es.SampleID = s.SampleID
                          AND es.ActionType IN 
                          (
                              'Approve',
                              'Approve Sample',
                              'Approval',
                              'Approved'
                          )
                        ORDER BY es.SignedAt DESC
                    ) approver

                    OUTER APPLY
                    (
                        SELECT TOP 1 es.SignedBy, es.SignedAt, es.MeaningOfSignature, es.UserRole
                        FROM ElectronicSignatures es
                        WHERE es.SampleID = s.SampleID
                          AND es.ActionType IN 
                          (
                              'COA Issuance',
                              'Issue Certificate',
                              'Certificate Issuance',
                              'Issued Certificate'
                          )
                        ORDER BY es.SignedAt DESC
                    ) issuer

                    WHERE s.SampleID = @sampleId";

                SqlParameter[] pars =
                {
                    new SqlParameter("@sampleId", sampleId)
                };

                bool hasIssuedSnapshot = await Task.Run(() => DatabaseHelper.HasIssuedCertificateSnapshot(sampleId));
                if (hasIssuedSnapshot)
                {
                    var snapshotValidation = await Task.Run(() =>
                    {
                        bool isValid = DatabaseHelper.ValidateIssuedCertificateSnapshot(sampleId, out string validationMessage);
                        return (IsValid: isValid, Message: validationMessage);
                    });
                    if (!snapshotValidation.IsValid)
                    {
                        throw new InvalidOperationException(
                            snapshotValidation.Message + " Certificate/report loading and printing were blocked to protect the approved record.");
                    }
                }

                DataTable sampleData = await Task.Run(() => hasIssuedSnapshot
                    ? DatabaseHelper.GetIssuedCertificateSnapshotHeader(sampleId)
                    : DatabaseHelper.ExecuteQuery(sampleQuery, pars, commandTimeoutSeconds: 10));

                string sampledBy = "";
                string certificateIssuedBy = "";
                string analystFromSignature = "";
                string reviewedByFromSignature = "";
                string approvedByFromSignature = "";
                string issuedByFromSignature = "";
                string analystSignedAt = "";
                string reviewedBySignedAt = "";
                string approvedBySignedAt = "";
                string issuedBySignedAt = "";
                string analystMeaning = "";
                string reviewedByMeaning = "";
                string approvedByMeaning = "";
                string issuedByMeaning = "";
                string analystRole = "";
                string reviewedByRole = "";
                string approvedByRole = "";
                string issuedByRole = "";
                string sampleType = "";
                string sampleStatus = "";

                if (sampleData.Rows.Count > 0)
                {
                    DataRow row = sampleData.Rows[0];

                    lblSampleNumber.Text = row.GetSafeString("SampleNumber");
                    lblSampleType.Text = row.GetSafeString("SampleType");
                    sampleType = lblSampleType.Text;
                    currentSampleType = sampleType;
                    currentPointCodeForSpec = row.GetSafeString("PointCode");

                    lblSamplingPoint.Text =
                        row.GetSafeString("PointCode") +
                        " - " +
                        row.GetSafeString("Location");

                    if (row["SamplingDateTime"] != DBNull.Value)
                    {
                        DateTime samplingDate = Convert.ToDateTime(row["SamplingDateTime"]);
                        lblSamplingDate.Text = samplingDate.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        lblSamplingDate.Text = "";
                    }

                    sampledBy = row.GetSafeString("SampledBy");
                    sampledBy = await Task.Run(() => GetSampledByDirect(sampledBy));

                    UpdateCertificateHeader(
                        row.GetSafeString("CertificateNumber"),
                        row["CertificateIssueDate"],
                        row.GetSafeString("VerificationCode"),
                        row.Table.Columns.Contains("RevisionNo") ? row["RevisionNo"] : null,
                        row.GetSafeString("CertificateStatus"),
                        row.GetSafeString("ReportHash"));

                    certificateIssuedBy = row.GetSafeString("CertificateIssuedBy");
                    analystFromSignature = row.GetSafeString("AnalystName");
                    reviewedByFromSignature = row.GetSafeString("ReviewedByName");
                    approvedByFromSignature = row.GetSafeString("ApprovedByName");
                    issuedByFromSignature = row.GetSafeString("IssuedByName");
                    analystSignedAt = FormatDateTimeForCertificate(row["AnalystSignedAt"]);
                    reviewedBySignedAt = FormatDateTimeForCertificate(row["ReviewedBySignedAt"]);
                    approvedBySignedAt = FormatDateTimeForCertificate(row["ApprovedBySignedAt"]);
                    issuedBySignedAt = FormatDateTimeForCertificate(row["IssuedBySignedAt"]);
                    analystMeaning = row.GetSafeString("AnalystMeaning");
                    reviewedByMeaning = row.GetSafeString("ReviewedByMeaning");
                    approvedByMeaning = row.GetSafeString("ApprovedByMeaning");
                    issuedByMeaning = row.GetSafeString("IssuedByMeaning");
                    analystRole = row.GetSafeString("AnalystRole");
                    reviewedByRole = row.GetSafeString("ReviewedByRole");
                    approvedByRole = row.GetSafeString("ApprovedByRole");
                    issuedByRole = row.GetSafeString("IssuedByRole");

                    lblSampledBy.Text = sampledBy;
                    lblSampledBy.ToolTip = sampledBy;
                    lblSampledBy.UpdateLayout();

                    lblReceivedDate.Text = FirstFormattedDate(row, "ReceivedDateTime", "CreatedDate", "SamplingDateTime");
                    lblAnalysisDate.Text = FirstFormattedDate(row, "AnalysisStartedDateTime", "AnalystSignedAt");
                    lblAnalysisCompletedDate.Text = FirstFormattedDate(row, "AnalysisCompletedDateTime", "AnalystSignedAt");
                    lblReviewedSummaryDate.Text = FirstFormattedDate(row, "ReviewedBySignedAt");
                    lblApprovedSummaryDate.Text = FirstFormattedDate(row, "ApprovedBySignedAt");

                    sampleStatus = row.GetSafeString("Status");
                    currentSampleStatus = sampleStatus;
                    lblStatus.Text = sampleStatus.Equals("COA Issued", StringComparison.OrdinalIgnoreCase) ? "Approved" : sampleStatus;
                    SetStatusBadge(sampleStatus);

                    lblSpecification.Text = IsPotableWater(sampleType)
                        ? "Drinking Water Specification"
                        : "MQC-G-0018";
                }
                else
                {
                    UpdateCertificateHeader("", null);
                    string directSampledBy = await Task.Run(() => GetSampledByDirect(""));
                    lblSampledBy.Text = directSampledBy;
                    lblSampledBy.ToolTip = directSampledBy;
                    lblSampledBy.UpdateLayout();
                }

                string analystName = ChoosePersonName(analystFromSignature, "");
                string reviewedByName = ChoosePersonName(reviewedByFromSignature, "");
                string approvedByName = ChoosePersonName(approvedByFromSignature, "");
                bool hasIssuedCertificate = !string.IsNullOrWhiteSpace(certificateNumber);
                string generatedBy = hasIssuedCertificate
                    ? ChoosePersonName(issuedByFromSignature, certificateIssuedBy)
                    : "";

                lblAnalyst.Text = !string.IsNullOrWhiteSpace(analystName) ? analystName : "Not signed";
                lblReviewedBy.Text = !string.IsNullOrWhiteSpace(reviewedByName) ? reviewedByName : "Not signed";
                lblApprovedBy.Text = !string.IsNullOrWhiteSpace(approvedByName) ? approvedByName : "Not signed";
                lblIssuedBy.Text = hasIssuedCertificate && !string.IsNullOrWhiteSpace(generatedBy) ? generatedBy : "Not signed";

                lblAnalystDate.Text = "Signed: " + (string.IsNullOrWhiteSpace(analystSignedAt) ? "Pending" : analystSignedAt);
                lblReviewedDate.Text = "Signed: " + (string.IsNullOrWhiteSpace(reviewedBySignedAt) ? "Pending" : reviewedBySignedAt);
                lblApprovedDate.Text = "Signed: " + (string.IsNullOrWhiteSpace(approvedBySignedAt) ? "Pending" : approvedBySignedAt);
                lblIssuedDate.Text = "Signed: " + (hasIssuedCertificate && !string.IsNullOrWhiteSpace(issuedBySignedAt) ? issuedBySignedAt : "Pending issuance");

                lblAnalystRole.Text = "Role: " + (string.IsNullOrWhiteSpace(analystRole) ? "Pending" : analystRole);
                lblReviewedRole.Text = "Role: " + (string.IsNullOrWhiteSpace(reviewedByRole) ? "Pending" : reviewedByRole);
                lblApprovedRole.Text = "Role: " + (string.IsNullOrWhiteSpace(approvedByRole) ? "Pending" : approvedByRole);
                lblIssuedRole.Text = "Role: " + (hasIssuedCertificate && !string.IsNullOrWhiteSpace(issuedByRole) ? issuedByRole : "Pending issuance");

                lblAnalystMeaning.Text = "Meaning: " + (string.IsNullOrWhiteSpace(analystMeaning) ? "Result Entry" : analystMeaning);
                lblReviewedMeaning.Text = "Meaning: " + (string.IsNullOrWhiteSpace(reviewedByMeaning) ? "Technical Review" : reviewedByMeaning);
                lblApprovedMeaning.Text = "Meaning: " + (string.IsNullOrWhiteSpace(approvedByMeaning) ? "QA Approval" : approvedByMeaning);
                lblIssuedMeaning.Text = "Meaning: " + (hasIssuedCertificate && !string.IsNullOrWhiteSpace(issuedByMeaning) ? issuedByMeaning : "Pending issuance");

                string testsQuery = @"
                    SELECT 
                        COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName) AS TestName,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.TestCategorySnapshot ELSE t.TestCategory END AS TestCategory,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.UnitSnapshot ELSE t.Unit END AS Unit,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
                        CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
                        st.ResultValue,
                        ISNULL(st.ResultStatus,N'') AS ResultStatus,
                        ISNULL(st.LimitDescription,N'') AS LimitDescription
                    FROM SampleTests st 
                    LEFT JOIN Tests t ON st.TestID = t.TestID 
                    WHERE st.SampleID = @sampleId
                    ORDER BY CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END,
                             COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'')";

                SqlParameter[] testPars =
                {
                    new SqlParameter("@sampleId", sampleId)
                };

                DataTable testResultsData = await Task.Run(() => hasIssuedSnapshot
                    ? DatabaseHelper.GetIssuedCertificateSnapshotTests(sampleId)
                    : DatabaseHelper.ExecuteQuery(testsQuery, testPars, commandTimeoutSeconds: 10));
                var results = new List<TestResultDisplay>();

                foreach (DataRow row in testResultsData.Rows)
                {
                    string testName = row.GetSafeString("TestName");

                    if (IsRemovedTest(testName))
                        continue;

                    string unit = row["Unit"] != DBNull.Value ? row.GetSafeString("Unit") : "";
                    string category = row.GetSafeString("TestCategory");

                    string limit = row.GetSafeString("LimitDescription");
                    if (string.IsNullOrWhiteSpace(limit)) limit = FormatLimitForCertificate(
                        sampleType,
                        testName,
                        unit,
                        row["AlertLimit"],
                        row["ActionLimit"]);

                    string resultText = FormatResultForCertificate(
                        testName,
                        unit,
                        row["ResultValue"]);

                    string savedStatus = row.GetSafeString("ResultStatus");
                    string conformity = savedStatus.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? "Conform"
                        : savedStatus.Equals("ALERT", StringComparison.OrdinalIgnoreCase) ? "Alert"
                        : savedStatus.Equals("OOS", StringComparison.OrdinalIgnoreCase) ? "Non-Conform"
                        : GetConformity(
                        sampleType,
                        testName,
                        unit,
                        row["ResultValue"],
                        row["AlertLimit"],
                        row["ActionLimit"]);

                    results.Add(new TestResultDisplay
                    {
                        DisplayOrder = GetDisplayOrder(testName, unit),
                        CategoryOrder = GetCategoryOrder(category),
                        GroupTitle = GetSectionTitle(category),
                        TestCategory = category,
                        TestName = testName,
                        Specification = limit,
                        Result = resultText,
                        Unit = GetUnitForCertificate(testName, unit),
                        Status = conformity
                    });
                }

                results.Sort(delegate (TestResultDisplay a, TestResultDisplay b)
                {
                    int categoryCompare = a.CategoryOrder.CompareTo(b.CategoryOrder);

                    if (categoryCompare != 0)
                        return categoryCompare;

                    int orderCompare = a.DisplayOrder.CompareTo(b.DisplayOrder);

                    if (orderCompare != 0)
                        return orderCompare;

                    return string.Compare(a.TestName, b.TestName, StringComparison.OrdinalIgnoreCase);
                });

                currentResults.Clear();
                currentResults.AddRange(results);

                ICollectionView groupedView = CollectionViewSource.GetDefaultView(currentResults);
                groupedView.GroupDescriptions.Clear();
                groupedView.GroupDescriptions.Add(new PropertyGroupDescription("GroupTitle"));
                dgResults.ItemsSource = groupedView;

                UpdateAnalysisHeader(sampleType, results);
                UpdateFinalConclusion(results);

                lblFooter.Text =
                    "Report Generated: " +
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                    " | Generated By: " +
                    (string.IsNullOrWhiteSpace(generatedBy) ? "Not signed" : generatedBy);

                if (BtnIssueCertificate != null)
                {
                    bool hasCertificate = await Task.Run(() => DatabaseHelper.HasCertificate(sampleId));
                    if (hasCertificate)
                    {
                        BtnIssueCertificate.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        BtnIssueCertificate.Visibility =
                            (sampleStatus == "Approved" || sampleStatus == "COA Cancelled")
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                    }
                }

                bool canIssue = await Task.Run(CanIssue);
                ApplyIssuePermission(canIssue);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading report data: " + Infrastructure.UserFacingError.SafeMessage(ex),
                                "Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

        private bool HasAnyNonConformResult()
        {
            foreach (TestResultDisplay item in currentResults)
            {
                if (item.Status == "Non-Conform")
                    return true;
            }

            return false;
        }

        private bool HasAnyPendingResult()
        {
            foreach (TestResultDisplay item in currentResults)
            {
                if (item.Status == "Pending")
                    return true;
            }

            return false;
        }

        private bool HasRequiredElectronicSignatures()
        {
            object result = DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
    EXISTS
    (
        SELECT 1
        FROM dbo.ElectronicSignatures
        WHERE SampleID = @SampleID
          AND ActionType IN (N'Review', N'Review Sample', N'Reviewed')
    )
    AND EXISTS
    (
        SELECT 1
        FROM dbo.ElectronicSignatures
        WHERE SampleID = @SampleID
          AND ActionType IN (N'Approve', N'Approve Sample', N'Approval', N'Approved')
    )
THEN 1 ELSE 0 END;",
                new[]
                {
                    new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId }
                });

            return result != null && result != DBNull.Value && Convert.ToInt32(result) == 1;
        }

        private async void BtnIssueCertificate_Click(object sender, RoutedEventArgs e)
        {
            if (certificateIssuanceInProgress)
                return;

            try
            {
                if (!CanIssue())
                {
                    MessageBox.Show(
                        "You don't have permission to issue certificates. Only QA can issue COA.",
                        "Permission Denied",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!DatabaseHelper.CanIssueCertificateForSample(sampleId, out string qualityEventMessage))
                {
                    MessageBox.Show(
                        qualityEventMessage,
                        "Quality Event Block",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                int pendingCount = DatabaseHelper.GetPendingResultsCount(sampleId);

                if (pendingCount > 0 || HasAnyPendingResult())
                {
                    MessageBox.Show(
                        "Cannot issue certificate: one or more test results are still pending.\nPlease enter all results first.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string statusQuery = "SELECT Status FROM Samples WHERE SampleID = @sid";
                SqlParameter[] statusPars =
                {
                    new SqlParameter("@sid", sampleId)
                };

                string status = DatabaseHelper.ExecuteScalar(statusQuery, statusPars)?.ToString() ?? "";

                if (status == "OOS" || status == "Failed" || status == "Under Investigation")
                {
                    MessageBox.Show(
                        "Cannot issue certificate: sample is under investigation. Please complete QA disposition first.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (status != "Approved" && status != "COA Cancelled")
                {
                    MessageBox.Show(
                        "Cannot issue certificate: Sample status is '" + status + "'. Sample must be approved first or cancelled for reissue.",
                        "Validation Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!HasRequiredElectronicSignatures())
                {
                    MessageBox.Show(
                        "Cannot issue the document because recorded Review and Approval electronic signatures are required.",
                        "Electronic Signature Block",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (DatabaseHelper.HasCertificate(sampleId))
                {
                    MessageBox.Show(
                        "A certificate has already been issued for this sample.",
                        "Certificate Exists",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    BtnIssueCertificate.Visibility = Visibility.Collapsed;
                    LoadCertificateNumber();
                    return;
                }

                if (!IsCurrentAdmin() &&
                    !DatabaseHelper.ValidateSampleWorkflowSeparation(sampleId, currentUser, "COA Issuance", out string separationMessage))
                {
                    MessageBox.Show(
                        separationMessage,
                        "Workflow Separation Block",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                string signatureAction = _controlledReissueFromCertificateId.HasValue
                    ? "Controlled Legacy COA Reissue"
                    : "COA Issuance";

                ElectronicSignature signatureWindow =
                    new ElectronicSignature(GetSampleNumber(), currentUser, signatureAction, true);

                signatureWindow.Owner = this;

                if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                    return;

                certificateIssuanceInProgress = true;
                BtnIssueCertificate.IsEnabled = false;
                string originalButtonText = BtnIssueCertificate.Content?.ToString() ?? "Issue Certificate";
                BtnIssueCertificate.Content = "Issuing...";
                Mouse.OverrideCursor = Cursors.Wait;

                string signatureReason = signatureWindow.Reason;
                if (_controlledReissueFromCertificateId.HasValue)
                {
                    string trace = $"Legacy Certificate Evidence Reconciliation #{_controlledLegacyReconciliationId}; replaces {_controlledLegacyCertificateNumber}.";
                    signatureReason = string.Join(" ", new[]
                    {
                        _controlledLegacyReissueReason,
                        trace,
                        signatureReason
                    }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
                }

                int certificateSampleId = sampleId;
                string certificateUser = currentUser;

                ApplicationLogger.Information(
                    $"Certificate issuance started for SampleID {certificateSampleId} by '{certificateUser}'.");

                string issuedCertificateNumber = await System.Threading.Tasks.Task.Run(() =>
                {
                    string generatedCertificateNumber = DatabaseHelper.IssueCertificateAtomic(
                        certificateSampleId,
                        certificateUser,
                        signatureReason,
                        _controlledReissueFromCertificateId.HasValue
                            ? "Controlled legacy COA reissue"
                            : "COA Issuance",
                        _controlledReissueFromCertificateId);

                    if (string.IsNullOrWhiteSpace(generatedCertificateNumber))
                        throw new InvalidOperationException("The certificate number could not be generated.");

                    return generatedCertificateNumber;
                });

                certificateNumber = issuedCertificateNumber;
                lblCertificateNumber.Text = "Certificate No: " + certificateNumber;
                BtnIssueCertificate.Visibility = Visibility.Collapsed;

                LoadCertificateNumber();
                await LoadReportDataAsync();

                ApplicationLogger.Information(
                    $"Certificate '{certificateNumber}' was issued successfully for SampleID {certificateSampleId}.");

                MessageBox.Show(
                    (IsPotableWater(currentSampleType)
                        ? "Report issued successfully!\n\nReport Number: "
                        : "Certificate issued successfully!\n\nCertificate Number: ") + certificateNumber,
                    IsPotableWater(currentSampleType) ? "Report Issued" : "Certificate Issued",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                BtnIssueCertificate.Content = originalButtonText;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Certificate issuance failed.", ex);
                MessageBox.Show(
                    "Error issuing certificate: " + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                certificateIssuanceInProgress = false;
                Mouse.OverrideCursor = null;

                if (BtnIssueCertificate.Visibility == Visibility.Visible)
                {
                    BtnIssueCertificate.IsEnabled = true;
                    BtnIssueCertificate.Content = _controlledReissueFromCertificateId.HasValue
                        ? IsPotableWater(currentSampleType)
                            ? "Issue Replacement Report"
                            : "Issue Replacement COA"
                        : IsPotableWater(currentSampleType)
                            ? "Issue Report"
                            : "Issue Certificate";
                }
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.P &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                PrintCertificate();
            }
        }

        private void BtnPrint_Click(object sender, RoutedEventArgs e)
        {
            PrintCertificate();
        }

        public void PrintCertificate()
        {
            FrameworkElement element = null;
            Visibility oldButtonsVisibility = Visibility.Visible;

            try
            {
                if (!CanPrint())
                {
                    MessageBox.Show(
                        "You do not have permission to print controlled reports.",
                        "Permission Denied",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(certificateNumber) &&
                    !DatabaseHelper.ValidateIssuedCertificateSnapshot(sampleId, out string snapshotMessage))
                {
                    MessageBox.Show(snapshotMessage + " Printing has been blocked.", "Certificate Integrity Block", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                PrintDialog printDialog = new PrintDialog();

                // Force the certificate to the international A4 portrait format.
                // A4 = 210 x 297 mm. The printer can still apply its own non-printable hardware margins.
                try
                {
                    PrintTicket ticket = printDialog.PrintTicket ?? new PrintTicket();
                    ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
                    ticket.PageOrientation = PageOrientation.Portrait;
                    printDialog.PrintTicket = ticket;
                }
                catch
                {
                    // Some legacy printer drivers reject programmatic media selection.
                    // The user may still select A4 manually in the printer dialog.
                }

                if (printDialog.ShowDialog() != true)
                    return;

                element = PrintableArea;

                if (element == null)
                {
                    MessageBox.Show("No printable content found.",
                                    "Print",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                    return;
                }

                oldButtonsVisibility = ActionButtonsPanel.Visibility;
                ActionButtonsPanel.Visibility = Visibility.Collapsed;

                // A4 portrait size in WPF device-independent pixels (96 DPI).
                const double a4Width = 793.7007874015749;
                const double a4Height = 1122.5196850393702;

                element.Measure(new Size(a4Width, double.PositiveInfinity));
                double measuredHeight = Math.Max(a4Height, element.DesiredSize.Height);
                element.Arrange(new Rect(0, 0, a4Width, measuredHeight));
                element.UpdateLayout();

                double sourceWidth = a4Width;
                double sourceHeight = Math.Max(a4Height, element.ActualHeight);

                if (sourceWidth <= 0 || sourceHeight <= 0)
                {
                    MessageBox.Show("Printable content size is invalid.",
                                    "Print",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                    return;
                }

                // Keep a small safety margin inside the printer's actual imageable area.
                const double safeMargin = 10;
                double pageWidth = printDialog.PrintableAreaWidth;
                double pageHeight = printDialog.PrintableAreaHeight;
                double printableWidth = Math.Max(1, pageWidth - (safeMargin * 2));
                double printableHeight = Math.Max(1, pageHeight - (safeMargin * 2));

                double scaleX = printableWidth / sourceWidth;
                double scaleY = printableHeight / sourceHeight;
                double scale = Math.Min(scaleX, scaleY);

                if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
                    scale = 1;

                // Never enlarge beyond the designed A4 size; only reduce when required by printer margins/content.
                scale = Math.Min(scale, 1.0);

                double renderedWidth = sourceWidth * scale;
                double renderedHeight = sourceHeight * scale;
                double offsetX = Math.Max(safeMargin, (pageWidth - renderedWidth) / 2.0);
                double offsetY = Math.Max(safeMargin, (pageHeight - renderedHeight) / 2.0);

                DrawingVisual visual = new DrawingVisual();

                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageWidth, pageHeight));
                    dc.PushTransform(new TranslateTransform(offsetX, offsetY));
                    dc.PushTransform(new ScaleTransform(scale, scale));

                    VisualBrush brush = new VisualBrush(element)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top
                    };

                    dc.DrawRectangle(brush, null, new Rect(0, 0, sourceWidth, sourceHeight));
                    dc.Pop();
                    dc.Pop();
                }

                string jobName = (IsPotableWater(currentSampleType)
                    ? "Potable Water Test Report - "
                    : "Purified Water Certificate of Analysis - ") + lblSampleNumber.Text;

                if (!string.IsNullOrWhiteSpace(certificateNumber))
                {
                    DatabaseHelper.AddCertificateLifecycleAudit(
                        sampleId,
                        certificateNumber,
                        "Print Requested",
                        "Active",
                        "Active",
                        "Controlled print request accepted before sending the issued document to the printer.",
                        currentUser);
                }

                printDialog.PrintVisual(visual, jobName);

                if (!string.IsNullOrWhiteSpace(certificateNumber))
                {
                    DatabaseHelper.AddCertificatePrintHistory(sampleId, certificateNumber, currentUser);
                    DatabaseHelper.AddCertificateLifecycleAudit(
                        sampleId,
                        certificateNumber,
                        "Print Completed",
                        "Active",
                        "Active",
                        "Issued document sent to the selected printer.",
                        currentUser);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(certificateNumber))
                    {
                        DatabaseHelper.AddCertificateLifecycleAudit(
                            sampleId,
                            certificateNumber,
                            "Print Failed",
                            "Active",
                            "Active",
                            Infrastructure.UserFacingError.SafeMessage(ex),
                            currentUser);
                    }
                }
                catch (Exception auditException)
                {
                    ApplicationLogger.Warning("Unable to record the failed certificate print attempt.", auditException);
                }

                MessageBox.Show("Error printing:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                                "Print Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
            finally
            {
                if (ActionButtonsPanel != null)
                    ActionButtonsPanel.Visibility = oldButtonsVisibility;

                if (element != null)
                    element.UpdateLayout();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    public class TestResultDisplay
    {
        public int DisplayOrder { get; set; }
        public int CategoryOrder { get; set; }
        public string GroupTitle { get; set; } = "";
        public string TestCategory { get; set; } = "";
        public string TestName { get; set; } = "";
        public string Specification { get; set; } = "";
        public string Result { get; set; } = "";
        public string Unit { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
