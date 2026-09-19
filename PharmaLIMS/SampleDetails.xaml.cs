#nullable disable
using Microsoft.Data.SqlClient;
using PharmaLIMS.Infrastructure;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Windows;

namespace PharmaLIMS
{
    public partial class SampleDetails : Window
    {
        private int _sampleId;
        private string _sampleNumber;
        private DataTable _sampleHeader;
        private SampleSituation _situation;

        public SampleDetails()
        {
            InitializeComponent();
            Loaded += SampleDetails_Loaded;
        }

        public SampleDetails(int sampleId) : this()
        {
            _sampleId = sampleId;
        }

        public SampleDetails(string sampleNumber) : this()
        {
            _sampleNumber = sampleNumber;
        }

        private void SampleDetails_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSampleDetails();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadSampleDetails();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnEnterResults_Click(object sender, RoutedEventArgs e)
        {
            if (_sampleId <= 0)
            {
                ShowInfo("No sample is selected.");
                return;
            }

            OpenWindowByTypeName("PharmaLIMS.ResultsEntry", _sampleId, _sampleNumber, true);
            LoadSampleDetails();
        }

        private void BtnOpenInvestigation_Click(object sender, RoutedEventArgs e)
        {
            if (_sampleId <= 0)
            {
                ShowInfo("No sample is selected.");
                return;
            }

            string[] possibleWindows =
            {
                "PharmaLIMS.QualityEventInvestigation",
                "PharmaLIMS.QualityEventInvestigationWindow",
                "PharmaLIMS.InvestigationWindow",
                "PharmaLIMS.QualityEvent"
            };

            foreach (string typeName in possibleWindows)
            {
                if (TryOpenWindowByTypeName(typeName, _sampleId, _sampleNumber, true))
                {
                    LoadSampleDetails();
                    return;
                }
            }

            ShowInfo("Investigation window was not found. The existing investigation screen name may be different.");
        }

        private void BtnViewCOA_Click(object sender, RoutedEventArgs e)
        {
            if (_sampleId <= 0)
            {
                ShowInfo("No sample is selected.");
                return;
            }

            string[] possibleWindows =
            {
                "PharmaLIMS.ReportCertificate",
                "PharmaLIMS.CertificatesWindow",
                "PharmaLIMS.CertificateManagement",
                "PharmaLIMS.COAWindow"
            };

            foreach (string typeName in possibleWindows)
            {
                if (TryOpenWindowByTypeName(typeName, _sampleId, _sampleNumber, true))
                {
                    LoadSampleDetails();
                    return;
                }
            }

            ShowInfo("COA / Certificate window was not found. The existing COA screen name may be different.");
        }

        private void LoadSampleDetails()
        {
            try
            {
                SetBusy(true, "Loading sample details...");

                ResolveSampleIdIfNeeded();

                if (_sampleId <= 0)
                {
                    SetBusy(false, "No sample selected.");
                    ShowInfo("Sample was not found.");
                    return;
                }

                LoadSampleHeader();
                LoadTestsAndResults();
                LoadTimeline();
                LoadInvestigations();
                LoadCOA();
                LoadAuditTrail();
                LoadSignatures();

                _situation = EvaluateSampleSituation(_sampleId);
                ApplySituationToUi(_situation);
                ApplyActionButtonStates(_situation);

                SetBusy(false, "Sample details loaded.");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Error loading sample details.");
                ShowError("Error loading sample details:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
            }
        }

        private void ResolveSampleIdIfNeeded()
        {
            if (_sampleId > 0)
                return;

            if (string.IsNullOrWhiteSpace(_sampleNumber))
                return;

            string query = @"
                SELECT TOP 1 SampleID
                FROM Samples
                WHERE SampleNumber = @sampleNumber";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleNumber", _sampleNumber)
            };

            DataTable dt = DatabaseHelper.ExecuteQuery(query, pars);
            if (dt.Rows.Count > 0)
                _sampleId = SafeInt(dt.Rows[0]["SampleID"]);
        }

        private void LoadSampleHeader()
        {
            string query = @"
                SELECT TOP 1
                    s.SampleID,
                    ISNULL(s.SampleNumber, '') AS SampleNumber,
                    ISNULL(s.SampleType, '') AS SampleType,
                    COALESCE(NULLIF(s.PointCodeSnapshot,N''),wp.PointCode, sp.PointCode, '') AS PointCode,
                    COALESCE(NULLIF(s.PointLocationSnapshot,N''),wp.Location, sp.Location, '') AS Location,
                    s.SamplingDateTime,
                    ISNULL(s.SampledBy, '') AS SampledBy,
                    ISNULL(s.Status, '') AS CurrentStatus,
                    s.CreatedDate,
                    ISNULL(s.Activity, '') AS Activity,
                    ISNULL(s.RejectionReason, '') AS RejectionReason
                FROM Samples s
                LEFT JOIN WaterSamplingPoints wp ON s.PointID = wp.Id
                LEFT JOIN SamplingPoints sp ON s.PointID = sp.PointID
                WHERE s.SampleID = @sampleId";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", _sampleId)
            };

            _sampleHeader = DatabaseHelper.ExecuteQuery(query, pars);

            if (_sampleHeader.Rows.Count == 0)
                return;

            DataRow row = _sampleHeader.Rows[0];
            _sampleNumber = GetCellText(row, "SampleNumber");

            txtSampleNumber.Text = Dash(GetCellText(row, "SampleNumber"));
            txtSampleType.Text = Dash(GetCellText(row, "SampleType"));
            txtPointCode.Text = Dash(GetCellText(row, "PointCode"));
            txtLocation.Text = Dash(GetCellText(row, "Location"));
            txtSamplingDateTime.Text = FormatDateTime(row["SamplingDateTime"]);
            txtSampledBy.Text = Dash(GetCellText(row, "SampledBy"));
            txtCurrentStatus.Text = Dash(GetCellText(row, "CurrentStatus"));
            txtCreatedDate.Text = FormatDateTime(row["CreatedDate"]);

            string remarks = GetCellText(row, "Activity");
            if (string.IsNullOrWhiteSpace(remarks))
                remarks = GetCellText(row, "RejectionReason");
            txtRemarks.Text = Dash(remarks);

            txtHeaderSubtitle.Text = "Sample No: " + Dash(_sampleNumber);
            Title = "Sample Details - " + Dash(_sampleNumber);
        }

        private void LoadTestsAndResults()
        {
            string query = @"
                SELECT
                    COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'') AS TestName,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END AS TestCategory,
                    CASE
                        WHEN st.ResultValue IS NULL OR LTRIM(RTRIM(CONVERT(nvarchar(100), st.ResultValue))) = '' THEN '-'
                        ELSE CONVERT(nvarchar(100), st.ResultValue)
                    END AS ResultValue,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.UnitSnapshot,N'') ELSE ISNULL(t.Unit,N'') END AS Unit,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit,
                    CASE
                        WHEN st.SampleTestID IS NULL THEN 'Pending'
                        WHEN UPPER(ISNULL(st.ResultStatus, '')) IN ('PASS', 'ALERT', 'OOS', 'ACTION', 'FAIL') THEN
                            CASE
                                WHEN UPPER(st.ResultStatus) IN ('OOS', 'ACTION') THEN 'FAIL'
                                ELSE UPPER(st.ResultStatus)
                            END
                        WHEN st.ResultValue IS NULL THEN 'Pending'
                        WHEN (CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END) IS NOT NULL
                             AND st.ResultValue > (CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END) THEN 'FAIL'
                        WHEN (CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END) IS NOT NULL
                             AND st.ResultValue > (CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END) THEN 'ALERT'
                        ELSE 'PASS'
                    END AS ResultStatus,
                    ISNULL(st.EnteredBy, ISNULL(st.ResultEnteredBy, '')) AS EnteredBy,
                    COALESCE(st.EnteredDate, st.ResultEnteredDate) AS EnteredDate,
                    ISNULL(st.Remarks, '') AS Remarks
                FROM SampleTests st
                LEFT JOIN Tests t ON st.TestID = t.TestID
                WHERE st.SampleID = @sampleId
                ORDER BY CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN ISNULL(st.TestCategorySnapshot,N'') ELSE ISNULL(t.TestCategory,N'') END,
                         COALESCE(NULLIF(st.TestNameSnapshot,N''),t.TestName,N'')";

            dgTests.ItemsSource = SafeQuery(query, new SqlParameter("@sampleId", _sampleId)).DefaultView;
        }

        private void LoadTimeline()
        {
            if (TableExists("SampleStatusHistory"))
            {
                string query = @"
                    SELECT
                        ISNULL(Status, ISNULL(NewStatus, '')) AS Status,
                        ISNULL(ChangedBy, ISNULL(PerformedBy, '')) AS PerformedBy,
                        COALESCE(ChangedDate, PerformedAt, CreatedDate) AS PerformedAt,
                        ISNULL(Remarks, ISNULL(Reason, '')) AS Remarks
                    FROM SampleStatusHistory
                    WHERE SampleID = @sampleId
                    ORDER BY COALESCE(ChangedDate, PerformedAt, CreatedDate) ASC";

                dgTimeline.ItemsSource = SafeQuery(query, new SqlParameter("@sampleId", _sampleId)).DefaultView;
                return;
            }

            DataTable fallback = new DataTable();
            fallback.Columns.Add("Status");
            fallback.Columns.Add("PerformedBy");
            fallback.Columns.Add("PerformedAt");
            fallback.Columns.Add("Remarks");

            if (_sampleHeader != null && _sampleHeader.Rows.Count > 0)
            {
                DataRow h = _sampleHeader.Rows[0];
                fallback.Rows.Add(
                    GetCellText(h, "CurrentStatus"),
                    GetCellText(h, "SampledBy"),
                    FormatDateTime(h["CreatedDate"]),
                    "Current sample status from Samples table");
            }

            dgTimeline.ItemsSource = fallback.DefaultView;
        }

        private void LoadInvestigations()
        {
            DataTable dt = new DataTable();
            dt.Columns.Add("InvestigationNo");
            dt.Columns.Add("RelatedTest");
            dt.Columns.Add("DeviationType");
            dt.Columns.Add("Status");
            dt.Columns.Add("OpenedBy");
            dt.Columns.Add("OpenedDate");
            dt.Columns.Add("ClosedBy");
            dt.Columns.Add("ClosedDate");
            dt.Columns.Add("RequiredAction");

            string[] possibleTables = { "QualityEvents", "QualityEventInvestigations", "Investigations", "SampleInvestigations" };
            string existingTable = possibleTables.FirstOrDefault(TableExists);

            if (!string.IsNullOrWhiteSpace(existingTable))
            {
                try
                {
                    string query = BuildInvestigationQuery(existingTable);
                    dt = DatabaseHelper.ExecuteQuery(query, new[] { new SqlParameter("@sampleId", _sampleId) });
                }
                catch
                {
                    // Keep empty grid if investigation schema is different.
                }
            }

            dgInvestigations.ItemsSource = dt.DefaultView;
        }

        private string BuildInvestigationQuery(string tableName)
        {
            return @"
                SELECT
                    COALESCE(CONVERT(nvarchar(100), InvestigationNo), CONVERT(nvarchar(100), EventNo), CONVERT(nvarchar(100), QualityEventNo), CONVERT(nvarchar(100), ID), '') AS InvestigationNo,
                    ISNULL(RelatedTest, ISNULL(TestName, '')) AS RelatedTest,
                    ISNULL(DeviationType, ISNULL(EventType, '')) AS DeviationType,
                    ISNULL(Status, '') AS Status,
                    ISNULL(OpenedBy, ISNULL(CreatedBy, '')) AS OpenedBy,
                    COALESCE(OpenedDate, CreatedDate) AS OpenedDate,
                    ISNULL(ClosedBy, '') AS ClosedBy,
                    ClosedDate,
                    ISNULL(RequiredAction, ISNULL(CAPA, '')) AS RequiredAction
                FROM " + tableName + @"
                WHERE SampleID = @sampleId
                ORDER BY COALESCE(OpenedDate, CreatedDate) DESC";
        }

        private void LoadCOA()
        {
            if (!TableExists("Certificates"))
            {
                dgCOA.ItemsSource = EmptyTable("CertificateNumber", "CertificateStatus", "RevisionNo", "IssueDate", "IssuedBy", "IsCancelled", "CancellationReason").DefaultView;
                return;
            }

            string query = @"
                SELECT
                    ISNULL(CertificateNumber, '') AS CertificateNumber,
                    ISNULL(CertificateStatus, ISNULL(Status, '')) AS CertificateStatus,
                    ISNULL(RevisionNo, 0) AS RevisionNo,
                    IssueDate,
                    ISNULL(IssuedBy, '') AS IssuedBy,
                    ISNULL(IsCancelled, 0) AS IsCancelled,
                    ISNULL(CancellationReason, '') AS CancellationReason
                FROM Certificates
                WHERE SampleID = @sampleId
                ORDER BY IssueDate DESC, CertificateID DESC";

            dgCOA.ItemsSource = SafeQuery(query, new SqlParameter("@sampleId", _sampleId)).DefaultView;
        }

        private void LoadAuditTrail()
        {
            if (!TableExists("AuditTrail"))
            {
                dgAuditTrail.ItemsSource = EmptyTable("ActionType", "FieldName", "OldValue", "NewValue", "Reason", "PerformedBy", "PerformedAt", "ComputerName").DefaultView;
                return;
            }

            string query = @"
                SELECT
                    ISNULL(ActionType, ISNULL(Action, '')) AS ActionType,
                    ISNULL(FieldName, '') AS FieldName,
                    ISNULL(OldValue, '') AS OldValue,
                    ISNULL(NewValue, '') AS NewValue,
                    ISNULL(Reason, ISNULL(Comments, '')) AS Reason,
                    ISNULL(PerformedBy, ISNULL(UserName, '')) AS PerformedBy,
                    COALESCE(PerformedAt, ActionDate, CreatedDate) AS PerformedAt,
                    ISNULL(ComputerName, ISNULL(WorkstationName, '')) AS ComputerName
                FROM AuditTrail
                WHERE SampleID = @sampleId
                   OR RecordID = @sampleId
                   OR ISNULL(RecordKey, '') = @sampleNumber
                ORDER BY COALESCE(PerformedAt, ActionDate, CreatedDate) DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", _sampleId),
                new SqlParameter("@sampleNumber", _sampleNumber ?? "")
            };

            dgAuditTrail.ItemsSource = SafeQuery(query, pars).DefaultView;
        }

        private void LoadSignatures()
        {
            if (!TableExists("ElectronicSignatures"))
            {
                dgSignatures.ItemsSource = EmptyTable("ActionType", "MeaningOfSignature", "SignedBy", "UserRole", "SignedAt", "ActionReason").DefaultView;
                return;
            }

            string query = @"
                SELECT
                    ISNULL(ActionType, '') AS ActionType,
                    ISNULL(MeaningOfSignature, '') AS MeaningOfSignature,
                    ISNULL(SignedBy, '') AS SignedBy,
                    ISNULL(UserRole, '') AS UserRole,
                    SignedAt,
                    ISNULL(ActionReason, ISNULL(Reason, '')) AS ActionReason
                FROM ElectronicSignatures
                WHERE SampleID = @sampleId
                   OR RecordID = @sampleId
                   OR ISNULL(RecordKey, '') = @sampleNumber
                ORDER BY SignedAt DESC";

            SqlParameter[] pars =
            {
                new SqlParameter("@sampleId", _sampleId),
                new SqlParameter("@sampleNumber", _sampleNumber ?? "")
            };

            dgSignatures.ItemsSource = SafeQuery(query, pars).DefaultView;
        }

        public static SampleSituation EvaluateSampleSituation(int sampleId)
        {
            SampleSituation situation = new SampleSituation
            {
                SampleID = sampleId,
                CurrentStage = "Unknown",
                COAStatusText = "Not Issued",
                InvestigationStatusText = "Not Required",
                NextAction = "Review sample workflow status."
            };

            if (sampleId <= 0)
                return situation;

            DataTable sample = DatabaseHelper.ExecuteQuery(@"
                SELECT TOP 1
                    SampleID,
                    ISNULL(SampleNumber, '') AS SampleNumber,
                    ISNULL(Status, '') AS Status,
                    ReviewedBy,
                    ReviewDate,
                    ApprovedBy,
                    ApprovalDate
                FROM Samples
                WHERE SampleID = @sampleId",
                new[] { new SqlParameter("@sampleId", sampleId) });

            if (sample.Rows.Count == 0)
                return situation;

            DataRow s = sample.Rows[0];
            situation.SampleNumber = GetCellTextStatic(s, "SampleNumber");
            string sampleStatus = GetCellTextStatic(s, "Status");
            situation.CurrentStage = string.IsNullOrWhiteSpace(sampleStatus) ? "Registered" : sampleStatus;
            situation.IsReviewed = !IsDbNullOrEmpty(s, "ReviewedBy") || !IsDbNullOrEmpty(s, "ReviewDate") || sampleStatus.Equals("Reviewed", StringComparison.OrdinalIgnoreCase) || sampleStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase) || sampleStatus.Equals("COA Issued", StringComparison.OrdinalIgnoreCase);
            situation.IsApproved = !IsDbNullOrEmpty(s, "ApprovedBy") || !IsDbNullOrEmpty(s, "ApprovalDate") || sampleStatus.Equals("Approved", StringComparison.OrdinalIgnoreCase) || sampleStatus.Equals("COA Issued", StringComparison.OrdinalIgnoreCase);

            DataTable tests = DatabaseHelper.ExecuteQuery(@"
                SELECT
                    st.SampleTestID,
                    st.ResultValue,
                    st.ResultStatus,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.AlertLimitSnapshot ELSE t.AlertLimit END AS AlertLimit,
                    CASE WHEN NULLIF(st.TestNameSnapshot,N'') IS NOT NULL THEN st.ActionLimitSnapshot ELSE t.ActionLimit END AS ActionLimit
                FROM SampleTests st
                LEFT JOIN Tests t ON st.TestID = t.TestID
                WHERE st.SampleID = @sampleId",
                new[] { new SqlParameter("@sampleId", sampleId) });

            situation.TotalTests = tests.Rows.Count;

            foreach (DataRow row in tests.Rows)
            {
                string status = GetCellTextStatic(row, "ResultStatus").Trim().ToUpperInvariant();
                bool hasResult = !IsDbNullOrEmpty(row, "ResultValue");

                if (!hasResult)
                {
                    situation.PendingResults++;
                    continue;
                }

                situation.CompletedResults++;

                if (status == "OOS" || status == "ACTION" || status == "FAIL")
                {
                    situation.FailResults++;
                    continue;
                }

                if (status == "ALERT")
                {
                    situation.AlertResults++;
                    continue;
                }

                if (TryGetDoubleStatic(row["ResultValue"], out double value))
                {
                    if (TryGetDoubleStatic(row["ActionLimit"], out double actionLimit) && value > actionLimit)
                        situation.FailResults++;
                    else if (TryGetDoubleStatic(row["AlertLimit"], out double alertLimit) && value > alertLimit)
                        situation.AlertResults++;
                }
            }

            situation.InvestigationRequired = situation.FailResults > 0;
            FillInvestigationSituation(sampleId, situation);
            FillCOASituation(sampleId, situation);
            BuildPermissionsAndNextAction(situation);

            return situation;
        }

        private static void FillInvestigationSituation(int sampleId, SampleSituation situation)
        {
            try
            {
                DataTable dt;
                string statusColumn;

                if (TableExistsStatic("QualityEvents"))
                {
                    // SampleDetails is used for dbo.Samples records (PRM is routed to its own
                    // PRM results window). Current Quality Events normally retain SampleID; the
                    // source link is kept as a compatibility path for water/sample events.
                    dt = DatabaseHelper.ExecuteQuery(@"
SELECT CurrentStatus AS InvestigationStatus
FROM dbo.QualityEvents
WHERE
    SampleID = @sampleId
    OR
    (
        SourceRecordID = @sampleId
        AND UPPER(LTRIM(RTRIM(ISNULL(SourceModule,N'')))) IN (N'WATER',N'PW',N'PTW',N'SAMPLE')
    )
ORDER BY QualityEventID;", new[] { new SqlParameter("@sampleId", SqlDbType.Int) { Value = sampleId } });
                    statusColumn = "InvestigationStatus";
                }
                else
                {
                    string[] legacyTables = { "QualityEventInvestigations", "Investigations", "SampleInvestigations" };
                    string table = legacyTables.FirstOrDefault(TableExistsStatic);
                    if (string.IsNullOrWhiteSpace(table))
                    {
                        situation.InvestigationStatusText = situation.InvestigationRequired ? "Required" : "Not Required";
                        return;
                    }

                    dt = DatabaseHelper.ExecuteQuery(
                        "SELECT Status AS InvestigationStatus FROM dbo.[" + table.Replace("]", "]]", StringComparison.Ordinal) + "] WHERE SampleID = @sampleId",
                        new[] { new SqlParameter("@sampleId", SqlDbType.Int) { Value = sampleId } });
                    statusColumn = "InvestigationStatus";
                }

                if (dt.Rows.Count == 0)
                {
                    situation.InvestigationStatusText = situation.InvestigationRequired ? "Required" : "Not Required";
                    return;
                }

                foreach (DataRow row in dt.Rows)
                {
                    string status = GetCellTextStatic(row, statusColumn).Trim();
                    bool isClosed =
                        status.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("QA Closed", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                        status.Equals("Rejected Closed", StringComparison.OrdinalIgnoreCase);

                    if (isClosed)
                        situation.HasClosedInvestigation = true;
                    else
                        situation.HasOpenInvestigation = true; // Unknown/legacy states fail safe as open.
                }

                if (situation.HasOpenInvestigation)
                    situation.InvestigationStatusText = "Open";
                else if (situation.HasClosedInvestigation)
                    situation.InvestigationStatusText = "Closed";
                else
                    situation.InvestigationStatusText = situation.InvestigationRequired ? "Required" : "Not Required";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Sample Details investigation query failed for SampleId={sampleId}.", ex);
                // A failed investigation lookup must not enable review/approval/issuance actions.
                situation.HasOpenInvestigation = true;
                situation.InvestigationStatusText = "Verification unavailable - treated as open";
            }
        }

        private static void FillCOASituation(int sampleId, SampleSituation situation)
        {
            if (!TableExistsStatic("Certificates"))
            {
                ApplicationLogger.Warning($"Sample Details certificate state could not be verified for SampleId={sampleId}: Certificates table is unavailable.");
                situation.COAStatusText = "Verification unavailable";
                return;
            }

            try
            {
                DataTable dt = DatabaseHelper.ExecuteQuery(@"
                    SELECT
                        ISNULL(CertificateStatus, ISNULL(Status, '')) AS CertificateStatus,
                        ISNULL(IsCancelled, 0) AS IsCancelled
                    FROM Certificates
                    WHERE SampleID = @sampleId",
                    new[] { new SqlParameter("@sampleId", sampleId) });

                foreach (DataRow row in dt.Rows)
                {
                    string status = GetCellTextStatic(row, "CertificateStatus").Trim().ToUpperInvariant();
                    bool cancelled = false;
                    try { cancelled = Convert.ToBoolean(row["IsCancelled"]); } catch { cancelled = false; }

                    if (!cancelled && (status == "ACTIVE" || status == "ISSUED" || status == "COA ISSUED"))
                        situation.HasActiveCOA = true;

                    if (cancelled || status == "CANCELLED")
                        situation.HasCancelledCOA = true;
                }

                if (situation.HasActiveCOA)
                    situation.COAStatusText = "Active";
                else if (situation.HasCancelledCOA)
                    situation.COAStatusText = "Cancelled";
                else
                    situation.COAStatusText = "Not Issued";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Sample Details certificate query failed for SampleId={sampleId}.", ex);
                situation.COAStatusText = "Verification unavailable";
            }
        }

        private static void BuildPermissionsAndNextAction(SampleSituation s)
        {
            if (string.Equals(s.COAStatusText, "Verification unavailable", StringComparison.OrdinalIgnoreCase))
            {
                s.CanEnterResults = false;
                s.CanSubmitForReview = false;
                s.CanApprove = false;
                s.CanIssueCOA = false;
                s.NextAction = "Certificate state verification unavailable. Run System Preflight / Database Maintenance and reload the sample before controlled actions.";
                return;
            }

            if (s.HasActiveCOA)
            {
                s.CanEnterResults = false;
                s.CanSubmitForReview = false;
                s.CanApprove = false;
                s.CanIssueCOA = false;
                s.NextAction = "COA already issued. Direct editing is blocked.";
                return;
            }

            s.CanEnterResults = !s.IsApproved && !s.HasActiveCOA;

            if (s.TotalTests == 0)
            {
                s.NextAction = "Assign tests.";
                return;
            }

            if (s.PendingResults > 0)
            {
                s.NextAction = "Complete pending results.";
                return;
            }

            if (s.FailResults > 0 && !s.HasOpenInvestigation && !s.HasClosedInvestigation)
            {
                s.InvestigationRequired = true;
                s.InvestigationStatusText = "Required";
                s.NextAction = "Open investigation for FAIL/OOS result.";
                return;
            }

            if (s.HasOpenInvestigation)
            {
                s.NextAction = "Close open investigation before approval.";
                return;
            }

            if (!s.IsReviewed)
            {
                s.CanSubmitForReview = true;
                s.NextAction = "Submit for review.";
                return;
            }

            if (s.IsReviewed && !s.IsApproved)
            {
                s.CanApprove = true;
                s.NextAction = "QA approval required.";
                return;
            }

            if (s.IsApproved && !s.HasActiveCOA)
            {
                s.CanIssueCOA = true;
                s.NextAction = "Issue COA.";
                return;
            }

            s.NextAction = "Review sample workflow status.";
        }

        private void ApplySituationToUi(SampleSituation situation)
        {
            if (situation == null)
                return;

            txtStage.Text = Dash(situation.CurrentStage);
            txtCurrentStatus.Text = Dash(situation.CurrentStage);
            txtTotalTests.Text = situation.TotalTests.ToString(CultureInfo.InvariantCulture);
            txtCompleted.Text = situation.CompletedResults.ToString(CultureInfo.InvariantCulture);
            txtPending.Text = situation.PendingResults.ToString(CultureInfo.InvariantCulture);
            txtAlertFail.Text = situation.AlertResults.ToString(CultureInfo.InvariantCulture) + " / " + situation.FailResults.ToString(CultureInfo.InvariantCulture);
            txtInvestigationStatus.Text = Dash(situation.InvestigationStatusText);
            txtCOAStatus.Text = Dash(situation.COAStatusText);
            txtNextAction.Text = Dash(situation.NextAction);
        }

        private void ApplyActionButtonStates(SampleSituation situation)
        {
            if (situation == null)
                return;

            btnEnterResults.IsEnabled = situation.CanEnterResults;
            btnOpenInvestigation.IsEnabled = situation.InvestigationRequired || situation.HasOpenInvestigation || situation.HasClosedInvestigation;
            btnViewCOA.IsEnabled = situation.IsApproved || situation.HasActiveCOA || situation.HasCancelledCOA;
        }

        private void OpenWindowByTypeName(string typeName, int sampleId, string sampleNumber, bool trySampleIdConstructor)
        {
            if (!TryOpenWindowByTypeName(typeName, sampleId, sampleNumber, trySampleIdConstructor))
                ShowInfo("The target window was not found:\n" + typeName);
        }

        private bool TryOpenWindowByTypeName(string typeName, int sampleId, string sampleNumber, bool trySampleIdConstructor)
        {
            try
            {
                Type windowType = Assembly.GetExecutingAssembly().GetType(typeName);
                if (windowType == null)
                    return false;

                object instance = null;

                if (trySampleIdConstructor && sampleId > 0)
                {
                    ConstructorInfo intConstructor = windowType.GetConstructor(new[] { typeof(int) });
                    if (intConstructor != null)
                        instance = intConstructor.Invoke(new object[] { sampleId });
                }

                if (instance == null && !string.IsNullOrWhiteSpace(sampleNumber))
                {
                    ConstructorInfo stringConstructor = windowType.GetConstructor(new[] { typeof(string) });
                    if (stringConstructor != null)
                        instance = stringConstructor.Invoke(new object[] { sampleNumber });
                }

                instance ??= Activator.CreateInstance(windowType);

                if (instance is Window window)
                {
                    window.Owner = this;
                    window.ShowDialog();
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                ShowError("Could not open target window:\n\n" + Infrastructure.UserFacingError.SafeMessage(ex));
                return true;
            }
        }

        private DataTable SafeQuery(string query, params SqlParameter[] parameters)
        {
            try
            {
                return DatabaseHelper.ExecuteQuery(query, parameters);
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Sample Details query failed for SampleId={_sampleId}.", ex);
                return new DataTable();
            }
        }

        private static bool TableExistsStatic(string tableName)
        {
            try
            {
                DataTable dt = DatabaseHelper.ExecuteQuery(@"
                    SELECT COUNT(*) AS Cnt
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_NAME = @tableName",
                    new[] { new SqlParameter("@tableName", tableName) });

                return dt.Rows.Count > 0 && Convert.ToInt32(dt.Rows[0]["Cnt"]) > 0;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning($"Unable to verify whether table '{tableName}' exists.", ex);
                return false;
            }
        }

        private bool TableExists(string tableName)
        {
            return TableExistsStatic(tableName);
        }

        private DataTable EmptyTable(params string[] columns)
        {
            DataTable dt = new DataTable();
            foreach (string col in columns)
                dt.Columns.Add(col);
            return dt;
        }

        private static string GetCellTextStatic(DataRow row, string columnName)
        {
            if (row == null || row.Table == null || !row.Table.Columns.Contains(columnName))
                return "";

            object value = row[columnName];
            if (value == null || value == DBNull.Value)
                return "";

            return value.ToString() ?? "";
        }

        private string GetCellText(DataRow row, string columnName)
        {
            return GetCellTextStatic(row, columnName);
        }

        private static bool IsDbNullOrEmpty(DataRow row, string columnName)
        {
            return string.IsNullOrWhiteSpace(GetCellTextStatic(row, columnName));
        }

        private static int SafeInt(object value)
        {
            if (value == null || value == DBNull.Value)
                return 0;

            return int.TryParse(value.ToString(), out int result) ? result : 0;
        }

        private static bool TryGetDoubleStatic(object value, out double result)
        {
            result = 0;

            if (value == null || value == DBNull.Value)
                return false;

            string text = value.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            text = text.Replace(",", ".");

            if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return false;

            return !double.IsNaN(result) && !double.IsInfinity(result);
        }

        private string FormatDateTime(object value)
        {
            if (value == null || value == DBNull.Value)
                return "-";

            if (DateTime.TryParse(value.ToString(), out DateTime dt))
                return dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            return value.ToString() ?? "-";
        }

        private string Dash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private void SetBusy(bool isBusy, string message)
        {
            lblStatus.Text = message;
            progressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowInfo(string message)
        {
            MessageBox.Show(message, "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ShowErrorStatic(string message)
        {
            MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        private void ShowError(string message)
        {
            ShowErrorStatic(message);
        }
    }
}
