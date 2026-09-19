using PharmaLIMS.Services;
using PharmaLIMS.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PharmaLIMS;

public partial class ExternalTrendThreeCycleReviewWindow : Window
{
    private ExternalTrendThreeCycleService service = new();
    private readonly List<ExternalTrendAreaOption> areaOptions = new();
    private IReadOnlyList<ExternalTrendAreaOption> visibleAreaOptions = Array.Empty<ExternalTrendAreaOption>();
    private CancellationTokenSource? loadCancellation;
    private readonly List<ReviewPacket> reviewPackets = new();
    private IReadOnlyList<ReviewPeriod> generatedPeriods = Array.Empty<ReviewPeriod>();
    private bool signedSnapshotSavedForCurrentReview;
    private ExternalTrendReviewSaveResult? approvedReview;
    private string approvedReviewerComment = string.Empty;
    private string approvedSignatureMeaning = string.Empty;
    private string approvedSignatureReason = string.Empty;
    private readonly List<DataCompletenessException> completenessExceptions = new();
    private bool controlsReady;
    private bool updatingReviewDates;
    private bool populationFallbackInProgress;
    private string populationFallbackNotice = string.Empty;
    private string dataSourceNotice = string.Empty;
    private string performanceReadinessNotice = string.Empty;

    public ExternalTrendThreeCycleReviewWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        controlsReady = false;
        SetDefaultReviewDates();
        try
        {
            SetBusy("Checking External Trend database performance readiness...");
            bool performanceReady = await Task.Run(service.IsPerformanceSchemaReady);
            if (!performanceReady)
            {
                performanceReadinessNotice = "PERFORMANCE WARNING: database update 20260829_001 is not applied. External Trend remains available, but large date ranges may load more slowly. Apply the controlled update when convenient.";
            }
            else
                performanceReadinessNotice = string.Empty;

            btnGenerateReview.IsEnabled = true;
            await ReloadSelectorCatalogAsync("Loading approved EM selector catalog...");
        }
        catch (Exception ex)
        {
            controlsReady = true;
            cboArea.ItemsSource = null;
            cboArea.IsEnabled = false;
            cboMethod.ItemsSource = null;
            cboParameter.ItemsSource = null;
            SetBusy("Area list could not be loaded. " + ToSafeMessage(ex));
        }
    }

    private async Task ReloadSelectorCatalogAsync(string busyText)
    {
        controlsReady = false;
        SetBusy(busyText);

        ExternalTrendImportAvailability availability = await Task.Run(service.GetImportAvailability);
        bool pendingIsNewer = availability.PendingNumericRows > 0 &&
            (!availability.LatestApprovedAt.HasValue ||
             (availability.LatestPendingAt.HasValue && availability.LatestPendingAt.Value > availability.LatestApprovedAt.Value));
        ExternalTrendDataMode desiredMode = pendingIsNewer
            ? ExternalTrendDataMode.PendingDraft
            : availability.ApprovedNumericRows > 0
                ? ExternalTrendDataMode.Approved
                : availability.PendingNumericRows > 0
                    ? ExternalTrendDataMode.PendingDraft
                    : ExternalTrendDataMode.Approved;
        if (service.DataMode != desiredMode)
            service = new ExternalTrendThreeCycleService(desiredMode);

        DataTable areaTable = await Task.Run(service.GetAreas);
        // If approved rows exist but cannot form a usable selector while a staged numeric
        // import is available, use the staged source only as an explicitly marked draft.
        // This keeps the screen usable without weakening controlled QA approval.
        if (areaTable.Rows.Count == 0 && !service.IsDraftMode && availability.PendingNumericRows > 0)
        {
            service = new ExternalTrendThreeCycleService(ExternalTrendDataMode.PendingDraft);
            areaTable = await Task.Run(service.GetAreas);
        }

        areaOptions.Clear();
        foreach (DataRow row in areaTable.Rows)
        {
            string code = (Convert.ToString(row["AreaCode"], CultureInfo.InvariantCulture) ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code))
                continue;
            string name = (Convert.ToString(row["AreaName"], CultureInfo.InvariantCulture) ?? code).Trim();
            string location = row.Table.Columns.Contains("LocationName") ? (Convert.ToString(row["LocationName"], CultureInfo.InvariantCulture) ?? string.Empty).Trim() : string.Empty;
            string population = (Convert.ToString(row["TrendPopulation"], CultureInfo.InvariantCulture) ?? "Classification Required").Trim();
            string classification = (Convert.ToString(row["AreaClassification"], CultureInfo.InvariantCulture) ?? "QA Classification Required").Trim();
            bool comparable = !row.IsNull("IsComparable") && Convert.ToBoolean(row["IsComparable"], CultureInfo.InvariantCulture);
            string display = string.Equals(code, name, StringComparison.OrdinalIgnoreCase) ? code : $"{code} — {name}";
            if (!string.IsNullOrWhiteSpace(location) && !display.Contains(location, StringComparison.OrdinalIgnoreCase))
                display += $" | {location}";
            areaOptions.Add(new ExternalTrendAreaOption(code, display, population, classification, comparable, false) { LocationName = location });
        }

        controlsReady = true;
        populationFallbackNotice = string.Empty;
        dataSourceNotice = service.IsDraftMode
            ? "DRAFT DATA SOURCE: the newest staged external EM import is Pending Approval. Trend viewing/export is allowed for review, but QA snapshot approval is blocked until the import batch is electronically approved and the review is regenerated."
            : availability.PendingNumericRows > 0
                ? "APPROVED DATA SOURCE: approved observations are shown. A pending external EM import also exists; open Import / Approve Data to review it before relying on this approved source as the latest dataset."
                : "APPROVED DATA SOURCE: QA-approved external EM observations.";
        ApplyPopulationFilter(selectFirst: true);
        btnApproveSnapshot.IsEnabled = !service.IsDraftMode;

        int reviewableCount = visibleAreaOptions.Count(item => !item.IsAllAreas);
        if (areaOptions.Count == 0)
        {
            btnGenerateReview.IsEnabled = false;
            SetBusy(DescribeImportAvailability(availability));
            return;
        }
        btnGenerateReview.IsEnabled = true;

        List<string> notices = new();
        if (!string.IsNullOrWhiteSpace(performanceReadinessNotice)) notices.Add(performanceReadinessNotice);
        if (!string.IsNullOrWhiteSpace(dataSourceNotice)) notices.Add(dataSourceNotice);
        if (!string.IsNullOrWhiteSpace(populationFallbackNotice)) notices.Add(populationFallbackNotice);
        if (reviewableCount == 0)
            notices.Add("External EM data exist, but the selected population has no comparable area profile. Choose another classification or review the imported AreaClassification values.");
        else
            notices.Add($"Selector catalog loaded: {reviewableCount:N0} reviewable area(s). Choose an area scope, method and result / parameter.");
        SetBusy(string.Join(" ", notices));
    }

    private static string DescribeImportAvailability(ExternalTrendImportAvailability availability)
    {
        if (availability.ApprovedNumericRows > 0)
            return $"Approved external EM data exist ({availability.ApprovedNumericRows:N0} numeric row(s)), but no valid selector profile could be built. Review EntityCode, Parameter and AreaClassification in the approved import.";

        if (availability.PendingNumericRows > 0)
        {
            string approvalNote = AppConfig.DevelopmentAdminFullPermissions
                ? "Development Admin override is enabled, so the same Admin account may approve its staged import for testing."
                : "A separate authorized approver must approve the staged import before controlled trend analysis.";
            return $"No approved external EM observations are available yet. {availability.PendingBatches:N0} pending batch(es) contain {availability.PendingNumericRows:N0} numeric row(s). Open Import / Approve Data. {approvalNote}";
        }

        return "No approved or pending numeric external EM data are available. Use Import / Approve Data to stage the controlled CSV/XLSX source first.";
    }

    private async void OpenImportApproval_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new ExternalTrendImportDialog("Environmental Monitoring") { Owner = this }.ShowDialog();
            await ReloadSelectorCatalogAsync("Refreshing approved EM selector catalog after import / approval...");
        }
        catch (Exception ex)
        {
            controlsReady = true;
            SetBusy("External Trend import / approval workflow could not be refreshed. " + ToSafeMessage(ex));
        }
    }

    private string CurrentDataLabel => service.IsDraftMode ? "staged pending-approval" : "QA-approved";
    private string CurrentDataRangeLabel => service.IsDraftMode ? "staged data" : "approved data";

    private async void CboArea_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateGeneratedReview();
        if (!controlsReady || cboArea.SelectedValue is not string areaCode || cboArea.SelectedItem is not ExternalTrendAreaOption profile)
            return;
        try
        {
            lblProfile.Text = profile.IsAllAreas
                ? profile.TrendPopulation.Equals("all classifications", StringComparison.OrdinalIgnoreCase)
                    ? "All classifications: each reviewable area remains a separate controlled assessment and report section."
                    : $"All {profile.TrendPopulation} areas: each area remains a separate controlled assessment and report section."
                : $"Population: {profile.TrendPopulation} | Classification: {profile.AreaClassification}.";
            cboMethod.ItemsSource = null;
            cboParameter.ItemsSource = null;
            SetBusy($"Loading methods from the {CurrentDataLabel} selector catalog...");
            string[] areaScope = CurrentVisibleAreaCodes();
            DataTable methods = await Task.Run(() => string.IsNullOrWhiteSpace(areaCode)
                ? service.GetMethodsForAreas(areaScope)
                : service.GetMethods(areaCode));
            if (!string.Equals(cboArea.SelectedValue?.ToString(), areaCode, StringComparison.Ordinal))
                return;
            cboMethod.ItemsSource = methods.DefaultView;
            cboMethod.IsEnabled = methods.Rows.Count > 0;
            if (methods.Rows.Count > 0)
                cboMethod.SelectedIndex = 0;
            else
                SetBusy("No numeric method is available for the selected area scope.");
        }
        catch (Exception ex)
        {
            SetBusy("Method list could not be loaded. " + ToSafeMessage(ex));
        }
    }

    private async void CboMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateGeneratedReview();
        if (!controlsReady || cboArea.SelectedValue is not string areaCode || cboMethod.SelectedValue is not string method)
            return;
        try
        {
            cboParameter.ItemsSource = null;
            SetBusy($"Loading result / parameter list from the {CurrentDataLabel} selector catalog...");
            string[] areaScope = CurrentVisibleAreaCodes();
            DataTable parameters = await Task.Run(() => string.IsNullOrWhiteSpace(areaCode)
                ? service.GetParametersForAreas(areaScope, method)
                : service.GetParameters(areaCode, method));
            if (!string.Equals(cboMethod.SelectedValue?.ToString(), method, StringComparison.Ordinal))
                return;
            cboParameter.ItemsSource = parameters.DefaultView;
            cboParameter.IsEnabled = parameters.Rows.Count > 0;
            if (parameters.Rows.Count > 0)
                cboParameter.SelectedIndex = 0;
            else
                SetBusy("No numeric result / parameter is available for the selected method and area scope.");
        }
        catch (Exception ex)
        {
            SetBusy("Result / parameter list could not be loaded. " + ToSafeMessage(ex));
        }
    }

    private async void CboParameter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateGeneratedReview();
        if (!controlsReady || cboArea.SelectedValue is not string areaCode || cboMethod.SelectedValue is not string method || cboParameter.SelectedValue is not string parameter)
            return;
        try
        {
            SetBusy($"Checking the {CurrentDataRangeLabel} date range...");
            string[] rangeScope = string.IsNullOrWhiteSpace(areaCode) ? CurrentVisibleAreaCodes() : new[] { areaCode };
            DataTable range = await Task.Run(() => service.GetAvailableDateRangeForAreas(rangeScope, method, parameter));
            if (!string.Equals(cboParameter.SelectedValue?.ToString(), parameter, StringComparison.Ordinal) || range.Rows.Count == 0)
                return;

            DataRow row = range.Rows[0];
            if (row.IsNull("FirstDate") || row.IsNull("LastDate"))
            {
                SetBusy("No numeric records are available for the selected method and result / parameter.");
                return;
            }

            DateTime first = Convert.ToDateTime(row["FirstDate"]);
            DateTime last = Convert.ToDateTime(row["LastDate"]);
            dpReviewEnd.SelectedDate = last;
            SyncReviewStartToScope(first);
            UpdatePeriodLabels();
            SetBusy($"{CurrentDataRangeLabel} available from {first:dd-MMM-yyyy} to {last:dd-MMM-yyyy}. The review end date was aligned to the latest available observation and the selected cycle scope was recalculated.");
        }
        catch (Exception ex)
        {
            SetBusy("External trend data date range could not be loaded. " + ToSafeMessage(ex));
        }
    }

    private void CboPopulation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        InvalidateGeneratedReview();
        if (!controlsReady || populationFallbackInProgress)
            return;
        populationFallbackNotice = string.Empty;
        ApplyPopulationFilter(selectFirst: true);
    }

    private void ApplyPopulationFilter(bool selectFirst = false)
    {
        string population = (cboPopulation.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        List<ExternalTrendAreaOption> filtered = areaOptions
            .Where(item => string.IsNullOrWhiteSpace(population) || item.TrendPopulation.Equals(population, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.IsComparable)
            .OrderBy(item => item.AreaCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filtered.Count == 0)
        {
            // Do not leave the review screen as a dead-end when approved data exist
            // under another controlled classification. Fall back visibly to All
            // classifications; the application never relabels the source rows.
            if (!string.IsNullOrWhiteSpace(population) &&
                areaOptions.Any(item => item.IsComparable) &&
                !populationFallbackInProgress)
            {
                string requestedPopulation = population;
                populationFallbackInProgress = true;
                try
                {
                    cboPopulation.SelectedIndex = 0; // All classifications
                }
                finally
                {
                    populationFallbackInProgress = false;
                }
                populationFallbackNotice = $"No {CurrentDataRangeLabel.ToLowerInvariant()} numeric external EM areas are classified as {requestedPopulation}. Showing All classifications instead; source classifications were not changed.";
                ApplyPopulationFilter(selectFirst: true);
                return;
            }

            visibleAreaOptions = Array.Empty<ExternalTrendAreaOption>();
            cboArea.ItemsSource = null;
            cboArea.SelectedIndex = -1;
            cboArea.IsEnabled = false;
            cboMethod.ItemsSource = null;
            cboMethod.IsEnabled = false;
            cboParameter.ItemsSource = null;
            cboParameter.IsEnabled = false;
            lblProfile.Text = string.IsNullOrWhiteSpace(population)
                ? $"No {CurrentDataRangeLabel.ToLowerInvariant()} numeric external EM areas are available."
                : $"No {CurrentDataRangeLabel.ToLowerInvariant()} numeric external EM areas are classified as {population}.";
            SetBusy(lblProfile.Text);
            return;
        }

        string scopeName = string.IsNullOrWhiteSpace(population) ? "all classifications" : population;
        ExternalTrendAreaOption all = new(
            string.Empty,
            string.IsNullOrWhiteSpace(population)
                ? "All Areas — separate controlled assessment for every area"
                : $"All {population} Areas — separate controlled assessment for every area",
            scopeName,
            string.IsNullOrWhiteSpace(population) ? "Mixed" : population,
            true,
            true);
        visibleAreaOptions = new[] { all }.Concat(filtered).ToArray();
        cboArea.ItemsSource = visibleAreaOptions;
        cboArea.IsEnabled = true;

        if (selectFirst || cboArea.SelectedItem is not ExternalTrendAreaOption selected || !visibleAreaOptions.Contains(selected))
            cboArea.SelectedIndex = 0;
    }

    private string[] CurrentVisibleAreaCodes() => visibleAreaOptions
        .Where(item => !item.IsAllAreas && !string.IsNullOrWhiteSpace(item.AreaCode))
        .Select(item => item.AreaCode)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private async void LoadComparison_Click(object sender, RoutedEventArgs e)
    {
        if (cboArea.SelectedValue is not string areaCode || cboMethod.SelectedValue is not string method || cboParameter.SelectedValue is not string parameter ||
            !TryGetSelectedPeriods(out IReadOnlyList<ReviewPeriod> periods))
        {
            SetBusy("Select one area, one method, one result / parameter, a review scope, and valid dates.");
            return;
        }

        try
        {
            ResetGeneratedReviewState();
            BeginLoad();
            DateTime batchFrom = periods.Min(period => period.From.Date);
            DateTime batchTo = periods.Max(period => period.To.Date);
            SetBusy($"Reading {CurrentDataLabel} environmental-monitoring data for the selected period...");
            (ExternalTrendGenerationData? Generation, Exception? Error) loadOutcome = await Task.Run(() =>
            {
                try
                {
                    return (service.GetGenerationData(method, parameter, batchFrom, batchTo), (Exception?)null);
                }
                catch (Exception ex)
                {
                    // Handle SQL/query failures inside the worker thread so Visual Studio does not
                    // stop on a user-unhandled Task exception before the WPF workflow can recover.
                    return ((ExternalTrendGenerationData?)null, ex);
                }
            });

            if (loadOutcome.Error != null || loadOutcome.Generation == null)
            {
                SetBusy("Comparison could not be loaded. " + ToSafeMessage(loadOutcome.Error ?? new InvalidOperationException("External Trend returned no generation result.")));
                return;
            }

            ExternalTrendGenerationData generation = loadOutcome.Generation;

            if (string.IsNullOrWhiteSpace(areaCode))
            {
                LoadAllAreasFromGeneration(method, parameter, periods, generation);
                if (reviewPackets.Count > 0)
                    generatedPeriods = periods.Select(period => new ReviewPeriod(period.From, period.To)).ToArray();
                return;
            }

            (TrendCycleSummary[] summaries, DataTable[] data, string sourceManifestSha256) = GetSummariesFromGeneration(generation, areaCode, periods);
            AddPeriodCompletenessExceptions(areaCode, method, parameter, summaries);
            int observationCount = summaries.Sum(item => item.Count);
            if (observationCount == 0)
            {
                dgSummary.ItemsSource = summaries.Select(summary => new AreaTrendSummaryRow(areaCode, summary)).ToList();
                TrendChart.Model = null;
                txtNarrative.Text = string.Empty;
                SetBusy("No numeric observations exist within the selected dates. No trend report was created; select dates within the displayed data range.");
                return;
            }
            dgSummary.ItemsSource = summaries.Select(summary => new AreaTrendSummaryRow(areaCode, summary)).ToList();
            string subject = $"{method} — {parameter}";
            txtNarrative.Text = BuildNarrative(areaCode, subject, summaries, ScopeDescription(), service.IsDraftMode);
            reviewPackets.Add(new ReviewPacket(areaCode, method, parameter, summaries, txtNarrative.Text, ToObservations(data), sourceManifestSha256) { LocationSummary = GetAreaLocationSummary(areaCode) });
            generatedPeriods = periods.Select(period => new ReviewPeriod(period.From, period.To)).ToArray();
            BuildComparisonChart(areaCode, summaries);
            SetBusy($"Comparison loaded: {observationCount:N0} numeric observations. Batch query mode avoided per-cycle SQL fan-out.");
        }
        catch (OperationCanceledException)
        {
            SetBusy("Review generation was cancelled. No audit snapshot was saved.");
        }
        catch (Exception ex)
        {
            SetBusy("Comparison could not be loaded. " + ToSafeMessage(ex));
        }
        finally
        {
            EndLoad();
        }
    }

    private void LoadAllAreasFromGeneration(string method, string parameter, IReadOnlyList<ReviewPeriod> periods, ExternalTrendGenerationData generation)
    {
        List<string> filteredAreaCodes = CurrentVisibleAreaCodes().ToList();
        if (filteredAreaCodes.Count == 0)
        {
            SetBusy("No areas match the selected classification.");
            return;
        }

        HashSet<string> reviewableAreaCodes = generation.SourceManifestSha256ByArea.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> areaCodes = filteredAreaCodes.Where(reviewableAreaCodes.Contains).ToList();
        List<string> excludedAreaCodes = filteredAreaCodes
            .Where(code => !reviewableAreaCodes.Contains(code))
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (areaCodes.Count == 0)
        {
            SetBusy("No external source data exist for the selected method and parameter in the selected area classification.");
            return;
        }

        CancellationToken token = loadCancellation?.Token ?? CancellationToken.None;
        progressLoad.Visibility = Visibility.Visible;
        progressLoad.Minimum = 0;
        progressLoad.Maximum = areaCodes.Count;
        progressLoad.Value = 0;
        SetBusy($"Generating {areaCodes.Count:N0} area reviews from one data batch...");
        List<AreaTrendSummaryRow> reportRows = new();
        List<string> narratives = new();
        foreach (string code in areaCodes)
        {
            token.ThrowIfCancellationRequested();
            (TrendCycleSummary[] summaries, DataTable[] data, string sourceManifestSha256) = GetSummariesFromGeneration(generation, code, periods);
            AddPeriodCompletenessExceptions(code, method, parameter, summaries);
            reportRows.AddRange(summaries.Select(summary => new AreaTrendSummaryRow(code, summary)));
            string subject = $"{method} — {parameter}";
            string narrative = BuildNarrative(code, subject, summaries, ScopeDescription(), service.IsDraftMode);
            narratives.Add($"===== {code} | {subject} ====={Environment.NewLine}{narrative}");
            reviewPackets.Add(new ReviewPacket(code, method, parameter, summaries, narrative, ToObservations(data), sourceManifestSha256) { LocationSummary = GetAreaLocationSummary(code) });
            progressLoad.Value += 1;
        }
        dgSummary.ItemsSource = reportRows;
        txtNarrative.Text = string.Join(Environment.NewLine + Environment.NewLine, narratives);
        if (reviewPackets.All(packet => packet.Observations.Count == 0))
        {
            TrendChart.Model = null;
            SetBusy("No numeric observations exist within the selected dates. No trend report was created; select dates within the displayed data range.");
            return;
        }
        if (reviewPackets.Count > 0)
        {
            BuildComparisonChart(reviewPackets[0].AreaCode, reviewPackets[0].Summaries);
            dgSummary.SelectedIndex = 0;
        }
        string excludedSuffix = excludedAreaCodes.Count > 0
            ? $" {excludedAreaCodes.Count:N0} area profile(s) outside the selected method/parameter scope were excluded and were not treated as missing data."
            : string.Empty;
        SetBusy($"Generated {areaCodes.Count:N0} separate area review(s) from one batch read for {method} — {parameter}. The default PDF is a concise portfolio summary; select detailed chart pages only when the full area pack is required." + excludedSuffix);
    }

    private static (TrendCycleSummary[] Summaries, DataTable[] Data, string SourceManifestSha256) GetSummariesFromGeneration(
        ExternalTrendGenerationData generation, string areaCode, IReadOnlyList<ReviewPeriod> periods)
    {
        if (!generation.SourceManifestSha256ByArea.TryGetValue(areaCode, out string? sourceManifestSha256))
            throw new InvalidOperationException($"No external source batch is available for {areaCode}.");

        DataTable[] data = periods.Select(period => FilterGenerationRows(generation.Rows, areaCode, period.From, period.To)).ToArray();
        TrendCycleSummary[] summaries = periods.Select((period, index) => ExternalTrendThreeCycleService.Summarize(period.Label, data[index])).ToArray();
        return (summaries, data, sourceManifestSha256);
    }

    private static DataTable FilterGenerationRows(DataTable source, string areaCode, DateTime from, DateTime to)
    {
        DataTable result = source.Clone();
        DateTimeOffset start = new(from.Date);
        DateTimeOffset endExclusive = new(to.Date.AddDays(1));
        foreach (DataRow row in source.Rows)
        {
            string canonicalArea = Convert.ToString(row["CanonicalAreaCode"], CultureInfo.InvariantCulture) ?? string.Empty;
            DateTimeOffset recordDate = row.Field<DateTimeOffset>("RecordDateTime");
            if (canonicalArea.Equals(areaCode, StringComparison.OrdinalIgnoreCase) && recordDate >= start && recordDate < endExclusive)
                result.ImportRow(row);
        }
        return result;
    }

    private static string BuildNarrative(string areaCode, string parameter, IReadOnlyList<TrendCycleSummary> cycles, string scope, bool sourceImportPending)
    {
        if (cycles.Count == 0)
        {
            return $"Scope: {areaCode} | {parameter}. {scope} No numeric observations were available in the selected dates. " +
                   "Regulatory interpretation: DATA COMPLETENESS REVIEW. No trend conclusion may be made until monitoring-plan completion and source-data completeness are assessed.";
        }

        TrendCycleSummary first = cycles[0];
        TrendCycleSummary latest = cycles[^1];
        decimal? alertLimit = cycles.Select(item => item.AlertLimit).FirstOrDefault(value => value.HasValue);
        decimal? actionLimit = cycles.Select(item => item.ActionLimit).FirstOrDefault(value => value.HasValue);
        string unit = cycles.Select(item => item.Unit).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        string trendConclusion = ReviewPacket.CalculateTrendConclusion(cycles);
        string excursionStatus = ReviewPacket.CalculateExcursionStatus(cycles);
        string disposition = ReviewPacket.CalculateRecommendedDisposition(cycles);
        string interpretation = ReviewPacket.CalculateRegulatoryInterpretation(cycles);
        string meanShift = ReviewPacket.CalculateMeanShiftDescription(cycles);

        string sourceStatement = sourceImportPending
            ? "SOURCE STATUS: DRAFT - observations come from a staged external import that is still Pending Approval. Results may be reviewed/exported for development or QA preparation, but no controlled QA snapshot may be approved until the import is approved and the review regenerated."
            : "Only QA-approved external-source observations are included.";

        List<string> lines = new()
        {
            $"Scope: {areaCode} | {parameter}. {scope} {sourceStatement}",
            "Statistical summary: " + string.Join("; ", cycles.Select((item, index) => $"P{index + 1} n={item.Count}, min={Number(item.Minimum)}, mean={Number(item.Mean)}, median={Number(item.Median)}, max={Number(item.Maximum)} {item.Unit}")) + ".",
            $"Three-cycle result: Trend={trendConclusion}; Excursion={excursionStatus}; Mean shift={meanShift}.",
            $"Regulatory interpretation: {interpretation}",
            $"Recommended QA disposition: {disposition}. This is a decision-support recommendation and does not replace the approved deviation/CAPA workflow or QA judgement."
        };

        if (excursionStatus == "ACTION")
        {
            int totalAction = cycles.Sum(item => item.ActionCount);
            string distribution = string.Join(", ", cycles.Select((item, index) => $"P{index + 1}={item.ActionCount}"));
            lines.Add($"Excursion follow-up: {totalAction} action-level observation(s) are present across the complete review ({distribution}). Verify every applicable investigation/deviation and CAPA/effectiveness record before final QA disposition.");
        }
        else if (excursionStatus == "ALERT")
        {
            int totalAlert = cycles.Sum(item => item.AlertCount);
            string distribution = string.Join(", ", cycles.Select((item, index) => $"P{index + 1}={item.AlertCount} ({item.AlertRate:0.#}%)"));
            lines.Add($"Excursion follow-up: {totalAlert} alert-level observation(s) are present across the complete review ({distribution}). Review recurrence, location/organism pattern and the need for enhanced monitoring under the approved procedure.");
        }
        else if (trendConclusion == "INCREASING")
            lines.Add($"Trend follow-up: cycle means increase sequentially ({first.Mean} -> {cycles[1].Mean} -> {latest.Mean}). No automatic failure threshold is invented; QA should assess whether the direction represents meaningful drift in the context of process/facility controls and historical performance.");

        bool incompleteLimits = cycles.Any(item => item.Count > 0 && !item.LimitsComplete);
        bool changedLimits = cycles.Any(item => item.Count > 0 && !item.LimitsConsistent);
        bool inconsistentUnits = cycles.Any(item => item.Count > 0 && !item.UnitsConsistent);
        if (inconsistentUnits)
            lines.Add("Data quality: missing or mixed result units were detected. Direct numerical comparison is not considered controlled until the source-unit issue is resolved.");
        if (incompleteLimits)
            lines.Add("Limits: one or more approved observations do not contain complete alert/action limits. The report must not infer normal excursion status from missing limits.");
        else if (changedLimits)
            lines.Add("Limits: approved alert/action limits changed within the selected review period. Excursion counts were evaluated against each observation's own approved source limit; a single limit line is intentionally not presented as if it applied to the entire period.");
        else if (!alertLimit.HasValue || !actionLimit.HasValue)
            lines.Add("Limits: controlled alert/action limits were not present in the approved source data. The report remains descriptive and excursion compliance must not be inferred from missing limits.");
        else
            lines.Add($"Approved source limits used: alert {alertLimit} {unit}; action {actionLimit} {unit}.");

        lines.Add("Required QA review considerations: repeated locations, organism identification where available, cleaning/disinfection, HVAC/pressure/temperature/humidity, personnel/material flow, maintenance or production events, seasonal effects, method/media performance, deviations and CAPA effectiveness as applicable.");
        lines.Add("Regulatory alignment: ICH Q10 process-performance/product-quality monitoring; ICH Q9(R1) quality risk management; WHO GMP pharmaceutical quality-system principles; and the approved MEDICA environmental-monitoring procedure. The software does not infer microbiological root cause automatically.");
        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static IReadOnlyList<TrendObservation> ToObservations(IEnumerable<DataTable> periods)
    {
        return periods.SelectMany(table => table.Rows.Cast<DataRow>())
            .Where(row => row.Field<decimal?>("ResultValue").HasValue)
            .Select(row => new TrendObservation(
                row.Field<DateTimeOffset>("RecordDateTime").DateTime,
                row.Field<decimal?>("ResultValue")!.Value,
                Convert.ToString(row["ResultQualifier"], CultureInfo.InvariantCulture) ?? string.Empty,
                row.Field<decimal?>("AlertLimit"),
                row.Field<decimal?>("ActionLimit")))
            .OrderBy(item => item.RecordDate)
            .ToList();
    }

    private void SetDefaultReviewDates()
    {
        DateTime authoritativeToday;
        try
        {
            authoritativeToday = service.GetDatabaseTime().Date;
        }
        catch (Exception ex)
        {
            ApplicationLogger.Warning(
                "Authoritative database time was unavailable while initializing External Trend review dates. Workstation time was not substituted.",
                ex);
            updatingReviewDates = true;
            try
            {
                dpReviewEnd.SelectedDate = null;
                dpReviewStart.SelectedDate = null;
            }
            finally
            {
                updatingReviewDates = false;
            }
            UpdatePeriodLabels();
            lblDateValidation.Text = "Authoritative database time is unavailable. Restore database connectivity before selecting the review period.";
            return;
        }

        updatingReviewDates = true;
        try
        {
            dpReviewEnd.SelectedDate = authoritativeToday;
            dpReviewStart.SelectedDate = authoritativeToday.AddMonths(-6).AddDays(1);
        }
        finally
        {
            updatingReviewDates = false;
        }
        SyncReviewStartToScope();
        UpdatePeriodLabels();
    }

    private void ReviewEndDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!controlsReady || updatingReviewDates) return;
        InvalidateGeneratedReview();
        string scope = (cboReviewScope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Custom";
        if (!scope.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            SyncReviewStartToScope();
        UpdatePeriodLabels();
    }

    private void ReviewScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!controlsReady || updatingReviewDates) return;
        InvalidateGeneratedReview();
        SyncReviewStartToScope();
        UpdatePeriodLabels();
    }

    private void InvalidateGeneratedReview()
    {
        if (!controlsReady || reviewPackets.Count == 0)
            return;

        ResetGeneratedReviewState();
        dgSummary.ItemsSource = null;
        TrendChart.Model = null;
        txtNarrative.Text = string.Empty;
        SetBusy("Review selections changed. Generate the review again before QA approval.");
    }

    private void ResetGeneratedReviewState()
    {
        reviewPackets.Clear();
        completenessExceptions.Clear();
        generatedPeriods = Array.Empty<ReviewPeriod>();
        signedSnapshotSavedForCurrentReview = false;
        approvedReview = null;
        approvedReviewerComment = string.Empty;
        approvedSignatureMeaning = string.Empty;
        approvedSignatureReason = string.Empty;
        if (txtReviewerComment != null)
            txtReviewerComment.Text = string.Empty;
        if (btnApproveSnapshot != null)
            btnApproveSnapshot.IsEnabled = !service.IsDraftMode;
    }

    private void SyncReviewStartToScope(DateTime? earliestAvailable = null)
    {
        if (dpReviewEnd?.SelectedDate is not DateTime end || cboReviewScope == null || cboCycleMonths == null)
            return;

        string scope = (cboReviewScope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Custom";
        bool custom = scope.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        dpReviewStart.IsEnabled = custom;
        pnlCycleLength.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        lblStartDateCaption.Text = custom ? "From date" : "Calculated start date";
        if (custom)
        {
            if (earliestAvailable.HasValue)
            {
                updatingReviewDates = true;
                try { dpReviewStart.SelectedDate = earliestAvailable.Value.Date; }
                finally { updatingReviewDates = false; }
            }
            return;
        }

        int cycleMonths = int.TryParse((cboCycleMonths.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int months) ? months : 6;
        int periodCount = scope == "Year" ? 1 : int.TryParse(scope, out int count) ? count : 3;
        int totalMonths = scope == "Year" ? 12 : checked(periodCount * cycleMonths);
        DateTime calculatedStart = end.Date.AddMonths(-totalMonths).AddDays(1);
        updatingReviewDates = true;
        try { dpReviewStart.SelectedDate = calculatedStart; }
        finally { updatingReviewDates = false; }
    }

    private void AddPeriodCompletenessExceptions(string areaCode, string method, string parameter, IReadOnlyList<TrendCycleSummary> summaries)
    {
        foreach (TrendCycleSummary summary in summaries)
        {
            if (summary.Count == 0)
            {
                completenessExceptions.Add(new DataCompletenessException(areaCode, method, parameter, summary.Cycle, 0, "No approved numeric observations"));
                continue;
            }
            if (summary.ExactCount < 2)
                completenessExceptions.Add(new DataCompletenessException(areaCode, method, parameter, summary.Cycle, summary.Count, "Fewer than two exact numeric observations are available for quantitative period comparison"));
            if (summary.CensoredCount > 0)
                completenessExceptions.Add(new DataCompletenessException(areaCode, method, parameter, summary.Cycle, summary.Count, $"{summary.CensoredCount} qualified/censored result(s); quantitative statistics exclude them"));
            if (!summary.UnitsConsistent)
                completenessExceptions.Add(new DataCompletenessException(areaCode, method, parameter, summary.Cycle, summary.Count, "Missing or mixed result units"));
            if (!summary.LimitsComplete)
                completenessExceptions.Add(new DataCompletenessException(areaCode, method, parameter, summary.Cycle, summary.Count, "Incomplete approved alert/action limits"));
            else if (!summary.LimitsConsistent)
                completenessExceptions.Add(new DataCompletenessException(areaCode, method, parameter, summary.Cycle, summary.Count, "Approved alert/action limits changed within period"));
        }
    }

    private IReadOnlyList<DataCompletenessException> GetReportCompletenessExceptions()
    {
        List<DataCompletenessException> consolidated = completenessExceptions
            .GroupBy(item => string.Join("|",
                item.AreaCode.Trim().ToUpperInvariant(),
                item.MethodName.Trim().ToUpperInvariant(),
                item.ParameterName.Trim().ToUpperInvariant(),
                item.Period.Trim().ToUpperInvariant(),
                item.Issue.Trim().ToUpperInvariant()), StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.ObservationCount).First())
            .ToList();

        if (reviewPackets.Count > 1 && generatedPeriods.Count > 0)
        {
            for (int index = 0; index < generatedPeriods.Count; index++)
            {
                string periodLabel = generatedPeriods[index].Label;
                bool globallyEmpty = reviewPackets.All(packet =>
                    packet.Summaries.Count <= index || packet.Summaries[index].Count == 0);
                if (!globallyEmpty)
                    continue;

                consolidated.RemoveAll(item =>
                    item.Period.Equals(periodLabel, StringComparison.OrdinalIgnoreCase) &&
                    item.ObservationCount == 0 &&
                    item.Issue.Equals("No approved numeric observations", StringComparison.OrdinalIgnoreCase));
                ReviewPacket first = reviewPackets[0];
                consolidated.Add(new DataCompletenessException(
                    "ALL INCLUDED AREAS",
                    first.MethodName,
                    first.ParameterName,
                    periodLabel,
                    0,
                    "No numeric observations in any included area"));
            }
        }

        return consolidated
            .OrderBy(item => item.Period, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.AreaCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Issue, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdatePeriodLabels()
    {
        if (dpReviewStart == null || dpReviewEnd == null || cboReviewScope == null || cboCycleMonths == null)
            return;

        string scope = (cboReviewScope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Custom";
        bool custom = scope.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        dpReviewStart.IsEnabled = custom;
        pnlCycleLength.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        lblStartDateCaption.Text = custom ? "From date" : "Calculated start date";
        lblReviewBasis.Text = custom
            ? "Custom review range: both dates are user-selected and the exact range is retained in the review and approval snapshot."
            : scope == "3"
                ? "Three-cycle comparison: dates are calculated from the selected end date and cycle length; the exact periods shown are used for analysis and reporting."
                : "Optional cycle comparison: dates are calculated from the selected end date and cycle length; the exact periods shown are used for analysis and reporting.";

        if (TryGetSelectedPeriods(out IReadOnlyList<ReviewPeriod> periods))
        {
            lblDateValidation.Text = string.Empty;
            lblPeriods.Text = string.Join("   |   ", periods.Select((period, index) => $"Period {index + 1}: {period.Label}"));
            return;
        }

        lblPeriods.Text = string.Empty;
        lblDateValidation.Text = "Select a valid review date range.";
    }

    private void BeginLoad()
    {
        loadCancellation?.Dispose();
        loadCancellation = new CancellationTokenSource();
        btnCancel.IsEnabled = true;
        progressLoad.Visibility = Visibility.Visible;
        progressLoad.IsIndeterminate = true;
    }

    private void EndLoad()
    {
        btnCancel.IsEnabled = false;
        progressLoad.IsIndeterminate = false;
        progressLoad.Visibility = Visibility.Collapsed;
        loadCancellation?.Dispose();
        loadCancellation = null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => loadCancellation?.Cancel();

    private void BuildComparisonChart(string areaCode, IReadOnlyList<TrendCycleSummary> summaries)
    {
        PlotModel model = new() { Title = $"{areaCode} — mean result by date period" };
        CategoryAxis axis = new() { Position = AxisPosition.Bottom };
        foreach (TrendCycleSummary item in summaries)
            axis.Labels.Add(item.Cycle);
        model.Axes.Add(axis);
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Title = summaries.FirstOrDefault()?.Unit ?? string.Empty, MinimumPadding = 0 });
        LineSeries meanSeries = new() { Title = "Mean", Color = OxyColors.SteelBlue, MarkerType = MarkerType.Circle, MarkerSize = 5, StrokeThickness = 2 };
        for (int index = 0; index < summaries.Count; index++)
        {
            if (summaries[index].Mean.HasValue)
                meanSeries.Points.Add(new DataPoint(index, (double)summaries[index].Mean!.Value));
        }
        model.Series.Add(meanSeries);
        decimal? alert = ConsistentSummaryLimit(summaries, item => item.AlertLimit);
        decimal? action = ConsistentSummaryLimit(summaries, item => item.ActionLimit);
        AddLimitSeries(model, "Alert limit", alert, OxyColors.DarkOrange, summaries.Count);
        AddLimitSeries(model, "Action limit", action, OxyColors.Red, summaries.Count);
        if (summaries.Any(item => item.Count > 0 && (!item.LimitsComplete || !item.LimitsConsistent)))
            model.Subtitle = "Approved limits are incomplete or changed within the review; see Period Summary / Interpretation.";
        else if (summaries.Any(item => item.CensoredCount > 0))
            model.Subtitle = "Qualified/censored results are retained as evidence but excluded from cycle mean/median/min/max statistics.";
        TrendChart.Model = model;
    }

    private static decimal? ConsistentSummaryLimit(IReadOnlyList<TrendCycleSummary> summaries, Func<TrendCycleSummary, decimal?> selector)
    {
        List<TrendCycleSummary> populated = summaries.Where(item => item.Count > 0).ToList();
        if (populated.Count == 0 || populated.Any(item => !item.LimitsComplete || !item.LimitsConsistent))
            return null;
        List<decimal> values = populated.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }

    private void DgSummary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (dgSummary.SelectedItem is not AreaTrendSummaryRow selected || reviewPackets.Count == 0)
            return;

        ReviewPacket? packet = reviewPackets.FirstOrDefault(item =>
            item.AreaCode.Equals(selected.Area, StringComparison.OrdinalIgnoreCase));
        if (packet == null)
            return;

        BuildComparisonChart(packet.AreaCode, packet.Summaries);
        lblChartHint.Text = $"Showing {packet.AreaCode}. Select another area row in Period Summary to change the chart.";
    }

    private static void AddLimitSeries(PlotModel model, string title, decimal? value, OxyColor color, int count)
    {
        if (!value.HasValue) return;
        LineSeries line = new() { Title = title, Color = color, LineStyle = LineStyle.Dash, MarkerType = MarkerType.None, StrokeThickness = 2 };
        for (int index = 0; index < count; index++)
            line.Points.Add(new DataPoint(index, (double)value.Value));
        model.Series.Add(line);
    }

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (service.IsDraftMode)
        {
            SetBusy("QA snapshot approval is blocked because the current review uses a staged import that is still Pending Approval. Open Import / Approve Data, electronically approve the import, refresh, regenerate the review, then approve the snapshot.");
            return;
        }
        if (signedSnapshotSavedForCurrentReview)
        {
            SetBusy("This generated review has already been electronically approved. Generate a new review to create another controlled snapshot.");
            return;
        }
        if (reviewPackets.Count == 0 || generatedPeriods.Count == 0)
        {
            SetBusy("Generate a review before QA electronic approval.");
            return;
        }
        if (generatedPeriods.Count < 1 || generatedPeriods.Count > 3)
        {
            SetBusy("QA approval supports one to three selected review periods. Generate the review again using a valid date range.");
            return;
        }
        if (ReviewContainsUnresolvedClassification())
        {
            SetBusy("QA approval is blocked because one or more reviewed areas use legacy/unresolved AreaClassification. The trend may be reviewed as a draft, but classification must be reconciled before a controlled snapshot is approved.");
            return;
        }

        string comment = txtReviewerComment.Text?.Trim() ?? string.Empty;
        if (comment.Length < 10)
        {
            SetBusy("Enter a QA reviewer comment of at least 10 characters before electronic approval.");
            txtReviewerComment.Focus();
            return;
        }

        string user = string.IsNullOrWhiteSpace(Login.CurrentUser) ? string.Empty : Login.CurrentUser.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            SetBusy("No authenticated user is available for electronic approval. Sign in again.");
            return;
        }
        if (!DatabaseHelper.CanApproveResults(user))
        {
            SetBusy("Your account is not authorized to approve external trend reviews.");
            return;
        }

        if (!await EnsureApprovalSnapshotSchemaAsync())
            return;

        string recordLabel = reviewPackets.Count == 1
            ? $"External EM Trend | {reviewPackets[0].AreaCode} | {reviewPackets[0].DisplaySubject}"
            : $"External EM Trend | {reviewPackets.Count} areas | {reviewPackets[0].DisplaySubject}";
        ElectronicSignature signature = new(recordLabel, user, "External EM Trend Review Approval")
        {
            Owner = this
        };
        if (signature.ShowDialog() != true || !signature.IsConfirmed)
        {
            SetBusy("External trend review approval was cancelled. No signed snapshot was saved.");
            return;
        }

        try
        {
            BeginLoad();
            btnApproveSnapshot.IsEnabled = false;
            TrendReviewPeriodSnapshot[] periods = generatedPeriods
                .Select(period => new TrendReviewPeriodSnapshot(period.From, period.To))
                .ToArray();
            List<ExternalTrendReviewSnapshotRequest> requests = reviewPackets.Select(packet =>
                new ExternalTrendReviewSnapshotRequest(
                    packet.AreaCode,
                    packet.MethodName,
                    packet.ParameterName,
                    periods,
                    packet.Summaries,
                    packet.Observations.Select(item => new ExternalTrendObservationSnapshot(item.RecordDate, item.Result, item.ResultQualifier, item.AlertLimit, item.ActionLimit)).ToArray(),
                    packet.Narrative,
                    comment,
                    packet.SourceManifestSha256))
                .ToList();

            ExternalTrendReviewSaveResult result = await Task.Run(() =>
                service.SaveSignedReviewSnapshots(
                    requests,
                    signature.SignedBy,
                    signature.Meaning,
                    signature.Reason));

            signedSnapshotSavedForCurrentReview = true;
            approvedReview = result;
            approvedReviewerComment = comment;
            approvedSignatureMeaning = signature.Meaning;
            approvedSignatureReason = signature.Reason;
            btnApproveSnapshot.IsEnabled = false;
            SetBusy($"QA electronic approval saved: {result.SnapshotCount:N0} immutable snapshot(s), signed by {result.SignedBy} ({result.ReviewerRole}) at {result.SignedAt:yyyy-MM-dd HH:mm:ss zzz}. PDF export can now create a controlled presentation of these approved snapshots; CSV remains a draft data export.");
        }
        catch (OperationCanceledException)
        {
            btnApproveSnapshot.IsEnabled = true;
            SetBusy("External trend review approval was cancelled. No signed snapshot was saved.");
        }
        catch (Exception ex)
        {
            btnApproveSnapshot.IsEnabled = true;
            SetBusy("External trend review approval could not be saved. " + ToSafeMessage(ex));
        }
        finally
        {
            EndLoad();
        }
    }

    private bool ReviewContainsUnresolvedClassification()
    {
        HashSet<string> reviewedAreas = reviewPackets
            .Select(packet => packet.AreaCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool unresolvedArea = areaOptions.Any(option =>
            reviewedAreas.Contains(option.AreaCode) &&
            (option.TrendPopulation.Equals("Classification Required", StringComparison.OrdinalIgnoreCase) ||
             option.TrendPopulation.Equals("Mixed classification", StringComparison.OrdinalIgnoreCase)));
        bool unresolvedMethod = reviewPackets.Any(packet =>
            packet.MethodName.Equals("Unspecified method", StringComparison.OrdinalIgnoreCase));
        return unresolvedArea || unresolvedMethod;
    }

    private async Task<bool> EnsureApprovalSnapshotSchemaAsync()
    {
        try
        {
            if (await Task.Run(service.IsApprovalSnapshotSchemaReady))
                return true;

            string guidance = AppConfig.IsDevelopment
                ? "Run System Preflight and the explicit Development Database Maintenance action before approving an External Trend review."
                : "Apply the approved Production database deployment before approving an External Trend review.";

            SetBusy(
                "External trend approval is blocked because the controlled snapshot schema is incomplete. " +
                "Operational workflow screens never apply database migrations. " + guidance);
            return false;
        }
        catch (Exception ex)
        {
            ApplicationLogger.Error("External Trend approval schema readiness check failed.", ex);
            SetBusy("External Trend approval remains blocked. " + ToSafeMessage(ex));
            return false;
        }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (reviewPackets.Count == 0 || reviewPackets.All(packet => packet.Observations.Count == 0))
        {
            SetBusy("A trend CSV cannot be exported because the selected dates contain no numeric observations.");
            return;
        }

        SaveFileDialog dialog = new() { Filter = "Excel-compatible CSV|*.csv", FileName = "EM_Three_Cycle_Trend_Review.csv" };
        if (dialog.ShowDialog(this) != true) return;
        StringBuilder csv = new();
        csv.AppendLine("Area,Parameter,Period,Observations,Minimum,Mean,Median,Maximum,Alert Count,Alert Rate %,Action Count,Action Rate %,Unit,Excursion Status,Trend Conclusion");
        foreach (ReviewPacket packet in reviewPackets)
        {
            foreach (TrendCycleSummary item in packet.Summaries)
            {
                csv.AppendLine(string.Join(",", Csv(packet.AreaCode), Csv(packet.DisplaySubject), Csv(item.Cycle), item.Count,
                    item.Minimum, item.Mean, item.Median, item.Maximum, item.AlertCount, item.AlertRate, item.ActionCount, item.ActionRate,
                    Csv(item.Unit), Csv(item.Signal), Csv(packet.TrendConclusion)));
            }
        }

        IReadOnlyList<DataCompletenessException> reportExceptions = GetReportCompletenessExceptions();
        if (reportExceptions.Count > 0)
        {
            csv.AppendLine();
            csv.AppendLine("DATA COMPLETENESS EXCEPTIONS");
            csv.AppendLine("Area,Parameter,Period,Observations,Issue");
            foreach (DataCompletenessException item in reportExceptions)
                csv.AppendLine(string.Join(",", Csv(item.AreaCode), Csv($"{item.MethodName} — {item.ParameterName}"), Csv(item.Period), item.ObservationCount, Csv(item.Issue)));
        }

        File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
        SetBusy(service.IsDraftMode
            ? "Excel-compatible CSV export completed from a staged Pending Approval source. This is a DRAFT review record and cannot be treated as controlled QA-approved output."
            : "Excel-compatible CSV export completed. This export is a draft review record, not an electronic approval.");
    }

    private void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (reviewPackets.Count == 0)
        {
            SetBusy("Generate a review before exporting.");
            return;
        }

        bool approved = signedSnapshotSavedForCurrentReview && approvedReview != null;
        List<ReviewPacket> dataPackets = reviewPackets.Where(packet => packet.Observations.Count > 0).ToList();
        if (dataPackets.Count == 0)
        {
            SetBusy("PDF export requires at least one area with numeric observations.");
            return;
        }

        DateTimeOffset generatedAt;
        try
        {
            generatedAt = service.GetDatabaseTime();
        }
        catch (Exception ex)
        {
            SetBusy("PDF export is blocked because the controlled database time could not be read. " + ToSafeMessage(ex));
            return;
        }

        string generatedBy = !string.IsNullOrWhiteSpace(Login.CurrentUserFullName)
            ? Login.CurrentUserFullName.Trim()
            : !string.IsNullOrWhiteSpace(Login.CurrentUser)
                ? Login.CurrentUser.Trim()
                : "Authenticated user";

        bool includeDetailedAreaPages = dataPackets.Count <= 1 || chkIncludeAreaPages.IsChecked == true;
        IReadOnlyList<DataCompletenessException> reportExceptions = GetReportCompletenessExceptions();
        string defaultName = approved
            ? includeDetailedAreaPages ? "EM_Three_Cycle_Trend_Review_FULL_APPROVED.pdf" : "EM_Three_Cycle_Trend_Summary_APPROVED.pdf"
            : includeDetailedAreaPages ? "EM_Three_Cycle_Trend_Review_FULL_DRAFT.pdf" : "EM_Three_Cycle_Trend_Summary_DRAFT.pdf";
        SaveFileDialog dialog = new() { Filter = "PDF report|*.pdf", FileName = defaultName };
        if (dialog.ShowDialog(this) != true) return;

        using PdfDocument document = new();
        document.Info.Title = "MEDICA External Environmental Monitoring Trend Review Report";
        document.Info.Author = "MEDICA PHARMACEUTICAL INDUSTRY - Microbiology Department";
        document.Info.Subject = approved
            ? "QA-approved external environmental-monitoring trend review based on immutable PharmaLIMS snapshots."
            : service.IsDraftMode
                ? "DRAFT external environmental-monitoring trend review generated from a staged source import that is still Pending Approval."
                : "Controlled draft external environmental-monitoring trend review pending QA electronic approval.";
        document.Info.Keywords = "Environmental Monitoring; Trend Review; ICH Q10; ICH Q9(R1); WHO GMP; PharmaLIMS";

        WritePdfCoverPage(document, generatedPeriods, dataPackets, includeDetailedAreaPages, reportExceptions.Count,
            approvedReview, approvedReviewerComment, approvedSignatureMeaning, approvedSignatureReason, generatedAt, generatedBy, service.IsDraftMode);
        if (dataPackets.Count > 1)
            WritePdfExecutiveSummary(document, dataPackets, generatedPeriods, reportExceptions, approvedReview);
        if (includeDetailedAreaPages)
        {
            foreach (ReviewPacket packet in dataPackets)
                WritePdfPage(document, packet, generatedPeriods, approvedReview, service.IsDraftMode);
        }
        if (reportExceptions.Count > 0)
            WriteDataCompletenessAppendix(document, reportExceptions, approvedReview);
        WriteRegulatoryMethodologyAppendix(document, approvedReview);
        document.Save(dialog.FileName);
        SetBusy(approved
            ? $"Approved Medica-formatted trend report exported from {approvedReview!.SnapshotCount:N0} immutable QA-approved snapshot(s); {reportExceptions.Count:N0} data-completeness finding(s) are included."
            : $"Draft Medica-formatted trend report exported: {dataPackets.Count:N0} area(s), {(includeDetailedAreaPages ? dataPackets.Count : 0):N0} detailed area page(s), {reportExceptions.Count:N0} data-completeness finding(s)." );
    }

    private static void WritePdfCoverPage(
        PdfDocument document,
        IReadOnlyList<ReviewPeriod> periods,
        IReadOnlyList<ReviewPacket> packets,
        bool includeDetailedAreaPages,
        int completenessFindingCount,
        ExternalTrendReviewSaveResult? approval,
        string reviewerComment,
        string signatureMeaning,
        string signatureReason,
        DateTimeOffset generatedAt,
        string generatedBy,
        bool sourceImportPending)
    {
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(842); page.Height = XUnit.FromPoint(595);
        using XGraphics gfx = XGraphics.FromPdfPage(page);
        DrawControlledHeader(gfx);

        XFont title = new("Arial", 16, XFontStyleEx.Bold);
        XFont subTitle = new("Arial", 9.5, XFontStyleEx.Bold);
        XFont label = new("Arial", 7.2, XFontStyleEx.Bold);
        XFont text = new("Arial", 7.2, XFontStyleEx.Regular);
        DrawCentered(gfx, "EXTERNAL ENVIRONMENTAL MONITORING TREND REVIEW REPORT", title, 119);
        DrawCentered(gfx, periods.Count == 3 ? "Controlled Three-Cycle Review" : "Exploratory Review - QA approval not permitted", subTitle, 139);

        PortfolioAssessment portfolio = BuildPortfolioAssessment(packets, completenessFindingCount);
        string reviewRef = approval == null
            ? $"DRAFT-{generatedAt:yyyyMMdd-HHmmss}"
            : $"EMTR-{approval.SignedAt:yyyy}-{approval.SnapshotIds.Min():D6}";
        string status = approval != null
            ? "QA APPROVED - controlled presentation of immutable PharmaLIMS snapshot(s)"
            : sourceImportPending
                ? "DRAFT - SOURCE IMPORT PENDING APPROVAL"
                : "DRAFT - QA APPROVAL PENDING";
        string methodParameter = packets.Count == 0 ? "-" : packets[0].DisplaySubject;
        string presentation = packets.Count > 1 && !includeDetailedAreaPages
            ? "Portfolio summary; detailed area pages omitted by user selection"
            : "Full portfolio with area detail pages";

        DrawPdfInfoCell(gfx, "Review Ref.", reviewRef, label, text, 45, 153, 180, 28);
        DrawPdfInfoCell(gfx, "Status", status, label, text, 231, 153, 150, 28);
        DrawPdfInfoCell(gfx, "Generated By", generatedBy, label, text, 387, 153, 180, 28);
        DrawPdfInfoCell(gfx, "Generated On", generatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), label, text, 573, 153, 224, 28);

        DrawPdfInfoCell(gfx, "Method / Parameter", methodParameter, label, text, 45, 186, 336, 30);
        DrawPdfInfoCell(gfx, "Areas", packets.Count.ToString("N0", CultureInfo.InvariantCulture), label, text, 387, 186, 90, 30);
        DrawPdfInfoCell(gfx, "Presentation", presentation, label, text, 483, 186, 314, 30);

        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)), 45, 222, 752, 54);
        gfx.DrawRectangle(XPens.LightGray, 45, 222, 752, 54);
        DrawPdfText(gfx, "REGULATORY / PROCEDURAL REVIEW BASIS", label, XBrushes.DarkBlue, 55, 236);
        DrawWrappedText(gfx,
            "Risk-based quality review aligned with ICH Q10 process-performance/product-quality monitoring, ICH Q9(R1) quality risk management, WHO GMP pharmaceutical quality-system principles, and the approved site environmental-monitoring procedure. Alert/action limits and event handling remain governed by approved site procedures; this automated interpretation does not replace QA judgement or deviation/CAPA records.",
            text, XBrushes.Black, 55, 249, 732, 10.5, 3);

        double cardWidth = 120;
        double gap = 6.4;
        double cardX = 45;
        DrawMetricCard(gfx, "AREAS", portfolio.AreaCount.ToString("N0", CultureInfo.InvariantCulture), cardX, 284, cardWidth); cardX += cardWidth + gap;
        DrawMetricCard(gfx, "OBSERVATIONS", portfolio.TotalObservations.ToString("N0", CultureInfo.InvariantCulture), cardX, 284, cardWidth); cardX += cardWidth + gap;
        DrawMetricCard(gfx, "ACTION", portfolio.ActionAreas.ToString("N0", CultureInfo.InvariantCulture), cardX, 284, cardWidth, portfolio.ActionAreas > 0 ? XBrushes.DarkRed : XBrushes.DarkGreen); cardX += cardWidth + gap;
        DrawMetricCard(gfx, "ALERT", portfolio.AlertAreas.ToString("N0", CultureInfo.InvariantCulture), cardX, 284, cardWidth, portfolio.AlertAreas > 0 ? XBrushes.DarkOrange : XBrushes.DarkGreen); cardX += cardWidth + gap;
        DrawMetricCard(gfx, "INCREASING", portfolio.IncreasingAreas.ToString("N0", CultureInfo.InvariantCulture), cardX, 284, cardWidth, portfolio.IncreasingAreas > 0 ? XBrushes.DarkOrange : XBrushes.DarkGreen); cardX += cardWidth + gap;
        DrawMetricCard(gfx, "DATA GAPS", portfolio.DataGapCount.ToString("N0", CultureInfo.InvariantCulture), cardX, 284, cardWidth, portfolio.DataGapCount > 0 ? XBrushes.DarkOrange : XBrushes.DarkGreen);

        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)), 45, 330, 752, 92);
        gfx.DrawRectangle(XPens.LightGray, 45, 330, 752, 92);
        DrawPdfText(gfx, "EXECUTIVE INTERPRETATION", label, XBrushes.DarkBlue, 55, 345);
        XBrush portfolioBrush = PortfolioStatusBrush(portfolio.Status);
        DrawStatusBadge(gfx, portfolio.Status, portfolioBrush, 585, 334, 202);
        DrawWrappedText(gfx, portfolio.Interpretation, text, XBrushes.Black, 55, 360, 720, 11, 3);
        DrawPdfText(gfx, "Recommended QA follow-up", label, XBrushes.DarkBlue, 55, 395);
        DrawWrappedText(gfx, portfolio.Recommendation, text, XBrushes.Black, 180, 395, 595, 11, 2);
        DrawPdfText(gfx, "Review periods", label, XBrushes.DarkBlue, 55, 417);
        DrawPdfText(gfx, FitText(gfx, string.Join(" | ", periods.Select((period, index) => $"P{index + 1}: {period.Label}")), text, 620), text, XBrushes.Black, 145, 417);

        if (approval == null)
        {
            string draftControl = sourceImportPending
                ? "CONTROL STATUS: DRAFT - source import is Pending Approval. Review/export only; QA snapshot approval is prohibited until source approval and regeneration."
                : "CONTROL STATUS: DRAFT - pending QA electronic approval. Do not use as an approved controlled review.";
            DrawPdfText(gfx, draftControl, text, XBrushes.DarkRed, 45, 441);
            DrawSignatureFields(gfx, 469);
        }
        else
        {
            DrawApprovedReviewRecord(gfx, approval, reviewerComment, signatureMeaning, signatureReason, 45, 438, 752);
        }
        DrawFooter(gfx, 1, approval != null);
    }

    private static void WritePdfExecutiveSummary(
        PdfDocument document,
        IReadOnlyList<ReviewPacket> packets,
        IReadOnlyList<ReviewPeriod> periods,
        IReadOnlyList<DataCompletenessException> exceptions,
        ExternalTrendReviewSaveResult? approval)
    {
        int offset = 0;
        bool firstPage = true;
        do
        {
            int rowsThisPage = firstPage ? 21 : 26;
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(842);
            page.Height = XUnit.FromPoint(595);
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            DrawControlledHeader(gfx);

            XFont heading = new("Arial", 13, XFontStyleEx.Bold);
            XFont label = new("Arial", 6.7, XFontStyleEx.Bold);
            XFont text = new("Arial", 6.7, XFontStyleEx.Regular);
            DrawPdfText(gfx, firstPage ? "Executive Portfolio Summary and Interpretation" : "Executive Portfolio Summary - continued", heading, XBrushes.DarkBlue, 45, 117);

            double tableY;
            if (firstPage)
            {
                PortfolioAssessment assessment = BuildPortfolioAssessment(packets, exceptions.Count);
                const double cardWidth = 118;
                double gap = 8.4;
                double xCard = 45;
                DrawMetricCard(gfx, "AREAS", assessment.AreaCount.ToString("N0", CultureInfo.InvariantCulture), xCard, 127, cardWidth); xCard += cardWidth + gap;
                DrawMetricCard(gfx, "OBSERVATIONS", assessment.TotalObservations.ToString("N0", CultureInfo.InvariantCulture), xCard, 127, cardWidth); xCard += cardWidth + gap;
                DrawMetricCard(gfx, "ACTION", assessment.ActionAreas.ToString("N0", CultureInfo.InvariantCulture), xCard, 127, cardWidth, assessment.ActionAreas > 0 ? XBrushes.DarkRed : XBrushes.DarkGreen); xCard += cardWidth + gap;
                DrawMetricCard(gfx, "ALERT", assessment.AlertAreas.ToString("N0", CultureInfo.InvariantCulture), xCard, 127, cardWidth, assessment.AlertAreas > 0 ? XBrushes.DarkOrange : XBrushes.DarkGreen); xCard += cardWidth + gap;
                DrawMetricCard(gfx, "INCREASING", assessment.IncreasingAreas.ToString("N0", CultureInfo.InvariantCulture), xCard, 127, cardWidth, assessment.IncreasingAreas > 0 ? XBrushes.DarkOrange : XBrushes.DarkGreen); xCard += cardWidth + gap;
                DrawMetricCard(gfx, "DATA GAPS", assessment.DataGapCount.ToString("N0", CultureInfo.InvariantCulture), xCard, 127, cardWidth, assessment.DataGapCount > 0 ? XBrushes.DarkOrange : XBrushes.DarkGreen);

                DrawPdfText(gfx, "Portfolio conclusion", label, XBrushes.DarkBlue, 45, 176);
                DrawStatusBadge(gfx, assessment.Status, PortfolioStatusBrush(assessment.Status), 135, 164, 205);
                DrawPdfText(gfx, "Review periods", label, XBrushes.DarkBlue, 355, 176);
                DrawPdfText(gfx, FitText(gfx, string.Join(" | ", periods.Select((period, index) => $"P{index + 1}: {period.Label}")), text, 365), text, XBrushes.Black, 430, 176);
                tableY = 192;
            }
            else
            {
                tableY = 132;
            }

            double[] columns = { 166, 42, 42, 42, 123, 88, 249 };
            string[] titles = { "Area", "P1 n", "P2 n", "P3 n", "Trend", "Excursion", "Recommended QA disposition" };
            double x = 45;
            for (int index = 0; index < columns.Length; index++)
            {
                gfx.DrawRectangle(XBrushes.DarkBlue, x, tableY, columns[index], 18);
                DrawPdfText(gfx, titles[index], label, XBrushes.White, x + 5, tableY + 12);
                x += columns[index];
            }

            int written = 0;
            foreach (ReviewPacket packet in packets.Skip(offset).Take(rowsThisPage))
            {
                double rowY = tableY + 18 + written * 15;
                XBrush rowFill = written % 2 == 0
                    ? new XSolidBrush(XColor.FromArgb(248, 250, 252))
                    : XBrushes.White;
                string[] values =
                {
                    FitText(gfx, packet.AreaCode, text, columns[0] - 10),
                    packet.Summaries.ElementAtOrDefault(0)?.Count.ToString(CultureInfo.InvariantCulture) ?? "-",
                    packet.Summaries.ElementAtOrDefault(1)?.Count.ToString(CultureInfo.InvariantCulture) ?? "-",
                    packet.Summaries.ElementAtOrDefault(2)?.Count.ToString(CultureInfo.InvariantCulture) ?? "-",
                    packet.TrendConclusion,
                    packet.ExcursionStatus,
                    FitText(gfx, packet.RecommendedDisposition, text, columns[6] - 10)
                };
                x = 45;
                for (int index = 0; index < columns.Length; index++)
                {
                    gfx.DrawRectangle(rowFill, x, rowY, columns[index], 15);
                    gfx.DrawRectangle(XPens.LightGray, x, rowY, columns[index], 15);
                    XBrush brush = index switch
                    {
                        5 => packet.ExcursionStatus == "ACTION" ? XBrushes.DarkRed : packet.ExcursionStatus is "ALERT" or "NOT ASSESSED" ? XBrushes.DarkOrange : XBrushes.DarkGreen,
                        4 when packet.TrendConclusion is "INCREASING" or "INCONCLUSIVE" => XBrushes.DarkOrange,
                        6 => DispositionBrush(packet.RecommendedDisposition),
                        _ => XBrushes.Black
                    };
                    DrawPdfText(gfx, values[index], text, brush, x + 5, rowY + 10.5);
                    x += columns[index];
                }
                written++;
            }

            offset += written;
            DrawFooter(gfx, document.PageCount, approval != null);
            firstPage = false;
        }
        while (offset < packets.Count);
    }

    private static void WritePdfPage(PdfDocument document, ReviewPacket packet, IReadOnlyList<ReviewPeriod> periods, ExternalTrendReviewSaveResult? approval, bool sourceImportPending)
    {
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(842); page.Height = XUnit.FromPoint(595);
        using XGraphics gfx = XGraphics.FromPdfPage(page);
        DrawControlledHeader(gfx);
        XFont heading = new("Arial", 13, XFontStyleEx.Bold);
        XFont bold = new("Arial", 8, XFontStyleEx.Bold);
        DrawPdfText(gfx, "External Environmental Monitoring Trend Review - Area Detail", heading, XBrushes.DarkBlue, 45, 117);
        DrawPdfText(gfx, $"Area / location: {packet.AreaCode} | {FitText(gfx, packet.LocationSummary, bold, 285)}", bold, XBrushes.Black, 45, 137);
        DrawPdfText(gfx, "Method / Parameter: " + FitText(gfx, packet.DisplaySubject, bold, 310), bold, XBrushes.Black, 230, 137);
        XBrush trendBrush = packet.TrendConclusion is "INCREASING" or "INCONCLUSIVE" ? XBrushes.DarkOrange : XBrushes.DarkGreen;
        XBrush excursionBrush = packet.ExcursionStatus == "ACTION"
            ? XBrushes.DarkRed
            : packet.ExcursionStatus is "ALERT" or "NOT ASSESSED"
                ? XBrushes.DarkOrange
                : XBrushes.DarkGreen;
        DrawStatusBadge(gfx, $"Trend: {packet.TrendConclusion}", trendBrush, 550, 116, 247);
        DrawStatusBadge(gfx, $"Excursion: {packet.ExcursionStatus}", excursionBrush, 550, 134, 120);
        DrawStatusBadge(gfx, FitText(gfx, packet.RecommendedDisposition, new XFont("Arial", 7.5, XFontStyleEx.Bold), 115), DispositionBrush(packet.RecommendedDisposition), 677, 134, 120);

        DrawTrendChart(gfx, packet, periods, 45, 157, 752, 163);
        DrawSummaryTable(gfx, packet.Summaries, 45, 332, 752);
        DrawAreaAssessment(gfx, packet, 45, 409, 752);
        string controlNote = approval != null
            ? $"QA APPROVED presentation of immutable snapshot set signed by {approval.SignedBy} ({approval.ReviewerRole}) at {approval.SignedAt:yyyy-MM-dd HH:mm:ss zzz}."
            : sourceImportPending
                ? "DRAFT: source import is Pending Approval; review/export only. QA snapshot approval is blocked until source approval and regeneration."
                : "DRAFT: automated interpretation is decision support only; QA electronic approval is pending.";
        DrawPdfText(gfx, controlNote, new XFont("Arial", 6.5, XFontStyleEx.Regular), approval == null ? XBrushes.DarkRed : XBrushes.Gray, 45, 558);
        DrawFooter(gfx, document.PageCount, approval != null);
    }

    private static void WriteDataCompletenessAppendix(PdfDocument document, IReadOnlyList<DataCompletenessException> exceptions, ExternalTrendReviewSaveResult? approval)
    {
        const int rowsPerPage = 19;
        for (int start = 0; start < exceptions.Count; start += rowsPerPage)
        {
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(842); page.Height = XUnit.FromPoint(595);
            using XGraphics gfx = XGraphics.FromPdfPage(page);
            DrawControlledHeader(gfx);
            XFont heading = new("Arial", 13, XFontStyleEx.Bold);
            XFont text = new("Arial", 7.5, XFontStyleEx.Regular);
            XFont bold = new("Arial", 7.5, XFontStyleEx.Bold);
            DrawPdfText(gfx, "Appendix - Data Quality / Data Completeness Exceptions", heading, XBrushes.DarkBlue, 45, 120);
            DrawWrappedText(gfx, "The following areas or review periods contain missing/insufficient observations, incomplete units or limits, or a controlled-limit change that requires explicit QA consideration. These rows are source-data/control-context findings, not automatic microbiological root-cause conclusions.", text, XBrushes.Black, 45, 138, 752, 11);
            double y = 180;
            double[] columns = { 165, 230, 160, 42, 155 };
            string[] titles = { "Area", "Method / result", "Period", "n", "Issue" };
            double x = 45;
            for (int i = 0; i < columns.Length; i++)
            {
                gfx.DrawRectangle(XBrushes.DarkBlue, x, y, columns[i], 18);
                DrawPdfText(gfx, titles[i], bold, XBrushes.White, x + 5, y + 12);
                x += columns[i];
            }

            foreach (DataCompletenessException item in exceptions.Skip(start).Take(rowsPerPage))
            {
                y += 18;
                string[] values =
                {
                    FitText(gfx, item.AreaCode, text, columns[0] - 10),
                    FitText(gfx, $"{item.MethodName} - {item.ParameterName}", text, columns[1] - 10),
                    FitText(gfx, item.Period, text, columns[2] - 10),
                    item.ObservationCount.ToString(CultureInfo.InvariantCulture),
                    FitText(gfx, item.Issue, text, columns[4] - 10)
                };
                x = 45;
                for (int i = 0; i < columns.Length; i++)
                {
                    gfx.DrawRectangle(XPens.LightGray, x, y, columns[i], 18);
                    DrawPdfText(gfx, values[i], text, i == 4 ? XBrushes.DarkOrange : XBrushes.Black, x + 5, y + 12);
                    x += columns[i];
                }
            }
            DrawFooter(gfx, document.PageCount, approval != null);
        }
    }

    private static void WriteRegulatoryMethodologyAppendix(PdfDocument document, ExternalTrendReviewSaveResult? approval)
    {
        PdfPage page = document.AddPage();
        page.Width = XUnit.FromPoint(842); page.Height = XUnit.FromPoint(595);
        using XGraphics gfx = XGraphics.FromPdfPage(page);
        DrawControlledHeader(gfx);
        XFont heading = new("Arial", 13, XFontStyleEx.Bold);
        XFont section = new("Arial", 8, XFontStyleEx.Bold);
        XFont text = new("Arial", 7.4, XFontStyleEx.Regular);
        DrawPdfText(gfx, "Regulatory Alignment and Trend-Interpretation Methodology", heading, XBrushes.DarkBlue, 45, 117);

        double y = 140;
        DrawMethodologySection(gfx, "1. Data governance", "Only QA-approved external environmental-monitoring import batches are eligible. Canonical area mapping, deduplication, controlled units, approved alert/action limits, source-manifest fingerprinting, and immutable signed snapshots protect traceability. Draft PDF output is not a QA-approved record.", section, text, ref y);
        DrawMethodologySection(gfx, "2. Statistical review", "The report uses descriptive statistics (n, minimum, mean, median, maximum), alert/action excursion counts and rates, and a three-cycle directional comparison of cycle means. No unapproved percentage threshold or statistical significance rule is invented by the software.", section, text, ref y);
        DrawMethodologySection(gfx, "3. Interpretation logic", "Action excursions drive investigation/CAPA review. Alert excursions or a sustained increasing direction drive enhanced QA review. Missing controlled units or incomplete alert/action limits produce a NOT ASSESSED excursion status rather than a false Normal result. When approved historical limits change, excursion counts use each observation's own approved source limit and a single limit line is not presented as universally applicable. Incomplete comparison periods drive a data-completeness review.", section, text, ref y);
        DrawMethodologySection(gfx, "4. Regulatory alignment", "The review is designed to support ICH Q10 process-performance and product-quality monitoring, ICH Q9(R1) quality risk management, and WHO GMP pharmaceutical quality-system principles. The approved site environmental-monitoring procedure, approved limits, deviation system, CAPA system, organism-identification practices, and facility controls remain the controlling operational requirements.", section, text, ref y);
        DrawMethodologySection(gfx, "5. Required QA considerations", "Where adverse or repeated signals occur, QA should consider recurrence by location, organism identification where available, cleaning/disinfection performance, HVAC and pressure conditions, temperature/humidity, personnel and material flow, maintenance activity, seasonality, method/media performance, deviations, CAPA effectiveness, and any relevant production events. The report does not infer microbiological root cause automatically. Automated interpretation does not replace QA judgement.", section, text, ref y);
        DrawMethodologySection(gfx, "6. Limitation", "This report is a controlled quality-review aid. It does not by itself release or reject product, replace a deviation/investigation record, or establish new alert/action limits. Any site-specific statistical action threshold must be defined and approved in the applicable procedure before the software may use it as a decision rule.", section, text, ref y);

        DrawPdfText(gfx, "Reference set", section, XBrushes.DarkBlue, 45, 524);
        DrawWrappedText(gfx, "ICH Q10 Pharmaceutical Quality System (process performance and product quality monitoring); ICH Q9(R1) Quality Risk Management; WHO Quality Assurance of Pharmaceuticals - GMP and Inspection Compendium, 10th ed. (2024); approved MEDICA environmental-monitoring procedure and controlled alert/action limits.", text, XBrushes.Black, 45, 538, 752, 10.5, 3);
        DrawFooter(gfx, document.PageCount, approval != null);
    }

    private static void DrawMethodologySection(XGraphics gfx, string title, string body, XFont sectionFont, XFont textFont, ref double y)
    {
        DrawPdfText(gfx, title, sectionFont, XBrushes.DarkBlue, 45, y);
        y += 14;
        DrawWrappedText(gfx, body, textFont, XBrushes.Black, 55, y, 732, 10.5, 4);
        y += 48;
    }

    private static PortfolioAssessment BuildPortfolioAssessment(IReadOnlyList<ReviewPacket> packets, int completenessFindingCount)
    {
        int areaCount = packets.Count;
        int totalObservations = packets.Sum(packet => packet.Summaries.Sum(summary => summary.Count));
        int actionAreas = packets.Count(packet => packet.ExcursionStatus.Equals("ACTION", StringComparison.OrdinalIgnoreCase));
        int alertAreas = packets.Count(packet => packet.ExcursionStatus.Equals("ALERT", StringComparison.OrdinalIgnoreCase));
        int notAssessedAreas = packets.Count(packet => packet.ExcursionStatus.Equals("NOT ASSESSED", StringComparison.OrdinalIgnoreCase));
        int increasingAreas = packets.Count(packet => packet.TrendConclusion.Equals("INCREASING", StringComparison.OrdinalIgnoreCase));
        int inconclusiveAreas = packets.Count(packet => packet.TrendConclusion.Equals("INCONCLUSIVE", StringComparison.OrdinalIgnoreCase));
        int dataGapCount = Math.Max(completenessFindingCount, Math.Max(inconclusiveAreas, notAssessedAreas));

        string status;
        string interpretation;
        string recommendation;
        if (actionAreas > 0)
        {
            status = "INVESTIGATION / CAPA REVIEW REQUIRED";
            interpretation = $"Action-level excursions are present in {actionAreas:N0} reviewed area(s). The portfolio contains an adverse signal that requires linked investigation/deviation review before QA can rely on the trend package as evidence of continued control.";
            recommendation = "Verify linked investigation/CAPA records, assess recurrence and common causes, confirm effectiveness actions, and document QA disposition for every action area.";
        }
        else if (notAssessedAreas > 0)
        {
            status = "DATA QUALITY / LIMIT REVIEW";
            interpretation = $"Excursion status could not be fully assessed for {notAssessedAreas:N0} reviewed area(s) because approved limits or controlled units are incomplete. The package must not infer normal excursion status from missing source controls.";
            recommendation = "Reconcile the approved source units and alert/action limits, document any justified historical exception, and regenerate the controlled trend review before relying on excursion status.";
        }
        else if (alertAreas > 0 || increasingAreas > 0)
        {
            status = "ENHANCED QA REVIEW";
            interpretation = $"No action-level excursion is present, but {alertAreas:N0} area(s) contain alert-level results and {increasingAreas:N0} area(s) show a sustained increasing direction. These are early-warning or drift signals rather than automatic failures.";
            recommendation = "Review recurrence, location/organism patterns and facility conditions; justify enhanced monitoring or other risk controls where appropriate and document the action/no-action rationale.";
        }
        else if (dataGapCount > 0)
        {
            status = "DATA COMPLETENESS REVIEW";
            interpretation = $"No action or alert excursion is identified in the available numeric data, but {dataGapCount:N0} data-completeness or trend-readiness finding(s) prevent a fully reliable portfolio conclusion.";
            recommendation = "Reconcile monitoring-plan completion and missing/insufficient periods before relying on the three-cycle conclusion; document any justified exception.";
        }
        else
        {
            status = "ROUTINE MONITORING";
            interpretation = "The reviewed approved external data contain no alert/action excursions and no sustained increasing three-cycle direction. No adverse signal is detected by the configured descriptive trend logic for the selected review period.";
            recommendation = "Continue routine environmental monitoring and periodic review under the approved procedure; retain QA oversight for emerging location, organism, seasonal or process-related patterns.";
        }

        return new PortfolioAssessment(areaCount, totalObservations, actionAreas, alertAreas, increasingAreas, dataGapCount, status, interpretation, recommendation);
    }

    private static XBrush PortfolioStatusBrush(string status) => status switch
    {
        "INVESTIGATION / CAPA REVIEW REQUIRED" => XBrushes.DarkRed,
        "ENHANCED QA REVIEW" => XBrushes.DarkOrange,
        "DATA COMPLETENESS REVIEW" => XBrushes.DarkOrange,
        "DATA QUALITY / LIMIT REVIEW" => XBrushes.DarkOrange,
        _ => XBrushes.DarkGreen
    };

    private static XBrush DispositionBrush(string disposition) => disposition switch
    {
        "INVESTIGATION / CAPA REVIEW" => XBrushes.DarkRed,
        "ENHANCED QA REVIEW" => XBrushes.DarkOrange,
        "DATA COMPLETENESS REVIEW" => XBrushes.DarkOrange,
        "DATA QUALITY / LIMIT REVIEW" => XBrushes.DarkOrange,
        "LIMIT CHANGE / QA REVIEW" => XBrushes.DarkOrange,
        _ => XBrushes.DarkGreen
    };

    private static void DrawPdfInfoCell(XGraphics gfx, string labelText, string value, XFont labelFont, XFont valueFont, double x, double y, double width, double height)
    {
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)), x, y, width, height);
        gfx.DrawRectangle(XPens.LightGray, x, y, width, height);
        DrawPdfText(gfx, labelText, labelFont, XBrushes.DarkBlue, x + 6, y + 10);
        DrawPdfText(gfx, FitText(gfx, value, valueFont, width - 12), valueFont, XBrushes.Black, x + 6, y + height - 6);
    }

    private static void DrawControlledHeader(XGraphics gfx)
    {
        const double left = 45, top = 28, height = 62, width = 752;
        XBrush navy = new XSolidBrush(XColor.FromArgb(30, 58, 95));
        gfx.DrawRectangle(navy, left, top, width, height);

        double logoX = left + 8;
        double logoY = top + 8;
        double logoWidth = 126;
        double logoHeight = 46;
        gfx.DrawRoundedRectangle(XPens.White, XBrushes.White, logoX, logoY, logoWidth, logoHeight, 4, 4);
        string logoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "medica-logo.png");
        if (File.Exists(logoPath))
        {
            try
            {
                using XImage logo = XImage.FromFile(logoPath);
                double padding = 4;
                double ratio = Math.Min((logoWidth - padding * 2) / logo.PixelWidth, (logoHeight - padding * 2) / logo.PixelHeight);
                double drawWidth = logo.PixelWidth * ratio;
                double drawHeight = logo.PixelHeight * ratio;
                gfx.DrawImage(logo, logoX + (logoWidth - drawWidth) / 2, logoY + (logoHeight - drawHeight) / 2, drawWidth, drawHeight);
            }
            catch
            {
                DrawCentered(gfx, "medica", new XFont("Arial", 22, XFontStyleEx.Bold), top + 42, logoX, logoWidth);
            }
        }
        else
        {
            DrawCentered(gfx, "medica", new XFont("Arial", 22, XFontStyleEx.Bold), top + 42, logoX, logoWidth);
        }

        XFont company = new("Arial", 13.5, XFontStyleEx.Bold);
        XFont department = new("Arial", 8.5, XFontStyleEx.Bold);
        XFont meta = new("Arial", 6.8, XFontStyleEx.Bold);
        gfx.DrawString("MEDICA PHARMACEUTICAL INDUSTRY", company, XBrushes.White,
            new XRect(left + 142, top + 10, 405, 18), XStringFormats.TopCenter);
        gfx.DrawString("Microbiology Department", department, XBrushes.White,
            new XRect(left + 142, top + 34, 405, 12), XStringFormats.TopCenter);

        double metaX = left + 565;
        DrawPdfText(gfx, "Form No: MQC-R-TREND-001", meta, XBrushes.White, metaX, top + 16);
        DrawPdfText(gfx, "Version: 01", meta, XBrushes.White, metaX, top + 30);
        DrawPdfText(gfx, "Procedure Ref: MQC-G-0009", meta, XBrushes.White, metaX, top + 44);
        DrawPdfText(gfx, "Annexure: A11 / G1/1 (Graph) | A12 / F4/1 (Summary)", meta, XBrushes.White, metaX, top + 57);
    }

    private static void DrawTrendChart(XGraphics gfx, ReviewPacket packet, IReadOnlyList<ReviewPeriod> periods, double x, double y, double width, double height)
    {
        gfx.DrawRectangle(XPens.LightGray, x, y, width, height);
        XFont label = new("Arial", 7, XFontStyleEx.Regular);
        XFont title = new("Arial", 8, XFontStyleEx.Bold);
        DrawPdfText(gfx, "Time-series results and approved limits", title, XBrushes.DarkBlue, x + 8, y + 14);
        if (packet.Observations.Count == 0)
        {
            DrawCentered(gfx, "NO APPROVED NUMERIC OBSERVATIONS - TREND CONCLUSION NOT POSSIBLE", title, y + height / 2, x, width);
            return;
        }

        double plotLeft = x + 48, plotTop = y + 32, plotWidth = width - 64, plotHeight = height - 56;
        decimal maximum = packet.Observations.Select(item => item.Result).Append(packet.Observations.Where(item => item.AlertLimit.HasValue).Select(item => item.AlertLimit!.Value).DefaultIfEmpty(0).Max()).Append(packet.Observations.Where(item => item.ActionLimit.HasValue).Select(item => item.ActionLimit!.Value).DefaultIfEmpty(0).Max()).Max();
        double maxY = Math.Max(1d, (double)maximum * 1.15d);
        DateTime start = periods.Count > 0 ? periods.Min(item => item.From).Date : packet.Observations.Min(item => item.RecordDate).Date;
        DateTime endExclusive = periods.Count > 0 ? periods.Max(item => item.To).Date.AddDays(1) : packet.Observations.Max(item => item.RecordDate).Date.AddDays(1);
        if (endExclusive <= start) endExclusive = start.AddDays(1);
        double spanDays = (endExclusive - start).TotalDays;

        for (int index = 0; index < periods.Count; index++)
        {
            double periodLeft = plotLeft + ((periods[index].From.Date - start).TotalDays / spanDays) * plotWidth;
            double periodRight = plotLeft + ((periods[index].To.Date.AddDays(1) - start).TotalDays / spanDays) * plotWidth;
            if (index % 2 == 1)
                gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)), periodLeft, plotTop, Math.Max(0, periodRight - periodLeft), plotHeight);
            gfx.DrawLine(XPens.LightGray, periodLeft, plotTop, periodLeft, plotTop + plotHeight);
            DrawCentered(gfx, $"P{index + 1}", label, plotTop - 4, periodLeft, Math.Max(0, periodRight - periodLeft));
        }
        gfx.DrawLine(XPens.Black, plotLeft, plotTop, plotLeft, plotTop + plotHeight);
        gfx.DrawLine(XPens.Black, plotLeft, plotTop + plotHeight, plotLeft + plotWidth, plotTop + plotHeight);
        for (int step = 0; step <= 4; step++)
        {
            double value = maxY * step / 4;
            double lineY = plotTop + plotHeight - (plotHeight * step / 4);
            gfx.DrawLine(XPens.LightGray, plotLeft, lineY, plotLeft + plotWidth, lineY);
            DrawPdfText(gfx, value.ToString("0.##", CultureInfo.InvariantCulture), label, XBrushes.Black, x + 5, lineY + 2);
        }
        foreach (TrendObservation point in packet.Observations)
        {
            double pointX = plotLeft + ((point.RecordDate.Date - start).TotalDays / spanDays) * plotWidth;
            double pointY = plotTop + plotHeight - ((double)point.Result / maxY) * plotHeight;
            XBrush pointBrush = point.IsQualified
                ? XBrushes.Purple
                : point.ActionLimit.HasValue && point.Result > point.ActionLimit
                    ? XBrushes.Red
                    : point.AlertLimit.HasValue && point.Result > point.AlertLimit
                        ? XBrushes.Orange
                        : XBrushes.DarkBlue;
            gfx.DrawEllipse(pointBrush, pointX - 2.5, pointY - 2.5, 5, 5);
        }
        decimal? alert = ConsistentObservationLimit(packet.Observations, item => item.AlertLimit);
        decimal? action = ConsistentObservationLimit(packet.Observations, item => item.ActionLimit);
        DrawLimit(gfx, plotLeft, plotTop, plotWidth, plotHeight, maxY, alert, XPens.Orange, XBrushes.Orange, "Alert");
        DrawLimit(gfx, plotLeft, plotTop, plotWidth, plotHeight, maxY, action, XPens.Red, XBrushes.Red, "Action");
        if (!alert.HasValue || !action.HasValue)
            DrawPdfText(gfx, "Single limit line not shown when approved limits are missing or vary; points use row-specific source limits.", new XFont("Arial", 6.2, XFontStyleEx.Regular), XBrushes.Gray, plotLeft + 135, plotTop + plotHeight + 13);
        if (packet.Observations.Any(item => item.IsQualified))
            DrawPdfText(gfx, "Purple points are qualified/censored values plotted at the reported boundary; they are excluded from exact-value statistics.", new XFont("Arial", 6.2, XFontStyleEx.Regular), XBrushes.Purple, plotLeft + 135, plotTop + plotHeight + 23);
        DrawPdfText(gfx, start.ToString("dd-MMM-yy"), label, XBrushes.Black, plotLeft, plotTop + plotHeight + 13);
        DrawPdfText(gfx, endExclusive.AddDays(-1).ToString("dd-MMM-yy"), label, XBrushes.Black, plotLeft + plotWidth - 38, plotTop + plotHeight + 13);
        DrawPdfText(gfx, packet.Summaries.FirstOrDefault()?.Unit ?? string.Empty, label, XBrushes.Black, plotLeft + 5, plotTop - 4);
    }

    private static decimal? ConsistentObservationLimit(IReadOnlyList<TrendObservation> observations, Func<TrendObservation, decimal?> selector)
    {
        if (observations.Count == 0 || observations.Any(item => !selector(item).HasValue))
            return null;
        List<decimal> values = observations.Select(selector).Where(value => value.HasValue).Select(value => value!.Value).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }

    private static void DrawLimit(XGraphics gfx, double left, double top, double width, double height, double maxY, decimal? limit, XPen pen, XBrush brush, string label)
    {
        if (!limit.HasValue) return;
        double y = top + height - ((double)limit.Value / maxY) * height;
        gfx.DrawLine(pen, left, y, left + width, y);
        DrawPdfText(gfx, label + " " + limit.Value.ToString("0.##", CultureInfo.InvariantCulture), new XFont("Arial", 7, XFontStyleEx.Bold), brush, left + width - 65, y - 3);
    }

    private static void DrawSummaryTable(XGraphics gfx, IReadOnlyList<TrendCycleSummary> summaries, double x, double y, double width)
    {
        XFont header = new("Arial", 6.6, XFontStyleEx.Bold);
        XFont text = new("Arial", 6.5, XFontStyleEx.Regular);
        string[] titles = { "Period", "n", "Min", "Mean", "Median", "Max", "Alert", "Action", "Signal", "Data quality" };
        double[] columns = { 185, 35, 55, 55, 55, 55, 70, 70, 80, 92 };
        double current = x;
        for (int i = 0; i < columns.Length; i++)
        {
            gfx.DrawRectangle(XBrushes.DarkBlue, current, y, columns[i], 18);
            DrawPdfText(gfx, titles[i], header, XBrushes.White, current + 4, y + 12);
            current += columns[i];
        }
        for (int row = 0; row < summaries.Count; row++)
        {
            TrendCycleSummary item = summaries[row];
            string[] values =
            {
                item.Cycle,
                item.CensoredCount > 0 ? $"{item.Count} ({item.ExactCount} exact/{item.CensoredCount} q)" : item.Count.ToString(CultureInfo.InvariantCulture),
                Number(item.Minimum),
                Number(item.Mean),
                Number(item.Median),
                Number(item.Maximum),
                $"{item.AlertCount} ({item.AlertRate:0.#}%)",
                $"{item.ActionCount} ({item.ActionRate:0.#}%)",
                item.Signal,
                item.DataQuality
            };
            current = x;
            double rowY = y + 18 + row * 18;
            for (int i = 0; i < columns.Length; i++)
            {
                gfx.DrawRectangle(XPens.LightGray, current, rowY, columns[i], 18);
                XBrush brush = item.Signal == "Action"
                    ? XBrushes.DarkRed
                    : item.Signal is "Alert" or "Not Assessed"
                        ? XBrushes.DarkOrange
                        : item.Count == 0
                            ? XBrushes.Gray
                            : XBrushes.Black;
                string value = i == 9 ? FitText(gfx, values[i], text, columns[i] - 8) : values[i];
                DrawPdfText(gfx, value, text, brush, current + 4, rowY + 12);
                current += columns[i];
            }
        }
    }

    private static void DrawMetricCard(XGraphics gfx, string label, string value, double x, double y, double width, XBrush? valueBrush = null)
    {
        XBrush fill = new XSolidBrush(XColor.FromArgb(248, 250, 252));
        gfx.DrawRectangle(fill, x, y, width, 38);
        gfx.DrawRectangle(XPens.LightGray, x, y, width, 38);
        DrawPdfText(gfx, label, new XFont("Arial", 6.5, XFontStyleEx.Bold), XBrushes.Gray, x + 8, y + 12);
        DrawPdfText(gfx, value, new XFont("Arial", 13, XFontStyleEx.Bold), valueBrush ?? XBrushes.DarkBlue, x + 8, y + 31);
    }

    private static void DrawStatusBadge(XGraphics gfx, string value, XBrush brush, double x, double y, double width)
    {
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)), x, y, width, 16);
        gfx.DrawRectangle(XPens.LightGray, x, y, width, 16);
        XFont font = new("Arial", 7.5, XFontStyleEx.Bold);
        DrawPdfText(gfx, FitText(gfx, value, font, width - 12), font, brush, x + 6, y + 11);
    }

    private static void DrawAreaAssessment(XGraphics gfx, ReviewPacket packet, double x, double y, double width)
    {
        XFont heading = new("Arial", 8, XFontStyleEx.Bold);
        XFont label = new("Arial", 6.8, XFontStyleEx.Bold);
        XFont text = new("Arial", 6.8, XFontStyleEx.Regular);
        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(248, 250, 252)), x, y, width, 137);
        gfx.DrawRectangle(XPens.LightGray, x, y, width, 137);
        DrawPdfText(gfx, "Regulatory interpretation and QA follow-up", heading, XBrushes.DarkBlue, x + 10, y + 15);

        string coverage = string.Join(" | ", packet.Summaries.Select((item, index) => $"P{index + 1}: n={item.Count}"));
        decimal? alert = ConsistentSummaryLimit(packet.Summaries, item => item.AlertLimit);
        decimal? action = ConsistentSummaryLimit(packet.Summaries, item => item.ActionLimit);
        string unit = packet.Summaries.Select(item => item.Unit).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
        bool incompleteLimits = packet.Summaries.Any(item => item.Count > 0 && !item.LimitsComplete);
        bool changedLimits = packet.Summaries.Any(item => item.Count > 0 && !item.LimitsConsistent);
        bool inconsistentUnits = packet.Summaries.Any(item => item.Count > 0 && !item.UnitsConsistent);
        string limits = incompleteLimits
            ? "Incomplete approved alert/action limits"
            : changedLimits
                ? "Approved limits changed; row-specific limits used"
                : alert.HasValue || action.HasValue
                    ? $"Alert {Number(alert)} {unit} | Action {Number(action)} {unit}"
                    : "Approved alert/action limits are not present in the selected source data.";
        string dataQuality = inconsistentUnits
            ? "Missing/mixed units"
            : incompleteLimits
                ? "Limit data incomplete"
                : changedLimits
                    ? "Limit history changed"
                    : "Complete";
        string sourceAnchor = string.IsNullOrWhiteSpace(packet.SourceManifestSha256)
            ? "Not available"
            : packet.SourceManifestSha256[..Math.Min(16, packet.SourceManifestSha256.Length)] + "...";

        DrawAssessmentRow(gfx, "Coverage", coverage, label, text, x + 10, y + 34, width - 20);
        DrawAssessmentRow(gfx, "Mean shift", packet.MeanShiftDescription, label, text, x + 10, y + 50, width - 20);
        DrawAssessmentRow(gfx, "Interpretation", packet.RegulatoryInterpretation, label, text, x + 10, y + 66, width - 20);
        DrawAssessmentRow(gfx, "QA disposition", packet.RecommendedDisposition, label, text, x + 10, y + 82, width - 20, DispositionBrush(packet.RecommendedDisposition));
        DrawAssessmentRow(gfx, "Limits / data", $"{limits} | Data quality: {dataQuality}", label, text, x + 10, y + 98, width - 20);
        DrawAssessmentRow(gfx, "Source / basis", $"Manifest {sourceAnchor} | ICH Q10 / ICH Q9(R1) / WHO GMP + approved site EM procedure.", label, text, x + 10, y + 114, width - 20);
    }

    private static void DrawAssessmentRow(XGraphics gfx, string labelText, string value, XFont labelFont, XFont valueFont, double x, double y, double width, XBrush? valueBrush = null)
    {
        const double labelWidth = 92;
        DrawPdfText(gfx, labelText, labelFont, XBrushes.DarkBlue, x, y);
        DrawPdfText(gfx, FitText(gfx, value, valueFont, width - labelWidth), valueFont, valueBrush ?? XBrushes.Black, x + labelWidth, y);
    }

    private static string Number(decimal? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-";

    private static string FitText(XGraphics gfx, string value, XFont font, double maximumWidth)
    {
        string safe = PdfSafeText(value);
        if (gfx.MeasureString(safe, font).Width <= maximumWidth)
            return safe;

        const string suffix = "...";
        int low = 0;
        int high = safe.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            string candidate = safe[..middle].TrimEnd() + suffix;
            if (gfx.MeasureString(candidate, font).Width <= maximumWidth)
                low = middle;
            else
                high = middle - 1;
        }
        return safe[..low].TrimEnd() + suffix;
    }

    private static string PdfSafeText(string value) => (value ?? string.Empty)
        .Replace("—", "-", StringComparison.Ordinal)
        .Replace("–", "-", StringComparison.Ordinal)
        .Replace("→", "->", StringComparison.Ordinal);

    private static void DrawSignatureFields(XGraphics gfx, double y)
    {
        XFont label = new("Arial", 8, XFontStyleEx.Bold);
        double[] columns = { 45, 298, 551 };
        string[] titles = { "Prepared By", "Checked By", "Approved By" };
        for (int index = 0; index < columns.Length; index++)
        {
            gfx.DrawRectangle(XPens.Black, columns[index], y, 205, 38);
            DrawPdfText(gfx, titles[index] + ":", label, XBrushes.Black, columns[index] + 8, y + 13);
            DrawPdfText(gfx, "Name / electronic signature / date:", new XFont("Arial", 6.5, XFontStyleEx.Regular), XBrushes.Black, columns[index] + 8, y + 28);
        }
    }

    private static void DrawCentered(XGraphics gfx, string text, XFont font, double y, double x = 45, double width = 752) =>
        gfx.DrawString(PdfSafeText(text), font, XBrushes.Black, new XRect(x, y - font.Size, width, font.Size + 4), XStringFormats.Center);

    private static void DrawWrappedText(XGraphics gfx, string value, XFont font, XBrush brush, double x, double y, double width, double lineHeight, int maxLines = 7)
    {
        List<string> lines = new();
        foreach (string sourceLine in PdfSafeText(value).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            StringBuilder line = new();
            foreach (string word in sourceLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (gfx.MeasureString(candidate, font).Width > width && line.Length > 0)
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    line.Append(word);
                }
                else
                {
                    line.Clear();
                    line.Append(candidate);
                }
            }
            if (line.Length > 0) lines.Add(line.ToString());
        }
        foreach (string line in lines.Take(maxLines))
        {
            DrawPdfText(gfx, line, font, brush, x, y);
            y += lineHeight;
        }
    }

    private static void DrawApprovedReviewRecord(
        XGraphics gfx,
        ExternalTrendReviewSaveResult approval,
        string reviewerComment,
        string signatureMeaning,
        string signatureReason,
        double x,
        double y,
        double width)
    {
        XFont label = new("Arial", 7.5, XFontStyleEx.Bold);
        XFont text = new("Arial", 7.2, XFontStyleEx.Regular);
        gfx.DrawRectangle(XPens.DarkGreen, x, y, width, 104);
        DrawPdfText(gfx, "QA ELECTRONIC APPROVAL RECORD", label, XBrushes.DarkGreen, x + 8, y + 14);
        DrawPdfText(gfx, $"Signed by: {approval.SignedBy} | Role: {approval.ReviewerRole} | Signed: {approval.SignedAt:yyyy-MM-dd HH:mm:ss zzz}", text, XBrushes.Black, x + 8, y + 29);
        DrawPdfText(gfx, $"Immutable Snapshot ID(s): {string.Join(", ", approval.SnapshotIds)}", text, XBrushes.Black, x + 8, y + 43);
        DrawWrappedText(gfx, $"Meaning: {signatureMeaning} | Reason: {signatureReason}", text, XBrushes.Black, x + 8, y + 57, width - 16, 11, 2);
        DrawWrappedText(gfx, $"QA comment: {reviewerComment}", text, XBrushes.Black, x + 8, y + 78, width - 16, 11, 2);
    }

    private static void DrawFooter(XGraphics gfx, int pageNumber, bool approved = false) =>
        DrawPdfText(gfx, $"MQC-R-TREND-001 | Ref: MQC-G-0009/A11-G1/1; A12-F4/1 | {(approved ? "QA Approved Snapshot Presentation" : "Controlled Draft")} | Page {pageNumber}", new XFont("Arial", 6.5, XFontStyleEx.Regular), XBrushes.Gray, 45, 580);

    private static void DrawPdfText(XGraphics graphics, string text, XFont font, XBrush brush, double x, double y) =>
        graphics.DrawString(PdfSafeText(text), font, brush, new XPoint(x, y));

    private bool TryGetSelectedPeriods(out IReadOnlyList<ReviewPeriod> periods)
    {
        periods = Array.Empty<ReviewPeriod>();
        if (!controlsReady || dpReviewStart == null || dpReviewEnd == null || cboReviewScope == null || cboCycleMonths == null)
            return false;
        if (dpReviewStart.SelectedDate is not DateTime selectedStart || dpReviewEnd.SelectedDate is not DateTime selectedEnd || selectedStart.Date > selectedEnd.Date)
            return false;

        string scope = (cboReviewScope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Custom";
        int cycleMonths = int.TryParse((cboCycleMonths.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out int months) ? months : 6;
        int count = scope == "Year" ? 1 : int.TryParse(scope, out int selectedCount) ? selectedCount : 0;
        if (scope == "Custom")
        {
            periods = new[] { new ReviewPeriod(selectedStart.Date, selectedEnd.Date) };
            return true;
        }

        int totalMonths = scope == "Year" ? 12 : checked(count * cycleMonths);
        DateTime firstStart = selectedEnd.Date.AddMonths(-totalMonths).AddDays(1);
        List<ReviewPeriod> selected = new();
        for (int index = 0; index < count; index++)
        {
            DateTime from = firstStart.AddMonths(index * cycleMonths);
            DateTime to = index == count - 1 ? selectedEnd.Date : firstStart.AddMonths((index + 1) * cycleMonths).AddDays(-1);
            selected.Add(new ReviewPeriod(from, to));
        }
        periods = selected;
        return true;
    }

    private string GetAreaLocationSummary(string areaCode)
    {
        ExternalTrendAreaOption? option = areaOptions.FirstOrDefault(item =>
            item.AreaCode.Equals(areaCode, StringComparison.OrdinalIgnoreCase));
        return option == null || string.IsNullOrWhiteSpace(option.LocationName)
            ? "Location not supplied in source import"
            : option.LocationName.Trim();
    }

    private string ScopeDescription()
    {
        string scopeText = (cboReviewScope.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Custom date range";
        string scopeTag = (cboReviewScope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Custom";
        return scopeTag.Equals("Custom", StringComparison.OrdinalIgnoreCase)
            ? $"Review scope: {scopeText}; the start and end dates were selected by the reviewer."
            : $"Review scope: {scopeText}; the review end date was selected by the reviewer and the start date was calculated automatically from the configured cycle length.";
    }

    private static string Csv(object? value) => Infrastructure.CsvSecurity.Escape(value);

    private static string Label(DateTime from, DateTime to) => $"{from:dd-MMM-yyyy} to {to:dd-MMM-yyyy}";
    private void SetBusy(string text) => lblStatus.Text = text;
    private static string ToSafeMessage(Exception ex) => ex is Microsoft.Data.SqlClient.SqlException sqlException && sqlException.Number == -2
        ? "The External Trend database read exceeded its controlled timeout. No review or approval snapshot was saved. Retry after any active import or database maintenance has completed; if it repeats, narrow the review scope or ask the database administrator to review SQL blocking/index health."
        : Infrastructure.UserFacingError.SafeMessage(ex);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record ExternalTrendAreaOption(
    string AreaCode,
    string Display,
    string TrendPopulation,
    string AreaClassification,
    bool IsComparable,
    bool IsAllAreas)
{
    public string LocationName { get; init; } = string.Empty;
}

public sealed class AreaTrendSummaryRow
{
    public AreaTrendSummaryRow(string areaCode, TrendCycleSummary summary)
    {
        Area = areaCode;
        Period = summary.Cycle;
        Observations = summary.Count;
        ExactObservations = summary.ExactCount;
        QualifiedObservations = summary.CensoredCount;
        Minimum = summary.Minimum;
        Mean = summary.Mean;
        Median = summary.Median;
        Maximum = summary.Maximum;
        AlertLimit = summary.AlertLimit;
        ActionLimit = summary.ActionLimit;
        AlertCount = summary.AlertCount;
        AlertRate = summary.AlertRate;
        ActionCount = summary.ActionCount;
        ActionRate = summary.ActionRate;
        Unit = summary.Unit;
        Signal = summary.Signal;
        DataQuality = summary.DataQuality;
    }

    public string Area { get; }
    public string Period { get; }
    public int Observations { get; }
    public int ExactObservations { get; }
    public int QualifiedObservations { get; }
    public decimal? Minimum { get; }
    public decimal? Mean { get; }
    public decimal? Median { get; }
    public decimal? Maximum { get; }
    public decimal? AlertLimit { get; }
    public decimal? ActionLimit { get; }
    public int AlertCount { get; }
    public decimal AlertRate { get; }
    public int ActionCount { get; }
    public decimal ActionRate { get; }
    public string Unit { get; }
    public string Signal { get; }
    public string DataQuality { get; }
}

public sealed class TrendObservation
{
    public TrendObservation(DateTime recordDate, decimal result, string resultQualifier, decimal? alertLimit, decimal? actionLimit)
    {
        RecordDate = recordDate;
        Result = result;
        ResultQualifier = resultQualifier?.Trim() ?? string.Empty;
        AlertLimit = alertLimit;
        ActionLimit = actionLimit;
    }

    public DateTime RecordDate { get; }
    public decimal Result { get; }
    public string ResultQualifier { get; }
    public bool IsQualified => !string.IsNullOrWhiteSpace(ResultQualifier);
    public decimal? AlertLimit { get; }
    public decimal? ActionLimit { get; }
}

public sealed record ReviewPeriod(DateTime From, DateTime To)
{
    public string Label => $"{From:dd-MMM-yyyy} to {To:dd-MMM-yyyy}";
}

public sealed class ReviewPacket
{
    public ReviewPacket(
        string areaCode,
        string methodName,
        string parameterName,
        IReadOnlyList<TrendCycleSummary> summaries,
        string narrative,
        IReadOnlyList<TrendObservation> observations,
        string sourceManifestSha256)
    {
        AreaCode = areaCode;
        MethodName = methodName;
        ParameterName = parameterName;
        Summaries = summaries;
        Narrative = narrative;
        Observations = observations;
        SourceManifestSha256 = sourceManifestSha256;
    }

    public string AreaCode { get; }
    public string MethodName { get; }
    public string ParameterName { get; }
    public string DisplaySubject => $"{MethodName} — {ParameterName}";
    public string LocationSummary { get; init; } = "See source import EntityName / Location";
    public IReadOnlyList<TrendCycleSummary> Summaries { get; }
    public string Narrative { get; }
    public IReadOnlyList<TrendObservation> Observations { get; }
    public string SourceManifestSha256 { get; }
    public string ExcursionStatus => CalculateExcursionStatus(Summaries);
    public string TrendConclusion => CalculateTrendConclusion(Summaries);
    public string RecommendedDisposition => CalculateRecommendedDisposition(Summaries);
    public string RegulatoryInterpretation => CalculateRegulatoryInterpretation(Summaries);
    public string MeanShiftDescription => CalculateMeanShiftDescription(Summaries);

    public static string CalculateExcursionStatus(IReadOnlyList<TrendCycleSummary> summaries)
    {
        if (summaries == null || summaries.Count == 0 || summaries.All(item => item.Count == 0))
            return "NO DATA";
        if (summaries.Any(item => item.ActionCount > 0))
            return "ACTION";
        if (summaries.Any(item => item.Count > 0 && (!item.LimitsComplete || !item.UnitsConsistent)))
            return "NOT ASSESSED";
        if (summaries.Any(item => item.AlertCount > 0))
            return "ALERT";
        if (summaries.Any(item => item.QualifiedNeedsReviewCount > 0))
            return "NOT ASSESSED";
        return "NORMAL";
    }

    public static string CalculateTrendConclusion(IReadOnlyList<TrendCycleSummary> summaries)
    {
        if (summaries == null || summaries.Count != 3 || summaries.Any(item => item.ExactCount < 2 || !item.Mean.HasValue || !item.UnitsConsistent))
            return "INCONCLUSIVE";

        decimal first = summaries[0].Mean!.Value;
        decimal middle = summaries[1].Mean!.Value;
        decimal latest = summaries[2].Mean!.Value;

        // Directional assessment only. No unapproved percentage threshold is invented
        // here; significance remains a controlled reviewer decision unless the site
        // procedure defines a statistical rule.
        if (first < middle && middle < latest)
            return "INCREASING";
        if (first > middle && middle > latest)
            return "DECREASING";
        return "NO CONSISTENT DIRECTION";
    }

    public static string CalculateRecommendedDisposition(IReadOnlyList<TrendCycleSummary> summaries)
    {
        string excursion = CalculateExcursionStatus(summaries);
        string trend = CalculateTrendConclusion(summaries);
        if (excursion == "ACTION")
            return "INVESTIGATION / CAPA REVIEW";
        if (excursion == "NOT ASSESSED")
            return "DATA QUALITY / LIMIT REVIEW";
        if (summaries.Any(item => item.Count > 0 && !item.LimitsConsistent))
            return "LIMIT CHANGE / QA REVIEW";
        if (excursion == "ALERT" || trend == "INCREASING")
            return "ENHANCED QA REVIEW";
        if (trend == "INCONCLUSIVE")
            return "DATA COMPLETENESS REVIEW";
        return "ROUTINE MONITORING";
    }

    public static string CalculateRegulatoryInterpretation(IReadOnlyList<TrendCycleSummary> summaries)
    {
        string excursion = CalculateExcursionStatus(summaries);
        string trend = CalculateTrendConclusion(summaries);
        if (excursion == "NO DATA")
            return "No approved numeric data are available; no trend conclusion is justified.";
        if (excursion == "NOT ASSESSED")
            return "Excursion compliance is not assessed because one or more observations lack complete alert/action limits or a controlled unit. Resolve the source-data/limit issue before interpreting the absence of excursions.";
        if (excursion == "ACTION")
            return "Action-level excursion(s) are present. Treat as an adverse signal requiring linked investigation/deviation review and QA disposition; the trend direction remains a separate descriptive assessment.";
        if (excursion == "ALERT")
            return trend == "INCREASING"
                ? "Alert-level excursion(s) and a sustained increasing direction are present. This combination warrants enhanced QA review for recurrence, emerging drift and appropriate risk controls."
                : "Alert-level excursion(s) are present. Review recurrence and context, document the action/no-action rationale, and consider enhanced monitoring according to the approved procedure.";
        if (trend == "INCREASING")
            return "No alert/action excursion is present, but cycle means increase sequentially. This is a potential drift signal, not an automatic failure; assess significance using quality risk management and historical/process context.";
        if (trend == "DECREASING")
            return "No alert/action excursion is present and cycle means decrease sequentially. The direction is favorable/descriptive; continue routine monitoring and verify that controls remain effective.";
        if (trend == "NO CONSISTENT DIRECTION")
            return "No alert/action excursion and no sustained directional drift are detected by the configured three-cycle logic. Continue routine monitoring and periodic review.";
        return "The available periods are insufficient for a controlled three-cycle conclusion. Resolve or justify the data-completeness gap before relying on trend direction.";
    }

    public static string CalculateMeanShiftDescription(IReadOnlyList<TrendCycleSummary> summaries)
    {
        if (summaries == null || summaries.Count < 2 || !summaries[0].Mean.HasValue || !summaries[^1].Mean.HasValue)
            return "Not available";

        decimal first = summaries[0].Mean!.Value;
        decimal latest = summaries[^1].Mean!.Value;
        decimal delta = latest - first;
        string signedDelta = delta >= 0 ? "+" + delta.ToString("0.##", CultureInfo.InvariantCulture) : delta.ToString("0.##", CultureInfo.InvariantCulture);
        if (first == 0)
            return $"{signedDelta} (first-cycle mean = 0)";

        decimal percent = Math.Round((delta / Math.Abs(first)) * 100m, 1);
        string signedPercent = percent >= 0 ? "+" + percent.ToString("0.0", CultureInfo.InvariantCulture) : percent.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{signedDelta} ({signedPercent}%) from first to latest cycle";
    }

}

public sealed record DataCompletenessException(
    string AreaCode,
    string MethodName,
    string ParameterName,
    string Period,
    int ObservationCount,
    string Issue);

public sealed record PortfolioAssessment(
    int AreaCount,
    int TotalObservations,
    int ActionAreas,
    int AlertAreas,
    int IncreasingAreas,
    int DataGapCount,
    string Status,
    string Interpretation,
    string Recommendation);
