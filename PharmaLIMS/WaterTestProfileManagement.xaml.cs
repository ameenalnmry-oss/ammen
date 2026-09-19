using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PharmaLIMS
{
    public sealed class WaterProfileTestEditorRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _lowerLimitText = string.Empty;
        private string _upperLimitText = string.Empty;
        private string _alertLimitText = string.Empty;
        private string _actionLimitText = string.Empty;
        private string _specificationText = string.Empty;

        public int TestID { get; init; }
        public string TestCode { get; init; } = string.Empty;
        public string TestName { get; init; } = string.Empty;
        public string TestCategory { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public bool IsActiveMaster { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public string LowerLimitText
        {
            get => _lowerLimitText;
            set { if (_lowerLimitText == value) return; _lowerLimitText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string UpperLimitText
        {
            get => _upperLimitText;
            set { if (_upperLimitText == value) return; _upperLimitText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string AlertLimitText
        {
            get => _alertLimitText;
            set { if (_alertLimitText == value) return; _alertLimitText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string ActionLimitText
        {
            get => _actionLimitText;
            set { if (_actionLimitText == value) return; _actionLimitText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public string SpecificationText
        {
            get => _specificationText;
            set { if (_specificationText == value) return; _specificationText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class WaterTestProfileManagement : Window
    {
        private readonly ObservableCollection<WaterProfileTestEditorRow> _tests = new();
        private const string FullDevInventoryPlaceholderPrefix = "DEVELOPMENT FULL TEST INVENTORY PLACEHOLDER";
        private const string MqcWaterSopReference = "MQC-G-0018 v2.0";
        private const string CtgWaterGuidelineReference = "CTG-11-02 v02";
        private readonly string _requestedProfileCode;
        private bool _loading;
        private int _profileId;
        private int _versionNo;
        private string _approvalStatus = string.Empty;
        private string _createdBy = string.Empty;
        private string _reviewedBy = string.Empty;

        public bool WasChanged { get; private set; }

        public WaterTestProfileManagement(string? profileCode = null)
        {
            InitializeComponent();
            _requestedProfileCode = NormalizeProfileCode(profileCode);
            dgTests.ItemsSource = _tests;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureProfileAccess();
                EnsureSchema();

                string initial = string.IsNullOrWhiteSpace(_requestedProfileCode) ? "PW" : _requestedProfileCode;
                SelectProfileCode(initial);
                LoadLatestProfile();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to open Controlled Water Test Profiles.", ex);
                MessageBox.Show(
                    "Unable to open Controlled Water Test Profiles: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Close();
            }
        }

        private static string NormalizeProfileCode(string? value)
        {
            string code = value?.Trim().ToUpperInvariant() ?? string.Empty;
            if (code.Contains("PURIFIED", StringComparison.Ordinal) || code == "PWS")
                return "PW";
            if (code.Contains("POTABLE", StringComparison.Ordinal) || code.Contains("DRINKING", StringComparison.Ordinal) || code == "PTWS")
                return "PTW";
            return code is "PW" or "PTW" ? code : string.Empty;
        }

        private string SelectedProfileCode()
        {
            if (cboProfileCode.SelectedItem is ComboBoxItem item)
                return NormalizeProfileCode(item.Tag?.ToString() ?? item.Content?.ToString());
            return NormalizeProfileCode(cboProfileCode.Text);
        }

        private void SelectProfileCode(string code)
        {
            foreach (object item in cboProfileCode.Items)
            {
                if (item is ComboBoxItem combo &&
                    string.Equals(NormalizeProfileCode(combo.Tag?.ToString()), code, StringComparison.OrdinalIgnoreCase))
                {
                    cboProfileCode.SelectedItem = combo;
                    return;
                }
            }

            if (cboProfileCode.Items.Count > 0)
                cboProfileCode.SelectedIndex = 0;
        }

        private static void EnsureProfileAccess()
        {
            string username = CurrentUsername();
            if (!DatabaseHelper.CanManageSettings(username) &&
                !DatabaseHelper.CanReviewResults(username) &&
                !DatabaseHelper.CanApproveResults(username))
            {
                throw new UnauthorizedAccessException(
                    "An authorized Water-profile author, reviewer, approver, or Development administrator is required.");
            }
        }

        private static void EnsureAuthorPermission()
        {
            string username = CurrentUsername();
            if (!DatabaseHelper.CanManageSettings(username))
            {
                throw new UnauthorizedAccessException(
                    "Settings permission is required to create or edit controlled Water Test Profile drafts.");
            }
        }

        private static void EnsureReviewerPermission()
        {
            string username = CurrentUsername();
            if (!DatabaseHelper.CanReviewResults(username))
            {
                throw new UnauthorizedAccessException(
                    "Review permission is required to review controlled Water Test Profiles.");
            }
        }

        private static void EnsureApproverPermission()
        {
            string username = CurrentUsername();
            if (!DatabaseHelper.CanApproveResults(username))
            {
                throw new UnauthorizedAccessException(
                    "Approval permission is required to approve controlled Water Test Profiles.");
            }
        }

        private static bool IsDevelopmentAdminWorkflowOverrideAllowed()
        {
            if (!AppConfig.DevelopmentAdminFullPermissions)
                return false;

            string role = Login.CurrentUserRole?.Trim() ?? string.Empty;
            return role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                   role.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDevelopmentAdminWorkflowOverrideAllowed(string? role)
        {
            if (!AppConfig.DevelopmentAdminFullPermissions)
                return false;

            string normalized = role?.Trim() ?? string.Empty;
            return normalized.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Equals("Administrator", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureSchema()
        {
            int ready = Convert.ToInt32(DatabaseHelper.ExecuteScalar(@"
SELECT CASE WHEN
    OBJECT_ID(N'dbo.WaterTestProfiles',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.WaterTestProfileTests',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.WaterSpecifications',N'U') IS NOT NULL
    AND OBJECT_ID(N'dbo.WaterTestProfileSignatures',N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.WaterTestProfiles',N'ControlledReference') IS NOT NULL
    AND COL_LENGTH(N'dbo.WaterSpecifications',N'ProfileID') IS NOT NULL
THEN 1 ELSE 0 END;") ?? 0, CultureInfo.InvariantCulture);

            if (ready != 1)
                throw new InvalidOperationException(
                    "The controlled Water-profile schema is not installed. Run Database Maintenance before configuring PW/PTW master data.");
        }

        private void CboProfileCode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || !IsLoaded)
                return;

            try
            {
                LoadLatestProfile();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to change Water Test Profile selection.", ex);
                MessageBox.Show(
                    "Unable to load the selected Water Test Profile: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void LoadLatestProfile()
        {
            _loading = true;
            try
            {
                string code = SelectedProfileCode();
                if (code is not ("PW" or "PTW"))
                    throw new InvalidOperationException("Select PW or PTW.");

                DataTable profile = DatabaseHelper.ExecuteQuery(@"
SELECT TOP (1)
    ProfileID,ProfileCode,ProfileName,VersionNo,ApprovalStatus,
    EffectiveFrom,EffectiveTo,IsActive,ReviewedBy,ReviewedAt,ApprovedBy,ApprovedAt,
    CreatedBy,CreatedAt,ControlledReference
FROM dbo.WaterTestProfiles
WHERE UPPER(LTRIM(RTRIM(ProfileCode)))=@Code
ORDER BY VersionNo DESC,ProfileID DESC;",
                    new[]
                    {
                        new SqlParameter("@Code", SqlDbType.NVarChar, 20) { Value = code }
                    });

                if (profile.Rows.Count == 0)
                {
                    _profileId = 0;
                    _versionNo = 0;
                    _approvalStatus = string.Empty;
                    _createdBy = string.Empty;
                    _reviewedBy = string.Empty;
                    txtProfileName.Text = code == "PW" ? "Purified Water" : "Potable Water";
                    txtVersionState.Text = "No controlled version";
                    txtControlledReference.Text = string.Empty;
                    dpEffectiveFrom.SelectedDate = null;
                    dpEffectiveTo.SelectedDate = null;
                    LoadTestsForProfile(0);
                    lblHeaderStatus.Text = code + " | Not configured";
                    lblStatus.Text = "No controlled " + code + " profile exists. Create a Draft; no tests or limits will be seeded automatically.";
                }
                else
                {
                    DataRow row = profile.Rows[0];
                    _profileId = Convert.ToInt32(row["ProfileID"], CultureInfo.InvariantCulture);
                    _versionNo = Convert.ToInt32(row["VersionNo"], CultureInfo.InvariantCulture);
                    _approvalStatus = Convert.ToString(row["ApprovalStatus"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    _createdBy = Convert.ToString(row["CreatedBy"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    _reviewedBy = Convert.ToString(row["ReviewedBy"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

                    txtProfileName.Text = Convert.ToString(row["ProfileName"], CultureInfo.InvariantCulture) ?? string.Empty;
                    txtVersionState.Text = $"v{_versionNo} | {_approvalStatus} | Active={Convert.ToBoolean(row["IsActive"], CultureInfo.InvariantCulture)}";
                    txtControlledReference.Text = Convert.ToString(row["ControlledReference"], CultureInfo.InvariantCulture) ?? string.Empty;
                    dpEffectiveFrom.SelectedDate = row["EffectiveFrom"] == DBNull.Value ? null : Convert.ToDateTime(row["EffectiveFrom"], CultureInfo.InvariantCulture);
                    dpEffectiveTo.SelectedDate = row["EffectiveTo"] == DBNull.Value ? null : Convert.ToDateTime(row["EffectiveTo"], CultureInfo.InvariantCulture);
                    LoadTestsForProfile(_profileId);

                    lblHeaderStatus.Text = $"{code} v{_versionNo} | {_approvalStatus}";
                    lblStatus.Text = BuildProfileSummary(row);
                }

                ApplyEditingState();
                UpdateSelectedCount();
            }
            finally
            {
                _loading = false;
            }
        }

        private static string BuildProfileSummary(DataRow row)
        {
            string created = Convert.ToString(row["CreatedBy"], CultureInfo.InvariantCulture) ?? string.Empty;
            string reviewed = Convert.ToString(row["ReviewedBy"], CultureInfo.InvariantCulture) ?? string.Empty;
            string approved = Convert.ToString(row["ApprovedBy"], CultureInfo.InvariantCulture) ?? string.Empty;
            return $"Created by: {created}; Reviewed by: {(string.IsNullOrWhiteSpace(reviewed) ? "Not reviewed" : reviewed)}; " +
                   $"Approved by: {(string.IsNullOrWhiteSpace(approved) ? "Not approved" : approved)}.";
        }

        private void LoadTestsForProfile(int profileId)
        {
            foreach (WaterProfileTestEditorRow existing in _tests)
                existing.PropertyChanged -= TestRow_PropertyChanged;
            _tests.Clear();

            DataTable table = DatabaseHelper.ExecuteQuery(@"
SELECT
    t.TestID,
    ISNULL(t.TestCode,N'') AS TestCode,
    t.TestName,
    ISNULL(t.TestCategory,N'') AS TestCategory,
    ISNULL(t.Unit,N'') AS Unit,
    ISNULL(t.IsActive,0) AS TestIsActive,
    CASE WHEN profileTest.ProfileTestID IS NULL OR ISNULL(profileTest.IsActive,1)=0 THEN 0 ELSE 1 END AS IsSelected,
    spec.LowerLimit,
    spec.UpperLimit,
    spec.AlertLimit,
    spec.ActionLimit,
    ISNULL(spec.SpecificationText,N'') AS SpecificationText
FROM dbo.Tests t
LEFT JOIN dbo.WaterTestProfileTests profileTest
  ON profileTest.ProfileID=@ProfileID
 AND profileTest.TestID=t.TestID
OUTER APPLY
(
    SELECT TOP (1)
        s.LowerLimit,s.UpperLimit,s.AlertLimit,s.ActionLimit,s.SpecificationText
    FROM dbo.WaterSpecifications s
    WHERE s.ProfileID=@ProfileID
      AND s.TestID=t.TestID
      AND (s.PointCode IS NULL OR LTRIM(RTRIM(s.PointCode))=N'')
    ORDER BY s.SpecificationID DESC
) spec
WHERE ISNULL(t.IsActive,0)=1 OR profileTest.ProfileTestID IS NOT NULL
ORDER BY ISNULL(profileTest.SortOrder,t.SortOrder),t.TestCategory,t.TestName;",
                new[]
                {
                    new SqlParameter("@ProfileID", SqlDbType.Int) { Value = profileId }
                });

            foreach (DataRow row in table.Rows)
            {
                var item = new WaterProfileTestEditorRow
                {
                    TestID = Convert.ToInt32(row["TestID"], CultureInfo.InvariantCulture),
                    TestCode = Convert.ToString(row["TestCode"], CultureInfo.InvariantCulture) ?? string.Empty,
                    TestName = Convert.ToString(row["TestName"], CultureInfo.InvariantCulture) ?? string.Empty,
                    TestCategory = Convert.ToString(row["TestCategory"], CultureInfo.InvariantCulture) ?? string.Empty,
                    Unit = Convert.ToString(row["Unit"], CultureInfo.InvariantCulture) ?? string.Empty,
                    IsActiveMaster = Convert.ToBoolean(row["TestIsActive"], CultureInfo.InvariantCulture),
                    IsSelected = Convert.ToInt32(row["IsSelected"], CultureInfo.InvariantCulture) == 1,
                    LowerLimitText = FormatNullableDecimal(row["LowerLimit"]),
                    UpperLimitText = FormatNullableDecimal(row["UpperLimit"]),
                    AlertLimitText = FormatNullableDecimal(row["AlertLimit"]),
                    ActionLimitText = FormatNullableDecimal(row["ActionLimit"]),
                    SpecificationText = Convert.ToString(row["SpecificationText"], CultureInfo.InvariantCulture) ?? string.Empty
                };
                item.PropertyChanged += TestRow_PropertyChanged;
                _tests.Add(item);
            }
        }

        private static string FormatNullableDecimal(object value)
        {
            if (value == null || value == DBNull.Value)
                return string.Empty;
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("0.######", CultureInfo.InvariantCulture);
        }

        private void TestRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WaterProfileTestEditorRow.IsSelected))
                UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            if (lblSelectedTests != null)
                lblSelectedTests.Text = "Selected: " + _tests.Count(t => t.IsSelected).ToString(CultureInfo.InvariantCulture);
        }

        private void ApplyEditingState()
        {
            bool draft = _profileId > 0 && _approvalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase);
            bool reviewed = _profileId > 0 && _approvalStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase);
            string currentUser = Login.CurrentUser?.Trim() ?? string.Empty;
            bool developmentAdminOverride = IsDevelopmentAdminWorkflowOverrideAllowed();

            bool canAuthor = !string.IsNullOrWhiteSpace(currentUser) &&
                DatabaseHelper.CanManageSettings(currentUser);
            bool canReview = !string.IsNullOrWhiteSpace(currentUser) &&
                DatabaseHelper.CanReviewResults(currentUser);
            bool canApprove = !string.IsNullOrWhiteSpace(currentUser) &&
                DatabaseHelper.CanApproveResults(currentUser);

            bool creatorCanEdit = draft && canAuthor &&
                (developmentAdminOverride ||
                 currentUser.Equals(_createdBy, StringComparison.OrdinalIgnoreCase));

            bool independentReviewer = draft && canReview &&
                (developmentAdminOverride ||
                 !currentUser.Equals(_createdBy, StringComparison.OrdinalIgnoreCase));

            bool independentApprover = reviewed && canApprove &&
                (developmentAdminOverride ||
                 (!currentUser.Equals(_reviewedBy, StringComparison.OrdinalIgnoreCase) &&
                  !currentUser.Equals(_createdBy, StringComparison.OrdinalIgnoreCase)));

            txtProfileName.IsReadOnly = !creatorCanEdit;
            txtControlledReference.IsReadOnly = !creatorCanEdit;
            dpEffectiveFrom.IsEnabled = creatorCanEdit;
            dpEffectiveTo.IsEnabled = creatorCanEdit;
            dgTests.IsReadOnly = !creatorCanEdit;

            BtnSaveDraft.IsEnabled = creatorCanEdit;
            BtnReview.IsEnabled = independentReviewer;
            BtnApprove.IsEnabled = independentApprover;
            BtnNewDraft.IsEnabled = !draft && canAuthor;

            bool showDevelopmentTemplate = AppConfig.IsDevelopment &&
                developmentAdminOverride;
            BtnLoadDevTemplate.Visibility = showDevelopmentTemplate
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnLoadDevTemplate.IsEnabled = showDevelopmentTemplate && creatorCanEdit;
            BtnLoadFullDevInventory.Visibility = showDevelopmentTemplate
                ? Visibility.Visible
                : Visibility.Collapsed;
            BtnLoadFullDevInventory.IsEnabled = showDevelopmentTemplate && creatorCanEdit;

            lblWorkflowNote.Text = _profileId == 0
                ? canAuthor
                    ? "Create a Draft to configure this profile. Review and Approval use the existing PharmaLIMS workflow permissions."
                    : "This controlled profile is read-only for your current permissions."
                : draft
                    ? creatorCanEdit
                        ? developmentAdminOverride
                            ? "Development Admin override is active. You may author, review, and approve for development testing; all actions remain signed and audited."
                            : "Configure and Save this Draft; Review requires an independent user with the existing Review permission."
                        : independentReviewer
                            ? "Draft is read-only for Review. Review uses the existing PharmaLIMS Review permission and an electronic signature."
                            : "Draft is read-only for your current workflow permissions."
                    : reviewed
                        ? independentApprover
                            ? developmentAdminOverride
                                ? "Development Admin override is active. Approval remains electronically signed and audited."
                                : "Reviewed version is locked. Approval uses the existing PharmaLIMS Approval permission and must be independent of author and reviewer."
                            : "Reviewed version is locked. Approval requires an independent user with the existing Approval permission."
                        : "Approved/obsolete versions are immutable in this screen. Create a new Draft for changes.";
        }

        private void BtnNewDraft_Click(object sender, RoutedEventArgs e)
        {
            EnsureAuthorPermission();
            string code = SelectedProfileCode();

            DataTable draft = DatabaseHelper.ExecuteQuery(@"
SELECT TOP(1) ProfileID
FROM dbo.WaterTestProfiles
WHERE UPPER(LTRIM(RTRIM(ProfileCode)))=@Code
  AND ApprovalStatus=N'Draft'
ORDER BY VersionNo DESC,ProfileID DESC;",
                new[]
                {
                    new SqlParameter("@Code", SqlDbType.NVarChar, 20) { Value = code }
                });

            if (draft.Rows.Count > 0)
            {
                MessageBox.Show(
                    "A Draft already exists for " + code + ". The latest Draft will be loaded instead of creating a duplicate.",
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                LoadLatestProfile();
                return;
            }

            try
            {
                int createdId = 0;
                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection,
                        transaction,
                        CurrentUsername(),
                        "CanManageSettings",
                        "create controlled Water Test Profile drafts");

                    using var create = new SqlCommand(@"
DECLARE @NextVersion INT =
(
    SELECT ISNULL(MAX(VersionNo),0)+1
    FROM dbo.WaterTestProfiles WITH(UPDLOCK,HOLDLOCK)
    WHERE UPPER(LTRIM(RTRIM(ProfileCode)))=@Code
);
DECLARE @SourceProfileID INT =
(
    SELECT TOP(1) ProfileID
    FROM dbo.WaterTestProfiles WITH(HOLDLOCK)
    WHERE UPPER(LTRIM(RTRIM(ProfileCode)))=@Code
      AND ISNULL(IsActive,0)=1
      AND ApprovalStatus=N'Approved'
    ORDER BY VersionNo DESC,ProfileID DESC
);
DECLARE @Inserted TABLE(ProfileID INT NOT NULL);

INSERT dbo.WaterTestProfiles
    (ProfileCode,ProfileName,VersionNo,ApprovalStatus,EffectiveFrom,EffectiveTo,IsActive,
     ReviewedBy,ReviewedAt,ApprovedBy,ApprovedAt,CreatedBy,CreatedAt,ControlledReference)
OUTPUT INSERTED.ProfileID INTO @Inserted(ProfileID)
SELECT
    @Code,
    COALESCE((SELECT ProfileName FROM dbo.WaterTestProfiles WHERE ProfileID=@SourceProfileID),@DefaultName),
    @NextVersion,N'Draft',NULL,NULL,0,NULL,NULL,NULL,NULL,@User,SYSUTCDATETIME(),
    COALESCE((SELECT ControlledReference FROM dbo.WaterTestProfiles WHERE ProfileID=@SourceProfileID),NULL);

SELECT TOP(1) @NewProfileID=ProfileID FROM @Inserted;

IF @SourceProfileID IS NOT NULL
BEGIN
    INSERT dbo.WaterTestProfileTests(ProfileID,TestID,SortOrder,IsActive)
    SELECT @NewProfileID,TestID,SortOrder,IsActive
    FROM dbo.WaterTestProfileTests
    WHERE ProfileID=@SourceProfileID;

    INSERT dbo.WaterSpecifications
        (ProfileCode,ProfileID,TestID,PointCode,LowerLimit,UpperLimit,AlertLimit,ActionLimit,
         SpecificationText,EffectiveFrom,EffectiveTo,ApprovalStatus,IsActive,
         ReviewedBy,ReviewedAt,ApprovedBy,ApprovedAt,CreatedBy,CreatedAt)
    SELECT
        @Code,@NewProfileID,TestID,PointCode,LowerLimit,UpperLimit,AlertLimit,ActionLimit,
        SpecificationText,NULL,NULL,N'Draft',0,NULL,NULL,NULL,NULL,@User,SYSUTCDATETIME()
    FROM dbo.WaterSpecifications
    WHERE ProfileID=@SourceProfileID;
END;

SELECT @NewProfileID;",
                        connection,
                        transaction);

                    create.Parameters.Add("@Code", SqlDbType.NVarChar, 20).Value = code;
                    create.Parameters.Add("@DefaultName", SqlDbType.NVarChar, 150).Value =
                        code == "PW" ? "Purified Water" : "Potable Water";
                    create.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = CurrentUsername();
                    var output = create.Parameters.Add("@NewProfileID", SqlDbType.Int);
                    output.Direction = ParameterDirection.Output;

                    object? scalar = create.ExecuteScalar();
                    createdId = output.Value != DBNull.Value
                        ? Convert.ToInt32(output.Value, CultureInfo.InvariantCulture)
                        : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "WaterTestProfiles",
                        createdId,
                        "Water Test Profile Draft Created",
                        "No draft",
                        code + " controlled profile draft",
                        "Controlled profile version created; no compendial limits were auto-seeded.",
                        CurrentUsername(),
                        "ProfileCode/VersionNo",
                        code,
                        code,
                        "Water");
                });

                WasChanged = true;
                LoadLatestProfile();
                lblStatus.Text = "Draft created. Confirm the site-approved tests, specification text, limits, effective date, and controlled reference.";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to create Water Test Profile Draft.", ex);
                MessageBox.Show(
                    "Unable to create Draft: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        private void BtnLoadDevTemplate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureAuthorPermission();

                if (!AppConfig.IsDevelopment || !IsDevelopmentAdminWorkflowOverrideAllowed())
                {
                    throw new UnauthorizedAccessException(
                        "The MQC-G-0018 authoring helper is available only to Development Admin when DevelopmentAdminFullPermissions is enabled.");
                }

                if (_profileId <= 0 || !_approvalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Load MQC-G-0018 only into an editable Draft Water Test Profile.");

                MessageBoxResult confirmation = MessageBox.Show(
                    "This helper will replace the current in-memory selections for this Draft with the PW/PTW tests and acceptance criteria explicitly represented by MQC-G-0018 v2.0 (Water Analysis).\n\n" +
                    "It does NOT save, review, approve, or activate the profile. Conditional items such as BET/Burkholderia remain visibly identified in Specification Text and must be confirmed for site applicability before Review.\n\n" +
                    "Continue?",
                    "Load MQC-G-0018 v2.0",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirmation != MessageBoxResult.Yes)
                    return;

                string code = SelectedProfileCode();
                ApplyMqcG0018ControlledProfile(code);

                if (!dpEffectiveFrom.SelectedDate.HasValue)
                    dpEffectiveFrom.SelectedDate = DateTime.Today;

                if (dpEffectiveTo.SelectedDate.HasValue &&
                    dpEffectiveFrom.SelectedDate.HasValue &&
                    dpEffectiveTo.SelectedDate.Value.Date == dpEffectiveFrom.SelectedDate.Value.Date)
                {
                    dpEffectiveTo.SelectedDate = null;
                }

                string currentReference = txtControlledReference.Text.Trim();
                if (string.IsNullOrWhiteSpace(currentReference) ||
                    currentReference.StartsWith("DEVELOPMENT BASELINE", StringComparison.OrdinalIgnoreCase) ||
                    currentReference.Equals("MQC-G-0018", StringComparison.OrdinalIgnoreCase))
                {
                    txtControlledReference.Text = MqcWaterSopReference;
                }

                UpdateSelectedCount();
                lblStatus.Text =
                    $"MQC-G-0018 v2.0 loaded in memory only for {code}: {_tests.Count(t => t.IsSelected)} controlled/conditional test(s) selected. " +
                    "Verify applicability, units and the controlled document copy, then Save Draft. Review and Approval remain required.";
            }
            catch (InvalidOperationException ex)
            {
                ApplicationLogger.Warning("MQC-G-0018 Water profile authoring validation was blocked.", ex);
                MessageBox.Show(
                    ex.Message,
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to load MQC-G-0018 Water profile.", ex);
                MessageBox.Show(
                    "Unable to load MQC-G-0018 Water profile: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        private void BtnLoadFullDevInventory_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureAuthorPermission();

                if (!AppConfig.IsDevelopment || !IsDevelopmentAdminWorkflowOverrideAllowed())
                {
                    throw new UnauthorizedAccessException(
                        "The CTG-11-02 PTW supplement helper is available only to Development Admin when DevelopmentAdminFullPermissions is enabled.");
                }

                if (_profileId <= 0 || !_approvalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Load the CTG-11-02 supplement only into an editable Draft Water Test Profile.");

                string code = SelectedProfileCode();
                if (!code.Equals("PTW", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("CTG-11-02 Annexure 11/A3 supplement applies only to the PTW Draft in this helper.");

                MessageBoxResult confirmation = MessageBox.Show(
                    "This helper adds the potable-water contaminant limits transcribed from CTG-11-02 v02 Annexure CTG 11/A3 to the current PTW Draft.\n\n" +
                    "Use it only if CTG-11-02 is still an approved/adopted controlled source at this site. It does NOT save, review, approve, or activate the profile.\n\n" +
                    "Continue?",
                    "Add CTG-11-02 PTW Supplement",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirmation != MessageBoxResult.Yes)
                    return;

                ApplyCtg1102PotableSupplement();

                string currentReference = txtControlledReference.Text.Trim();
                if (string.IsNullOrWhiteSpace(currentReference))
                    txtControlledReference.Text = CtgWaterGuidelineReference;
                else if (!currentReference.Contains("CTG-11-02", StringComparison.OrdinalIgnoreCase))
                    txtControlledReference.Text = currentReference + "; " + CtgWaterGuidelineReference;

                UpdateSelectedCount();
                lblStatus.Text =
                    $"CTG-11-02 PTW supplement loaded in memory only. {_tests.Count(t => t.IsSelected)} test(s) are selected. " +
                    "Confirm the guideline is currently adopted by the site before Save/Review.";
            }
            catch (InvalidOperationException ex)
            {
                ApplicationLogger.Warning("CTG-11-02 Water profile authoring validation was blocked.", ex);
                MessageBox.Show(
                    ex.Message,
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to load CTG-11-02 PTW supplement.", ex);
                MessageBox.Show(
                    "Unable to load CTG-11-02 PTW supplement: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }


        private void ApplyMqcG0018ControlledProfile(string profileCode)
        {
            profileCode = NormalizeProfileCode(profileCode);
            if (profileCode != "PW" && profileCode != "PTW")
                throw new InvalidOperationException("MQC-G-0018 can be loaded only for PW or PTW.");

            ResetTemplateRows();

            bool purified = profileCode.Equals("PW", StringComparison.OrdinalIgnoreCase);
            txtProfileName.Text = purified ? "Purified Water" : "Potable Water";

            WaterProfileTestEditorRow ph = RequireTemplateRow(
                "pH",
                "pH Value", "pH", "P.H");
            RequireCompatibleUnit(ph, "pH", "pH");
            SetTemplate(
                ph,
                lower: purified ? "5.0" : "6.5",
                upper: purified ? "7.0" : "8.5",
                alert: null,
                action: null,
                specification: purified
                    ? "MQC-G-0018 v2.0 §2.13.2.1: Purified Water pH 5.0–7.0."
                    : "MQC-G-0018 v2.0 §2.13.2.1: Potable Water pH 6.5–8.5.");

            WaterProfileTestEditorRow conductivity = RequireTemplateRow(
                "Conductivity",
                "Conductivity", "Electrical Conductivity");
            RequireCompatibleUnit(conductivity, "Conductivity", "µS/cm", "uS/cm");
            SetTemplate(
                conductivity,
                lower: null,
                upper: purified ? "1.3" : "500",
                alert: null,
                action: null,
                specification: purified
                    ? "MQC-G-0018 v2.0 §2.13.1.5–6: Purified Water conductivity ≤1.3 µS/cm at 25°C; follow the SOP temperature/conductivity procedure."
                    : "MQC-G-0018 v2.0 §2.13.1.5: Potable Water conductivity ≤500 µS/cm.");

            WaterProfileTestEditorRow tds = RequireTemplateRow(
                "Total Dissolved Solids (TDS)",
                "Total Dissolved Solids (TDS)", "Total Dissolved Solids", "TDS");
            RequireCompatibleUnit(tds, "Total Dissolved Solids (TDS)", "mg/L", "ppm");
            SetTemplate(
                tds,
                lower: null,
                upper: purified ? "1" : "500",
                alert: null,
                action: null,
                specification: purified
                    ? "MQC-G-0018 v2.0 §2.13.1.5: Purified Water TDS ≤1 mg/L."
                    : "MQC-G-0018 v2.0 §2.13.1.5: Potable Water TDS ≤500 mg/L.");

            if (purified)
            {
                WaterProfileTestEditorRow toc = RequireTemplateRow(
                    "Total Organic Carbon (TOC)",
                    "Total Organic Carbon (TOC)", "Total Organic Carbon", "TOC");
                string tocUnit = NormalizeUnit(toc.Unit);
                if (tocUnit.Contains("PPB", StringComparison.Ordinal))
                {
                    SetTemplate(
                        toc,
                        lower: null,
                        upper: "500",
                        alert: "300",
                        action: "500",
                        specification:
                            "MQC-G-0018 v2.0 §2.13.3.4–5: Purified Water TOC ≤500 ppb; Alert 300 ppb; Action 500 ppb.");
                }
                else if (tocUnit.Contains("MG/L", StringComparison.Ordinal) ||
                         tocUnit.Contains("MGL", StringComparison.Ordinal) ||
                         tocUnit.Contains("PPM", StringComparison.Ordinal))
                {
                    SetTemplate(
                        toc,
                        lower: null,
                        upper: "0.5",
                        alert: "0.3",
                        action: "0.5",
                        specification:
                            "MQC-G-0018 v2.0 §2.13.3.4–5: Purified Water TOC ≤500 ppb (0.5 mg/L); Alert 300 ppb (0.3 mg/L); Action 500 ppb (0.5 mg/L).");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"TOC master unit '{toc.Unit}' cannot safely represent MQC-G-0018. Expected ppb or mg/L.");
                }
            }

            WaterProfileTestEditorRow tamc = RequireTemplateRow(
                "Total Aerobic Microbial Count (TAMC)",
                "Total Aerobic Microbial Count (TAMC)",
                "Total Aerobic Microbial Count",
                "TAMC",
                "Total Viable Count");
            RequireCompatibleUnit(tamc, "Total Aerobic Microbial Count (TAMC)", "CFU/mL");
            SetTemplate(
                tamc,
                lower: null,
                upper: purified ? "100" : "500",
                alert: null,
                action: null,
                specification: purified
                    ? "MQC-G-0018 v2.0 §2.7: Purified Water TAMC NMT 100 CFU/mL. Alert/Action trending levels are established separately under MQC-G-0011 and are not invented here."
                    : "MQC-G-0018 v2.0 §2.7: Potable Water TAMC NMT 500 CFU/mL. Alert/Action trending levels are established separately under MQC-G-0011 and are not invented here.");

            ApplyRequiredAbsenceTemplate(
                "Escherichia coli",
                new[] { "Escherichia coli", "E. coli", "E coli", "E.Coli" },
                "MQC-G-0018 v2.0 §§2.4.4 and 2.7: Escherichia coli absent per 100 mL; any suspect growth requires confirmatory identification.");

            ApplyRequiredAbsenceTemplate(
                "Salmonella",
                new[] { "Salmonella" },
                "MQC-G-0018 v2.0 §§2.4.5 and 2.7: Salmonella absent per 100 mL; any suspect growth requires confirmatory identification.");

            ApplyRequiredAbsenceTemplate(
                "Pseudomonas aeruginosa",
                new[] { "Pseudomonas aeruginosa", "P. aeruginosa" },
                "MQC-G-0018 v2.0 §§2.4.6 and 2.7: Pseudomonas aeruginosa absent per 100 mL; any suspect growth requires confirmatory identification.");

            ApplyRequiredAbsenceTemplate(
                "Staphylococcus aureus",
                new[] { "Staphylococcus aureus", "S. aureus" },
                "MQC-G-0018 v2.0 §§2.4.7 and 2.7: Staphylococcus aureus absent per 100 mL; any suspect growth requires confirmatory identification.");

            ApplyRequiredAbsenceTemplate(
                "Burkholderia cepacia complex",
                new[] { "Burkholderia cepacia complex", "Burkholderia cepacia", "BCC" },
                "MQC-G-0018 v2.0 §2.4.8: Burkholderia cepacia complex must be absent when the test is applicable under MQC-G-0034/risk evaluation. This row is intentionally selected so applicability is explicitly confirmed or deselected during controlled Review.");

            if (purified)
            {
                WaterProfileTestEditorRow bet = RequireTemplateRow(
                    "Bacterial Endotoxin Test (BET)",
                    "Bacterial Endotoxin Test (BET)", "Bacterial Endotoxin Test", "BET");
                RequireCompatibleUnit(bet, "Bacterial Endotoxin Test (BET)", "EU/mL", "IU/mL");
                SetTemplate(
                    bet,
                    lower: null,
                    upper: "0.25",
                    alert: null,
                    action: "0.25",
                    specification:
                        "MQC-G-0018 v2.0 §2.8: BET is applicable to Purified Water as required; acceptance is <0.25 EU/IU per mL under the applicable compendium, method per MQC-G-0020. Deselect when the approved site requirement does not require BET.");
            }

            if (!purified)
            {
                WaterProfileTestEditorRow hardness = RequireTemplateRow(
                    "Hardness",
                    "Hardness", "Total Hardness", "Calcium & Magnesium", "Calcium and Magnesium");
                RequireCompatibleUnit(hardness, "Hardness", "mg/L", "ppm");
                SetTemplate(
                    hardness,
                    lower: null,
                    upper: "300",
                    alert: null,
                    action: null,
                    specification:
                        "MQC-G-0018 v2.0 §2.14.10.2: Potable Water hardness <300 ppm (as CaCO3). Because the source uses a strict '<' criterion, a result at or above 300 requires controlled review/investigation.");
            }

            ApplyMqcQualitativeChemicalTests();

            WaterProfileTestEditorRow residue = RequireTemplateRow(
                "Residue on Evaporation",
                "Residue on Evaporation", "Residue on Evaporation Test");
            string residueUnit = NormalizeUnit(residue.Unit);
            if (residueUnit.Contains("MG/100ML", StringComparison.Ordinal) ||
                residueUnit.Contains("MG100ML", StringComparison.Ordinal))
            {
                SetTemplate(
                    residue,
                    lower: null,
                    upper: "1",
                    alert: null,
                    action: null,
                    specification:
                        "MQC-G-0018 v2.0 §2.14.20: Residue on evaporation NMT 1 mg from 100 mL sample (0.001%).");
            }
            else if (residueUnit.Contains("MG/L", StringComparison.Ordinal) ||
                     residueUnit.Contains("MGL", StringComparison.Ordinal))
            {
                SetTemplate(
                    residue,
                    lower: null,
                    upper: "10",
                    alert: null,
                    action: null,
                    specification:
                        "MQC-G-0018 v2.0 §2.14.20: Residue on evaporation NMT 1 mg from 100 mL sample, equivalent to 10 mg/L.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Residue on Evaporation master unit '{residue.Unit}' cannot safely represent MQC-G-0018. Expected mg/100mL or mg/L.");
            }
        }

        private void ApplyMqcQualitativeChemicalTests()
        {
            ApplyRequiredComplianceTemplate(
                "Acidity",
                new[] { "Acidity" },
                "MQC-G-0018 v2.0 §2.14.12: After the specified methyl-red procedure, the resulting solution is not red. Record Complies/Does Not Comply.");

            ApplyRequiredComplianceTemplate(
                "Ammonium",
                new[] { "Ammonium", "Ammonia" },
                "MQC-G-0018 v2.0 §2.14.14: The test solution is not more intensely coloured than the specified comparator. Record Complies/Does Not Comply.");

            ApplyRequiredComplianceTemplate(
                "Heavy Metals",
                new[] { "Heavy Metals", "Heavy Metals (as Pb)", "Heavy Metals as Pb" },
                "MQC-G-0018 v2.0 §2.14.15: Colour produced with the test solution is not more intense than the specified lead-standard solution. Record Complies/Does Not Comply.");

            ApplyRequiredComplianceTemplate(
                "Chlorides",
                new[] { "Chloride", "Chlorides" },
                "MQC-G-0018 v2.0 §2.14.16: After the specified nitric-acid/silver-nitrate procedure, the appearance of the solution does not change for at least 15 minutes. Record Complies/Does Not Comply.");

            ApplyRequiredComplianceTemplate(
                "Nitrates",
                new[] { "Nitrate", "Nitrates" },
                "MQC-G-0018 v2.0 §2.14.17: Any blue colour is not more intense than the specified nitrate comparator solution. Record Complies/Does Not Comply.");

            ApplyRequiredComplianceTemplate(
                "Sulphates",
                new[] { "Sulphate", "Sulphates", "Sulfate", "Sulfates" },
                "MQC-G-0018 v2.0 §2.14.18: After the specified hydrochloric-acid/barium-chloride procedure, the appearance of the solution does not change for at least 1 hour. Record Complies/Does Not Comply.");

            ApplyRequiredComplianceTemplate(
                "Oxidisable Substances",
                new[] { "Oxidisable Substances", "Oxidizable Substances" },
                "MQC-G-0018 v2.0 §2.14.19: After the specified permanganate procedure and boiling for 5 minutes, the solution remains faintly pink. Record Complies/Does Not Comply.");
        }

        private void ApplyCtg1102PotableSupplement()
        {
            ApplyCtgNumericTemplate("Arsenic", new[] { "Arsenic" }, "0.05", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Arsenic maximum contaminant level 0.05 ppm.");
            ApplyCtgNumericTemplate("Barium", new[] { "Barium" }, "1", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Barium maximum contaminant level 1 ppm.");
            ApplyCtgNumericTemplate("Cadmium", new[] { "Cadmium" }, "0.010", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Cadmium maximum contaminant level 0.010 ppm.");
            ApplyCtgNumericTemplate("Chromium", new[] { "Chromium" }, "0.05", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Chromium maximum contaminant level 0.05 ppm.");
            ApplyCtgNumericTemplate("Lead", new[] { "Lead" }, "0.05", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Lead maximum contaminant level 0.05 ppm.");
            ApplyCtgNumericTemplate("Selenium", new[] { "Selenium" }, "0.01", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Selenium maximum contaminant level 0.01 ppm.");
            ApplyCtgNumericTemplate("Mercury", new[] { "Mercury" }, "0.002", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Mercury maximum contaminant level 0.002 ppm.");
            ApplyCtgNumericTemplate("Fluoride", new[] { "Fluoride" }, "1.4", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Fluoride maximum contaminant level 1.4 ppm.");
            ApplyCtgNumericTemplate("Nitrates (as N)", new[] { "Nitrates (as N)", "Nitrate (as N)" }, "10", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Nitrates (as N) maximum contaminant level 10 ppm.");
            ApplyCtgNumericTemplate("Nitrites (as N)", new[] { "Nitrites (as N)", "Nitrite (as N)" }, "1", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Nitrites (as N) maximum contaminant level 1 ppm.");
            ApplyCtgNumericTemplate("Turbidity", new[] { "Turbidity" }, "1", "NTU",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 1: Turbidity maximum contaminant level 1 NTU.");
            ApplyCtgNumericTemplate("Endrin", new[] { "Endrin" }, "0.0002", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 2: Endrin maximum contaminant level 0.0002 ppm.");
            ApplyCtgNumericTemplate("2,4 DDT", new[] { "2,4 DDT", "2,4-DDT" }, "0.0002", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 2: 2,4 DDT maximum contaminant level 0.0002 ppm.");
            ApplyCtgNumericTemplate("4,4 DDT", new[] { "4,4 DDT", "4,4-DDT" }, "0.0002", "ppm",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 2: 4,4 DDT maximum contaminant level 0.0002 ppm.");
            ApplyCtgNumericTemplate("Gross Alpha and Beta Activity", new[] { "Gross Alpha and Beta Activity", "Gross α and Gross β" }, "10", "pCi/L",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 3: Gross alpha and gross beta activity total maximum 10 pCi/L.");
            ApplyCtgNumericTemplate("Radium-226 and Radium-228", new[] { "Radium-226 and Radium-228", "Ra-226 and Ra-228" }, "5", "pCi/L",
                "CTG-11-02 v02 Annexure CTG 11/A3 Table 3: Ra-226 and Ra-228 total maximum 5 pCi/L.");

            WaterProfileTestEditorRow coliforms = RequireTemplateRow(
                "Total Coliforms",
                "Total Coliforms", "Total Coliform");
            RequireCompatibleUnit(coliforms, "Total Coliforms", "Count");
            SetTemplate(
                coliforms,
                lower: null,
                upper: "1.9999",
                alert: null,
                action: null,
                specification:
                    "CTG-11-02 v02 Annexure CTG 11/A3 Table 4: Total coliforms <2. The source table does not state a separate unit/sample basis; retain that exact source wording and verify the adopted site method before Review.");
        }

        private void ApplyCtgNumericTemplate(
            string logicalName,
            string[] aliases,
            string upper,
            string expectedUnit,
            string specification)
        {
            WaterProfileTestEditorRow row = RequireTemplateRow(logicalName, aliases);
            RequireCompatibleUnit(row, logicalName, expectedUnit);
            SetTemplate(row, lower: null, upper: upper, alert: null, action: null, specification: specification);
        }

        private void ApplyRequiredAbsenceTemplate(
            string logicalName,
            string[] aliases,
            string specification)
        {
            WaterProfileTestEditorRow row = RequireTemplateRow(logicalName, aliases);
            SetTemplate(row, lower: null, upper: null, alert: null, action: null, specification: specification);
        }

        private void ApplyRequiredComplianceTemplate(
            string logicalName,
            string[] aliases,
            string specification)
        {
            WaterProfileTestEditorRow row = RequireTemplateRow(logicalName, aliases);
            SetTemplate(row, lower: null, upper: null, alert: null, action: null, specification: specification);
        }

        private WaterProfileTestEditorRow RequireTemplateRow(string logicalName, params string[] aliases)
        {
            WaterProfileTestEditorRow? row = FindTemplateRow(aliases);
            if (row != null)
                return row;

            throw new InvalidOperationException(
                $"Required Water test '{logicalName}' is not available as an active Tests-master row. " +
                "Run Database Maintenance for migration 20260908_000, then Reload this Draft before using the controlled-source helper.");
        }

        private static void RequireCompatibleUnit(
            WaterProfileTestEditorRow row,
            string logicalName,
            params string[] acceptableUnits)
        {
            string actual = NormalizeUnit(row.Unit);
            bool compatible = acceptableUnits
                .Select(NormalizeUnit)
                .Where(expected => !string.IsNullOrWhiteSpace(expected))
                .Any(expected =>
                    actual.Equals(expected, StringComparison.Ordinal) ||
                    actual.Contains(expected, StringComparison.Ordinal) ||
                    expected.Contains(actual, StringComparison.Ordinal));

            if (!compatible)
            {
                throw new InvalidOperationException(
                    $"Tests-master unit mismatch for '{logicalName}': stored unit '{row.Unit}'. " +
                    $"Expected one of: {string.Join(", ", acceptableUnits)}. Correct the controlled Tests master instead of silently converting an incompatible unit.");
            }
        }

        private void ResetTemplateRows()
        {
            foreach (WaterProfileTestEditorRow row in _tests)
            {
                row.IsSelected = false;
                row.LowerLimitText = string.Empty;
                row.UpperLimitText = string.Empty;
                row.AlertLimitText = string.Empty;
                row.ActionLimitText = string.Empty;
                row.SpecificationText = string.Empty;
            }
        }

        private void ApplyTemplate(
            string[] aliases,
            string specification,
            string? lower = null,
            string? upper = null,
            string? alert = null,
            string? action = null)
        {
            WaterProfileTestEditorRow? row = FindTemplateRow(aliases);
            if (row == null)
                return;

            SetTemplate(row, lower, upper, alert, action, specification);
        }

        private WaterProfileTestEditorRow? FindTemplateRow(params string[] aliases)
        {
            string[] normalizedAliases = aliases
                .Select(NormalizeTestName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return _tests.FirstOrDefault(row =>
                row.IsActiveMaster &&
                normalizedAliases.Contains(NormalizeTestName(row.TestName), StringComparer.Ordinal));
        }

        private static void SetTemplate(
            WaterProfileTestEditorRow row,
            string? lower,
            string? upper,
            string? alert,
            string? action,
            string specification)
        {
            row.IsSelected = true;
            row.LowerLimitText = lower ?? string.Empty;
            row.UpperLimitText = upper ?? string.Empty;
            row.AlertLimitText = alert ?? string.Empty;
            row.ActionLimitText = action ?? string.Empty;
            row.SpecificationText = specification;
        }

        private static string NormalizeTestName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return new string(value
                .ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private static string NormalizeUnit(string? value) =>
            (value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Replace("µ", "U", StringComparison.Ordinal)
                .Replace("μ", "U", StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal);

        private void BtnSaveDraft_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveDraftInternal(showConfirmation: true);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to save Water Test Profile Draft.", ex);
                MessageBox.Show(
                    "Unable to save Draft: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void SaveDraftInternal(bool showConfirmation)
        {
            EnsureAuthorPermission();
            CommitGridEdits();

            if (_profileId <= 0 || !_approvalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only a Draft Water Test Profile can be edited.");

            string currentUser = CurrentUsername();
            if (!IsDevelopmentAdminWorkflowOverrideAllowed() &&
                !currentUser.Equals(_createdBy, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Only the Draft creator may edit this controlled profile. Development Admin may override this separation only when DevelopmentAdminFullPermissions is enabled.");
            }

            string profileName = txtProfileName.Text.Trim();
            if (string.IsNullOrWhiteSpace(profileName))
                throw new InvalidOperationException("Profile Name is required.");

            if (dpEffectiveFrom.SelectedDate.HasValue && dpEffectiveTo.SelectedDate.HasValue &&
                dpEffectiveTo.SelectedDate.Value.Date < dpEffectiveFrom.SelectedDate.Value.Date)
                throw new InvalidOperationException("Effective To cannot be earlier than Effective From.");

            foreach (WaterProfileTestEditorRow row in _tests.Where(t => t.IsSelected))
                ValidateNumericRelationships(row, requireSpecificationText: false);

            string code = SelectedProfileCode();
            string reference = txtControlledReference.Text.Trim();
            DateTime? effectiveFrom = dpEffectiveFrom.SelectedDate?.Date;
            DateTime? effectiveTo = dpEffectiveTo.SelectedDate?.Date;
            WaterProfileTestEditorRow[] selected = _tests.Where(t => t.IsSelected).ToArray();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                string authorRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                    connection,
                    transaction,
                    currentUser,
                    "CanManageSettings",
                    "edit controlled Water Test Profile drafts");
                bool developmentAdminOverride = IsDevelopmentAdminWorkflowOverrideAllowed(authorRole);

                using (var lockProfile = new SqlCommand(@"
SELECT ApprovalStatus, CreatedBy
FROM dbo.WaterTestProfiles WITH(UPDLOCK,HOLDLOCK)
WHERE ProfileID=@ProfileID;", connection, transaction))
                {
                    lockProfile.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                    using SqlDataReader locked = lockProfile.ExecuteReader();
                    if (!locked.Read())
                        throw new DBConcurrencyException("The Water Test Profile no longer exists. Reload before editing.");

                    string currentStatus = locked.IsDBNull(0) ? string.Empty : locked.GetString(0).Trim();
                    string lockedCreator = locked.IsDBNull(1) ? string.Empty : locked.GetString(1).Trim();
                    if (locked.Read())
                        throw new DBConcurrencyException("Duplicate Water Test Profile identity was detected.");

                    if (!currentStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                        throw new DBConcurrencyException("The profile is no longer Draft. Reload before editing.");

                    if (!developmentAdminOverride &&
                        !currentUser.Equals(lockedCreator, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException(
                            "The authenticated user is not the Draft author. Editing was blocked.");
                    }
                }

                using (var update = new SqlCommand(@"
UPDATE dbo.WaterTestProfiles
SET ProfileName=@Name,
    EffectiveFrom=@EffectiveFrom,
    EffectiveTo=@EffectiveTo,
    ControlledReference=@Reference,
    IsActive=0
WHERE ProfileID=@ProfileID
  AND ApprovalStatus=N'Draft';", connection, transaction))
                {
                    update.Parameters.Add("@Name", SqlDbType.NVarChar, 150).Value = profileName;
                    update.Parameters.Add("@EffectiveFrom", SqlDbType.Date).Value = effectiveFrom.HasValue ? effectiveFrom.Value : DBNull.Value;
                    update.Parameters.Add("@EffectiveTo", SqlDbType.Date).Value = effectiveTo.HasValue ? effectiveTo.Value : DBNull.Value;
                    update.Parameters.Add("@Reference", SqlDbType.NVarChar, 300).Value =
                        string.IsNullOrWhiteSpace(reference) ? DBNull.Value : reference;
                    update.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                    if (update.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("The Draft changed while it was being saved.");
                }

                using (var deleteSpecs = new SqlCommand(
                    @"DELETE FROM dbo.WaterSpecifications
                      WHERE ProfileID=@ProfileID
                        AND ApprovalStatus=N'Draft'
                        AND (PointCode IS NULL OR LTRIM(RTRIM(PointCode))=N'');",
                    connection,
                    transaction))
                {
                    deleteSpecs.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                    deleteSpecs.ExecuteNonQuery();
                }

                using (var deleteTests = new SqlCommand(
                    "DELETE FROM dbo.WaterTestProfileTests WHERE ProfileID=@ProfileID;",
                    connection,
                    transaction))
                {
                    deleteTests.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                    deleteTests.ExecuteNonQuery();
                }

                int sortOrder = 0;
                foreach (WaterProfileTestEditorRow row in selected)
                {
                    sortOrder += 10;
                    using (var testInsert = new SqlCommand(@"
INSERT dbo.WaterTestProfileTests(ProfileID,TestID,SortOrder,IsActive)
VALUES(@ProfileID,@TestID,@SortOrder,1);", connection, transaction))
                    {
                        testInsert.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        testInsert.Parameters.Add("@TestID", SqlDbType.Int).Value = row.TestID;
                        testInsert.Parameters.Add("@SortOrder", SqlDbType.Int).Value = sortOrder;
                        testInsert.ExecuteNonQuery();
                    }

                    ParseNullableDecimal(row.LowerLimitText, "Lower Limit", row.TestName, out decimal? lower);
                    ParseNullableDecimal(row.UpperLimitText, "Upper Limit", row.TestName, out decimal? upper);
                    ParseNullableDecimal(row.AlertLimitText, "Alert Limit", row.TestName, out decimal? alert);
                    ParseNullableDecimal(row.ActionLimitText, "Action Limit", row.TestName, out decimal? action);

                    using var specInsert = new SqlCommand(@"
INSERT dbo.WaterSpecifications
    (ProfileCode,ProfileID,TestID,PointCode,LowerLimit,UpperLimit,AlertLimit,ActionLimit,
     SpecificationText,EffectiveFrom,EffectiveTo,ApprovalStatus,IsActive,
     ReviewedBy,ReviewedAt,ApprovedBy,ApprovedAt,CreatedBy,CreatedAt)
VALUES
    (@Code,@ProfileID,@TestID,NULL,@Lower,@Upper,@Alert,@Action,
     @SpecificationText,@EffectiveFrom,@EffectiveTo,N'Draft',0,
     NULL,NULL,NULL,NULL,@User,SYSUTCDATETIME());", connection, transaction);
                    specInsert.Parameters.Add("@Code", SqlDbType.NVarChar, 20).Value = code;
                    specInsert.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                    specInsert.Parameters.Add("@TestID", SqlDbType.Int).Value = row.TestID;
                    AddNullableDecimal(specInsert, "@Lower", lower);
                    AddNullableDecimal(specInsert, "@Upper", upper);
                    AddNullableDecimal(specInsert, "@Alert", alert);
                    AddNullableDecimal(specInsert, "@Action", action);
                    specInsert.Parameters.Add("@SpecificationText", SqlDbType.NVarChar, 500).Value =
                        row.SpecificationText?.Trim() ?? string.Empty;
                    specInsert.Parameters.Add("@EffectiveFrom", SqlDbType.Date).Value =
                        effectiveFrom.HasValue ? effectiveFrom.Value : DBNull.Value;
                    specInsert.Parameters.Add("@EffectiveTo", SqlDbType.Date).Value =
                        effectiveTo.HasValue ? effectiveTo.Value : DBNull.Value;
                    specInsert.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = CurrentUsername();
                    specInsert.ExecuteNonQuery();
                }

                using (var removeUnassignedPointSpecifications = new SqlCommand(@"
DELETE s
FROM dbo.WaterSpecifications s
WHERE s.ProfileID=@ProfileID
  AND s.ApprovalStatus=N'Draft'
  AND NULLIF(LTRIM(RTRIM(ISNULL(s.PointCode,N''))),N'') IS NOT NULL
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.WaterTestProfileTests pt
      WHERE pt.ProfileID=s.ProfileID
        AND pt.TestID=s.TestID
        AND ISNULL(pt.IsActive,1)=1
  );", connection, transaction))
                {
                    removeUnassignedPointSpecifications.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                    removeUnassignedPointSpecifications.ExecuteNonQuery();
                }

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "WaterTestProfiles",
                    _profileId,
                    "Water Test Profile Draft Updated",
                    "Draft before edit",
                    BuildProfileEvidenceText(code, profileName, reference, selected.Length),
                    "Draft master-data edit; review and approval are still required.",
                    CurrentUsername(),
                    "Profile/Tests/Specifications",
                    code,
                    code,
                    "Water");
            });

            WasChanged = true;
            if (showConfirmation)
            {
                lblStatus.Text = "Draft saved. No operational profile was activated.";
                MessageBox.Show(
                    "Draft saved. It remains inactive until independent Review and Approval are completed.",
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void BtnReview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureReviewerPermission();
                if (_profileId <= 0 || !_approvalStatus.Equals("Draft", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only a saved Draft Water Test Profile can be reviewed.");

                string username = CurrentUsername();
                if (!IsDevelopmentAdminWorkflowOverrideAllowed() &&
                    username.Equals(_createdBy, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The Draft author cannot perform the independent Review. Sign in with a user who has the existing PharmaLIMS Review permission. Only Development Admin may perform both stages when DevelopmentAdminFullPermissions is enabled.");
                }

                ValidateControlledTransition();

                string code = SelectedProfileCode();
                var signature = new ElectronicSignature(
                    $"{code} v{_versionNo}",
                    username,
                    "Water Test Profile Review",
                    true)
                {
                    Owner = this
                };

                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection,
                        transaction,
                        signature.SignedBy,
                        "CanReviewResults",
                        "review controlled Water Test Profiles");
                    bool developmentAdminOverride = IsDevelopmentAdminWorkflowOverrideAllowed(signerRole);

                    using (var verify = new SqlCommand(@"
SELECT CASE WHEN
    p.ApprovalStatus=N'Draft'
    AND NULLIF(LTRIM(RTRIM(p.ControlledReference)),N'') IS NOT NULL
    AND p.EffectiveFrom IS NOT NULL
    AND (p.EffectiveTo IS NULL OR p.EffectiveTo>=p.EffectiveFrom)
    AND (@AllowSameUser=1 OR UPPER(LTRIM(RTRIM(p.CreatedBy)))<>UPPER(LTRIM(RTRIM(@User))))
    AND EXISTS
    (
        SELECT 1
        FROM dbo.WaterTestProfileTests pt
        INNER JOIN dbo.Tests t ON t.TestID=pt.TestID AND ISNULL(t.IsActive,0)=1
        WHERE pt.ProfileID=p.ProfileID
          AND ISNULL(pt.IsActive,1)=1
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM dbo.WaterTestProfileTests pt
        INNER JOIN dbo.Tests t ON t.TestID=pt.TestID AND ISNULL(t.IsActive,0)=1
        WHERE pt.ProfileID=p.ProfileID
          AND ISNULL(pt.IsActive,1)=1
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.WaterSpecifications s
              WHERE s.ProfileID=p.ProfileID
                AND s.TestID=pt.TestID
                AND (s.PointCode IS NULL OR LTRIM(RTRIM(s.PointCode))=N'')
                AND s.ApprovalStatus=N'Draft'
                AND NULLIF(LTRIM(RTRIM(s.SpecificationText)),N'') IS NOT NULL
                AND UPPER(LTRIM(RTRIM(s.SpecificationText))) NOT LIKE N'DEVELOPMENT FULL TEST INVENTORY PLACEHOLDER%'
          )
    )
THEN 1 ELSE 0 END
FROM dbo.WaterTestProfiles p WITH(UPDLOCK,HOLDLOCK)
WHERE p.ProfileID=@ProfileID;", connection, transaction))
                    {
                        verify.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        verify.Parameters.Add("@AllowSameUser", SqlDbType.Bit).Value = developmentAdminOverride;
                        verify.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        object? state = verify.ExecuteScalar();
                        if (state == null || state == DBNull.Value ||
                            Convert.ToInt32(state, CultureInfo.InvariantCulture) != 1)
                        {
                            throw new InvalidOperationException(
                                "The Draft changed or no longer satisfies the controlled Review contract. Reload and verify the saved profile before signing.");
                        }
                    }

                    using (var updateSpecs = new SqlCommand(@"
UPDATE dbo.WaterSpecifications
SET ApprovalStatus=N'Reviewed',
    ReviewedBy=@User,
    ReviewedAt=SYSUTCDATETIME(),
    IsActive=0
WHERE ProfileID=@ProfileID
  AND ApprovalStatus=N'Draft';", connection, transaction))
                    {
                        updateSpecs.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        updateSpecs.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        updateSpecs.ExecuteNonQuery();
                    }

                    using (var update = new SqlCommand(@"
UPDATE dbo.WaterTestProfiles WITH(ROWLOCK)
SET ApprovalStatus=N'Reviewed',
    ReviewedBy=@User,
    ReviewedAt=SYSUTCDATETIME(),
    IsActive=0
WHERE ProfileID=@ProfileID
  AND ApprovalStatus=N'Draft';", connection, transaction))
                    {
                        update.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        update.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        if (update.ExecuteNonQuery() != 1)
                            throw new DBConcurrencyException("The Draft changed before Review. Reload and retry.");
                    }

                    InsertProfileSignature(connection, transaction, _profileId, "Review", signature);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "WaterTestProfiles",
                        _profileId,
                        "Water Test Profile Reviewed",
                        "Draft",
                        "Reviewed",
                        signature.Reason + " | Meaning: " + signature.Meaning,
                        signature.SignedBy,
                        "ApprovalStatus",
                        code,
                        code,
                        "Water");
                });

                WasChanged = true;
                LoadLatestProfile();
                MessageBox.Show(
                    "The profile was reviewed and locked. Approval & Activation requires the existing PharmaLIMS Approval permission and an independent user, except for the controlled Development Admin override.",
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (InvalidOperationException ex)
            {
                ApplicationLogger.Warning("Water Test Profile Review was blocked by controlled validation.", ex);
                MessageBox.Show(
                    "Review was not completed: " + ex.Message,
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to review Water Test Profile.", ex);
                MessageBox.Show(
                    "Review was not completed: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnApprove_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureApproverPermission();
                if (_profileId <= 0 || !_approvalStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only a Reviewed Water Test Profile can be approved.");

                string username = CurrentUsername();
                if (!IsDevelopmentAdminWorkflowOverrideAllowed() &&
                    (username.Equals(_createdBy, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(_reviewedBy) &&
                      username.Equals(_reviewedBy, StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException(
                        "Approval must be independent of both the Draft author and reviewer. Sign in with a user who has the existing PharmaLIMS Approval permission. Only Development Admin may perform all stages when DevelopmentAdminFullPermissions is enabled.");
                }

                ValidateControlledTransition();

                string code = SelectedProfileCode();
                var signature = new ElectronicSignature(
                    $"{code} v{_versionNo}",
                    username,
                    "Water Test Profile Approval",
                    true)
                {
                    Owner = this
                };

                if (signature.ShowDialog() != true || !signature.IsConfirmed)
                    return;

                DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
                {
                    string signerRole = DatabaseHelper.EnsureUserPermissionInTransaction(
                        connection,
                        transaction,
                        signature.SignedBy,
                        "CanApproveResults",
                        "approve controlled Water Test Profiles");
                    bool developmentAdminOverride = IsDevelopmentAdminWorkflowOverrideAllowed(signerRole);

                    using (var verify = new SqlCommand(@"
SELECT CASE WHEN
    p.ApprovalStatus=N'Reviewed'
    AND NULLIF(LTRIM(RTRIM(p.ReviewedBy)),N'') IS NOT NULL
    AND (@AllowSameUser=1 OR
         (
             UPPER(LTRIM(RTRIM(p.ReviewedBy)))<>UPPER(LTRIM(RTRIM(@User)))
             AND UPPER(LTRIM(RTRIM(p.CreatedBy)))<>UPPER(LTRIM(RTRIM(@User)))
             AND UPPER(LTRIM(RTRIM(p.CreatedBy)))<>UPPER(LTRIM(RTRIM(p.ReviewedBy)))
         ))
    AND NULLIF(LTRIM(RTRIM(p.ControlledReference)),N'') IS NOT NULL
    AND p.EffectiveFrom IS NOT NULL
    AND p.EffectiveFrom<=CAST(GETDATE() AS date)
    AND (p.EffectiveTo IS NULL OR p.EffectiveTo>=CAST(GETDATE() AS date))
    AND EXISTS
    (
        SELECT 1
        FROM dbo.WaterTestProfileTests pt
        INNER JOIN dbo.Tests t ON t.TestID=pt.TestID AND ISNULL(t.IsActive,0)=1
        WHERE pt.ProfileID=p.ProfileID
          AND ISNULL(pt.IsActive,1)=1
    )
    AND NOT EXISTS
    (
        SELECT 1
        FROM dbo.WaterTestProfileTests pt
        INNER JOIN dbo.Tests t ON t.TestID=pt.TestID AND ISNULL(t.IsActive,0)=1
        WHERE pt.ProfileID=p.ProfileID
          AND ISNULL(pt.IsActive,1)=1
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.WaterSpecifications s
              WHERE s.ProfileID=p.ProfileID
                AND s.TestID=pt.TestID
                AND (s.PointCode IS NULL OR LTRIM(RTRIM(s.PointCode))=N'')
                AND s.ApprovalStatus=N'Reviewed'
                AND NULLIF(LTRIM(RTRIM(s.SpecificationText)),N'') IS NOT NULL
                AND UPPER(LTRIM(RTRIM(s.SpecificationText))) NOT LIKE N'DEVELOPMENT FULL TEST INVENTORY PLACEHOLDER%'
          )
    )
THEN 1 ELSE 0 END
FROM dbo.WaterTestProfiles p WITH(UPDLOCK,HOLDLOCK)
WHERE p.ProfileID=@ProfileID;", connection, transaction))
                    {
                        verify.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        verify.Parameters.Add("@AllowSameUser", SqlDbType.Bit).Value = developmentAdminOverride;
                        verify.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        object? state = verify.ExecuteScalar();
                        if (state == null || state == DBNull.Value || Convert.ToInt32(state, CultureInfo.InvariantCulture) != 1)
                        {
                            throw new InvalidOperationException(
                                "The reviewed profile is not eligible for activation. It must have a current effective date, controlled reference, and at least one active test with specification text.");
                        }
                    }

                    using (var obsoleteSpecs = new SqlCommand(@"
UPDATE s
SET s.IsActive=0,
    s.ApprovalStatus=CASE WHEN s.ApprovalStatus=N'Approved' THEN N'Obsolete' ELSE s.ApprovalStatus END
FROM dbo.WaterSpecifications s
INNER JOIN dbo.WaterTestProfiles p ON p.ProfileID=s.ProfileID
WHERE UPPER(LTRIM(RTRIM(p.ProfileCode)))=@Code
  AND p.ProfileID<>@ProfileID
  AND ISNULL(p.IsActive,0)=1;", connection, transaction))
                    {
                        obsoleteSpecs.Parameters.Add("@Code", SqlDbType.NVarChar, 20).Value = code;
                        obsoleteSpecs.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        obsoleteSpecs.ExecuteNonQuery();
                    }

                    using (var obsoleteProfiles = new SqlCommand(@"
UPDATE dbo.WaterTestProfiles
SET IsActive=0,
    ApprovalStatus=CASE WHEN ApprovalStatus=N'Approved' THEN N'Obsolete' ELSE ApprovalStatus END
WHERE UPPER(LTRIM(RTRIM(ProfileCode)))=@Code
  AND ProfileID<>@ProfileID
  AND ISNULL(IsActive,0)=1;", connection, transaction))
                    {
                        obsoleteProfiles.Parameters.Add("@Code", SqlDbType.NVarChar, 20).Value = code;
                        obsoleteProfiles.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        obsoleteProfiles.ExecuteNonQuery();
                    }

                    using (var approveSpecs = new SqlCommand(@"
UPDATE dbo.WaterSpecifications
SET ApprovalStatus=N'Approved',
    IsActive=1,
    ApprovedBy=@User,
    ApprovedAt=SYSUTCDATETIME()
WHERE ProfileID=@ProfileID
  AND ApprovalStatus=N'Reviewed';", connection, transaction))
                    {
                        approveSpecs.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        approveSpecs.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        if (approveSpecs.ExecuteNonQuery() <= 0)
                            throw new InvalidOperationException("No reviewed Water specifications were available for approval.");
                    }

                    using (var approve = new SqlCommand(@"
UPDATE dbo.WaterTestProfiles
SET ApprovalStatus=N'Approved',
    IsActive=1,
    ApprovedBy=@User,
    ApprovedAt=SYSUTCDATETIME()
WHERE ProfileID=@ProfileID
  AND ApprovalStatus=N'Reviewed'
  AND IsActive=0;", connection, transaction))
                    {
                        approve.Parameters.Add("@User", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
                        approve.Parameters.Add("@ProfileID", SqlDbType.Int).Value = _profileId;
                        if (approve.ExecuteNonQuery() != 1)
                            throw new DBConcurrencyException("The reviewed profile changed before Approval. Reload and retry.");
                    }

                    InsertProfileSignature(connection, transaction, _profileId, "Approval", signature);

                    DatabaseHelper.AddAuditTrailAdvanced(
                        connection,
                        transaction,
                        "WaterTestProfiles",
                        _profileId,
                        "Water Test Profile Approved and Activated",
                        "Reviewed / Inactive",
                        "Approved / Active",
                        signature.Reason + " | Meaning: " + signature.Meaning,
                        signature.SignedBy,
                        "ApprovalStatus/IsActive",
                        code,
                        code,
                        "Water");
                });

                WasChanged = true;
                LoadLatestProfile();
                MessageBox.Show(
                    "The controlled Water Test Profile is approved and active. Future direct Water registrations will use its approved test assignments and specification snapshots.",
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (InvalidOperationException ex)
            {
                ApplicationLogger.Warning("Water Test Profile Approval was blocked by controlled validation.", ex);
                MessageBox.Show(
                    "Approval was not completed: " + ex.Message,
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to approve Water Test Profile.", ex);
                MessageBox.Show(
                    "Approval was not completed: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ValidateControlledTransition()
        {
            CommitGridEdits();

            if (string.IsNullOrWhiteSpace(txtProfileName.Text))
                throw new InvalidOperationException("Profile Name is required.");
            if (!dpEffectiveFrom.SelectedDate.HasValue)
                throw new InvalidOperationException("Effective From is required before Review/Approval.");
            if (dpEffectiveTo.SelectedDate.HasValue &&
                dpEffectiveTo.SelectedDate.Value.Date < dpEffectiveFrom.SelectedDate.Value.Date)
                throw new InvalidOperationException("Effective To cannot be earlier than Effective From.");
            string controlledReference = txtControlledReference.Text.Trim();
            if (string.IsNullOrWhiteSpace(controlledReference))
                throw new InvalidOperationException(
                    "Controlled Reference is required. Cite the approved site specification/SOP/pharmacopoeial assessment; do not enter inferred global limits.");

            if (controlledReference.StartsWith("DEVELOPMENT BASELINE", StringComparison.OrdinalIgnoreCase) ||
                controlledReference.Contains("REPLACE WITH CURRENT CONTROLLED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The Development baseline placeholder cannot be reviewed or approved. Replace it with the current controlled site specification/SOP/pharmacopoeial assessment reference after verifying every selected test and limit.");
            }

            WaterProfileTestEditorRow[] selected = _tests.Where(t => t.IsSelected).ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException("At least one active controlled test must be assigned.");

            WaterProfileTestEditorRow[] unresolvedInventoryRows = selected
                .Where(row => row.SpecificationText.StartsWith(
                    FullDevInventoryPlaceholderPrefix,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (unresolvedInventoryRows.Length > 0)
            {
                string preview = string.Join(
                    ", ",
                    unresolvedInventoryRows
                        .Take(6)
                        .Select(row => row.TestName));
                if (unresolvedInventoryRows.Length > 6)
                    preview += ", ...";

                throw new InvalidOperationException(
                    $"Review/Approval is blocked because {unresolvedInventoryRows.Length} full-inventory test(s) still contain Development placeholders: {preview}. " +
                    "Replace each placeholder with the exact controlled acceptance criterion, or deselect tests that are not applicable.");
            }

            foreach (WaterProfileTestEditorRow row in selected)
            {
                if (!row.IsActiveMaster)
                    throw new InvalidOperationException("Selected test is inactive in the Tests master: " + row.TestName + ".");
                ValidateNumericRelationships(row, requireSpecificationText: true);
            }
        }

        private static void ValidateNumericRelationships(WaterProfileTestEditorRow row, bool requireSpecificationText)
        {
            ParseNullableDecimal(row.LowerLimitText, "Lower Limit", row.TestName, out decimal? lower);
            ParseNullableDecimal(row.UpperLimitText, "Upper Limit", row.TestName, out decimal? upper);
            ParseNullableDecimal(row.AlertLimitText, "Alert Limit", row.TestName, out decimal? alert);
            ParseNullableDecimal(row.ActionLimitText, "Action Limit", row.TestName, out decimal? action);

            if (lower.HasValue && upper.HasValue && upper.Value < lower.Value)
                throw new InvalidOperationException($"Upper Limit cannot be lower than Lower Limit for '{row.TestName}'.");
            if (alert.HasValue && action.HasValue && action.Value < alert.Value)
                throw new InvalidOperationException($"Action Limit cannot be lower than Alert Limit for '{row.TestName}'.");

            if (requireSpecificationText && string.IsNullOrWhiteSpace(row.SpecificationText))
                throw new InvalidOperationException($"Specification Text is required for selected test '{row.TestName}'.");
        }

        private static void ParseNullableDecimal(string? text, string field, string testName, out decimal? value)
        {
            string clean = text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clean))
            {
                value = null;
                return;
            }

            if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
                throw new InvalidOperationException($"{field} for '{testName}' must be a valid number using '.' as the decimal separator.");

            value = parsed;
        }

        private static void AddNullableDecimal(SqlCommand command, string name, decimal? value)
        {
            SqlParameter parameter = command.Parameters.Add(name, SqlDbType.Decimal);
            parameter.Precision = 18;
            parameter.Scale = 6;
            parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        }

        private static void InsertProfileSignature(
            SqlConnection connection,
            SqlTransaction transaction,
            int profileId,
            string actionType,
            ElectronicSignature signature)
        {
            using var command = new SqlCommand(@"
INSERT dbo.WaterTestProfileSignatures
    (ProfileID,ActionType,MeaningOfSignature,ActionReason,SignedBy,UserRole,SignedAt,SourceWorkstation)
VALUES
    (@ProfileID,@ActionType,@Meaning,@Reason,@SignedBy,@Role,SYSUTCDATETIME(),@Workstation);",
                connection,
                transaction);
            command.Parameters.Add("@ProfileID", SqlDbType.Int).Value = profileId;
            command.Parameters.Add("@ActionType", SqlDbType.NVarChar, 60).Value = actionType;
            command.Parameters.Add("@Meaning", SqlDbType.NVarChar, 255).Value = signature.Meaning;
            command.Parameters.Add("@Reason", SqlDbType.NVarChar, 1000).Value = signature.Reason;
            command.Parameters.Add("@SignedBy", SqlDbType.NVarChar, 100).Value = signature.SignedBy;
            command.Parameters.Add("@Role", SqlDbType.NVarChar, 100).Value =
                string.IsNullOrWhiteSpace(Login.CurrentUserRole) ? DBNull.Value : Login.CurrentUserRole;
            command.Parameters.Add("@Workstation", SqlDbType.NVarChar, 200).Value = Environment.MachineName;
            command.ExecuteNonQuery();
        }

        private static string CurrentUsername()
        {
            string username = Login.CurrentUser?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(username))
                throw new InvalidOperationException("An authenticated PharmaLIMS account is required.");
            return username;
        }

        private static string BuildProfileEvidenceText(string code, string name, string reference, int testCount) =>
            $"Code={code}; Name={name}; Tests={testCount}; ControlledReference={(string.IsNullOrWhiteSpace(reference) ? "Not yet assigned" : reference)}";

        private void CommitGridEdits()
        {
            dgTests.CommitEdit(DataGridEditingUnit.Cell, true);
            dgTests.CommitEdit(DataGridEditingUnit.Row, true);
        }

        private void DgTests_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateSelectedCount), DispatcherPriority.Background);
        }

        private void BtnReload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadLatestProfile();
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error("Unable to reload Water Test Profile.", ex);
                MessageBox.Show(
                    "Reload failed: " + UserFacingError.SafeMessage(ex),
                    "Water Test Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
