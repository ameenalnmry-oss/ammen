#nullable disable

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using PharmaLIMS.Models;
using PharmaLIMS.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using PharmaLIMS.Infrastructure;

namespace PharmaLIMS
{
    public partial class QualityEventInvestigation : Window
    {
        private readonly IQualityEventService? _qualityEventService;
        private readonly IAuthService? _authService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly int sampleId;
        private readonly string currentUser;
        private readonly string currentRole;
        private int qualityEventId;
        private int loadedSampleId;
        private string sampleNumber = "";
        private bool isReadOnlyMode = false;
        private DataTable checklistTable = new DataTable();
        private DataTable rootCauseWhysTable = new DataTable();
        private DataTable impactAssessmentTable = new DataTable();
        private DataTable capaItemsTable = new DataTable();
        private DataTable retestingTable = new DataTable();
        private DataTable distributionTable = new DataTable();
        private DataTable environmentalMonitoringContextTable = new DataTable();
        private bool currentEventIsEnvironmentalMonitoring;
        private bool isLoadingEvent;
        private string loadedClosedBy = string.Empty;
        private DateTime? loadedClosedDate;
        private DateTime? loadedEventCreatedDate;

        private sealed class QualityEventPaginatorSource : IDocumentPaginatorSource
        {
            public QualityEventPaginatorSource(DocumentPaginator paginator)
            {
                DocumentPaginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
            }

            public DocumentPaginator DocumentPaginator { get; }
        }

        private sealed class QualityEventReportPaginator : DocumentPaginator
        {
            private readonly DocumentPaginator innerPaginator;
            private readonly string verificationReference;
            private readonly string formReference;
            private readonly Brush navyBrush;
            private readonly Brush slateBrush;
            private readonly Pen outerPen;
            private readonly Pen innerPen;
            private readonly ImageSource headerLogo;

            public QualityEventReportPaginator(DocumentPaginator paginator, string verificationReference, string formReference, ImageSource headerLogo)
            {
                innerPaginator = paginator ?? throw new ArgumentNullException(nameof(paginator));
                this.verificationReference = verificationReference ?? string.Empty;
                this.formReference = formReference ?? string.Empty;
                this.headerLogo = headerLogo;
                navyBrush = new SolidColorBrush(Color.FromRgb(20, 66, 99));
                slateBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105));
                outerPen = new Pen(new SolidColorBrush(Color.FromRgb(20, 66, 99)), 1.35);
                innerPen = new Pen(new SolidColorBrush(Color.FromRgb(203, 213, 225)), 0.55);

                if (navyBrush.CanFreeze) navyBrush.Freeze();
                if (slateBrush.CanFreeze) slateBrush.Freeze();
                if (outerPen.CanFreeze) outerPen.Freeze();
                if (innerPen.CanFreeze) innerPen.Freeze();
            }

            public override bool IsPageCountValid => innerPaginator.IsPageCountValid;
            public override int PageCount => innerPaginator.PageCount;
            public override IDocumentPaginatorSource Source => innerPaginator.Source;

            public override Size PageSize
            {
                get => innerPaginator.PageSize;
                set => innerPaginator.PageSize = value;
            }

            public override DocumentPage GetPage(int pageNumber)
            {
                DocumentPage page = innerPaginator.GetPage(pageNumber);
                if (page == DocumentPage.Missing)
                    return page;

                ContainerVisual root = new ContainerVisual();
                DrawingVisual background = new DrawingVisual();
                using (DrawingContext dc = background.RenderOpen())
                    dc.DrawRectangle(Brushes.White, null, new Rect(new Point(0, 0), page.Size));

                root.Children.Add(background);
                root.Children.Add(page.Visual);

                DrawingVisual decoration = new DrawingVisual();
                using (DrawingContext dc = decoration.RenderOpen())
                {
                    Rect outer = new Rect(8, 8, Math.Max(0, page.Size.Width - 16), Math.Max(0, page.Size.Height - 16));
                    Rect inner = new Rect(12, 12, Math.Max(0, page.Size.Width - 24), Math.Max(0, page.Size.Height - 24));
                    dc.DrawRectangle(null, outerPen, outer);
                    dc.DrawRectangle(null, innerPen, inner);

                    double runningHeaderX = 24;
                    if (headerLogo != null)
                    {
                        dc.DrawImage(headerLogo, new Rect(24, 13.5, 44, 19));
                        runningHeaderX = 75;
                    }

                    FormattedText runningHeader = new FormattedText(
                        "MEDICA PHARMACEUTICAL INDUSTRY  |  Microbiology Department",
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                        7.6,
                        navyBrush,
                        1.0);
                    dc.DrawText(runningHeader, new Point(runningHeaderX, 16));

                    FormattedText formText = new FormattedText(
                        formReference,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        7.2,
                        slateBrush,
                        1.0);
                    dc.DrawText(formText, new Point(Math.Max(24, page.Size.Width - formText.Width - 24), 16));

                    string pageText = "Page " + (pageNumber + 1).ToString(CultureInfo.InvariantCulture) +
                                      " of " + PageCount.ToString(CultureInfo.InvariantCulture);
                    FormattedText pageNumberText = new FormattedText(
                        pageText,
                        CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        7.5,
                        slateBrush,
                        1.0);
                    dc.DrawText(pageNumberText, new Point(page.Size.Width - pageNumberText.Width - 24, page.Size.Height - 27));

                    if (!string.IsNullOrWhiteSpace(verificationReference))
                    {
                        FormattedText verificationText = new FormattedText(
                            "Verification: " + verificationReference,
                            CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"),
                            6.9,
                            slateBrush,
                            1.0);
                        dc.DrawText(verificationText, new Point(24, page.Size.Height - 27));
                    }
                }

                root.Children.Add(decoration);
                return new DocumentPage(root, page.Size, page.BleedBox, page.ContentBox);
            }
        }

        public QualityEventInvestigation(int sampleId, string currentUser, string currentRole)
        {
            InitializeComponent();
            this.sampleId = sampleId;
            this.loadedSampleId = sampleId;
            this.currentUser = ResolveCurrentUser(currentUser);
            this.currentRole = string.IsNullOrWhiteSpace(currentRole) ? "" : currentRole;

            _serviceProvider = App.ServiceProvider;
            if (_serviceProvider != null)
            {
                _qualityEventService = _serviceProvider.GetRequiredService<IQualityEventService>();
                _authService = _serviceProvider.GetRequiredService<IAuthService>();
            }

            Loaded += QualityEventInvestigation_Loaded;
        }

        public QualityEventInvestigation(int qualityEventId)
        {
            InitializeComponent();
            this.sampleId = 0;
            this.loadedSampleId = 0;
            this.qualityEventId = qualityEventId;

            _serviceProvider = App.ServiceProvider;
            if (_serviceProvider != null)
            {
                _qualityEventService = _serviceProvider.GetRequiredService<IQualityEventService>();
                _authService = _serviceProvider.GetRequiredService<IAuthService>();
            }

            var user = _authService?.GetCurrentUser();
            this.currentUser = ResolveCurrentUser(user?.Username ?? user?.FullName);
            this.currentRole = user?.Role ?? Login.CurrentUserRole ?? "";

            Loaded += QualityEventInvestigation_Loaded;
        }

        public QualityEventInvestigation()
        {
            InitializeComponent();
            _serviceProvider = App.ServiceProvider;
            if (_serviceProvider != null)
            {
                _qualityEventService = _serviceProvider.GetRequiredService<IQualityEventService>();
                _authService = _serviceProvider.GetRequiredService<IAuthService>();
            }
            currentUser = ResolveCurrentUser(null);
            currentRole = "";
            sampleId = 0;
            loadedSampleId = 0;
        }

        private async void QualityEventInvestigation_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= QualityEventInvestigation_Loaded;
            await LoadEventAsync();
        }

        private static string ResolveCurrentUser(string requestedUser)
        {
            if (!string.IsNullOrWhiteSpace(Login.CurrentUser))
                return Login.CurrentUser.Trim();

            throw new InvalidOperationException("An authenticated PharmaLIMS account is required to work with quality-event records.");
        }

        private DataTable GetFilteredChecklist()
        {
            if (qualityEventId <= 0)
                return CreateChecklistSchema();

            DataTable table = DatabaseHelper.GetQualityEventChecklist(qualityEventId);

            if (table == null)
                return CreateChecklistSchema();

            EnsureChecklistColumn(table, "QuestionID", typeof(int));
            EnsureChecklistColumn(table, "SectionName", typeof(string));
            EnsureChecklistColumn(table, "QuestionText", typeof(string));
            EnsureChecklistColumn(table, "InvestigationProfile", typeof(string));
            EnsureChecklistColumn(table, "QuestionLogic", typeof(string));
            EnsureChecklistColumn(table, "ExpectedAnswer", typeof(string));
            EnsureChecklistColumn(table, "AnswerType", typeof(string));
            EnsureChecklistColumn(table, "IsRequired", typeof(bool));
            EnsureChecklistColumn(table, "SortOrder", typeof(int));
            EnsureChecklistColumn(table, "AnswerValue", typeof(string));
            EnsureChecklistColumn(table, "Comments", typeof(string));

            return CollapseLogicalChecklistDuplicates(table);
        }

        private DataTable CreateChecklistSchema()
        {
            DataTable table = new DataTable();
            table.Columns.Add("QuestionID", typeof(int));
            table.Columns.Add("SectionName", typeof(string));
            table.Columns.Add("QuestionText", typeof(string));
            table.Columns.Add("InvestigationProfile", typeof(string));
            table.Columns.Add("QuestionLogic", typeof(string));
            table.Columns.Add("ExpectedAnswer", typeof(string));
            table.Columns.Add("AnswerType", typeof(string));
            table.Columns.Add("IsRequired", typeof(bool));
            table.Columns.Add("SortOrder", typeof(int));
            table.Columns.Add("AnswerValue", typeof(string));
            table.Columns.Add("Comments", typeof(string));
            return table;
        }

        private void EnsureChecklistColumn(DataTable table, string columnName, Type columnType)
        {
            if (table != null && !table.Columns.Contains(columnName))
                table.Columns.Add(columnName, columnType);
        }

        private DataTable CollapseLogicalChecklistDuplicates(DataTable source)
        {
            if (source == null || source.Rows.Count <= 1)
                return source ?? CreateChecklistSchema();

            Dictionary<string, DataRow> selected = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
            int anonymousIndex = 0;

            foreach (DataRow row in source.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string key = BuildChecklistLogicalKey(row);
                if (string.IsNullOrWhiteSpace(key))
                    key = "__ROW_" + (++anonymousIndex).ToString();

                if (!selected.TryGetValue(key, out DataRow existing))
                {
                    selected[key] = row;
                    continue;
                }

                int rowScore = ChecklistRowPreferenceScore(row);
                int existingScore = ChecklistRowPreferenceScore(existing);
                if (rowScore > existingScore ||
                    (rowScore == existingScore && GetChecklistQuestionId(row) < GetChecklistQuestionId(existing)))
                {
                    selected[key] = row;
                }
            }

            if (selected.Count == source.Rows.Count)
                return source;

            DataTable collapsed = source.Clone();
            foreach (DataRow row in selected.Values)
                collapsed.ImportRow(row);

            if (collapsed.Columns.Contains("SortOrder") && collapsed.Columns.Contains("QuestionID"))
            {
                DataView ordered = collapsed.DefaultView;
                ordered.Sort = "SortOrder ASC, QuestionID ASC";
                collapsed = ordered.ToTable();
            }

            ApplicationLogger.Warning(
                $"Collapsed {source.Rows.Count - collapsed.Rows.Count} duplicate Quality Event checklist definition(s) for QualityEventId={qualityEventId}. " +
                "Saved answers were preferred and no database history was deleted.");

            return collapsed;
        }

        private static string NormalizeChecklistIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string normalized = value.Normalize(NormalizationForm.FormKC)
                .Replace('\u00A0', ' ')
                .Replace("\u200B", "")
                .Replace("\uFEFF", "");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+([?.,;:!])", "$1");
            return normalized.ToUpperInvariant();
        }

        private static string BuildChecklistLogicalKey(DataRow row)
        {
            if (row == null)
                return "";

            string profile = NormalizeChecklistIdentity(row.GetSafeString("InvestigationProfile"));
            int sortOrder = 0;
            if (row.Table.Columns.Contains("SortOrder") && row["SortOrder"] != DBNull.Value)
                int.TryParse(row["SortOrder"].ToString(), out sortOrder);

            // 8010-8310 is the controlled Environmental Monitoring checklist namespace.
            // SortOrder is deliberately stable across releases and therefore survives
            // legacy QuestionID duplication or minor wording/Unicode changes.
            if (profile == "ENVIRONMENTAL MONITORING" && sortOrder >= 8010 && sortOrder <= 8310)
                return "EM|" + sortOrder.ToString();

            return NormalizeChecklistIdentity(row.GetSafeString("SectionName")) + "|" +
                   NormalizeChecklistIdentity(row.GetSafeString("QuestionText"));
        }

        private static int ChecklistRowPreferenceScore(DataRow row)
        {
            int score = 0;
            if (!string.IsNullOrWhiteSpace(row.GetSafeString("AnswerValue")))
                score += 100;
            if (!string.IsNullOrWhiteSpace(row.GetSafeString("Comments")))
                score += 10;
            if (row.Table.Columns.Contains("IsRequired") && row["IsRequired"] != DBNull.Value && Convert.ToBoolean(row["IsRequired"]))
                score += 1;
            return score;
        }

        private static int GetChecklistQuestionId(DataRow row)
        {
            if (row == null || !row.Table.Columns.Contains("QuestionID") || row["QuestionID"] == DBNull.Value)
                return int.MaxValue;

            try
            {
                return Convert.ToInt32(row["QuestionID"]);
            }
            catch
            {
                return int.MaxValue;
            }
        }

        private bool IsCurrentWaterEvent()
        {
            string source = lblDetectionSource?.Text ?? "";
            string sample = sampleNumber ?? "";

            return sample.StartsWith("PW-", StringComparison.OrdinalIgnoreCase) ||
                   sample.StartsWith("PTW-", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("Water", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("Purified", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("Potable", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCurrentEMEvent()
        {
            string eventType = lblEventType?.Text ?? "";
            string source = lblDetectionSource?.Text ?? "";
            string sample = sampleNumber ?? lblSampleNumber?.Text ?? "";

            return eventType.Contains("EM", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("Environmental", StringComparison.OrdinalIgnoreCase) ||
                   sample.StartsWith("EM-", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsEnvironmentalMonitoringHeader(DataRow row)
        {
            if (row == null)
                return false;

            string eventType = row.GetSafeString("EventType");
            string source = row.GetSafeString("DetectionSource");
            string sample = row.GetSafeString("SampleNumber");

            return eventType.Contains("EM", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("Environmental", StringComparison.OrdinalIgnoreCase) ||
                   sample.StartsWith("EM-", StringComparison.OrdinalIgnoreCase);
        }

        private DataTable LoadEnvironmentalMonitoringContext(string eventNumber)
        {
            DataTable empty = new DataTable();
            if (string.IsNullOrWhiteSpace(eventNumber))
                return empty;

            try
            {
                return DatabaseHelper.ExecuteQuery(@"
                    SELECT TOP 1
                        E.EventNo,
                        E.EventDate,
                        ISNULL(P.PlanNo,N'') AS PlanNo,
                        A.AreaCode,
                        A.AreaName,
                        A.AreaGroup,
                        A.Grade,
                        E.MonitoringCategory,
                        E.SamplingStage,
                        E.MediaUsed,
                        E.MediaLotNo,
                        E.SanitizationDetails,
                        E.SanitizationTime,
                        E.DisinfectantUsed,
                        E.SamplingTimeFrom,
                        E.SamplingTimeTo,
                        E.ActivityNoOfPersons,
                        E.AirSamplerNo,
                        E.AirSamplingTime,
                        E.IncubationTemperature,
                        E.IncubatorNo1,
                        E.IncubatorNo2,
                        E.IncubationStart,
                        E.IncubationEnd,
                        E.NegativeControlResult,
                        E.MaterialName,
                        E.BatchNo,
                        E.EmployeeName,
                        E.EmployeeDepartment,
                        E.EmployeeShift,
                        E.SurfaceLocation,
                        E.SurfaceType
                    FROM dbo.EM_Events E
                    INNER JOIN dbo.EM_Areas A ON A.Id = E.AreaId
                    LEFT JOIN dbo.EM_Plans P ON P.PlanID = E.PlanID
                    WHERE E.EventNo = @EventNo;",
                    new[]
                    {
                        new SqlParameter("@EventNo", SqlDbType.NVarChar, 200) { Value = eventNumber.Trim() }
                    });
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning(
                    $"Unable to load Environmental Monitoring context for Quality Event {qualityEventId}, EM Event '{eventNumber}'.",
                    ex);
                return empty;
            }
        }

        private void ApplyInvestigationProfileUI()
        {
            if (!currentEventIsEnvironmentalMonitoring)
                return;

            Title = "Environmental Monitoring Quality Event Investigation";
            txtInvestigationTitle.Text = "ENVIRONMENTAL MONITORING QUALITY EVENT INVESTIGATION";
            txtInvestigationSubtitle.Text = "Alert / Action excursion investigation, impact assessment, CAPA and QA area disposition";
            txtWorkflowSummary.Text = "Event & Context → EM Verification → EM Technical Investigation → Root Cause → Impact → Follow-up Monitoring → CAPA → QA Closure";

            tabClassification.Header = "1. EM Event & Classification";
            tabPhaseI.Header = "2. EM Verification Checklist";
            tabPhaseII.Header = "3. EM Technical Investigation";
            tabRootCause.Header = "4. EM Root Cause";
            tabImpactAssessment.Header = "5. EM Impact Assessment";
            tabRetesting.Header = "6. Follow-up Monitoring";
            tabCAPA.Header = "7. CAPA & Effectiveness";
            tabQAClosure.Header = "8. QA Area Disposition";

            pnlEmContext.Visibility = Visibility.Visible;
            pnlEmRequiredEvidence.Visibility = Visibility.Visible;
            txtPhaseIIScopeTitle.Text = "Environmental Monitoring Technical Review Scope";
            txtPhaseIIScopeText.Text =
                "Review the exact monitoring method and sampling conditions, approved limits, media/GPT and controls, incubation, cleaning/disinfection, facility/HVAC, interventions and personnel, organism identification, adjacent and historical trends, product/material exposure, and follow-up evidence.";
            txtRootCauseGuidance.Text =
                "Determine the confirmed or most probable EM cause using evidence. Distinguish true environmental/facility or personnel contributors from sampling, media, incubation, counting, handling, or other laboratory causes. 'No assignable cause' requires a documented scientific rationale and enhanced follow-up plan.";
            txtImpactGuidance.Text =
                "Complete every EM impact line: monitored area/state of control, adjacent locations, product/material/batch exposure, personnel/aseptic activity, HVAC/facility condition, and historical microbiological trend.";
            txtFollowUpGuidance.Text =
                "Document follow-up EM/resampling with locations, timing, performers, results, acceptance criteria and scientific purpose. A passing repeat result does not erase the original excursion; it is supporting evidence for the investigation and disposition.";
            txtQAClosureGuidance.Text =
                "QA conclusion must state the excursion and affected area/grade, confirmed or most probable cause, organism/trend review where applicable, product/material impact, follow-up monitoring evidence, CAPA/effectiveness plan, and the scientific basis for releasing or restricting the area.";
        }

        private void PopulateEnvironmentalMonitoringContext()
        {
            if (!currentEventIsEnvironmentalMonitoring)
            {
                pnlEmContext.Visibility = Visibility.Collapsed;
                return;
            }

            pnlEmContext.Visibility = Visibility.Visible;
            if (environmentalMonitoringContextTable == null || environmentalMonitoringContextTable.Rows.Count == 0)
            {
                lblEmAreaGrade.Text = "Context not available from EM_Events.";
                lblEmPlanCategory.Text = "Not available";
                lblEmSamplingActivity.Text = "Not available";
                lblEmMediaControls.Text = "Not available";
                lblEmCleaningFacility.Text = "Not available";
                lblEmIncubation.Text = "Not available";
                lblEmProductPersonnel.Text = "Not available";
                return;
            }

            DataRow row = environmentalMonitoringContextTable.Rows[0];
            lblEmAreaGrade.Text = JoinEvidence(
                row.GetSafeString("AreaCode"), row.GetSafeString("AreaName"), row.GetSafeString("AreaGroup"),
                string.IsNullOrWhiteSpace(row.GetSafeString("Grade")) ? "" : "Grade " + row.GetSafeString("Grade"));
            lblEmPlanCategory.Text = JoinEvidence(
                string.IsNullOrWhiteSpace(row.GetSafeString("PlanNo")) ? "" : "Plan " + row.GetSafeString("PlanNo"),
                row.GetSafeString("MonitoringCategory"), row.GetSafeString("SamplingStage"));
            lblEmSamplingActivity.Text = JoinEvidence(
                BuildRangeEvidence("Sampling", row.GetSafeString("SamplingTimeFrom"), row.GetSafeString("SamplingTimeTo")),
                string.IsNullOrWhiteSpace(row.GetSafeString("ActivityNoOfPersons")) ? "" : "Persons: " + row.GetSafeString("ActivityNoOfPersons"),
                string.IsNullOrWhiteSpace(row.GetSafeString("AirSamplerNo")) ? "" : "Air sampler: " + row.GetSafeString("AirSamplerNo"),
                string.IsNullOrWhiteSpace(row.GetSafeString("SurfaceLocation")) ? "" : "Surface: " + row.GetSafeString("SurfaceLocation"));
            lblEmMediaControls.Text = JoinEvidence(
                row.GetSafeString("MediaUsed"),
                string.IsNullOrWhiteSpace(row.GetSafeString("MediaLotNo")) ? "" : "Lot " + row.GetSafeString("MediaLotNo"),
                string.IsNullOrWhiteSpace(row.GetSafeString("NegativeControlResult")) ? "" : "Negative control: " + row.GetSafeString("NegativeControlResult"));
            lblEmCleaningFacility.Text = JoinEvidence(
                row.GetSafeString("SanitizationDetails"),
                string.IsNullOrWhiteSpace(row.GetSafeString("DisinfectantUsed")) ? "" : "Disinfectant: " + row.GetSafeString("DisinfectantUsed"),
                string.IsNullOrWhiteSpace(row.GetSafeString("SanitizationTime")) ? "" : "Sanitization: " + row.GetSafeString("SanitizationTime"));
            lblEmIncubation.Text = JoinEvidence(
                row.GetSafeString("IncubationTemperature"),
                BuildRangeEvidence("Incubation", row.GetSafeString("IncubationStart"), row.GetSafeString("IncubationEnd")),
                JoinEvidence(row.GetSafeString("IncubatorNo1"), row.GetSafeString("IncubatorNo2")));
            lblEmProductPersonnel.Text = JoinEvidence(
                string.IsNullOrWhiteSpace(row.GetSafeString("MaterialName")) ? "" : "Material: " + row.GetSafeString("MaterialName"),
                string.IsNullOrWhiteSpace(row.GetSafeString("BatchNo")) ? "" : "Batch: " + row.GetSafeString("BatchNo"),
                string.IsNullOrWhiteSpace(row.GetSafeString("EmployeeName")) ? "" : "Personnel: " + row.GetSafeString("EmployeeName"),
                row.GetSafeString("EmployeeDepartment"), row.GetSafeString("EmployeeShift"));
        }

        private static string BuildRangeEvidence(string label, string from, string to)
        {
            if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to))
                return "";
            if (string.IsNullOrWhiteSpace(from))
                return label + " to " + to;
            if (string.IsNullOrWhiteSpace(to))
                return label + " from " + from;
            return label + ": " + from + " - " + to;
        }

        private static string JoinEvidence(params string[] values)
        {
            string result = "";
            foreach (string value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                result += (result.Length == 0 ? "" : " | ") + value.Trim();
            }
            return string.IsNullOrWhiteSpace(result) ? "Not documented" : result;
        }

        private void EnsureEnvironmentalMonitoringStructuredDefaults()
        {
            if (!currentEventIsEnvironmentalMonitoring)
                return;

            if (!HasMeaningfulRows(rootCauseWhysTable, "Question", "Answer"))
            {
                rootCauseWhysTable.Rows.Clear();
                rootCauseWhysTable.Rows.Add(0, 1, "Why did the EM result exceed the approved alert/action limit or expected state?", "");
                rootCauseWhysTable.Rows.Add(0, 2, "Why did the identified contributing condition occur at the monitored area, activity, sampling, or laboratory step?", "");
                rootCauseWhysTable.Rows.Add(0, 3, "Why was that condition not prevented or detected by the existing controls before the excursion?", "");
                rootCauseWhysTable.Rows.Add(0, 4, "Why did procedure, training, facility, cleaning, monitoring, or supervisory controls fail to prevent recurrence?", "");
                rootCauseWhysTable.Rows.Add(0, 5, "What systemic cause or control weakness must be corrected to prevent recurrence?", "");
            }

            if (!HasMeaningfulRows(impactAssessmentTable, "Finding", "ImpactStatus"))
            {
                impactAssessmentTable.Rows.Clear();
                impactAssessmentTable.Rows.Add(0, "Monitored Area / State of Control", "", "");
                impactAssessmentTable.Rows.Add(0, "Adjacent / Related EM Locations", "", "");
                impactAssessmentTable.Rows.Add(0, "Product / Material / Batch Exposure", "", "");
                impactAssessmentTable.Rows.Add(0, "Personnel / Aseptic Activity", "", "");
                impactAssessmentTable.Rows.Add(0, "HVAC / Facility / Utilities", "", "");
                impactAssessmentTable.Rows.Add(0, "Historical Trend / Recurrent Flora", "", "");
            }
        }

        private bool HasMeaningfulRows(DataTable table, params string[] columns)
        {
            if (table == null)
                return false;

            foreach (DataRow row in table.Rows)
            {
                if (row.RowState != DataRowState.Deleted && HasAnyValue(row, columns))
                    return true;
            }
            return false;
        }

        private async Task LoadEventAsync()
        {
            if (isLoadingEvent) return;
            try
            {
                isLoadingEvent = true;
                IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                if (qualityEventId <= 0 && sampleId > 0)
                {
                    if (_qualityEventService != null)
                    {
                        var openEvent = await _qualityEventService.GetOpenQualityEventBySampleIdAsync(sampleId);
                        qualityEventId = openEvent != null
                            ? openEvent.QualityEventId
                            : await Task.Run(() => DatabaseHelper.GetLatestQualityEventId(sampleId));
                    }
                    else
                    {
                        qualityEventId = await Task.Run(() =>
                        {
                            int eventId = DatabaseHelper.GetOpenQualityEventId(sampleId);
                            return eventId > 0 ? eventId : DatabaseHelper.GetLatestQualityEventId(sampleId);
                        });
                    }
                }

                if (qualityEventId <= 0)
                {
                    MessageBox.Show("No Quality Event was found.",
                        "Quality Event", MessageBoxButton.OK, MessageBoxImage.Information);
                    Close();
                    return;
                }

                QualityEventLoadData loaded = await Task.Run(() =>
                {
                    DataTable header = DatabaseHelper.GetQualityEventHeader(qualityEventId);
                    if (header.Rows.Count == 0)
                        return null;

                    DataRow headerRow = header.Rows[0];
                    bool isEnvironmentalMonitoring = IsEnvironmentalMonitoringHeader(headerRow);

                    return new QualityEventLoadData
                    {
                        Header = header,
                        AffectedResults = DatabaseHelper.GetQualityEventAffectedResults(qualityEventId),
                        Actions = DatabaseHelper.GetQualityEventActions(qualityEventId),
                        Checklist = GetFilteredChecklist(),
                        RootCauseWhys = LoadRootCauseWhysTable(),
                        ImpactAssessment = LoadImpactAssessmentTable(),
                        CapaItems = LoadCAPAItemsTable(),
                        Retesting = LoadRetestingTable(),
                        Distribution = LoadDistributionTable(),
                        EnvironmentalMonitoringContext = isEnvironmentalMonitoring
                            ? LoadEnvironmentalMonitoringContext(headerRow.GetSafeString("SampleNumber"))
                            : new DataTable()
                    };
                });

                if (loaded == null)
                {
                    MessageBox.Show("Quality Event record was not found.",
                        "Quality Event", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Close();
                    return;
                }

                DataRow row = loaded.Header.Rows[0];
                loadedSampleId = sampleId;
                if (row.Table.Columns.Contains("SampleID") && row["SampleID"] != DBNull.Value)
                    loadedSampleId = Convert.ToInt32(row["SampleID"]);

                sampleNumber = row.GetSafeString("SampleNumber");
                lblEventNumber.Text = row.GetSafeString("EventNumber");
                lblEventType.Text = row.GetSafeString("EventType");
                lblSampleNumber.Text = sampleNumber;
                lblDetectedBy.Text = row.GetSafeString("DetectedBy");
                lblDetectionSource.Text = row.GetSafeString("DetectionSource");
                lblHeaderStatus.Text = row.GetSafeString("CurrentStatus");
                loadedClosedBy = row.Table.Columns.Contains("ClosedBy") ? row.GetSafeString("ClosedBy") : string.Empty;
                loadedClosedDate = row.Table.Columns.Contains("ClosedDate") ? row.GetSafeDateTime("ClosedDate") : null;

                DateTime? detectedDate = row.GetSafeDateTime("DetectedDate");
                loadedEventCreatedDate = row.Table.Columns.Contains("CreatedDate")
                    ? row.GetSafeDateTime("CreatedDate") ?? detectedDate
                    : detectedDate;
                lblDetectedDate.Text = detectedDate.HasValue ? detectedDate.Value.ToString("yyyy-MM-dd HH:mm") : "";

                SelectComboText(cboSeverity, row.GetSafeString("Severity"));
                SelectComboText(cboFinalDisposition, row.GetSafeString("FinalDisposition"));
                SelectComboText(cboRootCauseCategory, row.GetSafeString("RootCauseCategory"));
                txtInitialDescription.Text = row.GetSafeString("InitialDescription");
                txtImmediateAction.Text = row.GetSafeString("ImmediateAction");
                txtRootCauseDetails.Text = row.GetSafeString("RootCauseDetails");
                txtImpactAssessment.Text = row.GetSafeString("ImpactAssessment");
                txtQAConclusion.Text = row.GetSafeString("QAConclusion");
                chkCAPARequired.IsChecked = row.Table.Columns.Contains("CAPARequired") &&
                                            row["CAPARequired"] != DBNull.Value &&
                                            Convert.ToBoolean(row["CAPARequired"]);

                dgAffectedResults.ItemsSource = loaded.AffectedResults.DefaultView;
                dgActions.ItemsSource = loaded.Actions.DefaultView;
                checklistTable = loaded.Checklist;
                dgChecklist.ItemsSource = checklistTable.DefaultView;

                rootCauseWhysTable = loaded.RootCauseWhys;
                impactAssessmentTable = loaded.ImpactAssessment;
                capaItemsTable = loaded.CapaItems;
                retestingTable = loaded.Retesting;
                distributionTable = loaded.Distribution;
                environmentalMonitoringContextTable = loaded.EnvironmentalMonitoringContext ?? new DataTable();
                currentEventIsEnvironmentalMonitoring = IsCurrentEMEvent();
                EnsureEnvironmentalMonitoringStructuredDefaults();

                dgRootCauseWhys.ItemsSource = rootCauseWhysTable.DefaultView;
                dgImpactAssessment.ItemsSource = impactAssessmentTable.DefaultView;
                dgCAPAItems.ItemsSource = capaItemsTable.DefaultView;
                dgRetesting.ItemsSource = retestingTable.DefaultView;
                dgDistribution.ItemsSource = distributionTable.DefaultView;

                ApplyInvestigationProfileUI();
                PopulateEnvironmentalMonitoringContext();
                UpdateButtons(row.GetSafeString("CurrentStatus"));
                lblStatus.Text = "Loaded " + lblEventNumber.Text + ".";
            }
            catch (Exception ex)
            {
                ApplicationLogger.Error($"Error loading Quality Event Id={qualityEventId}, SampleId={sampleId}.", ex);
                MessageBox.Show(Infrastructure.UserFacingError.SafeMessage(ex, "Quality Event loading"),
                    "Quality Event", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isLoadingEvent = false;
                IsEnabled = true;
                Mouse.OverrideCursor = null;
            }
        }

        private void UpdateButtons(string status)
        {
            bool isClosed = status.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("QA Closed", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                            status.Equals("Rejected Closed", StringComparison.OrdinalIgnoreCase);

            isReadOnlyMode = isClosed;

            bool qaAuthorized = IsQaOrAdmin();
            BtnSave.IsEnabled = !isClosed;
            // Any authenticated investigator may submit a completed investigation to QA.
            // Only QA/Admin may perform the final QA closure.
            BtnSubmitQA.IsEnabled = !isClosed &&
                                    !status.Equals("QA Review", StringComparison.OrdinalIgnoreCase);
            BtnCloseEvent.IsEnabled = !isClosed && qaAuthorized &&
                                      status.Equals("QA Review", StringComparison.OrdinalIgnoreCase);

            if (BtnPrintReport != null)
                BtnPrintReport.IsEnabled = qualityEventId > 0;

            SetInvestigationReadOnly(isClosed);
        }

        private void SetInvestigationReadOnly(bool readOnly)
        {
            txtInitialDescription.IsReadOnly = readOnly;
            txtImmediateAction.IsReadOnly = readOnly;
            txtRootCauseDetails.IsReadOnly = readOnly;
            txtImpactAssessment.IsReadOnly = readOnly;
            txtQAConclusion.IsReadOnly = readOnly;
            txtActionNote.IsReadOnly = readOnly;

            cboSeverity.IsEnabled = !readOnly;
            cboRootCauseCategory.IsEnabled = !readOnly;
            cboFinalDisposition.IsEnabled = !readOnly;
            chkCAPARequired.IsEnabled = !readOnly;

            if (dgChecklist != null)
                dgChecklist.IsReadOnly = readOnly;
            if (dgRootCauseWhys != null)
                dgRootCauseWhys.IsReadOnly = readOnly;
            if (dgImpactAssessment != null)
                dgImpactAssessment.IsReadOnly = readOnly;
            if (dgCAPAItems != null)
                dgCAPAItems.IsReadOnly = readOnly;
            if (dgRetesting != null)
                dgRetesting.IsReadOnly = readOnly;
            if (dgDistribution != null)
                dgDistribution.IsReadOnly = readOnly;
        }

        private void SelectComboText(ComboBox combo, string value)
        {
            if (combo == null)
                return;

            foreach (object item in combo.Items)
            {
                string text = "";

                if (item is ComboBoxItem comboBoxItem)
                    text = comboBoxItem.Content?.ToString() ?? "";
                else
                    text = item?.ToString() ?? "";

                if (text.Equals(value ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            if (combo.Items.Count > 0 && combo.SelectedIndex < 0)
                combo.SelectedIndex = 0;
        }

        private string GetComboText(ComboBox combo)
        {
            if (combo == null)
                return "";

            if (combo.SelectedItem is ComboBoxItem item)
                return item.Content?.ToString() ?? "";

            return combo.SelectedItem?.ToString() ?? combo.Text ?? "";
        }

        private bool SaveInvestigation(string status)
        {
            if (qualityEventId <= 0)
                return false;

            CommitChecklistEdits();
            string actionNote = txtActionNote.Text.Trim();

            DatabaseHelper.ExecuteInTransaction((connection, transaction) =>
            {
                SaveInvestigationHeaderInTransaction(connection, transaction, status);
                SaveChecklistAnswersInTransaction(connection, transaction);
                SaveV15StructuredTables(connection, transaction, actionNote);

                if (!string.IsNullOrWhiteSpace(actionNote))
                    SaveActionNoteInTransaction(connection, transaction, actionNote);

                DatabaseHelper.AddAuditTrailAdvanced(
                    connection,
                    transaction,
                    "QualityEvents",
                    qualityEventId,
                    "Quality Event Investigation Update",
                    lblHeaderStatus.Text,
                    status,
                    "Investigation content updated",
                    currentUser,
                    "CurrentStatus",
                    null,
                    sampleNumber,
                    "Quality Event");
            });

            if (!string.IsNullOrWhiteSpace(actionNote))
                txtActionNote.Clear();

            return true;
        }

        private void SaveInvestigationHeaderInTransaction(SqlConnection connection, SqlTransaction transaction, string status)
        {
            int affected = DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                UPDATE dbo.QualityEvents
                SET Severity = @Severity,
                    InitialDescription = @InitialDescription,
                    ImmediateAction = @ImmediateAction,
                    RootCauseCategory = @RootCauseCategory,
                    RootCauseDetails = @RootCauseDetails,
                    ImpactAssessment = @ImpactAssessment,
                    CAPARequired = @CAPARequired,
                    QAConclusion = @QAConclusion,
                    FinalDisposition = @FinalDisposition,
                    CurrentStatus = @CurrentStatus,
                    ModifiedBy = @ModifiedBy,
                    ModifiedDate = GETDATE()
                WHERE QualityEventID = @QualityEventID",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                    new SqlParameter("@Severity", SqlDbType.NVarChar, 60) { Value = string.IsNullOrWhiteSpace(GetComboText(cboSeverity)) ? "Major" : GetComboText(cboSeverity) },
                    new SqlParameter("@InitialDescription", SqlDbType.NVarChar, -1) { Value = DbValue(txtInitialDescription.Text) },
                    new SqlParameter("@ImmediateAction", SqlDbType.NVarChar, -1) { Value = DbValue(txtImmediateAction.Text) },
                    new SqlParameter("@RootCauseCategory", SqlDbType.NVarChar, 120) { Value = DbValue(GetComboText(cboRootCauseCategory)) },
                    new SqlParameter("@RootCauseDetails", SqlDbType.NVarChar, -1) { Value = DbValue(txtRootCauseDetails.Text) },
                    new SqlParameter("@ImpactAssessment", SqlDbType.NVarChar, -1) { Value = DbValue(txtImpactAssessment.Text) },
                    new SqlParameter("@CAPARequired", SqlDbType.Bit) { Value = chkCAPARequired.IsChecked == true },
                    new SqlParameter("@QAConclusion", SqlDbType.NVarChar, -1) { Value = DbValue(txtQAConclusion.Text) },
                    new SqlParameter("@FinalDisposition", SqlDbType.NVarChar, 120) { Value = DbValue(GetComboText(cboFinalDisposition)) },
                    new SqlParameter("@CurrentStatus", SqlDbType.NVarChar, 60) { Value = string.IsNullOrWhiteSpace(status) ? "Open" : status.Trim() },
                    new SqlParameter("@ModifiedBy", SqlDbType.NVarChar, 120) { Value = DbValue(currentUser) }
                }, connection, transaction);

            if (affected != 1)
                throw new DBConcurrencyException("The Quality Event was not updated. It may have been removed or changed by another user.");
        }

        private static object DbValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
        }

        private void SaveChecklistAnswersInTransaction(SqlConnection connection, SqlTransaction transaction)
        {
            if (checklistTable == null || !V15TableExists("QualityEventChecklistAnswers"))
                return;

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !row.Table.Columns.Contains("QuestionID") || row["QuestionID"] == DBNull.Value)
                    continue;

                int questionId = Convert.ToInt32(row["QuestionID"]);
                if (questionId <= 0)
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                    UPDATE dbo.QualityEventChecklistAnswers
                    SET AnswerValue = @AnswerValue,
                        Comments = @Comments,
                        AnsweredBy = @AnsweredBy,
                        AnsweredDate = GETDATE()
                    WHERE QualityEventID = @QualityEventID AND QuestionID = @QuestionID;

                    IF @@ROWCOUNT = 0
                    BEGIN
                        INSERT INTO dbo.QualityEventChecklistAnswers
                            (QualityEventID, QuestionID, AnswerValue, Comments, AnsweredBy, AnsweredDate)
                        VALUES
                            (@QualityEventID, @QuestionID, @AnswerValue, @Comments, @AnsweredBy, GETDATE());
                    END",
                    new[]
                    {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@QuestionID", SqlDbType.Int) { Value = questionId },
                        new SqlParameter("@AnswerValue", SqlDbType.NVarChar, 50) { Value = DbValue(row.Table.Columns.Contains("AnswerValue") ? row.GetSafeString("AnswerValue") : "") },
                        new SqlParameter("@Comments", SqlDbType.NVarChar, -1) { Value = DbValue(row.Table.Columns.Contains("Comments") ? row.GetSafeString("Comments") : "") },
                        new SqlParameter("@AnsweredBy", SqlDbType.NVarChar, 120) { Value = DbValue(currentUser) }
                    }, connection, transaction);
            }
        }

        private void SaveActionNoteInTransaction(SqlConnection connection, SqlTransaction transaction, string actionNote)
        {
            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                INSERT INTO dbo.QualityEventActions
                    (QualityEventID, ActionType, ActionDescription, PerformedBy, PerformedDate, ElectronicSignatureID)
                VALUES
                    (@QualityEventID, N'Investigation Update', @ActionDescription, @PerformedBy, GETDATE(), NULL)",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                    new SqlParameter("@ActionDescription", SqlDbType.NVarChar, -1) { Value = actionNote },
                    new SqlParameter("@PerformedBy", SqlDbType.NVarChar, 120) { Value = currentUser }
                }, connection, transaction);
        }

        private bool ValidateRequiredChecklist(string stage)
        {
            CommitChecklistEdits();

            if (checklistTable == null || checklistTable.Rows.Count == 0)
                return true;

            List<string> issues = new List<string>();
            DataRow firstProblemRow = null;

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                bool isRequired = row.Table.Columns.Contains("IsRequired") &&
                                  row["IsRequired"] != DBNull.Value &&
                                  Convert.ToBoolean(row["IsRequired"]);

                string answer = row.Table.Columns.Contains("AnswerValue") ? row.GetSafeString("AnswerValue") : "";
                string expected = row.Table.Columns.Contains("ExpectedAnswer") ? row.GetSafeString("ExpectedAnswer") : "";
                string comments = row.Table.Columns.Contains("Comments") ? row.GetSafeString("Comments") : "";
                string section = row.GetSafeString("SectionName");
                string question = row.GetSafeString("QuestionText");

                // QA disposition/closure attestations are intentionally completed by QA
                // after submission; they must not block the investigator from submitting.
                if (stage.Equals("QA review", StringComparison.OrdinalIgnoreCase) &&
                    IsQaClosureOnlyChecklistItem(section, question))
                {
                    continue;
                }

                if (isRequired && string.IsNullOrWhiteSpace(answer))
                {
                    issues.Add($"{section}: {question} - answer is blank.");
                    firstProblemRow ??= row;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(answer) &&
                    DatabaseHelper.AnswerRequiresInvestigationComment(answer, expected))
                {
                    if (string.IsNullOrWhiteSpace(comments))
                    {
                        issues.Add($"{section}: {question} - '{answer}' requires comment/evidence.");
                        firstProblemRow ??= row;
                    }
                    else if (comments.Trim().Length < 10)
                    {
                        issues.Add($"{section}: {question} - comment/evidence for '{answer}' is too short.");
                        firstProblemRow ??= row;
                    }
                }
            }

            if (issues.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    $"The investigation checklist has {issues.Count} issue(s) before {stage}.",
                    issues,
                    "Complete all listed items and try again. Current edits are auto-saved as a draft before workflow validation."),
                "Investigation Checklist",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (firstProblemRow != null)
                FocusChecklistRow(firstProblemRow);
            return false;
        }

        private static bool IsQaClosureOnlyChecklistItem(string section, string question)
        {
            string normalizedSection = NormalizeChecklistIdentity(section);
            if (normalizedSection == "QA DISPOSITION" || normalizedSection == "QA CLOSURE")
                return true;

            string normalizedQuestion = NormalizeChecklistIdentity(question);
            return normalizedQuestion.Contains("FINAL QA DISPOSITION") ||
                   normalizedQuestion.Contains("BEFORE FINAL APPROVAL/REPORT ISSUANCE") ||
                   normalizedQuestion.Contains("APPROVED BY QA");
        }

        private static string BuildValidationIssueSummary(string heading, List<string> issues, string footer)
        {
            const int maxShown = 12;
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(heading);
            builder.AppendLine();

            int shown = Math.Min(maxShown, issues?.Count ?? 0);
            for (int i = 0; i < shown; i++)
                builder.AppendLine($"- {issues[i]}");

            if (issues != null && issues.Count > maxShown)
                builder.AppendLine($"- ... and {issues.Count - maxShown} more issue(s).");

            if (!string.IsNullOrWhiteSpace(footer))
            {
                builder.AppendLine();
                builder.Append(footer);
            }

            return builder.ToString();
        }

        private bool ValidateBeforeSubmitToQA()
        {
            CommitChecklistEdits();

            if (string.IsNullOrWhiteSpace(txtInitialDescription.Text))
            {
                MessageBox.Show("Initial description is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtInitialDescription.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtImmediateAction.Text))
            {
                MessageBox.Show("Immediate action is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtImmediateAction.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtImpactAssessment.Text))
            {
                MessageBox.Show("Impact assessment is required before QA review.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtImpactAssessment.Focus();
                return false;
            }

            if (!ValidateRequiredChecklist("QA review"))
                return false;

            if (currentEventIsEnvironmentalMonitoring && !ValidateEnvironmentalMonitoringForQaReview())
                return false;

            return true;
        }

        private bool ValidateBeforeClosure()
        {
            CommitChecklistEdits();

            if (!ValidateRootCauseAnalysis())
                return false;

            if (!ValidateImpactAssessmentCompleteness())
                return false;

            if (string.IsNullOrWhiteSpace(txtQAConclusion.Text))
            {
                MessageBox.Show("QA conclusion is required before closure.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtQAConclusion.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(GetComboText(cboFinalDisposition)))
            {
                MessageBox.Show("Final disposition is required before closure.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cboFinalDisposition.Focus();
                return false;
            }

            if (!ValidateCAPACompleteness())
                return false;

            if (!ValidateRetestingStrategy())
                return false;

            if (!ValidateRequiredChecklist("QA closure"))
                return false;

            if (!ValidateCriticalChecklistForClosure())
                return false;

            if (!ValidateDistributionList())
                return false;

            if (currentEventIsEnvironmentalMonitoring && !ValidateEnvironmentalMonitoringForClosure())
                return false;

            return true;
        }

        private bool ValidateEnvironmentalMonitoringForQaReview()
        {
            CommitChecklistEdits();

            if (!ValidateEnvironmentalMonitoringChecklistCoverage())
                return false;

            if (!ValidateEnvironmentalMonitoringEvidence())
                return false;

            if (!ValidateEnvironmentalMonitoringFishboneEvidence())
                return false;

            if (string.IsNullOrWhiteSpace(GetComboText(cboRootCauseCategory)))
            {
                MessageBox.Show("Root cause category is required for an Environmental Monitoring investigation before QA review.",
                    "EM Investigation", MessageBoxButton.OK, MessageBoxImage.Warning);
                cboRootCauseCategory.Focus();
                return false;
            }

            if (!ValidateMinimumNarrative(txtRootCauseDetails.Text, 60,
                "Environmental Monitoring root-cause details must contain a meaningful evidence-based conclusion (minimum 60 characters).",
                "EM Root Cause", txtRootCauseDetails))
                return false;

            if (!ValidateEnvironmentalMonitoringImpactAreas())
                return false;

            return true;
        }

        private bool ValidateEnvironmentalMonitoringForClosure()
        {
            if (!ValidateEnvironmentalMonitoringForQaReview())
                return false;

            if (!ValidateEnvironmentalMonitoringFollowUp())
                return false;

            if (chkCAPARequired.IsChecked == true && !ValidateEnvironmentalMonitoringCAPA())
                return false;

            if (!ValidateMinimumNarrative(txtQAConclusion.Text, 180,
                "QA conclusion for an Environmental Monitoring event must summarize the excursion, root cause, area/product impact, follow-up evidence, CAPA/effectiveness plan where applicable, and final area disposition (minimum 180 characters).",
                "EM QA Closure", txtQAConclusion))
                return false;

            if (!ValidateEnvironmentalMonitoringQaConclusionCoverage())
                return false;

            string disposition = GetComboText(cboFinalDisposition);
            if (disposition.Equals("Retest Approved", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("Resample Approved", StringComparison.OrdinalIgnoreCase) ||
                disposition.Equals("System Corrected / Monitoring Required", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    "This disposition indicates additional testing/monitoring is still required and is not a final Environmental Monitoring area-release decision.\n\nComplete the required follow-up and select a final disposition before QA closure.",
                    "EM QA Closure", MessageBoxButton.OK, MessageBoxImage.Warning);
                cboFinalDisposition.Focus();
                return false;
            }

            return true;
        }

        private bool IsCriticalOrMajorEnvironmentalMonitoring()
        {
            if (!currentEventIsEnvironmentalMonitoring)
                return false;
            string severity = GetComboText(cboSeverity);
            return severity.Equals("Critical", StringComparison.OrdinalIgnoreCase) ||
                   severity.Equals("Major", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWeakInvestigationNarrative(string value, int minimumLength)
        {
            string normalized = (value ?? "").Trim();
            if (normalized.Length < minimumLength)
                return true;

            string token = Regex.Replace(normalized.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
            string[] weak = { "ok", "not", "yes", "no", "na", "n a", "none", "normal", "good", "pass", "passed" };
            foreach (string weakValue in weak)
            {
                if (token.Equals(weakValue, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool ValidateEnvironmentalMonitoringQaConclusionCoverage()
        {
            string conclusion = txtQAConclusion.Text ?? "";
            string normalized = conclusion.ToLowerInvariant();

            bool hasCause = ContainsAny(normalized, "root cause", "probable cause", "cause", "سبب");
            bool hasImpact = ContainsAny(normalized, "impact", "product", "batch", "material", "area", "تأثير", "منتج", "دفعة", "منطقة");
            bool hasFollowUp = ContainsAny(normalized, "follow-up", "follow up", "resampl", "monitor", "repeat", "متابعة", "إعادة", "مراقب");
            bool hasCapa = chkCAPARequired.IsChecked != true || ContainsAny(normalized, "capa", "corrective", "preventive", "effectiveness", "تصحيح", "وقائي", "فعالية");
            bool hasDisposition = ContainsAny(normalized, "release", "released", "restrict", "disposition", "reject", "إطلاق", "قرار", "رفض");

            List<string> missing = new List<string>();
            if (!hasCause) missing.Add("root/most probable cause");
            if (!hasImpact) missing.Add("area and product/material/batch impact");
            if (!hasFollowUp) missing.Add("follow-up monitoring/resampling evidence");
            if (!hasCapa) missing.Add("CAPA/effectiveness plan");
            if (!hasDisposition) missing.Add("final area disposition rationale");

            if (missing.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "QA conclusion is not sufficiently structured for an Environmental Monitoring closure.",
                    missing.ConvertAll(item => "Missing topic: " + item),
                    "State the evidence and scientific basis for each topic; do not use a generic instruction such as 'continue cleaning and monitoring'."),
                "EM QA Closure",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            txtQAConclusion.Focus();
            return false;
        }

        private static bool ContainsAny(string source, params string[] terms)
        {
            if (string.IsNullOrWhiteSpace(source))
                return false;
            foreach (string term in terms)
            {
                if (!string.IsNullOrWhiteSpace(term) && source.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private bool ValidateEnvironmentalMonitoringChecklistCoverage()
        {
            CommitChecklistEdits();

            if (checklistTable == null || checklistTable.Rows.Count == 0)
            {
                MessageBox.Show("The Environmental Monitoring investigation checklist is missing. The event cannot be submitted or closed.",
                    "EM Investigation Checklist", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            string[] requiredSections =
            {
                "Phase I - Laboratory Review", "EM Sampling", "EM Media", "EM Incubation", "EM Result Review",
                "EM Identification", "Cleaning / Disinfection", "HVAC / Facility",
                "Trend Review", "Impact Assessment", "Follow-up Monitoring", "CAPA", "QA Disposition",
                "Root Cause / Fishbone"
            };

            List<string> missingSections = new List<string>();
            foreach (string section in requiredSections)
            {
                bool found = false;
                foreach (DataRow row in checklistTable.Rows)
                {
                    if (row.RowState == DataRowState.Deleted)
                        continue;
                    if (NormalizeChecklistIdentity(row.GetSafeString("SectionName")) == NormalizeChecklistIdentity(section))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                    missingSections.Add(section);
            }

            if (missingSections.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "The controlled Environmental Monitoring checklist is incomplete.",
                    missingSections.ConvertAll(section => "Missing section: " + section),
                    "Close and re-open the event with the current application version. If a section is still missing, the checklist master requires controlled repair."),
                "EM Investigation Checklist",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            dgChecklist?.Focus();
            return false;
        }

        private bool ValidateEnvironmentalMonitoringEvidence()
        {
            CommitChecklistEdits();

            HashSet<string> evidenceSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeChecklistIdentity("Phase I - Laboratory Review"),
                NormalizeChecklistIdentity("EM Identification"),
                NormalizeChecklistIdentity("Cleaning / Disinfection"),
                NormalizeChecklistIdentity("HVAC / Facility"),
                NormalizeChecklistIdentity("Area / Personnel"),
                NormalizeChecklistIdentity("Personnel"),
                NormalizeChecklistIdentity("Trend Review"),
                NormalizeChecklistIdentity("Related Records"),
                NormalizeChecklistIdentity("Root Cause / Fishbone"),
                NormalizeChecklistIdentity("QA Disposition")
            };

            List<string> issues = new List<string>();
            DataRow firstProblem = null;

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string section = NormalizeChecklistIdentity(row.GetSafeString("SectionName"));
                if (!evidenceSections.Contains(section))
                    continue;

                string answer = row.GetSafeString("AnswerValue").Trim();
                string comments = row.GetSafeString("Comments").Trim();
                if (string.IsNullOrWhiteSpace(answer))
                    continue; // Required-answer validation reports this separately.

                int minimumEvidence = IsAffirmativeAnswer(answer) ? 12 : 20;
                if (comments.Length < minimumEvidence)
                {
                    issues.Add($"{row.GetSafeString("SectionName")}: {row.GetSafeString("QuestionText")} - evidence/comment must be at least {minimumEvidence} characters for answer '{answer}'.");
                    firstProblem ??= row;
                }
            }

            if (issues.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "Environmental Monitoring evidence is incomplete.",
                    issues,
                    "Document the record reviewed, observation, reference, or scientific rationale. A Yes/No/N/A answer alone is not sufficient evidence for final QA disposition."),
                "EM Evidence Validation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (firstProblem != null)
                FocusChecklistRow(firstProblem);
            return false;
        }

        private bool ValidateEnvironmentalMonitoringFishboneEvidence()
        {
            string fishboneSection = NormalizeChecklistIdentity("Root Cause / Fishbone");
            string[] expectedFamilies =
            {
                "Personnel / Training", "Method / Procedure", "Equipment / Instrument",
                "Material / Media", "Environment / Facility", "Measurement / Data"
            };

            List<string> issues = new List<string>();
            foreach (string family in expectedFamilies)
            {
                DataRow match = null;
                foreach (DataRow row in checklistTable.Rows)
                {
                    if (row.RowState == DataRowState.Deleted ||
                        NormalizeChecklistIdentity(row.GetSafeString("SectionName")) != fishboneSection)
                        continue;
                    if (row.GetSafeString("QuestionText").IndexOf(family, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = row;
                        break;
                    }
                }

                if (match == null || string.IsNullOrWhiteSpace(match.GetSafeString("AnswerValue")) ||
                    match.GetSafeString("Comments").Trim().Length < 12)
                {
                    issues.Add(family + " - assessment answer and evidence are required.");
                }
            }

            if (issues.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "Root-cause causal families have not all been assessed.",
                    issues,
                    "Assess every fishbone family and document the evidence used to include or reasonably exclude it."),
                "EM Root Cause / Fishbone",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            dgChecklist?.Focus();
            return false;
        }

        private bool ValidateEnvironmentalMonitoringImpactAreas()
        {
            CommitChecklistEdits();

            string[] requiredAreas =
            {
                "Monitored Area / State of Control",
                "Adjacent / Related EM Locations",
                "Product / Material / Batch Exposure",
                "Personnel / Aseptic Activity",
                "HVAC / Facility / Utilities",
                "Historical Trend / Recurrent Flora"
            };

            HashSet<string> allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "No Impact", "Potential Impact", "Confirmed Impact", "Controlled / Acceptable", "Not Applicable"
            };

            List<string> issues = new List<string>();
            foreach (string requiredArea in requiredAreas)
            {
                DataRow matching = null;
                foreach (DataRow row in impactAssessmentTable.Rows)
                {
                    if (row.RowState == DataRowState.Deleted)
                        continue;
                    if (NormalizeChecklistIdentity(row.GetSafeString("AssessmentArea")) == NormalizeChecklistIdentity(requiredArea))
                    {
                        matching = row;
                        break;
                    }
                }

                if (matching == null)
                {
                    issues.Add(requiredArea + " - assessment row is missing.");
                    continue;
                }

                string finding = matching.GetSafeString("Finding").Trim();
                string status = matching.GetSafeString("ImpactStatus").Trim();
                if (finding.Length < 20)
                    issues.Add(requiredArea + " - document a scientific finding/rationale (minimum 20 characters).");
                if (!allowedStatuses.Contains(status))
                    issues.Add(requiredArea + " - select a controlled Impact Status instead of free text such as 'ok' or 'not'.");
            }

            if (issues.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "The Environmental Monitoring impact assessment is not ready for QA disposition.",
                    issues,
                    "Each impact area requires a meaningful finding and one controlled status."),
                "EM Impact Assessment",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            dgImpactAssessment?.Focus();
            return false;
        }

        private bool ValidateEnvironmentalMonitoringFollowUp()
        {
            List<string> issues = new List<string>();
            int recordNumber = 0;
            bool foundExplicitStrategy = false;

            foreach (DataRow row in retestingTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                bool hasAny = HasAnyValue(row, "RetestPerformed", "SampleAliquot", "Analyst", "RetestResult", "Justification", "ScientificBasis", "DispositionUse");
                if (!hasAny)
                    continue;

                recordNumber++;
                string performedText = row.GetSafeString("RetestPerformed").Trim();
                if (string.IsNullOrWhiteSpace(performedText))
                {
                    issues.Add($"Follow-up row {recordNumber}: select Yes or No; partially completed rows are not permitted.");
                    continue;
                }

                foundExplicitStrategy = true;
                bool performed = performedText.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                bool notPerformed = performedText.Equals("No", StringComparison.OrdinalIgnoreCase);
                if (!performed && !notPerformed)
                {
                    issues.Add($"Follow-up row {recordNumber}: Follow-up Performed must be Yes or No.");
                    continue;
                }

                if (performed)
                {
                    if (string.IsNullOrWhiteSpace(row.GetSafeString("SampleAliquot")))
                        issues.Add($"Follow-up row {recordNumber}: monitoring point / plate / sample is required.");
                    if (string.IsNullOrWhiteSpace(row.GetSafeString("Analyst")))
                        issues.Add($"Follow-up row {recordNumber}: performer is required.");
                    if (string.IsNullOrWhiteSpace(row.GetSafeString("RetestResult")))
                        issues.Add($"Follow-up row {recordNumber}: result is required.");
                    if (row.GetSafeString("Justification").Trim().Length < 20)
                        issues.Add($"Follow-up row {recordNumber}: rationale/justification must be at least 20 characters.");
                    if (row.GetSafeString("ScientificBasis").Trim().Length < 20)
                        issues.Add($"Follow-up row {recordNumber}: scientific basis/acceptance criteria must be at least 20 characters.");
                    if (row.GetSafeString("DispositionUse").Trim().Length < 30)
                        issues.Add($"Follow-up row {recordNumber}: explain how the follow-up evidence is used in QA disposition (minimum 30 characters; do not enter a date only).");
                }
                else
                {
                    if (row.GetSafeString("Justification").Trim().Length < 20)
                        issues.Add($"Follow-up row {recordNumber}: scientific justification is required when follow-up was not performed.");
                    if (row.GetSafeString("ScientificBasis").Trim().Length < 20)
                        issues.Add($"Follow-up row {recordNumber}: scientific basis and acceptance rationale are required when follow-up was not performed.");
                    if (row.GetSafeString("DispositionUse").Trim().Length < 30)
                        issues.Add($"Follow-up row {recordNumber}: explain how no follow-up was considered in final disposition (minimum 30 characters).");
                }
            }

            if (!foundExplicitStrategy)
                issues.Add("Document at least one explicit follow-up strategy row with Follow-up Performed = Yes or No.");

            if (issues.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "Environmental Monitoring follow-up is incomplete.",
                    issues,
                    "A passing repeat result supports the investigation but does not erase the original excursion."),
                "EM Follow-up Monitoring",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            dgRetesting?.Focus();
            return false;
        }

        private bool ValidateEnvironmentalMonitoringCAPA()
        {
            List<string> issues = new List<string>();
            int itemNumber = 0;
            string disposition = GetComboText(cboFinalDisposition);
            bool releaseAfterCorrectiveAction = disposition.Equals("Area Released after Corrective Action", StringComparison.OrdinalIgnoreCase);

            HashSet<string> allowedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Open", "In Progress", "Implemented", "Effectiveness Pending", "Effectiveness Verified", "Closed"
            };

            foreach (DataRow row in capaItemsTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted ||
                    !HasAnyValue(row, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus"))
                    continue;

                itemNumber++;
                string description = row.GetSafeString("ActionDescription").Trim();
                if (description.Length < 20)
                    issues.Add($"CAPA item {itemNumber}: Action Description must describe the actual corrective/preventive action (minimum 20 characters).");

                string dueDate = row.GetSafeString("DueDate");
                if (string.IsNullOrWhiteSpace(dueDate) || !TryParseFlexibleDate(dueDate, out DateTime parsedDueDate))
                {
                    issues.Add($"CAPA item {itemNumber}: Due Date '{dueDate}' is invalid. Use 2026-08-25 or 25/08/2026.");
                }
                else
                {
                    row["DueDate"] = parsedDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                string effectiveness = row.GetSafeString("EffectivenessCheck").Trim();
                if (effectiveness.Length < 30)
                    issues.Add($"CAPA item {itemNumber}: Effectiveness Check must define a measurable acceptance criterion (minimum 30 characters).");

                string status = row.GetSafeString("CAPAStatus").Trim();
                if (!allowedStatuses.Contains(status))
                    issues.Add($"CAPA item {itemNumber}: select a controlled CAPA Status.");

                if (releaseAfterCorrectiveAction && IsCriticalOrMajorEnvironmentalMonitoring() &&
                    (status.Equals("Open", StringComparison.OrdinalIgnoreCase) || status.Equals("In Progress", StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add($"CAPA item {itemNumber}: an area cannot be recorded as 'Released after Corrective Action' while the corrective action is still {status}. Record implementation first, then document effectiveness follow-up.");
                }
            }

            if (issues.Count > 0)
            {
                MessageBox.Show(
                    BuildValidationIssueSummary(
                        "Environmental Monitoring CAPA is not ready for final QA disposition.",
                        issues,
                        "Use controlled status values and measurable effectiveness criteria. Open follow-up may continue after implementation, but final area release must not claim that an unimplemented corrective action is complete."),
                    "EM CAPA", MessageBoxButton.OK, MessageBoxImage.Warning);
                dgCAPAItems?.Focus();
                return false;
            }

            return true;
        }

        private bool ValidateRootCauseAnalysis()
        {
            if (currentEventIsEnvironmentalMonitoring && txtRootCauseDetails.Text.Trim().Length < 80)
            {
                MessageBox.Show(
                    "Environmental Monitoring root-cause details must be evidence-based and at least 80 characters. Document the confirmed or most probable cause, evidence, and why alternative causes were excluded.",
                    "Root Cause Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtRootCauseDetails.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(txtRootCauseDetails.Text))
            {
                MessageBox.Show(
                    "Root cause details are required before closure.\n\n" +
                    "Document the confirmed or most probable root cause of the OOS/OOT/deviation.",
                    "Root Cause Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtRootCauseDetails.Focus();
                return false;
            }

            if (rootCauseWhysTable == null || rootCauseWhysTable.Rows.Count == 0)
                return true;

            int meaningfulRows = 0;
            int answeredRows = 0;
            string previousAnswer = "";
            int repeatedAnswerCount = 0;

            foreach (DataRow row in rootCauseWhysTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string question = row.GetSafeString("Question");
                string answer = row.GetSafeString("Answer");

                if (!HasAnyValue(row, "Question", "Answer"))
                    continue;

                meaningfulRows++;

                if (!string.IsNullOrWhiteSpace(answer))
                {
                    answeredRows++;

                    string normalized = answer.Trim().ToLowerInvariant();
                    if (normalized == previousAnswer && normalized.Length >= 4)
                        repeatedAnswerCount++;
                    previousAnswer = normalized;
                }

                if (string.IsNullOrWhiteSpace(question))
                {
                    MessageBox.Show(
                        "Each documented 5 Whys row must include a question.",
                        "Root Cause Validation",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    dgRootCauseWhys?.Focus();
                    return false;
                }
            }

            int minimumWhyLevels = currentEventIsEnvironmentalMonitoring && IsCriticalOrMajorEnvironmentalMonitoring() ? 4 : 3;
            if (meaningfulRows > 0 && answeredRows < minimumWhyLevels)
            {
                MessageBox.Show(
                    "Root cause analysis is incomplete.\n\n" +
                    $"Answered {answeredRows} of {meaningfulRows} documented Why rows.\n" +
                    $"Complete at least {minimumWhyLevels} meaningful levels for this event severity.",
                    "Root Cause Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dgRootCauseWhys?.Focus();
                return false;
            }

            if (currentEventIsEnvironmentalMonitoring)
            {
                List<string> weakWhyAnswers = new List<string>();
                foreach (DataRow row in rootCauseWhysTable.Rows)
                {
                    if (row.RowState == DataRowState.Deleted)
                        continue;
                    string answer = row.GetSafeString("Answer").Trim();
                    if (string.IsNullOrWhiteSpace(answer))
                        continue;
                    if (IsWeakInvestigationNarrative(answer, 15))
                        weakWhyAnswers.Add($"Why {row.GetSafeString("WhyLevel")}: '{answer}'");
                }

                if (weakWhyAnswers.Count > 0)
                {
                    MessageBox.Show(
                        BuildValidationIssueSummary(
                            "The 5 Whys contains answers that are too short or non-scientific.",
                            weakWhyAnswers,
                            "Each Why should explain a causal link, not a one-word response such as 'ok', 'not', 'yes', or 'no'."),
                        "Root Cause Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    dgRootCauseWhys?.Focus();
                    return false;
                }
            }

            if (answeredRows >= 3 && repeatedAnswerCount >= 2)
            {
                MessageBox.Show(
                    "The 5 Whys analysis appears repetitive.\n\n" +
                    "Do not repeat the same answer at multiple levels unless the comments clearly explain the system cause.\n" +
                    "Refine the answers so each Why moves deeper toward the true root cause.",
                    "Root Cause Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dgRootCauseWhys?.Focus();
                return false;
            }

            return true;
        }

        private bool ValidateImpactAssessmentCompleteness()
        {
            if (string.IsNullOrWhiteSpace(txtImpactAssessment.Text))
            {
                MessageBox.Show(
                    "Impact assessment is required before closure.",
                    "Impact Assessment",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                txtImpactAssessment.Focus();
                return false;
            }

            if (impactAssessmentTable == null)
                return true;

            foreach (DataRow row in impactAssessmentTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string area = row.GetSafeString("AssessmentArea");
                string finding = row.GetSafeString("Finding");
                string status = row.GetSafeString("ImpactStatus");

                bool userStartedThisRow =
                    !string.IsNullOrWhiteSpace(finding) ||
                    !string.IsNullOrWhiteSpace(status);

                if (!userStartedThisRow)
                    continue;

                if (string.IsNullOrWhiteSpace(finding) || string.IsNullOrWhiteSpace(status))
                {
                    MessageBox.Show(
                        "Each documented impact assessment row must include both Finding and Impact Status.\n\n" +
                        "Assessment Area: " + (string.IsNullOrWhiteSpace(area) ? "Not specified" : area),
                        "Impact Assessment",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    dgImpactAssessment?.Focus();
                    return false;
                }
            }

            return true;
        }

        private bool ValidateCriticalChecklistForClosure()
        {
            CommitChecklistEdits();

            if (checklistTable == null || checklistTable.Rows.Count == 0)
                return true;

            List<string> issues = new List<string>();
            DataRow firstProblemRow = null;

            foreach (DataRow row in checklistTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted)
                    continue;

                string question = row.Table.Columns.Contains("QuestionText") ? row.GetSafeString("QuestionText") : "";
                string answer = row.Table.Columns.Contains("AnswerValue") ? row.GetSafeString("AnswerValue") : "";
                string q = question.ToLowerInvariant();

                bool mustBeYesBeforeClosure =
                    q.Contains("raw data") && q.Contains("audit trail") && q.Contains("reviewed") ||
                    q.Contains("impact on related samples") && q.Contains("assessed") ||
                    q.Contains("capa requirement") && q.Contains("scientifically justified") ||
                    q.Contains("final qa disposition") && q.Contains("scientifically justified") ||
                    q.Contains("measuring instrument") && q.Contains("calibration") ||
                    q.Contains("standards") && q.Contains("within expiry") ||
                    q.Contains("probe") && q.Contains("condition acceptable") ||
                    q.Contains("colony count") && q.Contains("independently verified") ||
                    q.Contains("potential impact") && q.Contains("assessed") ||
                    q.Contains("follow-up monitoring") && q.Contains("scientifically justified");

                if (mustBeYesBeforeClosure && !IsAffirmativeAnswer(answer))
                {
                    issues.Add(question + " - current answer: " + (string.IsNullOrWhiteSpace(answer) ? "Blank" : answer));
                    firstProblemRow ??= row;
                }
            }

            if (issues.Count == 0)
                return true;

            MessageBox.Show(
                BuildValidationIssueSummary(
                    "Critical checklist items are not ready for QA closure.",
                    issues,
                    "These items must be Yes before closure. A No finding must remain open until resolved, justified, or escalated."),
                "QA Closure Checklist Validation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (firstProblemRow != null)
                FocusChecklistRow(firstProblemRow);
            return false;
        }

        private bool ValidateRetestingStrategy()
        {
            if (retestingTable == null || retestingTable.Rows.Count == 0)
                return true;

            foreach (DataRow row in retestingTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "RetestPerformed", "SampleAliquot", "Analyst", "RetestResult", "Justification", "ScientificBasis", "DispositionUse"))
                    continue;

                string retestPerformed = row.GetSafeString("RetestPerformed").Trim().ToLowerInvariant();
                bool performed = retestPerformed == "yes" || retestPerformed == "y" || retestPerformed == "true" || retestPerformed == "1";

                if (!performed)
                    continue;

                if (string.IsNullOrWhiteSpace(row.GetSafeString("Analyst")))
                {
                    MessageBox.Show("Analyst is required when a retest is performed.", "Retesting Strategy", MessageBoxButton.OK, MessageBoxImage.Warning);
                    dgRetesting?.Focus();
                    return false;
                }

                if (string.IsNullOrWhiteSpace(row.GetSafeString("RetestResult")))
                {
                    MessageBox.Show("Retest result is required when a retest is performed.", "Retesting Strategy", MessageBoxButton.OK, MessageBoxImage.Warning);
                    dgRetesting?.Focus();
                    return false;
                }

                if (!ValidateMinimumNarrative(row.GetSafeString("Justification"), 10,
                    "Retesting justification must be meaningful when a retest is performed. Avoid short entries such as 'ok'.",
                    "Retesting Strategy", dgRetesting))
                    return false;

                if (!ValidateMinimumNarrative(row.GetSafeString("ScientificBasis"), 10,
                    "Scientific basis is required when a retest is performed. Document the rationale for retesting and how the result will be interpreted.",
                    "Retesting Strategy", dgRetesting))
                    return false;

                if (!ValidateMinimumNarrative(row.GetSafeString("DispositionUse"), 10,
                    "Use in disposition is required when a retest is performed. Document whether the original result, retest result, or investigation conclusion controls final disposition.",
                    "Retesting Strategy", dgRetesting))
                    return false;
            }

            return true;
        }

        private bool ValidateMinimumNarrative(string value, int minimumLength, string message, string title, Control focusControl)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < minimumLength)
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                focusControl?.Focus();
                return false;
            }

            return true;
        }

        private bool ValidateCAPACompleteness()
        {
            if (chkCAPARequired.IsChecked != true)
                return true;

            if (capaItemsTable == null || FilterMeaningfulRows(capaItemsTable, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus").Rows.Count == 0)
            {
                MessageBox.Show(
                    "CAPA is required but no CAPA action items have been documented.\n\n" +
                    "Add at least one CAPA action item before QA closure.",
                    "CAPA Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dgCAPAItems?.Focus();
                return false;
            }

            bool hasCorrectiveAction = false;

            foreach (DataRow row in capaItemsTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "ActionDescription", "ActionType", "Responsible", "DueDate", "EffectivenessCheck", "CAPAStatus"))
                    continue;

                string actionType = row.GetSafeString("ActionType");
                string normalizedType = actionType.ToLowerInvariant();

                if (normalizedType.Contains("corrective"))
                    hasCorrectiveAction = true;

                if (string.IsNullOrWhiteSpace(row.GetSafeString("ActionDescription")))
                {
                    MessageBox.Show("All CAPA action items must have an action description.", "CAPA Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    dgCAPAItems?.Focus();
                    return false;
                }

                if (string.IsNullOrWhiteSpace(actionType))
                {
                    MessageBox.Show("All CAPA action items must have an action type.", "CAPA Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    dgCAPAItems?.Focus();
                    return false;
                }

                if (string.IsNullOrWhiteSpace(row.GetSafeString("Responsible")))
                {
                    MessageBox.Show("All CAPA action items must have a responsible person or department.", "CAPA Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    dgCAPAItems?.Focus();
                    return false;
                }
            }

            if (!hasCorrectiveAction)
            {
                MessageBox.Show(
                    "At least one corrective action is required in the CAPA plan.\n\n" +
                    "Corrective actions address the immediate issue.",
                    "CAPA Validation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dgCAPAItems?.Focus();
                return false;
            }

            return true;
        }

        private bool ValidateDistributionList()
        {
            if (distributionTable == null)
                return !IsCriticalOrMajorEnvironmentalMonitoring();

            int incompleteCount = 0;
            int completeCount = 0;

            foreach (DataRow row in distributionTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "Department", "Recipient", "DateReceived"))
                    continue;

                string recipient = row.GetSafeString("Recipient");
                string dateReceived = row.GetSafeString("DateReceived");

                if (string.IsNullOrWhiteSpace(recipient) || string.IsNullOrWhiteSpace(dateReceived) ||
                    !TryParseFlexibleDate(dateReceived, out DateTime parsedDateReceived) ||
                    parsedDateReceived.Date > DateTime.Today)
                {
                    incompleteCount++;
                }
                else
                {
                    row["DateReceived"] = parsedDateReceived.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    completeCount++;
                }
            }

            if (incompleteCount > 0)
            {
                MessageBox.Show(
                    $"The distribution list has {incompleteCount} incomplete or invalid entr{(incompleteCount == 1 ? "y" : "ies")}.\n\n" +
                    "Complete Recipient and a valid non-future Date Received (for example 2026-08-20), or leave the distribution row fully blank.",
                    "Distribution List",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dgDistribution?.Focus();
                return false;
            }

            if (IsCriticalOrMajorEnvironmentalMonitoring() && completeCount == 0)
            {
                MessageBox.Show(
                    "A Major/Critical Environmental Monitoring Quality Event requires at least one documented distribution recipient before final QA closure.",
                    "Distribution List",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                dgDistribution?.Focus();
                return false;
            }

            return true;
        }

        private void PersistDraftAndReloadForValidation(string validationStage)
        {
            // Do not validate transient WPF editor state. Persist the user's current draft
            // first, then validate a fresh database snapshot. This prevents a later
            // validation failure from discarding answers entered earlier in the same pass.
            CommitChecklistEdits();
            SaveInvestigation(lblHeaderStatus.Text);
            ReloadInvestigationTablesForValidation();
            lblStatus.Text = $"Draft auto-saved before {validationStage} validation.";
        }

        private void ReloadInvestigationTablesForValidation()
        {
            checklistTable = GetFilteredChecklist();
            rootCauseWhysTable = LoadRootCauseWhysTable();
            impactAssessmentTable = LoadImpactAssessmentTable();
            capaItemsTable = LoadCAPAItemsTable();
            retestingTable = LoadRetestingTable();
            distributionTable = LoadDistributionTable();

            EnsureEnvironmentalMonitoringStructuredDefaults();

            dgChecklist.ItemsSource = checklistTable.DefaultView;
            dgRootCauseWhys.ItemsSource = rootCauseWhysTable.DefaultView;
            dgImpactAssessment.ItemsSource = impactAssessmentTable.DefaultView;
            dgCAPAItems.ItemsSource = capaItemsTable.DefaultView;
            dgRetesting.ItemsSource = retestingTable.DefaultView;
            dgDistribution.ItemsSource = distributionTable.DefaultView;
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SaveInvestigation(lblHeaderStatus.Text);
                await LoadEventAsync();
                MessageBox.Show("Investigation saved successfully.", "Quality Event", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving investigation: " + Infrastructure.UserFacingError.SafeMessage(ex), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnSubmitQA_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                PersistDraftAndReloadForValidation("QA review");
                if (!ValidateBeforeSubmitToQA())
                    return;

                DatabaseHelper.SubmitQualityEventToQA(qualityEventId, currentUser);

                await LoadEventAsync();
                MessageBox.Show("Quality Event submitted to QA review.", "Quality Event", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error submitting to QA: " + Infrastructure.UserFacingError.SafeMessage(ex), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnCloseEvent_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                EnsureQaPermission();
                if (!lblHeaderStatus.Text.Equals("QA Review", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A Quality Event can be closed only from QA Review status.");

                PersistDraftAndReloadForValidation("QA closure");
                if (!ValidateBeforeClosure())
                    return;

                var signatureWindow = _serviceProvider?.GetRequiredService<ElectronicSignature>();
                if (signatureWindow == null)
                {
                    signatureWindow = new ElectronicSignature(
                        string.IsNullOrWhiteSpace(sampleNumber) ? lblEventNumber.Text : sampleNumber,
                        currentUser,
                        "Quality Event Closure",
                        true);
                }
                else
                {
                    signatureWindow.Configure(
                        string.IsNullOrWhiteSpace(sampleNumber) ? lblEventNumber.Text : sampleNumber,
                        currentUser,
                        "Quality Event Closure",
                        true);
                }

                signatureWindow.Owner = this;

                if (signatureWindow.ShowDialog() != true || !signatureWindow.IsConfirmed)
                    return;

                DatabaseHelper.CloseQualityEvent(
                    qualityEventId,
                    loadedSampleId,
                    txtQAConclusion.Text.Trim(),
                    GetComboText(cboFinalDisposition),
                    signatureWindow.SignedBy,
                    signatureWindow.Meaning,
                    signatureWindow.Reason,
                    currentRole);


                await LoadEventAsync();
                MessageBox.Show("Quality Event closed successfully. The linked laboratory record can continue according to QA disposition.",
                    "Quality Event", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error closing Quality Event: " + Infrastructure.UserFacingError.SafeMessage(ex), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool IsQaOrAdmin() => DatabaseHelper.CanCloseQualityEvent(currentUser);
        private void EnsureQaPermission() { if(!IsQaOrAdmin()) throw new UnauthorizedAccessException("QA or Administrator authorization is required."); }

        private void InvestigationGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (sender is not DataGrid grid || grid.Name != "dgChecklist")
                return;

            if (e.EditingElement is ComboBox comboBox)
            {
                comboBox.SelectionChanged -= ChecklistAnswerComboBox_SelectionChanged;
                comboBox.SelectionChanged += ChecklistAnswerComboBox_SelectionChanged;
            }
        }

        private void ChecklistAnswerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox comboBox || comboBox.DataContext is not DataRowView rowView)
                return;

            if (!rowView.Row.Table.Columns.Contains("AnswerValue"))
                return;

            string selected = comboBox.SelectedItem?.ToString() ?? comboBox.SelectedValue?.ToString() ?? comboBox.Text ?? "";
            rowView["AnswerValue"] = selected;
            rowView.EndEdit();
        }

        private void InvestigationGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e == null || e.EditAction != DataGridEditAction.Commit || e.Row?.Item is not DataRowView rowView)
                return;

            try
            {
                string columnName = ResolveEditedColumnName(sender as DataGrid, e.Column);
                if (string.IsNullOrWhiteSpace(columnName) || !rowView.Row.Table.Columns.Contains(columnName))
                    return;

                if (e.EditingElement is TextBox textBox)
                {
                    string editedValue = textBox.Text ?? "";
                    if ((columnName.Equals("DueDate", StringComparison.OrdinalIgnoreCase) ||
                         columnName.Equals("DateReceived", StringComparison.OrdinalIgnoreCase)) &&
                        TryParseFlexibleDate(editedValue, out DateTime parsedDate))
                    {
                        editedValue = parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        textBox.Text = editedValue;
                    }
                    rowView[columnName] = editedValue;
                }
                else if (e.EditingElement is ComboBox comboBox)
                {
                    rowView[columnName] = comboBox.SelectedItem?.ToString() ?? comboBox.Text ?? "";
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning("Unable to synchronize an investigation grid edit before validation.", ex);
            }
        }

        private static string ResolveEditedColumnName(DataGrid grid, DataGridColumn column)
        {
            if (grid == null || column == null)
                return "";

            if (column is DataGridBoundColumn boundColumn && boundColumn.Binding is Binding binding && binding.Path != null)
                return binding.Path.Path ?? "";

            string header = column.Header?.ToString() ?? "";
            if (grid.Name == "dgChecklist" && header.Equals("Answer", StringComparison.OrdinalIgnoreCase))
                return "AnswerValue";
            if (grid.Name == "dgCAPAItems" && header.Equals("Type", StringComparison.OrdinalIgnoreCase))
                return "ActionType";

            return "";
        }

        private void CommitChecklistEdits()
        {
            CommitGridEdits(dgChecklist);
            CommitGridEdits(dgRootCauseWhys);
            CommitGridEdits(dgImpactAssessment);
            CommitGridEdits(dgCAPAItems);
            CommitGridEdits(dgRetesting);
            CommitGridEdits(dgDistribution);
        }

        private static void CommitGridEdits(DataGrid grid)
        {
            if (grid == null)
                return;

            try
            {
                if (grid.CurrentItem != null && grid.CurrentColumn != null)
                {
                    FrameworkElement currentContent = grid.CurrentColumn.GetCellContent(grid.CurrentItem);
                    if (currentContent is TextBox currentTextBox)
                        currentTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    else if (currentContent is ComboBox currentComboBox)
                    {
                        currentComboBox.GetBindingExpression(ComboBox.SelectedItemProperty)?.UpdateSource();
                        currentComboBox.GetBindingExpression(ComboBox.SelectedValueProperty)?.UpdateSource();
                    }
                }

                grid.CommitEdit(DataGridEditingUnit.Cell, true);
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                grid.BindingGroup?.CommitEdit();

                if (grid.CurrentItem is DataRowView currentRowView)
                    currentRowView.EndEdit();

                if (grid.ItemsSource is DataView dataView)
                {
                    foreach (DataRowView rowView in dataView)
                    {
                        if (rowView?.Row == null || rowView.Row.RowState == DataRowState.Deleted)
                            continue;
                        rowView.EndEdit();
                    }
                }
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning($"Unable to commit pending edits for investigation grid '{grid.Name}'.", ex);
            }
        }

        private bool V15TableExists(string tableName)
        {
            try
            {
                object result = DatabaseHelper.ExecuteScalar(@"
                    SELECT COUNT(*)
                    FROM INFORMATION_SCHEMA.TABLES
                    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = @TableName",
                    new[] { new SqlParameter("@TableName", SqlDbType.NVarChar, 128) { Value = tableName } });
                return result != null && result != DBNull.Value && Convert.ToInt32(result) > 0;
            }
            catch (Exception ex)
            {
                ApplicationLogger.Warning($"Unable to verify Quality Event support table '{tableName}'.", ex);
                return false;
            }
        }

        private DataTable CreateRootCauseWhysSchema()
        {
            DataTable table = new DataTable();
            table.Columns.Add("WhyID", typeof(int));
            table.Columns.Add("WhyLevel", typeof(int));
            table.Columns.Add("Question", typeof(string));
            table.Columns.Add("Answer", typeof(string));
            return table;
        }

        private DataTable CreateImpactSchema()
        {
            DataTable table = new DataTable();
            table.Columns.Add("ImpactID", typeof(int));
            table.Columns.Add("AssessmentArea", typeof(string));
            table.Columns.Add("Finding", typeof(string));
            table.Columns.Add("ImpactStatus", typeof(string));
            return table;
        }

        private DataTable CreateCAPASchema()
        {
            DataTable table = new DataTable();
            table.Columns.Add("CAPAItemID", typeof(int));
            table.Columns.Add("ActionDescription", typeof(string));
            table.Columns.Add("ActionType", typeof(string));
            table.Columns.Add("Responsible", typeof(string));
            table.Columns.Add("DueDate", typeof(string));
            table.Columns.Add("EffectivenessCheck", typeof(string));
            table.Columns.Add("CAPAStatus", typeof(string));
            return table;
        }

        private DataTable CreateRetestingSchema()
        {
            DataTable table = new DataTable();
            table.Columns.Add("RetestingID", typeof(int));
            table.Columns.Add("RetestPerformed", typeof(string));
            table.Columns.Add("SampleAliquot", typeof(string));
            table.Columns.Add("Analyst", typeof(string));
            table.Columns.Add("RetestResult", typeof(string));
            table.Columns.Add("Justification", typeof(string));
            table.Columns.Add("ScientificBasis", typeof(string));
            table.Columns.Add("DispositionUse", typeof(string));
            return table;
        }

        private DataTable CreateDistributionSchema()
        {
            DataTable table = new DataTable();
            table.Columns.Add("DistributionID", typeof(int));
            table.Columns.Add("Department", typeof(string));
            table.Columns.Add("Recipient", typeof(string));
            table.Columns.Add("DateReceived", typeof(string));
            return table;
        }

        private void LoadV15StructuredTables()
        {
            rootCauseWhysTable = LoadRootCauseWhysTable();
            impactAssessmentTable = LoadImpactAssessmentTable();
            capaItemsTable = LoadCAPAItemsTable();
            retestingTable = LoadRetestingTable();
            distributionTable = LoadDistributionTable();

            dgRootCauseWhys.ItemsSource = rootCauseWhysTable.DefaultView;
            dgImpactAssessment.ItemsSource = impactAssessmentTable.DefaultView;
            dgCAPAItems.ItemsSource = capaItemsTable.DefaultView;
            dgRetesting.ItemsSource = retestingTable.DefaultView;
            dgDistribution.ItemsSource = distributionTable.DefaultView;
        }

        private DataTable LoadRootCauseWhysTable()
        {
            if (!V15TableExists("QualityEventRootCauseWhys"))
                return CreateDefaultRootCauseWhysTable();

            DataTable table = DatabaseHelper.ExecuteQuery(@"
                SELECT WhyID, WhyLevel, Question, Answer
                FROM dbo.QualityEventRootCauseWhys
                WHERE QualityEventID = @QualityEventID
                ORDER BY WhyLevel, WhyID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } });

            if (table.Rows.Count == 0)
                table = CreateDefaultRootCauseWhysTable();
            return table;
        }

        private DataTable CreateDefaultRootCauseWhysTable()
        {
            return CreateRootCauseWhysSchema();
        }

        private DataTable LoadImpactAssessmentTable()
        {
            if (!V15TableExists("QualityEventImpactAssessments"))
                return CreateDefaultImpactTable();

            DataTable table = DatabaseHelper.ExecuteQuery(@"
                SELECT ImpactID, AssessmentArea, Finding, ImpactStatus
                FROM dbo.QualityEventImpactAssessments
                WHERE QualityEventID = @QualityEventID
                ORDER BY ImpactID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } });

            if (table.Rows.Count == 0)
                table = CreateDefaultImpactTable();
            return table;
        }

        private DataTable CreateDefaultImpactTable()
        {
            return CreateImpactSchema();
        }

        private DataTable LoadCAPAItemsTable()
        {
            if (!V15TableExists("QualityEventCAPAItems"))
                return CreateCAPASchema();

            return DatabaseHelper.ExecuteQuery(@"
                SELECT CAPAItemID, ActionDescription, ActionType, Responsible,
                       CONVERT(nvarchar(10), DueDate, 120) AS DueDate,
                       EffectivenessCheck, CAPAStatus
                FROM dbo.QualityEventCAPAItems
                WHERE QualityEventID = @QualityEventID
                ORDER BY CAPAItemID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } });
        }

        private DataTable LoadRetestingTable()
        {
            if (!V15TableExists("QualityEventRetesting"))
            {
                DataTable defaultTable = CreateRetestingSchema();
                defaultTable.Rows.Add(0, "", "", "", "", "", "", "");
                return defaultTable;
            }

            DataTable table = DatabaseHelper.ExecuteQuery(@"
                SELECT RetestingID, RetestPerformed, SampleAliquot, Analyst, RetestResult,
                       Justification, ScientificBasis, DispositionUse
                FROM dbo.QualityEventRetesting
                WHERE QualityEventID = @QualityEventID
                ORDER BY RetestingID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } });

            if (table.Rows.Count == 0)
                table.Rows.Add(0, "", "", "", "", "", "", "");
            return table;
        }

        private DataTable LoadDistributionTable()
        {
            if (!V15TableExists("QualityEventDistribution"))
                return CreateDefaultDistributionTable();

            DataTable table = DatabaseHelper.ExecuteQuery(@"
                SELECT DistributionID, Department, Recipient, CONVERT(nvarchar(10), DateReceived, 120) AS DateReceived
                FROM dbo.QualityEventDistribution
                WHERE QualityEventID = @QualityEventID
                ORDER BY DistributionID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } });

            if (table.Rows.Count == 0)
                table = CreateDefaultDistributionTable();
            return table;
        }

        private DataTable CreateDefaultDistributionTable()
        {
            return CreateDistributionSchema();
        }

        private void SaveV15StructuredTables(SqlConnection connection, SqlTransaction transaction, string changeReason)
        {
            if (!V15TableExists("QualityEventInvestigationEvidenceHistory"))
            {
                throw new InvalidOperationException(
                    "Quality Event structured-evidence history is not installed. Apply the current controlled database migration before saving the investigation.");
            }

            string effectiveReason = string.IsNullOrWhiteSpace(changeReason)
                ? "Controlled structured evidence save"
                : changeReason.Trim();

            SaveRootCauseWhysTable(connection, transaction, effectiveReason);
            SaveImpactAssessmentTable(connection, transaction, effectiveReason);
            SaveCAPAItemsTable(connection, transaction, effectiveReason);
            SaveRetestingTable(connection, transaction, effectiveReason);
            SaveDistributionTable(connection, transaction, effectiveReason);
        }

        private string CaptureStructuredEvidenceSnapshot(
            SqlConnection connection,
            SqlTransaction transaction,
            string evidenceTable)
        {
            string query = evidenceTable switch
            {
                "QualityEventRootCauseWhys" => @"
                    SELECT WhyLevel, Question, Answer
                    FROM dbo.QualityEventRootCauseWhys
                    WHERE QualityEventID=@QualityEventID
                    ORDER BY WhyLevel
                    FOR JSON PATH, INCLUDE_NULL_VALUES",
                "QualityEventImpactAssessments" => @"
                    SELECT AssessmentArea, Finding, ImpactStatus
                    FROM dbo.QualityEventImpactAssessments
                    WHERE QualityEventID=@QualityEventID
                    ORDER BY ImpactID
                    FOR JSON PATH, INCLUDE_NULL_VALUES",
                "QualityEventCAPAItems" => @"
                    SELECT ActionDescription, ActionType, Responsible,
                           CONVERT(nvarchar(10),DueDate,23) AS DueDate,
                           EffectivenessCheck, CAPAStatus
                    FROM dbo.QualityEventCAPAItems
                    WHERE QualityEventID=@QualityEventID
                    ORDER BY CAPAItemID
                    FOR JSON PATH, INCLUDE_NULL_VALUES",
                "QualityEventRetesting" => @"
                    SELECT RetestPerformed, SampleAliquot, Analyst, RetestResult,
                           Justification, ScientificBasis, DispositionUse
                    FROM dbo.QualityEventRetesting
                    WHERE QualityEventID=@QualityEventID
                    ORDER BY RetestingID
                    FOR JSON PATH, INCLUDE_NULL_VALUES",
                "QualityEventDistribution" => @"
                    SELECT Department, Recipient, CONVERT(nvarchar(10),DateReceived,23) AS DateReceived
                    FROM dbo.QualityEventDistribution
                    WHERE QualityEventID=@QualityEventID
                    ORDER BY DistributionID
                    FOR JSON PATH, INCLUDE_NULL_VALUES",
                _ => throw new InvalidOperationException("Unsupported Quality Event structured-evidence table.")
            };

            using SqlCommand command = new SqlCommand(query, connection, transaction)
            {
                CommandTimeout = AppConfig.CommandTimeoutSeconds
            };
            command.Parameters.Add("@QualityEventID", SqlDbType.Int).Value = qualityEventId;

            // FOR JSON can return long output in multiple rows. Concatenate every
            // chunk so the append-only evidence history never stores truncated JSON.
            using SqlDataReader reader = command.ExecuteReader();
            StringBuilder json = new StringBuilder();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                    json.Append(reader.GetString(0));
            }

            return json.Length == 0 ? "[]" : json.ToString();
        }

        private void RecordStructuredEvidenceHistory(
            SqlConnection connection,
            SqlTransaction transaction,
            string evidenceTable,
            string oldRowsJson,
            string changeReason)
        {
            string newRowsJson = CaptureStructuredEvidenceSnapshot(connection, transaction, evidenceTable);
            if (string.Equals(oldRowsJson, newRowsJson, StringComparison.Ordinal))
                return;

            DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                INSERT INTO dbo.QualityEventInvestigationEvidenceHistory
                    (QualityEventID, EvidenceTable, OldRowsJson, NewRowsJson, ChangedBy, ChangedAt, ChangeReason)
                VALUES
                    (@QualityEventID, @EvidenceTable, @OldRowsJson, @NewRowsJson, @ChangedBy, SYSDATETIME(), @ChangeReason)",
                new[]
                {
                    new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                    new SqlParameter("@EvidenceTable", SqlDbType.NVarChar, 100) { Value = evidenceTable },
                    new SqlParameter("@OldRowsJson", SqlDbType.NVarChar, -1) { Value = oldRowsJson },
                    new SqlParameter("@NewRowsJson", SqlDbType.NVarChar, -1) { Value = newRowsJson },
                    new SqlParameter("@ChangedBy", SqlDbType.NVarChar, 120) { Value = currentUser },
                    new SqlParameter("@ChangeReason", SqlDbType.NVarChar, 1000) { Value = changeReason }
                }, connection, transaction);
        }

        private bool HasAnyValue(DataRow row, params string[] columnNames)
        {
            foreach (string columnName in columnNames)
            {
                if (row.Table.Columns.Contains(columnName) && !string.IsNullOrWhiteSpace(row.GetSafeString(columnName)))
                    return true;
            }
            return false;
        }

        private static string NormalizeDateInput(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            StringBuilder builder = new StringBuilder(value.Trim().Length);
            foreach (char ch in value.Trim())
            {
                if (ch >= '\u0660' && ch <= '\u0669')
                    builder.Append((char)('0' + (ch - '\u0660')));
                else if (ch >= '\u06F0' && ch <= '\u06F9')
                    builder.Append((char)('0' + (ch - '\u06F0')));
                else
                    builder.Append(ch);
            }

            return builder.ToString()
                .Replace('\u2212', '-')
                .Replace('\u2013', '-')
                .Replace('\u2014', '-');
        }

        private static bool TryParseFlexibleDate(string value, out DateTime date)
        {
            date = default;
            string normalized = NormalizeDateInput(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            string[] formats =
            {
                "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d", "yyyy.MM.dd",
                "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
                "MM/dd/yyyy", "M/d/yyyy"
            };

            if (DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out date))
            {
                date = date.Date;
                return true;
            }

            CultureInfo[] cultures =
            {
                CultureInfo.CurrentCulture,
                CultureInfo.CurrentUICulture,
                CultureInfo.InvariantCulture,
                CultureInfo.GetCultureInfo("en-GB"),
                CultureInfo.GetCultureInfo("en-US"),
                CultureInfo.GetCultureInfo("ar-IQ")
            };

            foreach (CultureInfo culture in cultures)
            {
                if (DateTime.TryParse(normalized, culture, DateTimeStyles.AllowWhiteSpaces, out date))
                {
                    date = date.Date;
                    return true;
                }
            }

            return false;
        }

        private object ParseNullableDate(string value)
        {
            return TryParseFlexibleDate(value, out DateTime date) ? date.Date : DBNull.Value;
        }

        private object ParseDatabaseDateOrDBNull(DataRow row, string columnName, string fieldLabel)
        {
            if (row == null || !row.Table.Columns.Contains(columnName) || row[columnName] == DBNull.Value || row[columnName] == null)
                return DBNull.Value;

            if (row[columnName] is DateTime dateValue)
                return dateValue.Date;

            string raw = row.GetSafeString(columnName);
            if (string.IsNullOrWhiteSpace(raw))
                return DBNull.Value;

            if (!TryParseFlexibleDate(raw, out DateTime parsed))
                throw new FormatException($"{fieldLabel} '{raw}' is not a recognized date. Use YYYY-MM-DD or DD/MM/YYYY.");

            string canonical = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            row[columnName] = canonical;
            return parsed.Date;
        }

        private void SaveRootCauseWhysTable(SqlConnection connection, SqlTransaction transaction, string changeReason)
        {
            if (!V15TableExists("QualityEventRootCauseWhys"))
                return;

            string oldRowsJson = CaptureStructuredEvidenceSnapshot(connection, transaction, "QualityEventRootCauseWhys");

            DatabaseHelper.ExecuteNonQueryWithTransaction("DELETE FROM dbo.QualityEventRootCauseWhys WHERE QualityEventID = @QualityEventID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } }, connection, transaction);

            foreach (DataRow row in rootCauseWhysTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "Question", "Answer"))
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventRootCauseWhys (QualityEventID, WhyLevel, Question, Answer, CreatedBy, ModifiedBy)
                    VALUES (@QualityEventID, @WhyLevel, @Question, @Answer, @User, @User)",
                    new[] {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@WhyLevel", SqlDbType.Int) { Value = row.Table.Columns.Contains("WhyLevel") && row["WhyLevel"] != DBNull.Value ? Convert.ToInt32(row["WhyLevel"]) : 0 },
                        new SqlParameter("@Question", SqlDbType.NVarChar, 500) { Value = (object)row.GetSafeString("Question") ?? DBNull.Value },
                        new SqlParameter("@Answer", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("Answer") ?? DBNull.Value },
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = currentUser }
                    }, connection, transaction);
            }
            RecordStructuredEvidenceHistory(connection, transaction, "QualityEventRootCauseWhys", oldRowsJson, changeReason);
        }

        private void SaveImpactAssessmentTable(SqlConnection connection, SqlTransaction transaction, string changeReason)
        {
            if (!V15TableExists("QualityEventImpactAssessments"))
                return;

            string oldRowsJson = CaptureStructuredEvidenceSnapshot(connection, transaction, "QualityEventImpactAssessments");

            DatabaseHelper.ExecuteNonQueryWithTransaction("DELETE FROM dbo.QualityEventImpactAssessments WHERE QualityEventID = @QualityEventID",
                new[] { new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId } }, connection, transaction);

            foreach (DataRow row in impactAssessmentTable.Rows)
            {
                if (row.RowState == DataRowState.Deleted || !HasAnyValue(row, "AssessmentArea", "Finding", "ImpactStatus"))
                    continue;

                DatabaseHelper.ExecuteNonQueryWithTransaction(@"
                    INSERT INTO dbo.QualityEventImpactAssessments (QualityEventID, AssessmentArea, Finding, ImpactStatus, CreatedBy, ModifiedBy)
                    VALUES (@QualityEventID, @AssessmentArea, @Finding, @ImpactStatus, @User, @User)",
                    new[] {
                        new SqlParameter("@QualityEventID", SqlDbType.Int) { Value = qualityEventId },
                        new SqlParameter("@AssessmentArea", SqlDbType.NVarChar, 200) { Value = (object)row.GetSafeString("AssessmentArea") ?? DBNull.Value },
                        new SqlParameter("@Finding", SqlDbType.NVarChar, -1) { Value = (object)row.GetSafeString("Finding") ?? DBNull.Value },
                        new SqlParameter("@ImpactStatus", SqlDbType.NVarChar, 100) { Value = (object)row.GetSafeString("ImpactStatus") ?? DBNull.Value },
                        new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = currentUser }
                    }, connection, transaction);
            }
            RecordStructuredEvidenceHistory(connection, transaction, "QualityEventImpactAssessments", oldRowsJson, changeReason);
        }
    }
}
