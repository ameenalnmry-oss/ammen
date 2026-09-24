using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using PharmaLIMS.Repositories;
using System;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

#nullable disable

namespace PharmaLIMS
{
    public partial class ProductionRawMaterialSamples : Window
    {
        private readonly PrmSpecificationRepository _prmSpecificationRepository = new();
        private int _selectedSampleId = 0;
        private bool _isLoading = true;
        private string _selectedCategory = "";
        private readonly ObservableCollection<SpecificationTestDraft> _specificationTests = new();
        private int _masterVersion;
        private string _masterApprovalStatus = "Draft";
        private bool _returnToRegistrationAfterMaster;
        private readonly bool _specificationMasterOnly;

        public ProductionRawMaterialSamples() : this(false)
        {
        }

        public ProductionRawMaterialSamples(bool specificationMasterOnly)
        {
            _specificationMasterOnly = specificationMasterOnly;
            _isLoading = true;
            InitializeComponent();
            Loaded += ProductionRawMaterialSamples_Loaded;
        }

        private async void ProductionRawMaterialSamples_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ProductionRawMaterialSamples_Loaded;

            // Keep this screen constrained to the visible desktop. Wide sample columns
            // scroll inside DgSamples instead of stretching the whole page off-screen.
            if (MainContentScroll != null)
                MainContentScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            Mouse.OverrideCursor = Cursors.Wait;
            IsEnabled = false;

            try
            {
                _isLoading = true;
                TxtCurrentUser.Text = "User: " + GetCurrentUserDisplayName();
                TxtStatus.Text = "Checking the controlled PRM database objects...";
                PrmSchemaReadinessResult schemaReadiness =
                    await PrmSchemaReadinessService.EnsureRegistrationReadyAsync();
                if (!schemaReadiness.IsReady)
                {
                    TxtStatus.Text = schemaReadiness.Message;
                    MessageBox.Show(schemaReadiness.Message,
                        "PRM Database Readiness", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SetDefaultValues();
                ApplyCategoryLayout();
                DgSpecificationTests.ItemsSource = _specificationTests;

                if (_specificationMasterOnly)
                {
                    Title = "PRM Specification Master";
                    OpenSpecificationMasterPanel();
                    TxtStatus.Text = "PRM Specification Master remediation mode is ready.";
                }
                else
                {
                    await LoadSamplesAsync();
                    TxtStatus.Text = "Production & Raw Material Samples module is ready.";
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Production and Raw Material Samples startup failed.", ex);
                MessageBox.Show("Error opening Production & Raw Material Samples module:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Module Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isLoading = false;
                IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        private void BtnOpenSpecificationMaster_Click(object sender, RoutedEventArgs e)
        {
            _returnToRegistrationAfterMaster = PnlRegistration.Visibility == Visibility.Visible;
            OpenSpecificationMasterPanel();
        }

        private void OpenSpecificationMasterPanel()
        {
            PnlTypeSelection.Visibility = Visibility.Collapsed;
            PnlRegistration.Visibility = Visibility.Collapsed;
            PnlSpecificationMaster.Visibility = Visibility.Visible;
            if (CmbMasterCategory.SelectedIndex < 0) CmbMasterCategory.SelectedIndex = 0;
            if (_specificationTests.Count == 0) NewSpecificationDraft();
        }

        private void BtnCloseSpecificationMaster_Click(object sender, RoutedEventArgs e)
        {
            if (_specificationMasterOnly)
            {
                Close();
                return;
            }

            PnlSpecificationMaster.Visibility = Visibility.Collapsed;
            if (_returnToRegistrationAfterMaster)
            {
                PnlTypeSelection.Visibility = Visibility.Collapsed;
                PnlRegistration.Visibility = Visibility.Visible;
                LoadApprovedSpecificationChoices();
            }
            else
            {
                PnlRegistration.Visibility = Visibility.Collapsed;
                PnlTypeSelection.Visibility = Visibility.Visible;
            }
        }

        private void BtnNewSpecification_Click(object sender, RoutedEventArgs e) => NewSpecificationDraft();

        private void BtnCloneActiveApproved_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string specificationNo = (TxtMasterSpecificationNo.Text ?? string.Empty).Trim();
                string category = MasterCategory;
                if (string.IsNullOrWhiteSpace(specificationNo) || string.IsNullOrWhiteSpace(category))
                    throw new InvalidOperationException("Specification No. and Sample Category are required before cloning the active approved version.");

                DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT *
FROM dbo.PRM_SpecificationTests
WHERE LTRIM(RTRIM(SpecificationNo))=@SpecificationNo
  AND LTRIM(RTRIM(SampleCategory))=@Category
  AND ApprovalStatus=N'Approved'
  AND IsActive=1
  AND VersionNo=(
      SELECT MAX(VersionNo)
      FROM dbo.PRM_SpecificationTests
      WHERE LTRIM(RTRIM(SpecificationNo))=@SpecificationNo
        AND LTRIM(RTRIM(SampleCategory))=@Category
        AND ApprovalStatus=N'Approved'
        AND IsActive=1)
ORDER BY ISNULL(SortOrder,SpecificationTestID),SpecificationTestID;",
                    new[]
                    {
                        new SqlParameter("@SpecificationNo", SqlDbType.NVarChar, 120) { Value = specificationNo },
                        new SqlParameter("@Category", SqlDbType.NVarChar, 40) { Value = category }
                    });

                if (table.Rows.Count == 0)
                    throw new InvalidOperationException("No active approved specification version was found for the selected number and category.");

                int sourceVersion = Convert.ToInt32(table.Rows[0]["VersionNo"], CultureInfo.InvariantCulture);

                _masterVersion = 0;
                _masterApprovalStatus = "Draft";
                TxtMasterVersion.Text = "New";
                TxtMasterReference.Text = table.Rows[0]["CompendialReference"] == DBNull.Value
                    ? string.Empty
                    : Convert.ToString(table.Rows[0]["CompendialReference"], CultureInfo.InvariantCulture) ?? string.Empty;
                TxtMasterItemCode.Text = table.Rows[0].Table.Columns.Contains("ItemCode") && table.Rows[0]["ItemCode"] != DBNull.Value
                    ? Convert.ToString(table.Rows[0]["ItemCode"], CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty;
                SetComboText(
                    CmbMasterProductionStage,
                    table.Rows[0].Table.Columns.Contains("ProductionStage") && table.Rows[0]["ProductionStage"] != DBNull.Value
                        ? Convert.ToString(table.Rows[0]["ProductionStage"], CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty);
                DpMasterEffective.SelectedDate = DateTime.Today;

                _specificationTests.Clear();
                foreach (DataRow row in table.Rows)
                {
                    _specificationTests.Add(new SpecificationTestDraft
                    {
                        TestCode = Convert.ToString(row["TestCode"], CultureInfo.InvariantCulture) ?? string.Empty,
                        TestName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty,
                        SpecificationText = Convert.ToString(row["SpecificationText"], CultureInfo.InvariantCulture) ?? string.Empty,
                        Unit = row["Unit"] == DBNull.Value ? string.Empty : Convert.ToString(row["Unit"], CultureInfo.InvariantCulture) ?? string.Empty,
                        ResultType = Convert.ToString(row["ResultType"], CultureInfo.InvariantCulture) ?? "Text",
                        RequiredTest = row["RequiredTest"] != DBNull.Value && Convert.ToBoolean(row["RequiredTest"], CultureInfo.InvariantCulture),
                        MinimumElapsedHours = row.Table.Columns.Contains("MinimumElapsedHours") && row["MinimumElapsedHours"] != DBNull.Value
                            ? Convert.ToDecimal(row["MinimumElapsedHours"], CultureInfo.InvariantCulture)
                            : 0m,
                        SortOrder = row["SortOrder"] == DBNull.Value ? 0 : Convert.ToInt32(row["SortOrder"], CultureInfo.InvariantCulture)
                    });
                }

                TxtMasterState.Text = "New Draft cloned from active approved v" +
                    sourceVersion.ToString(CultureInfo.InvariantCulture) +
                    " — verify scope, then Save Draft.";
                bool clonedTimingRequiresCompletion = _specificationTests.Any(test =>
                    test.RequiredTest && test.MinimumElapsedHours <= 0m);
                TxtStatus.Text = clonedTimingRequiresCompletion
                    ? "Active approved specification cloned into an unsaved draft. One or more required legacy rows have no controlled Minimum Elapsed Hours; enter and verify timing before Save Draft."
                    : "Active approved specification cloned into an unsaved draft. No database change has occurred.";
            }
            catch (Exception ex)
            {
                MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex), "Clone Active Approved", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnLoadPharmacopoeialTemplate_Click(object sender, RoutedEventArgs e)
        {
            string category = MasterCategory;
            if (string.IsNullOrWhiteSpace(category))
            {
                MessageBox.Show(
                    "Select the Sample Category before loading the pharmacopoeial template.",
                    "Pharmacopoeial Template", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyPharmacopoeialMicrobiologyTemplate(category);
            TxtMasterState.Text = "Draft template loaded — verify the current approved product/material specification, then Save Draft -> Review -> Approve.";
        }

        private void ApplyPharmacopoeialMicrobiologyTemplate(string category)
        {
            string normalized = (category ?? string.Empty).Trim();
            _specificationTests.Clear();

            if (normalized.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
            {
                TxtMasterReference.Text =
                    "USP <61>/<62>/<1111>; Ph. Eur. 2.6.12/2.6.13/5.1.4 - substances for pharmaceutical use: " +
                    "TAMC criterion 10^3 (maximum acceptable count 2000 CFU/g or mL), TYMC criterion 10^2 (maximum acceptable count 200 CFU/g or mL). " +
                    "Specified microorganisms are material-monograph/risk-assessment dependent.";
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "TAMC",
                    TestName = "Total Aerobic Microbial Count (TAMC)",
                    SpecificationText = "NMT 2000 CFU/g or mL",
                    Unit = "CFU/g or mL",
                    ResultType = "Numeric",
                    RequiredTest = true,
                    MinimumElapsedHours = 72m,
                    SortOrder = 10
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "TYMC",
                    TestName = "Total Yeast and Mold Count (TYMC)",
                    SpecificationText = "NMT 200 CFU/g or mL",
                    Unit = "CFU/g or mL",
                    ResultType = "Numeric",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 20
                });
                return;
            }

            bool oralSolidScope = normalized.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) ||
                                  normalized.Equals("Finished Product", StringComparison.OrdinalIgnoreCase) ||
                                  normalized.Equals("Stability", StringComparison.OrdinalIgnoreCase);
            if (!oralSolidScope)
            {
                _specificationTests.Add(new SpecificationTestDraft
                {
                    ResultType = "Text",
                    RequiredTest = true,
                    SortOrder = 1,
                    MinimumElapsedHours = 120m
                });
                return;
            }

            if (normalized.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase))
            {
                // MEDICA's controlled in-process profile is intentionally row-based:
                // every reportable microbiology determination receives its own result,
                // interpretation and traceable result-entry record. Do not collapse
                // TAMC/TYMC or specified organisms into a single free-text result.
                TxtMasterReference.Text = "MQC-G-0021";

                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "TAMC",
                    TestName = "Total Aerobic Microbial Count (TAMC)",
                    SpecificationText = "NMT 1000 CFU/g",
                    Unit = "CFU/g",
                    ResultType = "Numeric",
                    RequiredTest = true,
                    MinimumElapsedHours = 72m,
                    SortOrder = 10
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "TYMC",
                    TestName = "Total Yeast and Mold Count (TYMC)",
                    SpecificationText = "NMT 100 CFU/g",
                    Unit = "CFU/g",
                    ResultType = "Numeric",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 20
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "ECOLI",
                    TestName = "Escherichia coli",
                    SpecificationText = "Absent in 1 g",
                    Unit = string.Empty,
                    ResultType = "Qualitative",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 30
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "SALMONELLA",
                    TestName = "Salmonella spp.",
                    SpecificationText = "Absent in 10 g",
                    Unit = string.Empty,
                    ResultType = "Qualitative",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 40
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "SAUREUS",
                    TestName = "Staphylococcus aureus",
                    SpecificationText = "Absent in 1 g",
                    Unit = string.Empty,
                    ResultType = "Qualitative",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 50
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "PAERUGINOSA",
                    TestName = "Pseudomonas aeruginosa",
                    SpecificationText = "Absent in 1 g",
                    Unit = string.Empty,
                    ResultType = "Qualitative",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 60
                });
                _specificationTests.Add(new SpecificationTestDraft
                {
                    TestCode = "CALBICANS",
                    TestName = "Candida albicans",
                    SpecificationText = "Absent in 1 g",
                    Unit = string.Empty,
                    ResultType = "Qualitative",
                    RequiredTest = true,
                    MinimumElapsedHours = 120m,
                    SortOrder = 70
                });
                return;
            }

            TxtMasterReference.Text =
                "USP <61>/<62>/<1111>; Ph. Eur. 2.6.12/2.6.13/5.1.4 - non-aqueous preparations for oral use: " +
                "TAMC criterion 10^3 (maximum acceptable count 2000 CFU/g), TYMC criterion 10^2 (maximum acceptable count 200 CFU/g), " +
                "and Escherichia coli absent in 1 g.";

            _specificationTests.Add(new SpecificationTestDraft
            {
                TestCode = "TAMC",
                TestName = "Total Aerobic Microbial Count (TAMC)",
                SpecificationText = "NMT 2000 CFU/g",
                Unit = "CFU/g",
                ResultType = "Numeric",
                RequiredTest = true,
                MinimumElapsedHours = 72m,
                SortOrder = 10
            });
            _specificationTests.Add(new SpecificationTestDraft
            {
                TestCode = "TYMC",
                TestName = "Total Yeast and Mold Count (TYMC)",
                SpecificationText = "NMT 200 CFU/g",
                Unit = "CFU/g",
                ResultType = "Numeric",
                RequiredTest = true,
                MinimumElapsedHours = 120m,
                SortOrder = 20
            });
            _specificationTests.Add(new SpecificationTestDraft
            {
                TestCode = "ECOLI",
                TestName = "Escherichia coli",
                SpecificationText = "Absent in 1 g",
                Unit = string.Empty,
                ResultType = "Qualitative",
                RequiredTest = true,
                MinimumElapsedHours = 120m,
                SortOrder = 30
            });
        }

        private void NewSpecificationDraft()
        {
            _masterVersion = 0;
            _masterApprovalStatus = "Draft";
            TxtMasterSpecificationNo.Text = string.Empty;
            TxtMasterReference.Text = string.Empty;
            TxtMasterItemCode.Text = string.Empty;
            CmbMasterProductionStage.SelectedIndex = -1;
            TxtMasterVersion.Text = "New";
            TxtMasterState.Text = "New Draft";
            DpMasterEffective.SelectedDate = DateTime.Today;
            _specificationTests.Clear();
            _specificationTests.Add(new SpecificationTestDraft { ResultType = "Text", RequiredTest = true, SortOrder = 1, MinimumElapsedHours = 0m });
        }

        private string MasterCategory => GetComboText(CmbMasterCategory).Trim();

        private void BtnLoadSpecification_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string specificationNo = (TxtMasterSpecificationNo.Text ?? string.Empty).Trim();
                string category = MasterCategory;
                if (string.IsNullOrWhiteSpace(specificationNo) || string.IsNullOrWhiteSpace(category))
                    throw new InvalidOperationException("Specification No. and Sample Category are required.");

                DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT * FROM dbo.PRM_SpecificationTests
WHERE LTRIM(RTRIM(SpecificationNo))=@SpecificationNo
  AND LTRIM(RTRIM(SampleCategory))=@Category
  AND VersionNo=(SELECT MAX(VersionNo) FROM dbo.PRM_SpecificationTests WHERE LTRIM(RTRIM(SpecificationNo))=@SpecificationNo AND LTRIM(RTRIM(SampleCategory))=@Category)
ORDER BY ISNULL(SortOrder,SpecificationTestID),SpecificationTestID;",
                    new[]
                    {
                        new SqlParameter("@SpecificationNo", SqlDbType.NVarChar, 120) { Value = specificationNo },
                        new SqlParameter("@Category", SqlDbType.NVarChar, 40) { Value = category }
                    });
                if (table.Rows.Count == 0)
                    throw new InvalidOperationException("No specification version was found for the selected number and category.");

                _masterVersion = Convert.ToInt32(table.Rows[0]["VersionNo"], CultureInfo.InvariantCulture);
                _masterApprovalStatus = Convert.ToString(table.Rows[0]["ApprovalStatus"], CultureInfo.InvariantCulture) ?? "Draft";
                TxtMasterVersion.Text = _masterVersion.ToString(CultureInfo.InvariantCulture);
                TxtMasterState.Text = _masterApprovalStatus;
                TxtMasterReference.Text = table.Rows[0]["CompendialReference"] == DBNull.Value ? string.Empty : Convert.ToString(table.Rows[0]["CompendialReference"], CultureInfo.InvariantCulture) ?? string.Empty;
                TxtMasterItemCode.Text = table.Rows[0].Table.Columns.Contains("ItemCode") && table.Rows[0]["ItemCode"] != DBNull.Value
                    ? Convert.ToString(table.Rows[0]["ItemCode"], CultureInfo.InvariantCulture) ?? string.Empty
                    : string.Empty;
                SetComboText(
                    CmbMasterProductionStage,
                    table.Rows[0].Table.Columns.Contains("ProductionStage") && table.Rows[0]["ProductionStage"] != DBNull.Value
                        ? Convert.ToString(table.Rows[0]["ProductionStage"], CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty);
                DpMasterEffective.SelectedDate = table.Rows[0]["EffectiveDate"] == DBNull.Value ? null : Convert.ToDateTime(table.Rows[0]["EffectiveDate"], CultureInfo.InvariantCulture);
                _specificationTests.Clear();
                foreach (DataRow row in table.Rows)
                {
                    _specificationTests.Add(new SpecificationTestDraft
                    {
                        TestCode = Convert.ToString(row["TestCode"], CultureInfo.InvariantCulture) ?? string.Empty,
                        TestName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty,
                        SpecificationText = Convert.ToString(row["SpecificationText"], CultureInfo.InvariantCulture) ?? string.Empty,
                        Unit = row["Unit"] == DBNull.Value ? string.Empty : Convert.ToString(row["Unit"], CultureInfo.InvariantCulture) ?? string.Empty,
                        ResultType = Convert.ToString(row["ResultType"], CultureInfo.InvariantCulture) ?? "Text",
                        RequiredTest = row["RequiredTest"] != DBNull.Value && Convert.ToBoolean(row["RequiredTest"], CultureInfo.InvariantCulture),
                        MinimumElapsedHours = row.Table.Columns.Contains("MinimumElapsedHours") && row["MinimumElapsedHours"] != DBNull.Value
                            ? Convert.ToDecimal(row["MinimumElapsedHours"], CultureInfo.InvariantCulture)
                            : 0m,
                        SortOrder = row["SortOrder"] == DBNull.Value ? 0 : Convert.ToInt32(row["SortOrder"], CultureInfo.InvariantCulture)
                    });
                }
            }
            catch (Exception ex) { MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex), "Load Specification", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void BtnSaveSpecification_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!DatabaseHelper.CanEditResults(Login.CurrentUser))
                    throw new UnauthorizedAccessException("You are not authorized to maintain specification drafts.");
                if (!_masterApprovalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase) && _masterVersion > 0)
                    throw new InvalidOperationException("Reviewed or approved versions are immutable. Create a new draft version.");

                string specificationNo = (TxtMasterSpecificationNo.Text ?? string.Empty).Trim();
                string category = MasterCategory;
                string reference = (TxtMasterReference.Text ?? string.Empty).Trim();
                string itemCode = (TxtMasterItemCode.Text ?? string.Empty).Trim();
                string productionStage = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                    ? GetComboText(CmbMasterProductionStage).Trim()
                    : string.Empty;
                var validTests = new System.Collections.Generic.List<SpecificationTestDraft>();
                foreach (SpecificationTestDraft test in _specificationTests)
                {
                    if (string.IsNullOrWhiteSpace(test.TestCode) && string.IsNullOrWhiteSpace(test.TestName)) continue;
                    if (string.IsNullOrWhiteSpace(test.TestCode) || string.IsNullOrWhiteSpace(test.TestName) || string.IsNullOrWhiteSpace(test.SpecificationText))
                        throw new InvalidOperationException("Every test requires Code, Test Name, and Acceptance Criteria.");
                    if (test.MinimumElapsedHours < 0m)
                        throw new InvalidOperationException("Minimum Elapsed Hours cannot be negative.");
                    if (test.RequiredTest && test.MinimumElapsedHours <= 0m)
                        throw new InvalidOperationException("Every required microbiology test must define a Minimum Elapsed Hours value greater than zero before the profile can be saved.");
                    test.ResultType = NormalizeControlledResultType(test.ResultType);
                    validTests.Add(test);
                }
                if (string.IsNullOrWhiteSpace(specificationNo) || string.IsNullOrWhiteSpace(category) ||
                    string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(itemCode) || validTests.Count == 0)
                    throw new InvalidOperationException("Inspection Profile No., item/product code, reference, category, and at least one complete test are required.");
                if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(productionStage))
                    throw new InvalidOperationException("Production Stage is required for an In-Process inspection profile.");

                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection, transaction, Login.CurrentUser, "CanEnterResults", "maintain PRM specification drafts");

                    string draftCreatedBy = Login.CurrentUser;
                    DateTime draftCreatedDate;

                    if (_masterVersion <= 0)
                    {
                        using SqlCommand versionCommand = new SqlCommand("SELECT ISNULL(MAX(VersionNo),0)+1 FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK) WHERE SpecificationNo=@No AND SampleCategory=@Category", connection, transaction);
                        versionCommand.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        versionCommand.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        _masterVersion = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);

                        using SqlCommand createdAtCommand = new SqlCommand("SELECT SYSDATETIME();", connection, transaction);
                        draftCreatedDate = Convert.ToDateTime(createdAtCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        using (SqlCommand creatorCommand = new SqlCommand(@"
SELECT TOP(1) CreatedBy, CreatedDate
FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No
  AND SampleCategory=@Category
  AND VersionNo=@Version
  AND ApprovalStatus=N'Draft'
ORDER BY SpecificationTestID;", connection, transaction))
                        {
                            creatorCommand.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                            creatorCommand.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                            creatorCommand.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                            using SqlDataReader creatorReader = creatorCommand.ExecuteReader();
                            if (!creatorReader.Read())
                                throw new DBConcurrencyException("The specification draft no longer exists or is no longer editable. Reload before saving.");
                            draftCreatedBy = creatorReader.IsDBNull(0) ? string.Empty : creatorReader.GetString(0).Trim();
                            draftCreatedDate = creatorReader.GetDateTime(1);
                        }

                        using SqlCommand delete = new SqlCommand("DELETE dbo.PRM_SpecificationTests WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ApprovalStatus=N'Draft'", connection, transaction);
                        delete.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        delete.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        delete.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                        delete.ExecuteNonQuery();
                    }

                    int order = 0;
                    foreach (SpecificationTestDraft test in validTests)
                    {
                        using SqlCommand insert = new SqlCommand(@"
INSERT dbo.PRM_SpecificationTests
(SpecificationNo,SampleCategory,ItemCode,ProductionStage,CompendialReference,VersionNo,TestCode,TestName,SpecificationText,Unit,ResultType,RequiredTest,MinimumElapsedHours,SortOrder,ApprovalStatus,EffectiveDate,IsActive,CreatedBy,CreatedDate)
VALUES(@No,@Category,@ItemCode,@ProductionStage,@Reference,@Version,@Code,@Name,@Criteria,@Unit,@Type,@Required,@MinimumElapsedHours,@Order,N'Draft',@Effective,0,@CreatedBy,@CreatedDate);", connection, transaction);
                        insert.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        insert.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        insert.Parameters.Add("@ItemCode", SqlDbType.NVarChar, 80).Value = itemCode;
                        insert.Parameters.Add("@ProductionStage", SqlDbType.NVarChar, 80).Value = string.IsNullOrWhiteSpace(productionStage) ? DBNull.Value : productionStage;
                        insert.Parameters.Add("@Reference", SqlDbType.NVarChar, 160).Value = reference;
                        insert.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                        insert.Parameters.Add("@Code", SqlDbType.NVarChar, 40).Value = test.TestCode.Trim();
                        insert.Parameters.Add("@Name", SqlDbType.NVarChar, 160).Value = test.TestName.Trim();
                        insert.Parameters.Add("@Criteria", SqlDbType.NVarChar, 500).Value = test.SpecificationText.Trim();
                        insert.Parameters.Add("@Unit", SqlDbType.NVarChar, 50).Value = string.IsNullOrWhiteSpace(test.Unit) ? DBNull.Value : test.Unit.Trim();
                        insert.Parameters.Add("@Type", SqlDbType.NVarChar, 60).Value = string.IsNullOrWhiteSpace(test.ResultType) ? "Text" : test.ResultType.Trim();
                        insert.Parameters.Add("@Required", SqlDbType.Bit).Value = test.RequiredTest;
                        var minimumElapsedParameter = insert.Parameters.Add("@MinimumElapsedHours", SqlDbType.Decimal);
                        minimumElapsedParameter.Precision = 9;
                        minimumElapsedParameter.Scale = 2;
                        minimumElapsedParameter.Value = test.MinimumElapsedHours;
                        insert.Parameters.Add("@Order", SqlDbType.Int).Value = test.SortOrder > 0 ? test.SortOrder : ++order;
                        insert.Parameters.Add("@Effective", SqlDbType.Date).Value = DpMasterEffective.SelectedDate.HasValue ? DpMasterEffective.SelectedDate.Value.Date : DBNull.Value;
                        insert.Parameters.Add("@CreatedBy", SqlDbType.NVarChar, 120).Value = string.IsNullOrWhiteSpace(draftCreatedBy) ? DBNull.Value : draftCreatedBy;
                        SqlParameter createdDateParameter = insert.Parameters.Add("@CreatedDate", SqlDbType.DateTime2);
                        createdDateParameter.Scale = 0;
                        createdDateParameter.Value = draftCreatedDate;
                        insert.ExecuteNonQuery();
                    }

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection, transaction, "PRM_SpecificationTests", 0,
                        "PRM Specification Draft Saved", string.Empty,
                        specificationNo + " v" + _masterVersion.ToString(CultureInfo.InvariantCulture) +
                            "; ItemCode=" + itemCode +
                            (string.IsNullOrWhiteSpace(productionStage) ? string.Empty : "; ProductionStage=" + productionStage),
                        "Controlled draft creation or update.", Login.CurrentUser,
                        "ApprovalStatus", null, specificationNo, "PRM Specification");
                });
                TxtMasterVersion.Text = _masterVersion.ToString(CultureInfo.InvariantCulture);
                TxtMasterState.Text = "Draft";
                _masterApprovalStatus = "Draft";
                TxtStatus.Text = "Specification draft saved.";
            }
            catch (Exception ex) { MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex), "Save Specification", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void BtnReviewSpecification_Click(object sender, RoutedEventArgs e) => ChangeSpecificationState("Reviewed");
        private void BtnApproveSpecification_Click(object sender, RoutedEventArgs e) => ChangeSpecificationState("Approved");

        private void ChangeSpecificationState(string targetStatus)
        {
            try
            {
                if (_masterVersion <= 0) throw new InvalidOperationException("Save or load the specification version first.");
                bool approval = targetStatus == "Approved";
                string scopedItemCode = (TxtMasterItemCode.Text ?? string.Empty).Trim();
                string scopedProductionStage = MasterCategory.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                    ? GetComboText(CmbMasterProductionStage).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(scopedItemCode))
                    throw new InvalidOperationException("Material / Product Code is required before specification review or approval.");
                if (MasterCategory.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(scopedProductionStage))
                    throw new InvalidOperationException("Production Stage is required before an In-Process specification can be reviewed or approved.");
                if (approval && !_masterApprovalStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The specification must be independently reviewed before approval.");
                if (approval ? !DatabaseHelper.CanQaApproveResults(Login.CurrentUser) : !DatabaseHelper.CanReviewResults(Login.CurrentUser))
                    throw new UnauthorizedAccessException("You are not authorized for this specification workflow action.");

                if (approval && !(AppConfig.DevelopmentAdminFullPermissions &&
                    (string.Equals(Login.CurrentUserRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(Login.CurrentUserRole, "Administrator", StringComparison.OrdinalIgnoreCase))))
                {
                    object reviewedBy = DatabaseHelper.ExecuteScalar(@"
SELECT TOP(1) ReviewedBy FROM dbo.PRM_SpecificationTests
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version;",
                        new[]
                        {
                            new SqlParameter("@No", SqlDbType.NVarChar, 120) { Value = TxtMasterSpecificationNo.Text.Trim() },
                            new SqlParameter("@Category", SqlDbType.NVarChar, 40) { Value = MasterCategory },
                            new SqlParameter("@Version", SqlDbType.Int) { Value = _masterVersion }
                        });
                    if (string.Equals(Convert.ToString(reviewedBy, CultureInfo.InvariantCulture), Login.CurrentUser, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The specification approver must be independent from the reviewer.");
                }

                string action = approval ? "Approve Specification" : "Review Specification";
                ElectronicSignature signature = new ElectronicSignature(TxtMasterSpecificationNo.Text + " v" + _masterVersion, Login.CurrentUser, action) { Owner = this };
                if (signature.ShowDialog() != true || !signature.IsConfirmed) return;

                string specificationNo = TxtMasterSpecificationNo.Text.Trim();
                string category = MasterCategory;
                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    string authorizedRole = approval
                        ? DatabaseHelper.EnsureQaApprovalAuthorizationInTransaction(
                            connection,
                            transaction,
                            signature.SignedBy,
                            "approve a PRM specification")
                        : DatabaseHelper.EnsureUserPermissionInTransaction(
                            connection,
                            transaction,
                            signature.SignedBy,
                            "CanReviewResults",
                            "review a PRM specification");

                    string lockedStatus;
                    string lockedReviewedBy;
                    string lockedCreatedBy;
                    using (SqlCommand lockSpecification = new SqlCommand(@"
SELECT TOP(1)
    LTRIM(RTRIM(ISNULL(ApprovalStatus,N''))),
    LTRIM(RTRIM(ISNULL(ReviewedBy,N''))),
    LTRIM(RTRIM(ISNULL(CreatedBy,N'')))
FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
ORDER BY SpecificationTestID;", connection, transaction))
                    {
                        lockSpecification.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        lockSpecification.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        lockSpecification.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                        using SqlDataReader reader = lockSpecification.ExecuteReader();
                        if (!reader.Read())
                            throw new InvalidOperationException("The specification version no longer exists.");
                        lockedStatus = reader.GetString(0);
                        lockedReviewedBy = reader.GetString(1);
                        lockedCreatedBy = reader.GetString(2);
                    }

                    string expectedStatus = approval ? "Reviewed" : "Draft";
                    if (!lockedStatus.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase))
                        throw new DBConcurrencyException("The specification state changed. Reload before continuing.");

                    using (SqlCommand scopeCheck = new SqlCommand(@"
SELECT CASE WHEN COUNT(1)>0 AND COUNT(1)=SUM(CASE WHEN
       UPPER(LTRIM(RTRIM(ISNULL(ItemCode,N''))))=UPPER(LTRIM(RTRIM(@ItemCode)))
       AND (@Category<>N'Production / In-Process' OR UPPER(LTRIM(RTRIM(ISNULL(ProductionStage,N''))))=UPPER(LTRIM(RTRIM(@ProductionStage))) )
    THEN 1 ELSE 0 END) THEN 1 ELSE 0 END
FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version
;", connection, transaction))
                    {
                        scopeCheck.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        scopeCheck.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        scopeCheck.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                        scopeCheck.Parameters.Add("@ItemCode", SqlDbType.NVarChar, 80).Value = scopedItemCode;
                        scopeCheck.Parameters.Add("@ProductionStage", SqlDbType.NVarChar, 80).Value = scopedProductionStage;
                        int scopeValid = Convert.ToInt32(scopeCheck.ExecuteScalar(), CultureInfo.InvariantCulture);
                        if (scopeValid != 1)
                            throw new InvalidOperationException("Every test in the specification version must have the same exact item and production-stage scope.");
                    }

                    using (SqlCommand timingCheck = new SqlCommand(@"
SELECT CASE WHEN COUNT(1)>0 AND COUNT(1)=SUM(CASE WHEN
       ISNULL(RequiredTest,1)=0 OR (MinimumElapsedHours IS NOT NULL AND MinimumElapsedHours > 0)
    THEN 1 ELSE 0 END) THEN 1 ELSE 0 END
FROM dbo.PRM_SpecificationTests WITH(UPDLOCK,HOLDLOCK)
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version;", connection, transaction))
                    {
                        timingCheck.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        timingCheck.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        timingCheck.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                        int timingValid = Convert.ToInt32(timingCheck.ExecuteScalar(), CultureInfo.InvariantCulture);
                        if (timingValid != 1)
                            throw new InvalidOperationException("Every required test must contain an approved Minimum Elapsed Hours value greater than zero before the specification can be reviewed or approved.");
                    }

                    bool developmentOverride = AppConfig.DevelopmentAdminFullPermissions &&
                        !AppConfig.IsProduction &&
                        (string.Equals(authorizedRole, "Admin", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(authorizedRole, "Administrator", StringComparison.OrdinalIgnoreCase));

                    if (!developmentOverride && !approval &&
                        !string.IsNullOrWhiteSpace(lockedCreatedBy) &&
                        lockedCreatedBy.Equals(signature.SignedBy, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The specification reviewer must be independent from the draft creator.");

                    if (!developmentOverride && approval &&
                        !string.IsNullOrWhiteSpace(lockedReviewedBy) &&
                        lockedReviewedBy.Equals(signature.SignedBy, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The specification approver must be independent from the reviewer.");

                    if (approval)
                    {
                        using SqlCommand deactivate = new SqlCommand(@"UPDATE dbo.PRM_SpecificationTests SET IsActive=0
WHERE SpecificationNo=@No AND SampleCategory=@Category;", connection, transaction);
                        deactivate.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                        deactivate.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                        deactivate.ExecuteNonQuery();
                    }
                    using SqlCommand update = new SqlCommand(approval ? @"
UPDATE dbo.PRM_SpecificationTests SET ApprovalStatus=N'Approved',ApprovedBy=@User,ApprovedDate=SYSDATETIME(),IsActive=1
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ApprovalStatus=N'Reviewed';" : @"
UPDATE dbo.PRM_SpecificationTests SET ApprovalStatus=N'Reviewed',ReviewedBy=@User,ReviewedDate=SYSDATETIME()
WHERE SpecificationNo=@No AND SampleCategory=@Category AND VersionNo=@Version AND ApprovalStatus=N'Draft';", connection, transaction);
                    update.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                    update.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                    update.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                    update.Parameters.Add("@User", SqlDbType.NVarChar, 120).Value = Login.CurrentUser;
                    if (update.ExecuteNonQuery() == 0) throw new InvalidOperationException("The specification state changed; reload it and try again.");

                    using SqlCommand sign = new SqlCommand(@"
INSERT dbo.PRM_SpecificationSignatures(SpecificationNo,SampleCategory,VersionNo,ActionType,ActionReason,SignedBy,MeaningOfSignature,UserRole,SignedAt)
VALUES(@No,@Category,@Version,@Action,@Reason,@User,@Meaning,@Role,SYSDATETIME());", connection, transaction);
                    sign.Parameters.Add("@No", SqlDbType.NVarChar, 120).Value = specificationNo;
                    sign.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category;
                    sign.Parameters.Add("@Version", SqlDbType.Int).Value = _masterVersion;
                    sign.Parameters.Add("@Action", SqlDbType.NVarChar, 60).Value = action;
                    sign.Parameters.Add("@Reason", SqlDbType.NVarChar, -1).Value = signature.Reason;
                    sign.Parameters.Add("@User", SqlDbType.NVarChar, 120).Value = signature.SignedBy;
                    sign.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning;
                    sign.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value = authorizedRole;
                    sign.ExecuteNonQuery();

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection, transaction, "PRM_SpecificationTests", 0,
                        action, lockedStatus, targetStatus, signature.Reason,
                        signature.SignedBy, "ApprovalStatus", null,
                        specificationNo + " v" + _masterVersion.ToString(CultureInfo.InvariantCulture),
                        "PRM Specification");
                });
                _masterApprovalStatus = targetStatus;
                TxtMasterState.Text = targetStatus;
                TxtStatus.Text = "Specification version " + targetStatus.ToLowerInvariant() + ".";
                if (approval)
                    LoadApprovedSpecificationChoices();
            }
            catch (Exception ex) { MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex), "Specification Workflow", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private sealed class SpecificationTestDraft
        {
            public string TestCode { get; set; } = string.Empty;
            public string TestName { get; set; } = string.Empty;
            public string SpecificationText { get; set; } = string.Empty;
            public string Unit { get; set; } = string.Empty;
            public string ResultType { get; set; } = "Text";
            public bool RequiredTest { get; set; } = true;
            public decimal MinimumElapsedHours { get; set; } = 0m;
            public int SortOrder { get; set; }
        }

        private static string NormalizeControlledResultType(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            if (normalized.Equals("Numeric", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Numerical", StringComparison.OrdinalIgnoreCase))
                return "Numeric";
            if (normalized.Equals("Qualitative", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Presence/Absence", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Presence - Absence", StringComparison.OrdinalIgnoreCase))
                return "Qualitative";
            if (normalized.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Pass/Fail", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Text Pass/Fail", StringComparison.OrdinalIgnoreCase))
                return "Text";

            throw new InvalidOperationException(
                "Result Type must be one of: Numeric, Qualitative (Presence/Absence), or Text (Pass/Fail).");
        }

        private static string GetCurrentUserDisplayName()
        {
            // Controlled PRM records use the immutable account username for attribution.
            // The person's full name remains display metadata and is not a record identity.
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to use the Production and Raw Material Samples module.");
        }

        private void SetDefaultValues()
        {
            _selectedSampleId = 0;

            if (TxtSampleNo != null) TxtSampleNo.Text = "Auto";
            if (DpSampleDate != null) DpSampleDate.SelectedDate = DateTime.Today;
            if (TxtSampleTime != null) TxtSampleTime.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (DpReceivedDate != null) DpReceivedDate.SelectedDate = DateTime.Today;
            if (TxtSampledBy != null) TxtSampledBy.Text = GetCurrentUserDisplayName();
            if (TxtSearch != null) TxtSearch.Text = string.Empty;

            _selectedCategory = string.IsNullOrWhiteSpace(_selectedCategory) ? "Raw Material" : _selectedCategory;
            SetComboText(CmbUnit, "g");
            SetComboText(CmbRawPurpose, "New Material");
            SetComboText(CmbProductionStage, "After Mixing");


            if (TxtStatus != null) TxtStatus.Text = "New sample registration.";
        }

        private static string GetComboText(ComboBox combo)
        {
            if (combo == null)
                return string.Empty;

            if (combo.SelectedItem is ComboBoxItem item)
                return Convert.ToString(item.Content) ?? string.Empty;

            return combo.Text ?? string.Empty;
        }

        private static void SetComboText(ComboBox combo, string value)
        {
            if (combo == null)
                return;

            foreach (object obj in combo.Items)
            {
                if (obj is ComboBoxItem item &&
                    string.Equals(Convert.ToString(item.Content), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            combo.Text = value;
        }

        private string GetSelectedCategory()
        {
            if (string.IsNullOrWhiteSpace(_selectedCategory))
                return "Raw Material";

            return _selectedCategory;
        }

        private void SelectWorkflow(string category)
        {
            bool previousLoading = _isLoading;
            _isLoading = true;
            try
            {
                _selectedCategory = category;
                if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase))
                    SetComboText(CmbProductionStage, "After Mixing");
                else if (category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase))
                    SetComboText(CmbProductionStage, "Finished Product - After Packaging");
            }
            finally
            {
                _isLoading = previousLoading;
            }

            if (PnlTypeSelection != null)
                PnlTypeSelection.Visibility = Visibility.Collapsed;

            if (PnlRegistration != null)
                PnlRegistration.Visibility = Visibility.Visible;

            if (TxtSampleNo != null && _selectedSampleId == 0)
                TxtSampleNo.Text = "Auto";

            ApplyCategoryLayout();
        }

        private void BtnChooseProduction_Click(object sender, RoutedEventArgs e)
        {
            SelectWorkflow("Production / In-Process");
        }

        private void BtnChooseRaw_Click(object sender, RoutedEventArgs e)
        {
            SelectWorkflow("Raw Material");
        }

        private void BtnChooseStability_Click(object sender, RoutedEventArgs e)
        {
            SelectWorkflow("Stability");
        }

        private void BtnBackToTypes_Click(object sender, RoutedEventArgs e)
        {
            if (PnlRegistration != null)
                PnlRegistration.Visibility = Visibility.Collapsed;

            if (PnlTypeSelection != null)
                PnlTypeSelection.Visibility = Visibility.Visible;

            _selectedSampleId = 0;
            if (DgSamples != null)
                DgSamples.SelectedItem = null;
        }

        private void CmbProductionStage_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || !IsLoaded)
                return;

            string stage = GetComboText(CmbProductionStage);
            if (stage.Equals("Finished Product - After Packaging", StringComparison.OrdinalIgnoreCase))
            {
                _selectedCategory = "Finished Product";
            }
            else if (!string.IsNullOrWhiteSpace(stage))
            {
                _selectedCategory = "Production / In-Process";
            }

            ApplyCategoryLayout();
        }

        private void CmbRawPurpose_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || !IsLoaded)
                return;

            ApplyRetestLayout();
        }

        private void ApplyCategoryLayout()
        {
            string category = GetSelectedCategory();

            bool isRaw = category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase);
            bool isProduction = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase);
            bool isFinished = category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase);
            bool isStability = category.Equals("Stability", StringComparison.OrdinalIgnoreCase);

            if (TxtSelectedWorkflowTitle != null)
                TxtSelectedWorkflowTitle.Text = isRaw ? "Raw Material Sample Registration" :
                                                isStability ? "Stability Sample Registration" :
                                                isFinished ? "Finished Product Sample Registration" :
                                                "Production / In-Process Sample Registration";

            if (TxtSelectedWorkflowNote != null)
                TxtSelectedWorkflowNote.Text = isRaw ? "Register new material, retest, vendor qualification, additional test, or complaint/investigation samples." :
                                               isStability ? "Register stability study pull point samples for testing and reporting." :
                                               isFinished ? "Register finished product samples after packaging." :
                                               "Register in-process samples after mixing, compression, or coating.";

            SetVisibilitySafe(GrpRawMaterial, isRaw);
            SetVisibilitySafe(GrpProduction, isProduction || isFinished);
            SetVisibilitySafe(GrpStability, isStability);

            if (isFinished)
                SetComboText(CmbProductionStage, "Finished Product - After Packaging");

            if (isRaw)
                ApplyRetestLayout();
            else
                SetVisibilitySafe(PnlRetest, false);

            // Loading an existing record must preserve its frozen specification display,
            // including inactive historical versions that are no longer selectable for new work.
            if (!_isLoading)
                LoadApprovedSpecificationChoices();
        }

        private void TxtSpecificationNo_DropDownOpened(object sender, EventArgs e) => LoadApprovedSpecificationChoices();

        private void ProfileScope_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isLoading || !IsLoaded)
                return;

            LoadApprovedSpecificationChoices();
        }

        private void BtnCreateProfileForScope_Click(object sender, RoutedEventArgs e)
        {
            string category = GetSelectedCategory();
            string itemCode = GetSelectedItemCode(category);
            string productionStage = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                ? GetComboText(CmbProductionStage).Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(itemCode))
            {
                MessageBox.Show(
                    "Enter the current Material / Product Code before creating its inspection profile.",
                    "Inspection Profile Scope", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(productionStage))
            {
                MessageBox.Show(
                    "Select the current Production Stage before creating its inspection profile.",
                    "Inspection Profile Scope", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _returnToRegistrationAfterMaster = true;
            PnlTypeSelection.Visibility = Visibility.Collapsed;
            PnlRegistration.Visibility = Visibility.Collapsed;
            PnlSpecificationMaster.Visibility = Visibility.Visible;
            NewSpecificationDraft();
            SetComboText(CmbMasterCategory, category);
            TxtMasterItemCode.Text = itemCode;
            SetComboText(CmbMasterProductionStage, productionStage);
            ApplyPharmacopoeialMicrobiologyTemplate(category);
            TxtMasterState.Text = "New Draft — current sample scope copied and the pharmacopoeial microbiology template loaded. Verify the approved product/material specification before Review -> Approve.";
            TxtMasterSpecificationNo.Focus();
        }

        private int LoadApprovedSpecificationChoices()
        {
            if (TxtSpecificationNo == null) return 0;
            string category = GetSelectedCategory();
            string itemCode = GetSelectedItemCode(category);
            string productionStage = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                ? GetComboText(CmbProductionStage).Trim()
                : string.Empty;
            string previousSelection = (TxtSpecificationNo.Text ?? string.Empty).Trim();

            TxtSpecificationNo.Items.Clear();
            TxtSpecificationNo.SelectedIndex = -1;
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(itemCode) ||
                (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(productionStage)))
            {
                if (TxtProfileGuidance != null)
                {
                    TxtProfileGuidance.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
                    TxtProfileGuidance.Text = string.IsNullOrWhiteSpace(itemCode)
                        ? "Enter the Material / Product Code to load its approved inspection profiles."
                        : "Select the Production Stage to load its approved inspection profiles.";
                }
                return 0;
            }

            DataTable table = DatabaseHelper.ExecuteQuery(@"
;WITH Candidates AS
(
    SELECT configured.SpecificationNo,
           MAX(
               (CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(configured.ItemCode,N''))))=UPPER(LTRIM(RTRIM(@ItemCode))) THEN 2 ELSE 0 END) +
               (CASE WHEN @Category=N'Production / In-Process'
                          AND UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N''))))=UPPER(LTRIM(RTRIM(@ProductionStage)))
                     THEN 1 ELSE 0 END)
           ) AS MatchRank
    FROM dbo.PRM_SpecificationTests configured
    WHERE configured.SampleCategory=@Category
      AND configured.ApprovalStatus=N'Approved'
      AND configured.IsActive=1
      AND NOT (
          ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913'
          AND (
              ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913'
              OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'
          )
      )
" + PrmSpecificationRepository.ApprovedProfileEvidencePredicateSql + @"
      AND UPPER(LTRIM(RTRIM(ISNULL(configured.ItemCode,N'')))) IN
          (UPPER(LTRIM(RTRIM(@ItemCode))),N'*')
      AND
      (
          @Category<>N'Production / In-Process'
          OR UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N'')))) IN
             (UPPER(LTRIM(RTRIM(@ProductionStage))),N'*')
      )
      AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate<=CAST(GETDATE() AS date))
    GROUP BY configured.SpecificationNo
)
SELECT SpecificationNo
FROM Candidates
WHERE MatchRank=(SELECT MAX(MatchRank) FROM Candidates)
ORDER BY SpecificationNo;",
                new[]
                {
                    new SqlParameter("@Category", SqlDbType.NVarChar, 40) { Value = category },
                    new SqlParameter("@ItemCode", SqlDbType.NVarChar, 80) { Value = itemCode },
                    new SqlParameter("@ProductionStage", SqlDbType.NVarChar, 80) { Value = productionStage },
                    new SqlParameter("@RequireIndependentApprover", SqlDbType.Bit) { Value = AppConfig.IsProduction }
                });
            foreach (DataRow row in table.Rows)
                TxtSpecificationNo.Items.Add(Convert.ToString(row[0], CultureInfo.InvariantCulture));

            if (table.Rows.Count == 1)
            {
                TxtSpecificationNo.SelectedIndex = 0;
            }
            else if (!string.IsNullOrWhiteSpace(previousSelection))
            {
                foreach (object choice in TxtSpecificationNo.Items)
                {
                    if (string.Equals(Convert.ToString(choice, CultureInfo.InvariantCulture), previousSelection, StringComparison.OrdinalIgnoreCase))
                    {
                        TxtSpecificationNo.SelectedItem = choice;
                        break;
                    }
                }
            }

            if (TxtProfileGuidance != null)
            {
                if (table.Rows.Count == 0)
                {
                    TxtProfileGuidance.Foreground = System.Windows.Media.Brushes.Firebrick;
                    TxtProfileGuidance.Text = BuildNoApprovedProfileMessage(category, itemCode, productionStage);
                }
                else if (table.Rows.Count == 1)
                {
                    TxtProfileGuidance.Foreground = System.Windows.Media.Brushes.SeaGreen;
                    TxtProfileGuidance.Text = "The approved inspection profile was selected automatically.";
                }
                else
                {
                    TxtProfileGuidance.Foreground = System.Windows.Media.Brushes.DarkGoldenrod;
                    TxtProfileGuidance.Text = "Multiple approved profiles match this scope. Select the intended controlled profile.";
                }
            }

            return table.Rows.Count;
        }

        private static string BuildNoApprovedProfileMessage(string category, string itemCode, string productionStage)
        {
            string scope = category + " / " + itemCode;
            if (!string.IsNullOrWhiteSpace(productionStage))
                scope += " / " + productionStage;

            return "No approved active inspection profile matches " + scope +
                ". The controlled standard profile is unavailable. If a Draft or Reviewed profile exists, QA must verify the approved site/product specification and complete the normal Specification Master Review -> Approve electronic-signature workflow before registration can continue. " +
                "Use Database Maintenance/deployment only when System Preflight reports a pending controlled migration or schema blocker. " +
                "An Approved status without matching Review/Approve specification-signature evidence is intentionally excluded from registration. " +
                "Create an item-specific exception only when scientifically justified and formally approved.";
        }

        private static string BuildNoApprovedProfileOperatorMessage(string category, string itemCode, string productionStage)
        {
            string scope = category + " / " + (string.IsNullOrWhiteSpace(itemCode) ? "current item" : itemCode);
            if (!string.IsNullOrWhiteSpace(productionStage))
                scope += " / " + productionStage;

            return "No approved active inspection profile matches " + scope +
                ". Open Specification Master (or use Create Profile for This Scope), then complete Review -> Approve. " +
                "Registration remains blocked until an approved profile with valid electronic-signature evidence is available.";
        }

        private string GetSelectedItemCode(string category)
        {
            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
                return (TxtMaterialCode.Text ?? string.Empty).Trim();
            if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
                return (TxtStbProductCode.Text ?? string.Empty).Trim();
            return (TxtProductCode.Text ?? string.Empty).Trim();
        }

        private void ApplyRetestLayout()
        {
            string purpose = GetComboText(CmbRawPurpose);
            SetVisibilitySafe(PnlRetest, purpose.Equals("Retest", StringComparison.OrdinalIgnoreCase));
        }

        private static void SetVisibilitySafe(UIElement element, bool visible)
        {
            if (element == null)
                return;

            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnNew_Click(object sender, RoutedEventArgs e)
        {
            ClearForm();
        }

        private void ClearForm()
        {
            _selectedSampleId = 0;
            TxtSampleNo.Text = "Auto";

            TxtMaterialCode.Text = string.Empty;
            TxtMaterialName.Text = string.Empty;
            TxtManufacturer.Text = string.Empty;
            TxtSupplier.Text = string.Empty;
            TxtManufacturerLot.Text = string.Empty;
            TxtSupplierLot.Text = string.Empty;
            TxtGrn.Text = string.Empty;
            TxtRawStorage.Text = string.Empty;
            TxtRetestReason.Text = string.Empty;
            TxtPreviousReportNo.Text = string.Empty;
            TxtRetestRemarks.Text = string.Empty;

            TxtProductCode.Text = string.Empty;
            TxtProductName.Text = string.Empty;
            TxtBatchNo.Text = string.Empty;
            TxtBatchSize.Text = string.Empty;
            TxtSampledFrom.Text = string.Empty;
            TxtMachineLine.Text = string.Empty;
            TxtPackSize.Text = string.Empty;
            if (TxtStbProductCode != null) TxtStbProductCode.Text = string.Empty;
            if (TxtStbProductName != null) TxtStbProductName.Text = string.Empty;
            if (TxtStbBatchNo != null) TxtStbBatchNo.Text = string.Empty;
            if (TxtChamberNo != null) TxtChamberNo.Text = string.Empty;
            if (TxtProtocolNo != null) TxtProtocolNo.Text = string.Empty;

            TxtSpecificationNo.Text = string.Empty;
            TxtTestsRequired.Text = string.Empty;
            TxtSampleQty.Text = string.Empty;
            TxtRemarks.Text = string.Empty;

            DpSampleDate.SelectedDate = DateTime.Today;
            TxtSampleTime.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            DpReceivedDate.SelectedDate = DateTime.Today;
            DpManufacturerExpiry.SelectedDate = null;
            DpRetestDate.SelectedDate = null;
            DpPreviousRetestDate.SelectedDate = null;
            DpProposedRetestDate.SelectedDate = null;
            DpManufacturingDate.SelectedDate = null;
            DpPackagingDate.SelectedDate = null;
            DpExpiryDate.SelectedDate = null;

            bool previousLoading = _isLoading;
            _isLoading = true;
            try
            {
                SetComboText(CmbRawPurpose, "New Material");
                if (GetSelectedCategory().Equals("Finished Product", StringComparison.OrdinalIgnoreCase))
                    SetComboText(CmbProductionStage, "Finished Product - After Packaging");
                else if (GetSelectedCategory().Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase))
                    SetComboText(CmbProductionStage, "After Mixing");
            }
            finally
            {
                _isLoading = previousLoading;
            }

            ApplyCategoryLayout();

            TxtStatus.Text = "New sample registration.";
            DgSamples.SelectedItem = null;
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await LoadSamplesAsync();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Manual PRM sample refresh failed.", ex);
                MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex),
                    "Refresh PRM Samples", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LoadSamples()
        {
            DataTable table = QuerySamples((TxtSearch.Text ?? string.Empty).Trim());
            ApplySamples(table);
        }

        private async Task LoadSamplesAsync()
        {
            string search = (TxtSearch.Text ?? string.Empty).Trim();
            BtnRefresh.IsEnabled = false;
            TxtStatus.Text = "Loading PRM sample records...";

            try
            {
                DataTable table = await Task.Run(() => QuerySamples(search));
                ApplySamples(table);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("PRM sample list loading failed.", ex);
                TxtStatus.Text = "PRM sample records could not be loaded.";
                throw;
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
            }
        }

        private static DataTable QuerySamples(string search)
        {
            string query = @"
SELECT TOP 300
    SampleID,
    SampleNumber,
    SampleCategory,
    CASE
        WHEN SampleCategory = N'Raw Material' THEN ISNULL(SamplePurpose, N'')
        WHEN SampleCategory = N'Production / In-Process' THEN ISNULL(ProductionStage, N'')
        WHEN SampleCategory = N'Finished Product' THEN ISNULL(SampleSource, N'After Packaging')
        ELSE N''
    END AS PurposeOrStage,
    CASE
        WHEN SampleCategory = N'Raw Material' THEN ISNULL(MaterialName, N'')
        ELSE ISNULL(ProductName, N'')
    END AS ItemName,
    CASE
        WHEN SampleCategory = N'Raw Material' THEN ISNULL(ManufacturerLotNo, N'')
        ELSE ISNULL(BatchNo, N'')
    END AS LotOrBatch,
    CONVERT(NVARCHAR(20), SampleDateTime, 120) AS SampleDateTimeText,
    SampleStatus,
    ReportStatus,
    CreatedBy
FROM dbo.PRM_Samples
WHERE (@search = N''
       OR SampleNumber LIKE N'%' + @search + N'%'
       OR MaterialName LIKE N'%' + @search + N'%'
       OR ProductName LIKE N'%' + @search + N'%'
       OR ManufacturerLotNo LIKE N'%' + @search + N'%'
       OR BatchNo LIKE N'%' + @search + N'%')
ORDER BY SampleID DESC;";

            SqlParameter[] pars =
            {
                new SqlParameter("@search", SqlDbType.NVarChar, 200) { Value = search }
            };

            return DatabaseHelper.ExecuteQuery(query, pars, commandTimeoutSeconds: 10);
        }

        private void ApplySamples(DataTable table)
        {
            DgSamples.ItemsSource = table.DefaultView;
            TxtStatus.Text = "Loaded " + table.Rows.Count + " PRM sample record(s).";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string actor = GetCurrentUserDisplayName();
                if (!DatabaseHelper.CanRegisterSamples(actor))
                    throw new UnauthorizedAccessException("Sample Registration permission is required to create or edit PRM samples.");

                ValidateForm();

                if (_selectedSampleId > 0)
                    UpdateSample();
                else
                    InsertSample();

                LoadSamples();
            }
            catch (Exception ex)
            {
                ShowOperationError("Save Sample", ex);
            }
        }

        private void ValidateForm()
        {
            string category = GetSelectedCategory();
            if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) &&
                GetComboText(CmbProductionStage).Equals("Finished Product - After Packaging", StringComparison.OrdinalIgnoreCase))
            {
                category = "Finished Product";
                _selectedCategory = category;
            }

            if (string.IsNullOrWhiteSpace(category))
                throw new InvalidOperationException("Sample Category is required.");

            if (!DpSampleDate.SelectedDate.HasValue)
                throw new InvalidOperationException("Sample Date is required.");

            _ = GetRequiredSampleDateTime();

            if (string.IsNullOrWhiteSpace(TxtSampledBy.Text))
                throw new InvalidOperationException("Sampled By is required.");

            string specificationNo = (TxtSpecificationNo.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(specificationNo))
            {
                LoadApprovedSpecificationChoices();
                specificationNo = (TxtSpecificationNo.Text ?? string.Empty).Trim();
            }
            string itemCode = GetSelectedItemCode(category);
            string productionStage = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                ? GetComboText(CmbProductionStage).Trim()
                : string.Empty;
            if (string.IsNullOrWhiteSpace(specificationNo))
                throw new InvalidOperationException(BuildNoApprovedProfileOperatorMessage(category, itemCode, productionStage));
            object approvedSpecification = DatabaseHelper.ExecuteScalar(@"
SELECT COUNT(1)
FROM dbo.PRM_SpecificationTests configured
WHERE configured.SpecificationNo=@SpecificationNo
  AND configured.SampleCategory=@Category
  AND UPPER(LTRIM(RTRIM(ISNULL(configured.ItemCode,N'')))) IN (UPPER(LTRIM(RTRIM(@ItemCode))),N'*')
  AND (@Category<>N'Production / In-Process' OR UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N'')))) IN (UPPER(LTRIM(RTRIM(@ProductionStage))),N'*'))
  AND configured.ApprovalStatus=N'Approved'
  AND configured.IsActive=1
  AND NOT (
      ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913'
      AND (
          ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913'
          OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'
      )
  )
" + PrmSpecificationRepository.ApprovedProfileEvidencePredicateSql + @"
  AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate<=CAST(GETDATE() AS date));",
                new[]
                {
                    new SqlParameter("@SpecificationNo", SqlDbType.NVarChar, 120) { Value = specificationNo },
                    new SqlParameter("@Category", SqlDbType.NVarChar, 40) { Value = category },
                    new SqlParameter("@ItemCode", SqlDbType.NVarChar, 80) { Value = itemCode },
                    new SqlParameter("@ProductionStage", SqlDbType.NVarChar, 80) { Value = productionStage },
                    new SqlParameter("@RequireIndependentApprover", SqlDbType.Bit) { Value = AppConfig.IsProduction }
                });
            if (Convert.ToInt32(approvedSpecification, CultureInfo.InvariantCulture) == 0)
                throw new InvalidOperationException("No approved standard or item-specific inspection profile is available for this sample scope.");

            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(TxtMaterialCode.Text))
                    throw new InvalidOperationException("Material Code is required.");

                if (string.IsNullOrWhiteSpace(TxtMaterialName.Text))
                    throw new InvalidOperationException("Material Name is required.");

                if (string.IsNullOrWhiteSpace(TxtManufacturerLot.Text))
                    throw new InvalidOperationException("Manufacturer Lot No. is required.");

                if (GetComboText(CmbRawPurpose).Equals("Retest", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(TxtRetestReason.Text))
                    throw new InvalidOperationException("Retest Reason is required for Retest samples.");
            }
            else if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(TxtProductCode.Text))
                    throw new InvalidOperationException("Product Code is required.");

                if (string.IsNullOrWhiteSpace(TxtProductName.Text))
                    throw new InvalidOperationException("Product Name is required.");

                if (string.IsNullOrWhiteSpace(TxtBatchNo.Text))
                    throw new InvalidOperationException("Batch No. is required.");

                if (string.IsNullOrWhiteSpace(GetComboText(CmbProductionStage)))
                    throw new InvalidOperationException("Production Stage is required.");
            }
            else if (category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(TxtProductCode.Text))
                    throw new InvalidOperationException("Product Code is required.");

                if (string.IsNullOrWhiteSpace(TxtProductName.Text))
                    throw new InvalidOperationException("Product Name is required.");

                if (string.IsNullOrWhiteSpace(TxtBatchNo.Text))
                    throw new InvalidOperationException("Batch No. is required.");
            }
            else if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(TxtStbProductCode.Text))
                    throw new InvalidOperationException("Product Code is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(TxtStbProductName.Text))
                    throw new InvalidOperationException("Product Name is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(TxtStbBatchNo.Text))
                    throw new InvalidOperationException("Batch No. is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(GetComboText(CmbStabilityStudyType)))
                    throw new InvalidOperationException("Study Type is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(GetComboText(CmbPullPoint)))
                    throw new InvalidOperationException("Pull Point is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(GetComboText(CmbStabilityCondition)))
                    throw new InvalidOperationException("Storage Condition is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(TxtChamberNo.Text))
                    throw new InvalidOperationException("Chamber No. is required for Stability samples.");

                if (string.IsNullOrWhiteSpace(TxtProtocolNo.Text))
                    throw new InvalidOperationException("Protocol No. is required for Stability samples.");
            }
        }

        private void InsertSample()
        {
            string category = GetSelectedCategory();
            if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) &&
                GetComboText(CmbProductionStage).Equals("Finished Product - After Packaging", StringComparison.OrdinalIgnoreCase))
            {
                category = "Finished Product";
                _selectedCategory = category;
            }

            string actor = GetCurrentUserDisplayName();
            DateTime sampleDateTime = GetRequiredSampleDateTime();
            string sampleNumber = string.Empty;
            int sampleId = 0;
            int assignedTests = 0;

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanRegisterSamples", "register PRM samples");

                string itemCode = GetSelectedItemCode(category);
                string productionStage = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                    ? GetComboText(CmbProductionStage).Trim()
                    : string.Empty;
                int specificationVersion = EnsureApprovedSpecificationInTransaction(
                    connection, transaction, (TxtSpecificationNo.Text ?? string.Empty).Trim(), category, itemCode, productionStage);

                sampleNumber = GetNextSampleNumber(connection, transaction, category);
                SqlParameter[] parameters = BuildParameters(sampleNumber, sampleDateTime, specificationVersion);

                using (SqlCommand command = new SqlCommand(@"
INSERT INTO dbo.PRM_Samples
(
    SampleNumber, SampleCategory, SamplePurpose,
    MaterialCode, MaterialName, MaterialType, Manufacturer, Supplier, ManufacturerLotNo, SupplierLotNo, GRNNo,
    ReceivedDate, ManufacturerExpiryDate, RetestDate, RetestReason, PreviousReportNo, PreviousRetestDate, ProposedRetestDate, RetestRemarks,
    ProductCode, ProductName, BatchNo, BatchSize, DosageForm, ProductionStage, SampleSource, SampledFrom, MachineLineNo,
    ManufacturingDate, PackagingDate, ExpiryDate, PackSize,
    SpecificationNo, SpecificationVersionNo, TestsRequired, SampleQuantity, Unit, StorageCondition,
    StabilityChamberNo, StabilityProtocolNo, SampleDateTime, SampledBy, Department, Remarks,
    SampleStatus, ResultInterpretation, ReportStatus, TimingReconciliationStatus, CreatedBy
)
VALUES
(
    @SampleNumber, @SampleCategory, @SamplePurpose,
    @MaterialCode, @MaterialName, @MaterialType, @Manufacturer, @Supplier, @ManufacturerLotNo, @SupplierLotNo, @GRNNo,
    @ReceivedDate, @ManufacturerExpiryDate, @RetestDate, @RetestReason, @PreviousReportNo, @PreviousRetestDate, @ProposedRetestDate, @RetestRemarks,
    @ProductCode, @ProductName, @BatchNo, @BatchSize, @DosageForm, @ProductionStage, @SampleSource, @SampledFrom, @MachineLineNo,
    @ManufacturingDate, @PackagingDate, @ExpiryDate, @PackSize,
    @SpecificationNo, @SpecificationVersionNo, @TestsRequired, @SampleQuantity, @Unit, @StorageCondition,
    @StabilityChamberNo, @StabilityProtocolNo, @SampleDateTime, @SampledBy, @Department, @Remarks,
    N'Registered', N'Not Tested', N'Not Issued', N'Not Required', @CreatedBy
);
SELECT CAST(SCOPE_IDENTITY() AS INT);", connection, transaction))
                {
                    command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    command.Parameters.AddRange(parameters);
                    object result = command.ExecuteScalar();
                    if (result == null || result == DBNull.Value)
                        throw new InvalidOperationException("PRM sample registration did not return a SampleID. The transaction was cancelled.");

                    sampleId = Convert.ToInt32(result, CultureInfo.InvariantCulture);
                }

                assignedTests = _prmSpecificationRepository.EnsureSampleTestsAssigned(connection, transaction, sampleId);
                if (assignedTests <= 0)
                    throw new InvalidOperationException("No approved PRM tests were assigned. The sample registration was rolled back.");

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "PRM_Samples", sampleId,
                    "PRM Sample Registered", string.Empty,
                    ReadPrmRegistrationSnapshotInTransaction(connection, transaction, sampleId) +
                        "; AssignedTests=" + assignedTests.ToString(CultureInfo.InvariantCulture),
                    "Controlled PRM sample registration with approved specification and assigned tests.",
                    actor, "Registration", null, sampleNumber, "PRM");
            });

            _selectedSampleId = sampleId;
            TxtSampleNo.Text = sampleNumber;
            TxtStatus.Text = "Sample registered successfully: " + sampleNumber +
                ". Assigned tests: " + assignedTests.ToString(CultureInfo.InvariantCulture) + ".";
        }

        private void UpdateSample()
        {
            if (_selectedSampleId <= 0)
                throw new InvalidOperationException("Select a PRM sample before editing.");

            string actor = GetCurrentUserDisplayName();
            string sampleNumber = string.Empty;
            string newSpecification = (TxtSpecificationNo.Text ?? string.Empty).Trim();
            string newCategory = GetSelectedCategory().Trim();
            DateTime newSampleDateTime = GetRequiredSampleDateTime();
            int assignedTests = 0;

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection, transaction, actor, "CanRegisterSamples", "edit registered PRM samples");

                string originalSpecification;
                string originalCategory;
                string originalItemCode;
                string originalProductionStage;
                int originalSpecificationVersion;
                string sampleStatus;

                using (SqlCommand lockSample = new SqlCommand(@"
SELECT
    LTRIM(RTRIM(ISNULL(SampleNumber, N''))) AS SampleNumber,
    LTRIM(RTRIM(ISNULL(SpecificationNo, N''))) AS SpecificationNo,
    LTRIM(RTRIM(ISNULL(SampleCategory, N''))) AS SampleCategory,
    LTRIM(RTRIM(ISNULL(SampleStatus, N''))) AS SampleStatus,
    CASE WHEN UPPER(LTRIM(RTRIM(ISNULL(SampleCategory,N'')))) IN(N'RAW MATERIAL',N'RAW MATERIALS',N'RM')
         THEN LTRIM(RTRIM(ISNULL(MaterialCode,N'')))
         ELSE LTRIM(RTRIM(ISNULL(ProductCode,N''))) END AS ItemCode,
    LTRIM(RTRIM(ISNULL(ProductionStage,N''))) AS ProductionStage,
    ISNULL(SpecificationVersionNo,0) AS SpecificationVersionNo
FROM dbo.PRM_Samples WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID;", connection, transaction))
                {
                    lockSample.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    lockSample.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                    using SqlDataReader reader = lockSample.ExecuteReader();
                    if (!reader.Read())
                        throw new InvalidOperationException("The selected PRM sample was not found.");

                    sampleNumber = reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim();
                    originalSpecification = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
                    originalCategory = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
                    sampleStatus = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                    originalItemCode = reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim();
                    originalProductionStage = reader.IsDBNull(5) ? string.Empty : reader.GetString(5).Trim();
                    originalSpecificationVersion = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture);
                }

                if (!sampleStatus.Equals("Registered", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only Registered PRM samples can be edited in this registration screen.");

                if (!originalCategory.Equals(newCategory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Sample Category cannot be changed after PRM registration because the controlled sample number is category-specific. Register a new sample instead.");
                }

                string oldSnapshot = ReadPrmRegistrationSnapshotInTransaction(
                    connection, transaction, _selectedSampleId);

                string itemCode = GetSelectedItemCode(newCategory);
                string productionStage = newCategory.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase)
                    ? GetComboText(CmbProductionStage).Trim()
                    : string.Empty;
                int specificationVersion = EnsureApprovedSpecificationInTransaction(
                    connection, transaction, newSpecification, newCategory, itemCode, productionStage);

                bool specificationBindingChanged =
                    !originalSpecification.Equals(newSpecification, StringComparison.OrdinalIgnoreCase) ||
                    !originalItemCode.Equals(itemCode, StringComparison.OrdinalIgnoreCase) ||
                    (newCategory.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) &&
                     !originalProductionStage.Equals(productionStage, StringComparison.OrdinalIgnoreCase)) ||
                    originalSpecificationVersion != specificationVersion;

                if (specificationBindingChanged)
                {
                    using SqlCommand enteredResults = new SqlCommand(@"
SELECT COUNT(1)
FROM dbo.PRM_SampleTests WITH (UPDLOCK, HOLDLOCK)
WHERE SampleID = @SampleID
  AND NULLIF(LTRIM(RTRIM(ISNULL(ResultValue, N''))), N'') IS NOT NULL;", connection, transaction);
                    enteredResults.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    enteredResults.Parameters.Add("@SampleID", SqlDbType.Int).Value = _selectedSampleId;
                    if (Convert.ToInt32(enteredResults.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                        throw new InvalidOperationException("The specification or sample category cannot be changed after any PRM test result has been entered.");
                }

                SqlParameter[] parameters = BuildParameters(sampleNumber, newSampleDateTime, specificationVersion);
                Array.Resize(ref parameters, parameters.Length + 1);
                parameters[parameters.Length - 1] = new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId };

                using (SqlCommand update = new SqlCommand(@"
UPDATE dbo.PRM_Samples
SET
    SamplePurpose = @SamplePurpose,
    MaterialCode = @MaterialCode,
    MaterialName = @MaterialName,
    MaterialType = @MaterialType,
    Manufacturer = @Manufacturer,
    Supplier = @Supplier,
    ManufacturerLotNo = @ManufacturerLotNo,
    SupplierLotNo = @SupplierLotNo,
    GRNNo = @GRNNo,
    ReceivedDate = @ReceivedDate,
    ManufacturerExpiryDate = @ManufacturerExpiryDate,
    RetestDate = @RetestDate,
    RetestReason = @RetestReason,
    PreviousReportNo = @PreviousReportNo,
    PreviousRetestDate = @PreviousRetestDate,
    ProposedRetestDate = @ProposedRetestDate,
    RetestRemarks = @RetestRemarks,
    ProductCode = @ProductCode,
    ProductName = @ProductName,
    BatchNo = @BatchNo,
    BatchSize = @BatchSize,
    DosageForm = @DosageForm,
    ProductionStage = @ProductionStage,
    SampleSource = @SampleSource,
    SampledFrom = @SampledFrom,
    MachineLineNo = @MachineLineNo,
    ManufacturingDate = @ManufacturingDate,
    PackagingDate = @PackagingDate,
    ExpiryDate = @ExpiryDate,
    PackSize = @PackSize,
    SpecificationNo = @SpecificationNo,
    SpecificationVersionNo = @SpecificationVersionNo,
    TestsRequired = @TestsRequired,
    SampleQuantity = @SampleQuantity,
    Unit = @Unit,
    StorageCondition = @StorageCondition,
    StabilityChamberNo = @StabilityChamberNo,
    StabilityProtocolNo = @StabilityProtocolNo,
    SampleDateTime = @SampleDateTime,
    SampledBy = @SampledBy,
    Department = @Department,
    Remarks = @Remarks,
    ModifiedBy = @CreatedBy,
    ModifiedDate = SYSDATETIME()
WHERE SampleID = @SampleID
  AND SampleStatus = N'Registered';", connection, transaction))
                {
                    update.CommandTimeout = AppConfig.CommandTimeoutSeconds;
                    update.Parameters.AddRange(parameters);
                    if (update.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("The PRM sample is no longer Registered or was changed by another user. Reload and retry.");
                }

                assignedTests = specificationBindingChanged
                    ? _prmSpecificationRepository.ReplaceUnenteredSampleTests(connection, transaction, _selectedSampleId)
                    : _prmSpecificationRepository.EnsureSampleTestsAssigned(connection, transaction, _selectedSampleId);

                if (assignedTests <= 0)
                    throw new InvalidOperationException("No approved PRM tests are assigned after the edit. The update was rolled back.");

                string newSnapshot = ReadPrmRegistrationSnapshotInTransaction(
                    connection, transaction, _selectedSampleId) +
                    "; AssignedTests=" + assignedTests.ToString(CultureInfo.InvariantCulture);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection, transaction, "PRM_Samples", _selectedSampleId,
                    "PRM Registration Updated", oldSnapshot, newSnapshot,
                    "Controlled edit of a Registered PRM sample before result entry.",
                    actor, "Registration", null, sampleNumber, "PRM");
            });

            TxtSampleNo.Text = sampleNumber;
            TxtStatus.Text = "Sample updated successfully: " + sampleNumber +
                ". Assigned tests: " + assignedTests.ToString(CultureInfo.InvariantCulture) + ".";
        }

        private SqlParameter[] BuildParameters(string sampleNumber, DateTime sampleDateTime, int specificationVersion)
        {
            string category = GetSelectedCategory();
            bool isRaw = category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase);
            bool isProduction = category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase);
            bool isFinished = category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase);
            bool isStability = category.Equals("Stability", StringComparison.OrdinalIgnoreCase);

            decimal? sampleQty = TryParseDecimal(TxtSampleQty.Text);

            return new[]
            {
                P("@SampleNumber", sampleNumber),
                P("@SampleCategory", category),
                P("@SamplePurpose", isRaw ? GetComboText(CmbRawPurpose) : null),

                P("@MaterialCode", isRaw ? TxtMaterialCode.Text : null),
                P("@MaterialName", isRaw ? TxtMaterialName.Text : null),
                P("@MaterialType", isRaw ? GetComboText(CmbMaterialType) : null),
                P("@Manufacturer", isRaw ? TxtManufacturer.Text : null),
                P("@Supplier", isRaw ? TxtSupplier.Text : null),
                P("@ManufacturerLotNo", isRaw ? TxtManufacturerLot.Text : null),
                P("@SupplierLotNo", isRaw ? TxtSupplierLot.Text : null),
                P("@GRNNo", isRaw ? TxtGrn.Text : null),
                PDate("@ReceivedDate", isRaw ? DpReceivedDate.SelectedDate : null),
                PDate("@ManufacturerExpiryDate", isRaw ? DpManufacturerExpiry.SelectedDate : null),
                PDate("@RetestDate", isRaw ? DpRetestDate.SelectedDate : null),
                P("@RetestReason", isRaw ? TxtRetestReason.Text : null),
                P("@PreviousReportNo", isRaw ? TxtPreviousReportNo.Text : null),
                PDate("@PreviousRetestDate", isRaw ? DpPreviousRetestDate.SelectedDate : null),
                PDate("@ProposedRetestDate", isRaw ? DpProposedRetestDate.SelectedDate : null),
                P("@RetestRemarks", isRaw ? TxtRetestRemarks.Text : null),

                P("@ProductCode", isStability ? TxtStbProductCode.Text : ((isProduction || isFinished) ? TxtProductCode.Text : null)),
                P("@ProductName", isStability ? TxtStbProductName.Text : ((isProduction || isFinished) ? TxtProductName.Text : null)),
                P("@BatchNo", isStability ? TxtStbBatchNo.Text : ((isProduction || isFinished) ? TxtBatchNo.Text : null)),
                P("@BatchSize", (isProduction || isFinished) ? TxtBatchSize.Text : null),
                P("@DosageForm", (isProduction || isFinished) ? GetComboText(CmbDosageForm) : null),
                P("@ProductionStage", isProduction ? GetComboText(CmbProductionStage) : (isStability ? GetComboText(CmbStabilityStudyType) : null)),
                P("@SampleSource", isFinished ? "After Packaging" : (isStability ? GetComboText(CmbPullPoint) : null)),
                P("@SampledFrom", (isProduction || isFinished) ? TxtSampledFrom.Text : null),
                P("@MachineLineNo", (isProduction || isFinished) ? TxtMachineLine.Text : null),
                PDate("@ManufacturingDate", (isProduction || isFinished) ? DpManufacturingDate.SelectedDate : null),
                PDate("@PackagingDate", isFinished ? DpPackagingDate.SelectedDate : null),
                PDate("@ExpiryDate", isFinished ? DpExpiryDate.SelectedDate : null),
                P("@PackSize", isFinished ? TxtPackSize.Text : null),

                P("@SpecificationNo", TxtSpecificationNo.Text),
                new SqlParameter("@SpecificationVersionNo", SqlDbType.Int) { Value = specificationVersion },
                P("@TestsRequired", TxtTestsRequired.Text),
                PDecimal("@SampleQuantity", sampleQty),
                P("@Unit", GetComboText(CmbUnit)),
                P("@StorageCondition", isRaw ? TxtRawStorage.Text : (isStability ? GetComboText(CmbStabilityCondition) : null)),
                P("@StabilityChamberNo", isStability ? TxtChamberNo.Text : null),
                P("@StabilityProtocolNo", isStability ? TxtProtocolNo.Text : null),
                PDateTime("@SampleDateTime", sampleDateTime),
                P("@SampledBy", TxtSampledBy.Text),
                P("@Department", TxtDepartment != null ? TxtDepartment.Text : category),
                P("@Remarks", TxtRemarks.Text),
                P("@CreatedBy", GetCurrentUserDisplayName())
            };
        }

        private DateTime GetRequiredSampleDateTime()
        {
            if (!DpSampleDate.SelectedDate.HasValue)
                throw new InvalidOperationException("Sample Date is required.");

            string timeText = (TxtSampleTime.Text ?? string.Empty).Trim();
            string[] acceptedFormats = { "H:mm", "HH:mm" };
            if (!DateTime.TryParseExact(
                    timeText, acceptedFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime parsedTime))
            {
                throw new InvalidOperationException(
                    "Sample Time is required in 24-hour HH:mm format, for example 08:15 or 18:30.");
            }

            return DpSampleDate.SelectedDate.Value.Date.Add(parsedTime.TimeOfDay);
        }

        private static int EnsureApprovedSpecificationInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            string specificationNo,
            string category,
            string itemCode,
            string productionStage)
        {
            if (string.IsNullOrWhiteSpace(specificationNo) || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(itemCode))
                throw new InvalidOperationException("An approved Specification No., Sample Category, and item code are required.");
            if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(productionStage))
                throw new InvalidOperationException("An exact Production Stage is required for an In-Process specification.");

            using SqlCommand command = new SqlCommand(@"
SELECT ISNULL(MAX(configured.VersionNo),0)
FROM dbo.PRM_SpecificationTests configured WITH (UPDLOCK, HOLDLOCK)
WHERE LTRIM(RTRIM(configured.SpecificationNo)) = @SpecificationNo
  AND LTRIM(RTRIM(configured.SampleCategory)) = @Category
  AND UPPER(LTRIM(RTRIM(ISNULL(configured.ItemCode,N'')))) IN (UPPER(LTRIM(RTRIM(@ItemCode))),N'*')
  AND
  (
      @Category <> N'Production / In-Process'
      OR UPPER(LTRIM(RTRIM(ISNULL(configured.ProductionStage,N'')))) IN (UPPER(LTRIM(RTRIM(@ProductionStage))),N'*')
  )
  AND configured.ApprovalStatus = N'Approved'
  AND configured.IsActive = 1
  AND NOT (
      ISNULL(configured.CreatedBy,N'')=N'Controlled PRM Standard Profile Readiness 20260913'
      AND (
          ISNULL(configured.ReviewedBy,N'')=N'Controlled PRM Profile Review 20260913'
          OR ISNULL(configured.ApprovedBy,N'')=N'Controlled PRM Profile Approval 20260913'
      )
  )
" + PrmSpecificationRepository.ApprovedProfileEvidencePredicateSql + @"
  AND (configured.EffectiveDate IS NULL OR configured.EffectiveDate <= CAST(SYSDATETIME() AS date));", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SpecificationNo", SqlDbType.NVarChar, 120).Value = specificationNo.Trim();
            command.Parameters.Add("@Category", SqlDbType.NVarChar, 40).Value = category.Trim();
            command.Parameters.Add("@ItemCode", SqlDbType.NVarChar, 80).Value = itemCode.Trim();
            command.Parameters.Add("@ProductionStage", SqlDbType.NVarChar, 80).Value = (productionStage ?? string.Empty).Trim();
            command.Parameters.Add("@RequireIndependentApprover", SqlDbType.Bit).Value = AppConfig.IsProduction;

            int version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version <= 0)
            {
                throw new InvalidOperationException(
                    "The selected PRM specification is no longer approved, active, effective, or applicable to this item/stage. Reload the approved specification before saving.");
            }

            return version;
        }

        private static string ReadPrmRegistrationSnapshotInTransaction(
            SqlConnection connection,
            SqlTransaction transaction,
            int sampleId)
        {
            using SqlCommand command = new SqlCommand(@"
SELECT CONCAT(
    N'SampleCategory=', ISNULL(SampleCategory,N''),
    N'; SamplePurpose=', ISNULL(SamplePurpose,N''),
    N'; MaterialCode=', ISNULL(MaterialCode,N''),
    N'; MaterialName=', ISNULL(MaterialName,N''),
    N'; MaterialType=', ISNULL(MaterialType,N''),
    N'; Manufacturer=', ISNULL(Manufacturer,N''),
    N'; Supplier=', ISNULL(Supplier,N''),
    N'; ManufacturerLotNo=', ISNULL(ManufacturerLotNo,N''),
    N'; SupplierLotNo=', ISNULL(SupplierLotNo,N''),
    N'; GRNNo=', ISNULL(GRNNo,N''),
    N'; ReceivedDate=', ISNULL(CONVERT(NVARCHAR(10),ReceivedDate,23),N''),
    N'; ManufacturerExpiryDate=', ISNULL(CONVERT(NVARCHAR(10),ManufacturerExpiryDate,23),N''),
    N'; RetestDate=', ISNULL(CONVERT(NVARCHAR(10),RetestDate,23),N''),
    N'; RetestReason=', ISNULL(RetestReason,N''),
    N'; PreviousReportNo=', ISNULL(PreviousReportNo,N''),
    N'; PreviousRetestDate=', ISNULL(CONVERT(NVARCHAR(10),PreviousRetestDate,23),N''),
    N'; ProposedRetestDate=', ISNULL(CONVERT(NVARCHAR(10),ProposedRetestDate,23),N''),
    N'; RetestRemarks=', ISNULL(RetestRemarks,N''),
    N'; ProductCode=', ISNULL(ProductCode,N''),
    N'; ProductName=', ISNULL(ProductName,N''),
    N'; BatchNo=', ISNULL(BatchNo,N''),
    N'; BatchSize=', ISNULL(BatchSize,N''),
    N'; DosageForm=', ISNULL(DosageForm,N''),
    N'; ProductionStage=', ISNULL(ProductionStage,N''),
    N'; SampleSource=', ISNULL(SampleSource,N''),
    N'; SampledFrom=', ISNULL(SampledFrom,N''),
    N'; MachineLineNo=', ISNULL(MachineLineNo,N''),
    N'; ManufacturingDate=', ISNULL(CONVERT(NVARCHAR(10),ManufacturingDate,23),N''),
    N'; PackagingDate=', ISNULL(CONVERT(NVARCHAR(10),PackagingDate,23),N''),
    N'; ExpiryDate=', ISNULL(CONVERT(NVARCHAR(10),ExpiryDate,23),N''),
    N'; PackSize=', ISNULL(PackSize,N''),
    N'; SpecificationNo=', ISNULL(SpecificationNo,N''),
    N'; SpecificationVersionNo=', ISNULL(CONVERT(NVARCHAR(20),SpecificationVersionNo),N''),
    N'; TestsRequired=', ISNULL(TestsRequired,N''),
    N'; SampleQuantity=', ISNULL(CONVERT(NVARCHAR(50),SampleQuantity),N''),
    N'; Unit=', ISNULL(Unit,N''),
    N'; StorageCondition=', ISNULL(StorageCondition,N''),
    N'; StabilityChamberNo=', ISNULL(StabilityChamberNo,N''),
    N'; StabilityProtocolNo=', ISNULL(StabilityProtocolNo,N''),
    N'; SampleDateTime=', ISNULL(CONVERT(NVARCHAR(19),SampleDateTime,120),N''),
    N'; SampledBy=', ISNULL(SampledBy,N''),
    N'; Department=', ISNULL(Department,N''),
    N'; Remarks=', ISNULL(Remarks,N''),
    N'; SampleStatus=', ISNULL(SampleStatus,N'')
)
FROM dbo.PRM_Samples
WHERE SampleID=@SampleID;", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SampleID", SqlDbType.Int).Value = sampleId;
            return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static SqlParameter P(string name, string value)
        {
            return new SqlParameter(name, SqlDbType.NVarChar) { Value = string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value.Trim() };
        }

        private static SqlParameter PDate(string name, DateTime? value)
        {
            return new SqlParameter(name, SqlDbType.Date) { Value = value.HasValue ? (object)value.Value.Date : DBNull.Value };
        }

        private static SqlParameter PDateTime(string name, DateTime? value)
        {
            return new SqlParameter(name, SqlDbType.DateTime2) { Value = value.HasValue ? (object)value.Value : DBNull.Value };
        }

        private static SqlParameter PDecimal(string name, decimal? value)
        {
            return new SqlParameter(name, SqlDbType.Decimal) { Precision = 18, Scale = 3, Value = value.HasValue ? (object)value.Value : DBNull.Value };
        }

        private static decimal? TryParseDecimal(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value))
                return value;

            if (decimal.TryParse(text.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out value))
                return value;

            throw new InvalidOperationException("Sample Quantity must be numeric.");
        }

        private string GetNextSampleNumber(
            SqlConnection connection,
            SqlTransaction transaction,
            string category)
        {
            string sequenceName;

            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
                sequenceName = "RM_SAMPLE";
            else if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase))
                sequenceName = "IP_SAMPLE";
            else if (category.Equals("Finished Product", StringComparison.OrdinalIgnoreCase))
                sequenceName = "FP_SAMPLE";
            else if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
                sequenceName = "ST_SAMPLE";
            else
                throw new InvalidOperationException("Unknown sample category.");

            using SqlCommand command = new SqlCommand(@"
DECLARE @year INT = YEAR(SYSDATETIME());
DECLARE @prefix NVARCHAR(20);
DECLARE @last INT;

SELECT @prefix = Prefix, @last = LastNumber
FROM dbo.PRM_NumberSequences WITH (UPDLOCK, HOLDLOCK)
WHERE SequenceName = @SequenceName AND CurrentYear = @year;

IF @prefix IS NULL
BEGIN
    SELECT TOP 1 @prefix = Prefix
    FROM dbo.PRM_NumberSequences WITH (UPDLOCK, HOLDLOCK)
    WHERE SequenceName = @SequenceName;

    IF @prefix IS NULL SET @prefix = LEFT(@SequenceName, 2);

    IF EXISTS (SELECT 1 FROM dbo.PRM_NumberSequences WITH (UPDLOCK, HOLDLOCK) WHERE SequenceName = @SequenceName)
    BEGIN
        UPDATE dbo.PRM_NumberSequences
        SET CurrentYear = @year, LastNumber = 0, LastUpdated = SYSDATETIME()
        WHERE SequenceName = @SequenceName;
    END
    ELSE
    BEGIN
        INSERT INTO dbo.PRM_NumberSequences (SequenceName, Prefix, CurrentYear, LastNumber)
        VALUES (@SequenceName, @prefix, @year, 0);
    END;

    SET @last = 0;
END;

SET @last = @last + 1;

UPDATE dbo.PRM_NumberSequences
SET LastNumber = @last, LastUpdated = SYSDATETIME()
WHERE SequenceName = @SequenceName AND CurrentYear = @year;

SELECT @prefix + N'-' + CAST(@year AS NVARCHAR(4)) + N'-' + RIGHT(N'0000' + CAST(@last AS NVARCHAR(10)), 4);", connection, transaction);
            command.CommandTimeout = AppConfig.CommandTimeoutSeconds;
            command.Parameters.Add("@SequenceName", SqlDbType.NVarChar, 60).Value = sequenceName;

            string generated = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(generated))
                throw new InvalidOperationException("Failed to generate a controlled PRM sample number.");

            return generated.Trim();
        }

        private async void DgSamples_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || DgSamples.SelectedItem == null)
                return;

            if (DgSamples.SelectedItem is DataRowView row && row.Row.Table.Columns.Contains("SampleID"))
            {
                int sampleId = Convert.ToInt32(row["SampleID"], CultureInfo.InvariantCulture);
                try
                {
                    IsEnabled = false;
                    DataTable table = await Task.Run(() => QuerySampleForEdit(sampleId));
                    LoadSampleForEdit(sampleId, table);
                }
                catch (Exception ex)
                {
                    ApplicationLogger.Error("PRM sample selection loading failed.", ex);
                    MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex),
                        "Load PRM Sample", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                finally
                {
                    IsEnabled = true;
                }
            }
        }

        private static DataTable QuerySampleForEdit(int sampleId)
        {
            return DatabaseHelper.ExecuteQuery(
                "SELECT * FROM dbo.PRM_Samples WHERE SampleID = @SampleID",
                new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = sampleId } });
        }

        private void LoadSampleForEdit(int sampleId, DataTable table)
        {
            if (table.Rows.Count == 0)
                return;

            DataRow r = table.Rows[0];
            _selectedSampleId = sampleId;
            _isLoading = true;

            try
            {
                TxtSampleNo.Text = S(r, "SampleNumber");
                _selectedCategory = S(r, "SampleCategory");
                ApplyCategoryLayout();

                SetComboText(CmbRawPurpose, S(r, "SamplePurpose"));
                SetComboText(CmbMaterialType, S(r, "MaterialType"));
                TxtMaterialCode.Text = S(r, "MaterialCode");
                TxtMaterialName.Text = S(r, "MaterialName");
                TxtManufacturer.Text = S(r, "Manufacturer");
                TxtSupplier.Text = S(r, "Supplier");
                TxtManufacturerLot.Text = S(r, "ManufacturerLotNo");
                TxtSupplierLot.Text = S(r, "SupplierLotNo");
                TxtGrn.Text = S(r, "GRNNo");
                DpReceivedDate.SelectedDate = D(r, "ReceivedDate");
                DpManufacturerExpiry.SelectedDate = D(r, "ManufacturerExpiryDate");
                DpRetestDate.SelectedDate = D(r, "RetestDate");
                TxtRawStorage.Text = S(r, "StorageCondition");
                TxtRetestReason.Text = S(r, "RetestReason");
                TxtPreviousReportNo.Text = S(r, "PreviousReportNo");
                DpPreviousRetestDate.SelectedDate = D(r, "PreviousRetestDate");
                DpProposedRetestDate.SelectedDate = D(r, "ProposedRetestDate");
                TxtRetestRemarks.Text = S(r, "RetestRemarks");

                TxtProductCode.Text = S(r, "ProductCode");
                TxtProductName.Text = S(r, "ProductName");
                TxtBatchNo.Text = S(r, "BatchNo");
                TxtBatchSize.Text = S(r, "BatchSize");
                SetComboText(CmbDosageForm, S(r, "DosageForm"));
                SetComboText(CmbProductionStage, S(r, "ProductionStage"));

                TxtSampledFrom.Text = S(r, "SampledFrom");
                TxtMachineLine.Text = S(r, "MachineLineNo");
                DpManufacturingDate.SelectedDate = D(r, "ManufacturingDate");
                DpPackagingDate.SelectedDate = D(r, "PackagingDate");
                DpExpiryDate.SelectedDate = D(r, "ExpiryDate");
                TxtPackSize.Text = S(r, "PackSize");
                if (TxtStbProductCode != null) TxtStbProductCode.Text = S(r, "ProductCode");
                if (TxtStbProductName != null) TxtStbProductName.Text = S(r, "ProductName");
                if (TxtStbBatchNo != null) TxtStbBatchNo.Text = S(r, "BatchNo");
                if (CmbStabilityStudyType != null) SetComboText(CmbStabilityStudyType, S(r, "ProductionStage"));
                if (CmbPullPoint != null) SetComboText(CmbPullPoint, S(r, "SampleSource"));
                if (CmbStabilityCondition != null) SetComboText(CmbStabilityCondition, S(r, "StorageCondition"));
                if (TxtChamberNo != null) TxtChamberNo.Text = S(r, "StabilityChamberNo");
                if (TxtProtocolNo != null) TxtProtocolNo.Text = S(r, "StabilityProtocolNo");

                string frozenSpecificationNo = S(r, "SpecificationNo");
                TxtSpecificationNo.Items.Clear();
                if (!string.IsNullOrWhiteSpace(frozenSpecificationNo))
                {
                    TxtSpecificationNo.Items.Add(frozenSpecificationNo);
                    TxtSpecificationNo.SelectedIndex = 0;
                }
                TxtTestsRequired.Text = S(r, "TestsRequired");
                TxtSampleQty.Text = S(r, "SampleQuantity");
                SetComboText(CmbUnit, S(r, "Unit"));
                DateTime? storedSampleDateTime = D(r, "SampleDateTime");
                DpSampleDate.SelectedDate = storedSampleDateTime?.Date;
                TxtSampleTime.Text = storedSampleDateTime.HasValue
                    ? storedSampleDateTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : string.Empty;
                TxtSampledBy.Text = S(r, "SampledBy");
                TxtRemarks.Text = S(r, "Remarks");

                ApplyCategoryLayout();
                ApplyRetestLayout();

                TxtStatus.Text = "Loaded sample: " + TxtSampleNo.Text + ". Only Registered samples can be edited here.";
            }
            finally
            {
                _isLoading = false;
            }
        }

        private static string S(DataRow row, string column)
        {
            if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return string.Empty;

            return Convert.ToString(row[column], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static DateTime? D(DataRow row, string column)
        {
            if (!row.Table.Columns.Contains(column) || row[column] == DBNull.Value)
                return null;

            return Convert.ToDateTime(row[column], CultureInfo.InvariantCulture);
        }

        private void BtnOpenResults_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedSampleId <= 0)
                    throw new InvalidOperationException("Select a saved sample first, then open Results Entry.");

                var resultsWindow = new ProductionRawMaterialResults(_selectedSampleId);
                resultsWindow.Owner = this;
                resultsWindow.Closed += (_, _) => LoadSamples();
                resultsWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Error opening Production & Raw Material Results Entry:\n" + Infrastructure.UserFacingError.SafeMessage(ex),
                    "Results Entry Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnPreviewReport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_selectedSampleId <= 0)
                    throw new InvalidOperationException("Select a saved sample first.");

                DataTable table = DatabaseHelper.ExecuteQuery(
                    "SELECT * FROM dbo.PRM_Samples WHERE SampleID = @SampleID",
                    new[] { new SqlParameter("@SampleID", SqlDbType.Int) { Value = _selectedSampleId } });

                if (table.Rows.Count == 0)
                    throw new InvalidOperationException("Sample record was not found.");

                string html = BuildReportHtml(table.Rows[0]);
                Infrastructure.ControlledTempFiles.WriteHtmlAndOpen("PharmaLIMS_PRM_Report", html);
                TxtStatus.Text = "Preview report opened for " + S(table.Rows[0], "SampleNumber") + ".";
            }
            catch (Exception ex)
            {
                ShowOperationError("Preview Report", ex);
            }
        }

        private string BuildReportHtml(DataRow r)
        {
            string category = S(r, "SampleCategory");
            string reportTitle = GetReportTitle(r);
            string conclusion = "Conclusion will be generated after results entry, technical review, QA review, and report approval.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>" + WebUtility.HtmlEncode(reportTitle) + "</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:34px;color:#111827} .header{border-bottom:3px solid #0f172a;padding-bottom:12px;margin-bottom:22px}.title{font-size:22px;font-weight:700}.sub{font-size:12px;color:#475569;margin-top:5px} table{border-collapse:collapse;width:100%;margin-top:12px}td,th{border:1px solid #cbd5e1;padding:8px;font-size:12px;vertical-align:top}th{background:#f1f5f9;text-align:left}.section{margin-top:20px;font-size:15px;font-weight:700;color:#0f172a}.note{background:#fff7ed;border:1px solid #fdba74;padding:10px;margin-top:16px;font-size:12px}.sig td{height:58px}</style>");
            sb.AppendLine("</head><body>");
            sb.AppendLine("<div class='header'><div class='title'>" + WebUtility.HtmlEncode(reportTitle) + "</div>");
            sb.AppendLine("<div class='sub'>PharmaLIMS - Laboratory Analytical Report Preview</div></div>");

            sb.AppendLine("<div class='section'>Sample Information</div><table>");
            AddRow(sb, "Sample No.", S(r, "SampleNumber"), "Sample Category", category);

            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
            {
                AddRow(sb, "Sample Purpose", S(r, "SamplePurpose"), "Material Type", S(r, "MaterialType"));
                AddRow(sb, "Material Code", S(r, "MaterialCode"), "Material Name", S(r, "MaterialName"));
                AddRow(sb, "Manufacturer", S(r, "Manufacturer"), "Supplier", S(r, "Supplier"));
                AddRow(sb, "Manufacturer Lot No.", S(r, "ManufacturerLotNo"), "Supplier Lot No.", S(r, "SupplierLotNo"));
                AddRow(sb, "GRN No.", S(r, "GRNNo"), "Received Date", FormatDate(r, "ReceivedDate"));
                AddRow(sb, "Manufacturer Expiry Date", FormatDate(r, "ManufacturerExpiryDate"), "Retest Date", FormatDate(r, "RetestDate"));
                AddRow(sb, "Storage Condition", S(r, "StorageCondition"), "Previous Report No.", S(r, "PreviousReportNo"));
            }
            else if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
            {
                AddRow(sb, "Product Code", S(r, "ProductCode"), "Product Name", S(r, "ProductName"));
                AddRow(sb, "Batch No.", S(r, "BatchNo"), "Study Type", S(r, "ProductionStage"));
                AddRow(sb, "Pull Point", S(r, "SampleSource"), "Storage Condition", S(r, "StorageCondition"));
                AddRow(sb, "Chamber No.", S(r, "StabilityChamberNo"), "Protocol No.", S(r, "StabilityProtocolNo"));
            }
            else
            {
                AddRow(sb, "Product Code", S(r, "ProductCode"), "Product Name", S(r, "ProductName"));
                AddRow(sb, "Batch No.", S(r, "BatchNo"), "Batch Size", S(r, "BatchSize"));
                AddRow(sb, "Dosage Form", S(r, "DosageForm"), "Stage / Source", string.IsNullOrWhiteSpace(S(r, "ProductionStage")) ? S(r, "SampleSource") : S(r, "ProductionStage"));
                AddRow(sb, "Manufacturing Date", FormatDate(r, "ManufacturingDate"), "Packaging Date", FormatDate(r, "PackagingDate"));
                AddRow(sb, "Expiry Date", FormatDate(r, "ExpiryDate"), "Pack Size", S(r, "PackSize"));
                AddRow(sb, "Sampled From", S(r, "SampledFrom"), "Machine / Line No.", S(r, "MachineLineNo"));
            }

            AddRow(sb, "Sample Date", FormatDateTime(r, "SampleDateTime"), "Sampled By", S(r, "SampledBy"));
            AddRow(sb, "Specification No. / Version",
                S(r, "SpecificationNo") + (string.IsNullOrWhiteSpace(S(r, "SpecificationVersionNo")) ? string.Empty : " / v" + S(r, "SpecificationVersionNo")),
                "Tests Required", S(r, "TestsRequired"));
            sb.AppendLine("</table>");

            sb.AppendLine("<div class='section'>Laboratory Conclusion</div>");
            sb.AppendLine("<div class='note'>" + WebUtility.HtmlEncode(conclusion) + "<br/>This report preview does not represent batch/material/product release or rejection.</div>");

            sb.AppendLine("<div class='section'>Signatures</div><table class='sig'><tr><th>Analyst</th><th>Technical Reviewer</th><th>QA Reviewer</th><th>Approved By</th></tr><tr><td></td><td></td><td></td><td></td></tr></table>");
            sb.AppendLine("<div class='sub' style='margin-top:20px'>Printed: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "</div>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void AddRow(StringBuilder sb, string label1, string value1, string label2, string value2)
        {
            sb.AppendLine("<tr><th>" + WebUtility.HtmlEncode(label1) + "</th><td>" + WebUtility.HtmlEncode(value1) + "</td><th>" + WebUtility.HtmlEncode(label2) + "</th><td>" + WebUtility.HtmlEncode(value2) + "</td></tr>");
        }

        private string GetReportTitle(DataRow r)
        {
            string category = S(r, "SampleCategory");

            if (category.Equals("Raw Material", StringComparison.OrdinalIgnoreCase))
            {
                string purpose = S(r, "SamplePurpose");
                if (purpose.Equals("Retest", StringComparison.OrdinalIgnoreCase))
                    return "Raw Material Retest Report";
                return "Raw Material Analytical Report";
            }

            if (category.Equals("Production / In-Process", StringComparison.OrdinalIgnoreCase))
            {
                string stage = S(r, "ProductionStage");
                if (stage.Equals("After Mixing", StringComparison.OrdinalIgnoreCase))
                    return "Mixing Stage Analytical Report";
                if (stage.Equals("After Compression", StringComparison.OrdinalIgnoreCase))
                    return "Compression Stage Analytical Report";
                if (stage.Equals("After Coating", StringComparison.OrdinalIgnoreCase))
                    return "Coating Stage Analytical Report";
                return "In-Process Analytical Report";
            }

            if (category.Equals("Stability", StringComparison.OrdinalIgnoreCase))
                return "Stability Microbiological Test Report";

            return "Finished Product Analytical Report";
        }

        private static string FormatDate(DataRow r, string column)
        {
            DateTime? d = D(r, column);
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static string FormatDateTime(DataRow r, string column)
        {
            DateTime? d = D(r, column);
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : string.Empty;
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
